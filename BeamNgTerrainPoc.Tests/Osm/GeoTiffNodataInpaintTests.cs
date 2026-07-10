using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     DEM void inpainting (Park Row mesa root cause, 2026-07-07). A LiDAR bare-earth DTM has no ground
///     return under solid structures (Police Plaza deck over the tunneled Park Row) — those cells are
///     NODATA. The importer used to turn them into pits at the map's global minimum (directly, or via
///     normalization clamping to pixel 0 because the nodata value was never passed through), and the
///     terrain passes amplified the manufactured 25–30m cliffs into needle-spike fields.
///     <see cref="GeoTiffReader.FillNodataVoids"/> instead inpaints voids from their valid neighbours
///     (BFS dilation fill), so covered roads / structure shadows get smooth plausible ground.
/// </summary>
public class GeoTiffNodataInpaintTests
{
    private const double Nodata = -9999.0;

    private static double[] Grid(int width, int height, Func<int, int, double> value)
    {
        var data = new double[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            data[y * width + x] = value(x, y);
        return data;
    }

    [Fact]
    public void SingleVoidCell_FilledWithNeighborAverage()
    {
        // Flat 10m field, one nodata cell in the middle.
        var data = Grid(5, 5, (x, y) => x == 2 && y == 2 ? Nodata : 10.0);

        var filled = GeoTiffReader.FillNodataVoids(data, 5, 5, Nodata);

        Assert.Equal(1, filled);
        Assert.Equal(10.0, data[2 * 5 + 2], 3);
    }

    [Fact]
    public void VoidRegion_FilledFromBoundary_NoPit()
    {
        // 20x20 sloped field (west 10m → east 29m), 6x6 nodata hole in the middle.
        var data = Grid(20, 20, (x, y) =>
            x is >= 7 and <= 12 && y is >= 7 and <= 12 ? Nodata : 10.0 + x);

        var filled = GeoTiffReader.FillNodataVoids(data, 20, 20, Nodata);

        Assert.Equal(36, filled);
        for (var y = 7; y <= 12; y++)
        for (var x = 7; x <= 12; x++)
        {
            var v = data[y * 20 + x];
            // Filled values continue the surrounding terrain — never a pit below the
            // local neighbourhood (west rim 16m) nor a spike above the east rim (23m).
            Assert.InRange(v, 15.0, 24.0);
        }
    }

    [Fact]
    public void UndeclaredExtremeValues_AreTreatedAsVoids()
    {
        // Undeclared nodata sentinels: float-max fills, NaN, and the classic -9999/-32767 (the tag is
        // easily lost by cropping/reprojection). No real terrain sits below -430m (Dead Sea), so
        // anything under -1000m is a void, not ground.
        var data = Grid(5, 5, (_, _) => 10.0);
        data[6] = 3.4e38;
        data[12] = double.NaN;
        data[18] = -9999.0;

        var filled = GeoTiffReader.FillNodataVoids(data, 5, 5, nodataValue: null);

        Assert.Equal(3, filled);
        Assert.Equal(10.0, data[6], 3);
        Assert.Equal(10.0, data[12], 3);
        Assert.Equal(10.0, data[18], 3);
    }

    [Fact]
    public void NoVoids_ReturnsZero_AndLeavesDataUntouched()
    {
        var data = Grid(4, 4, (x, y) => 5.0 + x + y);
        var copy = (double[])data.Clone();

        Assert.Equal(0, GeoTiffReader.FillNodataVoids(data, 4, 4, Nodata));
        Assert.Equal(copy, data);
    }

    [Fact]
    public void AllVoid_ReturnsZero_CallerFallbackApplies()
    {
        // Nothing valid to fill from — the caller's min-elevation fallback stays responsible.
        var data = Grid(4, 4, (_, _) => Nodata);

        Assert.Equal(0, GeoTiffReader.FillNodataVoids(data, 4, 4, Nodata));
        Assert.All(data, v => Assert.Equal(Nodata, v, 3));
    }

    [Fact]
    public void HugeVoidComponent_IsLeftAlone_SmallOneStillFilled()
    {
        // Water bodies are sometimes NODATA on coastal maps: inpainting a whole river would smear
        // shoreline heights across it (terrain poking through the water plane). Components bigger
        // than the cap keep the legacy behaviour; small structure-shadow voids are still filled.
        var data = Grid(12, 12, (x, y) =>
            x >= 6 ? Nodata               // 6x12 = 72-cell "river" (over the cap below)
            : x == 2 && y == 2 ? Nodata   // 1-cell structure shadow
            : 10.0);

        var filled = GeoTiffReader.FillNodataVoids(data, 12, 12, Nodata, maxFillComponentCells: 50);

        Assert.Equal(1, filled);
        Assert.Equal(10.0, data[2 * 12 + 2], 3);
        for (var y = 0; y < 12; y++)
        for (var x = 6; x < 12; x++)
            Assert.Equal(Nodata, data[y * 12 + x], 3); // river untouched
    }
}
