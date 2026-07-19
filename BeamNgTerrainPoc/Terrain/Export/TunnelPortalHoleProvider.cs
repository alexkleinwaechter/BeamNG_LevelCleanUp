using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Processing;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
///     Tunnel plan Phase 4 (ai_docs/2026-07-18_tunnel_generation/01): computes terrain-hole cells from
///     the captured <c>network.TunnelSpans</c> + the FINAL heightmap and stamps them via
///     <see cref="TerrainHoleCutter" /> into the materialIndices grid (after
///     <c>BridgeUnderDeckMaterialPainter</c>, before terrain assembly — nothing repaints over holes).
///     <para>Per-cell criterion, two zones (tunneljena render 2026-07-18 taught the difference):</para>
///     <list type="bullet">
///       <item><b>Clip rule</b> (whole span, lateral extent = interior half-width ONLY, no dilation):
///       hole iff the heightmap surface pierces the shell SILHOUETTE — per-offset ceiling window
///       floorZ − ε &lt; terrainZ &lt; wall + local arch height + wallThickness + ε — so every holed
///       cell has tube shell directly behind it (the old corridor-wide flat window opened void
///       laterally beside the shell and above the wall shoulders). Deep sections keep intact
///       mountain — the player drives under real terrain.</item>
///       <item><b>Portal rule</b> (within <c>PortalHoleMinLengthMeters</c> of each mouth, full
///       roadway + wall + margin, dilated one cell): hole every cell with terrainZ &gt; floorZ + ε —
///       removes the terrain wall the heightmap necessarily forms between apron level and mountain
///       flank (a heightmap cannot overhang). Hidden behind the extruded portal headwall
///       (<c>PortalHeadwallFlareMeters</c>).</item>
///       <item>Guard: never hole a cell owned by ANOTHER road's painted surface
///       (<see cref="RoadSurfaceOwnerRaster" />) — protects crossing surface roads above the tunnel.</item>
///     </list>
///     Hole cells lose their material ⇒ groundcover/billboards vanish there automatically (same reason
///     <c>LayerMaskReader</c> excludes 255 from every mask) — no under-deck-painter analogue needed.
/// </summary>
public static class TunnelPortalHoleProvider
{
    private const float Epsilon = 0.05f;

    /// <summary>
    ///     Cuts portal holes for every captured tunnel span whose owning spline has
    ///     <c>EnablePortalHoles</c> on. Returns the merged stamping counters.
    /// </summary>
    public static HoleCutResult CutPortalHoles(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        byte[] materialIndices,
        int size,
        float metersPerPixel,
        bool log = true,
        int[,]? roadSurfaceOwner = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(heightMap);
        ArgumentNullException.ThrowIfNull(materialIndices);
        if (metersPerPixel <= 0f || network.TunnelSpans.Count == 0)
            return HoleCutResult.Empty;

        var total = HoleCutResult.Empty;
        foreach (var span in network.TunnelSpans)
        {
            var spline = network.GetSplineById(span.SplineId);
            var rules = spline?.Parameters.TunnelRules;
            if (rules?.EnablePortalHoles != true || span.Stations.Count < 2)
                continue;

            // Two zones with different discipline (tunneljena render 2026-07-18: the old
            // corridor-wide clip window holed terrain laterally OUTSIDE the shell and above the wall
            // shoulders — open void the mesh could never mask):
            // - CLIP zone (whole span): only cells whose terrain pierces the shell SILHOUETTE
            //   (per-offset arch height), no dilation — every holed cell has shell behind it.
            // - PORTAL zone (mouths): full roadway + margin, dilated — hides behind the flared headwall.
            var mask = BuildSpanHoleMask(span, rules, heightMap, size, metersPerPixel,
                out var portalMask);
            DilateOneCell(portalMask, size);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (portalMask[y, x])
                    mask[y, x] = true;

            // Owner guard AFTER dilation so a dilated rim can't creep onto a foreign road surface.
            if (roadSurfaceOwner != null)
            {
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    if (mask[y, x] && !RoadSurfaceOwnerRaster.CanWrite(roadSurfaceOwner, y, x, span.SplineId))
                        mask[y, x] = false;
            }

            var result = TerrainHoleCutter.Apply(materialIndices, size, mask);
            total = total.Add(result);

            if (log && result.CellsStamped > 0)
            {
                var s0 = span.Stations[0].DistanceAlongSpline;
                var s1 = span.Stations[^1].DistanceAlongSpline;
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[TUNNEL-HOLE] span {span.SpanId} spline {span.SplineId}: " +
                    $"cells={result.CellsStamped} (alreadyHole={result.CellsAlreadyHole}) " +
                    $"stations [{s0:F1},{s1:F1}]m");
            }
        }

        if (log && total.CellsStamped > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[TUNNEL-HOLE] total: {total.CellsStamped} cell(s) stamped across " +
                $"{network.TunnelSpans.Count} span(s)");

