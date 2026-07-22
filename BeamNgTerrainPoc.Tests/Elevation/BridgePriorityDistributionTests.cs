using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// V2 Phase A3 (plan doc 01, spec §3.5): priority-based raise/dip distribution. The non-ramp deficit D is
/// shared — deck raised by r·D, lower road dipped by (1−r)·D — with r from
/// <see cref="BridgeRuleSystemOptions.RaiseShareFor"/> over Δp = quantized CLASS steps of the OSM highway
/// classes (review P0-4: never the stored composite priorities, whose raw differences are meaningless).
/// `*_link` inherits its parent's step, so a motorway over its own exit ramp splits 50/50 instead of dipping
/// the ramp 80 % at the gore. Gated on <c>EnablePriorityDistribution</c>; flag off keeps the binary
/// dip/veto/split.
/// </summary>
public class BridgePriorityDistributionTests
{
    private const float Tol = 0.05f;

    // Non-ramp scenario: corridor approaches+deck at 10, under-road at 8, base C = 5 → deficit D = 3.
    private static (UnifiedRoadNetwork network, ParameterizedRoadSpline corridor) BuildScenario(
        string upperClass, string lowerClass, int upperPriority, int lowerPriority, bool distribute)
    {
        var span = new StructureSegment
        {
            StartDistance = 100, EndDistance = 200, Type = StructureType.Bridge, Layer = 1,
            OsmWayIds = { 99001L },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), osmRoadType: upperClass, priority: upperPriority,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        corridor.Layer = 0;
        corridor.Parameters.BridgeRules =
            new BridgeRuleSystemOptions { EnablePriorityDistribution = distribute }.WithTestClearance();

        var under = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 100), new(200, 200), osmRoadType: lowerClass, priority: lowerPriority);
        under.Layer = 0;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor, under);
        foreach (var cs in network.GetCrossSectionsForSpline(corridor.SplineId))
            cs.TargetElevation = 10f;
        foreach (var cs in network.GetCrossSectionsForSpline(under.SplineId))
            cs.TargetElevation = 8f;
        return (network, corridor);
    }

    private static BridgeElevationPlannerOptions NoTerrain() =>
        new();

    [Fact]
    public void MotorwayOverResidential_NeverRaises_FullDip()
    {
        // Δp = step(motorway 4) − step(residential 1) = +3 → r = 0 (2026-07-21, bridge 675150484): the
        // deck of the more important road NEVER leaves its approach level — an honest pure dip of the
        // full 3 m deficit (→5), the span is not marked raised.
        var (network, _) = BuildScenario("motorway", "residential", 10000, 5500, distribute: true);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, crossing.Action);
        Assert.Equal(3f, crossing.DipDepthMeters, Tol);
        Assert.Equal(5f, crossing.LowerRoadTargetZ, Tol);
        Assert.False(Assert.Single(plan.Spans).IsRaised);
    }

    [Fact]
    public void ResidentialBridgeOverMotorway_NeverDipsTheMotorway_FullRaise()
    {
        // Δp = step(residential 1) − step(motorway 4) = −3 → r = 1 (2026-07-21 mirror law): the outranking
        // lower road must never dip — a pure raise veto lifts the deck the full deficit (→13 = 8 + C 5).
        var (network, _) = BuildScenario("residential", "motorway", 5500, 10000, distribute: true);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(13f, crossing.DeckTargetZ, Tol);
        Assert.Equal(0f, crossing.DipDepthMeters, Tol);
    }

    [Fact]
    public void MotorwayOverOwnLink_SplitsEvenly_NoGoreDip()
    {
        // Review P0-4 / P1-5: motorway_link inherits the motorway step → Δp = 0 → 50/50 split (1.5/1.5).
        // The stored composite priorities differ (10000 vs 9500) — they must NOT drive the decision, else
        // the exit ramp gets the full 3 m dip at the gore.
        var (network, _) = BuildScenario("motorway", "motorway_link", 10000, 9500, distribute: true);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.Split, crossing.Action);
        Assert.Equal(11.5f, crossing.DeckTargetZ, Tol);
        Assert.Equal(1.5f, crossing.DipDepthMeters, Tol);
    }

    [Fact]
    public void FlagOff_SamePair_KeepsBinaryFullDip()
    {
        // Contrast case: distribution OFF, the composite priorities decide (10000 > 9500) → the link is
        // binary-dipped the FULL 3 m deficit, deck untouched. (This is exactly the gore artefact A3 fixes.)
        var (network, _) = BuildScenario("motorway", "motorway_link", 10000, 9500, distribute: false);

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.DipLowerRoad, crossing.Action);
        Assert.Equal(3f, crossing.DipDepthMeters, Tol);
        Assert.False(Assert.Single(plan.Spans).IsRaised);
    }

    [Fact]
    public void Distribution_AlreadyClearing_Untouched()
    {
        // Road far below (z=2): clearance 8 ≥ C 5 → AlreadyClears regardless of shares.
        var (network, _) = BuildScenario("motorway", "residential", 10000, 5500, distribute: true);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.TargetElevation = 2f;

        var plan = BridgeElevationPlanner.Plan(network, options: NoTerrain());

        Assert.Equal(BridgeElevationAction.AlreadyClears, Assert.Single(plan.Crossings).Action);
    }
}
