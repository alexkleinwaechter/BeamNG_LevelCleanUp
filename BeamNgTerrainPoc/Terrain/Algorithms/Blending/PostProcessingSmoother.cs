using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Post-processing smoothing for terrain to eliminate staircase artifacts.
/// 
/// Provides three smoothing algorithms:
/// - Gaussian: Standard blur with configurable sigma
/// - Box: Simple averaging (faster but lower quality)
/// - Bilateral: Edge-preserving smoothing (best quality)
/// 
/// Supports per-material smoothing parameters by applying different smoothing
/// strengths to different road regions based on spline ownership.
/// 
/// IMPORTANT: Junction handling ensures that where roads with different smoothing
/// parameters meet, BOTH smoothing passes cover the junction area. This prevents
/// unsmoothed gaps at material boundaries.
/// </summary>
public class PostProcessingSmoother
{
    /// <summary>
    /// Default radius for detecting junctions between parameter groups.
    /// Junctions within this distance will have their masks expanded to overlap.
    /// </summary>
    private const float JunctionOverlapRadiusMeters = 15.0f;
    
    /// <summary>
    /// Applies post-processing smoothing to eliminate staircase artifacts on the road surface.
    /// Uses a masked smoothing approach - only smooths within the road and shoulder areas.
    /// 
    /// Each material can have different smoothing parameters (kernel size, sigma, etc.).
    /// The smoothing is applied per-material, using each material's specific parameters
    /// for its road regions.
    /// 
    /// Junction areas where different parameter groups meet are handled specially:
    /// each group's mask is expanded at junctions to ensure overlapping coverage,
    /// preventing unsmoothed gaps at material boundaries.
    /// </summary>
    /// <param name="heightMap">The heightmap to smooth (modified in-place)</param>
    /// <param name="distanceField">Distance field from road cores</param>
    /// <param name="network">The road network</param>
    /// <param name="metersPerPixel">Scale factor</param>
    public void ApplyPostProcessingSmoothing(
        float[,] heightMap,
        float[,] distanceField,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        // Collect all splines that have post-processing enabled
        var splinesWithPostProcessing = network.Splines
            .Where(s => s.Parameters.EnablePostProcessingSmoothing)
            .ToList();
        
        // Log diagnostic info about which splines have post-processing
        TerrainLogger.Info("=== POST-PROCESSING SMOOTHING ===");
        TerrainLogger.Info($"  Total splines in network: {network.Splines.Count}");
        TerrainLogger.Info($"  Splines with post-processing enabled: {splinesWithPostProcessing.Count}");
        
        // Log per-material breakdown to help diagnose issues
        var splinesByMaterial = network.Splines
            .GroupBy(s => s.MaterialName)
            .Select(g => new {
                Material = g.Key,
                TotalSplines = g.Count(),
                PostProcessEnabled = g.Count(s => s.Parameters.EnablePostProcessingSmoothing),
                PostProcessDisabled = g.Count(s => !s.Parameters.EnablePostProcessingSmoothing)
            })
            .ToList();
        
        foreach (var mat in splinesByMaterial)
        {
            TerrainLogger.Info($"    Material '{mat.Material}': {mat.TotalSplines} splines, " +
                              $"{mat.PostProcessEnabled} enabled, {mat.PostProcessDisabled} disabled");
        }
        
        if (splinesWithPostProcessing.Count == 0)
        {
            TerrainLogger.Info("  No splines have post-processing enabled - skipping");
            return;
        }
        
        // Group splines by their unique smoothing parameter combinations
        // This allows different materials to have different smoothing strengths
        var parameterGroups = splinesWithPostProcessing
            .GroupBy(s => new SmoothingParameterKey(
                s.Parameters.SmoothingType,
                s.Parameters.SmoothingKernelSize,
                s.Parameters.SmoothingSigma,
                s.Parameters.SmoothingIterations,
                s.Parameters.RoadWidthMeters,
                s.Parameters.SmoothingMaskExtensionMeters))
            .ToList();
        
        TerrainLogger.Info($"  Unique parameter combinations: {parameterGroups.Count}");
        
        // If only one parameter group, no junction overlap handling needed
        if (parameterGroups.Count == 1)
        {
            var group = parameterGroups[0];
            var key = group.Key;
            var splineIds = group.Select(s => s.SplineId).ToHashSet();
            
            TerrainLogger.Info($"  Single parameter group - using simple mask");
            ApplySmoothingForGroup(heightMap, distanceField, network, metersPerPixel, 
                key, splineIds, junctionOverlapSplineIds: null, alreadySmoothedMask: null);
            
            TerrainLogger.Info("=== POST-PROCESSING SMOOTHING COMPLETE ===");
            return;
        }
        
        // Multiple parameter groups - need to handle junction overlaps
        // Find junctions where different parameter groups meet
        var junctionOverlaps = FindCrossGroupJunctions(network, parameterGroups, metersPerPixel);
        
        if (junctionOverlaps.Count > 0)
        {
            TerrainLogger.Info($"  Found {junctionOverlaps.Count} cross-group junction(s) requiring overlap handling");
        }
        
        // OVERSMOOTHING FIX: Track pixels that have already been smoothed to prevent double-smoothing
        // Junction pixels should only be smoothed ONCE by the first group that processes them
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var alreadySmoothedMask = new bool[height, width];
        
        // Apply smoothing for each parameter group, with junction overlap expansion
        foreach (var group in parameterGroups)
        {
            var key = group.Key;
            var splineIds = group.Select(s => s.SplineId).ToHashSet();
            
            // Find splines from OTHER groups that share junctions with THIS group
            var overlappingSplineIds = GetOverlappingSplineIds(splineIds, junctionOverlaps);
            
            TerrainLogger.Info($"  Processing group: Type={key.SmoothingType}, Kernel={key.KernelSize}, " +
                              $"Sigma={key.Sigma:F2}, Iterations={key.Iterations}, " +
                              $"Splines={splineIds.Count}" +
                              (overlappingSplineIds.Count > 0 ? $", +{overlappingSplineIds.Count} junction overlap(s)" : ""));
            
            ApplySmoothingForGroup(heightMap, distanceField, network, metersPerPixel,
                key, splineIds, overlappingSplineIds, alreadySmoothedMask);
        }

        TerrainLogger.Info("=== POST-PROCESSING SMOOTHING COMPLETE ===");
    }
    
