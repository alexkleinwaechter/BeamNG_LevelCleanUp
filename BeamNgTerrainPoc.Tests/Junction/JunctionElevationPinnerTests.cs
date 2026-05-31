using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

public class JunctionElevationPinnerTests
{
    private static float[,] FlatHeightMap(int size, float elevation)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            hm[y, x] = elevation;
        return hm;
    }

    [Fact]
    public void PinNetwork_FlagOff_LeavesAllHarmonizedElevationsAtNaN()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(100, 100));
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 42.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = false };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        Assert.All(network.Junctions, j => Assert.True(float.IsNaN(j.HarmonizedElevation)));
    }

    [Fact]
    public void PinNetwork_FlagOn_EndpointJunctionPinnedToTerrainSample()
    {
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(100, 100));
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 42.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var endpoints = network.Junctions.Where(j => j.Type == JunctionType.Endpoint).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, j => Assert.Equal(42.0f, j.HarmonizedElevation, 3));
    }

    [Fact]
    public void PinNetwork_FlagOn_EndpointSamplesAtCorrectXY_NotSwapped()
    {
        // X-direction slope: a bug that swaps Position.X/Y when sampling would read
        // from a different elevation. Endpoint at (100, 50) → slope = 100/199 * 100 ≈ 50.25;
        // a swap to (50, 100) would also yield ≈ 25.12 — both detectable.
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 50), new(100, 50));
        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s1);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = RoadNetworkTestHelpers.CreateSlopeHeightmap(200, startElevation: 0f, endElevation: 100f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var atX100 = network.Junctions.First(j => j.Position.X > 50f && j.Type == JunctionType.Endpoint);
        Assert.InRange(atX100.HarmonizedElevation, 49f, 51f);
    }

    [Fact]
    public void PinNetwork_FlagOn_TJunctionPinnedToTerrainSampleAtJunctionXY()
    {
        // Through-road horizontal + perpendicular terminator whose endpoint sits on the
        // through-road's centerline at (100, 100). The detector should label the cluster
        // a TJunction because one contributor (s1's mid-spline CS) is IsContinuous and
        // the other (s2's end) is IsEndpoint.
        var throughRoad = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 100), new(190, 100));
        var terminator = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(100, 10), new(100, 100));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { throughRoad, terminator })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 17.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var tJunction = network.Junctions.FirstOrDefault(j => j.Type == JunctionType.TJunction);
        Assert.NotNull(tJunction);
        Assert.Equal(17.0f, tJunction!.HarmonizedElevation, 3);
    }

    [Fact]
    public void PinNetwork_FlagOn_MidSplineCrossingStaysNaN()
    {
        // Two splines that cross at (100, 100); neither has an endpoint there.
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(10, 100), new(190, 100));
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(2, new(100, 10), new(100, 190));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var detected = detector.DetectJunctions(network);
        network.Junctions.Clear();
        network.Junctions.AddRange(detected);

        var hm = FlatHeightMap(200, 99.0f);
        var parameters = new JunctionHarmonizationParameters { EnablePhase19JunctionPinning = true };

        JunctionElevationPinner.PinNetwork(network, hm, metersPerPixel: 1f, parameters);

        var midSpline = network.Junctions.Where(j => j.Type == JunctionType.MidSplineCrossing).ToList();
        Assert.All(midSpline, j => Assert.True(float.IsNaN(j.HarmonizedElevation),
            $"MidSplineCrossing junction {j.JunctionId} unexpectedly pinned to {j.HarmonizedElevation}"));
    }
}
