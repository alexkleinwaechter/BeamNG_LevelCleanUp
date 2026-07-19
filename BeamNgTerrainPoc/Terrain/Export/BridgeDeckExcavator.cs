using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Lowers the terrain heightmap wherever it would poke up <b>through</b> a generated bridge deck.
///
/// Bridge cross-sections are excluded from terrain stamping (decision D1/D2), so the natural terrain under
/// a deck keeps its height and can rise above the deck — poking through / around it. For a driving simulator
/// the deck must be the clean driving surface, so the rule is deliberately minimal:
///
///   for every heightmap cell under the deck footprint whose terrain is ABOVE the deck,
///   lower it to just under the deck at THAT station; leave every other cell untouched.
///
/// Because each cross-section carries its own (sloped) deck elevation <i>and</i> bank angle, the cut follows
/// the deck's longitudinal slope and lateral tilt — there is no flat pad and therefore no slope-mismatch kink
/// at the seam, and a banked (curved) deck's low side is shaved to the actual deck surface rather than the
/// centerline (which would leave terrain poking through). Cells already at or below the deck are never raised,
/// so real gaps, ravines and water under the span are preserved.
/// Runs after <see cref="BridgeProfileSolver.RefineSpans"/> so it reads the final deck elevation.
/// </summary>
public static class BridgeDeckExcavator
{
    /// <summary>
    /// How far (m) poking terrain is set below the deck. A hair, just enough that the deck stays the visible
    /// driving surface without z-fighting — the terrain effectively meets the deck underside.
    /// </summary>
    public const float DefaultUndercutMeters = 0.05f;

    /// <summary>Extra lateral reach (m) beyond the deck half-width, so the deck never overhangs an un-shaved lip.</summary>
    public const float DefaultEdgeMarginMeters = 0.5f;

    /// <summary>
    /// Shaves terrain that pokes above any <see cref="BridgeDeckDaeExporter.ShouldGenerateDeck"/> deck.
    /// </summary>
    /// <param name="network">Solved network; bridge cross-sections carry the final deck elevation.</param>
    /// <param name="heightMap">Canonical 2D heightmap [y, x] in pre-base-height units (mutated in place).</param>
    /// <param name="metersPerPixel">Heightmap resolution (same convention as the elevation smoother).</param>
    /// <param name="undercutMeters">Distance poking terrain is set below the deck.</param>
    /// <param name="edgeMarginMeters">Extra lateral reach beyond the deck half-width.</param>
    /// <param name="abutmentOverlapMeters">
    /// Doc 06 §3.2: stations within this distance of a span end keep a ceiling of
    /// <c>deckZ − <paramref name="abutmentOverlapDropMeters"/></c> instead of <c>− undercut</c>, so the
    /// overlap tongue stamped by <see cref="BridgeAbutmentOverlapStamper"/> is not cut back down.
    /// 0 (default) = no exemption, byte-identical legacy behaviour.
    /// </param>
    /// <param name="abutmentOverlapDropMeters">The tongue's drop below the deck surface (doc 06).</param>
    /// <param name="log">Whether to emit a <c>[BRIDGE-EXCAVATE]</c> summary line.</param>
    public static BridgeExcavationResult Excavate(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        float undercutMeters = DefaultUndercutMeters,
        float edgeMarginMeters = DefaultEdgeMarginMeters,
        float abutmentOverlapMeters = 0f,
        float abutmentOverlapDropMeters = 0f,
        bool log = true,
        int[,]? roadSurfaceOwner = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(heightMap);
        if (metersPerPixel <= 0f)
            throw new ArgumentOutOfRangeException(nameof(metersPerPixel));

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);
        var result = new BridgeExcavationResult();

        // Sub-pixel lateral step so the shaved strip has no holes even at coarse resolutions.
        var lateralStep = MathF.Max(0.25f, metersPerPixel * 0.5f);

        var deckGroups = CollectDeckGroups(network);

