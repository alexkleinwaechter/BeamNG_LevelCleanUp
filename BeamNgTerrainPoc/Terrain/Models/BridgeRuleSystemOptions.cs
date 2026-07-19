using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Central configuration for the Bridge Rule System (plan `ai_docs/2026-06-10_bridge_generation_V2/01`,
///     spec `examples_for_ai/Bridge Rule System/Bridge_Rule_System_EN.md`): per-bridge required vertical
///     separation S = Clearance(obstacle) + StructuralDepth(span), distributed between raising the deck (R)
///     and lowering the obstacle road (L) by priority (§3.5), fed as pre-smooth pins the existing
///     smoother solves. Every rule is independently flag-gated; all flags default OFF so the rule system is
///     byte-identical to the current merged-corridor behaviour until enabled.
///     Values are plausibility defaults from COMBRI/Kapu (spec §3), not normative — all tunable.
/// </summary>
public class BridgeRuleSystemOptions
{
    // ========================================
    // FEATURE FLAGS (each rule independently togglable; all default OFF = byte-identical)
    // ========================================

    /// <summary>§3.5 priority-based R/L distribution replacing the binary raise/dip/split decision (A3).</summary>
    public bool EnablePriorityDistribution { get; set; }

    /// <summary>R4.5 ramp-length feasibility clamps + escalation (A4).</summary>
    public bool EnableRampFeasibility { get; set; }

    /// <summary>Bridge-over-bridge relationship detection (placeholder, no resolution — 0.5/R6).</summary>
    public bool EnableBridgeBridge { get; set; }

    /// <summary>
    ///     A0 (review P0-2): the planner reads approach/obstacle Z from a smoothed CENTERLINE-DEM estimate
    ///     instead of raw DEM samples (raw pre-smooth DEM ≈ embankment banks — the parked branch's §5a misfire).
    /// </summary>
    public bool EnableEarlyElevationEstimate { get; set; }

    /// <summary>
    ///     Graded deck profile (render review #3, bridge 762726404): instead of one FLAT deck Z, pin the
    ///     deck along the CHORD between the two approach elevations, lifted uniformly by the minimum amount
    ///     that satisfies every obstacle's separation at its own station (lift 0 when everything already
    ///     clears ⇒ both abutment seams sit flush). A flat pin on a sloping road can never meet both
    ///     approaches — one end pokes above the road, the other sits below it. COMBRI allows decks on a
    ///     grade (§3.3 class slopes); the chord grade equals the road's own approach grade.
    /// </summary>
    public bool EnableGradedDeck { get; set; }

    /// <summary>
    ///     Amendment 03 (render #5 "crumpled roads"): the planner emits NO pins at all — no dense deck
    ///     profile, no approach-ramp pins, no dip wells. The smoother treats the corridor as pure road;
    ///     <c>BridgeProfileSolver.ApplyToSpan</c> re-curves each span G0+G1 from the SOLVED approaches
    ///     (flush seams by construction, render #2 evidence) and enforces the rule engine's typed budgets
    ///     as interior FLOOR constraints (arch lift only when the natural curve is short — overshoot is
    ///     allowed). Dips go back to the post-solve resolver well against the final deck Z. Takes
    ///     precedence over <see cref="EnableGradedDeck"/> pin emission and owns the pre-smooth lower-road
    ///     dip wells unconditionally (formerly a separate dip-as-pin toggle).
    /// </summary>
    public bool EnableSparseDeckConstraints { get; set; }

    /// <summary>
    ///     A5: plan spans in DESCENDING owner-priority order and carry already-pinned deck sections into
    ///     later (lower-priority) spans as fixed obstacles (review P1-2 minimal carry: a pinned section
    ///     inside a later span's footprint contributes its pinned Z, kind=Road; a pinned deck below is
    ///     never dipped). Off ⇒ today's per-spline enumeration order, no carry.
    /// </summary>
    public bool EnableSpanSolveOrder { get; set; }

    /// <summary>
    ///     A6.5 (review P0-1, doc 16 §3): <c>BridgeProfileSolver.RefineSpans</c> SKIPS its elevation override
    ///     for spans the planner pinned — the smoother-held deck is kept instead of being re-curved (and
    ///     dropped) from the approach anchors. Off ⇒ today's merged behaviour (pins discarded post-smooth).
    /// </summary>
    public bool EnablePinnedDeckProfile { get; set; }

    /// <summary>
    ///     Re-resolve each bridge span's StartDistance/EndDistance by projecting the bridge way's ORIGINAL
    ///     endpoint coordinates onto the final merged spline, replacing the pre-Chaikin arc-length sums
    ///     (the documented station drift, docs 11/13). Changes default merged output when on (0.3a).
    /// </summary>
    public bool EnableBridgeStationReprojection { get; set; }

    /// <summary>
    ///     Doc 09: in-solver natural-profile anchor. Non-bridge roads hold their A0 (early-elevation)
    ///     profile; the affine junction leveling decays its correction over a class-slope run at ANY
    ///     junction inflated above A0 (not only BridgeRaisedJunctions), so bridge elevation cannot
    ///     diffuse into side roads. Also skips the post-solve ApplyApproachRaiseRamps (the deficit it
    ///     corrected no longer exists) in favour of a read-only clearance assertion. Off ⇒ legacy.
    /// </summary>
    public bool EnableNaturalProfileAnchor { get; set; }

    /// <summary>
    ///     Doc 10: join perfectly contiguous same-type structure spans on a merged spline ACROSS OSM
    ///     <c>layer</c> differences (the tag encodes only local crossing order — one physical viaduct is
    ///     mapped as many ways alternating layer 3/0/…, which the type+layer consolidation never joins).
    ///     One span = one deck = two real abutments instead of a fake abutment pair per way boundary.
    ///     Per-sub-range layers are kept (<see cref="RoadGeometry.StructureSegment.LayerRanges"/>) so
    ///     grade-separation classification still sees the right relative layer at every crossing station.
    /// </summary>
    public bool EnableContiguousSpanConsolidation { get; set; }

    /// <summary>
    ///     Doc 13: suppress abutment terraforming at span ends whose road continues onto ANOTHER bridge
    ///     deck (ramp landing mid-span on a trunk, end-to-end spline handoffs of one physical structure).
    ///     Those ends kept getting the full ground-abutment package — 3 m non-excluded overlap zone
    ///     (Phase-4 embankment pillar up to the deck corner), overlap tongue, excavator tongue ceiling.
    ///     Deliberately NOT part of <see cref="AnyEnabled"/>: consumed directly by the exclusion marking /
    ///     stamper / excavator, must not activate the planner pipeline alone.
    /// </summary>
    public bool EnableBridgeToBridgeAbutmentSuppression { get; set; }

    /// <summary>
    ///     Doc 14: deck-to-deck continuity at bridge→bridge merges (ramp landing mid-span on a trunk
    ///     deck, end-to-end deck handoffs). Treats the merge like a road T-junction with the trunk as
    ///     the through road: (a) at a junction whose station lies inside SEVERAL spans' footprints the
    ///     deck being LANDED ON (junction interior to its span) owns the junction Z — the landing
    ///     span's plan deckEnd cap never overwrites it; (b) the profile solver anchors a landing span
    ///     end to the trunk deck SURFACE (final solved Z at the landing station + lateral bank offset,
    ///     grade projected onto the landing span's +s), with spans solved in descending owner priority
    ///     so the trunk deck is final first; (c) junction-driven landing detection also flags
    ///     junction-connected ends the doc-13 radius test misses. Like
    ///     <see cref="EnableBridgeToBridgeAbutmentSuppression"/>, deliberately NOT part of
    ///     <see cref="AnyEnabled"/> — meaningless without merged spans. Off ⇒ byte-identical.
    /// </summary>
    public bool EnableDeckToDeckContinuity { get; set; }

    /// <summary>
    ///     Doc 15: seamless deck overlap at bridge→bridge merges — the AREA follow-up to
    ///     <see cref="EnableDeckToDeckContinuity"/>'s seam-POINT fix. (a) The profile solver conforms
    ///     every landing-span section still overlapping the landed-on deck's footprint EXACTLY onto that
    ///     deck's surface — center and both edges sampled independently, so the intersecting part is
    ///     coplanar by construction (bank included) — eased out past the overlap so no new kink appears;
    ///     (b) the deck mesh opens parapet segments where an edge runs INSIDE a conforming partner
    ///     span's footprint (the union roadway keeps parapets only on its OUTER boundary); (c) the mesh
    ///     end-stamp is skipped at ContinuesOntoDeck ends (doc-13's terrain suppression, mirrored to the
    ///     mesh). Consumed only when <see cref="EnableDeckToDeckContinuity"/> is also on (conformance
    ///     without end anchoring is meaningless); like it, deliberately NOT part of
    ///     <see cref="AnyEnabled"/>. Off ⇒ byte-identical.
    /// </summary>
    public bool EnableSeamlessDeckOverlap { get; set; }

    /// <summary>
    ///     Crossings hidden by the detector's connectivity heuristics (run 160210 bridge_8655179). Two
    ///     blindspots, one knob:
    ///     (a) <b>Junction-connected pairs</b> — the mid-spline crossing detector skips every spline pair
    ///     that already shares a junction ("that's a T-junction, not a crossing"), and the footprint safety
    ///     net inherited the skip. But a corridor that tees into another road can STILL bridge over it
    ///     elsewhere (the K 102 joins the B 53 120 m north of the K 102 bridge that crosses the B 53 —
    ///     zero clearance budgeted). With this flag the footprint pass re-examines those pairs, guarded by
    ///     <c>NetworkJunctionDetector.SharedJunctionCrossingExclusionMeters</c> so the shared junction's
    ///     own overlap area never reports as a crossing.
    ///     (b) <b>Self-crossings</b> — a corridor whose own ground leg passes under its OWN bridge span
    ///     (switchback/hairpin) is invisible to both detector passes (same-spline pairs are structurally
    ///     excluded); the planner sweeps the span footprint for own-leg sections and emits a synthetic Road
    ///     obstacle carrying the leg's along-spline STATION
    ///     (<see cref="RoadGeometry.GradeSeparatedCrossing.SelfLowerStationMeters"/> — an XY lookup on the
    ///     shared spline would find the deck itself). Never dipped (raise-only veto, like rail/water).
    ///     Like <see cref="EnableDeckToDeckContinuity"/>, deliberately NOT part of
    ///     <see cref="AnyEnabled"/> — meaningless without merged spans. Off ⇒ byte-identical.
    /// </summary>
    public bool EnableHiddenCrossingDetection { get; set; }

    /// <summary>
    ///     Turns on every rule of the system plus pier generation. Product default for the UI since
    ///     2026-07: the rules are no longer user-facing feature flags — the app always runs the full
    ///     system. Property initializers stay OFF so library consumers and tests keep the legacy
    ///     byte-identical baseline; the UI layer opts in via this method /
    ///     <see cref="CreateWithAllRulesEnabled"/>.
    /// </summary>
    public void EnableAllRules()
    {
        EnablePriorityDistribution = true;
        EnableRampFeasibility = true;
        EnableBridgeBridge = true;
        EnableEarlyElevationEstimate = true;
        EnableGradedDeck = true;
        EnableSparseDeckConstraints = true;
        EnableSpanSolveOrder = true;
        EnablePinnedDeckProfile = true;
        EnableBridgeStationReprojection = true;
        EnableNaturalProfileAnchor = true;
        EnableContiguousSpanConsolidation = true;
        EnableBridgeToBridgeAbutmentSuppression = true;
        EnableDeckToDeckContinuity = true;
        EnableSeamlessDeckOverlap = true;
        EnableHiddenCrossingDetection = true;
        EnableBridgePiers = true;
    }

    /// <summary>Fresh options with every rule enabled (see <see cref="EnableAllRules"/>) and default tunables.</summary>
    public static BridgeRuleSystemOptions CreateWithAllRulesEnabled()
    {
        var options = new BridgeRuleSystemOptions();
        options.EnableAllRules();
        return options;
    }

    /// <summary>True when any rule of the system is active (cheap pipeline gate).</summary>
    public bool AnyEnabled =>
        EnablePriorityDistribution || EnableRampFeasibility ||
        EnableBridgeBridge ||
        EnableBridgeStationReprojection || EnableEarlyElevationEstimate || EnablePinnedDeckProfile ||
        EnableSpanSolveOrder || EnableGradedDeck || EnableSparseDeckConstraints ||
        EnableNaturalProfileAnchor || EnableContiguousSpanConsolidation;

    // ========================================
    // §3.1 VERTICAL CLEARANCE per obstacle kind (m)
    // ========================================

    /// <summary>Road under the bridge: EU standard 4.5 m + reserve.</summary>
    public float RoadClearanceMeters { get; set; } = 4.70f;

    /// <summary>Electrified rail under the bridge (catenary).</summary>
    public float RailClearanceElectrifiedMeters { get; set; } = 6.00f;

    /// <summary>Non-electrified rail (`electrified=no`).</summary>
    public float RailClearanceNonElectrifiedMeters { get; set; } = 5.00f;

    /// <summary>Freeboard above a non-navigable water surface (width &lt; <see cref="NavigableWaterWidthMeters"/>).</summary>
    public float WaterFreeboardMeters { get; set; } = 2.00f;

    /// <summary>Navigable water (`boat=yes`, `CEMT=*`, canal, or width ≥ <see cref="NavigableWaterWidthMeters"/>).</summary>
    public float NavigableWaterClearanceMeters { get; set; } = 5.25f;

    /// <summary>Width (m) at/above which water counts as navigable.</summary>
    public float NavigableWaterWidthMeters { get; set; } = 20f;

    /// <summary>R4 step 7 last resort: build with reduced road clearance and log a warning.</summary>
    public float ReducedRoadClearanceMeters { get; set; } = 4.20f;

    /// <summary>
    ///     §3.1: the vertical clearance required between the obstacle and the deck SOFFIT, by obstacle kind
    ///     (A2 — applied per obstacle inside the planner loop, envelope rule). Terrain is 0: over plain
    ///     terrain only the structural depth separates deck top from ground (spec R1 valley bridge).
    /// </summary>
    public float ClearanceFor(BridgeObstacleKind kind, bool electrified = false, bool navigable = false)
    {
        return kind switch
        {
            BridgeObstacleKind.Road => RoadClearanceMeters,
            BridgeObstacleKind.Rail => electrified ? RailClearanceElectrifiedMeters : RailClearanceNonElectrifiedMeters,
            BridgeObstacleKind.Water => navigable ? NavigableWaterClearanceMeters : WaterFreeboardMeters,
            _ => 0f,
        };
    }

    // ========================================
    // §3.2 DECK STRUCTURAL DEPTH (clearance budget ONLY — the visible mesh keeps its own thin thickness)
    // ========================================

    /// <summary>Girder depth ≈ span / this (COMBRI span/20…25; ≡ the deck mesh's 0.05·span ratio).</summary>
    public float StructuralDepthSpanDivisor { get; set; } = 20f;

    /// <summary>
    ///     Clamps aligned with the RENDERED deck mesh (<c>BridgeDeckProfile.ComputeDeckThicknessMeters</c>,
    ///     UI default 0.45–2.0) — NOT the COMBRI 0.8–6.5 girder table. We model no piers, so the whole
    ///     structure length reads as one "span"; budgeting a 6.5 m girder that is never rendered floats the
    ///     deck top metres above the visible soffit (render review #2: the 285 m river bridge was pinned
    ///     4.5 m too high). Clearance is measured to the soffit of the deck that actually exists.
    /// </summary>
    public float StructuralDepthMinMeters { get; set; } = 0.45f;

    public float StructuralDepthMaxMeters { get; set; } = 2.0f;

    /// <summary>
    ///     §3.2: deckDepth = clamp(span / divisor, min, max). For multi-span bridges pass the largest single
    ///     span. Used for the clearance budget S only; the rendered deck mesh thickness is unchanged
    ///     (<c>BridgeDeckProfile.ComputeDeckThicknessMeters</c>, ≤ ~1.2 m).
    /// </summary>
    public float StructuralDepthMeters(float spanMeters)
    {
        if (StructuralDepthSpanDivisor <= 0f) return StructuralDepthMinMeters;
        return Math.Clamp(spanMeters / StructuralDepthSpanDivisor, StructuralDepthMinMeters, StructuralDepthMaxMeters);
    }

    // ========================================
    // §3.4 LOWERING LIMITS (road underneath)
    // ========================================

    /// <summary>Default max cut depth (m) — drainage plausibility.</summary>
    public float MaxCutDepthMeters { get; set; } = 4.0f;

    /// <summary>Absolute max cut depth (m) — R4 step 7 escalation.</summary>
    public float MaxCutDepthHardMeters { get; set; } = 6.0f;

    // ========================================
    // COHERENT UNDERPASS (doc 28)
    // ========================================

    /// <summary>
    ///     Doc 28 Step A: grade-separated crossings on the SAME lower road whose along-road stations are
    ///     within this gap (m) form one underpass CLUSTER (an interchange) and get ONE dip-or-raise decision
    ///     instead of fragmented per-crossing ones. Dip-ramp-length scale. Gated (with the rest of the
    ///     coherent-underpass pass) on <see cref="EnablePriorityDistribution"/>.
    /// </summary>
    public float UnderpassClusterGapMeters { get; set; } = 120f;

    /// <summary>
    ///     Doc 28 Step D: max coherent-underpass dip (m) — deeper than <see cref="MaxCutDepthMeters"/> so a
    ///     full-clearance interchange underpass (≈ 6 m) clears via the dip alone. Over-cap behavior is
    ///     cap + warn, stay coherent: the residual under-clearance is ACCEPTED (logged), never converted
    ///     into bridge raises (raising would reintroduce the fragmentation the pass removes).
    /// </summary>
    public float MaxUnderpassDipMeters { get; set; } = 6.0f;

    // ========================================
    // ABUTMENT OVERLAP TONGUE + UNDER-DECK PAINT (docs 06/07)
    // ========================================

    /// <summary>
    ///     Doc 06 §3.1: how far (m) the terrain road stamp continues ONTO each span end at deck level — the
    ///     overlap tongue that makes the terrain↔deck joint gap-free (a butt-joint at finite raster never
    ///     is). 0 disables. Sparse mode only.
    /// </summary>
    public float AbutmentOverlapMeters { get; set; } = 3f;

    /// <summary>
    ///     Doc 06 §3.1: how far (m) the overlap tongue sits BELOW the deck surface, so the deck mesh stays
    ///     the visible/physical driving surface (no z-fighting). The excavator's ceiling inside the overlap
    ///     zone uses this instead of the regular undercut (§3.2).
    /// </summary>
    public float AbutmentOverlapDropMeters { get; set; } = 0.01f;

    /// <summary>
    ///     Max lift (m) the abutment overlap tongue may apply to a single terrain cell. The tongue exists
    ///     to seal the raster seam between the stamped approach road and the deck mesh — a cell needing
    ///     more than this is not a seam but genuinely LOW ground under an elevated deck end (winningen
    ///     render 2026-07-02: a doc-28 underpass well passing close to the abutment got a deck-level patch
    ///     stamped ACROSS the dipped road, `[BRIDGE-OVERLAP] maxLift=9.99m`). Such cells are skipped —
    ///     the excavator / abutment-placement machinery owns them.
    /// </summary>
    public float AbutmentOverlapMaxLiftMeters { get; set; } = 2.0f;

    /// <summary>
    ///     Doc 07: terrain material painted under each bridge deck footprint so material-keyed billboard /
    ///     groundcover vegetation cannot grow through the deck (doc 06 deliberately tucks terrain TIGHT under
    ///     it — overlap tongues 1–3 cm below the top, shaved cells just below the soffit). Resolved by name
    ///     against the level's terrain material list at generation time; null/empty or unknown ⇒ no repaint
    ///     (warned). The UI prepopulates it via <see cref="Export.BridgeUnderDeckMaterialPainter.ResolveDefaultMaterialName"/>.
    /// </summary>
    public string? UnderDeckMaterialName { get; set; }

    /// <summary>
    ///     Doc 07: only cells whose terrain is within this distance (m) BELOW the local deck surface are
    ///     repainted — the "tight" cells where billboards poke through. Deep ravines / water under tall spans
    ///     keep their natural material.
    /// </summary>
    public float UnderDeckPaintClearanceMeters { get; set; } = 1.0f;

    // ========================================
    // BRIDGE PIERS (doc 19 — supports for long spans, styled by OSM structure type)
    // ========================================

    /// <summary>
    ///     Doc 19 master gate: generate intermediate pier supports (cap + columns) under long bridge decks.
    ///     Off ⇒ byte-identical output (no planning, no meshes, no building obstacles in the set). Like the
    ///     deck-mesh flags (docs 13/14/15), deliberately NOT part of <see cref="AnyEnabled"/> — consumed at
    ///     export/keep-out time, never activates the planner pipeline alone.
    /// </summary>
    public bool EnableBridgePiers { get; set; }

    /// <summary>Doc 19 §3a: spans shorter than this get abutments only (corpus §6: piers only past ~35–40 m).</summary>
    public float MinSpanLengthForPiersMeters { get; set; } = 35f;

    /// <summary>Doc 19 §3a: nominal bay length (corpus: multi-span overpasses pier every 20–40 m).
    /// Viaduct archetype keeps an exact rhythm at this spacing.</summary>
    public float PierSpacingMeters { get; set; } = 30f;

    /// <summary>Doc 19 §3a: soffit-to-ground below this ⇒ no pier — a low deck sits on doc-08 embankment,
    /// a stub pier there is clutter.</summary>
    public float MinPierHeightMeters { get; set; } = 2.5f;

    /// <summary>Doc 19 §3b: extra keep-out margin (m) beyond obstacle extent + pier footprint.</summary>
    public float PierClearanceMarginMeters { get; set; } = 3.0f;

    /// <summary>Doc 19 §3c: max slide (m) away from the nominal station before the pier is skipped
    /// (the keep-out is absolute, the spacing is not).</summary>
    public float MaxPierSlideMeters { get; set; } = 12f;

    /// <summary>Doc 19 §3c: two piers slid closer than this merge to one (or the second is dropped).</summary>
    public float MinPierGapMeters { get; set; } = 8f;

    /// <summary>Doc 19 §3d: column embed (m) below the LOWEST footprint corner, so cross-slope ground
    /// swallows the column instead of daylighting it downhill (building-foundation trick).</summary>
    public float PierGroundEmbedMeters { get; set; } = 1.5f;

    /// <summary>Doc 19 §4: octagonal column diameter (m). Corpus: 0.9–1.2 m round columns standard.</summary>
    public float PierColumnDiameterMeters { get; set; } = 1.2f;

    /// <summary>Doc 19 §4: decks at least this wide (m) get a two-column bent instead of one center column.</summary>
    public float PierTwinColumnThresholdMeters { get; set; } = 10f;

    /// <summary>Doc 19 §4: pier-cap (head beam) length (m) along the deck axis.</summary>
    public float PierCapLengthMeters { get; set; } = 1.5f;

    /// <summary>Doc 19 §4: pier-cap depth (m) below the soffit.</summary>
    public float PierCapDepthMeters { get; set; } = 1.0f;

    /// <summary>Doc 19 §4: cap plan width = deck width − 2 × this inset (m).</summary>
    public float PierCapSideInsetMeters { get; set; } = 0.5f;

    // ========================================
    // §3.3 + §3.5 STATIC TABLES (class steps, ramp slopes, priority shares)
    // ========================================

    /// <summary>
    ///     Quantized road-class step for the §3.5 Δp table, derived from the OSM highway class — NOT from the
    ///     stored composite priority (`osmPriority*100+materialOrder`), whose raw differences are meaningless
    ///     (non-uniform bands, material-order pollution). `*_link` classes inherit their parent's step so a
    ///     motorway over its own exit ramp is Δp 0 at the gore only via the parent mapping — review P0-4.
    ///     Steps: motorway/trunk=4, primary=3, secondary/tertiary=2, residential/unclassified/living_street=1,
    ///     service/track and everything smaller=0. Unknown/null ⇒ 1 (the unclassified band).
    /// </summary>
    public static int ClassStepFor(string? osmHighwayType)
    {
        if (string.IsNullOrWhiteSpace(osmHighwayType)) return 1;
        var cls = osmHighwayType.Trim().ToLowerInvariant();
        if (cls.EndsWith("_link", StringComparison.Ordinal))
            cls = cls[..^"_link".Length];
        return cls switch
        {
            "motorway" or "trunk" => 4,
            "primary" => 3,
            "secondary" or "tertiary" => 2,
            "residential" or "unclassified" or "living_street" or "service" or "road" => 1,
            _ => 0,
        };
    }

    /// <summary>§3.3 normal max longitudinal ramp gradient (%) per class step. Applies ONLY to constructed
    /// approach ramps, never the natural through-road profile.</summary>
    public static float NormalMaxSlopePercent(int classStep) => classStep switch
    {
        >= 4 => 4f, // motorway, trunk
        3 => 5f,    // primary
        2 => 6f,    // secondary, tertiary
        1 => 8f,    // residential, unclassified, service
        _ => 10f,   // track, footway
    };

    /// <summary>§3.3 absolute (fallback) max ramp gradient (%) per class step — R4 step 7 escalation.</summary>
    public static float AbsoluteMaxSlopePercent(int classStep) => classStep switch
    {
        >= 4 => 5f,
        3 => 6f,
        2 => 8f,
        1 => 10f,
        _ => 14f,
    };

    /// <summary>
    ///     §3.5: fraction of the remaining demand D taken by RAISING the deck, from
    ///     Δp = stepUpper − stepLower. The more important road keeps its smooth profile:
    ///     upper much more important (Δp ≥ +2) ⇒ raise only 20 %, the lower road absorbs 80 % by dipping.
    /// </summary>
    public static float RaiseShareFor(int priorityStepDelta) => priorityStepDelta switch
    {
        >= 2 => 0.20f,
        1 => 0.35f,
        0 => 0.50f,
        -1 => 0.65f,
        _ => 0.80f,
    };
}
