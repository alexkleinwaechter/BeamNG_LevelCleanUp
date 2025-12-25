using System.Diagnostics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Top-level orchestrator for unified road network smoothing.
/// 
/// This replaces the material-centric processing in MultiMaterialRoadSmoother with
/// a network-centric approach that:
/// 
/// 1. Builds a unified road network from all materials
/// 2. Calculates target elevations per-spline using each spline's parameters
/// 3. Harmonizes junctions across the entire network (including cross-material)
/// 4. Applies protected terrain blending in a single pass
/// 5. Paints material layers separately (using surface width, not elevation width)
/// 
/// Key benefits:
/// - Single EDT computation (faster than per-material)
/// - Road core pixels are protected from neighbor's blend zones
/// - Proper cross-material junction handling
/// - Per-spline parameter respect in unified processing
/// </summary>
public class UnifiedRoadSmoother
{
    private readonly UnifiedRoadNetworkBuilder _networkBuilder;
    private readonly NetworkJunctionDetector _junctionDetector;
    private readonly NetworkJunctionHarmonizer _junctionHarmonizer;
    private readonly UnifiedTerrainBlender _terrainBlender;
    private readonly MaterialPainter _materialPainter;
    private readonly IHeightCalculator _elevationCalculator;

    public UnifiedRoadSmoother()
    {
        _networkBuilder = new UnifiedRoadNetworkBuilder();
        _junctionDetector = new NetworkJunctionDetector();
        _junctionHarmonizer = new NetworkJunctionHarmonizer();
        _terrainBlender = new UnifiedTerrainBlender();
        _materialPainter = new MaterialPainter();
        _elevationCalculator = new OptimizedElevationSmoother();
    }

    /// <summary>
    /// Smooths all roads in the unified network.
    /// 
    /// This is the main entry point that orchestrates the entire pipeline:
    /// 1. Build unified network from all road materials
    /// 2. Calculate target elevations for each spline
    /// 3. Detect and harmonize junctions across the network
    /// 4. Apply terrain blending in a single pass
    /// 5. Paint material layers based on spline ownership
    /// </summary>
    /// <param name="heightMap">The original terrain heightmap.</param>
    /// <param name="materials">List of material definitions (only those with RoadParameters are processed).</param>
    /// <param name="metersPerPixel">Scale factor for converting meters to pixels.</param>
    /// <param name="size">Terrain size in pixels.</param>
    /// <param name="enableCrossMaterialHarmonization">Whether to harmonize junctions across materials.</param>
    /// <param name="globalJunctionDetectionRadius">Global junction detection radius in meters (used when material's UseGlobalSettings is true).</param>
    /// <param name="globalJunctionBlendDistance">Global junction blend distance in meters (used when material's UseGlobalSettings is true).</param>
    /// <returns>Result containing smoothed heightmap, material layers, and network data.</returns>
    public UnifiedSmoothingResult? SmoothAllRoads(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization = true,
        float globalJunctionDetectionRadius = 10.0f,
        float globalJunctionBlendDistance = 30.0f)
    {
        var perfLog = TerrainCreationLogger.Current;
        var totalSw = Stopwatch.StartNew();

        var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();

        if (roadMaterials.Count == 0)
        {
            TerrainLogger.Info("UnifiedRoadSmoother: No road materials to process");
            return null;
        }

        TerrainLogger.Info("=== UNIFIED ROAD SMOOTHING ===");
        TerrainLogger.Info($"  Materials: {roadMaterials.Count}");
        TerrainLogger.Info($"  Cross-material harmonization: {enableCrossMaterialHarmonization}");
        perfLog?.LogSection("UnifiedRoadSmoother");

        // Phase 1: Build unified road network from all materials
        perfLog?.LogSection("Phase 1: Network Building");
        TerrainLogger.Info("Phase 1: Building unified road network...");
        var sw = Stopwatch.StartNew();
        var network = _networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size);
        perfLog?.Timing($"BuildNetwork: {sw.Elapsed.TotalSeconds:F2}s");

        if (network.Splines.Count == 0)
        {
            TerrainLogger.Warning("No splines extracted from materials");
            return null;
        }

