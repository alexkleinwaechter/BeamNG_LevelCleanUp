using System.Numerics;
using BeamNG.Procedural3D.RoadMesh;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tunnel plan Phase 3a: the swept tube mesh. Straight tube along +X, floor Z 50, road width 8,
///     side clearance 1 (interior half-width 5), wall 0.6, interior height 5, arch segments 8.
///     Quad counts: per sweep segment 2 floor + (A+2) interior + (A+2) outer = 2A+6; each portal
///     COLLAR is a solid — front/back annulus + rim (3·(A+2)) + front/back bottom strips + underside
///     slab (3) = 3A+9 per end. AddFace emits 4 verts / 2 tris per quad.
/// </summary>
public class TunnelMeshBuilderTests
{
    private const int Arch = 8;

    private static TunnelMeshProfile Profile(float tongue = 0f) => new()
    {
        InteriorHeightMeters = 5f,
        WallThicknessMeters = 0.6f,
        SideClearanceMeters = 1f,
        ArchSegments = Arch,
        HeadwallFlareMeters = 1f,
        HeadwallDepthMeters = 1f, // fixture tube is only 10 m long — production default (10) would overlap
        FloorTongueMeters = tongue
    };

    private static List<RoadCrossSection> StraightTube(int stations, float spacing = 5f)
    {
        var list = new List<RoadCrossSection>();
        for (var i = 0; i < stations; i++)
        {
            list.Add(new RoadCrossSection
            {
                CenterPoint = new Vector2(i * spacing, 100f),
                CenterElevation = 50f,
                TangentDirection = new Vector2(1f, 0f),
                NormalDirection = new Vector2(0f, -1f),
                WidthMeters = 8f,
                DistanceAlongRoad = i * spacing,
                LeftEdgeElevation = 50f,
                RightEdgeElevation = 50f
            });
        }

        return list;
    }

