namespace BeamNG.Procedural3D.RoadMesh;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Profile of the swept tunnel tube (tunnel plan Phase 3a, ai_docs/2026-07-18_tunnel_generation/01).
/// The interior is a drivable floor + vertical side walls + a segmented semi-ellipse ceiling arch whose
/// apex sits <see cref="InteriorHeightMeters"/> above the floor; the outer shell is the same profile
/// offset outward by <see cref="WallThicknessMeters"/>. Interior width per station =
/// station width + 2 × <see cref="SideClearanceMeters"/>.
/// </summary>
public sealed class TunnelMeshProfile
{
    /// <summary>Floor → ceiling apex (m).</summary>
    public float InteriorHeightMeters { get; set; } = 5.0f;

    /// <summary>Interior surface → exterior shell (m); also the floor slab thickness.</summary>
    public float WallThicknessMeters { get; set; } = 0.6f;

    /// <summary>Roadway edge → interior wall, per side (m).</summary>
    public float SideClearanceMeters { get; set; } = 1.0f;

    /// <summary>Segments of the ceiling arch semi-ellipse.</summary>
    public int ArchSegments { get; set; } = 8;

    /// <summary>Portal headwall flare beyond the outer shell (m), applied left/right AND up (floor
    /// corners extend down by half of it) — masks the terrain hole's jagged 1 m-raster edge.</summary>
    public float HeadwallFlareMeters { get; set; } = 5.0f;

    /// <summary>Portal headwall thickness (m), extruded INTO the mountain from the portal plane —
    /// the collar is a massive solid with outward faces on every side, not a one-sided plate
    /// (a flat plate is invisible from behind/above through the hole edges).</summary>
    public float HeadwallDepthMeters { get; set; } = 10.0f;

    /// <summary>Drivable floor slab (m) extended OUT of each portal toward the ground road,
    /// <see cref="TongueSubFlushMeters"/> below the road surface (safety net over holed cells). 0 = off.</summary>
    public float FloorTongueMeters { get; set; } = 6.0f;

    /// <summary>How far the floor tongue rides below the road surface (m) — the stamped apron terrain
    /// stays the visible/drivable surface where it exists; the tongue catches wheels over hole cells.</summary>
    public float TongueSubFlushMeters { get; set; } = 0.02f;

    /// <summary>Fraction of the interior height taken by the vertical side walls (rest is the arch rise).</summary>
    public float WallHeightFraction { get; set; } = 0.6f;
}

