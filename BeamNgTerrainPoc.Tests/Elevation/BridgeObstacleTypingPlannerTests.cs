using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// V2 Phase A1 (plan doc 01): obstacle typing in the rule engine. Rail/water obstacles have NO road spline —
/// the planner emits SYNTHETIC <see cref="GradeSeparatedCrossing"/>s (LowerSplineId = −1) from the
/// pre-projected OSM feature set (<c>RoadSmoothingParameters.BridgeObstacles</c>); obstacle typing is
/// unconditional (doc 17 §4a). Rail/water are never dipped (§3.5 L=0); water Z is the
/// v1 min-DEM-in-footprint estimate; the span's own way ids never report as obstacles under their own deck.
/// Detector-built road-vs-road crossings carry <see cref="BridgeObstacleKind.Road"/> + the OSM classes (P0-4).
/// </summary>
public class BridgeObstacleTypingPlannerTests
{
    private const float Tol = 0.05f;
    private const long CorridorWayId = 99001L;

    // A ground-level corridor (50,150)→(450,150) carrying a bridge span over [spanStart, spanEnd] m.
    private static ParameterizedRoadSpline BuildCorridor(
        int splineId = 1, float spanStart = 100, float spanEnd = 200, int priority = 8002)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart,
            EndDistance = spanEnd,
            Type = StructureType.Bridge,
            Layer = 1,
            OsmWayIds = { CorridorWayId },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new(50, 150), new(450, 150), priority: priority, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        return corridor;
    }

    private static void SetFlatElevation(UnifiedRoadNetwork network, int splineId, float z)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            cs.TargetElevation = z;
    }

    // A rail/water polyline crossing the span footprint vertically at x = crossingX (span is y ≈ 150).
    private static BridgeObstacleFeature BuildFeature(
        BridgeObstacleKind kind, float crossingX = 200, long osmId = 555L,
        bool electrified = false, bool navigable = false)
    {
        List<Vector2> points = [new(crossingX, 100), new(crossingX, 200)];
        return new BridgeObstacleFeature
        {
            OsmId = osmId,
            Kind = kind,
            Electrified = electrified,
            Navigable = navigable,
            Points = points,
            Min = new Vector2(crossingX, 100),
            Max = new Vector2(crossingX, 200),
        };
    }

    private static void EnableTyping(ParameterizedRoadSpline corridor, params BridgeObstacleFeature[] features)
    {
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions();
        corridor.Parameters.BridgeObstacles = new BridgeObstacleSet(features);
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    // ── Synthetic rail obstacle ──────────────────────────────────────────────────────────────────────────

    // The span is 100 m → structural depth = clamp(100/20, 0.45, 2.0) = 2.0 (the typed budget's depth is
    // clamped to the RENDERED deck thickness — no phantom girder, render review #2).
    private const float Depth = 2f;

    [Fact]
    public void RailUnderSpan_EmitsSyntheticCrossing_AndRaisesDeck()
    {
        // Electrified rail at terrain z=0 under a low corridor (approaches 0) → S = 6.0 + depth 2 = 8;
        // requiredDeckZ 8 exceeds the approaches by ≥ C on both sides → Rule 1 ramp raise. The crossing is
        // synthetic (no lower spline).
        var corridor = BuildCorridor();
        EnableTyping(corridor, BuildFeature(BridgeObstacleKind.Rail, electrified: true));

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 0f);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        Assert.Equal(6f + Depth, span.RequiredDeckZ, Tol); // rail 0 + electrified 6.0 + depth 5
        Assert.Equal(Depth, span.StructuralDepthMeters, Tol);

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Equal(6f + Depth, crossing.RequiredSeparationMeters, Tol);
        Assert.Equal(BridgeObstacleKind.Rail, crossing.Crossing.LowerKind);
        Assert.True(crossing.Crossing.LowerElectrified);
        Assert.False(crossing.Crossing.HasLowerSpline);
        Assert.Equal(-1, crossing.Crossing.LowerSplineId);
    }

    [Fact]
    public void RailUnderSpan_NoRamp_IsNeverDipped_DeckRaisedInstead()
    {
        // High corridor (11) over non-electrified rail at 8: S = 5.0 + depth 2 = 7 → required 15, only 4
        // above the approaches (< C 5) → NOT a ramp. Rail can never be lowered (§3.5 L=0) — the non-ramp
        // path must produce a veto-style raise to 15, NEVER a DipLowerRoad.
        var corridor = BuildCorridor();
        EnableTyping(corridor, BuildFeature(BridgeObstacleKind.Rail));

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 11f);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 8f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        Assert.Equal(15f, span.RequiredDeckZ, Tol); // rail 8 + non-electrified 5.0 + depth 2

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(0f, crossing.DipDepthMeters, Tol);
    }

    // ── Synthetic water obstacle (v1 surface Z = min DEM in footprint) ───────────────────────────────────

    [Fact]
    public void WaterUnderSpan_UsesMinDemInsideFootprint_AsSurfaceZ()
    {
        // Terrain is 10 everywhere except a 4 m-deep channel where the stream passes under the deck. The
        // v1 water surface Z must be the MIN DEM inside the footprint (4), not the bank value (10):
        // deck = 4 + freeboard 2.0 + depth 5 = 11.
        var corridor = BuildCorridor();
        EnableTyping(corridor, BuildFeature(BridgeObstacleKind.Water, navigable: false));

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 0f);

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);
        for (var y = 140; y <= 160; y++)
        for (var x = 195; x <= 205; x++)
            hm[y, x] = 4f; // the channel bed under the crossing

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        Assert.Equal(4f + 2f + Depth, span.RequiredDeckZ, Tol); // bed 4 + freeboard 2 + depth 5, NOT bank 10

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeObstacleKind.Water, crossing.Crossing.LowerKind);
        Assert.False(crossing.Crossing.LowerNavigable);
    }

    [Fact]
    public void NavigableWater_GetsNavigationClearance()
    {
        // Navigable waterway (canal) at bed z=0 → S = 5.25 + depth 5 = 10.25.
        var corridor = BuildCorridor();
        EnableTyping(corridor, BuildFeature(BridgeObstacleKind.Water, navigable: true));

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 0f);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());

        Assert.Equal(5.25f + Depth, Assert.Single(plan.Spans).RequiredDeckZ, Tol);
        var crossing = Assert.Single(plan.Crossings);
        Assert.True(crossing.Crossing.LowerNavigable);
        Assert.Equal(5.25f + Depth, crossing.RequiredSeparationMeters, Tol);
    }

    // ── A2: terrain is NOT an obstacle in typed mode (spec §3.1 terrain = 0, doc-20 floating-deck fix) ──

    [Fact]
    public void TypedMode_TerrainHill_NeverRaisesDeck()
    {
        // An 11 m hill under the span, NO road/rail/water obstacle. Terrain is never an obstacle under
        // typing (§3.1 = 0): the deck follows the road profile (stay un-raised) — raising it to
        // terrainMax + structural depth is the render-review-#1 floating-deck bug (394: pinZ 18.05 vs
        // approaches 14.5). High ground is the excavator's / Phase B's job.
        var corridor = BuildCorridor();
        EnableTyping(corridor); // typing on, empty obstacle set

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 0f);

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);
        for (var y = 140; y <= 160; y++)
        for (var x = 140; x <= 260; x++)
            hm[y, x] = 11f; // hill plateau under the span footprint

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f); // terrain clearance at its default (on)

        var span = Assert.Single(plan.Spans);
        Assert.False(span.IsRaised);
        Assert.Empty(span.Pins); // terrain never sampled as an obstacle
    }

    // ── A2: typed budget on ROAD crossings ───────────────────────────────────────────────────────────────

    [Fact]
    public void TypedRoadCrossing_UsesRoadClearancePlusStructuralDepth()
    {
        // With typing ON, a road-vs-road crossing budgets S = 4.70 + depth 5 = 9.70 (not the legacy 5).
        var corridor = BuildCorridor();
        EnableTyping(corridor); // empty obstacle set — the road is detected via the spline crossing
        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), priority: 8002);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetFlatElevation(network, corridor.SplineId, 0f);
        SetFlatElevation(network, under.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(4.70f + Depth, Assert.Single(plan.Spans).RequiredDeckZ, Tol);
        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Equal(4.70f + Depth, crossing.RequiredSeparationMeters, Tol);
    }

    [Fact]
    public void NoBridgeRules_RoadCrossing_UsesDefaultTypedClearance()
    {
        // No BridgeRules at all → typing is still unconditional (doc 17 §4a); the planner falls back to the
        // default clearances: road 4.70 + structural depth (not the retired generic 5).
        var corridor = BuildCorridor();
        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), priority: 8002);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetFlatElevation(network, corridor.SplineId, 0f);
        SetFlatElevation(network, under.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.Equal(4.70f + Depth, span.RequiredDeckZ, Tol);
        Assert.Equal(4.70f + Depth, Assert.Single(plan.Crossings).RequiredSeparationMeters, Tol);
    }

    // ── Guards: self-obstacle ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpanOwnWayId_IsNeverItsOwnObstacle()
    {
        // The obstacle set contains a feature whose OsmId IS the span's way id — the ignore set
        // (StructureSegment.OsmWayIds) must drop it, else the deck would raise to clear itself.
        var corridor = BuildCorridor();
        EnableTyping(corridor, BuildFeature(BridgeObstacleKind.Rail, osmId: CorridorWayId));

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 0f);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());

        Assert.Empty(plan.Crossings);
        Assert.False(Assert.Single(plan.Spans).IsRaised);
    }

    [Fact]
    public void RoadFeaturesInObstacleSet_AreNotDoubleReported()
    {
        // A Road-kind feature under the span is skipped by the synthetic path — generated roads are already
        // detected via the grade-separated crossings; double-reporting would double-clear.
        var corridor = BuildCorridor();
        EnableTyping(corridor, BuildFeature(BridgeObstacleKind.Road));

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);
        SetFlatElevation(network, corridor.SplineId, 0f);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        var plan = BridgeElevationPlanner.Plan(network, hm, 1f, NoTerrain());

        Assert.Empty(plan.Crossings);
    }

    // ── Detector-built crossings carry typing metadata (A1, review P0-4) ─────────────────────────────────

    [Fact]
    public void DetectorCrossing_CarriesRoadKind_AndOsmClasses()
    {
        var corridor = BuildCorridor();
        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), osmRoadType: "residential", priority: 50);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);

        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(BridgeObstacleKind.Road, crossing.LowerKind);
        Assert.True(crossing.HasLowerSpline);
        Assert.Equal("primary", crossing.UpperOsmClass);   // helper default
        Assert.Equal("residential", crossing.LowerOsmClass);
    }
}
