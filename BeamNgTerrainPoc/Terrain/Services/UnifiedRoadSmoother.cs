using System.Diagnostics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     Top-level orchestrator for unified road network smoothing.
///     This replaces the material-centric processing in MultiMaterialRoadSmoother with
///     a network-centric approach that:
///     1. Builds a unified road network from all materials
///     2. Calculates target elevations per-spline using each spline's parameters
///     3. Harmonizes junctions across the entire network (including cross-material)
///     4. Applies protected terrain blending in a single pass
///     5. Paints material layers separately (using surface width, not elevation width)
///     Key benefits:
///     - Single EDT computation (faster than per-material)
///     - Road core pixels are protected from neighbor's blend zones
///     - Proper cross-material junction handling
///     - Per-spline parameter respect in unified processing
/// </summary>
public class UnifiedRoadSmoother
{
    private readonly BankingOrchestrator _bankingOrchestrator;
    private readonly IHeightCalculator _elevationCalculator;
    private readonly NetworkJunctionDetector _junctionDetector;
    private readonly NetworkJunctionHarmonizer _junctionHarmonizer;
    private readonly MaterialPainter _materialPainter;
    private readonly UnifiedRoadNetworkBuilder _networkBuilder;
    private readonly UnifiedTerrainBlender _terrainBlender;

    public UnifiedRoadSmoother()
    {
        _networkBuilder = new UnifiedRoadNetworkBuilder();
        _junctionDetector = new NetworkJunctionDetector();
        _junctionHarmonizer = new NetworkJunctionHarmonizer();
        _bankingOrchestrator = new BankingOrchestrator();
        _terrainBlender = new UnifiedTerrainBlender();
        _materialPainter = new MaterialPainter();
        _elevationCalculator = new OptimizedElevationSmoother();
    }

