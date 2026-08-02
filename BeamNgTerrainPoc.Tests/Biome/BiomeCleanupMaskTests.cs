using System.Globalization;
using BeamNgTerrainPoc.Terrain.Biome;

namespace BeamNgTerrainPoc.Tests.Biome;

/// <summary>
/// Negative-list cleanup mask logic: OR-combining material regions and OSM masks,
/// EDT buffer expansion in meters, the world-position hit test (the exact inverse of
/// the sampler's pixel+jitter placement), and the predicate line filter that powers
/// the foreign-item cleanup.
/// </summary>
public class BiomeCleanupMaskTests
{
    private const int Size = 32;

    [Fact]
    public void OrMaterial_And_OrMask_Combine()
    {
        var mask = new bool[Size * Size];

        var materialData = new byte[Size * Size];
        materialData[5] = 3;
        materialData[6] = 3;
        materialData[7] = 2; // different material — must not enter

        BiomeCleanupMask.OrMaterial(mask, materialData, materialIndex: 3);

        var other = new bool[Size * Size];
        other[6] = true; // overlap
        other[9] = true;
        BiomeCleanupMask.OrMask(mask, other);

        Assert.Equal(3, BiomeCleanupMask.CountSet(mask));
        Assert.True(mask[5]);
        Assert.True(mask[6]);
        Assert.False(mask[7]);
        Assert.True(mask[9]);
    }

    [Fact]
    public void ExpandByMeters_GrowsByEuclideanDistance()
    {
        var mask = new bool[Size * Size];
        mask[10 * Size + 10] = true;

        var expanded = BiomeCleanupMask.ExpandByMeters(mask, Size, metersPerPixel: 1f, bufferMeters: 2.0);

        Assert.True(expanded[10 * Size + 12], "distance 2.0 must be inside the buffer");
        Assert.True(expanded[11 * Size + 11], "distance √2 must be inside the buffer");
        Assert.False(expanded[11 * Size + 12], "distance √5 ≈ 2.24 must be outside the buffer");
        Assert.False(expanded[10 * Size + 13], "distance 3.0 must be outside the buffer");
    }

    [Fact]
    public void ExpandByMeters_HonorsMetersPerPixel()
    {
        var mask = new bool[Size * Size];
        mask[10 * Size + 10] = true;

        // 4 m buffer at 2 m/px = 2 px radius.
        var expanded = BiomeCleanupMask.ExpandByMeters(mask, Size, metersPerPixel: 2f, bufferMeters: 4.0);

        Assert.True(expanded[10 * Size + 12]);
        Assert.False(expanded[10 * Size + 13]);
    }

    [Fact]
    public void ExpandByMeters_ZeroBufferOrEmptyMask_ReturnsInputUnchanged()
    {
        var mask = new bool[Size * Size];
        mask[3] = true;
        Assert.Same(mask, BiomeCleanupMask.ExpandByMeters(mask, Size, 1f, 0.0));

        var empty = new bool[Size * Size];
        Assert.Same(empty, BiomeCleanupMask.ExpandByMeters(empty, Size, 1f, 10.0));
    }

    [Theory]
    [InlineData(32, -32f, -32f)]     // centered origin (-half at mpp 2)
    [InlineData(33, -33f, -33f)]     // odd size — fractional half
    [InlineData(32, -20f, -50f)]     // OFF-CENTER origin — the ellern_map squareSize-1.2 case
    public void ContainsWorldPosition_InvertsTheSamplerPlacement(int size, float originX, float originY)
    {
        // The sampler places at (pixel + jitter∈[0,1)) · mpp in terrain space, the writer
        // shifts by TerrainBlock.position (the origin) into world space. Every jitter inside
        // the pixel must map back to exactly that pixel — regardless of where the terrain
        // is anchored (it is NOT necessarily centered).
        const float mpp = 2f;
        var mask = new bool[size * size];
        const int px = 7;
        const int py = 20;
        mask[py * size + px] = true;

        foreach (var jitter in new[] { 0.0, 0.5, 0.999 })
        {
            var worldX = originX + (px + jitter) * mpp;
            var worldY = originY + (py + jitter) * mpp;
            Assert.True(BiomeCleanupMask.ContainsWorldPosition(mask, size, mpp, originX, originY, worldX, worldY),
                $"jitter {jitter} escaped its pixel");
        }

        // The neighboring pixel's origin must NOT hit.
        Assert.False(BiomeCleanupMask.ContainsWorldPosition(
            mask, size, mpp, originX, originY, originX + (px + 1) * mpp, originY + py * mpp));
    }

