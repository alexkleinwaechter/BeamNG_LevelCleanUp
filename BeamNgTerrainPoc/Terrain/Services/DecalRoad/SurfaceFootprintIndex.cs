using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Spatial hash grid built from spline surface corridors.
/// Used to detect whether a point overlaps any road's physical surface.
/// </summary>
public class SurfaceFootprintIndex
{
    private const float CellSize = 20f; // ~typical max road width
    private const float Margin = 0.5f;

    private readonly Dictionary<(int, int), List<FootprintSegment>> _grid = new();

    /// <summary>
    /// Adds a spline's full road surface to the spatial index.
    /// Uses the spline centerline and uniform surface half-width instead of
    /// individual DecalRoad layer widths.
    /// </summary>
    public void AddSplineSurface(SplineSurfaceData surface)
    {
        var points = surface.CenterlinePoints;
        // Each segment uses the full surface width (surfaceHalfWidth * 2)
        // so that IsPointInSegment's halfWidth calculation (width/2 + Margin) yields
        // surfaceHalfWidth + Margin — exactly the detection radius we want.
        var segmentWidth = surface.SurfaceHalfWidth * 2f;

        for (int i = 0; i < points.Count - 1; i++)
        {
            var seg = new FootprintSegment(
                points[i],
                points[i + 1],
                segmentWidth,
                segmentWidth,
                surface.SplineId);

            var halfW = surface.SurfaceHalfWidth + Margin;
            var minX = MathF.Min(seg.A.X, seg.B.X) - halfW;
            var minY = MathF.Min(seg.A.Y, seg.B.Y) - halfW;
            var maxX = MathF.Max(seg.A.X, seg.B.X) + halfW;
            var maxY = MathF.Max(seg.A.Y, seg.B.Y) + halfW;

            var cellMinX = (int)MathF.Floor(minX / CellSize);
            var cellMinY = (int)MathF.Floor(minY / CellSize);
            var cellMaxX = (int)MathF.Floor(maxX / CellSize);
            var cellMaxY = (int)MathF.Floor(maxY / CellSize);

            for (int cx = cellMinX; cx <= cellMaxX; cx++)
            for (int cy = cellMinY; cy <= cellMaxY; cy++)
            {
                var key = (cx, cy);
                if (!_grid.TryGetValue(key, out var list))
                {
                    list = [];
                    _grid[key] = list;
                }
                list.Add(seg);
            }
        }
    }

    /// <summary>
    /// Checks whether a point overlaps any surface road's footprint,
    /// excluding roads belonging to the specified spline.
    /// </summary>
    public (bool IsOverlapping, int OverlappingSplineId) CheckPoint(
        float x, float y, int excludeSplineId)
    {
        var key = ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(y / CellSize));
        if (!_grid.TryGetValue(key, out var segments))
            return (false, -1);

        var point = new Vector2(x, y);
        foreach (var seg in segments)
        {
            if (seg.SplineId == excludeSplineId) continue;
            if (IsPointInSegment(point, seg))
                return (true, seg.SplineId);
        }

        return (false, -1);
    }

    /// <summary>
    /// Point-in-segment test: project point onto line A→B, check lateral distance
    /// against interpolated half-width.
    /// </summary>
    private static bool IsPointInSegment(Vector2 point, FootprintSegment seg)
    {
        var ab = seg.B - seg.A;
        var abLenSq = ab.LengthSquared();
        if (abLenSq < 0.001f) return false;

        var ap = point - seg.A;
        var t = Vector2.Dot(ap, ab) / abLenSq;
        if (t < 0f || t > 1f) return false;

        var center = Vector2.Lerp(seg.A, seg.B, t);
        var dist = Vector2.Distance(point, center);
        var halfWidth = MathF.Max(seg.WidthA, seg.WidthB) / 2f + Margin;

        return dist < halfWidth;
    }

    private readonly record struct FootprintSegment(
        Vector2 A, Vector2 B,
        float WidthA, float WidthB,
        int SplineId);
}
