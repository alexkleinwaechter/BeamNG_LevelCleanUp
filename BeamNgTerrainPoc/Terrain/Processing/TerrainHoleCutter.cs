using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Processing;

/// <summary>
///     Stamps terrain holes (material index 255) into the flat material-index grid that
///     <see cref="TerrainCreator" /> serializes into the .ter layer map. Source-agnostic: callers
///     (tunnel portal provider, hole-map import) compute the cells; this class only validates and
///     stamps. Grid convention: row-major, <c>index = y * size + x</c>, y = 0 at the BOTTOM/south
///     edge (BeamNG terrain space, matching <see cref="MaterialLayerProcessor" /> /
///     <see cref="HeightmapProcessor" />).
///     <para>Idempotent: re-stamping an already-hole cell is a counted no-op, so multiple providers
///     (tunnel portals + an imported hole map) can run in sequence. No unstamping — regeneration
///     always starts from a fresh grid.</para>
/// </summary>
public static class TerrainHoleCutter
{
    /// <summary>
    ///     BeamNG hole sentinel in the .ter layer map (V9 format: byte 255 ⇒ <c>IsHole=true</c> on read).
    ///     Terrain materials must use indices 0..254 — <see cref="Validation.TerrainValidator" /> warns
    ///     when a level's material count would collide with this sentinel.
    /// </summary>
    public const byte HoleMaterialIndex = byte.MaxValue; // 255

    /// <summary>
    ///     Stamps the given cells as holes. Out-of-bounds cells are skipped (counted).
    ///     Returns the number of cells newly converted to holes (already-hole cells don't count).
    /// </summary>
    public static HoleCutResult Apply(byte[] materialIndices, int size, IEnumerable<(int X, int Y)> holeCells)
    {
        ArgumentNullException.ThrowIfNull(materialIndices);
        ArgumentNullException.ThrowIfNull(holeCells);
        if (size <= 0 || materialIndices.Length != size * size)
            throw new ArgumentException(
                $"materialIndices length {materialIndices.Length} does not match size {size}×{size}.", nameof(size));

        var stamped = 0;
        var alreadyHole = 0;
        var outOfBounds = 0;
        foreach (var (x, y) in holeCells)
        {
            if (x < 0 || x >= size || y < 0 || y >= size)
            {
                outOfBounds++;
                continue;
            }

            var i = y * size + x;
            if (materialIndices[i] == HoleMaterialIndex)
            {
                alreadyHole++;
                continue;
            }

            materialIndices[i] = HoleMaterialIndex;
            stamped++;
        }

        return new HoleCutResult(stamped, alreadyHole, outOfBounds);
    }

    /// <summary>
    ///     Mask overload: <c>mask[y, x] == true</c> ⇒ hole. Mask dimensions must equal size×size
    ///     (throws otherwise). Mask is in terrain space (y = 0 = bottom/south), same as the grid.
    /// </summary>
    public static HoleCutResult Apply(byte[] materialIndices, int size, bool[,] holeMask)
    {
        ArgumentNullException.ThrowIfNull(holeMask);
        if (holeMask.GetLength(0) != size || holeMask.GetLength(1) != size)
            throw new ArgumentException(
                $"Hole mask is {holeMask.GetLength(0)}×{holeMask.GetLength(1)} but terrain is {size}×{size}.",
                nameof(holeMask));

        return Apply(materialIndices, size, EnumerateMaskCells(holeMask, size));
    }

    private static IEnumerable<(int X, int Y)> EnumerateMaskCells(bool[,] holeMask, int size)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            if (holeMask[y, x])
                yield return (x, y);
    }

    /// <summary>
    ///     Loads a hole mask from a hole-map PNG (image space, y-down) into terrain space (y-up —
    ///     the same flip as <see cref="MaterialLayerProcessor" />.ProcessRow). Luminance &lt; 128 =
    ///     hole when <paramref name="blackMeansHole" /> (the polarity is explicit because the in-repo
    ///     conventions contradict — see ai_docs/2026-07-18_tunnel_generation/02 §"polarity").
    ///     Throws if the image dimensions are not size×size.
    /// </summary>
    public static bool[,] LoadHoleMask(string pngPath, int size, bool blackMeansHole = true)
    {
        using var image = Image.Load<L8>(pngPath);
        if (image.Width != size || image.Height != size)
            throw new InvalidDataException(
                $"Hole map '{pngPath}' is {image.Width}×{image.Height} but terrain is {size}×{size}.");

        var mask = new bool[size, size];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < size; y++)
            {
                var row = accessor.GetRowSpan(y);
                var flippedY = size - 1 - y; // image y-down → terrain y-up
                for (var x = 0; x < size; x++)
                {
                    var dark = row[x].PackedValue < 128;
                    mask[flippedY, x] = blackMeansHole ? dark : !dark;
                }
            }
        });

        return mask;
    }
}

/// <summary>Counts from a <see cref="TerrainHoleCutter" /> stamping pass (feeds [TUNNEL-HOLE]/import logs).</summary>
public sealed record HoleCutResult(int CellsStamped, int CellsAlreadyHole, int CellsOutOfBounds)
{
    public static HoleCutResult Empty { get; } = new(0, 0, 0);

    /// <summary>Merges counters from sequential stamping passes over the same grid.</summary>
    public HoleCutResult Add(HoleCutResult other) => new(
        CellsStamped + other.CellsStamped,
        CellsAlreadyHole + other.CellsAlreadyHole,
        CellsOutOfBounds + other.CellsOutOfBounds);
}
