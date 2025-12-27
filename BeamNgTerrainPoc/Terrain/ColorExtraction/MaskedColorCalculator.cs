using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Calculates dominant colors from textures using layer masks.
/// </summary>
public static class MaskedColorCalculator
{
    /// <summary>
    /// Finds the dominant (most frequent) color of a texture within the masked area.
    /// The texture (baseColorBaseTex) may be smaller than the terrain and is SCALED (not tiled)
    /// to cover the entire terrain.
    /// 
    /// IMPORTANT: This method counts unique TEXTURE pixels, not terrain pixels.
    /// Each texture pixel is counted only ONCE, regardless of how many terrain pixels
    /// map to it. This ensures the dominant color reflects the actual texture content,
    /// not an inflated count from upscaling.
    /// 
    /// Example: For a 1024x1024 texture on a 2048x2048 terrain, we iterate over the
    /// texture pixels (not terrain pixels), check if any terrain pixel in that texture
    /// pixel's coverage area is masked, and count that texture pixel's color once.
    /// </summary>
    /// <param name="texturePath">Path to the basecolor texture PNG</param>
    /// <param name="mask">Boolean mask array (Size*Size, row-major, BeamNG coords: bottom-left origin)</param>
    /// <param name="terrainSize">Size of the terrain (width = height) from the .ter file</param>
    /// <returns>Hex color string (#RRGGBB) of the most frequent color, or null if no pixels matched or texture not found</returns>
    public static string? CalculateDominantColor(string texturePath, bool[] mask, uint terrainSize)
    {
        if (!File.Exists(texturePath))
        {
            return null;
        }

        using var image = Image.Load<Rgba32>(texturePath);

        int size = (int)terrainSize;
        int textureWidth = image.Width;
        int textureHeight = image.Height;

        // Calculate how many terrain pixels each texture pixel covers
        // If texture is 1024 and terrain is 2048, each texture pixel covers 2x2 terrain pixels
        float terrainPixelsPerTexelX = (float)size / textureWidth;
        float terrainPixelsPerTexelY = (float)size / textureHeight;

        // Count frequency of each color by iterating over TEXTURE pixels (not terrain pixels)
        var colorCounts = new Dictionary<int, long>();

        for (int texY = 0; texY < textureHeight; texY++)
        {
            // Convert texture Y to BeamNG terrain Y (flip because texture y=0 is top, BeamNG y=0 is bottom)
            int beamngTexY = textureHeight - 1 - texY;

            // Calculate which terrain rows this texture row covers
            int beamngTerrainStartY = (int)(beamngTexY * terrainPixelsPerTexelY);
            int beamngTerrainEndY = Math.Min((int)((beamngTexY + 1) * terrainPixelsPerTexelY), size);

            for (int texX = 0; texX < textureWidth; texX++)
            {
                // Calculate which terrain columns this texture pixel covers
                int terrainStartX = (int)(texX * terrainPixelsPerTexelX);
                int terrainEndX = Math.Min((int)((texX + 1) * terrainPixelsPerTexelX), size);

                // Check if ANY terrain pixel in this texture pixel's coverage is masked
                bool isTexturePixelMasked = false;

                for (int ty = beamngTerrainStartY; ty < beamngTerrainEndY && !isTexturePixelMasked; ty++)
                {
                    for (int tx = terrainStartX; tx < terrainEndX && !isTexturePixelMasked; tx++)
                    {
                        int maskIndex = ty * size + tx;
                        if (maskIndex >= 0 && maskIndex < mask.Length && mask[maskIndex])
                        {
                            isTexturePixelMasked = true;
                        }
                    }
                }

                if (!isTexturePixelMasked)
                    continue;

                // Get the color of this texture pixel
                var pixel = image[texX, texY];

                // Pack RGB into a single int for fast dictionary lookup (ignore alpha)
                int colorKey = (pixel.R << 16) | (pixel.G << 8) | pixel.B;

                if (colorCounts.TryGetValue(colorKey, out var count))
                {
                    colorCounts[colorKey] = count + 1;
                }
                else
                {
                    colorCounts[colorKey] = 1;
                }
            }
        }

        if (colorCounts.Count == 0)
        {
            return null;
        }

        // Find the color with the highest count
        int dominantColorKey = 0;
        long maxCount = 0;

        foreach (var kvp in colorCounts)
        {
            if (kvp.Value > maxCount)
            {
                maxCount = kvp.Value;
                dominantColorKey = kvp.Key;
            }
        }

        // Unpack the color back to RGB
        byte r = (byte)((dominantColorKey >> 16) & 0xFF);
        byte g = (byte)((dominantColorKey >> 8) & 0xFF);
        byte b = (byte)(dominantColorKey & 0xFF);

        return ToHexColor(r, g, b);
    }

