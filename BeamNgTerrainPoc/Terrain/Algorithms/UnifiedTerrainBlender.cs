using System.Diagnostics;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Single-pass terrain blender for the unified road network.
///     Key improvements over per-material blending:
///     1. Single EDT computation for the entire road network (faster)
///     2. Road core pixels are PROTECTED - never modified by neighbor's blend zone
///     3. Per-spline blend ranges are respected
///     4. Overlapping blend zones use smooth interpolation between both splines
///     This eliminates the problem where sequential material processing would
///     overwrite previously smoothed road surfaces.
/// </summary>
public class UnifiedTerrainBlender
{
    /// <summary>
    ///     Spatial index cell size in meters for elevation map building.
    /// </summary>
    private const int SpatialIndexCellSize = 32;

    /// <summary>
    ///     Buffer added to road width for protection mask (in meters).
    ///     This ensures the protection extends slightly beyond the nominal road edge
    ///     to account for rasterization precision and prevent edge artifacts.
    ///     A value of 0.5-1.0m typically covers sub-pixel inaccuracies.
    /// </summary>
    private const float ProtectionMaskBufferMeters = 1.0f;

    /// <summary>
    ///     Distance field from the last blend operation (for post-processing).
    /// </summary>
    private float[,]? _lastDistanceField;

    /// <summary>
    ///     Gets the last computed distance field for reuse in post-processing.
    /// </summary>
    /// <exception cref="InvalidOperationException">If no distance field has been computed yet.</exception>
    public float[,] GetLastDistanceField()
    {
        if (_lastDistanceField == null)
            throw new InvalidOperationException(
                "No distance field has been computed yet. Call BlendNetworkWithTerrain first.");
        return _lastDistanceField;
    }

    /// <summary>
    ///     Blends the unified road network with the terrain using a single-pass protected algorithm.
    ///     Algorithm:
    ///     1. Build COMBINED road core mask from ALL splines (for EDT)
    ///     2. Build road core PROTECTION mask with ownership (filled polygons tracking which spline owns each pixel)
    ///     3. Compute SINGLE global EDT from combined mask
    ///     4. Build elevation map with per-pixel source spline tracking (respecting protection mask ownership)
    ///     5. Apply protected blending (ANY road core pixel is NEVER modified by ANY blend zone)
    /// </summary>
    /// <param name="originalHeightMap">The original terrain heightmap.</param>
    /// <param name="network">The unified road network with harmonized elevations.</param>
    /// <param name="metersPerPixel">Scale factor for converting meters to pixels.</param>
    /// <returns>The blended heightmap.</returns>
    public float[,] BlendNetworkWithTerrain(
        float[,] originalHeightMap,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        var perfLog = TerrainCreationLogger.Current;
        var totalSw = Stopwatch.StartNew();

        if (network.CrossSections.Count == 0)
        {
            TerrainLogger.Info("UnifiedTerrainBlender: No cross-sections to blend");
            return (float[,])originalHeightMap.Clone();
        }

        var height = originalHeightMap.GetLength(0);
        var width = originalHeightMap.GetLength(1);

        TerrainLogger.Info("=== UNIFIED TERRAIN BLENDING ===");
        TerrainLogger.Info($"  Network: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections");
        TerrainLogger.Info($"  Terrain: {width}x{height} pixels, {metersPerPixel}m/pixel");

        // Step 1: Build COMBINED road core mask from ALL splines (for EDT)
        TerrainLogger.Info("Step 1: Building combined road core mask...");
        var sw = Stopwatch.StartNew();
        var combinedCoreMask = BuildCombinedRoadCoreMask(network, width, height, metersPerPixel);
        perfLog?.Timing($"  BuildCombinedRoadCoreMask: {sw.ElapsedMilliseconds}ms");

        // Step 2: Build road core PROTECTION mask with ownership (tracks which spline owns each road core pixel)
        TerrainLogger.Info("Step 2: Building road core protection mask with ownership...");
        sw.Restart();
        var (protectionMask, coreOwnershipMap, coreElevationMap) = BuildRoadCoreProtectionMaskWithOwnership(
            network, width, height, metersPerPixel);
        perfLog?.Timing($"  BuildRoadCoreProtectionMaskWithOwnership: {sw.ElapsedMilliseconds}ms");

        // Step 3: Compute SINGLE global EDT from combined mask
        TerrainLogger.Info("Step 3: Computing global distance field (EDT)...");
        sw.Restart();
        var distanceField = ComputeDistanceField(combinedCoreMask, metersPerPixel);
        _lastDistanceField = distanceField;
        perfLog?.Timing($"  ComputeDistanceField: {sw.ElapsedMilliseconds}ms");

        // Step 4: Build elevation map with per-pixel source spline tracking (respecting core ownership)
        TerrainLogger.Info("Step 4: Building elevation map with ownership...");
        sw.Restart();
        var (elevationMap, splineOwnerMap, maxBlendRangeMap) = BuildElevationMapWithOwnership(
            network, width, height, metersPerPixel, protectionMask, coreOwnershipMap, coreElevationMap);
        perfLog?.Timing($"  BuildElevationMapWithOwnership: {sw.ElapsedMilliseconds}ms");

        // Step 5: Apply protected blending
        TerrainLogger.Info("Step 5: Applying protected blending...");
        sw.Restart();
        var result = ApplyProtectedBlending(
            originalHeightMap,
            distanceField,
            elevationMap,
            splineOwnerMap,
            maxBlendRangeMap,
            protectionMask,
            network,
            metersPerPixel);
        perfLog?.Timing($"  ApplyProtectedBlending: {sw.ElapsedMilliseconds}ms");

        totalSw.Stop();
        perfLog?.Timing($"UnifiedTerrainBlender TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s");
        TerrainLogger.Info("=== UNIFIED TERRAIN BLENDING COMPLETE ===");

        return result;
    }

