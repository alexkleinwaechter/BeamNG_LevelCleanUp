using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// The bridge-elevation rule engine (plan doc 14 §4). A <b>pure</b>, side-effect-free decision layer: given a
/// settled <see cref="UnifiedRoadNetwork"/> snapshot (and optionally the terrain heightmap), it decides, per
/// merged-corridor bridge span, the <b>required deck elevation</b> and, per grade-separated crossing under that
/// span, <b>who moves</b> — raise the bridge, dip the lower road, or split the difference.
///
/// <para>It mutates nothing: it reads <see cref="UnifiedRoadNetwork.GradeSeparatedCrossings"/> (recorded earlier
/// by <see cref="NetworkJunctionDetector"/>, Phase A) plus the spans' <see cref="StructureSegment"/>s and the
/// cross-section geometry/elevations, and returns a <see cref="BridgeElevationPlan"/>. Later phases consume that
/// plan: Phase C pins the span deck Z (<c>UnifiedCrossSection.PinnedElevation</c>) so the smoother grows the
/// rising ramps, and Phase D dips the planned lower roads against the final stamped Z. This keeps the heavily
/// reviewed decision logic isolated and exhaustively unit-testable (plan §11 Phase B).</para>
///
/// <para><b>Flag-gated &amp; output-neutral:</b> the only spans it sees come from
/// <see cref="BridgeSpanFootprint"/>'s gate (<see cref="RoadSmoothingParameters.MergeStructuresIntoCorridor"/> +
/// an excluded bridge <see cref="StructureSegment"/>), so with the merge flag off there are no spans and the plan
/// is empty (<see cref="BridgeElevationPlan.IsEmpty"/>).</para>
///
/// <para><b>Decision basis (D1, §4.4):</b> the "is this an elevated, ramped viaduct?" test is driven by the
/// <b>clearance requirement</b> (<c>requiredDeckZ</c> over the obstacles + terrain beneath the span), NOT by the
/// already-flattened smoothed grade — otherwise the test would never fire on the very bug it fixes.</para>
/// </summary>
public static class BridgeElevationPlanner
{
    private const float Eps = 1e-3f;

    /// <summary>Clearance defaults used when a span has no <c>BridgeRules</c> (no-rules maps / direct-planner
    /// tests). Obstacle typing is unconditional (doc 17 §4a); the values are the COMBRI/EU defaults.</summary>
    private static readonly BridgeRuleSystemOptions DefaultClearanceRules = new();

    /// <summary>
    /// Plans deck elevations and per-crossing outcomes for every merged-corridor bridge span in the network.
    /// Pure — the network is read, never written.
    /// </summary>
    /// <param name="network">The settled road network (cross-section <c>TargetElevation</c> may be the
    /// stamped/smoothed surface where available, else NaN — see <paramref name="heightMap"/>).</param>
    /// <param name="heightMap">Optional terrain heightmap <c>[y, x]</c> for the terrain-clearance obstacle term
    /// (§4.1) and for an obstacle's Z when its road section elevation is not yet solved. Null ⇒ terrain
    /// clearance is skipped and unsolved obstacles are ignored.</param>
    /// <param name="metersPerPixel">World metres per heightmap pixel.</param>
    /// <param name="options">Tunables (§10). Null ⇒ defaults.</param>
    /// <returns>An empty plan when there are no spans (flag off / no structure segments).</returns>
    public static BridgeElevationPlan Plan(
        UnifiedRoadNetwork network,
        float[,]? heightMap = null,
        float metersPerPixel = 1f,
        BridgeElevationPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        options ??= new BridgeElevationPlannerOptions();

        var spans = new List<SpanDeckPlan>();
        var crossings = new List<CrossingPlan>();

        // A5: solve high-priority bridges first so later (lower-priority) spans see their pinned decks as
        // fixed constraints (carry). Layer ascends within a priority so a lower deck is fixed before a
        // higher one crosses it. Flag off ⇒ original per-spline order, no carry (byte-identical).
        var rules = network.Splines
            .Select(s => s.Parameters.BridgeRules)
            .FirstOrDefault(r => r != null);
        var solveOrder = rules?.EnableSpanSolveOrder == true;
        var ordered = solveOrder
            ? EnumerateSpans(network).OrderByDescending(s => s.Spline.Priority).ThenBy(s => s.Seg.Layer)
            : EnumerateSpans(network);

        // Doc 28 Step A/B (coherent underpass, gated on EnablePriorityDistribution): group grade-separated
        // crossings by lower road + station window and take ONE dip-or-raise decision per cluster. Member
        // crossings of a dip-decided cluster are resolved as coherent dips inside PlanSpan — their bridges'
        // raises are SUPPRESSED (that is why the set must exist BEFORE any span is planned).
        var clusterSeeds = rules?.EnablePriorityDistribution == true
            ? BuildUnderpassClusterSeeds(network, rules.UnderpassClusterGapMeters)
            : [];
        var coherentDips = clusterSeeds.Count > 0
            ? clusterSeeds.SelectMany(s => s.Members).Select(m => m.Crossing).ToHashSet()
            : null;

        var carry = solveOrder ? new List<PinnedCarry>() : null;
        foreach (var span in ordered)
        {
            PlanSpan(network, span, heightMap, metersPerPixel, options, spans, crossings, carry, coherentDips);
        }

        return new BridgeElevationPlan
        {
            Spans = spans,
            Crossings = crossings,
            UnderpassClusters = BuildUnderpassClusterPlans(clusterSeeds, crossings),
        };
    }

    // ── Span planning ────────────────────────────────────────────────────────────────────────────────────

