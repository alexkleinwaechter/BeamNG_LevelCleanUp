using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
/// Calculates road curvature at each cross-section for banking purposes.
/// Uses the angle between adjacent segment vectors divided by segment length.
/// Curvature sign indicates turn direction (positive = left, negative = right).
/// 
/// Curvature is defined as 1/radius where radius is the radius of the osculating circle
/// at that point. For discrete points, we approximate using the angle change between
/// adjacent segments divided by the arc length.
/// </summary>
public class CurvatureCalculator
{
    /// <summary>
    /// Minimum segment length to consider for curvature calculation.
    /// Segments shorter than this are treated as zero-length to avoid numerical instability.
    /// </summary>
    private const float MinSegmentLength = 0.001f;

    /// <summary>
    /// Calculates curvature for all cross-sections of a spline.
    /// Uses central differencing where possible, one-sided at endpoints.
    /// </summary>
    /// <param name="crossSections">Ordered list of cross-sections for a single spline</param>
    public void CalculateCurvature(IList<UnifiedCrossSection> crossSections)
    {
        if (crossSections.Count < 3)
        {
            // Not enough points to calculate curvature - set all to zero
            foreach (var cs in crossSections)
            {
                cs.Curvature = 0;
            }
            return;
        }

        for (int i = 0; i < crossSections.Count; i++)
        {
            var cs = crossSections[i];

            if (i == 0)
            {
                // First point: use forward difference (same as second point)
                cs.Curvature = CalculateCurvatureCentral(crossSections, 1);
            }
            else if (i == crossSections.Count - 1)
            {
                // Last point: use backward difference (same as second-to-last point)
                cs.Curvature = CalculateCurvatureCentral(crossSections, crossSections.Count - 2);
            }
            else
            {
                // Interior point: central difference
                cs.Curvature = CalculateCurvatureCentral(crossSections, i);
            }
        }
    }

    /// <summary>
    /// Central difference curvature calculation.
    /// curvature = (angle between prev?curr and curr?next) / avg_segment_length
    /// </summary>
    /// <param name="sections">The list of cross-sections</param>
    /// <param name="index">The index of the point to calculate curvature for (must be interior point)</param>
    /// <returns>Signed curvature value (positive = left curve, negative = right curve)</returns>
    private float CalculateCurvatureCentral(IList<UnifiedCrossSection> sections, int index)
    {
        if (index < 1 || index >= sections.Count - 1)
        {
            return 0;
        }

        var prev = sections[index - 1].CenterPoint;
        var curr = sections[index].CenterPoint;
        var next = sections[index + 1].CenterPoint;

        var v1 = curr - prev; // Vector from prev to curr
        var v2 = next - curr; // Vector from curr to next

        var len1 = v1.Length();
        var len2 = v2.Length();

        // Handle degenerate cases (coincident points)
        if (len1 < MinSegmentLength || len2 < MinSegmentLength)
        {
            return 0;
        }

        // Normalize vectors
        v1 /= len1;
        v2 /= len2;

        // Calculate angle between vectors using dot product
        var dot = Vector2.Dot(v1, v2);
        dot = Math.Clamp(dot, -1f, 1f); // Clamp to avoid NaN from floating point errors
        var angle = MathF.Acos(dot);

        // Determine sign via 2D cross product (z-component of 3D cross)
        // Positive cross = left turn (counterclockwise), Negative = right turn (clockwise)
        var cross = Cross2D(v1, v2);
        var signedAngle = cross >= 0 ? angle : -angle;

        // Curvature = angle / average segment length
        var avgLength = (len1 + len2) / 2f;
        return signedAngle / avgLength;
    }

    /// <summary>
    /// Calculates the 2D cross product (z-component of 3D cross product).
    /// Result is positive if v2 is counterclockwise from v1 (left turn).
    /// </summary>
    private static float Cross2D(Vector2 v1, Vector2 v2)
    {
        return v1.X * v2.Y - v1.Y * v2.X;
    }

    /// <summary>
    /// Converts curvature to approximate curve radius.
    /// </summary>
    /// <param name="curvature">The curvature value (1/meters)</param>
    /// <returns>Radius in meters, or float.MaxValue for essentially straight sections</returns>
    public static float CurvatureToRadius(float curvature)
    {
        if (MathF.Abs(curvature) < 0.0001f)
        {
            return float.MaxValue; // Essentially straight
        }
        return 1f / MathF.Abs(curvature);
    }

