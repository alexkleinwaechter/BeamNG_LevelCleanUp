using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     Exports road splines to BeamNG master spline JSON format.
///     Master splines can be imported into BeamNG's road editor for further editing.
///     Coordinate system notes:
///     - Input splines are in terrain meter coordinates (origin at bottom-left, Y up)
///     - Output JSON uses BeamNG world coordinates (origin at center, X/Y/Z right-handed)
///     - The transformation uses BeamNgCoordinateTransformer.TerrainToWorld
/// </summary>
public static class MasterSplineExporter
{
    /// <summary>
    ///     Exports all splines from a unified road network to a single BeamNG master spline JSON file.
    ///     This is the preferred method for exporting splines from the unified road smoothing pipeline.
    ///     Splines are named using the format: "{MaterialName}_{index:D3}" (e.g., "Asphalt_001", "DirtRoad_002")
    ///     All materials' splines are combined into one JSON file for easy import into BeamNG.
    /// </summary>
    /// <param name="network">The unified road network containing all materials' splines</param>
    /// <param name="heightMap">
    ///     Heightmap array for elevation lookup (uses smoothed elevations from cross-sections when
    ///     available)
    /// </param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="terrainSizePixels">Terrain size in pixels</param>
    /// <param name="terrainBaseHeight">Terrain base height offset for Z coordinates</param>
    /// <param name="outputDirectory">Directory to write the master_splines.json file</param>
    /// <param name="nodeDistanceMeters">Distance between nodes in the exported splines (default: 15m)</param>
    public static void ExportFromUnifiedNetwork(
        UnifiedRoadNetwork network,
        float[,]? heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        string outputDirectory,
        float nodeDistanceMeters = 15.0f)
    {
        if (network.Splines.Count == 0)
        {
            TerrainLogger.Warning("No splines in unified network to export as master splines.");
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var splinesDir = Path.Combine(outputDirectory, "splines");
        Directory.CreateDirectory(splinesDir);

        TerrainCreationLogger.Current?.Detail(
            $"Exporting {network.Splines.Count} master spline(s) from unified network to JSON...");
        TerrainCreationLogger.Current?.Detail(
            $"  TerrainBaseHeight={terrainBaseHeight:F1}m, NodeDistance={nodeDistanceMeters:F1}m");

        var masterSplines = new List<MasterSpline>();

        // Group splines by material for organized naming
        var splinesByMaterial = network.Splines
            .GroupBy(s => s.MaterialName)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var materialGroup in splinesByMaterial)
        {
            var materialName = materialGroup.Key;
            var materialSplines = materialGroup.OrderBy(s => s.SplineId).ToList();

            TerrainCreationLogger.Current?.Detail($"  Material '{materialName}': {materialSplines.Count} spline(s)");

            var splineIndex = 0;
            foreach (var paramSpline in materialSplines)
            {
                splineIndex++;
                var spline = paramSpline.Spline;

                if (spline == null || spline.ControlPoints.Count < 2)
                    continue;

                // Get cross-sections for this spline to use smoothed elevations
                var crossSections = network.GetCrossSectionsForSpline(paramSpline.SplineId).ToList();

                // Sample nodes along the spline
                List<SplineNode> nodes;
                if (crossSections.Count >= 2)
                    // Use cross-section elevations (smoothed/harmonized)
                    nodes = SampleNodesFromUnifiedCrossSections(
                        crossSections,
                        heightMap,
                        metersPerPixel,
                        terrainSizePixels,
                        terrainBaseHeight,
                        nodeDistanceMeters);
                else
                    // Fallback to direct spline sampling
                    nodes = SampleNodesFromSpline(
                        spline,
                        heightMap,
                        metersPerPixel,
                        terrainSizePixels,
                        terrainBaseHeight,
                        nodeDistanceMeters);

                if (nodes.Count < 2)
                    continue;

                // Use road surface width for the master spline
                var roadWidth = paramSpline.Parameters.EffectiveMasterSplineWidthMeters;

                // Create spline name: MaterialName_index (e.g., "Asphalt_001")
                var splineName = $"{SanitizeName(materialName)}_{splineIndex:D3}";

                // NOTE: We intentionally export WITHOUT banking data.
                // Our terrain banking system is applied directly to the heightmap, which works great.
                // However, BeamNG's master spline banking system works differently and conflicts
                // with our terrain-based banking. Exporting flat splines ensures the road surface
                // in BeamNG matches our pre-banked terrain.
                var masterSpline = new MasterSpline
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = splineName,
                    Nodes = nodes,

                    // Always disable banking in export - terrain already has banking applied
                    IsAutoBanking = false,
                    BankStrength = 0.0,
                    AutoBankFalloff = 0.6,

                    // Always use vertical normals - terrain banking is in the heightmap
                    Nmls = nodes.Select(_ => new SplineNormal { X = 0, Y = 0, Z = 1 }).ToList(),

                    Widths = nodes.Select(_ => roadWidth).ToList()
                };

                masterSplines.Add(masterSpline);
            }
        }