        foreach (var deckSections in deckGroups)
        {
            var lowered = 0;
            var firstDist = deckSections[0].DistanceAlongSpline;
            var lastDist = deckSections[^1].DistanceAlongSpline;

            // Doc 13: no tongue at a bridge-to-bridge continuation end ⇒ no exemption either.
            var ownerSeg = network.GetSplineById(deckSections[0].OwnerSplineId)
                ?.StructureSegments?.FirstOrDefault(s => s.SpanId == deckSections[0].StructureSpanId);
            var suppressStart = ownerSeg?.StartContinuesOntoDeck == true;
            var suppressEnd = ownerSeg?.EndContinuesOntoDeck == true;

            foreach (var cs in deckSections)
            {
                var deckZ = cs.TargetElevation;
                if (float.IsNaN(deckZ) || float.IsInfinity(deckZ))
                    continue;

                // Doc 06 §3.2: inside the abutment overlap the stamped tongue (deckZ − drop) must survive —
                // shave only above it; the regular daylight undercut applies beyond the overlap.
                var inOverlap = abutmentOverlapMeters > 0f &&
                                ((cs.DistanceAlongSpline - firstDist <= abutmentOverlapMeters && !suppressStart) ||
                                 (lastDist - cs.DistanceAlongSpline <= abutmentOverlapMeters && !suppressEnd));
                var effectiveUndercut = inOverlap ? abutmentOverlapDropMeters : undercutMeters;

                var halfWidth = cs.EffectiveRoadWidth / 2f + edgeMarginMeters;
                var normal = cs.NormalDirection;

                // The deck is banked: its surface tilts laterally with the cross-section's bank angle, so a
                // single centerline ceiling would leave terrain poking through the deck's low side (and
                // over-cut the high side). Match the deck mesh exactly — RoadCrossSection puts the +Normal
                // edge at center + halfWidth·sin(bank) and the −Normal edge at center − halfWidth·sin(bank)
                // — so the deck surface at a lateral offset o along NormalDirection is deckZ + o·sin(bank).
                var bankSlope = MathF.Sin(cs.BankAngleRadians);

                for (var offset = -halfWidth; offset <= halfWidth; offset += lateralStep)
                {
                    var worldX = cs.CenterPoint.X + normal.X * offset;
                    var worldY = cs.CenterPoint.Y + normal.Y * offset;

                    var px = Math.Clamp((int)(worldX / metersPerPixel), 0, mapWidth - 1);
                    var py = Math.Clamp((int)(worldY / metersPerPixel), 0, mapHeight - 1);

                    // Never cut a NEIGHBOURING road's protected surface — only this bridge's own footprint +
                    // bare terrain (self/no-owner). No raster ⇒ legacy behaviour.
                    if (!RoadSurfaceOwnerRaster.CanWrite(roadSurfaceOwner, py, px, cs.OwnerSplineId))
                        continue;

                    // Per-station, per-offset ceiling → the cut tracks both the deck's longitudinal slope
                    // and its lateral bank; no flat pad, no seam kink, no poke-through on the banked low side.
                    var ceiling = deckZ + offset * bankSlope - effectiveUndercut;

                    var current = heightMap[py, px];
                    // Only terrain ABOVE the deck is touched; at/below the deck is left untouched.
                    if (float.IsNaN(current) || float.IsInfinity(current) || current <= ceiling)
                        continue;

                    heightMap[py, px] = ceiling;
                    lowered++;
                    result.CellsLowered++;
                    var cut = current - ceiling;
                    if (cut > result.MaxCutMeters)
                        result.MaxCutMeters = cut;
                }
            }

            if (lowered > 0)
                result.BridgesExcavated++;
        }

        if (log && result.CellsLowered > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-EXCAVATE] bridges={result.BridgesExcavated} cellsLowered={result.CellsLowered} " +
                $"maxCut={result.MaxCutMeters:F2}m undercut={undercutMeters:F2}m");

        return result;
    }

    /// <summary>
    /// Deck-carrying cross-sections, one group per deck — the canonical deck FOOTPRINT shared by every
    /// under-deck terrain pass (excavation, <see cref="BridgeUnderDeckMaterialPainter"/>). Merged-corridor
    /// mode (plan doc 11, Phase 5): a deck is only the tagged span sub-range of a corridor, so group by
    /// (spline, span). Legacy mode: every cross-section of a whole bridge spline.
    /// </summary>
    public static List<List<UnifiedCrossSection>> CollectDeckGroups(UnifiedRoadNetwork network)
    {
        if (network.CrossSections.Any(c => c.StructureSpanId >= 0 && c.StructureSpanType == StructureType.Bridge))
        {
            return network.CrossSections
                .Where(c => c.StructureSpanId >= 0 && c.StructureSpanType == StructureType.Bridge)
                .GroupBy(c => (c.OwnerSplineId, c.StructureSpanId))
                .Select(g => g.OrderBy(c => c.LocalIndex).ToList())
                .ToList();
        }

        return network.Splines
            .Where(BridgeDeckDaeExporter.ShouldGenerateDeck)
            .Select(s => network.GetCrossSectionsForSpline(s.SplineId).ToList())
            .Where(l => l.Count > 0)
            .ToList();
    }
}

/// <summary>Aggregate result of a <see cref="BridgeDeckExcavator.Excavate"/> pass.</summary>
public sealed class BridgeExcavationResult
{
    /// <summary>Number of bridges that had at least one terrain cell lowered.</summary>
    public int BridgesExcavated { get; set; }

    /// <summary>Total heightmap cells lowered.</summary>
    public int CellsLowered { get; set; }

    /// <summary>Largest single downward cut (m).</summary>
    public float MaxCutMeters { get; set; }
}
