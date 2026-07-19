using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
/// E-A stage (c): <see cref="GradeSeparationResolver"/> applies D-3a — the bridge holds its deck Z and the
/// lower road dips to clear it, unless the lower road outranks the bridge (priority veto), in which case the
/// bridge is raised instead via an interior constraint.
/// </summary>
public class GradeSeparationResolverTests
{
    // Horizontal road (the lower member) crossing a vertical bridge (the upper member) at (150,150).
    private static UnifiedRoadNetwork BuildCrossing(int roadPriority = 50, int bridgePriority = 50)
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(50, 150), new(250, 150), priority: roadPriority);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 50), new(150, 250), priority: bridgePriority, isBridge: true);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road, bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300, 100f));
        return network;
    }

    private static void SetSplineElevation(UnifiedRoadNetwork network, int splineId, float z)
    {
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
            cs.TargetElevation = z;
    }

    [Fact]
    public void Setup_Sanity_OneGradeSeparatedCrossing_RoadIsLower()
    {
        var network = BuildCrossing();
        var crossing = Assert.Single(network.GradeSeparatedCrossings);
        Assert.Equal(2, crossing.UpperSplineId); // bridge
        Assert.Equal(1, crossing.LowerSplineId); // road
    }

    [Fact]
    public void ApplyLowerRoadDips_RoadAlreadyClears_LeavesRoadUntouched()
    {
        var network = BuildCrossing();
        SetSplineElevation(network, 2, 110f); // deck high
        SetSplineElevation(network, 1, 100f); // road 10m below → clears the 5m minimum

        var roadBefore = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();
        GradeSeparationResolver.ApplyLowerRoadDips(network, log: false);

        Assert.Equal(GradeSeparationAction.AlreadyClears, network.GradeSeparatedCrossings[0].Action);
        Assert.Equal(roadBefore, network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList());
    }

    [Fact]
    public void ApplyLowerRoadDips_InsufficientClearance_DipsRoadAtCrossing_EasedToZeroAtEdges()
    {
        var network = BuildCrossing();
        SetSplineElevation(network, 2, 110f); // deck
        SetSplineElevation(network, 1, 108f); // road only 2m below → needs a 3m dip for 5m clearance

        GradeSeparationResolver.ApplyLowerRoadDips(network, dipRampLengthMeters: 30f, log: false);

        var crossing = network.GradeSeparatedCrossings[0];
        Assert.Equal(GradeSeparationAction.DippedLowerRoad, crossing.Action);
        Assert.Equal(3f, crossing.AppliedDipMeters, precision: 2);

        // The well's deepest point reaches deck − 5m clearance (108 − 3 = 105).
        var deepest = network.GetCrossSectionsForSpline(1).MinBy(c => c.TargetElevation)!;
        Assert.Equal(105f, deepest.TargetElevation, precision: 1);

        // A road section well outside the ramp window is untouched (eased to zero).
        var farSection = network.GetCrossSectionsForSpline(1)
            .OrderByDescending(cs => Vector2.DistanceSquared(cs.CenterPoint, new Vector2(150, 150)))
            .First();
        Assert.Equal(108f, farSection.TargetElevation, precision: 2);

        // Banked edges track the dipped centerline (flat bank here → edges equal centre).
        Assert.Equal(deepest.TargetElevation, deepest.LeftEdgeElevation, precision: 2);
        Assert.Equal(deepest.TargetElevation, deepest.RightEdgeElevation, precision: 2);
    }

    /// <summary>Per-bridge 3D deck thickness from its span — the offset added to the clearance (soffit, not top).</summary>
    private static float BridgeDeckThickness(UnifiedRoadNetwork network, int bridgeId, BridgeDeckProfile profile)
    {
        var s = network.GetCrossSectionsForSpline(bridgeId).ToList();
        var span = s.Max(c => c.DistanceAlongSpline) - s.Min(c => c.DistanceAlongSpline);
        return BridgeDeckProfile.ComputeDeckThicknessMeters(span, profile);
    }

    [Fact]
    public void ApplyLowerRoadDips_WithDeckProfile_MeasuresClearanceToSoffit_NotDeckTop()
    {
        // The 3D deck box hangs `thickness` below the solved deck-top Z. Passing a deckProfile must add that
        // thickness to the clearance so the SOFFIT (not the top) clears the road by the configured minimum.
        var network = BuildCrossing();
        SetSplineElevation(network, 2, 110f); // deck top
        SetSplineElevation(network, 1, 108f); // road 2m below the deck top

        var profile = new BridgeDeckProfile();
        var thickness = BridgeDeckThickness(network, 2, profile);
        Assert.True(thickness > 0.5f, "test bridge should generate a deck with real thickness");

        GradeSeparationResolver.ApplyLowerRoadDips(
            network, minClearanceMeters: 5f, deckProfile: profile, dipRampLengthMeters: 30f, log: false);

        var crossing = network.GradeSeparatedCrossings[0];
        Assert.Equal(GradeSeparationAction.DippedLowerRoad, crossing.Action);

        // Effective clearance = 5 + thickness; raw clearance = 2 → dip = 5 + thickness − 2.
        Assert.Equal(5f + thickness - 2f, crossing.AppliedDipMeters, precision: 2);

        // The soffit (deck top − thickness) clears the dipped road by EXACTLY the 5 m minimum.
        var deepest = network.GetCrossSectionsForSpline(1).MinBy(c => c.TargetElevation)!;
        var soffitZ = 110f - thickness;
        Assert.Equal(5f, soffitZ - deepest.TargetElevation, precision: 1);

        // And it dips deeper than the no-profile (deck-top) result (105) would — by the thickness offset.
        Assert.True(deepest.TargetElevation < 105f - 0.1f,
            $"with the soffit offset the road must dip below the deck-top-based 105m, got {deepest.TargetElevation}");
    }

    [Fact]
    public void PlanConstraints_WithDeckProfile_RaisesBridgeSoSoffitClears()
    {
        // Priority veto (motorway under a minor bridge): the deck is raised. The raise target must lift the
        // SOFFIT to roadZ + clearance, i.e. the deck-top constraint = roadZ + clearance + thickness.
        var network = BuildCrossing(roadPriority: 100, bridgePriority: 50);
        SetSplineElevation(network, 2, 101f);
        SetSplineElevation(network, 1, 100f);

        var profile = new BridgeDeckProfile();
        var thickness = BridgeDeckThickness(network, 2, profile);

        var constraints = GradeSeparationResolver.PlanConstraints(
            network, minClearanceMeters: 5f, deckProfile: profile, log: false);

        var constraint = Assert.Single(constraints);
        Assert.Equal(2, constraint.BridgeSplineId);
        // deck-top min-Z = roadZ(100) + clearance(5) + thickness, so the soffit lands at road + 5.
        Assert.Equal(100f + 5f + thickness, constraint.MinZ, precision: 2);
    }

    [Fact]
    public void ApplyLowerRoadDips_CarvesDipIntoHeightmap_NotJustCrossSections()
    {
        // Regression for the "clearance has no effect in-game" bug: the road is stamped into the heightmap
        // during Phase-4 blending BEFORE this runs, so the dip must be carved into the heightmap too — moving
        // only cross-section Z leaves the driven terrain surface unchanged.
        var network = BuildCrossing();
        SetSplineElevation(network, 2, 110f); // deck
        SetSplineElevation(network, 1, 108f); // road 2m below → needs a 3m dip for 5m clearance

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(300, 108f); // terrain at the stamped road level

        GradeSeparationResolver.ApplyLowerRoadDips(network, hm, metersPerPixel: 1f,
            minClearanceMeters: 5f, dipRampLengthMeters: 30f, log: false);

        // The deepest carved cell drops ~3m toward deck − 5m clearance (108 − 3 = 105).
        var minHeight = float.MaxValue;
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
            minHeight = MathF.Min(minHeight, hm[y, x]);
        Assert.True(minHeight is <= 105.3f and >= 104.7f, $"deepest carved cell {minHeight:F2} not ~105");

        // A cell far from any crossing is untouched (lower-only, localized carve).
        Assert.Equal(108f, hm[10, 10], precision: 2);
    }

    [Fact]
    public void ApplyLowerRoadDips_SkipsCellsOwnedByAnotherSpline_StillCarvesOwnRoad()
    {
        // The dip carves the lower road's footprint — but it must not gouge a NEIGHBOURING road's protected
        // surface caught in the same footprint. Cells owned by a different spline stay at terrain level; the
        // lower road's own cells (and bare terrain) still carve down toward deck − clearance.
        var network = BuildCrossing();
        SetSplineElevation(network, 2, 110f); // deck
        SetSplineElevation(network, 1, 108f); // road 2m below → needs a 3m dip

        var hm = RoadNetworkTestHelpers.CreateFlatHeightmap(300, 108f);

        var owner = new int[300, 300];
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
            owner[y, x] = RoadSurfaceOwnerRaster.NoOwner;
        owner[150, 150] = 99; // foreign road surface at the crossing centre (the deepest carve point)

        GradeSeparationResolver.ApplyLowerRoadDips(network, hm, metersPerPixel: 1f,
            minClearanceMeters: 5f, dipRampLengthMeters: 30f, log: false, roadSurfaceOwner: owner);

        Assert.Equal(108f, hm[150, 150], precision: 2); // foreign cell preserved, not carved

        // The lower road is still carved elsewhere — the deepest carved cell reaches ~105 off the foreign cell.
        var minHeight = float.MaxValue;
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
            minHeight = MathF.Min(minHeight, hm[y, x]);
        Assert.True(minHeight is <= 105.3f and >= 104.7f, $"own road still carved, deepest {minHeight:F2}");
    }

    [Fact]
    public void PlanConstraints_PriorityVeto_RaisesBridge_LeavesHighClassRoadUntouched()
    {
        // Lower road is a motorway (priority 100) under a minor bridge (priority 50): never dip the motorway.
        var network = BuildCrossing(roadPriority: 100, bridgePriority: 50);
        SetSplineElevation(network, 2, 101f); // deck barely above
        SetSplineElevation(network, 1, 100f); // road 1m below → would need a dip, but it's vetoed

        var roadBefore = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();

        var constraints = GradeSeparationResolver.PlanConstraints(network, minClearanceMeters: 5f, log: false);

        var crossing = network.GradeSeparatedCrossings[0];
        Assert.Equal(GradeSeparationAction.RaisedBridge, crossing.Action);

        var constraint = Assert.Single(constraints);
        Assert.Equal(2, constraint.BridgeSplineId);
        Assert.Equal(105f, constraint.MinZ, precision: 2); // roadZ(100) + clearance(5)

        // The motorway must not move during the dip phase either.
        GradeSeparationResolver.ApplyLowerRoadDips(network, minClearanceMeters: 5f, log: false);
        Assert.Equal(roadBefore, network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList());
    }

    [Fact]
    public void ApplyLowerRoadDips_ClampsDipShortOfJunctions_PreservesEndpointElevation()
    {
        // Short lower road whose endpoints (Endpoint junctions) sit ~20m from the crossing — closer than the
        // 60m ramp. The dip MUST clamp so the harmonized junction endpoints keep their elevation (no-go to
        // disturb harmonization), while the centre still dips.
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(130, 150), new(170, 150), priority: 50);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 50), new(150, 250), priority: 50, isBridge: true);
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road, bridge);
        foreach (var cs in network.GetCrossSectionsForSpline(2))
            cs.IsExcluded = true;
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300, 100f));

        Assert.Single(network.GradeSeparatedCrossings);
        SetSplineElevation(network, 2, 110f); // deck
        SetSplineElevation(network, 1, 108f); // road needs a dip

        GradeSeparationResolver.ApplyLowerRoadDips(network, minClearanceMeters: 5f, log: false);

        var sections = network.GetCrossSectionsForSpline(1).ToList();

        // Endpoints (junctions) untouched — the well eased to zero before reaching them.
        Assert.Equal(108f, sections.First().TargetElevation, precision: 2);
        Assert.Equal(108f, sections.Last().TargetElevation, precision: 2);

        // The centre (deepest section = well centre) still dipped.
        var centre = sections.MinBy(c => c.TargetElevation)!;
        var centreDist = centre.DistanceAlongSpline;
        Assert.True(centre.TargetElevation < 107.5f, $"centre not dipped: {centre.TargetElevation:F2}");

        // The dip is SYMMETRIC about its centre: sections equidistant either side dip by the same amount
        // (the fix for the lopsided long-gentle / short-steep result).
        for (var off = 3f; off <= 9f; off += 3f)
        {
            var left = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - (centreDist - off))).First();
            var right = sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - (centreDist + off))).First();
            Assert.Equal(left.TargetElevation, right.TargetElevation, precision: 1);
        }
    }

    [Fact]
    public void PlanConstraints_NoVeto_ReturnsNoConstraints()
    {
        var network = BuildCrossing(roadPriority: 40, bridgePriority: 80); // road lower-class → dip, no veto
        var constraints = GradeSeparationResolver.PlanConstraints(network, log: false);
        Assert.Empty(constraints);
        Assert.Equal(GradeSeparationAction.Pending, network.GradeSeparatedCrossings[0].Action);
    }

    [Fact]
    public void Resolver_NoCrossings_IsNoop()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50));
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road);
        RoadNetworkTestHelpers.RunChainSmoothing(network, RoadNetworkTestHelpers.CreateFlatHeightmap(300));

        Assert.Empty(GradeSeparationResolver.PlanConstraints(network, log: false));
        var before = network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList();
        GradeSeparationResolver.ApplyLowerRoadDips(network, log: false);
        Assert.Equal(before, network.GetCrossSectionsForSpline(1).Select(c => c.TargetElevation).ToList());
    }
}