    private static void PlanSpan(
        UnifiedRoadNetwork network,
        SpanContext span,
        float[,]? heightMap,
        float metersPerPixel,
        BridgeElevationPlannerOptions options,
        List<SpanDeckPlan> spans,
        List<CrossingPlan> crossings,
        List<PinnedCarry>? carry = null,
        HashSet<GradeSeparatedCrossing>? coherentDips = null)
    {
        // A2 typed separation budget (spec S = Clearance(kind) + StructuralDepth(span), envelope rule):
        // per-obstacle clearance by kind plus the §3.2 structural deck depth — which REPLACES the retired
        // DeckThicknessOffset (review P0-4: no double count). Obstacle typing is unconditional (doc 17 §4a);
        // a span without BridgeRules falls back to the COMBRI/EU clearance defaults.
        var rules = span.Spline.Parameters.BridgeRules;
        var r = rules ?? DefaultClearanceRules;
        var structuralDepth = r.StructuralDepthMeters(span.Seg.EndDistance - span.Seg.StartDistance);
        float SeparationFor(Obstacle ob) =>
            r.ClearanceFor(ob.Crossing.LowerKind, ob.Crossing.LowerElectrified, ob.Crossing.LowerNavigable) +
            structuralDepth;

        // The generic ramp-test threshold / recorded ClearanceUsed: the representative road clearance
        // (doc 17 §4a retired the generic MinBridgeClearanceMeters in favour of the typed Road clearance).
        var c = r.RoadClearanceMeters;

        // Approach (ramp) elevations: the in-spline sections immediately outside the span on each side.
        var (approachLeft, approachRight) = ApproachElevations(span, heightMap, metersPerPixel, options);
        var approachesBothSides = IsFinite(approachLeft) && IsFinite(approachRight);
        var approachDeckZ = AverageFinite(approachLeft, approachRight);

        // Graded deck (render review #3): the deck reference is the CHORD between the approach elevations
        // — a deck on the road's own grade — instead of the flat span-average. Per-obstacle deficits are
        // measured against the chord AT THE CROSSING STATION; the deck is then lifted uniformly by the
        // worst deficit (lift 0 ⇒ pins follow the chord exactly ⇒ flush abutment seams). A flat deck on a
        // sloping road can never meet both approaches. Flag off ⇒ legacy flat behaviour, byte-identical.
        var chordStart = IsFinite(approachLeft) ? approachLeft : approachRight;
        var chordEnd = IsFinite(approachRight) ? approachRight : approachLeft;
        var graded = rules?.EnableGradedDeck == true && IsFinite(chordStart) && IsFinite(chordEnd);

        float ChordAt(float dist)
        {
            var len = span.Seg.EndDistance - span.Seg.StartDistance;
            if (len <= Eps) return chordStart;
            var t = Math.Clamp((dist - span.Seg.StartDistance) / len, 0f, 1f);
            return chordStart + (chordEnd - chordStart) * t;
        }

        // The deck-level reference an obstacle's deficit is measured against (chord at its station when
        // graded; the legacy flat span-average otherwise).
        float DeckRefAt(Obstacle ob) => graded
            ? ChordAt(UpperStationOf(span, ob.Crossing.CrossingXY))
            : approachDeckZ;

        // Terrain is NEVER an obstacle under typing (§3.1 clearance 0): the deck follows the ROAD profile;
        // high ground under a span is handled by the excavator's daylight shave + Phase B, never a raise
        // (doc-20: raising to clear terrain produced the floating decks / +5 m humps over GeoTIFF-flat
        // streams). The legacy terrain-max-under-span obstacle term was removed with typing (doc 17 §4a).

        // Under-road obstacles: the grade-separated crossings whose UPPER member is this corridor and whose XY
        // lies inside this span's footprint. The lower member's solved Z (or terrain) is the obstacle height.
        var obstacles = CollectObstacles(network, span, heightMap, metersPerPixel, options);
        if (carry != null)
            AddCarryObstacles(span, carry, obstacles);

        // requiredDeckZ to fully clear EVERYTHING under the span (each obstacle + its separation budget).
        // Graded: additionally track the worst STATION-LOCAL deficit against the chord — the uniform lift.
        var requiredDeckZFull = float.NegativeInfinity;
        var liftFull = float.NegativeInfinity;

        // Doc 28 Step B: a crossing that belongs to a dip-decided underpass cluster is cleared by the
        // road's coherent dip — it NEVER demands a deck raise, so it is excluded from the clearance
        // requirement (and thereby from the ramp/infeasibility tests below). The plans are built UP FRONT
        // (not lazily at emission) because a DEGENERATE member — the road sits ≥ the dip cap above the
        // un-raised deck, so even a full-cap dip cannot reach below it — yields null and must fall back to
        // the normal rules INCLUDING its raise contribution here.
        Dictionary<GradeSeparatedCrossing, CrossingPlan>? coherentPlans = null;
        if (rules != null && coherentDips != null)
            foreach (var ob in obstacles)
            {
                if (!coherentDips.Contains(ob.Crossing))
                    continue;
                var coherent = BuildCoherentDip(ob, DeckRefAt(ob), SeparationFor(ob), rules);
                if (coherent != null)
                    (coherentPlans ??= [])[ob.Crossing] = coherent;
            }

        bool IsCoherentDip(Obstacle o) => coherentPlans?.ContainsKey(o.Crossing) == true;

        foreach (var ob in obstacles)
        {
            if (IsCoherentDip(ob)) continue;
            var required = ob.Z + SeparationFor(ob);
            requiredDeckZFull = MathF.Max(requiredDeckZFull, required);
            liftFull = MathF.Max(liftFull, required - DeckRefAt(ob));
        }

        // ── Ramp test (D1, §4.2): does the structure clearly want to stand as an elevated, ramped viaduct? ──
        // Primary driver = the clearance requirement sits at least a full clearance-height C above the approach
        // on BOTH sides (the deck must genuinely climb above its low approaches — a real ramp), and there is
        // approach length on both sides to build it. The boundary is inclusive: a deck that climbs exactly C
        // above its approaches IS the headline interchange-flyover case (its ramps just clear the under-roads,
        // §1a spline 394), so it must classify as Rule 1, not fall through to a dip. (The raw-terrain grade is a
        // secondary, recorded confirmation only — never the gate — to avoid the §4.4 circularity.)
        var isRamp = graded
            ? IsFinite(liftFull) && approachesBothSides && liftFull >= c - Eps
            : IsFinite(requiredDeckZFull) && approachesBothSides &&
              requiredDeckZFull - approachLeft >= c - Eps &&
              requiredDeckZFull - approachRight >= c - Eps;

        // A4 (R4.5): the raise room the span's approaches offer — ramp length on the shorter side times the
        // §3.3 slope for the corridor's class. Only computed when the feasibility flag is on.
        var feasibility = rules?.EnableRampFeasibility == true;
        float raiseMaxNormal = float.PositiveInfinity, raiseMaxAbs = float.PositiveInfinity;
        if (feasibility)
        {
            var upperStep = BridgeRuleSystemOptions.ClassStepFor(span.Spline.OsmRoadType);
            var rampLen = MathF.Min(
                MeasureRampLength(network, span.Spline, span.Seg.StartDistance, forward: false),
                MeasureRampLength(network, span.Spline, span.Seg.EndDistance, forward: true));
            raiseMaxNormal = rampLen * BridgeRuleSystemOptions.NormalMaxSlopePercent(upperStep) / 100f;
            raiseMaxAbs = rampLen * BridgeRuleSystemOptions.AbsoluteMaxSlopePercent(upperStep) / 100f;
        }

        float spanPinZ;
        var spanLift = float.NegativeInfinity; // graded: uniform lift above the chord
        var spanCrossings = new List<CrossingPlan>();

        if (isRamp)
        {
            // Rule 1 — a ramped viaduct: the deck normally raises to clear everything and leaves every road
            // under it alone. EXCEPTION (spec 2026-07-01): when that raise can't be ramped in the available
            // approach length (feasibility warning) AND an under-road is strictly lower priority, dip those
            // roads instead of forcing the motorway up. The deck then rises only for the obstacles that still
            // demand it (rail/water/equal-or-higher priority/bridge-under/terrain).
            var raiseAboveApproaches = graded
                ? liftFull
                : requiredDeckZFull - MathF.Min(approachLeft, approachRight);
            var infeasible = feasibility && approachesBothSides && raiseAboveApproaches > raiseMaxAbs + Eps;

            // A strictly-lower-priority under-ROAD that may be dipped (mirrors the rail/water/bridge veto).
            bool IsDippable(Obstacle o) =>
                o.Crossing.HasLowerSpline &&
                o.Crossing.LowerKind == BridgeObstacleKind.Road &&
                o.Crossing.LowerPriority < o.Crossing.UpperPriority &&
                !(rules?.EnableSpanSolveOrder == true && o.Crossing.LowerIsBridge);

            var dipFallback = infeasible && obstacles.Any(IsDippable);

            // Recompute the deck requirement from the obstacles that STILL force a raise. When dipFallback is
            // false this reproduces requiredDeckZFull/liftFull exactly (byte-identical feasible viaduct).
            spanPinZ = float.NegativeInfinity;
            spanLift = float.NegativeInfinity;
            foreach (var ob in obstacles)
            {
                if (IsCoherentDip(ob)) continue; // doc 28: cleared by the cluster underpass, never a raise
                if (dipFallback && IsDippable(ob)) continue; // dipped ⇒ no longer raises the deck
                var required = ob.Z + SeparationFor(ob);
                spanPinZ = MathF.Max(spanPinZ, required);
                spanLift = MathF.Max(spanLift, required - DeckRefAt(ob));
            }

            // Warn only if the (possibly reduced) raise is still over-steep.
            var reducedRaise = graded
                ? spanLift
                : IsFinite(spanPinZ) ? spanPinZ - MathF.Min(approachLeft, approachRight) : float.NegativeInfinity;
            var rampWarning = feasibility && approachesBothSides && reducedRaise > raiseMaxAbs + Eps
                ? "Rule-1 raise exceeds absolute ramp slope for the approach length"
                : null;

            foreach (var ob in obstacles)
            {
                if (IsCoherentDip(ob))
                {
                    // Doc 28 Step B/D: the cluster decided — the road dips under this bridge; its raise is
                    // suppressed (even when the bridge outranks nothing, e.g. winningen 47/42).
                    spanCrossings.Add(coherentPlans![ob.Crossing]);
                }
                else if (dipFallback && IsDippable(ob))
                {
                    var deckRef = DeckRefAt(ob);
                    var sep = SeparationFor(ob);
                    spanCrossings.Add(new CrossingPlan
                    {
                        Crossing = ob.Crossing,
                        ObstacleZEstimate = ob.Z,
                        Action = BridgeElevationAction.DipLowerRoad,
                        LowerRoadTargetZ = deckRef - sep,
                        DipDepthMeters = ob.Z + sep - deckRef,
                        RequiredSeparationMeters = sep,
                        // Observability (spec 2026-07-01 M2): this span's Rule-1 raise was infeasible for its
                        // approach length, so we dipped the lower-priority road instead of raising the deck.
                        // Surfaced through Warning so it appears in the [BRIDGE-PLAN] WARN log (UnifiedRoadSmoother),
                        // replacing the "Rule-1 raise exceeds ramp slope" line these spans no longer emit.
                        Warning = "Rule-1 raise infeasible for the approach length — dipped lower-priority road " +
                                  "instead (deck held at approach level)",
                    });
                }
                else
                {
                    spanCrossings.Add(new CrossingPlan
                    {
                        Crossing = ob.Crossing,
                        ObstacleZEstimate = ob.Z,
                        Action = BridgeElevationAction.RaiseBridge,
                        DeckTargetZ = graded ? DeckRefAt(ob) + spanLift : spanPinZ,
                        RequiredSeparationMeters = SeparationFor(ob),
                        Warning = rampWarning,
                    });
                }
            }
        }
        else
        {
            // Not a ramped viaduct: decide per-crossing by priority (Rules 2 & 3). The deck only rises for
            // veto-raises and the raise-half of a split; pure dips leave the deck at approach level.
            spanPinZ = float.NegativeInfinity;

            foreach (var ob in obstacles)
            {
                var deckRef = DeckRefAt(ob);
                CrossingPlan plan;
                if (IsCoherentDip(ob))
                {
                    // Doc 28 Step B: the cluster decided — coherent dip; no §3.5 split, no feasibility
                    // re-shuffle onto the deck (the underpass must stay one smooth well).
                    plan = coherentPlans![ob.Crossing];
                }
                else
                {
                    plan = ClassifyNonRampCrossing(
                        ob, deckRef, SeparationFor(ob), rules, options.GradeSepSplitRatio);
                    if (feasibility)
                        plan = ApplyRampFeasibility(
                            network, plan, ob, deckRef, structuralDepth, rules!, raiseMaxNormal, raiseMaxAbs);
                }

                spanCrossings.Add(plan);
                if (IsFinite(plan.DeckTargetZ))
                {
                    spanPinZ = MathF.Max(spanPinZ, plan.DeckTargetZ);
                    spanLift = MathF.Max(spanLift, plan.DeckTargetZ - deckRef);
                }
            }
        }

        // Graded: raised ⇔ a positive uniform lift above the chord; the deck profile is chord + lift.
        // Legacy: raised ⇔ the flat pin exceeds the approach average; the deck profile is the flat pin.
        var lift = graded && IsFinite(spanLift) ? MathF.Max(0f, spanLift) : 0f;
        var isRaised = graded
            ? lift > Eps
            : IsFinite(spanPinZ) && (!IsFinite(approachDeckZ) || spanPinZ > approachDeckZ + Eps);
        var deckZ = graded
            ? isRaised ? MathF.Max(chordStart, chordEnd) + lift : approachDeckZ
            : isRaised ? spanPinZ : approachDeckZ;

        // Reconcile every dip/split crossing against the FINAL deck Z at ITS station. When another crossing
        // (veto / split / terrain) raised the deck above this one's own first cut, the shared higher deck
        // shrinks — or removes — this crossing's residual dip. The Split label is preserved.
        for (var i = 0; i < spanCrossings.Count; i++)
        {
            var localDeck = graded ? DeckRefAt(obstacles[i]) + lift : deckZ;
            spanCrossings[i] = ReconcileDipAgainstDeck(spanCrossings[i], localDeck);
        }

        // Amendment 03 v3 ("give the bridge cross-sections", render #7: hard-held chords stepped one
        // abutment and had zero curvature while every UN-pinned cubic span was seamKink≈0): under sparse
        // mode the pins are HUMP-shaped — the chord plus a per-crossing eased rise to each clearance
        // target, NO uniform end lift — and they are consumed as SOFT raw-input shaping only (the filter
        // solves the span like road; nothing is hard-held, so nothing can step). Legacy/graded pins stay
        // hard-held when sparse is off.
        var sparse = rules?.EnableSparseDeckConstraints == true;
        var pins = isRaised
            ? sparse && graded
                ? BuildUniformSoftPins(span, ChordAt, obstacles, spanCrossings, SeparationFor)
                : graded ? BuildGradedPins(span, ChordAt, lift) : BuildPins(span, deckZ)
            : sparse && graded
                // 2026-07-21 #3 (render: trunk deck SAGGED once the no-raise law un-raised its span): an
                // UNRAISED sparse span still must not let the under-road valley into the filter input —
                // the box window otherwise drags the deck down into the underpass (the render-#6 sink the
                // soft shaping exists for; previously masked because the graded shares raised every
                // clearance span). Dip-only spans have no raise/veto/split crossings ⇒ lift is 0 ⇒ these
                // pins are the pure boundary chord, rise 0: deck level with its approaches, never above.
                ? BuildUniformSoftPins(span, ChordAt, obstacles, spanCrossings, SeparationFor)
                : [];

        // A5 carry: expose this span's pinned deck sections to later (lower-priority) spans as fixed
        // constraints. Un-raised decks need no carry — their sections keep TargetElevation, which the
        // regular crossing path already reads.
        if (carry != null && isRaised)
            foreach (var pin in pins)
                carry.Add(new PinnedCarry(
                    pin.Section.CenterPoint, pin.DeckZ, span.Seg.Layer, span.Spline.SplineId,
                    span.Spline.Priority, span.Spline.OsmRoadType));

        spans.Add(new SpanDeckPlan
        {
            OwnerSplineId = span.Spline.SplineId,
            SpanId = span.Footprint.SpanId,
            StartDistance = span.Seg.StartDistance,
            EndDistance = span.Seg.EndDistance,
            Layer = span.Seg.Layer,
            RequiredDeckZ = deckZ,
            IsRaised = isRaised,
            ApproachZLeft = approachLeft,
            ApproachZRight = approachRight,
            ClearanceUsed = c,
            StructuralDepthMeters = structuralDepth,
            Pins = pins,
        });
        crossings.AddRange(spanCrossings);
    }

