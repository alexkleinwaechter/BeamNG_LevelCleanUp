using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Anchor elevation data for a spline endpoint at a junction.
///     Used by WI-6 to bias smoothed elevations toward the terrain elevation at junction centers.
/// </summary>
public readonly struct EndpointAnchor
{
    /// <summary>Terrain elevation sampled at the junction center.</summary>
    public float AnchorElevation { get; init; }

    /// <summary>Decay distance in meters for the exponential blend (typically matches junction blend distance).</summary>
    public float DecayDistanceMeters { get; init; }
}

/// <summary>
///     Optimized elevation smoothing with support for Box filter (prefix sums) and Butterworth low-pass filter.
///     Box Filter: O(N) using prefix sums - fast, suitable for flat terrain.
///     Butterworth Filter: O(N x order) - maximally flat passband, ideal for hilly terrain.
///     PERFORMANCE: ~100x faster than naive moving average for large windows.
/// </summary>
public class OptimizedElevationSmoother : IHeightCalculator
{
    /// <summary>
    ///     Legacy overload: wraps RoadGeometry cross-sections into UnifiedCrossSections
    ///     and delegates to the primary implementation.
    /// </summary>
    public void CalculateTargetElevations(RoadGeometry geometry, float[,] heightMap, float metersPerPixel)
    {
        var crossSections = geometry.CrossSections;
        var parameters = geometry.Parameters;

        // Convert CrossSections → UnifiedCrossSections for the primary implementation
        var unified = new List<UnifiedCrossSection>(crossSections.Count);
        foreach (var cs in crossSections)
            unified.Add(UnifiedCrossSection.FromCrossSection(cs, cs.PathId, 0, 0));

        CalculateTargetElevations(unified, parameters, heightMap, metersPerPixel);

        // Copy results back to the original CrossSection objects
        for (var i = 0; i < crossSections.Count; i++)
            crossSections[i].TargetElevation = unified[i].TargetElevation;
    }

    /// <summary>
    ///     Primary implementation: calculates target elevations for UnifiedCrossSections directly.
    ///     Avoids the RoadGeometry/CrossSection conversion roundtrip.
    ///     Processing pipeline:
    ///     1. Sample terrain elevations at cross-section centers
    ///     2. Apply longitudinal smoothing (Box or Butterworth filter)
    ///     3. Apply GlobalLevelingStrength (blend toward network average elevation)
    ///     4. Enforce RoadMaxSlopeDegrees constraint (limit maximum grade)
    /// </summary>
    public void CalculateTargetElevations(
        List<UnifiedCrossSection> crossSections,
        RoadSmoothingParameters parameters,
        float[,] heightMap,
        float metersPerPixel)
    {
        // Get smoothing parameters from SplineParameters
        var splineParams = parameters?.GetSplineParameters();
        var windowSize = splineParams?.SmoothingWindowSize ?? 301;
        var useButterworthFilter = splineParams?.UseButterworthFilter ?? false;
        var butterworthOrder = splineParams?.ButterworthFilterOrder ?? 4;
        var crossSectionSpacing = parameters?.CrossSectionIntervalMeters ?? 0.5f;
        var smoothingRadiusMeters = windowSize / 2.0f * crossSectionSpacing;

        // Get global leveling and slope constraint parameters
        var globalLevelingStrength = splineParams?.GlobalLevelingStrength ?? 0.0f;
        var enableMaxSlopeConstraint = parameters?.EnableMaxSlopeConstraint ?? false;
        var roadMaxSlopeDegrees = parameters?.RoadMaxSlopeDegrees ?? 6.0f;

        var filterType = useButterworthFilter
            ? $"Butterworth (order {butterworthOrder})"
            : "Box (prefix-sum)";

        TerrainCreationLogger.Current?.Detail($"Calculating target elevations using {filterType} filter...");
        TerrainCreationLogger.Current?.Detail($"Smoothing window: {windowSize} cross-sections (~{smoothingRadiusMeters:F1}m radius)");

        if (globalLevelingStrength > 0.001f)
            TerrainCreationLogger.Current?.Detail($"Global leveling strength: {globalLevelingStrength:P0}");

        if (enableMaxSlopeConstraint)
            TerrainCreationLogger.Current?.Detail($"Max road slope constraint: {roadMaxSlopeDegrees:F1}\u00b0 (ENABLED)");

        // Group by OwnerSplineId for per-spline processing
        var splineGroups = crossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.OwnerSplineId)
            .ToList();

        var totalSections = 0;

        // Collect all smoothed elevations for global average calculation
        var allSmoothedElevations = new List<float>();
        var splineSmoothedArrays = new Dictionary<int, (List<UnifiedCrossSection> sections, float[] smoothed)>();

        // First pass: Apply longitudinal smoothing to each spline
        var invalidSamplesTotal = 0;

