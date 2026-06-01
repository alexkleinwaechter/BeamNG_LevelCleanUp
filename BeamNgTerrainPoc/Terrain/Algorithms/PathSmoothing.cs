using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Chaikin corner-cutting smoothing for polyline paths.
/// Shared between PNG pipeline (UnifiedRoadNetworkBuilder) and OSM pipeline (OsmGeometryProcessor).
/// </summary>
public static class PathSmoothing
{
    /// <summary>
    /// Chaikin corner-cutting smoothing algorithm.
    /// Creates smoother control points by iteratively cutting corners.
    /// Each iteration replaces each segment with two new points at 1/4 and 3/4,
    /// roughly doubling the point count while smoothing sharp turns.
    /// First and last points are always preserved.
    /// </summary>
    public static List<Vector2> ChaikinSmooth(List<Vector2> points, int iterations)
    {
        if (points.Count < 3 || iterations <= 0)
            return points;

        var result = new List<Vector2>(points);

        for (int iter = 0; iter < iterations; iter++)
        {
            var smoothed = new List<Vector2>();

            // Keep the first point
            smoothed.Add(result[0]);

            // Apply corner cutting to intermediate segments
            for (int i = 0; i < result.Count - 1; i++)
            {
                var p0 = result[i];
                var p1 = result[i + 1];

                // Create two new points at 1/4 and 3/4 along the segment
                var q = new Vector2(
                    0.75f * p0.X + 0.25f * p1.X,
                    0.75f * p0.Y + 0.25f * p1.Y);
                var r = new Vector2(
                    0.25f * p0.X + 0.75f * p1.X,
                    0.25f * p0.Y + 0.75f * p1.Y);

                // Don't duplicate start/end points
                if (i > 0)
                    smoothed.Add(q);
                if (i < result.Count - 2)
                    smoothed.Add(r);
            }

            // Keep the last point
            smoothed.Add(result[^1]);

            result = smoothed;
        }

        return result;
    }
}
