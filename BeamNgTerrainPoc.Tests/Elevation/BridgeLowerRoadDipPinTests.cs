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
/// emitted PRE-smooth as pinned eased wells (FULL well incl. ramps, so the pin-exemption masks cover it);
/// the smoother solves the dip continuously; the junction blender is pin-aware (no tug-of-war); and the
/// demoted <c>GradeSeparationResolver.ApplyLowerRoadDips</c> never drops <c>TargetElevation</c> a second
/// time (no double-dip).
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
    public void DipPins_PinTheFullEasedWell_BottomAtPlannedTarget()
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

        // Well bottom = the planned dip target (baseZ 8 − depth 3).
        var center = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 50f)).First();
        Assert.True(center.PinnedElevation.HasValue);
        Assert.Equal(5f, center.PinnedElevation!.Value, Tol);

        // Half-way up the ramp (u = 0.5 → w = 0.5): 8 − 1.5 = 6.5. Half-length 48 → 24 m from the centre.
        var mid = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - (50f + 24f))).First();
        Assert.True(mid.PinnedElevation.HasValue);
        Assert.Equal(6.5f, mid.PinnedElevation!.Value, 0.15f);

        // Beyond the well (u ≥ 1) nothing is pinned — but the RAMPS inside it are (full-well coverage).
        Assert.All(sections.Where(c => MathF.Abs(c.DistanceAlongSpline - 50f) >= 49f),
            c => Assert.False(c.PinnedElevation.HasValue));
        Assert.Contains(sections, c =>
            MathF.Abs(c.DistanceAlongSpline - 50f) is > 30f and < 40f && c.PinnedElevation.HasValue);
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

        // The pinned dip well on road A: centre at station 200, depth 3 from the z=8 base, half-length 42
        // → well spans [158, 242]; the junction at station 250 is OUTSIDE the well (post-A4 reality) but
        // its blend radius reaches the shoulder.
        var roadACs = network.GetCrossSectionsForSpline(roadA.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();
        foreach (var cs in roadACs)
        {
            var u = MathF.Abs(cs.DistanceAlongSpline - 200f) / 42f;
            if (u >= 1f) continue;
            var w = (1f - u) * (1f - u) * (1f + 2f * u);
            cs.PinnedElevation = 8f - 3f * w;
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
        var (network, roadACs, prms) = BuildWellWithNearbyCrossing();
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);

        RunIterationsWithJunctionBlend(network, hm, prms, iterations: 3);

        // Every pinned section of the well — bottom AND ramps — still sits exactly on its pin.
        var pinnedSections = roadACs.Where(c => c.PinnedElevation.HasValue).ToList();
        Assert.NotEmpty(pinnedSections);
        Assert.All(pinnedSections, c =>
            Assert.Equal(c.PinnedElevation!.Value, c.TargetElevation, 0.02f));

        // The well bottom is the full 3 m dip — no V-flattening, no shoulder kink from the blend.
        var bottom = roadACs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 200f)).First();
        Assert.Equal(5f, bottom.TargetElevation, Tol);
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
