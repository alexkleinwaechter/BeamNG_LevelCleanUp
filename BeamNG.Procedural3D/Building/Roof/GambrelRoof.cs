namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Gambrel (barn) roof: dual-slope with steep lower portion and gentle upper portion.
/// Ridge at polygon edge (offset = 0), with intermediate cap parts at 1/6 to 5/6.
/// 1:1 port of OSM2World's GambrelRoof.java.
/// </summary>
public class GambrelRoof : RoofWithRidge
{
    private readonly (Vector2 P1, Vector2 P2) cap1part;
    private readonly (Vector2 P1, Vector2 P2) cap2part;

    public GambrelRoof(BuildingData building, List<Vector2> polygon)
        : base(0f, building, polygon)
    {
        // Port of:
        // cap1part = new LineSegmentXZ(
        //     interpolateBetween(cap1.p1, cap1.p2, 1/6.0),
        //     interpolateBetween(cap1.p1, cap1.p2, 5/6.0));
        cap1part = (
            Vector2.Lerp(cap1.P1, cap1.P2, 1f / 6f),
            Vector2.Lerp(cap1.P1, cap1.P2, 5f / 6f)
        );

        // Port of:
        // cap2part = new LineSegmentXZ(
        //     interpolateBetween(cap2.p1, cap2.p2, 1/6.0),
        //     interpolateBetween(cap2.p1, cap2.p2, 5/6.0));
        cap2part = (
            Vector2.Lerp(cap2.P1, cap2.P2, 1f / 6f),
            Vector2.Lerp(cap2.P1, cap2.P2, 5f / 6f)
        );
    }

    /// <summary>
    /// Returns polygon with ridge and cap part endpoints inserted.
    /// Port of GambrelRoof.getPolygon().
    /// </summary>
    public override List<Vector2> GetPolygon()
    {
        // Port of:
        // newOuter = insertIntoPolygon(newOuter, ridge.p1, SNAP_DISTANCE);
        // newOuter = insertIntoPolygon(newOuter, ridge.p2, SNAP_DISTANCE);
        // newOuter = insertIntoPolygon(newOuter, cap1part.p1, SNAP_DISTANCE);
        // ... etc
        var poly = FaceDecompositionUtil.InsertIntoPolygon(originalPolygon, ridge.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, ridge.P2, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap1part.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap1part.P2, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap2part.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap2part.P2, SNAP_DISTANCE);
        return poly;
    }

    /// <summary>
    /// No inner points.
    /// Port of GambrelRoof.getInnerPoints(): return emptyList();
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// Returns ridge + 2 diagonal cross-connections between cap parts.
    /// Port of GambrelRoof.getInnerSegments().
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments()
    {
        var poly = GetPolygon();

        // Snap all endpoints to actual polygon vertices
        var actualRidgeP1 = FindNearestVertex(poly, ridge.P1);
        var actualRidgeP2 = FindNearestVertex(poly, ridge.P2);
        var actualCap1P1 = FindNearestVertex(poly, cap1part.P1);
        var actualCap1P2 = FindNearestVertex(poly, cap1part.P2);
        var actualCap2P1 = FindNearestVertex(poly, cap2part.P1);
        var actualCap2P2 = FindNearestVertex(poly, cap2part.P2);

        // Port of: return asList(ridge,
        //     new LineSegmentXZ(cap1part.p1, cap2part.p2),
        //     new LineSegmentXZ(cap1part.p2, cap2part.p1));
        return new List<(Vector2, Vector2)>
        {
            (actualRidgeP1, actualRidgeP2),
            (actualCap1P1, actualCap2P2),
            (actualCap1P2, actualCap2P1)
        };
    }

    /// <summary>
    /// Piecewise height formula: gentle upper slope (< 2/3), steep lower slope (>= 2/3).
    /// 1:1 port of GambrelRoof.getRoofHeightAt_noInterpolation().
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        // Port of:
        // double distRidge = distanceFromLineSegment(pos, ridge);
        // double relativePlacement = distRidge / maxDistanceToRidge;
        float distRidge = FaceDecompositionUtil.DistancePointToSegment(pos, ridge.P1, ridge.P2);
        float relativePlacement = distRidge / maxDistanceToRidge;

        // Port of:
        // if (relativePlacement < 2/3.0) {
        //     return roofHeight - 1/2.0 * roofHeight * relativePlacement;
        // } else {
        //     return roofHeight - 1/3.0 * roofHeight - 2 * roofHeight * (relativePlacement - 2/3.0);
        // }
        if (relativePlacement < 2f / 3f)
        {
            return roofHeight - 0.5f * roofHeight * relativePlacement;
        }
        else
        {
            return roofHeight - roofHeight / 3f - 2f * roofHeight * (relativePlacement - 2f / 3f);
        }
    }
}
