using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Exports road splines to BeamNG master spline JSON format.
/// Master splines can be imported into BeamNG's road editor for further editing.
/// 
/// Coordinate system notes:
/// - Input splines are in terrain meter coordinates (origin at bottom-left, Y up)
/// - Output JSON uses BeamNG world coordinates (origin at center, X/Y/Z right-handed)
/// - The transformation uses BeamNgCoordinateTransformer.TerrainToWorld
/// </summary>
public static class MasterSplineExporter
{
    /// <summary>
    /// Exports splines from road geometry to BeamNG master spline JSON format.
    /// 
    /// This method exports splines extracted from PNG layer masks.
    /// Splines are named sequentially (path_001, path_002, etc.) or by PathId.
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
        
        var roadWidth = parameters.EffectiveRoadSurfaceWidthMeters;
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
        
        TerrainLogger.Info($"Exporting {pathGroups.Count} master spline(s) to JSON (TerrainBaseHeight={terrainBaseHeight:F1}m, NodeDistance={nodeDistance:F1}m)...");
        
        var masterSplines = new List<MasterSpline>();
        int pathIndex = 0;
        
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
        
        TerrainLogger.Info($"Exported {masterSplines.Count} master spline(s) to: {outputPath}");
    }
    
    /// <summary>
    /// Exports pre-built splines (from OSM or other sources) to BeamNG master spline JSON format.
    /// 
    /// This method exports splines that were created from OSM data.
    /// Splines are named using OSM feature names when available.
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
        
        var roadWidth = parameters.EffectiveRoadSurfaceWidthMeters;
        var terrainBaseHeight = parameters.TerrainBaseHeight;
        var nodeDistance = parameters.MasterSplineNodeDistanceMeters;
        
        TerrainLogger.Info($"Exporting {splines.Count} pre-built spline(s) to JSON (TerrainBaseHeight={terrainBaseHeight:F1}m, NodeDistance={nodeDistance:F1}m)...");
        
        var masterSplines = new List<MasterSpline>();
        
        for (int i = 0; i < splines.Count; i++)
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
            {
                name = splineNames[i];
            }
            else
            {
                name = $"osm_spline_{i + 1:D3}";
            }
            
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
        
        TerrainLogger.Info($"Exported {masterSplines.Count} OSM master spline(s) to: {outputPath}");
    }
    
    /// <summary>
    /// Samples nodes from cross-sections, converting to BeamNG world coordinates.
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
        
        for (int i = 0; i < crossSections.Count; i += step)
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
        {
            nodes.Add(new SplineNode
            {
                X = lastWorldPos.X,
                Y = lastWorldPos.Y,
                Z = lastWorldPos.Z
            });
        }
        
        return nodes;
    }
    
    /// <summary>
    /// Samples nodes from a RoadSpline, converting to BeamNG world coordinates.
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
        {
            nodes.Add(new SplineNode
            {
                X = lastWorldPos.X,
                Y = lastWorldPos.Y,
                Z = lastWorldPos.Z
            });
        }
        
        return nodes;
    }
    
    /// <summary>
    /// Estimates the total path length from cross-sections.
    /// </summary>
    private static float EstimatePathLength(List<CrossSection> crossSections)
    {
        float length = 0;
        for (int i = 1; i < crossSections.Count; i++)
        {
            length += Vector2.Distance(crossSections[i - 1].CenterPoint, crossSections[i].CenterPoint);
        }
        return length;
    }
    
    /// <summary>
    /// Writes the spline file to disk with proper JSON formatting.
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
    /// Root object for the master spline JSON file.
    /// </summary>
    private class MasterSplineFile
    {
        [JsonPropertyName("linkedSplines")]
        public Dictionary<string, object> LinkedSplines { get; set; } = new();
        
        [JsonPropertyName("masterSplines")]
        public List<MasterSpline> MasterSplines { get; set; } = new();
    }
    
    /// <summary>
    /// Represents a single master spline in BeamNG format.
    /// </summary>
    private class MasterSpline
    {
        [JsonPropertyName("autoBankFalloff")]
        public double AutoBankFalloff { get; set; } = 0.6;
        
        [JsonPropertyName("bankStrength")]
        public double BankStrength { get; set; } = 0.5;
        
        [JsonPropertyName("homologationPreset")]
        public string HomologationPreset { get; set; } = "Freeway / Autobahn";
        
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        
        [JsonPropertyName("isAutoBanking")]
        public bool IsAutoBanking { get; set; } = false;
        
        [JsonPropertyName("isConformToTerrain")]
        public bool IsConformToTerrain { get; set; } = false;
        
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; } = true;
        
        [JsonPropertyName("isLoop")]
        public bool IsLoop { get; set; } = false;
        
        [JsonPropertyName("layers")]
        public Dictionary<string, object> Layers { get; set; } = new();
        
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        
        [JsonPropertyName("nmls")]
        public List<SplineNormal> Nmls { get; set; } = new();
        
        [JsonPropertyName("nodes")]
        public List<SplineNode> Nodes { get; set; } = new();
        
        [JsonPropertyName("splineAnalysisMode")]
        public int SplineAnalysisMode { get; set; } = 0;
        
        [JsonPropertyName("widths")]
        public List<float> Widths { get; set; } = new();
    }
    
    /// <summary>
    /// Represents a node position in a master spline.
    /// </summary>
    private class SplineNode
    {
        [JsonPropertyName("x")]
        public double X { get; set; }
        
        [JsonPropertyName("y")]
        public double Y { get; set; }
        
        [JsonPropertyName("z")]
        public double Z { get; set; }
    }
    
    /// <summary>
    /// Represents the normal vector at a spline node (typically pointing up).
    /// </summary>
    private class SplineNormal
    {
        [JsonPropertyName("x")]
        public double X { get; set; }
        
        [JsonPropertyName("y")]
        public double Y { get; set; }
        
        [JsonPropertyName("z")]
        public double Z { get; set; }
    }
    
    #endregion
}
