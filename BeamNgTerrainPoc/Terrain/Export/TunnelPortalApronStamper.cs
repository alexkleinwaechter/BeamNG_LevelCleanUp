using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
///     Tunnel plan Phase 2c (ai_docs/2026-07-18_tunnel_generation/01), modeled on
///     <see cref="BridgeAbutmentOverlapStamper" />: stamps the terrain across each tunnel portal APRON
///     (the first/last <c>PortalApronMeters</c> of the span, which the exclusion shrink left as ordinary
///     stamped road) to the SOLVED road surface, so terrain and tunnel-floor mesh meet at exactly the
///     same Z at the portal mouth — no lip, no step. Unlike the bridge tongue (raise-only), the apron
///     stamps BOTH ways: the approach cut into the mountain flank must be carved down to road level just
///     as low spots must be filled — the portal face terrain IS the road surface there.
///     <para>Write-guarded by <see cref="RoadSurfaceOwnerRaster" /> (only own/unowned cells), runs
///     POST-solve (reads the same final floor Z the mesh uses), before the DAM report and DecalRoads.
///     No-op without tunnel spans / with <c>EnablePortalAprons</c> off — flag-off byte-identical.</para>
/// </summary>
public static class TunnelPortalApronStamper
{
    public static int Stamp(
        UnifiedRoadNetwork network,
        float[,]? heightMap,
        float metersPerPixel,
        bool log = true,
        int[,]? roadSurfaceOwner = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (heightMap == null || metersPerPixel <= 0f)
            return 0;

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);
        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

        var spanGroups = network.CrossSections
            .Where(c => c.StructureSpanId >= 0 && c.StructureSpanType == StructureType.Tunnel)
            .GroupBy(c => (c.OwnerSplineId, c.StructureSpanId))
            .Select(g => g.OrderBy(c => c.DistanceAlongSpline).ToList());

        var cells = 0;
        var spansStamped = 0;
        var maxRaise = 0f;
        var maxCut = 0f;

        foreach (var span in spanGroups)
        {
            var spline = network.GetSplineById(span[0].OwnerSplineId);
            var rules = spline?.Parameters.TunnelRules;
            if (rules?.EnablePortalAprons != true || rules.PortalApronMeters <= 0f)
                continue;

            var apron = rules.PortalApronMeters;
            var banked = rules.EnableTunnelBanking;
            var affectedRange = MathF.Max(0f, spline!.Parameters.TerrainAffectedRangeMeters);
            var first = span[0].DistanceAlongSpline;
            var last = span[^1].DistanceAlongSpline;

            var allSections = network.GetCrossSectionsForSpline(span[0].OwnerSplineId)
                .OrderBy(c => c.DistanceAlongSpline).ToList();

            var stampedCells = 0;

            // Start portal: [approach neighbour] + span sections within the apron of the span start.
            var approachBefore = allSections.LastOrDefault(c =>
                c.StructureSpanId != span[0].StructureSpanId && c.DistanceAlongSpline < first);
            var startRun = new List<UnifiedCrossSection>();
            if (approachBefore != null) startRun.Add(approachBefore);
            startRun.AddRange(span.Where(c => c.DistanceAlongSpline - first <= apron));
            stampedCells += StampRun(startRun, span[0].OwnerSplineId, affectedRange,
                heightMap, metersPerPixel, mapWidth, mapHeight, lateralStep,
                roadSurfaceOwner, banked, ref maxRaise, ref maxCut);

            // End portal.
            var approachAfter = allSections.FirstOrDefault(c =>
                c.StructureSpanId != span[0].StructureSpanId && c.DistanceAlongSpline > last);
            var endRun = span.Where(c => last - c.DistanceAlongSpline <= apron).ToList();
            if (approachAfter != null) endRun.Add(approachAfter);
            stampedCells += StampRun(endRun, span[0].OwnerSplineId, affectedRange,
                heightMap, metersPerPixel, mapWidth, mapHeight, lateralStep,
                roadSurfaceOwner, banked, ref maxRaise, ref maxCut);

            cells += stampedCells;
            if (stampedCells > 0)
                spansStamped++;
        }