    /// <summary>
    ///     Smooths all roads in the unified network.
    ///     This is the main entry point that orchestrates the entire pipeline:
    ///     1. Build unified network from all road materials
    ///     2. Calculate target elevations for each spline
    ///     3. Detect and harmonize junctions across the network
    ///     4. Apply terrain blending in a single pass
    ///     5. Paint material layers based on spline ownership
    /// </summary>
    /// <param name="heightMap">The original terrain heightmap.</param>
    /// <param name="materials">List of material definitions (only those with RoadParameters are processed).</param>
    /// <param name="metersPerPixel">Scale factor for converting meters to pixels.</param>
    /// <param name="size">Terrain size in pixels.</param>
    /// <param name="enableCrossMaterialHarmonization">Whether to harmonize junctions across materials.</param>
    /// <param name="globalJunctionDetectionRadius">
    ///     Global junction detection radius in meters (used when material's
    ///     UseGlobalSettings is true).
    /// </param>
    /// <param name="globalJunctionBlendDistance">
    ///     Global junction blend distance in meters (used when material's
    ///     UseGlobalSettings is true).
    /// </param>
    /// <param name="flipMaterialProcessingOrder">
    ///     When true, materials at top of list (index 0) get higher priority for road
    ///     smoothing.
    /// </param>
    /// <returns>Result containing smoothed heightmap, material layers, and network data.</returns>
    public UnifiedSmoothingResult? SmoothAllRoads(
        float[,] heightMap,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size,
        bool enableCrossMaterialHarmonization = true,
        float globalJunctionDetectionRadius = 10.0f,
        float globalJunctionBlendDistance = 30.0f,
        bool flipMaterialProcessingOrder = true)
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
        var network =
            _networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size, flipMaterialProcessingOrder);
        perfLog?.Timing($"BuildNetwork: {sw.Elapsed.TotalSeconds:F2}s");

        if (network.Splines.Count == 0)
        {
            TerrainLogger.Warning("No splines extracted from materials");
            return null;
        }

        TerrainLogger.Info(
            $"  Network built: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections");

        // Phase 2: Calculate target elevations for each spline
        perfLog?.LogSection("Phase 2: Elevation Calculation");
        TerrainLogger.Info("Phase 2: Calculating target elevations...");
        sw.Restart();
        CalculateNetworkElevations(network, heightMap, metersPerPixel);
        perfLog?.Timing($"CalculateNetworkElevations: {sw.Elapsed.TotalSeconds:F2}s");

        // Phase 2.5: Pre-calculate banking (bank angles and edge elevations)
        // This must happen BEFORE junction harmonization so that the harmonizer can
        // account for banked road surfaces when calculating connection point elevations.
        // Without this, secondary roads connecting to banked primary roads would get
        // the wrong elevation (centerline instead of edge).
        var bankingApplied = false;
        if (HasAnyBankingEnabled(roadMaterials))
        {
            perfLog?.LogSection("Phase 2.5: Banking Pre-calculation");
            TerrainLogger.Info("Phase 2.5: Pre-calculating road banking (for junction awareness)...");
            sw.Restart();
            bankingApplied = _bankingOrchestrator.ApplyBankingPreCalculation(network, globalJunctionBlendDistance);
            perfLog?.Timing($"ApplyBankingPreCalculation: {sw.Elapsed.TotalSeconds:F2}s");
        }

        // Phase 3: Detect and harmonize junctions
        // This handles both within-material junctions (single material) and cross-material junctions (multiple materials)
        // IMPORTANT: Now banking-aware - uses edge elevations for banked roads when calculating connection points
        if (ShouldHarmonize(roadMaterials))
        {
            perfLog?.LogSection("Phase 3: Junction Harmonization");
            TerrainLogger.Info("Phase 3: Detecting and harmonizing junctions...");
            TerrainLogger.Info(
                $"  Cross-material harmonization: {enableCrossMaterialHarmonization && roadMaterials.Count > 1}");
            TerrainLogger.Info($"  Banking-aware: {bankingApplied}");
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

        // Phase 3.5: Finalize banking (adapt secondary roads to banked junctions)
        // Now that junction harmonization has run, we need to:
        // 1. Recalculate edge elevations based on harmonized TargetElevation
        // 2. Adapt secondary road elevations to smoothly meet banked primary roads
        if (bankingApplied)
        {
            perfLog?.LogSection("Phase 3.5: Banking Finalization");
            TerrainLogger.Info("Phase 3.5: Finalizing road banking (adapting to junctions)...");
            sw.Restart();
            _bankingOrchestrator.FinalizeBankingAfterHarmonization(network, globalJunctionBlendDistance);
            perfLog?.Timing($"FinalizeBankingAfterHarmonization: {sw.Elapsed.TotalSeconds:F2}s");
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
    ///     Determines if junction harmonization should be performed.
    /// </summary>
    private bool ShouldHarmonize(List<MaterialDefinition> roadMaterials)
    {
        // At least one material must have harmonization enabled
        return roadMaterials.Any(m =>
            m.RoadParameters?.JunctionHarmonizationParameters?.EnableJunctionHarmonization == true);
    }

    /// <summary>
    ///     Determines if any material has road banking (superelevation) enabled.
    /// </summary>
    private static bool HasAnyBankingEnabled(List<MaterialDefinition> roadMaterials)
    {
        return roadMaterials.Any(m =>
            m.RoadParameters?.GetSplineParameters()?.Banking?.EnableAutoBanking == true);
    }

    /// <summary>
    ///     Calculates target elevations for all cross-sections in the network.
    ///     Each spline uses its own parameters for elevation calculation.
    /// </summary>
    private void CalculateNetworkElevations(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel)
    {
        var totalCalculated = 0;

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
            foreach (var ucs in crossSections) tempGeometry.CrossSections.Add(ucs.ToCrossSection());

            // Sample raw terrain elevations BEFORE smoothing for OriginalTerrainElevation
            var mapHeight = heightMap.GetLength(0);
            var mapWidth = heightMap.GetLength(1);
            for (var i = 0; i < crossSections.Count; i++)
            {
                var px = (int)(crossSections[i].CenterPoint.X / metersPerPixel);
                var py = (int)(crossSections[i].CenterPoint.Y / metersPerPixel);
                px = Math.Clamp(px, 0, mapWidth - 1);
                py = Math.Clamp(py, 0, mapHeight - 1);
                crossSections[i].OriginalTerrainElevation = heightMap[py, px];
            }

            // Calculate elevations using existing calculator
            _elevationCalculator.CalculateTargetElevations(tempGeometry, heightMap, metersPerPixel);

            // Copy calculated (smoothed) elevations back to UnifiedCrossSections
            for (var i = 0; i < crossSections.Count && i < tempGeometry.CrossSections.Count; i++)
            {
                crossSections[i].TargetElevation = tempGeometry.CrossSections[i].TargetElevation;
                totalCalculated++;
            }
        }

        TerrainCreationLogger.Current?.Detail($"Calculated elevations for {totalCalculated} cross-sections");
    }

    /// <summary>
    ///     Calculates delta map (modified - original).
    /// </summary>
    private float[,] CalculateDeltaMap(float[,] original, float[,] modified)
    {
        var h = original.GetLength(0);
        var w = original.GetLength(1);
        var delta = new float[h, w];

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            delta[y, x] = modified[y, x] - original[y, x];

        return delta;
    }

    /// <summary>
    ///     Calculates smoothing statistics.
    /// </summary>
    private SmoothingStatistics CalculateStatistics(
        float[,] original,
        float[,] modified,
        float metersPerPixel)
    {
        var stats = new SmoothingStatistics();
        var h = original.GetLength(0);
        var w = original.GetLength(1);
        var pixelArea = metersPerPixel * metersPerPixel;
        const float threshold = 0.001f;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var delta = modified[y, x] - original[y, x];
            if (MathF.Abs(delta) > threshold)
            {
                stats.PixelsModified++;
                if (delta < 0)
                    stats.TotalCutVolume += MathF.Abs(delta) * pixelArea;
                else
                    stats.TotalFillVolume += delta * pixelArea;
            }
        }

        stats.MeetsAllConstraints = true;
        return stats;
    }

    /// <summary>
    ///     Exports a single unified junction debug image if ANY material requests it.
    ///     The debug image shows all junctions across all materials in the unified network,
    ///     which is the key benefit of cross-material junction detection.
    ///     The image is exported to the main debug folder (parent of material-specific folders).
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

            var imageWidth = heightMap.GetLength(1);
            var imageHeight = heightMap.GetLength(0);

            _junctionHarmonizer.ExportJunctionDebugImage(
                network,
                harmonizationResult.PreHarmonizationElevations,
                imageWidth,
                imageHeight,
                metersPerPixel,
                outputPath);

            TerrainCreationLogger.Current?.Detail($"Exported unified junction debug image: {outputPath}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to export unified junction debug image: {ex.Message}");
        }
    }

    /// <summary>
    ///     Exports debug images if requested by any material.
    ///     Also exports unified master splines JSON with all materials' splines combined.
    ///     Per-material debug images (spline_debug, elevation_debug) are exported to material-specific folders.
    /// </summary>
    private void ExportDebugImagesIfRequested(
        UnifiedRoadNetwork network,
        float[,] smoothedHeightMap,
        float[,] originalHeightMap,
        float metersPerPixel,
        List<MaterialDefinition> roadMaterials)
    {
        // Find the main debug output directory (parent of material-specific folders)
        string? mainDebugDir = null;
        float terrainBaseHeight = 0;
        var nodeDistanceMeters = 15.0f;
        var terrainSize = smoothedHeightMap.GetLength(0);

        foreach (var material in roadMaterials)
        {
            var parameters = material.RoadParameters;
            if (parameters == null)
                continue;

            var outputDir = parameters.DebugOutputDirectory ?? ".";

            // Get the parent directory (MT_TerrainGeneration)
            var parentDir = Path.GetDirectoryName(outputDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                mainDebugDir = parentDir;
                terrainBaseHeight = parameters.TerrainBaseHeight;
                nodeDistanceMeters = parameters.MasterSplineNodeDistanceMeters;
            }

            // Export per-material debug images
            ExportPerMaterialDebugImages(
                network,
                material,
                parameters,
                smoothedHeightMap,
                metersPerPixel,
                terrainSize);
        }

        // Export unified smoothed heightmap with outlines to main folder
        var firstMaterial =
            roadMaterials.FirstOrDefault(m => m.RoadParameters?.ExportSmoothedHeightmapWithOutlines == true);
        if (firstMaterial != null && !string.IsNullOrEmpty(mainDebugDir))
            try
            {
                var heightmapPath = Path.Combine(mainDebugDir, "unified_smoothed_heightmap_with_outlines.png");
                ExportSmoothedHeightmapWithOutlines(
                    smoothedHeightMap,
                    network,
                    _terrainBlender.GetLastDistanceField(),
                    metersPerPixel,
                    heightmapPath);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export smoothed heightmap: {ex.Message}");
            }

        // Export unified master splines JSON with all materials' splines
        // This goes to the main debug folder (MT_TerrainGeneration), not material-specific subfolder
        if (!string.IsNullOrEmpty(mainDebugDir) && network.Splines.Count > 0)
            try
            {
                MasterSplineExporter.ExportFromUnifiedNetwork(
                    network,
                    smoothedHeightMap,
                    metersPerPixel,
                    terrainSize,
                    terrainBaseHeight,
                    mainDebugDir,
                    nodeDistanceMeters);
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export unified master splines: {ex.Message}");
            }
    }

    /// <summary>
    ///     Exports per-material debug images (spline_debug.png, spline_smoothed_elevation_debug.png).
    ///     These are exported to each material's debug folder.
    /// </summary>
    private void ExportPerMaterialDebugImages(
        UnifiedRoadNetwork network,
        MaterialDefinition material,
        RoadSmoothingParameters parameters,
        float[,] smoothedHeightMap,
        float metersPerPixel,
        int terrainSize)
    {
        var splineParams = parameters.GetSplineParameters();
        var outputDir = parameters.DebugOutputDirectory ?? ".";
        Directory.CreateDirectory(outputDir);

        // Get splines for this material
        var materialSplines = network.Splines.Where(s => s.MaterialName == material.MaterialName).ToList();
        if (materialSplines.Count == 0)
            return;

        // Export spline debug image if requested
        if (splineParams.ExportSplineDebugImage)
            try
            {
                ExportMaterialSplineDebugImage(
                    materialSplines,
                    network,
                    terrainSize,
                    metersPerPixel,
                    Path.Combine(outputDir, "spline_debug.png"));
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export spline debug for {material.MaterialName}: {ex.Message}");
            }

        // Export smoothed elevation debug image if requested
        if (splineParams.ExportSmoothedElevationDebugImage)
            try
            {
                ExportMaterialElevationDebugImage(
                    materialSplines,
                    network,
                    parameters,
                    terrainSize,
                    metersPerPixel,
                    Path.Combine(outputDir, "spline_smoothed_elevation_debug.png"));
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to export elevation debug for {material.MaterialName}: {ex.Message}");
            }
    }

    /// <summary>
    ///     Exports a debug image showing spline centerlines for a specific material.
    /// </summary>
    private void ExportMaterialSplineDebugImage(
        List<ParameterizedRoadSpline> materialSplines,
        UnifiedRoadNetwork network,
        int terrainSize,
        float metersPerPixel,
        string outputPath)
    {
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(0, 0, 0, 255));

        var sampleInterval = 0.5f; // Sample interval for drawing

        foreach (var paramSpline in materialSplines)
        {
            var spline = paramSpline.Spline;
            if (spline == null || spline.TotalLength < 1f) continue;

            // Draw original control points in cyan
            foreach (var cp in spline.ControlPoints)
            {
                var cpx = (int)(cp.X / metersPerPixel);
                var cpy = (int)(cp.Y / metersPerPixel);
                if (cpx >= 1 && cpx < terrainSize - 1 && cpy >= 1 && cpy < terrainSize - 1)
                    for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        image[cpx + dx, terrainSize - 1 - (cpy + dy)] = new Rgba32(0, 255, 255, 255);
            }

            // Draw interpolated spline centerline in yellow
            for (float d = 0; d <= spline.TotalLength; d += sampleInterval)
            {
                var p = spline.GetPointAtDistance(d);
                var px = (int)(p.X / metersPerPixel);
                var py = (int)(p.Y / metersPerPixel);
                if (px >= 0 && px < terrainSize && py >= 0 && py < terrainSize)
                    image[px, terrainSize - 1 - py] = new Rgba32(255, 255, 0, 255);
            }

            // Draw cross-section widths in green (every few cross-sections)
            var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId).ToList();
            var step = Math.Max(1, crossSections.Count / 20); // ~20 width indicators per spline
            for (var i = 0; i < crossSections.Count; i += step)
            {
                var cs = crossSections[i];
                var halfWidth = paramSpline.Parameters.RoadWidthMeters / 2.0f;
                var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
                var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

                var lx = (int)(left.X / metersPerPixel);
                var ly = (int)(left.Y / metersPerPixel);
                var rx = (int)(right.X / metersPerPixel);
                var ry = (int)(right.Y / metersPerPixel);

                DrawLineOnImage(image, lx, ly, rx, ry, new Rgba32(0, 255, 0, 255), terrainSize);
            }
        }

        image.SaveAsPng(outputPath);
        TerrainCreationLogger.Current?.Detail($"Exported spline debug image: {outputPath}");
    }

    /// <summary>
    ///     Exports a debug image showing elevation-coded road segments for a specific material.
    /// </summary>
    private void ExportMaterialElevationDebugImage(
        List<ParameterizedRoadSpline> materialSplines,
        UnifiedRoadNetwork network,
        RoadSmoothingParameters parameters,
        int terrainSize,
        float metersPerPixel,
        string outputPath)
    {
        using var image = new Image<Rgba32>(terrainSize, terrainSize, new Rgba32(0, 0, 0, 255));

        // Collect all elevations for this material to find range
        var elevations = new List<float>();
        foreach (var paramSpline in materialSplines)
        {
            var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId);
            elevations.AddRange(crossSections
                .Where(cs => !float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
                .Select(cs => cs.TargetElevation));
        }

        if (elevations.Count == 0)
        {
            TerrainLogger.Warning("No valid elevations for elevation debug image");
            return;
        }

        var minElev = elevations.Min();
        var maxElev = elevations.Max();
        var range = maxElev - minElev;
        if (range < 0.01f) range = 1f;

        // Draw each cross-section color-coded by elevation
        foreach (var paramSpline in materialSplines)
        {
            var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId).ToList();
            var halfWidth = paramSpline.Parameters.RoadWidthMeters / 2.0f;

            foreach (var cs in crossSections)
            {
                if (float.IsNaN(cs.TargetElevation) || cs.TargetElevation <= -1000f) continue;

                var normalizedElevation = (cs.TargetElevation - minElev) / range;
                var color = GetColorForElevation(normalizedElevation);

                var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
                var right = cs.CenterPoint + cs.NormalDirection * halfWidth;
                var lx = (int)(left.X / metersPerPixel);
                var ly = (int)(left.Y / metersPerPixel);
                var rx = (int)(right.X / metersPerPixel);
                var ry = (int)(right.Y / metersPerPixel);

                DrawLineOnImage(image, lx, ly, rx, ry, color, terrainSize);
            }
        }

        image.SaveAsPng(outputPath);
        TerrainCreationLogger.Current?.Detail($"Exported smoothed elevation debug image: {outputPath}");
        TerrainCreationLogger.Current?.Detail($"Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)");
    }

    /// <summary>
    ///     Draws a line on an image with Y-flipping.
    /// </summary>
    private static void DrawLineOnImage(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color, int height)
    {
        // Flip Y coordinates
        y0 = height - 1 - y0;
        y1 = height - 1 - y1;

        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < img.Width && y0 >= 0 && y0 < img.Height)
                img[x0, y0] = color;
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    ///     Gets a color for elevation visualization (blue=low, green=mid, red=high).
    /// </summary>
    private static Rgba32 GetColorForElevation(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        var r = Math.Clamp(value * 2.0f, 0f, 1f);
        var b = Math.Clamp((1.0f - value) * 2.0f, 0f, 1f);
        var g = 1.0f - Math.Abs(value - 0.5f) * 2.0f;
        return new Rgba32(r, g, b);
    }

    /// <summary>
    ///     Exports heightmap with road outlines overlaid.
    /// </summary>
    private void ExportSmoothedHeightmapWithOutlines(
        float[,] heightMap,
        UnifiedRoadNetwork network,
        float[,] distanceField,
        float metersPerPixel,
        string outputPath)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);

        // Find min/max elevations
        var minElev = float.MaxValue;
        var maxElev = float.MinValue;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var h = heightMap[y, x];
            if (h < minElev) minElev = h;
            if (h > maxElev) maxElev = h;
        }

        var elevRange = maxElev - minElev;
        if (elevRange < 0.001f) elevRange = 1.0f;

        using var image = new Image<Rgba32>(width, height);

        // Get max road width and blend range for outline calculation
        var maxHalfWidth = network.Splines.Max(s => s.Parameters.RoadWidthMeters) / 2.0f;
        var maxBlendRange = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);

        // Draw heightmap with outlines
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var flippedY = height - 1 - y;
            var h = heightMap[y, x];
            var d = distanceField[y, x];

            // Normalize elevation to 0-1
            var normalizedH = (h - minElev) / elevRange;
            var gray = (byte)(normalizedH * 255);

            Rgba32 color;

            // Check if on road edge outline
            if (MathF.Abs(d - maxHalfWidth) < metersPerPixel * 1.5f)
                // Cyan outline at road edge
                color = new Rgba32(0, 255, 255, 255);
            else if (MathF.Abs(d - (maxHalfWidth + maxBlendRange)) < metersPerPixel * 1.5f)
                // Magenta outline at blend zone edge
                color = new Rgba32(255, 0, 255, 255);
            else
                // Grayscale heightmap
                color = new Rgba32(gray, gray, gray, 255);

            image[x, flippedY] = color;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        image.SaveAsPng(outputPath);

        TerrainCreationLogger.Current?.Detail($"Exported smoothed heightmap with outlines: {outputPath}");
    }

    /// <summary>
    ///     Converts a UnifiedSmoothingResult to a standard SmoothingResult for backward compatibility.
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