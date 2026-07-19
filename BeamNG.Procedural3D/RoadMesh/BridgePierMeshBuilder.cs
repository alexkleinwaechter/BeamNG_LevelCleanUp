namespace BeamNG.Procedural3D.RoadMesh;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// One fully-resolved pier to build, in BeamNG world coordinates (doc 19 §4). The placement layer
/// (<c>BridgePierPlanner</c>) owns all decisions — station, keep-out, column count/positions, ground
/// embed — this type only carries the resulting geometry frame.
/// </summary>
public sealed class BridgePierSpec
{
    /// <summary>Deck lateral normal at the pier station (unit, plan view, points right).</summary>
    public required Vector2 Normal { get; init; }

    /// <summary>Deck forward tangent at the pier station (unit, plan view).</summary>
    public required Vector2 Tangent { get; init; }

    /// <summary>Cap-top corner on the LEFT (−normal) cap extent, ON the banked soffit plane.</summary>
    public required Vector3 CapTopLeft { get; init; }

    /// <summary>Cap-top corner on the RIGHT (+normal) cap extent, ON the banked soffit plane.</summary>
    public required Vector3 CapTopRight { get; init; }

    /// <summary>Cap (head beam) length along the deck tangent (m).</summary>
    public required float CapLength { get; init; }

    /// <summary>Cap depth below the soffit (m).</summary>
    public required float CapDepth { get; init; }

    /// <summary>Columns of this pier (1 centered, or a twin bent).</summary>
    public required IReadOnlyList<BridgePierColumnSpec> Columns { get; init; }
}

/// <summary>One octagonal column: plan center (world), absolute bottom Z (ground − embed), diameter.</summary>
public sealed record BridgePierColumnSpec(Vector2 CenterXY, float BottomZ, float Diameter);

/// <summary>
/// Builds the solid pier meshes for one bridge span (doc 19 §4): per pier a <b>cap</b> box whose top
/// face follows the banked soffit (the end-stamp construction of <c>a73106d</c>, applied to a 6-face
/// box) and 1–2 <b>octagonal columns</b> from just inside the cap down to <c>BottomZ</c> (embedded
/// below ground, §3d). Every face is authored through <see cref="BridgeDeckMeshBuilder.AddFace"/> —
/// the direct BeamNG-DAE path writes positions/normals/winding as-is, so outward flat normals in
/// plain Z-up are mandatory (see the deck builder's remarks). Solids overlap (column tops embed into
/// the cap, cap top touches the soffit) instead of sharing coplanar faces, so nothing z-fights and
/// each solid stays watertight on its own.
/// </summary>
public sealed class BridgePierMeshBuilder
{
    /// <summary>How far (m) a column's top reaches UP into the cap solid (overlap, not coplanar).</summary>
    private const float ColumnCapOverlapMeters = 0.05f;

    private const int OctagonSides = 8;

    /// <summary>Builds all piers of one span into a single mesh. Empty mesh when no specs.</summary>
    public Mesh Build(IReadOnlyList<BridgePierSpec> piers, string meshName, string? materialName)
    {
        ArgumentNullException.ThrowIfNull(piers);

        var builder = new MeshBuilder()
            .WithName(meshName)
            .WithMaterial(materialName);

        foreach (var pier in piers)
            BuildPier(builder, pier);

        return builder.Build();
    }

