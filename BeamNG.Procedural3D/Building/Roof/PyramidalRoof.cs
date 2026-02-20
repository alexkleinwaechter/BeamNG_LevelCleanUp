namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Pyramidal roof: all faces converge to a single apex at the polygon centroid.
/// 1:1 port of OSM2World's PyramidalRoof.java.
///
/// Each polygon vertex connects to the central apex via an inner segment,
/// creating N triangular faces (N = polygon vertex count).
/// </summary>
public class PyramidalRoof : HeightfieldRoof
{
    private readonly Vector2 apex;
    private readonly List<(Vector2 P1, Vector2 P2)> innerSegments;

    public PyramidalRoof(BuildingData building, List<Vector2> polygon)
        : base(building, polygon)
    {
        // Port of: apex = outerPoly.getCentroid();
        apex = RoofWithRidge.CalculateCentroid(polygon);

        // Port of: for (VectorXZ v : outerPoly.getVertices()) innerSegments.add(new LineSegmentXZ(v, apex));
        innerSegments = new List<(Vector2, Vector2)>();
        foreach (var v in polygon)
        {
            innerSegments.Add((v, apex));
        }

        // Calculate roof height
        roofHeight = building.RoofHeight > 0 ? building.RoofHeight : CalculateDefaultRoofHeight();
    }

    /// <summary>
    /// Returns the original polygon unchanged.
    /// Port of PyramidalRoof.getPolygon(): return originalPolygon;
    /// </summary>
    public override List<Vector2> GetPolygon() => originalPolygon;

    /// <summary>
    /// Returns the apex as the single inner point.
    /// Port of PyramidalRoof.getInnerPoints(): return singletonList(apex);
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new() { apex };

    /// <summary>
    /// Returns line segments from each polygon vertex to the apex.
    /// Port of PyramidalRoof.getInnerSegments(): return innerSegments;
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments() => innerSegments;

    /// <summary>
    /// Height at known positions: apex = roofHeight, polygon vertices = 0, else null.
    /// Port of PyramidalRoof.getRoofHeightAt_noInterpolation().
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        // Port of: if (apex.equals(pos)) return roofHeight;
        if (Vector2.DistanceSquared(pos, apex) < 1e-6f)
            return roofHeight;

        // Port of: if (originalPolygon.getOuter().getVertices().contains(pos)) return 0.0;
        foreach (var v in originalPolygon)
        {
            if (Vector2.DistanceSquared(pos, v) < 1e-6f)
                return 0f;
        }

        // Port of: return null;
        return null;
    }

    /// <summary>
    /// Default roof height when not specified by tags.
    /// Uses max distance from any vertex to apex as basis.
    /// </summary>
    private float CalculateDefaultRoofHeight()
    {
        float maxDist = 0;
        foreach (var v in originalPolygon)
        {
            float d = Vector2.Distance(v, apex);
            maxDist = MathF.Max(maxDist, d);
        }
        return MathF.Max(1.5f, maxDist * 0.4f);
    }
}
