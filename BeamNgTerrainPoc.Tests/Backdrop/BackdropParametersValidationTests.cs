using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropParametersValidationTests
{
    private static BackdropGenerationParameters Valid() => new()
    {
        TerrainHeightMap = new float[64, 64],
        TerrainSizePixels = 64,
        TerrainMetersPerPixel = 2.0f,
        TerrainBaseHeight = 100f,
        TerrainCropMinElevation = 100.0,
        SourceGeoTiffPath = "unused-in-validation.tif",
        SourceRasterWidth = 400,
        SourceRasterHeight = 300,
        SourceGeoTransform = [500000, 2, 0, 5400000, 0, -2],
        ProjectionWkt = null,
        TerrainRect = new PixelRect(150, 100, 64, 64),
        BackdropRect = new PixelRect(100, 50, 200, 180),
        LevelPath = "unused",
        LevelName = "test_level",
        EdgeBandMeters = 20,
    };

    [Fact]
    public void ValidParameters_Pass()
    {
        var r = Valid().Validate();
        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    // Core stays caller-driven: collision ON is the library default (existing bakes all have
    // collision); the fast-loading default lives in the app layer (BackdropSettings).
    [Fact]
    public void CollisionMesh_DefaultsToTrue()
    {
        Assert.True(Valid().CollisionMesh);
    }

    [Fact]
    public void BackdropMustContainTerrainRect()
    {
        var p = Valid() with { BackdropRect = new PixelRect(160, 50, 200, 180) };
        var r = p.Validate();
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackdropMustLieInsideMosaic()
    {
        var p = Valid() with { BackdropRect = new PixelRect(-5, 50, 200, 180) };
        Assert.False(p.Validate().IsValid);
    }

    [Fact]
    public void AllZeroMargins_IsError()
    {
        var p = Valid() with { BackdropRect = new PixelRect(150, 100, 64, 64) };
        var r = p.Validate();
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("margin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarginSmallerThanBand_ProducesWarningNotError()
    {
        // West margin = 5 px. Meters per source px = 64*2/64 = 2 → 10 m < EdgeBandMeters(20) → warning.
        var p = Valid() with { BackdropRect = new PixelRect(145, 50, 150, 180) };
        var r = p.Validate();
        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("band", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeightmapSizeMismatch_IsError()
    {
        var p = Valid() with { TerrainHeightMap = new float[32, 64] };
        Assert.False(p.Validate().IsValid);
    }

    [Fact]
    public void GeoTransformMustHaveSixElements()
    {
        var p = Valid() with { SourceGeoTransform = [1.0, 2.0] };
        Assert.False(p.Validate().IsValid);
    }
}
