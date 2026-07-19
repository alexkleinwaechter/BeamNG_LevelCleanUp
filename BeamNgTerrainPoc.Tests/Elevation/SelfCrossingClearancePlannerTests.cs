using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// <c>EnableHiddenCrossingDetection</c>, self-crossing half: a corridor whose ground leg passes
/// under its OWN bridge span (a switchback/hairpin). The junction detector excludes same-spline pairs
/// structurally, so the planner detects the leg directly from the span footprint and emits a synthetic
/// Road obstacle (LowerSplineId −1, station-disambiguated via SelfLowerStationMeters). The crossing is
/// raise-only (nothing to dip — the "lower road" is the deck's own approach chain), and the post-solve
/// passes resolve the leg's final Z by STATION because an XY lookup on the shared spline finds the deck.
/// </summary>
public class SelfCrossingClearancePlannerTests
{
    private const float Tol = 0.05f;

    // Hairpin corridor: east along y=150 (bridge span [100,200] → x∈[150,250]), then down, west and back
    // north crossing under its own deck at (200,150) — leg station ≈ 710, deck station ≈ 150 (t=0.5).
    //   (50,150) → (350,150) → (350,20) → (200,20) → (200,300)
    private static ParameterizedRoadSpline BuildHairpinCorridor(
        bool selfCrossing, bool priorityDistribution = false)
    {
        var points = new List<Vector2>
        {
            new(50, 150), new(350, 150), new(350, 20), new(200, 20), new(200, 300),
        };
        var span = new StructureSegment
        {
            StartDistance = 100,
            EndDistance = 200,
            Type = StructureType.Bridge,
            Layer = 1,
            OsmWayIds = { 88001L },
        };
        var rules = new BridgeRuleSystemOptions().WithTestClearance();
        rules.EnableHiddenCrossingDetection = selfCrossing;
        rules.EnablePriorityDistribution = priorityDistribution;

        return new ParameterizedRoadSpline
        {
            Spline = new RoadSpline(points, SplineInterpolationType.LinearControlPoints),
            Parameters = new RoadSmoothingParameters
            {
                RoadWidthMeters = 8f,
                TerrainAffectedRangeMeters = 6f,
                CrossSectionIntervalMeters = 0.5f,
                ExcludeBridgesFromTerrain = true,
                ExcludeTunnelsFromTerrain = true,
                MergeStructuresIntoCorridor = true,
                BridgeRules = rules,
            },
            MaterialName = "asphalt",
            SplineId = 1,
            OsmRoadType = "tertiary",
            Priority = 6001,
            StructureSegments = [span],
        };
    }

    // Deck + approaches at 10; the return leg (station ≥ 400 — past the first corner) at legZ.
    private static void SetHairpinElevation(UnifiedRoadNetwork network, float legZ)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(1))
            cs.TargetElevation = cs.DistanceAlongSpline >= 400f ? legZ : 10f;
    }

    [Fact]
    public void SelfCrossing_EmitsRaiseOnlyVeto_WithStationDisambiguatedLeg()
    {
        var corridor = BuildHairpinCorridor(selfCrossing: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        SetHairpinElevation(network, legZ: 8f); // deck 10, leg 8 ⇒ deficit = 8 + 5 − 10 = 3

        var plan = BridgeElevationPlanner.Plan(network);

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(13f, crossing.DeckTargetZ, Tol); // leg 8 + road clearance 5
        Assert.Equal(5f, crossing.RequiredSeparationMeters, Tol);
        Assert.Equal(BridgeObstacleKind.Road, crossing.Crossing.LowerKind);
        Assert.False(crossing.Crossing.HasLowerSpline); // never dippable — it is the deck's own chain
        Assert.True(crossing.Crossing.HasSelfLowerStation);
        Assert.InRange(crossing.Crossing.SelfLowerStationMeters, 700f, 720f); // the leg, not the deck
        Assert.InRange(crossing.Crossing.CrossingXY.X, 195f, 205f);
        Assert.InRange(crossing.Crossing.CrossingXY.Y, 145f, 155f);

        var span = Assert.Single(plan.Spans);
        Assert.True(span.IsRaised);
        Assert.Equal(13f, span.RequiredDeckZ, Tol);
    }

    [Fact]
    public void SelfCrossing_FlagOff_PlansNothing()
    {
        var corridor = BuildHairpinCorridor(selfCrossing: false);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        SetHairpinElevation(network, legZ: 8f);

        var plan = BridgeElevationPlanner.Plan(network);

        Assert.Empty(plan.Crossings); // legacy baseline: the leg is invisible, deck stays put
        var span = Assert.Single(plan.Spans);
        Assert.False(span.IsRaised);
    }

    [Fact]
    public void SelfCrossing_PriorityDistribution_NeverSplitsToAPhantomDip()
    {
        // Δclass = 0 would nominally split 50/50 — but there is no lower spline to execute the dip half,
        // so the synthetic crossing must veto-raise the full deficit instead of silently under-clearing.
        var corridor = BuildHairpinCorridor(selfCrossing: true, priorityDistribution: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        SetHairpinElevation(network, legZ: 8f);

        var plan = BridgeElevationPlanner.Plan(network);

        var crossing = Assert.Single(plan.Crossings);
        Assert.Equal(BridgeElevationAction.RaiseBridgeVeto, crossing.Action);
        Assert.Equal(13f, crossing.DeckTargetZ, Tol);
        Assert.Equal(0f, crossing.DipDepthMeters, Tol);
    }

    [Fact]
    public void StraightCorridor_FlagOn_DetectsNoSelfCrossing()
    {
        // The span's own approaches touch the footprint at the abutments; the 30 m station gap must keep
        // them from ever reporting as an obstacle under their own deck.
        var span = new StructureSegment
        {
            StartDistance = 100,
            EndDistance = 200,
            Type = StructureType.Bridge,
            Layer = 1,
            OsmWayIds = { 88002L },
        };
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(450, 150), priority: 6001,
            mergeStructuresIntoCorridor: true, structureSegments: [span]);
        var rules = new BridgeRuleSystemOptions().WithTestClearance();
        rules.EnableHiddenCrossingDetection = true;
        corridor.Parameters.BridgeRules = rules;

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        foreach (var cs in network.GetCrossSectionsForSpline(1))
            cs.TargetElevation = 10f;

        var plan = BridgeElevationPlanner.Plan(network);

        Assert.Empty(plan.Crossings);
    }

    [Fact]
    public void PlanFloorConstraints_ResolvesLegZ_ByStation_NotByXY()
    {
        var corridor = BuildHairpinCorridor(selfCrossing: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(corridor);
        SetHairpinElevation(network, legZ: 8f);

        network.BridgeElevationPlan = BridgeElevationPlanner.Plan(network);

        // The leg keeps moving after planning (smoother/dams) — the floor must read the FINAL leg Z by
        // station. An XY lookup on the shared spline would find the DECK section (z 10) instead and
        // produce a floor of 15.
        foreach (var cs in network.GetCrossSectionsForSpline(1))
            if (cs.DistanceAlongSpline >= 400f)
                cs.TargetElevation = 7.5f;

        var floors = GradeSeparationResolver.PlanFloorConstraints(network, log: false);

        var floor = Assert.Single(floors);
        Assert.Equal(1, floor.BridgeSplineId);
        Assert.InRange(floor.DistanceAlongSpline, 140f, 160f); // anchored on the DECK station (t≈0.5)
        Assert.Equal(12.5f, floor.MinZ, Tol); // final leg 7.5 + clearance 5
    }
}
