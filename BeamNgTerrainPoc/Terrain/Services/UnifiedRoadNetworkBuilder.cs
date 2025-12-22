using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
/// Builds a unified road network from multiple material definitions.
/// Extracts splines from each material (either from OSM or PNG layer maps),
/// assigns parameters and priorities, and creates a single unified network
/// for material-agnostic processing.
/// </summary>
public class UnifiedRoadNetworkBuilder
{
    private readonly SkeletonizationRoadExtractor _skeletonExtractor;
    private readonly OsmGeometryProcessor _osmProcessor;

    /// <summary>
    /// Minimum cross-sections required per path to be included in the network.
    /// Paths with fewer cross-sections are considered noise/fragments and skipped.
    /// </summary>
    private const int MinCrossSectionsPerPath = 10;

    public UnifiedRoadNetworkBuilder()
    {
        _skeletonExtractor = new SkeletonizationRoadExtractor();
        _osmProcessor = new OsmGeometryProcessor();
    }

    /// <summary>
    /// Builds a unified road network from all materials that have road parameters.
    /// </summary>
    /// <param name="materials">List of material definitions (only those with RoadParameters are processed).</param>
    /// <param name="heightMap">The terrain heightmap for elevation sampling.</param>
    /// <param name="metersPerPixel">Scale factor for converting between pixels and meters.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <returns>A unified road network containing all splines from all materials.</returns>
    public UnifiedRoadNetwork BuildNetwork(
        List<MaterialDefinition> materials,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSize)
    {
        var perfLog = TerrainCreationLogger.Current;
        var network = new UnifiedRoadNetwork();
        var splineIdCounter = 0;
        
        // Filter to materials with road parameters
        var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();
        
        if (roadMaterials.Count == 0)
        {
            TerrainLogger.Info("UnifiedRoadNetworkBuilder: No road materials to process");
            return network;
        }
        
        TerrainLogger.Info($"UnifiedRoadNetworkBuilder: Building network from {roadMaterials.Count} road material(s)");
        perfLog?.LogSection("UnifiedRoadNetworkBuilder");
        
        // Process each material
        for (var materialIndex = 0; materialIndex < roadMaterials.Count; materialIndex++)
        {
            var material = roadMaterials[materialIndex];
            var parameters = material.RoadParameters!;
            
            TerrainLogger.Info($"  Processing material: {material.MaterialName}");
            
            try
            {
                // Get splines from this material (either pre-built or extracted from layer)
                var splines = ExtractSplinesFromMaterial(material, parameters, metersPerPixel, terrainSize);
                
                if (splines.Count == 0)
                {
                    TerrainLogger.Warning($"    No splines extracted for material: {material.MaterialName}");
                    continue;
                }
                
                TerrainLogger.Info($"    Extracted {splines.Count} spline(s)");
                
                // Convert to ParameterizedRoadSpline and add to network
                foreach (var spline in splines)
                {
                    // Determine OSM road type if available
                    string? osmRoadType = null;
                    string? displayName = null;
                    
                    // For OSM-based materials, we could extract road type from the spline
                    // This would require additional metadata to be passed through
                    // For now, we use width-based priority as fallback
                    
                    var paramSpline = new ParameterizedRoadSpline
                    {
                        Spline = spline,
                        Parameters = parameters,
                        MaterialName = material.MaterialName,
                        SplineId = splineIdCounter,
                        OsmRoadType = osmRoadType,
                        DisplayName = displayName
                    };
                    
                    // Calculate priority using the cascade:
                    // 1. OSM road type (if available)
                    // 2. Road width
                    // 3. Material order (earlier = higher priority)
                    paramSpline.Priority = paramSpline.CalculateEffectivePriority(materialIndex);
                    
                    network.AddSpline(paramSpline);
                    splineIdCounter++;
                }
                
                TerrainLogger.Info($"    Added {splines.Count} spline(s) with priority range " +
                                  $"[{network.Splines.Where(s => s.MaterialName == material.MaterialName).Min(s => s.Priority)}-" +
                                  $"{network.Splines.Where(s => s.MaterialName == material.MaterialName).Max(s => s.Priority)}]");
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"    Error processing material '{material.MaterialName}': {ex.Message}");
            }
        }
        
