namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Gabled roof: two sloped planes meeting at a central ridge that spans the full
/// length of the building. The ridge endpoints touch the polygon outline.
///
/// 1:1 port of OSM2World's GabledRoof.java.
///
/// Key differences from HippedRoof:
///   - relativeRoofOffset = 0 (ridge goes all the way to the polygon edges)
///   - Ridge endpoints are inserted into the polygon (getPolygon() modifies the polygon)
///   - Inner segments: just the ridge (1 segment → 2 faces)
///
/// Gable walls (triangular end walls) are NOT generated here.
/// They are automatically created by ExteriorBuildingWall because the wall top boundary
/// includes the ridge endpoint (from GetPolygon() → InsertIntoPolygon), matching Java's
/// architecture where gable walls are part of ExteriorBuildingWall, not the Roof class.
/// </summary>
public class GabledRoof : RoofWithRidge
{
    /// <summary>
    /// Creates a gabled roof.
    /// Port of: new GabledRoof(originalPolygon, tags, material)
    ///   → super(0, originalPolygon, tags, material)
    /// </summary>
    public GabledRoof(BuildingData building, List<Vector2> polygon)
        : base(0f, building, polygon) { }

    /// <summary>
    /// Returns the polygon with ridge endpoints inserted.
    /// Port of GabledRoof.getPolygon():
    ///   SimplePolygonXZ newOuter = originalPolygon.getOuter();
    ///   newOuter = insertIntoPolygon(newOuter, ridge.p1, SNAP_DISTANCE);
    ///   newOuter = insertIntoPolygon(newOuter, ridge.p2, SNAP_DISTANCE);
    /// </summary>
    public override List<Vector2> GetPolygon()
    {
        var poly = FaceDecompositionUtil.InsertIntoPolygon(originalPolygon, ridge.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, ridge.P2, SNAP_DISTANCE);
        return poly;
    }

    /// <summary>
    /// Returns inner segments: just the ridge.
    /// Port of GabledRoof.getInnerSegments():
    ///   return singleton(ridge);
    ///
    /// Critical: ridge endpoints must use EXACT polygon vertex coordinates.
    /// When InsertIntoPolygon snapped a ridge endpoint to an existing vertex
    /// (within SNAP_DISTANCE), the inner segment must use that same vertex,
    /// not the original ridge coordinate. Otherwise the PointPool in
    /// FaceDecompositionUtil assigns different IDs → disconnected graph.
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments()
    {
        var poly = GetPolygon();

        // Snap ridge endpoints to actual polygon vertices
        var actualP1 = FindNearestVertex(poly, ridge.P1);
        var actualP2 = FindNearestVertex(poly, ridge.P2);

        return new List<(Vector2, Vector2)> { (actualP1, actualP2) };
    }

}
