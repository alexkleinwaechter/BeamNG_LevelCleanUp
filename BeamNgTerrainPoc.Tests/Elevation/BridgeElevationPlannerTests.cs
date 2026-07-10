using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Phase B (plan doc 14 §4, §11): the pure <see cref="BridgeElevationPlanner"/> rule engine. Each test builds a
/// merged-corridor bridge span over an obstacle (a road and/or terrain) with controlled cross-section
/// elevations, runs the planner, and asserts the exact deck/road outcome — Rule 1 (ramp ⇒ raise), Rule 2
/// (dip / veto-raise), Rule 3 (equal split), the terrain-max obstacle, the span-vs-span tie-break, multi-span,
/// and the flag-off / empty-span early return. The planner mutates nothing.
/// </summary>
public class BridgeElevationPlannerTests
{
    private const float Tol = 0.05f;

    // Clean round road clearance (5 m, no structural depth) for readable raise/dip arithmetic. Typing is
    // unconditional (doc 17 §4a); real typed budgets are covered by BridgeObstacleTypingPlannerTests.
    private static BridgeRuleSystemOptions TestClearance() => new()
    {
        RoadClearanceMeters = 5f,
        StructuralDepthMinMeters = 0f,
        StructuralDepthMaxMeters = 0f,
    };

    // ── Builders ─────────────────────────────────────────────────────────────────────────────────────────

