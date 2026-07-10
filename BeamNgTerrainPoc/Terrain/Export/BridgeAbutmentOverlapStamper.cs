using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Doc 06 §3.1 — the abutment overlap tongue: stamps the first/last <c>AbutmentOverlapMeters</c> of each
/// (sparse-mode) bridge span into the heightmap at the FINAL solved deck elevation minus
/// <c>AbutmentOverlapDropMeters</c>, with the road's normal lateral falloff. The stamped road otherwise
/// ENDS at the exclusion boundary (RoadMaskBuilder skips excluded sections), and a butt-joint between the
/// raster terrain and the smooth deck mesh can never be gap-free at finite resolution — so the terrain is
/// made to run CONTINUOUSLY from the approach under the deck end; any residual raster step lies below the
/// deck surface where a wheel never touches it.
///
/// Raise-only (cuts are <see cref="BridgeDeckExcavator"/>'s job — which must exempt the overlap zone, its
/// ceiling there is <c>deckZ − drop</c> instead of <c>− undercut</c>). Runs POST-solve, after
/// <see cref="BridgeProfileSolver.RefineSpans"/> + the approach-raise ramps and BEFORE the excavator, so
/// it reads the same final deck Z the mesh uses. No-op without sparse-mode spans — flag-off byte-identical.
/// </summary>
public static class BridgeAbutmentOverlapStamper
{
    public static int Stamp(
        UnifiedRoadNetwork network,
        float[,]? heightMap,
        float metersPerPixel,
        bool log = true,
        int[,]? roadSurfaceOwner = null,
        int[,]? deckFootprint = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (heightMap == null || metersPerPixel <= 0f)
            return 0;

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);
        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

        var deckGroups = network.CrossSections
            .Where(c => c.StructureSpanId >= 0)
            .GroupBy(c => (c.OwnerSplineId, c.StructureSpanId))
            .Select(g => g.OrderBy(c => c.DistanceAlongSpline).ToList());

        var cells = 0;
        var spansStamped = 0;
        var maxLift = 0f;

        foreach (var deck in deckGroups)
        {
            var spline = network.GetSplineById(deck[0].OwnerSplineId);
            var rules = spline?.Parameters.BridgeRules;
            if (rules?.EnableSparseDeckConstraints != true || rules.AbutmentOverlapMeters <= 0f)
                continue;

            var overlap = rules.AbutmentOverlapMeters;
            var drop = rules.AbutmentOverlapDropMeters;
            var affectedRange = MathF.Max(0f, spline!.Parameters.TerrainAffectedRangeMeters);
            var first = deck[0].DistanceAlongSpline;
            var last = deck[^1].DistanceAlongSpline;

            // Doc 13: a span end that continues onto another deck is not a ground abutment — no tongue.
            var seg = spline.StructureSegments?.FirstOrDefault(s => s.SpanId == deck[0].StructureSpanId);
            var suppressStart = seg?.StartContinuesOntoDeck == true;
            var suppressEnd = seg?.EndContinuesOntoDeck == true;

            // Neighbouring APPROACH sections (already terrain-stamped by the mask at their road Z): the
            // segment between them and the first/last span section belongs to nobody otherwise — exactly
            // the alignment-dependent cap hole. Include them so the tongue runs road → deck seamlessly.
            var allSections = network.GetCrossSectionsForSpline(deck[0].OwnerSplineId)
                .OrderBy(c => c.DistanceAlongSpline).ToList();

            // Build the two overlap runs as node chains (approach node carries NO drop — it IS the road
            // surface), then march each consecutive pair SUB-STEPPED so no heightmap row is skipped even
            // when the cross-section interval exceeds the cell size (per-section stamping left
            // alignment-dependent holes — "works at one end, not the other").
            var maxLiftAllowed = MathF.Max(0f, rules.AbutmentOverlapMaxLiftMeters);
            var stampedCells = 0;

            if (!suppressStart)
            {
                var approachBefore = allSections.LastOrDefault(c =>
                    c.StructureSpanId != deck[0].StructureSpanId && c.DistanceAlongSpline < first);
                var startRun = new List<UnifiedCrossSection>();
                if (approachBefore != null) startRun.Add(approachBefore);
                startRun.AddRange(deck.Where(c => c.DistanceAlongSpline - first <= overlap));
                stampedCells += StampRun(startRun, deck[0].StructureSpanId, deck[0].OwnerSplineId, drop,
                    affectedRange, heightMap, metersPerPixel, mapWidth, mapHeight, lateralStep,
                    roadSurfaceOwner, deckFootprint, maxLiftAllowed, ref maxLift);
            }

            if (!suppressEnd)
            {
                var approachAfter = allSections.FirstOrDefault(c =>
                    c.StructureSpanId != deck[0].StructureSpanId && c.DistanceAlongSpline > last);
                var endRun = deck.Where(c => last - c.DistanceAlongSpline <= overlap).ToList();
                if (approachAfter != null) endRun.Add(approachAfter);
                stampedCells += StampRun(endRun, deck[0].StructureSpanId, deck[0].OwnerSplineId, drop,
                    affectedRange, heightMap, metersPerPixel, mapWidth, mapHeight, lateralStep,
                    roadSurfaceOwner, deckFootprint, maxLiftAllowed, ref maxLift);
            }
            cells += stampedCells;
            if (stampedCells > 0)
                spansStamped++;
        }

