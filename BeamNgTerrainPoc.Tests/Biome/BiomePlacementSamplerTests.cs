using BeamNgTerrainPoc.Terrain.Biome;

namespace BeamNgTerrainPoc.Tests.Biome;

/// <summary>
/// The sampler must be deterministic (same seed → identical forest), stay inside its zone,
/// honor the spacing rule and the slope/elevation filters, and saturate gracefully when a
/// zone can't hold the target count.
/// </summary>
public class BiomePlacementSamplerTests
{
    private const int Size = 64;

    private static BiomeTerrainContext FlatTerrain(float baseHeight = 0f)
    {
        return new BiomeTerrainContext
        {
            Size = Size,
            MetersPerPixel = 1f,
            HeightData = new ushort[Size * Size],
            MaxHeight = 100f,
            TerrainBaseHeight = baseHeight,
        };
    }

    private static int[] FullZone()
    {
        var pixels = new int[Size * Size];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = i;
        }
        return pixels;
    }

    private static BiomeItemSpec Tree(int density = 20) => new()
    {
        TypeName = "test_tree",
        DensityPercent = density,
        RadiusMeters = 0.5,
        ScaleMin = 0.8,
        ScaleMax = 1.2,
        SinkMin = 0,
        SinkMax = 0.1,
    };

    [Fact]
    public void SameSeed_ProducesIdenticalPlacements()
    {
        var terrain = FlatTerrain();
        var zone = FullZone();
        var items = new[] { Tree() };

        var a = BiomePlacementSampler.SampleZone(terrain, zone, items, seed: 42);
        var b = BiomePlacementSampler.SampleZone(terrain, zone, items, seed: 42);

        Assert.NotEmpty(a);
        Assert.Equal(a, b); // record value equality, exact floats — full determinism
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentPlacements()
    {
        var terrain = FlatTerrain();
        var zone = FullZone();
        var items = new[] { Tree() };

        var a = BiomePlacementSampler.SampleZone(terrain, zone, items, seed: 1);
        var b = BiomePlacementSampler.SampleZone(terrain, zone, items, seed: 2);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AllPlacements_StayInsideZonePixels()
    {
        var terrain = FlatTerrain();
        // Zone = left quarter of the map only.
        var pixels = new List<int>();
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size / 4; x++)
            {
                pixels.Add(y * Size + x);
            }
        }
        var zoneSet = new HashSet<int>(pixels);

        var placements = BiomePlacementSampler.SampleZone(terrain, pixels.ToArray(), new[] { Tree(50) }, seed: 7);

        Assert.NotEmpty(placements);
        foreach (var p in placements)
        {
            var px = (int)(p.TerrainX / terrain.MetersPerPixel);
            var py = (int)(p.TerrainY / terrain.MetersPerPixel);
            Assert.Contains(py * Size + px, zoneSet);
        }
    }

    [Fact]
    public void SpacingRule_NoPairCloserThanFootprints()
    {
        var terrain = FlatTerrain();
        var items = new[] { Tree(100) };
        const double spacingFactor = 1.0;

        var placements = BiomePlacementSampler.SampleZone(
            terrain, FullZone(), items, seed: 42,
            new BiomeSamplerOptions { SpacingFactor = spacingFactor });

        Assert.NotEmpty(placements);
        for (var i = 0; i < placements.Count; i++)
        {
            for (var j = i + 1; j < placements.Count; j++)
            {
                var a = placements[i];
                var b = placements[j];
                var dx = a.TerrainX - b.TerrainX;
                var dy = a.TerrainY - b.TerrainY;
                var minDist = spacingFactor * (0.5 * a.Scale + 0.5 * b.Scale);
                Assert.True(
                    Math.Sqrt(dx * dx + dy * dy) >= minDist - 1e-4,
                    $"pair {i}/{j} violates spacing");
            }
        }
    }

    [Fact]
    public void SlopeFilter_RejectsSteepTerrain()
    {
        // Left half flat, right half a steep ramp (dz/dx = 10 → ~84°).
        var heights = new ushort[Size * Size];
        const float maxHeight = 1000f;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var h = x >= 32 ? (x - 32) * 10f : 0f;
                heights[y * Size + x] = (ushort)(h / maxHeight * 65535f);
            }
        }
        var terrain = new BiomeTerrainContext
        {
            Size = Size,
            MetersPerPixel = 1f,
            HeightData = heights,
            MaxHeight = maxHeight,
            TerrainBaseHeight = 0f,
        };

        var item = new BiomeItemSpec
        {
            TypeName = "t",
            DensityPercent = 60,
            SlopeMaxDeg = 45,
        };

        var placements = BiomePlacementSampler.SampleZone(terrain, FullZone(), new[] { item }, seed: 3);

        Assert.NotEmpty(placements);
        foreach (var p in placements)
        {
            // x=32 is the first pixel with a steep central-difference gradient.
            Assert.True((int)p.TerrainX <= 31, $"placement on steep slope at x={p.TerrainX}");
        }
    }

    [Fact]
    public void ElevationFilter_UsesAbsoluteWorldZ()
    {
        // Flat terrain at world Z=100 (base height). ElevationMin=150 excludes everything;
        // ElevationMax=150 allows everything.
        var terrain = FlatTerrain(baseHeight: 100f);

        var tooLow = new BiomeItemSpec { TypeName = "t", DensityPercent = 30, ElevationMin = 150 };
        var ok = new BiomeItemSpec { TypeName = "t", DensityPercent = 30, ElevationMax = 150 };

        Assert.Empty(BiomePlacementSampler.SampleZone(terrain, FullZone(), new[] { tooLow }, seed: 5));
        Assert.NotEmpty(BiomePlacementSampler.SampleZone(terrain, FullZone(), new[] { ok }, seed: 5));
    }

    [Fact]
    public void WorldZ_IsGroundPlusBaseMinusSink()
    {
        var terrain = FlatTerrain(baseHeight: 50f);
        var item = new BiomeItemSpec
        {
            TypeName = "t",
            DensityPercent = 10,
            SinkMin = 0.2,
            SinkMax = 0.3,
        };

        var placements = BiomePlacementSampler.SampleZone(terrain, FullZone(), new[] { item }, seed: 9);

        Assert.NotEmpty(placements);
        foreach (var p in placements)
        {
            Assert.InRange(p.WorldZ, 50f - 0.3f - 1e-3f, 50f - 0.2f + 1e-3f);
        }
    }

    [Fact]
    public void CrowdedZone_SaturatesBelowTargetWithoutFailing()
    {
        var terrain = FlatTerrain();
        // Tiny zone (8x8 px = 64 m²) with a huge item at full density.
        var pixels = new List<int>();
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                pixels.Add(y * Size + x);
            }
        }
        var item = new BiomeItemSpec
        {
            TypeName = "big",
            DensityPercent = 100,
            RadiusMeters = 3.0,
            ScaleMin = 1.0,
            ScaleMax = 1.0,
        };

        var placements = BiomePlacementSampler.SampleZone(terrain, pixels.ToArray(), new[] { item }, seed: 11);

        // A 64 m² zone fits very few 3m-radius items; must not loop forever or throw.
        Assert.True(placements.Count <= 5);
    }

    [Fact]
    public void ZeroDensityOrEmptyZone_ReturnsEmpty()
    {
        var terrain = FlatTerrain();
        Assert.Empty(BiomePlacementSampler.SampleZone(terrain, FullZone(), new[] { Tree(0) }, seed: 1));
        Assert.Empty(BiomePlacementSampler.SampleZone(terrain, Array.Empty<int>(), new[] { Tree(50) }, seed: 1));
    }

    [Fact]
    public void StreamingSink_MatchesListVersion()
    {
        var terrain = FlatTerrain();
        var zone = FullZone();
        var items = new[] { Tree(30) };

        var listVersion = BiomePlacementSampler.SampleZone(terrain, zone, items, seed: 42);

        var streamed = new List<BiomePlacement>();
        var count = BiomePlacementSampler.SampleZoneStreaming(terrain, zone, items, seed: 42, streamed.Add);

        Assert.Equal(listVersion.Count, count);
        Assert.Equal(listVersion, streamed);
    }

    [Fact]
    public void MultipleItemTypes_AllGetPlaced()
    {
        var terrain = FlatTerrain();
        var items = new[]
        {
            new BiomeItemSpec { TypeName = "a", DensityPercent = 20 },
            new BiomeItemSpec { TypeName = "b", DensityPercent = 20 },
        };

        var placements = BiomePlacementSampler.SampleZone(terrain, FullZone(), items, seed: 13);

        Assert.Contains(placements, p => p.TypeName == "a");
        Assert.Contains(placements, p => p.TypeName == "b");
    }
}