        TerrainLogger.Info($"  Network built: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections");

        // Phase 2: Calculate target elevations for each spline
        perfLog?.LogSection("Phase 2: Elevation Calculation");
        TerrainLogger.Info("Phase 2: Calculating target elevations...");
        sw.Restart();
        CalculateNetworkElevations(network, heightMap, metersPerPixel);
        perfLog?.Timing($"CalculateNetworkElevations: {sw.Elapsed.TotalSeconds:F2}s");

        // Phase 3: Detect and harmonize junctions
        // This handles both within-material junctions (single material) and cross-material junctions (multiple materials)
        if (ShouldHarmonize(roadMaterials))
        {
            perfLog?.LogSection("Phase 3: Junction Harmonization");
            TerrainLogger.Info("Phase 3: Detecting and harmonizing junctions...");
            TerrainLogger.Info($"  Cross-material harmonization: {enableCrossMaterialHarmonization && roadMaterials.Count > 1}");
            sw.Restart();

            // Use global params, but harmonizer will respect per-material UseGlobalSettings
            var harmonizationResult = _junctionHarmonizer.HarmonizeNetwork(
                network,
                heightMap,
                metersPerPixel,
                globalJunctionDetectionRadius,
                globalJunctionBlendDistance);

            perfLog?.Timing($"HarmonizeNetwork: {sw.Elapsed.TotalSeconds:F2}s, " +
                           $"modified {harmonizationResult.ModifiedCrossSections} cross-sections");

            // Export junction debug image if requested
            ExportJunctionDebugImageIfRequested(network, harmonizationResult, heightMap, metersPerPixel, roadMaterials);
        }
        else
        {
            TerrainLogger.Info("Phase 3: Junction harmonization skipped (no materials have it enabled)");
        }

        // Phase 4: Apply terrain blending (single pass)
        perfLog?.LogSection("Phase 4: Terrain Blending");
        TerrainLogger.Info("Phase 4: Applying protected terrain blending...");
        sw.Restart();
        var smoothedHeightMap = _terrainBlender.BlendNetworkWithTerrain(heightMap, network, metersPerPixel);
        perfLog?.Timing($"BlendNetworkWithTerrain: {sw.Elapsed.TotalSeconds:F2}s");

        // Apply post-processing smoothing if enabled
        if (roadMaterials.Any(m => m.RoadParameters?.EnablePostProcessingSmoothing == true))
        {
            sw.Restart();
            _terrainBlender.ApplyPostProcessingSmoothing(smoothedHeightMap, network, metersPerPixel);
            perfLog?.Timing($"PostProcessingSmoothing: {sw.Elapsed.TotalSeconds:F2}s");
        }

        // Phase 5: Paint material layers
        perfLog?.LogSection("Phase 5: Material Painting");
        TerrainLogger.Info("Phase 5: Painting material layers...");
        sw.Restart();
        var materialLayers = _materialPainter.PaintMaterials(network, size, size, metersPerPixel);
        perfLog?.Timing($"PaintMaterials: {sw.Elapsed.TotalSeconds:F2}s");

        // Calculate statistics
        var statistics = CalculateStatistics(heightMap, smoothedHeightMap, metersPerPixel);
        var deltaMap = CalculateDeltaMap(heightMap, smoothedHeightMap);

        // Export debug images if requested
        ExportDebugImagesIfRequested(network, smoothedHeightMap, heightMap, metersPerPixel, roadMaterials);

        totalSw.Stop();
        perfLog?.Timing($"=== UnifiedRoadSmoother TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s ===");
        perfLog?.LogMemoryUsage("After unified road smoothing");

        TerrainLogger.Info($"=== UNIFIED SMOOTHING COMPLETE ({totalSw.Elapsed.TotalSeconds:F2}s) ===");