        if (log && cells > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-OVERLAP] spans={spansStamped} cellsRaised={cells} maxLift={maxLift:F2}m");

        return cells;
    }

    /// <summary>
    /// Marches one overlap run (consecutive node chain) and paints every heightmap row along it:
    /// consecutive pairs are SUB-STEPPED at ≤ half a cell so the tongue is contiguous regardless of the
    /// cross-section interval, with center/normal/Z/width/bank interpolated per step. Span nodes stamp at
    /// <c>TargetElevation − drop</c>; an approach node stamps at its road Z (drop 0) so the tongue lerps
    /// road → deck across the exclusion boundary. Raise-only, monotone per cell.
    /// </summary>
    private static int StampRun(
        List<UnifiedCrossSection> run,
        int spanId,
        int ownerSplineId,
        float drop,
        float affectedRange,
        float[,] heightMap,
        float metersPerPixel,
        int mapWidth,
        int mapHeight,
        float lateralStep,
        int[,]? roadSurfaceOwner,
        int[,]? deckFootprint,
        float maxLiftAllowed,
        ref float maxLift)
    {
        var cells = 0;
        for (var i = 0; i + 1 < run.Count || (run.Count == 1 && i == 0); i++)
        {
            var a = run[i];
            var b = run.Count == 1 ? a : run[i + 1];

            var zA = TopZ(a, spanId, drop);
            var zB = TopZ(b, spanId, drop);
            if (float.IsNaN(zA) || float.IsNaN(zB))
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
                // Full smoothing-corridor width (EffectiveRoadWidth) — matches the corridor-width deck mesh +
                // excavator, so the tongue raises terrain to the deck across the whole deck footprint. The
                // per-cell road-surface owner guard (below) keeps it off NEIGHBOURING roads, so corridor width
                // is safe again (no narrow tongue needed). Collapses to RoadWidthMeters when no width profile.
                var halfWidth = (a.EffectiveRoadWidth + (b.EffectiveRoadWidth - a.EffectiveRoadWidth) * t) / 2f;
                var bankSlope = MathF.Sin(a.BankAngleRadians + (b.BankAngleRadians - a.BankAngleRadians) * t);
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

                    // Never raise a NEIGHBOURING road's protected surface — the tongue shapes only this
                    // bridge's own footprint + bare terrain (self/no-owner). No raster ⇒ legacy behaviour.
                    if (!RoadSurfaceOwnerRaster.CanWrite(roadSurfaceOwner, py, px, ownerSplineId))
                        continue;

                    // Never raise terrain over a FOREIGN bridge's deck footprint (doc 09 §9.2): the
                    // tongue supports only its own bridge end; another deck's driving surface must
                    // stay daylighted. Own-deck cells stay writable (the tongue overlaps its own span).
                    if (!BridgeDeckFootprintRaster.CanRaise(deckFootprint, py, px, ownerSplineId))
                        continue;

                    // The tongue follows the deck's slope AND bank, a hair below the driving surface.
                    var target = z + offset * bankSlope;
                    var current = heightMap[py, px];
                    if (float.IsNaN(current) || float.IsInfinity(current) || target <= current)
                        continue; // raise-only: never cut (excavator), never bury high ground deeper
                    if (target - current > maxLiftAllowed)
                        continue; // not a seam: genuinely LOW ground under an elevated deck end (e.g. a
                                  // doc-28 underpass well near the abutment) — never wall it up to deck level

                    var newZ = current + (target - current) * lateral;
                    heightMap[py, px] = newZ;
                    cells++;
                    if (newZ - current > maxLift)
                        maxLift = newZ - current;
                }
            }
        }

        return cells;
    }

    /// <summary>Span node → deck Z − drop; approach node → its stamped road Z (no drop).</summary>
    private static float TopZ(UnifiedCrossSection cs, int spanId, float drop)
    {
        if (float.IsNaN(cs.TargetElevation) || float.IsInfinity(cs.TargetElevation))
            return float.NaN;
        return cs.StructureSpanId == spanId ? cs.TargetElevation - drop : cs.TargetElevation;
    }

    /// <summary>1 across the road half-width, smoothstepping to 0 over the terrain-affected range —
    /// the same lateral blend the dip carve / ramp fill use.</summary>
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