        foreach (var splineGroup in splineGroups)
        {
            var sections = splineGroup.OrderBy(cs => cs.LocalIndex).ToList();

            if (sections.Count == 0) continue;

            // Step 1: Sample terrain elevations at cross-section centers
            var rawElevations = new float[sections.Count];
            var invalidSamplesInPath = 0;

            for (var i = 0; i < sections.Count; i++)
            {
                var cs = sections[i];
                var px = (int)(cs.CenterPoint.X / metersPerPixel);
                var py = (int)(cs.CenterPoint.Y / metersPerPixel);

                px = Math.Clamp(px, 0, heightMap.GetLength(1) - 1);
                py = Math.Clamp(py, 0, heightMap.GetLength(0) - 1);

                var sampledElevation = heightMap[py, px];

                // CRITICAL FIX: Detect and handle invalid elevation samples
                // Invalid samples (NaN, Infinity, extremely negative) cause terrain spikes
                // Note: We do NOT reject 0.0f because valid terrain can be at sea level
                if (float.IsNaN(sampledElevation) || float.IsInfinity(sampledElevation) ||
                    sampledElevation < -1000.0f)
                {
                    invalidSamplesInPath++;
                    // Use interpolation from neighbors if available, otherwise sample nearby
                    if (i > 0 && !float.IsNaN(rawElevations[i - 1]))
                        sampledElevation = rawElevations[i - 1]; // Use previous valid value
                    else
                        // Sample from nearby valid pixels instead
                        sampledElevation = SampleValidNeighborElevation(heightMap, px, py);
                }

                rawElevations[i] = sampledElevation;
            }

            if (invalidSamplesInPath > 0) invalidSamplesTotal += invalidSamplesInPath;

            // Step 2: Apply smoothing filter based on configuration
            var smoothed = useButterworthFilter
                ? ButterworthLowPassFilter(rawElevations, windowSize, butterworthOrder)
                : BoxFilterPrefixSum(rawElevations, windowSize);

            splineSmoothedArrays[splineGroup.Key] = (sections, smoothed);
            allSmoothedElevations.AddRange(smoothed);
            totalSections += sections.Count;
        }

        // Step 3: Apply GlobalLevelingStrength (blend toward network average)
        if (globalLevelingStrength > 0.001f && allSmoothedElevations.Count > 0)
        {
            var globalAverage = allSmoothedElevations.Average();
            TerrainCreationLogger.Current?.Detail($"Network average elevation: {globalAverage:F2}m");

            foreach (var kvp in splineSmoothedArrays)
            {
                var smoothed = kvp.Value.smoothed;
                for (var i = 0; i < smoothed.Length; i++)
                    // Blend local elevation toward global average
                    smoothed[i] = smoothed[i] * (1.0f - globalLevelingStrength)
                                  + globalAverage * globalLevelingStrength;
            }

            TerrainCreationLogger.Current?.Detail($"Applied global leveling: {globalLevelingStrength:P0} toward {globalAverage:F1}m");
        }

        // Step 4: Enforce RoadMaxSlopeDegrees constraint (only if enabled)
        if (enableMaxSlopeConstraint)
        {
            var constrainedSections = 0;

            foreach (var kvp in splineSmoothedArrays)
            {
                var smoothed = kvp.Value.smoothed;
                var modified = EnforceMaxSlopeConstraint(smoothed, crossSectionSpacing, roadMaxSlopeDegrees);
                constrainedSections += modified;
            }

            if (constrainedSections > 0)
                TerrainCreationLogger.Current?.Detail($"Slope constraint modified {constrainedSections:N0} cross-sections");
        }

        // Step 5: Assign final elevations directly to UnifiedCrossSections
        foreach (var kvp in splineSmoothedArrays)
        {
            var (sections, smoothed) = kvp.Value;
            for (var i = 0; i < sections.Count; i++) sections[i].TargetElevation = smoothed[i];
        }

        TerrainCreationLogger.Current?.Detail(
            $"Smoothed elevations for {totalSections:N0} cross-sections across {splineGroups.Count} spline(s)");