        // Generate cross-sections for all splines
        if (network.Splines.Count > 0)
        {
            GenerateCrossSections(network, metersPerPixel);
        }
        
        // Log network statistics
        var stats = network.GetStatistics();
        TerrainLogger.Info(stats.ToString());
        
        return network;
    }

    /// <summary>
    /// Builds a unified road network with OSM road type information preserved.
    /// Use this overload when you have OSM feature metadata to preserve road classifications.
    /// </summary>
    /// <param name="materials">List of material definitions.</param>
    /// <param name="osmRoadTypes">Dictionary mapping material name to list of OSM road types for each spline.</param>
    /// <param name="heightMap">The terrain heightmap.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <returns>A unified road network with OSM road type priorities.</returns>
    public UnifiedRoadNetwork BuildNetworkWithOsmMetadata(
        List<MaterialDefinition> materials,
        Dictionary<string, List<string?>> osmRoadTypes,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSize)
    {
        var network = new UnifiedRoadNetwork();
        var splineIdCounter = 0;
        
        var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();
        
        if (roadMaterials.Count == 0)
        {
            return network;
        }
        
        TerrainLogger.Info($"UnifiedRoadNetworkBuilder: Building network with OSM metadata from {roadMaterials.Count} material(s)");
        
        for (var materialIndex = 0; materialIndex < roadMaterials.Count; materialIndex++)
        {
            var material = roadMaterials[materialIndex];
            var parameters = material.RoadParameters!;
            
            try
            {
                var splines = ExtractSplinesFromMaterial(material, parameters, metersPerPixel, terrainSize);
                
                // Get OSM road types for this material if available
                var roadTypesForMaterial = osmRoadTypes.GetValueOrDefault(material.MaterialName);
                
                for (var splineIndex = 0; splineIndex < splines.Count; splineIndex++)
                {
                    var spline = splines[splineIndex];
                    
                    // Get OSM road type if available
                    string? osmRoadType = null;
                    if (roadTypesForMaterial != null && splineIndex < roadTypesForMaterial.Count)
                    {
                        osmRoadType = roadTypesForMaterial[splineIndex];
                    }
                    
                    var paramSpline = new ParameterizedRoadSpline
                    {
                        Spline = spline,
                        Parameters = parameters,
                        MaterialName = material.MaterialName,
                        SplineId = splineIdCounter,
                        OsmRoadType = osmRoadType
                    };
                    
                    paramSpline.Priority = paramSpline.CalculateEffectivePriority(materialIndex);
                    
                    network.AddSpline(paramSpline);
                    splineIdCounter++;
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"Error processing material '{material.MaterialName}': {ex.Message}");
            }
        }
        
        if (network.Splines.Count > 0)
        {
            GenerateCrossSections(network, metersPerPixel);
        }
        
        return network;
    }

    /// <summary>
    /// Extracts road splines from a material definition.
    /// Uses pre-built splines if available (OSM), otherwise extracts from layer image (PNG).
    /// </summary>
    private List<RoadSpline> ExtractSplinesFromMaterial(
        MaterialDefinition material,
        RoadSmoothingParameters parameters,
        float metersPerPixel,
        int terrainSize)
    {
        // Check for pre-built splines first (from OSM)
        if (parameters.UsePreBuiltSplines)
        {
            TerrainLogger.Info($"    Using {parameters.PreBuiltSplines!.Count} pre-built splines from OSM");
            return FilterShortSplines(parameters.PreBuiltSplines!, parameters.CrossSectionIntervalMeters);
        }
        
        // Fall back to extracting from layer image
        if (string.IsNullOrEmpty(material.LayerImagePath))
        {
            TerrainLogger.Warning($"    Material '{material.MaterialName}' has no layer image path");
            return [];
        }
        
        // Load and process layer image
        var roadLayer = LoadLayerImage(material.LayerImagePath, terrainSize);
        
        return ExtractSplinesFromLayerImage(roadLayer, parameters, metersPerPixel);
    }

    /// <summary>
    /// Extracts road splines from a binary layer image using skeleton extraction.
    /// </summary>
    private List<RoadSpline> ExtractSplinesFromLayerImage(
        byte[,] roadLayer,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var splines = new List<RoadSpline>();
        
        // Get spline interpolation type from parameters
        var interpolationType = parameters.GetSplineParameters().SplineInterpolationType;
        
        // Extract centerline paths using skeletonization
        var centerlinePathsPixels = _skeletonExtractor.ExtractCenterlinePaths(roadLayer, parameters);
        
        if (centerlinePathsPixels.Count == 0)
        {
            return splines;
        }
        
        TerrainLogger.Info($"    Extracted {centerlinePathsPixels.Count} skeleton path(s)");
        
        // Merge broken curves
        var mergedPathPixels = MergeBrokenCurves(centerlinePathsPixels, roadLayer, parameters);
        
        TerrainLogger.Info($"    After merging: {mergedPathPixels.Count} path(s)");
        
        // Convert to splines
        foreach (var pathPixels in mergedPathPixels)
        {
            if (pathPixels.Count < 2)
                continue;
            
            // Convert to world coordinates (meters)
            var worldPoints = pathPixels
                .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
                .ToList();
            
            try
            {
                var spline = new RoadSpline(worldPoints, interpolationType);
                
                // Filter short paths
                var estimatedCrossSections = (int)(spline.TotalLength / parameters.CrossSectionIntervalMeters);
                if (estimatedCrossSections >= MinCrossSectionsPerPath)
                {
                    splines.Add(spline);
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"    Failed to create spline: {ex.Message}");
            }
        }
        
        return splines;
    }

    /// <summary>
    /// Filters out splines that are too short to generate meaningful cross-sections.
    /// </summary>
    private List<RoadSpline> FilterShortSplines(List<RoadSpline> splines, float crossSectionInterval)
    {
        return splines
            .Where(s =>
            {
                var estimatedCrossSections = (int)(s.TotalLength / crossSectionInterval);
                return estimatedCrossSections >= MinCrossSectionsPerPath;
            })
            .ToList();
    }

    /// <summary>
    /// Generates cross-sections for all splines in the network.
    /// Cross-sections are sampled along each spline at the interval specified in its parameters.
    /// </summary>
    private void GenerateCrossSections(UnifiedRoadNetwork network, float metersPerPixel)
    {
        var globalIndex = 0;
        
        // Sort splines by priority (highest first) to ensure deterministic ordering
        var orderedSplines = network.Splines.OrderByDescending(s => s.Priority).ToList();
        
        foreach (var paramSpline in orderedSplines)
        {
            var parameters = paramSpline.Parameters;
            var spline = paramSpline.Spline;
            
            // Sample the spline at regular intervals
            var samples = spline.SampleByDistance(parameters.CrossSectionIntervalMeters);
            
            for (var localIndex = 0; localIndex < samples.Count; localIndex++)
            {
                var sample = samples[localIndex];
                
                var crossSection = UnifiedCrossSection.FromSplineSample(
                    sample,
                    paramSpline,
                    globalIndex,
                    localIndex);
                
                // Mark start/end cross-sections
                crossSection.IsSplineStart = localIndex == 0;
                crossSection.IsSplineEnd = localIndex == samples.Count - 1;
                
                network.AddCrossSection(crossSection);
                globalIndex++;
            }
        }
        
        TerrainLogger.Info($"    Generated {network.CrossSections.Count} cross-sections from {network.Splines.Count} splines");
    }

    /// <summary>
    /// Loads a layer image from disk.
    /// </summary>
    private byte[,] LoadLayerImage(string layerPath, int expectedSize)
    {
        using var image = Image.Load<L8>(layerPath);
        
        if (image.Width != expectedSize || image.Height != expectedSize)
        {
            throw new InvalidOperationException(
                $"Layer image size ({image.Width}x{image.Height}) does not match terrain size ({expectedSize}x{expectedSize})");
        }
        
        var layer = new byte[expectedSize, expectedSize];
        
        for (var y = 0; y < expectedSize; y++)
        {
            for (var x = 0; x < expectedSize; x++)
            {
                // Flip Y axis (image Y=0 at top, terrain Y=0 at bottom)
                var flippedY = expectedSize - 1 - y;
                layer[flippedY, x] = image[x, y].PackedValue;
            }
        }
        
        return layer;
    }

    /// <summary>
    /// Merges broken curves that should be continuous.
    /// Reuses the algorithm from MedialAxisRoadExtractor.
    /// </summary>
    private List<List<Vector2>> MergeBrokenCurves(
        List<List<Vector2>> rawPaths,
        byte[,] roadMask,
        RoadSmoothingParameters parameters)
    {
        if (rawPaths.Count <= 1)
            return rawPaths;
        
        var sp = parameters.GetSplineParameters();
        var maxGap = (int)Math.Max(2, Math.Round(sp.BridgeEndpointMaxDistancePixels));
        var maxAngleDeg = Math.Max(10f, sp.JunctionAngleThreshold);
        
        // Main merge loop
        var paths = rawPaths.Select(p => p.ToList()).ToList();
        bool merged;
        var pass = 0;
        
        do
        {
            pass++;
            merged = false;
            
            for (var i = 0; i < paths.Count && !merged; i++)
            {
                for (var j = i + 1; j < paths.Count && !merged; j++)
                {
                    var a = paths[i];
                    var b = paths[j];
                    
                    if (TryMerge(a, b, maxGap, maxAngleDeg, roadMask, out var mergedPath) ||
                        TryMerge(a, Reverse(b), maxGap, maxAngleDeg, roadMask, out mergedPath) ||
                        TryMerge(Reverse(a), b, maxGap, maxAngleDeg, roadMask, out mergedPath) ||
                        TryMerge(Reverse(a), Reverse(b), maxGap, maxAngleDeg, roadMask, out mergedPath))
                    {
                        paths[i] = mergedPath;
                        paths.RemoveAt(j);
                        merged = true;
                    }
                }
            }
        } while (merged && paths.Count > 1);
        
        if (pass > 1)
        {
            TerrainLogger.Info($"    Path merging: {pass - 1} merge(s), remaining {paths.Count} paths");
        }
        
        return paths;
    }

    private static List<Vector2> Reverse(List<Vector2> p)
    {
        var r = new List<Vector2>(p);
        r.Reverse();
        return r;
    }

    private bool TryMerge(
        List<Vector2> a,
        List<Vector2> b,
        int maxGap,
        float maxAngleDeg,
        byte[,] roadMask,
        out List<Vector2> merged)
    {
        merged = null!;
        if (a.Count == 0 || b.Count == 0) return false;
        
        var aEnd = a[^1];
        var bStart = b[0];
        
        // Proximity check
        var dist = Vector2.Distance(aEnd, bStart);
        if (dist > maxGap) return false;
        
        // Direction continuity check
        var aDir = TangentAtEnd(a, true);
        var bDir = TangentAtEnd(b, false);
        if (aDir.Length() < 1e-3f || bDir.Length() < 1e-3f) return false;
        
        var ang = AngleBetween(aDir, bDir);
        if (ang > maxAngleDeg) return false;
        
        // Connectivity check through road mask
        if (!IsBridgeInsideRoadMask(aEnd, bStart, roadMask)) return false;
        
        // Merge paths
        merged = new List<Vector2>(a.Count + b.Count - 1);
        merged.AddRange(a);
        if (b.Count > 0)
            merged.AddRange(b.Skip(1));
        return true;
    }

    private static Vector2 TangentAtEnd(List<Vector2> p, bool atEnd)
    {
        if (p.Count < 2) return Vector2.Zero;
        
        if (atEnd)
        {
            var a = p[Math.Max(0, p.Count - 3)];
            var b = p[^1];
            return new Vector2(b.X - a.X, b.Y - a.Y);
        }
        else
        {
            var a = p[0];
            var b = p[Math.Min(p.Count - 1, 2)];
            return new Vector2(b.X - a.X, b.Y - a.Y);
        }
    }

    private static float AngleBetween(Vector2 v1, Vector2 v2)
    {
        v1 = Vector2.Normalize(v1);
        v2 = Vector2.Normalize(v2);
        var dot = Math.Clamp(Vector2.Dot(v1, v2), -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    private static bool IsBridgeInsideRoadMask(Vector2 a, Vector2 b, byte[,] mask)
    {
        var w = mask.GetLength(1);
        var h = mask.GetLength(0);
        int x0 = (int)a.X, y0 = (int)a.Y, x1 = (int)b.X, y1 = (int)b.Y;
        
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        
        int total = 0, inside = 0;
        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                total++;
                if (mask[y0, x0] > 0) inside++;
            }
            
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
        
        return total == 0 ? false : inside / (float)total >= 0.6f;
    }

    /// <summary>
    /// Calculates priority from road width when OSM type is not available.
    /// Uses a linear scale where wider roads have higher priority.
    /// </summary>
    /// <param name="roadWidthMeters">Road width in meters.</param>
    /// <returns>Priority value (typically 10-100).</returns>
    public static int CalculateWidthBasedPriority(float roadWidthMeters)
    {
        return ParameterizedRoadSpline.GetWidthBasedPriority(roadWidthMeters);
    }

    /// <summary>
    /// Builds a unified road network from materials with OSM feature data.
    /// This overload extracts OSM road types from the features for proper priority calculation.
    /// </summary>
    /// <param name="materials">List of material definitions with OSM layer sources.</param>
    /// <param name="osmQueryResult">The OSM query result containing all features.</param>
    /// <param name="bbox">Geographic bounding box for coordinate transformation.</param>
    /// <param name="heightMap">The terrain heightmap.</param>
    /// <param name="metersPerPixel">Scale factor.</param>
    /// <param name="terrainSize">Terrain size in pixels.</param>
    /// <returns>A unified road network with OSM road type priorities.</returns>
    public UnifiedRoadNetwork BuildNetworkFromOsmFeatures(
        List<MaterialDefinition> materials,
        OsmQueryResult osmQueryResult,
        GeoBoundingBox bbox,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSize)
    {
        var network = new UnifiedRoadNetwork();
        var splineIdCounter = 0;
        
        var roadMaterials = materials.Where(m => m.RoadParameters != null).ToList();
        
        if (roadMaterials.Count == 0)
        {
            TerrainLogger.Info("UnifiedRoadNetworkBuilder: No road materials to process");
            return network;
        }
        
        TerrainLogger.Info($"UnifiedRoadNetworkBuilder: Building network from OSM features for {roadMaterials.Count} material(s)");
        
        for (var materialIndex = 0; materialIndex < roadMaterials.Count; materialIndex++)
        {
            var material = roadMaterials[materialIndex];
            var parameters = material.RoadParameters!;
            
            TerrainLogger.Info($"  Processing material: {material.MaterialName}");
            
            try
            {
                // Check if material uses OSM layer source
                if (material.LayerSource?.SourceType == LayerSourceType.OsmFeatures &&
                    material.LayerSource.SelectedOsmFeatures?.Count > 0)
                {
                    // Get the full features from the query result
                    var selectedFeatureIds = material.LayerSource.SelectedOsmFeatures.Select(s => s.FeatureId).ToHashSet();
                    var lineFeatures = osmQueryResult.Features
                        .Where(f => selectedFeatureIds.Contains(f.Id) && f.GeometryType == OsmGeometryType.LineString)
                        .ToList();
                    
                    if (lineFeatures.Count == 0)
                    {
                        TerrainLogger.Warning($"    No line features found for material: {material.MaterialName}");
                        continue;
                    }
                    
                    TerrainLogger.Info($"    Found {lineFeatures.Count} OSM line feature(s)");
                    
                    // Get spline interpolation type from parameters
                    var interpolationType = parameters.GetSplineParameters().SplineInterpolationType;
                    
                    // Convert each OSM feature to a spline with its road type
                    foreach (var feature in lineFeatures)
                    {
                        var spline = ConvertFeatureToSpline(feature, bbox, terrainSize, metersPerPixel, interpolationType);
                        if (spline == null) continue;
                        
                        // Filter short splines
                        var estimatedCrossSections = (int)(spline.TotalLength / parameters.CrossSectionIntervalMeters);
                        if (estimatedCrossSections < MinCrossSectionsPerPath)
                        {
                            continue;
                        }
                        
                        // Extract OSM road type from feature tags
                        string? osmRoadType = null;
                        if (feature.Tags.TryGetValue("highway", out var highwayType))
                        {
                            osmRoadType = highwayType;
                        }
                        
                        var paramSpline = new ParameterizedRoadSpline
                        {
                            Spline = spline,
                            Parameters = parameters,
                            MaterialName = material.MaterialName,
                            SplineId = splineIdCounter,
                            OsmRoadType = osmRoadType,
                            DisplayName = feature.DisplayName
                        };
                        
                        paramSpline.Priority = paramSpline.CalculateEffectivePriority(materialIndex);
                        
                        network.AddSpline(paramSpline);
                        splineIdCounter++;
                    }
                    
                    var splineCount = network.Splines.Count(s => s.MaterialName == material.MaterialName);
                    if (splineCount > 0)
                    {
                        var priorities = network.Splines.Where(s => s.MaterialName == material.MaterialName);
                        TerrainLogger.Info($"    Added {splineCount} spline(s) with priority range " +
                                          $"[{priorities.Min(s => s.Priority)}-{priorities.Max(s => s.Priority)}]");
                    }
                }
                else
                {
                    // Fall back to standard extraction (PNG or pre-built splines)
                    var splines = ExtractSplinesFromMaterial(material, parameters, metersPerPixel, terrainSize);
                    
                    if (splines.Count == 0)
                    {
                        TerrainLogger.Warning($"    No splines extracted for material: {material.MaterialName}");
                        continue;
                    }
                    
                    foreach (var spline in splines)
                    {
                        var paramSpline = new ParameterizedRoadSpline
                        {
                            Spline = spline,
                            Parameters = parameters,
                            MaterialName = material.MaterialName,
                            SplineId = splineIdCounter
                        };
                        
                        paramSpline.Priority = paramSpline.CalculateEffectivePriority(materialIndex);
                        
                        network.AddSpline(paramSpline);
                        splineIdCounter++;
                    }
                    
                    TerrainLogger.Info($"    Added {splines.Count} spline(s) from PNG/pre-built source");
                }
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"    Error processing material '{material.MaterialName}': {ex.Message}");
            }
        }
        
        // Generate cross-sections for all splines
        if (network.Splines.Count > 0)
        {
            GenerateCrossSections(network, metersPerPixel);
        }
        
        // Log network statistics
        var stats = network.GetStatistics();
        TerrainLogger.Info(stats.ToString());
        
        return network;
    }

    /// <summary>
    /// Converts a single OSM feature to a RoadSpline.
    /// </summary>
    private RoadSpline? ConvertFeatureToSpline(
        OsmFeature feature,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType = SplineInterpolationType.SmoothInterpolated)
    {
        if (feature.Coordinates.Count < 2)
            return null;
        
        // Transform coordinates to terrain space, then to meters
        var terrainCoords = feature.Coordinates
            .Select(c => _osmProcessor.TransformToTerrainCoordinate(c, bbox, terrainSize))
            .ToList();
        
        // Crop to terrain bounds
        var croppedCoords = _osmProcessor.CropLineToTerrain(terrainCoords, terrainSize);
        
        if (croppedCoords.Count < 2)
            return null;
        
        // Convert to meter coordinates
        var meterCoords = croppedCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();
        
        // Remove duplicate consecutive points
        var uniqueCoords = RemoveDuplicateConsecutivePoints(meterCoords, 0.01f);
        
        if (uniqueCoords.Count < 2)
            return null;
        
        try
        {
            return new RoadSpline(uniqueCoords, interpolationType);
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to create spline from OSM feature {feature.Id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Removes consecutive duplicate points from a path.
    /// </summary>
    private static List<Vector2> RemoveDuplicateConsecutivePoints(List<Vector2> points, float tolerance)
    {
        if (points.Count < 2)
            return points;
        
        var result = new List<Vector2> { points[0] };
        var toleranceSquared = tolerance * tolerance;
        
        for (int i = 1; i < points.Count; i++)
        {
            var distSquared = Vector2.DistanceSquared(result[^1], points[i]);
            if (distSquared > toleranceSquared)
            {
                result.Add(points[i]);
            }
        }
        
        return result;
    }
}
