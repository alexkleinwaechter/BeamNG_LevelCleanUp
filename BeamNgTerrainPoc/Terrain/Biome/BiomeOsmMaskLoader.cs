using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// Loads MT_TerrainGeneration OSM mask PNGs into terrain-space region masks.
/// Image space is y-down (row 0 = north); the .ter arrays are y-up (row 0 = south),
/// so rows are flipped on load — the same convention as MaterialLayerProcessor.ProcessRow.
/// A pixel is in-region when its luminance is &gt; 127 (white = feature present).
/// </summary>
public static class BiomeOsmMaskLoader
{
    /// <summary>Luminance above this value counts as in-region (MaterialLayerProcessor rule).</summary>
    public const byte Threshold = 127;

    /// <summary>
    /// Flat row-major terrain-space mask (index = y*size + x, row 0 = south).
    /// Throws <see cref="InvalidDataException"/> when the PNG dimensions differ from size×size —
    /// a mismatched mask belongs to a different terrain and must never be banded.
    /// </summary>
    public static bool[] Load(string pngPath, int size)
    {
        using var image = Image.Load<L8>(pngPath);
        if (image.Width != size || image.Height != size)
        {
            throw new InvalidDataException(
                $"OSM mask '{Path.GetFileName(pngPath)}' is {image.Width}×{image.Height} but the terrain is {size}×{size}.");
        }

        var mask = new bool[size * size];
        image.ProcessPixelRows(accessor =>
        {
            for (var imageY = 0; imageY < size; imageY++)
            {
                var row = accessor.GetRowSpan(imageY);
                var maskRow = (size - 1 - imageY) * size; // image y-down → terrain y-up
                for (var x = 0; x < size; x++)
                {
                    mask[maskRow + x] = row[x].PackedValue > Threshold;
                }
            }
        });
        return mask;
    }

    /// <summary>
    /// Clears mask pixels that are terrain holes (.ter material byte 255) — nothing may be
    /// placed over a hole even when the OSM polygon covers it. Returns the number cleared.
    /// </summary>
    public static int SubtractHoles(bool[] mask, byte[] materialData, byte holeIndex = 255)
    {
        if (mask.Length != materialData.Length)
        {
            throw new ArgumentException(
                $"Mask length {mask.Length} does not match material data length {materialData.Length}.", nameof(mask));
        }

        var cleared = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] && materialData[i] == holeIndex)
            {
                mask[i] = false;
                cleared++;
            }
        }
        return cleared;
    }

    /// <summary>Number of in-region pixels — drives coverage/empty-mask reporting.</summary>
    public static long CountInRegion(bool[] mask)
    {
        long count = 0;
        foreach (var set in mask)
        {
            if (set)
            {
                count++;
            }
        }
        return count;
    }
}
