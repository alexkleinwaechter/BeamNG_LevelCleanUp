using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates target elevations for road cross-sections using perpendicular sampling.
/// REVISED: Global road network leveling added before smoothing.
/// </summary>
public class CrossSectionalHeightCalculator : IHeightCalculator
{
    private const int MaxSlopeIterations = 50;
    
    public void CalculateTargetElevations(
        RoadGeometry geometry, 
        float[,] heightMap,
        float metersPerPixel)
    {
        if (geometry.CrossSections.Count == 0)
        {
            Console.WriteLine("No cross-sections to process");
            return;
        }
        
        Console.WriteLine($"Calculating target elevations for {geometry.CrossSections.Count} cross-sections...");
        
        // Step 1: Calculate initial target elevations from heightmap
        CalculateInitialElevations(geometry.CrossSections, heightMap, metersPerPixel);
        
        var initialMin = geometry.CrossSections.Min(cs => cs.TargetElevation);
        var initialMax = geometry.CrossSections.Max(cs => cs.TargetElevation);
        var initialRange = initialMax - initialMin;
        var globalAverage = geometry.CrossSections.Average(cs => cs.TargetElevation);
        
        Console.WriteLine($"  Initial elevation range: {initialRange:F2}m ({initialMin:F2}m - {initialMax:F2}m)");
        Console.WriteLine($"  Global average elevation: {globalAverage:F2}m");
        
        // Step 2: OPTIONAL Global Leveling (only if explicitly enabled)
        if (geometry.Parameters.GlobalLevelingStrength > 0.001f)
        {
            float levelingStrength = geometry.Parameters.GlobalLevelingStrength;
            Console.WriteLine($"  Applying global road network leveling (strength={levelingStrength:F2})...");
            
            foreach (var cs in geometry.CrossSections.Where(cs => !cs.IsExcluded))
            {
                cs.TargetElevation = cs.TargetElevation * (1 - levelingStrength) + globalAverage * levelingStrength;
            }
            
            var afterLevelingMin = geometry.CrossSections.Min(cs => cs.TargetElevation);
            var afterLevelingMax = geometry.CrossSections.Max(cs => cs.TargetElevation);
            var afterLevelingRange = afterLevelingMax - afterLevelingMin;
            float levelingReduction = (1 - afterLevelingRange / initialRange) * 100f;
            Console.WriteLine($"    After global leveling range: {afterLevelingRange:F2}m (reduced by {levelingReduction:F1}%)");
        }
        else
        {
            Console.WriteLine($"  Global leveling DISABLED (using local terrain-following smoothing)");
        }
        
        // Step 3: ULTRA-AGGRESSIVE smoothing with Butterworth or Gaussian filter
        int baseWindow = geometry.Parameters.SmoothingWindowSize;
        bool useButterworthFilter = geometry.Parameters.UseButterworthFilter;
        int butterworthOrder = geometry.Parameters.ButterworthFilterOrder;
        
        if (useButterworthFilter)
        {
            Console.WriteLine($"  Using BUTTERWORTH filter (order={butterworthOrder}) for maximally flat roads...");
            Console.WriteLine($"  Pass 1: Butterworth smoothing (window={baseWindow})...");
            ApplyButterworthSmoothing(geometry.CrossSections, baseWindow, butterworthOrder);
            
            Console.WriteLine($"  Pass 2: Second Butterworth smoothing (window={baseWindow})...");
            ApplyButterworthSmoothing(geometry.CrossSections, baseWindow, butterworthOrder);
            
            Console.WriteLine($"  Pass 3: Third Butterworth smoothing (window={baseWindow})...");
            ApplyButterworthSmoothing(geometry.CrossSections, baseWindow, butterworthOrder);
        }
        else
        {
            Console.WriteLine($"  Using GAUSSIAN smoothing (window={baseWindow})...");
            Console.WriteLine($"  Pass 1: Aggressive smoothing (window={baseWindow})...");
            ApplyGaussianSmoothing(geometry.CrossSections, baseWindow);
            
            Console.WriteLine($"  Pass 2: Second smoothing (window={baseWindow})...");
            ApplyGaussianSmoothing(geometry.CrossSections, baseWindow);
            
            Console.WriteLine($"  Pass 3: Third smoothing (window={baseWindow})...");
            ApplyGaussianSmoothing(geometry.CrossSections, baseWindow);
        }
        
        var afterSmoothingMin = geometry.CrossSections.Min(cs => cs.TargetElevation);
        var afterSmoothingMax = geometry.CrossSections.Max(cs => cs.TargetElevation);
        var afterSmoothingRange = afterSmoothingMax - afterSmoothingMin;
        float smoothingReduction = (1 - afterSmoothingRange / initialRange) * 100f;
        Console.WriteLine($"  After triple smoothing range: {afterSmoothingRange:F2}m (total reduced by {smoothingReduction:F1}%)");
        
        // Step 4: Apply GENTLE slope constraints (only if reasonable)
        if (geometry.Parameters.RoadMaxSlopeDegrees < 45.0f)
        {
            Console.WriteLine($"  Applying gentle slope constraints (max={geometry.Parameters.RoadMaxSlopeDegrees:F1}°)...");
            ApplySlopeConstraintsGentle(geometry.CrossSections, geometry.Parameters.RoadMaxSlopeDegrees);
        }
        
        // Step 5: Final polish smoothing (smaller window)
        int polishWindow = Math.Max(20, baseWindow / 4);
        Console.WriteLine($"  Final polish smoothing (window={polishWindow})...");
        
        if (useButterworthFilter)
        {
            ApplyButterworthSmoothing(geometry.CrossSections, polishWindow, butterworthOrder);
        }
        else
        {
            ApplyGaussianSmoothing(geometry.CrossSections, polishWindow);
        }
        
        // Final debug output
        var finalMin = geometry.CrossSections.Min(cs => cs.TargetElevation);
        var finalMax = geometry.CrossSections.Max(cs => cs.TargetElevation);
        var finalRange = finalMax - finalMin;
        float totalSmoothing = (1 - finalRange / initialRange) * 100f;
        
        Console.WriteLine($"Target elevations calculated:");
        Console.WriteLine($"  Average: {geometry.CrossSections.Average(cs => cs.TargetElevation):F2}m");
        Console.WriteLine($"  Min: {finalMin:F2}m, Max: {finalMax:F2}m");
        Console.WriteLine($"  Final range: {finalRange:F2}m");
        Console.WriteLine($"  Total smoothing: {totalSmoothing:F1}% reduction ? KEY METRIC!");
        
        if (totalSmoothing < 50.0f && geometry.Parameters.GlobalLevelingStrength < 0.5f)
        {
            Console.WriteLine($"  ?? INFO: Low smoothing is normal when GlobalLevelingStrength=0 (terrain-following mode)");
        }
        else if (totalSmoothing < 90.0f && geometry.Parameters.GlobalLevelingStrength > 0.7f)
        {
            Console.WriteLine($"  ?? WARNING: Smoothing reduction is low for high global leveling!");
            Console.WriteLine($"  Try increasing SmoothingWindowSize or check cross-section count");
        }
    }
    