        return total;
    }

    /// <summary>
    ///     Matches <c>TunnelMeshProfile.WallHeightFraction</c> (the exporter keeps the builder default):
    ///     fraction of the interior height taken by the vertical walls, rest is the arch rise. The clip
    ///     rule's per-offset ceiling window must track the same silhouette the mesh builds.
    /// </summary>
    private const float WallHeightFraction = 0.6f;

    /// <summary>
    ///     Rasterizes one span's corridor. CLIP cells (terrain pierces the shell silhouette — per-offset
    ///     arch height window, lateral extent = interior half-width only) go into the returned mask
    ///     un-dilated: every one of them has tube shell directly behind it. PORTAL-mouth cells (full
    ///     roadway + margin, any terrain above the floor) go into <paramref name="portalMask" /> for
    ///     dilation — they hide behind the flared headwall. Masks are in terrain space (y = 0 = south).
    /// </summary>
    private static bool[,] BuildSpanHoleMask(
        BridgeSpanSnapshot span,
        TunnelRuleSystemOptions rules,
        float[,] heightMap,
        int size,
        float metersPerPixel,
        out bool[,] portalMask)
    {
        var mask = new bool[size, size];
        portalMask = new bool[size, size];
        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);

        var wallHeight = rules.TunnelInteriorHeightMeters * WallHeightFraction;
        var archRise = rules.TunnelInteriorHeightMeters - wallHeight;
        var portalLen = MathF.Max(0f, rules.PortalHoleMinLengthMeters);
        var stations = span.Stations;
        var spanStart = stations[0].DistanceAlongSpline;
        var spanEnd = stations[^1].DistanceAlongSpline;

        var step = MathF.Max(0.25f, metersPerPixel * 0.5f);
        for (var i = 0; i + 1 < stations.Count; i++)
        {
            var a = stations[i];
            var b = stations[i + 1];
            if (!float.IsFinite(a.CenterZ) || !float.IsFinite(b.CenterZ))
                continue;

            var segLen = MathF.Abs(b.DistanceAlongSpline - a.DistanceAlongSpline);
            var steps = Math.Max(1, (int)MathF.Ceiling(segLen / step));

            var slopeA = CrossSlope(a);
            var slopeB = CrossSlope(b);
            for (var s = 0; s <= steps; s++)
            {
                var t = (float)s / steps;
                var center = Vector2.Lerp(a.Center, b.Center, t);
                var normal = a.Normal + (b.Normal - a.Normal) * t;
                var normalLen = normal.Length();
                if (normalLen > 1e-4f)
                    normal /= normalLen;

                var floorZ = a.CenterZ + (b.CenterZ - a.CenterZ) * t;
                var slope = slopeA + (slopeB - slopeA) * t;
                var station = a.DistanceAlongSpline + (b.DistanceAlongSpline - a.DistanceAlongSpline) * t;
                var width = a.Width + (b.Width - a.Width) * t;
                var interiorHalf = width / 2f + rules.TunnelSideClearanceMeters;
                var portalHalf = interiorHalf + rules.TunnelWallThicknessMeters +
                                 rules.PortalHoleLateralMarginMeters;
                var nearPortal = station - spanStart <= portalLen || spanEnd - station <= portalLen;
                var scanHalf = nearPortal ? portalHalf : interiorHalf;

                for (var offset = -scanHalf; offset <= scanHalf; offset += step)
                {
                    var worldX = center.X + normal.X * offset;
                    var worldY = center.Y + normal.Y * offset;
                    var px = (int)(worldX / metersPerPixel);
                    var py = (int)(worldY / metersPerPixel);
                    if (px < 0 || px >= size || py < 0 || py >= size ||
                        px >= mapWidth || py >= mapHeight)
                        continue;

                    var terrainZ = heightMap[py, px];
                    if (!float.IsFinite(terrainZ))
                        continue;

                    // Banking shear (doc 03): both windows follow the tilted floor line, so the hole
                    // edge hugs the sheared silhouette. Flat spans ⇒ slope 0 ⇒ floorAtOffset = floorZ.
                    var floorAtOffset = floorZ + offset * slope;

                    // Portal rule: near the mouth, any terrain above the floor is the "wall across
                    // the road" the heightmap forms between apron and mountain flank. Behind the
                    // flared headwall — dilated later.
                    if (nearPortal && terrainZ > floorAtOffset + Epsilon)
                    {
                        portalMask[py, px] = true;
                        continue;
                    }

                    // Clip rule: the heightmap surface pierces the shell SILHOUETTE at this lateral
                    // offset — ceiling window = wall height + local arch height + wall thickness.
                    if (MathF.Abs(offset) > interiorHalf)
                        continue;
                    var u = interiorHalf > 1e-3f ? MathF.Abs(offset) / interiorHalf : 0f;
                    var ceilingOuterZ = floorAtOffset + wallHeight +
                                        archRise * MathF.Sqrt(MathF.Max(0f, 1f - u * u)) +
                                        rules.TunnelWallThicknessMeters;
                    if (terrainZ > floorAtOffset - Epsilon && terrainZ < ceilingOuterZ + Epsilon)
                        mask[py, px] = true;
                }
            }
        }

        return mask;
    }

    /// <summary>
    ///     Floor cross-slope (dz per lateral meter, +normal side up) from a station's banked edge Zs —
    ///     the same shear source the mesh builder uses. 0 for flat or degenerate stations.
    /// </summary>
    private static float CrossSlope(BridgeStation st) =>
        st.Width > 1e-3f && float.IsFinite(st.LeftEdgeZ) && float.IsFinite(st.RightEdgeZ)
            ? (st.RightEdgeZ - st.LeftEdgeZ) / st.Width
            : 0f;

    /// <summary>4-neighbour dilation by one cell (jagged hole edges tuck behind shell + headwall).</summary>
    private static void DilateOneCell(bool[,] mask, int size)
    {
        var dilated = new List<(int X, int Y)>();
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (mask[y, x])
                continue;
            if ((x > 0 && mask[y, x - 1]) || (x < size - 1 && mask[y, x + 1]) ||
                (y > 0 && mask[y - 1, x]) || (y < size - 1 && mask[y + 1, x]))
                dilated.Add((x, y));
        }

        foreach (var (x, y) in dilated)
            mask[y, x] = true;
    }
}
