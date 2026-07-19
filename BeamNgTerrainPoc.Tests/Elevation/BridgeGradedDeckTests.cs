using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// Graded deck profile (render review #3, bridge 762726404): with <c>EnableGradedDeck</c> the deck is pinned
/// along the CHORD between the two approach elevations plus a uniform lift = the worst station-local obstacle
/// deficit (0 when everything clears). A flat deck on a sloping road can never meet both approaches — one end
/// pokes above the road, the other sits below it. Flag off = legacy flat pins, byte-identical.
/// </summary>
public class BridgeGradedDeckTests
{
    private const float Tol = 0.05f;

    // Corridor (50,150)→(450,150), span [100,200]. Approaches slope: left side at zLeft, right side at
    // zRight, span interior NaN-free at the chord midpoint value (irrelevant — the planner reads approaches).
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor) BuildSlopedScenario(
        float zLeft, float zRight, bool graded, float underZ = float.NaN, int underPriority = 50)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 99001L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = new BridgeRuleSystemOptions { EnableGradedDeck = graded }.WithTestClearance();

        ParameterizedRoadSpline? under = null;
        if (!float.IsNaN(underZ))
        {
            under = RoadNetworkTestHelpers.CreateParameterizedSpline(
                2, new(200, 100), new(200, 200), priority: underPriority); // crosses at span station 150
            under.Layer = 0;
        }

        var network = under != null
            ? RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under)
            : RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);

        // Corridor: linear grade from zLeft (station 0) to zRight (station 400).
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = zLeft + (zRight - zLeft) * (cs.DistanceAlongSpline / 400f);
        if (under != null)
            foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
                cs.TargetElevation = underZ;

        return (network, corridor);
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    [Fact]
    public void GradedDeck_AlreadyClearing_NotRaised_NoPins()
    {
        // Descending corridor 22 → 14 (chord through the span ≈ 17–19), under-road far below at 2:
        // chord clears 2 + 5 everywhere → lift 0 → un-raised, no pins, flush seams by construction.
        var (network, _) = BuildSlopedScenario(zLeft: 22f, zRight: 14f, graded: true, underZ: 2f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.False(span.IsRaised);
        Assert.Empty(span.Pins);
        Assert.Equal(BridgeElevationAction.AlreadyClears, Assert.Single(plan.Crossings).Action);
    }

    [Fact]
    public void GradedDeck_Lift_PinsFollowChordPlusUniformLift()
    {
        // Corridor grade 10 → 6 over 400 m; chord over the span: 9.0 (st 100) → 8.0 (st 200), crossing at
        // station 150 → chord 8.5. Under-road at 8 needs 13 → station-local deficit 4.5 ≥ ... < C 5 → not
        // a ramp; equal-ish priorities? Use lower priority road (binary dip would fire) — so use a HIGHER
        // priority under-road to force the veto-raise: deck = chord + 4.5 lift everywhere.
        var (network, _) = BuildSlopedScenario(
            zLeft: 10f, zRight: 6f, graded: true, underZ: 8f, underPriority: 20000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(13f, crossing.DeckTargetZ, 0.1f); // 8 + 5, at the crossing station

        // Pins follow the chord + uniform lift (4.5): station 100 → 9.0+4.5 = 13.5; station 200 → 8.0+4.5
        // = 12.5; midpoint → 13.0. The deck has the road's grade, not a horizontal plateau.
        var pins = span.Pins.OrderBy(p => p.DistanceAlongSpline).ToList();
        Assert.NotEmpty(pins);
        Assert.Equal(13.5f, pins.First().DeckZ, 0.1f);
        Assert.Equal(12.5f, pins.Last().DeckZ, 0.1f);
        var mid = pins.OrderBy(p => MathF.Abs(p.DistanceAlongSpline - 150f)).First();
        Assert.Equal(13.0f, mid.DeckZ, 0.1f);

        // Constant grade: strictly monotone, no plateau-then-step.
        for (var i = 1; i < pins.Count; i++)
            Assert.True(pins[i].DeckZ <= pins[i - 1].DeckZ + 0.001f, "graded pins must descend with the chord");
    }

    [Fact]
    public void FlagOff_SameScenario_KeepsFlatPins()
    {
        var (network, _) = BuildSlopedScenario(
            zLeft: 10f, zRight: 6f, graded: false, underZ: 8f, underPriority: 20000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        // Legacy: one horizontal deck Z for every pin (13 = 8 + 5).
        Assert.All(span.Pins, p => Assert.Equal(13f, p.DeckZ, Tol));
    }

    [Fact]
    public void ApproachRampPins_EaseFromDeckEnd_ToNaturalProfile()
    {
        // The veto-lift scenario: deck ends at 13.5 (station 100) / 12.5 (station 200) while the natural
        // road runs 9.0 / 8.0 there — delta ≈ 4.5. Approach-ramp pins must hold the engineered climb on
        // BOTH approaches: ≈ deck-end Z just outside the abutment, back at the natural profile at the ramp
        // end. Primary → 5 % tangent → LengthFor(4.5, 0.05) = 120 m: the back ramp is clamped to the 100 m
        // of room before the way start; the forward ramp gets its full 120 m (stations 200–320).
        var (network, corridor) = BuildSlopedScenario(
            zLeft: 10f, zRight: 6f, graded: true, underZ: 8f, underPriority: 20000);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());
        Assert.True(plan.Spans.Single().IsRaised);

        var pinned = UnifiedRoadSmoother.ApplyApproachRampPins(network, plan, null, hm, 1f);
        Assert.True(pinned > 0);

        var sections = network.GetCrossSectionsForSpline(corridor.SplineId)
            .OrderBy(c => c.DistanceAlongSpline).ToList();

        // Just outside the span start (station ≈ 99): pinned ≈ the deck-end Z (13.5).
        var nearStart = sections.Last(c => c.DistanceAlongSpline < 100f);
        Assert.True(nearStart.PinnedElevation.HasValue, "approach next to the abutment must be pinned");
        Assert.Equal(13.5f, nearStart.PinnedElevation!.Value, 0.2f);

        // Just outside the span end (station ≈ 201): pinned ≈ the far deck-end Z (12.5).
        var nearEnd = sections.First(c => c.DistanceAlongSpline > 200f);
        Assert.True(nearEnd.PinnedElevation.HasValue);
        Assert.Equal(12.5f, nearEnd.PinnedElevation!.Value, 0.2f);

        // Mid-ramp (station ≈ 50, halfway down the clamped 100 m back ramp): on the constant-grade
        // tangent, half the delta remains (base 9.5 + 4.5·0.5 = 11.75).
        var midRamp = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - 50f)).First();
        Assert.True(midRamp.PinnedElevation.HasValue);
        Assert.InRange(midRamp.PinnedElevation!.Value, 10.5f, 12.5f);

        // Beyond the forward ramp (station > 320) the road is free.
        Assert.All(sections.Where(c => c.DistanceAlongSpline > 321f),
            c => Assert.False(c.PinnedElevation.HasValue));

        // The pinned ramp descends monotonically away from the deck on the back side.
        var backRamp = sections
            .Where(c => c.PinnedElevation.HasValue && c.DistanceAlongSpline < 100f)
            .OrderByDescending(c => c.DistanceAlongSpline).ToList();
        for (var i = 1; i < backRamp.Count; i++)
            Assert.True(backRamp[i].PinnedElevation!.Value <= backRamp[i - 1].PinnedElevation!.Value + 0.01f);
    }

    [Fact]
    public void ApproachRampPins_FlushDeck_PinsNothing()
    {
        // Un-raised graded span (already clears) → no deck pins → no approach ramps.
        var (network, _) = BuildSlopedScenario(zLeft: 22f, zRight: 14f, graded: true, underZ: 2f);
        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(512, 0f);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(0, UnifiedRoadSmoother.ApplyApproachRampPins(network, plan, null, hm, 1f));
    }

    [Fact]
    public void GradedDeck_RampCase_LiftAppliesOnBothEnds()
    {
        // Flat-ish low corridor (2 → 0) over a high-priority road at 4: required 9, chord ≈ 1.5/1.0 →
        // deficit ≈ 7.6 ≥ C 5 → Rule-1 ramp. Pins = chord + lift: both ends raised by the SAME lift, so
        // the deck keeps the chord's grade.
        var (network, _) = BuildSlopedScenario(
            zLeft: 2f, zRight: 0f, graded: true, underZ: 4f, underPriority: 20000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        Assert.Equal(BridgeElevationAction.RaiseBridge, Assert.Single(plan.Crossings).Action);

        var pins = span.Pins.OrderBy(p => p.DistanceAlongSpline).ToList();
        var gradeFirstToLast = pins.Last().DeckZ - pins.First().DeckZ;
        // chord drop over the span is (1.5 − 1.0) = −0.5 → the lifted deck preserves it.
        Assert.Equal(-0.5f, gradeFirstToLast, 0.1f);
    }
}