/// <summary>
/// Builds a closed, solid swept tunnel tube from a span's ordered cross-sections (tunnel plan Phase 3a):
/// drivable floor slab, interior side walls + segmented semi-ellipse ceiling (faces pointing INTO the
/// bore — toward the driver), an outer shell arch offset by the wall thickness (faces outward), and a
/// portal HEADWALL ring at both ends (interior ring → flared outer ring, masking the terrain hole edge).
/// </summary>
/// <remarks>
/// Authored with the <see cref="BridgeDeckMeshBuilder.AddFace"/> outward-flat-normal discipline: tunnel
/// DAEs go through the direct BeamNG-DAE XML path (no Assimp mirror), so every face's geometric normal
/// must already point the right way in plain Z-up — for interior surfaces "outward" is INTO the bore.
/// Solids overlap rather than share coplanar faces (the pier-mesh lesson): the headwall plate covers the
/// shell's open end annulus. Anti-fold: every ring-vertex path is clamped against plan-view backtracking
/// (the <see cref="BridgeDeckMeshBuilder.BuildAntiFoldEdges"/> miter-limit rule, generalized to all
/// profile paths) so tight curves pivot cleanly instead of pleating.
/// </remarks>
public sealed class TunnelMeshBuilder
{
    /// <summary>
    /// Builds the solid tube mesh for the given ordered cross-sections (world coords; center elevation =
    /// tunnel FLOOR). Returns an empty mesh for fewer than 2 sections.
    /// </summary>
    public Mesh Build(
        IReadOnlyList<RoadCrossSection> crossSections,
        TunnelMeshProfile profile,
        string meshName,
        string? materialName)
    {
        ArgumentNullException.ThrowIfNull(crossSections);
        ArgumentNullException.ThrowIfNull(profile);

        int n = crossSections.Count;
        if (n < 2)
            return new Mesh { Name = meshName, MaterialName = materialName };

        var builder = new MeshBuilder()
            .WithName(meshName)
            .WithMaterial(materialName);

        // Ring templates (lateral u, height v above floor). Interior and outer rings have identical
        // point counts so sweep quads and headwall quads pair 1:1.
        int ringCount = profile.ArchSegments + 3; // floorL, wallTopL, A-1 arch pts, wallTopR, floorR

        // Per-station world-space rings.
        var interior = new Vector3[n][];
        var outer = new Vector3[n][];
        for (int i = 0; i < n; i++)
        {
            var s = crossSections[i];
            var halfInterior = s.WidthMeters / 2f + profile.SideClearanceMeters;
            interior[i] = BuildRing(s, halfInterior, 0f, profile, ringCount);
            outer[i] = BuildRing(s, halfInterior + profile.WallThicknessMeters,
                profile.WallThicknessMeters, profile, ringCount);
        }

        AntiFoldRings(crossSections, interior);
        AntiFoldRings(crossSections, outer);

        // Sweep faces between consecutive stations.
        for (int i = 0; i < n - 1; i++)
        {
            var s = crossSections[i];
            var boreCenter = BoreCenter(s, profile);

            // Floor: drivable top (interior floor corners), outer bottom (outer floor corners).
            AddQuad(builder, interior[i][0], interior[i + 1][0],
                interior[i + 1][ringCount - 1], interior[i][ringCount - 1], Vector3.UnitZ);
            AddQuad(builder, outer[i][0], outer[i + 1][0],
                outer[i + 1][ringCount - 1], outer[i][ringCount - 1], -Vector3.UnitZ);

            // Interior surface (walls + arch): faces point INTO the bore.
            for (int j = 0; j < ringCount - 1; j++)
            {
                var mid = (interior[i][j] + interior[i][j + 1]) * 0.5f;
                var inward = boreCenter - mid;
                inward.Z *= 0.5f; // bias toward horizontal so wall faces read sideways, arch faces down-ish
                AddQuad(builder, interior[i][j], interior[i][j + 1],
                    interior[i + 1][j + 1], interior[i + 1][j], inward);
            }

            // Outer shell: faces point away from the bore.
            for (int j = 0; j < ringCount - 1; j++)
            {
                var mid = (outer[i][j] + outer[i][j + 1]) * 0.5f;
                var outward = mid - boreCenter;
                AddQuad(builder, outer[i][j], outer[i][j + 1],
                    outer[i + 1][j + 1], outer[i + 1][j], outward);
            }
        }

        // Portal collars: massive solids swept along the REAL span stations for the first
        // HeadwallDepthMeters — a straight prism along the end tangent intruded into the bore on
        // curved/graded tunnels (user render 2026-07-18: collar wall across the roadway inside the
        // tube). Swept, the collar's inner boundary at every station is that station's interior
        // ring — it can never cut into the drivable space.
        BuildPortalCollar(builder, crossSections, interior, outer, profile, ringCount, atStart: true);
        BuildPortalCollar(builder, crossSections, interior, outer, profile, ringCount, atStart: false);

        // Portal floor tongues: the drivable slab continues OUT of the bore toward the ground road,
        // slightly sub-flush — the safety surface over the ragged 1 m hole edge at the mouth.
        if (profile.FloorTongueMeters > 0f)
        {
            BuildFloorTongue(builder, crossSections, profile, atStart: true);
            BuildFloorTongue(builder, crossSections, profile, atStart: false);
        }

        return builder.Build();
    }

