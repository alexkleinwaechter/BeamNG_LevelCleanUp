using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropRasterTests
{
    [Fact]
    public void SampleBilinear_ReproducesGridValuesAtPixelCenters()
    {
        // 3x2 window at mosaic (10, 20); value = 100 + x + 10*y (local indices)
        var data = new float[] { 100, 101, 102, 110, 111, 112 };
        var raster = new BackdropRaster(data, 3, 2, new PixelRect(10, 20, 3, 2));
        // Mosaic pixel-center (10.5, 20.5) = local pixel (0,0) center
        Assert.Equal(100.0, raster.SampleBilinearAtSource(10.5, 20.5), 6);
        Assert.Equal(112.0, raster.SampleBilinearAtSource(12.5, 21.5), 6);
    }

    [Fact]
    public void SampleBilinear_InterpolatesBetweenCenters()
    {
        var data = new float[] { 0, 10, 0, 10 };
        var raster = new BackdropRaster(data, 2, 2, new PixelRect(0, 0, 2, 2));
        Assert.Equal(5.0, raster.SampleBilinearAtSource(1.0, 0.5), 6);  // halfway between (0,0) and (1,0) centers
    }

    [Fact]
    public void SampleBilinear_ClampsAtWindowBorder()
    {
        var data = new float[] { 1, 2, 3, 4 };
        var raster = new BackdropRaster(data, 2, 2, new PixelRect(5, 5, 2, 2));
        Assert.Equal(1.0, raster.SampleBilinearAtSource(4.0, 4.0), 6);   // outside NW → clamped to first pixel
        Assert.Equal(4.0, raster.SampleBilinearAtSource(99.0, 99.0), 6); // outside SE → clamped to last pixel
    }

    [Fact]
    public void DownsampledWindow_SamplesInMosaicCoordinates()
    {
        // 2x1 raster covering a 4x2 mosaic window: each raster pixel spans 2x2 mosaic pixels.
        var data = new float[] { 10, 20 };
        var raster = new BackdropRaster(data, 2, 1, new PixelRect(0, 0, 4, 2));
        Assert.Equal(10.0, raster.SampleBilinearAtSource(1.0, 1.0), 6);  // center of first coarse pixel
        Assert.Equal(20.0, raster.SampleBilinearAtSource(3.0, 1.0), 6);
        Assert.Equal(15.0, raster.SampleBilinearAtSource(2.0, 1.0), 6);  // midpoint
    }

    [Fact]
    public void FillNodata_UsesNearestValidSample()
    {
        // 4x1: [5, X, X, 9] → nearest fill: [5, 5, 9, 9]
        var data = new float[] { 5, 0, 0, 9 };
        var nodata = new[] { false, true, true, false };
        var filled = BackdropRaster.FillNodataByEdgeExtension(data, nodata, 4, 1);
        Assert.Equal(2, filled);
        Assert.Equal(new float[] { 5, 5, 9, 9 }, data);
    }

    [Fact]
    public void FillNodata_AllNodata_FillsZeroAndReportsAll()
    {
        var data = new float[] { 0, 0 };
        var nodata = new[] { true, true };
        var filled = BackdropRaster.FillNodataByEdgeExtension(data, nodata, 2, 1);
        Assert.Equal(2, filled); // nothing to extend from → values stay 0, all counted
    }
}
