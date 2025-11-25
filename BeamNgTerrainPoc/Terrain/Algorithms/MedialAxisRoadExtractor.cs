using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

public class MedialAxisRoadExtractor : IRoadExtractor
{
    private readonly SkeletonizationRoadExtractor _skeletonExtractor;
    public MedialAxisRoadExtractor(){ _skeletonExtractor = new SkeletonizationRoadExtractor(); }
    
    public RoadGeometry ExtractRoadGeometry(byte[,] roadLayer, RoadSmoothingParameters parameters, float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadLayer, parameters);
        Console.WriteLine("Extracting road geometry with skeletonization-based spline approach...");
        var centerlinePathsPixels = _skeletonExtractor.ExtractCenterlinePaths(roadLayer, parameters);
        if(centerlinePathsPixels.Count==0){ Console.WriteLine("Warning: No centerline paths extracted"); return geometry; }
        Console.WriteLine($"Extracted {centerlinePathsPixels.Count} path(s)");
        int totalCrossSections=0; var allCrossSections=new List<CrossSection>(); int globalIndex=0; int pathId=0;
        foreach(var pathPixels in centerlinePathsPixels){ if(pathPixels.Count<3){ Console.WriteLine($"  Skipping path with only {pathPixels.Count} points"); pathId++; continue; }
            var worldPoints = pathPixels.Select(p=> new Vector2(p.X*metersPerPixel, p.Y*metersPerPixel)).ToList();
            try {
                var pathSpline = new RoadSpline(worldPoints);
                float pathLength = pathSpline.TotalLength;
                Console.WriteLine($"  Path {pathId}: spline length {pathLength:F1}m, points {worldPoints.Count}");
                var splineSamples = pathSpline.SampleByDistance(parameters.CrossSectionIntervalMeters);
                int localIndex=0;
                foreach(var sample in splineSamples){ allCrossSections.Add(new CrossSection{ Index=globalIndex++, PathId=pathId, LocalIndex=localIndex++, CenterPoint=sample.Position, TangentDirection=sample.Tangent, NormalDirection=sample.Normal, WidthMeters=parameters.RoadWidthMeters, IsExcluded=false }); }
                totalCrossSections += splineSamples.Count;
                if(geometry.Centerline.Count==0 || worldPoints.Count>geometry.Centerline.Count){ geometry.Centerline=worldPoints; geometry.Spline=pathSpline; }
            } catch(Exception ex){ Console.WriteLine($"  Warning: Failed to create spline for path {pathId}: {ex.Message}"); }
            pathId++;
        }
        geometry.CrossSections = allCrossSections;
        Console.WriteLine($"Generated {totalCrossSections} total cross-sections from {centerlinePathsPixels.Count} paths");
        return geometry;
    }
}
