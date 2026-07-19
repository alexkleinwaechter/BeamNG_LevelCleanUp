using System.Numerics;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.RoadMesh;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     E-B Stage 2 — box bridge deck (spec <c>09-bridge-deck-mesh-spec.md</c> §3). Verifies
///     <see cref="BridgeDeckMeshBuilder"/> produces a closed box: deck top + soffit at
///     <c>top − thickness</c> + two fascia side faces + start/end caps, with thickness from the shared
///     <see cref="BridgeDeckProfile.ComputeDeckThicknessMeters"/> rule and the soffit/fascia built off the
///     banked deck edges.
/// </summary>
public class BridgeDeckMeshBuilderTests
{
    private const float Eps = 1e-3f;

    /// <summary>Straight, level deck with <paramref name="sections"/> evenly spaced over <paramref name="length"/> m.</summary>
    private static List<RoadCrossSection> StraightDeck(
        int sections, float length, float width, float z, float bankRadians = 0f)
    {
        var list = new List<RoadCrossSection>(sections);
        for (var i = 0; i < sections; i++)
        {
            var d = length * i / (sections - 1);
            list.Add(new RoadCrossSection
            {
                CenterPoint = new Vector2(d, 0f),
                CenterElevation = z,
                TangentDirection = new Vector2(1f, 0f),   // along +X
                NormalDirection = new Vector2(0f, 1f),    // points right (+Y)
                WidthMeters = width,
                BankAngleRadians = bankRadians,
                DistanceAlongRoad = d
                // LeftEdgeElevation/RightEdgeElevation left null → derived from bank in RoadCrossSection.
            });
        }

        return list;
    }

    private static Vector3 FaceNormal(Mesh mesh, Triangle t)
    {
        var p0 = mesh.Vertices[t.V0].Position;
        var p1 = mesh.Vertices[t.V1].Position;
        var p2 = mesh.Vertices[t.V2].Position;
        return Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
    }

    [Theory]
    [InlineData(12f, 0.6f)]   // 0.05 * 12 = 0.6, inside clamp
    [InlineData(20f, 1.0f)]   // 0.05 * 20 = 1.0, inside clamp
    [InlineData(1000f, 1.2f)] // 0.05 * 1000 = 50 → clamped to max 1.2
    [InlineData(2f, 0.45f)]   // 0.05 * 2 = 0.1 → clamped to min 0.45
    public void ComputeDeckThickness_ClampsToConfiguredRange(float span, float expected)
    {
        var profile = new BridgeDeckProfile();
        Assert.Equal(expected, BridgeDeckProfile.ComputeDeckThicknessMeters(span, profile), precision: 4);
    }

    /// <summary>Straight deck along +X for 10 stations (1 m spacing), then a hard
    /// <paramref name="angleDegrees"/> kink at a single station, continuing straight for 10 more.</summary>
    private static List<RoadCrossSection> KinkedDeck(float angleDegrees, float width)
    {
        var rad = angleDegrees * MathF.PI / 180f;
        var dir1 = new Vector2(1f, 0f);
        var n1 = new Vector2(0f, 1f);
        var dir2 = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
        var n2 = new Vector2(-dir2.Y, dir2.X);

        var list = new List<RoadCrossSection>();
        var p = Vector2.Zero;
        var d = 0f;
        for (var i = 0; i < 10; i++)
        {
            list.Add(new RoadCrossSection
            {
                CenterPoint = p, CenterElevation = 10f, TangentDirection = dir1,
                NormalDirection = n1, WidthMeters = width, DistanceAlongRoad = d
            });
            p += dir1;
            d += 1f;
        }

        for (var i = 0; i < 10; i++)
        {
            list.Add(new RoadCrossSection
            {
                CenterPoint = p, CenterElevation = 10f, TangentDirection = dir2,
                NormalDirection = n2, WidthMeters = width, DistanceAlongRoad = d
            });
            p += dir2;
            d += 1f;
        }

        return list;
    }

