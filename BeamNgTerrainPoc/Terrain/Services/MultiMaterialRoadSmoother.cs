using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Handles road smoothing across multiple material layers with cross-material junction harmonization.
/// 
/// This service implements a two-phase approach:
/// 1. Extract road geometries from ALL materials first
/// 2. Calculate target elevations for each material's roads
/// 3. Detect cross-material junctions (where different material roads meet)
/// 4. Harmonize elevations at cross-material junctions
/// 5. Apply terrain blending for all materials
/// 
/// This ensures smooth transitions where different road types (e.g., highway and local road) meet.
/// </summary>
public class MultiMaterialRoadSmoother
{
    private readonly RoadSmoothingService _singleMaterialSmoother;
    private readonly JunctionElevationHarmonizer _junctionHarmonizer;
    
    /// <summary>
    /// Represents extracted road data for a single material layer.
    /// </summary>
    public class MaterialRoadData
    {
        public required MaterialDefinition Material { get; init; }
        public required RoadGeometry Geometry { get; init; }
        public required byte[,] RoadLayer { get; init; }
        public required RoadSmoothingParameters Parameters { get; init; }
    }
    
    public MultiMaterialRoadSmoother()
    {
        _singleMaterialSmoother = new RoadSmoothingService();
        _junctionHarmonizer = new JunctionElevationHarmonizer();
    }
    
    /// <summary>
    /// Applies road smoothing across multiple materials with cross-material junction harmonization.
    /// </summary>
    /// <param name="heightMap">2D heightmap to modify</param>
    /// <param name="materials">List of materials (only those with RoadParameters will be processed)</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="size">Terrain size in pixels</param>
    /// <param name="enableCrossMaterialHarmonization">Global setting to enable cross-material junction harmonization</param>
    /// <returns>Final smoothing result with combined statistics</returns>
    public SmoothingResult? SmoothAllRoads(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization = true)
    {
        var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();
        
        if (roadMaterials.Count == 0)
        {
            TerrainLogger.Info("No road materials to process");
            return null;
        }
        
        TerrainLogger.Info($"=== MULTI-MATERIAL ROAD SMOOTHING ({roadMaterials.Count} materials) ===");
        
        // Check if cross-material harmonization should be applied:
        // 1. Global setting must be enabled
        // 2. Must have more than one road material
        // 3. At least one material must have junction harmonization enabled
        bool useCrossMaterial = enableCrossMaterialHarmonization && 
            roadMaterials.Count > 1 && 
            roadMaterials.Any(m => m.RoadParameters?.JunctionHarmonizationParameters?.EnableJunctionHarmonization == true);
        
        if (!useCrossMaterial)
        {
            if (!enableCrossMaterialHarmonization)
                TerrainLogger.Info("Cross-material harmonization disabled (global setting)");
            else if (roadMaterials.Count <= 1)
                TerrainLogger.Info("Single material - using standard processing");
            else
                TerrainLogger.Info("No materials have junction harmonization enabled");
            
            return SmoothSequentially(heightMap, roadMaterials, metersPerPixel, size);
        }
        
        TerrainLogger.Info("Using cross-material junction harmonization");
        return SmoothWithCrossMaterialHarmonization(heightMap, roadMaterials, metersPerPixel, size);
    }
    
    /// <summary>
    /// Original sequential processing (each material modifies heightmap in order).
    /// Used when cross-material harmonization is disabled.
    /// </summary>
    private SmoothingResult? SmoothSequentially(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size)
    {
        SmoothingResult? finalResult = null;
        
        foreach (var material in materials)
        {
            if (string.IsNullOrEmpty(material.LayerImagePath))
            {
                TerrainLogger.Warning($"Road material '{material.MaterialName}' has no layer image path");
                continue;
            }
            
            try
            {
                var roadLayer = LoadLayerImage(material.LayerImagePath, size);
                
                var result = _singleMaterialSmoother.SmoothRoadsInHeightmap(
                    heightMap,
                    roadLayer,
                    material.RoadParameters!,
                    metersPerPixel);
                
                heightMap = result.ModifiedHeightMap;
                finalResult = result;
                
                TerrainLogger.Info($"Applied road smoothing for material: {material.MaterialName}");
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"Error smoothing road '{material.MaterialName}': {ex.Message}");
            }
        }
        