    private void CalculateInitialElevations(
        List<CrossSection> crossSections, 
        float[,] heightMap,
        float metersPerPixel)
    {
        int heightMapHeight = heightMap.GetLength(0);
        int heightMapWidth = heightMap.GetLength(1);
        
        int fallbackCount = 0;
        
        foreach (var crossSection in crossSections)
        {
            if (crossSection.IsExcluded)
                continue;
            
            // Sample along the PERPENDICULAR (Normal direction) to the road
            var heights = SampleAlongPerpendicular(
                crossSection,
                heightMap,
                heightMapWidth,
                heightMapHeight,
                metersPerPixel);
            
            if (heights.Count == 0)
            {
                // Fallback: use center pixel
                int pixelX = (int)(crossSection.CenterPoint.X / metersPerPixel);
                int pixelY = (int)(crossSection.CenterPoint.Y / metersPerPixel);
                
                pixelX = Math.Clamp(pixelX, 0, heightMapWidth - 1);
                pixelY = Math.Clamp(pixelY, 0, heightMapHeight - 1);
                
                crossSection.TargetElevation = heightMap[pixelY, pixelX];
                fallbackCount++;
            }
            else
            {
                // Use MEDIAN (more robust than average - filters outliers)
                heights.Sort();
                crossSection.TargetElevation = heights[heights.Count / 2];
            }
        }
        
        if (fallbackCount > 0)
        {
            Console.WriteLine($"  Initial elevations: {fallbackCount} sections used fallback");
        }
    }
    
