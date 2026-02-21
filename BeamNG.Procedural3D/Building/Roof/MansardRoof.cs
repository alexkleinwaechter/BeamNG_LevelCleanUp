namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Mansard roof: four-sided gambrel-style hip roof with two slopes per side.
/// Ridge offset = 1/3 (same as hipped), with intermediate mansard edges at 1/3 interpolation.
/// 13 inner segments define the complex face structure.
/// 1:1 port of OSM2World's MansardRoof.java.
/// </summary>
public class MansardRoof : RoofWithRidge
{
    private readonly (Vector2 P1, Vector2 P2) mansardEdge1;
    private readonly (Vector2 P1, Vector2 P2) mansardEdge2;

    public MansardRoof(BuildingData building, List<Vector2> polygon)
        : base(1f / 3f, building, polygon)
    {
        // Port of:
        // mansardEdge1 = new LineSegmentXZ(
        //     interpolateBetween(cap1.p1, ridge.p1, 1/3.0),
        //     interpolateBetween(cap2.p2, ridge.p2, 1/3.0));
        mansardEdge1 = (
            Vector2.Lerp(cap1.P1, ridge.P1, 1f / 3f),
            Vector2.Lerp(cap2.P2, ridge.P2, 1f / 3f)
        );

        // Port of:
        // mansardEdge2 = new LineSegmentXZ(
        //     interpolateBetween(cap1.p2, ridge.p1, 1/3.0),
        //     interpolateBetween(cap2.p1, ridge.p2, 1/3.0));
        mansardEdge2 = (
            Vector2.Lerp(cap1.P2, ridge.P1, 1f / 3f),
            Vector2.Lerp(cap2.P1, ridge.P2, 1f / 3f)
        );
    }

    /// <summary>
    /// Returns the original polygon unchanged.
    /// Port of MansardRoof.getPolygon(): return originalPolygon;
    /// </summary>
    public override List<Vector2> GetPolygon() => originalPolygon;

    /// <summary>
    /// No inner points.
    /// Port of MansardRoof.getInnerPoints(): return emptyList();
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// Returns 13 inner segments defining the mansard face structure.
    /// 1:1 port of MansardRoof.getInnerSegments().
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments()
    {
        var poly = GetPolygon();

        // Snap cap endpoints to polygon vertices
        var actualCap1P1 = FindNearestVertex(poly, cap1.P1);
        var actualCap1P2 = FindNearestVertex(poly, cap1.P2);
        var actualCap2P1 = FindNearestVertex(poly, cap2.P1);
        var actualCap2P2 = FindNearestVertex(poly, cap2.P2);

        // Port of: return asList(ridge,
        //     mansardEdge1, mansardEdge2,
        //     ridge.p1→mansardEdge1.p1, ridge.p1→mansardEdge2.p1,
        //     ridge.p2→mansardEdge1.p2, ridge.p2→mansardEdge2.p2,
        //     cap1.p1→mansardEdge1.p1, cap2.p2→mansardEdge1.p2,
        //     cap1.p2→mansardEdge2.p1, cap2.p1→mansardEdge2.p2,
        //     mansardEdge1.p1→mansardEdge2.p1, mansardEdge1.p2→mansardEdge2.p2);
        return new List<(Vector2, Vector2)>
        {
            // Ridge
            (ridge.P1, ridge.P2),
            // Mansard edges
            (mansardEdge1.P1, mansardEdge1.P2),
            (mansardEdge2.P1, mansardEdge2.P2),
            // Ridge to mansard edge connections
            (ridge.P1, mansardEdge1.P1),
            (ridge.P1, mansardEdge2.P1),
            (ridge.P2, mansardEdge1.P2),
            (ridge.P2, mansardEdge2.P2),
            // Cap to mansard edge connections
            (actualCap1P1, mansardEdge1.P1),
            (actualCap2P2, mansardEdge1.P2),
            (actualCap1P2, mansardEdge2.P1),
            (actualCap2P1, mansardEdge2.P2),
            // Cross-connections between mansard edges
            (mansardEdge1.P1, mansardEdge2.P1),
            (mansardEdge1.P2, mansardEdge2.P2)
        };
    }

    /// <summary>
    /// Height at key points: ridge, mansard edge vertices, polygon vertices.
    /// 1:1 port of MansardRoof.getRoofHeightAt_noInterpolation().
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        // Port of: if (ridge.p1.equals(pos) || ridge.p2.equals(pos)) return roofHeight;
        if (Vector2.DistanceSquared(pos, ridge.P1) < 1e-6f ||
            Vector2.DistanceSquared(pos, ridge.P2) < 1e-6f)
        {
            return roofHeight;
        }

        // Port of: if (getPolygon().getOuter().getVertexCollection().contains(pos)) return 0.0;
        foreach (var v in originalPolygon)
        {
            if (Vector2.DistanceSquared(pos, v) < 1e-6f)
                return 0f;
        }

        // Port of: if (mansardEdge1.p1.equals(pos) || ... ) return roofHeight - 1/3.0 * roofHeight;
        if (Vector2.DistanceSquared(pos, mansardEdge1.P1) < 1e-6f ||
            Vector2.DistanceSquared(pos, mansardEdge1.P2) < 1e-6f ||
            Vector2.DistanceSquared(pos, mansardEdge2.P1) < 1e-6f ||
            Vector2.DistanceSquared(pos, mansardEdge2.P2) < 1e-6f)
        {
            return roofHeight - roofHeight / 3f;
        }

        // Port of: return null;
        return null;
    }
}
