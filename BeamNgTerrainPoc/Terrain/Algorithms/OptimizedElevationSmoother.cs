using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Optimized elevation smoothing using prefix sums (O(N)) instead of moving average.
/// Replaces O(N×W) windowed averaging with O(N) box filter.
/// 
/// PERFORMANCE: ~100x faster than naive moving average for large windows.
/// </summary>
public class OptimizedElevationSmoother : IHeightCalculator
{
    /// <summary>
    /// Calculates target elevations for road cross-sections using optimized prefix-sum smoothing.
    /// Window size is determined by parameters (default: 101 for highway quality).
    /// </summary>
    public void CalculateTargetElevations(RoadGeometry geometry, float[,] heightMap, float metersPerPixel)
    {
        Console.WriteLine("Calculating target elevations (optimized prefix-sum)...");

        // Get smoothing window size from parameters (default: 101 for highway quality)
        int windowSize = geometry.Parameters?.SmoothingWindowSize ?? 101;
        float crossSectionSpacing = geometry.Parameters?.CrossSectionIntervalMeters ?? 0.5f;
        float smoothingRadiusMeters = (windowSize / 2.0f) * crossSectionSpacing;
        
        Console.WriteLine($"  Smoothing window: {windowSize} cross-sections (~{smoothingRadiusMeters:F1}m radius)");

        // Group by PathId for per-path processing
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .ToList();

        int totalSections = 0;

        foreach (var pathGroup in pathGroups)
        {
            var sections = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();

            if (sections.Count == 0) continue;

            // Step 1: Sample terrain elevations at cross-section centers
            var rawElevations = new float[sections.Count];

            for (int i = 0; i < sections.Count; i++)
            {
                var cs = sections[i];
                int px = (int)(cs.CenterPoint.X / metersPerPixel);
                int py = (int)(cs.CenterPoint.Y / metersPerPixel);

                px = Math.Clamp(px, 0, heightMap.GetLength(1) - 1);
                py = Math.Clamp(py, 0, heightMap.GetLength(0) - 1);

                rawElevations[i] = heightMap[py, px];
            }

            // Step 2: Apply box filter smoothing using prefix sums (O(N))
            var smoothed = BoxFilterPrefixSum(rawElevations, windowSize);

            // Step 3: Assign smoothed elevations
            for (int i = 0; i < sections.Count; i++)
            {
                sections[i].TargetElevation = smoothed[i];
            }

            totalSections += sections.Count;
        }

        Console.WriteLine($"  Smoothed elevations for {totalSections:N0} cross-sections across {pathGroups.Count} path(s)");
    }

    /// <summary>
    /// O(N) box filter using prefix sums.
    /// Equivalent to moving average but 100x faster for large windows.
    /// 
    /// Algorithm:
    /// 1. Build cumulative sum array: prefixSum[i] = sum(input[0..i-1])
    /// 2. For each position i: avg = (prefixSum[right+1] - prefixSum[left]) / count
    /// </summary>
    private float[] BoxFilterPrefixSum(float[] input, int windowSize)
    {
        int n = input.Length;
        var result = new float[n];

        // Edge case
        if (n == 0) return result;
        if (windowSize <= 1)
        {
            Array.Copy(input, result, n);
            return result;
        }

        // Build prefix sum array: O(N)
        var prefixSum = new float[n + 1];
        prefixSum[0] = 0;

        for (int i = 0; i < n; i++)
        {
            prefixSum[i + 1] = prefixSum[i] + input[i];
        }

        // Apply box filter: O(N) - each lookup is O(1)
        int halfWindow = windowSize / 2;

        for (int i = 0; i < n; i++)
        {
            int left = Math.Max(0, i - halfWindow);
            int right = Math.Min(n - 1, i + halfWindow);

            // Range sum in O(1) using prefix sums
            float sum = prefixSum[right + 1] - prefixSum[left];
            int count = right - left + 1;

            result[i] = sum / count;
        }

        return result;
    }
}
