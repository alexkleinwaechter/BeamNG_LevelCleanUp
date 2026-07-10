using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Phase D (plan doc 14 §3.2, §6, §11): the post-smoothing trim. The deck-elevation decision now lives in the
/// rule engine (<see cref="BridgeElevationPlanner"/>, run pre-smoothing and stashed on
/// <c>UnifiedRoadNetwork.BridgeElevationPlan</c>); <see cref="BridgeProfileSolver.RefineSpans"/> only re-curves +
/// snapshots; and <see cref="GradeSeparationResolver.ApplyLowerRoadDips"/> consumes the plan — it dips ONLY the
/// rule engine's "dip"/"split" crossings against the final stamped deck Z, leaving Rule-1 raise/veto crossings
/// alone. Legacy (no plan) behaviour is unchanged (covered by <c>GradeSeparationResolverTests</c>).
/// </summary>
public class BridgePhaseDDipTests
{
    private const float Tol = 0.05f;

    // ── Builders (mirror BridgeElevationPlannerTests) ────────────────────────────────────────────────────

    private static ParameterizedRoadSpline BuildCorridor(
        int splineId, float spanStart, float spanEnd,
        int priority = 8002, int spanLayer = 1, float length = 400f, long wayId = 99001L)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart, EndDistance = spanEnd,
            Type = StructureType.Bridge, Layer = spanLayer, OsmWayIds = { wayId },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new(50, 150), new(50 + length, 150), priority: priority, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions().WithTestClearance();
        return corridor;
    }

    private static ParameterizedRoadSpline BuildUnderRoad(int splineId, float crossingX, int priority = 8002)
    {
        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new(crossingX, 100), new(crossingX, 200), priority: priority);
        under.Layer = 0;
        return under;
    }

    private static void SetFlatElevation(UnifiedRoadNetwork network, int splineId, float z)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            cs.TargetElevation = z;
    }

    private static void SetCorridorElevation(
        UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, float spanStart, float spanEnd,
        float approachZ, float deckBaseZ)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            var inSpan = cs.DistanceAlongSpline >= spanStart && cs.DistanceAlongSpline <= spanEnd;
            cs.TargetElevation = inSpan ? deckBaseZ : approachZ;
        }
    }

    // Sets the span interior to a single (final, "stamped") deck elevation — what the pin + smoother produced.
    private static void SetSpanElevation(
        UnifiedRoadNetwork network, int splineId, float spanStart, float spanEnd, float z)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            if (cs.DistanceAlongSpline >= spanStart && cs.DistanceAlongSpline <= spanEnd)
                cs.TargetElevation = z;
    }

    private static List<float> UnderRoadZ(UnifiedRoadNetwork network, int splineId) =>
        network.GetCrossSectionsForSpline(splineId).Select(c => c.TargetElevation).ToList();

    private static BridgeElevationPlannerOptions NoTerrain() => new();

    // ── Rule 1 raise crossing is NEVER dipped, even when the deck falls a little short of clearance ───────

    [Fact]
    public void Merged_RaiseCrossing_LeavesUnderRoadAlone_EvenWhenDeckIsShortOfClearance()
    {
        // Rule 1 (ramp ⇒ raise): the planner pins the deck and decides the under-road stays put. Post-smoothing
        // the deck lands a touch low (requiredDeckZ−1 = 4) so road-vs-deck clearance (4) is BELOW the 5 m
        // minimum — the legacy dip logic WOULD lower the road here. The Phase-D plan gate must override that and
        // leave the road untouched (Rule-1 roads are never dipped — they are cleared by raising the deck).
        var corridor = BuildCorridor(1, 100, 200);
        var under = BuildUnderRoad(2, crossingX: 200);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 0f, deckBaseZ: 0f);
        SetFlatElevation(network, under.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());
        network.BridgeElevationPlan = plan;
        Assert.Equal(BridgeElevationAction.RaiseBridge, Assert.Single(plan.Crossings).Action);

        // Smoother result: deck raised but 1 m short of requiredDeckZ (5) → clearance 4 < 5.
        SetSpanElevation(network, corridor.SplineId, 100, 200, plan.Spans[0].RequiredDeckZ - 1f);
        var roadBefore = UnderRoadZ(network, under.SplineId);

        GradeSeparationResolver.ApplyLowerRoadDips(network, minClearanceMeters: 5f, log: false);

        Assert.Equal(GradeSeparationAction.RaisedBridge, plan.Crossings[0].Crossing.Action);
        Assert.Equal(roadBefore, UnderRoadZ(network, under.SplineId)); // untouched
    }

    // ── Rule 2 dip crossing is lowered against the final stamped deck Z ───────────────────────────────────

    [Fact]
    public void Merged_DipCrossing_LowersUnderRoadAgainstFinalDeckZ()
    {
        // Rule 2: an un-raised deck (flat at 10) over a lower-priority road at 8 — the planner says dip. The
        // deck is NOT pinned (IsRaised=false), so post-smoothing it stays at 10; ApplyLowerRoadDips dips the
        // road the 3 m deficit so it sits 5 m below the deck.
        var corridor = BuildCorridor(1, 100, 200, priority: 8002);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 50);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 8f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());
        network.BridgeElevationPlan = plan;
        Assert.False(plan.Spans[0].IsRaised);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, Assert.Single(plan.Crossings).Action);

        GradeSeparationResolver.ApplyLowerRoadDips(
            network, minClearanceMeters: 5f, dipRampLengthMeters: 30f, log: false);

        var crossing = plan.Crossings[0].Crossing;
        Assert.Equal(GradeSeparationAction.DippedLowerRoad, crossing.Action);
        Assert.Equal(3f, crossing.AppliedDipMeters, Tol);                              // (8+5) − 10
        Assert.Equal(5f, UnderRoadZ(network, under.SplineId).Min(), precision: 1);     // deck 10 − C 5
    }

    // ── Rule 3 split: the residual dip is fitted against the (raised) final deck Z ────────────────────────

    [Fact]
    public void Merged_SplitCrossing_DipsResidualAgainstRaisedDeck()
    {
        // Rule 3 (equal priority, no ramp): deck base 10, road 8 → deficit 3, 0.5 split ⇒ pin deck to 11.5 and
        // dip the road by the residual. Post-smoothing the pinned deck is at 11.5; ApplyLowerRoadDips lowers the
        // road to deck − C = 6.5 (residual 1.5 m).
        var corridor = BuildCorridor(1, 100, 200, priority: 8002);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 8002);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 8f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());
        network.BridgeElevationPlan = plan;
        Assert.Equal(BridgeElevationAction.Split, Assert.Single(plan.Crossings).Action);
        Assert.Equal(11.5f, plan.Spans[0].RequiredDeckZ, Tol);

        // Smoother result: the deck is pinned at 11.5.
        SetSpanElevation(network, corridor.SplineId, 100, 200, plan.Spans[0].RequiredDeckZ);

        GradeSeparationResolver.ApplyLowerRoadDips(network, minClearanceMeters: 5f, dipRampLengthMeters: 30f, log: false);

        var crossing = plan.Crossings[0].Crossing;
        Assert.Equal(GradeSeparationAction.DippedLowerRoad, crossing.Action);
        Assert.Equal(1.5f, crossing.AppliedDipMeters, Tol);                          // (8+5) − 11.5
        Assert.Equal(6.5f, UnderRoadZ(network, under.SplineId).Min(), precision: 1); // deck 11.5 − C 5
    }

    // ── Already-clear crossing under a raised deck: no dip ────────────────────────────────────────────────

    [Fact]
    public void Merged_RaisedDeckThatAlreadyClears_DoesNotDip()
    {
        // Rule 1 raise that fully clears post-smoothing: deck pinned at 5, road at 0 → clearance 5 ≥ 5 → the
        // crossing reports RaisedBridge and the road is untouched.
        var corridor = BuildCorridor(1, 100, 200);
        var under = BuildUnderRoad(2, crossingX: 200);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 0f, deckBaseZ: 0f);
        SetFlatElevation(network, under.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());
        network.BridgeElevationPlan = plan;
        SetSpanElevation(network, corridor.SplineId, 100, 200, plan.Spans[0].RequiredDeckZ); // 5

        var roadBefore = UnderRoadZ(network, under.SplineId);
        GradeSeparationResolver.ApplyLowerRoadDips(network, minClearanceMeters: 5f, log: false);

        Assert.Equal(GradeSeparationAction.RaisedBridge, plan.Crossings[0].Crossing.Action);
        Assert.Equal(roadBefore, UnderRoadZ(network, under.SplineId));
    }
}
