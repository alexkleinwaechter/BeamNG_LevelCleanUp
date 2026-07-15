namespace BeamNG.Procedural3D.RoadMesh;

using System.Numerics;
using BeamNG.Procedural3D.Builders;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Builds a closed, solid 3D <b>box</b> bridge deck mesh from a bridge spline's ordered cross-sections
/// (E-B). The solid is: a box shell (deck top + soffit at <c>top − thickness</c> + two fascia side faces +
/// start/end caps), a solid trapezoidal <b>parapet</b> on each edge (gated on
/// <see cref="BridgeDeckProfile.ParapetHeightMeters"/> &gt; 0), and a solid <b>end-stamp</b> block under each
/// bridge end (gated on <see cref="BridgeDeckProfile.GenerateAbutments"/>). Thickness comes from
/// <see cref="BridgeDeckProfile.ComputeDeckThicknessMeters"/> using the deck span.
/// </summary>
/// <remarks>
/// <para><b>Normals (important).</b> Bridge DAEs are written through the direct BeamNG-DAE XML path
/// (<c>ColladaExporter.Export(BeamNgDaeScene,…)</c>), which writes positions, normals and winding <i>as-is</i>
/// with NO coordinate conversion — unlike the road mesh, which goes through the Assimp path and gets a
/// <c>(-X, Z, Y)</c> mirror that flips winding handedness. So for the deck, every face must be authored with
/// its <b>geometric normal already pointing outward</b> in plain Z-up. <see cref="AddFace"/> guarantees this:
/// it orients each quad's winding so <c>cross(b-a, c-a)</c> points along the requested outward direction and
/// writes that exact (flat) outward normal on all four vertices. There is deliberately NO smooth-normal pass —
/// the solid reads with crisp, correctly-lit faces and no inverted/inward normals.</para>
/// <para>All geometry is built from <c>Center</c>, <c>Normal</c> (points RIGHT) and world-Z; the section
/// <c>Tangent</c> is used only for the cap/stamp end facing. Top edges already carry the banked Left/Right
/// edge elevations that <c>BridgeProfileSolver</c> wrote, so the soffit, fascia, parapets and stamps (all
/// derived from those edges) tilt with the deck automatically.</para>
/// </remarks>
public sealed class BridgeDeckMeshBuilder
{
    /// <summary>
    /// Builds the solid deck mesh for the given ordered cross-sections.
    /// </summary>
    /// <param name="crossSections">Ordered bridge cross-sections (world coords, banked edge Z already set).</param>
    /// <param name="profile">Deck profile supplying thickness, parapet and end-stamp config.</param>
    /// <param name="meshName">Name for the generated mesh.</param>
    /// <param name="materialName">Material assigned to the mesh.</param>
    /// <param name="trim">Doc 15 deck-to-deck merge trims (parapet opening masks, end-stamp
    /// suppression). Null ⇒ byte-identical legacy mesh.</param>
    /// <returns>A single solid <see cref="Mesh"/> with all-outward flat normals; empty if fewer than 2 sections.</returns>
    public Mesh Build(
        IReadOnlyList<RoadCrossSection> crossSections,
        BridgeDeckProfile profile,
        string meshName,
        string? materialName,
        BridgeDeckTrim? trim = null)
    {
        ArgumentNullException.ThrowIfNull(crossSections);
        ArgumentNullException.ThrowIfNull(profile);

        int n = crossSections.Count;
        if (n < 2)
            return new Mesh { Name = meshName, MaterialName = materialName };

        float span = crossSections[n - 1].DistanceAlongRoad - crossSections[0].DistanceAlongRoad;
        float thickness = BridgeDeckProfile.ComputeDeckThicknessMeters(span, profile);
        var down = new Vector3(0f, 0f, thickness);

        var builder = new MeshBuilder()
            .WithName(meshName)
            .WithMaterial(materialName);

        var (left, right) = BuildAntiFoldEdges(crossSections);

        BuildBoxShell(builder, crossSections, down, left, right);

        if (profile.ParapetHeightMeters > 0f)
        {
            BuildParapet(builder, crossSections, profile, leftSide: true, left, trim?.LeftParapetSuppressed);
            BuildParapet(builder, crossSections, profile, leftSide: false, right, trim?.RightParapetSuppressed);
        }

        if (profile.GenerateAbutments)
        {
            // Doc 15 (c): no stamp at ends that continue onto another deck — it would hang through the
            // trunk deck mid-air (the terrain layer learned this in doc 13; this is the mesh mirror).
            if (trim is not { SuppressStartStamp: true })
                AddEndStamp(builder, crossSections, down, profile, isStart: true, left, right);
            if (trim is not { SuppressEndStamp: true })
                AddEndStamp(builder, crossSections, down, profile, isStart: false, left, right);
        }

        // Normals are written explicitly (flat, outward) by AddFace — no smooth/flat recompute pass.
        return builder.Build();
    }

