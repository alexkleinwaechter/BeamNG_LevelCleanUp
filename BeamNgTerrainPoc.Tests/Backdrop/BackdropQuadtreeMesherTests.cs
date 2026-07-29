using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropQuadtreeMesherTests
{
    private const int Size = 32;
    private const float U = 1.0f;
    private const double Half = 16.0;

    private static (BackdropHeightField Field, BackdropMesherOptions Options, List<IBackdropImportanceSource> Importance)
        Setup(Func<double, double, double> demElevation, double band = 4.0, double maxMargin = 64.0)
    {
        var terrain = new float[Size, Size];
        var window = new PixelRect(0, 0, 160, 160);   // terrain rect at (64,64,32,32) inside a 160² mosaic
        var far = new float[160 * 160];
        var mapper = new BackdropCoordinateMapper(new PixelRect(64, 64, Size, Size), Size, U);
        for (var y = 0; y < 160; y++)
        for (var x = 0; x < 160; x++)
        {
            var (wx, wy) = mapper.SourcePixelToWorld(x + 0.5, y + 0.5);
            far[y * 160 + x] = (float)demElevation(wx, wy);
        }
        var field = new BackdropHeightField(new BackdropRaster(far, 160, 160, window), [],
            terrain, mapper, Size, U, terrainBaseHeight: 0f, terrainCropMinElevation: 0.0, band);
        var options = new BackdropMesherOptions
        {
            EdgeBandMeters = band, MaxMarginMeters = maxMargin,
            LatticeUnitMeters = U, HalfSizeMeters = Half
        };
        var importance = new List<IBackdropImportanceSource> { new EdgeBandImportanceSource(Half, band, U) };
        return (field, options, importance);
    }

    private static BackdropChunkDefinition Chunk(int lx, int ly, int lw, int lh, double distance = 0) => new()
    {
        Cx = 0, Cy = 0, LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
        WorldMinX = lx * U - Half, WorldMinY = ly * U - Half,
        WorldMaxX = (lx + lw) * U - Half, WorldMaxY = (ly + lh) * U - Half,
        SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 0, SourceRectHeight = 0,
        DaeFileName = "backdrop_0_0.dae", TextureFileName = "backdrop_0_0.color.png",
        MaterialName = "mt_backdrop_0_0", TextureSize = 256, DistanceToTerrainMeters = distance
    };

    [Fact]
    public void PlanarDem_CollapsesToCoarseLeaves_OutsideBand()
    {
        var (field, options, importance) = Setup((x, y) => 100.0 + 0.01 * x);   // near-plane → tiny error
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        // Chunk far east of the band: lattice (48..64, 0..16) → world x in [32, 48], d ≥ 16 > band 4.
        var leaves = mesher.RefineChunk(Chunk(48, 0, 16, 16, distance: 16));
        Assert.True(leaves.Count <= 4, $"plane should not refine (got {leaves.Count} leaves)");
    }

    [Fact]
    public void EdgeBand_ForcesUnitCells()
    {
        var (field, options, importance) = Setup((_, _) => 100.0);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        // Chunk touching the east seam: lattice (32..48, 0..16); band = 4 m → cells with worldX in [16,20] must be 1×1.
        var leaves = mesher.RefineChunk(Chunk(32, 0, 16, 16));
        foreach (var leaf in leaves)
        {
            var minX = leaf.X * U - Half;
            if (minX < 16.0 + 4.0 - 1e-9 && leaf.X < 32 + 4)
                Assert.True(leaf.Width == 1 && leaf.Height == 1,
                    $"band leaf at lattice ({leaf.X},{leaf.Y}) is {leaf.Width}x{leaf.Height}");
        }
    }

    [Fact]
    public void SineDem_RefinesUntilErrorBound_Holds()
    {
        var (field, options, importance) = Setup((x, y) => 100.0 + 6.0 * Math.Sin(x / 3.0) * Math.Cos(y / 3.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var chunk = Chunk(48, 0, 16, 16, distance: 16);
        var leaves = mesher.RefineChunk(chunk);
        // Verify the vertical error bound per leaf against the tolerance at its distance (spec §13).
        foreach (var leaf in leaves)
        {
            double minX = leaf.X * U - Half, minY = leaf.Y * U - Half;
            double maxX = minX + leaf.Width * U, maxY = minY + leaf.Height * U;
            if (leaf.Width == 1 && leaf.Height == 1) continue;      // cannot refine further
            var tol = ToleranceAt(field, options, minX, minY, maxX, maxY);
            var err = ProbeError(field, minX, minY, maxX, maxY, 4);
            Assert.True(err <= tol + 1e-6, $"leaf error {err:F3} > tolerance {tol:F3}");
        }
    }

    [Fact]
    public void RestrictedQuadtree_AdjacentLeafLevelsDifferByAtMostOne()
    {
        var (field, options, importance) = Setup((x, y) => 100.0 + 6.0 * Math.Sin(x / 2.5));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var leaves = mesher.RefineChunk(Chunk(32, 0, 32, 32));
        foreach (var a in leaves)
        foreach (var b in leaves)
        {
            if (!SharesEdge(a, b)) continue;
            var la = Level(a); var lb = Level(b);
            Assert.True(Math.Abs(la - lb) <= 1, $"leaves {a} and {b} differ by {Math.Abs(la - lb)} levels");
        }
    }

    [Fact]
    public void EdgeSubdivider_IsDeterministic_AndFullResOnSeam()
    {
        var (field, options, importance) = Setup((_, _) => 100.0);
        // Terrain seam border (fixed x = lattice 32, i.e. worldX = +16): full res → every lattice point.
        var s1 = BackdropEdgeSubdivider.Subdivide(32, verticalEdge: true, 0, 32, field, options, importance);
        var s2 = BackdropEdgeSubdivider.Subdivide(32, verticalEdge: true, 0, 32, field, options, importance);
        Assert.Equal(s1, s2);
        Assert.Equal(33, s1.Count);                                  // 0..32 inclusive
        Assert.Equal(Enumerable.Range(0, 33), s1);
    }

    private static bool SharesEdge(LeafCell a, LeafCell b) =>
        (a.X + a.Width == b.X || b.X + b.Width == a.X) && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height
        || (a.Y + a.Height == b.Y || b.Y + b.Height == a.Y) && a.X < b.X + b.Width && b.X < a.X + a.Width;

    private static int Level(LeafCell c) => (int)Math.Ceiling(Math.Log2(Math.Max(c.Width, c.Height)));

    private static double ToleranceAt(BackdropHeightField field, BackdropMesherOptions o,
        double minX, double minY, double maxX, double maxY)
    {
        var d = Math.Max(0, Math.Min(Math.Min(field.SignedDistanceToTerrainRect(minX, minY),
            field.SignedDistanceToTerrainRect(maxX, minY)), Math.Min(
            field.SignedDistanceToTerrainRect(minX, maxY), field.SignedDistanceToTerrainRect(maxX, maxY))));
        var t = Math.Clamp(d / o.MaxMarginMeters, 0, 1);
        return o.MaxVerticalErrorNearMeters + (o.MaxVerticalErrorFarMeters - o.MaxVerticalErrorNearMeters) * t;
    }

    private static double ProbeError(BackdropHeightField field,
        double minX, double minY, double maxX, double maxY, int n)
    {
        double z00 = field.SampleWorldZ(minX, minY), z10 = field.SampleWorldZ(maxX, minY);
        double z01 = field.SampleWorldZ(minX, maxY), z11 = field.SampleWorldZ(maxX, maxY);
        var worst = 0.0;
        for (var j = 0; j <= n; j++)
        for (var i = 0; i <= n; i++)
        {
            double fx = (double)i / n, fy = (double)j / n;
            var plane = (z00 * (1 - fx) + z10 * fx) * (1 - fy) + (z01 * (1 - fx) + z11 * fx) * fy;
            var actual = field.SampleWorldZ(minX + fx * (maxX - minX), minY + fy * (maxY - minY));
            worst = Math.Max(worst, Math.Abs(actual - plane));
        }
        return worst;
    }
}
