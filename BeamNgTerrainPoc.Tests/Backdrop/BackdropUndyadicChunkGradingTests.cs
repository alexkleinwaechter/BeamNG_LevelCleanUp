using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

/// <summary>
///     Regression pins for the kattenesbackdrop full-res flood (2026-07-29): the planner emits
///     un-dyadic chunk widths (e.g. 1365 = 12288/9), and naive floor-midpoint splits then produce
///     children whose ceil(log2) levels cannot form a clean 2:1 ladder against the edge band's
///     forced unit cells — the balance pass cascaded 1–2 m cells across whole chunks (855k leaves
///     in a 1365x1024 chunk, 2.72 GB of DAEs, BeamNG level-load hang). Splits now snap to the
///     global dyadic lattice (<see cref="BackdropEdgeSubdivider.DyadicMid"/>).
/// </summary>
public class BackdropUndyadicChunkGradingTests
{
    private const int TerrainSize = 1024;   // lattice [0,1024]^2, world +-512
    private const float U = 1.0f;
    private const double Half = 512.0;
    private const int Margin = 512;         // ring width per side
    private const int Mosaic = TerrainSize + 2 * Margin;   // 2048
    private const double Band = 50.0;

    private static double Dem(double wx, double wy) =>
        100.0 + 5.0 * Math.Sin(wx / 40.0) * Math.Cos(wy / 37.0);

    private static BackdropQuadtreeMesher CreateMesher()
    {
        var mapper = new BackdropCoordinateMapper(
            new PixelRect(Margin, Margin, TerrainSize, TerrainSize), TerrainSize, U);

        var far = new float[Mosaic * Mosaic];
        for (var y = 0; y < Mosaic; y++)
        for (var x = 0; x < Mosaic; x++)
        {
            var (wx, wy) = mapper.SourcePixelToWorld(x + 0.5, y + 0.5);
            far[y * Mosaic + x] = (float)Dem(wx, wy);
        }

        // Terrain heightmap consistent with the DEM (row 0 = SOUTH edge, world y = j*u - half),
        // mirroring a real run where terrain and backdrop share the same elevation source.
        var terrain = new float[TerrainSize, TerrainSize];
        for (var j = 0; j < TerrainSize; j++)
        for (var i = 0; i < TerrainSize; i++)
            terrain[j, i] = (float)Dem(i * U - Half, j * U - Half);

        var field = new BackdropHeightField(
            new BackdropRaster(far, Mosaic, Mosaic, new PixelRect(0, 0, Mosaic, Mosaic)), [],
            terrain, mapper, TerrainSize, U, terrainBaseHeight: 0f, terrainCropMinElevation: 0.0, Band);

        var options = new BackdropMesherOptions
        {
            EdgeBandMeters = Band,
            MaxMarginMeters = Margin,
            MaxVerticalErrorNearMeters = 1.0,
            MaxVerticalErrorFarMeters = 32.0,
            LatticeUnitMeters = U,
            HalfSizeMeters = Half
        };
        var importance = new List<IBackdropImportanceSource> { new EdgeBandImportanceSource(Half, Band, U) };
        return new BackdropQuadtreeMesher(field, options, importance);
    }

    private static BackdropChunkDefinition Chunk(int lx, int ly, int lw, int lh, double distance) => new()
    {
        Cx = 0, Cy = 0, LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
        WorldMinX = lx * U - Half, WorldMinY = ly * U - Half,
        WorldMaxX = (lx + lw) * U - Half, WorldMaxY = (ly + lh) * U - Half,
        SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 0, SourceRectHeight = 0,
        DaeFileName = "pin.dae", TextureFileName = "pin.color.png",
        MaterialName = "pin", TextureSize = 256, DistanceToTerrainMeters = distance
    };

    [Theory]
    [InlineData(0, 512)]     // dyadic control (power-of-two width)
    [InlineData(0, 341)]     // un-dyadic width, kattenes-style (12288/9 scaled)
    [InlineData(57, 455)]    // un-dyadic width AND un-dyadic origin
    public void SeamChunk_LeafCount_StaysNearBandCells_RegardlessOfDyadicity(int lx, int width)
    {
        var mesher = CreateMesher();
        var leaves = mesher.RefineChunk(Chunk(lx, -256, width, 256, distance: 0));

        // Healthy grading = the forced band cells (width x 50) plus a geometric transition
        // ladder. Pre-fix, un-dyadic widths ballooned to ~2.2x the band count (341w: 38,956).
        var bandCells = width * (int)Band;
        Assert.True(leaves.Count <= bandCells * 1.15,
            $"width {width}: {leaves.Count} leaves for {bandCells} forced band cells — grading cascade is back");
    }

    [Theory]
    [InlineData(0, 512, 256)]      // power-of-two range → exact midpoint, unchanged behavior
    [InlineData(0, 1365, 1024)]    // kattenes chunk width → largest dyadic boundary
    [InlineData(1024, 1365, 1280)] // remainder strip → next dyadic boundary
    [InlineData(-256, 0, -128)]    // negative lattice coords (south/west ring chunks)
    [InlineData(5, 7, 6)]          // minimal splittable range → the only interior point
    public void DyadicMid_PicksLargestPowerOfTwoBoundary_NearMidpoint(int a, int b, int expected)
    {
        Assert.Equal(expected, BackdropEdgeSubdivider.DyadicMid(a, b));
    }
}
