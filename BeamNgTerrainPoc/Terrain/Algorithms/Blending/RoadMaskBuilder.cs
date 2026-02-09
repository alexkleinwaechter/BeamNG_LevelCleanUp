using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Builds road masks and protection masks for terrain blending.
/// 
/// The road mask is used for distance field computation (binary mask of road pixels).
/// The protection mask tracks ownership and elevation for each road core pixel,
/// ensuring that higher-priority roads win ownership conflicts.
/// </summary>
public class RoadMaskBuilder
{
    /// <summary>
    /// Result of building a road protection mask with ownership tracking.
    /// </summary>
    public record ProtectionMaskResult(
        bool[,] ProtectionMask,
        int[,] OwnershipMap,
        float[,] ElevationMap,
        int ProtectedPixels,
        int OverwrittenByPriority);

    /// <summary>
    /// Builds a combined road core mask from all splines in the network.
    /// White pixels (255) indicate road core areas.
    /// </summary>
    /// <param name="network">The unified road network</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="metersPerPixel">Scale factor</param>
    /// <returns>Binary mask where 255 = road core</returns>
    public byte[,] BuildCombinedRoadCoreMask(
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

            RasterizationUtils.DrawLine(mask, x0, y0, x1, y1, width, height);
            sectionsProcessed++;
        }