    /// <summary>
    /// Rules 2 &amp; 3 for one crossing of a non-ramp span. With <c>EnablePriorityDistribution</c> (A3, §3.5)
    /// the deficit D is shared between raising the deck (R = r·D) and dipping the lower road (L = (1−r)·D),
    /// where r comes from <see cref="BridgeRuleSystemOptions.RaiseShareFor"/> over the quantized class-step
    /// delta (<see cref="BridgeRuleSystemOptions.ClassStepFor"/> on the OSM classes — NEVER the stored
    /// composite priorities, review P0-4). Legacy (flag off): the lower-priority road loses (dip), unless it
    /// outranks the bridge (veto ⇒ raise the deck), and equal priority splits the deficit (§4.2).
    /// <paramref name="sep"/> is the crossing's separation budget S (A2: per-kind clearance + structural
    /// depth when typing is on; the base clearance C otherwise).
    /// </summary>
    private static CrossingPlan ClassifyNonRampCrossing(
        Obstacle ob, float approachDeckZ, float sep, BridgeRuleSystemOptions? rules, float splitRatio)
    {
        // How far short the un-raised deck (sitting at the approach level) is of clearing this obstacle.
        var deficit = ob.Z + sep - approachDeckZ;
        if (!IsFinite(deficit) || deficit <= Eps)
            return new CrossingPlan
            {
                Crossing = ob.Crossing,
                ObstacleZEstimate = ob.Z,
                Action = BridgeElevationAction.AlreadyClears,
                RequiredSeparationMeters = sep,
            };

        if (ob.Crossing.LowerKind is BridgeObstacleKind.Rail or BridgeObstacleKind.Water ||
            !ob.Crossing.HasLowerSpline ||
            (rules?.EnableSpanSolveOrder == true && ob.Crossing.LowerIsBridge))
            // A1/§3.5: rail and water can never be lowered (L=0 always) — raise the deck interior instead,
            // exactly like a priority veto. ANY synthetic crossing (no lower spline — incl. the Road-kind
            // self-crossing) has nothing to dip either: without this a §3.5 split would assign a phantom
            // dip share that no pass can execute, silently under-clearing the deck.
            // A5: a pinned bridge deck below is equally immovable — never dip another bridge's deck.
            return new CrossingPlan
            {
                Crossing = ob.Crossing,
                ObstacleZEstimate = ob.Z,
                Action = BridgeElevationAction.RaiseBridgeVeto,
                DeckTargetZ = ob.Z + sep,
                RequiredSeparationMeters = sep,
            };

        if (rules?.EnablePriorityDistribution == true)
        {
            // §3.5 (A3): share the deficit by Δp = stepUpper − stepLower from the OSM highway classes.
            // The more important road keeps its smooth profile; *_link inherits its parent's step inside
            // ClassStepFor, so a motorway over its own exit ramp is Δp=0 ⇒ 50/50 — never an 80 % gore dip.
            var dp = BridgeRuleSystemOptions.ClassStepFor(ob.Crossing.UpperOsmClass) -
                     BridgeRuleSystemOptions.ClassStepFor(ob.Crossing.LowerOsmClass);
            var share = BridgeRuleSystemOptions.RaiseShareFor(dp);
            if (share <= 0f)
                // 2026-07-21 (bridge 675150484): Δp > 0 ⇒ zero raise share — degenerate to an HONEST pure
                // dip, not a Split with a 0 m deck target: the span is then never marked raised, the
                // uniform soft lift ignores the crossing, and the logs read "dip", which is what happens.
                return new CrossingPlan
                {
                    Crossing = ob.Crossing,
                    ObstacleZEstimate = ob.Z,
                    Action = BridgeElevationAction.DipLowerRoad,
                    LowerRoadTargetZ = ob.Z - deficit,
                    DipDepthMeters = deficit,
                    RequiredSeparationMeters = sep,
                };

            if (share >= 1f)
                // Mirror (same session): Δp < 0 ⇒ the lower road outranks the bridge's road and must never
                // be dipped — a pure raise veto, exactly like binary Rule 2.
                return new CrossingPlan
                {
                    Crossing = ob.Crossing,
                    ObstacleZEstimate = ob.Z,
                    Action = BridgeElevationAction.RaiseBridgeVeto,
                    DeckTargetZ = ob.Z + sep,
                    RequiredSeparationMeters = sep,
                };

            var raiseBy = share * deficit;
            var dipBy = deficit - raiseBy;
            return new CrossingPlan
            {
                Crossing = ob.Crossing,
                ObstacleZEstimate = ob.Z,
                Action = BridgeElevationAction.Split,
                DeckTargetZ = approachDeckZ + raiseBy,
                LowerRoadTargetZ = ob.Z - dipBy,
                DipDepthMeters = dipBy,
                RequiredSeparationMeters = sep,
            };
        }

        if (ob.Crossing.LowerPriority < ob.Crossing.UpperPriority)
            // Rule 2 — lower-priority road loses: dip it the full deficit; deck stays at approach level.
            return new CrossingPlan
            {
                Crossing = ob.Crossing,
                ObstacleZEstimate = ob.Z,
                Action = BridgeElevationAction.DipLowerRoad,
                LowerRoadTargetZ = ob.Z - deficit,
                DipDepthMeters = deficit,
                RequiredSeparationMeters = sep,
            };

        if (ob.Crossing.LowerPriority > ob.Crossing.UpperPriority)
            // Rule 2 — the lower road outranks the bridge: veto the dip, raise the deck interior instead.
            return new CrossingPlan
            {
                Crossing = ob.Crossing,
                ObstacleZEstimate = ob.Z,
                Action = BridgeElevationAction.RaiseBridgeVeto,
                DeckTargetZ = ob.Z + sep,
                RequiredSeparationMeters = sep,
            };

        // Rule 3 — equal priority, not a ramp: split. Raise the deck by split·deficit, dip the road by the rest.
        var raise = splitRatio * deficit;
        var dip = (1f - splitRatio) * deficit;
        return new CrossingPlan
        {
            Crossing = ob.Crossing,
            Action = BridgeElevationAction.Split,
            DeckTargetZ = approachDeckZ + raise,
            LowerRoadTargetZ = ob.Z - dip,
            DipDepthMeters = dip,
            RequiredSeparationMeters = sep,
        };
    }