    /// <summary>
    /// A watertight floor slab extending from one span end OUTWARD along the end tangent for
    /// <see cref="TunnelMeshProfile.FloorTongueMeters"/>, following the end grade, top at road Z −
    /// <see cref="TunnelMeshProfile.TongueSubFlushMeters"/> and slab thickness = wall thickness.
    /// Where the apron terrain survives the hole cutter it stays the (2 cm higher) driving surface;
    /// over holed cells the tongue catches the wheels. Overlaps the tube floor at the portal plane
    /// (accepted overlapping-solids pattern).
    /// </summary>
    private static void BuildFloorTongue(
        MeshBuilder b, IReadOnlyList<RoadCrossSection> cs, TunnelMeshProfile profile, bool atStart)
    {
        int n = cs.Count;
        var end = atStart ? cs[0] : cs[n - 1];
        var next = atStart ? cs[1] : cs[n - 2];

        var tangent = end.TangentDirection;
        var tLen = tangent.Length();
        if (tLen < 1e-4f)
            return;
        tangent /= tLen;
        var dir = atStart ? -tangent : tangent;

        var ds = MathF.Max(MathF.Abs(end.DistanceAlongRoad - next.DistanceAlongRoad), 0.25f);
        var grade = (end.CenterElevation - next.CenterElevation) / ds; // per meter moving OUT of the span

        var half = end.WidthMeters / 2f + profile.SideClearanceMeters;
        var normal = end.NormalDirection;
        var len = profile.FloorTongueMeters;
        var slope = CrossSlope(end); // banked end station ⇒ the tongue top tilts with the floor (doc 03)

        Vector3 At(float along, float lateral, float dz) => new(
            end.CenterPoint.X + dir.X * along + normal.X * lateral,
            end.CenterPoint.Y + dir.Y * along + normal.Y * lateral,
            end.CenterElevation + grade * along + lateral * slope - profile.TongueSubFlushMeters + dz);

        var drop = profile.WallThicknessMeters;
        Vector3 nearL = At(0f, -half, 0f), nearR = At(0f, half, 0f);
        Vector3 farL = At(len, -half, 0f), farR = At(len, half, 0f);
        var down = new Vector3(0f, 0f, drop);
        var outward = new Vector3(dir.X, dir.Y, 0f);

        AddQuad(b, nearL, nearR, farR, farL, Vector3.UnitZ);                         // drivable top
        AddQuad(b, nearL - down, nearR - down, farR - down, farL - down, -Vector3.UnitZ); // bottom
        AddQuad(b, nearL, farL, farL - down, nearL - down,
            new Vector3(-normal.X, -normal.Y, 0f));                                  // left side
        AddQuad(b, nearR, farR, farR - down, nearR - down,
            new Vector3(normal.X, normal.Y, 0f));                                    // right side
        AddQuad(b, farL, farR, farR - down, farL - down, outward);                   // far end cap
        AddQuad(b, nearL, nearR, nearR - down, nearL - down, -outward);              // near cap (buried)
    }

    /// <summary>
    /// One profile ring in world space at a station: floor corners, vertical walls, semi-ellipse arch.
    /// <paramref name="floorDrop"/> lowers the floor edge (outer ring: slab bottom).
    /// Banking (doc 03) is a vertical SHEAR of the flat ring — the floor line tilts between the
    /// station's banked edge Zs while walls stay plumb and the apex stays over the center at
    /// centerZ + interiorHeight (real bores don't roll; the pavement tilts inside the lining).
    /// Flat stations (equal edge Zs) shear by 0 — byte-identical to the unbanked ring.
    /// </summary>
    private static Vector3[] BuildRing(
        RoadCrossSection s, float halfWidth, float floorDrop, TunnelMeshProfile profile, int ringCount)
    {
        int archSegments = profile.ArchSegments;
        float wallHeight = profile.InteriorHeightMeters * profile.WallHeightFraction;
        float rise = profile.InteriorHeightMeters - wallHeight + floorDrop; // outer arch rises by +wt

        var floorZ = s.CenterElevation;
        var center = s.CenterPoint;
        var normal = s.NormalDirection;
        var slope = CrossSlope(s);

        Vector3 At(float u, float v) =>
            new(center.X + normal.X * u, center.Y + normal.Y * u, floorZ + u * slope + v);

        var ring = new Vector3[ringCount];
        var idx = 0;
        ring[idx++] = At(-halfWidth, -floorDrop);
        ring[idx++] = At(-halfWidth, wallHeight);
        for (int k = 1; k < archSegments; k++)
        {
            var theta = MathF.PI - MathF.PI * k / archSegments;
            ring[idx++] = At(halfWidth * MathF.Cos(theta), wallHeight + rise * MathF.Sin(theta));
        }

        ring[idx++] = At(halfWidth, wallHeight);
        ring[idx] = At(halfWidth, -floorDrop);
        return ring;
    }

