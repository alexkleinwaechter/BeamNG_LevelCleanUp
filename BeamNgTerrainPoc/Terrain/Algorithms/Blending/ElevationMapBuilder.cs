using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms.Banking;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Builds elevation maps for terrain blending with per-pixel ownership tracking.
/// 
/// This handles both road core pixels (which use pre-computed ownership from the protection mask)
/// and blend zone pixels (which use cross-section elevation lookup with inverse-distance weighting).
/// 
/// IMPORTANT: Different interpolation strategies are used based on road source:
/// - OSM roads: Inverse-distance weighted interpolation from ALL nearby cross-sections
///   (works well for clean vector data)
/// - PNG roads: Inverse-distance weighted interpolation from ONLY ADJACENT cross-sections
///   along the spline path (±2 from nearest). This prevents spikes at curves where different
///   parts of the same spline are geometrically close but at different elevations.
/// 
/// Both approaches use interpolation (not nearest-neighbor) to avoid discontinuities.
/// </summary>
public class ElevationMapBuilder
{
    /// <summary>
    /// Result of building an elevation map with ownership tracking.
    /// </summary>
    public record ElevationMapResult(
        float[,] Elevations,
        int[,] Owners,
        float[,] MaxBlendRanges,
        int CorePixelsUsed,
        int BlendPixelsSet);

    /// <summary>
    /// Builds elevation map with per-pixel ownership tracking.
    /// 
    /// IMPORTANT: For pixels inside road cores (marked in protectionMask), we use the
    /// pre-computed ownership and elevation from the protection mask builder.
    /// This ensures that road core pixels get the correct road's elevation, not just
    /// the nearest cross-section (which could be from a different road at tight-angle junctions).
    /// 
    /// For pixels in blend zones (outside all road cores), we use nearest cross-section logic.
    /// 
    /// PERFORMANCE: The distanceField (from EDT) is used for early rejection. Pixels whose
    /// global distance exceeds the maximum possible influence distance (maxRoadHalfWidth + maxBlendRange)
    /// are skipped without any spatial index lookup, eliminating work for 90%+ of pixels on large terrains.
    /// </summary>
    public ElevationMapResult BuildElevationMapWithOwnership(
        UnifiedRoadNetwork network,
        int width,
        int height,
        float metersPerPixel,
        bool[,] protectionMask,
        int[,] coreOwnershipMap,
        float[,] coreElevationMap,
        float[,]? distanceField = null)
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

        TerrainCreationLogger.Current?.Detail($"Pre-filled {corePixelsUsed:N0} road core pixels from protection mask");

        // Build spatial index for cross-sections (for blend zone processing)
        var spatialIndex = new CrossSectionSpatialIndex(network.CrossSections, metersPerPixel);

        // Build lookup for whether each spline is from OSM source
        var splineIsOsm = network.Splines.ToDictionary(
            s => s.SplineId,
            s => !string.IsNullOrEmpty(s.OsmRoadType));

        var blendPixelsSet = 0;
        var processorCount = Environment.ProcessorCount;

