using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Calculates dominant roughness values from grayscale roughness textures using layer masks.
/// </summary>
public static class MaskedRoughnessCalculator
{
    /// <summary>
    /// Finds the dominant (most frequent) roughness value of a grayscale texture within the masked area.
    /// The texture (roughnessBaseTex) may be smaller than the terrain and is SCALED (not tiled)
    /// to cover the entire terrain.
    /// 
    /// IMPORTANT: This method counts unique TEXTURE pixels, not terrain pixels.
    /// Each texture pixel is counted only ONCE, regardless of how many terrain pixels
    /// map to it. This ensures the dominant roughness reflects the actual texture content,
    /// not an inflated count from upscaling.
    /// 
    /// Roughness values: 0 = very shiny/smooth (black), 255 = very rough/matte (white)
    /// </summary>
    /// <param name="texturePath">Path to the roughness texture PNG (grayscale)</param>
    /// <param name="mask">Boolean mask array (Size*Size, row-major, BeamNG coords: bottom-left origin)</param>
    /// <param name="terrainSize">Size of the terrain (width = height) from the .ter file</param>
    /// <returns>Roughness value (0-255) of the most frequent intensity, or null if no pixels matched or texture not found</returns>
    public static int? CalculateDominantRoughness(string texturePath, bool[] mask, uint terrainSize)
    {
        if (!File.Exists(texturePath))
        {
            return null;
        }

        // Try to load as grayscale first, fall back to RGBA if needed
        int textureWidth, textureHeight;
        byte[,]? grayscaleData = null;

        try
        {
            // Try loading as L8 (grayscale 8-bit)
            using var imageL8 = Image.Load<L8>(texturePath);
            textureWidth = imageL8.Width;
            textureHeight = imageL8.Height;
            grayscaleData = ExtractGrayscaleData(imageL8);
        }
        catch
        {
            // Fall back to RGBA and convert to grayscale
            using var imageRgba = Image.Load<Rgba32>(texturePath);
            textureWidth = imageRgba.Width;
            textureHeight = imageRgba.Height;
            grayscaleData = ExtractGrayscaleFromRgba(imageRgba);
        }

        if (grayscaleData == null)
        {
            return null;
        }

        int size = (int)terrainSize;

        // Calculate how many terrain pixels each texture pixel covers
        float terrainPixelsPerTexelX = (float)size / textureWidth;
        float terrainPixelsPerTexelY = (float)size / textureHeight;

        // Count frequency of each grayscale value (0-255) by iterating over TEXTURE pixels
        var valueCounts = new long[256];

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

                // Get the grayscale value of this texture pixel
                byte grayscaleValue = grayscaleData[texY, texX];
                valueCounts[grayscaleValue]++;
            }
        }

        // Find the grayscale value with the highest count
        long maxCount = 0;
        int dominantValue = -1;

        for (int i = 0; i < 256; i++)
        {
            if (valueCounts[i] > maxCount)
            {
                maxCount = valueCounts[i];
                dominantValue = i;
            }
        }

        return dominantValue >= 0 ? dominantValue : null;
    }

    /// <summary>
    /// Finds the dominant roughness with detailed statistics about value distribution.
    /// </summary>
    /// <param name="texturePath">Path to the roughness texture PNG (grayscale)</param>
    /// <param name="mask">Boolean mask array</param>
    /// <param name="terrainSize">Size of the terrain from the .ter file</param>
    /// <returns>Result containing dominant roughness, pixel count, and coverage percentage</returns>
    public static DominantRoughnessResult? CalculateDominantRoughnessDetailed(string texturePath, bool[] mask, uint terrainSize)
    {
        if (!File.Exists(texturePath))
        {
            return null;
        }

        // Try to load as grayscale first, fall back to RGBA if needed
        int textureWidth, textureHeight;
        byte[,]? grayscaleData = null;

        try
        {
            using var imageL8 = Image.Load<L8>(texturePath);
            textureWidth = imageL8.Width;
            textureHeight = imageL8.Height;
            grayscaleData = ExtractGrayscaleData(imageL8);
        }
        catch
        {
            using var imageRgba = Image.Load<Rgba32>(texturePath);
            textureWidth = imageRgba.Width;
            textureHeight = imageRgba.Height;
            grayscaleData = ExtractGrayscaleFromRgba(imageRgba);
        }

        if (grayscaleData == null)
        {
            return null;
        }

        int size = (int)terrainSize;

        float terrainPixelsPerTexelX = (float)size / textureWidth;
        float terrainPixelsPerTexelY = (float)size / textureHeight;

        var valueCounts = new long[256];
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

                byte grayscaleValue = grayscaleData[texY, texX];
                valueCounts[grayscaleValue]++;
                totalTexturePixels++;
            }
        }

        if (totalTexturePixels == 0)
        {
            return null;
        }

        // Find dominant value and count unique values
        long maxCount = 0;
        int dominantValue = 0;
        int uniqueValueCount = 0;

        for (int i = 0; i < 256; i++)
        {
            if (valueCounts[i] > 0)
            {
                uniqueValueCount++;
                if (valueCounts[i] > maxCount)
                {
                    maxCount = valueCounts[i];
                    dominantValue = i;
                }
            }
        }

        return new DominantRoughnessResult(
            RoughnessValue: dominantValue,
            DominantPixelCount: maxCount,
            TotalMaskedPixels: totalTexturePixels,
            UniqueValueCount: uniqueValueCount,
            DominantPercentage: totalTexturePixels > 0 ? (float)maxCount / totalTexturePixels * 100f : 0f
        );
    }

    /// <summary>
    /// Extracts grayscale data from an L8 image.
    /// </summary>
    private static byte[,] ExtractGrayscaleData(Image<L8> image)
    {
        var data = new byte[image.Height, image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                data[y, x] = image[x, y].PackedValue;
            }
        }
        return data;
    }

    /// <summary>
    /// Extracts grayscale data from an RGBA image by averaging RGB channels.
    /// </summary>
    private static byte[,] ExtractGrayscaleFromRgba(Image<Rgba32> image)
    {
        var data = new byte[image.Height, image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                // Use luminance formula for accurate grayscale conversion
                // For roughness textures, we often just use R channel since they're typically grayscale
                // but using all channels is safer for edge cases
                data[y, x] = (byte)((pixel.R * 0.299f + pixel.G * 0.587f + pixel.B * 0.114f));
            }
        }
        return data;
    }
}

/// <summary>
/// Result of dominant roughness calculation with statistics.
/// </summary>
/// <param name="RoughnessValue">The dominant roughness value (0-255)</param>
/// <param name="DominantPixelCount">Number of pixels with the dominant roughness value</param>
/// <param name="TotalMaskedPixels">Total number of pixels in the masked area</param>
/// <param name="UniqueValueCount">Number of unique roughness values found in the masked area</param>
/// <param name="DominantPercentage">Percentage of masked area covered by the dominant roughness value (0-100)</param>
public record DominantRoughnessResult(
    int RoughnessValue,
    long DominantPixelCount,
    long TotalMaskedPixels,
    int UniqueValueCount,
    float DominantPercentage
);