    // ── Doc 28 coherent underpass (Steps A/B/D) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Doc 28 Step B/D: the coherent-dip outcome for one member crossing of a dip-decided underpass
    /// cluster. The road dips the full deficit below this bridge's (un-raised) deck, bounded by
    /// <see cref="BridgeRuleSystemOptions.MaxUnderpassDipMeters"/>. Over-cap = cap + warn, stay coherent:
    /// the accepted residual REDUCES the separation budget (mirroring the R4-step-7 mechanism) so the
    /// post-solve verify does not carve the accepted shortfall back in per-crossing fragments — and the
    /// bridges are never raised for it.
    ///
    /// <para>Returns null for a DEGENERATE member: the road sits at/above the un-raised deck by ≥ the cap,
    /// so even a full-cap dip cannot get it meaningfully below the deck — the reduced separation would go
    /// ≤ 0, which the verify path reads as "unset" (full-clearance A7 carve), the plan-clearance diagnostic
    /// skips, and the reconcile turns into a road target ABOVE the deck. Such a crossing is not coherently
    /// dippable; the caller falls back to the normal per-crossing rules (raise/veto/split) for it.</para>
    /// </summary>
    private static CrossingPlan? BuildCoherentDip(
        Obstacle ob, float deckRef, float sep, BridgeRuleSystemOptions rules)
    {
        var deficit = ob.Z + sep - deckRef;
        if (!IsFinite(deficit))
            return null;
        if (deficit <= Eps)
            return new CrossingPlan
            {
                Crossing = ob.Crossing,
                ObstacleZEstimate = ob.Z,
                Action = BridgeElevationAction.AlreadyClears,
                RequiredSeparationMeters = sep,
            };

        var dip = MathF.Min(deficit, MathF.Max(0f, rules.MaxUnderpassDipMeters));
        var residual = deficit - dip;
        if (residual >= sep - 0.01f)
            return null; // degenerate: the accepted residual would consume the whole separation budget

        return new CrossingPlan
        {
            Crossing = ob.Crossing,
            ObstacleZEstimate = ob.Z,
            Action = BridgeElevationAction.DipLowerRoad,
            LowerRoadTargetZ = ob.Z - dip,
            DipDepthMeters = dip,
            RequiredSeparationMeters = sep - residual,
            AcceptedResidualMeters = residual > Eps ? residual : 0f,
            Warning = residual > Eps
                ? $"coherent underpass: dip capped at {rules.MaxUnderpassDipMeters:F1} m — " +
                  $"{residual:F2} m under-clearance accepted (bridges not raised for the residual)"
                : "coherent underpass: dipped under the bridge cluster (deck held at approach level)",
        };
    }

    /// <summary>One prospective underpass cluster (doc 28 Step A), members in lower-road station order.</summary>
    private sealed record UnderpassClusterSeed(
        int LowerSplineId, List<(GradeSeparatedCrossing Crossing, float Station)> Members);

    /// <summary>
    /// Doc 28 Step A + the Step-B decision: groups the network's road-under-bridge crossings by
    /// <c>LowerSplineId</c>, splits each group where the along-road station gap exceeds
    /// <paramref name="gapMeters"/>, and keeps only the clusters that (a) have ≥ 2 crossings — a single
    /// crossing is owned by the existing per-crossing rules — and (b) decide to DIP: the road is strictly
    /// outranked by the highest-priority bridge of the cluster. Rail/water (no lower spline) and
    /// bridge-under-bridge crossings never cluster (they can never be dipped).
    /// </summary>
    private static List<UnderpassClusterSeed> BuildUnderpassClusterSeeds(
        UnifiedRoadNetwork network, float gapMeters)
    {
        var byLower = new Dictionary<int, List<(GradeSeparatedCrossing Crossing, float Station)>>();
        foreach (var crossing in network.GradeSeparatedCrossings)
        {
            if (!crossing.HasLowerSpline || crossing.LowerKind != BridgeObstacleKind.Road)
                continue;
            if (crossing.LowerIsBridge || !crossing.UpperIsBridge)
                continue;

            var section = NearestSection(network, crossing.LowerSplineId, crossing.CrossingXY);
            if (section == null)
                continue;

            if (!byLower.TryGetValue(crossing.LowerSplineId, out var list))
                byLower[crossing.LowerSplineId] = list = [];
            list.Add((crossing, section.DistanceAlongSpline));
        }

        var seeds = new List<UnderpassClusterSeed>();
        foreach (var (lowerId, members) in byLower)
        {
            members.Sort((a, b) => a.Station.CompareTo(b.Station));

            var start = 0;
            for (var i = 1; i <= members.Count; i++)
            {
                if (i < members.Count && members[i].Station - members[i - 1].Station <= gapMeters)
                    continue; // still within the window — same cluster

                var slice = members[start..i];
                start = i;
                if (slice.Count < 2)
                    continue;

                var maxUpperPriority = slice.Max(m => m.Crossing.UpperPriority);
                if (slice[0].Crossing.LowerPriority >= maxUpperPriority)
                    continue; // the road outranks every bridge → bridges raise, road stays flat (today)

                seeds.Add(new UnderpassClusterSeed(lowerId, slice));
            }
        }

        return seeds;
    }

