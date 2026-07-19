using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Coherent underpass resolution (doc 28): a road crossed by a CLUSTER of bridges (an interchange) must not
/// receive fragmented per-crossing decisions (dip here, raise there, carve in between — the winningen road-164
/// washboard). Crossings are grouped by the lower road (station gap ≤ <c>UnderpassClusterGapMeters</c>); when
/// the road is outranked by the highest-priority bridge of the cluster, the WHOLE cluster resolves as one
/// bounded underpass: every crossing dips the road (raises suppressed, including lower-priority bridges), the
/// dip is capped at <c>MaxUnderpassDipMeters</c> (cap + warn, residual accepted), and the dip wells are merged
/// into ONE smooth envelope well spanning first→last crossing. Gated on <c>EnablePriorityDistribution</c>.
/// </summary>
public class BridgeCoherentUnderpassTests
{
    private const float Tol = 0.05f;

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    /// <summary>
    /// Interchange fixture: a lower road (id 9) along y=150 from x=50..450 (station = x − 50), crossed by
    /// three separate vertical bridge corridors at the given x positions. Two motorway bridges (10002)
    /// outrank the road; one residential bridge (3000) is outranked BY the road — the winningen 360/361 vs
    /// 47/42 mixed-priority shape. All roads sit at <paramref name="corridorElev"/>, the lower road at
    /// <paramref name="underElev"/>.
    /// </summary>
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline under) BuildInterchange(
        BridgeRuleSystemOptions rules,
        float[] bridgeX,
        float corridorElev = 10f,
        float underElev = 10f,
        int underPriority = 8001,
        string underClass = "primary",
        int[]? bridgePriorities = null,
        string[]? bridgeClasses = null)
    {
        rules.WithTestClearance();
        var splines = new List<ParameterizedRoadSpline>();
        for (var i = 0; i < bridgeX.Length; i++)
        {
            var span = new StructureSegment
            {
                StartDistance = 40, EndDistance = 60, Type = StructureType.Bridge, Layer = 1,
                OsmWayIds = { 99001L + i },
            };
            var priority = bridgePriorities != null ? bridgePriorities[i] : i < 2 ? 10002 : 3000;
            var cls = bridgeClasses != null ? bridgeClasses[i] : i < 2 ? "motorway" : "residential";
            var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
                i + 1, new(bridgeX[i], 100), new(bridgeX[i], 200), osmRoadType: cls, priority: priority,
                mergeStructuresIntoCorridor: true, structureSegments: [span]);
            bridge.Layer = 0;
            bridge.Parameters.BridgeRules = rules;
            splines.Add(bridge);
        }

        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            9, new(50, 150), new(450, 150), osmRoadType: underClass, priority: underPriority);
        under.Layer = 0;
        under.Parameters.BridgeRules = rules;
        splines.Add(under);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(splines.ToArray());
        foreach (var spline in splines)
        foreach (var cs in network.GetCrossSectionsForSpline(spline.SplineId))
            cs.TargetElevation = spline.SplineId == under.SplineId ? underElev : corridorElev;
        return (network, under);
    }

    private static BridgeRuleSystemOptions GateOn() => new() { EnablePriorityDistribution = true };

    // ── Step B: one decision per cluster — everything dips, no raises ────────────────────────────────────

    [Fact]
    public void Cluster_MixedPriorities_AllCrossingsDip_NoBridgeRaised()
    {
        // Three bridges within 50 m along the lower road (stations 100/150/200) — one cluster. The road
        // (8001) is outranked by the motorways (10002) → the WHOLE cluster dips, including the crossing
        // under the residential bridge (3000) that would individually have raised its own deck (Rule 1).
        var (network, _) = BuildInterchange(GateOn(), bridgeX: [150f, 200f, 250f]);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(3, plan.Crossings.Count);
        Assert.All(plan.Crossings, c => Assert.Equal(BridgeElevationAction.DipLowerRoad, c.Action));
        Assert.All(plan.Crossings, c => Assert.Equal(5f, c.DipDepthMeters, Tol));   // deficit 10+5−10
        Assert.All(plan.Crossings, c => Assert.Equal(5f, c.LowerRoadTargetZ, Tol)); // 10 − 5
        Assert.All(plan.Spans, s => Assert.False(s.IsRaised)); // no bridge moves — the underpass clears all
        Assert.All(plan.Crossings, c =>
        {
            Assert.NotNull(c.Warning);
            Assert.Contains("coherent underpass", c.Warning);
        });
    }

    [Fact]
    public void Cluster_SurfacedOnPlan_WithBridgesAndDepth()
    {
        var (network, under) = BuildInterchange(GateOn(), bridgeX: [150f, 200f, 250f]);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var cluster = Assert.Single(plan.UnderpassClusters);
        Assert.Equal(under.SplineId, cluster.LowerSplineId);
        Assert.Equal(3, cluster.Crossings.Count);
        Assert.Equal(new[] { 1, 2, 3 }, cluster.UpperSplineIds.OrderBy(id => id).ToArray());
        Assert.Equal(5f, cluster.MaxDipMeters, Tol);
        Assert.Equal(0f, cluster.CappedResidualMeters, Tol);
    }

    // ── Step D: bounded — cap + warn, stay coherent ──────────────────────────────────────────────────────

    [Fact]
    public void Cluster_DipOverCap_IsCapped_WarnsAndAcceptsResidual()
    {
        // Lower road ABOVE the bridges (13 vs 10): full deficit 13+5−10 = 8 m > MaxUnderpassDip 6 → dip 6,
        // residual 2 m accepted via a REDUCED separation budget (5 → 3) so the post-solve verify does not
        // re-carve the accepted shortfall. The bridges are NOT raised for the residual.
        var (network, _) = BuildInterchange(GateOn(), bridgeX: [150f, 200f, 250f], underElev: 13f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.All(plan.Crossings, c => Assert.Equal(BridgeElevationAction.DipLowerRoad, c.Action));
        Assert.All(plan.Crossings, c => Assert.Equal(6f, c.DipDepthMeters, Tol));           // capped
        Assert.All(plan.Crossings, c => Assert.Equal(7f, c.LowerRoadTargetZ, Tol));         // 13 − 6
        Assert.All(plan.Crossings, c => Assert.Equal(3f, c.RequiredSeparationMeters, Tol)); // 5 − 2 residual
        Assert.All(plan.Spans, s => Assert.False(s.IsRaised));
        Assert.All(plan.Crossings, c =>
        {
            Assert.NotNull(c.Warning);
            Assert.Contains("capped", c.Warning);
        });

        var cluster = Assert.Single(plan.UnderpassClusters);
        Assert.Equal(6f, cluster.MaxDipMeters, Tol);
        Assert.Equal(2f, cluster.CappedResidualMeters, Tol);
    }

    [Fact]
    public void DegenerateMember_RoadFarAboveDeck_FallsBackToNormalRules()
    {
        // Review finding 1: the lower road sits ≥ MaxUnderpassDip (6) ABOVE the un-raised decks (17 vs 10)
        // — even a full-cap dip cannot bring it below them, and the old code emitted a coherent dip with a
        // NEGATIVE RequiredSeparationMeters (sep 5 − residual 6), which the resolver verify then treated as
        // 'unset' (full-clearance A7 carve), the plan-clearance diagnostic skipped, and the reconcile turned
        // into a road target ABOVE the deck. Such members must fall back to the normal rules (Rule-1 raise).
        var (network, _) = BuildInterchange(GateOn(), bridgeX: [150f, 200f, 250f], underElev: 17f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.All(plan.Crossings, c => Assert.Equal(BridgeElevationAction.RaiseBridge, c.Action));
        Assert.All(plan.Crossings, c => Assert.True(
            c.RequiredSeparationMeters > 0f,
            $"separation must stay positive, was {c.RequiredSeparationMeters}"));
        Assert.All(plan.Spans, s => Assert.True(s.IsRaised));
        Assert.Empty(plan.UnderpassClusters); // no member dipped ⇒ no coherent underpass to merge or log
    }

    // ── Step C: envelope profile unit behavior ───────────────────────────────────────────────────────────

    [Fact]
    public void WellProfile_CoincidentStations_KeepDeepestTarget()
    {
        // Review finding 2: two decks crossing within one section spacing snap to the SAME lower-road
        // station; the profile must keep the DEEPEST target there (first-sorted-wins under-dipped the
        // deck that needed the lower road).
        var profile = new UnderpassWellProfile([(100f, 7f), (100f, 4f)], 60f, 60f);

        Assert.Equal(4f, profile.ZAt(100f, 10f), Tol);
        Assert.Equal(7f, profile.ZAt(130f, 10f), Tol); // exit ramp blends 4 → natural 10 (u=0.5 → w=0.5)
        Assert.Equal(10f, profile.ZAt(161f, 10f), Tol);
    }

    [Fact]
    public void WellProfile_ClassRampLength_ScalesWithDepthAndClass()
    {
        // §3.3 normal slopes: primary 5 %, motorway 4 %, residential 8 % — the ramp recovers the well's
        // end depth at the class grade, never shorter than the classic 60 m default.
        Assert.Equal(120f, UnderpassWellProfile.ClassRampLengthMeters(6f, "primary", 60f), Tol);
        Assert.Equal(150f, UnderpassWellProfile.ClassRampLengthMeters(6f, "motorway", 60f), Tol);
        Assert.Equal(60f, UnderpassWellProfile.ClassRampLengthMeters(4f, "residential", 60f), Tol);
    }

    [Fact]
    public void WellProfile_InteriorIsEngineeredZ_IgnoresBaseNoise()
    {
        // Winningen render #2: the well bottom must be an engineered curve through the crossing targets —
        // base-estimate noise between crossings (DEM artifacts under real interchanges) must NOT reappear
        // in the (hard-pinned) well bottom. The natural elevation only acts as a never-raise clamp and
        // fades back in over the end ramps.
        var profile = new UnderpassWellProfile([(0f, 8f), (100f, 4f)], 20f, 20f);

        Assert.Equal(6f, profile.ZAt(50f, 10f), Tol);    // midpoint of the smoothstep 8 → 4
        Assert.Equal(6f, profile.ZAt(50f, 13.5f), Tol);  // +3.5 m base hump mid-well → filtered out
        Assert.Equal(8f, profile.ZAt(1f, 10f), 0.01f);   // ~flat leaving the first crossing
        Assert.Equal(4f, profile.ZAt(99f, 10f), 0.01f);  // ~flat entering the last crossing
        Assert.Equal(5f, profile.ZAt(50f, 5f), Tol);     // never raised above a naturally lower road
    }

    // ── Step A gating: window, flag, priority ────────────────────────────────────────────────────────────

    [Fact]
    public void CrossingsBeyondGapWindow_AreNotClustered_KeepPerCrossingBehavior()
    {
        // Two bridges 240 m apart along the lower road (> 120 m window) → two single-crossing groups →
        // no coherent pass. Each span is an at-grade Rule-1 ramp and raises its own deck (today's path).
        var (network, _) = BuildInterchange(
            GateOn(), bridgeX: [150f, 390f],
            bridgePriorities: [10002, 10002], bridgeClasses: ["motorway", "motorway"]);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Empty(plan.UnderpassClusters);
        Assert.Equal(2, plan.Crossings.Count);
        Assert.All(plan.Crossings, c => Assert.Equal(BridgeElevationAction.RaiseBridge, c.Action));
        Assert.All(plan.Spans, s => Assert.True(s.IsRaised));
    }

    [Fact]
    public void GateOff_NoCoherentPass_ByteIdenticalRaises()
    {
        var (network, _) = BuildInterchange(new BridgeRuleSystemOptions(), bridgeX: [150f, 200f, 250f]);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Empty(plan.UnderpassClusters);
        Assert.All(plan.Crossings, c => Assert.Equal(BridgeElevationAction.RaiseBridge, c.Action));
        Assert.All(plan.Spans, s => Assert.True(s.IsRaised));
    }

    [Fact]
    public void RoadOutranksEveryBridge_BridgesRaise_RoadStaysFlat()
    {
        // The lower road (20000) outranks even the motorway bridges (10002) → no underpass; the bridges
        // raise exactly as today (Step B else-branch).
        var (network, _) = BuildInterchange(
            GateOn(), bridgeX: [150f, 200f, 250f], underPriority: 20000, underClass: "motorway");

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Empty(plan.UnderpassClusters);
        Assert.All(plan.Crossings, c => Assert.Equal(BridgeElevationAction.RaiseBridge, c.Action));
        Assert.All(plan.Spans, s => Assert.True(s.IsRaised));
    }

    // ── Step C: ONE smooth envelope well (pin emitter) ───────────────────────────────────────────────────

    [Fact]
    public void DipPins_Cluster_EmitOneCoherentWell_NoInteriorHump()
    {
        // The cluster's dip pins must form ONE well spanning first→last crossing (stations 100..200), not
        // three independent 60 m wells fighting each other. Interior stations BETWEEN crossings must stay
        // at full envelope depth (old behavior: station 175 sat on well C's ramp at ≈ 6.8 — the washboard).
        var rules = GateOn();
        var (network, under) = BuildInterchange(rules, bridgeX: [150f, 200f, 250f]);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());
        var pinned = UnifiedRoadSmoother.ApplyLowerRoadDipPins(network, plan, null, hm, 1f);
        Assert.True(pinned > 0);

        var sections = network.GetCrossSectionsForSpline(under.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();

        float PinAt(float station)
        {
            var cs = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();
            Assert.True(cs.PinnedElevation.HasValue,
                $"section at station {station} must be pinned into the underpass well");
            return cs.PinnedElevation!.Value;
        }

        // Full depth at every crossing AND at interior stations between them — one continuous well bottom.
        Assert.Equal(5f, PinAt(100f), 0.15f);
        Assert.Equal(5f, PinAt(150f), 0.15f);
        Assert.Equal(5f, PinAt(200f), 0.15f);
        Assert.Equal(5f, PinAt(125f), 0.2f);
        Assert.Equal(5f, PinAt(175f), 0.2f); // ← the anti-washboard assertion

        // Eased ends beyond the cluster: the exit ramps are depth/class-sized (§3.3: primary 5 % → 100 m
        // desired for a 5 m well; the back side is junction-clamped to 98 m by the way-end junction).
        // Half-way up each ramp (u=0.5 → w=0.5) ≈ 10 − 2.5.
        Assert.Equal(7.5f, PinAt(51f), 0.3f);  // 100 − 98/2
        Assert.Equal(7.5f, PinAt(250f), 0.3f); // 200 + 100/2

        // Beyond the well ([2, 300] incl. ramps) the road is untouched.
        Assert.All(sections.Where(c => c.DistanceAlongSpline < 1.5f || c.DistanceAlongSpline > 301f),
            c => Assert.False(c.PinnedElevation.HasValue));
    }

    [Fact]
    public void DipPins_BaseEstimateNoiseMidCluster_DoesNotReachWellBottom()
    {
        // Winningen render #2: dip pins ride the base elevation chain (estimate/DEM), which carries
        // artifacts exactly under real interchanges — a depth-offset well reproduced every wiggle 1:1 in
        // the HARD-pinned bottom where the smoother cannot fix it. The interior must be an engineered
        // curve through the crossing targets, immune to base noise between them.
        var rules = GateOn();
        var (network, under) = BuildInterchange(rules, bridgeX: [150f, 200f, 250f]);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        // A +1 m base artifact between the first two crossings (stations 100/150 on the lower road).
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            if (cs.DistanceAlongSpline is > 115f and < 135f)
                cs.TargetElevation = 11f;

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());
        UnifiedRoadSmoother.ApplyLowerRoadDipPins(network, plan, null, hm, 1f);

        var noisy = network.GetCrossSectionsForSpline(under.SplineId)
            .Where(c => c.DistanceAlongSpline is > 116f and < 134f).ToList();
        Assert.NotEmpty(noisy);
        Assert.All(noisy, c =>
        {
            Assert.True(c.PinnedElevation.HasValue);
            Assert.Equal(5f, c.PinnedElevation!.Value, 0.1f); // engineered bottom — the +1 m hump is gone
        });
    }

    // ── Step C: ONE envelope well in the resolver's active (non-pin) path ────────────────────────────────

    [Fact]
    public void ResolverActivePath_Cluster_DipsOneCoherentWell()
    {
        // No dip-as-pin / sparse → the dips are applied post-solve by ApplyLowerRoadDips. The cluster must
        // come out as ONE well on TargetElevation: full depth between crossings, eased beyond, and never
        // the stacked/overlapping per-crossing wells of the fragmented path.
        var (network, under) = BuildInterchange(GateOn(), bridgeX: [150f, 200f, 250f]);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());
        network.BridgeElevationPlan = plan;

        GradeSeparationResolver.ApplyLowerRoadDips(network, hm, 1f, log: false);

        var sections = network.GetCrossSectionsForSpline(under.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();

        float ZAt(float station) => sections
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First().TargetElevation;

        Assert.Equal(5f, ZAt(100f), 0.15f);
        Assert.Equal(5f, ZAt(150f), 0.15f);
        Assert.Equal(5f, ZAt(200f), 0.15f);
        Assert.Equal(5f, ZAt(175f), 0.2f);   // no hump between crossings
        Assert.Equal(7.5f, ZAt(250f), 0.3f); // depth/class-sized eased exit ramp (100 m), half-way up
        Assert.Equal(10f, ZAt(305f), 0.15f); // untouched beyond the well

        Assert.All(network.GradeSeparatedCrossings,
            c => Assert.Equal(GradeSeparationAction.DippedLowerRoad, c.Action));
    }
}
