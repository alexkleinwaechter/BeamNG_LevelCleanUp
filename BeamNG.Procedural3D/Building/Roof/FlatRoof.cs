namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;

/// <summary>
/// Flat roof: height = 0 everywhere.
/// 1:1 port of OSM2World's FlatRoof.java.
///
/// Needed so ALL roofs (including flat) go through the HeightfieldRoof interface,
/// allowing ExteriorBuildingWall to query roof.GetRoofHeightAt() uniformly.
/// </summary>
public class FlatRoof : HeightfieldRoof
{
    public FlatRoof(BuildingData building, List<Vector2> polygon)
        : base(building, polygon)
    {
        roofHeight = 0f;
    }

    /// <summary>
    /// Returns the original polygon unchanged.
    /// Port of FlatRoof.getPolygon(): return originalPolygon;
    /// </summary>
    public override List<Vector2> GetPolygon() => originalPolygon;

    /// <summary>
    /// No inner points for flat roofs.
    /// Port of FlatRoof.getInnerPoints(): return emptyList();
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// No inner segments for flat roofs.
    /// Port of FlatRoof.getInnerSegments(): return emptyList();
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments() => new();

    /// <summary>
    /// Height is always 0 for flat roofs.
    /// Port of FlatRoof.getRoofHeightAt_noInterpolation(): return 0.0;
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos) => 0f;
}