    /// <summary>
    /// Applies Gaussian-weighted smoothing to cross-section elevations.
    /// ULTRA-AGGRESSIVE version that completely flattens roads.
    /// </summary>
    private void ApplyGaussianSmoothing(List<CrossSection> crossSections, int windowSize)
    {
        if (windowSize <= 1)
        {
            Console.WriteLine("    Skipping smoothing (window size <= 1)");
            return;
        }
        
        // Group cross-sections by path to smooth each path independently
        var pathGroups = crossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .ToList();
        
        int totalSmoothed = 0;
        float totalVarianceReduction = 0;
        
        foreach (var pathGroup in pathGroups)
        {
            var orderedSections = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();
            
            if (orderedSections.Count < 2)
                continue;
            
            // Calculate initial variance
            var originalElevations = orderedSections.Select(cs => cs.TargetElevation).ToArray();
            float originalVariance = CalculateVariance(originalElevations);
            
            // Calculate Gaussian weights once
            int halfWindow = windowSize / 2;
            float sigma = halfWindow / 3.0f; // Standard deviation
            var weights = new float[windowSize];
            float weightSum = 0;
            
            for (int i = 0; i < windowSize; i++)
            {
                int offset = i - halfWindow;
                weights[i] = MathF.Exp(-(offset * offset) / (2 * sigma * sigma));
                weightSum += weights[i];
            }
            
            // Normalize weights
            for (int i = 0; i < windowSize; i++)
            {
                weights[i] /= weightSum;
            }
            
            // Apply Gaussian smoothing
            for (int i = 0; i < orderedSections.Count; i++)
            {
                int startIdx = Math.Max(0, i - halfWindow);
                int endIdx = Math.Min(orderedSections.Count - 1, i + halfWindow);
                
                float smoothedElevation = 0;
                float actualWeightSum = 0;
                
                for (int j = startIdx; j <= endIdx; j++)
                {
                    int weightIndex = (j - i) + halfWindow;
                    if (weightIndex >= 0 && weightIndex < windowSize)
                    {
                        smoothedElevation += originalElevations[j] * weights[weightIndex];
                        actualWeightSum += weights[weightIndex];
                    }
                }
                
                // Normalize by actual weight sum (important at path ends)
                if (actualWeightSum > 0)
                {
                    orderedSections[i].TargetElevation = smoothedElevation / actualWeightSum;
                }
                
                totalSmoothed++;
            }
            
            // Calculate final variance
            var smoothedElevations = orderedSections.Select(cs => cs.TargetElevation).ToArray();
            float smoothedVariance = CalculateVariance(smoothedElevations);
            
            if (originalVariance > 0)
            {
                totalVarianceReduction += (1 - smoothedVariance / originalVariance) * 100f;
            }
        }
        
        if (pathGroups.Count > 0)
        {
            Console.WriteLine($"    Smoothed {totalSmoothed} sections, avg variance reduction: {totalVarianceReduction / pathGroups.Count:F1}%");
        }
    }
    
