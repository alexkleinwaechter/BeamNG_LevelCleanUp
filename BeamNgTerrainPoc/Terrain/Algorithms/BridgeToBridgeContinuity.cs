using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Doc 13 — bridge-to-bridge continuity detection. A physical structure is split across splines
///     (trunk + ramps) and, rarely, across un-consolidated same-spline segments; a span end where the
///     road continues onto ANOTHER deck is NOT a ground abutment, yet it kept getting the full abutment
///     package (3 m non-excluded overlap zone → Phase-4 ground-to-deck embankment pillar, overlap
///     tongue, excavator tongue ceiling) — terrain walls at mid-air deck corners (bridge_904452323,
///     bridge_1546435469). This pass marks such ends on the <see cref="StructureSegment"/> so the three
///     consumers suppress the treatment. Gated per spline on
///     <c>BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression</c>; off ⇒ no flags set ⇒
///     byte-identical.
///
///     <para>Doc 14 extension: foreign-deck landings are additionally RECORDED
///     (<see cref="StructureSegment.StartDeckLanding"/>/<see cref="StructureSegment.EndDeckLanding"/> —
///     landed-on spline + station + junction id) so the profile solver and the deck-seam diagnostics
///     can anchor to the trunk deck surface without re-searching. When junctions are supplied, a
///     junction whose contributors place the span end ON a foreign deck resolves the landing even where
///     the doc-13 radius test misses (904452323's start at j103, just outside halfWidth+1 m); flipping
///     the suppression FLAG from that junction evidence is gated on
///     <c>BridgeRuleSystemOptions.EnableDeckToDeckContinuity</c> (records themselves are inert).</para>
/// </summary>
internal static class BridgeToBridgeContinuity
{
    /// <summary>Beyond the deck half-width, how much lateral slack still counts as ON the deck.
    /// Small on purpose: parallel twin decks (~10 m apart) must not suppress each other's true
    /// shore abutments.</summary>
    internal const float OnDeckMarginMeters = 1.0f;

    /// <summary>
    /// Doc 14: how close (m) to a span boundary a station counts as "at the span end" — beyond it the
    /// deck is considered to CONTINUE THROUGH (interior), which is what makes it the landing authority.
    /// Shared by the landing detection here, the junction authority rule
    /// (<c>UnifiedRoadSmoother.PinOnDeckJunctions</c>) and the profile-solver anchor.
    /// </summary>
    internal const float DeckEndEpsilonMeters = 10f;

    internal static void MarkContinuationEnds(
        IEnumerable<ParameterizedRoadSpline> splines,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        IReadOnlyList<NetworkJunction>? junctions = null)
    {
        var all = splines as IReadOnlyList<ParameterizedRoadSpline> ?? splines.ToList();

        // Opt-in check first — building the deck index is pointless when nobody consumes it.
        if (!all.Any(s => s.Parameters.BridgeRules?.EnableBridgeToBridgeAbutmentSuppression == true))
            return;

        // Deck-section index over ALL splines' bridge segments (segment RANGES, not StructureSpanId
        // tags — the tags are not guaranteed to be set yet when the exclusion marking runs).
        var deckIndex = new List<(Vector2 Center, float HalfWidth, int SplineId, float Station)>();
        foreach (var spline in all)
        {
            if (spline.StructureSegments is not { Count: > 0 }) continue;
            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var sections)) continue;