    /// <summary>
    ///     Builds a combined road core mask from all splines in the network.
    ///     White pixels (255) indicate road core areas.
    /// </summary>
    private byte[,] BuildCombinedRoadCoreMask(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel)
    {
        var mask = new byte[height, width];
        var sectionsProcessed = 0;

        foreach (var cs in network.CrossSections.Where(s => !s.IsExcluded))
        {
            // Skip cross-sections with invalid elevations
            if (float.IsNaN(cs.TargetElevation) || float.IsInfinity(cs.TargetElevation))
                continue;

            // Get road half-width for this cross-section
            var halfWidth = cs.EffectiveRoadWidth / 2.0f;

            // Rasterize cross-section line (left to right edge)
            var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
            var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

            var x0 = (int)(left.X / metersPerPixel);
            var y0 = (int)(left.Y / metersPerPixel);
            var x1 = (int)(right.X / metersPerPixel);
            var y1 = (int)(right.Y / metersPerPixel);

            DrawLine(mask, x0, y0, x1, y1, width, height);
            sectionsProcessed++;
        }

        TerrainLogger.Info($"  Rasterized {sectionsProcessed} cross-sections into combined road mask");
        return mask;
    }

    /// <summary>
    ///     Builds a protection mask that marks ALL road core pixels from ALL splines,
    ///     along with ownership (which spline owns each pixel) and elevation data.
    ///     This is the KEY fix for the junction problem: When roads overlap at tight angles,
    ///     we need to know definitively which road owns each pixel based on the actual
    ///     road geometry, not just "nearest cross-section".
    ///     Priority rules for overlapping road cores:
    ///     1. Higher priority spline wins
    ///     2. If equal priority, first one processed wins (stable ordering)
    /// </summary>
    private (bool[,] protectionMask, int[,] ownershipMap, float[,] elevationMap)
        BuildRoadCoreProtectionMaskWithOwnership(
            UnifiedRoadNetwork network,
            int width,
            int height,
            float metersPerPixel)
    {
        var protectionMask = new bool[height, width];
        var ownershipMap = new int[height, width];
        var elevationMap = new float[height, width];
        var priorityMap = new int[height, width]; // Track priority for conflict resolution

        // Initialize ownership to -1 (no owner)
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            ownershipMap[y, x] = -1;
            elevationMap[y, x] = float.NaN;
            priorityMap[y, x] = int.MinValue;
        }

        var protectedPixels = 0;
        var overwrittenByPriority = 0;

        // Group cross-sections by spline for efficient processing
        var crossSectionsBySpline = network.CrossSections
            .Where(cs => !cs.IsExcluded && IsValidTargetElevation(cs.TargetElevation))
            .GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

        // Get spline priority lookup
        var splinePriority = network.Splines.ToDictionary(s => s.SplineId, s => s.Priority);