    /// <summary>Bore center (mid-height point above the floor centerline) — the inward/outward reference.</summary>
    private static Vector3 BoreCenter(RoadCrossSection s, TunnelMeshProfile profile) =>
        new(s.CenterPoint.X, s.CenterPoint.Y, s.CenterElevation + profile.InteriorHeightMeters * 0.5f);

    /// <summary>
    /// Floor cross-slope (dz per lateral meter, +normal side up) from the station's banked edge Zs —
    /// the doc 03 shear source: no bank-angle plumbing, the edge Zs the solver wrote ARE the truth.
    /// 0 for flat, missing or degenerate stations.
    /// </summary>
    private static float CrossSlope(RoadCrossSection s)
    {
        if (s.LeftEdgeElevation is not { } left || s.RightEdgeElevation is not { } right ||
            !float.IsFinite(left) || !float.IsFinite(right) || s.WidthMeters < 1e-3f)
            return 0f;
        return (right - left) / s.WidthMeters;
    }

    /// <summary>
    /// The miter-limit anti-fold weld generalized to every ring-vertex path: a path point whose
    /// plan-view advance opposes the centerline direction is clamped to its predecessor (the inside of
    /// a tight curve pivots instead of pleating). Straight/normally-curved tubes are unchanged.
    /// </summary>
    private static void AntiFoldRings(IReadOnlyList<RoadCrossSection> cs, Vector3[][] rings)
    {
        int n = rings.Length;
        int ringCount = rings[0].Length;
        for (int i = 1; i < n; i++)
        {
            var advance = cs[i].CenterPoint - cs[i - 1].CenterPoint;
            if (advance.LengthSquared() < 1e-10f)
                advance = cs[i].TangentDirection;

            for (int j = 0; j < ringCount; j++)
            {
                var d = new Vector2(rings[i][j].X - rings[i - 1][j].X, rings[i][j].Y - rings[i - 1][j].Y);
                if (Vector2.Dot(d, advance) <= 0f)
                    rings[i][j] = rings[i - 1][j];
            }
        }
    }

