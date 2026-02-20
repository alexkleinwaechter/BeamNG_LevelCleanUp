namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Round roof: cylindrical arc profile perpendicular to the ridge.
/// Ridge at polygon edge (offset = 0), subdivided into multiple rings
/// to approximate the curved surface.
/// 1:1 port of OSM2World's RoundRoof.java.
/// </summary>
public class RoundRoof : RoofWithRidge
{
    private const float ROOF_SUBDIVISIONS_PER_HEIGHT_METER = 1f;

    private readonly List<(Vector2 P1, Vector2 P2)> capParts;
    private readonly int rings;

    public RoundRoof(BuildingData building, List<Vector2> polygon)
        : base(0f, building, polygon)
    {
        // Port of:
        // double height = Objects.requireNonNullElse(super.calculatePreliminaryHeight(), BuildingPart.DEFAULT_RIDGE_HEIGHT);
        // rings = (int)max(3, height / ROOF_SUBDIVISIONS_PER_HEIGHT_METER);
        float height = roofHeight > 0 ? roofHeight : 5f; // DEFAULT_RIDGE_HEIGHT = 5
        rings = (int)MathF.Max(3f, height / ROOF_SUBDIVISIONS_PER_HEIGHT_METER);

        // Port of: decide where to place the segments
        // capParts = new ArrayList<>(rings*2);
        // float step = 0.5f / (rings + 1);
        // for (int i = 1; i <= rings; i++) {
        //     capParts.add(new LineSegmentXZ(
        //         interpolateBetween(cap1.p1, cap1.p2, i * step),
        //         interpolateBetween(cap1.p1, cap1.p2, 1 - i * step)));
        //     capParts.add(new LineSegmentXZ(
        //         interpolateBetween(cap2.p1, cap2.p2, i * step),
        //         interpolateBetween(cap2.p1, cap2.p2, 1 - i * step)));
        // }
        capParts = new List<(Vector2, Vector2)>(rings * 2);
        float step = 0.5f / (rings + 1);
        for (int i = 1; i <= rings; i++)
        {
            capParts.Add((
                Vector2.Lerp(cap1.P1, cap1.P2, i * step),
                Vector2.Lerp(cap1.P1, cap1.P2, 1f - i * step)
            ));
            capParts.Add((
                Vector2.Lerp(cap2.P1, cap2.P2, i * step),
                Vector2.Lerp(cap2.P1, cap2.P2, 1f - i * step)
            ));
        }
    }

    /// <summary>
    /// Returns polygon with ridge and all cap part endpoints inserted.
    /// Port of RoundRoof.getPolygon().
    /// </summary>
    public override List<Vector2> GetPolygon()
    {
        // Port of:
        // newOuter = insertIntoPolygon(newOuter, ridge.p1, SNAP_DISTANCE);
        // newOuter = insertIntoPolygon(newOuter, ridge.p2, SNAP_DISTANCE);
        // for (LineSegmentXZ capPart : capParts) {
        //     newOuter = insertIntoPolygon(newOuter, capPart.p1, SNAP_DISTANCE);
        //     newOuter = insertIntoPolygon(newOuter, capPart.p2, SNAP_DISTANCE);
        // }
        var poly = FaceDecompositionUtil.InsertIntoPolygon(originalPolygon, ridge.P1, SNAP_DISTANCE);
        poly = FaceDecompositionUtil.InsertIntoPolygon(poly, ridge.P2, SNAP_DISTANCE);

        foreach (var capPart in capParts)
        {
            poly = FaceDecompositionUtil.InsertIntoPolygon(poly, capPart.P1, SNAP_DISTANCE);
            poly = FaceDecompositionUtil.InsertIntoPolygon(poly, capPart.P2, SNAP_DISTANCE);
        }

        return poly;
    }

    /// <summary>
    /// No inner points.
    /// Port of RoundRoof.getInnerPoints(): return emptyList();
    /// </summary>
    public override List<Vector2> GetInnerPoints() => new();

