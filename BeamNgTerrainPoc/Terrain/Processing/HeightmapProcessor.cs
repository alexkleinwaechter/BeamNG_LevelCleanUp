using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Processing;

/// <summary>
/// Processes heightmap images for terrain creation.
/// </summary>
public static class HeightmapProcessor
{
    /// <summary>
    /// Converts a 16-bit grayscale heightmap image to height values for BeamNG terrain.
    /// </summary>
    /// <param name="heightmapImage">16-bit grayscale heightmap image (L16 format)</param>
    /// <param name="maxHeight">Maximum terrain height in world units</param>
    /// <returns>Array of height values in BeamNG coordinate system (bottom-left to top-right)</returns>
    public static float[] ProcessHeightmap(Image<L16> heightmapImage, float maxHeight)
    {
        int size = heightmapImage.Width;
        var heights = new float[size * size];
        
        // ImageSharp images are top-down (0,0 = top-left)
        // BeamNG expects bottom-up (0,0 = bottom-left)
        // We need to flip vertically during read
        
        // Use row-level span access for much faster pixel reads
        // (avoids per-pixel bounds checks and enables better memory access patterns)
        heightmapImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < size; y++)
            {
                var row = accessor.GetRowSpan(y);
                int flippedY = size - 1 - y;
                int rowOffset = flippedY * size;
                
                for (int x = 0; x < size; x++)
                {
                    heights[rowOffset + x] = row[x].PackedValue / 65535f * maxHeight;
                }
            }
        });
        
        return heights;
    }
}
