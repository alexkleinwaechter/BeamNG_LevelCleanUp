using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Erosion;

public class HydraulicErosionProcessorTests
{
    [Fact]
    public void DisabledSettings_ReturnCopyWithoutChangingValues()
    {
        var heights = CreateSlopedHeightmap(16, 100f);
        var processor = new HydraulicErosionProcessor();
        var settings = new HydraulicErosionSettings { Enabled = false, IterationCount = 500 };

        var result = processor.Apply(heights, 16, 100f, settings);

        Assert.NotSame(heights, result);
        Assert.Equal(heights, result);
    }

    [Fact]
    public void SameSeed_ProducesDeterministicOutput()
    {
        var heights = CreateSlopedHeightmap(32, 120f);
        var settings = CreateEnabledSettings(randomSeed: 42);

        var first = new HydraulicErosionProcessor().Apply(heights, 32, 120f, settings);
        var second = new HydraulicErosionProcessor().Apply(heights, 32, 120f, settings);

        Assert.Equal(first, second);
    }

    [Fact]
    public void EnabledErosion_ModifiesNonFlatTerrain()
    {
        var heights = CreateSlopedHeightmap(32, 120f);
        var processor = new HydraulicErosionProcessor();
        var settings = CreateEnabledSettings(randomSeed: 7);

        var result = processor.Apply(heights, 32, 120f, settings);

        Assert.True(result.Where((value, index) => Math.Abs(value - heights[index]) > 0.0001f).Any());
    }

    [Fact]
    public void EnabledErosion_KeepsValuesFiniteAndClamped()
    {
        var heights = CreateSlopedHeightmap(32, 80f);
        var processor = new HydraulicErosionProcessor();
        var settings = CreateEnabledSettings(randomSeed: 99);

        var result = processor.Apply(heights, 32, 80f, settings);

        Assert.All(result, value =>
        {
            Assert.True(float.IsFinite(value));
            Assert.InRange(value, 0f, 80f);
        });
    }

    private static HydraulicErosionSettings CreateEnabledSettings(int randomSeed) => new()
    {
        Enabled = true,
        IterationCount = 2_000,
        ErosionRadius = 3,
        RandomSeed = randomSeed
    };

    private static float[] CreateSlopedHeightmap(int size, float maxHeight)
    {
        var heights = new float[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var slope = (x + y) / (float)(size * 2 - 2);
            var ridge = MathF.Sin(x * 0.6f) * MathF.Cos(y * 0.4f) * 0.08f;
            heights[y * size + x] = Math.Clamp((slope + ridge) * maxHeight, 0f, maxHeight);
        }

        return heights;
    }
}