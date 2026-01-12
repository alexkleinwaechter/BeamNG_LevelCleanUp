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
    /// <param name="flipMaterialProcessingOrder">When true, reverses material order so index 0 gets highest priority.</param>
    /// <returns>A unified road network containing all splines from all materials.</returns>
    public UnifiedRoadNetwork BuildNetwork(
        List<MaterialDefinition> materials,
        float[,] heightMap,
        float metersPerPixel,
        int terrainSize,
        bool flipMaterialProcessingOrder = true)
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
        
        // Material order handling for priority calculation:
        // - Materials are listed in UI with index 0 at top
        // - For TEXTURE PAINTING: the LAST material (highest index) wins overlaps
        // - For JUNCTION HARMONIZATION: we want configurable priority
        //
        // When flipMaterialProcessingOrder is TRUE (default):
        //   - Reverse the list so index 0 becomes last processed with highest materialOrderIndex
        //   - This gives the FIRST material (top of UI list) HIGHEST priority for road smoothing
        //
        // When flipMaterialProcessingOrder is FALSE:
        //   - Process in original order, so LAST material (bottom of UI list) gets highest priority
        //   - This matches the texture painting behavior
        if (flipMaterialProcessingOrder)
        {
            roadMaterials.Reverse();
            TerrainLogger.Info($"UnifiedRoadNetworkBuilder: Material order FLIPPED - top material (index 0) gets highest priority");
        }
        else
        {
            TerrainLogger.Info($"UnifiedRoadNetworkBuilder: Material order NORMAL - bottom material gets highest priority");
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
        
        // Get spline parameters
        var splineParams = parameters.GetSplineParameters();
        var interpolationType = splineParams.SplineInterpolationType;
        
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
            
            // CRITICAL: Simplify the dense skeleton path to sparse control points like OSM has.
            // The skeleton produces pixel-by-pixel points that create jagged splines.
            // By simplifying first, we get clean waypoints that spline interpolation can smooth.
            var simplifiedPoints = SimplifyPathForSpline(worldPoints, splineParams, metersPerPixel);
            
            if (simplifiedPoints.Count < 2)
                continue;
            
            if (simplifiedPoints.Count < worldPoints.Count)
            {
                TerrainLogger.Info($"      Path simplified: {worldPoints.Count} -> {simplifiedPoints.Count} control points");
            }
            
            try
            {
                var spline = new RoadSpline(simplifiedPoints, interpolationType);
                
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
    /// Simplifies a dense skeleton path to sparse control points suitable for spline creation.
    /// This is the key to getting smooth results from PNG sources - similar to how OSM provides
    /// clean waypoints rather than dense pixel data.
    /// 
    /// The process:
    /// 1. Apply Ramer-Douglas-Peucker simplification to reduce points while preserving shape
    /// 2. Enforce minimum spacing between control points
    /// 3. Optionally apply Chaikin smoothing to reduce jaggedness in the control points
    /// </summary>
    private static List<Vector2> SimplifyPathForSpline(
        List<Vector2> densePoints,
        SplineRoadParameters splineParams,
        float metersPerPixel)
    {
        if (densePoints.Count < 3)
            return densePoints;
        
        // Step 1: Ramer-Douglas-Peucker simplification
        // Convert tolerance from pixels to meters
        var rdpTolerance = splineParams.SimplifyTolerancePixels * metersPerPixel;
        
        // Use a minimum tolerance to avoid excessive control points (at least 0.5 pixels worth)
        var effectiveTolerance = Math.Max(rdpTolerance, metersPerPixel * 0.5f);
        
        var simplified = RamerDouglasPeucker(densePoints, effectiveTolerance);
        
        // Step 2: If still very dense, enforce minimum spacing
        // Target roughly 1 control point per 5-10 meters of road for good spline quality
        var targetSpacing = 5.0f * metersPerPixel; // Minimum 5 pixels worth of spacing
        if (simplified.Count > 100)
        {
            simplified = EnforceMinimumSpacing(simplified, targetSpacing);
        }
        
        // Step 3: Apply Chaikin corner-cutting smoothing to the control points themselves
        // This pre-smooths the waypoints before spline interpolation
        // Only apply when using smooth interpolation mode (not linear)
        if (simplified.Count >= 4 && splineParams.SplineInterpolationType == SplineInterpolationType.SmoothInterpolated)
        {
            // One iteration of Chaikin smoothing on the control points
            simplified = ChaikinSmooth(simplified, 1);
        }
        
        return simplified;
    }

    /// <summary>
    /// Ramer-Douglas-Peucker line simplification algorithm.
    /// Reduces the number of points in a path while preserving overall shape.
    /// </summary>
    private static List<Vector2> RamerDouglasPeucker(List<Vector2> points, float epsilon)
    {
        if (points.Count < 3)
            return new List<Vector2>(points);
        
        // Find the point with maximum distance from the line between first and last
        float maxDistance = 0;
        int maxIndex = 0;
        
        var lineStart = points[0];
        var lineEnd = points[^1];
        
        for (int i = 1; i < points.Count - 1; i++)
        {
            var distance = PerpendicularDistance(points[i], lineStart, lineEnd);
            if (distance > maxDistance)
            {
                maxDistance = distance;
                maxIndex = i;
            }
        }
        
        // If max distance is greater than epsilon, recursively simplify
        if (maxDistance > epsilon)
        {
            var left = RamerDouglasPeucker(points.Take(maxIndex + 1).ToList(), epsilon);
            var right = RamerDouglasPeucker(points.Skip(maxIndex).ToList(), epsilon);
            
            // Combine results (avoiding duplicate point at maxIndex)
            var result = new List<Vector2>(left);
            result.AddRange(right.Skip(1));
            return result;
        }
        else
        {
            // All intermediate points can be removed
            return new List<Vector2> { lineStart, lineEnd };
        }
    }
    
    /// <summary>
    /// Calculates perpendicular distance from a point to a line segment.
    /// </summary>
    private static float PerpendicularDistance(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
    {
        var dx = lineEnd.X - lineStart.X;
        var dy = lineEnd.Y - lineStart.Y;
        
        var lineLengthSquared = dx * dx + dy * dy;
        
        if (lineLengthSquared < 0.0001f)
        {
            // Line segment is essentially a point
            return Vector2.Distance(point, lineStart);
        }
        
        // Project point onto line and find perpendicular distance
        var t = ((point.X - lineStart.X) * dx + (point.Y - lineStart.Y) * dy) / lineLengthSquared;
        t = Math.Clamp(t, 0, 1);
        
        var projectionX = lineStart.X + t * dx;
        var projectionY = lineStart.Y + t * dy;
        
        return Vector2.Distance(point, new Vector2(projectionX, projectionY));
    }
    
    /// <summary>
    /// Enforces minimum spacing between consecutive points by removing points that are too close.
    /// </summary>
    private static List<Vector2> EnforceMinimumSpacing(List<Vector2> points, float minSpacing)
    {
        if (points.Count < 3)
            return points;
        
        var result = new List<Vector2> { points[0] };
        var minSpacingSquared = minSpacing * minSpacing;
        
        for (int i = 1; i < points.Count - 1; i++)
        {
            var distSquared = Vector2.DistanceSquared(result[^1], points[i]);
            if (distSquared >= minSpacingSquared)
            {
                result.Add(points[i]);
            }
        }
        
        // Always include the last point
        result.Add(points[^1]);
        
        return result;
    }
    
    /// <summary>
    /// Chaikin corner-cutting smoothing algorithm.
    /// Creates smoother control points by iteratively cutting corners.
    /// </summary>
    private static List<Vector2> ChaikinSmooth(List<Vector2> points, int iterations)
    {
        if (points.Count < 3 || iterations <= 0)
            return points;
        
        var result = new List<Vector2>(points);
        
        for (int iter = 0; iter < iterations; iter++)
        {
            var smoothed = new List<Vector2>();
            
            // Keep the first point
            smoothed.Add(result[0]);
            
            // Apply corner cutting to intermediate segments
            for (int i = 0; i < result.Count - 1; i++)
            {
                var p0 = result[i];
                var p1 = result[i + 1];
                
                // Create two new points at 1/4 and 3/4 along the segment
                var q = new Vector2(
                    0.75f * p0.X + 0.25f * p1.X,
                    0.75f * p0.Y + 0.25f * p1.Y);
                var r = new Vector2(
                    0.25f * p0.X + 0.75f * p1.X,
                    0.25f * p0.Y + 0.75f * p1.Y);
                
                // Don't duplicate start/end points
                if (i > 0)
                    smoothed.Add(q);
                if (i < result.Count - 2)
                    smoothed.Add(r);
            }
            
            // Keep the last point
            smoothed.Add(result[^1]);
            
            result = smoothed;
        }
        
        return result;
    }

    /// <summary>
    /// Densifies control points near the start and end of a spline path.
    /// This improves junction harmonization for OSM roads by providing more control points
    /// for the blend algorithm to work with near endpoints (similar to how PNG roads have
    /// more dense control points after simplification).
    /// </summary>
    /// <param name="points">The sparse control points from OSM.</param>
    /// <param name="densifyRadius">How far from endpoints to densify (in meters).</param>
    /// <param name="targetSpacing">Target spacing between densified points (in meters).</param>
    /// <returns>Points with additional interpolated points near endpoints.</returns>
    private static List<Vector2> DensifyNearEndpoints(List<Vector2> points, float densifyRadius = 30f, float targetSpacing = 3f)
    {
        if (points.Count < 2)
            return points;
        
        var result = new List<Vector2>();
        
        // Calculate cumulative distances along the path
        var distances = new List<float> { 0f };
        for (int i = 1; i < points.Count; i++)
        {
            distances.Add(distances[^1] + Vector2.Distance(points[i - 1], points[i]));
        }
        var totalLength = distances[^1];
        
        // If the path is shorter than 2x densifyRadius, densify the whole thing
        var effectiveStartRadius = Math.Min(densifyRadius, totalLength / 2);
        var effectiveEndRadius = Math.Min(densifyRadius, totalLength / 2);
        var endThreshold = totalLength - effectiveEndRadius;
        
        // Process each segment
        for (int i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[i];
            var p1 = points[i + 1];
            var segmentStart = distances[i];
            var segmentEnd = distances[i + 1];
            var segmentLength = segmentEnd - segmentStart;
            
            // Always add the start point of the segment
            if (i == 0 || result.Count == 0 || Vector2.DistanceSquared(result[^1], p0) > 0.01f)
            {
                result.Add(p0);
            }
            
            // Check if this segment is near start or end
            var isNearStart = segmentStart < effectiveStartRadius;
            var isNearEnd = segmentEnd > endThreshold;
            
            if ((isNearStart || isNearEnd) && segmentLength > targetSpacing * 1.5f)
            {
                // Densify this segment
                var numPoints = (int)Math.Ceiling(segmentLength / targetSpacing);
                for (int j = 1; j < numPoints; j++)
                {
                    var t = (float)j / numPoints;
                    var interpolated = Vector2.Lerp(p0, p1, t);
                    result.Add(interpolated);
                }
            }
        }
        
        // Always add the last point
        result.Add(points[^1]);
        
        return result;
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
    /// For PNG-extracted splines, the normals are smoothed to reduce jaggedness from skeleton extraction.
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
            
            var crossSections = new List<UnifiedCrossSection>();
            
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
                
                crossSections.Add(crossSection);
                globalIndex++;
            }
            
            // Smooth cross-section normals to reduce bumpiness in road edges and elevation transitions.
            // Apply to ALL splines (both OSM and PNG) for consistent junction harmonization behavior.
            // Originally this was only for PNG splines, but OSM splines also benefit from smoothing.
            if (crossSections.Count >= 5)
            {
                SmoothCrossSectionNormals(crossSections);
            }
            
            // Add all cross-sections to the network
            foreach (var cs in crossSections)
            {
                network.AddCrossSection(cs);
            }
        }
        
        TerrainLogger.Info($"    Generated {network.CrossSections.Count} cross-sections from {network.Splines.Count} splines");
    }
    
    /// <summary>
    /// Smooths the normal vectors of cross-sections using a moving average filter.
    /// This reduces the jaggedness that comes from skeleton-extracted PNG paths,
    /// resulting in smoother road edges and elevation transitions.
    /// 
    /// Uses a 5-point moving average (2 before, current, 2 after) which provides
    /// good smoothing while preserving overall road direction.
    /// </summary>
    private static void SmoothCrossSectionNormals(List<UnifiedCrossSection> crossSections)
    {
        if (crossSections.Count < 5)
            return;
        
        const int windowHalfSize = 2; // 5-point moving average (2 + 1 + 2)
        
        // Store original normals
        var originalNormals = crossSections.Select(cs => cs.NormalDirection).ToList();
        var originalTangents = crossSections.Select(cs => cs.TangentDirection).ToList();
        
        // Apply moving average to normals
        for (var i = 0; i < crossSections.Count; i++)
        {
            var sumNormal = Vector2.Zero;
            var sumTangent = Vector2.Zero;
            var count = 0;
            
            for (var j = i - windowHalfSize; j <= i + windowHalfSize; j++)
            {
                if (j < 0 || j >= crossSections.Count)
                    continue;
                
                sumNormal += originalNormals[j];
                sumTangent += originalTangents[j];
                count++;
            }
            
            if (count > 0)
            {
                // Normalize the averaged vectors
                var avgNormal = sumNormal / count;
                var avgTangent = sumTangent / count;
                
                var normalLength = avgNormal.Length();
                var tangentLength = avgTangent.Length();
                
                if (normalLength > 0.001f)
                    crossSections[i].NormalDirection = avgNormal / normalLength;
                
                if (tangentLength > 0.001f)
                    crossSections[i].TangentDirection = avgTangent / tangentLength;
            }
        }
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
        
        // Densify control points near endpoints for better junction harmonization
        // OSM roads have sparse waypoints which can cause poor blending at junctions
        // This adds interpolated points near start/end to match PNG road behavior
        var densifiedCoords = DensifyNearEndpoints(uniqueCoords, densifyRadius: 30f, targetSpacing: 3f);
        
        try
        {
            return new RoadSpline(densifiedCoords, interpolationType);
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