    /// <summary>
    /// Applies Butterworth low-pass filter smoothing for maximally flat passband.
    /// Butterworth filter provides flatter response in the passband compared to Gaussian,
    /// which results in smoother roads with less ripple while still removing high-frequency bumps.
    /// </summary>
    private void ApplyButterworthSmoothing(List<CrossSection> crossSections, int windowSize, int order)
    {
        if (windowSize <= 1)
        {
            Console.WriteLine("    Skipping smoothing (window size <= 1)");
            return;
        }
        
        // Group cross-sections by path to smooth each path independently
        var pathGroups = crossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .ToList();
        
        int totalSmoothed = 0;
        float totalVarianceReduction = 0;
        
        foreach (var pathGroup in pathGroups)
        {
            var orderedSections = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();
            
            if (orderedSections.Count < 2)
                continue;
            
            // Calculate initial variance
            var originalElevations = orderedSections.Select(cs => cs.TargetElevation).ToArray();
            float originalVariance = CalculateVariance(originalElevations);
            
            // Calculate Butterworth weights
            int halfWindow = windowSize / 2;
            var weights = CalculateButterworthWeights(windowSize, order);
            
            // Apply Butterworth smoothing
            for (int i = 0; i < orderedSections.Count; i++)
            {
                int startIdx = Math.Max(0, i - halfWindow);
                int endIdx = Math.Min(orderedSections.Count - 1, i + halfWindow);
                
                float smoothedElevation = 0;
                float actualWeightSum = 0;
                
                for (int j = startIdx; j <= endIdx; j++)
                {
                    int weightIndex = (j - i) + halfWindow;
                    if (weightIndex >= 0 && weightIndex < windowSize)
                    {
                        smoothedElevation += originalElevations[j] * weights[weightIndex];
                        actualWeightSum += weights[weightIndex];
                    }
                }
                
                // Normalize by actual weight sum (important at path ends)
                if (actualWeightSum > 0)
                {
                    orderedSections[i].TargetElevation = smoothedElevation / actualWeightSum;
                }
                
                totalSmoothed++;
            }
            
            // Calculate final variance
            var smoothedElevations = orderedSections.Select(cs => cs.TargetElevation).ToArray();
            float smoothedVariance = CalculateVariance(smoothedElevations);
            
            if (originalVariance > 0)
            {
                totalVarianceReduction += (1 - smoothedVariance / originalVariance) * 100f;
            }
        }
        
        if (pathGroups.Count > 0)
        {
            Console.WriteLine($"    Smoothed {totalSmoothed} sections, avg variance reduction: {totalVarianceReduction / pathGroups.Count:F1}%");
        }
    }
    
    /// <summary>
    /// Calculates Butterworth low-pass filter weights for given window size and filter order.
    /// Provides maximally flat frequency response in passband (smoother roads).
    /// </summary>
    private float[] CalculateButterworthWeights(int windowSize, int order)
    {
        var weights = new float[windowSize];
        int halfWindow = windowSize / 2;
        
        // Cutoff frequency (normalized): adjust to control smoothing strength
        // Lower cutoff = more aggressive smoothing (flatter roads)
        float cutoffNormalized = 0.3f; // 30% of Nyquist frequency
        
        float weightSum = 0;
        
        for (int i = 0; i < windowSize; i++)
        {
            // Distance from center (normalized by half window)
            float t = (i - halfWindow) / (float)halfWindow;
            
            // Butterworth filter magnitude response:
            // H(?) = 1 / sqrt(1 + (?/?c)^(2n))
            // where n = order, ?c = cutoff frequency
            
            float omega = MathF.Abs(t); // Normalized frequency (0 at center, 1 at edge)
            float ratio = omega / cutoffNormalized;
            float response = 1.0f / MathF.Sqrt(1.0f + MathF.Pow(ratio, 2 * order));
            
            weights[i] = response;
            weightSum += response;
        }
        
        // Normalize weights to sum to 1
        for (int i = 0; i < windowSize; i++)
        {
            weights[i] /= weightSum;
        }
        
        return weights;
    }
    
    private float CalculateVariance(float[] values)
    {
        if (values.Length == 0) return 0;
        float mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / values.Length;
    }
    
