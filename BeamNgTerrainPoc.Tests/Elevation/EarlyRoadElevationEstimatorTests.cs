using BeamNgTerrainPoc.Terrain.Algorithms;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
/// V2 plan A0 (review P0-2): the early road-elevation estimate the bridge planner reads pre-smoothing —
/// centerline DEM low-passed along the spline, so a single-cell DEM spike or an embankment bank does not
/// masquerade as the road's elevation.
/// </summary>
public class EarlyRoadElevationEstimatorTests
{
    private static float[,] FlatHeightMap(int size, float z)
    {
        var hm = new float[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                hm[y, x] = z;
        return hm;
    }

    [Fact]
    public void FlatTerrain_EstimateEqualsTerrain()
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(50, 150), new(450, 150));
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(spline);
        var hm = FlatHeightMap(512, 10f);

        var estimate = EarlyRoadElevationEstimator.Build(network, hm, metersPerPixel: 1f);

        Assert.NotEmpty(estimate);
        Assert.All(estimate.Values, z => Assert.Equal(10f, z, 0.01f));
    }

    [Fact]
    public void SingleCellSpike_IsSmoothedAway()
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(50, 150), new(450, 150));
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(spline);
        var hm = FlatHeightMap(512, 10f);
        hm[150, 250] = 40f; // one 30 m spike directly under the centerline

        var estimate = EarlyRoadElevationEstimator.Build(network, hm, metersPerPixel: 1f, windowMeters: 30f);

        // The raw sample at x=250 would be 40; the 30 m window mean is ≈ 10 + 30/30 ≈ 11.
        var atSpike = network.GetCrossSectionsForSpline(1)
            .First(c => Math.Abs(c.CenterPoint.X - 250f) < 0.6f);
        Assert.True(estimate.TryGetValue(atSpike.Index, out var z));
        Assert.InRange(z, 10f, 12.5f);
    }

    [Fact]
    public void GradualGrade_IsPreserved()
    {
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(1, new(50, 150), new(450, 150));
        var network = RoadNetworkTestHelpers.BuildNetworkWithJunctions(spline);

        // Terrain climbs 1 m per 20 m of X — a genuine grade the estimate must NOT flatten.
        var hm = new float[512, 512];
        for (var y = 0; y < 512; y++)
            for (var x = 0; x < 512; x++)
                hm[y, x] = x / 20f;

        var estimate = EarlyRoadElevationEstimator.Build(network, hm, metersPerPixel: 1f);

        var at100 = network.GetCrossSectionsForSpline(1).First(c => Math.Abs(c.CenterPoint.X - 100f) < 0.6f);
        var at400 = network.GetCrossSectionsForSpline(1).First(c => Math.Abs(c.CenterPoint.X - 400f) < 0.6f);
        Assert.Equal(5f, estimate[at100.Index], 0.5f);
        Assert.Equal(20f, estimate[at400.Index], 0.5f);
    }
}