    /// <summary>
    /// The portal COLLAR: a massive watertight solid swept along the REAL span stations for the first
    /// <see cref="TunnelMeshProfile.HeadwallDepthMeters"/> from the portal (snapped to station
    /// spacing). Per station the outer boundary is that station's shell ring flared outward by
    /// <see cref="TunnelMeshProfile.HeadwallFlareMeters"/> — full flare left/right AND up, floor
    /// corners drop a half-flare buried skirt. Faces, all outward: front annulus at the portal plane
    /// (out of the bore), swept outer rim band left/right/crown (radial, tangent component removed),
    /// swept underside slab (down, buried), back annulus at the collar's inner end (facing INTO the
    /// tunnel — the visible collar edge from inside the bore). Because the sweep follows the curve
    /// and the grade, the collar's inner boundary at every station is that station's interior ring —
    /// unlike the earlier straight prism it can never slice into the drivable space on curved or
    /// graded tunnels (user render 2026-07-18). Inner opening stays with the tube's own interior
    /// surfaces (coincident faces would z-fight).
    /// </summary>
    private static void BuildPortalCollar(
        MeshBuilder b, IReadOnlyList<RoadCrossSection> cs, Vector3[][] interior, Vector3[][] outer,
        TunnelMeshProfile profile, int ringCount, bool atStart)
    {
        int n = cs.Count;
        var depth = MathF.Max(0.05f, profile.HeadwallDepthMeters);
        var flare = profile.HeadwallFlareMeters;

        int portalIdx = atStart ? 0 : n - 1;
        var portalDist = cs[portalIdx].DistanceAlongRoad;

        // Walk inward until the collar depth is reached (or the span ends — short spans get a
        // full-length collar; overlapping solids are the accepted pattern).
        int innerIdx = portalIdx;
        while (true)
        {
            int next = atStart ? innerIdx + 1 : innerIdx - 1;
            if (next < 0 || next >= n)
                break;
            innerIdx = next;
            if (MathF.Abs(cs[innerIdx].DistanceAlongRoad - portalDist) >= depth)
                break;
        }

        if (innerIdx == portalIdx)
            return;

        int i0 = Math.Min(portalIdx, innerIdx);
        int i1 = Math.Max(portalIdx, innerIdx);
        int count = i1 - i0 + 1;

        // Flared rings per collar station (radial flare from that station's own center), then the
        // same anti-fold clamp as the shell so tight curves pivot instead of pleating.
        var flared = new Vector3[count][];
        for (int i = 0; i < count; i++)
        {
            var s = cs[i0 + i];
            var center = new Vector2(s.CenterPoint.X, s.CenterPoint.Y);
            var src = outer[i0 + i];
            var ring = new Vector3[ringCount];
            for (int j = 0; j < ringCount; j++)
            {
                var p = src[j];
                var lateral = new Vector2(p.X - center.X, p.Y - center.Y);
                var len = lateral.Length();
                var dir = len > 1e-4f ? lateral / len : Vector2.Zero;
                var isFloorCorner = j == 0 || j == ringCount - 1;
                var up = isFloorCorner ? -flare * 0.5f : flare;
                ring[j] = new Vector3(p.X + dir.X * flare, p.Y + dir.Y * flare, p.Z + up);
            }

            flared[i] = ring;
        }

        for (int i = 1; i < count; i++)
        {
            var advance = cs[i0 + i].CenterPoint - cs[i0 + i - 1].CenterPoint;
            if (advance.LengthSquared() < 1e-10f)
                advance = cs[i0 + i].TangentDirection;
            for (int j = 0; j < ringCount; j++)
            {
                var d = new Vector2(flared[i][j].X - flared[i - 1][j].X, flared[i][j].Y - flared[i - 1][j].Y);
                if (Vector2.Dot(d, advance) <= 0f)
                    flared[i][j] = flared[i - 1][j];
            }
        }

        // Swept outer rim (left/right/crown, radial normals) + underside slab per segment.
        for (int i = 0; i < count - 1; i++)
        {
            var s = cs[i0 + i];
            var boreCenter = BoreCenter(s, profile);
            var tangent3 = new Vector3(s.TangentDirection.X, s.TangentDirection.Y, 0f);
            for (int j = 0; j < ringCount - 1; j++)
            {
                var mid = (flared[i][j] + flared[i][j + 1]) * 0.5f;
                var outward = mid - boreCenter;
                outward -= tangent3 * Vector3.Dot(outward, tangent3); // pure radial, never along the bore
                AddQuad(b, flared[i][j], flared[i][j + 1], flared[i + 1][j + 1], flared[i + 1][j], outward);
            }

            AddQuad(b, flared[i][0], flared[i][ringCount - 1],
                flared[i + 1][ringCount - 1], flared[i + 1][0], -Vector3.UnitZ);
        }

        // Front annulus at the portal plane (out of the bore) + floor strip.
        int pl = portalIdx - i0;
        int il = innerIdx - i0;
        var sp = cs[portalIdx];
        var frontSign = atStart ? -1f : 1f;
        var facing = new Vector3(frontSign * sp.TangentDirection.X, frontSign * sp.TangentDirection.Y, 0f);
        var ip = interior[portalIdx];
        for (int j = 0; j < ringCount - 1; j++)
            AddQuad(b, ip[j], ip[j + 1], flared[pl][j + 1], flared[pl][j], facing);
        AddQuad(b, ip[0], ip[ringCount - 1], flared[pl][ringCount - 1], flared[pl][0], facing);

        // Back annulus at the collar's inner end (facing INTO the tunnel) + floor strip.
        var si = cs[innerIdx];
        var backFacing = new Vector3(-frontSign * si.TangentDirection.X, -frontSign * si.TangentDirection.Y, 0f);
        var ii = interior[innerIdx];
        for (int j = 0; j < ringCount - 1; j++)
            AddQuad(b, ii[j], ii[j + 1], flared[il][j + 1], flared[il][j], backFacing);
        AddQuad(b, ii[0], ii[ringCount - 1], flared[il][ringCount - 1], flared[il][0], backFacing);
    }

    private static void AddQuad(MeshBuilder b, Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Vector3 outward)
        => BridgeDeckMeshBuilder.AddFace(b, a, bb, c, d, outward);
}