        if (log && cells > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[TUNNEL-PORTAL] spans={spansStamped} cellsStamped={cells} " +
                $"maxRaise={maxRaise:F2}m maxCut={maxCut:F2}m");

        return cells;
    }

    /// <summary>
    ///     Marches one apron run sub-stepped at ≤ half a cell (the tongue's alignment-hole fix) and
    ///     stamps each cell toward the interpolated road surface Z with the road's lateral falloff:
    ///     full stamp across the road half-width, smoothstep blend into surrounding terrain.
    /// </summary>
    private static int StampRun(
        List<UnifiedCrossSection> run,
        int ownerSplineId,
        float affectedRange,
        float[,] heightMap,
        float metersPerPixel,
        int mapWidth,
        int mapHeight,
        float lateralStep,
        int[,]? roadSurfaceOwner,
        bool banked,
        ref float maxRaise,
        ref float maxCut)
    {
        var cells = 0;
        for (var i = 0; i + 1 < run.Count || (run.Count == 1 && i == 0); i++)
        {
            var a = run[i];
            var b = run.Count == 1 ? a : run[i + 1];

            var zA = a.TargetElevation;
            var zB = b.TargetElevation;
            if (!float.IsFinite(zA) || !float.IsFinite(zB))
                continue;

            var segLen = MathF.Abs(b.DistanceAlongSpline - a.DistanceAlongSpline);
            var steps = run.Count == 1 ? 1 : Math.Max(1, (int)MathF.Ceiling(segLen / lateralStep));

            for (var s = 0; s <= steps; s++)
            {
                var t = steps == 0 ? 0f : (float)s / steps;
                var center = a.CenterPoint + (b.CenterPoint - a.CenterPoint) * t;
                var normal = a.NormalDirection + (b.NormalDirection - a.NormalDirection) * t;
                var normalLen = normal.Length();
                if (normalLen > 1e-4f)
                    normal /= normalLen;
                var z = zA + (zB - zA) * t;
                var halfWidth = (a.EffectiveRoadWidth + (b.EffectiveRoadWidth - a.EffectiveRoadWidth) * t) / 2f;
                // Banking on ⇒ the apron surface tilts with the road (the BridgeAbutmentOverlapStamper
                // formula); off ⇒ flat across the width (the approach neighbour may carry a bank even
                // in flat-tunnel mode, so the slope must be gated, not derived).
                var bankSlope = banked
                    ? MathF.Sin(a.BankAngleRadians + (b.BankAngleRadians - a.BankAngleRadians) * t)
                    : 0f;
                var reach = halfWidth + affectedRange;

                for (var offset = -reach; offset <= reach; offset += lateralStep)
                {
                    var lateral = LateralFalloff(MathF.Abs(offset), halfWidth, affectedRange);
                    if (lateral <= 0f)
                        continue;

                    var worldX = center.X + normal.X * offset;
                    var worldY = center.Y + normal.Y * offset;
                    var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                    var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                    // Never touch a NEIGHBOURING road's protected surface.
                    if (!RoadSurfaceOwnerRaster.CanWrite(roadSurfaceOwner, py, px, ownerSplineId))
                        continue;

                    var target = z + offset * bankSlope;
                    var current = heightMap[py, px];
                    if (!float.IsFinite(current))
                        continue;

                    var delta = (target - current) * lateral;
                    if (MathF.Abs(delta) < 1e-4f)
                        continue;

                    heightMap[py, px] = current + delta;
                    cells++;
                    if (delta > maxRaise) maxRaise = delta;
                    if (-delta > maxCut) maxCut = -delta;
                }
            }
        }

        return cells;
    }

    /// <summary>1 across the road half-width, smoothstepping to 0 over the terrain-affected range.</summary>
    private static float LateralFalloff(float absOffset, float halfWidth, float affectedRange)
    {
        if (absOffset <= halfWidth)
            return 1f;
        if (affectedRange <= 0f || absOffset >= halfWidth + affectedRange)
            return 0f;
        var t = (absOffset - halfWidth) / affectedRange;
        return 1f - t * t * (3f - 2f * t);
    }
}
