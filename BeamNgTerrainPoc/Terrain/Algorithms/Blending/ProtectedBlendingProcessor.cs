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
                s.Priority,
                s.Parameters.SideMaxSlopeDegrees));

        // Build spatial index for cross-sections grouped by spline ID
        var crossSectionsBySpline = new SplineGroupedSpatialIndex(network.CrossSections, metersPerPixel);

        // Pre-build priority-aware spatial index for fast higher-priority lookups.
        // This eliminates the O(all_splines) loop per blend-zone pixel by pre-computing
        // which splines' protection zones cover each grid cell.
        var priorityProtectionParams = network.Splines.ToDictionary(
            s => s.SplineId,
            s => (HalfWidth: s.Parameters.RoadWidthMeters / 2.0f,
                  ProtectionBuffer: s.Parameters.RoadEdgeProtectionBufferMeters,
                  Priority: s.Priority));
        var priorityIndex = new PriorityProtectionIndex(
            network, priorityProtectionParams, metersPerPixel);

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
                    // 
                    // KEY INSIGHT: The protection mask exists to prevent OTHER roads from modifying
                    // pixels near THIS road. But we still need to create a proper embankment/blend
                    // for this road's own transition to terrain.
                    //
                    // Previous bug: When protectionMask was true, we set newH = targetElevation directly,
                    // which created vertical cliffs at the protection boundary.
                    //
                    // Fix: Always apply the slope/blend calculation for our own road's blend zone.
                    // The protection mask only matters when checking if ANOTHER road should modify this pixel.

                    // Check if we're in the blend zone of a lower-priority road but within
                    // the protection buffer of a higher-priority road.
                    // Uses pre-built PriorityProtectionIndex for O(nearby_splines) instead of O(all_splines).
                    var higherPriorityElevation = FindHigherPriorityProtectedElevation(
                        worldPos, x, y, ownerId, ownerPriority,
                        priorityIndex, crossSectionsBySpline);

                    if (higherPriorityElevation.HasValue)
                    {
                        // A higher-priority road claims this pixel - use its elevation
                        newH = higherPriorityElevation.Value;
                        localProtectedByHigherPriority++;
                    }
                    else
                    {
                        // This is our road's blend zone - apply proper embankment shaping
                        // Use distance from OWNING spline for blend calculation
                        var t = (distToOwner - halfWidth) / blendRange;
                        t = Math.Clamp(t, 0f, 1f);

                        var blend = BlendFunctions.Apply(t, blendFunctionType);
                        var blendedH = targetElevation * (1f - blend) + original[y, x] * blend;
                        
                        // Enforce maximum side slope constraint with smooth transition
                        // This prevents steep "cliff" embankments when road elevation differs
                        // significantly from terrain elevation, and ensures smooth transition
                        // to terrain WITHIN the blend range
                        newH = EnforceSideMaxSlope(
                            targetElevation,
                            original[y, x],
                            blendedH,
                            distToOwner - halfWidth,
                            blendRange,
                            ownerParams.SideMaxSlopeDegrees);
                        
                        // Track if this was in the protection zone (for statistics only)
                        if (protectionMask[y, x])
                            localProtected++;
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
    /// 
    /// Uses PriorityProtectionIndex for spatial pre-filtering: instead of iterating
    /// over ALL splines (O(S)), we only check splines whose protection zones are
    /// known to cover this grid cell (typically 0-3 splines).
    /// </summary>
    private static float? FindHigherPriorityProtectedElevation(
        Vector2 worldPos,
        int pixelX,
        int pixelY,
        int currentOwnerId,
        int currentPriority,
        PriorityProtectionIndex priorityIndex,
        SplineGroupedSpatialIndex crossSectionsBySpline)
    {
        // Get candidate splines from the spatial index for this pixel's grid cell.
        // This is O(1) lookup + O(nearby_splines) iteration instead of O(all_splines).
        var candidates = priorityIndex.GetCandidates(pixelX, pixelY);
        if (candidates.Length == 0)
            return null;

        float? bestElevation = null;
        var bestPriority = currentPriority;
        var bestDistance = float.MaxValue;

        foreach (ref readonly var candidate in candidates.AsSpan())
        {
            // Skip self and lower/equal priority
            if (candidate.SplineId == currentOwnerId)
                continue;

            if (candidate.Priority <= currentPriority)
                continue;

            var nearestCs = crossSectionsBySpline.FindNearestForSpline(
                worldPos, candidate.SplineId, candidate.ProtectionRadius);

            if (nearestCs == null)
                continue;

            var distToSpline = Vector2.Distance(worldPos, nearestCs.CenterPoint);

            if (distToSpline <= candidate.ProtectionRadius)
            {
                if (candidate.Priority > bestPriority ||
                    (candidate.Priority == bestPriority && distToSpline < bestDistance))
                {
                    // Use banking/junction-constraint-aware elevation calculation
                    // This properly accounts for road tilt and junction surface constraints
                    bestElevation = BankedTerrainHelper.GetBankedElevation(nearestCs, worldPos);
                    bestPriority = candidate.Priority;
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
    /// Enforces the maximum side slope constraint on the blended elevation.
    /// 
    /// When the elevation difference between road and terrain is large relative to the blend range,
    /// a simple blend function would create slopes steeper than SideMaxSlopeDegrees (creating cliffs).
    /// 
    /// This method ensures the embankment ALWAYS respects the maximum slope angle by:
    /// 1. Following the max slope from road edge toward terrain
    /// 2. When max slope reaches terrain level OR blend range ends, returning terrain elevation
    /// 3. Never exceeding the specified maximum slope angle
    /// 
    /// IMPORTANT: If the blend range is too small for the elevation difference at the max slope,
    /// the terrain will have a visible "step" at the blend boundary. This is unavoidable without
    /// either increasing the blend range or accepting steeper slopes.
    /// 
    /// The fix for "cliff at blend boundary" is to simply follow the max slope continuously
    /// and clamp to terrain when we reach it OR when we exit the blend range.
    /// </summary>
    private static float EnforceSideMaxSlope(
        float roadElevation,
        float terrainElevation,
        float blendedElevation,
        float distanceFromRoadEdge,
        float blendRange,
        float sideMaxSlopeDegrees)
    {
        // If distance is too small, no slope enforcement needed
        if (distanceFromRoadEdge < 0.01f)
            return blendedElevation;
        
        // Calculate the elevation difference between road and terrain
        var elevationDiff = MathF.Abs(roadElevation - terrainElevation);
        
        // If difference is small, just use the blend function result
        if (elevationDiff < 0.1f)
            return blendedElevation;
        
        // Calculate max slope parameters
        var maxSlopeRad = sideMaxSlopeDegrees * MathF.PI / 180f;
        var tanMaxSlope = MathF.Tan(maxSlopeRad);
        
        // Calculate how much elevation can change at max slope over this distance
        var maxElevationChange = distanceFromRoadEdge * tanMaxSlope;
        
        bool isCut = roadElevation > terrainElevation;
        float slopeConstrainedElevation;
        
        if (isCut)
        {
            // Road above terrain - slope goes down, but never below terrain
            slopeConstrainedElevation = MathF.Max(roadElevation - maxElevationChange, terrainElevation);
        }
        else
        {
            // Road below terrain - slope goes up, but never above terrain
            slopeConstrainedElevation = MathF.Min(roadElevation + maxElevationChange, terrainElevation);
        }
        
        // Check if slope constraint is needed (blend function alone would be steeper than max slope)
        var blendExceedsSlope = isCut 
            ? blendedElevation < slopeConstrainedElevation 
            : blendedElevation > slopeConstrainedElevation;
        
        if (!blendExceedsSlope)
        {
            // Blend function is gentler than max slope - no constraint needed
            return blendedElevation;
        }
        
        // Slope constraint is active - use the slope-constrained elevation
        // This already clamps to terrain elevation, so no cliff at the point where slope meets terrain
        return slopeConstrainedElevation;
    }

    /// <summary>
    /// Parameters for a spline's blending behavior.
    /// </summary>
    private record SplineBlendParams(
        float HalfWidth,
        float BlendRange,
        BlendFunctionType BlendFunction,
        float ProtectionBuffer,
        int Priority,
        float SideMaxSlopeDegrees);
}
