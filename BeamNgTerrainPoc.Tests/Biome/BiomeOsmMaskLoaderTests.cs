using BeamNgTerrainPoc.Terrain.Biome;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Tests.Biome;

/// <summary>
/// OSM mask PNGs live in image space (y-down, row 0 = north) while the .ter arrays are
/// y-up (row 0 = south). These tests pin the Y-flip on an asymmetric fixture, the
/// &gt;127 luminance threshold, the dimension guard, and terrain-hole subtraction.
/// </summary>
public class BiomeOsmMaskLoaderTests : IDisposable
{
    private const int Size = 8;
    private readonly string _tempDir;

    public BiomeOsmMaskLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "biome-osm-mask-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private string WritePng(int size, params (int X, int ImageY, byte Luminance)[] pixels)
    {
        var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".png");
        using var image = new Image<L8>(size, size); // all black by default
        foreach (var (x, imageY, luminance) in pixels)
        {
            image[x, imageY] = new L8(luminance);
        }
        image.SaveAsPng(path);
        return path;
    }

    [Fact]
    public void Load_FlipsImageRowsIntoTerrainSpace()
    {
        // Image top row (imageY=0) is the terrain's NORTH edge = terrain row Size-1.
        var path = WritePng(Size,
            (2, 0, 255),  // north edge → terrain row 7
            (5, 6, 255)); // one above the image bottom → terrain row 1

        var mask = BiomeOsmMaskLoader.Load(path, Size);

        Assert.Equal(2, BiomeOsmMaskLoader.CountInRegion(mask));
        Assert.True(mask[(Size - 1) * Size + 2], "image row 0 must land on terrain row Size-1");
        Assert.True(mask[1 * Size + 5], "image row 6 must land on terrain row 1");
        Assert.False(mask[0 * Size + 2], "the un-flipped position must stay clear");
    }

    [Fact]
    public void Load_ThresholdIsExclusiveAt127()
    {
        // Same rule as MaterialLayerProcessor: > 127 is in-region, 127 itself is not.
        var path = WritePng(Size, (1, 3, 127), (2, 3, 128));

        var mask = BiomeOsmMaskLoader.Load(path, Size);

        var terrainRow = (Size - 1 - 3) * Size;
        Assert.False(mask[terrainRow + 1]);
        Assert.True(mask[terrainRow + 2]);
    }

    [Fact]
    public void Load_DimensionMismatch_Throws()
    {
        var path = WritePng(Size);

        Assert.Throws<InvalidDataException>(() => BiomeOsmMaskLoader.Load(path, Size * 2));
    }

    [Fact]
    public void SubtractHoles_ClearsHolePixelsAndReportsCount()
    {
        var mask = new bool[Size * Size];
        mask[10] = true;
        mask[11] = true;
        mask[12] = true;

        var materialData = new byte[Size * Size];
        materialData[11] = 255; // terrain hole under an in-region pixel
        materialData[20] = 255; // hole outside the region — must not count

        var cleared = BiomeOsmMaskLoader.SubtractHoles(mask, materialData);

        Assert.Equal(1, cleared);
        Assert.True(mask[10]);
        Assert.False(mask[11]);
        Assert.True(mask[12]);
    }

    [Fact]
    public void SubtractHoles_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            BiomeOsmMaskLoader.SubtractHoles(new bool[4], new byte[9]));
    }

    [Fact]
    public void Load_RoundTripsThroughZoneBander()
    {
        // A 4×4 white block drawn in image space must band exactly like the equivalent
        // terrain-space mask — proves the flipped mask plugs into the existing pipeline.
        var pixels = new List<(int, int, byte)>();
        for (var imageY = 1; imageY <= 4; imageY++)
        {
            for (var x = 2; x <= 5; x++)
            {
                pixels.Add((x, imageY, 255));
            }
        }
        var path = WritePng(Size, pixels.ToArray());

        var mask = BiomeOsmMaskLoader.Load(path, Size);
        var bands = new[] { new BiomeZoneBandDefinition(0.0, IsInterior: true) };
        var zones = BiomeZoneBander.ComputeZonePixels(mask, Size, 1f, bands);

        Assert.Equal(16, zones[0].Length);
        foreach (var index in zones[0])
        {
            var x = index % Size;
            var terrainY = index / Size;
            Assert.InRange(x, 2, 5);
            Assert.InRange(terrainY, Size - 1 - 4, Size - 1 - 1); // image rows 1..4 flipped
        }
    }
}
