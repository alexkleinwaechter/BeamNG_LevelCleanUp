using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Analyzes terrain road network without performing full generation.
/// Extracts splines, detects junctions, and allows interactive modification
/// before final terrain generation.
/// </summary>
public class TerrainAnalyzer
{
    private readonly UnifiedRoadNetworkBuilder _networkBuilder;
    private readonly NetworkJunctionDetector _junctionDetector;
    private readonly IHeightCalculator _elevationCalculator;

    /// <summary>
    /// The last analyzed network (retained for modification).
    /// </summary>
    private UnifiedRoadNetwork? _analyzedNetwork;

    /// <summary>
    /// Pre-harmonization elevations from the last analysis.
    /// </summary>
    private Dictionary<int, float> _preHarmonizationElevations = new();

    public TerrainAnalyzer()
    {
        _networkBuilder = new UnifiedRoadNetworkBuilder();
        _junctionDetector = new NetworkJunctionDetector();
        _elevationCalculator = new OptimizedElevationSmoother();
    }

    /// <summary>
    /// Result of terrain analysis containing the unified road network.
    /// </summary>
    public class AnalysisResult
    {
        /// <summary>
        /// Whether the analysis completed successfully.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The unified road network with detected junctions.
        /// </summary>
        public UnifiedRoadNetwork? Network { get; init; }

        /// <summary>
        /// Error message if analysis failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Elevations before any harmonization, for comparison.
        /// Key = cross-section index, Value = elevation in meters.
        /// </summary>
        public Dictionary<int, float> PreHarmonizationElevations { get; init; } = new();

        /// <summary>
        /// Debug image data as PNG bytes (null if not generated).
        /// </summary>
        public byte[]? JunctionDebugImage { get; init; }

        /// <summary>
        /// Width of the debug image in pixels.
        /// </summary>
        public int ImageWidth { get; init; }

        /// <summary>
        /// Height of the debug image in pixels.
        /// </summary>
        public int ImageHeight { get; init; }

        /// <summary>
        /// Number of splines detected in the network.
        /// </summary>
        public int SplineCount => Network?.Splines.Count ?? 0;

        /// <summary>
        /// Number of junctions detected in the network.
        /// </summary>
        public int JunctionCount => Network?.Junctions.Count ?? 0;

        /// <summary>
        /// Total road length in meters.
        /// </summary>
        public float TotalRoadLengthMeters => Network?.Splines.Sum(s => s.TotalLengthMeters) ?? 0;

        /// <summary>
        /// Statistics breakdown by junction type.
        /// </summary>
        public Dictionary<JunctionType, int> JunctionsByType => Network?.Junctions
            .GroupBy(j => j.Type)
            .ToDictionary(g => g.Key, g => g.Count()) ?? new();
    }

