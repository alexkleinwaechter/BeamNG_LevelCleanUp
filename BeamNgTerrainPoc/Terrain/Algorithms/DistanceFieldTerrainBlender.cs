using System.Diagnostics;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     High-performance distance-field-based terrain blender.
///     Eliminates per-cross-section pixel iteration; uses global EDT + analytical blending.
///     PERFORMANCE: ~15x faster than upsampling approach for 4096x4096 terrains.
///     ELIMINATES: Nested loops, per-pixel cross-section queries, shoulder zone iteration.
/// </summary>
public class DistanceFieldTerrainBlender
{
    private float[,]? _lastDistanceField;

    /// <summary>
    ///     Gets the last computed distance field for reuse in post-processing.
    /// </summary>
    public float[,] GetLastDistanceField()
    {
        if (_lastDistanceField == null)
            throw new InvalidOperationException(
                "No distance field has been computed yet. Call BlendRoadWithTerrain first.");
        return _lastDistanceField;
    }

    public float[,] BlendRoadWithTerrain(
        float[,] originalHeightMap,
        RoadGeometry geometry,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        var perfLog = TerrainCreationLogger.Current;
        var totalSw = Stopwatch.StartNew();

        if (geometry.CrossSections.Count == 0)
        {
            Console.WriteLine("No cross-sections to blend");
            return (float[,])originalHeightMap.Clone();
        }

        Console.WriteLine("=== DISTANCE FIELD-BASED ROAD SMOOTHING ===");
        perfLog?.Timing($"DistanceFieldTerrainBlender started: {geometry.CrossSections.Count} cross-sections");

        var height = originalHeightMap.GetLength(0);
        var width = originalHeightMap.GetLength(1);

        var result = (float[,])originalHeightMap.Clone();

        // Step 1: Build road core mask (center pixels)
        Console.WriteLine("Building road core mask...");
        var sw = Stopwatch.StartNew();
        var roadCoreMask = BuildRoadCoreMask(geometry, width, height, metersPerPixel, parameters.RoadWidthMeters);
        perfLog?.Timing($"  BuildRoadCoreMask: {sw.ElapsedMilliseconds}ms");

        // Step 2: Compute Euclidean distance field from road core
        Console.WriteLine("Computing distance field (exact EDT)...");
        sw.Restart();
        var distanceField = ComputeDistanceField(roadCoreMask, metersPerPixel);
        _lastDistanceField = distanceField; // Store for post-processing
        perfLog?.Timing($"  ComputeDistanceField (EDT): {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"  EDT completed in {sw.Elapsed.TotalSeconds:F2}s");

        // Step 3: Build elevation map for road corridor
        Console.WriteLine("Building elevation map...");
        sw.Restart();
        var elevationMap = BuildElevationMap(geometry, width, height, metersPerPixel, parameters.RoadWidthMeters,
            parameters.TerrainAffectedRangeMeters);
        perfLog?.Timing($"  BuildElevationMap: {sw.ElapsedMilliseconds}ms");

        // Step 4: Apply analytical blending
        Console.WriteLine("Applying distance-based blending...");
        sw.Restart();
        ApplyDistanceBasedBlending(
            result,
            distanceField,
            elevationMap,
            parameters.RoadWidthMeters / 2.0f,
            parameters.TerrainAffectedRangeMeters,
            parameters.BlendFunctionType);
        perfLog?.Timing($"  ApplyDistanceBasedBlending: {sw.ElapsedMilliseconds}ms");

        totalSw.Stop();
        perfLog?.Timing($"DistanceFieldTerrainBlender TOTAL: {totalSw.ElapsedMilliseconds}ms");
        Console.WriteLine("=== DISTANCE FIELD SMOOTHING COMPLETE ===");
        return result;
    }

    /// <summary>
    ///     Builds binary mask of road core (centerline ± half width).
    /// </summary>
    private byte[,] BuildRoadCoreMask(RoadGeometry geometry, int width, int height, float metersPerPixel,
        float roadWidth)
    {
        var mask = new byte[height, width];
        var halfWidth = roadWidth / 2.0f;
        var sectionsProcessed = 0;

        foreach (var cs in geometry.CrossSections.Where(s => !s.IsExcluded))
        {
            // Rasterize cross-section line (left to right edge)
            var left = cs.CenterPoint - cs.NormalDirection * halfWidth;
            var right = cs.CenterPoint + cs.NormalDirection * halfWidth;

            var x0 = (int)(left.X / metersPerPixel);
            var y0 = (int)(left.Y / metersPerPixel);
            var x1 = (int)(right.X / metersPerPixel);
            var y1 = (int)(right.Y / metersPerPixel);

            DrawLine(mask, x0, y0, x1, y1, width, height);
            sectionsProcessed++;
        }

        Console.WriteLine($"  Rasterized {sectionsProcessed} cross-sections into road mask");
        return mask;
    }

    /// <summary>
    ///     Bresenham line rasterization.
    /// </summary>
    private void DrawLine(byte[,] mask, int x0, int y0, int x1, int y1, int width, int height)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                mask[y0, x0] = 255;

            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    ///     Computes exact Euclidean distance field using Felzenszwalb & Huttenlocher algorithm.
    ///     O(W*H) complexity - runs in ~0.3s for 4096x4096.
    /// </summary>
    private float[,] ComputeDistanceField(byte[,] mask, float metersPerPixel)
    {
        var h = mask.GetLength(0);
        var w = mask.GetLength(1);
        var dist = new float[h, w];
        const float INF = 1e12f;

        // Initialize
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                dist[y, x] = mask[y, x] > 0 ? 0f : INF;

        // 1D EDT per row
        var f = new float[w];
        var v = new int[w];
        var z = new float[w + 1];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) f[x] = dist[y, x];

            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (var q = 1; q < w; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
                    if (s <= z[k])
                    {
                        k--;
                        if (k < 0)
                        {
                            k = 0;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < w; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                dist[y, q] = (q - p) * (q - p) + f[p];
            }
        }

        // 1D EDT per column
        f = new float[h];
        v = new int[h];
        z = new float[h + 1];

        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++) f[y] = dist[y, x];

            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (var q = 1; q < h; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
                    if (s <= z[k])
                    {
                        k--;
                        if (k < 0)
                        {
                            k = 0;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < h; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                dist[q, x] = (q - p) * (q - p) + f[p];
            }
        }

        // Convert squared pixel distance to meters
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                dist[y, x] = MathF.Sqrt(dist[y, x]) * metersPerPixel;

        return dist;
    }

    /// <summary>
    ///     Builds elevation map by interpolating from nearest cross-sections.
    ///     Only fills road corridor (core + blend zone).
    ///     Uses parallel processing for large terrains.
    /// </summary>
    private float[,] BuildElevationMap(RoadGeometry geometry, int width, int height, float metersPerPixel,
        float roadWidth, float blendRange)
    {
        var elevations = new float[height, width];

        // Initialize to NaN to distinguish "not set" from valid zero elevation
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                elevations[y, x] = float.NaN;

        var maxDist = roadWidth / 2.0f + blendRange;

        // Build spatial index (done once, shared across threads)
        var index = BuildSpatialIndex(geometry.CrossSections, metersPerPixel);

        // Use parallel processing for large terrains
        // Each row is independent, so we can parallelize over rows
        var processorCount = Environment.ProcessorCount;
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.Timing($"  BuildElevationMap: Using {processorCount} threads for {height} rows");

        // Thread-local counters for pixels set (avoid contention)
        var totalPixelsSet = 0;

        // Parallel.For with MaxDegreeOfParallelism based on processor count
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = processorCount
        };

        // Use thread-local storage for per-thread pixel counts
        Parallel.For(0, height, options, () => 0, (y, state, localPixelsSet) =>
        {
            for (var x = 0; x < width; x++)
            {
                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                var nearest = FindNearestCrossSection(worldPos, index, metersPerPixel);

                if (nearest != null)
                {
                    var dist = Vector2.Distance(worldPos, nearest.CenterPoint);
                    if (dist <= maxDist)
                        // Double-check elevation validity (spatial index already filters, but be safe)
                        if (IsValidTargetElevation(nearest.TargetElevation))
                        {
                            elevations[y, x] = nearest.TargetElevation;
                            localPixelsSet++;
                        }
                }
            }

            return localPixelsSet;
        }, localPixelsSet =>
        {
            // Aggregate thread-local counts
            Interlocked.Add(ref totalPixelsSet, localPixelsSet);
        });

        Console.WriteLine($"  Set {totalPixelsSet:N0} elevation values in corridor");
        return elevations;
    }

