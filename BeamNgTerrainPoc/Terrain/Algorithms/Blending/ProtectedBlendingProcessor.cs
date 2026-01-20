using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Applies protected blending to terrain with road elevations.
/// 
/// Key protection rules:
/// 1. Road core pixels (marked in protectionMask) are NEVER modified by ANY blend zone
/// 2. When blending for a lower-priority road, if the pixel is within a higher-priority
///    road's protection zone, use the higher-priority road's elevation instead of blending
///    toward terrain. This prevents secondary roads from creating "dents" at junctions.
/// </summary>
public class ProtectedBlendingProcessor
{
    /// <summary>
    /// Result statistics from protected blending.
    /// </summary>
    public record BlendingStatistics(
        int ModifiedPixels,
        int RoadCorePixels,
        int ShoulderPixels,
        int ProtectedFromBlend,
        int ProtectedByHigherPriority);

    /// <summary>
    /// Applies protected blending with per-spline blend ranges.
    /// 
    /// IMPORTANT: The blend zone calculation uses the distance from the OWNING spline's
    /// nearest cross-section, not the global distance field. This ensures that the blend
    /// transition is relative to the road that "owns" this pixel, providing smooth
    /// road-to-terrain transitions regardless of the FlipMaterialProcessingOrder setting.
    /// </summary>
    public (float[,] result, BlendingStatistics stats) ApplyProtectedBlending(
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
            s => new SplineBlendParams(
                s.Parameters.RoadWidthMeters / 2.0f,
                s.Parameters.TerrainAffectedRangeMeters,
                s.Parameters.BlendFunctionType,
                s.Parameters.RoadEdgeProtectionBufferMeters,
                s.Priority));

        // Build spatial index for cross-sections grouped by spline ID
        var crossSectionsBySpline = new SplineGroupedSpatialIndex(network.CrossSections, metersPerPixel);

