using BeamNgTerrainPoc.Terrain.Processing;
using Grille.BeamNG;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using GrilleTerrain = Grille.BeamNG.Terrain;

namespace BeamNgTerrainPoc.Tests.Processing;

/// <summary>
///     Tunnel plan Phase 1 (ai_docs/2026-07-18_tunnel_generation/02): the standalone, source-agnostic
///     hole cutter. Stamps material index 255 (the BeamNG .ter hole sentinel) into the flat
///     material-index grid; providers (tunnel portals Phase 4, hole-map import later) compute the cells.
/// </summary>
public class TerrainHoleCutterTests
{
    private static byte[] MakeGrid(int size, byte fill = 3)
    {
        var grid = new byte[size * size];
        Array.Fill(grid, fill);
        return grid;
    }

    [Fact]
    public void Apply_Cells_StampsOnlyRequestedCells_AndCounts()
    {
        const int size = 8;
        var grid = MakeGrid(size);
        grid[2 * size + 5] = TerrainHoleCutter.HoleMaterialIndex; // pre-existing hole

        var result = TerrainHoleCutter.Apply(grid, size,
        [
            (1, 1),          // fresh
            (5, 2),          // already hole
            (7, 7),          // fresh
            (-1, 0), (8, 0), (0, -1), (0, 8) // out of bounds
        ]);

        Assert.Equal(2, result.CellsStamped);
        Assert.Equal(1, result.CellsAlreadyHole);
        Assert.Equal(4, result.CellsOutOfBounds);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var expectedHole = (x, y) is (1, 1) or (5, 2) or (7, 7);
            Assert.Equal(expectedHole ? TerrainHoleCutter.HoleMaterialIndex : (byte)3, grid[y * size + x]);
        }
    }

    [Fact]
    public void Apply_IsIdempotent_SecondPassStampsNothing()
    {
        const int size = 4;
        var grid = MakeGrid(size);

        var first = TerrainHoleCutter.Apply(grid, size, [(0, 0), (3, 3)]);
        var second = TerrainHoleCutter.Apply(grid, size, [(0, 0), (3, 3)]);

        Assert.Equal(2, first.CellsStamped);
        Assert.Equal(0, second.CellsStamped);
        Assert.Equal(2, second.CellsAlreadyHole);
    }

    [Fact]
    public void Apply_SizeMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => TerrainHoleCutter.Apply(new byte[10], 4, [(0, 0)]));
    }

    [Fact]
    public void Apply_Mask_StampsMatchingCells_TerrainSpaceOrientation()
    {
        const int size = 4;
        var grid = MakeGrid(size);
        var mask = new bool[size, size];
        mask[0, 2] = true; // y=0 = BOTTOM row in terrain space → index 0*size+2

        var result = TerrainHoleCutter.Apply(grid, size, mask);

        Assert.Equal(1, result.CellsStamped);
        Assert.Equal(TerrainHoleCutter.HoleMaterialIndex, grid[0 * size + 2]);
    }

    [Fact]
    public void Apply_Mask_DimensionMismatch_Throws()
    {
        var grid = MakeGrid(4);
        Assert.Throws<ArgumentException>(() => TerrainHoleCutter.Apply(grid, 4, new bool[3, 4]));
        Assert.Throws<ArgumentException>(() => TerrainHoleCutter.Apply(grid, 4, new bool[4, 5]));
    }

    [Fact]
    public void LoadHoleMask_FlipsImageYToTerrainSpace_AndHonorsPolarity()
    {
        const int size = 4;
        var png = Path.Combine(Path.GetTempPath(), $"holemask_{Guid.NewGuid():N}.png");
        try
        {
            // Image space: single BLACK pixel at (x=1, y=0) = TOP row; everything else white.
            using (var image = new Image<L8>(size, size, new L8(255)))
            {
                image[1, 0] = new L8(0);
                image.SaveAsPng(png);
            }

            var mask = TerrainHoleCutter.LoadHoleMask(png, size, blackMeansHole: true);
            // Terrain space: top image row lands at y = size-1.
            Assert.True(mask[size - 1, 1]);
            Assert.Equal(1, CountTrue(mask));

            var inverted = TerrainHoleCutter.LoadHoleMask(png, size, blackMeansHole: false);
            Assert.False(inverted[size - 1, 1]);
            Assert.Equal(size * size - 1, CountTrue(inverted));
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public void LoadHoleMask_SizeMismatch_Throws()
    {
        var png = Path.Combine(Path.GetTempPath(), $"holemask_{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<L8>(4, 4))
            {
                image.SaveAsPng(png);
            }

            Assert.Throws<InvalidDataException>(() => TerrainHoleCutter.LoadHoleMask(png, 8));
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>
    ///     Pipeline round-trip: stamped grid → hardened TerrainCreator fill (byte 255 ⇒ IsHole, material 0)
    ///     → Grille serialize → deserialize ⇒ IsHole survives, height preserved, non-hole cells untouched.
    ///     Extends the Grille lib's own IsHole round-trip test at the pipeline contract level.
    /// </summary>
    [Fact]
    public void StampedGrid_SurvivesTerFileRoundTrip_AsHoles()
    {
        const int size = 16;
        const float maxHeight = 100f;
        var grid = MakeGrid(size, fill: 1);
        TerrainHoleCutter.Apply(grid, size, [(3, 4), (5, 4)]);

        var terrain = new GrilleTerrain(size, new List<string> { "grass", "rock" });
        for (var i = 0; i < size * size; i++)
        {
            // Mirrors TerrainCreator's hardened fill loop.
            var isHole = grid[i] == TerrainHoleCutter.HoleMaterialIndex;
            terrain.Data[i] = new TerrainData
            {
                Height = 40f + i * 0.01f,
                Material = isHole ? 0 : grid[i],
                IsHole = isHole
            };
        }

        using var stream = new MemoryStream();
        terrain.Serialize(stream, maxHeight);
        stream.Position = 0;
        var reloaded = new GrilleTerrain(stream, maxHeight);

        var holeA = reloaded.Data[4 * size + 3];
        var holeB = reloaded.Data[4 * size + 5];
        Assert.True(holeA.IsHole);
        Assert.True(holeB.IsHole);
        Assert.Equal(0, holeA.Material);
        Assert.Equal(40f + (4 * size + 3) * 0.01f, holeA.Height, 0.01f);

        var solid = reloaded.Data[0];
        Assert.False(solid.IsHole);
        Assert.Equal(1, solid.Material);
    }

    private static int CountTrue(bool[,] mask)
    {
        var n = 0;
        foreach (var b in mask)
            if (b)
                n++;
        return n;
    }
}