    /// <summary>
    /// Finds the dominant color with detailed statistics about color distribution.
    /// Iterates over texture pixels (not terrain pixels) to count each unique color once.
    /// </summary>
    /// <param name="texturePath">Path to the basecolor texture PNG</param>
    /// <param name="mask">Boolean mask array</param>
    /// <param name="terrainSize">Size of the terrain from the .ter file</param>
    /// <returns>Result containing dominant color, pixel count, and coverage percentage</returns>
    public static DominantColorResult? CalculateDominantColorDetailed(string texturePath, bool[] mask, uint terrainSize)
    {
        if (!File.Exists(texturePath))
        {
            return null;
        }

        using var image = Image.Load<Rgba32>(texturePath);

        int size = (int)terrainSize;
        int textureWidth = image.Width;
        int textureHeight = image.Height;

        float terrainPixelsPerTexelX = (float)size / textureWidth;
        float terrainPixelsPerTexelY = (float)size / textureHeight;

        var colorCounts = new Dictionary<int, long>();
        long totalTexturePixels = 0;

        for (int texY = 0; texY < textureHeight; texY++)
        {
            int beamngTexY = textureHeight - 1 - texY;
            int beamngTerrainStartY = (int)(beamngTexY * terrainPixelsPerTexelY);
            int beamngTerrainEndY = Math.Min((int)((beamngTexY + 1) * terrainPixelsPerTexelY), size);

            for (int texX = 0; texX < textureWidth; texX++)
            {
                int terrainStartX = (int)(texX * terrainPixelsPerTexelX);
                int terrainEndX = Math.Min((int)((texX + 1) * terrainPixelsPerTexelX), size);

                bool isTexturePixelMasked = false;

                for (int ty = beamngTerrainStartY; ty < beamngTerrainEndY && !isTexturePixelMasked; ty++)
                {
                    for (int tx = terrainStartX; tx < terrainEndX && !isTexturePixelMasked; tx++)
                    {
                        int maskIndex = ty * size + tx;
                        if (maskIndex >= 0 && maskIndex < mask.Length && mask[maskIndex])
                        {
                            isTexturePixelMasked = true;
                        }
                    }
                }

                if (!isTexturePixelMasked)
                    continue;

                var pixel = image[texX, texY];
                int colorKey = (pixel.R << 16) | (pixel.G << 8) | pixel.B;

                if (colorCounts.TryGetValue(colorKey, out var count))
                {
                    colorCounts[colorKey] = count + 1;
                }
                else
                {
                    colorCounts[colorKey] = 1;
                }

                totalTexturePixels++;
            }
        }

        if (colorCounts.Count == 0)
        {
            return null;
        }

        int dominantColorKey = 0;
        long maxCount = 0;

        foreach (var kvp in colorCounts)
        {
            if (kvp.Value > maxCount)
            {
                maxCount = kvp.Value;
                dominantColorKey = kvp.Key;
            }
        }

        byte r = (byte)((dominantColorKey >> 16) & 0xFF);
        byte g = (byte)((dominantColorKey >> 8) & 0xFF);
        byte b = (byte)(dominantColorKey & 0xFF);

        return new DominantColorResult(
            HexColor: ToHexColor(r, g, b),
            DominantPixelCount: maxCount,
            TotalMaskedPixels: totalTexturePixels,
            UniqueColorCount: colorCounts.Count,
            DominantPercentage: totalTexturePixels > 0 ? (float)maxCount / totalTexturePixels * 100f : 0f
        );
    }

    /// <summary>
    /// Converts RGB byte values to hex color string.
    /// </summary>
    /// <param name="r">Red component (0-255)</param>
    /// <param name="g">Green component (0-255)</param>
    /// <param name="b">Blue component (0-255)</param>
    /// <returns>Hex color in #RRGGBB format</returns>
    public static string ToHexColor(byte r, byte g, byte b)
    {
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// Parses a hex color string to RGB components.
    /// </summary>
    /// <param name="hexColor">Hex color in #RRGGBB or RRGGBB format</param>
    /// <returns>Tuple of (R, G, B) values, or null if parsing fails</returns>
    public static (byte R, byte G, byte B)? ParseHexColor(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
            return null;

        var hex = hexColor.TrimStart('#');
        if (hex.Length != 6)
            return null;

        try
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return (r, g, b);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the number of true values in a mask.
    /// </summary>
    /// <param name="mask">The boolean mask array</param>
    /// <returns>Count of true values</returns>
    public static int CountMaskedPixels(bool[] mask)
    {
        int count = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i])
                count++;
        }
        return count;
    }
}

/// <summary>
/// Result of dominant color calculation with statistics.
/// </summary>
/// <param name="HexColor">The dominant color in #RRGGBB format</param>
/// <param name="DominantPixelCount">Number of pixels with the dominant color</param>
/// <param name="TotalMaskedPixels">Total number of pixels in the masked area</param>
/// <param name="UniqueColorCount">Number of unique colors found in the masked area</param>
/// <param name="DominantPercentage">Percentage of masked area covered by the dominant color (0-100)</param>
public record DominantColorResult(
    string HexColor,
    long DominantPixelCount,
    long TotalMaskedPixels,
    int UniqueColorCount,
    float DominantPercentage
);