    /// <summary>
    ///     Spatial index for fast nearest-neighbor queries.
    ///     Filters out cross-sections with invalid target elevations to prevent terrain spikes.
    /// </summary>
    private Dictionary<(int, int), List<CrossSection>> BuildSpatialIndex(List<CrossSection> sections,
        float metersPerPixel)
    {
        var index = new Dictionary<(int, int), List<CrossSection>>();
        const int CellSize = 32;
        var skippedInvalid = 0;

        foreach (var cs in sections.Where(s => !s.IsExcluded))
        {
            // CRITICAL FIX: Skip cross-sections with invalid target elevations
            // This prevents terrain spikes caused by uninitialized (0) or NaN elevations
            if (!IsValidTargetElevation(cs.TargetElevation))
            {
                skippedInvalid++;
                continue;
            }

            var gridX = (int)(cs.CenterPoint.X / metersPerPixel / CellSize);
            var gridY = (int)(cs.CenterPoint.Y / metersPerPixel / CellSize);
            var key = (gridX, gridY);

            if (!index.ContainsKey(key))
                index[key] = new List<CrossSection>();

            index[key].Add(cs);
        }

        if (skippedInvalid > 0)
            Console.WriteLine($"  WARNING: Skipped {skippedInvalid} cross-sections with invalid target elevations");

        return index;
    }