    /// <summary>
    /// Returns ridge + cross-connections between cap parts.
    /// Port of RoundRoof.getInnerSegments().
    /// </summary>
    public override List<(Vector2 P1, Vector2 P2)> GetInnerSegments()
    {
        var poly = GetPolygon();

        // Port of:
        // innerSegments.add(ridge);
        // for (int i = 0; i < rings * 2; i += 2) {
        //     LineSegmentXZ cap1part = capParts.get(i);
        //     LineSegmentXZ cap2part = capParts.get(i+1);
        //     innerSegments.add(new LineSegmentXZ(cap1part.p1, cap2part.p2));
        //     innerSegments.add(new LineSegmentXZ(cap1part.p2, cap2part.p1));
        // }
        var segments = new List<(Vector2, Vector2)>(rings * 2 + 1);

        // Snap ridge to polygon vertices
        var actualRidgeP1 = FindNearestVertex(poly, ridge.P1);
        var actualRidgeP2 = FindNearestVertex(poly, ridge.P2);
        segments.Add((actualRidgeP1, actualRidgeP2));

        for (int i = 0; i < rings * 2; i += 2)
        {
            var cap1part = capParts[i];
            var cap2part = capParts[i + 1];

            var actualCap1P1 = FindNearestVertex(poly, cap1part.P1);
            var actualCap1P2 = FindNearestVertex(poly, cap1part.P2);
            var actualCap2P1 = FindNearestVertex(poly, cap2part.P1);
            var actualCap2P2 = FindNearestVertex(poly, cap2part.P2);

            segments.Add((actualCap1P1, actualCap2P2));
            segments.Add((actualCap1P2, actualCap2P1));
        }

        return segments;
    }

    /// <summary>
    /// Height using circular arc formula.
    /// 1:1 port of RoundRoof.getRoofHeightAt_noInterpolation().
    /// </summary>
    protected override float? GetRoofHeightAtNoInterpolation(Vector2 pos)
    {
        // Port of:
        // double radius;
        // if (roofHeight < maxDistanceToRidge) {
        //     double squaredHeight = roofHeight * roofHeight;
        //     double squaredDist = maxDistanceToRidge * maxDistanceToRidge;
        //     double centerY = (squaredDist - squaredHeight) / (2 * roofHeight);
        //     radius = sqrt(squaredDist + centerY * centerY);
        // } else {
        //     radius = 0;
        // }
        float radius;
        if (roofHeight < maxDistanceToRidge)
        {
            float squaredHeight = roofHeight * roofHeight;
            float squaredDist = maxDistanceToRidge * maxDistanceToRidge;
            float centerY = (squaredDist - squaredHeight) / (2f * roofHeight);
            radius = MathF.Sqrt(squaredDist + centerY * centerY);
        }
        else
        {
            radius = 0;
        }

        float distRidge = FaceDecompositionUtil.DistancePointToSegment(pos, ridge.P1, ridge.P2);
        float result;

        if (radius > 0)
        {
            // Port of:
            // double relativePlacement = distRidge / radius;
            // result = roofHeight - radius + sqrt(1.0 - relativePlacement * relativePlacement) * radius;
            float relativePlacement = distRidge / radius;
            float clamped = MathF.Min(1f, relativePlacement); // Clamp to avoid NaN in sqrt
            result = roofHeight - radius + MathF.Sqrt(1f - clamped * clamped) * radius;
        }
        else
        {
            // Port of: double relativePlacement = distRidge / maxDistanceToRidge;
            //          result = (1 - pow(relativePlacement, 2.5)) * roofHeight;
            float relativePlacement = distRidge / maxDistanceToRidge;
            result = (1f - MathF.Pow(relativePlacement, 2.5f)) * roofHeight;
        }

        // Port of: return max(result, 0.0);
        return MathF.Max(result, 0f);
    }

    /// <summary>
    /// Override to apply smooth normals for rounded appearance.
    /// Port of: material.makeSmooth() in Java constructor.
    /// </summary>
    public override Mesh GenerateMesh(Vector2 textureScale)
    {
        var builder = new MeshBuilder()
            .WithName($"roof_{building.OsmId}")
            .WithMaterial(building.RoofMaterial);

        var poly = GetPolygon();
        var innerSegs = GetInnerSegments();
        var innerPts = GetInnerPoints();

        var faces = FaceDecompositionUtil.SplitPolygonIntoFaces(poly, innerSegs);

        float baseEle = building.RoofBaseHeight;
        var knownAngles = new List<float>();

        foreach (var face in faces)
        {
            if (face.Count < 3) continue;
            RenderFace(builder, face, baseEle, textureScale, knownAngles);
        }

        // Apply smooth normals for round appearance (port of material.makeSmooth())
        builder.CalculateSmoothNormals();

        return builder.Build();
    }
}
