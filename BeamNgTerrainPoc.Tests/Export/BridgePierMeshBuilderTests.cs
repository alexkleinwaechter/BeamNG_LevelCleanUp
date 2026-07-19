using System.Numerics;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.RoadMesh;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Doc 19 §4 — pier solids: a 6-face cap box whose top follows the banked soffit, plus 1–2
///     octagonal columns from inside the cap down to the ground-embedded bottom. Same authoring
///     discipline as the deck (<c>AddFace</c>: outward flat normals, direct BeamNG-DAE path).
/// </summary>
public class BridgePierMeshBuilderTests
{
    // Cap: 6 quads (24 v / 12 t). Column: 8 side quads (32 v / 16 t) + 2 octagon fans (9 v / 8 t each).
    private const int CapVerts = 24, CapTris = 12;
    private const int ColumnVerts = 32 + 2 * 9, ColumnTris = 16 + 2 * 8;

    private static BridgePierSpec Spec(
        float capLeftZ = 8.8f, float capRightZ = 8.8f, int columns = 1,
        float bottomZ = -1.5f, float capDepth = 1.0f)
    {
        var columnSpecs = new List<BridgePierColumnSpec>();
        for (var i = 0; i < columns; i++)
            columnSpecs.Add(new BridgePierColumnSpec(new Vector2(100f, 100f + i * 4f), bottomZ, 1.2f));

        return new BridgePierSpec
        {
            Normal = new Vector2(0f, 1f),
            Tangent = new Vector2(1f, 0f),
            CapTopLeft = new Vector3(100f, 96.5f, capLeftZ),
            CapTopRight = new Vector3(100f, 103.5f, capRightZ),
            CapLength = 1.5f,
            CapDepth = capDepth,
            Columns = columnSpecs,
        };
    }

    private static Mesh Build(params BridgePierSpec[] specs) =>
        new BridgePierMeshBuilder().Build(specs, "piers", "mat");

    [Fact]
    public void SingleColumnPier_MatchesCountFormula()
    {
        var mesh = Build(Spec());

        Assert.Equal(CapVerts + ColumnVerts, mesh.Vertices.Count);
        Assert.Equal(CapTris + ColumnTris, mesh.Triangles.Count);
        Assert.Equal("piers", mesh.Name);
        Assert.Equal("mat", mesh.MaterialName);
    }

    [Fact]
    public void TwinColumnPier_MatchesCountFormula()
    {
        var mesh = Build(Spec(columns: 2));

        Assert.Equal(CapVerts + 2 * ColumnVerts, mesh.Vertices.Count);
        Assert.Equal(CapTris + 2 * ColumnTris, mesh.Triangles.Count);
    }

    [Fact]
    public void CapTop_TouchesSoffit_AndColumnBottom_IsGroundEmbed()
    {
        var mesh = Build(Spec());

        Assert.Equal(8.8f, mesh.Vertices.Max(v => v.Position.Z), 3);  // cap top on the soffit
        Assert.Equal(-1.5f, mesh.Vertices.Min(v => v.Position.Z), 3); // column bottom (ground − embed)
    }

    [Fact]
    public void BankedDeck_CapTopFollowsTheSoffitTilt()
    {
        var mesh = Build(Spec(capLeftZ: 8.4f, capRightZ: 9.2f));

        // Both banked cap-top corner elevations must appear in the solid (the top face tilts).
        Assert.Contains(mesh.Vertices, v => MathF.Abs(v.Position.Z - 8.4f) < 1e-3f);
        Assert.Contains(mesh.Vertices, v => MathF.Abs(v.Position.Z - 9.2f) < 1e-3f);
        Assert.Equal(9.2f, mesh.Vertices.Max(v => v.Position.Z), 3);
    }

    [Fact]
    public void ColumnTop_EmbedsIntoTheCap_NoCoplanarSeam()
    {
        var mesh = Build(Spec());

        // Cap bottom at 8.8 − 1.0 = 7.8; column top must sit 0.05 ABOVE it (inside the cap solid).
        Assert.Contains(mesh.Vertices, v => MathF.Abs(v.Position.Z - 7.85f) < 1e-3f);
        Assert.DoesNotContain(mesh.Vertices,
            v => MathF.Abs(v.Position.Z - 7.8f) < 1e-4f && v.Normal.Z > 0.9f); // no up-facing face AT cap bottom
    }

    [Fact]
    public void CapBelowGround_SkipsTheColumn_KeepsTheCap()
    {
        // Degenerate: column bottom above the cap bottom (deck almost on the ground).
        var mesh = Build(Spec(bottomZ: 8.5f));

        Assert.Equal(CapVerts, mesh.Vertices.Count);
        Assert.Equal(CapTris, mesh.Triangles.Count);
    }

    [Fact]
    public void AllFaces_HaveOutwardFlatNormals()
    {
        var mesh = Build(Spec(capLeftZ: 8.4f, capRightZ: 9.2f, columns: 2));

        foreach (var t in mesh.Triangles)
        {
            var p0 = mesh.Vertices[t.V0].Position;
            var p1 = mesh.Vertices[t.V1].Position;
            var p2 = mesh.Vertices[t.V2].Position;
            var geometric = Vector3.Cross(p1 - p0, p2 - p0);
            Assert.True(geometric.LengthSquared() > 1e-10f, "degenerate triangle");
            geometric = Vector3.Normalize(geometric);

            // Flat shading: the stored normal IS the face normal (winding CCW-from-outside).
            var stored = mesh.Vertices[t.V0].Normal;
            Assert.True(Vector3.Dot(geometric, stored) > 0.99f,
                $"stored normal {stored} disagrees with winding normal {geometric}");
        }
    }

    [Fact]
    public void ColumnSides_PointAwayFromTheColumnAxis()
    {
        var mesh = Build(Spec());
        var axis = new Vector2(100f, 100f);

        foreach (var v in mesh.Vertices)
        {
            if (MathF.Abs(v.Normal.Z) > 0.01f) continue; // only the vertical side faces
            if (v.Position.Z > 7.5f) continue;           // skip all cap verts (cap spans 7.8–8.8)
            var radial = new Vector2(v.Position.X, v.Position.Y) - axis;
            if (radial.LengthSquared() < 1e-6f) continue;
            Assert.True(Vector2.Dot(Vector2.Normalize(radial), new Vector2(v.Normal.X, v.Normal.Y)) > 0f,
                $"column side normal points inward at {v.Position}");
        }
    }

    [Fact]
    public void EmptySpecs_EmptyMesh()
    {
        var mesh = Build();
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Triangles);
    }
}