        return finalResult;
    }
    
    /// <summary>
    /// Two-phase processing with cross-material junction harmonization.
    /// </summary>
    private SmoothingResult? SmoothWithCrossMaterialHarmonization(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size)
    {
        // Phase 1: Extract all road geometries and calculate elevations
        TerrainLogger.Info("Phase 1: Extracting road geometries from all materials...");
        var allMaterialData = new List<MaterialRoadData>();
        
        foreach (var material in materials)
        {
            if (string.IsNullOrEmpty(material.LayerImagePath))
            {
                TerrainLogger.Warning($"Road material '{material.MaterialName}' has no layer image path");
                continue;
            }
            
            try
            {
                var roadLayer = LoadLayerImage(material.LayerImagePath, size);
                var parameters = material.RoadParameters!;
                
                // Extract geometry only (don't apply terrain blending yet)
                var geometry = ExtractRoadGeometry(roadLayer, parameters, metersPerPixel);
                
                if (geometry.CrossSections.Count == 0)
                {
                    TerrainLogger.Warning($"No cross-sections extracted for material: {material.MaterialName}");
                    continue;
                }
                
                // Calculate target elevations
                CalculateTargetElevations(geometry, heightMap, parameters, metersPerPixel);
                
                // Apply within-material junction harmonization first
                if (parameters.JunctionHarmonizationParameters?.EnableJunctionHarmonization == true)
                {
                    _junctionHarmonizer.HarmonizeElevations(geometry, heightMap, parameters, metersPerPixel);
                }
                
                // Export spline masks and spline debug image (before blending)
                if (parameters.Approach == RoadSmoothingApproach.Spline && geometry.CrossSections.Count > 0)
                {
                    try { RoadDebugExporter.ExportSplineMasks(geometry, metersPerPixel, parameters); }
                    catch (Exception ex) { TerrainLogger.Warning($"Spline mask export failed for {material.MaterialName}: {ex.Message}"); }
                    
                    if (parameters.ExportSplineDebugImage)
                    {
                        try { RoadDebugExporter.ExportSplineDebugImage(geometry, metersPerPixel, parameters, "spline_debug.png"); }
                        catch (Exception ex) { TerrainLogger.Warning($"Spline debug export failed for {material.MaterialName}: {ex.Message}"); }
                    }
                    
                    // Export smoothed elevation debug image (after harmonization, before blending)
                    if (parameters.ExportSmoothedElevationDebugImage)
                    {
                        try { RoadDebugExporter.ExportSmoothedElevationDebugImage(geometry, metersPerPixel, parameters); }
                        catch (Exception ex) { TerrainLogger.Warning($"Smoothed elevation debug export failed for {material.MaterialName}: {ex.Message}"); }
                    }
                }
                
                allMaterialData.Add(new MaterialRoadData
                {
                    Material = material,
                    Geometry = geometry,
                    RoadLayer = roadLayer,
                    Parameters = parameters
                });
                
                TerrainLogger.Info($"  Extracted {geometry.CrossSections.Count} cross-sections for: {material.MaterialName}");
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"Error extracting geometry for '{material.MaterialName}': {ex.Message}");
            }
        }
        
        if (allMaterialData.Count == 0)
        {
            TerrainLogger.Warning("No road geometries extracted");
            return null;
        }
        
        // Phase 2: Detect and harmonize cross-material junctions
        if (allMaterialData.Count > 1)
        {
            TerrainLogger.Info("Phase 2: Harmonizing cross-material junctions...");
            HarmonizeCrossMaterialJunctions(allMaterialData, metersPerPixel);
        }
        
        // Phase 3: Apply terrain blending for all materials
        TerrainLogger.Info("Phase 3: Applying terrain blending...");
        SmoothingResult? finalResult = null;
        var combinedStats = new SmoothingStatistics();
        
        foreach (var data in allMaterialData)
        {
            try
            {
                var result = ApplyTerrainBlending(
                    heightMap,
                    data.Geometry,
                    data.Parameters,
                    metersPerPixel);
                
                heightMap = result.ModifiedHeightMap;
                finalResult = result;
                
                // Accumulate statistics
                combinedStats.PixelsModified += result.Statistics.PixelsModified;
                combinedStats.TotalCutVolume += result.Statistics.TotalCutVolume;
                combinedStats.TotalFillVolume += result.Statistics.TotalFillVolume;
                combinedStats.MaxRoadSlope = Math.Max(combinedStats.MaxRoadSlope, result.Statistics.MaxRoadSlope);
                combinedStats.MaxDiscontinuity = Math.Max(combinedStats.MaxDiscontinuity, result.Statistics.MaxDiscontinuity);
                
                TerrainLogger.Info($"  Applied blending for: {data.Material.MaterialName}");
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"Error blending '{data.Material.MaterialName}': {ex.Message}");
            }
        }
        
        if (finalResult != null)
        {
            // Return combined result with accumulated statistics
            return new SmoothingResult(
                finalResult.ModifiedHeightMap,
                finalResult.DeltaMap,
                combinedStats,
                finalResult.Geometry);
        }
        
        return null;
    }
    
    /// <summary>
    /// Detects and harmonizes junctions between roads from different materials.
    /// </summary>
    private void HarmonizeCrossMaterialJunctions(
        List<MaterialRoadData> allMaterialData,
        float metersPerPixel)
    {
        // Get harmonization parameters from the first material that has them
        var junctionParams = allMaterialData
            .Select(d => d.Parameters.JunctionHarmonizationParameters)
            .FirstOrDefault(p => p != null) ?? new JunctionHarmonizationParameters();
        
        float detectionRadius = junctionParams.JunctionDetectionRadiusMeters;
        float blendDistance = junctionParams.JunctionBlendDistanceMeters;
        
        TerrainLogger.Info($"  Cross-material junction detection radius: {detectionRadius}m");
        
        // Collect all endpoints from all materials
        var allEndpoints = new List<(CrossSection cs, MaterialRoadData data)>();
        
        foreach (var data in allMaterialData)
        {
            var pathGroups = data.Geometry.CrossSections
                .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
                .GroupBy(cs => cs.PathId)
                .ToList();
            
            foreach (var group in pathGroups)
            {
                var ordered = group.OrderBy(cs => cs.LocalIndex).ToList();
                if (ordered.Count >= 1)
                {
                    allEndpoints.Add((ordered[0], data));
                    if (ordered.Count > 1)
                    {
                        allEndpoints.Add((ordered[^1], data));
                    }
                }
            }
        }
        
        TerrainLogger.Info($"  Found {allEndpoints.Count} total endpoints across {allMaterialData.Count} materials");
        
        // Find cross-material junctions (endpoints from different materials that are close)
        var crossMaterialJunctions = new List<List<(CrossSection cs, MaterialRoadData data)>>();
        var assigned = new HashSet<int>();
        
        for (int i = 0; i < allEndpoints.Count; i++)
        {
            if (assigned.Contains(i)) continue;
            
            var (cs1, data1) = allEndpoints[i];
            var cluster = new List<(CrossSection cs, MaterialRoadData data)> { (cs1, data1) };
            assigned.Add(i);
            
            // Find other endpoints within radius
            for (int j = i + 1; j < allEndpoints.Count; j++)
            {
                if (assigned.Contains(j)) continue;
                
                var (cs2, data2) = allEndpoints[j];
                
                // Check if this is from a DIFFERENT material
                if (data2.Material == data1.Material) continue;
                
                // Check distance
                float dist = Vector2.Distance(cs1.CenterPoint, cs2.CenterPoint);
                if (dist <= detectionRadius)
                {
                    cluster.Add((cs2, data2));
                    assigned.Add(j);
                }
            }
            
            // Only consider it a cross-material junction if it has endpoints from multiple materials
            var uniqueMaterials = cluster.Select(c => c.data.Material).Distinct().Count();
            if (uniqueMaterials > 1)
            {
                crossMaterialJunctions.Add(cluster);
            }
        }
        
        TerrainLogger.Info($"  Detected {crossMaterialJunctions.Count} cross-material junctions");
        
        // Harmonize each cross-material junction
        int modifiedCount = 0;
        foreach (var junction in crossMaterialJunctions)
        {
            // Calculate harmonized elevation (weighted average)
            float totalWeight = 0f;
            float weightedSum = 0f;
            var junctionCenter = new Vector2(
                junction.Average(j => j.cs.CenterPoint.X),
                junction.Average(j => j.cs.CenterPoint.Y));
            
            foreach (var (cs, _) in junction)
            {
                float dist = Vector2.Distance(cs.CenterPoint, junctionCenter);
                float weight = 1.0f / (dist + 0.1f);
                totalWeight += weight;
                weightedSum += cs.TargetElevation * weight;
            }
            
            float harmonizedElevation = totalWeight > 0 ? weightedSum / totalWeight : junction.Average(j => j.cs.TargetElevation);
            
            // Apply blending along each contributing path
            foreach (var (endpointCs, data) in junction)
            {
                var pathSections = data.Geometry.CrossSections
                    .Where(cs => cs.PathId == endpointCs.PathId && !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
                    .OrderBy(cs => cs.LocalIndex)
                    .ToList();
                
                if (pathSections.Count == 0) continue;
                
                bool isStartOfPath = endpointCs.LocalIndex == pathSections[0].LocalIndex;
                
                // Calculate distances from endpoint
                var distances = new float[pathSections.Count];
                if (isStartOfPath)
                {
                    distances[0] = 0;
                    for (int i = 1; i < pathSections.Count; i++)
                    {
                        distances[i] = distances[i - 1] + 
                            Vector2.Distance(pathSections[i].CenterPoint, pathSections[i - 1].CenterPoint);
                    }
                }
                else
                {
                    distances[^1] = 0;
                    for (int i = pathSections.Count - 2; i >= 0; i--)
                    {
                        distances[i] = distances[i + 1] + 
                            Vector2.Distance(pathSections[i].CenterPoint, pathSections[i + 1].CenterPoint);
                    }
                }
                
                // Apply smooth blend
                for (int i = 0; i < pathSections.Count; i++)
                {
                    float dist = distances[i];
                    if (dist >= blendDistance) continue;
                    
                    var cs = pathSections[i];
                    float originalElevation = cs.TargetElevation;
                    
                    // Cosine blend: 0 at junction, 1 at blend distance
                    float t = dist / blendDistance;
                    float blend = 0.5f - 0.5f * MathF.Cos(MathF.PI * t);
                    
                    cs.TargetElevation = harmonizedElevation * (1.0f - blend) + originalElevation * blend;
                    
                    if (MathF.Abs(cs.TargetElevation - originalElevation) > 0.001f)
                        modifiedCount++;
                }
            }
            
            // Log junction details
            var materialNames = string.Join(" + ", junction.Select(j => j.data.Material.MaterialName).Distinct());
            TerrainLogger.Info($"    Junction [{materialNames}] at ({junctionCenter.X:F1}, {junctionCenter.Y:F1}) -> {harmonizedElevation:F2}m");
        }
        
        TerrainLogger.Info($"  Modified {modifiedCount} cross-sections for cross-material harmonization");
    }
    
    /// <summary>
    /// Extracts road geometry without applying terrain blending.
    /// </summary>
    private RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        IRoadExtractor extractor = parameters.Approach == RoadSmoothingApproach.Spline
            ? new MedialAxisRoadExtractor()
            : new DirectRoadExtractor();
        
        return extractor.ExtractRoadGeometry(roadLayer, parameters, metersPerPixel);
    }
    
    /// <summary>
    /// Calculates target elevations for road cross-sections.
    /// </summary>
    private void CalculateTargetElevations(
        RoadGeometry geometry,
        float[,] heightMap,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        if (parameters.Approach != RoadSmoothingApproach.Spline)
        {
            // DirectMask doesn't use pre-calculated elevations
            return;
        }
        
        IHeightCalculator calculator = new OptimizedElevationSmoother();
        calculator.CalculateTargetElevations(geometry, heightMap, metersPerPixel);
    }
    
    /// <summary>
    /// Applies terrain blending using pre-calculated elevations.
    /// </summary>
    private SmoothingResult ApplyTerrainBlending(
        float[,] heightMap,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        if (parameters.Approach == RoadSmoothingApproach.Spline)
        {
            var blender = new DistanceFieldTerrainBlender();
            var result = blender.BlendRoadWithTerrain(heightMap, geometry, parameters, metersPerPixel);
            
            // Apply post-processing if enabled
            if (parameters.EnablePostProcessingSmoothing)
            {
                blender.ApplyPostProcessingSmoothing(
                    result,
                    blender.GetLastDistanceField(),
                    parameters,
                    metersPerPixel);
            }
            
            // Export heightmap with road outlines if enabled
            if (parameters.ExportSmoothedHeightmapWithOutlines)
            {
                try { RoadDebugExporter.ExportSmoothedHeightmapWithRoadOutlines(result, geometry, blender.GetLastDistanceField(), metersPerPixel, parameters); }
                catch (Exception ex) { TerrainLogger.Warning($"Smoothed heightmap with outlines export failed: {ex.Message}"); }
            }
            
            // Calculate statistics
            var deltaMap = CalculateDeltaMap(heightMap, result);
            var stats = CalculateStatistics(heightMap, result, parameters, metersPerPixel);
            
            return new SmoothingResult(result, deltaMap, stats, geometry);
        }
        else
        {
            // DirectMask approach
            var blender = new DirectTerrainBlender();
            var result = blender.BlendRoadWithTerrain(heightMap, geometry, parameters, metersPerPixel);
            
            var deltaMap = CalculateDeltaMap(heightMap, result);
            var stats = CalculateStatistics(heightMap, result, parameters, metersPerPixel);
            
            return new SmoothingResult(result, deltaMap, stats, geometry);
        }
    }
    
    private byte[,] LoadLayerImage(string layerPath, int expectedSize)
    {
        using var image = Image.Load<L8>(layerPath);
        
        if (image.Width != expectedSize || image.Height != expectedSize)
            throw new InvalidOperationException(
                $"Layer image size ({image.Width}x{image.Height}) does not match terrain size ({expectedSize}x{expectedSize})");
        
        var layer = new byte[expectedSize, expectedSize];
        
        for (var y = 0; y < expectedSize; y++)
        for (var x = 0; x < expectedSize; x++)
        {
            var flippedY = expectedSize - 1 - y;
            layer[flippedY, x] = image[x, y].PackedValue;
        }
        
        return layer;
    }
    
    private float[,] CalculateDeltaMap(float[,] original, float[,] modified)
    {
        int h = original.GetLength(0);
        int w = original.GetLength(1);
        var delta = new float[h, w];
        
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                delta[y, x] = modified[y, x] - original[y, x];
        
        return delta;
    }
    
    private SmoothingStatistics CalculateStatistics(
        float[,] original,
        float[,] modified,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var stats = new SmoothingStatistics();
        int h = original.GetLength(0);
        int w = original.GetLength(1);
        float pixelArea = metersPerPixel * metersPerPixel;
        const float threshold = 0.001f;
        
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float delta = modified[y, x] - original[y, x];
                if (MathF.Abs(delta) > threshold)
                {
                    stats.PixelsModified++;
                    if (delta < 0)
                        stats.TotalCutVolume += MathF.Abs(delta) * pixelArea;
                    else
                        stats.TotalFillVolume += delta * pixelArea;
                }
            }
        }
        
        stats.MeetsAllConstraints = true;
        return stats;
    }
}