    /// <summary>
    /// Assembles the plan-surface <see cref="UnderpassClusterPlan"/>s from the seeds and the planned
    /// crossings (doc 28): per cluster the bridges cleared, the deepest applied dip and the worst accepted
    /// over-cap residual — the dip appliers merge each cluster into ONE envelope well off this record, and
    /// the smoother logs the per-cluster observability line from it.
    /// </summary>
    private static IReadOnlyList<UnderpassClusterPlan> BuildUnderpassClusterPlans(
        List<UnderpassClusterSeed> seeds, List<CrossingPlan> crossings)
    {
        if (seeds.Count == 0)
            return [];

        var byCrossing = new Dictionary<GradeSeparatedCrossing, CrossingPlan>(crossings.Count);
        foreach (var cp in crossings)
            byCrossing[cp.Crossing] = cp;

        var plans = new List<UnderpassClusterPlan>(seeds.Count);
        foreach (var seed in seeds)
        {
            var maxDip = 0f;
            var residual = 0f;
            var dips = 0;
            foreach (var (crossing, _) in seed.Members)
            {
                if (!byCrossing.TryGetValue(crossing, out var cp))
                    continue;
                if (cp.Action != BridgeElevationAction.DipLowerRoad)
                    continue; // degenerate fallback (raise/veto/split) or reconciled-away — no dip to merge
                dips++;
                maxDip = MathF.Max(maxDip, cp.DipDepthMeters);
                residual = MathF.Max(residual, cp.AcceptedResidualMeters);
            }

            if (dips == 0)
                continue; // no member actually dips — there is no coherent underpass to merge or log

            plans.Add(new UnderpassClusterPlan
            {
                LowerSplineId = seed.LowerSplineId,
                Crossings = seed.Members.Select(m => m.Crossing).ToList(),
                UpperSplineIds = seed.Members.Select(m => m.Crossing.UpperSplineId).Distinct().ToList(),
                StartStation = seed.Members[0].Station,
                EndStation = seed.Members[^1].Station,
                MaxDipMeters = maxDip,
                CappedResidualMeters = residual,
            });
        }

        return plans;
    }

    /// <summary>
    /// Recomputes a dip/split crossing's residual against the final deck Z: if the deck now clears the obstacle,
    /// the dip vanishes (<see cref="BridgeElevationAction.AlreadyClears"/>); otherwise the lower road dips just
    /// far enough to keep its separation budget (<see cref="CrossingPlan.RequiredSeparationMeters"/>) below the
    /// deck top. The original action label is preserved — a <see cref="BridgeElevationAction.Split"/> stays a
    /// split (deck raised + road dipped), only its dip portion is re-fitted to the shared deck.
    /// Raise/veto/already-clears crossings pass through unchanged.
    /// </summary>
    private static CrossingPlan ReconcileDipAgainstDeck(CrossingPlan plan, float deckZ)
    {
        if (plan.Action is not (BridgeElevationAction.DipLowerRoad or BridgeElevationAction.Split))
            return plan;
        if (!IsFinite(deckZ))
            return plan;

        var obstacleZ = plan.LowerRoadTargetZ + plan.DipDepthMeters; // recover the original obstacle Z
        var roadTarget = deckZ - plan.RequiredSeparationMeters; // the road must sit S below the deck top
        var dip = obstacleZ - roadTarget;
        if (dip <= Eps)
            return new CrossingPlan
            {
                Crossing = plan.Crossing,
                ObstacleZEstimate = plan.ObstacleZEstimate,
                Action = BridgeElevationAction.AlreadyClears,
                RequiredSeparationMeters = plan.RequiredSeparationMeters,
            };

        return plan with
        {
            LowerRoadTargetZ = roadTarget,
            DipDepthMeters = dip,
            DeckTargetZ = plan.Action == BridgeElevationAction.Split ? deckZ : float.NaN,
        };
    }

    // ── A4 ramp feasibility (R4.5) ───────────────────────────────────────────────────────────────────────

    /// <summary>Junction safety margin (m) a ramp / dip well must stop short of, so a junction's
    /// harmonized elevation is never disturbed. SINGLE SOURCE OF TRUTH for this margin across the
    /// planner/resolver layers — <c>GradeSeparationResolver.JunctionClearanceMarginMeters</c> derives
    /// from this constant. Lowered 8 → 2 so dip wells and ramps may reach closer to junctions
    /// (the smoother solves the pinned well continuously and the junction blender is pin-aware).</summary>
    public const float JunctionMarginMeters = 2f;

    /// <summary>
    /// A4 (R4.5): the room available to build a ramp on <paramref name="spline"/> from
    /// <paramref name="station"/> in one direction — the generalization of
    /// <c>GradeSeparationResolver.ClampRampToJunctions</c>. The ramp stops at the way end, at the nearest
    /// junction on this spline (less the safety margin; a junction within the margin ⇒ 0 — the
    /// "junction-in-sag" rule), and at the near edge of the next structure span (a ramp cannot run onto
    /// another deck). <paramref name="ignoreJunctions"/> (doc 05 §4.3): measure only the way-end /
    /// structure-span room — for callers that re-pin the junctions inside the run to the ramp/well line
    /// pre-smooth instead of stopping short of them.
    /// </summary>
    public static float MeasureRampLength(
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, float station, bool forward,
        bool ignoreJunctions = false)
    {
        var limit = forward ? spline.TotalLengthMeters - station : station; // way end
        if (limit < 0f) limit = 0f;

        if (!ignoreJunctions)
            foreach (var junction in network.Junctions)
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.Spline.SplineId != spline.SplineId)
                    continue;

