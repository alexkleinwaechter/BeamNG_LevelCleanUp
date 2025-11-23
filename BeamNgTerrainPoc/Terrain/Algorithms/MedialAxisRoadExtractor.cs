using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Extracts road geometry using skeletonization-based spline fitting.
/// Uses Zhang-Suen thinning to get clean centerlines that handle intersections naturally.
/// </summary>
public class MedialAxisRoadExtractor : IRoadExtractor
{
    private readonly SkeletonizationRoadExtractor _skeletonExtractor;
    
    public MedialAxisRoadExtractor()
    {
        _skeletonExtractor = new SkeletonizationRoadExtractor();
    }
    
    public RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        
        Console.WriteLine("Extracting road geometry with skeletonization-based spline approach...");
        
        // Step 1: Extract centerline paths using skeletonization
        var centerlinePathsPixels = _skeletonExtractor.ExtractCenterlinePaths(roadLayer);
        
        if (centerlinePathsPixels.Count == 0)
        {
            Console.WriteLine("Warning: No centerline paths extracted");
            return geometry;
        }
        
        Console.WriteLine($"Extracted {centerlinePathsPixels.Count} path(s)");
        
        // Step 2: Process each path separately
        int totalCrossSections = 0;
        var allCrossSections = new List<CrossSection>();
        int sectionIndex = 0;
        
        foreach (var pathPixels in centerlinePathsPixels)
        {
            if (pathPixels.Count < 3)
            {
                Console.WriteLine($"  Skipping path with only {pathPixels.Count} points");
                continue;
            }
            
            // For bitmap skeletons, use ALL points - they're already simplified by thinning!
            // Skeletonization mathematically reduced the road to 1-pixel wide
            // No need for additional simplification
            var simplified = pathPixels;
            Console.WriteLine($"  Path: {pathPixels.Count} skeleton points (no simplification needed for bitmap data)");
            
            if (simplified.Count < 2)
                continue;
            
            // Convert to world coordinates
            var worldPoints = simplified
                .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
                .ToList();
            
            // Create spline for this path
            try
            {
                var pathSpline = new RoadSpline(worldPoints);
                float pathLength = pathSpline.TotalLength;
                
                Console.WriteLine($"  Spline: {pathLength:F1}m length, {simplified.Count} control points from skeleton");
                
                // Sample spline to create cross-sections
                var splineSamples = pathSpline.SampleByDistance(parameters.CrossSectionIntervalMeters);
                
                // Generate cross-sections from samples
                foreach (var sample in splineSamples)
                {
                    allCrossSections.Add(new CrossSection
                    {
                        Index = sectionIndex++,
                        CenterPoint = sample.Position,
                        TangentDirection = sample.Tangent,
                        NormalDirection = sample.Normal,
                        WidthMeters = parameters.RoadWidthMeters,
                        IsExcluded = false
                    });
                }
                
                totalCrossSections += splineSamples.Count;
                
                // Store the first/longest path as the main centerline
                if (geometry.Centerline.Count == 0 || worldPoints.Count > geometry.Centerline.Count)
                {
                    geometry.Centerline = worldPoints;
                    geometry.Spline = pathSpline;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Failed to create spline for path: {ex.Message}");
            }
        }
        
        geometry.CrossSections = allCrossSections;
        
        Console.WriteLine($"Generated {totalCrossSections} total cross-sections from {centerlinePathsPixels.Count} paths");
        
        return geometry;
    }
}