    [Fact]
    public void AntiFoldWeld_SharpKink_InnerEdgeNeverBacktracks()
    {
        // Manhattan span 1192907311 between merged ways 432550257/46177152: a ~27° bend
        // concentrated over a few stations of a 21.6 m deck made the inside edge polyline REVERSE —
        // the deck top pleated over itself (winding-forced normals ⇒ overlapping geometry, vehicles
        // wedged into the fold). The weld must clamp any edge point whose plan advance reverses.
        var cs = KinkedDeck(angleDegrees: 35f, width: 22f);
        var (left, right) = BridgeDeckMeshBuilder.BuildAntiFoldEdges(cs);

        var welded = false;
        for (var i = 1; i < cs.Count; i++)
        {
            var advance = cs[i].CenterPoint - cs[i - 1].CenterPoint;
            var dl = new Vector2(left[i].X - left[i - 1].X, left[i].Y - left[i - 1].Y);
            var dr = new Vector2(right[i].X - right[i - 1].X, right[i].Y - right[i - 1].Y);
            Assert.True(Vector2.Dot(dl, advance) >= 0f, $"left edge backtracks at station {i}");
            Assert.True(Vector2.Dot(dr, advance) >= 0f, $"right edge backtracks at station {i}");
            welded = welded || left[i] == left[i - 1] || right[i] == right[i - 1];
        }

        Assert.True(welded, "the 35° kink on a 22 m deck must actually trigger the weld");
    }

    [Fact]
    public void AntiFoldWeld_StraightDeck_LeavesEdgesUntouched()
    {
        var cs = StraightDeck(10, 90f, 22f, 10f);
        var (left, right) = BridgeDeckMeshBuilder.BuildAntiFoldEdges(cs);

        for (var i = 0; i < cs.Count; i++)
        {
            Assert.Equal(cs[i].GetLeftEdgePosition(), left[i]);
            Assert.Equal(cs[i].GetRightEdgePosition(), right[i]);
        }
    }

