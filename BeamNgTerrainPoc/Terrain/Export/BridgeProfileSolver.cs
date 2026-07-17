using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Bridge structural profile solver — replaces the terrain-following elevation of excluded bridge
/// cross-sections with a vertical curve that spans the gap and matches the connected approach roads in
/// height AND grade (G0 + G1). See <c>ai_docs/2026-06-03_bridge_generation/05-bridge-elevation-and-continuity-plan.md</c>.
///
/// This file currently implements <b>Step 1 (diagnostics)</b> and <b>Step 2 (shared connected-road
/// contributor lookup with grade estimation)</b>. The vertical-curve override (Step 3) and the
/// plan-view normal-only seam pass (Step 5) build on the same <see cref="FindConnectedRoadContributor"/>.
///
/// Diagnostics run as a hard decision gate: log the real per-seam numbers on a problem map before
/// building the correctors, so we know whether the artifact is vertical (sag / grade mismatch),
/// positional (XY gap / centerline heading), or orientation (normal skew).
/// </summary>
public static class BridgeProfileSolver
{
    /// <summary>Approach length (m) used to estimate the endpoint longitudinal grade.</summary>
    public const float DefaultGradeSampleLengthMeters = 10f;

    /// <summary>
    /// Cap (m) for the overshoot guard. The actual bulge threshold for a bridge is
    /// <c>min(0.25·L, this)</c>; if the cubic deviates from the chord by more it falls back to a
    /// parabola, then the straight chord (§4.3). Never clamps the approach grades themselves.
    /// </summary>
    public const float DefaultMaxProfileBulgeCapMeters = 4f;

    /// <summary>Deck-above-terrain clearance warn threshold (m). Diagnostic only in v1 (§4.7). Derives
    /// from the single source of truth <see cref="GradeSeparationResolver.DefaultMinClearanceMeters"/>.</summary>
    public const float DefaultMinClearanceMeters = GradeSeparationResolver.DefaultMinClearanceMeters;

    /// <summary>
    /// Max distance (m) the deck may bow BELOW the straight endpoint-to-endpoint chord. A bridge spans —
    /// it must not sag into the gap. When steep, opposed approach grades would dip the cubic deeper than
    /// this, the curve is blended uniformly toward the chord so the deepest dip equals this tolerance
    /// (endpoints stay exact, grade continuity is partially traded). Arching above the chord is untouched.
    /// </summary>
    public const float DefaultMaxSagBelowChordMeters = 1f;

    /// <summary>
    /// Doc 14 (b) anchor plausibility cap: a landing anchor is only applied when the landed-on deck's
    /// surface is within this many meters of the span end's own solved Z. The doc-13 radius test is
    /// plan-view only, so a ramp passing UNDER a deck records a "landing" too (Manhattan 214227: spline
    /// 51's start vs the Brooklyn deck 19 m above it, headingΔ 42.7° — a crossing, not a merge).
    /// Genuine merge drift observed ≤ ~4 m; beyond the cap the end keeps its normal anchor.
    /// </summary>
    public const float MaxLandingAnchorZGapMeters = 6f;

    /// <summary>
    /// Anchor plausibility gap (m) between the landed-on deck surface and the span end — the MINIMUM
    /// over every available authority for where that end is MEANT to be: (1) the end's own solved Z,
    /// (2) the end section's planner deck pin (hard-pin modes; sparse-soft has none), (3) the landing
    /// junction's junction-on-deck PLAN elevation, captured INTO the landing record at creation
    /// (<see cref="DeckLandingRecord.PlannedDeckZ"/> — junction ids are re-assigned by later phases,
    /// so no id lookup here, and <c>HarmonizedElevation</c> is not drift-proof either: the no-blend
    /// endpoint targeting overwrites it from interim profiles). The solved end can drift meters above
    /// the plan when per-pass affine retargeting re-reads the through-deck's interim profile
    /// (Manhattan 111802/114757: spline 55/56 end 40,9 while its landing junction was planned at 34,3
    /// and the final deck sat at 34,7 — a 6,3 m own-Z "gap" that is pure solver drift the anchor
    /// exists to repair). Genuine plan-view crossings stay capped: every authority they have sits far
    /// from the crossed deck's surface. Returns +∞ when no authority is finite — callers keep the
    /// legacy "no own Z → anchor unconditionally" behaviour.
    /// </summary>
    private static float MinLandingAnchorGap(
        DeckLandingRecord landing,
        UnifiedCrossSection endSection,
        float deckZ)
    {
        var gap = float.PositiveInfinity;

        if (IsFinite(endSection.TargetElevation))
            gap = MathF.Min(gap, MathF.Abs(deckZ - endSection.TargetElevation));

        if (endSection.PinnedElevation is { } pin && IsFinite(pin))
            gap = MathF.Min(gap, MathF.Abs(deckZ - pin));

        if (landing.PlannedDeckZ is { } planned && IsFinite(planned))
            gap = MathF.Min(gap, MathF.Abs(deckZ - planned));

        return gap;
    }

    /// <summary>
    /// Doc 15 (a): lateral slack (m) beyond the landed-on deck half-width within which a landing-span
    /// point still counts as overlapping the deck footprint (the conformance-zone membership test).
    /// </summary>
    internal const float DeckOverlapLateralMarginMeters = 0.5f;

    /// <summary>
    /// Doc 15 (a): safety cap (m) on the conformance walk from the landed end; the REAL terminator is
    /// the footprint test (the walk stops at the first section outside the landed-on deck). The
    /// per-span cap is min(span/2, this). The doc's original 60 m ("a merge overlap is an end
    /// phenomenon") underestimated shallow merges — Manhattan run 230330 capped FIVE merges mid-
    /// overlap (58→2 et al., "201 station(s) over 59,8m"), leaving overlapping decks that diverge up
    /// to the ease delta INSIDE the shared roadway, where the coplanarity parapet mask then correctly
    /// re-erects the wall mid-merge.
    /// </summary>
    internal const float DeckOverlapMaxWalkMeters = 250f;

    /// <summary>
    /// Doc 15 (a): minimum ease-out run (m) past the last overlapping section (actual run =
    /// max(deck width, this, delta-scaled), capped so the far anchor is never moved).
    /// </summary>
    internal const float DeckOverlapMinTransitionMeters = 10f;

    /// <summary>
    /// Doc 15 (a): ease-out run gained per meter of boundary correction. The smoothstep's peak slope is
    /// 1.5·Δ/run, so 10 m/m bounds the ADDED grade of the ease to ≈15 % — a steep merge (large Δ where
    /// conformance ends) stretches the transition instead of folding the full correction into the fixed
    /// minimum run (which would be exactly the new kink the ease exists to prevent).
    /// </summary>
    internal const float DeckOverlapEaseRunPerDeltaMeter = 10f;

    /// <summary>
    /// Doc 15: projection window (m) around the station hint when sampling a deck surface at an
    /// arbitrary point — keeps the polyline projection local on corridors that loop back through the map.
    /// </summary>
    private const float DeckOverlapStationWindowMeters = 120f;

    /// <summary>
    /// A connected non-bridge approach contributor at a bridge endpoint, carrying everything the
    /// vertical (Z + grade) and plan-view (tangent + normal + width) passes need. Unlike the old
    /// elevation-only lookup, this returns a single best contributor — a pose/grade cannot be averaged.
    /// </summary>
    /// <param name="RoadSplineId">Spline id of the chosen approach road.</param>
    /// <param name="Elevation">Approach centerline target elevation at the junction (m).</param>
    /// <param name="GradeAlongBridge">
    /// Longitudinal grade dZ/ds expressed in the bridge's +s (increasing-distance) direction at this
    /// endpoint, so it can be used directly as the Hermite endpoint slope. Positive = rising in +s.
    /// </param>
    /// <param name="ForwardTangent">Approach plan tangent (unit), pointing along the approach's +distance.</param>
    /// <param name="Normal">Approach lateral normal (unit, right-hand).</param>
    /// <param name="Width">Approach effective road width (m).</param>
    public sealed record BridgeEndpointContributor(
        int RoadSplineId,
        float Elevation,
        float GradeAlongBridge,
        Vector2 ForwardTangent,
        Vector2 Normal,
        float Width);

    /// <summary>
    /// An interior minimum-elevation constraint on a bridge span (feature E-A, decision D-4): at the given
    /// distance along the bridge spline the deck must sit at or above <paramref name="MinZ"/> (e.g. to
    /// clear a high-class road that may not be dipped under it). The solver honours it by adding a smooth
    /// interior arch — never by clamping the approach grades (standing no-grade-clamp feedback).
    /// </summary>
    public readonly record struct BridgeInteriorConstraint(
        int BridgeSplineId,
        float DistanceAlongSpline,
        float MinZ);

    /// <summary>One bridge endpoint ("seam") diagnostic record. Returned for tests; also logged.</summary>
    public sealed class BridgeSeamDiagnostic
    {
        public int BridgeSplineId { get; init; }
        public bool IsStart { get; init; }
        public bool Connected { get; init; }
        public int? RoadSplineId { get; init; }

        /// <summary>Bridge endpoint current (smoothed-terrain) elevation, m.</summary>
        public float BridgeEndElevation { get; init; }
        /// <summary>Connected approach elevation at the junction, m. NaN if unconnected.</summary>
        public float ApproachElevation { get; init; }
        /// <summary>approach − bridge endpoint elevation, m.</summary>
        public float ZGapMeters { get; init; }

        /// <summary>Bridge endpoint current grade in +s direction (dZ/ds).</summary>
        public float BridgeGrade { get; init; }
        /// <summary>Approach grade in bridge +s direction (dZ/ds). NaN if unconnected.</summary>
        public float ApproachGrade { get; init; }
        /// <summary>|atan(bridgeGrade) − atan(approachGrade)| in degrees.</summary>
        public float GradeDeltaDegrees { get; init; }

        /// <summary>Acute angle between bridge and approach plan tangents, degrees.</summary>
        public float HeadingDeltaDegrees { get; init; }
        /// <summary>Acute angle between bridge and approach lateral normals, degrees.</summary>
        public float NormalDeltaDegrees { get; init; }
        /// <summary>Plan-view distance between bridge endpoint and approach endpoint centers, m.</summary>
        public float XyGapMeters { get; init; }
        /// <summary>bridge width − approach width, m.</summary>
        public float WidthDeltaMeters { get; init; }
    }

    /// <summary>
    /// Computes (and optionally logs) seam diagnostics for every generated bridge endpoint in the
    /// network. Pure read-only — does not mutate the network.
    /// </summary>
    public static IReadOnlyList<BridgeSeamDiagnostic> DiagnoseSeams(
        UnifiedRoadNetwork network,
        float gradeSampleLengthMeters = DefaultGradeSampleLengthMeters,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);

        var diagnostics = new List<BridgeSeamDiagnostic>();
        var bridgeSplines = network.Splines.Where(BridgeDeckDaeExporter.ShouldGenerateDeck).ToList();

        foreach (var spline in bridgeSplines)
        {
            var sections = network.GetCrossSectionsForSpline(spline.SplineId)
                .OrderBy(c => c.LocalIndex).ToList();
            if (sections.Count < 2)
                continue;

            foreach (var isStart in new[] { true, false })
            {
                var bridgeEnd = isStart ? sections[0] : sections[^1];
                var bridgeForwardTangent = SafeNormalize(bridgeEnd.TangentDirection);
                // Bridge endpoint grade measured in the bridge's +s (increasing-distance) direction.
                var bridgeGrade = EstimateForwardGrade(sections, atStart: isStart, gradeSampleLengthMeters);

                var contributor = FindConnectedRoadContributor(network, spline.SplineId, isStart, gradeSampleLengthMeters);

                BridgeSeamDiagnostic d;
                if (contributor == null)
                {
                    d = new BridgeSeamDiagnostic
                    {
                        BridgeSplineId = spline.SplineId,
                        IsStart = isStart,
                        Connected = false,
                        BridgeEndElevation = bridgeEnd.TargetElevation,
                        ApproachElevation = float.NaN,
                        ZGapMeters = float.NaN,
                        BridgeGrade = bridgeGrade,
                        ApproachGrade = float.NaN,
                        GradeDeltaDegrees = float.NaN,
                        HeadingDeltaDegrees = float.NaN,
                        NormalDeltaDegrees = float.NaN,
                        XyGapMeters = float.NaN,
                        WidthDeltaMeters = float.NaN
                    };
                }
                else
                {
                    // Orient approach tangent into the same direction as the bridge tangent so the
                    // acute heading deviation (the visible kink) is what we report.
                    var sign = Vector2.Dot(contributor.ForwardTangent, bridgeForwardTangent) < 0 ? -1f : 1f;
                    var approachTangentAligned = contributor.ForwardTangent * sign;
                    var approachNormalAligned = contributor.Normal * sign;

                    d = new BridgeSeamDiagnostic
                    {
                        BridgeSplineId = spline.SplineId,
                        IsStart = isStart,
                        Connected = true,
                        RoadSplineId = contributor.RoadSplineId,
                        BridgeEndElevation = bridgeEnd.TargetElevation,
                        ApproachElevation = contributor.Elevation,
                        ZGapMeters = contributor.Elevation - bridgeEnd.TargetElevation,
                        BridgeGrade = bridgeGrade,
                        ApproachGrade = contributor.GradeAlongBridge,
                        GradeDeltaDegrees = MathF.Abs(GradeToDegrees(bridgeGrade) - GradeToDegrees(contributor.GradeAlongBridge)),
                        HeadingDeltaDegrees = AngleDegreesBetween(bridgeForwardTangent, approachTangentAligned),
                        NormalDeltaDegrees = AngleDegreesBetween(SafeNormalize(bridgeEnd.NormalDirection), approachNormalAligned),
                        XyGapMeters = Vector2.Distance(bridgeEnd.CenterPoint, ApproachEndpointCenter(network, contributor.RoadSplineId, bridgeEnd.CenterPoint)),
                        WidthDeltaMeters = bridgeEnd.EffectiveRoadWidth - contributor.Width
                    };
                }

                diagnostics.Add(d);
                if (log)
                    TerrainCreationLogger.Current?.InfoFileOnly(FormatSeam(d));
            }
        }

