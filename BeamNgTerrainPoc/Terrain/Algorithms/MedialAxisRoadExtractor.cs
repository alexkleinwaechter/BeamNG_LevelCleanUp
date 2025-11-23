using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Extracts road geometry using direct road mask approach.
/// Simple and robust - no complex centerline extraction needed.
/// </summary>
public class MedialAxisRoadExtractor : IRoadExtractor
{
    public RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        
        Console.WriteLine("Using direct road mask approach (no centerline extraction)...");
        
        // Step 1: Convert to binary mask
        var binaryMask = CreateBinaryMask(roadLayer);
        
        // Step 2: Find all road pixels
        var roadPixels = FindRoadPixels(binaryMask);
        
        if (roadPixels.Count == 0)
        {
            Console.WriteLine("Warning: No road pixels found");
            return geometry;
        }
        
        Console.WriteLine($"Found {roadPixels.Count:N0} road pixels");
        
        // For direct approach, we don't need centerline or cross-sections
        // The TerrainBlender will work directly with the road mask
        geometry.Centerline = new List<Vector2>(); // Empty - not needed
        geometry.CrossSections = new List<CrossSection>(); // Empty - not needed
        
        return geometry;
    }
    
    private byte[,] CreateBinaryMask(byte[,] roadLayer)
    {
        int height = roadLayer.GetLength(0);
        int width = roadLayer.GetLength(1);
        var binary = new byte[height, width];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                binary[y, x] = roadLayer[y, x] > 128 ? (byte)255 : (byte)0;
            }
        }
        
        return binary;
    }
    
    private List<Vector2> FindRoadPixels(byte[,] binaryMask)
    {
        int height = binaryMask.GetLength(0);
        int width = binaryMask.GetLength(1);
        var pixels = new List<Vector2>();
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (binaryMask[y, x] > 0)
                {
                    pixels.Add(new Vector2(x, y));
                }
            }
        }
        
        return pixels;
    }
}