    /// <summary>
    /// Applies smoothing for a single parameter group, optionally including junction overlap regions.
    /// Tracks which pixels have been smoothed to prevent double-smoothing at junctions.
    /// </summary>
    private static void ApplySmoothingForGroup(
        float[,] heightMap,
        float[,] distanceField,
        UnifiedRoadNetwork network,
        float metersPerPixel,
        SmoothingParameterKey key,
        HashSet<int> splineIds,
        HashSet<int>? junctionOverlapSplineIds,
        bool[,]? alreadySmoothedMask = null)
    {
        // Build a mask for the splines in this group
        var maxSmoothingDist = key.RoadWidth / 2.0f + key.MaskExtension;
        var groupMask = BuildSmoothingMaskForSplines(
            distanceField, network, splineIds, maxSmoothingDist, metersPerPixel);
        
        // If there are junction overlaps, expand the mask to include those regions
        if (junctionOverlapSplineIds != null && junctionOverlapSplineIds.Count > 0)
        {
            ExpandMaskForJunctionOverlaps(
                groupMask, network, splineIds, junctionOverlapSplineIds, 
                maxSmoothingDist, metersPerPixel);
        }
        
        // OVERSMOOTHING FIX: Remove pixels that have already been smoothed by previous groups
        // This prevents junction areas from being smoothed multiple times
        var skippedPixels = 0;
        if (alreadySmoothedMask != null)
        {
            var height = groupMask.GetLength(0);
            var width = groupMask.GetLength(1);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (groupMask[y, x] && alreadySmoothedMask[y, x])
                {
                    groupMask[y, x] = false;
                    skippedPixels++;
                }
            }
            
            if (skippedPixels > 0)
            {
                TerrainLogger.Info($"    Skipping {skippedPixels:N0} already-smoothed pixels (preventing oversmoothing)");
            }
        }
        
