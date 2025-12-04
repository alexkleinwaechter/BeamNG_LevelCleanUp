using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Direct road mask smoothing approach (Option A).
/// Simple, robust, works with any road shape including complex intersections.
/// No centerline or spline extraction needed.
/// </summary>
public class DirectRoadExtractor : IRoadExtractor
{
    public RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        
        Console.WriteLine("Using direct road mask approach (no centerline/spline extraction)...");
        
        // Find all road pixels
        var roadPixels = FindRoadPixels(roadLayer);
        
        if (roadPixels.Count == 0)
        {
            Console.WriteLine("Warning: No road pixels found");
            return geometry;
        }
        
        Console.WriteLine($"Found {roadPixels.Count:N0} road pixels");
        
        // For direct approach, we don't need centerline, spline, or cross-sections
        // The DirectTerrainBlender will work directly with the road mask
        geometry.Centerline = new List<Vector2>();
        geometry.Spline = null;
        geometry.CrossSections = new List<CrossSection>();
        
        return geometry;
    }
    
    private List<Vector2> FindRoadPixels(byte[,] roadLayer)
    {
        int height = roadLayer.GetLength(0);
        int width = roadLayer.GetLength(1);
        var pixels = new List<Vector2>();
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (roadLayer[y, x] > 128)
                {
                    pixels.Add(new Vector2(x, y));
                }
            }
        }
        
        return pixels;
    }
}