    private List<float> SampleAlongPerpendicular(
        CrossSection crossSection,
        float[,] heightMap,
        int heightMapWidth,
        int heightMapHeight,
        float metersPerPixel)
    {
        var heights = new List<float>();
        
        // Sample at many points across the road width
        int numSamples = 11;
        float halfWidth = crossSection.WidthMeters / 2.0f;
        
        for (int i = 0; i < numSamples; i++)
        {
            float t = i / (float)(numSamples - 1);
            float offset = (t - 0.5f) * 2.0f * halfWidth;
            
            var samplePos = crossSection.CenterPoint + crossSection.NormalDirection * offset;
            
            float pixelX = samplePos.X / metersPerPixel;
            float pixelY = samplePos.Y / metersPerPixel;
            
            float height = GetInterpolatedHeight(heightMap, pixelX, pixelY, heightMapWidth, heightMapHeight);
            if (!float.IsNaN(height))
            {
                heights.Add(height);
            }
        }
        
        return heights;
    }
    
    private float GetInterpolatedHeight(float[,] heightMap, float x, float y, int width, int height)
    {
        if (x < 0 || x >= width - 1 || y < 0 || y >= height - 1)
        {
            int ix = Math.Clamp((int)x, 0, width - 1);
            int iy = Math.Clamp((int)y, 0, height - 1);
            return heightMap[iy, ix];
        }
        
        int x0 = (int)x;
        int y0 = (int)y;
        int x1 = x0 + 1;
        int y1 = y0 + 1;
        
        float fx = x - x0;
        float fy = y - y0;
        
        float h00 = heightMap[y0, x0];
        float h10 = heightMap[y0, x1];
        float h01 = heightMap[y1, x0];
        float h11 = heightMap[y1, x1];
        
        float h0 = h00 * (1 - fx) + h10 * fx;
        float h1 = h01 * (1 - fx) + h11 * fx;
        
        return h0 * (1 - fy) + h1 * fy;
    }
    
    /// <summary>
    /// GENTLE slope constraints that preserve smoothness.
    /// Only makes MINIMAL adjustments to extreme violations.
    /// </summary>
    private void ApplySlopeConstraintsGentle(List<CrossSection> crossSections, float maxSlopeDegrees)
    {
        if (crossSections.Count < 2)
            return;
        
        var pathGroups = crossSections
            .Where(cs => !cs.IsExcluded)
            .GroupBy(cs => cs.PathId)
            .ToList();
        
        float maxSlopeRatio = MathF.Tan(maxSlopeDegrees * MathF.PI / 180.0f);
        
        int totalAdjustments = 0;
        
        foreach (var pathGroup in pathGroups)
        {
            var activeSections = pathGroup.OrderBy(cs => cs.LocalIndex).ToList();
            
            if (activeSections.Count < 2)
                continue;
            
            // VERY gentle iterations - only fix extreme violations
            for (int iteration = 0; iteration < 5; iteration++)
            {
                bool changed = false;
                
                for (int i = 1; i < activeSections.Count; i++)
                {
                    var cs1 = activeSections[i - 1];
                    var cs2 = activeSections[i];
                    
                    float distance = Vector2.Distance(cs1.CenterPoint, cs2.CenterPoint);
                    
                    if (distance < 0.001f)
                        continue;
                    
                    float currentSlope = (cs2.TargetElevation - cs1.TargetElevation) / distance;
                    
                    // Only adjust if slope is MORE than 50% over limit (very gentle!)
                    if (MathF.Abs(currentSlope) > maxSlopeRatio * 1.5f)
                    {
                        // MINIMAL adjustment - only move 20% toward target
                        float targetSlope = MathF.Sign(currentSlope) * maxSlopeRatio;
                        float midpoint = (cs2.TargetElevation + cs1.TargetElevation) / 2.0f;
                        
                        float targetElev1 = midpoint - distance * targetSlope / 2.0f;
                        float targetElev2 = midpoint + distance * targetSlope / 2.0f;
                        
                        // Blend only 20% toward constraint, keep 80% of smoothed value
                        cs1.TargetElevation = cs1.TargetElevation * 0.8f + targetElev1 * 0.2f;
                        cs2.TargetElevation = cs2.TargetElevation * 0.8f + targetElev2 * 0.2f;
                        changed = true;
                        totalAdjustments++;
                    }
                }
                
                if (!changed)
                    break;
            }
        }
        
        Console.WriteLine($"    Made {totalAdjustments} gentle slope adjustments");
    }
}
