using BeamNgTerrainPoc.Terrain.Biome;

namespace BeamNgTerrainPoc.Tests.Biome;

public class BiomeDensityModelTests
{
    [Fact]
    public void EstimateCount_ZeroForEmptyZoneOrZeroDensity()
    {
        Assert.Equal(0, BiomeDensityModel.EstimateCount(0, 1f, 50, 0.5, 1, 1));
        Assert.Equal(0, BiomeDensityModel.EstimateCount(1000, 1f, 0, 0.5, 1, 1));
    }

    [Fact]
    public void EstimateCount_MonotonicInDensityPercent()
    {
        var prev = 0L;
        for (var pct = 10; pct <= 100; pct += 10)
        {
            var count = BiomeDensityModel.EstimateCount(10_000, 1f, pct, 0.5, 1, 1);
            Assert.True(count >= prev, $"count dropped at {pct}%");
            prev = count;
        }
        Assert.True(prev > 0);
    }

    [Fact]
    public void LargerRadius_MeansFewerItems()
    {
        var small = BiomeDensityModel.EstimateCount(10_000, 1f, 50, 0.5, 1, 1);
        var large = BiomeDensityModel.EstimateCount(10_000, 1f, 50, 2.0, 1, 1);
        Assert.True(large < small);
    }

    [Fact]
    public void AreaScalesWithMetersPerPixel()
    {
        // Same pixel count at mpp=2 covers 4× the area → ~4× the items.
        var mpp1 = BiomeDensityModel.EstimateCount(10_000, 1f, 50, 0.5, 1, 1);
        var mpp2 = BiomeDensityModel.EstimateCount(10_000, 2f, 50, 0.5, 1, 1);
        Assert.InRange(mpp2, mpp1 * 4 - 2, mpp1 * 4 + 2);
    }

    [Fact]
    public void TinyRadius_IsFlooredToKeepDensitySane()
    {
        // Grass-tuft radii (0.05 m) must not anchor 100 % density at 100+ items/m² —
        // that produced multi-GB forest files. The floor caps the anchor at ~5.1/m².
        var perM2 = BiomeDensityModel.MaxDensityPerSquareMeter(0.05, 1.0);
        Assert.True(perM2 <= 5.2, $"density anchor {perM2}/m² exceeds the sane cap");

        // 10 000 m² at 100 % with a tiny radius stays close to the floor-derived count.
        var count = BiomeDensityModel.EstimateCount(10_000, 1f, 100, 0.05, 1, 1);
        Assert.InRange(count, 40_000, 52_000);
    }
}

public class BiomeRandomTests
{
    [Fact]
    public void SameSeed_SameSequence()
    {
        var a = new BiomeRandom(12345);
        var b = new BiomeRandom(12345);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextUInt64(), b.NextUInt64());
        }
    }

    [Fact]
    public void NextDouble_InUnitInterval()
    {
        var rng = new BiomeRandom(7);
        for (var i = 0; i < 10_000; i++)
        {
            var d = rng.NextDouble();
            Assert.InRange(d, 0.0, 0.9999999999999999);
        }
    }

    [Fact]
    public void NextInt_RespectsBound()
    {
        var rng = new BiomeRandom(7);
        for (var i = 0; i < 10_000; i++)
        {
            Assert.InRange(rng.NextInt(13), 0, 12);
        }
    }

    [Fact]
    public void SeedDerivation_IsStableAndDiscriminates()
    {
        var s1 = BiomeSeed.Derive(42, "layer-a", 0);
        var s2 = BiomeSeed.Derive(42, "layer-a", 0);
        Assert.Equal(s1, s2);

        Assert.NotEqual(s1, BiomeSeed.Derive(42, "layer-a", 1));
        Assert.NotEqual(s1, BiomeSeed.Derive(42, "layer-b", 0));
        Assert.NotEqual(s1, BiomeSeed.Derive(43, "layer-a", 0));
    }
}