        if (masterSplines.Count == 0)
        {
            TerrainLogger.Warning("No master splines generated from unified network.");
            return;
        }

        // Write the unified JSON file
        var splineFile = new MasterSplineFile
        {
            MasterSplines = masterSplines
        };

        var outputPath = Path.Combine(splinesDir, "master_splines.json");
        WriteSplineFile(splineFile, outputPath);

        TerrainCreationLogger.Current?.Detail($"Exported {masterSplines.Count} master spline(s) to: {outputPath}");

        // Also export combined spline mask
        ExportUnifiedSplineMasks(network, metersPerPixel, terrainSizePixels, splinesDir);
    }

    /// <summary>
    ///     Exports spline masks from unified network as PNG images.
    ///     Creates:
    ///     - One PNG per spline with material name prefix (e.g., Asphalt_001.png, DirtRoad_001.png)
    ///     - One combined PNG with all splines from all materials (all_splines.png)
    /// </summary>
    private static void ExportUnifiedSplineMasks(
        UnifiedRoadNetwork network,
        float metersPerPixel,
        int terrainSizePixels,
        string splinesDir)
    {
        using var combinedImage = new Image<L16>(terrainSizePixels, terrainSizePixels, new L16(0));

        // Group splines by material for organized naming
        var splinesByMaterial = network.Splines
            .GroupBy(s => s.MaterialName)
            .OrderBy(g => g.Key)
            .ToList();

        var totalSplineCount = 0;

        foreach (var materialGroup in splinesByMaterial)
        {
            var materialName = SanitizeName(materialGroup.Key);
            var materialSplines = materialGroup.OrderBy(s => s.SplineId).ToList();

            var splineIndex = 0;
            foreach (var paramSpline in materialSplines)
            {
                splineIndex++;
                var spline = paramSpline.Spline;
                if (spline == null || spline.TotalLength < 1f)
                    continue;

                var halfWidth = paramSpline.Parameters.EffectiveMasterSplineWidthMeters / 2.0f;

                // Create individual spline image
                using var splineImage = new Image<L16>(terrainSizePixels, terrainSizePixels, new L16(0));

                // Sample spline and draw road
                var samples = spline.SampleByDistance(0.5f); // Fine sampling for accuracy

                for (var i = 0; i < samples.Count - 1; i++)
                {
                    var s1 = samples[i];
                    var s2 = samples[i + 1];

                    // Get corners of road segment
                    var left1 = s1.Position - s1.Normal * halfWidth;
                    var right1 = s1.Position + s1.Normal * halfWidth;
                    var left2 = s2.Position - s2.Normal * halfWidth;
                    var right2 = s2.Position + s2.Normal * halfWidth;

                    // Convert to pixels
                    var l1x = (int)(left1.X / metersPerPixel);
                    var l1y = (int)(left1.Y / metersPerPixel);
                    var r1x = (int)(right1.X / metersPerPixel);
                    var r1y = (int)(right1.Y / metersPerPixel);
                    var l2x = (int)(left2.X / metersPerPixel);
                    var l2y = (int)(left2.Y / metersPerPixel);
                    var r2x = (int)(right2.X / metersPerPixel);
                    var r2y = (int)(right2.Y / metersPerPixel);

                    // Draw quad on individual spline image
                    FillQuadL16(splineImage, l1x, l1y, r1x, r1y, r2x, r2y, l2x, l2y,
                        new L16(ushort.MaxValue), terrainSizePixels);

                    // Draw quad on combined image
                    FillQuadL16(combinedImage, l1x, l1y, r1x, r1y, r2x, r2y, l2x, l2y,
                        new L16(ushort.MaxValue), terrainSizePixels);
                }

                // Save individual spline mask with material name prefix
                var splineFileName = $"{materialName}_{splineIndex:D3}.png";
                var splineFilePath = Path.Combine(splinesDir, splineFileName);
                splineImage.SaveAsPng(splineFilePath);

                totalSplineCount++;
            }
        }

        // Save combined mask
        var combinedFilePath = Path.Combine(splinesDir, "all_splines.png");
        combinedImage.SaveAsPng(combinedFilePath);

        TerrainCreationLogger.Current?.Detail(
            $"Exported {totalSplineCount} individual spline mask(s) + combined mask (16-bit grayscale)");
        TerrainCreationLogger.Current?.Detail($"  Combined mask: {combinedFilePath}");
    }

    // NOTE: ExportBankedNormals method removed.
    // We no longer export banking data to master splines because:
    // 1. Our terrain banking is applied directly to the heightmap (works perfectly)
    // 2. BeamNG's spline banking system works differently and would conflict
    // 3. Exporting flat splines ensures BeamNG roads match our pre-banked terrain

    /// <summary>
    ///     Samples nodes from unified cross-sections, using their calculated target elevations.
    /// </summary>
    private static List<SplineNode> SampleNodesFromUnifiedCrossSections(
        List<UnifiedCrossSection> crossSections,
        float[,]? heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeDistanceMeters)
    {
        var nodes = new List<SplineNode>();

        // Calculate total path length and determine sampling
        var totalLength = EstimatePathLengthFromUnified(crossSections);
        var nodeCount = Math.Max(2, (int)Math.Ceiling(totalLength / nodeDistanceMeters) + 1);
        var step = Math.Max(1, crossSections.Count / nodeCount);

        for (var i = 0; i < crossSections.Count; i += step)
        {
            var cs = crossSections[i];

            var terrainX = cs.CenterPoint.X;
            var terrainY = cs.CenterPoint.Y;

            // Use smoothed target elevation if available
            float elevation = 0;
            if (!float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
            {
                elevation = cs.TargetElevation;
            }
            else if (heightMap != null)
            {
                var pixelX = (int)(terrainX / metersPerPixel);
                var pixelY = (int)(terrainY / metersPerPixel);
                var size = heightMap.GetLength(0);
                pixelX = Math.Clamp(pixelX, 0, size - 1);
                pixelY = Math.Clamp(pixelY, 0, size - 1);
                elevation = heightMap[pixelY, pixelX];
            }

            elevation += terrainBaseHeight;

            var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                terrainX, terrainY, elevation,
                terrainSizePixels, metersPerPixel);

            nodes.Add(new SplineNode
            {
                X = worldPos.X,
                Y = worldPos.Y,
                Z = worldPos.Z
            });
        }

        // Always include the last cross-section
        var lastCs = crossSections[^1];
        var lastElevation = !float.IsNaN(lastCs.TargetElevation) && lastCs.TargetElevation > -1000f
            ? lastCs.TargetElevation
            : GetHeightMapElevation(heightMap, lastCs.CenterPoint.X, lastCs.CenterPoint.Y, metersPerPixel);

        lastElevation += terrainBaseHeight;

        var lastWorldPos = BeamNgCoordinateTransformer.TerrainToWorld(
            lastCs.CenterPoint.X, lastCs.CenterPoint.Y, lastElevation,
            terrainSizePixels, metersPerPixel);

        if (nodes.Count == 0 ||
            Math.Abs(nodes[^1].X - lastWorldPos.X) > 0.1 ||
            Math.Abs(nodes[^1].Y - lastWorldPos.Y) > 0.1)
            nodes.Add(new SplineNode
            {
                X = lastWorldPos.X,
                Y = lastWorldPos.Y,
                Z = lastWorldPos.Z
            });

        return nodes;
    }

    /// <summary>
    ///     Estimates path length from unified cross-sections.
    /// </summary>
    private static float EstimatePathLengthFromUnified(List<UnifiedCrossSection> crossSections)
    {
        float length = 0;
        for (var i = 1; i < crossSections.Count; i++)
            length += Vector2.Distance(crossSections[i - 1].CenterPoint, crossSections[i].CenterPoint);
        return length;
    }

    /// <summary>
    ///     Gets elevation from heightmap at terrain coordinates.
    /// </summary>
    private static float GetHeightMapElevation(float[,]? heightMap, float terrainX, float terrainY,
        float metersPerPixel)
    {
        if (heightMap == null) return 0;
        var pixelX = (int)(terrainX / metersPerPixel);
        var pixelY = (int)(terrainY / metersPerPixel);
        var size = heightMap.GetLength(0);
        pixelX = Math.Clamp(pixelX, 0, size - 1);
        pixelY = Math.Clamp(pixelY, 0, size - 1);
        return heightMap[pixelY, pixelX];
    }

    /// <summary>
    ///     Sanitizes a material name for use in spline names.
    ///     Removes special characters and replaces spaces with underscores.
    /// </summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";

        // Replace spaces and special characters
        var sanitized = new StringBuilder();
        foreach (var c in name)
            if (char.IsLetterOrDigit(c))
                sanitized.Append(c);
            else if (c == ' ' || c == '-' || c == '_')
                sanitized.Append('_');

        var result = sanitized.ToString();

        // Remove consecutive underscores and trim
        while (result.Contains("__"))
            result = result.Replace("__", "_");

        return result.Trim('_');
    }

    /// <summary>
    ///     Exports splines from road geometry to BeamNG master spline JSON format.
    ///     This method exports splines extracted from PNG layer masks.
    ///     Splines are named sequentially (path_001, path_002, etc.) or by PathId.
    /// </summary>
    /// <param name="geometry">Road geometry containing cross-sections with PathIds</param>
    /// <param name="heightMap">Heightmap array for elevation lookup (may be null)</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="terrainSizePixels">Terrain size in pixels</param>
    /// <param name="parameters">Road smoothing parameters (includes TerrainBaseHeight)</param>
    public static void ExportFromGeometry(
        RoadGeometry geometry,
        float[,]? heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        RoadSmoothingParameters parameters)
    {
        if (geometry.CrossSections.Count == 0)
        {
            TerrainLogger.Warning("No cross-sections to export as master splines.");
            return;
        }

        var baseDir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Directory.GetCurrentDirectory();
        var splinesDir = Path.Combine(baseDir, "splines");
        Directory.CreateDirectory(splinesDir);

        var roadWidth = parameters.EffectiveMasterSplineWidthMeters;
        var terrainBaseHeight = parameters.TerrainBaseHeight;
        var nodeDistance = parameters.MasterSplineNodeDistanceMeters;

        // Group cross-sections by PathId to reconstruct spline paths
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .OrderBy(g => g.Key)
            .ToList();

        if (pathGroups.Count == 0)
        {
            TerrainLogger.Warning("No valid paths to export as master splines.");
            return;
        }

        TerrainCreationLogger.Current?.Detail(
            $"Exporting {pathGroups.Count} master spline(s) to JSON (TerrainBaseHeight={terrainBaseHeight:F1}m, NodeDistance={nodeDistance:F1}m)...");

        var masterSplines = new List<MasterSpline>();
        var pathIndex = 0;

        foreach (var pathGroup in pathGroups)
        {
            pathIndex++;
            var pathSections = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();

            if (pathSections.Count < 2)
                continue;

            // Sample nodes from cross-sections (not every cross-section, spaced appropriately)
            var nodes = SampleNodesFromCrossSections(
                pathSections,
                heightMap,
                metersPerPixel,
                terrainSizePixels,
                terrainBaseHeight,
                nodeDistance);

            if (nodes.Count < 2)
                continue;

            var spline = new MasterSpline
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"path_{pathIndex:D3}",
                Nodes = nodes,
                Nmls = nodes.Select(_ => new SplineNormal { X = 0, Y = 0, Z = 1 }).ToList(),
                Widths = nodes.Select(_ => roadWidth).ToList()
            };

            masterSplines.Add(spline);
        }

        if (masterSplines.Count == 0)
        {
            TerrainLogger.Warning("No master splines generated from geometry.");
            return;
        }

        // Write the JSON file
        var splineFile = new MasterSplineFile
        {
            MasterSplines = masterSplines
        };

        var outputPath = Path.Combine(splinesDir, "master_splines.json");
        WriteSplineFile(splineFile, outputPath);

        TerrainCreationLogger.Current?.Detail($"Exported {masterSplines.Count} master spline(s) to: {outputPath}");
    }

    /// <summary>
    ///     Exports pre-built splines (from OSM or other sources) to BeamNG master spline JSON format.
    ///     This method exports splines that were created from OSM data.
    ///     Splines are named using OSM feature names when available.
    /// </summary>
    /// <param name="splines">List of pre-built RoadSpline objects</param>
    /// <param name="heightMap">Heightmap array for elevation lookup (may be null)</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <param name="terrainSizePixels">Terrain size in pixels</param>
    /// <param name="parameters">Road smoothing parameters (includes TerrainBaseHeight)</param>
    /// <param name="splineNames">Optional list of names corresponding to each spline (from OSM features)</param>
    public static void ExportFromPreBuiltSplines(
        List<RoadSpline> splines,
        float[,]? heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        RoadSmoothingParameters parameters,
        List<string>? splineNames = null)
    {
        if (splines.Count == 0)
        {
            TerrainLogger.Warning("No pre-built splines to export.");
            return;
        }

        var baseDir = parameters.DebugOutputDirectory;
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Directory.GetCurrentDirectory();
        var splinesDir = Path.Combine(baseDir, "splines");
        Directory.CreateDirectory(splinesDir);

        var roadWidth = parameters.EffectiveMasterSplineWidthMeters;
        var terrainBaseHeight = parameters.TerrainBaseHeight;
        var nodeDistance = parameters.MasterSplineNodeDistanceMeters;

        TerrainCreationLogger.Current?.Detail(
            $"Exporting {splines.Count} pre-built spline(s) to JSON (TerrainBaseHeight={terrainBaseHeight:F1}m, NodeDistance={nodeDistance:F1}m)...");

        var masterSplines = new List<MasterSpline>();

        for (var i = 0; i < splines.Count; i++)
        {
            var spline = splines[i];

            if (spline.ControlPoints.Count < 2)
                continue;

            // Sample nodes along the spline
            var nodes = SampleNodesFromSpline(
                spline,
                heightMap,
                metersPerPixel,
                terrainSizePixels,
                terrainBaseHeight,
                nodeDistance);

            if (nodes.Count < 2)
                continue;

            // Determine spline name
            string name;
            if (splineNames != null && i < splineNames.Count && !string.IsNullOrEmpty(splineNames[i]))
                name = splineNames[i];
            else
                name = $"osm_spline_{i + 1:D3}";

            var masterSpline = new MasterSpline
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Nodes = nodes,
                Nmls = nodes.Select(_ => new SplineNormal { X = 0, Y = 0, Z = 1 }).ToList(),
                Widths = nodes.Select(_ => roadWidth).ToList()
            };

            masterSplines.Add(masterSpline);
        }

        if (masterSplines.Count == 0)
        {
            TerrainLogger.Warning("No master splines generated from pre-built splines.");
            return;
        }

        // Write the JSON file
        var splineFile = new MasterSplineFile
        {
            MasterSplines = masterSplines
        };

        var outputPath = Path.Combine(splinesDir, "master_splines.json");
        WriteSplineFile(splineFile, outputPath);

        TerrainCreationLogger.Current?.Detail($"Exported {masterSplines.Count} OSM master spline(s) to: {outputPath}");
    }

    /// <summary>
    ///     Samples nodes from cross-sections, converting to BeamNG world coordinates.
    /// </summary>
    private static List<SplineNode> SampleNodesFromCrossSections(
        List<CrossSection> crossSections,
        float[,]? heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeDistanceMeters)
    {
        var nodes = new List<SplineNode>();

        // Calculate total path length and determine sampling
        var totalLength = EstimatePathLength(crossSections);
        var nodeCount = Math.Max(2, (int)Math.Ceiling(totalLength / nodeDistanceMeters) + 1);
        var step = Math.Max(1, crossSections.Count / nodeCount);

        for (var i = 0; i < crossSections.Count; i += step)
        {
            var cs = crossSections[i];

            // CenterPoint is in terrain meter coordinates
            var terrainX = cs.CenterPoint.X;
            var terrainY = cs.CenterPoint.Y;

            // Get elevation from cross-section target elevation or heightmap
            float elevation = 0;
            if (!float.IsNaN(cs.TargetElevation) && cs.TargetElevation > -1000f)
            {
                elevation = cs.TargetElevation;
            }
            else if (heightMap != null)
            {
                // Convert meters back to pixels for heightmap lookup
                var pixelX = (int)(terrainX / metersPerPixel);
                var pixelY = (int)(terrainY / metersPerPixel);
                var size = heightMap.GetLength(0);
                pixelX = Math.Clamp(pixelX, 0, size - 1);
                pixelY = Math.Clamp(pixelY, 0, size - 1);
                elevation = heightMap[pixelY, pixelX];
            }

            // Add terrain base height offset to elevation
            elevation += terrainBaseHeight;

            // Convert to BeamNG world coordinates
            var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                terrainX, terrainY, elevation,
                terrainSizePixels, metersPerPixel);

            nodes.Add(new SplineNode
            {
                X = worldPos.X,
                Y = worldPos.Y,
                Z = worldPos.Z
            });
        }

        // Always include the last cross-section
        var lastCs = crossSections[^1];
        var lastTerrainX = lastCs.CenterPoint.X;
        var lastTerrainY = lastCs.CenterPoint.Y;
        float lastElevation = 0;

        if (!float.IsNaN(lastCs.TargetElevation) && lastCs.TargetElevation > -1000f)
        {
            lastElevation = lastCs.TargetElevation;
        }
        else if (heightMap != null)
        {
            var pixelX = (int)(lastTerrainX / metersPerPixel);
            var pixelY = (int)(lastTerrainY / metersPerPixel);
            var size = heightMap.GetLength(0);
            pixelX = Math.Clamp(pixelX, 0, size - 1);
            pixelY = Math.Clamp(pixelY, 0, size - 1);
            lastElevation = heightMap[pixelY, pixelX];
        }

        // Add terrain base height offset to last elevation
        lastElevation += terrainBaseHeight;

        var lastWorldPos = BeamNgCoordinateTransformer.TerrainToWorld(
            lastTerrainX, lastTerrainY, lastElevation,
            terrainSizePixels, metersPerPixel);

        // Only add if different from the last added node
        if (nodes.Count == 0 ||
            Math.Abs(nodes[^1].X - lastWorldPos.X) > 0.1 ||
            Math.Abs(nodes[^1].Y - lastWorldPos.Y) > 0.1)
            nodes.Add(new SplineNode
            {
                X = lastWorldPos.X,
                Y = lastWorldPos.Y,
                Z = lastWorldPos.Z
            });

        return nodes;
    }

    /// <summary>
    ///     Samples nodes from a RoadSpline, converting to BeamNG world coordinates.
    /// </summary>
    private static List<SplineNode> SampleNodesFromSpline(
        RoadSpline spline,
        float[,]? heightMap,
        float metersPerPixel,
        int terrainSizePixels,
        float terrainBaseHeight,
        float nodeDistanceMeters)
    {
        var nodes = new List<SplineNode>();

        // Sample at the specified node distance
        var nodeCount = Math.Max(2, (int)Math.Ceiling(spline.TotalLength / nodeDistanceMeters) + 1);
        var spacing = spline.TotalLength / (nodeCount - 1);

        for (float distance = 0; distance <= spline.TotalLength; distance += spacing)
        {
            var pos = spline.GetPointAtDistance(distance);

            // Spline coordinates are already in terrain meter coordinates
            var terrainX = pos.X;
            var terrainY = pos.Y;

            // Get elevation from heightmap
            float elevation = 0;
            if (heightMap != null)
            {
                var pixelX = (int)(terrainX / metersPerPixel);
                var pixelY = (int)(terrainY / metersPerPixel);
                var size = heightMap.GetLength(0);
                pixelX = Math.Clamp(pixelX, 0, size - 1);
                pixelY = Math.Clamp(pixelY, 0, size - 1);
                elevation = heightMap[pixelY, pixelX];
            }

            // Add terrain base height offset to elevation
            elevation += terrainBaseHeight;

            // Convert to BeamNG world coordinates
            var worldPos = BeamNgCoordinateTransformer.TerrainToWorld(
                terrainX, terrainY, elevation,
                terrainSizePixels, metersPerPixel);

            nodes.Add(new SplineNode
            {
                X = worldPos.X,
                Y = worldPos.Y,
                Z = worldPos.Z
            });
        }

        // Always include the final point
        var lastPos = spline.GetPointAtDistance(spline.TotalLength);
        float lastElevation = 0;
        if (heightMap != null)
        {
            var pixelX = (int)(lastPos.X / metersPerPixel);
            var pixelY = (int)(lastPos.Y / metersPerPixel);
            var size = heightMap.GetLength(0);
            pixelX = Math.Clamp(pixelX, 0, size - 1);
            pixelY = Math.Clamp(pixelY, 0, size - 1);
            lastElevation = heightMap[pixelY, pixelX];
        }

        // Add terrain base height offset to last elevation
        lastElevation += terrainBaseHeight;

        var lastWorldPos = BeamNgCoordinateTransformer.TerrainToWorld(
            lastPos.X, lastPos.Y, lastElevation,
            terrainSizePixels, metersPerPixel);

        if (nodes.Count == 0 ||
            Math.Abs(nodes[^1].X - lastWorldPos.X) > 0.1 ||
            Math.Abs(nodes[^1].Y - lastWorldPos.Y) > 0.1)
            nodes.Add(new SplineNode
            {
                X = lastWorldPos.X,
                Y = lastWorldPos.Y,
                Z = lastWorldPos.Z
            });

        return nodes;
    }

    /// <summary>
    ///     Estimates the total path length from cross-sections.
    /// </summary>
    private static float EstimatePathLength(List<CrossSection> crossSections)
    {
        float length = 0;
        for (var i = 1; i < crossSections.Count; i++)
            length += Vector2.Distance(crossSections[i - 1].CenterPoint, crossSections[i].CenterPoint);
        return length;
    }

    /// <summary>
    ///     Writes the spline file to disk with proper JSON formatting.
    /// </summary>
    private static void WriteSplineFile(MasterSplineFile splineFile, string outputPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(splineFile, options);
        File.WriteAllText(outputPath, json);
    }

    #region JSON Data Models

    /// <summary>
    ///     Root object for the master spline JSON file.
    /// </summary>
    internal class MasterSplineFile
    {
        [JsonPropertyName("linkedSplines")] public Dictionary<string, object> LinkedSplines { get; set; } = new();

        [JsonPropertyName("masterSplines")] public List<MasterSpline> MasterSplines { get; set; } = new();
    }

    /// <summary>
    ///     Represents a single master spline in BeamNG format.
    /// </summary>
    internal class MasterSpline
    {
        [JsonPropertyName("autoBankFalloff")] public double AutoBankFalloff { get; set; } = 0.6;

        [JsonPropertyName("bankStrength")] public double BankStrength { get; set; } = 0.5;

        [JsonPropertyName("homologationPreset")]
        public string HomologationPreset { get; set; } = "Freeway / Autobahn";

        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

        [JsonPropertyName("isAutoBanking")] public bool IsAutoBanking { get; set; }

        [JsonPropertyName("isConformToTerrain")]
        public bool IsConformToTerrain { get; set; } = false;

        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;

        [JsonPropertyName("isLoop")] public bool IsLoop { get; set; } = false;

        [JsonPropertyName("layers")] public Dictionary<string, object> Layers { get; set; } = new();

        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nmls")] public List<SplineNormal> Nmls { get; set; } = new();

        [JsonPropertyName("nodes")] public List<SplineNode> Nodes { get; set; } = new();

        [JsonPropertyName("splineAnalysisMode")]
        public int SplineAnalysisMode { get; set; } = 0;

        [JsonPropertyName("widths")] public List<float> Widths { get; set; } = new();
    }

    /// <summary>
    ///     Represents a node position in a master spline.
    /// </summary>
    internal class SplineNode
    {
        [JsonPropertyName("x")] public double X { get; set; }

        [JsonPropertyName("y")] public double Y { get; set; }

        [JsonPropertyName("z")] public double Z { get; set; }
    }

    /// <summary>
    ///     Represents the normal vector at a spline node (typically pointing up).
    /// </summary>
    internal class SplineNormal
    {
        [JsonPropertyName("x")] public double X { get; set; }

        [JsonPropertyName("y")] public double Y { get; set; }

        [JsonPropertyName("z")] public double Z { get; set; }
    }

    #endregion

    #region Drawing Helpers

    /// <summary>
    ///     Fills a quadrilateral on a 16-bit grayscale image.
    /// </summary>
    private static void FillQuadL16(
        Image<L16> img,
        int x0, int y0, int x1, int y1, int x2, int y2, int x3, int y3,
        L16 color, int imgHeight)
    {
        // Flip Y coordinates
        y0 = imgHeight - 1 - y0;
        y1 = imgHeight - 1 - y1;
        y2 = imgHeight - 1 - y2;
        y3 = imgHeight - 1 - y3;

        var minY = Math.Max(0, Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)));
        var maxY = Math.Min(img.Height - 1, Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)));
        var minX = Math.Max(0, Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)));
        var maxX = Math.Min(img.Width - 1, Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)));

        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (IsPointInQuad(x, y, x0, y0, x1, y1, x2, y2, x3, y3))
                img[x, y] = color;
    }

    /// <summary>
    ///     Checks if a point is inside a convex quadrilateral using cross product test.
    /// </summary>
    private static bool IsPointInQuad(int px, int py, int x0, int y0, int x1, int y1, int x2, int y2, int x3, int y3)
    {
        var sign0 = Sign((x1 - x0) * (py - y0) - (y1 - y0) * (px - x0));
        var sign1 = Sign((x2 - x1) * (py - y1) - (y2 - y1) * (px - x1));
        var sign2 = Sign((x3 - x2) * (py - y2) - (y3 - y2) * (px - x2));
        var sign3 = Sign((x0 - x3) * (py - y3) - (y0 - y3) * (px - x3));

        return (sign0 >= 0 && sign1 >= 0 && sign2 >= 0 && sign3 >= 0) ||
               (sign0 <= 0 && sign1 <= 0 && sign2 <= 0 && sign3 <= 0);
    }

    private static int Sign(int value)
    {
        return value > 0 ? 1 : value < 0 ? -1 : 0;
    }

    #endregion
}