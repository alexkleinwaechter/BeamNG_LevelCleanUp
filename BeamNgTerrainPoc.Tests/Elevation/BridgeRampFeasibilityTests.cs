using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// V2 Phase A4 (plan doc 01, spec R4.5): ramp-length feasibility. <c>MeasureRampLength</c> generalizes the
/// resolver's junction clamp (stops at way ends, junctions − 8 m margin, the next structure span);
/// <c>R_max = rampLen·maxSlope(class)</c>, <c>L_max = min(maxCut, rampLen·maxSlope)</c>, junction-in-sag ⇒
/// L_max = 0, with R4 escalation: absolute slopes + hard cut → reduced road clearance (4.2 m) + warn →
/// over-steep deck raise + warn. Gated on <c>EnableRampFeasibility</c>.
/// </summary>
public class BridgeRampFeasibilityTests
{
    private const float Tol = 0.05f;

    // Non-ramp scenario: corridor approaches+deck at 10, under-road at 8 (crossing ≈ station 50 on the
    // under-road), base C = 5 → deficit D = 3.
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor, ParameterizedRoadSpline under)
        BuildScenario(
            string upperClass, string lowerClass, BridgeRuleSystemOptions rules,
            float spanStart = 100, float spanEnd = 200,
            float underElev = 8f, int underPriority = 8002)
    {
        var span = new StructureSegment
        {
            StartDistance = spanStart, EndDistance = spanEnd, Type = StructureType.Bridge, Layer = 1,
            OsmWayIds = { 99001L },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), osmRoadType: upperClass, priority: 8002,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = rules.WithTestClearance();

        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), osmRoadType: lowerClass, priority: underPriority);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = 10f;
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = underElev;
        return (network, corridor, under);
    }

    private static void AddManualJunction(
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, float station)
    {
        var cs = network.GetCrossSectionsForSpline(spline.SplineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();
        var junction = new NetworkJunction { Position = cs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = cs, Spline = spline });
        network.Junctions.Add(junction);
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    // ── MeasureRampLength ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MeasureRampLength_StopsAtWayEnds()
    {
        // The detector marks the corridor's dead ends as Endpoint junctions, so the ramp room is the
        // way-end distance less the 2 m junction margin: 100−2 / 200−2.
        var (network, corridor, _) = BuildScenario("primary", "primary", new BridgeRuleSystemOptions());

        Assert.Equal(98f, BridgeElevationPlanner.MeasureRampLength(network, corridor, 100f, forward: false), 1f);
        Assert.Equal(198f, BridgeElevationPlanner.MeasureRampLength(network, corridor, 200f, forward: true), 1f);
    }

    [Fact]
    public void MeasureRampLength_StopsAtJunction_LessMargin()
    {
        var (network, corridor, _) = BuildScenario("primary", "primary", new BridgeRuleSystemOptions());
        AddManualJunction(network, corridor, station: 150f);

        // From station 100 forward: junction at ~150 − 2 m margin → ~48 m of ramp room.
        var len = BridgeElevationPlanner.MeasureRampLength(network, corridor, 100f, forward: true);
        Assert.Equal(48f, len, 1f);

        // The junction is not in the backward direction → the way end (endpoint junction − margin) governs.
        Assert.Equal(98f, BridgeElevationPlanner.MeasureRampLength(network, corridor, 100f, forward: false), 1f);
    }

    [Fact]
    public void MeasureRampLength_JunctionWithinMargin_IsZero()
    {
        var (network, corridor, _) = BuildScenario("primary", "primary", new BridgeRuleSystemOptions());
        AddManualJunction(network, corridor, station: 101f);

        // A junction ~1 m ahead is inside the 2 m margin → no ramp room.
        Assert.Equal(0f, BridgeElevationPlanner.MeasureRampLength(network, corridor, 100f, forward: true), Tol);
    }

    [Fact]
    public void MeasureRampLength_StopsAtNextStructureSpan()
    {
        var spanA = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 1L }
        };
        var spanB = new StructureSegment
        {
            StartDistance = 240, EndDistance = 300, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 2L }
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), mergeStructuresIntoCorridor: true,
            structureSegments: [spanA, spanB]);
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, corridor);

        // Forward from span A's end (200): the next deck starts at 240 → 40 m of approach.
        Assert.Equal(40f, BridgeElevationPlanner.MeasureRampLength(network, corridor, 200f, forward: true), Tol);
        // Backward from span B's start (240): span A ends at 200 → the same 40 m gap.
        Assert.Equal(40f, BridgeElevationPlanner.MeasureRampLength(network, corridor, 240f, forward: false), Tol);
    }

    // ── Distribution under caps ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DipCappedByMaxCut_RemainderMovesToDeck()
    {
        // Motorway over residential (Δp +3 → ideal raise 0.6 / dip 2.4), but MaxCutDepth = 1.0 caps the dip;
        // the remaining 1.4 m moves to the deck (raise room is ample): raise 2.0 / dip 1.0.
        var rules = new BridgeRuleSystemOptions
        {
            EnablePriorityDistribution = true, EnableRampFeasibility = true, MaxCutDepthMeters = 1.0f,
        };
        var (network, _, _) = BuildScenario("motorway", "residential", rules);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.Split, crossing.Action);
        Assert.Equal(12f, crossing.DeckTargetZ, Tol);     // 10 + 2.0
        Assert.Equal(1f, crossing.DipDepthMeters, Tol);   // capped at MaxCutDepth
        Assert.Equal(7f, crossing.LowerRoadTargetZ, Tol); // 8 − 1
        Assert.Null(crossing.Warning);                    // normal-slope refill sufficed
    }

    [Fact]
    public void JunctionInSag_LowerRoadNeverDipped_DeckTakesAll()
    {
        // A junction sits ON the lower road at the crossing station → L_max = 0 (junction-in-sag rule):
        // the whole 3 m deficit moves to the deck.
        var rules = new BridgeRuleSystemOptions
        {
            EnablePriorityDistribution = true, EnableRampFeasibility = true,
        };
        var (network, _, under) = BuildScenario("motorway", "residential", rules);
        AddManualJunction(network, under, station: 50f); // the crossing is at ≈ station 50

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(13f, crossing.DeckTargetZ, Tol); // 8 + 5 — all on the deck
        Assert.Equal(0f, crossing.DipDepthMeters, Tol);
    }

    [Fact]
    public void Infeasible_EscalatesThroughReducedClearance_AndWarns()
    {
        // Almost no ramp room anywhere: span [10,390] leaves 10 m of corridor approach (primary: normal
        // 0.5 m, absolute 0.6 m of raise) and the cut limits are 0.5 m. D = 3 → normal+absolute reach
        // 0.6 + 0.5 = 1.1; R4 step 7 shaves 0.8 m off the clearance (5 → 4.2, warn); the final 1.1 m goes
        // onto the deck as an over-steep last resort (warn).
        var rules = new BridgeRuleSystemOptions
        {
            EnablePriorityDistribution = true, EnableRampFeasibility = true,
            MaxCutDepthMeters = 0.5f, MaxCutDepthHardMeters = 0.5f,
        };
        var (network, _, _) = BuildScenario("primary", "primary", rules, spanStart: 10, spanEnd: 390);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.Split, crossing.Action);
        Assert.Equal(4.2f, crossing.RequiredSeparationMeters, Tol); // reduced clearance applied
        Assert.Equal(0.5f, crossing.DipDepthMeters, Tol);           // hard cut limit
        Assert.Equal(11.7f, crossing.DeckTargetZ, Tol);             // 10 + 0.6 abs + 1.1 forced
        Assert.NotNull(crossing.Warning);
        Assert.Contains("infeasible", crossing.Warning);
    }

    [Fact]
    public void FeasibilityOff_SharesUnclamped()
    {
        // Same MaxCutDepth, but the feasibility flag is OFF → the ideal §3.5 shares stand (dip 2.4 > cut limit).
        var rules = new BridgeRuleSystemOptions
        {
            EnablePriorityDistribution = true, MaxCutDepthMeters = 1.0f,
        };
        var (network, _, _) = BuildScenario("motorway", "residential", rules);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(2.4f, Assert.Single(plan.Crossings).DipDepthMeters, Tol);
    }

    // ── Rule-1 infeasible → dip lower-priority under-road (spec 2026-07-01) ───────────────────────────────

    [Fact]
    public void Rule1_Infeasible_LowerPriorityRoad_Dips()
    {
        // isRamp fires: under-road at the corridor level (10) ⇒ the deck must climb a full clearance (5) above
        // both approaches. The long span [10,390] leaves ~8 m of approach ⇒ motorway absolute slope 5 % allows
        // only ~0.4 m of raise ⇒ the mandated 5 m raise is INFEASIBLE. The under-road is strictly lower
        // priority (3000 < 8002), so it must be DIPPED instead of raising the motorway.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, _, _) = BuildScenario(
            "motorway", "residential", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 3000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, crossing.Action);
        Assert.Equal(5f, crossing.DipDepthMeters, Tol);       // deficit = ob.Z 10 + sep 5 − approach 10
        Assert.Equal(5f, crossing.LowerRoadTargetZ, Tol);     // deckRef 10 − sep 5
        Assert.False(Assert.Single(plan.Spans).IsRaised);     // the motorway deck stays at approach level
        // Observability: the fallback carries an informational trace so the dipped span is visible in the
        // [BRIDGE-PLAN] WARN log (replacing the "Rule-1 raise exceeds ramp slope" line these spans lose).
        Assert.NotNull(crossing.Warning);
        Assert.Contains("dipped lower-priority", crossing.Warning);
    }

    [Fact]
    public void Rule1_Infeasible_EqualPriorityRoad_StillRaises()
    {
        // Same infeasible Rule-1 geometry, but the under-road is EQUAL priority (8002 == 8002) ⇒ NOT dippable
        // ⇒ the deck still raises and warns, exactly as before the fallback existed.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, _, _) = BuildScenario(
            "motorway", "motorway", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 8002);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.NotNull(crossing.Warning);
        Assert.Contains("ramp slope", crossing.Warning);
    }

    [Fact]
    public void Rule1_Feasible_LowerPriorityRoad_StillRaises()
    {
        // isRamp fires (under at corridor level) but the span [140,160] leaves ~138 m of approach ⇒ motorway
        // absolute slope 5 % allows ~6.9 m of raise ≥ the 5 m mandate ⇒ FEASIBLE. Only *infeasible* Rule-1
        // spans dip, so a lower-priority under-road here is still cleared by raising the deck.
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var (network, _, _) = BuildScenario(
            "motorway", "residential", rules,
            spanStart: 140, spanEnd: 160, underElev: 10f, underPriority: 3000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Null(crossing.Warning);                    // feasible ⇒ no over-steep warning
        Assert.True(Assert.Single(plan.Spans).IsRaised);
    }

    [Fact]
    public void Rule1_Infeasible_FeasibilityFlagOff_StillRaises()
    {
        // EnableRampFeasibility OFF ⇒ no feasibility test is computed ⇒ dipFallback can never trigger ⇒
        // byte-identical old behavior: full Rule-1 raise, no warning.
        var rules = new BridgeRuleSystemOptions();
        var (network, _, _) = BuildScenario(
            "motorway", "residential", rules,
            spanStart: 10, spanEnd: 390, underElev: 10f, underPriority: 3000);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridge, crossing.Action);
        Assert.Null(crossing.Warning);
    }

    [Fact]
    public void Rule1_Infeasible_MixedObstacles_RaisesForPeer_DipsLowerRoad()
    {
        // Infeasible Rule-1 span crossing TWO under-roads: one equal priority (non-dippable ⇒ raises the deck)
        // and one strictly lower priority (dippable ⇒ dips). The deck must end up raised (for the peer) and the
        // lower-priority road must be dipped.
        //
        // The peer sits at elevation 10 ⇒ the deck is forced up to 10 + clearance(5) = 15. The lower-priority
        // road is placed at 12 (above the corridor's own 10, but still under the equal-priority peer's 15-deck
        // requirement) so that, after ReconcileDipAgainstDeck measures its dip against the FINAL deck (15), a
        // real positive dip survives: roadTarget = 15 − 5 = 10 < obstacleZ 12 ⇒ dip = 2. (A same-elevation lower
        // road at 10 would fully clear once the deck is raised for the peer — dip 0 ⇒ AlreadyClears — which
        // would not exercise the DipLowerRoad path this test targets.)
        var span = new StructureSegment
        {
            StartDistance = 10, EndDistance = 390, Type = StructureType.Bridge, Layer = 1,
            OsmWayIds = { 99001L },
        };
        var rules = new BridgeRuleSystemOptions { EnableRampFeasibility = true };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), osmRoadType: "motorway", priority: 8002,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules = rules;

        // Peer under-road: equal priority (non-dippable ⇒ deck must raise for it).
        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), osmRoadType: "motorway", priority: 8002);
        under.Layer = 0;

        // Second under-road, strictly lower priority, crossing the corridor at x=300 (inside the span).
        var lower = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(300, 100), new(300, 200), osmRoadType: "residential", priority: 3000);
        lower.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under, lower);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = 10f;
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = 10f;
        foreach (var cs in network.GetCrossSectionsForSpline(lower.SplineId))
            cs.TargetElevation = 12f;

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.True(Assert.Single(plan.Spans).IsRaised); // raised for the equal-priority peer
        var dipCrossing = Assert.Single(
            plan.Crossings, c => c.Action == BridgeElevationAction.DipLowerRoad);
        Assert.Equal(2f, dipCrossing.DipDepthMeters, Tol);   // 12 − (15 − 5)
        Assert.Equal(10f, dipCrossing.LowerRoadTargetZ, Tol); // 15 − 5
        Assert.Contains(plan.Crossings, c => c.Action == BridgeElevationAction.RaiseBridge);
    }
}