    [Fact]
    public void FewerThanTwoSections_EmptyMesh()
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(1), Profile(), "t", "m");
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Triangles);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void VertexAndTriangleCounts_MatchFormula(int stations)
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(stations), Profile(), "t", "m");

        var quads = (stations - 1) * (2 * Arch + 6) + 2 * (3 * Arch + 9);
        Assert.Equal(quads * 4, mesh.Vertices.Count);
        Assert.Equal(quads * 2, mesh.Triangles.Count);
    }

    [Fact]
    public void DrivableFloor_TopFaceUp_AtFloorZ_InteriorWidth()
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(3), Profile(), "t", "m");

        // Floor-top vertices: z == 50, normal +Z, |y - 100| <= interior half (5).
        var floorTop = mesh.Vertices
            .Where(v => MathF.Abs(v.Position.Z - 50f) < 1e-3f && v.Normal.Z > 0.99f)
            .ToList();
        Assert.NotEmpty(floorTop);
        Assert.All(floorTop, v => Assert.True(MathF.Abs(v.Position.Y - 100f) <= 5f + 1e-3f));
        // Spans the full interior width.
        Assert.Contains(floorTop, v => v.Position.Y > 104.9f);
        Assert.Contains(floorTop, v => v.Position.Y < 95.1f);
    }

    [Fact]
    public void InteriorWallFaces_PointIntoTheBore()
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(3), Profile(), "t", "m");

        // Left interior wall sits at y = 105 (center 100 + interior half 5, left = −Normal = +Y).
        // Its faces must point INTO the bore (−Y). Wall quads have corners at z=50 (floor) and
        // z=53 (wall top); exclude headwall faces (|Normal.X| ≈ 1) and floor/ceiling (|Normal.Z| high).
        var leftWall = mesh.Vertices
            .Where(v => MathF.Abs(v.Position.Y - 105f) < 1e-3f &&
                        v.Position.Z is >= 49.9f and <= 53.1f &&
                        MathF.Abs(v.Normal.Z) < 0.7f && MathF.Abs(v.Normal.X) < 0.5f)
            .ToList();
        Assert.NotEmpty(leftWall);
        Assert.All(leftWall, v => Assert.True(v.Normal.Y < -0.3f,
            $"left interior wall normal must point into the bore: {v.Normal}"));

        // Outer shell left wall at y = 105.6 points AWAY (+Y).
        var outerLeft = mesh.Vertices
            .Where(v => MathF.Abs(v.Position.Y - 105.6f) < 1e-3f &&
                        v.Position.Z is >= 49.3f and <= 53.1f &&
                        MathF.Abs(v.Normal.Z) < 0.7f && MathF.Abs(v.Normal.X) < 0.5f)
            .ToList();
        Assert.NotEmpty(outerLeft);
        Assert.All(outerLeft, v => Assert.True(v.Normal.Y > 0.3f,
            $"outer shell normal must point away from the bore: {v.Normal}"));
    }

    [Fact]
    public void CeilingApex_AtInteriorHeight()
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(3), Profile(), "t", "m");

        // Interior arch apex = floor + 5 = 55; outer apex = 55 + wall 0.6 = 55.6; the extruded
        // headwall rises by the FULL flare (1 m here). Nothing above 56.7.
        var maxZ = mesh.Vertices.Max(v => v.Position.Z);
        Assert.True(maxZ >= 55.5f && maxZ <= 56.7f, $"maxZ={maxZ}");

        // Interior ceiling faces near the apex point DOWN into the bore.
        var apexFaces = mesh.Vertices
            .Where(v => MathF.Abs(v.Position.Z - 55f) < 0.05f && v.Normal.Z < -0.5f)
            .ToList();
        Assert.NotEmpty(apexFaces);
    }

    [Fact]
    public void Headwalls_PresentAtBothEnds_FacingOutward_Flared()
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(3), Profile(), "t", "m");

        // Start headwall at x=0 faces −X; end headwall at x=10 faces +X.
        var start = mesh.Vertices.Where(v => MathF.Abs(v.Position.X) < 1e-3f && v.Normal.X < -0.99f).ToList();
        var end = mesh.Vertices.Where(v => MathF.Abs(v.Position.X - 10f) < 1e-3f && v.Normal.X > 0.99f).ToList();
        Assert.NotEmpty(start);
        Assert.NotEmpty(end);

        // Flare: the headwall reaches laterally beyond the outer shell (5.6) by the 1 m flare,
        // rises above the outer apex (55.6) by the full flare, and drops a buried skirt below
        // the slab bottom (49.4) at the floor corners.
        Assert.Contains(start, v => MathF.Abs(v.Position.Y - 100f) > 6.4f);
        Assert.Contains(start, v => v.Position.Z > 56.4f);
        Assert.Contains(start, v => v.Position.Z < 49.2f);
    }

    [Fact]
    public void Headwall_IsSolidCollar_BackAndRimFacesOutward()
    {
        // Depth 1 m with 5 m station spacing snaps the swept collar to the first station: x ∈ [0, 5].
        var mesh = new TunnelMeshBuilder().Build(StraightTube(3), Profile(), "t", "m");

        var collar = mesh.Vertices
            .Where(v => v.Position.X is >= -0.01f and <= 5.01f &&
                        MathF.Abs(v.Position.Y - 100f) > 5.61f) // beyond the outer shell = collar only
            .ToList();
        Assert.NotEmpty(collar);

        // Back annulus at the collar's inner end faces INTO the tunnel (+X for the start portal).
        Assert.Contains(mesh.Vertices, v =>
            MathF.Abs(v.Position.X - 5f) < 1e-3f && v.Normal.X > 0.99f);

        // Rim band: visible thickness facing sideways (walls) and upward (crown) — never along the bore.
        Assert.Contains(collar, v => v.Normal.Y > 0.9f && MathF.Abs(v.Normal.X) < 0.1f);
        Assert.Contains(mesh.Vertices, v =>
            v.Position.X is >= -0.01f and <= 5.01f && v.Position.Z > 56.4f && v.Normal.Z > 0.9f);

        // Underside slab faces down (buried).
        Assert.Contains(collar, v => v.Normal.Z < -0.99f && v.Position.Z < 49.2f);
    }

    /// <summary>
    ///     User render 2026-07-18 (in-bore screenshot): the straight-prism collar sliced into the
    ///     drivable space on curved tunnels. The swept collar must keep every face OUTSIDE the
    ///     interior ring at every station — no vertex of the collar may lie strictly inside the
    ///     bore cross-section of any station.
    /// </summary>
    [Fact]
    public void Collar_OnCurvedTunnel_NeverIntrudesIntoTheBore()
    {
        // 90° curve, radius 40 m, stations every ~3.5 m, deep collar (10 m) with a big flare (5 m).
        var sections = new List<RoadCrossSection>();
        const int n = 19;
        for (var i = 0; i < n; i++)
        {
            var a = MathF.PI / 2f * i / (n - 1);
            sections.Add(new RoadCrossSection
            {
                CenterPoint = new Vector2(40f * MathF.Sin(a), 100f - 40f * (1f - MathF.Cos(a))),
                CenterElevation = 50f + i * 0.15f, // graded too
                TangentDirection = new Vector2(MathF.Cos(a), -MathF.Sin(a)),
                NormalDirection = new Vector2(-MathF.Sin(a), -MathF.Cos(a)),
                WidthMeters = 8f,
                DistanceAlongRoad = i * (40f * MathF.PI / 2f / (n - 1)),
                LeftEdgeElevation = 50f + i * 0.15f,
                RightEdgeElevation = 50f + i * 0.15f
            });
        }

        var profile = new TunnelMeshProfile
        {
            InteriorHeightMeters = 5f,
            WallThicknessMeters = 0.6f,
            SideClearanceMeters = 1f,
            ArchSegments = Arch,
            HeadwallFlareMeters = 5f,
            HeadwallDepthMeters = 10f,
            FloorTongueMeters = 0f
        };
        var mesh = new TunnelMeshBuilder().Build(sections, profile, "t", "m");

        // For every mesh vertex, find the nearest station and verify it is NOT strictly inside that
        // station's bore (|lateral| < interiorHalf − margin AND floor+margin < z < wallTop − margin).
        const float interiorHalf = 5f;   // 8/2 + 1 clearance
        const float margin = 0.15f;
        foreach (var v in mesh.Vertices)
        {
            RoadCrossSection? nearest = null;
            var bestD = float.MaxValue;
            foreach (var s in sections)
            {
                var d = Vector2.DistanceSquared(new Vector2(v.Position.X, v.Position.Y), s.CenterPoint);
                if (d < bestD)
                {
                    bestD = d;
                    nearest = s;
                }
            }

            var lateral = Vector2.Dot(
                new Vector2(v.Position.X, v.Position.Y) - nearest!.CenterPoint, nearest.NormalDirection);
            var inPlan = MathF.Abs(lateral) < interiorHalf - margin;
            var inHeight = v.Position.Z > nearest.CenterElevation + margin &&
                           v.Position.Z < nearest.CenterElevation + 3f - margin; // below the wall top band
            Assert.False(inPlan && inHeight,
                $"collar/shell vertex intrudes into the bore at ({v.Position.X:F1},{v.Position.Y:F1},{v.Position.Z:F1})");
        }
    }

    [Fact]
    public void FloorTongue_ExtendsOutOfBothPortals_SubFlush()
    {
        var mesh = new TunnelMeshBuilder().Build(StraightTube(3), Profile(tongue: 6f), "t", "m");

        // Start portal at x=0 (tangent +X ⇒ tongue extends toward −X), end portal at x=10 (+X).
        var startTongue = mesh.Vertices.Where(v => v.Position.X < -0.5f).ToList();
        var endTongue = mesh.Vertices.Where(v => v.Position.X > 10.5f).ToList();
        Assert.NotEmpty(startTongue);
        Assert.NotEmpty(endTongue);

        // Reaches the full 6 m, spans the interior width (±5), top rides 2 cm below the floor.
        Assert.Contains(startTongue, v => v.Position.X < -5.9f);
        Assert.Contains(endTongue, v => v.Position.X > 15.9f);
        Assert.All(startTongue, v => Assert.True(MathF.Abs(v.Position.Y - 100f) <= 5f + 1e-3f));
        var top = startTongue.Max(v => v.Position.Z);
        Assert.Equal(50f - 0.02f, top, 0.005f);
        // Slab thickness = wall thickness.
        var bottom = startTongue.Min(v => v.Position.Z);
        Assert.Equal(top - 0.6f, bottom, 0.005f);

        // Tongue off ⇒ nothing beyond the span ends.
        var noTongue = new TunnelMeshBuilder().Build(StraightTube(3), Profile(), "t", "m");
        Assert.DoesNotContain(noTongue.Vertices, v => v.Position.X < -1.5f);
    }

    /// <summary>
    ///     Banking follow-up (doc 03): the banked ring is the flat ring sheared vertically between
    ///     the station's edge Zs. Edge Zs ±0.4 on width 8 ⇒ cross-slope 0.1. Floor-top corners at
    ///     the interior half-width (±5) must sit on the extrapolated edge line (50 ± 0.5), the arch
    ///     apex stays over the center at centerZ + interiorHeight, walls stay plumb (wall-face
    ///     geometric normals gain no Z component), and the quad-count formula is unchanged.
    /// </summary>
    private static List<RoadCrossSection> BankedTube(int stations, float spacing = 5f)
    {
        var list = StraightTube(stations, spacing);
        foreach (var s in list)
        {
            s.LeftEdgeElevation = 50f - 0.4f;   // left = −normal side (y = 105)
            s.RightEdgeElevation = 50f + 0.4f;  // right = +normal side (y = 95)
        }

        return list;
    }

    [Fact]
    public void BankedTube_FloorSheared_CornersOnEdgeLine_QuadCountUnchanged()
    {
        var mesh = new TunnelMeshBuilder().Build(BankedTube(3), Profile(), "t", "m");

        // Same quad count as the flat tube — the shear moves vertices, never adds faces.
        var quads = 2 * (2 * Arch + 6) + 2 * (3 * Arch + 9);
        Assert.Equal(quads * 4, mesh.Vertices.Count);

        // Floor-top corners (normal +Z): right corner y=95 (u=+5) on 50 + 5·0.1 = 50.5,
        // left corner y=105 (u=−5) on 49.5 — the extrapolated banked edge line.
        var floorTop = mesh.Vertices.Where(v => v.Normal.Z > 0.9f && v.Position.Z is > 49f and < 51f).ToList();
        Assert.NotEmpty(floorTop);
        foreach (var v in floorTop)
        {
            var u = 100f - v.Position.Y; // normal (0,−1): u = +5 at y=95
            Assert.Equal(50f + u * 0.1f, v.Position.Z, 0.01f);
        }

        Assert.Contains(floorTop, v => MathF.Abs(v.Position.Y - 95f) < 1e-3f && MathF.Abs(v.Position.Z - 50.5f) < 0.01f);
        Assert.Contains(floorTop, v => MathF.Abs(v.Position.Y - 105f) < 1e-3f && MathF.Abs(v.Position.Z - 49.5f) < 0.01f);
    }

    [Fact]
    public void BankedTube_ApexOverCenter_WallsPlumb()
    {
        var mesh = new TunnelMeshBuilder().Build(BankedTube(3), Profile(), "t", "m");

        // Interior arch apex: over the center (y=100) at centerZ + interiorHeight = 55 (mid-slope).
        var apex = mesh.Vertices
            .Where(v => MathF.Abs(v.Position.Z - 55f) < 0.05f && v.Normal.Z < -0.5f)
            .ToList();
        Assert.NotEmpty(apex);
        Assert.Contains(apex, v => MathF.Abs(v.Position.Y - 100f) < 0.05f);

        // Walls plumb: interior wall faces (sideways normals, wall band above their sheared floor
        // corner) keep world-vertical planes — geometric normal Z stays 0 despite the bank. Band
        // stops below the wall top (52.5): the first arch segment shares y=105 vertices there and
        // is legitimately tilted.
        var leftWall = mesh.Vertices
            .Where(v => MathF.Abs(v.Position.Y - 105f) < 1e-3f &&
                        v.Position.Z is >= 49.4f and <= 52.4f &&
                        MathF.Abs(v.Normal.Z) < 0.7f && MathF.Abs(v.Normal.X) < 0.5f)
            .ToList();
        Assert.NotEmpty(leftWall);
        Assert.All(leftWall, v => Assert.True(MathF.Abs(v.Normal.Z) < 0.01f,
            $"banked wall face must stay plumb: normal {v.Normal}"));
    }

    [Fact]
    public void BankedTube_FloorTongue_TopPlaneTilted()
    {
        var mesh = new TunnelMeshBuilder().Build(BankedTube(3), Profile(tongue: 6f), "t", "m");

        // Start tongue (x < 0): top plane tilts between the end station's edge line — high side
        // (y=95) at 50.5 − 0.02, low side (y=105) at 49.5 − 0.02.
        var tongueTop = mesh.Vertices
            .Where(v => v.Position.X < -0.5f && v.Normal.Z > 0.9f)
            .ToList();
        Assert.NotEmpty(tongueTop);
        var high = tongueTop.Where(v => MathF.Abs(v.Position.Y - 95f) < 1e-3f).Max(v => v.Position.Z);
        var low = tongueTop.Where(v => MathF.Abs(v.Position.Y - 105f) < 1e-3f).Max(v => v.Position.Z);
        Assert.Equal(50.5f - 0.02f, high, 0.01f);
        Assert.Equal(49.5f - 0.02f, low, 0.01f);
    }

    /// <summary>
    ///     Doc 03: the collar intrusion guarantee must hold under shear too — banked curved tunnel,
    ///     no collar/shell vertex strictly inside the (sheared) bore of its nearest station.
    /// </summary>
    [Fact]
    public void Collar_OnCurvedBankedTunnel_NeverIntrudesIntoTheBore()
    {
        // Same 90° curve / grade as the flat collar test, plus a 0.1 cross-slope (edge Zs ±0.4).
        var sections = new List<RoadCrossSection>();
        const int n = 19;
        for (var i = 0; i < n; i++)
        {
            var a = MathF.PI / 2f * i / (n - 1);
            var centerZ = 50f + i * 0.15f;
            sections.Add(new RoadCrossSection
            {
                CenterPoint = new Vector2(40f * MathF.Sin(a), 100f - 40f * (1f - MathF.Cos(a))),
                CenterElevation = centerZ,
                TangentDirection = new Vector2(MathF.Cos(a), -MathF.Sin(a)),
                NormalDirection = new Vector2(-MathF.Sin(a), -MathF.Cos(a)),
                WidthMeters = 8f,
                DistanceAlongRoad = i * (40f * MathF.PI / 2f / (n - 1)),
                LeftEdgeElevation = centerZ - 0.4f,
                RightEdgeElevation = centerZ + 0.4f
            });
        }

        var profile = new TunnelMeshProfile
        {
            InteriorHeightMeters = 5f,
            WallThicknessMeters = 0.6f,
            SideClearanceMeters = 1f,
            ArchSegments = Arch,
            HeadwallFlareMeters = 5f,
            HeadwallDepthMeters = 10f,
            FloorTongueMeters = 0f
        };
        var mesh = new TunnelMeshBuilder().Build(sections, profile, "t", "m");

        // Bore test against the SHEARED floor line of the nearest station.
        const float interiorHalf = 5f;
        const float margin = 0.15f;
        foreach (var v in mesh.Vertices)
        {
            RoadCrossSection? nearest = null;
            var bestD = float.MaxValue;
            foreach (var s in sections)
            {
                var d = Vector2.DistanceSquared(new Vector2(v.Position.X, v.Position.Y), s.CenterPoint);
                if (d < bestD)
                {
                    bestD = d;
                    nearest = s;
                }
            }

            var lateral = Vector2.Dot(
                new Vector2(v.Position.X, v.Position.Y) - nearest!.CenterPoint, nearest.NormalDirection);
            var floorAt = nearest.CenterElevation + lateral * 0.1f;
            var inPlan = MathF.Abs(lateral) < interiorHalf - margin;
            var inHeight = v.Position.Z > floorAt + margin &&
                           v.Position.Z < floorAt + 3f - margin; // below the wall-top band
            Assert.False(inPlan && inHeight,
                $"collar/shell vertex intrudes into the banked bore at ({v.Position.X:F1},{v.Position.Y:F1},{v.Position.Z:F1})");
        }
    }

    [Fact]
    public void AntiFold_TightCurve_NoBacktrackingOnInsideEdge()
    {
        // 90° elbow with stations 1 m apart on a 2 m radius — inside edge would backtrack unclamped.
        var sections = new List<RoadCrossSection>();
        const int n = 10;
        for (var i = 0; i < n; i++)
        {
            var a = MathF.PI / 2f * i / (n - 1);
            var center = new Vector2(20f * MathF.Sin(a), 100f - 20f * (1f - MathF.Cos(a)));
            var tangent = new Vector2(MathF.Cos(a), -MathF.Sin(a));
            var normal = new Vector2(-MathF.Sin(a), -MathF.Cos(a));
            sections.Add(new RoadCrossSection
            {
                CenterPoint = center,
                CenterElevation = 50f,
                TangentDirection = tangent,
                NormalDirection = normal,
                WidthMeters = 30f, // half 16 > radius 20·(step) — forces inside-edge backtracking
                DistanceAlongRoad = i * (20f * MathF.PI / 2f / (n - 1)),
                LeftEdgeElevation = 50f,
                RightEdgeElevation = 50f
            });
        }

        // Must not throw and must produce a mesh; the anti-fold clamp is structural (no NaNs).
        var mesh = new TunnelMeshBuilder().Build(sections, Profile(), "t", "m");
        Assert.NotEmpty(mesh.Triangles);
        Assert.All(mesh.Vertices, v =>
        {
            Assert.True(float.IsFinite(v.Position.X));
            Assert.True(float.IsFinite(v.Position.Y));
            Assert.True(float.IsFinite(v.Position.Z));
        });
    }
}