    /// <summary>
    /// Converts a curve radius to curvature.
    /// </summary>
    /// <param name="radius">The radius in meters</param>
    /// <returns>Curvature value (1/meters)</returns>
    public static float RadiusToCurvature(float radius)
    {
        if (radius < MinSegmentLength)
        {
            return float.MaxValue; // Infinitely tight curve
        }
        return 1f / radius;
    }

    /// <summary>
    /// Applies Gaussian smoothing to curvature values to reduce noise.
    /// Useful for eliminating jitter from discretization artifacts.
    /// </summary>
    /// <param name="crossSections">Cross-sections with curvature already calculated</param>
    /// <param name="windowSize">Size of the smoothing window (should be odd)</param>
    /// <param name="sigma">Standard deviation for Gaussian kernel (in number of samples)</param>
    public void SmoothCurvature(IList<UnifiedCrossSection> crossSections, int windowSize = 5, float sigma = 1.0f)
    {
        if (crossSections.Count < windowSize || windowSize < 3)
        {
            return;
        }

        // Ensure window size is odd
        if (windowSize % 2 == 0)
        {
            windowSize++;
        }

        var halfWindow = windowSize / 2;

        // Pre-compute Gaussian kernel
        var kernel = new float[windowSize];
        var kernelSum = 0f;
        for (int i = 0; i < windowSize; i++)
        {
            var x = i - halfWindow;
            kernel[i] = MathF.Exp(-(x * x) / (2 * sigma * sigma));
            kernelSum += kernel[i];
        }

        // Normalize kernel
        for (int i = 0; i < windowSize; i++)
        {
            kernel[i] /= kernelSum;
        }

        // Apply convolution
        var smoothedCurvatures = new float[crossSections.Count];
        for (int i = 0; i < crossSections.Count; i++)
        {
            var sum = 0f;
            var weightSum = 0f;

            for (int j = -halfWindow; j <= halfWindow; j++)
            {
                var idx = i + j;
                if (idx >= 0 && idx < crossSections.Count)
                {
                    var weight = kernel[j + halfWindow];
                    sum += crossSections[idx].Curvature * weight;
                    weightSum += weight;
                }
            }

            smoothedCurvatures[i] = weightSum > 0 ? sum / weightSum : crossSections[i].Curvature;
        }

        // Apply smoothed values
        for (int i = 0; i < crossSections.Count; i++)
        {
            crossSections[i].Curvature = smoothedCurvatures[i];
        }
    }

    /// <summary>
    /// Calculates curvature statistics for a set of cross-sections.
    /// Useful for debugging and parameter tuning.
    /// </summary>
    /// <param name="crossSections">Cross-sections with curvature calculated</param>
    /// <returns>Statistics about the curvature distribution</returns>
    public static CurvatureStatistics GetStatistics(IList<UnifiedCrossSection> crossSections)
    {
        if (crossSections.Count == 0)
        {
            return new CurvatureStatistics();
        }

        var curvatures = crossSections.Select(cs => cs.Curvature).ToList();
        var absCurvatures = curvatures.Select(MathF.Abs).ToList();

        return new CurvatureStatistics
        {
            MinCurvature = curvatures.Min(),
            MaxCurvature = curvatures.Max(),
            MeanCurvature = curvatures.Average(),
            MaxAbsCurvature = absCurvatures.Max(),
            MeanAbsCurvature = absCurvatures.Average(),
            TightestRadius = absCurvatures.Max() > 0.0001f ? 1f / absCurvatures.Max() : float.MaxValue,
            StraightSectionCount = curvatures.Count(c => MathF.Abs(c) < 0.001f),
            TotalSectionCount = crossSections.Count
        };
    }
}

/// <summary>
/// Statistics about curvature distribution in a road network.
/// </summary>
public record CurvatureStatistics
{
    /// <summary>Minimum curvature value (most negative = tightest right turn)</summary>
    public float MinCurvature { get; init; }

    /// <summary>Maximum curvature value (most positive = tightest left turn)</summary>
    public float MaxCurvature { get; init; }

    /// <summary>Mean curvature (indicates overall turn bias)</summary>
    public float MeanCurvature { get; init; }

    /// <summary>Maximum absolute curvature (tightest turn regardless of direction)</summary>
    public float MaxAbsCurvature { get; init; }

    /// <summary>Mean absolute curvature (overall curviness)</summary>
    public float MeanAbsCurvature { get; init; }

    /// <summary>Radius of the tightest curve in meters</summary>
    public float TightestRadius { get; init; }

    /// <summary>Number of essentially straight sections (|curvature| &lt; 0.001)</summary>
    public int StraightSectionCount { get; init; }

    /// <summary>Total number of cross-sections analyzed</summary>
    public int TotalSectionCount { get; init; }
}