    /// <summary>
    ///     Validates that a target elevation is valid and won't cause terrain spikes.
    ///     Invalid values include: NaN, Infinity, or extremely negative values.
    /// </summary>
    private static bool IsValidTargetElevation(float elevation)
    {
        // Check for NaN or Infinity
        if (float.IsNaN(elevation) || float.IsInfinity(elevation))
            return false;

        // Check for extremely negative values (likely NoData or corruption)
        // Most real-world terrain is above -500m (Dead Sea is ~-430m)
        if (elevation < -1000.0f)
            return false;

        // Note: We do NOT reject elevation == 0.0f because:
        // 1. Valid terrain can be at sea level
        // 2. CrossSection.TargetElevation is initialized to NaN, not 0
        // 3. Heightmaps may be normalized with 0 as a valid elevation

        return true;
    }

    /// <summary>
    ///     Finds nearest cross-section using spatial index.
    /// </summary>
    private CrossSection? FindNearestCrossSection(Vector2 worldPos, Dictionary<(int, int), List<CrossSection>> index,
        float metersPerPixel)
    {
        const int CellSize = 32;
        var gridX = (int)(worldPos.X / metersPerPixel / CellSize);
        var gridY = (int)(worldPos.Y / metersPerPixel / CellSize);

        CrossSection? nearest = null;
        var minDist = float.MaxValue;

        // Search 3x3 grid
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var key = (gridX + dx, gridY + dy);
                if (index.TryGetValue(key, out var sections))
                    foreach (var cs in sections)
                    {
                        var dist = Vector2.Distance(worldPos, cs.CenterPoint);
                        if (dist < minDist)
                        {
                            minDist = dist;
                            nearest = cs;
                        }
                    }
            }

