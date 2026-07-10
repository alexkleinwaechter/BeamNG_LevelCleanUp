using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// V2 Phase A5 (plan doc 01, review P1-2): spans are planned in descending owner-priority order and each
/// raised span's pinned deck sections CARRY into later (lower-priority) spans as fixed obstacles — so a
/// flyover over an already-raised bridge clears the RAISED deck, not the stale pre-raise TargetElevation.
/// A pinned deck below is never dipped. Gated on <c>EnableSpanSolveOrder</c>; off = no order change, no carry.
/// </summary>
public class BridgeSpanSolveOrderTests
{
    private const float Tol = 0.05f;

    // Three-spline stack:
    //   lower corridor (priority 10000, span [100,200] layer 1) over an under-road at x=220 → raised to 5.
    //   upper corridor (priority 5500, span [60,140] layer 2) crosses the lower span at (180,150).
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline lower, ParameterizedRoadSpline upper)
        BuildStack(BridgeRuleSystemOptions rules, string upperClass = "residential")
    {
        var lowerSpan = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1, OsmWayIds = { 1L }
        };
        var lower = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), osmRoadType: "motorway", priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [lowerSpan]);
        lower.Layer = 0;
        lower.Parameters.BridgeRules = rules.WithTestClearance();

        var upperSpan = new StructureSegment
        {
            StartDistance = 60, EndDistance = 140, Type = StructureType.Bridge, Layer = 2, OsmWayIds = { 2L }
        };
        var upper = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(180, 50), new(180, 250), osmRoadType: upperClass, priority: 5500,
            mergeStructuresIntoCorridor: true, structureSegments: [upperSpan]);
        upper.Layer = 0;
        upper.Parameters.BridgeRules = rules;

        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(220, 100), new(220, 200), osmRoadType: "residential", priority: 50);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(lower, upper, under);
        foreach (var cs in network.CrossSections)
            cs.TargetElevation = 0f;
        return (network, lower, upper);
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    [Fact]
    public void CarryOn_LaterSpanClears_TheRaisedDeck_NotTheStaleElevation()
    {
        // Lower (high-priority) span raises to 5 (under-road 0 + C 5). With carry, the upper span must
        // clear the PINNED deck: 5 + 5 = 10 — not the lower spline's stale TargetElevation (0 + 5 = 5).
        var (network, lower, upper) = BuildStack(new BridgeRuleSystemOptions { EnableSpanSolveOrder = true });

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var lowerSpanPlan = plan.Spans.Single(s => s.OwnerSplineId == lower.SplineId);
        Assert.True(lowerSpanPlan.IsRaised);
        Assert.Equal(5f, lowerSpanPlan.RequiredDeckZ, Tol);

        var upperSpanPlan = plan.Spans.Single(s => s.OwnerSplineId == upper.SplineId);
        Assert.True(upperSpanPlan.IsRaised);
        Assert.Equal(10f, upperSpanPlan.RequiredDeckZ, Tol); // pinned lower deck 5 + C 5
    }

    [Fact]
    public void CarryOff_LaterSpanSeesOnly_TheStaleElevation()
    {
        // Identical stack, flag OFF: the upper span reads the lower spline's un-raised TargetElevation (0)
        // → deck 5. (This is exactly the under-clearance the A5 carry + A7 backstop exist for.)
        var (network, _, upper) = BuildStack(new BridgeRuleSystemOptions());

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(5f, plan.Spans.Single(s => s.OwnerSplineId == upper.SplineId).RequiredDeckZ, Tol);
    }

    [Fact]
    public void PinnedDeckBelow_IsNeverDipped_EvenByDistribution()
    {
        // Upper approaches at 8 → required 10 is only 2 above them (< C 5) → non-ramp. Distribution
        // (residential over motorway, Δp −3 → dip 20 %) would dip the pinned deck 0.4 m — the
        // LowerIsBridge guard must veto-raise instead.
        var rules = new BridgeRuleSystemOptions
        {
            EnableSpanSolveOrder = true, EnablePriorityDistribution = true,
        };
        var (network, lower, upper) = BuildStack(rules);
        foreach (var cs in network.GetCrossSectionsForSpline(upper.SplineId))
            cs.TargetElevation = 8f;

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var carryCrossing = plan.Crossings.Single(c =>
            c.Crossing.UpperSplineId == upper.SplineId && c.Crossing.LowerSplineId == lower.SplineId);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, carryCrossing.Action);
        Assert.Equal(0f, carryCrossing.DipDepthMeters, Tol);
        Assert.Equal(10f, carryCrossing.DeckTargetZ, Tol); // pinned deck 5 + C 5

        Assert.Equal(10f, plan.Spans.Single(s => s.OwnerSplineId == upper.SplineId).RequiredDeckZ, Tol);
    }
}
