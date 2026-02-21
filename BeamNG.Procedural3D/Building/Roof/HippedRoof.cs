namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Hipped roof: four sloped planes with an inset ridge.
/// The ridge is offset inward by 1/3 of the cap length, creating
/// triangular hip faces at each end.
///
/// 1:1 port of OSM2World's HippedRoof.java.
///
/// Key differences from GabledRoof:
///   - relativeRoofOffset = 1/3 (ridge is pushed inward from polygon edges)
///   - Polygon is unchanged (getPolygon() returns originalPolygon)
///   - Inner segments: ridge + 4 hip lines (5 segments → 4 faces)
///     The hip lines connect ridge endpoints to cap segment endpoints.
/// </summary>
public class HippedRoof : RoofWithRidge
{
    /// <summary>
    /// Creates a hipped roof.
    /// Port of: new HippedRoof(originalPolygon, tags, material)
    ///   → super(1/3.0, originalPolygon, tags, material)
    /// </summary>
    public HippedRoof(BuildingData building, List<Vector2> polygon)
        : base(1f / 3f, building, polygon) { }

    /// <summary>
    /// Returns the original polygon unchanged.
    /// Port of HippedRoof.getPolygon():
    ///   return originalPolygon;
    /// </summary>
    public override List<Vector2> GetPolygon()
    {
        return originalPolygon;
    }

    /// <summary>
    /// Returns inner segments: the ridge + 4 hip lines to cap segment endpoints.
    /// Port of HippedRoof.getInnerSegments():
    ///   return asList(
    ///       ridge,
    ///       new LineSegmentXZ(ridge.p1, cap1.p1),
    ///       new LineSegmentXZ(ridge.p1, cap1.p2),
    ///       new LineSegmentXZ(ridge.p2, cap2.p1),
    ///       new LineSegmentXZ(ridge.p2, cap2.p2));
    ///
    /// Cap endpoints come from the simplified polygon which is a vertex subset
    /// of the original polygon, so coordinates should be bitwise identical.
    /// We snap as a safety measure for float precision.
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments()
    {
        var poly = GetPolygon();

        // Snap cap endpoints to nearest polygon vertices
        var actualCap1P1 = FindNearestVertex(poly, cap1.P1);
        var actualCap1P2 = FindNearestVertex(poly, cap1.P2);
        var actualCap2P1 = FindNearestVertex(poly, cap2.P1);
        var actualCap2P2 = FindNearestVertex(poly, cap2.P2);

        return new List<(Vector2, Vector2)>
        {
            (ridge.P1, ridge.P2),           // ridge
            (ridge.P1, actualCap1P1),       // ridge end 1 → cap1 start
            (ridge.P1, actualCap1P2),       // ridge end 1 → cap1 end
            (ridge.P2, actualCap2P1),       // ridge end 2 → cap2 start
            (ridge.P2, actualCap2P2)        // ridge end 2 → cap2 end
        };
    }
}
