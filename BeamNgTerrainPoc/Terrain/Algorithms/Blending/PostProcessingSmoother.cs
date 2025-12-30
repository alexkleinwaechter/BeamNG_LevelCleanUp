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
/// </summary>
public class PostProcessingSmoother
{
    /// <summary>
    /// Applies post-processing smoothing to eliminate staircase artifacts on the road surface.
    /// Uses a masked smoothing approach - only smooths within the road and shoulder areas.
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
        // Find the dominant parameters for smoothing (use first spline's parameters)
        var firstSpline = network.Splines.FirstOrDefault();
        if (firstSpline == null)
            return;

        var parameters = firstSpline.Parameters;
        if (!parameters.EnablePostProcessingSmoothing)
            return;

        TerrainLogger.Info("=== POST-PROCESSING SMOOTHING ===");
        TerrainLogger.Info($"  Type: {parameters.SmoothingType}");
        TerrainLogger.Info($"  Kernel Size: {parameters.SmoothingKernelSize}");
        TerrainLogger.Info($"  Sigma: {parameters.SmoothingSigma:F2}");
        TerrainLogger.Info($"  Mask Extension: {parameters.SmoothingMaskExtensionMeters}m");
        TerrainLogger.Info($"  Iterations: {parameters.SmoothingIterations}");

        // Build smoothing mask based on maximum road width + extension
        var maxRoadWidth = network.Splines.Max(s => s.Parameters.RoadWidthMeters);
        var maxSmoothingDist = maxRoadWidth / 2.0f + parameters.SmoothingMaskExtensionMeters;
        var smoothingMask = BuildSmoothingMask(distanceField, maxSmoothingDist);

        // Apply smoothing iterations
        for (var iter = 0; iter < parameters.SmoothingIterations; iter++)
        {
            if (parameters.SmoothingIterations > 1)
                TerrainLogger.Info($"  Iteration {iter + 1}/{parameters.SmoothingIterations}...");

            switch (parameters.SmoothingType)
            {
                case PostProcessingSmoothingType.Gaussian:
                    ApplyGaussianSmoothing(heightMap, smoothingMask, 
                        parameters.SmoothingKernelSize, parameters.SmoothingSigma);
                    break;
                case PostProcessingSmoothingType.Box:
                    ApplyBoxSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize);
                    break;
                case PostProcessingSmoothingType.Bilateral:
                    ApplyBilateralSmoothing(heightMap, smoothingMask, 
                        parameters.SmoothingKernelSize, parameters.SmoothingSigma);
                    break;
            }
        }

        TerrainLogger.Info("=== POST-PROCESSING SMOOTHING COMPLETE ===");
    }

    /// <summary>
    /// Builds a binary mask indicating which pixels should be smoothed.
    /// </summary>
    private static bool[,] BuildSmoothingMask(float[,] distanceField, float maxDistance)
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

        TerrainCreationLogger.Current?.Detail($"Smoothing mask: {maskedPixels:N0} pixels");
        return mask;
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