    /// <summary>
    /// Analyzes the road network and detects all junctions without modifying terrain.
    /// This runs the spline extraction and junction detection pipeline but stops
    /// before terrain blending, allowing users to review and modify the results.
    /// </summary>
    /// <param name="materials">Material definitions with road parameters.</param>
    /// <param name="heightMap">The terrain heightmap.</param>
    /// <param name="metersPerPixel">Scale factor for coordinate conversion.</param>
    /// <param name="size">Terrain size in pixels.</param>
    /// <param name="globalJunctionDetectionRadius">Global junction detection radius in meters.</param>
    /// <param name="generateDebugImage">Whether to generate a debug image.</param>
    /// <returns>Analysis result containing the network and junctions.</returns>
    public AnalysisResult Analyze(
        List<MaterialDefinition> materials,
        float[,] heightMap,
        float metersPerPixel,
        int size,
        float globalJunctionDetectionRadius = 10.0f,
        bool generateDebugImage = true)
    {
        try
        {
            TerrainLogger.Info("=== TERRAIN ANALYSIS (Preview Mode) ===");

            var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();

            if (roadMaterials.Count == 0)
            {
                TerrainLogger.Warning("TerrainAnalyzer: No road materials to analyze");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage = "No road materials found with road smoothing parameters."
                };
            }

            TerrainCreationLogger.Current?.InfoFileOnly($"  Analyzing {roadMaterials.Count} road material(s)...");

            // Phase 1: Build unified road network from all materials
            TerrainCreationLogger.Current?.InfoFileOnly("  Phase 1: Building unified road network...");
            var network = _networkBuilder.BuildNetwork(materials, heightMap, metersPerPixel, size);

            if (network.Splines.Count == 0)
            {
                TerrainLogger.Warning("TerrainAnalyzer: No splines extracted from materials");
                return new AnalysisResult
                {
                    Success = false,
                    ErrorMessage = "No road splines could be extracted from the materials."
                };
            }

            TerrainCreationLogger.Current?.Detail($"Built network: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections");

            // Phase 2: Calculate target elevations for each spline
            TerrainCreationLogger.Current?.InfoFileOnly("  Phase 2: Calculating target elevations...");
            CalculateNetworkElevations(network, heightMap, metersPerPixel);

            // Capture pre-harmonization elevations for comparison
            _preHarmonizationElevations = CaptureElevations(network);

            // Phase 3: Detect junctions
            TerrainCreationLogger.Current?.InfoFileOnly("  Phase 3: Detecting junctions...");
            var junctions = _junctionDetector.DetectJunctions(network, globalJunctionDetectionRadius);
            TerrainCreationLogger.Current?.InfoFileOnly($"    Detected {junctions.Count} junction(s)");

            // Store the analyzed network for later modification
            _analyzedNetwork = network;

            // Generate debug image if requested
            byte[]? debugImageData = null;
            int imageWidth = heightMap.GetLength(1);
            int imageHeight = heightMap.GetLength(0);

            if (generateDebugImage)
            {
                TerrainCreationLogger.Current?.InfoFileOnly("  Generating analysis preview image...");
                debugImageData = GenerateAnalysisDebugImage(
                    network,
                    _preHarmonizationElevations,
                    imageWidth,
                    imageHeight,
                    metersPerPixel);
            }

            // Calculate statistics
            var stats = network.GetStatistics();
            TerrainCreationLogger.Current?.InfoFileOnly($"  Analysis complete: {stats.TotalSplines} splines, {stats.TotalJunctions} junctions, {stats.TotalRoadLengthMeters:F1}m total");
            TerrainCreationLogger.Current?.Detail($"Cross-sections: {stats.TotalCrossSections}");
            TerrainLogger.Info("=== TERRAIN ANALYSIS COMPLETE ===");

            return new AnalysisResult
            {
                Success = true,
                Network = network,
                PreHarmonizationElevations = _preHarmonizationElevations,
                JunctionDebugImage = debugImageData,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight
            };
        }
        catch (Exception ex)
        {
            TerrainLogger.Error($"TerrainAnalyzer: Analysis failed: {ex.Message}");
            return new AnalysisResult
            {
                Success = false,
                ErrorMessage = $"Analysis failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Marks specific junctions as excluded (won't be harmonized).
    /// Call this after user interactively selects junctions to exclude.
    /// </summary>
    /// <param name="network">The network to modify.</param>
    /// <param name="junctionIds">IDs of junctions to exclude.</param>
    /// <param name="reason">Optional reason for exclusion.</param>
    public void ExcludeJunctions(UnifiedRoadNetwork network, IEnumerable<int> junctionIds, string? reason = null)
    {
        var idsSet = junctionIds.ToHashSet();

        foreach (var junction in network.Junctions)
        {
            if (idsSet.Contains(junction.JunctionId))
            {
                junction.IsExcluded = true;
                junction.ExclusionReason = reason ?? "User excluded";
                TerrainCreationLogger.Current?.Detail($"Junction #{junction.JunctionId} ({junction.Type}) marked as excluded: {junction.ExclusionReason}");
            }
        }
    }

    /// <summary>
    /// Clears exclusion from specific junctions.
    /// </summary>
    /// <param name="network">The network to modify.</param>
    /// <param name="junctionIds">IDs of junctions to include again.</param>
    public void IncludeJunctions(UnifiedRoadNetwork network, IEnumerable<int> junctionIds)
    {
        var idsSet = junctionIds.ToHashSet();

        foreach (var junction in network.Junctions)
        {
            if (idsSet.Contains(junction.JunctionId))
            {
                junction.IsExcluded = false;
                junction.ExclusionReason = null;
                TerrainCreationLogger.Current?.Detail($"Junction #{junction.JunctionId} ({junction.Type}) exclusion cleared");
            }
        }
    }

    /// <summary>
    /// Toggles exclusion state of a single junction.
    /// </summary>
    /// <param name="network">The network to modify.</param>
    /// <param name="junctionId">ID of the junction to toggle.</param>
    /// <returns>True if the junction is now excluded, false if included.</returns>
    public bool ToggleJunctionExclusion(UnifiedRoadNetwork network, int junctionId)
    {
        var junction = network.Junctions.FirstOrDefault(j => j.JunctionId == junctionId);
        if (junction == null)
            return false;

        junction.IsExcluded = !junction.IsExcluded;
        if (junction.IsExcluded)
        {
            junction.ExclusionReason = "User excluded";
            TerrainCreationLogger.Current?.Detail($"Junction #{junction.JunctionId} ({junction.Type}) marked as excluded");
        }
        else
        {
            junction.ExclusionReason = null;
            TerrainCreationLogger.Current?.Detail($"Junction #{junction.JunctionId} ({junction.Type}) exclusion cleared");
        }

        return junction.IsExcluded;
    }

    /// <summary>
    /// Gets the last analyzed network for use in full terrain generation.
    /// The network will have any user exclusions already applied.
    /// </summary>
    /// <returns>The modified network, or null if no analysis has been performed.</returns>
    public UnifiedRoadNetwork? GetAnalyzedNetwork()
    {
        return _analyzedNetwork;
    }

    /// <summary>
    /// Gets the pre-harmonization elevations from the last analysis.
    /// </summary>
    /// <returns>Dictionary of cross-section index to elevation.</returns>
    public Dictionary<int, float> GetPreHarmonizationElevations()
    {
        return _preHarmonizationElevations;
    }

    /// <summary>
    /// Clears the cached analysis results.
    /// </summary>
    public void ClearAnalysis()
    {
        _analyzedNetwork = null;
        _preHarmonizationElevations.Clear();
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

            // Calculate elevations directly on UnifiedCrossSections (no conversion roundtrip)
            _elevationCalculator.CalculateTargetElevations(crossSections, parameters, heightMap, metersPerPixel);
            totalCalculated += crossSections.Count;
        }

        TerrainCreationLogger.Current?.Detail($"Calculated elevations for {totalCalculated} cross-sections");
    }

    /// <summary>
    /// Captures current elevations for later comparison.
    /// </summary>
    private Dictionary<int, float> CaptureElevations(UnifiedRoadNetwork network)
    {
        return network.CrossSections
            .Where(cs => !cs.IsExcluded && !float.IsNaN(cs.TargetElevation))
            .ToDictionary(cs => cs.Index, cs => cs.TargetElevation);
    }

    /// <summary>
    /// Generates a debug image showing the analysis results as PNG byte array.
    /// Includes visualization for:
    /// - Road cross-sections (gray)
    /// - Spline centerlines (color-coded by material)
    /// - Network junction types (color-coded by geometric type)
    /// - OSM junction hints (colored outer ring when OSM data available)
    /// - Excluded junctions (gray with X mark)
    /// - Cross-material indicators (white outer ring)
    /// </summary>
    private byte[] GenerateAnalysisDebugImage(
        UnifiedRoadNetwork network,
        Dictionary<int, float> preHarmonizationElevations,
        int imageWidth,
        int imageHeight,
        float metersPerPixel)
    {
        using var image = new Image<Rgba32>(imageWidth, imageHeight, new Rgba32(30, 30, 30, 255));

        // Draw cross-sections as road paths (in gray)
        foreach (var cs in network.CrossSections.Where(c => !c.IsExcluded && !float.IsNaN(c.TargetElevation)))
        {
            var halfWidth = cs.EffectiveRoadWidth / 2.0f;
            var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
            var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

            var lx = (int)(left.X / metersPerPixel);
            var ly = (int)(left.Y / metersPerPixel);
            var rx = (int)(right.X / metersPerPixel);
            var ry = (int)(right.Y / metersPerPixel);

            DrawLine(image, lx, ly, rx, ry, new Rgba32(100, 100, 100, 255), imageHeight);
        }

        // Draw spline centerlines (color by material)
        var materialColors = new Dictionary<string, Rgba32>();
        byte colorIndex = 0;
        foreach (var spline in network.Splines)
        {
            if (!materialColors.TryGetValue(spline.MaterialName, out var color))
            {
                // Generate a unique color for each material
                color = colorIndex switch
                {
                    0 => new Rgba32(255, 200, 0, 255),   // Gold
                    1 => new Rgba32(0, 200, 255, 255),   // Cyan
                    2 => new Rgba32(255, 100, 100, 255), // Light Red
                    3 => new Rgba32(100, 255, 100, 255), // Light Green
                    4 => new Rgba32(200, 100, 255, 255), // Light Purple
                    _ => new Rgba32(200, 200, 200, 255)  // Gray
                };
                materialColors[spline.MaterialName] = color;
                colorIndex++;
            }

            // Draw the spline path
            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            for (int i = 0; i < crossSections.Count - 1; i++)
            {
                var p1 = crossSections[i].CenterPoint;
                var p2 = crossSections[i + 1].CenterPoint;

                var x1 = (int)(p1.X / metersPerPixel);
                var y1 = (int)(p1.Y / metersPerPixel);
                var x2 = (int)(p2.X / metersPerPixel);
                var y2 = (int)(p2.Y / metersPerPixel);

                DrawLine(image, x1, y1, x2, y2, color, imageHeight);
            }
        }

        // Draw detected junctions
        foreach (var junction in network.Junctions)
        {
            var jx = (int)(junction.Position.X / metersPerPixel);
            var jy = imageHeight - 1 - (int)(junction.Position.Y / metersPerPixel);

            var radius = junction.Type switch
            {
                JunctionType.Complex => 6,
                JunctionType.CrossRoads => 5,
                JunctionType.Roundabout => 7,
                _ => 4
            };

            Rgba32 junctionColor;
            if (junction.IsExcluded)
            {
                junctionColor = new Rgba32(128, 128, 128, 180);
            }
            else
            {
                junctionColor = junction.Type switch
                {
                    JunctionType.TJunction => new Rgba32(255, 165, 0, 200),
                    JunctionType.CrossRoads => new Rgba32(255, 0, 0, 200),
                    JunctionType.Complex => new Rgba32(255, 0, 255, 200),
                    JunctionType.Roundabout => new Rgba32(0, 255, 255, 200),
                    JunctionType.MidSplineCrossing => new Rgba32(255, 255, 0, 200),
                    _ => new Rgba32(0, 255, 0, 200)
                };
            }

            DrawFilledCircle(image, jx, jy, radius, junctionColor);

            // Draw cross-material indicator (white outline)
            if (junction.IsCrossMaterial && !junction.IsExcluded)
            {
                DrawCircleOutline(image, jx, jy, radius + 3, new Rgba32(255, 255, 255, 200));
            }

            // Draw exclusion indicator (X mark)
            if (junction.IsExcluded)
            {
                DrawX(image, jx, jy, radius + 2, new Rgba32(255, 50, 50, 255));
            }
        }

        // Convert to PNG bytes
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    #region Drawing Helpers

    private static void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color, int height)
    {
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
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void DrawFilledCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
            if (x * x + y * y <= radius * radius)
            {
                var px = cx + x;
                var py = cy + y;
                if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    img[px, py] = color;
            }
    }

    private static void DrawCircleOutline(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 2)
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }

    /// <summary>
    /// Draws a dotted circle outline (used for OSM-sourced junction indicators).
    /// </summary>
    private static void DrawDottedCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 15) // Skip every few degrees for dotted effect
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }

    private static void DrawX(Image<Rgba32> img, int cx, int cy, int size, Rgba32 color)
    {
        for (int i = -size; i <= size; i++)
        {
            // Diagonal 1
            var px1 = cx + i;
            var py1 = cy + i;
            if (px1 >= 0 && px1 < img.Width && py1 >= 0 && py1 < img.Height)
                img[px1, py1] = color;

            // Diagonal 2
            var px2 = cx + i;
            var py2 = cy - i;
            if (px2 >= 0 && px2 < img.Width && py2 >= 0 && py2 < img.Height)
                img[px2, py2] = color;
        }
    }

    #endregion
}