        if (log && diagnostics.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(FormatSummary(diagnostics));

        return diagnostics;
    }

    /// <summary>One deck-to-deck seam (doc 14 d): a span end that continues onto another deck.</summary>
    public sealed class DeckSeamDiagnostic
    {
        public int SplineId { get; init; }
        public int SpanId { get; init; }
        public bool IsStart { get; init; }
        public int DeckSplineId { get; init; }
        public float DeckStation { get; init; }
        public int? JunctionId { get; init; }

        /// <summary>Landing span end's final centerline Z (m).</summary>
        public float EndElevation { get; init; }

        /// <summary>Landed-on deck's final SURFACE Z at the landing station + lateral offset (m).</summary>
        public float DeckSurfaceElevation { get; init; }

        /// <summary>end − deck surface (m) — the step a vehicle hits at the merge. The doc-14 regression metric.</summary>
        public float ZGapMeters { get; init; }

        /// <summary>|deck-end grade − deck-surface grade along the span +s| in degrees.</summary>
        public float GradeDeltaDegrees { get; init; }

        /// <summary>Acute plan angle between the span end tangent and the landed-on deck tangent.</summary>
        public float HeadingDeltaDegrees { get; init; }

        /// <summary>Doc 15 §5: landing-span stations (walked from the landed end) still overlapping the
        /// landed-on deck footprint.</summary>
        public int OverlapStations { get; init; }

        /// <summary>
        /// Doc 15 §5: max |landing surface − deck surface| (m) over ALL overlapping stations ×
        /// {center, left, right} — the AREA metric (<see cref="ZGapMeters"/> covers only the end
        /// center). Baseline (flag off) shows the real step across the gore; ≈0 once
        /// <c>EnableSeamlessDeckOverlap</c> has conformed the zone.
        /// </summary>
        public float OverlapMaxGapMeters { get; init; }
    }

    /// <summary>
    /// Doc 14 (d) — deck-to-deck seam diagnostics: one record per span end with a recorded deck landing
    /// (<see cref="StructureSegment.StartDeckLanding"/>/<see cref="StructureSegment.EndDeckLanding"/>),
    /// measuring the FINAL solved profiles (call after <see cref="RefineSpans"/>). Turns the merge kink
    /// into a measurable regression metric: 135439 baseline expects 58's end at zGap ≈ +1.5 m; with
    /// <c>EnableDeckToDeckContinuity</c> the landing anchor must bring it to ≈ 0. Read-only; runs off the
    /// landing RECORDS, which exist whenever the doc-13 suppression flag marked the ends — so the
    /// baseline (fix off) is measurable too.
    /// </summary>
    public static IReadOnlyList<DeckSeamDiagnostic> DiagnoseDeckToDeckSeams(
        UnifiedRoadNetwork network,
        float gradeSampleLengthMeters = DefaultGradeSampleLengthMeters,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);

        var seams = new List<DeckSeamDiagnostic>();
        foreach (var spline in network.Splines)
        {
            if (spline.StructureSegments is not { Count: > 0 }) continue;

            foreach (var seg in spline.StructureSegments)
            {
                if (!seg.IsBridge) continue;
                if (seg.StartDeckLanding == null && seg.EndDeckLanding == null) continue;

                var spanSections = network.GetCrossSectionsForSpline(spline.SplineId)
                    .Where(c => c.StructureSpanId == seg.SpanId)
                    .OrderBy(c => c.LocalIndex)
                    .ToList();
                if (spanSections.Count < 2) continue;

                foreach (var isStart in new[] { true, false })
                {
                    var landing = isStart ? seg.StartDeckLanding : seg.EndDeckLanding;
                    if (landing == null) continue;

                    var endSection = isStart ? spanSections[0] : spanSections[^1];
                    if (!IsFinite(endSection.TargetElevation)) continue;
                    if (!TrySampleDeckSurface(network, landing, endSection, gradeSampleLengthMeters, out var deck))
                        continue;

                    var endGrade = EstimateForwardGrade(spanSections, atStart: isStart, gradeSampleLengthMeters);
                    var spanTangent = SafeNormalize(endSection.TangentDirection);
                    var sign = Vector2.Dot(deck.deckTangent, spanTangent) < 0 ? -1f : 1f;
                    var (overlapStations, overlapMaxGap) =
                        MeasureDeckOverlapGap(network, spanSections, isStart, landing);

                    var d = new DeckSeamDiagnostic
                    {
                        SplineId = spline.SplineId,
                        SpanId = seg.SpanId,
                        IsStart = isStart,
                        DeckSplineId = landing.DeckSplineId,
                        DeckStation = landing.DeckStation,
                        JunctionId = landing.JunctionId,
                        EndElevation = endSection.TargetElevation,
                        DeckSurfaceElevation = deck.z,
                        ZGapMeters = endSection.TargetElevation - deck.z,
                        GradeDeltaDegrees = MathF.Abs(GradeToDegrees(endGrade) - GradeToDegrees(deck.grade)),
                        HeadingDeltaDegrees = AngleDegreesBetween(spanTangent, deck.deckTangent * sign),
                        OverlapStations = overlapStations,
                        OverlapMaxGapMeters = overlapMaxGap
                    };
                    seams.Add(d);

                    if (log)
                        TerrainCreationLogger.Current?.InfoFileOnly(
                            $"[BRIDGE-PROFILE] deck-seam spline={d.SplineId} span={d.SpanId} " +
                            $"{(d.IsStart ? "start" : "end")} deck={d.DeckSplineId} station={d.DeckStation:F1}m " +
                            $"z={d.EndElevation:F2} deckZ={d.DeckSurfaceElevation:F2} zGap={d.ZGapMeters:F2} " +
                            $"gradeΔ={d.GradeDeltaDegrees:F1}deg headingΔ={d.HeadingDeltaDegrees:F1}deg" +
                            (d.OverlapStations > 0
                                ? $" overlapStations={d.OverlapStations} overlapMaxGap={d.OverlapMaxGapMeters:F2}m"
                                : "") +
                            (d.JunctionId is { } j ? $" junction={j}" : "") +
                            (MathF.Abs(d.ZGapMeters) > MaxLandingAnchorZGapMeters
                                ? " (exceeds anchor cap — crossing, not a merge)"
                                : ""));
                }
            }
        }

