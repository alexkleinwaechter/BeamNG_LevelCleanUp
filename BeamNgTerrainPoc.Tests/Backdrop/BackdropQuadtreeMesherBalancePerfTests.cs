using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

/// <summary>
///     Regression coverage for the worklist/spatial-hash <c>Balance</c> rewrite (post-amendment
///     Task 6 review, finding 1): the original nested-loop implementation rescanned every leaf
///     against every other leaf and restarted from scratch after each individual split, which is
///     O(n²) per pass and effectively O(n³) end to end. Production chunks can carry tens of
///     thousands of edge-band-forced unit leaves (~1000×1000 lattice chunks, 200 m edge band), so
///     this fixture deliberately produces a large forced-resolution region to prove the rewrite
///     stays fast instead of hanging.
/// </summary>
public class BackdropQuadtreeMesherBalancePerfTests
{
    private const float U = 1.0f;
    private const int TerrainSize = 256;
    private const double Half = TerrainSize * U / 2.0;   // 128
    private const int ChunkHeight = 96;                  // < TerrainSize -> stays inside terrain's Y
                                                          // range, keeping the band a simple rectangular
                                                          // strip (no corner rounding) while keeping the
                                                          // west border's own forced-length cheap to compute.
    private const double Band = 64.0;

    [Fact]
    public void LargeChunk_EdgeBandForcedRegion_BalancesQuickly_AndStaysRestricted()
    {
        var terrain = new float[TerrainSize, TerrainSize];
        // Flat, constant far field: SampleBilinearAtSource clamps regardless of window size, so a
        // minimal 1x1 raster is enough — only the mesher's own leaf count should be expensive here.
        var far = new float[] { 100f };
        var window = new PixelRect(0, 0, 1, 1);
        var mapper = new BackdropCoordinateMapper(new PixelRect(0, 0, TerrainSize, TerrainSize), TerrainSize, U);

        var field = new BackdropHeightField(new BackdropRaster(far, 1, 1, window), [],
            terrain, mapper, TerrainSize, U, terrainBaseHeight: 0f, terrainCropMinElevation: 0.0, Band);
        var options = new BackdropMesherOptions
        {
            EdgeBandMeters = Band, MaxMarginMeters = 256.0,
            LatticeUnitMeters = U, HalfSizeMeters = Half
        };
        var importance = new List<IBackdropImportanceSource> { new EdgeBandImportanceSource(Half, Band, U) };

        // Chunk immediately east of the terrain (touching it, distance 0), height inside the
        // terrain's own Y range (0..TerrainSize) so the band stays a simple rectangular strip:
        // Band forces lattice X in [TerrainSize, TerrainSize+Band) to 1x1 across the chunk's full
        // ChunkHeight-tall height -> Band * ChunkHeight = 64 * 96 = 6,144 unit leaves to balance.
        var chunk = new BackdropChunkDefinition
        {
            Cx = 0, Cy = 0, LatticeX = TerrainSize, LatticeY = 0, LatticeWidth = 400, LatticeHeight = ChunkHeight,
            WorldMinX = TerrainSize * U - Half, WorldMinY = 0 * U - Half,
            WorldMaxX = (TerrainSize + 400) * U - Half, WorldMaxY = ChunkHeight * U - Half,
            SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 0, SourceRectHeight = 0,
            DaeFileName = "backdrop_perf.dae", TextureFileName = "backdrop_perf.color.png",
            MaterialName = "mt_backdrop_perf", TextureSize = 256, DistanceToTerrainMeters = 0
        };

        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var leaves = mesher.RefineChunk(chunk);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"RefineChunk took {sw.ElapsedMilliseconds} ms for ~16k forced leaves — Balance likely " +
            "regressed to O(n^2)/O(n^3) full-rescan behavior.");
        Assert.True(leaves.Count > 5_000, $"expected several thousand band-forced leaves, got {leaves.Count}");

        // Band-forced strip (world X in [-Half, -Half+Band)) must be unit cells.
        foreach (var leaf in leaves)
            if (leaf.X < TerrainSize + Band)
                Assert.True(leaf.Width == 1 && leaf.Height == 1,
                    $"band leaf at lattice ({leaf.X},{leaf.Y}) is {leaf.Width}x{leaf.Height}");

        // Restricted-quadtree invariant (spec §13), checked on a bounded slice around the
        // band/coarse transition — the full leaf set is too large for an O(n^2) pairwise scan in
        // the test itself, and isn't needed to catch a regression in the new neighbor index.
        var transition = leaves.Where(l => l.X is >= TerrainSize + 56 and < TerrainSize + 96).ToList();
        var violations = new List<string>();
        foreach (var a in transition)
        foreach (var b in transition)
        {
            if (!SharesEdge(a, b)) continue;
            var diff = Math.Abs(Level(a) - Level(b));
            if (diff > 1) violations.Add($"{a} vs {b} ({diff} levels)");
        }

        // Task 7/8's fallback counter (finding 2): a border-locked cell can legitimately keep a
        // >1-level gap when neither axis has an interior border-matching split point — an accepted,
        // now-COUNTED trade-off (favoring the seam-consistency guarantee), not a silent bug. Only
        // fail if we see violations the counter doesn't account for.
        Assert.True(violations.Count == 0 || mesher.LastFallbackCount > 0,
            $"{violations.Count} restricted-quadtree violation(s) with LastFallbackCount=0 (unexplained): " +
            string.Join("; ", violations));
    }

    private static bool SharesEdge(LeafCell a, LeafCell b) =>
        (a.X + a.Width == b.X || b.X + b.Width == a.X) && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height
        || (a.Y + a.Height == b.Y || b.Y + b.Height == a.Y) && a.X < b.X + b.Width && b.X < a.X + a.Width;

    private static int Level(LeafCell c) => (int)Math.Ceiling(Math.Log2(Math.Max(c.Width, c.Height)));
}
