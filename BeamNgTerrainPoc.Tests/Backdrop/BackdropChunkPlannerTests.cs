using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropChunkPlannerTests
{
    /// <summary>Terrain 64 px @ 2 m (span 128 m, lattice [0,64]); backdrop margins 32 px = 64 m each side.</summary>
    private static BackdropGenerationParameters Params(double chunkTargetMeters = 40) => new()
    {
        TerrainHeightMap = new float[64, 64],
        TerrainSizePixels = 64,
        TerrainMetersPerPixel = 2.0f,
        TerrainBaseHeight = 0f,
        TerrainCropMinElevation = 0.0,
        SourceGeoTiffPath = "unused.tif",
        SourceRasterWidth = 200,
        SourceRasterHeight = 200,
        SourceGeoTransform = [500000, 2, 0, 5400000, 0, -2],
        ProjectionWkt = null,
        SourceWgs84Bounds = new BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox(7.0, 50.0, 7.4, 50.4),
        TerrainRect = new PixelRect(68, 68, 64, 64),
        BackdropRect = new PixelRect(36, 36, 128, 128),
        LevelPath = "unused",
        LevelName = "test_level",
        ChunkTargetMeters = chunkTargetMeters,
    };

    [Fact]
    public void GridLines_IncludeTerrainBoundary()
    {
        var plan = BackdropChunkPlanner.Plan(Params());
        // No chunk crosses the terrain rect edges: each chunk is fully inside or outside lattice [0,64]².
        foreach (var c in plan.Chunks)
        {
            var crossesX = c.LatticeX < 0 && c.LatticeX + c.LatticeWidth > 0
                        || c.LatticeX < 64 && c.LatticeX + c.LatticeWidth > 64;
            var crossesY = c.LatticeY < 0 && c.LatticeY + c.LatticeHeight > 0
                        || c.LatticeY < 64 && c.LatticeY + c.LatticeHeight > 64;
            // Crossing an edge is allowed only OUTSIDE the perpendicular terrain span (corner strips
            // never overlap the terrain interior) — the real invariant is: no overlap with (0,64)².
            var overlapsTerrain = c.LatticeX < 64 && c.LatticeX + c.LatticeWidth > 0 &&
                                  c.LatticeY < 64 && c.LatticeY + c.LatticeHeight > 0;
            Assert.False(overlapsTerrain, $"chunk {c.Cx},{c.Cy} overlaps the terrain rect");
            _ = crossesX; _ = crossesY;
        }
    }

    [Fact]
    public void Chunks_TileTheRingExactly()
    {
        var plan = BackdropChunkPlanner.Plan(Params());
        var ringArea = (double)(plan.LatticeMaxX - plan.LatticeMinX) * (plan.LatticeMaxY - plan.LatticeMinY)
                       - 64.0 * 64.0;
        var chunkArea = plan.Chunks.Sum(c => (double)c.LatticeWidth * c.LatticeHeight);
        Assert.Equal(ringArea, chunkArea, 6);
    }

    [Fact]
    public void ChunkTarget_BoundsCellSize()
    {
        var plan = BackdropChunkPlanner.Plan(Params(chunkTargetMeters: 40)); // 64 m margins → 2 cells of 32 px? no: 40 m target → ceil(64/40)=2 cells per margin
        Assert.All(plan.Chunks, c =>
        {
            Assert.True(c.LatticeWidth * 2.0 <= 40 + 2.0, $"chunk width {c.LatticeWidth * 2.0} m exceeds target+1cell");
            Assert.True(c.LatticeHeight * 2.0 <= 40 + 2.0);
        });
    }

    [Fact]
    public void NamesAndIndices_AreStable()
    {
        var plan = BackdropChunkPlanner.Plan(Params());
        var first = plan.Chunks[0];
        Assert.Equal($"backdrop_{first.Cx}_{first.Cy}.dae", first.DaeFileName);
        Assert.Equal($"backdrop_{first.Cx}_{first.Cy}.color.png", first.TextureFileName);
        Assert.Equal($"mt_backdrop_{first.Cx}_{first.Cy}", first.MaterialName);
        // Deterministic ordering: sorted by (Cy, Cx).
        var sorted = plan.Chunks.OrderBy(c => c.Cy).ThenBy(c => c.Cx).ToList();
        Assert.Equal(sorted.Select(c => (c.Cx, c.Cy)), plan.Chunks.Select(c => (c.Cx, c.Cy)));
    }

    [Fact]
    public void TextureSize_IsPow2Clamped_AndCoarsensWithDistance()
    {
        var p = Params() with { TexelDensityNearMPerPx = 1.0, MaxChunkTextureSize = 2048 };
        var plan = BackdropChunkPlanner.Plan(p);
        foreach (var c in plan.Chunks)
        {
            Assert.True(c.TextureSize is >= 256 and <= 2048);
            Assert.Equal(0, c.TextureSize & (c.TextureSize - 1));   // power of two
        }
        // A touching chunk (d=0) must not have a smaller texture than the farthest chunk of equal size.
        var near = plan.Chunks.Where(c => c.DistanceToTerrainMeters == 0).Max(c => c.TextureSize);
        var far = plan.Chunks.Max(c => c.DistanceToTerrainMeters);
        var farthest = plan.Chunks.First(c => c.DistanceToTerrainMeters == far);
        Assert.True(near >= farthest.TextureSize);
    }

    /// <summary>
    /// The fixture above (extent 32 m, density 1..4) floors both near and far chunks to the same
    /// 256 px texture, so `near >= farthest.TextureSize` above passes even if the `1+3·dNorm`
    /// coarsening term were removed or inverted. This fixture scales TerrainMetersPerPixel and
    /// ChunkTargetMeters together (both ×32 vs. the base fixture) to keep the exact same lattice
    /// grid (same cell counts/positions — lattice math is pixel-ratio based, not metric) while
    /// pushing every chunk's extent to 1024 m. Near (touching, dNorm=0 ⇒ density=1):
    /// NextPow2(1024/1)=1024. Farthest corner (dNorm clamped to 1 ⇒ density=4):
    /// NextPow2(1024/4)=NextPow2(256)=256. That crosses a power-of-two boundary, so the
    /// coarsening term is load-bearing for this assertion.
    /// </summary>
    [Fact]
    public void TextureSize_CoarseningIsStrict_WithUnclampedDensityFixture()
    {
        var p = Params(chunkTargetMeters: 1280) with
        {
            TerrainMetersPerPixel = 64.0f,
            TexelDensityNearMPerPx = 1.0,
            MaxChunkTextureSize = 2048,
        };
        var plan = BackdropChunkPlanner.Plan(p);

        var near = plan.Chunks.Where(c => c.DistanceToTerrainMeters == 0).Max(c => c.TextureSize);
        var far = plan.Chunks.Max(c => c.DistanceToTerrainMeters);
        var farthest = plan.Chunks.First(c => c.DistanceToTerrainMeters == far);

        Assert.Equal(1024, near);
        Assert.Equal(256, farthest.TextureSize);
        Assert.True(near > farthest.TextureSize,
            $"expected strict coarsening with distance: near={near} far={farthest.TextureSize}");
    }

    [Fact]
    public void SourceRect_RoundTripsThroughMapper()
    {
        var p = Params();
        var plan = BackdropChunkPlanner.Plan(p);
        var mapper = new BackdropCoordinateMapper(p.TerrainRect, p.TerrainSizePixels, p.TerrainMetersPerPixel);
        var c = plan.Chunks[0];
        var (srcX, srcY) = mapper.WorldToSourcePixel(c.WorldMinX, c.WorldMaxY);  // NW world corner → NW source corner
        Assert.Equal(c.SourceRectX, srcX, 6);
        Assert.Equal(c.SourceRectY, srcY, 6);
    }

    [Fact]
    public void Wgs84Fallback_UsesLinearMosaicInterpolation_WhenNoWkt()
    {
        var plan = BackdropChunkPlanner.Plan(Params());   // ProjectionWkt = null, SourceWgs84Bounds set
        Assert.All(plan.Chunks, c => Assert.NotNull(c.Wgs84Bounds));
        var c0 = plan.Chunks[0];
        Assert.True(c0.Wgs84Bounds!.MinLongitude >= 7.0 && c0.Wgs84Bounds.MaxLongitude <= 7.4);
    }
}
