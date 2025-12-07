using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Optimized elevation smoothing with support for Box filter (prefix sums) and Butterworth low-pass filter.
/// 
/// Box Filter: O(N) using prefix sums - fast, suitable for flat terrain.
/// Butterworth Filter: O(N x order) - maximally flat passband, ideal for hilly terrain.
/// 
/// PERFORMANCE: ~100x faster than naive moving average for large windows.
/// </summary>
public class OptimizedElevationSmoother : IHeightCalculator
{
    /// <summary>
    /// Calculates target elevations for road cross-sections using optimized smoothing.
    /// Supports both Box filter (prefix-sum) and Butterworth low-pass filter based on parameters.
    /// 
    /// Processing pipeline:
    /// 1. Sample terrain elevations at cross-section centers
    /// 2. Apply longitudinal smoothing (Box or Butterworth filter)
    /// 3. Apply GlobalLevelingStrength (blend toward network average elevation)
    /// 4. Enforce RoadMaxSlopeDegrees constraint (limit maximum grade)
    /// </summary>
    public void CalculateTargetElevations(RoadGeometry geometry, float[,] heightMap, float metersPerPixel)
    {
        // Get smoothing parameters
        int windowSize = geometry.Parameters?.SmoothingWindowSize ?? 101;
        bool useButterworthFilter = geometry.Parameters?.UseButterworthFilter ?? false;
        int butterworthOrder = geometry.Parameters?.ButterworthFilterOrder ?? 3;
        float crossSectionSpacing = geometry.Parameters?.CrossSectionIntervalMeters ?? 0.5f;
        float smoothingRadiusMeters = (windowSize / 2.0f) * crossSectionSpacing;
        
        // Get global leveling and slope constraint parameters
        float globalLevelingStrength = geometry.Parameters?.GlobalLevelingStrength ?? 0.0f;
        bool enableMaxSlopeConstraint = geometry.Parameters?.EnableMaxSlopeConstraint ?? false;
        float roadMaxSlopeDegrees = geometry.Parameters?.RoadMaxSlopeDegrees ?? 4.0f;

        string filterType = useButterworthFilter 
            ? $"Butterworth (order {butterworthOrder})" 
            : "Box (prefix-sum)";
        
        Console.WriteLine($"Calculating target elevations using {filterType} filter...");
        Console.WriteLine($"  Smoothing window: {windowSize} cross-sections (~{smoothingRadiusMeters:F1}m radius)");
        
        if (globalLevelingStrength > 0.001f)
            Console.WriteLine($"  Global leveling strength: {globalLevelingStrength:P0}");
        
        if (enableMaxSlopeConstraint)
            Console.WriteLine($"  Max road slope constraint: {roadMaxSlopeDegrees:F1}° (ENABLED)");

        // Group by PathId for per-path processing
        var pathGroups = geometry.CrossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .ToList();

        int totalSections = 0;
        
        // Collect all smoothed elevations for global average calculation
        var allSmoothedElevations = new List<float>();
        var pathSmoothedArrays = new Dictionary<int, (List<CrossSection> sections, float[] smoothed)>();

        // First pass: Apply longitudinal smoothing to each path
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

            // Step 2: Apply smoothing filter based on configuration
            float[] smoothed = useButterworthFilter
                ? ButterworthLowPassFilter(rawElevations, windowSize, butterworthOrder)
                : BoxFilterPrefixSum(rawElevations, windowSize);

            pathSmoothedArrays[pathGroup.Key] = (sections, smoothed);
            allSmoothedElevations.AddRange(smoothed);
            totalSections += sections.Count;
        }

        // Step 3: Apply GlobalLevelingStrength (blend toward network average)
        if (globalLevelingStrength > 0.001f && allSmoothedElevations.Count > 0)
        {
            float globalAverage = allSmoothedElevations.Average();
            Console.WriteLine($"  Network average elevation: {globalAverage:F2}m");
            
            foreach (var kvp in pathSmoothedArrays)
            {
                var smoothed = kvp.Value.smoothed;
                for (int i = 0; i < smoothed.Length; i++)
                {
                    // Blend local elevation toward global average
                    smoothed[i] = smoothed[i] * (1.0f - globalLevelingStrength) 
                                + globalAverage * globalLevelingStrength;
                }
            }
            
            Console.WriteLine($"  Applied global leveling: {globalLevelingStrength:P0} toward {globalAverage:F1}m");
        }