        // Calculate maximum search radius based on largest road parameters
        var maxRoadHalfWidth = network.Splines.Max(s => s.Parameters.RoadWidthMeters / 2.0f);
        var maxBlendRange = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);
        var maxSearchRadius = maxRoadHalfWidth + maxBlendRange;

        // Pre-compute whether we have a distance field for early rejection
        var hasDistanceField = distanceField != null;
        var earlyRejectCount = 0;

        // Parallel processing for blend zone pixels only
        var options = new ParallelOptions { MaxDegreeOfParallelism = processorCount };

        Parallel.For(0, height, options, () => (pixelsSet: 0, rejected: 0), (y, state, localCounts) =>
        {
            // Pre-allocate a buffer for FindWithinRadius results to avoid yield return allocations.
            // 64 entries is generous for typical road networks (most grid cells have <20 cross-sections).
            var searchBuffer = new (UnifiedCrossSection cs, float distance)[64];

            for (var x = 0; x < width; x++)
            {
                // Skip pixels already handled (road cores)
                if (protectionMask[y, x])
                    continue;

                // EARLY REJECTION using global distance field:
                // If the EDT distance at this pixel exceeds the maximum possible influence
                // distance (maxRoadHalfWidth + maxBlendRange), no road can affect this pixel.
                // This eliminates spatial index lookups for 90%+ of pixels on large terrains.
                if (hasDistanceField && distanceField![y, x] > maxSearchRadius)
                {
                    localCounts.rejected++;
                    continue;
                }

                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);

                // Find the nearest cross-section to determine which road "owns" this pixel
                var (nearestCs, nearestDist) = spatialIndex.FindNearest(worldPos);
                
                if (nearestCs == null)
                    continue;
                
                var nearestSplineId = nearestCs.OwnerSplineId;
                var isOsm = splineIsOsm.GetValueOrDefault(nearestSplineId, false);

                // Use different interpolation strategies based on road source
                float interpolatedElevation;
                float blendRange;
                int dominantOwner;
                
                if (isOsm)
                {
                    // OSM roads: Use inverse-distance weighted interpolation from ALL nearby cross-sections
                    // This works well for clean vector data from OSM
                    (interpolatedElevation, dominantOwner, blendRange, _) =
                        InterpolateNearbyCrossSectionsBuffered(worldPos, spatialIndex, maxSearchRadius, searchBuffer);
                }
                else
                {
                    // PNG roads: Use inverse-distance weighted interpolation from ONLY the nearest spline
                    // This prevents bumpy artifacts from mixing cross-sections with inconsistent normals
                    // that result from skeleton extraction jaggedness.
                    (interpolatedElevation, dominantOwner, blendRange, _) =
                        InterpolateFromSingleSplineBuffered(worldPos, spatialIndex, maxSearchRadius, nearestSplineId, searchBuffer);
                }

                if (dominantOwner < 0 || float.IsNaN(interpolatedElevation))
                    continue;

                // Get the maximum impact distance for the dominant owner
                var maxDist = maxRoadHalfWidth + blendRange;

                if (nearestDist > maxDist)
                    continue;

                // Check if this is closer than any existing assignment
                if (nearestDist < distances[y, x])
                {
                    distances[y, x] = nearestDist;
                    elevations[y, x] = interpolatedElevation;
                    owners[y, x] = dominantOwner;
                    maxBlendRanges[y, x] = blendRange;
                    localCounts.pixelsSet++;
                }
            }

            return localCounts;
        }, localCounts =>
        {
            Interlocked.Add(ref blendPixelsSet, localCounts.pixelsSet);
            Interlocked.Add(ref earlyRejectCount, localCounts.rejected);
        });

        if (hasDistanceField)
            TerrainCreationLogger.Current?.Detail($"Early distance-field rejection skipped {earlyRejectCount:N0} pixels");

        TerrainCreationLogger.Current?.Detail($"Set {blendPixelsSet:N0} blend zone elevation values");
        TerrainCreationLogger.Current?.Detail($"Total: {corePixelsUsed + blendPixelsSet:N0} pixels with elevation data");

        return new ElevationMapResult(elevations, owners, maxBlendRanges, corePixelsUsed, blendPixelsSet);
    }

    /// <summary>
    /// Interpolates elevation from multiple nearby cross-sections using inverse-distance weighting.
    /// This smooths inner curve artifacts at hairpin turns where cross-sections overlap.
    /// 
    /// For banked roads, elevation is calculated considering the lateral offset from each
    /// cross-section's centerline using BankedTerrainHelper.
    /// 
    /// Algorithm:
    /// 1. Find all cross-sections within the search radius
    /// 2. Weight each by 1/distance² (closer = more influence)
    /// 3. For banked roads, calculate elevation at the world position (not center)
    /// 4. Return weighted average elevation and dominant owner (HIGHEST PRIORITY, not just nearest)
    /// 
    /// NOTE: This overload uses the yield-return FindWithinRadius. For hot loops, use
    /// InterpolateNearbyCrossSectionsBuffered instead.
    /// </summary>
    private static (float elevation, int dominantOwner, float blendRange, float nearestDist) 
        InterpolateNearbyCrossSections(
            Vector2 worldPos,
            CrossSectionSpatialIndex spatialIndex,
            float searchRadius)
    {
        var weightedElevation = 0f;
        var totalWeight = 0f;
        var minDist = float.MaxValue;
        UnifiedCrossSection? dominantCs = null;
        var dominantPriority = int.MinValue;
        var dominantDistance = float.MaxValue;

        foreach (var (cs, dist) in spatialIndex.FindWithinRadius(worldPos, searchRadius))
        {
            // Track absolute nearest for distance calculation
            if (dist < minDist)
                minDist = dist;

            // Determine dominant owner by PRIORITY first, then by distance
            var shouldClaimOwnership = false;
            if (cs.Priority > dominantPriority)
            {
                shouldClaimOwnership = true;
            }
            else if (cs.Priority == dominantPriority && dist < dominantDistance)
            {
                shouldClaimOwnership = true;
            }

            if (shouldClaimOwnership)
            {
                dominantCs = cs;
                dominantPriority = cs.Priority;
                dominantDistance = dist;
            }

            // Get elevation at the world position (banking-aware)
            // This uses the cross-section's banking to calculate proper elevation
            var elevationAtPos = BankedTerrainHelper.GetBankedElevation(cs, worldPos);
            if (float.IsNaN(elevationAtPos))
                elevationAtPos = cs.TargetElevation; // Fallback to center elevation

            // Inverse-distance weighting with epsilon to avoid division by zero
            var weight = 1f / MathF.Max(dist * dist, 0.01f);
            weightedElevation += elevationAtPos * weight;
            totalWeight += weight;
        }

        if (dominantCs == null || totalWeight <= 0)
            return (float.NaN, -1, 0, float.MaxValue);

        return (weightedElevation / totalWeight, dominantCs.OwnerSplineId, dominantCs.EffectiveBlendRange, minDist);
    }

    /// <summary>
    /// Buffer-based version of InterpolateNearbyCrossSections for hot loops.
    /// Uses a pre-allocated array instead of yield return to avoid IEnumerable state machine
    /// allocations at millions of calls per terrain generation.
    /// </summary>
    private static (float elevation, int dominantOwner, float blendRange, float nearestDist)
        InterpolateNearbyCrossSectionsBuffered(
            Vector2 worldPos,
            CrossSectionSpatialIndex spatialIndex,
            float searchRadius,
            (UnifiedCrossSection cs, float distance)[] searchBuffer)
    {
        var resultCount = spatialIndex.FindWithinRadius(worldPos, searchRadius, searchBuffer);

        if (resultCount == 0)
            return (float.NaN, -1, 0, float.MaxValue);

        var weightedElevation = 0f;
        var totalWeight = 0f;
        var minDist = float.MaxValue;
        UnifiedCrossSection? dominantCs = null;
        var dominantPriority = int.MinValue;
        var dominantDistance = float.MaxValue;

        // Process up to buffer capacity (if more results exist, they are the farthest and least influential)
        var count = Math.Min(resultCount, searchBuffer.Length);
        for (var i = 0; i < count; i++)
        {
            var (cs, dist) = searchBuffer[i];

            if (dist < minDist)
                minDist = dist;

            var shouldClaimOwnership = false;
            if (cs.Priority > dominantPriority)
                shouldClaimOwnership = true;
            else if (cs.Priority == dominantPriority && dist < dominantDistance)
                shouldClaimOwnership = true;

            if (shouldClaimOwnership)
            {
                dominantCs = cs;
                dominantPriority = cs.Priority;
                dominantDistance = dist;
            }

            var elevationAtPos = BankedTerrainHelper.GetBankedElevation(cs, worldPos);
            if (float.IsNaN(elevationAtPos))
                elevationAtPos = cs.TargetElevation;

            var weight = 1f / MathF.Max(dist * dist, 0.01f);
            weightedElevation += elevationAtPos * weight;
            totalWeight += weight;
        }

        if (dominantCs == null || totalWeight <= 0)
            return (float.NaN, -1, 0, float.MaxValue);

        return (weightedElevation / totalWeight, dominantCs.OwnerSplineId, dominantCs.EffectiveBlendRange, minDist);
    }

    /// <summary>
    /// Interpolates elevation from cross-sections belonging to a SINGLE spline only.
    /// Used for PNG roads to avoid mixing elevations from different road segments
    /// that may have inconsistent normal vectors due to skeleton extraction.
    /// 
    /// IMPORTANT: This method only considers cross-sections that are ADJACENT along the spline path,
    /// not just geometrically close. This prevents spikes at curves where different parts of the
    /// same spline are within the search radius but at very different elevations.
    /// 
    /// Algorithm:
    /// 1. Find the nearest cross-section from the target spline
    /// 2. Only interpolate with that cross-section and its immediate neighbors (±2 along LocalIndex)
    /// 3. This ensures smooth elevation transitions along the road path
    /// 
    /// NOTE: This overload uses the yield-return FindWithinRadius. For hot loops, use
    /// InterpolateFromSingleSplineBuffered instead.
    /// </summary>
    private static (float elevation, int dominantOwner, float blendRange, float nearestDist)
        InterpolateFromSingleSpline(
            Vector2 worldPos,
            CrossSectionSpatialIndex spatialIndex,
            float searchRadius,
            int targetSplineId)
    {
        // First pass: find the nearest cross-section from the target spline
        var minDist = float.MaxValue;
        UnifiedCrossSection? nearestCs = null;

        foreach (var (cs, dist) in spatialIndex.FindWithinRadius(worldPos, searchRadius))
        {
            if (cs.OwnerSplineId != targetSplineId)
                continue;

            if (dist < minDist)
            {
                minDist = dist;
                nearestCs = cs;
            }
        }

        if (nearestCs == null)
            return (float.NaN, -1, 0, float.MaxValue);

        // Second pass: only interpolate with cross-sections that are adjacent along the spline path
        // This prevents spikes at curves where distant parts of the spline are geometrically close
        var nearestLocalIndex = nearestCs.LocalIndex;
        const int maxIndexDistance = 2; // Only consider ±2 cross-sections along the path

        var weightedElevation = 0f;
        var totalWeight = 0f;

        foreach (var (cs, dist) in spatialIndex.FindWithinRadius(worldPos, searchRadius))
        {
            if (cs.OwnerSplineId != targetSplineId)
                continue;

            // Only consider cross-sections that are adjacent along the spline path
            var indexDistance = Math.Abs(cs.LocalIndex - nearestLocalIndex);
            if (indexDistance > maxIndexDistance)
                continue;

            // Get elevation at the world position (banking-aware)
            var elevationAtPos = BankedTerrainHelper.GetBankedElevation(cs, worldPos);
            if (float.IsNaN(elevationAtPos))
                elevationAtPos = cs.TargetElevation;

            // Weight by both geometric distance AND path distance
            // Cross-sections further along the path get less weight
            var pathWeight = 1f / (1f + indexDistance);
            var distWeight = 1f / MathF.Max(dist * dist, 0.01f);
            var weight = pathWeight * distWeight;
            
            weightedElevation += elevationAtPos * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0)
            return (float.NaN, -1, 0, float.MaxValue);

        return (weightedElevation / totalWeight, nearestCs.OwnerSplineId, nearestCs.EffectiveBlendRange, minDist);
    }

    /// <summary>
    /// Buffer-based version of InterpolateFromSingleSpline for hot loops.
    /// Uses a pre-allocated array instead of yield return to avoid IEnumerable state machine
    /// allocations at millions of calls per terrain generation.
    /// 
    /// Combines both passes (find nearest + interpolate adjacent) into a single buffer read,
    /// eliminating the second FindWithinRadius call entirely.
    /// </summary>
    private static (float elevation, int dominantOwner, float blendRange, float nearestDist)
        InterpolateFromSingleSplineBuffered(
            Vector2 worldPos,
            CrossSectionSpatialIndex spatialIndex,
            float searchRadius,
            int targetSplineId,
            (UnifiedCrossSection cs, float distance)[] searchBuffer)
    {
        var resultCount = spatialIndex.FindWithinRadius(worldPos, searchRadius, searchBuffer);

        if (resultCount == 0)
            return (float.NaN, -1, 0, float.MaxValue);

        // Single pass over buffered results: find nearest from target spline
        var minDist = float.MaxValue;
        UnifiedCrossSection? nearestCs = null;
        var count = Math.Min(resultCount, searchBuffer.Length);

        for (var i = 0; i < count; i++)
        {
            var (cs, dist) = searchBuffer[i];
            if (cs.OwnerSplineId != targetSplineId)
                continue;
            if (dist < minDist)
            {
                minDist = dist;
                nearestCs = cs;
            }
        }

        if (nearestCs == null)
            return (float.NaN, -1, 0, float.MaxValue);

        // Second pass over SAME buffer: interpolate with adjacent cross-sections
        var nearestLocalIndex = nearestCs.LocalIndex;
        const int maxIndexDistance = 2;

        var weightedElevation = 0f;
        var totalWeight = 0f;

        for (var i = 0; i < count; i++)
        {
            var (cs, dist) = searchBuffer[i];
            if (cs.OwnerSplineId != targetSplineId)
                continue;

            var indexDistance = Math.Abs(cs.LocalIndex - nearestLocalIndex);
            if (indexDistance > maxIndexDistance)
                continue;

            var elevationAtPos = BankedTerrainHelper.GetBankedElevation(cs, worldPos);
            if (float.IsNaN(elevationAtPos))
                elevationAtPos = cs.TargetElevation;

            var pathWeight = 1f / (1f + indexDistance);
            var distWeight = 1f / MathF.Max(dist * dist, 0.01f);
            var weight = pathWeight * distWeight;

            weightedElevation += elevationAtPos * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0)
            return (float.NaN, -1, 0, float.MaxValue);

        return (weightedElevation / totalWeight, nearestCs.OwnerSplineId, nearestCs.EffectiveBlendRange, minDist);
    }
}
