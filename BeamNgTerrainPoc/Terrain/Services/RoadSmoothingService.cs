using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Main service for applying road smoothing to heightmaps.
/// Supports both Direct (robust) and Spline-based (curved roads) approaches.
/// </summary>
public class RoadSmoothingService
{
    private IRoadExtractor? _roadExtractor;
    private IHeightCalculator? _heightCalculator;
    private ExclusionZoneProcessor? _exclusionProcessor;
    private object? _terrainBlender; // Can be TerrainBlender or DirectTerrainBlender
    
    public RoadSmoothingService(
        IRoadExtractor roadExtractor,
        IHeightCalculator heightCalculator,
        ExclusionZoneProcessor exclusionProcessor,
        object terrainBlender)
    {
        _roadExtractor = roadExtractor ?? throw new ArgumentNullException(nameof(roadExtractor));
        _heightCalculator = heightCalculator ?? throw new ArgumentNullException(nameof(heightCalculator));
        _exclusionProcessor = exclusionProcessor ?? throw new ArgumentNullException(nameof(exclusionProcessor));
        _terrainBlender = terrainBlender ?? throw new ArgumentNullException(nameof(terrainBlender));
    }
    
    /// <summary>
    /// Constructor - components will be created based on parameters.Approach
    /// </summary>
    public RoadSmoothingService()
    {
        _exclusionProcessor = new ExclusionZoneProcessor();
    }
    
    public SmoothingResult SmoothRoadsInHeightmap(
        float[,] heightMap,
        byte[,] roadLayer,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        Console.WriteLine($"Starting road smoothing (Approach: {parameters.Approach})...");
        
        // Validate parameters
        var validationErrors = parameters.Validate();
        if (validationErrors.Any())
        {
            throw new ArgumentException($"Invalid parameters: {string.Join(", ", validationErrors)}");
        }
        
        // Auto-adjust CrossSectionIntervalMeters to prevent "dotted road" artifacts
        float totalImpactRadius = (parameters.RoadWidthMeters / 2.0f) + parameters.TerrainAffectedRangeMeters;
        float recommendedMaxInterval = totalImpactRadius / 3.0f; // Need at least 3 cross-sections across impact zone
        
        if (parameters.CrossSectionIntervalMeters > recommendedMaxInterval)
        {
            Console.WriteLine($"  ?? WARNING: CrossSectionIntervalMeters ({parameters.CrossSectionIntervalMeters}m) may cause gaps!");
            Console.WriteLine($"  Recommended max: {recommendedMaxInterval:F2}m for {totalImpactRadius:F1}m impact radius");
            Console.WriteLine($"  Auto-adjusting to prevent dotted roads...");
            parameters.CrossSectionIntervalMeters = recommendedMaxInterval * 0.8f; // Use 80% for safety margin
            Console.WriteLine($"  ? Adjusted to: {parameters.CrossSectionIntervalMeters:F2}m");
        }
        
        // Warn about global leveling with small blend zones
        if (parameters.GlobalLevelingStrength > 0.5f && parameters.TerrainAffectedRangeMeters < 15.0f)
        {
            Console.WriteLine($"  ?? WARNING: High GlobalLevelingStrength ({parameters.GlobalLevelingStrength:F2}) + small blend range ({parameters.TerrainAffectedRangeMeters}m)");
            Console.WriteLine($"  This combination may create disconnected road segments (dots)!");
            Console.WriteLine($"  Consider: GlobalLevelingStrength=0 (terrain-following) OR TerrainAffectedRangeMeters?15m");
        }
        
        // Initialize components based on approach
        InitializeComponents(parameters.Approach);
        
        // 1. Process exclusions
        byte[,]? exclusionMask = null;
        if (parameters.ExclusionLayerPaths?.Any() == true)
        {
            Console.WriteLine($"Processing {parameters.ExclusionLayerPaths.Count} exclusion layers...");
            exclusionMask = _exclusionProcessor!.CombineExclusionLayers(
                parameters.ExclusionLayerPaths,
                roadLayer.GetLength(1),
                roadLayer.GetLength(0));
        }
        
        // 2. Extract road geometry
        byte[,] smoothingMask = exclusionMask != null
            ? _exclusionProcessor!.ApplyExclusionsToRoadMask(roadLayer, exclusionMask)
            : roadLayer;
        
        Console.WriteLine("Extracting road geometry...");
        var geometry = _roadExtractor!.ExtractRoadGeometry(
            smoothingMask,
            parameters,
            metersPerPixel);
        
        // Optional spline debug export
        if (parameters.Approach == RoadSmoothingApproach.SplineBased && parameters.ExportSplineDebugImage)
        {
            try { ExportSplineDebugImage(geometry, metersPerPixel, parameters, "spline_debug.png"); }
            catch (Exception ex) { Console.WriteLine($"Spline debug export failed: {ex.Message}"); }
        }
        
        float[,] newHeightMap = heightMap; // default no change
        
        if (!parameters.EnableTerrainBlending)
        {
            Console.WriteLine("Terrain blending disabled (debug mode). Returning original heightmap with geometry only.");
        }
        else
        {
            if (parameters.Approach == RoadSmoothingApproach.DirectMask)
            {
                // Direct approach - no cross-sections needed
                var directBlender = (DirectTerrainBlender)_terrainBlender!;
                newHeightMap = directBlender.BlendRoadWithTerrain(
                    heightMap,
                    geometry,
                    parameters,
                    metersPerPixel);
            }
            else
            {
                // Spline approach - calculate elevations first
                if (geometry.CrossSections.Count > 0)
                {
                    Console.WriteLine("Calculating target elevations for cross-sections...");
                    _heightCalculator!.CalculateTargetElevations(geometry, heightMap, metersPerPixel);

                    // Export smoothed elevation debug image if enabled
                    if (parameters.ExportSmoothedElevationDebugImage)
                    {
                        try { ExportSmoothedElevationDebugImage(geometry, metersPerPixel, parameters); }
                        catch (Exception ex) { Console.WriteLine($"Smoothed elevation debug export failed: {ex.Message}"); }
                    }
                }
                
                var splineBlender = (TerrainBlender)_terrainBlender!;
                newHeightMap = splineBlender.BlendRoadWithTerrain(
                    heightMap,
                    geometry,
                    parameters,
                    metersPerPixel);
            }
        }
        
        // 4. Calculate delta map and statistics
        var deltaMap = CalculateDeltaMap(heightMap, newHeightMap);
        var statistics = CalculateStatistics(heightMap, newHeightMap, parameters, metersPerPixel);
        
        Console.WriteLine("Road smoothing complete!");
        
        return new SmoothingResult(newHeightMap, deltaMap, statistics, geometry);
    }
    