            foreach (var seg in spline.StructureSegments)
            {
                if (!seg.IsBridge) continue;
                foreach (var c in sections)
                    if (c.DistanceAlongSpline >= seg.StartDistance &&
                        c.DistanceAlongSpline <= seg.EndDistance)
                        deckIndex.Add((c.CenterPoint, c.EffectiveRoadWidth / 2f, spline.SplineId,
                            c.DistanceAlongSpline));
            }
        }

        if (deckIndex.Count == 0) return;

        foreach (var spline in all)
        {
            var rules = spline.Parameters.BridgeRules;
            if (rules?.EnableBridgeToBridgeAbutmentSuppression != true) continue;
            if (!spline.Parameters.MergeStructuresIntoCorridor) continue;
            if (spline.StructureSegments is not { Count: > 0 }) continue;
            if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var sections) ||
                sections.Count == 0) continue;

            var bridgeSegs = spline.StructureSegments.Where(s => s.IsBridge)
                .OrderBy(s => s.StartDistance).ToList();
            var neighbourTolerance = 2f * MathF.Max(0f, rules.AbutmentOverlapMeters);
            var junctionDrivenFlag = rules.EnableDeckToDeckContinuity;

            foreach (var seg in bridgeSegs)
            {
                var (startContinues, startLanding) = ResolveContinuation(
                    spline, seg, seg.StartDistance, bridgeSegs, neighbourTolerance,
                    sections, deckIndex, junctions, junctionDrivenFlag);
                seg.StartContinuesOntoDeck = startContinues;
                seg.StartDeckLanding = startLanding;

                var (endContinues, endLanding) = ResolveContinuation(
                    spline, seg, seg.EndDistance, bridgeSegs, neighbourTolerance,
                    sections, deckIndex, junctions, junctionDrivenFlag);
                seg.EndContinuesOntoDeck = endContinues;
                seg.EndDeckLanding = endLanding;
            }
        }
    }

    /// <summary>
    /// One span end: does the road continue onto another deck, and if onto a FOREIGN one, where?
    /// The returned flag drives the doc-13 abutment suppression; the landing record (doc 14) is set for
    /// every resolved foreign landing regardless of which detector found it. A landing found ONLY via a
    /// junction flips the flag only when <paramref name="junctionDrivenFlag"/> is on (doc-13 output
    /// stays byte-identical otherwise).
    /// </summary>
    private static (bool continues, DeckLandingRecord? landing) ResolveContinuation(
        ParameterizedRoadSpline spline,
        StructureSegment seg,
        float endStation,
        List<StructureSegment> sameSplineBridgeSegs,
        float neighbourTolerance,
        List<UnifiedCrossSection> sections,
        List<(Vector2 Center, float HalfWidth, int SplineId, float Station)> deckIndex,
        IReadOnlyList<NetworkJunction>? junctions,
        bool junctionDrivenFlag)
    {
        // Same-spline neighbour segment within tolerance (un-consolidated same-spline joint) — chain-
        // continuous by construction, no landing record needed.
        foreach (var other in sameSplineBridgeSegs)
        {
            if (ReferenceEquals(other, seg)) continue;
            if (MathF.Abs(other.StartDistance - endStation) <= neighbourTolerance ||
                MathF.Abs(other.EndDistance - endStation) <= neighbourTolerance)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"[BRIDGE-B2B] span={seg.SpanId} spline={spline.SplineId} " +
                    $"{(endStation <= seg.StartDistance ? "start" : "end")} continues into span={other.SpanId} " +
                    "— abutment suppressed");
                return (true, null);
            }
        }

        // Foreign-deck landing, radius test: the end cross-section sits ON another spline's deck
        // footprint. Nearest hit wins so the recorded station is the closest deck section's.
        UnifiedCrossSection? endCs = null;
        var best = float.MaxValue;
        foreach (var c in sections)
        {
            var d = MathF.Abs(c.DistanceAlongSpline - endStation);
            if (d < best)
            {
                best = d;
                endCs = c;
            }
        }

        if (endCs == null) return (false, null);

        DeckLandingRecord? radiusLanding = null;
        var bestGap = float.MaxValue;
        foreach (var (center, halfWidth, splineId, station) in deckIndex)
        {
            if (splineId == spline.SplineId) continue;
            var gap = Vector2.Distance(center, endCs.CenterPoint);
            if (gap <= halfWidth + OnDeckMarginMeters && gap < bestGap)
            {
                bestGap = gap;
                radiusLanding = new DeckLandingRecord(splineId, station, JunctionId: null);
            }
        }

        // Junction-driven landing (doc 14): a junction at this span end whose contributors include a
        // FOREIGN deck resolves the landing exactly (contributor station), even when the radius test
        // misses (station drift put 904452323's start just outside halfWidth+1 m at j103).
        var junctionLanding = FindJunctionLanding(spline, endStation, neighbourTolerance, junctions);

        if (junctionLanding != null)
        {
            var continues = radiusLanding != null || junctionDrivenFlag;
            if (continues)
                TerrainCreationLogger.Current?.Detail(
                    $"[BRIDGE-B2B] span={seg.SpanId} spline={spline.SplineId} " +
                    $"{(endStation <= seg.StartDistance ? "start" : "end")} lands on " +
                    $"spline={junctionLanding.DeckSplineId} via junction={junctionLanding.JunctionId} " +
                    "— abutment suppressed");
            return (continues, junctionLanding);
        }

        if (radiusLanding != null)
        {
            TerrainCreationLogger.Current?.Detail(
                $"[BRIDGE-B2B] span={seg.SpanId} spline={spline.SplineId} " +
                $"{(endStation <= seg.StartDistance ? "start" : "end")} lands on spline={radiusLanding.DeckSplineId} " +
                "— abutment suppressed");
            return (true, radiusLanding);
        }

        return (false, null);
    }

    /// <summary>
    /// Doc 14: resolves a span end's landing from the junction graph. Looks for a junction with a
    /// contributor on THIS spline within <paramref name="tolerance"/> of the end station whose OTHER
    /// contributors include a foreign deck (merged bridge-segment range, or a whole-spline generated
    /// deck). Prefers a contributor the deck CONTINUES THROUGH (interior — the landing authority),
    /// then higher spline priority. Null when junctions are unavailable or nothing matches.
    /// </summary>
    private static DeckLandingRecord? FindJunctionLanding(
        ParameterizedRoadSpline spline,
        float endStation,
        float tolerance,
        IReadOnlyList<NetworkJunction>? junctions)
    {
        if (junctions is not { Count: > 0 })
            return null;

        DeckLandingRecord? bestLanding = null;
        var bestInterior = false;
        var bestPriority = int.MinValue;

        foreach (var junction in junctions)
        {
            var atThisEnd = junction.Contributors.Any(c =>
                c.Spline.SplineId == spline.SplineId &&
                MathF.Abs(c.CrossSection.DistanceAlongSpline - endStation) <= tolerance);
            if (!atThisEnd)
                continue;

            foreach (var c in junction.Contributors)
            {
                if (c.Spline.SplineId == spline.SplineId) continue;
                if (!IsOnDeck(c, out var interior)) continue;

                if (bestLanding == null ||
                    (interior && !bestInterior) ||
                    (interior == bestInterior && c.Spline.Priority > bestPriority))
                {
                    bestLanding = new DeckLandingRecord(
                        c.Spline.SplineId, c.CrossSection.DistanceAlongSpline, junction.JunctionId);
                    bestInterior = interior;
                    bestPriority = c.Spline.Priority;
                }
            }
        }

        return bestLanding;
    }

    /// <summary>
    /// Whether this junction contributor's station sits on a bridge deck of its spline, and whether the
    /// deck continues THROUGH the station (interior, i.e. more than <see cref="DeckEndEpsilonMeters"/>
    /// from the span boundary — the "landed-on" trunk side of a deck-deck merge).
    /// </summary>
    private static bool IsOnDeck(JunctionContributor c, out bool interior)
    {
        interior = false;

        var station = c.CrossSection.DistanceAlongSpline;
        if (c.Spline.StructureSegments is { Count: > 0 })
        {
            foreach (var seg in c.Spline.StructureSegments)
            {
                if (!seg.IsBridge) continue;
                if (station < seg.StartDistance - OnDeckMarginMeters ||
                    station > seg.EndDistance + OnDeckMarginMeters) continue;

                interior = station > seg.StartDistance + DeckEndEpsilonMeters &&
                           station < seg.EndDistance - DeckEndEpsilonMeters;
                return true;
            }
        }

        // Whole-spline generated deck (separated bridge spline): every station is deck; "interior"
        // = the deck continues through the junction (contributor is not a spline endpoint).
        if (BridgeDeckDaeExporter.ShouldGenerateDeck(c.Spline))
        {
            interior = c.IsContinuous;
            return true;
        }

        return false;
    }
}
