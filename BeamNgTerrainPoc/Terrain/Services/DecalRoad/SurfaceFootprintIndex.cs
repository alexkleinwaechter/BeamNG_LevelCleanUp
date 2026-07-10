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

    /// <summary>
    /// Max |node Z − surface Z| (m) for a point to count as overlapping a road surface.
    /// The plan test alone interrupted lane markings where a bridge deck merely CROSSES
    /// another road in plan — both the deck's and the street's markings were cut at a
    /// "junction" that is 5+ m apart vertically. Same rule as the bridge parapet
    /// coplanarity test (doc 15, ParapetCoplanarToleranceMeters): overlap requires the
    /// surfaces to actually meet. Looser than the parapet's 0.5 because the footprint
    /// carries centerline Z only — a node near a banked partner road's EDGE legitimately
    /// differs by width/2·sin(bank) plus grade between stations. Genuine junctions are
    /// harmonized coplanar (Δz≈0); minimum bridge clearance is ~4.5 m, so 1.0 has margin
    /// on both sides.
    /// </summary>
    private const float VerticalOverlapToleranceMeters = 1.0f;

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
                new Vector2(points[i].X, points[i].Y),
                new Vector2(points[i + 1].X, points[i + 1].Y),
                points[i].Z,
                points[i + 1].Z,
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
    /// Overlap requires plan containment AND vertical coplanarity
    /// (<see cref="VerticalOverlapToleranceMeters"/>) — z must be in the same
    /// frame as the footprint's centerline Z (DecalRoad node world Z).
    /// </summary>
    public (bool IsOverlapping, int OverlappingSplineId) CheckPoint(
        float x, float y, float z, int excludeSplineId)
    {
        var key = ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(y / CellSize));
        if (!_grid.TryGetValue(key, out var segments))
            return (false, -1);

        var point = new Vector2(x, y);
        foreach (var seg in segments)
        {
            if (seg.SplineId == excludeSplineId) continue;
            if (IsPointInSegment(point, z, seg))
                return (true, seg.SplineId);
        }

        return (false, -1);
    }

    /// <summary>
    /// Point-in-segment test: project point onto line A→B, check lateral distance
    /// against interpolated half-width and vertical distance against the
    /// station-lerped surface Z.
    /// </summary>
    private static bool IsPointInSegment(Vector2 point, float pointZ, FootprintSegment seg)
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
        if (dist >= halfWidth) return false;

        var surfaceZ = seg.ZA + (seg.ZB - seg.ZA) * t;
        return MathF.Abs(pointZ - surfaceZ) <= VerticalOverlapToleranceMeters;
    }

    private readonly record struct FootprintSegment(
        Vector2 A, Vector2 B,
        float ZA, float ZB,
        float WidthA, float WidthB,
        int SplineId);
}
