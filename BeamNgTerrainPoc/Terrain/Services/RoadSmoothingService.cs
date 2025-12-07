using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Logging;
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
    private JunctionElevationHarmonizer? _junctionHarmonizer;
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
        _junctionHarmonizer = new JunctionElevationHarmonizer();
    }
    
    /// <summary>
    /// Constructor - components will be created based on parameters.Approach
    /// </summary>
    public RoadSmoothingService()
    {
        _exclusionProcessor = new ExclusionZoneProcessor();
        _junctionHarmonizer = new JunctionElevationHarmonizer();
    }
    
    public SmoothingResult SmoothRoadsInHeightmap(
        float[,] heightMap,
        byte[,] roadLayer,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        TerrainLogger.Info($"Starting road smoothing (Approach: {parameters.Approach})...");
        
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
            TerrainLogger.Warning($"CrossSectionIntervalMeters ({parameters.CrossSectionIntervalMeters}m) may cause gaps!");
            TerrainLogger.Warning($"Recommended max: {recommendedMaxInterval:F2}m for {totalImpactRadius:F1}m impact radius");
            TerrainLogger.Info("Auto-adjusting to prevent dotted roads...");
            parameters.CrossSectionIntervalMeters = recommendedMaxInterval * 0.8f; // Use 80% for safety margin
            TerrainLogger.Info($"Adjusted to: {parameters.CrossSectionIntervalMeters:F2}m");
        }
        
        // Warn about global leveling with small blend zones
        if (parameters.GlobalLevelingStrength > 0.5f && parameters.TerrainAffectedRangeMeters < 15.0f)
        {
            TerrainLogger.Warning($"High GlobalLevelingStrength ({parameters.GlobalLevelingStrength:F2}) + small blend range ({parameters.TerrainAffectedRangeMeters}m)");
            TerrainLogger.Warning("This combination may create disconnected road segments (dots)!");
            TerrainLogger.Warning("Consider: GlobalLevelingStrength=0 (terrain-following) OR TerrainAffectedRangeMeters?15m");
        }
        
        // Initialize components based on approach
        InitializeComponents(parameters.Approach);
        
        // 1. Process exclusions
        byte[,]? exclusionMask = null;
        if (parameters.ExclusionLayerPaths?.Any() == true)
        {
            TerrainLogger.Info($"Processing {parameters.ExclusionLayerPaths.Count} exclusion layers...");
            exclusionMask = _exclusionProcessor!.CombineExclusionLayers(
                parameters.ExclusionLayerPaths,
                roadLayer.GetLength(1),
                roadLayer.GetLength(0));
        }
        
        // 2. Extract road geometry
        byte[,] smoothingMask = exclusionMask != null
            ? _exclusionProcessor!.ApplyExclusionsToRoadMask(roadLayer, exclusionMask)
            : roadLayer;
        
        TerrainLogger.Info("Extracting road geometry...");
        var geometry = _roadExtractor!.ExtractRoadGeometry(
            smoothingMask,
            parameters,
            metersPerPixel);
        
        // Optional spline debug export
        if (parameters.Approach == RoadSmoothingApproach.Spline && parameters.ExportSplineDebugImage)
        {
            try { ExportSplineDebugImage(geometry, metersPerPixel, parameters, "spline_debug.png"); }
            catch (Exception ex) { TerrainLogger.Warning($"Spline debug export failed: {ex.Message}"); }
        }
        
        float[,] newHeightMap = heightMap; // default no change
        
        if (!parameters.EnableTerrainBlending)
        {
            TerrainLogger.Info("Terrain blending disabled (debug mode). Returning original heightmap with geometry only.");
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
            else // RoadSmoothingApproach.Spline
            {
                // Spline approach - calculate elevations first, then use distance field blending
                if (geometry.CrossSections.Count > 0)
                {
                    TerrainLogger.Info("Calculating target elevations for cross-sections...");
                    _heightCalculator!.CalculateTargetElevations(geometry, heightMap, metersPerPixel);

                    // Apply junction/endpoint elevation harmonization
                    if (_junctionHarmonizer != null)
                    {
                        _junctionHarmonizer.HarmonizeElevations(geometry, heightMap, parameters, metersPerPixel);
                    }

                    // Export smoothed elevation debug image if enabled
                    if (parameters.ExportSmoothedElevationDebugImage)
                    {
                        try { ExportSmoothedElevationDebugImage(geometry, metersPerPixel, parameters); }
                        catch (Exception ex) { TerrainLogger.Warning($"Smoothed elevation debug export failed: {ex.Message}"); }
                    }
                }
                
                var distanceFieldBlender = (DistanceFieldTerrainBlender)_terrainBlender!;
                newHeightMap = distanceFieldBlender.BlendRoadWithTerrain(
                    heightMap,
                    geometry,
                    parameters,
                    metersPerPixel);

                // Apply post-processing smoothing if enabled (Spline approach only)
                if (parameters.EnablePostProcessingSmoothing)
                {
                    TerrainLogger.Info("Applying post-processing smoothing to eliminate staircase artifacts...");
                    distanceFieldBlender.ApplyPostProcessingSmoothing(
                        newHeightMap,
                        distanceFieldBlender.GetLastDistanceField(),
                        parameters,
                        metersPerPixel);
                }

                // Export heightmap with road outlines if enabled
                if (parameters.ExportSmoothedHeightmapWithOutlines)
                {
                    try { ExportSmoothedHeightmapWithRoadOutlines(newHeightMap, geometry, distanceFieldBlender.GetLastDistanceField(), metersPerPixel, parameters); }
                    catch (Exception ex) { TerrainLogger.Warning($"Smoothed heightmap with outlines export failed: {ex.Message}"); }
                }
            }
        }
        
        // 4. Calculate delta map and statistics
        var deltaMap = CalculateDeltaMap(heightMap, newHeightMap);
        var statistics = CalculateStatistics(heightMap, newHeightMap, parameters, metersPerPixel);
        
        TerrainLogger.Info("Road smoothing complete!");
        
        return new SmoothingResult(newHeightMap, deltaMap, statistics, geometry);
    }
    
    private void InitializeComponents(RoadSmoothingApproach approach)
    {
        if (approach == RoadSmoothingApproach.DirectMask)
        {
            TerrainLogger.Info("Using DIRECT road mask approach (robust, handles intersections)");
            _roadExtractor = new DirectRoadExtractor();
            _terrainBlender = new DirectTerrainBlender();
            _heightCalculator = null; // Not needed for direct approach
        }
        else // RoadSmoothingApproach.Spline
        {
            TerrainLogger.Info("Using OPTIMIZED SPLINE approach (fast, smooth, EDT-based)");
            _roadExtractor = new MedialAxisRoadExtractor();
            _heightCalculator = new OptimizedElevationSmoother(); // O(N) prefix-sum smoothing
            _terrainBlender = new DistanceFieldTerrainBlender(); // Global EDT-based blending
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
        TerrainLogger.Info($"Exported spline debug image: {filePath}");
    }

    private void ExportSmoothedElevationDebugImage(RoadGeometry geometry, float metersPerPixel, RoadSmoothingParameters parameters)
    {
        int width = geometry.Width;
        int height = geometry.Height;
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));

        var elevations = geometry.CrossSections.Select(cs => cs.TargetElevation).Where(e => e > 0).ToList();
        if (!elevations.Any())
        {
            TerrainLogger.Warning("No valid elevations to create smoothed debug image.");
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
            if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation <= -1000f) continue; // Skip uninitialized or invalid elevations

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
        TerrainLogger.Info($"Exported smoothed elevation debug image: {filePath}");
        TerrainLogger.Info($"  Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)");
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

    /// <summary>
    /// Exports the smoothed heightmap as a grayscale image with road outlines overlaid.
    /// Shows:
    /// - Smoothed heightmap as grayscale background
    /// - Thin cyan outline at road edges (± roadWidth/2)
    /// - Thin magenta outline at terrain blending edges (± roadWidth/2 + terrainAffectedRange)
    /// </summary>
    private void ExportSmoothedHeightmapWithRoadOutlines(
        float[,] smoothedHeightMap, 
        RoadGeometry geometry, 
        float[,] distanceField,
        float metersPerPixel, 
        RoadSmoothingParameters parameters)
    {
        int width = smoothedHeightMap.GetLength(1);
        int height = smoothedHeightMap.GetLength(0);
        
        TerrainLogger.Info($"Exporting smoothed heightmap with road outlines ({width}x{height})...");

        // Step 1: Find height range for grayscale normalization
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float h = smoothedHeightMap[y, x];
                if (h < minHeight) minHeight = h;
                if (h > maxHeight) maxHeight = h;
            }
        }
        float heightRange = maxHeight - minHeight;
        if (heightRange < 0.01f) heightRange = 1f; // Avoid division by zero

        // Step 2: Create grayscale heightmap image
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float normalizedHeight = (smoothedHeightMap[y, x] - minHeight) / heightRange;
                byte gray = (byte)(normalizedHeight * 255f);
                image[x, height - 1 - y] = new Rgba32(gray, gray, gray, 255); // Flip Y for visualization
            }
        }

        // Step 3: Draw road outlines (thin lines at road edges and blend zone edges)
        float roadHalfWidth = parameters.RoadWidthMeters / 2.0f;
        float blendZoneMaxDist = roadHalfWidth + parameters.TerrainAffectedRangeMeters;
        
        // Tolerance for edge detection (in meters) - makes lines 1-2 pixels wide
        float edgeTolerance = metersPerPixel * 0.75f;

        int roadEdgePixels = 0;
        int blendEdgePixels = 0;

        // Scan all pixels and check distance field
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dist = distanceField[y, x];

                // Road edge outline (cyan) - thin line at roadHalfWidth
                if (Math.Abs(dist - roadHalfWidth) < edgeTolerance)
                {
                    image[x, height - 1 - y] = new Rgba32(0, 255, 255, 255); // Cyan
                    roadEdgePixels++;
                }
                // Blend zone edge outline (magenta) - thin line at max blend distance
                else if (Math.Abs(dist - blendZoneMaxDist) < edgeTolerance)
                {
                    image[x, height - 1 - y] = new Rgba32(255, 0, 255, 255); // Magenta
                    blendEdgePixels++;
                }
            }
        }

        // Step 4: Save image
        var dir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, "smoothed_heightmap_with_road_outlines.png");
        image.SaveAsPng(filePath);
        
        TerrainLogger.Info($"Exported smoothed heightmap with outlines: {filePath}");
        TerrainLogger.Info($"  Height range: {minHeight:F2}m (black) to {maxHeight:F2}m (white)");
        TerrainLogger.Info($"  Road edge outline (cyan): {roadEdgePixels:N0} pixels at ±{roadHalfWidth:F1}m from centerline");
        TerrainLogger.Info($"  Blend zone edge outline (magenta): {blendEdgePixels:N0} pixels at ±{blendZoneMaxDist:F1}m from centerline");
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