    private void InitializeComponents(RoadSmoothingApproach approach)
    {
        if (approach == RoadSmoothingApproach.DirectMask)
        {
            Console.WriteLine("Using DIRECT road mask approach (robust, handles intersections)");
            _roadExtractor = new DirectRoadExtractor();
            _terrainBlender = new DirectTerrainBlender();
            _heightCalculator = null; // Not needed for direct approach
        }
        else
        {
            Console.WriteLine("Using SPLINE-BASED approach (level on curves, simple roads only)");
            _roadExtractor = new MedialAxisRoadExtractor();
            _heightCalculator = new CrossSectionalHeightCalculator();
            _terrainBlender = new TerrainBlender();
        }
    }
    
    private void ExportSplineDebugImage(RoadGeometry geometry, float metersPerPixel, RoadSmoothingParameters parameters, string fileName)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        using var image = new Image<Rgba32>(width, height, new Rgba32(0,0,0,255));
        
        // Draw road mask faintly
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (geometry.RoadMask[y, x] > 128)
                {
                    image[x, height - 1 - y] = new Rgba32(32,32,32,255); // flip Y for visualization
                }
            }
        }
        
        // Draw spline centerline samples
        float sampleInterval = parameters.CrossSectionIntervalMeters;
        for (float d = 0; d <= geometry.Spline.TotalLength; d += sampleInterval)
        {
            var p = geometry.Spline.GetPointAtDistance(d);
            int px = (int)(p.X / metersPerPixel);
            int py = (int)(p.Y / metersPerPixel);
            if (px >=0 && px < width && py >=0 && py < height)
            {
                image[px, height - 1 - py] = new Rgba32(255,255,0,255); // yellow centerline
            }
        }
        
        // Draw road width (perpendicular segments) for a subset of cross-sections
        int step = Math.Max(1, (int)(2.0f * parameters.CrossSectionIntervalMeters));
        foreach (var cs in geometry.CrossSections.Where(c => c.LocalIndex % step == 0))
        {
            var center = cs.CenterPoint;
            float halfWidth = parameters.RoadWidthMeters / 2.0f;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            DrawLine(image, lx, ly, rx, ry, new Rgba32(0,255,0,255)); // green road width
        }
        
        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, fileName);
        image.SaveAsPng(filePath);
        Console.WriteLine($"Exported spline debug image: {filePath}");
    }

    private void ExportSmoothedElevationDebugImage(RoadGeometry geometry, float metersPerPixel, RoadSmoothingParameters parameters)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));

        var elevations = geometry.CrossSections.Select(cs => cs.TargetElevation).Where(e => e > 0).ToList();
        if (!elevations.Any())
        {
            Console.WriteLine("No valid elevations to create smoothed debug image.");
            return;
        }
        float minElev = elevations.Min();
        float maxElev = elevations.Max();
        float range = maxElev - minElev;
        if (range < 0.01f) range = 1f;

        // Draw road mask
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (geometry.RoadMask[y, x] > 128)
                {
                    image[x, height - 1 - y] = new Rgba32(32, 32, 32, 255);
                }
            }
        }

        // Color code the road based on smoothed target elevations
        foreach (var cs in geometry.CrossSections)
        {
            if (cs.TargetElevation <= 0) continue;

            float normalizedElevation = (cs.TargetElevation - minElev) / range;
            var color = GetColorForValue(normalizedElevation);

            var center = cs.CenterPoint;
            float halfWidth = parameters.RoadWidthMeters / 2.0f;
            var left = center - cs.NormalDirection * halfWidth;
            var right = center + cs.NormalDirection * halfWidth;
            int lx = (int)(left.X / metersPerPixel);
            int ly = (int)(left.Y / metersPerPixel);
            int rx = (int)(right.X / metersPerPixel);
            int ry = (int)(right.Y / metersPerPixel);
            DrawLine(image, lx, ly, rx, ry, color);
        }

        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "spline_smoothed_elevation_debug.png");
        image.SaveAsPng(filePath);
        Console.WriteLine($"Exported smoothed elevation debug image: {filePath}");
        Console.WriteLine($"  Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)");
    }

    private Rgba32 GetColorForValue(float value)
    {
        // Blue -> Cyan -> Green -> Yellow -> Red gradient
        value = Math.Clamp(value, 0f, 1f);
        float r = Math.Clamp(value * 2.0f, 0f, 1f);
        float b = Math.Clamp((1.0f - value) * 2.0f, 0f, 1f);
        float g = 1.0f - Math.Abs(value - 0.5f) * 2.0f;
        return new Rgba32(r, g, b);
    }
    
    private void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        int height = img.Height;
        // Flip Y input -> image coordinates
        y0 = height - 1 - y0;
        y1 = height - 1 - y1;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1; int err = dx + dy;
        while (true)
        {
            if (x0 >=0 && x0 < img.Width && y0 >=0 && y0 < img.Height)
                img[x0, y0] = color;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
    
    private float[,] CalculateDeltaMap(float[,] original, float[,] modified)
    {
        int h = original.GetLength(0); int w = original.GetLength(1); var delta = new float[h,w];
        for (int y=0;y<h;y++) for (int x=0;x<w;x++) delta[y,x] = modified[y,x]-original[y,x];
        return delta;
    }
    
    private SmoothingStatistics CalculateStatistics(float[,] original,float[,] modified,RoadSmoothingParameters parameters,float metersPerPixel)
    {
        var stats = new SmoothingStatistics();
        int h = original.GetLength(0); int w = original.GetLength(1);
        float cut=0, fill=0; int modPixels=0; float maxDisc=0; float pixelArea = metersPerPixel * metersPerPixel; const float th=0.001f;
        var mask = new bool[h,w];
        for (int y=0;y<h;y++) for (int x=0;x<w;x++) { float d = modified[y,x]-original[y,x]; if (MathF.Abs(d)>th){modPixels++; mask[y,x]=true; if(d<0) cut+=MathF.Abs(d)*pixelArea; else fill+=d*pixelArea;} }
        for (int y=0;y<h;y++) for (int x=0;x<w;x++) if(mask[y,x]) { if(x<w-1 && mask[y,x+1]) { float disc=MathF.Abs(modified[y,x+1]-modified[y,x]); if(disc>maxDisc) maxDisc=disc;} if(y<h-1 && mask[y+1,x]) { float disc=MathF.Abs(modified[y+1,x]-modified[y,x]); if(disc>maxDisc) maxDisc=disc;} }
        stats.TotalCutVolume=cut; stats.TotalFillVolume=fill; stats.PixelsModified=modPixels; stats.MaxDiscontinuity=maxDisc; stats.MaxRoadSlope=MathF.Atan(maxDisc/metersPerPixel)*180.0f/MathF.PI; stats.MaxSideSlope=parameters.SideMaxSlopeDegrees; stats.MaxTransverseSlope=0; stats.MeetsAllConstraints=true; if(stats.MaxRoadSlope>parameters.RoadMaxSlopeDegrees+0.1f){ stats.ConstraintViolations.Add($"Max road slope ({stats.MaxRoadSlope:F2}°) exceeds limit ({parameters.RoadMaxSlopeDegrees}°)"); stats.MeetsAllConstraints=false; } return stats;
    }
}
