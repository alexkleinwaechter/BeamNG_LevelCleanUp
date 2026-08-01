using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropCoordinateMapperTests
{
    // Terrain: 64 px @ 2 m/px = 128 m world span, half = 64. Crop rect 100x100 source px at (150, 100).
    private static BackdropCoordinateMapper Mapper() =>
        new(new PixelRect(150, 100, 100, 100), terrainSizePixels: 64, terrainMetersPerPixel: 2.0f);

    [Fact]
    public void TerrainRectCorners_MapToWorldBounds()
    {
        var m = Mapper();
        // NW source corner (150,100) → world (−half, +half); SE source corner (250,200) → (+half, −half).
        var nw = m.SourcePixelToWorld(150, 100);
        var se = m.SourcePixelToWorld(250, 200);
        Assert.Equal(-64.0, nw.WorldX, 10);
        Assert.Equal(64.0, nw.WorldY, 10);
        Assert.Equal(64.0, se.WorldX, 10);
        Assert.Equal(-64.0, se.WorldY, 10);
    }

    [Fact]
    public void TerrainRectCenter_MapsToOrigin()
    {
        var (wx, wy) = Mapper().SourcePixelToWorld(200, 150);
        Assert.Equal(0.0, wx, 10);
        Assert.Equal(0.0, wy, 10);
    }

    [Fact]
    public void RoundTrip_IsExact()
    {
        var m = Mapper();
        var (wx, wy) = m.SourcePixelToWorld(123.25, 77.5);
        var (sx, sy) = m.WorldToSourcePixel(wx, wy);
        Assert.Equal(123.25, sx, 9);
        Assert.Equal(77.5, sy, 9);
    }

    [Fact]
    public void MetersPerSourcePixel_ComesFromTerrainMapping_NotNativeSize()
    {
        // 64 px * 2 m = 128 m spread over 100 source px → 1.28 m per source px.
        var m = Mapper();
        Assert.Equal(1.28, m.MetersPerSourcePixelX, 10);
        Assert.Equal(1.28, m.MetersPerSourcePixelY, 10);
    }

    [Fact]
    public void SourceYIncreasesSouthward_WorldYDecreases()
    {
        var m = Mapper();
        var a = m.SourcePixelToWorld(200, 120);
        var b = m.SourcePixelToWorld(200, 130);
        Assert.True(b.WorldY < a.WorldY);
    }
}
