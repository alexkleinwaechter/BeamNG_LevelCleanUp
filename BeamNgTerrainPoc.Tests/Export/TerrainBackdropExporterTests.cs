using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Export;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Tests.Export;

public class TerrainBackdropExporterTests
{
    [Fact]
    public void Export_WritesSeparateNonCollidingSceneAndIsIdempotent()
    {
        var levelPath = CreateLevelDirectory();
        try
        {
            var heightMap = new float[8, 8];
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                heightMap[y, x] = x + y * 2;

            var first = TerrainBackdropExporter.Export(
                heightMap, levelPath, "backdrop_test", 8, 1f, 100f, 1000f);
            var second = TerrainBackdropExporter.Export(
                heightMap, levelPath, "backdrop_test", 8, 1f, 100f, 2000f);

            Assert.True(File.Exists(first.DaePath));
            Assert.True(File.Exists(first.MaterialPath));
            Assert.True(File.Exists(first.TexturePath));
            Assert.True(second.VertexCount > first.VertexCount);
            Assert.True(second.TriangleCount > first.TriangleCount);
            Assert.Contains(TerrainBackdropExporter.MaterialName, File.ReadAllText(first.DaePath));

            var parentItemsPath = Path.Combine(levelPath, "main", "MissionGroup", "items.level.json");
            var groupEntries = File.ReadLines(parentItemsPath)
                .Count(line => HasSceneIdentity(line, "SimGroup", TerrainBackdropExporter.GroupName));
            Assert.Equal(1, groupEntries);

            var backdropItemsPath = Path.Combine(
                levelPath, "main", "MissionGroup", TerrainBackdropExporter.GroupName, "items.level.json");
            var sceneLine = Assert.Single(File.ReadAllLines(backdropItemsPath));
            using var scene = JsonDocument.Parse(sceneLine);
            Assert.Equal("TSStatic", scene.RootElement.GetProperty("class").GetString());
            Assert.Equal("None", scene.RootElement.GetProperty("collisionType").GetString());
            Assert.DoesNotContain("TerrainBlock", sceneLine);
        }
        finally
        {
            Directory.Delete(levelPath, true);
        }
    }

    [Fact]
    public void RefreshTexture_DownsamplesFinalBaseColorTo2048Pixels()
    {
        var levelPath = CreateLevelDirectory();
        try
        {
            var heightMap = new float[4, 4];
            TerrainBackdropExporter.Export(heightMap, levelPath, "texture_test", 4, 1f, 0f, 500f);

            var terrainDirectory = Path.Combine(levelPath, "art", "terrains");
            Directory.CreateDirectory(terrainDirectory);
            using (var source = new Image<Rgba32>(4096, 2048, new Rgba32(50, 100, 150, 255)))
                source.SaveAsPng(Path.Combine(terrainDirectory, "MT_basecolor.png"));

            Assert.True(TerrainBackdropExporter.RefreshTexture(levelPath));
            var texturePath = Path.Combine(
                levelPath, "art", "shapes", TerrainBackdropExporter.GroupName,
                TerrainBackdropExporter.BaseColorFileName);
            using var result = Image.Load<Rgba32>(texturePath);
            Assert.Equal(2048, result.Width);
            Assert.Equal(1024, result.Height);
        }
        finally
        {
            Directory.Delete(levelPath, true);
        }
    }

    [Theory]
    [InlineData(0f, 0f, 0.5f, 0.5f)]
    [InlineData(0f, 100f, 0.5f, 0f)]
    [InlineData(0f, 200f, 0.5f, 0f)]
    [InlineData(200f, 0f, 1f, 0.5f)]
    [InlineData(-200f, -200f, 0f, 1f)]
    public void BackdropUv_PreservesTerrainSeamAndClampsOutsideIt(
        float worldX,
        float worldY,
        float expectedU,
        float expectedV)
    {
        var uv = TerrainBackdropExporter.CalculateClampedTextureCoordinate(worldX, worldY, 100f);

        Assert.Equal(expectedU, uv.X, 5);
        Assert.Equal(expectedV, uv.Y, 5);
    }

    private static string CreateLevelDirectory()
    {
        var levelPath = Path.Combine(Path.GetTempPath(), "terrain-backdrop-tests", Guid.NewGuid().ToString("N"));
        var missionGroupDirectory = Path.Combine(levelPath, "main", "MissionGroup");
        Directory.CreateDirectory(missionGroupDirectory);
        File.WriteAllText(
            Path.Combine(missionGroupDirectory, "items.level.json"),
            JsonSerializer.Serialize(new { name = "MissionGroup", @class = "SimGroup" }) + Environment.NewLine);
        return levelPath;
    }

    private static bool HasSceneIdentity(string line, string className, string name)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("class").GetString() == className &&
               document.RootElement.GetProperty("name").GetString() == name;
    }
}