    private static void BuildPier(MeshBuilder b, BridgePierSpec pier)
    {
        var normal3 = new Vector3(pier.Normal.X, pier.Normal.Y, 0f);
        var tangent3 = new Vector3(pier.Tangent.X, pier.Tangent.Y, 0f);
        var halfLen = tangent3 * (pier.CapLength / 2f);
        var drop = new Vector3(0f, 0f, pier.CapDepth);

        // ── Cap box: top face ON the banked soffit line (same Z fore/aft — 1.5 m of longitudinal
        // grade is invisible), bottom parallel to it, so the whole cap tilts with the deck. ──
        Vector3 tlB = pier.CapTopLeft - halfLen, tlF = pier.CapTopLeft + halfLen;
        Vector3 trB = pier.CapTopRight - halfLen, trF = pier.CapTopRight + halfLen;
        Vector3 blB = tlB - drop, blF = tlF - drop, brB = trB - drop, brF = trF - drop;

        BridgeDeckMeshBuilder.AddFace(b, tlB, trB, trF, tlF, Vector3.UnitZ);      // top (under soffit)
        BridgeDeckMeshBuilder.AddFace(b, blB, brB, brF, blF, -Vector3.UnitZ);     // bottom
        BridgeDeckMeshBuilder.AddFace(b, tlB, tlF, blF, blB, -normal3);           // left side
        BridgeDeckMeshBuilder.AddFace(b, trB, trF, brF, brB, normal3);            // right side
        BridgeDeckMeshBuilder.AddFace(b, tlF, trF, brF, blF, tangent3);           // front cap
        BridgeDeckMeshBuilder.AddFace(b, tlB, trB, brB, blB, -tangent3);          // back cap

        // ── Columns: octagonal prisms from inside the cap down to BottomZ. Column top Z is the cap
        // BOTTOM at the column's lateral position (lerped between the banked cap corners) plus a
        // small upward overlap into the cap solid. ──
        var capExtent = Vector3.Distance(pier.CapTopLeft, pier.CapTopRight);
        foreach (var column in pier.Columns)
        {
            var center = new Vector3(column.CenterXY.X, column.CenterXY.Y, 0f);
            float topZ;
            if (capExtent > 1e-3f)
            {
                var t = Math.Clamp(
                    Vector3.Dot(center - pier.CapTopLeft, pier.CapTopRight - pier.CapTopLeft) /
                    (capExtent * capExtent), 0f, 1f);
                topZ = pier.CapTopLeft.Z + (pier.CapTopRight.Z - pier.CapTopLeft.Z) * t
                       - pier.CapDepth + ColumnCapOverlapMeters;
            }
            else
            {
                topZ = pier.CapTopLeft.Z - pier.CapDepth + ColumnCapOverlapMeters;
            }

            if (topZ <= column.BottomZ) continue; // degenerate (cap below ground) — no column

            BuildColumn(b, column.CenterXY, topZ, column.BottomZ, column.Diameter);
        }
    }

    /// <summary>Watertight octagonal prism: 8 side quads + top/bottom triangle fans (flat ±Z normals).
    /// 8 sides read round at driving distance for 10 quads' worth of geometry (corpus 09a §6).</summary>
    private static void BuildColumn(MeshBuilder b, Vector2 center, float topZ, float bottomZ, float diameter)
    {
        var r = diameter / 2f;
        var ring = new Vector2[OctagonSides];
        for (var i = 0; i < OctagonSides; i++)
        {
            var angle = MathF.PI * 2f * i / OctagonSides;
            ring[i] = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
        }

        for (var i = 0; i < OctagonSides; i++)
        {
            var p0 = ring[i];
            var p1 = ring[(i + 1) % OctagonSides];
            var mid = (p0 + p1) / 2f - center;
            var outward = new Vector3(mid.X, mid.Y, 0f);
            BridgeDeckMeshBuilder.AddFace(b,
                new Vector3(p0.X, p0.Y, bottomZ), new Vector3(p1.X, p1.Y, bottomZ),
                new Vector3(p1.X, p1.Y, topZ), new Vector3(p0.X, p0.Y, topZ),
                outward);
        }

        AddOctagonFan(b, ring, center, topZ, up: true);
        AddOctagonFan(b, ring, center, bottomZ, up: false);
    }

    /// <summary>Octagon cap as a triangle fan around the center, flat ±Z normal, CCW from outside.</summary>
    private static void AddOctagonFan(MeshBuilder b, Vector2[] ring, Vector2 center, float z, bool up)
    {
        var normal = up ? Vector3.UnitZ : -Vector3.UnitZ;
        var centerIdx = b.AddVertex(new Vector3(center.X, center.Y, z), normal, new Vector2(0.5f, 0.5f));
        var ringIdx = new int[ring.Length];
        for (var i = 0; i < ring.Length; i++)
            ringIdx[i] = b.AddVertex(new Vector3(ring[i].X, ring[i].Y, z), normal, new Vector2(0f, 0f));

        for (var i = 0; i < ring.Length; i++)
        {
            var next = (i + 1) % ring.Length;
            if (up)
                b.AddTriangle(centerIdx, ringIdx[i], ringIdx[next]); // CCW seen from +Z
            else
                b.AddTriangle(centerIdx, ringIdx[next], ringIdx[i]); // CCW seen from −Z
        }
    }
}
