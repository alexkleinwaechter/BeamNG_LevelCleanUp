namespace BeamNG.Procedural3D.Building.Roof;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Cone roof: identical geometry to PyramidalRoof, but with smooth normals.
/// 1:1 port of OSM2World's ConeRoof.java.
///
/// In Java: ConeRoof extends PyramidalRoof and passes material.makeSmooth().
/// In C#: we override GenerateMesh() to apply smooth normals after generation.
/// Reuses HeightfieldRoof.RenderFace() for correct slope-aware UV mapping.
/// </summary>
public class ConeRoof : PyramidalRoof
{
    public ConeRoof(BuildingData building, List<Vector2> polygon)
        : base(building, polygon) { }

    /// <summary>
    /// Generates the cone mesh: same triangles as pyramidal, but with smooth normals
    /// for a rounded appearance instead of faceted.
    /// Port of: super(originalPolygon, tags, material.makeSmooth())
    /// </summary>
    public override Mesh GenerateMesh(Vector2 textureScale)
    {
        var builder = new MeshBuilder()
            .WithName($"roof_{building.OsmId}")
            .WithMaterial(building.RoofMaterial);

        var poly = GetPolygon();
        var innerSegs = GetInnerSegments();

        var faces = FaceDecompositionUtil.SplitPolygonIntoFaces(poly, innerSegs);

        float baseEle = building.RoofBaseHeight;
        var knownAngles = new List<float>();

        foreach (var face in faces)
        {
            if (face.Count < 3) continue;
            RenderFace(builder, face, baseEle, textureScale, knownAngles);
        }

        // Apply smooth normals for cone appearance (port of material.makeSmooth())
        builder.CalculateSmoothNormals();

        return builder.Build();
    }
}
