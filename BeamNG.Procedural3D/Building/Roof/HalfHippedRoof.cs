namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Half-hipped roof: hybrid between gabled and hipped.
/// The ridge offset is 1/6 of cap length (between gabled=0 and hipped=1/3).
/// The cap ends have a partial hip with a vertical gable portion above.
/// 1:1 port of OSM2World's HalfHippedRoof.java.
/// </summary>
public class HalfHippedRoof : RoofWithRidge
{
    private readonly (Vector2 P1, Vector2 P2) cap1part;
    private readonly (Vector2 P1, Vector2 P2) cap2part;

    public HalfHippedRoof(BuildingData building, List<Vector2> polygon)
        : base(1f / 6f, building, polygon)
    {
        // Port of:
        // cap1part = new LineSegmentXZ(
        //     interpolateBetween(cap1.p1, cap1.p2, 0.5 - ridgeOffset / cap1.getLength()),
        //     interpolateBetween(cap1.p1, cap1.p2, 0.5 + ridgeOffset / cap1.getLength()));
        float cap1Length = Vector2.Distance(cap1.P1, cap1.P2);
        float cap1Ratio = cap1Length > 1e-6f ? ridgeOffset / cap1Length : 0f;

        cap1part = (
            Vector2.Lerp(cap1.P1, cap1.P2, 0.5f - cap1Ratio),
            Vector2.Lerp(cap1.P1, cap1.P2, 0.5f + cap1Ratio)
        );

        // Port of: cap2part uses cap1.getLength() (not cap2!) — matches Java exactly
        cap2part = (
            Vector2.Lerp(cap2.P1, cap2.P2, 0.5f - cap1Ratio),
            Vector2.Lerp(cap2.P1, cap2.P2, 0.5f + cap1Ratio)
        );
    }

    /// <summary>
    /// Returns polygon with cap part endpoints inserted.
    /// Port of HalfHippedRoof.getPolygon().
    /// </summary>
    public override List<Vector2> GetPolygon()
    {
        // Port of:
        // newOuter = insertIntoPolygon(newOuter, cap1part.p1, SNAP_DISTANCE);
        // newOuter = insertIntoPolygon(newOuter, cap1part.p2, SNAP_DISTANCE);
        // newOuter = insertIntoPolygon(newOuter, cap2part.p1, SNAP_DISTANCE);
        // newOuter = insertIntoPolygon(newOuter, cap2part.p2, SNAP_DISTANCE);
        var poly = FaceDecompositionUtil.InsertIntoPolygon(originalPolygon, cap1part.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap1part.P2, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap2part.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, cap2part.P2, SNAP_DISTANCE);
        return poly;
    }

    /// <summary>
    /// No inner points.
    /// Port of HalfHippedRoof.getInnerPoints(): return emptyList();
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// Returns ridge + 4 lines from ridge endpoints to cap part endpoints.
    /// Port of HalfHippedRoof.getInnerSegments().
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments()
    {
        var poly = GetPolygon();

        // Snap all endpoints to actual polygon vertices
        var actualCap1P1 = FindNearestVertex(poly, cap1part.P1);
        var actualCap1P2 = FindNearestVertex(poly, cap1part.P2);
        var actualCap2P1 = FindNearestVertex(poly, cap2part.P1);
        var actualCap2P2 = FindNearestVertex(poly, cap2part.P2);

        // Port of: return asList(ridge,
        //     new LineSegmentXZ(ridge.p1, cap1part.p1),
        //     new LineSegmentXZ(ridge.p1, cap1part.p2),
        //     new LineSegmentXZ(ridge.p2, cap2part.p1),
        //     new LineSegmentXZ(ridge.p2, cap2part.p2));
        return new List<(Vector2, Vector2)>
        {
            (ridge.P1, ridge.P2),
            (ridge.P1, actualCap1P1),
            (ridge.P1, actualCap1P2),
            (ridge.P2, actualCap2P1),
            (ridge.P2, actualCap2P2)
        };
    }

    /// <summary>
    /// Height at known positions. Piecewise calculation based on position relative to ridge and caps.
    /// 1:1 port of HalfHippedRoof.getRoofHeightAt_noInterpolation().
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        // Port of: if (ridge.p1.equals(pos) || ridge.p2.equals(pos)) return roofHeight;
        if (Vector2.DistanceSquared(pos, ridge.P1) < 1e-6f ||
            Vector2.DistanceSquared(pos, ridge.P2) < 1e-6f)
        {
            return roofHeight;
        }

        // Port of: if (distanceFromLineSegment(pos, cap1part) < 0.05)
        //     return roofHeight - roofHeight * ridgeOffset / (cap1.getLength()/2);
        if (FaceDecompositionUtil.DistancePointToSegment(pos, cap1part.P1, cap1part.P2) < 0.05f)
        {
            float cap1HalfLength = Vector2.Distance(cap1.P1, cap1.P2) / 2f;
            return cap1HalfLength > 1e-6f
                ? roofHeight - roofHeight * ridgeOffset / cap1HalfLength
                : roofHeight;
        }

        // Port of: if (distanceFromLineSegment(pos, cap2part) < 0.05)
        //     return roofHeight - roofHeight * ridgeOffset / (cap2.getLength()/2);
        if (FaceDecompositionUtil.DistancePointToSegment(pos, cap2part.P1, cap2part.P2) < 0.05f)
        {
            float cap2HalfLength = Vector2.Distance(cap2.P1, cap2.P2) / 2f;
            return cap2HalfLength > 1e-6f
                ? roofHeight - roofHeight * ridgeOffset / cap2HalfLength
                : roofHeight;
        }

        // Port of: if (distanceFromLineSegment(pos, cap1) < 0.05)
        //     double relativeRidgeDist = distanceFromLine(pos, ridge.p1, ridge.p2) / (cap1.getLength() / 2);
        //     return roofHeight * (1 - relativeRidgeDist);
        if (FaceDecompositionUtil.DistancePointToSegment(pos, cap1.P1, cap1.P2) < 0.05f)
        {
            float cap1HalfLength = Vector2.Distance(cap1.P1, cap1.P2) / 2f;
            if (cap1HalfLength > 1e-6f)
            {
                float relativeRidgeDist = FaceDecompositionUtil.DistanceFromLine(pos, ridge.P1, ridge.P2) / cap1HalfLength;
                return roofHeight * (1f - relativeRidgeDist);
            }
            return 0f;
        }

        // Port of: if (distanceFromLineSegment(pos, cap2) < 0.05)
        if (FaceDecompositionUtil.DistancePointToSegment(pos, cap2.P1, cap2.P2) < 0.05f)
        {
            float cap2HalfLength = Vector2.Distance(cap2.P1, cap2.P2) / 2f;
            if (cap2HalfLength > 1e-6f)
            {
                float relativeRidgeDist = FaceDecompositionUtil.DistanceFromLine(pos, ridge.P1, ridge.P2) / cap2HalfLength;
                return roofHeight * (1f - relativeRidgeDist);
            }
            return 0f;
        }

        // Port of: if (getPolygon().getOuter().getVertexCollection().contains(pos)) return 0.0;
        var poly = GetPolygon();
        foreach (var v in poly)
        {
            if (Vector2.DistanceSquared(pos, v) < 1e-6f)
                return 0f;
        }

        // Port of: return null;
        return null;
    }
}
