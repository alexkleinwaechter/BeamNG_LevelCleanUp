using System.Numerics;
using System.Reflection;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Tests for bridge/tunnel spline elevation chaining.
///     Verifies that bridge splines participate in elevation chains and produce
///     smooth elevation profiles across bridge boundaries.
/// </summary>
public class BridgeElevationChainingTests
{
    [Fact]
    public void HarmonizerLookup_IncludesExcludedBridgeWithConnectedEndpoint()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary");

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);
        MarkBridgeCrossSectionsExcluded(network);

        var lookup = BuildHarmonizerLookup(network);

        Assert.True(lookup.ContainsKey(2), "connected excluded bridge spline should be visible to junction harmonization");
        Assert.True(lookup.ContainsKey(1), "regular approach road should remain visible to junction harmonization");
        Assert.True(lookup.ContainsKey(3), "regular approach road should remain visible to junction harmonization");
    }

    [Fact]
    public void HarmonizerLookup_ExcludesIsolatedGeneratedBridgeEndpoint()
    {
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(bridge);
        MarkBridgeCrossSectionsExcluded(network);

        var lookup = BuildHarmonizerLookup(network);

        Assert.False(lookup.ContainsKey(2), "isolated excluded bridge endpoint must not be treated like a terrain-tapered road end");
    }

    [Fact]
    public void BridgeEndpointHarmonization_DoesNotPullDeckEndsTowardTerrain()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        var hm = new float[300, 300];
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
        {
            if (x < 80 || x > 220)
                hm[y, x] = 100f;
            else if (x >= 120 && x <= 180)
                hm[y, x] = 60f;
            else if (x < 120)
                hm[y, x] = 100f - (x - 80f) / 40f * 40f;
            else
                hm[y, x] = 60f + (x - 180f) / 40f * 40f;
        }

        MarkBridgeCrossSectionsExcluded(network);
        var (_, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);
        var bridgeCs = csBySpline[2].OrderBy(c => c.LocalIndex).ToList();
        var chainStart = bridgeCs.First().TargetElevation;
        var chainEnd = bridgeCs.Last().TargetElevation;

        var harmonizer = new NetworkJunctionHarmonizer();
        harmonizer.HarmonizeNetwork(network, hm, 1f, skipDetection: true);

        Assert.True(Math.Abs(bridgeCs.First().TargetElevation - chainStart) < 1.0f,
            $"bridge start changed from chain value by {Math.Abs(bridgeCs.First().TargetElevation - chainStart):F3}m");
        Assert.True(Math.Abs(bridgeCs.Last().TargetElevation - chainEnd) < 1.0f,
            $"bridge end changed from chain value by {Math.Abs(bridgeCs.Last().TargetElevation - chainEnd):F3}m");
        Assert.True(bridgeCs[bridgeCs.Count / 2].TargetElevation > hm[50, 150] + 5f,
            "bridge deck should remain above valley terrain after junction harmonization");
    }

    /// <summary>
    ///     When co-located splines have no junction detection, they should still
    ///     be connected if the elevation graph can cluster co-located synthetic endpoints.
    ///     Currently, without junctions, each spline becomes its own chain — this test
    ///     verifies the fix that makes co-located endpoints share nodes.
    /// </summary>
    [Fact]
    public void CoLocatedSplines_WithoutJunctions_FormSingleChain()
    {
        // Arrange: 3 splines sharing endpoints but NO junction detection run
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary");
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(300, 50), "primary");

        // Build network WITHOUT junction detection (simulates shouldHarmonize=false)
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s2);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s3);
        // Note: network.Junctions is empty — no DetectJunctions() called

        // Act
        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        // Assert: All 3 splines should form a single chain even without junction detection
        Assert.Single(chains);
        Assert.Equal(3, chains[0].Segments.Count);
    }

    /// <summary>
    ///     Bridge spline between two road splines should form a single chain
    ///     even without junction detection (the fix must cluster co-located endpoints).
    /// </summary>
    [Fact]
    public void BridgeSpline_WithoutJunctions_ChainsWithAdjacentRoads()
    {
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary",
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road1);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridge);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, road2);

        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Single(chains);
        Assert.Equal(3, chains[0].Segments.Count);

        // Verify bridge edge preserves IsBridge flag
        var bridgeEdge = graph.GetEdgeForSpline(2);
        Assert.NotNull(bridgeEdge);
        Assert.True(bridgeEdge.IsBridge);
    }

    /// <summary>
    ///     When a bridge spans a valley (low terrain), the chain-smoothed elevation profile
    ///     should be continuous (no sharp steps) at bridge boundaries.
    /// </summary>
    [Fact]
    public void BridgeOverValley_ChainSmoothing_ProducesSmoothProfile()
    {
        // Arrange: road → bridge → road over a valley heightmap
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        // Create valley heightmap: high at edges, low in the middle (bridge area at x=100-200)
        var hm = new float[300, 300];
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
        {
            if (x < 80 || x > 220)
                hm[y, x] = 100f; // High plateau
            else if (x >= 120 && x <= 180)
                hm[y, x] = 60f; // Deep valley under bridge
            else if (x < 120)
                hm[y, x] = 100f - (x - 80f) / 40f * 40f; // Slope down
            else
                hm[y, x] = 60f + (x - 180f) / 40f * 40f; // Slope up
        }

        // Act: Run chain-based smoothing
        var (chains, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);

        // Assert: Single chain
        Assert.Single(chains);
        Assert.Equal(3, chains[0].Segments.Count);

        // Check elevation continuity at bridge boundaries
        var cs1 = csBySpline[1].OrderBy(c => c.LocalIndex).ToList();
        var csBridge = csBySpline[2].OrderBy(c => c.LocalIndex).ToList();
        var cs3 = csBySpline[3].OrderBy(c => c.LocalIndex).ToList();

        // Elevation at road1 end vs bridge start should be nearly identical
        var road1End = cs1.Last().TargetElevation;
        var bridgeStart = csBridge.First().TargetElevation;
        var elevDiff1 = Math.Abs(road1End - bridgeStart);
        Assert.True(elevDiff1 < 1.0f,
            $"Elevation gap at road1→bridge: {elevDiff1:F3}m (road1 end={road1End:F1}, bridge start={bridgeStart:F1})");

        // Elevation at bridge end vs road2 start should be nearly identical
        var bridgeEnd = csBridge.Last().TargetElevation;
        var road2Start = cs3.First().TargetElevation;
        var elevDiff2 = Math.Abs(bridgeEnd - road2Start);
        Assert.True(elevDiff2 < 1.0f,
            $"Elevation gap at bridge→road2: {elevDiff2:F3}m (bridge end={bridgeEnd:F1}, road2 start={road2Start:F1})");
    }

    /// <summary>
    ///     A very short bridge (only 2 OSM nodes) should still chain with adjacent roads
    ///     and produce smooth elevation. This tests the edge case from OSM way 65166514
    ///     which has only nodes 35301234 and 659892002.
    /// </summary>
    [Fact]
    public void VeryShortBridge_TwoNodes_ChainsAndSmoothsCorrectly()
    {
        // Simulate: road1(10→100), tiny bridge(100→110), road2(110→290)
        // Bridge is only ~10m — much shorter than smoothing window
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(110, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(110, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        // Valley under the short bridge
        var hm = new float[300, 300];
        for (var y = 0; y < 300; y++)
        for (var x = 0; x < 300; x++)
        {
            if (x >= 95 && x <= 115)
                hm[y, x] = 80f; // Valley under bridge
            else
                hm[y, x] = 100f; // Plateau
        }

        var (chains, csBySpline) = RoadNetworkTestHelpers.RunChainSmoothing(network, hm);

        // Should be a single chain with 3 segments
        Assert.Single(chains);
        Assert.Equal(3, chains[0].Segments.Count);

        // Bridge CS should exist and have valid elevations
        var bridgeCS = csBySpline[2];
        Assert.True(bridgeCS.Count >= 2, $"Short bridge should have at least 2 CS (got {bridgeCS.Count})");
        Assert.All(bridgeCS, cs => Assert.False(float.IsNaN(cs.TargetElevation)));

        // Elevation at boundaries should be continuous
        var road1End = csBySpline[1].Last().TargetElevation;
        var bridgeStart = bridgeCS.First().TargetElevation;
        var gap1 = Math.Abs(road1End - bridgeStart);
        Assert.True(gap1 < 1.0f, $"Gap at road1→bridge: {gap1:F3}m");

        var bridgeEnd = bridgeCS.Last().TargetElevation;
        var road2Start = csBySpline[3].First().TargetElevation;
        var gap2 = Math.Abs(bridgeEnd - road2Start);
        Assert.True(gap2 < 1.0f, $"Gap at bridge→road2: {gap2:F3}m");
    }

    /// <summary>
    ///     Non-co-located splines should NOT be clustered together — they must remain
    ///     in separate chains. Only endpoints within a small tolerance should merge.
    /// </summary>
    [Fact]
    public void NonCoLocatedSplines_WithoutJunctions_RemainSeparateChains()
    {
        // Two parallel roads 20m apart — should NOT cluster endpoints
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(200, 50), "primary");
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(10, 70), new(200, 70), "primary");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s2);

        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Equal(2, chains.Count);
    }

    /// <summary>
    ///     When the seed edge is in the middle, backward-extended segments must have
    ///     the correct TraverseReversed flag so CS end at the connection point.
    ///     Verifies the backward extension fix produces spatially coherent chains.
    /// </summary>
    [Fact]
    public void BackwardExtension_CorrectTraverseReversed_SpatiallyCoherent()
    {
        // 3 splines: road1(10→100), bridge(100→200), road2(200→290)
        // bridge is longest (100m) and will be the seed.
        // road1 connects at bridge.StartNode(100) via backward extension.
        var road1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary", 80);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 50), new(200, 50), "primary", 80,
            isBridge: true);
        var road2 = RoadNetworkTestHelpers.CreateParameterizedSpline(3, new(200, 50), new(290, 50), "primary", 80);

        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(road1, bridge, road2);

        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        Assert.Single(chains);
        var chain = chains[0];
        Assert.Equal(3, chain.Segments.Count);

        // Concatenate and verify spatial monotonicity (X should increase throughout)
        var csBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        var concatenated = OptimizedElevationSmoother.ConcatenateChainCrossSections(
            chain, csBySpline, 0.5f);

        // Verify no spatial jumps larger than 2m between consecutive CS
        for (var i = 1; i < concatenated.Count; i++)
        {
            var dist = Vector2.Distance(concatenated[i].CenterPoint, concatenated[i - 1].CenterPoint);
            Assert.True(dist < 2.0f,
                $"Spatial jump at index {i}: {dist:F1}m between ({concatenated[i - 1].CenterPoint.X:F0},{concatenated[i - 1].CenterPoint.Y:F0}) " +
                $"and ({concatenated[i].CenterPoint.X:F0},{concatenated[i].CenterPoint.Y:F0})");
        }
    }

    /// <summary>
    ///     Endpoint clustering should use a tight tolerance (e.g., 2m) to avoid
    ///     falsely merging nearby but distinct road endpoints.
    /// </summary>
    [Fact]
    public void EndpointClustering_TightTolerance_DoesNotMergeNearbyEndpoints()
    {
        // Two splines ending 3m apart — should NOT cluster
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50), "primary");
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 53), new(200, 50), "primary");

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s2);

        var graph = new NetworkElevationGraph();
        graph.BuildFromNetwork(network);
        var chains = graph.BuildElevationChains();

        // 3m apart > clustering tolerance → should be separate
        Assert.Equal(2, chains.Count);
    }

    private static void MarkBridgeCrossSectionsExcluded(UnifiedRoadNetwork network)
    {
        var bridgeSplineIds = network.Splines
            .Where(s => s.IsBridge && s.Parameters.ExcludeBridgesFromTerrain)
            .Select(s => s.SplineId)
            .ToHashSet();

        foreach (var cs in network.CrossSections.Where(cs => bridgeSplineIds.Contains(cs.OwnerSplineId)))
            cs.IsExcluded = true;
    }

    private static Dictionary<int, List<UnifiedCrossSection>> BuildHarmonizerLookup(UnifiedRoadNetwork network)
    {
        var method = typeof(NetworkJunctionHarmonizer).GetMethod(
            "BuildCrossSectionLookupForHarmonization",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, [network, network.Junctions]);
        return Assert.IsType<Dictionary<int, List<UnifiedCrossSection>>>(result);
    }
}