    /// <summary>
    /// Per-station top edge positions with an ANTI-FOLD weld (miter limit). Where the corridor's
    /// local curvature exceeds 1/halfWidth — a kink concentrated over a few stations — the inside
    /// edge polyline BACKTRACKS: consecutive edge points reverse against the direction of travel,
    /// the top quads pleat over themselves, and the winding-forced normals turn the pleat into
    /// overlapping geometry that breaks collision (Manhattan span 1192907311, ~27° over 11 m on a
    /// 21.6 m deck between merged ways 432550257/46177152 — vehicles wedged INTO the fold). Any
    /// edge point whose plan-view advance is not positive along the centerline direction is clamped
    /// to its predecessor, so the inside edge PIVOTS in place (a clean fan) while the outside edge
    /// sweeps — no crossing, no pleat, watertight collision. Straight and normally-curved decks are
    /// byte-identical.
    /// </summary>
    public static (Vector3[] Left, Vector3[] Right) BuildAntiFoldEdges(
        IReadOnlyList<RoadCrossSection> crossSections)
    {
        int n = crossSections.Count;
        var left = new Vector3[n];
        var right = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            left[i] = crossSections[i].GetLeftEdgePosition();
            right[i] = crossSections[i].GetRightEdgePosition();
        }

        for (int i = 1; i < n; i++)
        {
            var advance = crossSections[i].CenterPoint - crossSections[i - 1].CenterPoint;
            if (advance.LengthSquared() < 1e-10f)
                advance = crossSections[i].TangentDirection;

            var dl = new Vector2(left[i].X - left[i - 1].X, left[i].Y - left[i - 1].Y);
            if (Vector2.Dot(dl, advance) <= 0f)
                left[i] = left[i - 1];

            var dr = new Vector2(right[i].X - right[i - 1].X, right[i].Y - right[i - 1].Y);
            if (Vector2.Dot(dr, advance) <= 0f)
                right[i] = right[i - 1];
        }