    // A long ground-level corridor (layer 0, not a whole-spline bridge) carrying a bridge StructureSegment over
    // the arc range [spanStart, spanEnd] m at layer `spanLayer`. Cross-sections are spaced 1 m; their
    // TargetElevation is filled by `approachZ` outside the span and `deckBaseZ` inside it (the flattened deck
    // the smoother produced — what the planner must lift).
    private static ParameterizedRoadSpline BuildCorridor(
        int splineId, float spanStart, float spanEnd,
        int priority = 8002, int spanLayer = 1, float length = 400f,
        long wayId = 99001L)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart,
            EndDistance = spanEnd,
            Type = StructureType.Bridge,
            Layer = spanLayer,
            OsmWayIds = { wayId },
        };

        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new(50, 150), new(50 + length, 150), priority: priority, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        // Obstacle typing is unconditional (doc 17 §4a). These decision-logic tests use a clean round
        // road clearance (5 m, no structural depth) so the raise/dip arithmetic stays readable; the real
        // typed budgets (4.7 + span/20 depth, rail, water) are covered by BridgeObstacleTypingPlannerTests.
        corridor.Parameters.BridgeRules = TestClearance();
        return corridor;
    }

    private static ParameterizedRoadSpline BuildUnderRoad(
        int splineId, float crossingX, int priority = 8002, float roadWidth = 8f)
    {
        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new(crossingX, 100), new(crossingX, 200), priority: priority, roadWidth: roadWidth);
        under.Layer = 0;
        return under;
    }

    // Sets every cross-section of a spline to a flat elevation.
    private static void SetFlatElevation(UnifiedRoadNetwork network, int splineId, float z)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            cs.TargetElevation = z;
    }

    // Sets the corridor: approaches at approachZ, span interior at deckBaseZ (the flattened deck).
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

    private static SpanDeckPlan SingleSpan(BridgeElevationPlan plan) => Assert.Single(plan.Spans);

    // ── Rule 1 — ramp ⇒ raise the deck, dip nothing ──────────────────────────────────────────────────────

    [Fact]
    public void Rule1_Ramp_RaisesDeckToClearObstacle_LeavesRoadAlone()
    {
        // Corridor crosses an equal-priority road at x=200 (≈150 m along, inside the span [100,200]). The
        // under-road sits at z=0; the approaches are also at 0 (a flat-looking corridor) but the obstacle forces
        // requiredDeckZ = 0 + 5 = 5, which exceeds the approaches by > C on both sides ⇒ a ramp ⇒ Rule 1.
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 200);
        var under = BuildUnderRoad(2, crossingX: 200);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 0f, deckBaseZ: 0f);
        SetFlatElevation(network, under.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network);

        var span = SingleSpan(plan);
        Assert.True(span.IsRaised);
        Assert.Equal(5f, span.RequiredDeckZ, Tol); // underRoad 0 + C 5
        Assert.NotEmpty(span.Pins);
        Assert.All(span.Pins, p => Assert.Equal(5f, p.DeckZ, Tol));

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Equal(5f, crossing.DeckTargetZ, Tol);
        Assert.Equal(0f, crossing.DipDepthMeters, Tol); // the road was NOT dipped
    }

    [Fact]
    public void Rule1_Pins_CoverEverySpanSection_AtTheDeckZ()
    {
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 150);
        var under = BuildUnderRoad(2, crossingX: 175); // ≈125 m along, inside [100,150]

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 150, approachZ: 0f, deckBaseZ: 0f);
        SetFlatElevation(network, under.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network);
        var span = SingleSpan(plan);

        var spanSectionCount = network.GetCrossSectionsForSpline(corridor.SplineId)
            .Count(cs => cs.DistanceAlongSpline >= 100 && cs.DistanceAlongSpline <= 150);
        Assert.Equal(spanSectionCount, span.Pins.Count);
        // Every pin references a real span cross-section (Phase C sets PinnedElevation on these).
        Assert.All(span.Pins, p => Assert.NotNull(p.Section));
    }

    // ── Rule 2 — no ramp, lower-priority road loses ⇒ dip it ─────────────────────────────────────────────

    [Fact]
    public void Rule2_NoRamp_LowerPriorityRoad_IsDipped_DeckNotRaised()
    {
        // The deck already sits high (approaches & deck at z=10) so it is NOT a ramp (nothing to clear by
        // rising). A lower-priority road crosses at z=8 → only 2 m below a deck needing 5 m clearance ⇒ dip it
        // the 3 m deficit. The deck stays put.
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 200, priority: 8002);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 50); // lower priority than 8002

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 8f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = SingleSpan(plan);
        Assert.False(span.IsRaised);
        Assert.Empty(span.Pins);

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, crossing.Action);
        Assert.Equal(3f, crossing.DipDepthMeters, Tol); // (8 + 5) − 10 = 3
        Assert.Equal(5f, crossing.LowerRoadTargetZ, Tol); // 8 − 3 = 5 (i.e. deck 10 − C 5)
    }

    [Fact]
    public void Rule2_NoRamp_LowerRoadAlreadyClears_NoMove()
    {
        // Deck at z=10, road far below at z=2 → clearance 8 ≥ C 5 → nothing to do.
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 200, priority: 8002);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 2f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.False(SingleSpan(plan).IsRaised);
        Assert.Equal(BridgeElevationAction.AlreadyClears, Assert.Single(plan.Crossings).Action);
    }

    // ── Rule 2 veto — the lower road outranks the bridge ⇒ raise the deck ────────────────────────────────

    [Fact]
    public void Rule2Veto_HigherPriorityRoadUnderBridge_RaisesDeck_NotDipsRoad()
    {
        // A high-priority motorway (priority 100) passes under a lower-priority footbridge span (priority 25).
        // Dipping the motorway is vetoed; the deck is raised over it instead. The deck base sits at 10 (no
        // ramp), the road at z=8 → deck must reach 8 + 5 = 13.
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 200, priority: 25);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 100);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 8f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = SingleSpan(plan);
        Assert.True(span.IsRaised);
        Assert.Equal(13f, span.RequiredDeckZ, Tol); // 8 + 5

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(13f, crossing.DeckTargetZ, Tol);
        Assert.Equal(0f, crossing.DipDepthMeters, Tol); // the motorway is NOT dipped
    }

    // ── Rule 3 — equal priority, no ramp ⇒ split the deficit ─────────────────────────────────────────────

    [Fact]
    public void Rule3_EqualPriority_NoRamp_SplitsDeckRaiseAndRoadDip()
    {
        // Equal priority (8002 = 8002), deck base at 10 (no ramp), road at z=8 → deficit (8+5)−10 = 3. With a
        // 0.5 split the deck rises 1.5 (to 11.5) and the road dips 1.5 (to 6.5) — together they reach C=5.
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 200, priority: 8002);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 8002);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 8f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = SingleSpan(plan);
        Assert.True(span.IsRaised);
        Assert.Equal(11.5f, span.RequiredDeckZ, Tol); // 10 + 0.5·3

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.Split, crossing.Action);
        Assert.Equal(11.5f, crossing.DeckTargetZ, Tol);
        // After the deck is pinned at 11.5, the residual dip the road still needs = (8+5) − 11.5 = 1.5.
        Assert.Equal(1.5f, crossing.DipDepthMeters, Tol);
        Assert.Equal(6.5f, crossing.LowerRoadTargetZ, Tol);
    }

    [Fact]
    public void Rule3_SplitRatio_BiasedTowardRaising_LiftsDeckMore()
    {
        var corridor = BuildCorridor(1, spanStart: 100, spanEnd: 200, priority: 8002);
        var under = BuildUnderRoad(2, crossingX: 200, priority: 8002);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        SetCorridorElevation(network, corridor, 100, 200, approachZ: 10f, deckBaseZ: 10f);
        SetFlatElevation(network, under.SplineId, 8f);

        var plan = BridgeElevationPlanner.Plan(network,
            options: new BridgeElevationPlannerOptions { GradeSepSplitRatio = 0.7f });

        // deficit 3 → raise 0.7·3 = 2.1 → deck 12.1; residual dip (13 − 12.1) = 0.9.
        Assert.Equal(12.1f, SingleSpan(plan).RequiredDeckZ, Tol);
        Assert.Equal(0.9f, Assert.Single(plan.Crossings).DipDepthMeters, Tol);
    }

    // Terrain is never an obstacle under typing (§3.1 clearance 0) — the legacy terrain-max-under-span
    // raise tests were removed with the terrain-max path (doc 17 §4a).

    // ── Span-vs-span tie-break — a higher deck is not an "under" obstacle for the lower span ─────────────

    [Fact]
    public void SpanVsSpan_UpperClearsLowerDeck_LowerSpanNotRaisedByUpper()
    {
        // Two crossing corridors, both carrying bridge spans: the upper on layer 2, the lower on layer 1. The
        // upper must clear the lower deck (z=6) → 6 + 5 = 11. The lower span must NOT be raised on account of the
        // upper (the upper is above it) — it only sees terrain/nothing, so it stays un-raised.
        var upper = BuildCorridor(1, spanStart: 100, spanEnd: 200, priority: 8002, spanLayer: 2, wayId: 1L);
        var lower = BuildCorridorVertical(2, crossingX: 200, spanStart: 20, spanEnd: 80, spanLayer: 1, wayId: 2L);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(upper, lower);
        SetCorridorElevation(network, upper, 100, 200, approachZ: 0f, deckBaseZ: 0f);
        SetVerticalCorridorElevation(network, lower, 20, 80, approachZ: 6f, deckBaseZ: 6f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(2, plan.Spans.Count);
        var upperSpan = plan.Spans.Single(s => s.OwnerSplineId == upper.SplineId);
        var lowerSpan = plan.Spans.Single(s => s.OwnerSplineId == lower.SplineId);

        Assert.True(upperSpan.IsRaised);
        Assert.Equal(11f, upperSpan.RequiredDeckZ, Tol); // clears the lower deck (6) + C (5)

        Assert.False(lowerSpan.IsRaised); // the upper deck above it is not an obstacle to clear

        var crossing = Assert.Single(plan.Crossings); // only the upper-over-lower crossing
        Assert.Equal(upper.SplineId, crossing.Crossing.UpperSplineId);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
    }

    // ── Multi-span corridor — one plan entry per span ────────────────────────────────────────────────────

    [Fact]
    public void MultiSpanCorridor_PlansEachSpanIndependently()
    {
        var spanA = new StructureSegment
        {
            StartDistance = 80, EndDistance = 140, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 11L }
        };
        var spanB = new StructureSegment
        {
            StartDistance = 240, EndDistance = 300, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 22L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 8002, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [spanA, spanB]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = TestClearance();

        // Each span has its own under-road; both equal priority and flat → Rule 1 raise to obstacle + C.
        var underA = BuildUnderRoad(2, crossingX: 50 + 110); // ≈110 m along → span A
        var underB = BuildUnderRoad(3, crossingX: 50 + 270); // ≈270 m along → span B

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, underA, underB);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = 0f;
        SetFlatElevation(network, underA.SplineId, 0f);
        SetFlatElevation(network, underB.SplineId, 1f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(2, plan.Spans.Count);
        Assert.Equal(2, plan.Crossings.Count);
        var a = plan.Spans.Single(s => MathF.Abs(s.StartDistance - 80) < 1f);
        var b = plan.Spans.Single(s => MathF.Abs(s.StartDistance - 240) < 1f);
        Assert.Equal(5f, a.RequiredDeckZ, Tol); // road 0 + C 5
        Assert.Equal(6f, b.RequiredDeckZ, Tol); // road 1 + C 5
    }

    // ── Flag-off / empty-span early return ───────────────────────────────────────────────────────────────

    [Fact]
    public void FlagOff_NoStructureSegments_ReturnsEmptyPlan()
    {
        // Plain crossing roads, merge flag off → no spans → empty plan, even though there IS a junction/crossing.
        var a = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(50, 150), new(250, 150));
        var b = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 50), new(150, 250));
        b.Layer = 1; // make it grade-separated so a crossing exists

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(a, b);
        SetFlatElevation(network, a.SplineId, 0f);
        SetFlatElevation(network, b.SplineId, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.True(plan.IsEmpty);
    }

    // ── Helpers for the span-vs-span case (a vertical corridor carrying a span) ──────────────────────────

    private static ParameterizedRoadSpline BuildCorridorVertical(
        int splineId, float crossingX, float spanStart, float spanEnd, int spanLayer, long wayId)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart, EndDistance = spanEnd,
            Type = StructureType.Bridge, Layer = spanLayer, OsmWayIds = { wayId },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId, new(crossingX, 100), new(crossingX, 200), priority: 8002, isBridge: false,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        return corridor;
    }

    private static void SetVerticalCorridorElevation(
        UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, float spanStart, float spanEnd,
        float approachZ, float deckBaseZ)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
        {
            var inSpan = cs.DistanceAlongSpline >= spanStart && cs.DistanceAlongSpline <= spanEnd;
            cs.TargetElevation = inSpan ? deckBaseZ : approachZ;
        }
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();
}