        var maskedPixels = CountMaskedPixels(groupMask);
        if (maskedPixels == 0)
        {
            TerrainLogger.Info($"    No pixels to smooth, skipping");
            return;
        }
        
        TerrainLogger.Info($"    Mask covers {maskedPixels:N0} pixels");
        
        // Apply smoothing iterations with this group's parameters
        for (var iter = 0; iter < key.Iterations; iter++)
        {
            if (key.Iterations > 1)
                TerrainLogger.Info($"    Iteration {iter + 1}/{key.Iterations}...");

            switch (key.SmoothingType)
            {
                case PostProcessingSmoothingType.Gaussian:
                    ApplyGaussianSmoothing(heightMap, groupMask, key.KernelSize, key.Sigma);
                    break;
                case PostProcessingSmoothingType.Box:
                    ApplyBoxSmoothing(heightMap, groupMask, key.KernelSize);
                    break;
                case PostProcessingSmoothingType.Bilateral:
                    ApplyBilateralSmoothing(heightMap, groupMask, key.KernelSize, key.Sigma);
                    break;
            }
        }
        
        // OVERSMOOTHING FIX: Mark these pixels as smoothed for future groups
        if (alreadySmoothedMask != null)
        {
            var height = groupMask.GetLength(0);
            var width = groupMask.GetLength(1);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (groupMask[y, x])
                {
                    alreadySmoothedMask[y, x] = true;
                }
            }
        }
    }
    
    /// <summary>
    /// Finds junctions where splines from different parameter groups meet.
    /// Returns a list of (splineId1, splineId2, junctionPosition) tuples.
    /// </summary>
    private static List<(int SplineId1, int SplineId2, Vector2 Position)> FindCrossGroupJunctions(
        UnifiedRoadNetwork network,
        List<IGrouping<SmoothingParameterKey, ParameterizedRoadSpline>> parameterGroups,
        float metersPerPixel)
    {
        var junctions = new List<(int, int, Vector2)>();
        
        // Build a lookup of spline ID -> parameter group index
        var splineToGroup = new Dictionary<int, int>();
        for (var i = 0; i < parameterGroups.Count; i++)
        {
            foreach (var spline in parameterGroups[i])
            {
                splineToGroup[spline.SplineId] = i;
            }
        }
        
        // Collect all spline endpoints
        var endpoints = new List<(int SplineId, Vector2 Position, bool IsStart)>();
        foreach (var spline in network.Splines.Where(s => s.Parameters.EnablePostProcessingSmoothing))
        {
            endpoints.Add((spline.SplineId, spline.StartPoint, true));
            endpoints.Add((spline.SplineId, spline.EndPoint, false));
        }
        
        // Find endpoints from different groups that are close together
        for (var i = 0; i < endpoints.Count; i++)
        {
            for (var j = i + 1; j < endpoints.Count; j++)
            {
                var ep1 = endpoints[i];
                var ep2 = endpoints[j];
                
                // Skip if same spline
                if (ep1.SplineId == ep2.SplineId)
                    continue;
                
                // Skip if same parameter group
                if (splineToGroup.GetValueOrDefault(ep1.SplineId, -1) == 
                    splineToGroup.GetValueOrDefault(ep2.SplineId, -2))
                    continue;
                
                // Check distance
                var dist = Vector2.Distance(ep1.Position, ep2.Position);
                if (dist <= JunctionOverlapRadiusMeters)
                {
                    var midpoint = (ep1.Position + ep2.Position) / 2.0f;
                    junctions.Add((ep1.SplineId, ep2.SplineId, midpoint));
                }
            }
        }
        
        // Also check for T-junctions (endpoint of one spline near middle of another)
        foreach (var spline in network.Splines.Where(s => s.Parameters.EnablePostProcessingSmoothing))
        {
            var splineGroup = splineToGroup.GetValueOrDefault(spline.SplineId, -1);
            
            // Check this spline's endpoints against all cross-sections of other splines
            foreach (var endpoint in new[] { spline.StartPoint, spline.EndPoint })
            {
                foreach (var cs in network.CrossSections.Where(c => 
                    c.OwnerSplineId != spline.SplineId && 
                    !c.IsExcluded &&
                    splineToGroup.GetValueOrDefault(c.OwnerSplineId, -2) != splineGroup))
                {
                    var dist = Vector2.Distance(endpoint, cs.CenterPoint);
                    if (dist <= JunctionOverlapRadiusMeters)
                    {
                        // T-junction found - avoid duplicates
                        if (!junctions.Any(j => 
                            (j.Item1 == spline.SplineId && j.Item2 == cs.OwnerSplineId) ||
                            (j.Item1 == cs.OwnerSplineId && j.Item2 == spline.SplineId)))
                        {
                            junctions.Add((spline.SplineId, cs.OwnerSplineId, cs.CenterPoint));
                        }
                        break; // Found a junction for this endpoint, move on
                    }
                }
            }
        }
        
        return junctions;
    }
    
    /// <summary>
    /// Gets the set of spline IDs from other groups that share junctions with the given spline IDs.
    /// </summary>
    private static HashSet<int> GetOverlappingSplineIds(
        HashSet<int> groupSplineIds,
        List<(int SplineId1, int SplineId2, Vector2 Position)> junctionOverlaps)
    {
        var overlapping = new HashSet<int>();
        
        foreach (var (splineId1, splineId2, _) in junctionOverlaps)
        {
            if (groupSplineIds.Contains(splineId1) && !groupSplineIds.Contains(splineId2))
            {
                overlapping.Add(splineId2);
            }
            else if (groupSplineIds.Contains(splineId2) && !groupSplineIds.Contains(splineId1))
            {
                overlapping.Add(splineId1);
            }
        }
        
        return overlapping;
    }
    
    /// <summary>
    /// Expands the mask to include junction overlap regions from other splines.
    /// Only expands near the actual junction points, not the entire other spline.
    /// </summary>
    private static void ExpandMaskForJunctionOverlaps(
        bool[,] mask,
        UnifiedRoadNetwork network,
        HashSet<int> groupSplineIds,
        HashSet<int> overlapSplineIds,
        float maxDistance,
        float metersPerPixel)
    {
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        
        // Find the junction points between the group splines and overlap splines
        var groupEndpoints = new List<Vector2>();
        foreach (var spline in network.Splines.Where(s => groupSplineIds.Contains(s.SplineId)))
        {
            groupEndpoints.Add(spline.StartPoint);
            groupEndpoints.Add(spline.EndPoint);
        }
        
        // Get cross-sections from overlap splines that are near group endpoints
        var overlapCrossSections = network.CrossSections
            .Where(cs => overlapSplineIds.Contains(cs.OwnerSplineId) && !cs.IsExcluded)
            .Where(cs => groupEndpoints.Any(ep => 
                Vector2.Distance(ep, cs.CenterPoint) <= JunctionOverlapRadiusMeters * 1.5f))
            .ToList();
        
        // Expand mask around these cross-sections
        foreach (var cs in overlapCrossSections)
        {
            var halfWidth = cs.EffectiveRoadWidth / 2.0f + maxDistance;
            var centerPx = (int)(cs.CenterPoint.X / metersPerPixel);
            var centerPy = (int)(cs.CenterPoint.Y / metersPerPixel);
            var radiusPx = (int)MathF.Ceiling(halfWidth / metersPerPixel) + 1;
            
            var minX = Math.Max(0, centerPx - radiusPx);
            var maxX = Math.Min(width - 1, centerPx + radiusPx);
            var minY = Math.Max(0, centerPy - radiusPx);
            var maxY = Math.Min(height - 1, centerPy + radiusPx);
            
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var worldX = x * metersPerPixel;
                var worldY = y * metersPerPixel;
                var dx = worldX - cs.CenterPoint.X;
                var dy = worldY - cs.CenterPoint.Y;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                
                if (dist <= halfWidth)
                {
                    mask[y, x] = true;
                }
            }
        }
    }
    
    /// <summary>
    /// Key for grouping splines with identical smoothing parameters.
    /// </summary>
    private readonly record struct SmoothingParameterKey(
        PostProcessingSmoothingType SmoothingType,
        int KernelSize,
        float Sigma,
        int Iterations,
        float RoadWidth,
        float MaskExtension);
    
    /// <summary>
    /// Builds a smoothing mask for specific splines only.
    /// This allows different materials to have different smoothing regions.
    /// </summary>
    private static bool[,] BuildSmoothingMaskForSplines(
        float[,] distanceField,
        UnifiedRoadNetwork network,
        HashSet<int> splineIds,
        float maxDistance,
        float metersPerPixel)
    {
        var height = distanceField.GetLength(0);
        var width = distanceField.GetLength(1);
        var mask = new bool[height, width];
        
        // Get cross-sections for the specified splines
        var relevantCrossSections = network.CrossSections
            .Where(cs => splineIds.Contains(cs.OwnerSplineId) && !cs.IsExcluded)
            .ToList();
        
        if (relevantCrossSections.Count == 0)
            return mask;
        
        // For each relevant cross-section, mark pixels within maxDistance
        foreach (var cs in relevantCrossSections)
        {
            // Calculate bounding box around the cross-section
            var halfWidth = cs.EffectiveRoadWidth / 2.0f + maxDistance;
            var centerPx = (int)(cs.CenterPoint.X / metersPerPixel);
            var centerPy = (int)(cs.CenterPoint.Y / metersPerPixel);
            var radiusPx = (int)MathF.Ceiling(halfWidth / metersPerPixel) + 1;
            
            var minX = Math.Max(0, centerPx - radiusPx);
            var maxX = Math.Min(width - 1, centerPx + radiusPx);
            var minY = Math.Max(0, centerPy - radiusPx);
            var maxY = Math.Min(height - 1, centerPy + radiusPx);
            
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                // Check if this pixel is within maxDistance of the cross-section center
                var worldX = x * metersPerPixel;
                var worldY = y * metersPerPixel;
                var dx = worldX - cs.CenterPoint.X;
                var dy = worldY - cs.CenterPoint.Y;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                
                if (dist <= halfWidth)
                {
                    mask[y, x] = true;
                }
            }
        }
        
        return mask;
    }
    
    /// <summary>
    /// Counts the number of true pixels in a mask.
    /// </summary>
    private static int CountMaskedPixels(bool[,] mask)
    {
        var count = 0;
        var height = mask.GetLength(0);
        var width = mask.GetLength(1);
        
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (mask[y, x])
                count++;
        
        return count;
    }

    /// <summary>
    /// Applies Gaussian blur with the specified kernel size and sigma.
    /// Only smooths pixels within the mask.
    /// </summary>
    private static void ApplyGaussianSmoothing(float[,] heightMap, bool[,] mask, int kernelSize, float sigma)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var radius = kernelSize / 2;

        var kernel = BuildGaussianKernel(kernelSize, sigma);
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
        TerrainCreationLogger.Current?.Detail($"Gaussian smoothed {smoothedPixels:N0} pixels");
    }

    /// <summary>
    /// Builds a 2D Gaussian kernel.
    /// </summary>
    private static float[,] BuildGaussianKernel(int size, float sigma)
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
    /// Applies box blur (simple averaging).
    /// </summary>
    private static void ApplyBoxSmoothing(float[,] heightMap, bool[,] mask, int kernelSize)
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
        TerrainCreationLogger.Current?.Detail($"Box smoothed {smoothedPixels:N0} pixels");
    }

    /// <summary>
    /// Applies bilateral filtering - edge-preserving smoothing.
    /// </summary>
    private static void ApplyBilateralSmoothing(float[,] heightMap, bool[,] mask, int kernelSize, float sigma)
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
        TerrainCreationLogger.Current?.Detail($"Bilateral smoothed {smoothedPixels:N0} pixels");
    }
}
