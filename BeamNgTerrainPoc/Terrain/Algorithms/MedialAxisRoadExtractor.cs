using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Extracts road geometry using spline-based approach.
/// Creates smooth road spline for direction-aware cross-sectional smoothing.
/// </summary>
public class MedialAxisRoadExtractor : IRoadExtractor
{
    private readonly SparseCenterlineExtractor _centerlineExtractor;
    
    public MedialAxisRoadExtractor()
    {
        _centerlineExtractor = new SparseCenterlineExtractor();
    }
    
    public RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        
        Console.WriteLine("Extracting road geometry with spline-based approach...");
        
        // Step 1: Extract sparse centerline points (pixel coordinates)
        var centerlinePixels = _centerlineExtractor.ExtractCenterlinePoints(roadLayer, metersPerPixel);
        
        if (centerlinePixels.Count < 2)
        {
            Console.WriteLine("Warning: Insufficient centerline points for spline");
            return geometry;
        }
        
        // Step 2: Convert to world coordinates
        geometry.Centerline = centerlinePixels
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();
        
        Console.WriteLine($"Centerline: {geometry.Centerline.Count} points");
        
        // Step 3: Create spline through centerline points
        try
        {
            geometry.Spline = new RoadSpline(geometry.Centerline);
            Console.WriteLine($"Spline created: {geometry.Spline.TotalLength:F1} meters total length");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to create spline: {ex.Message}");
            return geometry;
        }
        
        // Step 4: Sample spline to create cross-sections
        var splineSamples = geometry.Spline.SampleByDistance(parameters.CrossSectionIntervalMeters);
        Console.WriteLine($"Sampled {splineSamples.Count} points along spline");
        
        // Step 5: Convert spline samples to cross-sections
        geometry.CrossSections = splineSamples
            .Select((sample, index) => new CrossSection
            {
                Index = index,
                CenterPoint = sample.Position,
                TangentDirection = sample.Tangent,
                NormalDirection = sample.Normal,
                WidthMeters = parameters.RoadWidthMeters,
                IsExcluded = false
            })
            .ToList();
        
        Console.WriteLine($"Generated {geometry.CrossSections.Count} cross-sections");
        
        return geometry;
    }
}