        TerrainCreationLogger.Current?.Detail($"Rasterized {sectionsProcessed} cross-sections into combined road mask");
        return mask;
    }

    /// <summary>
    /// Builds a protection mask that marks ALL road core pixels from ALL splines,
    /// along with ownership (which spline owns each pixel) and elevation data.
    /// 
    /// This is the KEY fix for the junction problem: When roads overlap at tight angles,
    /// we need to know definitively which road owns each pixel based on the actual
    /// road geometry, not just "nearest cross-section".
    /// 
    /// Priority rules for overlapping road cores:
    /// 1. Higher priority spline wins
    /// 2. If equal priority, first one processed wins (stable ordering)
    /// </summary>
    public ProtectionMaskResult BuildRoadCoreProtectionMaskWithOwnership(
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

        // Get spline protection buffer lookup (per-spline buffer values)
        var splineProtectionBuffer = network.Splines.ToDictionary(
            s => s.SplineId,
            s => s.Parameters.RoadEdgeProtectionBufferMeters);

        foreach (var (splineId, crossSections) in crossSectionsBySpline)
        {
            if (crossSections.Count < 2)
                continue;

            var priority = splinePriority.GetValueOrDefault(splineId, 0);
            var protectionBuffer = splineProtectionBuffer.GetValueOrDefault(splineId, 2.0f);

            // For each consecutive pair of cross-sections, fill the road core polygon
            for (var i = 0; i < crossSections.Count - 1; i++)
            {
                var cs1 = crossSections[i];
                var cs2 = crossSections[i + 1];

                // Add per-spline buffer to road width for protection mask to prevent edge artifacts
                var halfWidth1 = cs1.EffectiveRoadWidth / 2.0f + protectionBuffer;
                var halfWidth2 = cs2.EffectiveRoadWidth / 2.0f + protectionBuffer;

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

                // Check if segment has banking - use banking-aware elevation if so
                var hasBanking = BankedTerrainHelper.SegmentHasBanking(cs1, cs2);

                // Fill the polygon with ownership tracking
                var (filled, overwritten) = FillConvexPolygonWithOwnershipAndBanking(
                    protectionMask, ownershipMap, elevationMap, priorityMap,
                    corners, width, height,
                    splineId, priority,
                    cs1, cs2, metersPerPixel, hasBanking);

                protectedPixels += filled;
                overwrittenByPriority += overwritten;
            }
        }

        TerrainCreationLogger.Current?.Detail($"Protection mask: {protectedPixels:N0} road core pixels protected");
        if (overwrittenByPriority > 0)
            TerrainCreationLogger.Current?.Detail(
                $"Priority resolution: {overwrittenByPriority:N0} pixels assigned to higher-priority roads");

        return new ProtectionMaskResult(protectionMask, ownershipMap, elevationMap, protectedPixels, overwrittenByPriority);
    }

    /// <summary>
    /// Fills a convex polygon with ownership tracking and banking-aware elevation.
    /// Higher priority splines overwrite lower priority ones.
    /// For banked roads, elevation is calculated per-pixel based on lateral offset.
    /// Returns (newPixelsFilled, pixelsOverwrittenByPriority).
    /// Uses scanline rasterization for efficient fill (no per-pixel point-in-polygon test).
    /// </summary>
    private static (int filled, int overwritten) FillConvexPolygonWithOwnershipAndBanking(
        bool[,] protectionMask,
        int[,] ownershipMap,
        float[,] elevationMap,
        int[,] priorityMap,
        Vector2[] corners,
        int width,
        int height,
        int splineId,
        int priority,
        UnifiedCrossSection cs1,
        UnifiedCrossSection cs2,
        float metersPerPixel,
        bool hasBanking)
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

        // Pre-calculate average elevation for non-banked case (optimization)
        var averageElevation = hasBanking 
            ? 0f  // Will calculate per-pixel
            : BankedTerrainHelper.GetSegmentAverageElevation(cs1, cs2);

        var cornerCount = corners.Length;

        // Scanline fill using edge intersection
        for (var y = minY; y <= maxY; y++)
        {
            var scanY = y + 0.5f;

            // Find intersection points with polygon edges
            Span<float> intersections = stackalloc float[cornerCount];
            var intersectionCount = 0;

            for (var i = 0; i < cornerCount; i++)
            {
                var v1 = corners[i];
                var v2 = corners[(i + 1) % cornerCount];

                if ((v1.Y <= scanY && v2.Y > scanY) || (v2.Y <= scanY && v1.Y > scanY))
                {
                    var t = (scanY - v1.Y) / (v2.Y - v1.Y);
                    intersections[intersectionCount++] = v1.X + t * (v2.X - v1.X);
                }
            }

            // Sort intersections (simple insertion sort for small count)
            for (var i = 1; i < intersectionCount; i++)
            {
                var key = intersections[i];
                var j = i - 1;
                while (j >= 0 && intersections[j] > key)
                {
                    intersections[j + 1] = intersections[j];
                    j--;
                }
                intersections[j + 1] = key;
            }

            // Fill between pairs of intersections
            for (var i = 0; i + 1 < intersectionCount; i += 2)
            {
                var xStart = Math.Max(minX, (int)MathF.Floor(intersections[i]));
                var xEnd = Math.Min(maxX, (int)MathF.Ceiling(intersections[i + 1]));

                for (var x = xStart; x <= xEnd; x++)
                {
                    // Calculate elevation for this pixel
                    float pixelElevation;
                    if (hasBanking)
                    {
                        var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                        pixelElevation = BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos);
                    }
                    else
                    {
                        pixelElevation = averageElevation;
                    }

                    // Check if we should claim this pixel
                    if (!protectionMask[y, x])
                    {
                        protectionMask[y, x] = true;
                        ownershipMap[y, x] = splineId;
                        elevationMap[y, x] = pixelElevation;
                        priorityMap[y, x] = priority;
                        filledCount++;
                    }
                    else if (priority > priorityMap[y, x])
                    {
                        ownershipMap[y, x] = splineId;
                        elevationMap[y, x] = pixelElevation;
                        priorityMap[y, x] = priority;
                        overwrittenCount++;
                    }
                }
            }
        }

        return (filledCount, overwrittenCount);
    }

    /// <summary>
    /// Fills a convex polygon with ownership tracking.
    /// Higher priority splines overwrite lower priority ones.
    /// Returns (newPixelsFilled, pixelsOverwrittenByPriority).
    /// Uses scanline rasterization for efficient fill (no per-pixel point-in-polygon test).
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

        var cornerCount = corners.Length;

        // Scanline fill using edge intersection
        for (var y = minY; y <= maxY; y++)
        {
            var scanY = y + 0.5f;

            // Find intersection points with polygon edges
            Span<float> intersections = stackalloc float[cornerCount];
            var intersectionCount = 0;

            for (var i = 0; i < cornerCount; i++)
            {
                var v1 = corners[i];
                var v2 = corners[(i + 1) % cornerCount];

                if ((v1.Y <= scanY && v2.Y > scanY) || (v2.Y <= scanY && v1.Y > scanY))
                {
                    var t = (scanY - v1.Y) / (v2.Y - v1.Y);
                    intersections[intersectionCount++] = v1.X + t * (v2.X - v1.X);
                }
            }

            // Sort intersections (simple insertion sort for small count)
            for (var i = 1; i < intersectionCount; i++)
            {
                var key = intersections[i];
                var j = i - 1;
                while (j >= 0 && intersections[j] > key)
                {
                    intersections[j + 1] = intersections[j];
                    j--;
                }
                intersections[j + 1] = key;
            }

            // Fill between pairs of intersections
            for (var i = 0; i + 1 < intersectionCount; i += 2)
            {
                var xStart = Math.Max(minX, (int)MathF.Floor(intersections[i]));
                var xEnd = Math.Min(maxX, (int)MathF.Ceiling(intersections[i + 1]));

                for (var x = xStart; x <= xEnd; x++)
                {
                    if (!protectionMask[y, x])
                    {
                        protectionMask[y, x] = true;
                        ownershipMap[y, x] = splineId;
                        elevationMap[y, x] = elevation;
                        priorityMap[y, x] = priority;
                        filledCount++;
                    }
                    else if (priority > priorityMap[y, x])
                    {
                        ownershipMap[y, x] = splineId;
                        elevationMap[y, x] = elevation;
                        priorityMap[y, x] = priority;
                        overwrittenCount++;
                    }
                }
            }
        }

        return (filledCount, overwrittenCount);
    }

    /// <summary>
    /// Validates that a target elevation is valid.
    /// </summary>
    private static bool IsValidTargetElevation(float elevation)
    {
        if (float.IsNaN(elevation) || float.IsInfinity(elevation))
            return false;
        if (elevation < -1000.0f)
            return false;
        return true;
    }
}