    [Fact]
    public void ContainsWorldPosition_OutsideTerrain_IsNeverAHit()
    {
        var mask = new bool[Size * Size];
        Array.Fill(mask, true);

        const float originX = -10f;
        const float originY = -20f;
        Assert.False(BiomeCleanupMask.ContainsWorldPosition(mask, Size, 1f, originX, originY, originX - 1, originY));
        Assert.False(BiomeCleanupMask.ContainsWorldPosition(mask, Size, 1f, originX, originY, originX, originY + Size + 1));
        Assert.True(BiomeCleanupMask.ContainsWorldPosition(mask, Size, 1f, originX, originY, originX + 1, originY + 1));
    }

    [Fact]
    public void FilterLinesWhere_RemovesMatchingItems_KeepsEverythingElseVerbatim()
    {
        var lines = new[]
        {
            """{"type":"oak","pos":[5.0,1.0,10.0],"scale":1.0}""",
            """{"type":"oak","pos":[-5.0,1.0,10.0],"scale":1.0}""",
            "  ", // whitespace line — kept
            """{"name":"ForestBrushGroup","class":"SimGroup"}""", // non-item line — kept
            "not json at all", // malformed — kept
            """{"type":"pine","pos":[6.5,2.0,10.0],"scale":0.9}""",
        };

        var kept = new List<string>();
        var removed = BiomeForestLineFilter.FilterLinesWhereStreaming(
            lines, (_, x, _, _, _) => x > 0, kept.Add);

        Assert.Equal(2, removed);
        Assert.Equal(new[]
        {
            """{"type":"oak","pos":[-5.0,1.0,10.0],"scale":1.0}""",
            "  ",
            """{"name":"ForestBrushGroup","class":"SimGroup"}""",
            "not json at all",
        }, kept);
    }

    [Fact]
    public void CountLinesWhere_MatchesFilterWithoutConsuming()
    {
        var lines = new[]
        {
            """{"type":"oak","pos":[1.0,1.0,0.0],"scale":1.0}""",
            """{"type":"oak","pos":[2.0,1.0,0.0],"scale":1.0}""",
            """{"type":"oak","pos":[-2.0,1.0,0.0],"scale":1.0}""",
        };

        var count = BiomeForestLineFilter.CountLinesWhere(lines, (_, x, _, _, _) => x > 0);

        Assert.Equal(2, count);
    }

    [Fact]
    public void MaskPredicate_EndToEnd_RemovesOnlyItemsOnMask()
    {
        // Simulates the foreign-item cleanup: a mask column in terrain space, items in
        // world coordinates, the predicate joining the two.
        const int size = 16;
        const float mpp = 1f;
        const float originX = -size / 2f;
        const float originY = -size / 2f;
        var mask = new bool[size * size];
        for (var y = 0; y < size; y++)
        {
            mask[y * size + 4] = true; // terrain column x=4
        }

        var onMask = string.Create(CultureInfo.InvariantCulture,
            $$"""{"type":"oak","pos":[{{originX + 4.5}},0.0,5.0],"scale":1.0}""");
        var offMask = string.Create(CultureInfo.InvariantCulture,
            $$"""{"type":"oak","pos":[{{originX + 6.5}},0.0,5.0],"scale":1.0}""");

        var kept = new List<string>();
        var removed = BiomeForestLineFilter.FilterLinesWhereStreaming(
            new[] { onMask, offMask },
            (_, x, y, _, _) => BiomeCleanupMask.ContainsWorldPosition(mask, size, mpp, originX, originY, x, y),
            kept.Add);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { offMask }, kept);
    }
}
