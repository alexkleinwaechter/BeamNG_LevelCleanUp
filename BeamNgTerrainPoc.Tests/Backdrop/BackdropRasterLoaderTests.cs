using BeamNgTerrainPoc.Terrain.Backdrop;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropRasterLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backdrop_loader_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Creates a 100x80 float GeoTIFF, value = x + 100*y, nodata (−9999) in a 10x10 block at (20,20).</summary>
    private string CreateTestTiff()
    {
        BeamNgTerrainPoc.Terrain.GeoTiff.GeoTiffReader.InitializeGdal();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "test.tif");
        using var driver = Gdal.GetDriverByName("GTiff");
        using var ds = driver.Create(path, 100, 80, 1, DataType.GDT_Float32, null);
        ds.SetGeoTransform([500000.0, 2.0, 0.0, 5400000.0, 0.0, -2.0]);
        var band = ds.GetRasterBand(1);
        band.SetNoDataValue(-9999.0);
        var data = new float[100 * 80];
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 100; x++)
            data[y * 100 + x] = x >= 20 && x < 30 && y >= 20 && y < 30 ? -9999f : x + 100f * y;
        band.WriteRaster(0, 0, 100, 80, data, 100, 80, 0, 0);
        ds.FlushCache();
        return path;
    }

    [Fact]
    public void LoadWindow_NativeResolution_ReadsValues()
    {
        var path = CreateTestTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(50, 40, 20, 10), null, out var nodata);
        Assert.Equal(0.0, nodata, 3);
        Assert.Equal(50 + 100 * 40, raster.SampleBilinearAtSource(50.5, 40.5), 3);
    }

    [Fact]
    public void LoadWindow_Downsampled_CapsLargerSide()
    {
        var path = CreateTestTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(0, 0, 100, 80), 50, out _);
        Assert.Equal(50, raster.Width);
        Assert.Equal(40, raster.Height);
        Assert.Equal(new PixelRect(0, 0, 100, 80), raster.SourceWindow);
    }

    [Fact]
    public void LoadWindow_FillsNodata_AndReportsPercentage()
    {
        var path = CreateTestTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(15, 15, 20, 20), null, out var nodata);
        Assert.Equal(100.0 * 100 / 400, nodata, 1);                 // 10x10 of 20x20
        var filled = raster.SampleBilinearAtSource(25.5, 25.5);     // inside the hole → edge-extended
        Assert.True(filled >= 0, "nodata not filled");
        Assert.NotEqual(-9999.0, filled, 1);
    }

    /// <summary>Creates a 20x20 float GeoTIFF with NO nodata tag declared, but a 6x6 block filled with an
    /// undeclared sentinel (-9999) — mirrors real-world mosaics where gap-fill values have no tag at all.</summary>
    private string CreateUntaggedSentinelTiff()
    {
        BeamNgTerrainPoc.Terrain.GeoTiff.GeoTiffReader.InitializeGdal();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "untagged.tif");
        using var driver = Gdal.GetDriverByName("GTiff");
        using var ds = driver.Create(path, 20, 20, 1, DataType.GDT_Float32, null);
        ds.SetGeoTransform([500000.0, 2.0, 0.0, 5400000.0, 0.0, -2.0]);
        var data = new float[20 * 20];
        for (var y = 0; y < 20; y++)
        for (var x = 0; x < 20; x++)
            data[y * 20 + x] = x >= 7 && x < 13 && y >= 7 && y < 13 ? -9999f : 50f + x + y;
        // Deliberately no SetNoDataValue() call — the sentinel is completely undeclared.
        ds.GetRasterBand(1).WriteRaster(0, 0, 20, 20, data, 20, 20, 0, 0);
        ds.FlushCache();
        return path;
    }

    [Fact]
    public void LoadWindow_DetectsUndeclaredSentinelValues_WithoutNodataTag()
    {
        // FINDING 2 regression pin: a mosaic gap filled with an out-of-range sentinel (-9999) but with
        // NO nodata tag on the file must still be detected and edge-extended, not read as a valid
        // elevation (which would otherwise become e.g. a -9999 m "plateau" after the datum formula).
        var path = CreateUntaggedSentinelTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(0, 0, 20, 20), null, out var nodata);
        Assert.Equal(100.0 * 36 / 400, nodata, 1);                  // 6x6 of 20x20
        var filled = raster.SampleBilinearAtSource(9.5, 9.5);       // inside the sentinel block
        Assert.NotEqual(-9999.0, filled, 1);
        Assert.True(filled > 0, "undeclared sentinel not filled");
    }

    [Fact]
    public void LoadWindow_DetectsNaNCells_EvenWhenDeclaredNodataTagDiffers()
    {
        // FINDING 2 regression pin: comparing raw values against a declared nodata tag via
        // `Math.Abs(v - nodataValue) < tolerance` can never match actual NaN cells (NaN arithmetic is
        // never < anything) — NaN must be caught unconditionally, independent of whatever tag is declared.
        BeamNgTerrainPoc.Terrain.GeoTiff.GeoTiffReader.InitializeGdal();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "nan_cells.tif");
        using (var driver = Gdal.GetDriverByName("GTiff"))
        using (var ds = driver.Create(path, 10, 10, 1, DataType.GDT_Float32, null))
        {
            ds.SetGeoTransform([500000.0, 2.0, 0.0, 5400000.0, 0.0, -2.0]);
            var band = ds.GetRasterBand(1);
            band.SetNoDataValue(-9999.0);   // declared tag does NOT match the actual void value below
            var data = new float[10 * 10];
            for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                data[y * 10 + x] = x >= 3 && x < 7 && y >= 3 && y < 7 ? float.NaN : 10f + x + y;
            band.WriteRaster(0, 0, 10, 10, data, 10, 10, 0, 0);
            ds.FlushCache();
        }

        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(0, 0, 10, 10), null, out var nodata);
        Assert.Equal(100.0 * 16 / 100, nodata, 1);                  // 4x4 NaN block of 10x10
        var filled = raster.SampleBilinearAtSource(4.5, 4.5);
        Assert.False(double.IsNaN(filled), "NaN cell not filled");
    }
}
