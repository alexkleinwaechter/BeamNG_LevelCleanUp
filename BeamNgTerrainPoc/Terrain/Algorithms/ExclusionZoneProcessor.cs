using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Processes exclusion zones (water, bridges, etc.) where road smoothing should not occur
/// </summary>
public class ExclusionZoneProcessor
{
    /// <summary>
    /// Combines multiple exclusion layer images into a single binary mask
    /// </summary>
    public byte[,] CombineExclusionLayers(List<string> layerPaths, int width, int height)
    {
        var combinedMask = new byte[height, width];
        
        foreach (var layerPath in layerPaths)
        {
            if (!File.Exists(layerPath))
            {
                Console.WriteLine($"Warning: Exclusion layer not found: {layerPath}");
                continue;
            }
            
            try
            {
                using var image = Image.Load<L8>(layerPath);
                
                if (image.Width != width || image.Height != height)
                {
                    Console.WriteLine($"Warning: Exclusion layer size mismatch: {layerPath}. " +
                                    $"Expected {width}x{height}, got {image.Width}x{image.Height}");
                    continue;
                }
                
                // OR operation: any white pixel in any layer = excluded
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (image[x, y].PackedValue > 128)
                        {
                            combinedMask[y, x] = 255;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading exclusion layer {layerPath}: {ex.Message}");
            }
        }
        
        return combinedMask;
    }
    
    /// <summary>
    /// Subtracts exclusion mask from road mask (for smoothing only, not material placement)
    /// </summary>
    public byte[,] ApplyExclusionsToRoadMask(byte[,] roadMask, byte[,] exclusionMask)
    {
        int height = roadMask.GetLength(0);
        int width = roadMask.GetLength(1);
        var result = new byte[height, width];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // AND NOT operation: road pixel AND NOT excluded
                result[y, x] = (roadMask[y, x] > 0 && exclusionMask[y, x] == 0) 
                    ? (byte)255 
                    : (byte)0;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Marks cross-sections that fall in exclusion zones
    /// </summary>
    public void MarkExcludedCrossSections(RoadGeometry geometry, byte[,] exclusionMask, float metersPerPixel)
    {
        int height = exclusionMask.GetLength(0);
        int width = exclusionMask.GetLength(1);
        
        foreach (var crossSection in geometry.CrossSections)
        {
            // Convert world coordinates back to pixel coordinates
            int pixelX = (int)(crossSection.CenterPoint.X / metersPerPixel);
            int pixelY = (int)(crossSection.CenterPoint.Y / metersPerPixel);
            
            // Check if within bounds
            if (pixelX >= 0 && pixelX < width && pixelY >= 0 && pixelY < height)
            {
                // Mark as excluded if the pixel is in the exclusion mask
                crossSection.IsExcluded = exclusionMask[pixelY, pixelX] > 128;
            }
        }
        
        int excludedCount = geometry.CrossSections.Count(cs => cs.IsExcluded);
        if (excludedCount > 0)
        {
            Console.WriteLine($"Marked {excludedCount} cross-sections as excluded");
        }
    }
}
