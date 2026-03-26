namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

public class RoadWidthProfile
{
    public List<WidthSegment> Segments { get; }
    public float TransitionLengthMeters { get; set; } = 15f;

    public RoadWidthProfile(List<WidthSegment> segments)
    {
        Segments = segments ?? throw new ArgumentNullException(nameof(segments));
        if (segments.Count == 0)
            throw new ArgumentException("At least one segment required.", nameof(segments));
    }

    /// <summary>
    /// Returns interpolated (surface, corridor, masterSpline) widths at the given distance.
    /// Uses binary search + linear interpolation in transition zones.
    /// </summary>
    public (float surface, float corridor, float masterSpline) GetWidthsAtDistance(float distance)
    {
        if (Segments.Count == 1)
            return (Segments[0].RoadSurfaceWidth, Segments[0].SmoothingCorridorWidth, Segments[0].MasterSplineWidth);

        // Binary search for the segment containing this distance
        int idx = 0;
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            if (distance >= Segments[i].StartDistance)
            {
                idx = i;
                break;
            }
        }

        var current = Segments[idx];

        // Check if we're in a transition zone to the next segment
        if (TransitionLengthMeters > 0 && idx < Segments.Count - 1)
        {
            var next = Segments[idx + 1];
            var halfTransition = TransitionLengthMeters / 2f;
            var boundary = next.StartDistance;

            if (distance >= boundary - halfTransition)
            {
                // In transition zone: interpolate
                var t = (distance - (boundary - halfTransition)) / TransitionLengthMeters;
                t = Math.Clamp(t, 0f, 1f);
                return (
                    surface: Lerp(current.RoadSurfaceWidth, next.RoadSurfaceWidth, t),
                    corridor: Lerp(current.SmoothingCorridorWidth, next.SmoothingCorridorWidth, t),
                    masterSpline: Lerp(current.MasterSplineWidth, next.MasterSplineWidth, t)
                );
            }
        }

        // Check if we're in a transition zone from the previous segment
        if (TransitionLengthMeters > 0 && idx > 0)
        {
            var prev = Segments[idx - 1];
            var halfTransition = TransitionLengthMeters / 2f;
            var boundary = current.StartDistance;

            if (distance < boundary + halfTransition)
            {
                var t = (distance - (boundary - halfTransition)) / TransitionLengthMeters;
                t = Math.Clamp(t, 0f, 1f);
                return (
                    surface: Lerp(prev.RoadSurfaceWidth, current.RoadSurfaceWidth, t),
                    corridor: Lerp(prev.SmoothingCorridorWidth, current.SmoothingCorridorWidth, t),
                    masterSpline: Lerp(prev.MasterSplineWidth, current.MasterSplineWidth, t)
                );
            }
        }

        return (current.RoadSurfaceWidth, current.SmoothingCorridorWidth, current.MasterSplineWidth);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