        if (log && seams.Count > 0)
        {
            // Beyond the anchor cap the "seam" is a plan-view crossing the anchor deliberately skips —
            // report it separately so the merge metric stays readable.
            var merges = seams.Where(s => MathF.Abs(s.ZGapMeters) <= MaxLandingAnchorZGapMeters).ToList();
            var maxZGap = merges.Count > 0 ? merges.Max(s => MathF.Abs(s.ZGapMeters)) : 0f;
            var maxGrade = merges.Count > 0 ? merges.Max(s => s.GradeDeltaDegrees) : 0f;
            var maxOverlapGap = merges.Count > 0 ? merges.Max(s => s.OverlapMaxGapMeters) : 0f;
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-PROFILE] deck-seam summary seams={seams.Count} " +
                $"maxZGap={maxZGap:F2}m maxGradeΔ={maxGrade:F1}deg " +
                $"overlapMaxGap={maxOverlapGap:F2}m " +
                $"over0.25m={merges.Count(s => MathF.Abs(s.ZGapMeters) > 0.25f)} " +
                $"crossings={seams.Count - merges.Count}");
        }

        return seams;
    }

    /// <summary>
    /// Re-curves each bridge span with a smooth G0+G1 vertical curve fitted to the (now elevated) approaches
    /// and captures its <c>BridgeSpanSnapshot</c>. Replaces the terrain-following / sagging chain-solve result
    /// over the deck and re-derives the dependent banked edge elevations.
    ///
    /// <para><b>Demoted in plan doc 14 Phase D.</b> On a merged corridor the deck height is no longer this
    /// pass's job: <c>BridgeElevationPlanner</c> pinned the span deck Z pre-smoothing (Phase 1.85) and the
    /// smoother already grew the rising approach ramps to it. This pass only SMOOTHS that pinned, ramp-matched
    /// deck into a clean curve and snapshots it — the deck-Z decision moved upstream to the rule engine. The
    /// legacy whole-spline branch (flag off) is unchanged and still resolves its own deck height + honours the
    /// priority-veto interior constraints (retired in Phase F).</para>
    ///
    /// Runs as a single network-level pass and MUST be called before DecalRoad generation and deck export
    /// so both consumers read the same corrected elevation (this is the single source of truth that removes
    /// the old export-time endpoint band-aid and the deck/marking divergence — plan §1.3, §4.1).
    ///
    /// Because the curve is derived only from the two approach endpoints (whose elevations come from the
    /// approaches' own chains, not the bridge's), it also rescues bridges that never joined an elevation
    /// chain — overwriting NaN/garbage sections with a clean span as long as one end is connected (§4.6).
    /// </summary>
    /// <param name="network">The solved unified road network (mutated in place).</param>
    /// <param name="gradeSampleLengthMeters">Approach length used to estimate endpoint grade (§4.2).</param>
    /// <param name="maxProfileBulgeCapMeters">Overshoot-guard cap; threshold = min(0.25·L, this) (§4.3).</param>
    /// <param name="minClearanceMeters">Deck-above-terrain warn threshold for the diagnostic (§4.7).</param>
    /// <param name="interiorConstraints">
    /// Per-crossing interior minimum-Z clearance constraints (legacy whole-spline mode only; feature E-A, D-4).
    /// When the deck's natural curve dips below a constraint, a smooth interior arch lifts it without clamping
    /// any grade. Null/empty → endpoint-only behaviour. Merged corridors pass null — the planner's pin already
    /// raised the deck, so there is nothing left for the solver to lift.
    /// </param>
    /// <param name="log">Whether to emit <c>[BRIDGE-PROFILE]</c> per-bridge + summary log lines.</param>
    public static BridgeProfileResult RefineSpans(
        UnifiedRoadNetwork network,
        float gradeSampleLengthMeters = DefaultGradeSampleLengthMeters,
        float maxProfileBulgeCapMeters = DefaultMaxProfileBulgeCapMeters,
        float minClearanceMeters = DefaultMinClearanceMeters,
        float maxSagBelowChordMeters = DefaultMaxSagBelowChordMeters,
        IReadOnlyList<BridgeInteriorConstraint>? interiorConstraints = null,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);

        var result = new BridgeProfileResult();

        var constraintsBySpline = interiorConstraints is { Count: > 0 }
            ? interiorConstraints.GroupBy(c => c.BridgeSplineId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<BridgeInteriorConstraint>)g.ToList())
            : null;

        // Merged-corridor mode (plan doc 11, Phase 4): bridge spans were tagged on cross-sections in Phase 3.
        // Re-home the solver onto each (spline, span): the approach endpoints are now the IN-SPLINE neighbours
        // just outside the span — no fragile junction walk — and only the span sections are overridden, so the
        // road keeps its chain elevation and continuity is structural. Each span is captured into BridgeSpans.
        var spanKeys = network.CrossSections
            .Where(c => c.StructureSpanId >= 0)
            .Select(c => (SplineId: c.OwnerSplineId, SpanId: c.StructureSpanId))
            .Distinct()
            .OrderBy(k => k.SplineId).ThenBy(k => k.SpanId)
            .ToList();

        // Doc 14 (b): a landing span's end anchors to the trunk deck it lands on, so the trunk's
        // profile must be FINAL first. Ordered by the LANDING DEPENDENCY GRAPH, not by priority —
        // Manhattan run 214227 refuted the doc's priority assumption: the Brooklyn Bridge trunk
        // (p9000) is OUTRANKED by its own ramps (p9500), so ramp 51 anchored to the trunk's
        // pre-refine surface (32.25 where the final deck is 51.12). Circular landings (A on B,
        // B on A) can't be ordered — warn, first-solved re-anchors in the second pass below.
        var deckToDeck = network.Splines.Any(s => s.Parameters.BridgeRules?.EnableDeckToDeckContinuity == true);
        if (deckToDeck)
        {
            spanKeys = OrderSpansByLandingDependencies(network, spanKeys);
            WarnOnCircularDeckLandings(network);
        }

        if (spanKeys.Count > 0)
        {
            network.BridgeSpans.Clear();
            var appIndexBySpan = new Dictionary<(int SplineId, int SpanId), int>();
            foreach (var (splineId, spanId) in spanKeys)
            {
                IReadOnlyList<BridgeInteriorConstraint>? perBridge = null;
                constraintsBySpline?.TryGetValue(splineId, out perBridge);

                var app = ApplyToSpan(network, splineId, spanId, gradeSampleLengthMeters,
                    maxProfileBulgeCapMeters, minClearanceMeters, maxSagBelowChordMeters, perBridge);
                if (app == null)
                    continue;

                appIndexBySpan[(splineId, spanId)] = result.Applications.Count;
                result.Applications.Add(app);
                if (log)
                    TerrainCreationLogger.Current?.InfoFileOnly(FormatApplication(app));
            }

            // Doc 14 (b) cycle re-pass: inside a landing CYCLE some span necessarily anchored before
            // its target was final (220209: 3-end +1.44 against 4's pre-refine deck). Re-solve exactly
            // the spans whose landing-target spline was refined after them — the targets are final
            // now, and the span's own road anchors are unchanged, so the re-solve is a pure re-anchor.
            // The snapshot is replaced, never duplicated.
            if (deckToDeck)
            {
                var lastRankBySpline = spanKeys.Select((k, i) => (k.SplineId, Rank: i))
                    .GroupBy(x => x.SplineId)
                    .ToDictionary(g => g.Key, g => g.Max(x => x.Rank));
                for (var i = 0; i < spanKeys.Count; i++)
                {
                    var (splineId, spanId) = spanKeys[i];
                    var seg = network.GetSplineById(splineId)?.StructureSegments?
                        .FirstOrDefault(s => s.SpanId == spanId);
                    if (seg == null) continue;

                    var stale =
                        (seg.StartDeckLanding is { } sl &&
                         lastRankBySpline.TryGetValue(sl.DeckSplineId, out var ra) && ra > i) ||
                        (seg.EndDeckLanding is { } el &&
                         lastRankBySpline.TryGetValue(el.DeckSplineId, out var rb) && rb > i);
                    if (!stale) continue;

                    network.BridgeSpans.RemoveAll(s => s.SplineId == splineId && s.SpanId == spanId);
                    IReadOnlyList<BridgeInteriorConstraint>? perBridge = null;
                    constraintsBySpline?.TryGetValue(splineId, out perBridge);
                    var app = ApplyToSpan(network, splineId, spanId, gradeSampleLengthMeters,
                        maxProfileBulgeCapMeters, minClearanceMeters, maxSagBelowChordMeters, perBridge);
                    if (app == null) continue;

                    if (appIndexBySpan.TryGetValue((splineId, spanId), out var idx))
                        result.Applications[idx] = app;
                    else
                        result.Applications.Add(app);
                    if (log)
                        TerrainCreationLogger.Current?.InfoFileOnly(
                            $"[BRIDGE-PROFILE] re-solve spline={splineId} span={spanId} " +
                            $"(landing target refined later — cycle): {FormatApplication(app)}");
                }
            }

            if (log && result.Applications.Count > 0)
                TerrainCreationLogger.Current?.InfoFileOnly(FormatApplySummary(result));

            return result;
        }

        // Legacy whole-spline mode (flag off): the bridge IS its own spline; the approach endpoints come from
        // the junction walk. Byte-identical to the pre-refactor behaviour.
        var bridges = network.Splines.Where(BridgeDeckDaeExporter.ShouldGenerateDeck).ToList();

        foreach (var spline in bridges)
        {
            IReadOnlyList<BridgeInteriorConstraint>? perBridge = null;
            constraintsBySpline?.TryGetValue(spline.SplineId, out perBridge);

            var app = ApplyToBridge(network, spline.SplineId, gradeSampleLengthMeters,
                maxProfileBulgeCapMeters, minClearanceMeters, maxSagBelowChordMeters, perBridge);
            if (app == null)
                continue;

            result.Applications.Add(app);
            if (log)
                TerrainCreationLogger.Current?.InfoFileOnly(FormatApplication(app));
        }

        if (log && result.Applications.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(FormatApplySummary(result));

        return result;
    }

    /// <summary>
    /// Merged-corridor span solver (plan doc 11 §4.5). Overrides ONLY the cross-sections of one bridge span
    /// (tagged <see cref="UnifiedCrossSection.StructureSpanId"/> == <paramref name="spanId"/>) with a smooth
    /// G0+G1 vertical curve fitted to the IN-SPLINE neighbours just outside the span — the road sections kept
    /// at their chain elevation. Because the deck curve passes exactly through (and matches the grade of) the
    /// adjacent road sections, continuity at each abutment is structural; there is no junction walk. The
    /// finalised span geometry is captured into <c>network.BridgeSpans</c> before any heightmap carve.
    /// </summary>
    private static BridgeProfileApplication? ApplyToSpan(
        UnifiedRoadNetwork network,
        int splineId,
        int spanId,
        float gradeSampleLengthMeters,
        float maxProfileBulgeCapMeters,
        float minClearanceMeters,
        float maxSagBelowChordMeters,
        IReadOnlyList<BridgeInteriorConstraint>? interiorConstraints = null)
    {
        var allSections = network.GetCrossSectionsForSpline(splineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (allSections.Count < 2)
            return null;

        var spanSections = allSections.Where(c => c.StructureSpanId == spanId).ToList();
        if (spanSections.Count < 1)
            return null;

        var firstLocal = spanSections[0].LocalIndex;
        var lastLocal = spanSections[^1].LocalIndex;

        // The approach endpoints are the in-spline neighbours just outside the (contiguous) span.
        var roadBefore = allSections.Where(c => c.LocalIndex < firstLocal).ToList();
        var roadAfter = allSections.Where(c => c.LocalIndex > lastLocal).ToList();

        var startIsolated = roadBefore.Count == 0;
        var endIsolated = roadAfter.Count == 0;

        // Doc 14 (b): a span end recorded as landing on another deck anchors to THAT deck's surface —
        // the trunk deck is the approach truth there (solve order made it final first). Resolved before
        // the isolation rule so a spline-covering span with landings is still refined.
        var spline = network.GetSplineById(splineId);
        var d2dSeg = spline?.Parameters.BridgeRules?.EnableDeckToDeckContinuity == true
            ? spline?.StructureSegments?.FirstOrDefault(s => s.SpanId == spanId)
            : null;
        var startLanding = d2dSeg?.StartDeckLanding;
        var endLanding = d2dSeg?.EndDeckLanding;

        // A span covering the whole spline (no road either side) has no approach truth — leave it terrain-
        // following, mirroring the legacy "both ends isolated" rule (§4.4).
        if (startIsolated && endIsolated && startLanding == null && endLanding == null)
            return NotApplied(splineId, false, false, 0f, "span covers the whole spline — both ends isolated, left untouched");

        // Start anchor: the road section just before the span (its chain Z + the grade approaching the span),
        // or the span's own first section when the span touches the spline start (isolated-end fallback, §4.4).
        float z0, g0, s0;
        if (!startIsolated)
        {
            var anchor = roadBefore[^1];
            s0 = anchor.DistanceAlongSpline;
            z0 = anchor.TargetElevation;
            g0 = EstimateForwardGrade(roadBefore, atStart: false, gradeSampleLengthMeters);
        }
        else
        {
            var anchor = spanSections[0];
            s0 = anchor.DistanceAlongSpline;
            z0 = anchor.TargetElevation;
            g0 = EstimateForwardGrade(spanSections, atStart: true, gradeSampleLengthMeters);
        }

        float z1, g1, s1;
        if (!endIsolated)
        {
            var anchor = roadAfter[0];
            s1 = anchor.DistanceAlongSpline;
            z1 = anchor.TargetElevation;
            g1 = EstimateForwardGrade(roadAfter, atStart: true, gradeSampleLengthMeters);
        }
        else
        {
            var anchor = spanSections[^1];
            s1 = anchor.DistanceAlongSpline;
            z1 = anchor.TargetElevation;
            g1 = EstimateForwardGrade(spanSections, atStart: false, gradeSampleLengthMeters);
        }

        // Doc 14 (b): landing anchors override — z = trunk deck surface at the landing station and
        // lateral offset (center Z + offset·sin(bank)), grade = the deck surface's directional
        // derivative along this span's +s. The Hermite below then ends ON the trunk deck (G0) with its
        // slope (G1) instead of re-curving from this span's own approach (135439: 58 end z=26.43 in
        // isolation vs trunk 24.90 → the 1.5 m step at j106). The plausibility cap rejects plan-view
        // false positives — a deck the span merely passes under/over is not a merge.
        var startLanded = false;
        var endLanded = false;
        var landingSkips = new List<string>();
        if (startLanding != null &&
            TrySampleDeckSurface(network, startLanding, spanSections[0], gradeSampleLengthMeters, out var la0))
        {
            var gap0 = MinLandingAnchorGap(startLanding, spanSections[0], la0.z);
            if (!float.IsPositiveInfinity(gap0) && gap0 > MaxLandingAnchorZGapMeters)
            {
                landingSkips.Add(
                    $"start landing on spline={startLanding.DeckSplineId} skipped " +
                    $"(deck {la0.z:F1} vs end {spanSections[0].TargetElevation:F1} gap {gap0:F1} " +
                    $"exceeds {MaxLandingAnchorZGapMeters:F0}m — crossing, not a merge)");
            }
            else
            {
                s0 = spanSections[0].DistanceAlongSpline;
                z0 = la0.z;
                g0 = la0.grade;
                startLanded = true;
            }
        }

        if (endLanding != null &&
            TrySampleDeckSurface(network, endLanding, spanSections[^1], gradeSampleLengthMeters, out var la1))
        {
            var gap1 = MinLandingAnchorGap(endLanding, spanSections[^1], la1.z);
            if (!float.IsPositiveInfinity(gap1) && gap1 > MaxLandingAnchorZGapMeters)
            {
                landingSkips.Add(
                    $"end landing on spline={endLanding.DeckSplineId} skipped " +
                    $"(deck {la1.z:F1} vs end {spanSections[^1].TargetElevation:F1} gap {gap1:F1} " +
                    $"exceeds {MaxLandingAnchorZGapMeters:F0}m — crossing, not a merge)");
            }
            else
            {
                s1 = spanSections[^1].DistanceAlongSpline;
                z1 = la1.z;
                g1 = la1.grade;
                endLanded = true;
            }
        }

        // Doc 15: expose the merge-vs-crossing decision to the mesh layer (parapet openings key off
        // it — a cap-skipped crossing keeps full parapets). Re-written on every solve, so the cycle
        // re-pass refreshes it.
        if (d2dSeg != null)
        {
            d2dSeg.StartDeckLandingApplied = startLanded;
            d2dSeg.EndDeckLandingApplied = endLanded;
        }

        var startConnected = !startIsolated || startLanded;
        var endConnected = !endIsolated || endLanded;

        var length = s1 - s0;
        if (length <= 0.01f)
            return NotApplied(splineId, startConnected, endConnected, length, "degenerate span length — left untouched");

        // A6.5 (V2 review P0-1, doc 16 §3): the planner pinned this span's deck pre-smoothing and the smoother
        // hard-held it through every iteration — re-curving the deck from the (lower) approach anchors here
        // would throw the pinned viaduct away and sag it to a chord. Skip the override for pinned spans; keep
        // the banked-edge recompute and the snapshot capture from the held elevations. Flag-gated so today's
        // merged output stays byte-identical until render-validated.
        if (spline?.Parameters.BridgeRules?.EnablePinnedDeckProfile == true &&
            spanSections.Any(c => c.PinnedElevation.HasValue))
            return ApplyPinnedSpan(network, splineId, spanId, spanSections,
                s0, z0, g0, s1, z1, g1, length, startConnected, endConnected, minClearanceMeters);

        // Unchained rescue (§4.6): extend a straight grade line from the connected end if one end has no Z.
        var rescued = spanSections.Any(c => !IsFinite(c.TargetElevation));
        var v0 = IsFinite(z0);
        var v1 = IsFinite(z1);
        if (!v0 && v1) { g0 = g1; z0 = z1 - g1 * length; v0 = true; rescued = true; }
        if (!v1 && v0) { g1 = g0; z1 = z0 + g0 * length; v1 = true; rescued = true; }
        if (!v0 || !v1)
            return NotApplied(splineId, startConnected, endConnected, length,
                "no finite endpoint elevation available — left untouched");

        // Curve over the span sections, parameterised relative to the start anchor s0 (so P(0)=z0 at the road
        // before the span and P(length)=z1 at the road after — the abutments).
        var sLocals = spanSections.Select(c => c.DistanceAlongSpline - s0).ToArray();
        var (profile, curve, maxBulge, sagCapFactor) =
            SelectCurve(sLocals, length, z0, z1, g0, g1, maxProfileBulgeCapMeters, maxSagBelowChordMeters);

        var interiorLift = ComputeInteriorLift(profile, interiorConstraints, s0, length);
        if (interiorLift > 0f)
        {
            var baseProfile = profile;
            var spanLength = length;
            profile = s =>
            {
                var t = s / spanLength;
                return baseProfile(s) + interiorLift * 16f * t * t * (1f - t) * (1f - t);
            };
            float Chord(float s) => z0 + (z1 - z0) * (s / length);
            maxBulge = MaxDeviation(sLocals, profile, Chord);
        }

        // Seam kink (diagnostic): the deck's grade at each abutment vs the approach grade. Measured with a
        // small step from the abutment so it is robust regardless of how many sections the span has.
        var eps = MathF.Min(0.5f, length * 0.05f);
        var deckGradeStart = SafeSlope(profile(0f), profile(eps), 0f, eps);
        var deckGradeEnd = SafeSlope(profile(length - eps), profile(length), length - eps, length);
        var seamKinkStartDeg = MathF.Abs(GradeToDegrees(deckGradeStart) - GradeToDegrees(g0));
        var seamKinkEndDeg = MathF.Abs(GradeToDegrees(deckGradeEnd) - GradeToDegrees(g1));

        // Apply: override ONLY the span sections' centerline elevation + banked edges (§4.5); track clearance.
        var minClearance = float.NaN;
        foreach (var cs in spanSections)
        {
            var z = profile(cs.DistanceAlongSpline - s0);
            cs.TargetElevation = z;

            var halfWidth = cs.EffectiveRoadWidth / 2f;
            var edgeDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
            cs.LeftEdgeElevation = z - edgeDelta;
            cs.RightEdgeElevation = z + edgeDelta;

            if (IsFinite(cs.OriginalTerrainElevation))
            {
                var clearance = z - cs.OriginalTerrainElevation;
                minClearance = float.IsNaN(minClearance) ? clearance : MathF.Min(minClearance, clearance);
            }
        }

        // Doc 15 (a): conformance zone — the anchor above fixed the seam POINT; the intersecting AREA
        // of a genuine merge must follow the landed-on deck surface exactly. Runs after the Hermite is
        // applied and BEFORE the snapshot capture so deck mesh, excavator and bridge DecalRoads all
        // inherit the conformed geometry for free.
        string? overlapNote = null;
        if ((startLanded || endLanded) && spline?.Parameters.BridgeRules?.EnableSeamlessDeckOverlap == true)
        {
            var overlapNotes = new List<string>();
            if (startLanded &&
                ConformDeckOverlapZone(network, spanSections, fromStart: true, startLanding!, length) is { } sNote)
                overlapNotes.Add($"start {sNote}");
            if (endLanded &&
                ConformDeckOverlapZone(network, spanSections, fromStart: false, endLanding!, length) is { } eNote)
                overlapNotes.Add($"end {eNote}");
            if (overlapNotes.Count > 0)
                overlapNote = string.Join("; ", overlapNotes);
        }

        // Capture the immutable snapshot (plan §3 option B) NOW — after the override, before any carve.
        CaptureSpanSnapshot(network, splineId, spanId, spanSections);

        // Typed mode is unconditional (doc 17 §4a): warn against the rule engine's per-crossing budget, not
        // deck-vs-natural-DEM (terrain is not an obstacle; the excavator shaves what pokes above the deck).
        var planClearance = ComputePlanClearance(network, splineId, spanId);

        var note = BuildNote(startConnected, endConnected, rescued, curve, sagCapFactor, planClearance);
        if (startLanded || endLanded || landingSkips.Count > 0)
        {
            var landingNote = string.Join("; ", new[]
            {
                startLanded ? $"start anchored to deck spline={startLanding!.DeckSplineId}@{startLanding.DeckStation:F1}m" : null,
                endLanded ? $"end anchored to deck spline={endLanding!.DeckSplineId}@{endLanding.DeckStation:F1}m" : null,
                overlapNote
            }.Where(p => p != null).Concat(landingSkips));
            note = note == "ok" ? landingNote : $"{note}; {landingNote}";
        }

        return new BridgeProfileApplication
        {
            BridgeSplineId = splineId,
            Applied = true,
            Curve = curve,
            StartConnected = startConnected,
            EndConnected = endConnected,
            RescuedUnchained = rescued,
            StartElevation = z0,
            EndElevation = z1,
            StartGrade = g0,
            EndGrade = g1,
            LengthMeters = length,
            MaxBulgeMeters = maxBulge,
            SagCapFactor = sagCapFactor,
            SeamKinkStartDeg = seamKinkStartDeg,
            SeamKinkEndDeg = seamKinkEndDeg,
            InteriorLiftMeters = interiorLift,
            MinClearanceMeters = minClearance,
            PlanMinClearanceMeters = planClearance?.MinClearance ?? float.NaN,
            PlanRequiredSeparationMeters = planClearance?.RequiredSeparation ?? float.NaN,
            PlanCrossingsChecked = planClearance?.CrossingsChecked ?? 0,
            Note = note
        };
    }

    /// <summary>
    /// A6.5 (V2 review P0-1): the no-override path for spans whose deck the planner pinned. The smoother's
    /// hard-held elevations ARE the deck profile; this only recomputes the banked edges from them (the same
    /// formula the override path uses), tracks clearance, captures the snapshot, and reports the seam-kink
    /// diagnostic against the approach anchors. The doc-16 §3b "ramp may not fully reach the pin" step shows
    /// up here as seamKink — judged from the render, not auto-corrected.
    /// </summary>
    private static BridgeProfileApplication ApplyPinnedSpan(
        UnifiedRoadNetwork network,
        int splineId,
        int spanId,
        List<UnifiedCrossSection> spanSections,
        float s0, float z0, float g0,
        float s1, float z1, float g1,
        float length,
        bool startConnected,
        bool endConnected,
        float minClearanceMeters)
    {
        var minClearance = float.NaN;
        var pinMin = float.PositiveInfinity;
        var pinMax = float.NegativeInfinity;

        foreach (var cs in spanSections)
        {
            // The held elevation is authoritative; fall back to the pin itself if a pass left it NaN.
            if (!IsFinite(cs.TargetElevation) && cs.PinnedElevation is { } p)
                cs.TargetElevation = p;
            var z = cs.TargetElevation;

            var halfWidth = cs.EffectiveRoadWidth / 2f;
            var edgeDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
            cs.LeftEdgeElevation = z - edgeDelta;
            cs.RightEdgeElevation = z + edgeDelta;

            if (cs.PinnedElevation is { } pin)
            {
                pinMin = MathF.Min(pinMin, pin);
                pinMax = MathF.Max(pinMax, pin);
            }

            if (IsFinite(cs.OriginalTerrainElevation))
            {
                var clearance = z - cs.OriginalTerrainElevation;
                minClearance = float.IsNaN(minClearance) ? clearance : MathF.Min(minClearance, clearance);
            }
        }

        // Seam-kink diagnostic: deck grade across the first/last span step vs the approach grades.
        var first = spanSections[0];
        var last = spanSections[^1];
        var deckGradeStart = SafeSlope(z0, first.TargetElevation, s0, first.DistanceAlongSpline);
        var deckGradeEnd = SafeSlope(last.TargetElevation, z1, last.DistanceAlongSpline, s1);
        var seamKinkStartDeg = MathF.Abs(GradeToDegrees(deckGradeStart) - GradeToDegrees(g0));
        var seamKinkEndDeg = MathF.Abs(GradeToDegrees(deckGradeEnd) - GradeToDegrees(g1));

        CaptureSpanSnapshot(network, splineId, spanId, spanSections);

        // Typed mode is unconditional (doc 17 §4a): warn against the rule engine's per-crossing budget.
        var planClearance = ComputePlanClearance(network, splineId, spanId);
        var lowClearance = LowClearanceWarning(planClearance);

        var pinNote = float.IsPositiveInfinity(pinMin)
            ? "pinned span"
            : $"pinned span (pinZ {pinMin:F2}..{pinMax:F2})";
        return new BridgeProfileApplication
        {
            BridgeSplineId = splineId,
            Applied = true,
            Curve = BridgeProfileCurve.Pinned,
            StartConnected = startConnected,
            EndConnected = endConnected,
            RescuedUnchained = false,
            StartElevation = z0,
            EndElevation = z1,
            StartGrade = g0,
            EndGrade = g1,
            LengthMeters = length,
            MaxBulgeMeters = 0f,
            SagCapFactor = 0f,
            SeamKinkStartDeg = seamKinkStartDeg,
            SeamKinkEndDeg = seamKinkEndDeg,
            InteriorLiftMeters = 0f,
            MinClearanceMeters = minClearance,
            PlanMinClearanceMeters = planClearance?.MinClearance ?? float.NaN,
            PlanRequiredSeparationMeters = planClearance?.RequiredSeparation ?? float.NaN,
            PlanCrossingsChecked = planClearance?.CrossingsChecked ?? 0,
            Note = $"{pinNote} — smoother-held deck kept, override skipped (A6.5)" +
                   (lowClearance != null ? $"; {lowClearance}" : string.Empty),
        };
    }

    /// <summary>
    /// Re-captures one span's <see cref="BridgeSpanSnapshot"/> from the network's CURRENT cross-section
    /// elevations, replacing the snapshot taken during <see cref="RefineSpans"/>. For post-solve passes
    /// that move span sections (the doc 04 §4.A approach-raise ramps) so the deck mesh, excavator and
    /// bridge DecalRoads keep reading the same geometry as the road profile.
    /// </summary>
    public static void RecaptureSpanSnapshot(UnifiedRoadNetwork network, int splineId, int spanId)
    {
        ArgumentNullException.ThrowIfNull(network);

        var spanSections = network.GetCrossSectionsForSpline(splineId)
            .Where(c => c.StructureSpanId == spanId)
            .OrderBy(c => c.LocalIndex)
            .ToList();
        if (spanSections.Count == 0)
            return;

        network.BridgeSpans.RemoveAll(s => s.SplineId == splineId && s.SpanId == spanId);
        CaptureSpanSnapshot(network, splineId, spanId, spanSections);
    }

    private static void CaptureSpanSnapshot(
        UnifiedRoadNetwork network, int splineId, int spanId, List<UnifiedCrossSection> spanSections)
    {
        var seg = network.GetSplineById(splineId)?.StructureSegments?.FirstOrDefault(s => s.SpanId == spanId);
        network.BridgeSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = splineId,
            SpanId = spanId,
            OsmWayIds = seg != null ? new HashSet<long>(seg.OsmWayIds) : [],
            OsmTags = seg?.OsmTags,
            Stations = spanSections.Select(cs => new BridgeStation
            {
                Center = cs.CenterPoint,
                Normal = cs.NormalDirection,
                Tangent = cs.TangentDirection,
                Width = cs.EffectiveRoadWidth,
                CenterZ = cs.TargetElevation,
                LeftEdgeZ = cs.LeftEdgeElevation,
                RightEdgeZ = cs.RightEdgeElevation,
                DistanceAlongSpline = cs.DistanceAlongSpline
            }).ToList()
        });
    }

    private static BridgeProfileApplication? ApplyToBridge(
        UnifiedRoadNetwork network,
        int splineId,
        float gradeSampleLengthMeters,
        float maxProfileBulgeCapMeters,
        float minClearanceMeters,
        float maxSagBelowChordMeters,
        IReadOnlyList<BridgeInteriorConstraint>? interiorConstraints = null)
    {
        var allSections = network.GetCrossSectionsForSpline(splineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (allSections.Count < 2)
            return null;

        // The deck = the excluded sections (for a generated bridge all sections are excluded). We override
        // these — including the two endpoint sections, which is exactly what gives G0 continuity.
        var sections = allSections.Where(c => c.IsExcluded).OrderBy(c => c.LocalIndex).ToList();
        if (sections.Count < 2)
            return null;

        var startC = FindConnectedRoadContributor(network, splineId, isStart: true, gradeSampleLengthMeters);
        var endC = FindConnectedRoadContributor(network, splineId, isStart: false, gradeSampleLengthMeters);

        var s0 = sections[0].DistanceAlongSpline;
        var length = sections[^1].DistanceAlongSpline - s0;

        // §4.4 Both ends isolated → no approach truth to honour; leave the chain-solve result untouched.
        if (startC == null && endC == null)
            return NotApplied(splineId, false, false, length, "both ends isolated — left untouched");

        if (length <= 0.01f)
            return NotApplied(splineId, startC != null, endC != null, length,
                "degenerate span length — left untouched");

        // Resolve both endpoints (Z, grade) with isolated-end fallback (§4.4).
        var (z0, g0, v0) = ResolveEndpoint(sections, atStart: true, startC, gradeSampleLengthMeters);
        var (z1, g1, v1) = ResolveEndpoint(sections, atStart: false, endC, gradeSampleLengthMeters);

        // Unchained rescue (§4.6): any non-finite section means the bridge's chain failed.
        var rescued = sections.Any(c => !IsFinite(c.TargetElevation));

        // If an end has no finite elevation (isolated AND unchained), extend a straight grade line from
        // the connected end so the span is still clean rather than NaN.
        if (!v0 && v1) { g0 = g1; z0 = z1 - g1 * length; v0 = true; rescued = true; }
        if (!v1 && v0) { g1 = g0; z1 = z0 + g0 * length; v1 = true; rescued = true; }
        if (!v0 || !v1)
            return NotApplied(splineId, startC != null, endC != null, length,
                "no finite endpoint elevation available — left untouched");

        // Pick the curve: cubic Hermite, sag-capped toward the chord, with overshoot guard → parabola → chord.
        var sLocals = sections.Select(c => c.DistanceAlongSpline - s0).ToArray();
        var (profile, curve, maxBulge, sagCapFactor) =
            SelectCurve(sLocals, length, z0, z1, g0, g1, maxProfileBulgeCapMeters, maxSagBelowChordMeters);

        // E-A (D-4): honour interior minimum-Z clearance constraints by adding a smooth interior arch on top
        // of the chosen curve. The bump shape 16·t²·(1−t)² is zero in BOTH value and slope at t=0 and t=1, so
        // the abutment elevation (G0) and grade (G1) are preserved exactly — only the span interior rises.
        // The deck gains an arch to clear the obstacle, which is what a real bridge does; no grade is clamped.
        var interiorLift = ComputeInteriorLift(profile, interiorConstraints, s0, length);
        if (interiorLift > 0f)
        {
            var baseProfile = profile;
            var spanLength = length;
            profile = s =>
            {
                var t = s / spanLength;
                return baseProfile(s) + interiorLift * 16f * t * t * (1f - t) * (1f - t);
            };
            float Chord(float s) => z0 + (z1 - z0) * (s / length);
            maxBulge = MaxDeviation(sLocals, profile, Chord);
        }

        // Drivability: the deck's actual endpoint grade (after sag-capping / fallback) vs the approach grade
        // is the vertical kink a vehicle hits at the abutment. Flattening the deck trades sag for this kink.
        var deckGradeStart = SafeSlope(profile(sLocals[0]), profile(sLocals[1]), sLocals[0], sLocals[1]);
        var deckGradeEnd = SafeSlope(profile(sLocals[^2]), profile(sLocals[^1]), sLocals[^2], sLocals[^1]);
        var seamKinkStartDeg = MathF.Abs(GradeToDegrees(deckGradeStart) - GradeToDegrees(g0));
        var seamKinkEndDeg = MathF.Abs(GradeToDegrees(deckGradeEnd) - GradeToDegrees(g1));

        // Apply: override centerline elevation + recompute banked edges (§4.5); track clearance (§4.7).
        var minClearance = float.NaN;
        foreach (var cs in sections)
        {
            var z = profile(cs.DistanceAlongSpline - s0);
            cs.TargetElevation = z;

            var halfWidth = cs.EffectiveRoadWidth / 2f;
            var edgeDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
            cs.LeftEdgeElevation = z - edgeDelta;
            cs.RightEdgeElevation = z + edgeDelta;

            // OriginalTerrainElevation was sampled by the chain solve in the same (pre-base-height) space
            // as TargetElevation, so the difference is the deck-above-terrain clearance.
            if (IsFinite(cs.OriginalTerrainElevation))
            {
                var clearance = z - cs.OriginalTerrainElevation;
                minClearance = float.IsNaN(minClearance) ? clearance : MathF.Min(minClearance, clearance);
            }
        }

        return new BridgeProfileApplication
        {
            BridgeSplineId = splineId,
            Applied = true,
            Curve = curve,
            StartConnected = startC != null,
            EndConnected = endC != null,
            RescuedUnchained = rescued,
            StartElevation = z0,
            EndElevation = z1,
            StartGrade = g0,
            EndGrade = g1,
            LengthMeters = length,
            MaxBulgeMeters = maxBulge,
            SagCapFactor = sagCapFactor,
            SeamKinkStartDeg = seamKinkStartDeg,
            SeamKinkEndDeg = seamKinkEndDeg,
            InteriorLiftMeters = interiorLift,
            MinClearanceMeters = minClearance,
            Note = BuildNote(startC != null, endC != null, rescued, curve, sagCapFactor)
        };
    }

    private static BridgeProfileApplication NotApplied(
        int splineId, bool startConnected, bool endConnected, float length, string note) =>
        new()
        {
            BridgeSplineId = splineId,
            Applied = false,
            Curve = BridgeProfileCurve.None,
            StartConnected = startConnected,
            EndConnected = endConnected,
            LengthMeters = length,
            Note = note
        };

    /// <summary>
    /// Resolves one bridge endpoint to (elevation, grade-in-+s, isFinite). Uses the connected approach
    /// when present, else falls back to the bridge's own chain-solve endpoint Z + its own grade (§4.4).
    /// </summary>
    private static (float z, float grade, bool valid) ResolveEndpoint(
        IReadOnlyList<UnifiedCrossSection> sections,
        bool atStart,
        BridgeEndpointContributor? contributor,
        float gradeSampleLengthMeters)
    {
        if (contributor != null)
            return (contributor.Elevation, contributor.GradeAlongBridge, true);

        var endSec = atStart ? sections[0] : sections[^1];
        var z = endSec.TargetElevation;
        var g = EstimateForwardGrade(sections, atStart, gradeSampleLengthMeters);
        if (!IsFinite(g))
            g = 0f;
        return (z, g, IsFinite(z));
    }

    /// <summary>
    /// Chooses the span curve over <paramref name="sLocals"/> (section distances relative to the span start)
    /// honouring P(0)=z0, P(L)=z1 and, where the bulge guard allows, P'(0)=g0, P'(L)=g1. Returns the
    /// evaluator, the chosen curve type, and the curve's max deviation from the straight chord (the bulge).
    /// </summary>
    internal static (Func<float, float> profile, BridgeProfileCurve curve, float maxBulge, float sagCapFactor)
        SelectCurve(
            float[] sLocals, float length, float z0, float z1, float g0, float g1,
            float bulgeCapMeters, float maxSagBelowChordMeters)
    {
        var bulgeThreshold = MathF.Min(0.25f * length, bulgeCapMeters);

        float Chord(float s) => z0 + (z1 - z0) * (s / length);

        // Cubic Hermite: the unique low-order curve meeting all four endpoint constraints (§4.3).
        float Cubic(float s)
        {
            var t = s / length;
            var t2 = t * t;
            var t3 = t2 * t;
            var h00 = 2f * t3 - 3f * t2 + 1f;
            var h10 = t3 - 2f * t2 + t;
            var h01 = -2f * t3 + 3f * t2;
            var h11 = t3 - t2;
            return h00 * z0 + h10 * (length * g0) + h01 * z1 + h11 * (length * g1);
        }

        // Cap how far the deck may bow BELOW the chord. A bridge spans — it must not sag into the gap. When
        // steep, opposed approach grades dip the cubic deeper than the tolerance, blend the whole curve
        // uniformly toward the chord so the deepest dip equals the tolerance. This keeps the endpoints exact
        // and stays smooth (a cubic+linear blend is still a cubic); it trades some endpoint-grade fidelity.
        var (capped, sagCapFactor) = CapSagBelowChord(Cubic, Chord, sLocals, maxSagBelowChordMeters);

        var cubicBulge = MaxDeviation(sLocals, capped, Chord);
        if (cubicBulge <= bulgeThreshold)
            return (capped, BridgeProfileCurve.Cubic, cubicBulge, sagCapFactor);

        // Overshoot guard: a degree-2 parabola through both endpoints anchored on one approach grade. It
        // sacrifices grade continuity at the opposite end but bulges far less than a strongly-opposed cubic.
        // Pick whichever anchoring (start or end) bulges least — no grade clamp is ever introduced.
        Func<float, float> ParabolaAnchoredStart()
        {
            var c = (z1 - z0 - g0 * length) / (length * length);
            return s => z0 + g0 * s + c * s * s;
        }

        Func<float, float> ParabolaAnchoredEnd()
        {
            var c = (z0 - z1 + g1 * length) / (length * length);
            return s => z1 + g1 * (s - length) + c * (s - length) * (s - length);
        }

        var pStart = ParabolaAnchoredStart();
        var pEnd = ParabolaAnchoredEnd();
        var bStart = MaxDeviation(sLocals, pStart, Chord);
        var bEnd = MaxDeviation(sLocals, pEnd, Chord);
        var (parabola, parabolaBulge) = bStart <= bEnd ? (pStart, bStart) : (pEnd, bEnd);

        if (parabolaBulge <= bulgeThreshold)
            return (parabola, BridgeProfileCurve.Parabola, parabolaBulge, 1f);

        // Last resort: the straight chord — zero bulge, sacrifices grade continuity but never overshoots.
        return (Chord, BridgeProfileCurve.Chord, 0f, 1f);
    }

    /// <summary>
    /// Blends <paramref name="curve"/> uniformly toward <paramref name="chord"/> so its deepest dip below
    /// the chord equals <paramref name="maxSagMeters"/>. Returns the (possibly unchanged) curve and the
    /// blend factor used (1 = untouched). Endpoints are preserved because the curve and chord coincide
    /// there; arches above the chord are scaled by the same factor (harmless — the overshoot guard bounds
    /// arch separately). No grade clamp: the curve family is changed by blending, grades are not capped.
    /// </summary>
    private static (Func<float, float> profile, float factor) CapSagBelowChord(
        Func<float, float> curve, Func<float, float> chord, float[] sLocals, float maxSagMeters)
    {
        var maxSag = 0f;
        foreach (var s in sLocals)
        {
            var dip = chord(s) - curve(s); // positive when the curve is below the chord
            if (dip > maxSag)
                maxSag = dip;
        }

        if (maxSag <= maxSagMeters || maxSag <= 1e-4f)
            return (curve, 1f);

        var factor = maxSagMeters / maxSag;
        return (s => chord(s) + factor * (curve(s) - chord(s)), factor);
    }

    private static float MaxDeviation(float[] sLocals, Func<float, float> curve, Func<float, float> chord)
    {
        var max = 0f;
        foreach (var s in sLocals)
            max = MathF.Max(max, MathF.Abs(curve(s) - chord(s)));
        return max;
    }

    /// <summary>
    /// Computes the peak height (m) of the interior arch needed so that, for every interior constraint,
    /// <c>profile(s) + lift·16·t²·(1−t)²</c> reaches the constraint's <c>MinZ</c>. Returns 0 when no
    /// constraint is violated. Constraints at or beyond the abutments are ignored (clearance there is ~0
    /// by design — only the span interior is enforced, doc 07 §2). The arch peaks at mid-span (t=0.5), so a
    /// constraint off-centre needs a proportionally larger lift to be satisfied at its station.
    /// </summary>
    private static float ComputeInteriorLift(
        Func<float, float> profile,
        IReadOnlyList<BridgeInteriorConstraint>? constraints,
        float s0,
        float length)
    {
        if (constraints == null || constraints.Count == 0 || length <= 0.01f)
            return 0f;

        var lift = 0f;
        foreach (var c in constraints)
        {
            var sLocal = c.DistanceAlongSpline - s0;
            if (sLocal <= 0.01f || sLocal >= length - 0.01f)
                continue; // interior only

            var t = sLocal / length;
            var shape = 16f * t * t * (1f - t) * (1f - t);
            if (shape <= 1e-3f)
                continue;

            var deficit = c.MinZ - profile(sLocal);
            if (deficit <= 0f)
                continue;

            lift = MathF.Max(lift, deficit / shape);
        }

        return lift;
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    /// <summary>
    /// The worst planned-crossing clearance under one span, measured against the rule engine's typed
    /// per-crossing budget (<see cref="Algorithms.CrossingPlan.RequiredSeparationMeters"/>) — the honest
    /// replacement for the deck-vs-natural-DEM LOW CLEARANCE warn when obstacle typing is on.
    /// </summary>
    private readonly record struct PlanClearanceDiagnostic(
        float MinClearance, float RequiredSeparation, int CrossingsChecked)
    {
        public bool IsShort => MinClearance < RequiredSeparation - 0.05f;
    }

    /// <summary>
    /// V2 typed-budget clearance diagnostic: for each rule-engine crossing under this span, measure the
    /// FINAL solved deck Z at the crossing station against the lower member's FINAL solved Z (which
    /// includes an A6 dip-as-pin well — the natural DEM does not) — synthetic rail/water crossings fall
    /// back to the planner's lower-road target / obstacle estimate. Returns the binding crossing's
    /// clearance and its <see cref="Algorithms.CrossingPlan.RequiredSeparationMeters"/>. Null when the
    /// plan has no measurable crossing under the span. In typed mode terrain is NOT an obstacle (the
    /// excavator shaves what pokes above the deck later), so deck-below-DEM is expected, not a fault.
    /// </summary>
    private static PlanClearanceDiagnostic? ComputePlanClearance(
        UnifiedRoadNetwork network, int splineId, int spanId)
    {
        var plan = network.BridgeElevationPlan;
        if (plan == null || plan.Crossings.Count == 0)
            return null;

        List<UnifiedCrossSection>? splineSections = null;
        var worstMargin = float.PositiveInfinity;
        var worstClear = float.NaN;
        var worstRequired = float.NaN;
        var checkedCount = 0;

        foreach (var cp in plan.Crossings)
        {
            if (cp.Crossing.UpperSplineId != splineId || cp.RequiredSeparationMeters <= 0f)
                continue;

            // Station-match the crossing to THIS span: the nearest section over the whole spline must
            // belong to it (a corridor can carry several spans). Self-crossing: restrict to deck sections
            // — the plain XY lookup finds the crossing's own ground-leg section (distance 0) instead.
            splineSections ??= network.GetCrossSectionsForSpline(splineId).ToList();
            var upper = cp.Crossing.HasSelfLowerStation
                ? NearestSection(splineSections.Where(cs => cs.StructureSpanId == spanId), cp.Crossing.CrossingXY)
                : NearestSection(splineSections, cp.Crossing.CrossingXY);
            if (upper == null || upper.StructureSpanId != spanId || !IsFinite(upper.TargetElevation))
                continue;

            float lowerZ;
            if (cp.Crossing.HasLowerSpline)
            {
                var lower = NearestSection(
                    network.GetCrossSectionsForSpline(cp.Crossing.LowerSplineId), cp.Crossing.CrossingXY);
                lowerZ = lower?.TargetElevation ?? float.NaN;
            }
            else
            {
                // Self-crossing: the own ground leg's final Z, resolved by STATION (XY finds the deck).
                lowerZ = GradeSeparationResolver.ResolveSelfLowerZ(network, cp.Crossing)
                         ?? (IsFinite(cp.LowerRoadTargetZ) ? cp.LowerRoadTargetZ : cp.ObstacleZEstimate);
            }

            if (!IsFinite(lowerZ))
                continue;

            checkedCount++;
            var clearance = upper.TargetElevation - lowerZ;
            var margin = clearance - cp.RequiredSeparationMeters;
            if (margin < worstMargin)
            {
                worstMargin = margin;
                worstClear = clearance;
                worstRequired = cp.RequiredSeparationMeters;
            }
        }

        return checkedCount > 0
            ? new PlanClearanceDiagnostic(worstClear, worstRequired, checkedCount)
            : null;
    }

    private static UnifiedCrossSection? NearestSection(IEnumerable<UnifiedCrossSection> sections, Vector2 xy)
    {
        UnifiedCrossSection? best = null;
        var bestDist = float.MaxValue;
        foreach (var cs in sections)
        {
            var d = Vector2.DistanceSquared(cs.CenterPoint, xy);
            if (d < bestDist)
            {
                bestDist = d;
                best = cs;
            }
        }

        return best;
    }

    private static float SafeSlope(float zA, float zB, float sA, float sB)
    {
        var ds = sB - sA;
        return MathF.Abs(ds) > 1e-4f ? (zB - zA) / ds : 0f;
    }

    private static string BuildNote(
        bool startConnected, bool endConnected, bool rescued,
        BridgeProfileCurve curve, float sagCapFactor, PlanClearanceDiagnostic? planClearance = null)
    {
        var parts = new List<string>();
        if (!startConnected)
            parts.Add("start isolated → fallback");
        if (!endConnected)
            parts.Add("end isolated → fallback");
        if (rescued)
            parts.Add("unchained rescue");
        if (sagCapFactor < 0.999f)
            parts.Add($"sag-capped (f={sagCapFactor:F2})");
        if (curve == BridgeProfileCurve.Parabola)
            parts.Add("overshoot guard → parabola");
        if (curve == BridgeProfileCurve.Chord)
            parts.Add("overshoot guard → chord");
        var lowClearance = LowClearanceWarning(planClearance);
        if (lowClearance != null)
            parts.Add(lowClearance);
        return parts.Count == 0 ? "ok" : string.Join("; ", parts);
    }

    /// <summary>
    /// The LOW CLEARANCE warn text, or null when clearance is fine. With obstacle typing OFF this is the
    /// legacy deck-vs-natural-DEM check against the 5 m constant (byte-identical). With typing ON, terrain
    /// is not an obstacle — the warn instead compares the worst planned crossing against its typed
    /// per-crossing budget (no plan crossing under the span ⇒ nothing to warn about).
    /// </summary>
    // Obstacle typing is unconditional (doc 17 §4a): warn against the rule engine's per-crossing budget.
    private static string? LowClearanceWarning(PlanClearanceDiagnostic? planClearance) =>
        planClearance is { IsShort: true } pc
            ? $"LOW CLEARANCE (typed) {pc.MinClearance:F1}m < {pc.RequiredSeparation:F1}m at a planned crossing"
            : null;

    private static string FormatApplication(BridgeProfileApplication a)
    {
        if (!a.Applied)
            return $"[BRIDGE-PROFILE] apply spline={a.BridgeSplineId} OVERRIDE=no ({a.Note})";

        return $"[BRIDGE-PROFILE] apply spline={a.BridgeSplineId} OVERRIDE=yes curve={a.Curve} " +
               $"L={a.LengthMeters:F1}m z0={a.StartElevation:F2} z1={a.EndElevation:F2} " +
               $"g0={a.StartGrade * 100f:F1}% g1={a.EndGrade * 100f:F1}% bulge={a.MaxBulgeMeters:F2}m " +
               (a.InteriorLiftMeters > 0f ? $"arch={a.InteriorLiftMeters:F2}m " : "") +
               $"seamKink={a.SeamKinkStartDeg:F1}/{a.SeamKinkEndDeg:F1}deg " +
               $"minClear={a.MinClearanceMeters:F1}m " +
               (a.PlanCrossingsChecked > 0
                   ? $"planClear={a.PlanMinClearanceMeters:F1}/{a.PlanRequiredSeparationMeters:F1}m " +
                     $"({a.PlanCrossingsChecked} planned) "
                   : "") +
               $"start={(a.StartConnected ? "conn" : "iso")} " +
               $"end={(a.EndConnected ? "conn" : "iso")}" +
               (a.Note == "ok" ? "" : $" [{a.Note}]");
    }

    private static string FormatApplySummary(BridgeProfileResult r)
    {
        var applied = r.Applications.Where(a => a.Applied).ToList();
        var maxKink = applied.Count > 0
            ? applied.Max(a => MathF.Max(a.SeamKinkStartDeg, a.SeamKinkEndDeg))
            : 0f;
        return $"[BRIDGE-PROFILE] apply summary bridges={r.BridgesProcessed} overridden={r.BridgesOverridden} " +
               $"isolated={r.BridgesLeftIsolated} " +
               $"cubic={applied.Count(a => a.Curve == BridgeProfileCurve.Cubic)} " +
               $"parabola={applied.Count(a => a.Curve == BridgeProfileCurve.Parabola)} " +
               $"chord={applied.Count(a => a.Curve == BridgeProfileCurve.Chord)} " +
               $"sagCapped={applied.Count(a => a.SagCapFactor < 0.999f)} " +
               $"rescued={applied.Count(a => a.RescuedUnchained)} maxSeamKink={maxKink:F1}deg";
    }

    /// <summary>
    /// Finds the single best connected non-bridge approach contributor at a bridge endpoint, with its
    /// elevation, grade (in bridge +s terms), tangent, normal and width. Returns null if the endpoint is
    /// isolated (no connected non-bridge road). Shared by the vertical and plan-view passes.
    /// </summary>
    public static BridgeEndpointContributor? FindConnectedRoadContributor(
        UnifiedRoadNetwork network,
        int bridgeSplineId,
        bool isStart,
        float gradeSampleLengthMeters = DefaultGradeSampleLengthMeters)
    {
        ArgumentNullException.ThrowIfNull(network);

        var bridgeSections = network.GetCrossSectionsForSpline(bridgeSplineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (bridgeSections.Count < 2)
            return null;

        var bridgeEnd = isStart ? bridgeSections[0] : bridgeSections[^1];
        var bridgeForwardTangent = SafeNormalize(bridgeEnd.TangentDirection); // points +s everywhere
        var deckToDeck = network.GetSplineById(bridgeSplineId)
            ?.Parameters.BridgeRules?.EnableDeckToDeckContinuity == true;

        foreach (var junction in network.Junctions)
        {
            if (junction.IsExcluded)
                continue;

            var bridgeContributor = junction.Contributors.FirstOrDefault(c =>
                c.Spline.SplineId == bridgeSplineId && c.IsEndpoint && c.IsSplineStart == isStart);
            if (bridgeContributor == null)
                continue;

            var candidates = junction.Contributors.Where(c =>
                c.Spline.SplineId != bridgeSplineId &&
                !BridgeDeckDaeExporter.ShouldGenerateDeck(c.Spline) &&
                c.IsEndpoint &&
                !float.IsNaN(c.CrossSection.TargetElevation) &&
                !float.IsInfinity(c.CrossSection.TargetElevation)).ToList();

            // Doc 14 (b): a DECK neighbour is a valid seam anchor too — endpoint-to-endpoint deck
            // handoffs are anchored BY DESIGN (not the merged-corridor luck that saved 70↔14), and a
            // mid-spline span-tagged contributor is a trunk deck this bridge lands on. Road
            // contributors keep precedence, so flag-on changes nothing at road-connected seams.
            if (candidates.Count == 0 && deckToDeck)
            {
                candidates = junction.Contributors.Where(c =>
                    c.Spline.SplineId != bridgeSplineId &&
                    !float.IsNaN(c.CrossSection.TargetElevation) &&
                    !float.IsInfinity(c.CrossSection.TargetElevation) &&
                    (c.IsEndpoint
                        ? BridgeDeckDaeExporter.ShouldGenerateDeck(c.Spline)
                        : c.CrossSection.StructureSpanId >= 0)).ToList();
            }

            if (candidates.Count == 0)
                continue;

            // Best = smallest plan-view gap to the bridge endpoint; tie-break by higher road priority.
            var best = candidates
                .OrderBy(c => Vector2.Distance(c.CrossSection.CenterPoint, bridgeEnd.CenterPoint))
                .ThenByDescending(c => c.Spline.Priority)
                .First();

            var approachSections = network.GetCrossSectionsForSpline(best.Spline.SplineId)
                .OrderBy(c => c.LocalIndex).ToList();
            // A mid-spline (deck-landing) contributor needs the LOCAL grade at its station — the
            // endpoint estimator would report the far end of the trunk.
            var approachForwardGrade = best.IsEndpoint
                ? EstimateForwardGrade(approachSections, atStart: best.IsSplineStart, gradeSampleLengthMeters)
                : EstimateLocalGrade(approachSections, best.CrossSection.DistanceAlongSpline, gradeSampleLengthMeters);
            var approachForwardTangent = SafeNormalize(best.CrossSection.TangentDirection);

            // Convert the approach grade (per metre in the approach's forward spatial direction) into
            // the bridge's +s spatial direction so it is usable directly as a Hermite endpoint slope.
            var sign = Vector2.Dot(approachForwardTangent, bridgeForwardTangent) < 0 ? -1f : 1f;
            var gradeAlongBridge = approachForwardGrade * sign;

            return new BridgeEndpointContributor(
                best.Spline.SplineId,
                best.CrossSection.TargetElevation,
                gradeAlongBridge,
                approachForwardTangent,
                SafeNormalize(best.CrossSection.NormalDirection),
                best.CrossSection.EffectiveRoadWidth);
        }

        return null;
    }

    /// <summary>
    /// Estimates the forward (increasing-distance) longitudinal grade dZ/ds near one end of an ordered
    /// cross-section list, averaging over up to <paramref name="sampleLengthMeters"/> of road to reduce noise.
    /// </summary>
    internal static float EstimateForwardGrade(
        IReadOnlyList<UnifiedCrossSection> ordered, bool atStart, float sampleLengthMeters)
    {
        if (ordered.Count < 2)
            return 0f;

        if (atStart)
        {
            var d0 = ordered[0].DistanceAlongSpline;
            var z0 = ordered[0].TargetElevation;
            var far = ordered[1];
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].DistanceAlongSpline - d0 <= sampleLengthMeters)
                    far = ordered[i];
                else
                    break;
            }
            var dd = far.DistanceAlongSpline - d0;
            return dd > 0.01f ? (far.TargetElevation - z0) / dd : 0f;
        }
        else
        {
            var dL = ordered[^1].DistanceAlongSpline;
            var zL = ordered[^1].TargetElevation;
            var near = ordered[^2];
            for (var i = ordered.Count - 2; i >= 0; i--)
            {
                if (dL - ordered[i].DistanceAlongSpline <= sampleLengthMeters)
                    near = ordered[i];
                else
                    break;
            }
            var dd = dL - near.DistanceAlongSpline;
            return dd > 0.01f ? (zL - near.TargetElevation) / dd : 0f;
        }
    }

    /// <summary>
    /// Local dZ/ds around an arbitrary interior station of an ordered section list — the mid-spline
    /// counterpart of <see cref="EstimateForwardGrade"/> (which only handles the two ends). Averages over
    /// the finite sections within ±half the sample length; falls back to the nearest bracketing pair.
    /// </summary>
    internal static float EstimateLocalGrade(
        IReadOnlyList<UnifiedCrossSection> ordered, float station, float sampleLengthMeters)
    {
        if (ordered.Count < 2)
            return 0f;

        var half = MathF.Max(sampleLengthMeters / 2f, 0.5f);
        UnifiedCrossSection? left = null, right = null, prev = null, next = null;
        foreach (var cs in ordered)
        {
            if (!IsFinite(cs.TargetElevation)) continue;
            var d = cs.DistanceAlongSpline;
            if (d <= station)
            {
                prev = cs;
                if (d >= station - half && left == null) left = cs;
            }
            else
            {
                next ??= cs;
                if (d <= station + half) right = cs;
                else break;
            }
        }

        left ??= prev;
        right ??= next;
        if (left == null || right == null)
            return 0f;

        var dd = right.DistanceAlongSpline - left.DistanceAlongSpline;
        return dd > 0.01f ? (right.TargetElevation - left.TargetElevation) / dd : 0f;
    }

    /// <summary>
    /// Doc 14 (b): samples the landed-on deck's SURFACE at a landing — center Z interpolated at the
    /// landing station plus the landing end's lateral bank offset (offset·sin(bank), the banked-edge
    /// formula), grade = the deck surface's directional derivative along the landing span's +s
    /// (longitudinal slope projected onto the span tangent + the bank cross-slope component). False when
    /// the landed-on spline has no usable solved sections there — the caller keeps its normal anchor.
    /// </summary>
    private static bool TrySampleDeckSurface(
        UnifiedRoadNetwork network,
        DeckLandingRecord landing,
        UnifiedCrossSection landingEnd,
        float gradeSampleLengthMeters,
        out (float z, float grade, Vector2 deckTangent) anchor)
    {
        anchor = default;
        var deckSections = network.GetCrossSectionsForSpline(landing.DeckSplineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (deckSections.Count < 2)
            return false;

        var station = Math.Clamp(landing.DeckStation,
            deckSections[0].DistanceAlongSpline, deckSections[^1].DistanceAlongSpline);

        // Bracketing pair around the station (sections are distance-ordered along the spline).
        var hi = 1;
        while (hi < deckSections.Count - 1 && deckSections[hi].DistanceAlongSpline < station)
            hi++;
        var a = deckSections[hi - 1];
        var b = deckSections[hi];
        if (!IsFinite(a.TargetElevation) || !IsFinite(b.TargetElevation))
            return false;

        var ds = b.DistanceAlongSpline - a.DistanceAlongSpline;
        var t = ds > 1e-4f ? Math.Clamp((station - a.DistanceAlongSpline) / ds, 0f, 1f) : 0f;
        var near = t < 0.5f ? a : b;

        var centerZ = a.TargetElevation + (b.TargetElevation - a.TargetElevation) * t;
        var center = Vector2.Lerp(a.CenterPoint, b.CenterPoint, t);
        var bank = a.BankAngleRadians + (b.BankAngleRadians - a.BankAngleRadians) * t;

        var deckTangent = SafeNormalize(near.TangentDirection);
        var deckNormal = SafeNormalize(near.NormalDirection);
        var spanTangent = SafeNormalize(landingEnd.TangentDirection);

        // Lateral offset of the landing end on the deck, clamped to the deck half-width (a landing at
        // the deck edge must not extrapolate the bank plane beyond the surface).
        var halfWidth = near.EffectiveRoadWidth / 2f;
        var offset = Math.Clamp(
            Vector2.Dot(landingEnd.CenterPoint - center, deckNormal), -halfWidth, halfWidth);

        var longitudinalGrade = EstimateLocalGrade(deckSections, station, gradeSampleLengthMeters);
        var bankSlope = MathF.Sin(bank);

        var z = centerZ + offset * bankSlope;
        var grade = longitudinalGrade * Vector2.Dot(deckTangent, spanTangent)
                    + bankSlope * Vector2.Dot(deckNormal, spanTangent);
        if (!IsFinite(z) || !IsFinite(grade))
            return false;

        anchor = (z, grade, deckTangent);
        return true;
    }

    /// <summary>One arbitrary-point deck-surface sample (doc 15). See <see cref="TrySampleDeckSurfaceAt"/>.</summary>
    /// <param name="Z">Deck surface elevation at the point (center Z + clamped offset · sin(bank)).</param>
    /// <param name="Station">Arc-length station of the projection along the deck spline.</param>
    /// <param name="LateralOffset">UNclamped signed lateral offset of the point from the deck centerline (m).</param>
    /// <param name="DeckHalfWidth">Local deck half-width (m) — with the offset, the caller's footprint test.</param>
    /// <param name="LongitudinalOvershoot">Along-tangent distance (m) past the polyline's first/last vertex
    /// when the projection clamped there; 0 for interior projections.</param>
    internal readonly record struct DeckSurfaceSample(
        float Z, float Station, float LateralOffset, float DeckHalfWidth, float LongitudinalOvershoot);

    /// <summary>
    /// Doc 15: samples a deck's SURFACE at an arbitrary plan-view point — the area generalization of
    /// <see cref="TrySampleDeckSurface"/> (which samples only at a recorded landing station). The point
    /// is projected onto the deck centerline polyline near <paramref name="stationHint"/> (±
    /// <see cref="DeckOverlapStationWindowMeters"/>, so a corridor looping back through the map cannot
    /// steal the projection); the surface Z is the interpolated center Z plus the clamped lateral
    /// offset · sin(bank) — the banked-edge formula, matching the landing-anchor sampler. The unclamped
    /// offset, local half-width and end overshoot are returned for the caller's footprint test.
    /// </summary>
    internal static bool TrySampleDeckSurfaceAt(
        IReadOnlyList<UnifiedCrossSection> deckSections,
        Vector2 point,
        float stationHint,
        out DeckSurfaceSample sample)
    {
        sample = default;
        if (deckSections.Count < 2)
            return false;

        var lo = stationHint - DeckOverlapStationWindowMeters;
        var hi = stationHint + DeckOverlapStationWindowMeters;

        UnifiedCrossSection? bestA = null, bestB = null;
        var bestT = 0f;
        var bestDistSq = float.MaxValue;
        var bestIndex = -1;
        for (var i = 0; i < deckSections.Count - 1; i++)
        {
            var a = deckSections[i];
            var b = deckSections[i + 1];
            if (b.DistanceAlongSpline < lo || a.DistanceAlongSpline > hi)
                continue;
            if (!IsFinite(a.TargetElevation) || !IsFinite(b.TargetElevation))
                continue;

            var ab = b.CenterPoint - a.CenterPoint;
            var lenSq = ab.LengthSquared();
            var t = lenSq > 1e-8f ? Math.Clamp(Vector2.Dot(point - a.CenterPoint, ab) / lenSq, 0f, 1f) : 0f;
            var distSq = Vector2.DistanceSquared(point, a.CenterPoint + ab * t);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestA = a;
                bestB = b;
                bestT = t;
                bestIndex = i;
            }
        }

        if (bestA == null || bestB == null)
            return false;

        var station = bestA.DistanceAlongSpline + (bestB.DistanceAlongSpline - bestA.DistanceAlongSpline) * bestT;
        var centerZ = bestA.TargetElevation + (bestB.TargetElevation - bestA.TargetElevation) * bestT;
        var bank = bestA.BankAngleRadians + (bestB.BankAngleRadians - bestA.BankAngleRadians) * bestT;
        var near = bestT < 0.5f ? bestA : bestB;
        var center = Vector2.Lerp(bestA.CenterPoint, bestB.CenterPoint, bestT);
        var normal = SafeNormalize(near.NormalDirection);

        var halfWidth = near.EffectiveRoadWidth / 2f;
        var offset = Vector2.Dot(point - center, normal);
        var z = centerZ + Math.Clamp(offset, -halfWidth, halfWidth) * MathF.Sin(bank);
        if (!IsFinite(z))
            return false;

        // A projection clamped at the very first/last polyline vertex means the point lies BEYOND the
        // deck end — report the along-tangent overshoot so the footprint test can reject it.
        var overshoot = 0f;
        if ((bestIndex == 0 && bestT <= 0f) ||
            (bestIndex == deckSections.Count - 2 && bestT >= 1f))
            overshoot = MathF.Abs(Vector2.Dot(point - center, SafeNormalize(near.TangentDirection)));

        sample = new DeckSurfaceSample(z, station, offset, halfWidth, overshoot);
        return true;
    }

    /// <summary>
    /// Doc 15: samples the landed-on deck surface under one landing-span section at its center and
    /// BOTH edge points, and reports whether ANY of the three lies inside the deck footprint
    /// (half-width + margin, not past the deck ends) — the conformance-zone membership test. On
    /// success <paramref name="stationHint"/> advances to the center's projected station so the walk
    /// tracks the deck.
    /// </summary>
    private static bool TrySampleOverlapSection(
        IReadOnlyList<UnifiedCrossSection> deckSections,
        UnifiedCrossSection cs,
        ref float stationHint,
        out (float Center, float Left, float Right) deckZ)
    {
        deckZ = default;
        var halfW = cs.EffectiveRoadWidth / 2f;
        var normal = SafeNormalize(cs.NormalDirection);
        var leftPt = cs.CenterPoint - normal * halfW;
        var rightPt = cs.CenterPoint + normal * halfW;

        if (!TrySampleDeckSurfaceAt(deckSections, cs.CenterPoint, stationHint, out var c) ||
            !TrySampleDeckSurfaceAt(deckSections, leftPt, stationHint, out var l) ||
            !TrySampleDeckSurfaceAt(deckSections, rightPt, stationHint, out var r))
            return false;

        static bool Inside(DeckSurfaceSample s) =>
            MathF.Abs(s.LateralOffset) <= s.DeckHalfWidth + DeckOverlapLateralMarginMeters &&
            s.LongitudinalOvershoot <= DeckOverlapLateralMarginMeters;

        if (!Inside(c) && !Inside(l) && !Inside(r))
            return false;

        deckZ = (c.Z, l.Z, r.Z);
        stationHint = c.Station;
        return true;
    }

    /// <summary>
    /// Doc 15 (a): the deck conformance zone. Walks the landed span's sections inward from the landed
    /// end; every section still overlapping the landed-on deck's footprint is set EXACTLY onto that
    /// deck's surface — center and both edges sampled independently, so the intersecting part is
    /// coplanar by construction, bank included (the edges are sampled, not offset). Past the last
    /// overlapping section the boundary correction eases out over a smoothstep run — (1−u)²(1+2u) is 1
    /// with zero slope at the boundary and 0 with zero slope at the run end — capped at the remaining
    /// span so the far anchor is never moved. The walk itself is capped at min(span/2,
    /// <see cref="DeckOverlapMaxWalkMeters"/>): a merge overlap is an end phenomenon. The landed-on
    /// deck is UNTOUCHED (one-directional, like the junction authority rule). Returns a note for the
    /// [BRIDGE-PROFILE] log line, or null when nothing overlapped.
    /// </summary>
    private static string? ConformDeckOverlapZone(
        UnifiedRoadNetwork network,
        List<UnifiedCrossSection> spanSections,
        bool fromStart,
        DeckLandingRecord landing,
        float spanLength)
    {
        var deckSections = network.GetCrossSectionsForSpline(landing.DeckSplineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (deckSections.Count < 2)
            return null;

        var walkCap = MathF.Min(spanLength / 2f, DeckOverlapMaxWalkMeters);
        var endStation = fromStart ? spanSections[0].DistanceAlongSpline : spanSections[^1].DistanceAlongSpline;
        var step = fromStart ? 1 : -1;

        var hint = landing.DeckStation;
        var conformed = 0;
        var maxShift = 0f;
        UnifiedCrossSection? boundary = null;
        float dCenter = 0f, dLeft = 0f, dRight = 0f;

        var i = fromStart ? 0 : spanSections.Count - 1;
        for (; i >= 0 && i < spanSections.Count; i += step)
        {
            var cs = spanSections[i];
            if (MathF.Abs(cs.DistanceAlongSpline - endStation) > walkCap)
                break;
            if (!TrySampleOverlapSection(deckSections, cs, ref hint, out var deckZ))
                break;

            dCenter = deckZ.Center - cs.TargetElevation;
            dLeft = deckZ.Left - cs.LeftEdgeElevation;
            dRight = deckZ.Right - cs.RightEdgeElevation;
            maxShift = MathF.Max(maxShift,
                MathF.Max(MathF.Abs(dCenter), MathF.Max(MathF.Abs(dLeft), MathF.Abs(dRight))));

            cs.TargetElevation = deckZ.Center;
            cs.LeftEdgeElevation = deckZ.Left;
            cs.RightEdgeElevation = deckZ.Right;
            boundary = cs;
            conformed++;
        }

        if (boundary == null)
            return null;

        var farStation = fromStart ? spanSections[^1].DistanceAlongSpline : spanSections[0].DistanceAlongSpline;
        var available = MathF.Abs(farStation - boundary.DistanceAlongSpline);
        var boundaryDelta = MathF.Max(MathF.Abs(dCenter), MathF.Max(MathF.Abs(dLeft), MathF.Abs(dRight)));
        var run = MathF.Min(
            MathF.Max(MathF.Max(boundary.EffectiveRoadWidth, DeckOverlapMinTransitionMeters),
                boundaryDelta * DeckOverlapEaseRunPerDeltaMeter),
            available);
        if (run > 0.01f)
        {
            for (; i >= 0 && i < spanSections.Count; i += step)
            {
                var cs = spanSections[i];
                var u = MathF.Abs(cs.DistanceAlongSpline - boundary.DistanceAlongSpline) / run;
                if (u >= 1f)
                    break;
                var w = (1f - u) * (1f - u) * (1f + 2f * u);
                cs.TargetElevation += dCenter * w;
                cs.LeftEdgeElevation += dLeft * w;
                cs.RightEdgeElevation += dRight * w;
            }
        }

        var zone = MathF.Abs(boundary.DistanceAlongSpline - endStation);
        return $"overlap conformed {conformed} station(s) over {zone:F1}m (maxΔ {maxShift:F2}m, ease {run:F1}m)";
    }

    /// <summary>
    /// Doc 15 §5: the read-only AREA metric behind <see cref="DeckSeamDiagnostic.OverlapMaxGapMeters"/>
    /// — max |landing-span surface − landed-on deck surface| over all overlapping stations × {center,
    /// left, right}, walked exactly like the conformance zone (same footprint test, same cap). The
    /// baseline (flag off) shows the real step a vehicle crosses in the gore; ≈0 once
    /// <c>EnableSeamlessDeckOverlap</c> has conformed the zone.
    /// </summary>
    private static (int stations, float maxGap) MeasureDeckOverlapGap(
        UnifiedRoadNetwork network,
        List<UnifiedCrossSection> spanSections,
        bool fromStart,
        DeckLandingRecord landing)
    {
        var deckSections = network.GetCrossSectionsForSpline(landing.DeckSplineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (deckSections.Count < 2 || spanSections.Count < 2)
            return (0, 0f);

        var spanLength = spanSections[^1].DistanceAlongSpline - spanSections[0].DistanceAlongSpline;
        var walkCap = MathF.Min(spanLength / 2f, DeckOverlapMaxWalkMeters);
        var endStation = fromStart ? spanSections[0].DistanceAlongSpline : spanSections[^1].DistanceAlongSpline;
        var step = fromStart ? 1 : -1;

        var hint = landing.DeckStation;
        var stations = 0;
        var maxGap = 0f;
        for (var i = fromStart ? 0 : spanSections.Count - 1; i >= 0 && i < spanSections.Count; i += step)
        {
            var cs = spanSections[i];
            if (MathF.Abs(cs.DistanceAlongSpline - endStation) > walkCap)
                break;
            if (!TrySampleOverlapSection(deckSections, cs, ref hint, out var deckZ))
                break;

            stations++;
            if (IsFinite(cs.TargetElevation))
                maxGap = MathF.Max(maxGap, MathF.Abs(cs.TargetElevation - deckZ.Center));
            if (IsFinite(cs.LeftEdgeElevation))
                maxGap = MathF.Max(maxGap, MathF.Abs(cs.LeftEdgeElevation - deckZ.Left));
            if (IsFinite(cs.RightEdgeElevation))
                maxGap = MathF.Max(maxGap, MathF.Abs(cs.RightEdgeElevation - deckZ.Right));
        }

        return (stations, maxGap);
    }

    /// <summary>
    /// Doc 14 (b) solve order, corrected on Manhattan run 214227: the landing records ARE the
    /// dependency graph — a spline that is landed ON must be refined before every spline landing on
    /// it (priority is no proxy: the Brooklyn trunk is outranked by its own ramps). Kahn's algorithm
    /// over the spline-level graph; among ready splines higher priority then smaller id (deterministic);
    /// a stall (cycle — warned separately) falls back to the same key, making first-solved the authority.
    /// </summary>
    private static List<(int SplineId, int SpanId)> OrderSpansByLandingDependencies(
        UnifiedRoadNetwork network, List<(int SplineId, int SpanId)> spanKeys)
    {
        var splineIds = spanKeys.Select(k => k.SplineId).Distinct().ToList();
        var inSet = splineIds.ToHashSet();

        // dependencies[a] = splines a lands on ⇒ they refine before a.
        var dependencies = splineIds.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var id in splineIds)
        {
            var spline = network.GetSplineById(id);
            if (spline?.StructureSegments is not { Count: > 0 }) continue;
            foreach (var seg in spline.StructureSegments)
            {
                if (seg.StartDeckLanding is { } sl && sl.DeckSplineId != id && inSet.Contains(sl.DeckSplineId))
                    dependencies[id].Add(sl.DeckSplineId);
                if (seg.EndDeckLanding is { } el && el.DeckSplineId != id && inSet.Contains(el.DeckSplineId))
                    dependencies[id].Add(el.DeckSplineId);
            }
        }

        int Priority(int id) => network.GetSplineById(id)?.Priority ?? 0;

        // True when the spline can reach itself through the dependency edges of the remaining set —
        // i.e. it is part of the cycle that stalled Kahn. A stall must be broken INSIDE the cycle:
        // run 220209 showed the naive fallback picking a cycle DEPENDENT by priority (spline 60,
        // p9500) before the 14↔462 cycle it depends on, re-creating the stale-anchor −3.77 seam.
        static bool InCycle(int id, Dictionary<int, HashSet<int>> deps, HashSet<int> remaining)
        {
            var stack = new Stack<int>(deps[id].Where(remaining.Contains));
            var seen = new HashSet<int>();
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == id) return true;
                if (!seen.Add(cur)) continue;
                foreach (var d in deps[cur].Where(remaining.Contains))
                    stack.Push(d);
            }

            return false;
        }

        var ordered = new List<int>(splineIds.Count);
        var placed = new HashSet<int>();
        var remaining = new HashSet<int>(splineIds);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => dependencies[id].All(placed.Contains)).ToList();
            if (ready.Count == 0)
                ready = remaining.Where(id => InCycle(id, dependencies, remaining)).ToList();
            if (ready.Count == 0)
                ready = [.. remaining];
            var next = ready.OrderByDescending(Priority).ThenBy(id => id).First();
            ordered.Add(next);
            placed.Add(next);
            remaining.Remove(next);
        }

        var rank = ordered.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return spanKeys.OrderBy(k => rank[k.SplineId]).ThenBy(k => k.SpanId).ToList();
    }

    /// <summary>
    /// Doc 14 caution: A landing on B while B lands on A cannot be ordered "trunk first" — physically
    /// impossible, but OSM data can produce it. Warn; the first-solved deck simply acts as authority.
    /// </summary>
    private static void WarnOnCircularDeckLandings(UnifiedRoadNetwork network)
    {
        var landings = new HashSet<(int From, int To)>();
        foreach (var spline in network.Splines)
        {
            if (spline.StructureSegments is not { Count: > 0 }) continue;
            foreach (var seg in spline.StructureSegments)
            {
                if (seg.StartDeckLanding != null)
                    landings.Add((spline.SplineId, seg.StartDeckLanding.DeckSplineId));
                if (seg.EndDeckLanding != null)
                    landings.Add((spline.SplineId, seg.EndDeckLanding.DeckSplineId));
            }
        }

        foreach (var (from, to) in landings)
            if (from < to && landings.Contains((to, from)))
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"[BRIDGE-PROFILE] WARN circular deck landing between splines {from} and {to} — " +
                    "cannot order the solve; the first-solved deck acts as authority");
    }

    private static Vector2 ApproachEndpointCenter(UnifiedRoadNetwork network, int? roadSplineId, Vector2 fallback)
    {
        if (roadSplineId is not { } id)
            return fallback;

        var sections = network.GetCrossSectionsForSpline(id).OrderBy(c => c.LocalIndex).ToList();
        if (sections.Count == 0)
            return fallback;

        // Whichever endpoint of the approach is nearer the bridge endpoint is the shared junction end.
        var firstGap = Vector2.Distance(sections[0].CenterPoint, fallback);
        var lastGap = Vector2.Distance(sections[^1].CenterPoint, fallback);
        return firstGap <= lastGap ? sections[0].CenterPoint : sections[^1].CenterPoint;
    }

    private static float GradeToDegrees(float grade) => MathF.Atan(grade) * 180f / MathF.PI;

    /// <summary>Acute angle (0–180°) between two (assumed unit) vectors.</summary>
    private static float AngleDegreesBetween(Vector2 a, Vector2 b)
    {
        var dot = Vector2.Dot(a, b);
        var cross = a.X * b.Y - a.Y * b.X;
        return MathF.Abs(MathF.Atan2(cross, dot)) * 180f / MathF.PI;
    }

    private static Vector2 SafeNormalize(Vector2 v)
    {
        var lenSq = v.LengthSquared();
        return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : Vector2.Zero;
    }

    private static string FormatSeam(BridgeSeamDiagnostic d)
    {
        var end = d.IsStart ? "start" : "end";
        if (!d.Connected)
            return $"[BRIDGE-PROFILE] spline={d.BridgeSplineId} {end} connected=no " +
                   $"z={d.BridgeEndElevation:F2} gBridge={d.BridgeGrade * 100f:F1}% (isolated endpoint)";

        return $"[BRIDGE-PROFILE] spline={d.BridgeSplineId} {end} road={d.RoadSplineId} connected=yes " +
               $"z={d.BridgeEndElevation:F2} approachZ={d.ApproachElevation:F2} zGap={d.ZGapMeters:F2} " +
               $"gBridge={d.BridgeGrade * 100f:F1}% gApproach={d.ApproachGrade * 100f:F1}% gradeΔ={d.GradeDeltaDegrees:F1}deg " +
               $"headingΔ={d.HeadingDeltaDegrees:F1}deg normalΔ={d.NormalDeltaDegrees:F1}deg " +
               $"xyGap={d.XyGapMeters:F2} widthΔ={d.WidthDeltaMeters:F2}";
    }

    private static string FormatSummary(IReadOnlyList<BridgeSeamDiagnostic> all)
    {
        var connected = all.Where(d => d.Connected).ToList();
        float MaxOr0(IEnumerable<float> xs)
        {
            var vals = xs.Where(v => !float.IsNaN(v)).ToList();
            return vals.Count > 0 ? vals.Max() : 0f;
        }

        var maxZGap = MaxOr0(connected.Select(d => MathF.Abs(d.ZGapMeters)));
        var maxGradeDelta = MaxOr0(connected.Select(d => d.GradeDeltaDegrees));
        var maxHeading = MaxOr0(connected.Select(d => d.HeadingDeltaDegrees));
        var maxXyGap = MaxOr0(connected.Select(d => d.XyGapMeters));
        var headingOver3 = connected.Count(d => !float.IsNaN(d.HeadingDeltaDegrees) && d.HeadingDeltaDegrees > 3f);

        return $"[BRIDGE-PROFILE] summary seams={all.Count} connected={connected.Count} isolated={all.Count - connected.Count} " +
               $"maxZGap={maxZGap:F2}m maxGradeΔ={maxGradeDelta:F1}deg maxHeadingΔ={maxHeading:F1}deg " +
               $"maxXyGap={maxXyGap:F2}m seamsOverHeading3deg={headingOver3}";
    }

    /// <summary>Which curve family the overshoot guard selected for a bridge span.</summary>
    public enum BridgeProfileCurve
    {
        /// <summary>No override applied (e.g. both ends isolated).</summary>
        None,

        /// <summary>Cubic Hermite — exact G0 + G1 at both ends (the normal case).</summary>
        Cubic,

        /// <summary>Degree-2 parabola — overshoot guard fallback, sacrifices grade at one end.</summary>
        Parabola,

        /// <summary>Straight chord — last-resort fallback, sacrifices grade at both ends.</summary>
        Chord,

        /// <summary>
        /// A6.5 (V2 review P0-1): the span carries planner deck pins, so the smoother already solved the held
        /// deck + rising ramps — the elevation override was SKIPPED (re-curving from the approach anchors would
        /// drop a pinned viaduct back onto its low approaches, doc 16 §3). Snapshot + edges still captured.
        /// </summary>
        Pinned
    }

    /// <summary>The outcome of applying a structural profile to one bridge. Returned for tests; also logged.</summary>
    public sealed class BridgeProfileApplication
    {
        public int BridgeSplineId { get; init; }

        /// <summary>True if the bridge's excluded sections were overridden; false if left untouched (§4.4).</summary>
        public bool Applied { get; init; }

        public BridgeProfileCurve Curve { get; init; }
        public bool StartConnected { get; init; }
        public bool EndConnected { get; init; }

        /// <summary>True if any excluded section was non-finite before override (unchained bridge rescued).</summary>
        public bool RescuedUnchained { get; init; }

        /// <summary>Applied start/end centerline elevation (m). NaN when not applied.</summary>
        public float StartElevation { get; init; } = float.NaN;
        public float EndElevation { get; init; } = float.NaN;

        /// <summary>Applied endpoint grades in the bridge +s direction (dZ/ds).</summary>
        public float StartGrade { get; init; }
        public float EndGrade { get; init; }

        public float LengthMeters { get; init; }

        /// <summary>Max deviation of the applied curve from the straight chord (m).</summary>
        public float MaxBulgeMeters { get; init; }

        /// <summary>Blend factor used by the sag cap (1 = untouched; &lt;1 = blended toward the chord).</summary>
        public float SagCapFactor { get; init; } = 1f;

        /// <summary>Vertical grade mismatch (deg) between the deck and the approach at the start abutment — the seam kink a vehicle hits.</summary>
        public float SeamKinkStartDeg { get; init; }

        /// <summary>Vertical grade mismatch (deg) between the deck and the approach at the end abutment.</summary>
        public float SeamKinkEndDeg { get; init; }

        /// <summary>Peak height (m) of the interior clearance arch added for E-A grade-separation constraints (D-4). 0 = none.</summary>
        public float InteriorLiftMeters { get; init; }

        /// <summary>Minimum deck-above-terrain clearance over the span (m), diagnostic only. NaN if unknown.</summary>
        public float MinClearanceMeters { get; init; } = float.NaN;

        /// <summary>
        /// V2 typed mode: the worst planned-crossing clearance under the span — final deck Z minus the
        /// lower member's final (possibly dip-pinned) Z. NaN when typing is off / no plan crossing here.
        /// </summary>
        public float PlanMinClearanceMeters { get; init; } = float.NaN;

        /// <summary>The binding crossing's typed separation budget (<c>RequiredSeparationMeters</c>). NaN as above.</summary>
        public float PlanRequiredSeparationMeters { get; init; } = float.NaN;

        /// <summary>How many rule-engine crossings under this span the typed diagnostic measured.</summary>
        public int PlanCrossingsChecked { get; init; }

        public string Note { get; init; } = "";
    }

    /// <summary>Aggregate result of a <see cref="RefineSpans"/> pass.</summary>
    public sealed class BridgeProfileResult
    {
        public List<BridgeProfileApplication> Applications { get; } = [];

        public int BridgesProcessed => Applications.Count;
        public int BridgesOverridden => Applications.Count(a => a.Applied);
        public int BridgesLeftIsolated => Applications.Count(a => !a.Applied);
    }
}