        foreach (var (splineId, crossSections) in crossSectionsBySpline)
        {
            if (crossSections.Count < 2)
                continue;

            var priority = splinePriority.GetValueOrDefault(splineId, 0);

            // For each consecutive pair of cross-sections, fill the road core polygon
            for (var i = 0; i < crossSections.Count - 1; i++)
            {
                var cs1 = crossSections[i];
                var cs2 = crossSections[i + 1];

                // Add buffer to road width for protection mask to prevent edge artifacts
                // This ensures protection extends slightly beyond the nominal road edge
                var halfWidth1 = cs1.EffectiveRoadWidth / 2.0f + ProtectionMaskBufferMeters;
                var halfWidth2 = cs2.EffectiveRoadWidth / 2.0f + ProtectionMaskBufferMeters;

                // Calculate the four corners of the road segment polygon
                var left1 = cs1.CenterPoint - cs1.NormalDirection * halfWidth1;
                var right1 = cs1.CenterPoint + cs1.NormalDirection * halfWidth1;
                var left2 = cs2.CenterPoint - cs2.NormalDirection * halfWidth2;
                var right2 = cs2.CenterPoint + cs2.NormalDirection * halfWidth2;

                // Convert to pixel coordinates
                var corners = new[]
                {
                    new Vector2(left1.X / metersPerPixel, left1.Y / metersPerPixel),
                    new Vector2(right1.X / metersPerPixel, right1.Y / metersPerPixel),
                    new Vector2(right2.X / metersPerPixel, right2.Y / metersPerPixel),
                    new Vector2(left2.X / metersPerPixel, left2.Y / metersPerPixel)
                };

                // Average elevation for this segment
                var segmentElevation = (cs1.TargetElevation + cs2.TargetElevation) / 2.0f;

                // Fill the polygon with ownership tracking
                var (filled, overwritten) = FillConvexPolygonWithOwnership(
                    protectionMask, ownershipMap, elevationMap, priorityMap,
                    corners, width, height,
                    splineId, priority, segmentElevation);

                protectedPixels += filled;
                overwrittenByPriority += overwritten;
            }
        }

        TerrainLogger.Info($"  Protection mask: {protectedPixels:N0} road core pixels protected");
        if (overwrittenByPriority > 0)
            TerrainLogger.Info(
                $"  Priority resolution: {overwrittenByPriority:N0} pixels assigned to higher-priority roads");

