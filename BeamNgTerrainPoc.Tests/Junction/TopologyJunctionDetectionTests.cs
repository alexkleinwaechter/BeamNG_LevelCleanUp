using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

public class TopologyJunctionDetectionTests
{
    [Fact]
    public void SharedNodePreUnion_ThreeSplinesShareNode_SingleJunction()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 100);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: 100);
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 150), endOsmNodeId: 100);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2, s3 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        // Tiny radius — too small for spatial clustering but topology should work
        var junctions = detector.DetectJunctions(network, detectionRadiusOverride: 0.1f);

        var junctionsWithThreeContributors = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 3)
            .ToList();

        Assert.Single(junctionsWithThreeContributors);
    }

    [Fact]
    public void MixedTopologyAndSpatial_OsmAndPngSplines_BothClustered()
    {
        var osmS1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 200);
        var osmS2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: 200);
        var pngS3 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 152));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { osmS1, osmS2, pngS3 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        var junctionsWithThreeEndpoints = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 3)
            .ToList();

        Assert.Single(junctionsWithThreeEndpoints);
    }

    [Fact]
    public void PngPipelineNoNodeIds_PureSpatialClustering_BehaviorUnchanged()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150));
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150));
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 150));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2, s3 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        var junctionsWithThreeEndpoints = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 3)
            .ToList();

        Assert.Single(junctionsWithThreeEndpoints);
    }

    [Fact]
    public void BridgeRoadSharedNode_DifferentLayers_SameJunction()
    {
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 300);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(250, 150), isBridge: true, startOsmNodeId: 300);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { road, bridge })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network, detectionRadiusOverride: 0.1f);

        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }

    [Fact]
    public void SingleEndpointNode_NoPreUnion_HandledNormally()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 400);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(500, 500), new(600, 500), startOsmNodeId: 500);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        Assert.True(junctions.Count >= 2);
        Assert.All(junctions, j => Assert.True(j.Contributors.Count <= 2));
    }

    [Fact]
    public void CroppedBoundaryFallback_NullNodeIdNearEndpoint_SpatialClusters()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 600);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: null);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }

    [Fact]
    public void MergedPathNodeIds_OuterNodesFormJunctions()
    {
        var merged = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(200, 150), startOsmNodeId: 800, endOsmNodeId: 802);
        var next = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 150), new(350, 150), startOsmNodeId: 802);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { merged, next })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network, detectionRadiusOverride: 0.1f);

        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }

    [Fact]
    public void DifferentNodeIdsSameLocation_SpatialFallbackMerges()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 700);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: 701);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }
}
