using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

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
        var splineParams = parameters.GetSplineParameters();
        if (splineParams.GlobalLevelingStrength > 0.5f && parameters.TerrainAffectedRangeMeters < 15.0f)
        {
            TerrainLogger.Warning($"High GlobalLevelingStrength ({splineParams.GlobalLevelingStrength:F2}) + small blend range ({parameters.TerrainAffectedRangeMeters}m)");
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
        
        RoadGeometry geometry;
        
        // Check if we have pre-built splines from OSM or other external source
        if (parameters.UsePreBuiltSplines)
        {
            TerrainLogger.Info($"Using {parameters.PreBuiltSplines!.Count} pre-built splines from external source (skipping skeleton extraction)...");
            geometry = BuildGeometryFromPreBuiltSplines(smoothingMask, parameters, metersPerPixel);
        }
        else
        {
            TerrainLogger.Info("Extracting road geometry from layer map...");
            geometry = _roadExtractor!.ExtractRoadGeometry(
                smoothingMask,
                parameters,
                metersPerPixel);
        }
        
        // Export spline masks and initial debug images (before elevation calculation)
        // Note: Smoothed elevation and heightmap with outlines are exported later after blending
        if (parameters.Approach == RoadSmoothingApproach.Spline && geometry.CrossSections.Count > 0)
        {
            try { RoadDebugExporter.ExportSplineMasks(geometry, metersPerPixel, parameters); }
            catch (Exception ex) { TerrainLogger.Warning($"Spline mask export failed: {ex.Message}"); }
            
            if (splineParams.ExportSplineDebugImage)
            {
                try { RoadDebugExporter.ExportSplineDebugImage(geometry, metersPerPixel, parameters, "spline_debug.png"); }
                catch (Exception ex) { TerrainLogger.Warning($"Spline debug export failed: {ex.Message}"); }
            }
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
                    if (splineParams.ExportSmoothedElevationDebugImage)
                    {
                        try { RoadDebugExporter.ExportSmoothedElevationDebugImage(geometry, metersPerPixel, parameters); }
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
                    try { RoadDebugExporter.ExportSmoothedHeightmapWithRoadOutlines(newHeightMap, geometry, distanceFieldBlender.GetLastDistanceField(), metersPerPixel, parameters); }
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
    
    /// <summary>
    /// Builds road geometry from pre-built splines (OSM data).
    /// This bypasses skeleton extraction when external vector data is available.
    /// </summary>
    private RoadGeometry BuildGeometryFromPreBuiltSplines(
        byte[,] roadMask,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var geometry = new RoadGeometry(roadMask, parameters);
        var splines = parameters.PreBuiltSplines!;
        
        var globalIndex = 0;
        var pathId = 0;
        
        foreach (var spline in splines)
        {
            // Sample the spline at regular intervals to generate cross-sections
            var samples = spline.SampleByDistance(parameters.CrossSectionIntervalMeters);
            var localIndex = 0;
            
            foreach (var sample in samples)
            {
                // Create cross-section at this position
                var crossSection = new CrossSection
                {
                    CenterPoint = sample.Position,
                    NormalDirection = sample.Normal,
                    TangentDirection = sample.Tangent,
                    WidthMeters = parameters.RoadWidthMeters,
                    Index = globalIndex,
                    PathId = pathId,
                    LocalIndex = localIndex,
                    IsExcluded = false
                };
                
                geometry.CrossSections.Add(crossSection);
                globalIndex++;
                localIndex++;
            }
            
            pathId++;
        }
        
        TerrainLogger.Info($"Generated {globalIndex} cross-sections from {splines.Count} pre-built splines");
        
        return geometry;
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