        return (protectionMask, ownershipMap, elevationMap);
    }

    /// <summary>
    ///     Fills a convex polygon with ownership tracking.
    ///     Higher priority splines overwrite lower priority ones.
    ///     Returns (newPixelsFilled, pixelsOverwrittenByPriority).
    /// </summary>
    private static (int filled, int overwritten) FillConvexPolygonWithOwnership(
        bool[,] protectionMask,
        int[,] ownershipMap,
        float[,] elevationMap,
        int[,] priorityMap,
        Vector2[] corners,
        int width,
        int height,
        int splineId,
        int priority,
        float elevation)
    {
        var filledCount = 0;
        var overwrittenCount = 0;

        // Find bounding box
        var minY = (int)MathF.Floor(corners.Min(c => c.Y));
        var maxY = (int)MathF.Ceiling(corners.Max(c => c.Y));
        var minX = (int)MathF.Floor(corners.Min(c => c.X));
        var maxX = (int)MathF.Ceiling(corners.Max(c => c.X));

        // Clamp to image bounds
        minY = Math.Max(0, minY);
        maxY = Math.Min(height - 1, maxY);
        minX = Math.Max(0, minX);
        maxX = Math.Min(width - 1, maxX);

        // Scanline fill using point-in-polygon test
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            if (!IsPointInConvexPolygon(new Vector2(x, y), corners))
                continue;

            // Check if we should claim this pixel
            if (!protectionMask[y, x])
            {
                // New pixel - claim it
                protectionMask[y, x] = true;
                ownershipMap[y, x] = splineId;
                elevationMap[y, x] = elevation;
                priorityMap[y, x] = priority;
                filledCount++;
            }
            else if (priority > priorityMap[y, x])
            {
                // Pixel already claimed, but we have higher priority - overwrite
                ownershipMap[y, x] = splineId;
                elevationMap[y, x] = elevation;
                priorityMap[y, x] = priority;
                overwrittenCount++;
            }
            // else: pixel claimed by equal or higher priority road - leave it
        }

        return (filledCount, overwrittenCount);
    }

    /// <summary>
    ///     Legacy method - replaced by BuildRoadCoreProtectionMaskWithOwnership.
    ///     Kept for reference but not used.
    /// </summary>
    [Obsolete("Use BuildRoadCoreProtectionMaskWithOwnership instead")]
    private bool[,] BuildRoadCoreProtectionMask(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel)
    {
        var (protectionMask, _, _) = BuildRoadCoreProtectionMaskWithOwnership(network, width, height, metersPerPixel);
        return protectionMask;
    }

    /// <summary>
    ///     Fills a convex polygon (quad) using scanline algorithm.
    ///     Returns the number of pixels filled.
    ///     This is the simple version without ownership tracking.
    /// </summary>
    private static int FillConvexPolygon(bool[,] mask, Vector2[] corners, int width, int height)
    {
        var filledCount = 0;

        // Find bounding box
        var minY = (int)MathF.Floor(corners.Min(c => c.Y));
        var maxY = (int)MathF.Ceiling(corners.Max(c => c.Y));
        var minX = (int)MathF.Floor(corners.Min(c => c.X));
        var maxX = (int)MathF.Ceiling(corners.Max(c => c.X));

        // Clamp to image bounds
        minY = Math.Max(0, minY);
        maxY = Math.Min(height - 1, maxY);
        minX = Math.Max(0, minX);
        maxX = Math.Min(width - 1, maxX);

        // Scanline fill using point-in-polygon test
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (!mask[y, x] && IsPointInConvexPolygon(new Vector2(x, y), corners))
            {
                mask[y, x] = true;
                filledCount++;
            }

        return filledCount;
    }

    /// <summary>
    ///     Tests if a point is inside a convex polygon using cross product method.
    /// </summary>
    private static bool IsPointInConvexPolygon(Vector2 point, Vector2[] polygon)
    {
        var n = polygon.Length;
        var sign = 0;

        for (var i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];

            // Cross product of (b - a) and (point - a)
            var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);

            if (cross != 0)
            {
                var currentSign = cross > 0 ? 1 : -1;
                if (sign == 0)
                    sign = currentSign;
                else if (sign != currentSign)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Bresenham line rasterization with bounds checking.
    /// </summary>
    private static void DrawLine(byte[,] mask, int x0, int y0, int x1, int y1, int width, int height)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                mask[y0, x0] = 255;

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
    ///     Computes exact Euclidean distance field using Felzenszwalb &amp; Huttenlocher algorithm.
    ///     O(W*H) complexity.
    /// </summary>
    private float[,] ComputeDistanceField(byte[,] mask, float metersPerPixel)
    {
        var h = mask.GetLength(0);
        var w = mask.GetLength(1);
        var dist = new float[h, w];
        const float INF = 1e12f;

        // Initialize: 0 for road pixels, INF for non-road
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            dist[y, x] = mask[y, x] > 0 ? 0f : INF;

        // 1D EDT per row
        var f = new float[w];
        var v = new int[w];
        var z = new float[w + 1];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) f[x] = dist[y, x];

            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (var q = 1; q < w; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
                    if (s <= z[k])
                    {
                        k--;
                        if (k < 0)
                        {
                            k = 0;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < w; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                dist[y, q] = (q - p) * (q - p) + f[p];
            }
        }

        // 1D EDT per column
        f = new float[h];
        v = new int[h];
        z = new float[h + 1];

        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++) f[y] = dist[y, x];

            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (var q = 1; q < h; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
                    if (s <= z[k])
                    {
                        k--;
                        if (k < 0)
                        {
                            k = 0;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < h; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                dist[q, x] = (q - p) * (q - p) + f[p];
            }
        }

        // Convert squared pixel distance to meters
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            dist[y, x] = MathF.Sqrt(dist[y, x]) * metersPerPixel;

        return dist;
    }

    /// <summary>
    ///     Builds elevation map with per-pixel ownership tracking.
    ///     IMPORTANT: For pixels inside road cores (marked in protectionMask), we use the
    ///     pre-computed ownership and elevation from BuildRoadCoreProtectionMaskWithOwnership.
    ///     This ensures that road core pixels get the correct road's elevation, not just
    ///     the nearest cross-section (which could be from a different road at tight-angle junctions).
    ///     For pixels in blend zones (outside all road cores), we use nearest cross-section logic.
    /// </summary>
    private (float[,] elevations, int[,] owners, float[,] maxBlendRanges) BuildElevationMapWithOwnership(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel,
        bool[,] protectionMask,
        int[,] coreOwnershipMap,
        float[,] coreElevationMap)
    {
        var elevations = new float[height, width];
        var owners = new int[height, width];
        var maxBlendRanges = new float[height, width];
        var distances = new float[height, width];

        // Build spline blend range lookup
        var splineBlendRanges = network.Splines.ToDictionary(
            s => s.SplineId,
            s => s.Parameters.TerrainAffectedRangeMeters);

        // Initialize - copy core data where available
        var corePixelsUsed = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (protectionMask[y, x] && coreOwnershipMap[y, x] >= 0)
            {
                // This pixel is inside a road core - use pre-computed ownership
                elevations[y, x] = coreElevationMap[y, x];
                owners[y, x] = coreOwnershipMap[y, x];
                maxBlendRanges[y, x] = splineBlendRanges.GetValueOrDefault(coreOwnershipMap[y, x], 0);
                distances[y, x] = 0; // Inside core, distance is 0
                corePixelsUsed++;
            }
            else
            {
                elevations[y, x] = float.NaN;
                owners[y, x] = -1;
                maxBlendRanges[y, x] = 0;
                distances[y, x] = float.MaxValue;
            }

        TerrainLogger.Info($"  Pre-filled {corePixelsUsed:N0} road core pixels from protection mask");

        // Build spatial index for cross-sections (for blend zone processing)
        var spatialIndex = BuildSpatialIndex(network.CrossSections, metersPerPixel);

        var blendPixelsSet = 0;
        var processorCount = Environment.ProcessorCount;

        // Parallel processing for blend zone pixels only
        var options = new ParallelOptions { MaxDegreeOfParallelism = processorCount };

        Parallel.For(0, height, options, () => 0, (y, state, localPixelsSet) =>
        {
            for (var x = 0; x < width; x++)
            {
                // Skip pixels already handled (road cores)
                if (protectionMask[y, x])
                    continue;

                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);

                // Find nearest cross-section for blend zone
                var (nearest, nearestDist) = FindNearestCrossSection(worldPos, spatialIndex, metersPerPixel);

                if (nearest == null)
                    continue;

                // Get the maximum impact distance for this spline
                var maxDist = nearest.EffectiveRoadWidth / 2.0f + nearest.EffectiveBlendRange;

                if (nearestDist > maxDist)
                    continue;

                // Validate elevation
                if (float.IsNaN(nearest.TargetElevation) || float.IsInfinity(nearest.TargetElevation))
                    continue;

                // Check if this is closer than any existing assignment
                if (nearestDist < distances[y, x])
                {
                    distances[y, x] = nearestDist;
                    elevations[y, x] = nearest.TargetElevation;
                    owners[y, x] = nearest.OwnerSplineId;
                    maxBlendRanges[y, x] = nearest.EffectiveBlendRange;
                    localPixelsSet++;
                }
            }

            return localPixelsSet;
        }, localCount => Interlocked.Add(ref blendPixelsSet, localCount));

        TerrainLogger.Info($"  Set {blendPixelsSet:N0} blend zone elevation values");
        TerrainLogger.Info($"  Total: {corePixelsUsed + blendPixelsSet:N0} pixels with elevation data");

        return (elevations, owners, maxBlendRanges);
    }

    /// <summary>
    ///     Builds a spatial index for fast nearest-neighbor queries.
    /// </summary>
    private Dictionary<(int, int), List<UnifiedCrossSection>> BuildSpatialIndex(
        List<UnifiedCrossSection> sections,
        float metersPerPixel)
    {
        var index = new Dictionary<(int, int), List<UnifiedCrossSection>>();
        var skippedInvalid = 0;

        foreach (var cs in sections.Where(s => !s.IsExcluded))
        {
            // Skip cross-sections with invalid target elevations
            if (!IsValidTargetElevation(cs.TargetElevation))
            {
                skippedInvalid++;
                continue;
            }

            var gridX = (int)(cs.CenterPoint.X / metersPerPixel / SpatialIndexCellSize);
            var gridY = (int)(cs.CenterPoint.Y / metersPerPixel / SpatialIndexCellSize);
            var key = (gridX, gridY);

            if (!index.ContainsKey(key))
                index[key] = [];

            index[key].Add(cs);
        }

        if (skippedInvalid > 0)
            TerrainLogger.Info($"  WARNING: Skipped {skippedInvalid} cross-sections with invalid target elevations");

        return index;
    }

    /// <summary>
    ///     Validates that a target elevation is valid.
    /// </summary>
    private static bool IsValidTargetElevation(float elevation)
    {
        if (float.IsNaN(elevation) || float.IsInfinity(elevation))
            return false;
        if (elevation < -1000.0f)
            return false;
        return true;
    }

    /// <summary>
    ///     Finds the nearest cross-section to a world position using the spatial index.
    /// </summary>
    private (UnifiedCrossSection? nearest, float distance) FindNearestCrossSection(
        Vector2 worldPos,
        Dictionary<(int, int), List<UnifiedCrossSection>> index,
        float metersPerPixel)
    {
        var gridX = (int)(worldPos.X / metersPerPixel / SpatialIndexCellSize);
        var gridY = (int)(worldPos.Y / metersPerPixel / SpatialIndexCellSize);

        UnifiedCrossSection? nearest = null;
        var minDist = float.MaxValue;

        // Search 3x3 grid around the position
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var key = (gridX + dx, gridY + dy);
            if (index.TryGetValue(key, out var sections))
                foreach (var cs in sections)
                {
                    var dist = Vector2.Distance(worldPos, cs.CenterPoint);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = cs;
                    }
                }
        }

        return (nearest, minDist);
    }

    /// <summary>
    ///     Applies protected blending with per-spline blend ranges.
    ///     Key protection rule: ANY road core pixel (marked in protectionMask) is NEVER
    ///     modified by ANY blend zone, regardless of which spline owns the blend zone.
    ///     This prevents secondary roads' blend zones from destroying primary roads at junctions.
    /// </summary>
    private float[,] ApplyProtectedBlending(
        float[,] original,
        float[,] distanceField,
        float[,] elevationMap,
        int[,] splineOwnerMap,
        float[,] maxBlendRangeMap,
        bool[,] protectionMask,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        var height = original.GetLength(0);
        var width = original.GetLength(1);
        var result = (float[,])original.Clone();

        // Build spline parameters lookup for fast access
        var splineParams = network.Splines.ToDictionary(
            s => s.SplineId,
            s => (
                HalfWidth: s.Parameters.RoadWidthMeters / 2.0f,
                BlendRange: s.Parameters.TerrainAffectedRangeMeters,
                BlendFunction: s.Parameters.BlendFunctionType
            ));

        // Thread-safe counters
        var modifiedPixels = 0;
        var roadCorePixels = 0;
        var shoulderPixels = 0;
        var protectedFromBlend = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.For(0, height, options, y =>
        {
            var localModified = 0;
            var localCore = 0;
            var localShoulder = 0;
            var localProtected = 0;

            for (var x = 0; x < width; x++)
            {
                var ownerId = splineOwnerMap[y, x];
                if (ownerId < 0)
                    continue; // Not near any road

                var targetElevation = elevationMap[y, x];
                if (float.IsNaN(targetElevation))
                    continue;

                // Get distance from road core
                var d = distanceField[y, x];

                // Get owner spline parameters
                if (!splineParams.TryGetValue(ownerId, out var ownerParams))
                    continue;

                var halfWidth = ownerParams.HalfWidth;
                var blendRange = ownerParams.BlendRange;
                var blendFunctionType = ownerParams.BlendFunction;

                float newH;

                if (d <= halfWidth)
                {
                    // ROAD CORE - Protected zone, use target elevation directly
                    newH = targetElevation;
                    localCore++;
                }
                else if (d <= halfWidth + blendRange)
                {
                    // BLEND ZONE - But first check if this pixel is protected (part of another road's core)
                    if (protectionMask[y, x])
                    {
                        // This pixel is part of a road core (possibly different road) - DON'T modify it
                        // The elevation was already set correctly when that road's core was processed
                        localProtected++;
                        continue;
                    }

                    // Safe to blend - Smooth transition to terrain
                    var t = (d - halfWidth) / blendRange;
                    t = Math.Clamp(t, 0f, 1f);

                    var blend = ApplyBlendFunction(t, blendFunctionType);

                    // Interpolate between target elevation and original terrain
                    newH = targetElevation * (1f - blend) + original[y, x] * blend;
                    localShoulder++;
                }
                else
                {
                    // Outside influence zone - keep original
                    continue;
                }

                // Only modify if there's a meaningful change
                if (MathF.Abs(result[y, x] - newH) > 0.001f)
                {
                    result[y, x] = newH;
                    localModified++;
                }
            }

            Interlocked.Add(ref modifiedPixels, localModified);
            Interlocked.Add(ref roadCorePixels, localCore);
            Interlocked.Add(ref shoulderPixels, localShoulder);
            Interlocked.Add(ref protectedFromBlend, localProtected);
        });

        TerrainLogger.Info($"  Modified {modifiedPixels:N0} pixels total");
        TerrainLogger.Info($"    Road core: {roadCorePixels:N0} pixels");
        TerrainLogger.Info($"    Shoulder: {shoulderPixels:N0} pixels");
        TerrainLogger.Info($"    Protected from blend overlap: {protectedFromBlend:N0} pixels");

        return result;
    }

    /// <summary>
    ///     Applies the configured blend function.
    /// </summary>
    private static float ApplyBlendFunction(float t, BlendFunctionType blendType)
    {
        return blendType switch
        {
            BlendFunctionType.Cosine => 0.5f - 0.5f * MathF.Cos(MathF.PI * t),
            BlendFunctionType.Cubic => t * t * (3f - 2f * t),
            BlendFunctionType.Quintic => t * t * t * (t * (t * 6f - 15f) + 10f),
            _ => t // Linear
        };
    }

    /// <summary>
    ///     Applies post-processing smoothing to eliminate staircase artifacts on the road surface.
    ///     Uses a masked smoothing approach - only smooths within the road and shoulder areas.
    /// </summary>
    public void ApplyPostProcessingSmoothing(
        float[,] heightMap,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        // Find the dominant parameters for smoothing (use first spline's parameters)
        var firstSpline = network.Splines.FirstOrDefault();
        if (firstSpline == null)
            return;

        var parameters = firstSpline.Parameters;
        if (!parameters.EnablePostProcessingSmoothing)
            return;

        if (_lastDistanceField == null)
        {
            TerrainLogger.Warning("Cannot apply post-processing: no distance field available");
            return;
        }

        TerrainLogger.Info("=== POST-PROCESSING SMOOTHING ===");
        TerrainLogger.Info($"  Type: {parameters.SmoothingType}");
        TerrainLogger.Info($"  Kernel Size: {parameters.SmoothingKernelSize}");
        TerrainLogger.Info($"  Sigma: {parameters.SmoothingSigma:F2}");
        TerrainLogger.Info($"  Mask Extension: {parameters.SmoothingMaskExtensionMeters}m");
        TerrainLogger.Info($"  Iterations: {parameters.SmoothingIterations}");

        // Build smoothing mask based on maximum road width + extension
        var maxRoadWidth = network.Splines.Max(s => s.Parameters.RoadWidthMeters);
        var maxSmoothingDist = maxRoadWidth / 2.0f + parameters.SmoothingMaskExtensionMeters;
        var smoothingMask = BuildSmoothingMask(_lastDistanceField, maxSmoothingDist);

        // Apply smoothing iterations
        for (var iter = 0; iter < parameters.SmoothingIterations; iter++)
        {
            if (parameters.SmoothingIterations > 1)
                TerrainLogger.Info($"  Iteration {iter + 1}/{parameters.SmoothingIterations}...");

            switch (parameters.SmoothingType)
            {
                case PostProcessingSmoothingType.Gaussian:
                    ApplyGaussianSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize,
                        parameters.SmoothingSigma);
                    break;
                case PostProcessingSmoothingType.Box:
                    ApplyBoxSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize);
                    break;
                case PostProcessingSmoothingType.Bilateral:
                    ApplyBilateralSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize,
                        parameters.SmoothingSigma);
                    break;
            }
        }

        TerrainLogger.Info("=== POST-PROCESSING SMOOTHING COMPLETE ===");
    }

    /// <summary>
    ///     Builds a binary mask indicating which pixels should be smoothed.
    /// </summary>
    private bool[,] BuildSmoothingMask(float[,] distanceField, float maxDistance)
    {
        var height = distanceField.GetLength(0);
        var width = distanceField.GetLength(1);
        var mask = new bool[height, width];
        var maskedPixels = 0;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (distanceField[y, x] <= maxDistance)
            {
                mask[y, x] = true;
                maskedPixels++;
            }

        TerrainLogger.Info($"  Smoothing mask: {maskedPixels:N0} pixels");
        return mask;
    }

    /// <summary>
    ///     Applies Gaussian blur with the specified kernel size and sigma.
    ///     Only smooths pixels within the mask.
    /// </summary>
    private void ApplyGaussianSmoothing(float[,] heightMap, bool[,] mask, int kernelSize, float sigma)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var radius = kernelSize / 2;

        // Build Gaussian kernel
        var kernel = BuildGaussianKernel(kernelSize, sigma);

        // Create temporary buffer
        var tempMap = new float[height, width];
        var smoothedPixels = 0;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!mask[y, x])
            {
                tempMap[y, x] = heightMap[y, x];
                continue;
            }

            var sum = 0f;
            var weightSum = 0f;

            for (var ky = -radius; ky <= radius; ky++)
            for (var kx = -radius; kx <= radius; kx++)
            {
                var ny = y + ky;
                var nx = x + kx;

                if (ny >= 0 && ny < height && nx >= 0 && nx < width)
                {
                    var weight = kernel[ky + radius, kx + radius];
                    sum += heightMap[ny, nx] * weight;
                    weightSum += weight;
                }
            }

            tempMap[y, x] = weightSum > 0 ? sum / weightSum : heightMap[y, x];
            smoothedPixels++;
        }

        Array.Copy(tempMap, heightMap, height * width);
        TerrainLogger.Info($"    Gaussian smoothed {smoothedPixels:N0} pixels");
    }

    /// <summary>
    ///     Builds a 2D Gaussian kernel.
    /// </summary>
    private float[,] BuildGaussianKernel(int size, float sigma)
    {
        var kernel = new float[size, size];
        var radius = size / 2;
        var sum = 0f;
        var twoSigmaSquared = 2f * sigma * sigma;

        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
        {
            float distance = x * x + y * y;
            var value = MathF.Exp(-distance / twoSigmaSquared);
            kernel[y + radius, x + radius] = value;
            sum += value;
        }

        // Normalize
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            kernel[y, x] /= sum;

        return kernel;
    }

    /// <summary>
    ///     Applies box blur (simple averaging).
    /// </summary>
    private void ApplyBoxSmoothing(float[,] heightMap, bool[,] mask, int kernelSize)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var radius = kernelSize / 2;

        var tempMap = new float[height, width];
        var smoothedPixels = 0;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!mask[y, x])
            {
                tempMap[y, x] = heightMap[y, x];
                continue;
            }

            var sum = 0f;
            var count = 0;

            for (var ky = -radius; ky <= radius; ky++)
            for (var kx = -radius; kx <= radius; kx++)
            {
                var ny = y + ky;
                var nx = x + kx;

                if (ny >= 0 && ny < height && nx >= 0 && nx < width)
                {
                    sum += heightMap[ny, nx];
                    count++;
                }
            }

            tempMap[y, x] = count > 0 ? sum / count : heightMap[y, x];
            smoothedPixels++;
        }

        Array.Copy(tempMap, heightMap, height * width);
        TerrainLogger.Info($"    Box smoothed {smoothedPixels:N0} pixels");
    }

    /// <summary>
    ///     Applies bilateral filtering - edge-preserving smoothing.
    /// </summary>
    private void ApplyBilateralSmoothing(float[,] heightMap, bool[,] mask, int kernelSize, float sigma)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var radius = kernelSize / 2;
        var sigmaSpatial = sigma;
        var sigmaRange = sigma * 0.5f;

        var tempMap = new float[height, width];
        var smoothedPixels = 0;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (!mask[y, x])
            {
                tempMap[y, x] = heightMap[y, x];
                continue;
            }

            var centerValue = heightMap[y, x];
            var sum = 0f;
            var weightSum = 0f;

            for (var ky = -radius; ky <= radius; ky++)
            for (var kx = -radius; kx <= radius; kx++)
            {
                var ny = y + ky;
                var nx = x + kx;

                if (ny >= 0 && ny < height && nx >= 0 && nx < width)
                {
                    var neighborValue = heightMap[ny, nx];

                    float spatialDist = kx * kx + ky * ky;
                    var spatialWeight = MathF.Exp(-spatialDist / (2f * sigmaSpatial * sigmaSpatial));

                    var valueDiff = centerValue - neighborValue;
                    var rangeWeight = MathF.Exp(-(valueDiff * valueDiff) / (2f * sigmaRange * sigmaRange));

                    var weight = spatialWeight * rangeWeight;
                    sum += neighborValue * weight;
                    weightSum += weight;
                }
            }

            tempMap[y, x] = weightSum > 0 ? sum / weightSum : heightMap[y, x];
            smoothedPixels++;
        }

        Array.Copy(tempMap, heightMap, height * width);
        TerrainLogger.Info($"    Bilateral smoothed {smoothedPixels:N0} pixels");
    }
}