                var d = contributor.CrossSection.DistanceAlongSpline - station;
                if (MathF.Abs(d) <= 0.01f)
                    return 0f; // junction at the station itself
                if (forward ? d > 0f : d < 0f)
                    limit = MathF.Min(limit, MathF.Max(0f, MathF.Abs(d) - JunctionMarginMeters));
            }

        if (spline.StructureSegments != null)
            foreach (var seg in spline.StructureSegments)
            {
                if (forward && seg.StartDistance > station + 0.01f)
                    limit = MathF.Min(limit, seg.StartDistance - station);
                else if (!forward && seg.EndDistance < station - 0.01f)
                    limit = MathF.Min(limit, station - seg.EndDistance);
            }

        return limit;
    }

    /// <summary>
    /// A4 (R4.5): clamps a non-ramp crossing's ideal raise/dip to what the available ramp room and the cut
    /// limits permit, then escalates per spec R4: (1) refill from whichever side still has NORMAL-slope
    /// room, (2) absolute slopes + hard cut depth, (3) reduced ROAD clearance (4.2 m) + warn, (4) take the
    /// rest on the deck anyway + warn (the deck must clear — the approach ramp ends up steeper than the
    /// absolute table value). Raise room comes from the SPAN approaches (<paramref name="raiseMaxNormal"/>/
    /// <paramref name="raiseMaxAbs"/>); dip room is measured on the lower road here. Junction-in-sag ⇒ the
    /// lower ramp room is 0 ⇒ L_max = 0 (the whole deficit moves to the deck) — LEGACY mode only: in
    /// sparse mode junctions yield to the well (doc 05 §4.3) and the room ignores them. 2026-07-21 user
    /// law: refills never cross party lines — a pure-dip plan's deck and a veto plan's road are off
    /// limits; only the mandatory last resort may still put residue on the deck.
    /// </summary>
    private static CrossingPlan ApplyRampFeasibility(
        UnifiedRoadNetwork network, CrossingPlan plan, Obstacle ob, float approachDeckZ,
        float structuralDepth, BridgeRuleSystemOptions rules, float raiseMaxNormal, float raiseMaxAbs)
    {
        if (plan.Action == BridgeElevationAction.AlreadyClears || !IsFinite(approachDeckZ))
            return plan;

        var sep = plan.RequiredSeparationMeters;
        var deficit = ob.Z + sep - approachDeckZ;
        if (deficit <= Eps)
            return plan;

        // Dip room on the lower road (0 for rail/water/synthetic/bridge decks — they are never lowered).
        // 2026-07-21 (bridge 675150484, run 145158): in SPARSE mode junctions do NOT box the dip — the
        // well executor measures its room with ignoreJunctions and re-pins junctions inside the well DOWN
        // to the well line (doc 05 §4.3). Measuring junction-clamped room here made A4 believe 17 m of
        // room existed where the executor had hundreds, and the "unachievable" remainder raised the deck.
        float dipMaxNormal = 0f, dipMaxAbs = 0f;
        if (ob.Crossing.LowerKind == BridgeObstacleKind.Road && ob.LowerSpline != null &&
            !ob.Crossing.LowerIsBridge)
        {
            var junctionsYield = rules.EnableSparseDeckConstraints;
            var lowerStep = BridgeRuleSystemOptions.ClassStepFor(ob.Crossing.LowerOsmClass);
            var rampLen = MathF.Min(
                MeasureRampLength(network, ob.LowerSpline, ob.LowerStation, forward: false,
                    ignoreJunctions: junctionsYield),
                MeasureRampLength(network, ob.LowerSpline, ob.LowerStation, forward: true,
                    ignoreJunctions: junctionsYield));
            dipMaxNormal = MathF.Min(rules.MaxCutDepthMeters,
                rampLen * BridgeRuleSystemOptions.NormalMaxSlopePercent(lowerStep) / 100f);
            dipMaxAbs = MathF.Min(rules.MaxCutDepthHardMeters,
                rampLen * BridgeRuleSystemOptions.AbsoluteMaxSlopePercent(lowerStep) / 100f);
        }

        var idealRaise = IsFinite(plan.DeckTargetZ) ? MathF.Max(0f, plan.DeckTargetZ - approachDeckZ) : 0f;
        var idealDip = MathF.Max(0f, plan.DipDepthMeters);

        // User law 2026-07-21 (bridge 675150484): the rules already decided WHO moves — the feasibility
        // escalation must not quietly re-assign deficit to the party the rules protected. A pure-dip
        // plan's deck is not a refill source (it remains only the mandatory last resort below), and a
        // veto plan's outranking road is never dipped at all.
        var deckMayRefill = plan.Action is not BridgeElevationAction.DipLowerRoad;
        var dipMayRefill = plan.Action is BridgeElevationAction.DipLowerRoad or BridgeElevationAction.Split;

        var raise = MathF.Min(idealRaise, raiseMaxNormal);
        var dip = MathF.Min(idealDip, dipMaxNormal);
        string? warning = null;

        var rem = deficit - raise - dip;
        if (rem > Eps && deckMayRefill) // refill from normal-slope slack, raise first (keeps the through-road smoother)
        {
            var extra = MathF.Min(rem, MathF.Max(0f, raiseMaxNormal - raise));
            raise += extra;
            rem -= extra;
        }

        if (rem > Eps && dipMayRefill)
        {
            var extra = MathF.Min(rem, MathF.Max(0f, dipMaxNormal - dip));
            dip += extra;
            rem -= extra;
        }

        if (rem > Eps) // escalation 1: absolute slopes + hard cut depth
        {
            if (deckMayRefill)
            {
                var extra = MathF.Min(rem, MathF.Max(0f, raiseMaxAbs - raise));
                raise += extra;
                rem -= extra;
            }

            if (rem > Eps && dipMayRefill)
            {
                var extra = MathF.Min(rem, MathF.Max(0f, dipMaxAbs - dip));
                dip += extra;
                rem -= extra;
            }

            if (rem <= Eps)
                warning = "feasibility escalated to absolute ramp slopes / hard cut depth";
        }

        if (rem > Eps && ob.Crossing.LowerKind == BridgeObstacleKind.Road)
        {
            // Escalation 2 (R4 step 7): build with reduced road clearance and warn.
            var clearancePart = sep - structuralDepth;
            var relief = MathF.Min(rem, MathF.Max(0f, clearancePart - rules.ReducedRoadClearanceMeters));
            if (relief > Eps)
            {
                sep -= relief;
                rem -= relief;
                warning = $"clearance reduced to {sep - structuralDepth:F2} m (R4 step 7)";
            }
        }

        if (rem > Eps)
        {
            // Last resort: the deck must clear — take the rest on the raise and warn. For a pure-dip plan
            // this is the ONLY way deficit ever reaches the deck (user law), and the raise may well be
            // within the ramp slopes — word the warning accordingly.
            raise += rem;
            warning = raise > raiseMaxAbs + Eps
                ? "infeasible within slope/cut limits - deck raised beyond absolute ramp slope"
                : "dip room/cut limits exhausted - remainder forced onto the deck";
        }

        return new CrossingPlan
        {
            Crossing = plan.Crossing,
            ObstacleZEstimate = ob.Z,
            Action = dip > Eps
                ? raise > Eps ? BridgeElevationAction.Split : BridgeElevationAction.DipLowerRoad
                : BridgeElevationAction.RaiseBridgeVeto,
            DeckTargetZ = raise > Eps ? approachDeckZ + raise : float.NaN,
            LowerRoadTargetZ = dip > Eps ? ob.Z - dip : float.NaN,
            DipDepthMeters = dip > Eps ? dip : 0f,
            RequiredSeparationMeters = sep,
            Warning = warning,
        };
    }

    // ── Geometry / obstacle collection ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The grade-separated crossings whose upper member is this corridor and whose XY falls inside this span's
    /// footprint, paired with the lower road's obstacle elevation. Skips any "obstacle" whose effective layer is
    /// not strictly below the span (the span-vs-span tie-break, §5: a deck at or above this one is not something
    /// to clear from below — it is handled as the upper member of its own crossing).
    /// </summary>
    private static List<Obstacle> CollectObstacles(
        UnifiedRoadNetwork network, SpanContext span, float[,]? heightMap, float metersPerPixel,
        BridgeElevationPlannerOptions? options = null)
    {
        var obstacles = new List<Obstacle>();
        foreach (var crossing in network.GradeSeparatedCrossings)
        {
            if (crossing.UpperSplineId != span.Spline.SplineId)
                continue;
            if (!span.Footprint.Contains(crossing.CrossingXY))
                continue;
            if (crossing.LowerLayer >= span.Seg.Layer)
                continue; // tie-break: a same/higher deck is not an under obstacle

            var lowerSection = NearestSection(network, crossing.LowerSplineId, crossing.CrossingXY);
            var z = SectionZ(lowerSection, heightMap, metersPerPixel, options);
            if (!IsFinite(z) && IsFinite(crossing.CrossingXY.X))
                z = SampleTerrain(heightMap, metersPerPixel, crossing.CrossingXY);
            if (!IsFinite(z))
                continue;

            obstacles.Add(new Obstacle(crossing, z,
                network.GetSplineById(crossing.LowerSplineId), lowerSection?.DistanceAlongSpline ?? 0f));
        }

        AddSyntheticObstacles(span, heightMap, metersPerPixel, obstacles);
        AddSelfCrossingObstacles(span, heightMap, metersPerPixel, options, obstacles);

        return obstacles;
    }

    /// <summary>
    /// Minimum along-road distance (m) between a span boundary and an own-spline section for it to count
    /// as the corridor's SELF-crossing ground leg (a hairpin passing under its own bridge). Sections closer
    /// than this are the span's own approaches touching the footprint at the abutment — never an obstacle.
    /// A real self-crossing cannot be closer: the road must descend a full clearance height between
    /// leaving the deck and passing under it. SINGLE SOURCE OF TRUTH — the resolver's self-leg exclusion
    /// window (<c>GradeSeparationResolver.NearestSectionExcludingSelfLeg</c>) derives from this constant.
    /// </summary>
    public const float SelfCrossingMinStationGapMeters = 30f;

    /// <summary>Station gap (m) above which own-leg sections inside the footprint split into separate
    /// self-crossings (a spiral passing under the same span twice).</summary>
    private const float SelfCrossingClusterGapMeters = 25f;

    /// <summary>
    /// Self-crossing obstacles (<see cref="BridgeRuleSystemOptions.EnableHiddenCrossingDetection"/>): the
    /// corridor's own ground leg passing under this span. The junction detector excludes same-spline pairs
    /// structurally (proximity sampler AND footprint sweep), and <see cref="AddSyntheticObstacles"/> drops
    /// Road-kind OSM features on the assumption that road crossings always arrive via the detector — false
    /// exactly for a switchback under its own bridge (run 143909 bridge_8655179: the deck skimmed its own
    /// lower leg with ~0 m clearance). Detected here directly and station-disambiguated: own-spline
    /// sections whose XY lies inside the span footprint but whose station is far outside the span ARE the
    /// under-deck leg. Emitted with <c>LowerSplineId = −1</c> (never dipped — the lower member is the
    /// deck's own approach chain) and <see cref="GradeSeparatedCrossing.SelfLowerStationMeters"/> so the
    /// post-solve passes can read the leg's FINAL Z by station (an XY lookup would find the deck itself).
    /// </summary>
    private static void AddSelfCrossingObstacles(
        SpanContext span, float[,]? heightMap, float metersPerPixel,
        BridgeElevationPlannerOptions? options, List<Obstacle> obstacles)
    {
        if (span.Spline.Parameters.BridgeRules?.EnableHiddenCrossingDetection != true)
            return;

        // Own-spline ground sections under the deck: inside the footprint, station well outside the span,
        // and not part of ANY structure span (a lower deck of the same corridor is R6 multi-level, not a
        // ground leg). span.Sections is station-ordered (EnumerateSpans), so runs cluster by adjacency.
        List<UnifiedCrossSection>? under = null;
        foreach (var cs in span.Sections)
        {
            if (cs.DistanceAlongSpline > span.Seg.StartDistance - SelfCrossingMinStationGapMeters &&
                cs.DistanceAlongSpline < span.Seg.EndDistance + SelfCrossingMinStationGapMeters)
                continue;
            if (cs.StructureSpanId >= 0)
                continue;
            if (!float.IsFinite(cs.CenterPoint.X) || !float.IsFinite(cs.CenterPoint.Y))
                continue;
            if (!span.Footprint.Contains(cs.CenterPoint))
                continue;
            (under ??= []).Add(cs);
        }

        if (under == null)
            return;

        var runStart = 0;
        for (var i = 1; i <= under.Count; i++)
        {
            if (i < under.Count &&
                under[i].DistanceAlongSpline - under[i - 1].DistanceAlongSpline <= SelfCrossingClusterGapMeters)
                continue;

            var run = under[runStart..i];
            runStart = i;

            // Obstacle Z = the leg's highest section over the run (errs toward MORE clearance, like the
            // water-surface estimate); the crossing anchors at the run's midpoint section.
            var z = float.NaN;
            foreach (var cs in run)
            {
                var sz = SectionZ(cs, heightMap, metersPerPixel, options);
                if (IsFinite(sz))
                    z = float.IsNaN(z) ? sz : MathF.Max(z, sz);
            }

            if (!IsFinite(z))
                continue;

            var mid = run[run.Count / 2];
            obstacles.Add(new Obstacle(new GradeSeparatedCrossing
            {
                UpperSplineId = span.Spline.SplineId,
                LowerSplineId = -1, // self: resolvable only by station — XY is ambiguous with the deck
                SelfLowerStationMeters = mid.DistanceAlongSpline,
                CrossingXY = mid.CenterPoint,
                UpperLayer = span.Seg.Layer,
                LowerLayer = 0,
                UpperPriority = span.Spline.Priority,
                LowerPriority = span.Spline.Priority,
                UpperIsBridge = true,
                LowerIsBridge = false,
                LowerKind = BridgeObstacleKind.Road,
                UpperOsmClass = span.Spline.OsmRoadType,
                LowerOsmClass = span.Spline.OsmRoadType,
            }, z));

            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[BRIDGE-PLAN] SELF-CROSSING spline={span.Spline.SplineId} span={span.Footprint.SpanId} " +
                $"own leg under deck at leg-station {mid.DistanceAlongSpline:F1}m " +
                $"xy=({mid.CenterPoint.X:F0},{mid.CenterPoint.Y:F0}) z={z:F2} " +
                $"({run.Count} section(s)) — typed Road budget applies (raise-only)");
        }
    }

    /// <summary>
    /// A1 (V2 plan doc 01): rail lines and waterways have NO road spline — they exist only as pre-projected
    /// OSM features (<see cref="RoadSmoothingParameters.BridgeObstacles"/>). For each rail/water feature
    /// passing under this span's footprint, emit a SYNTHETIC <see cref="GradeSeparatedCrossing"/>
    /// (<c>LowerSplineId = −1</c>) so the rule engine treats it like any other obstacle. The span's own
    /// way ids are excluded — a bridge way is itself <c>highway=*</c> and must never report as an obstacle
    /// under its own deck. Road features are skipped: generated roads are already detected via the
    /// grade-separated crossings, and double-reporting would double-clear.
    /// </summary>
    private static void AddSyntheticObstacles(
        SpanContext span, float[,]? heightMap, float metersPerPixel, List<Obstacle> obstacles)
    {
        var parameters = span.Spline.Parameters;
        if (parameters.BridgeObstacles is not { Count: > 0 } set) return;

        foreach (var hit in BridgeObstacleClassifier.FindCrossings(
                     set, span.Footprint, ignoreOsmWayIds: span.Seg.OsmWayIds))
        {
            if (hit.Feature.Kind is not (BridgeObstacleKind.Rail or BridgeObstacleKind.Water))
                continue;

            // Rail rides on the terrain at the crossing point; water uses the v1 surface estimate
            // (min centerline DEM of the feature inside the footprint — errs high ⇒ more clearance).
            var z = hit.Feature.Kind == BridgeObstacleKind.Water
                ? WaterSurfaceZ(hit.Feature, span.Footprint, heightMap, metersPerPixel, hit.CrossingPoint)
                : SampleTerrain(heightMap, metersPerPixel, hit.CrossingPoint);
            if (!IsFinite(z))
                continue;

            var crossing = new GradeSeparatedCrossing
            {
                UpperSplineId = span.Spline.SplineId,
                LowerSplineId = -1, // synthetic: no road spline below
                CrossingXY = hit.CrossingPoint,
                UpperLayer = span.Seg.Layer,
                LowerLayer = 0,
                UpperPriority = span.Spline.Priority,
                LowerPriority = 0,
                UpperIsBridge = true,
                LowerIsBridge = false,
                LowerKind = hit.Feature.Kind,
                LowerElectrified = hit.Feature.Electrified,
                LowerNavigable = hit.Feature.Navigable,
                UpperOsmClass = span.Spline.OsmRoadType,
            };
            obstacles.Add(new Obstacle(crossing, z));
        }
    }

    /// <summary>
    /// A5 (review P1-2 minimal carry): already-pinned deck sections of earlier (higher-priority) spans that
    /// lie inside this span's footprint act as fixed obstacles. When the lower spline is already an obstacle
    /// (the span-vs-span crossing — whose Z was read from the still-unraised <c>TargetElevation</c>), its Z
    /// is lifted to the pinned deck; otherwise a synthetic Road crossing is added. A pinned deck below is
    /// never dipped (see the LowerIsBridge guard in <see cref="ClassifyNonRampCrossing"/>).
    /// </summary>
    private static void AddCarryObstacles(SpanContext span, List<PinnedCarry> carry, List<Obstacle> obstacles)
    {
        foreach (var pc in carry)
        {
            if (pc.SplineId == span.Spline.SplineId) continue;
            if (pc.Layer >= span.Seg.Layer) continue; // only decks genuinely below this span constrain it
            if (!span.Footprint.Contains(pc.XY)) continue;

            var idx = obstacles.FindIndex(o => o.Crossing.LowerSplineId == pc.SplineId);
            if (idx >= 0)
            {
                if (pc.DeckZ > obstacles[idx].Z)
                    obstacles[idx] = obstacles[idx] with { Z = pc.DeckZ };
                continue;
            }

            obstacles.Add(new Obstacle(new GradeSeparatedCrossing
            {
                UpperSplineId = span.Spline.SplineId,
                LowerSplineId = pc.SplineId,
                CrossingXY = pc.XY,
                UpperLayer = span.Seg.Layer,
                LowerLayer = pc.Layer,
                UpperPriority = span.Spline.Priority,
                LowerPriority = pc.Priority,
                UpperIsBridge = true,
                LowerIsBridge = true,
                LowerKind = BridgeObstacleKind.Road,
                UpperOsmClass = span.Spline.OsmRoadType,
                LowerOsmClass = pc.OsmClass,
            }, pc.DeckZ));
        }
    }

    /// <summary>
    /// v1 water-surface Z (review P0-3): the minimum terrain sample along the water feature within the span
    /// footprint — no water-level concept exists in the pipeline, and min-DEM errs toward MORE clearance.
    /// Falls back to the crossing-point sample when no in-footprint sample lands on the heightmap.
    /// </summary>
    private static float WaterSurfaceZ(
        BridgeObstacleFeature feature, BridgeSpanFootprint footprint,
        float[,]? heightMap, float metersPerPixel, Vector2 fallbackPoint)
    {
        var min = float.PositiveInfinity;
        foreach (var p in BridgeObstacleClassifier.SamplePolyline(feature, 2f))
        {
            if (!footprint.Contains(p)) continue;
            var v = SampleTerrain(heightMap, metersPerPixel, p);
            if (IsFinite(v) && v < min) min = v;
        }

        return float.IsPositiveInfinity(min)
            ? SampleTerrain(heightMap, metersPerPixel, fallbackPoint)
            : min;
    }

    private static (float left, float right) ApproachElevations(
        SpanContext span, float[,]? heightMap, float metersPerPixel,
        BridgeElevationPlannerOptions? options = null)
    {
        UnifiedCrossSection? before = null, after = null;
        var bestBefore = float.NegativeInfinity;
        var bestAfter = float.PositiveInfinity;
        foreach (var cs in span.Sections)
        {
            if (cs.DistanceAlongSpline < span.Seg.StartDistance - Eps)
            {
                if (cs.DistanceAlongSpline > bestBefore) { bestBefore = cs.DistanceAlongSpline; before = cs; }
            }
            else if (cs.DistanceAlongSpline > span.Seg.EndDistance + Eps)
            {
                if (cs.DistanceAlongSpline < bestAfter) { bestAfter = cs.DistanceAlongSpline; after = cs; }
            }
        }

        return (SectionZ(before, heightMap, metersPerPixel, options),
            SectionZ(after, heightMap, metersPerPixel, options));
    }

    /// <summary>
    /// The best-available elevation of a section: its solved <see cref="UnifiedCrossSection.TargetElevation"/>
    /// when finite (post-smoothing), else the A0 early estimate when provided (smoothed centerline DEM —
    /// review P0-2), else the raw terrain sample (the planner's pre-Phase-2.0 first cut, §6).
    /// </summary>
    private static float SectionZ(UnifiedCrossSection? cs, float[,]? heightMap, float metersPerPixel,
        BridgeElevationPlannerOptions? options = null)
    {
        if (cs == null) return float.NaN;
        if (IsFinite(cs.TargetElevation)) return cs.TargetElevation;

        if (options?.EarlyElevation != null)
        {
            var early = options.EarlyElevation(cs);
            if (IsFinite(early)) return early;
        }

        return SampleTerrain(heightMap, metersPerPixel, cs.CenterPoint);
    }

    /// <summary>Max heightmap value sampled across each span rib (centre ± half-width), per D5.</summary>
    private static float SampleTerrain(float[,]? heightMap, float metersPerPixel, Vector2 p)
    {
        if (heightMap == null || metersPerPixel <= 0f)
            return float.NaN;
        var w = heightMap.GetLength(1);
        var h = heightMap.GetLength(0);
        var px = Math.Clamp((int)(p.X / metersPerPixel), 0, w - 1);
        var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, h - 1);
        var v = heightMap[py, px];
        return IsFinite(v) ? v : float.NaN;
    }

    private static List<DeckPin> BuildPins(SpanContext span, float deckZ)
    {
        var pins = new List<DeckPin>(span.SpanSections.Count);
        foreach (var cs in span.SpanSections)
            pins.Add(new DeckPin(cs, cs.Index, cs.DistanceAlongSpline, deckZ));
        return pins;
    }

    /// <summary>Graded-deck pins: the chord between the approaches plus the uniform lift — a deck on the
    /// road's own grade instead of a horizontal plateau.</summary>
    private static List<DeckPin> BuildGradedPins(SpanContext span, Func<float, float> chordAt, float lift)
    {
        var pins = new List<DeckPin>(span.SpanSections.Count);
        foreach (var cs in span.SpanSections)
            pins.Add(new DeckPin(cs, cs.Index, cs.DistanceAlongSpline, chordAt(cs.DistanceAlongSpline) + lift));
        return pins;
    }

    /// <summary>
    /// Amendment 03 v3 sparse-mode pins, doc 05 §4.1 uniform form (render #10 user law: "the bridges must
    /// be equally raised with equally distributed cross-sections"): the chord between the approaches plus
    /// ONE uniform lift — the worst station-local clearance rise over all raise/veto/split crossings — on
    /// EVERY span section. The earlier per-crossing eased humps (30–150 m) were narrower than the box
    /// filter's ~150 m window and diluted to 20–30 % (doc 04 §3.1); a span-wide lift survives the filter
    /// and pulls BOTH approaches up. Consumed as soft raw-input shaping, never hard-held: the filter
    /// solves the span like road and clearance is finalized by floors + the post-solve uniform raise.
    /// Dip-only and already-clear crossings contribute no lift (the deck does not move for them).
    /// </summary>
    private static List<DeckPin> BuildUniformSoftPins(
        SpanContext span,
        Func<float, float> chordAt,
        List<Obstacle> obstacles,
        List<CrossingPlan> spanCrossings,
        Func<Obstacle, float> separationFor)
    {
        var lift = 0f;
        for (var i = 0; i < spanCrossings.Count && i < obstacles.Count; i++)
        {
            var plan = spanCrossings[i];
            if (plan.Action is not (BridgeElevationAction.RaiseBridge or BridgeElevationAction.RaiseBridgeVeto
                or BridgeElevationAction.Split))
                continue;

            var ob = obstacles[i];
            var station = UpperStationOf(span, ob.Crossing.CrossingXY);
            // Split: the deck only owes its §3.5 share (the dip covers the rest); raise/veto owe the
            // full per-crossing minimum — obstacle + typed separation, NOT a span-wide level.
            var target = plan.Action == BridgeElevationAction.Split && IsFinite(plan.DeckTargetZ)
                ? plan.DeckTargetZ
                : ob.Z + separationFor(ob);
            var rise = target - chordAt(station);
            if (IsFinite(rise) && rise > lift)
                lift = rise;
        }

        var pins = new List<DeckPin>(span.SpanSections.Count);
        foreach (var cs in span.SpanSections)
            pins.Add(new DeckPin(cs, cs.Index, cs.DistanceAlongSpline,
                chordAt(cs.DistanceAlongSpline) + lift, lift));

        return pins;
    }

    /// <summary>Arc-length station of the span cross-section nearest to <paramref name="xy"/> (where an
    /// obstacle passes under the deck) — the chord is evaluated there for station-local deficits.</summary>
    private static float UpperStationOf(SpanContext span, Vector2 xy)
    {
        UnifiedCrossSection? best = null;
        var bestDist = float.MaxValue;
        foreach (var cs in span.SpanSections)
        {
            var d = Vector2.DistanceSquared(cs.CenterPoint, xy);
            if (d < bestDist)
            {
                bestDist = d;
                best = cs;
            }
        }

        return best?.DistanceAlongSpline ?? (span.Seg.StartDistance + span.Seg.EndDistance) * 0.5f;
    }

    private static UnifiedCrossSection? NearestSection(UnifiedRoadNetwork network, int splineId, Vector2 xy)
    {
        UnifiedCrossSection? best = null;
        var bestDist = float.MaxValue;
        foreach (var cs in network.GetCrossSectionsForSpline(splineId))
        {
            var d = Vector2.DistanceSquared(cs.CenterPoint, xy);
            if (d < bestDist) { bestDist = d; best = cs; }
        }

        return best;
    }

    // ── Span enumeration (mirrors BridgeSpanFootprint.BuildAll's gate) ───────────────────────────────────

    private static IEnumerable<SpanContext> EnumerateSpans(UnifiedRoadNetwork network)
    {
        foreach (var spline in network.Splines)
        {
            var parameters = spline.Parameters;
            if (!parameters.MergeStructuresIntoCorridor) continue;
            if (spline.StructureSegments is not { Count: > 0 } segs) continue;

            var sections = network.GetCrossSectionsForSpline(spline.SplineId)
                .OrderBy(c => c.DistanceAlongSpline).ToList();

            foreach (var seg in segs)
            {
                if (!(seg.IsBridge && parameters.ExcludeBridgesFromTerrain)) continue;
                var footprint = BridgeSpanFootprint.Build(spline.SplineId, seg, sections);
                if (footprint == null) continue;

                var spanSections = sections
                    .Where(c => c.DistanceAlongSpline >= seg.StartDistance && c.DistanceAlongSpline <= seg.EndDistance)
                    .ToList();
                if (spanSections.Count == 0) continue;

                yield return new SpanContext(spline, seg, footprint, sections, spanSections);
            }
        }
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static float AverageFinite(float a, float b)
    {
        var aF = IsFinite(a);
        var bF = IsFinite(b);
        if (aF && bF) return 0.5f * (a + b);
        if (aF) return a;
        if (bF) return b;
        return float.NaN;
    }

    /// <summary><paramref name="LowerSpline"/>/<paramref name="LowerStation"/> locate the obstacle on the
    /// lower road for the A4 dip-ramp measurement; null/0 for synthetic rail/water (never dipped).</summary>
    private readonly record struct Obstacle(
        GradeSeparatedCrossing Crossing, float Z,
        ParameterizedRoadSpline? LowerSpline = null, float LowerStation = 0f);

    /// <summary>A5 carry: one already-pinned deck section visible to later (lower-priority) spans.</summary>
    private readonly record struct PinnedCarry(
        Vector2 XY, float DeckZ, int Layer, int SplineId, int Priority, string? OsmClass);

    private sealed record SpanContext(
        ParameterizedRoadSpline Spline,
        StructureSegment Seg,
        BridgeSpanFootprint Footprint,
        List<UnifiedCrossSection> Sections,
        List<UnifiedCrossSection> SpanSections);
}