    [Fact]
    public void Build_ReturnsEmptyMesh_ForFewerThanTwoSections()
    {
        var mesh = new BridgeDeckMeshBuilder()
            .Build(StraightDeck(2, 20f, 8f, 10f).Take(1).ToList(), new BridgeDeckProfile(), "deck", "mat");

        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Triangles);
        Assert.Equal("deck", mesh.Name);
        Assert.Equal("mat", mesh.MaterialName);
    }

    /// <summary>A profile with parapets and end stamps disabled (pure box shell geometry).</summary>
    private static BridgeDeckProfile BoxOnlyProfile() =>
        new() { ParapetHeightMeters = 0f, GenerateAbutments = false };

    // Box-shell counts: per segment 4 faces (top/soffit/2 fascia) × (4 verts, 2 tris) = 16 v, 8 t;
    // + 2 end caps × (4 verts, 2 tris) = 8 v, 4 t.  Every face is its own quad (dedicated verts,
    // explicit outward flat normals — no shared/smoothed verts), so verts = 16(N-1)+8, tris = 8(N-1)+4.
    private static (int verts, int tris) BoxCounts(int n) => (16 * (n - 1) + 8, 8 * (n - 1) + 4);

    [Fact]
    public void Build_SoffitVerts_SitExactlyThicknessBelowTop()
    {
        // span 20 → thickness 1.0; level deck at z = 10. (Box only so the deck top is the highest geom.)
        var cs = StraightDeck(2, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, BoxOnlyProfile(), "deck", "mat");

        var thickness = BridgeDeckProfile.ComputeDeckThicknessMeters(20f, new BridgeDeckProfile());
        Assert.Equal(1.0f, thickness, precision: 4);

        var topZ = 10f;
        var minZ = mesh.Vertices.Min(v => v.Position.Z);
        var maxZ = mesh.Vertices.Max(v => v.Position.Z);

        Assert.Equal(topZ, maxZ, precision: 3);                 // deck top (no parapet)
        Assert.Equal(topZ - thickness, minZ, precision: 3);     // soffit sits thickness below
    }

    [Fact]
    public void Build_VertexAndTriangleCounts_MatchBoxFormula()
    {
        var cs = StraightDeck(4, 30f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, BoxOnlyProfile(), "deck", "mat");

        var (verts, tris) = BoxCounts(cs.Count);
        Assert.Equal(verts, mesh.Vertices.Count);
        Assert.Equal(tris, mesh.Triangles.Count);
    }

    [Fact]
    public void Build_BoxOnly_WhenAllEndFeaturesDisabled()
    {
        var cs = StraightDeck(5, 40f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, BoxOnlyProfile(), "deck", "mat");

        var (verts, tris) = BoxCounts(cs.Count);
        Assert.Equal(verts, mesh.Vertices.Count);
        Assert.Equal(tris, mesh.Triangles.Count);
    }

    [Fact]
    public void Build_FullMesh_AddsExpectedVertAndTriCounts()
    {
        // Default profile = box + parapets + solid end stamps (no aprons). Every face is a dedicated quad
        // (4 verts / 2 tris) so the solid carries exact outward flat normals. Build-up:
        //   Box shell:   16(N-1)+8 verts, 8(N-1)+4 tris.
        //   Parapets:    per edge 4 faces/segment + 2 end caps → 16(N-1)+8 v, 8(N-1)+4 t;
        //                two edges → 32(N-1)+16 v, 16(N-1)+8 t.
        //   End stamps:  soffit-following, 2 caps + 4 faces per stamp segment; station spacing 10 m >
        //                stamp length 3 m → ONE clipped segment each → 2 stamps × 6 faces → 48 v, 24 t.
        // Full mesh: 16(N-1)+8 + 32(N-1)+16 + 48 = 48(N-1)+72 verts;  8(N-1)+4 + 16(N-1)+8 + 24 = 24(N-1)+36 tris.
        var cs = StraightDeck(4, 30f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");

        int n = cs.Count;
        Assert.Equal(48 * (n - 1) + 72, mesh.Vertices.Count);
        Assert.Equal(24 * (n - 1) + 36, mesh.Triangles.Count);
    }

    [Fact]
    public void Build_NoEndStamps_WhenDisabled()
    {
        // GenerateAbutments = false → drops the 48 stamp verts / 24 stamp tris from the full mesh.
        var cs = StraightDeck(4, 30f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder()
            .Build(cs, new BridgeDeckProfile { GenerateAbutments = false }, "deck", "mat");

        int n = cs.Count;
        Assert.Equal(48 * (n - 1) + 72 - 48, mesh.Vertices.Count);
        Assert.Equal(24 * (n - 1) + 36 - 24, mesh.Triangles.Count);
    }

    [Fact]
    public void Build_EndStamp_IsSolidBlock_FromSoffitToAbutmentDepth_ExtrudedInwardByStampLength()
    {
        // Straight level deck, default profile. span 20 → thickness 1.0, deck top at z = 10.
        var profile = new BridgeDeckProfile();
        var cs = StraightDeck(2, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, profile, "deck", "mat");

        var deckTopZ = 10f;
        var thickness = BridgeDeckProfile.ComputeDeckThicknessMeters(20f, profile);
        var soffitZ = deckTopZ - thickness;                       // 9.0
        var stampBottomZ = soffitZ - profile.AbutmentDepthMeters;  // 8.0

        // The mesh minimum Z is the solid stamp's base, below the soffit.
        var minZ = mesh.Vertices.Min(v => v.Position.Z);
        Assert.Equal(stampBottomZ, minZ, precision: 3);
        Assert.True(stampBottomZ < soffitZ - Eps, "stamp base must sit below the soffit");

        // The start stamp is extruded INWARD (+X) by EndStampLengthMeters: its inner face sits at x ≈ stampLen,
        // at the soffit AND at the dropped base — a solid block, not a zero-thickness wall.
        var stampLen = profile.EndStampLengthMeters; // 3.0
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - stampLen) < Eps &&
                                            Math.Abs(v.Position.Z - soffitZ) < Eps);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - stampLen) < Eps &&
                                            Math.Abs(v.Position.Z - stampBottomZ) < Eps);
        // And the end stamp's inner face is stampLen back from the far end (x ≈ 20 − 3 = 17).
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - (20f - stampLen)) < Eps &&
                                            Math.Abs(v.Position.Z - stampBottomZ) < Eps);
    }

    [Fact]
    public void Build_EndStamp_FollowsArchedSoffit_NoGapUnderRisingDeck()
    {
        // Deck rising into the span (arch): stations at z 10 / 11 / 11.5 over 20 m. The old stamp was a
        // straight horizontal extrusion of the END section — flat top at the end soffit while the real
        // soffit climbed away above it, opening a see-through slit. The stamp top must ride the soffit
        // polyline instead: at the 3 m cut its inner top corners sit ON the soffit (lerped between
        // stations 0 and 1), and the bottom follows at AbutmentDepth below.
        var profile = new BridgeDeckProfile();
        var cs = StraightDeck(3, 20f, 8f, 10f);
        cs[1].CenterElevation = 11f;
        cs[2].CenterElevation = 11.5f;

        var mesh = new BridgeDeckMeshBuilder().Build(cs, profile, "deck", "mat");

        var stampLen = profile.EndStampLengthMeters; // 3.0
        // Soffit (top − thickness 1.0): z = 9 at x = 0, z = 10 at x = 10 → lerp at x = 3 → 9.3.
        var innerTopZ = 9f + (10f - 9f) * (stampLen / 10f);

        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - stampLen) < Eps &&
                                            Math.Abs(v.Position.Y - 4f) < Eps &&
                                            Math.Abs(v.Position.Z - innerTopZ) < Eps);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - stampLen) < Eps &&
                                            Math.Abs(v.Position.Y + 4f) < Eps &&
                                            Math.Abs(v.Position.Z - innerTopZ) < Eps);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - stampLen) < Eps &&
                                            Math.Abs(v.Position.Y - 4f) < Eps &&
                                            Math.Abs(v.Position.Z - (innerTopZ - profile.AbutmentDepthMeters)) < Eps);

        // The old flat-topped cube put the inner top corner at the END soffit height (z = 9) — gone.
        Assert.DoesNotContain(mesh.Vertices, v => Math.Abs(v.Position.X - stampLen) < Eps &&
                                                  Math.Abs(v.Position.Y - 4f) < Eps &&
                                                  Math.Abs(v.Position.Z - 9f) < Eps);
    }

    [Fact]
    public void Build_Parapets_RiseToParapetHeightAboveDeckEdge()
    {
        var profile = new BridgeDeckProfile();
        var cs = StraightDeck(3, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, profile, "deck", "mat");

        // Level deck: every edge is at z = 10. The parapet tops are the highest geometry now.
        var deckEdgeZ = cs[0].GetRightEdgePosition().Z; // 10
        var expectedTop = deckEdgeZ + profile.ParapetHeightMeters; // 10.9

        var maxZ = mesh.Vertices.Max(v => v.Position.Z);
        Assert.Equal(expectedTop, maxZ, precision: 3);

        // There exist verts exactly at edgeZ + 0.9 (the parapet tops).
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Z - expectedTop) < Eps);
    }

    [Fact]
    public void Build_Parapets_OuterFaceFlushWithDeckEdge()
    {
        var profile = new BridgeDeckProfile();
        var cs = StraightDeck(3, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, profile, "deck", "mat");

        // Right deck-edge XY (lateral offset 0) with the parapet-top Z must exist → outer face is vertical/flush.
        var edge = cs[0].GetRightEdgePosition();
        var topZ = edge.Z + profile.ParapetHeightMeters;

        Assert.Contains(mesh.Vertices, v =>
            Math.Abs(v.Position.X - edge.X) < Eps &&
            Math.Abs(v.Position.Y - edge.Y) < Eps &&
            Math.Abs(v.Position.Z - topZ) < Eps);

        // And the matching left deck-edge outer-top vert is also flush.
        var leftEdge = cs[0].GetLeftEdgePosition();
        var leftTopZ = leftEdge.Z + profile.ParapetHeightMeters;
        Assert.Contains(mesh.Vertices, v =>
            Math.Abs(v.Position.X - leftEdge.X) < Eps &&
            Math.Abs(v.Position.Y - leftEdge.Y) < Eps &&
            Math.Abs(v.Position.Z - leftTopZ) < Eps);
    }

    [Fact]
    public void Build_Parapets_WallThicknessNeverBelowMinimum_EvenForThinProfiles()
    {
        // A profile configured thinner than MinParapetThicknessMeters must be clamped up: topW → 0.4,
        // baseW → max(base, topW) = 0.4. Right edge is at y = +4 (width 8, normal +Y), inward = −Y.
        var profile = new BridgeDeckProfile { ParapetBaseWidthMeters = 0.15f, ParapetTopWidthMeters = 0.1f };
        var cs = StraightDeck(3, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, profile, "deck", "mat");

        const float minW = BridgeDeckProfile.MinParapetThicknessMeters;
        var topZ = 10f + profile.ParapetHeightMeters;

        // Inner wall verts sit exactly minW inward of the flush outer face — at the base and at the top.
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Y - (4f - minW)) < Eps &&
                                            Math.Abs(v.Position.Z - 10f) < Eps);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Y - (4f - minW)) < Eps &&
                                            Math.Abs(v.Position.Z - topZ) < Eps);

        // Nothing at parapet-top height lies strictly between the outer face and the 0.4 m inner face
        // (a thinner wall would put its inner verts in that band). Same for the mirrored left side.
        Assert.DoesNotContain(mesh.Vertices, v => Math.Abs(v.Position.Z - topZ) < Eps &&
                                                  v.Position.Y > 4f - minW + Eps && v.Position.Y < 4f - Eps);
        Assert.DoesNotContain(mesh.Vertices, v => Math.Abs(v.Position.Z - topZ) < Eps &&
                                                  v.Position.Y < -4f + minW - Eps && v.Position.Y > -4f + Eps);
    }

    [Fact]
    public void Build_Parapets_DefaultProfile_TopThicknessIsLiteralTopWidth()
    {
        // ParapetTopWidthMeters is the literal wall thickness at the top (the old code emitted
        // base − top = 0.25 m, violating the 0.4 m minimum). Default: base 0.45, top 0.40.
        var profile = new BridgeDeckProfile();
        Assert.True(profile.ParapetTopWidthMeters >= BridgeDeckProfile.MinParapetThicknessMeters);

        var cs = StraightDeck(3, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, profile, "deck", "mat");

        var topZ = 10f + profile.ParapetHeightMeters;
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Y - (4f - profile.ParapetTopWidthMeters)) < Eps &&
                                            Math.Abs(v.Position.Z - topZ) < Eps);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Y - (4f - profile.ParapetBaseWidthMeters)) < Eps &&
                                            Math.Abs(v.Position.Z - 10f) < Eps);
    }

    [Fact]
    public void Build_Parapets_AreClosedSolids_CappedAtSpanEnds_WithBottomFaces()
    {
        // Each parapet run must be watertight: outer + inner + top + BOTTOM faces and a vertical cap at
        // BOTH span ends (previously the ends were open — a see-through hole along the roadway).
        const float deckTopZ = 10f;
        var cs = StraightDeck(3, 20f, 8f, deckTopZ);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");

        bool IsParapetCap(Triangle t, float x, float outwardX)
        {
            var verts = new[] { mesh.Vertices[t.V0], mesh.Vertices[t.V1], mesh.Vertices[t.V2] };
            // All verts on the end plane, all at/above deck top, reaching the parapet (distinguishes the
            // parapet cap from the box-shell end cap, whose verts extend below the deck top).
            return verts.All(v => Math.Abs(v.Position.X - x) < Eps) &&
                   verts.All(v => v.Position.Z >= deckTopZ - Eps) &&
                   verts.Any(v => v.Position.Z > deckTopZ + Eps) &&
                   FaceNormal(mesh, t).X * outwardX > 0.9f;
        }

        Assert.Contains(mesh.Triangles, t => IsParapetCap(t, 0f, -1f));   // start cap faces −X
        Assert.Contains(mesh.Triangles, t => IsParapetCap(t, 20f, 1f));   // end cap faces +X

        // Down-facing faces at deck-top height = the parapet bottoms (the soffit is 1 m lower).
        Assert.Contains(mesh.Triangles, t =>
            FaceNormal(mesh, t).Z < -0.9f &&
            Math.Abs(mesh.Vertices[t.V0].Position.Z - deckTopZ) < Eps &&
            Math.Abs(mesh.Vertices[t.V1].Position.Z - deckTopZ) < Eps &&
            Math.Abs(mesh.Vertices[t.V2].Position.Z - deckTopZ) < Eps);
    }

    [Fact]
    public void Build_IsClosedBox_HasUpDownLateralAndCapFaces()
    {
        var cs = StraightDeck(3, 20f, 8f, 10f);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");

        // Use geometric face normals (from winding), which must agree with the stored outward normals.
        var normals = mesh.Triangles.Select(t => FaceNormal(mesh, t)).ToList();

        Assert.Contains(normals, nrm => nrm.Z > 0.9f);   // deck top / parapet top faces up
        Assert.Contains(normals, nrm => nrm.Z < -0.9f);  // soffit / stamp bottom faces down
        Assert.Contains(normals, nrm => nrm.Y > 0.9f);   // right fascia +Y
        Assert.Contains(normals, nrm => nrm.Y < -0.9f);  // left fascia −Y
        Assert.Contains(normals, nrm => nrm.X > 0.9f);   // end cap +X
        Assert.Contains(normals, nrm => nrm.X < -0.9f);  // start cap −X
    }

    [Fact]
    public void Build_AllFaceNormals_PointOutward_NotInverted()
    {
        // Regression for the inverted-normal bug: the bridge DAE path applies NO mirror, so faces must be
        // authored outward in plain Z-up. The deck TOP must face UP (+Z) and the SOFFIT DOWN (−Z) — the old
        // winding produced the opposite. Check every box-shell triangle, and that stored vertex normals match
        // the geometric (winding) normal (AddFace writes the exact outward normal, no smoothing).
        const float deckTopZ = 10f, thickness = 1f; // span 20 → thickness 1.0
        var cs = StraightDeck(3, 20f, 8f, deckTopZ);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, BoxOnlyProfile(), "deck", "mat");

        foreach (var t in mesh.Triangles)
        {
            var geo = FaceNormal(mesh, t);

            // Stored vertex normals are the exact outward face normal (flat, no smoothing).
            foreach (var vi in new[] { t.V0, t.V1, t.V2 })
                Assert.True(Vector3.Dot(mesh.Vertices[vi].Normal, geo) > 0.99f,
                    "stored vertex normal must equal the geometric outward normal");

            var c = (mesh.Vertices[t.V0].Position + mesh.Vertices[t.V1].Position + mesh.Vertices[t.V2].Position) / 3f;

            // Deck-top triangles (all verts at z = deckTopZ) MUST face up; soffit triangles MUST face down.
            bool allTop = Math.Abs(mesh.Vertices[t.V0].Position.Z - deckTopZ) < Eps &&
                          Math.Abs(mesh.Vertices[t.V1].Position.Z - deckTopZ) < Eps &&
                          Math.Abs(mesh.Vertices[t.V2].Position.Z - deckTopZ) < Eps;
            bool allSoffit = Math.Abs(mesh.Vertices[t.V0].Position.Z - (deckTopZ - thickness)) < Eps &&
                             Math.Abs(mesh.Vertices[t.V1].Position.Z - (deckTopZ - thickness)) < Eps &&
                             Math.Abs(mesh.Vertices[t.V2].Position.Z - (deckTopZ - thickness)) < Eps;

            if (allTop) Assert.True(geo.Z > 0.9f, $"deck-top triangle must face UP, got {geo}");
            if (allSoffit) Assert.True(geo.Z < -0.9f, $"soffit triangle must face DOWN, got {geo}");

            // Fascia (±Y) triangles must face away from the deck centerline (y=0), not into it.
            if (Math.Abs(geo.Y) > 0.9f && Math.Abs(c.Y) > Eps)
                Assert.True(geo.Y * c.Y > 0f, $"side face at y={c.Y} must face outward, normal {geo}");
        }
    }

    // ── Doc 15 (b)/(c): deck trims at deck-to-deck merges ───────────────────────────────────────────

    [Fact]
    public void Build_NullOrEmptyTrim_IsByteIdenticalToLegacy()
    {
        var cs = StraightDeck(4, 30f, 8f, 10f);
        var legacy = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");
        var withNull = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat", trim: null);
        var withEmpty = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat", new BridgeDeckTrim());

        Assert.Equal(legacy.Vertices.Count, withNull.Vertices.Count);
        Assert.Equal(legacy.Triangles.Count, withNull.Triangles.Count);
        Assert.Equal(legacy.Vertices.Count, withEmpty.Vertices.Count);
        Assert.Equal(legacy.Triangles.Count, withEmpty.Triangles.Count);
    }

    [Fact]
    public void Build_Trim_SuppressesEndStampsPerEnd()
    {
        // Each end stamp here is one clipped segment = 6 faces = 24 verts / 12 tris (station spacing
        // 10 m > stamp length 3 m); the end CAP of the box shell stays (it closes the mesh flush under
        // the trunk deck).
        var cs = StraightDeck(4, 30f, 8f, 10f);
        var full = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");
        var noEnd = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat",
            new BridgeDeckTrim { SuppressEndStamp = true });
        var noBoth = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat",
            new BridgeDeckTrim { SuppressStartStamp = true, SuppressEndStamp = true });

        Assert.Equal(full.Vertices.Count - 24, noEnd.Vertices.Count);
        Assert.Equal(full.Triangles.Count - 12, noEnd.Triangles.Count);
        Assert.Equal(full.Vertices.Count - 48, noBoth.Vertices.Count);
        Assert.Equal(full.Triangles.Count - 24, noBoth.Triangles.Count);
    }

    [Fact]
    public void Build_Trim_ParapetMask_OpensRunAndCapsTheInteriorEnd()
    {
        // 5 stations, left mask suppresses stations 3+4 (a merge end): parapet segments 2 and 3 drop
        // (a segment needs BOTH stations unsuppressed), segments 0–1 survive as one run closed by the
        // span-start cap at station 0 and the interior cap at station 2. Full left parapet =
        // 4 faces × 4 segments + 2 end caps = 18 faces; masked = 2 × 4 + 2 caps = 10 faces
        // ⇒ −8 faces = −32 verts / −16 tris. Right side untouched.
        var cs = StraightDeck(5, 40f, 8f, 10f);
        var full = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");
        var masked = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat",
            new BridgeDeckTrim { LeftParapetSuppressed = [false, false, false, true, true] });

        Assert.Equal(full.Vertices.Count - 32, masked.Vertices.Count);
        Assert.Equal(full.Triangles.Count - 16, masked.Triangles.Count);

        // The cap closes the run at station 2 (x = 20): a vertex at the parapet top over the left edge.
        var st2Left = cs[2].GetLeftEdgePosition();
        var topZ = st2Left.Z + new BridgeDeckProfile().ParapetHeightMeters;
        Assert.Contains(masked.Vertices, v =>
            Math.Abs(v.Position.X - st2Left.X) < Eps &&
            Math.Abs(v.Position.Y - st2Left.Y) < Eps &&
            Math.Abs(v.Position.Z - topZ) < Eps);
    }

    [Fact]
    public void Build_Trim_FullySuppressedParapet_EmitsNothingForThatEdge()
    {
        // Left parapet fully suppressed = −18 faces (4 × 4 segments + 2 end caps) = −72 verts / −36 tris.
        var cs = StraightDeck(5, 40f, 8f, 10f);
        var full = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");
        var masked = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat",
            new BridgeDeckTrim { LeftParapetSuppressed = [true, true, true, true, true] });

        Assert.Equal(full.Vertices.Count - 72, masked.Vertices.Count);
        Assert.Equal(full.Triangles.Count - 36, masked.Triangles.Count);
    }

    [Fact]
    public void Build_BankedSection_SoffitTracksBankedEdges()
    {
        // Positive bank raises the right edge, lowers the left. The soffit (top − thickness in world-Z)
        // must inherit that tilt: left-bottom Z < right-bottom Z.
        const float bank = 0.1f; // radians
        var cs = StraightDeck(2, 20f, 8f, 10f, bankRadians: bank);
        var mesh = new BridgeDeckMeshBuilder().Build(cs, new BridgeDeckProfile(), "deck", "mat");

        var section = cs[0];
        var topL = section.GetLeftEdgePosition();
        var topR = section.GetRightEdgePosition();
        Assert.True(topR.Z > topL.Z + Eps, "banked deck top should have right edge above left edge");

        var thickness = BridgeDeckProfile.ComputeDeckThicknessMeters(20f, new BridgeDeckProfile());
        var expectedBotL = topL.Z - thickness;
        var expectedBotR = topR.Z - thickness;

        // The two distinct lowest Z levels (left vs right soffit corner) differ by the same banked amount.
        Assert.True(expectedBotR > expectedBotL + Eps);

        // Both banked soffit corners are present in the mesh.
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Z - expectedBotL) < Eps);
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.Z - expectedBotR) < Eps);
        Assert.NotEqual(expectedBotL, expectedBotR);
    }
}