        // Step 4: Enforce RoadMaxSlopeDegrees constraint (only if enabled)
        if (enableMaxSlopeConstraint)
        {
            int constrainedSections = 0;
            
            foreach (var kvp in pathSmoothedArrays)
            {
                var smoothed = kvp.Value.smoothed;
                int modified = EnforceMaxSlopeConstraint(smoothed, crossSectionSpacing, roadMaxSlopeDegrees);
                constrainedSections += modified;
            }
            
            if (constrainedSections > 0)
                Console.WriteLine($"  Slope constraint modified {constrainedSections:N0} cross-sections");
        }

        // Step 5: Assign final elevations to cross-sections
        foreach (var kvp in pathSmoothedArrays)
        {
            var (sections, smoothed) = kvp.Value;
            for (int i = 0; i < sections.Count; i++)
            {
                sections[i].TargetElevation = smoothed[i];
            }
        }

        Console.WriteLine($"  Smoothed elevations for {totalSections:N0} cross-sections across {pathGroups.Count} path(s)");
    }

    /// <summary>
    /// Enforces maximum road slope constraint using iterative forward-backward passes.
    /// This ensures no segment exceeds the specified maximum grade.
    /// 
    /// Algorithm:
    /// 1. Calculate max rise per cross-section from slope angle
    /// 2. Forward pass: limit uphill slope (each point can't be too high relative to previous)
    /// 3. Backward pass: limit downhill slope (each point can't be too high relative to next)
    /// 4. Repeat until no changes needed (converges quickly, usually 2-3 iterations)
    /// </summary>
    /// <param name="elevations">Array of elevations to modify in-place</param>
    /// <param name="crossSectionSpacing">Distance between cross-sections in meters</param>
    /// <param name="maxSlopeDegrees">Maximum allowed slope in degrees</param>
    /// <returns>Number of elevations that were modified</returns>
    private int EnforceMaxSlopeConstraint(float[] elevations, float crossSectionSpacing, float maxSlopeDegrees)
    {
        int n = elevations.Length;
        if (n < 2) return 0;

        // Convert slope angle to max rise per cross-section
        float maxSlopeRatio = MathF.Tan(maxSlopeDegrees * MathF.PI / 180.0f);
        float maxRise = maxSlopeRatio * crossSectionSpacing;

        int totalModified = 0;
        bool changed = true;
        int iterations = 0;
        const int maxIterations = 10; // Safety limit

        while (changed && iterations < maxIterations)
        {
            changed = false;
            iterations++;

            // Forward pass: limit uphill slope
            for (int i = 1; i < n; i++)
            {
                float maxAllowed = elevations[i - 1] + maxRise;
                if (elevations[i] > maxAllowed)
                {
                    elevations[i] = maxAllowed;
                    changed = true;
                    totalModified++;
                }
            }

            // Backward pass: limit downhill slope (from the other direction)
            for (int i = n - 2; i >= 0; i--)
            {
                float maxAllowed = elevations[i + 1] + maxRise;
                if (elevations[i] > maxAllowed)
                {
                    elevations[i] = maxAllowed;
                    changed = true;
                    totalModified++;
                }
            }
        }

        return totalModified;
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

    /// <summary>
    /// Butterworth low-pass filter for maximally flat passband.
    /// Uses zero-phase forward-backward filtering (filtfilt) to avoid phase shift.
    /// 
    /// The cutoff frequency is derived from the window size:
    /// - Larger window = lower cutoff = more smoothing
    /// - Smaller window = higher cutoff = less smoothing
    /// 
    /// Algorithm:
    /// 1. Convert window size to normalized cutoff frequency
    /// 2. Pre-warp for bilinear transform (analog to digital)
    /// 3. Apply cascaded biquad sections (2nd-order each)
    /// 4. Forward-backward filtering for zero phase shift
    /// </summary>
    /// <param name="input">Raw elevation samples</param>
    /// <param name="windowSize">Equivalent window size (for cutoff calculation)</param>
    /// <param name="order">Filter order (1-8, higher = sharper cutoff)</param>
    /// <returns>Smoothed elevation samples</returns>
    private float[] ButterworthLowPassFilter(float[] input, int windowSize, int order)
    {
        int n = input.Length;
        if (n < 3) return (float[])input.Clone();
        
        // Clamp order to valid range
        order = Math.Clamp(order, 1, 8);

        // Convert window size to normalized cutoff frequency (0.0 to 1.0, relative to Nyquist)
        // A window of W samples corresponds to keeping frequencies with period > W samples
        // Normalized frequency = 2.0 / windowSize (where 1.0 = Nyquist = Fs/2)
        float cutoffNormalized = 2.0f / windowSize;
        cutoffNormalized = Math.Clamp(cutoffNormalized, 0.001f, 0.99f);

        // Pre-warp the cutoff frequency for bilinear transform
        // This compensates for frequency warping in the analog-to-digital conversion
        float wc = MathF.Tan(MathF.PI * cutoffNormalized / 2.0f);

        // Start with input signal
        var result = (float[])input.Clone();

        // Apply cascaded biquad sections (each section is 2nd order)
        // For odd orders, we need (order+1)/2 sections (last one is 1st order, but we approximate with 2nd)
        int numSections = (order + 1) / 2;
        
        for (int section = 0; section < numSections; section++)
        {
            // Calculate pole angle for this section
            // Butterworth poles are evenly distributed on left half of unit circle
            float theta = MathF.PI * (2 * section + 1) / (2 * order);
            float alpha = -MathF.Sin(theta);  // Real part of pole (negative for stability)
            
            // For odd order and last section, use first-order approximation
            bool isFirstOrderSection = (order % 2 == 1) && (section == numSections - 1);
            
            if (isFirstOrderSection)
            {
                // First-order lowpass section
                // H(s) = wc / (s + wc)
                // Bilinear transform gives:
                float k = wc + 1.0f;
                float b0 = wc / k;
                float b1 = b0;
                float a1 = (wc - 1.0f) / k;
                
                result = ApplyFirstOrderSection(result, b0, b1, a1);
            }
            else
            {
                // Second-order (biquad) section
                // Calculate bilinear transform coefficients
                float wc2 = wc * wc;
                float k1 = -2.0f * wc * alpha;  // Note: alpha is already negative
                float k2 = wc2 + k1 + 1.0f;
                
                // Numerator coefficients (lowpass: all zeros at z = -1)
                float b0 = wc2 / k2;
                float b1 = 2.0f * b0;
                float b2 = b0;
                
                // Denominator coefficients
                float a1 = 2.0f * (wc2 - 1.0f) / k2;
                float a2 = (wc2 - k1 + 1.0f) / k2;
                
                result = ApplyBiquadSectionZeroPhase(result, b0, b1, b2, a1, a2);
            }
        }

        return result;
    }

    /// <summary>
    /// Apply a first-order IIR section with zero-phase (forward-backward) filtering.
    /// </summary>
    private float[] ApplyFirstOrderSection(float[] input, float b0, float b1, float a1)
    {
        int n = input.Length;
        var forward = new float[n];
        var result = new float[n];
        
        // Forward pass
        float x1 = input[0];
        float y1 = input[0];
        
        for (int i = 0; i < n; i++)
        {
            float x0 = input[i];
            float y0 = b0 * x0 + b1 * x1 - a1 * y1;
            forward[i] = y0;
            x1 = x0;
            y1 = y0;
        }

        // Backward pass (zero-phase filtering)
        x1 = forward[n - 1];
        y1 = forward[n - 1];
        
        for (int i = n - 1; i >= 0; i--)
        {
            float x0 = forward[i];
            float y0 = b0 * x0 + b1 * x1 - a1 * y1;
            result[i] = y0;
            x1 = x0;
            y1 = y0;
        }

        return result;
    }

    /// <summary>
    /// Apply a biquad (second-order IIR) section with zero-phase (forward-backward) filtering.
    /// This eliminates phase distortion that would otherwise shift features along the road.
    /// </summary>
    private float[] ApplyBiquadSectionZeroPhase(float[] input, float b0, float b1, float b2, float a1, float a2)
    {
        int n = input.Length;
        var forward = new float[n];
        var result = new float[n];

        // Initialize state variables with signal start value to minimize transients
        float x1 = input[0], x2 = input[0];
        float y1 = input[0], y2 = input[0];

        // Forward pass
        for (int i = 0; i < n; i++)
        {
            float x0 = input[i];
            float y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            forward[i] = y0;
            
            // Shift state
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
        }

        // Backward pass (zero-phase filtering - eliminates phase shift)
        x1 = forward[n - 1]; x2 = forward[n - 1];
        y1 = forward[n - 1]; y2 = forward[n - 1];

        for (int i = n - 1; i >= 0; i--)
        {
            float x0 = forward[i];
            float y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            result[i] = y0;
            
            // Shift state
            x2 = x1; x1 = x0;
            y2 = y1; y1 = y0;
        }

        return result;
    }
}
