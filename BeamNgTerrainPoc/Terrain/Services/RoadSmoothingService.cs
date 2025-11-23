using BeamNgTerrainPoc.Terrain.Algorithms;
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
        
        // 3. Process based on approach
        float[,] newHeightMap;
        
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
            }
            
            var splineBlender = (TerrainBlender)_terrainBlender!;
            newHeightMap = splineBlender.BlendRoadWithTerrain(
                heightMap,
                geometry,
                parameters,
                metersPerPixel);
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
    
    private float[,] CalculateDeltaMap(float[,] original, float[,] modified)
    {
        int height = original.GetLength(0);
        int width = original.GetLength(1);
        var delta = new float[height, width];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                delta[y, x] = modified[y, x] - original[y, x];
            }
        }
        
        return delta;
    }
    
    private SmoothingStatistics CalculateStatistics(
        float[,] original,
        float[,] modified,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var stats = new SmoothingStatistics();
        
        int height = original.GetLength(0);
        int width = original.GetLength(1);
        
        // Calculate volumes and pixel counts
        float cutVolume = 0;
        float fillVolume = 0;
        int modifiedPixels = 0;
        float maxDiscontinuity = 0;
        
        float pixelArea = metersPerPixel * metersPerPixel;
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float delta = modified[y, x] - original[y, x];
                
                if (MathF.Abs(delta) > 0.001f)
                {
                    modifiedPixels++;
                    
                    if (delta < 0)
                        cutVolume += MathF.Abs(delta) * pixelArea;
                    else
                        fillVolume += delta * pixelArea;
                }
                
                // Check discontinuities with neighbors
                if (x < width - 1)
                {
                    float discontinuity = MathF.Abs(modified[y, x + 1] - modified[y, x]);
                    maxDiscontinuity = MathF.Max(maxDiscontinuity, discontinuity);
                }
                if (y < height - 1)
                {
                    float discontinuity = MathF.Abs(modified[y + 1, x] - modified[y, x]);
                    maxDiscontinuity = MathF.Max(maxDiscontinuity, discontinuity);
                }
            }
        }
        
        stats.TotalCutVolume = cutVolume;
        stats.TotalFillVolume = fillVolume;
        stats.PixelsModified = modifiedPixels;
        stats.MaxDiscontinuity = maxDiscontinuity;
        
        // Estimate max road slope from discontinuities
        stats.MaxRoadSlope = MathF.Atan(maxDiscontinuity / metersPerPixel) * 180.0f / MathF.PI;
        stats.MaxSideSlope = parameters.SideMaxSlopeDegrees;
        stats.MaxTransverseSlope = 0;
        
        // Check constraints
        stats.MeetsAllConstraints = true;
        
        if (stats.MaxRoadSlope > parameters.RoadMaxSlopeDegrees + 0.1f)
        {
            stats.ConstraintViolations.Add($"Max road slope ({stats.MaxRoadSlope:F2}°) exceeds limit ({parameters.RoadMaxSlopeDegrees}°)");
            stats.MeetsAllConstraints = false;
        }
        
        return stats;
    }
}