        return (left, right);
    }

    /// <summary>The closed box shell: deck top, soffit, two fascia sides and the two end caps.</summary>
    private static void BuildBoxShell(
        MeshBuilder b, IReadOnlyList<RoadCrossSection> cs, Vector3 down, Vector3[] left, Vector3[] right)
    {
        int n = cs.Count;
        for (int i = 0; i < n - 1; i++)
        {
            var s0 = cs[i];

            Vector3 tl0 = left[i], tr0 = right[i];
            Vector3 tl1 = left[i + 1], tr1 = right[i + 1];
            Vector3 bl0 = tl0 - down, br0 = tr0 - down, bl1 = tl1 - down, br1 = tr1 - down;

            AddFace(b, tl0, tr0, tr1, tl1, Vector3.UnitZ);                       // deck top → up
            AddFace(b, bl0, br0, br1, bl1, -Vector3.UnitZ);                      // soffit → down
            AddFace(b, tl0, tl1, bl1, bl0, Normal3D(s0, -1f));                   // left fascia → −Normal
            AddFace(b, tr0, tr1, br1, br0, Normal3D(s0, 1f));                    // right fascia → +Normal
        }

        // Start cap → −Tangent, end cap → +Tangent.
        AddFace(b, left[0], right[0], right[0] - down, left[0] - down, Tangent3D(cs[0], -1f));
        AddFace(b, left[n - 1], right[n - 1], right[n - 1] - down, left[n - 1] - down, Tangent3D(cs[^1], 1f));
    }

    /// <summary>
    /// A solid trapezoidal parapet extruded along one deck edge: outer face vertical and flush with the deck
    /// edge, inner face sloped inward (base width → top width), top and bottom faces, in world-Z. Four faces
    /// per segment. The wall thickness is enforced to at least
    /// <see cref="BridgeDeckProfile.MinParapetThicknessMeters"/> at every height: the top width is clamped up
    /// to the minimum and the base can never be thinner than the top. With a doc-15
    /// <paramref name="suppressed"/> mask, a segment is emitted only when BOTH its stations are unsuppressed
    /// (the edge runs outside every conforming partner deck's footprint). EVERY surviving run is closed with
    /// a vertical cap face at BOTH of its ends — span ends and interior mask boundaries alike — so each
    /// parapet run is a watertight solid with outward normals from any viewpoint (an open end reads as a
    /// see-through hole in the wall along the roadway). Null mask ⇒ full-length parapet, still capped.
    /// </summary>
    private static void BuildParapet(
        MeshBuilder b, IReadOnlyList<RoadCrossSection> cs, BridgeDeckProfile p, bool leftSide,
        Vector3[] edges, IReadOnlyList<bool>? suppressed = null)
    {
        int n = cs.Count;
        float topW = MathF.Max(p.ParapetTopWidthMeters, BridgeDeckProfile.MinParapetThicknessMeters);
        float baseW = MathF.Max(p.ParapetBaseWidthMeters, topW);
        var up = new Vector3(0f, 0f, p.ParapetHeightMeters);

        var baseOuter = new Vector3[n];
        var baseInner = new Vector3[n];
        var topInner = new Vector3[n];
        var topOuter = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            var s = cs[i];
            Vector3 edge = edges[i]; // anti-fold welded — the wall pivots with the deck edge
            Vector3 inward = Normal3D(s, leftSide ? 1f : -1f); // toward deck centre
            baseOuter[i] = edge;
            baseInner[i] = edge + inward * baseW;
            topInner[i] = edge + inward * topW + up;
            topOuter[i] = edge + up;
        }

        bool Kept(int seg) => suppressed == null ||
                              (!(seg < suppressed.Count && suppressed[seg]) &&
                               !(seg + 1 < suppressed.Count && suppressed[seg + 1]));

        void AddCap(int station, float tangentSign) => AddFace(b,
            baseOuter[station], baseInner[station], topInner[station], topOuter[station],
            Tangent3D(cs[station], tangentSign));

        for (int i = 0; i < n - 1; i++)
        {
            if (!Kept(i))
                continue;

            var s = cs[i];
            Vector3 outerN = Normal3D(s, leftSide ? -1f : 1f); // edge-outward (flush face)
            Vector3 innerN = Normal3D(s, leftSide ? 1f : -1f); // toward centre (sloped face)

            AddFace(b, baseOuter[i], topOuter[i], topOuter[i + 1], baseOuter[i + 1], outerN); // outer (flush)
            AddFace(b, baseInner[i], topInner[i], topInner[i + 1], baseInner[i + 1], innerN); // inner (sloped)
            AddFace(b, topOuter[i], topInner[i], topInner[i + 1], topOuter[i + 1], Vector3.UnitZ); // top
            AddFace(b, baseOuter[i], baseInner[i], baseInner[i + 1], baseOuter[i + 1], -Vector3.UnitZ); // bottom

            // Close the run wherever it starts/ends: the span extremities AND interior mask boundaries.
            if (i == 0 || !Kept(i - 1))
                AddCap(i, -1f);
            if (i == n - 2 || !Kept(i + 1))
                AddCap(i + 1, 1f);
        }
    }

    /// <summary>
    /// A solid end-stamp block under one bridge end, <see cref="BridgeDeckProfile.EndStampLengthMeters"/>
    /// long along the road (clipped to the span) and <see cref="BridgeDeckProfile.AbutmentDepthMeters"/>
    /// deep below the soffit. The stamp FOLLOWS the deck: its top rides the soffit edge polyline of the
    /// actual cross-sections inside the stamp length (the soffit itself is piecewise-linear between
    /// stations, so the stamp top is coincident with it — an arched or curved deck can never open a
    /// see-through slit above a straight flat-topped block, which the old single-section extrusion did).
    /// Watertight: two caps + per segment left/right/top/bottom faces, all outward via <see cref="AddFace"/>.
    /// </summary>
    private static void AddEndStamp(
        MeshBuilder b, IReadOnlyList<RoadCrossSection> cs, Vector3 down, BridgeDeckProfile p, bool isStart,
        Vector3[] leftEdges, Vector3[] rightEdges)
    {
        int n = cs.Count;
        var end = isStart ? cs[0] : cs[n - 1];
        float endDist = end.DistanceAlongRoad;
        float span = MathF.Abs(cs[n - 1].DistanceAlongRoad - cs[0].DistanceAlongRoad);
        float len = MathF.Min(p.EndStampLengthMeters, span);

        // Soffit edge corners from the deck end inward, cut at exactly `len` with a section lerped
        // between the bracketing stations (so the knob keeps its meaning regardless of station spacing).
        var left = new List<Vector3> { leftEdges[isStart ? 0 : n - 1] - down };
        var right = new List<Vector3> { rightEdges[isStart ? 0 : n - 1] - down };
        for (int k = 1; k < n; k++)
        {
            int curIdx = isStart ? k : n - 1 - k;
            float d = MathF.Abs(cs[curIdx].DistanceAlongRoad - endDist);
            if (d >= len - 1e-3f)
            {
                int prevIdx = isStart ? k - 1 : n - k;
                float dPrev = MathF.Abs(cs[prevIdx].DistanceAlongRoad - endDist);
                float t = d > dPrev ? Math.Clamp((len - dPrev) / (d - dPrev), 0f, 1f) : 0f;
                left.Add(Vector3.Lerp(leftEdges[prevIdx], leftEdges[curIdx], t) - down);
                right.Add(Vector3.Lerp(rightEdges[prevIdx], rightEdges[curIdx], t) - down);
                break;
            }

            left.Add(leftEdges[curIdx] - down);
            right.Add(rightEdges[curIdx] - down);
        }

        int m = left.Count;
        if (m < 2)
            return;

        var drop = new Vector3(0f, 0f, p.AbutmentDepthMeters);

        AddFace(b, left[0], right[0], right[0] - drop, left[0] - drop, Tangent3D(end, isStart ? -1f : 1f));  // outer (deck end)
        AddFace(b, left[m - 1], right[m - 1], right[m - 1] - drop, left[m - 1] - drop,
            Tangent3D(end, isStart ? 1f : -1f));                                                             // inner (toward span)

        for (int j = 0; j < m - 1; j++)
        {
            AddFace(b, left[j], left[j + 1], left[j + 1] - drop, left[j] - drop, Normal3D(end, -1f));   // left side
            AddFace(b, right[j], right[j + 1], right[j + 1] - drop, right[j] - drop, Normal3D(end, 1f)); // right side
            AddFace(b, left[j], right[j], right[j + 1], left[j + 1], Vector3.UnitZ);                     // top (under soffit)
            AddFace(b, left[j] - drop, right[j] - drop, right[j + 1] - drop, left[j + 1] - drop,
                -Vector3.UnitZ);                                                                         // bottom
        }
    }

    private static Vector3 Normal3D(RoadCrossSection s, float sign)
        => new(sign * s.NormalDirection.X, sign * s.NormalDirection.Y, 0f);

    private static Vector3 Tangent3D(RoadCrossSection s, float sign)
        => new(sign * s.TangentDirection.X, sign * s.TangentDirection.Y, 0f);

    /// <summary>
    /// Emits a planar quad (corners a→b→c→d) as two triangles, wound so its geometric normal points along
    /// <paramref name="approxOutward"/>, and writes that exact outward normal (flat) on all four vertices.
    /// Reversing the quad order flips the planar normal, so the winding is guaranteed CCW-from-outside.
    /// Internal so <see cref="BridgePierMeshBuilder"/> shares the exact same authoring discipline.
    /// </summary>
    internal static void AddFace(MeshBuilder b, Vector3 a, Vector3 bb, Vector3 c, Vector3 d, Vector3 approxOutward)
    {
        // Pick the winding whose normal agrees with the desired outward direction.
        Vector3 fwd = Vector3.Cross(bb - a, c - a);
        Vector3 v0, v1, v2, v3;
        if (Vector3.Dot(fwd, approxOutward) >= 0f)
        {
            v0 = a; v1 = bb; v2 = c; v3 = d;
        }
        else
        {
            v0 = a; v1 = d; v2 = c; v3 = bb; // reversed → planar normal flips outward
        }

        Vector3 geo = Vector3.Cross(v1 - v0, v2 - v0);
        Vector3 nrm = geo.LengthSquared() > 1e-12f
            ? Vector3.Normalize(geo)
            : (approxOutward.LengthSquared() > 1e-12f ? Vector3.Normalize(approxOutward) : Vector3.UnitZ);

        int i0 = b.AddVertex(v0, nrm, new Vector2(0f, 0f));
        int i1 = b.AddVertex(v1, nrm, new Vector2(1f, 0f));
        int i2 = b.AddVertex(v2, nrm, new Vector2(1f, 1f));
        int i3 = b.AddVertex(v3, nrm, new Vector2(0f, 1f));
        b.AddTriangle(i0, i1, i2);
        b.AddTriangle(i0, i2, i3);
    }
}