        // Thread-safe counters
        var modifiedPixels = 0;
        var roadCorePixels = 0;
        var shoulderPixels = 0;
        var protectedFromBlend = 0;
        var protectedByHigherPriority = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.For(0, height, options, y =>
        {
            var localModified = 0;
            var localCore = 0;
            var localShoulder = 0;
            var localProtected = 0;
            var localProtectedByHigherPriority = 0;

            for (var x = 0; x < width; x++)
            {
                var ownerId = splineOwnerMap[y, x];
                if (ownerId < 0)
                    continue; // Not near any road

                var targetElevation = elevationMap[y, x];
                if (float.IsNaN(targetElevation))
                    continue;

                // Get owner spline parameters
                if (!splineParams.TryGetValue(ownerId, out var ownerParams))
                    continue;

                var halfWidth = ownerParams.HalfWidth;
                var blendRange = ownerParams.BlendRange;
                var blendFunctionType = ownerParams.BlendFunction;
                var ownerPriority = ownerParams.Priority;

                // Calculate distance from the OWNING spline's road core, not global distance field.
                // This is critical for correct blend transitions - we need the distance relative
                // to the road that owns this pixel, not the nearest road core overall.
                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                var distToOwner = CalculateDistanceToOwningSpline(
                    worldPos, ownerId, crossSectionsBySpline, halfWidth);
                
                // Use global distance field only for road core detection (as fallback),
                // but use owner-specific distance for blend zone calculations
                var globalDist = distanceField[y, x];

                float newH;

                // Use the minimum of global distance and owner distance for core detection
                // This ensures we're in the road core if EITHER measure says so
                var effectiveDistForCore = MathF.Min(globalDist, distToOwner);

                if (effectiveDistForCore <= halfWidth)
                {
                    // ROAD CORE - Protected zone, use target elevation directly
                    newH = targetElevation;
                    localCore++;
                }
                else if (distToOwner <= halfWidth + blendRange)
                {
                    // BLEND ZONE - Use distance from owning spline for proper transition
                    // Check protection rules

                    // Rule 1: If this pixel is inside any road's protection zone, USE the protected elevation
                    // (The protection zone extends beyond the road core by RoadEdgeProtectionBufferMeters,
                    // so these pixels should get the road's elevation, not terrain-blended elevation)
                    if (protectionMask[y, x])
                    {
                        newH = targetElevation;
                        localProtected++;
                    }
                    else
                    {
                        // Rule 2: Check if we're in the blend zone of a lower-priority road but within
                        // the protection buffer of a higher-priority road.
                        var higherPriorityElevation = FindHigherPriorityProtectedElevation(
                            worldPos, ownerId, ownerPriority, network.Splines, splineParams, 
                            crossSectionsBySpline, metersPerPixel);

                        if (higherPriorityElevation.HasValue)
                        {
                            newH = higherPriorityElevation.Value;
                            localProtectedByHigherPriority++;
                        }
                        else
                        {
                            // Safe to blend - Smooth transition to terrain
                            // Use distance from OWNING spline for blend calculation
                            var t = (distToOwner - halfWidth) / blendRange;
                            t = Math.Clamp(t, 0f, 1f);

                            var blend = BlendFunctions.Apply(t, blendFunctionType);
                            newH = targetElevation * (1f - blend) + original[y, x] * blend;
                        }
                    }

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
            Interlocked.Add(ref protectedByHigherPriority, localProtectedByHigherPriority);
        });

        var stats = new BlendingStatistics(
            modifiedPixels, roadCorePixels, shoulderPixels, 
            protectedFromBlend, protectedByHigherPriority);

        LogStatistics(stats);

        return (result, stats);
    }

    /// <summary>
    /// Calculates the distance from a world position to the owning spline's road core.
    /// This is used for blend zone calculations to ensure proper road-to-terrain transitions.
    /// </summary>
    /// <param name="worldPos">The world position to calculate distance from</param>
    /// <param name="ownerId">The ID of the spline that owns this pixel</param>
    /// <param name="crossSectionsBySpline">Spatial index of cross-sections by spline</param>
    /// <param name="halfWidth">Half-width of the road for this spline</param>
    /// <returns>Distance from the owning spline's centerline to the world position</returns>
    private static float CalculateDistanceToOwningSpline(
        Vector2 worldPos,
        int ownerId,
        SplineGroupedSpatialIndex crossSectionsBySpline,
        float halfWidth)
    {
        // Find the nearest cross-section of the owning spline
        var searchRadius = halfWidth * 4; // Search radius to find nearby cross-sections
        var nearestCs = crossSectionsBySpline.FindNearestForSpline(worldPos, ownerId, searchRadius);

        if (nearestCs == null)
        {
            // Fallback: if we can't find a cross-section, return a large distance
            // This shouldn't happen if ownership is set correctly
            return float.MaxValue;
        }

        // Calculate perpendicular distance from the cross-section's centerline
        // Project the position onto the normal direction to get lateral offset
        var toPoint = worldPos - nearestCs.CenterPoint;
        var lateralOffset = MathF.Abs(Vector2.Dot(toPoint, nearestCs.NormalDirection));

        return lateralOffset;
    }

    /// <summary>
    /// Checks if a pixel is within a higher-priority road's protection buffer zone.
    /// Returns the banking/constraint-aware elevation at that position.
    /// </summary>
    private static float? FindHigherPriorityProtectedElevation(
        Vector2 worldPos,
        int currentOwnerId,
        int currentPriority,
        List<ParameterizedRoadSpline> splines,
        Dictionary<int, SplineBlendParams> splineParams,
        SplineGroupedSpatialIndex crossSectionsBySpline,
        float metersPerPixel)
    {
        float? bestElevation = null;
        var bestPriority = currentPriority;
        var bestDistance = float.MaxValue;

        foreach (var spline in splines)
        {
            if (spline.SplineId == currentOwnerId)
                continue;

            if (!splineParams.TryGetValue(spline.SplineId, out var params_))
                continue;

            if (params_.Priority <= currentPriority)
                continue;

            var protectionRadius = params_.HalfWidth + params_.ProtectionBuffer;

            var nearestCs = crossSectionsBySpline.FindNearestForSpline(
                worldPos, spline.SplineId, protectionRadius);
            
            if (nearestCs == null)
                continue;

            var distToSpline = Vector2.Distance(worldPos, nearestCs.CenterPoint);

            if (distToSpline <= protectionRadius)
            {
                if (params_.Priority > bestPriority ||
                    (params_.Priority == bestPriority && distToSpline < bestDistance))
                {
                    // Use banking/junction-constraint-aware elevation calculation
                    // This properly accounts for road tilt and junction surface constraints
                    bestElevation = BankedTerrainHelper.GetBankedElevation(nearestCs, worldPos);
                    bestPriority = params_.Priority;
                    bestDistance = distToSpline;
                }
            }
        }

        return bestElevation;
    }

    private static void LogStatistics(BlendingStatistics stats)
    {
        TerrainCreationLogger.Current?.Detail($"Modified {stats.ModifiedPixels:N0} pixels total");
        TerrainCreationLogger.Current?.Detail($"  Road core: {stats.RoadCorePixels:N0} pixels");
        TerrainCreationLogger.Current?.Detail($"  Shoulder: {stats.ShoulderPixels:N0} pixels");
        TerrainCreationLogger.Current?.Detail($"  Protected from blend overlap: {stats.ProtectedFromBlend:N0} pixels");
        if (stats.ProtectedByHigherPriority > 0)
            TerrainCreationLogger.Current?.Detail(
                $"  Protected by higher-priority road buffer: {stats.ProtectedByHigherPriority:N0} pixels");
    }

    /// <summary>
    /// Parameters for a spline's blending behavior.
    /// </summary>
    private record SplineBlendParams(
        float HalfWidth,
        float BlendRange,
        BlendFunctionType BlendFunction,
        float ProtectionBuffer,
        int Priority);
}
