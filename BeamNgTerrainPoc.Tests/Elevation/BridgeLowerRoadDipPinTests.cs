using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Sparse-mode lower-road dip wells (plan doc 01, review R-1; formerly the standalone dip-as-pin flag,
/// now owned unconditionally by <c>EnableSparseDeckConstraints</c>). The planned lower-road dips are
/// emitted PRE-smooth as SOFT eased wells — relative <c>SoftDipMeters</c> drops (road-272 ramp-end humps,
/// 2026-07-21; formerly absolute <c>PinnedElevation</c> off the estimate base, which held the near-zero
/// ramp ends UP wherever the solved road ran below the estimate). The smoother anchors the well on the
/// actual approaches and solves the dip continuously; the junction blender is well-aware (no tug-of-war);
/// and the demoted <c>GradeSeparationResolver.ApplyLowerRoadDips</c> never drops <c>TargetElevation</c> a
/// second time (no double-dip).
/// </summary>
public class BridgeLowerRoadDipPinTests
{
    private const float Tol = 0.05f;

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    // ── Pin emission: the full eased well ────────────────────────────────────────────────────────────────

    // Rule-2 dip scenario: corridor (priority 10000) at 10 over an under-road (priority 50) at 8 → the
    // planner dips the under-road 3 m to 5 (deficit (8+5)−10).
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, ParameterizedRoadSpline under)
        BuildDipScenario()
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true }.WithTestClearance();

        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), priority: 50);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = 10f;
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = 8f;
        return (network, corridor, under);
    }

    [Fact]
    public void DipPins_EmitTheFullEasedWell_AsRelativeSoftDrops()
    {
        var (network, _, under) = BuildDipScenario();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());
        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, crossing.Action);
        Assert.Equal(5f, crossing.LowerRoadTargetZ, Tol);

        var pinned = UnifiedRoadSmoother.ApplyLowerRoadDipPins(network, plan, null, hm, 1f);
        Assert.True(pinned > 0);

        // The under-road's crossing is at ≈ station 50; endpoint junctions clamp the well to
        // min(50−2, 50−2, 60) = 48 m half-length (junction margin lowered 8 → 2).
        var sections = network.GetCrossSectionsForSpline(under.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();

        // Road-272: the emission is the relative DROP, never an absolute Z off the estimate base —
        // and never a hard pin.
        Assert.All(sections, c => Assert.False(c.PinnedElevation.HasValue));

        // Well bottom = the full planned depth.
        var center = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 50f)).First();
        Assert.True(center.SoftDipMeters.HasValue);
        Assert.Equal(3f, center.SoftDipMeters!.Value, Tol);

        // Half-way up the ramp (u = 0.5 → w = 0.5): drop 1.5. Half-length 48 → 24 m from the centre.
        var mid = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - (50f + 24f))).First();
        Assert.True(mid.SoftDipMeters.HasValue);
        Assert.Equal(1.5f, mid.SoftDipMeters!.Value, 0.15f);

        // Beyond the well (u ≥ 1) nothing is emitted — but the RAMPS inside it are (full-well coverage).
        Assert.All(sections.Where(c => MathF.Abs(c.DistanceAlongSpline - 50f) >= 49f),
            c => Assert.False(c.SoftDipMeters.HasValue));
        Assert.Contains(sections, c =>
            MathF.Abs(c.DistanceAlongSpline - 50f) is > 30f and < 40f && c.SoftDipMeters.HasValue);
    }

    // ── THE survival test: 3 smoother iterations WITH junction harmonization ON and a junction blend
    //    zone reaching into the well (review R-1.1 — UnifiedJunctionProfileBlender is pin-aware only under
    //    the sparse guard; without it the blend would partially overwrite the well shoulder every
    //    iteration). ──

    private static (UnifiedRoadNetwork network, List<UnifiedCrossSection> roadCs, RoadSmoothingParameters prms)
        BuildWellWithNearbyCrossing()
    {
        // Road A: the dipped lower road, 400 m at y=150. Road B crosses it AT GRADE at x=250 (same layer)
        // → a MidSplineCrossing junction whose blend zone reaches the well's shoulder. B carries a much
        // higher priority and sits at z=12, so the harmonized junction elevation pulls A upward — the
        // exact tug-of-war hazard.
        var roadA = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(0, 150), new(400, 150), priority: 50);
        var roadB = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(250, 50), new(250, 250), priority: 10000);
        var rules = new BridgeRuleSystemOptions { EnableSparseDeckConstraints = true }.WithTestClearance();
        roadA.Parameters.BridgeRules = rules;
        roadB.Parameters.BridgeRules = rules;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(roadA, roadB);

        // The soft dip well on road A: centre at station 200, depth 3, half-length 42 → well spans
        // [158, 242]; the junction at station 250 is OUTSIDE the well (post-A4 reality) but its blend
        // radius reaches the shoulder. Relative drops (road-272): the base Z never enters the emission.
        var roadACs = network.GetCrossSectionsForSpline(roadA.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        foreach (var cs in roadACs)
        {
            var u = MathF.Abs(cs.DistanceAlongSpline - 200f) / 42f;
            if (u >= 1f) continue;
            var w = (1f - u) * (1f - u) * (1f + 2f * u);
            if (3f * w <= 1e-3f) continue;
            cs.SoftDipMeters = 3f * w;
        }

        var prms = new RoadSmoothingParameters
        {
            CrossSectionIntervalMeters = 1f,
            SplineParameters = new SplineRoadParameters { SmoothingWindowSize = 41, UseButterworthFilter = false },
        };
        return (network, roadACs, prms);
    }

    private static void RunIterationsWithJunctionBlend(
        UnifiedRoadNetwork network, float[,] hm, RoadSmoothingParameters prms, int iterations)
    {
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();
        var csBySpline = network.CrossSections
            .GroupBy(c => c.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.LocalIndex).ToList());

        var smoother = new OptimizedElevationSmoother();
        for (var it = 0; it < iterations; it++)
        {
            foreach (var chain in chains)
            {
                var chainCS = OptimizedElevationSmoother.ConcatenateChainCrossSections(
                    chain, csBySpline, prms.CrossSectionIntervalMeters);
                if (it == 0)
                    smoother.CalculateChainElevations(chainCS, prms, hm, 1f);
                else
                    smoother.ReSmoothChainFromExistingElevations(chainCS, prms);
                OptimizedElevationSmoother.PropagateToDeduped(chain);
            }

            // Road B sits high (12) so the priority²-weighted MidSplineCrossing harmonization pulls
            // road A's sections upward inside the junction blend radius.
            foreach (var cs in network.GetCrossSectionsForSpline(2))
                cs.TargetElevation = 12f;

            var originalElevations = network.CrossSections.ToDictionary(c => c.Index, c => c.TargetElevation);
            var originalBanks = network.CrossSections.ToDictionary(c => c.Index, c => c.BankAngleRadians);
            new UnifiedJunctionProfileBlender().ApplyUnifiedProfiles(
                network, originalElevations, originalBanks, hm, 1f);
        }
    }

    [Fact]
    public void DipWell_SurvivesThreeIterations_WithJunctionHarmonizationOn()
    {
        // Soft-well survival contract (R-1.1, road-272 edition). The well is applied as a post-filter
        // per-section depression of the SOLVED base, so the z=12 junction blend can neither overwrite the
        // well sections (blender respect) nor tilt the well interior through its filter base at 50 m
        // distance — and re-smooth iterations un-dip their input first, so nothing accumulates or erodes.
        var (network2, roadACs2, prms2) = BuildWellWithNearbyCrossing();
        var hm2 = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);
        RunIterationsWithJunctionBlend(network2, hm2, prms2, iterations: 2);
        var bottomAfter2 = roadACs2.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 200f))
            .First().TargetElevation;

        var (network, roadACs, prms) = BuildWellWithNearbyCrossing();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);
        RunIterationsWithJunctionBlend(network, hm, prms, iterations: 3);

        var wellSections = roadACs.Where(c => c.SoftDipMeters.HasValue).ToList();
        Assert.NotEmpty(wellSections);

        // The well bottom is the full 3 m dip — the junction pull cannot lift it.
        var bottom = roadACs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 200f)).First();
        Assert.Equal(5f, bottom.TargetElevation, Tol);

        // THE survival property: iteration 3 changes nothing vs iteration 2 — no progressive erosion.
        Assert.Equal(bottomAfter2, bottom.TargetElevation, 0.05f);

        // Left half of the well (the junction blend zone is on the right): never above the natural 8 —
        // the road-272 invariant, a dip well must only ever move the road DOWN.
        Assert.All(wellSections.Where(c => c.DistanceAlongSpline <= 200f),
            c => Assert.True(c.TargetElevation <= 8f + Tol,
                $"well section at {c.DistanceAlongSpline:F0} m lifted to {c.TargetElevation:F2} (> natural 8)"));

        // Mid-ramp shape retained (station 179, u=0.5 → drop 1.5): the eased descent survives the filter,
        // no V-flattening.
        var midRamp = roadACs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 179f)).First();
        Assert.Equal(6.5f, midRamp.TargetElevation, 0.15f);
    }

    // ── No-double-dip: the demoted resolver is verify-only ───────────────────────────────────────────────

    [Fact]
    public void Resolver_SparseDipPins_NeverDropsTargetElevationAgain_NoCarve()
    {
        // The smoother (pins) already dipped the under-road fully to 5 (clearance met). Verify-only:
        // elevations and heightmap untouched; action recorded from the plan.
        var (network, _, under) = BuildDipScenario();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);

        network.BridgeElevationPlan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = 5f; // the fully-dipped final profile the pins produced

        var hmBefore = (float[,])hm.Clone();
        GradeSeparationResolver.ApplyLowerRoadDips(network, hm, 1f);

        Assert.All(network.GetCrossSectionsForSpline(under.SplineId),
            cs => Assert.Equal(5f, cs.TargetElevation, 0.001f)); // NO second drop
        for (var y = 0; y < 512; y += 16)
        for (var x = 0; x < 512; x += 16)
            Assert.Equal(hmBefore[y, x], hm[y, x]); // NO carve

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(GradeSeparationAction.DippedLowerRoad, crossing.Action);
        Assert.Equal(3f, crossing.AppliedDipMeters, Tol); // the planned (pinned) dip
    }

    // ── A7 backstop: bounded local carve for residual shortfall, road profile untouched ─────────────────

    [Fact]
    public void Resolver_A7_ResidualShortfall_CarvesHeightmapOnly()
    {
        // The pins under-delivered: the road sits at 6 (clearance 4, required 5). A7 carves the 1 m
        // residual into the heightmap as a bounded eased well — and NEVER touches TargetElevation.
        var (network, _, under) = BuildDipScenario();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);

        network.BridgeElevationPlan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = 6f; // 1 m short

        GradeSeparationResolver.ApplyLowerRoadDips(network, hm, 1f);

        // Road profile untouched (the no-double-dip invariant holds even when short).
        Assert.All(network.GetCrossSectionsForSpline(under.SplineId),
            cs => Assert.Equal(6f, cs.TargetElevation, 0.001f));

        // Heightmap carved ~1 m at the crossing (200,150) — the under-road runs along x=200.
        Assert.Equal(7f, hm[150, 200], 0.1f);
        // Bounded: far from the well the terrain is untouched.
        Assert.Equal(8f, hm[150, 100], 0.001f);
        Assert.Equal(8f, hm[300, 200], 0.001f);
    }

    [Fact]
    public void Planner_RecordsObstacleZEstimate_ForA7Logging()
    {
        var (network, _, _) = BuildDipScenario();
        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(8f, Assert.Single(plan.Crossings).ObstacleZEstimate, Tol);
    }
}