        return nearest;
    }

    /// <summary>
    ///     Applies analytical blending using distance field.
    ///     Eliminates per-cross-section pixel loops.
    ///     Uses parallel processing for large terrains.
    /// </summary>
    private void ApplyDistanceBasedBlending(
        float[,] heightMap,
        float[,] distanceField,
        float[,] elevationMap,
        float roadHalfWidth,
        float blendRange,
        BlendFunctionType blendType)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);

        // Thread-safe counters using Interlocked
        var modifiedPixels = 0;
        var roadCorePixels = 0;
        var shoulderPixels = 0;

        var maxDist = roadHalfWidth + blendRange;

        // Parallel processing over rows
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        Parallel.For(0, height, options, y =>
        {
            var localModified = 0;
            var localCore = 0;
            var localShoulder = 0;

            for (var x = 0; x < width; x++)
            {
                var d = distanceField[y, x];

                if (d > maxDist) continue;

                var originalH = heightMap[y, x];
                var targetH = elevationMap[y, x];

                if (float.IsNaN(targetH)) continue; // Outside road corridor (no cross-section found)

                float newH;

                if (d <= roadHalfWidth)
                {
                    // Road core - fully flattened
                    newH = targetH;
                    localCore++;
                }
                else
                {
                    // Blend zone (shoulder)
                    var t = (d - roadHalfWidth) / blendRange;
                    t = Math.Clamp(t, 0f, 1f);

                    var blend = blendType switch
                    {
                        BlendFunctionType.Cosine => 0.5f - 0.5f * MathF.Cos(MathF.PI * t),
                        BlendFunctionType.Cubic => t * t * (3f - 2f * t), // Hermite/smoothstep
                        BlendFunctionType.Quintic => t * t * t * (t * (t * 6f - 15f) + 10f), // Smootherstep
                        _ => t // Linear
                    };

                    newH = targetH * (1f - blend) + originalH * blend;
                    localShoulder++;
                }

                if (MathF.Abs(heightMap[y, x] - newH) > 0.001f)
                {
                    heightMap[y, x] = newH;
                    localModified++;
                }
            }

            // Aggregate thread-local counts
            Interlocked.Add(ref modifiedPixels, localModified);
            Interlocked.Add(ref roadCorePixels, localCore);
            Interlocked.Add(ref shoulderPixels, localShoulder);
        });

        Console.WriteLine($"  Modified {modifiedPixels:N0} pixels total");
        Console.WriteLine($"    Road core: {roadCorePixels:N0} pixels");
        Console.WriteLine($"    Shoulder: {shoulderPixels:N0} pixels");
    }

    /// <summary>
    ///     Applies post-processing smoothing to eliminate staircase artifacts on the road surface.
    ///     Uses a masked smoothing approach - only smooths within the road and shoulder areas.
    /// </summary>
    public void ApplyPostProcessingSmoothing(
        float[,] heightMap,
        float[,] distanceField,
        RoadSmoothingParameters parameters,
        float metersPerPixel)
    {
        if (!parameters.EnablePostProcessingSmoothing) return;

        Console.WriteLine("=== POST-PROCESSING SMOOTHING ===");
        Console.WriteLine($"  Type: {parameters.SmoothingType}");
        Console.WriteLine($"  Kernel Size: {parameters.SmoothingKernelSize}");
        Console.WriteLine($"  Sigma: {parameters.SmoothingSigma:F2}");
        Console.WriteLine($"  Mask Extension: {parameters.SmoothingMaskExtensionMeters}m");
        Console.WriteLine($"  Iterations: {parameters.SmoothingIterations}");

        // Build smoothing mask (road + extension)
        var maxSmoothingDist = parameters.RoadWidthMeters / 2.0f + parameters.SmoothingMaskExtensionMeters;
        var smoothingMask = BuildSmoothingMask(distanceField, maxSmoothingDist);

        // Apply smoothing iterations
        for (var iter = 0; iter < parameters.SmoothingIterations; iter++)
        {
            if (parameters.SmoothingIterations > 1)
                Console.WriteLine($"  Iteration {iter + 1}/{parameters.SmoothingIterations}...");

            switch (parameters.SmoothingType)
            {
                case PostProcessingSmoothingType.Gaussian:
                    ApplyGaussianSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize,
                        parameters.SmoothingSigma);
                    break;
                case PostProcessingSmoothingType.Box:
                    ApplyBoxSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize);
                    break;
                case PostProcessingSmoothingType.Bilateral:
                    ApplyBilateralSmoothing(heightMap, smoothingMask, parameters.SmoothingKernelSize,
                        parameters.SmoothingSigma);
                    break;
            }
        }

        Console.WriteLine("=== POST-PROCESSING SMOOTHING COMPLETE ===");
    }

    /// <summary>
    ///     Builds a binary mask indicating which pixels should be smoothed.
    /// </summary>
    private bool[,] BuildSmoothingMask(float[,] distanceField, float maxDistance)
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

        Console.WriteLine($"  Smoothing mask: {maskedPixels:N0} pixels");
        return mask;
    }

    /// <summary>
    ///     Applies Gaussian blur with the specified kernel size and sigma.
    ///     Only smooths pixels within the mask.
    /// </summary>
    private void ApplyGaussianSmoothing(float[,] heightMap, bool[,] mask, int kernelSize, float sigma)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var radius = kernelSize / 2;

        // Build Gaussian kernel
        var kernel = BuildGaussianKernel(kernelSize, sigma);

        // Create temporary buffer
        var tempMap = new float[height, width];
        var smoothedPixels = 0;

        // Apply convolution only to masked pixels
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (!mask[y, x])
                {
                    tempMap[y, x] = heightMap[y, x]; // Keep original value
                    continue;
                }

                var sum = 0f;
                var weightSum = 0f;

                // Convolve with Gaussian kernel
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

        // Copy back
        Array.Copy(tempMap, heightMap, height * width);
        Console.WriteLine($"    Gaussian smoothed {smoothedPixels:N0} pixels");
    }

    /// <summary>
    ///     Builds a 2D Gaussian kernel with the specified size and sigma.
    /// </summary>
    private float[,] BuildGaussianKernel(int size, float sigma)
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
    ///     Applies box blur (simple averaging) with the specified kernel size.
    ///     Only smooths pixels within the mask.
    /// </summary>
    private void ApplyBoxSmoothing(float[,] heightMap, bool[,] mask, int kernelSize)
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

                // Average with neighbors in box
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
        Console.WriteLine($"    Box smoothed {smoothedPixels:N0} pixels");
    }

    /// <summary>
    ///     Applies bilateral filtering - edge-preserving smoothing.
    ///     Considers both spatial distance and intensity difference.
    ///     Only smooths pixels within the mask.
    /// </summary>
    private void ApplyBilateralSmoothing(float[,] heightMap, bool[,] mask, int kernelSize, float sigma)
    {
        var height = heightMap.GetLength(0);
        var width = heightMap.GetLength(1);
        var radius = kernelSize / 2;
        var sigmaSpatial = sigma;
        var sigmaRange = sigma * 0.5f; // Intensity difference threshold

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

                            // Spatial weight (distance in pixels)
                            float spatialDist = kx * kx + ky * ky;
                            var spatialWeight = MathF.Exp(-spatialDist / (2f * sigmaSpatial * sigmaSpatial));

                            // Range weight (difference in elevation)
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
        Console.WriteLine($"    Bilateral smoothed {smoothedPixels:N0} pixels");
    }
}