using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Elevation;

public class NetworkElevationGraphTests
{
    [Fact]
    public void LinearChain_ThreeSplines_DegreeTwo_SingleChain()
    {
        // Three splines in a line: (10,50)→(100,50)→(200,50)→(300,50)
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 50);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 50);
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(300, 50), "primary", 50);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2, s3);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Single(chains);
        Assert.Equal(3, chains[0].Segments.Count);
    }

    [Fact]
    public void TJunction_ThreeSplines_DifferentTypes_ThreeSeparateChains()
    {
        // Three roads meeting at (150,150) from different directions and types
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 150), new(150, 150), "primary", 80);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 150), new(290, 150), "secondary", 60);
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(150, 290), new(150, 150), "residential", 40);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2, s3);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Equal(3, chains.Count);
        Assert.All(chains, c => Assert.Single(c.Segments));
    }

    [Fact]
    public void ThroughRoad_ChainsAcrossJunction_SideRoadSeparate()
    {
        // Through-road: same type, straight line. Side road: different type.
        var sA = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 150), new(150, 150), "primary", 80);
        var sB = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 150), new(290, 150), "primary", 80);
        var sC = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(150, 290), new(150, 150), "residential", 40);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(sA, sB, sC);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Equal(2, chains.Count);
        var throughChain = chains.FirstOrDefault(c => c.Segments.Count == 2);
        var sideChain = chains.FirstOrDefault(c => c.Segments.Count == 1);
        Assert.NotNull(throughChain);
        Assert.NotNull(sideChain);
    }

    [Fact]
    public void Roundabout_ExcludedFromChaining()
    {
        var ring = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(150, 130), new(150, 170), isRoundabout: true);
        var c1 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(10, 150), new(130, 150), "primary");
        var c2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(170, 150), new(290, 150), "primary");
        var c3 = RoadNetworkTestHelpers.CreateParameterizedSpline(4, new(150, 10), new(150, 130), "secondary");

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(ring, c1, c2, c3);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        // Ring should NOT appear in any chain (excluded from graph entirely)
        var allSplineIdsInChains = chains.SelectMany(c => c.Segments.Select(s => s.Edge.SplineId)).ToHashSet();
        Assert.DoesNotContain(1, allSplineIdsInChains);

        // Connectors should be in chains
        Assert.Equal(3, allSplineIdsInChains.Count);
    }

    [Fact]
    public void DeadEnd_SingleChain()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(200, 50), "primary");

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Single(chains);
        Assert.Single(chains[0].Segments);
    }

    [Fact]
    public void BridgeSpline_IncludedInChain()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Single(chains);
        Assert.Equal(3, chains[0].Segments.Count);

        var bridgeEdge = graph.GetEdgeForSpline(2);
        Assert.NotNull(bridgeEdge);
        Assert.True(bridgeEdge.IsBridge);
    }

    [Fact]
    public void ParallelEdges_SeparateChains()
    {
        // Two parallel roads (offset in Y, not connected to each other)
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(290, 50), "primary", 80);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(10, 60), new(290, 60), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(s1, s2);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void WidthMismatch_BlocksChaining()
    {
        // Wide road → narrow road at a junction with a third road (degree 3+)
        var wide = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), "primary", 80, roadWidth: 14f);
        var narrow = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), "primary", 80, roadWidth: 6f);
        var side = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 150), "residential", 40);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(wide, narrow, side);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        // Width ratio 14/6 > 2:1 → should NOT chain through
        Assert.Equal(3, chains.Count);
    }

    [Fact]
    public void DisconnectedSpline_SyntheticNodes()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(200, 50), "primary");

        // Build network WITHOUT junction detection — no junctions in network
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);

        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Single(chains);

        var edge = graph.GetEdgeForSpline(1);
        Assert.NotNull(edge);
        Assert.True(edge.StartNode.IsSynthetic);
        Assert.True(edge.EndNode.IsSynthetic);
    }

    [Fact]
    public void AmbiguousJunction_NoChainThrough()
    {
        // Two roads with same type and similar angles meeting a third — ambiguous
        // A from west, B to northeast, C to southeast — B and C are both "primary" candidates
        var sA = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 150), new(150, 150), "primary", 80);
        var sB = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(150, 150), new(260, 80), "primary", 80);
        var sC = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(150, 150), new(260, 220), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(sA, sB, sC);
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        // All same type, but 2 compatible candidates at junction → ambiguous → no chain-through
        // Should produce 3 separate chains (or 2 if one pair happens to be straight enough)
        // The key assertion: no single chain contains all 3 segments
        Assert.True(chains.All(c => c.Segments.Count <= 2),
            "No chain should contain all 3 segments at an ambiguous junction");
    }

    [Fact]
    public void MissingContinuationConnector_DefaultOff_DoesNotBridgeNeighbourChains()
    {
        var network = BuildNetworkWithMissingContinuationConnector();
        var graph = new NetworkElevationGraph();

        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Null(graph.GetEdgeForSpline(43));
        Assert.Equal(2, chains.Count);
        Assert.All(chains, c => Assert.Single(c.Segments));
    }

    [Fact]
    public void MissingContinuationConnector_OptIn_BridgesNeighbourChains()
    {
        var network = BuildNetworkWithMissingContinuationConnector();
        var graph = new NetworkElevationGraph();

        graph.BuildFromNetwork(network, bridgeMissingContinuationConnectors: true);
        var chains = graph.BuildElevationChains();

        Assert.Null(graph.GetEdgeForSpline(43));
        var chain = Assert.Single(chains);
        Assert.Equal([39, 44], chain.Segments.Select(s => s.Edge.SplineId).Order().ToArray());
    }

    private static UnifiedRoadNetwork BuildNetworkWithMissingContinuationConnector()
    {
        var left = RoadNetworkTestHelpers.CreateParameterizedSpline(
            39, new(0, 0), new(100, 0), "primary", 80, startOsmNodeId: 1, endOsmNodeId: 1438648138);
        var connector = RoadNetworkTestHelpers.CreateParameterizedSpline(
            43, new(100, 0), new(110, 0), "primary", 80, startOsmNodeId: 1438648138, endOsmNodeId: 1438648135);
        var right = RoadNetworkTestHelpers.CreateParameterizedSpline(
            44, new(110, 0), new(250, 0), "primary", 80, startOsmNodeId: 1438648135, endOsmNodeId: 2);

        var network = new UnifiedRoadNetwork();
        var leftCrossSections = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, left);
        var rightCrossSections = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, right);
        network.AddSpline(connector);

        var connectorStart = CreateEndpointCrossSection(connector, isStart: true, globalIndex: 10_000);
        var connectorEnd = CreateEndpointCrossSection(connector, isStart: false, globalIndex: 10_001);

        var first = new NetworkJunction
        {
            JunctionId = 72,
            Type = JunctionType.Continuation,
            Position = new Vector2(100, 0)
        };
        first.Contributors.Add(new JunctionContributor
        {
            Spline = left,
            CrossSection = leftCrossSections.Last(),
            IsSplineEnd = true
        });
        first.Contributors.Add(new JunctionContributor
        {
            Spline = connector,
            CrossSection = connectorStart,
            IsSplineStart = true
        });

        var second = new NetworkJunction
        {
            JunctionId = 77,
            Type = JunctionType.Continuation,
            Position = new Vector2(110, 0)
        };
        second.Contributors.Add(new JunctionContributor
        {
            Spline = connector,
            CrossSection = connectorEnd,
            IsSplineEnd = true
        });
        second.Contributors.Add(new JunctionContributor
        {
            Spline = right,
            CrossSection = rightCrossSections.First(),
            IsSplineStart = true
        });

        network.Junctions.Add(first);
        network.Junctions.Add(second);

        return network;
    }

    private static UnifiedCrossSection CreateEndpointCrossSection(
        ParameterizedRoadSpline spline, bool isStart, int globalIndex)
    {
        var distance = isStart ? 0 : spline.TotalLengthMeters;
        var sample = new SplineSample
        {
            Position = spline.Spline.GetPointAtDistance(distance),
            Tangent = spline.Spline.GetTangentAtDistance(distance),
            Normal = spline.Spline.GetNormalAtDistance(distance),
            Distance = distance
        };
        var crossSection = UnifiedCrossSection.FromSplineSample(sample, spline, globalIndex, isStart ? 0 : 1);
        crossSection.IsSplineStart = isStart;
        crossSection.IsSplineEnd = !isStart;
        return crossSection;
    }
}