        if (invalidSamplesTotal > 0)
            TerrainLogger.Warning(
                $"  WARNING: Found {invalidSamplesTotal} invalid elevation samples (zero/NaN) - interpolated from neighbors");
    }

    /// <summary>
    ///     Applies endpoint anchoring to smoothed elevations (WI-6).
    ///     For each spline endpoint that participates in a junction, biases the smoothed
    ///     elevation profile toward the terrain elevation at the junction center using
    ///     exponential decay. This reduces the gap between Phase 2 smoothed elevations
    ///     and Phase 3 harmonized elevations, leading to smaller corrections and smoother results.
    /// </summary>
    /// <param name="crossSections">Cross-sections for a single spline, ordered by LocalIndex.</param>
    /// <param name="startAnchor">Anchor for the spline start endpoint (null if no junction).</param>
    /// <param name="endAnchor">Anchor for the spline end endpoint (null if no junction).</param>
    public void ApplyEndpointAnchoring(
        List<UnifiedCrossSection> crossSections,
        EndpointAnchor? startAnchor,
        EndpointAnchor? endAnchor)
    {
        if (crossSections.Count == 0) return;
        if (!startAnchor.HasValue && !endAnchor.HasValue) return;

        var totalLength = crossSections[^1].DistanceAlongSpline;
        if (totalLength < 0.01f) return;

        var anchored = 0;

        for (var i = 0; i < crossSections.Count; i++)
        {
            var cs = crossSections[i];
            var originalElevation = cs.TargetElevation;

            // Accumulate weighted anchor contributions from both ends.
            // Each anchor applies an exponential decay: weight = 0.5 * exp(-dist / decay).
            // For splines with both endpoints at junctions, the decays from both ends
            // naturally blend in the middle.
            float totalWeight = 0f;
            float weightedElevation = 0f;

            if (startAnchor.HasValue && startAnchor.Value.DecayDistanceMeters > 0.01f)
            {
                var w = 0.5f * MathF.Exp(-cs.DistanceAlongSpline / startAnchor.Value.DecayDistanceMeters);
                totalWeight += w;
                weightedElevation += w * startAnchor.Value.AnchorElevation;
            }

            if (endAnchor.HasValue && endAnchor.Value.DecayDistanceMeters > 0.01f)
            {
                var distFromEnd = totalLength - cs.DistanceAlongSpline;
                var w = 0.5f * MathF.Exp(-distFromEnd / endAnchor.Value.DecayDistanceMeters);
                totalWeight += w;
                weightedElevation += w * endAnchor.Value.AnchorElevation;
            }

            if (totalWeight > 0.001f)
            {
                // Clamp total weight to prevent over-anchoring on very short splines
                totalWeight = MathF.Min(totalWeight, 0.8f);
                // Blend: (1 - totalWeight) * smoothed + totalWeight * (weighted average of anchors)
                var anchorAvg = weightedElevation / totalWeight;
                cs.TargetElevation = cs.TargetElevation * (1f - totalWeight) + anchorAvg * totalWeight;

                if (MathF.Abs(cs.TargetElevation - originalElevation) > 0.001f)
                    anchored++;
            }
        }

        if (anchored > 0)
            TerrainCreationLogger.Current?.Detail(
                $"  Endpoint anchoring modified {anchored} cross-sections (spline {crossSections[0].OwnerSplineId})");
    }

    /// <summary>
    ///     Re-smooths elevations using existing TargetElevation values as input instead of sampling
    ///     from the heightmap. Used in iterative junction refinement where the smoother operates
    ///     on already-harmonized profiles to produce smoother results.
    /// </summary>
    public void ReSmoothFromExistingElevations(
        List<UnifiedCrossSection> crossSections,
        RoadSmoothingParameters parameters)
    {
        var splineParams = parameters?.GetSplineParameters();
        var windowSize = splineParams?.SmoothingWindowSize ?? 301;
        var useButterworthFilter = splineParams?.UseButterworthFilter ?? false;
        var butterworthOrder = splineParams?.ButterworthFilterOrder ?? 4;
        var crossSectionSpacing = parameters?.CrossSectionIntervalMeters ?? 0.5f;
        var enableMaxSlopeConstraint = parameters?.EnableMaxSlopeConstraint ?? false;
        var roadMaxSlopeDegrees = parameters?.RoadMaxSlopeDegrees ?? 6.0f;

        // Group by OwnerSplineId for per-spline processing
        var splineGroups = crossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.OwnerSplineId)
            .ToList();

        foreach (var splineGroup in splineGroups)
        {
            var sections = splineGroup.OrderBy(cs => cs.LocalIndex).ToList();
            if (sections.Count == 0) continue;

            // Use existing TargetElevation as raw input instead of sampling heightmap
            var rawElevations = new float[sections.Count];
            for (var i = 0; i < sections.Count; i++)
                rawElevations[i] = sections[i].TargetElevation;

            // Apply smoothing filter
            var smoothed = useButterworthFilter
                ? ButterworthLowPassFilter(rawElevations, windowSize, butterworthOrder)
                : BoxFilterPrefixSum(rawElevations, windowSize);

            // Enforce max slope constraint if enabled
            if (enableMaxSlopeConstraint)
                EnforceMaxSlopeConstraint(smoothed, crossSectionSpacing, roadMaxSlopeDegrees);

            // Assign final elevations
            for (var i = 0; i < sections.Count; i++)
                sections[i].TargetElevation = smoothed[i];
        }
    }

    /// <summary>
    ///     Samples a valid elevation from neighboring pixels when the center pixel has invalid data.
    ///     Searches in expanding rings until a valid value is found.
    /// </summary>
    private float SampleValidNeighborElevation(float[,] heightMap, int centerX, int centerY)
    {
        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        // Search in expanding rings (1, 2, 3, 5, 10 pixels)
        int[] searchRadii = { 1, 2, 3, 5, 10 };

        foreach (var radius in searchRadii)
        {
            float sum = 0;
            var count = 0;

            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                // Only check edge of ring (not interior)
                if (Math.Abs(dx) != radius && Math.Abs(dy) != radius) continue;

                var nx = centerX + dx;
                var ny = centerY + dy;

                if (nx >= 0 && nx < mapWidth && ny >= 0 && ny < mapHeight)
                {
                    var val = heightMap[ny, nx];
                    // Check if this is a valid value (NaN, Infinity, or extremely negative are invalid)
                    // Note: 0.0f IS valid - terrain can be at sea level
                    if (!float.IsNaN(val) && !float.IsInfinity(val) && val >= -1000.0f)
                    {
                        sum += val;
                        count++;
                    }
                }
            }

            if (count > 0) return sum / count;
        }

        // Fallback: no valid neighbors found - use a reasonable default
        // This shouldn't happen in practice, but prevents crashes
        TerrainCreationLogger.Current?.Detail($"No valid neighbor elevation found at ({centerX}, {centerY}) - using fallback");
        return 100.0f; // Reasonable default elevation
    }

    /// <summary>
    ///     Enforces maximum road slope constraint using iterative forward-backward passes.
    ///     This ensures no segment exceeds the specified maximum grade.
    ///     Algorithm:
    ///     1. Calculate max rise per cross-section from slope angle
    ///     2. Forward pass: limit uphill slope (each point can't be too high relative to previous)
    ///     3. Backward pass: limit downhill slope (each point can't be too high relative to next)
    ///     4. Repeat until no changes needed (converges quickly, usually 2-3 iterations)
    /// </summary>
    /// <param name="elevations">Array of elevations to modify in-place</param>
    /// <param name="crossSectionSpacing">Distance between cross-sections in meters</param>
    /// <param name="maxSlopeDegrees">Maximum allowed slope in degrees</param>
    /// <returns>Number of elevations that were modified</returns>
    private int EnforceMaxSlopeConstraint(
        float[] elevations, float crossSectionSpacing, float maxSlopeDegrees, bool[]? exempt = null)
    {
        var n = elevations.Length;
        if (n < 2) return 0;

        // Convert slope angle to max rise per cross-section
        var maxSlopeRatio = MathF.Tan(maxSlopeDegrees * MathF.PI / 180.0f);
        var maxRise = maxSlopeRatio * crossSectionSpacing;

        var totalModified = 0;
        var changed = true;
        var iterations = 0;
        const int maxIterations = 10; // Safety limit

        while (changed && iterations < maxIterations)
        {
            changed = false;
            iterations++;

            // Forward pass: limit uphill slope
            for (var i = 1; i < n; i++)
            {
                if (exempt != null && exempt[i]) continue; // bridge-deck pin + ramp neighbourhood (§7 step 4)
                var maxAllowed = elevations[i - 1] + maxRise;
                if (elevations[i] > maxAllowed)
                {
                    elevations[i] = maxAllowed;
                    changed = true;
                    totalModified++;
                }
            }

            // Backward pass: limit downhill slope (from the other direction)
            for (var i = n - 2; i >= 0; i--)
            {
                if (exempt != null && exempt[i]) continue; // bridge-deck pin + ramp neighbourhood (§7 step 4)
                var maxAllowed = elevations[i + 1] + maxRise;
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
    ///     O(N) box filter using prefix sums.
    ///     Equivalent to moving average but 100x faster for large windows.
    ///     Algorithm:
    ///     1. Build cumulative sum array: prefixSum[i] = sum(input[0..i-1])
    ///     2. For each position i: avg = (prefixSum[right+1] - prefixSum[left]) / count
    /// </summary>
    private float[] BoxFilterPrefixSum(float[] input, int windowSize)
    {
        var n = input.Length;
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

        for (var i = 0; i < n; i++) prefixSum[i + 1] = prefixSum[i] + input[i];

        // Apply box filter: O(N) - each lookup is O(1)
        var halfWindow = windowSize / 2;

        for (var i = 0; i < n; i++)
        {
            var left = Math.Max(0, i - halfWindow);
            var right = Math.Min(n - 1, i + halfWindow);

            // Range sum in O(1) using prefix sums
            var sum = prefixSum[right + 1] - prefixSum[left];
            var count = right - left + 1;

            result[i] = sum / count;
        }

        return result;
    }

    /// <summary>
    ///     Butterworth low-pass filter for maximally flat passband.
    ///     Uses zero-phase forward-backward filtering (filtfilt) to avoid phase shift.
    ///     The cutoff frequency is derived from the window size:
    ///     - Larger window = lower cutoff = more smoothing
    ///     - Smaller window = higher cutoff = less smoothing
    ///     Algorithm:
    ///     1. Convert window size to normalized cutoff frequency
    ///     2. Pre-warp for bilinear transform (analog to digital)
    ///     3. Apply cascaded biquad sections (2nd-order each)
    ///     4. Forward-backward filtering for zero phase shift
    /// </summary>
    /// <param name="input">Raw elevation samples</param>
    /// <param name="windowSize">Equivalent window size (for cutoff calculation)</param>
    /// <param name="order">Filter order (1-8, higher = sharper cutoff)</param>
    /// <returns>Smoothed elevation samples</returns>
    private float[] ButterworthLowPassFilter(float[] input, int windowSize, int order)
    {
        var n = input.Length;
        if (n < 3) return (float[])input.Clone();

        // Clamp order to valid range
        order = Math.Clamp(order, 1, 8);

        // Convert window size to normalized cutoff frequency (0.0 to 1.0, relative to Nyquist)
        // A window of W samples corresponds to keeping frequencies with period > W samples
        // Normalized frequency = 2.0 / windowSize (where 1.0 = Nyquist = Fs/2)
        var cutoffNormalized = 2.0f / windowSize;
        cutoffNormalized = Math.Clamp(cutoffNormalized, 0.001f, 0.99f);

        // Pre-warp the cutoff frequency for bilinear transform
        // This compensates for frequency warping in the analog-to-digital conversion
        var wc = MathF.Tan(MathF.PI * cutoffNormalized / 2.0f);

        // Start with input signal
        var result = (float[])input.Clone();

        // Apply cascaded biquad sections (each section is 2nd order)
        // For odd orders, we need (order+1)/2 sections (last one is 1st order, but we approximate with 2nd)
        var numSections = (order + 1) / 2;

        for (var section = 0; section < numSections; section++)
        {
            // Calculate pole angle for this section
            // Butterworth poles are evenly distributed on left half of unit circle
            var theta = MathF.PI * (2 * section + 1) / (2 * order);
            var alpha = -MathF.Sin(theta); // Real part of pole (negative for stability)

            // For odd order and last section, use first-order approximation
            var isFirstOrderSection = order % 2 == 1 && section == numSections - 1;

            if (isFirstOrderSection)
            {
                // First-order lowpass section
                // H(s) = wc / (s + wc)
                // Bilinear transform gives:
                var k = wc + 1.0f;
                var b0 = wc / k;
                var b1 = b0;
                var a1 = (wc - 1.0f) / k;

                result = ApplyFirstOrderSection(result, b0, b1, a1);
            }
            else
            {
                // Second-order (biquad) section
                // Calculate bilinear transform coefficients
                var wc2 = wc * wc;
                var k1 = -2.0f * wc * alpha; // Note: alpha is already negative
                var k2 = wc2 + k1 + 1.0f;

                // Numerator coefficients (lowpass: all zeros at z = -1)
                var b0 = wc2 / k2;
                var b1 = 2.0f * b0;
                var b2 = b0;

                // Denominator coefficients
                var a1 = 2.0f * (wc2 - 1.0f) / k2;
                var a2 = (wc2 - k1 + 1.0f) / k2;

                result = ApplyBiquadSectionZeroPhase(result, b0, b1, b2, a1, a2);
            }
        }

        return result;
    }

    /// <summary>
    ///     Apply a first-order IIR section with zero-phase (forward-backward) filtering.
    /// </summary>
    private float[] ApplyFirstOrderSection(float[] input, float b0, float b1, float a1)
    {
        var n = input.Length;
        var forward = new float[n];
        var result = new float[n];

        // Forward pass
        var x1 = input[0];
        var y1 = input[0];

        for (var i = 0; i < n; i++)
        {
            var x0 = input[i];
            var y0 = b0 * x0 + b1 * x1 - a1 * y1;
            forward[i] = y0;
            x1 = x0;
            y1 = y0;
        }

        // Backward pass (zero-phase filtering)
        x1 = forward[n - 1];
        y1 = forward[n - 1];

        for (var i = n - 1; i >= 0; i--)
        {
            var x0 = forward[i];
            var y0 = b0 * x0 + b1 * x1 - a1 * y1;
            result[i] = y0;
            x1 = x0;
            y1 = y0;
        }

        return result;
    }

    /// <summary>
    ///     Apply a biquad (second-order IIR) section with zero-phase (forward-backward) filtering.
    ///     This eliminates phase distortion that would otherwise shift features along the road.
    /// </summary>
    private float[] ApplyBiquadSectionZeroPhase(float[] input, float b0, float b1, float b2, float a1, float a2)
    {
        var n = input.Length;
        var forward = new float[n];
        var result = new float[n];

        // Initialize state variables with signal start value to minimize transients
        float x1 = input[0], x2 = input[0];
        float y1 = input[0], y2 = input[0];

        // Forward pass
        for (var i = 0; i < n; i++)
        {
            var x0 = input[i];
            var y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            forward[i] = y0;

            // Shift state
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        // Backward pass (zero-phase filtering - eliminates phase shift)
        x1 = forward[n - 1];
        x2 = forward[n - 1];
        y1 = forward[n - 1];
        y2 = forward[n - 1];

        for (var i = n - 1; i >= 0; i--)
        {
            var x0 = forward[i];
            var y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            result[i] = y0;

            // Shift state
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }

        return result;
    }

    // ========================================
    // CHAIN-AWARE ELEVATION FILTERING
    // ========================================

    /// <summary>
    ///     Calculates target elevations for a chain of concatenated cross-sections.
    ///     Samples terrain, applies smoothing filter, global leveling, and slope constraint
    ///     on the full chain profile — preventing boundary artifacts at spline joints.
    /// </summary>
    /// <param name="chainCrossSections">Concatenated cross-sections from all splines in chain order (already deduped).</param>
    /// <param name="parameters">Smoothing parameters (from the highest-priority spline in the chain).</param>
    /// <param name="heightMap">Terrain heightmap for sampling.</param>
    /// <param name="metersPerPixel">Heightmap resolution.</param>
    public void CalculateChainElevations(
        List<UnifiedCrossSection> chainCrossSections,
        RoadSmoothingParameters parameters,
        float[,] heightMap,
        float metersPerPixel)
    {
        if (chainCrossSections.Count == 0) return;

        var splineParams = parameters?.GetSplineParameters();
        var windowSize = splineParams?.SmoothingWindowSize ?? 301;
        var useButterworthFilter = splineParams?.UseButterworthFilter ?? false;
        var butterworthOrder = splineParams?.ButterworthFilterOrder ?? 4;
        var crossSectionSpacing = parameters?.CrossSectionIntervalMeters ?? 0.5f;
        var enableMaxSlopeConstraint = parameters?.EnableMaxSlopeConstraint ?? false;
        var roadMaxSlopeDegrees = parameters?.RoadMaxSlopeDegrees ?? 6.0f;

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        // Step 1: Sample terrain elevations for the entire chain
        var rawElevations = new float[chainCrossSections.Count];
        for (var i = 0; i < chainCrossSections.Count; i++)
        {
            var cs = chainCrossSections[i];
            var px = Math.Clamp((int)(cs.CenterPoint.X / metersPerPixel), 0, mapWidth - 1);
            var py = Math.Clamp((int)(cs.CenterPoint.Y / metersPerPixel), 0, mapHeight - 1);

            var sampledElevation = heightMap[py, px];
            if (float.IsNaN(sampledElevation) || float.IsInfinity(sampledElevation) ||
                sampledElevation < -1000.0f)
            {
                sampledElevation = i > 0 && !float.IsNaN(rawElevations[i - 1])
                    ? rawElevations[i - 1]
                    : SampleValidNeighborElevation(heightMap, px, py);
            }

            rawElevations[i] = sampledElevation;
            cs.OriginalTerrainElevation = sampledElevation;
        }

        // Step 1.5: bridge-deck pins (plan doc 14 §7, Phase C). Overwrite the raw terrain sample on pinned span
        // sections with the planner's required deck Z so the filter input near the deck is the deck, not the
        // river/terrain it was burying into.
        var hasPins = ApplyPinsToRaw(chainCrossSections, rawElevations);

        // Step 1.55 (Amendment 03 v3, "give the bridge cross-sections"): SOFT span shaping — replace the
        // raw terrain under each soft-shaped span (the river!) with a chord anchored at the ACTUAL approach
        // raws plus the planner's clearance humps. Nothing is hard-held: the filter solves the span like
        // ordinary road, so both abutment seams are continuous by construction and the deck gets a natural
        // vertical curve. Anchors re-read the current raw each call, so iterations converge onto the
        // solved approaches.
        var hasSoft = ApplySoftShapingToRaw(chainCrossSections, rawElevations);

        // Step 1.6 (Amendment 03 v2): soft approach ramps — feather the deck-end delta into the RAW input
        // outside each pinned/soft run, so the filter's output CLIMBS to the deck instead of stopping
        // half-way (doc 16 §3b abutment step) — soft runs included (2026-07-13), so the climb is the
        // engineered class-grade ramp, not the filter window's smear of the deck-edge step. The approaches
        // stay un-held: the filter blends the ramp with the real road context, so estimate errors smooth
        // out instead of being stamped (the render-#5 crumple).
        if ((hasPins || hasSoft) && parameters?.BridgeRules?.EnableSparseDeckConstraints == true)
            FeatherRawApproachRamps(chainCrossSections, rawElevations, crossSectionSpacing);

        // Step 2: Apply smoothing filter on the full chain
        var smoothed = useButterworthFilter
            ? ButterworthLowPassFilter(rawElevations, windowSize, butterworthOrder)
            : BoxFilterPrefixSum(rawElevations, windowSize);

        // Step 2.5: hard-hold the pin AFTER the filter — the box filter at a span edge is a symmetric blur, not
        // a clean ramp, so without this the deck edge sags toward terrain and never reaches the pin (§7 step 1).
        if (hasPins)
            HardHoldPins(chainCrossSections, smoothed);

        // Step 3: Enforce max slope constraint on the full chain (no kinks at spline joints). Exempt the pinned
        // deck + its rising-ramp neighbourhood (§7 step 4) — clamping there would flatten the intentionally steep
        // approach ramp and pull the deck down (consistent with the no-grade-clamp stance).
        if (enableMaxSlopeConstraint)
            EnforceMaxSlopeConstraint(smoothed, crossSectionSpacing, roadMaxSlopeDegrees,
                hasPins ? BuildPinExemptMask(chainCrossSections, windowSize) : null);

        // Step 4: Assign final elevations
        for (var i = 0; i < chainCrossSections.Count; i++)
            chainCrossSections[i].TargetElevation = smoothed[i];
    }

    /// <summary>
    ///     Re-smooths a chain of cross-sections using existing TargetElevation values as input.
    ///     Used in iterative junction refinement (iterations 1+). Must operate on chains
    ///     to avoid reintroducing boundary artifacts that chain-based smoothing eliminated.
    /// </summary>
    /// <param name="chainCrossSections">Concatenated cross-sections from all splines in chain order (already deduped).</param>
    /// <param name="parameters">Smoothing parameters.</param>
    public void ReSmoothChainFromExistingElevations(
        List<UnifiedCrossSection> chainCrossSections,
        RoadSmoothingParameters parameters)
    {
        if (chainCrossSections.Count == 0) return;

        var splineParams = parameters?.GetSplineParameters();
        var windowSize = splineParams?.SmoothingWindowSize ?? 301;
        var useButterworthFilter = splineParams?.UseButterworthFilter ?? false;
        var butterworthOrder = splineParams?.ButterworthFilterOrder ?? 4;
        var crossSectionSpacing = parameters?.CrossSectionIntervalMeters ?? 0.5f;
        var enableMaxSlopeConstraint = parameters?.EnableMaxSlopeConstraint ?? false;
        var roadMaxSlopeDegrees = parameters?.RoadMaxSlopeDegrees ?? 6.0f;

        // Use existing TargetElevation as raw input
        var rawElevations = new float[chainCrossSections.Count];
        for (var i = 0; i < chainCrossSections.Count; i++)
            rawElevations[i] = chainCrossSections[i].TargetElevation;

        // Re-apply bridge-deck pins each iteration (plan doc 14 §7 step 2) — without this the deck drifts back
        // toward terrain a little on every re-smooth pass.
        var hasPins = ApplyPinsToRaw(chainCrossSections, rawElevations);

        // Amendment 03 v3: re-shape the soft spans each iteration — the chord re-anchors on the PREVIOUS
        // iteration's solved approaches, so the deck converges flush while the humps keep the clearance.
        var hasSoft = ApplySoftShapingToRaw(chainCrossSections, rawElevations);

        // Amendment 03 v2: re-feather the soft approach ramps each iteration (soft runs included,
        // 2026-07-13). The raw base here is the PREVIOUS iteration's solved profile, so the ramp
        // converges onto the real approach — flush seams.
        if ((hasPins || hasSoft) && parameters?.BridgeRules?.EnableSparseDeckConstraints == true)
            FeatherRawApproachRamps(chainCrossSections, rawElevations, crossSectionSpacing);

        // Apply smoothing filter on the full chain
        var smoothed = useButterworthFilter
            ? ButterworthLowPassFilter(rawElevations, windowSize, butterworthOrder)
            : BoxFilterPrefixSum(rawElevations, windowSize);

        if (hasPins)
            HardHoldPins(chainCrossSections, smoothed);

        // Enforce max slope constraint on the full chain
        if (enableMaxSlopeConstraint)
            EnforceMaxSlopeConstraint(smoothed, crossSectionSpacing, roadMaxSlopeDegrees,
                hasPins ? BuildPinExemptMask(chainCrossSections, windowSize) : null);

        // Assign final elevations
        for (var i = 0; i < chainCrossSections.Count; i++)
            chainCrossSections[i].TargetElevation = smoothed[i];
    }

    /// <summary>
    /// Overwrites the raw filter input on bridge-deck-pinned sections (<see cref="UnifiedCrossSection.PinnedElevation"/>)
    /// with their pinned Z. Returns true if any pin was applied (plan doc 14 §7, Phase C).
    /// </summary>
    private static bool ApplyPinsToRaw(List<UnifiedCrossSection> cs, float[] raw)
    {
        var any = false;
        for (var i = 0; i < cs.Count; i++)
            if (cs[i].PinnedElevation is { } p)
            {
                raw[i] = p;
                any = true;
            }

        return any;
    }

    /// <summary>
    /// Amendment 03 v3 ("give the bridge cross-sections"): rewrites the RAW filter input of each
    /// contiguous <see cref="UnifiedCrossSection.SoftDeckRiseMeters"/> run as `boundary-anchored chord +
    /// rise`. The chord runs between the raw values just OUTSIDE the run (the actual approaches —
    /// iteration 0: terrain; iterations 1+: the previous SOLVED road), so the span's raw input is
    /// continuous with the road on BOTH sides — the filter then produces a deck profile with natural
    /// curvature and seam steps are impossible (nothing is hard-held). The rise is the planner's eased
    /// per-crossing clearance hump, transported RELATIVE so estimate offsets cannot reach the road and a
    /// hump that reaches the span end keeps its full value (the ramp then spills into the approach via
    /// the filter window — the climb the deck needs). Iterations re-anchor and converge.
    /// </summary>
    internal static bool ApplySoftShapingToRaw(List<UnifiedCrossSection> cs, float[] raw)
    {
        var n = cs.Count;
        var i = 0;
        var any = false;
        while (i < n)
        {
            if (cs[i].SoftDeckRiseMeters is null)
            {
                i++;
                continue;
            }

            any = true;
            var runStart = i;
            while (i < n && cs[i].SoftDeckRiseMeters is not null) i++;
            var runEnd = i - 1;

            var zL = runStart > 0 ? raw[runStart - 1] : raw[runStart];
            var zR = runEnd < n - 1 ? raw[runEnd + 1] : raw[runEnd];

            var len = Math.Max(1, runEnd - runStart);
            for (var k = runStart; k <= runEnd; k++)
            {
                var t = (float)(k - runStart) / len;
                var boundaryChord = zL + (zR - zL) * t;
                raw[k] = boundaryChord + MathF.Max(0f, cs[k].SoftDeckRiseMeters!.Value);
            }
        }

        return any;
    }

    /// <summary>Soft approach ramps: slope used to size the feather length from the deck-end delta (5 %).</summary>
    private const float ApproachRampSlope = 0.05f;

    /// <summary>Soft approach ramps: minimum / maximum feather length (m).</summary>
    private const float ApproachRampMinMeters = 30f;
    private const float ApproachRampMaxMeters = 150f;

    /// <summary>
    /// Amendment 03 v2 (render #6: decks sank into under-roads): for each contiguous structure run in the
    /// chain — hard-PINNED sections or SOFT-shaped deck sections (2026-07-13: soft runs previously got no
    /// feather at all, so their approach climb was just the filter window smearing the deck-edge step) —
    /// eases the boundary delta (deck raw − raw approach) into the RAW filter input on the free approach
    /// side, shaped by <see cref="ApproachRampProfile"/> (parabolic crest at the deck, constant-grade
    /// tangent, parabolic sag at the bottom) over a length sized so the tangent runs at
    /// <see cref="ApproachRampSlope"/>. The approach sections are NOT hard-held — the filter blends this
    /// soft ramp with the real road context, so the output climbs to the deck end (the ramp the deck needs
    /// to arrive at clearance height) without stamping planner-time estimates into the road (the render-#5
    /// crumple). Stops at any other pinned/soft section (junction pins and other spans win) and never
    /// touches the run itself. Idempotent per iteration: re-smooth passes re-base the ramp on the previous
    /// solved profile, so it converges flush.
    /// </summary>
    internal static void FeatherRawApproachRamps(
        List<UnifiedCrossSection> cs, float[] raw, float crossSectionSpacing)
    {
        var n = cs.Count;
        var spacing = crossSectionSpacing > 0.01f ? crossSectionSpacing : 0.5f;

        var i = 0;
        while (i < n)
        {
            if (!IsStructureSection(cs[i]))
            {
                i++;
                continue;
            }

            // Contiguous pinned/soft structure run [runStart, runEnd].
            var runStart = i;
            while (i < n && IsStructureSection(cs[i])) i++;
            var runEnd = i - 1;

            FeatherOneSide(cs, raw, spacing, boundary: runStart, dir: -1);
            FeatherOneSide(cs, raw, spacing, boundary: runEnd, dir: +1);
        }
    }

    /// <summary>A section whose raw input is authored by a structure (hard deck/dip pin or soft deck shaping).</summary>
    private static bool IsStructureSection(UnifiedCrossSection cs) =>
        cs.PinnedElevation is not null || cs.SoftDeckRiseMeters is not null;

    private static void FeatherOneSide(
        List<UnifiedCrossSection> cs, float[] raw, float spacing, int boundary, int dir)
    {
        var n = cs.Count;
        var neighbor = boundary + dir;
        if (neighbor < 0 || neighbor >= n || IsStructureSection(cs[neighbor]))
            return; // chain end, or an adjacent structure run (junction/other span) — nothing to feather

        var delta = raw[boundary] - raw[neighbor];
        if (MathF.Abs(delta) <= 0.05f)
            return; // already flush

        var rampMeters = Math.Clamp(ApproachRampProfile.LengthFor(delta, ApproachRampSlope),
            ApproachRampMinMeters, ApproachRampMaxMeters);
        var rampSamples = Math.Max(2, (int)(rampMeters / spacing));

        // 2026-07-14 (junction-aware feather): a PINNED junction inside the run is an agreement point the
        // blender will enforce after the filter regardless of what we author here. Anchor the ramp to the
        // NEAREST one: descend from the deck-edge delta to the junction's own lift (its pinned Z − raw)
        // over the distance to the junction, and stop — beyond it the junction machinery owns the profile.
        // Feathering past it on our free class-grade run loses the argument post-blend and notches the
        // road right before the abutment (winningen splines 8/15/21/42/66, ±35 % arrival grades).
        var endSamples = rampSamples;
        var endDelta = 0f; // free run: ease all the way back to the natural raw
        for (var k = 1; k <= rampSamples; k++)
        {
            var idx = boundary + dir * k;
            if (idx < 0 || idx >= n || IsStructureSection(cs[idx]))
                break;
            if (cs[idx].JunctionPinnedElevation is { } junctionZ)
            {
                endSamples = k;
                endDelta = junctionZ - raw[idx];
                break;
            }
        }

        for (var k = 1; k <= endSamples; k++)
        {
            var idx = boundary + dir * k;
            if (idx < 0 || idx >= n)
                break;
            if (IsStructureSection(cs[idx]))
                break; // junction pin or the next span — its own authoring wins; stop the ramp here

            var u = (float)k / endSamples; // 0 at the abutment → 1 at the ramp/anchor end
            var w = ApproachRampProfile.Weight(u); // crest VC → constant-grade tangent → sag VC
            raw[idx] += endDelta + (delta - endDelta) * w;
        }
    }

    /// <summary>Hard-holds pinned sections to their pin AFTER filtering, so the deck edge does not sag (§7 step 1/2).</summary>
    private static void HardHoldPins(List<UnifiedCrossSection> cs, float[] smoothed)
    {
        for (var i = 0; i < cs.Count; i++)
            if (cs[i].PinnedElevation is { } p)
                smoothed[i] = p;
    }

    /// <summary>
    /// Builds the slope-clamp exemption mask: the pinned deck sections plus a half-window neighbourhood on each
    /// side (the rising approach ramp the box filter produces, ~±windowHalf), so the clamp never flattens the
    /// ramp or pulls the deck down (plan doc 14 §7 step 4). Null when there are no pins.
    /// </summary>
    private static bool[]? BuildPinExemptMask(List<UnifiedCrossSection> cs, int windowSize)
    {
        var n = cs.Count;
        var margin = Math.Max(1, windowSize / 2);
        bool[]? mask = null;
        for (var i = 0; i < n; i++)
        {
            // v3: soft-shaped spans carry intentional clearance grades too — exempt them like hard pins.
            if (cs[i].PinnedElevation is null && cs[i].SoftDeckRiseMeters is null) continue;
            mask ??= new bool[n];
            var lo = Math.Max(0, i - margin);
            var hi = Math.Min(n - 1, i + margin);
            for (var k = lo; k <= hi; k++) mask[k] = true;
        }

        return mask;
    }

    /// <summary>
    ///     Concatenates cross-sections from a chain's segments in traversal order,
    ///     with deduplication at segment joints to avoid duplicate samples.
    ///     Deduped cross-sections are tracked and will have their elevation copied
    ///     from the kept neighbor after chain filtering completes.
    /// </summary>
    /// <param name="chain">The elevation chain.</param>
    /// <param name="crossSectionsBySpline">Cross-sections grouped by spline ID, ordered by LocalIndex.</param>
    /// <param name="crossSectionSpacing">Nominal spacing between cross-sections for dedup threshold.</param>
    /// <returns>Concatenated cross-sections in chain traversal order.</returns>
    public static List<UnifiedCrossSection> ConcatenateChainCrossSections(
        ElevationChain chain,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        float crossSectionSpacing)
    {
        var result = new List<UnifiedCrossSection>();
        var dedupPairs = new List<(UnifiedCrossSection skipped, UnifiedCrossSection kept)>();
        var dedupThreshold = crossSectionSpacing / 2f;
        var chainIndex = 0;

        foreach (var (edge, traverseReversed) in chain.Segments)
        {
            if (!crossSectionsBySpline.TryGetValue(edge.SplineId, out var splineCS))
                continue;

            // Get cross-sections in traversal order
            IEnumerable<UnifiedCrossSection> ordered = traverseReversed
                ? splineCS.AsEnumerable().Reverse()
                : splineCS;

            foreach (var cs in ordered)
            {
                // Dedup: skip if co-located with last appended CS
                if (result.Count > 0)
                {
                    var last = result[^1];
                    var dist = Vector2.Distance(last.CenterPoint, cs.CenterPoint);
                    if (dist < dedupThreshold)
                    {
                        // Track the skipped CS so we can copy elevation from its neighbor later
                        dedupPairs.Add((cs, last));
                        cs.ChainId = chain.ChainId;
                        cs.ChainIndex = -1; // Mark as deduped
                        continue;
                    }
                }

                cs.ChainId = chain.ChainId;
                cs.ChainIndex = chainIndex++;
                result.Add(cs);
            }
        }

        // Store dedup pairs for post-processing (accessed via PropagateToDeduped)
        chain.DedupPairs = dedupPairs;

        return result;
    }

    /// <summary>
    ///     After chain elevation filtering, propagates TargetElevation to cross-sections
    ///     that were deduped during concatenation. Must be called after CalculateChainElevations
    ///     or ReSmoothChainFromExistingElevations.
    /// </summary>
    public static void PropagateToDeduped(ElevationChain chain)
    {
        if (chain.DedupPairs == null) return;

        foreach (var (skipped, kept) in chain.DedupPairs)
        {
            skipped.TargetElevation = kept.TargetElevation;
            skipped.OriginalTerrainElevation = kept.OriginalTerrainElevation;
        }
    }
}