        // Build result
        return new UnifiedSmoothingResult
        {
            ModifiedHeightMap = smoothedHeightMap,
            MaterialLayers = materialLayers,
            Network = network,
            Statistics = statistics,
            DeltaMap = deltaMap
        };
    }

    /// <summary>
    /// Determines if junction harmonization should be performed.
    /// </summary>
    private bool ShouldHarmonize(List<MaterialDefinition> roadMaterials)
    {
        // At least one material must have harmonization enabled
        return roadMaterials.Any(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.EnableJunctionHarmonization == true);
    }

    /// <summary>
    /// Calculates target elevations for all cross-sections in the network.
    /// Each spline uses its own parameters for elevation calculation.
    /// </summary>
    private void CalculateNetworkElevations(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel)
    {
        int totalCalculated = 0;

        // Group cross-sections by spline for efficient processing
        var crossSectionsBySpline = network.CrossSections
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        foreach (var spline in network.Splines)
        {
            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
                continue;

            var parameters = spline.Parameters;

            // Create a temporary RoadGeometry for compatibility with existing elevation calculator
            var tempGeometry = new RoadGeometry(new byte[1, 1], parameters);
            tempGeometry.CrossSections.Clear();

            // Convert UnifiedCrossSections to standard CrossSections
            foreach (var ucs in crossSections)
            {
                tempGeometry.CrossSections.Add(ucs.ToCrossSection());
            }

            // Sample raw terrain elevations BEFORE smoothing for OriginalTerrainElevation
            int mapHeight = heightMap.GetLength(0);
            int mapWidth = heightMap.GetLength(1);
            for (int i = 0; i < crossSections.Count; i++)
            {
                int px = (int)(crossSections[i].CenterPoint.X / metersPerPixel);
                int py = (int)(crossSections[i].CenterPoint.Y / metersPerPixel);
                px = Math.Clamp(px, 0, mapWidth - 1);
                py = Math.Clamp(py, 0, mapHeight - 1);
                crossSections[i].OriginalTerrainElevation = heightMap[py, px];
            }

            // Calculate elevations using existing calculator
            _elevationCalculator.CalculateTargetElevations(tempGeometry, heightMap, metersPerPixel);

            // Copy calculated (smoothed) elevations back to UnifiedCrossSections
            for (int i = 0; i < crossSections.Count && i < tempGeometry.CrossSections.Count; i++)
            {
                crossSections[i].TargetElevation = tempGeometry.CrossSections[i].TargetElevation;
                totalCalculated++;
            }
        }

        TerrainLogger.Info($"  Calculated elevations for {totalCalculated} cross-sections");
    }

    /// <summary>
    /// Calculates delta map (modified - original).
    /// </summary>
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

    /// <summary>
    /// Calculates smoothing statistics.
    /// </summary>
    private SmoothingStatistics CalculateStatistics(
        float[,] original,
        float[,] modified,
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

    /// <summary>
    /// Exports a single unified junction debug image if ANY material requests it.
    /// The debug image shows all junctions across all materials in the unified network,
    /// which is the key benefit of cross-material junction detection.
    /// The image is exported to the main debug folder (parent of material-specific folders).
    /// </summary>
    private void ExportJunctionDebugImageIfRequested(
        UnifiedRoadNetwork network,
        HarmonizationResult harmonizationResult,
        float[,] heightMap,
        float metersPerPixel,
        List<MaterialDefinition> roadMaterials)
    {
        // Check if ANY material has ExportJunctionDebugImage enabled
        var materialWithJunctionDebug = roadMaterials.FirstOrDefault(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.ExportJunctionDebugImage == true);

        if (materialWithJunctionDebug == null)
            return;

        try
        {
            // Get the debug output directory from the first material that requested it
            var materialDebugDir = materialWithJunctionDebug.RoadParameters!.DebugOutputDirectory ?? ".";
            
            // Go up one level to the main debug folder (MT_TerrainGeneration)
            // Material debug dirs are typically: MT_TerrainGeneration/{MaterialName}
            // We want to output to: MT_TerrainGeneration/
            var mainDebugDir = Path.GetDirectoryName(materialDebugDir);
            if (string.IsNullOrEmpty(mainDebugDir))
                mainDebugDir = materialDebugDir;
            
            var outputPath = Path.Combine(mainDebugDir, "unified_junction_harmonization_debug.png");

            int imageWidth = heightMap.GetLength(1);
            int imageHeight = heightMap.GetLength(0);

            _junctionHarmonizer.ExportJunctionDebugImage(
                network,
                harmonizationResult.PreHarmonizationElevations,
                imageWidth,
                imageHeight,
                metersPerPixel,
                outputPath);

            TerrainLogger.Info($"  Exported unified junction debug image: {outputPath}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to export unified junction debug image: {ex.Message}");
        }
    }

    /// <summary>
    /// Exports debug images if requested by any material.
    /// </summary>
    private void ExportDebugImagesIfRequested(
        UnifiedRoadNetwork network,
        float[,] smoothedHeightMap,
        float[,] originalHeightMap,
        float metersPerPixel,
        List<MaterialDefinition> roadMaterials)
    {
        foreach (var material in roadMaterials)
        {
            var parameters = material.RoadParameters;
            if (parameters == null)
                continue;

            var outputDir = parameters.DebugOutputDirectory ?? ".";

            // Export smoothed heightmap with outlines if requested
            if (parameters.ExportSmoothedHeightmapWithOutlines)
            {
                try
                {
                    var outputPath = Path.Combine(outputDir, "unified_smoothed_heightmap_with_outlines.png");
                    ExportSmoothedHeightmapWithOutlines(
                        smoothedHeightMap,
                        network,
                        _terrainBlender.GetLastDistanceField(),
                        metersPerPixel,
                        outputPath);
                }
                catch (Exception ex)
                {
                    TerrainLogger.Warning($"Failed to export smoothed heightmap: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Exports heightmap with road outlines overlaid.
    /// </summary>
    private void ExportSmoothedHeightmapWithOutlines(
        float[,] heightMap,
        UnifiedRoadNetwork network,
        float[,] distanceField,
        float metersPerPixel,
        string outputPath)
    {
        int height = heightMap.GetLength(0);
        int width = heightMap.GetLength(1);

        // Find min/max elevations
        float minElev = float.MaxValue;
        float maxElev = float.MinValue;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float h = heightMap[y, x];
                if (h < minElev) minElev = h;
                if (h > maxElev) maxElev = h;
            }
        }

        float elevRange = maxElev - minElev;
        if (elevRange < 0.001f) elevRange = 1.0f;

        using var image = new Image<Rgba32>(width, height);

        // Get max road width and blend range for outline calculation
        float maxHalfWidth = network.Splines.Max(s => s.Parameters.RoadWidthMeters) / 2.0f;
        float maxBlendRange = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);

        // Draw heightmap with outlines
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int flippedY = height - 1 - y;
                float h = heightMap[y, x];
                float d = distanceField[y, x];

                // Normalize elevation to 0-1
                float normalizedH = (h - minElev) / elevRange;
                byte gray = (byte)(normalizedH * 255);

                Rgba32 color;

                // Check if on road edge outline
                if (MathF.Abs(d - maxHalfWidth) < metersPerPixel * 1.5f)
                {
                    // Cyan outline at road edge
                    color = new Rgba32(0, 255, 255, 255);
                }
                else if (MathF.Abs(d - (maxHalfWidth + maxBlendRange)) < metersPerPixel * 1.5f)
                {
                    // Magenta outline at blend zone edge
                    color = new Rgba32(255, 0, 255, 255);
                }
                else
                {
                    // Grayscale heightmap
                    color = new Rgba32(gray, gray, gray, 255);
                }

                image[x, flippedY] = color;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        image.SaveAsPng(outputPath);

        TerrainLogger.Info($"  Exported smoothed heightmap with outlines: {outputPath}");
    }

    /// <summary>
    /// Converts a UnifiedSmoothingResult to a standard SmoothingResult for backward compatibility.
    /// </summary>
    /// <param name="unifiedResult">The unified result.</param>
    /// <param name="originalRoadMask">Original road mask for geometry creation.</param>
    /// <param name="parameters">Road smoothing parameters.</param>
    /// <returns>A SmoothingResult compatible with existing code.</returns>
    public static SmoothingResult? ToSmoothingResult(
        UnifiedSmoothingResult? unifiedResult,
        byte[,] originalRoadMask,
        RoadSmoothingParameters parameters)
    {
        if (unifiedResult == null)
            return null;

        return unifiedResult.ToSmoothingResult(originalRoadMask, parameters);
    }
}
