# Amendment 03 — Sparse Floor Constraints (deck = road, floors not profiles)

**Date:** 2026-06-10 (evening), after render #5.
**Status:** RATIFIED by the user ("Why don't we set the 'junctions' at bridge ends to the calculated
height and let our road smoother/profiler do its work on the bridges as if it would be roads … Change
the bridge asset and not the roads. Pin the elevation early and that's it." + "and if we overshoot
constraints, why not.").
**Supersedes (flag-gated, not deleted):** dense graded-deck pins (`8e67645`), approach-ramp pins
(`a549005`), and — when the new flag is on — A6 dip-as-pin wells (`c51b1a4`).

## What render #5 showed

Crumpled, sawtoothed road surfaces on bridge approaches, at abutment seams, and on dipped under-roads
("Catastrophe!"). All three artifact zones are exactly the regions where the planner now AUTHORS
profile geometry as dense `PinnedElevation` runs:

1. graded-deck pins — every span section pinned to chord+lift,
2. approach-ramp pins — eased ramps pinned onto approach road sections, shaped at planner time from
   the A0 DEM estimate,
3. dip-as-pin — full eased wells incl. ramps pinned onto lower roads.

## Diagnosis

The implementation drifted from doc 01's locked architecture ("bridges mirror junctions:
detect → pin → smoother honors" — i.e. sparse constraints) into the planner authoring whole
profiles, with the smoother reduced to a rubber stamp. Every A0-estimate error and every seam
between pin families (ramp↔deck↔junction↔unpinned) is stamped verbatim into the road. This is the
§5a/§5c lesson ("approach-based gates are unreliable at planner time") repeated with geometry
instead of gates — strictly worse.

**Render evidence:** render #2's 13 UN-pinned spans (typed mode, lift 0 ⇒ no pins) came out
`seamKink≈0°` — flush, perfect, via the plain `ApplyToSpan` cubic re-curve from the SOLVED
approaches. The only artifact spans were the pinned ones. The fix trajectory (more pins to fix
pinned spans) was the wrong direction.

**Why sparse point PINS also cannot work (the user's literal mechanism, corrected):** the chain
smoother's raw filter input for span sections is the terrain UNDER the bridge; `ApplyPinsToRaw`
overwrites only pinned samples and `HardHoldPins` forces them after the box filter. One isolated
hard pin metres above the filtered average = a needle in the road. Junction pins only behave
because their value ≈ the natural profile. Hence: floors in the span re-curve, not pins in the
filter.

## The model (this amendment)

With `BridgeRuleSystemOptions.EnableSparseDeckConstraints`:

- **The planner emits NO pins at all.** No deck pins, no approach-ramp pins, no dip-well pins.
  The decision engine (A1 typing, A2 typed budgets, A3 §3.5 distribution, A4 feasibility/warnings,
  A5 order) survives unchanged as the **author of constraint VALUES**, not geometry.
- **The smoother treats the corridor as pure road.** Approaches are never touched.
- **`BridgeProfileSolver.ApplyToSpan` re-curves each span** cubic G0+G1 from the solved approaches
  (flush seams by construction) and receives **interior floor constraints** built from the plan:
  per raise/veto/split/already-clears crossing, `MinZ = lowerFinalZ + RequiredSeparation − plannedDipShare`
  at the crossing's upper station (synthetic rail/water → `ObstacleZEstimate` base). The existing
  `ComputeInteriorLift` arches the deck over a floor ONLY when the natural curve is short —
  **overshoot is allowed and untouched** (the user's "if we overshoot constraints, why not").
- **Dips return to the proven post-solve resolver path** (`ApplyLowerRoadDips` active mode): eased
  well + carve against the FINAL deck Z — the path validated in render `…005444`.
- **The deck asset follows the solved road** via the span snapshot (already the case).

## Known v1 limitations (render-judged)

- A floor near an abutment (shape weight < 0.25, i.e. station outside ~[0.15, 0.85] of the span)
  is SKIPPED with a `[BRIDGE-PLAN]` warning — arching the whole deck to fix an end deficit would
  be a huge central hump. End deficits are junction/approach territory (doc-16 §3b, Phase B).
- A5 carry has no pinned sections to carry under sparse mode — bridge-over-bridge ordering is
  deferred (it already was, R6).
- Raise/veto crossings whose floor was skipped have no automatic corrector (resolver only dips
  dip/split plans); A4/floor-skip warnings + render review cover this.

---

## v2 (same evening, after render #6 — log `…193231`)

**Render #6 falsified the "no pins at all" form:** 8 of 11 floors were skipped near abutments
(`skippedNearAbutment=8`, t = 0.07–0.10 / 0.86–0.94 — on kattenes the crossings sit at the span ends),
so nothing lifted the decks and they sank into the under-roads. User: "not using pins for the ramp is a
bad idea … we need correct ramps, so that the bridge reaches clearance height. just set the pins at the
bridge ends to the calculated clearance."

**v2 = span pins return; the smoother builds the ramps** (commit `cb04048`):

- The planner emits the SPAN pins again under sparse mode (graded chord + lift — the deck ends sit at
  the calculated clearance heights). What stays banned under sparse: hard approach-ramp pins and
  dip-as-pin wells (the #5 crumple sources — authored, estimate-shaped, hard-held profiles).
- NEW `OptimizedElevationSmoother.FeatherRawApproachRamps` (gated on the sparse flag): per contiguous
  pinned run, the boundary delta is eased into the RAW filter input on the approach side
  (`(1−u)²(1+2u)`, length `|delta|/5 %` clamped 30–150 m, stops at any other pinned section — junction
  pins win). The approaches are NOT hard-held: the filter blends the soft ramp with the real road
  context, so its output climbs to the deck (closes the doc-16 §3b half-way stop) while estimate
  errors smooth out instead of being stamped. Re-applied every iteration on the previous SOLVED
  profile → converges flush (test: residual abutment step < 1 m after 3 iterations, monotone climb).
- `UnifiedJunctionProfileBlender.respectPins` is now also true under sparse (span pins carry the same
  tug-of-war risk the dip pins did).
- Interior floors in RefineSpans remain for UN-pinned spans; the near-abutment skip is now harmless
  (end crossings are covered by the span pins' chord + lift).

Render #7 checklist: `pinnedSections>0` + `approachRampPins=0 dipPinnedSections=0 mode=sparse-floors`;
decks CLEAR the under-roads (no sinking, no z-fighting); approaches climb smoothly (no crumple, no
abutment step); `[BRIDGE-PROFILE]` seamKink small on raised spans; under-roads dipped by the resolver
where planned.

---

## v3 (same night, after render #7 — log `…201750`): "give the bridge cross-sections"

**Render #7 falsified the v2 hard-held chord:** the 13 UN-pinned cubic spans were seamKink 0.0–0.9°
(perfect), the 3 HARD-pinned spans were 45–64°. The river bridge 394: `pinZ 8,08..10,19` — a dead-straight
chord ("no curvature"), start approach 8.24 ≈ pin 8.08 (the matching side), end approach 8.92 vs pin 10.19
= the 1.3 m abutment step. The v2 feather could not close it (truncated by junction pins / limited
iterations) — and structurally cannot: **anything hard-held can step; anything the filter solves cannot.**
User: "the bridge should follow the same principles as a normal road would do with our road
smoothing/profiling. Be brave and give the bridge some crossections."

**v3 = the span becomes ordinary road in the filter; nothing on a bridge is hard-held:**

- `UnifiedCrossSection.SoftDeckRiseMeters` — the planner's per-crossing eased clearance humps,
  transported as a RELATIVE rise (estimate offsets cannot reach the road; a hump that reaches the span
  end keeps its full value). `BridgeElevationPlanner.BuildSoftHumpPins`: chord + per-crossing
  `(1−u)²(1+2u)` humps at 5 % run-out (30–150 m) — **no uniform end lift** (the #4 step). `DeckPin`
  gains `SoftRiseMeters`.
- `OptimizedElevationSmoother.ApplySoftShapingToRaw` (both filter passes): each soft run's RAW input
  becomes `boundary-anchored chord + rise` — anchored on the actual approach raws each iteration
  (iteration 0: terrain; 1+: the solved road), so the span raw is continuous with the road on both
  sides. The filter then yields natural curvature and seam steps are impossible. Slope-clamp exemption
  mask + junction-blender `respectPins` cover soft sections too.
- Division of labour: **sustained END rises** (the near-abutment crossings the floors skip) converge
  through the re-anchoring iterations into real approach ramps; **narrow MID-span humps** are diluted
  by the filter window and are instead finalized exactly by the `ApplyToSpan` cubic + interior FLOORS;
  road dips stay on the post-solve resolver. A hard-held deck no longer exists under sparse mode —
  `[BRIDGE-PLAN]` reports `softPinnedSections=` and `mode=sparse-soft`.

Render #8 checklist: `pinnedSections=0 softPinnedSections>0 mode=sparse-soft`; river bridge 394 matches
BOTH approaches (curve=Cubic again, seamKink < a few degrees) with visible vertical curvature; clearance
at 394/395/199's end crossings honest (planClear vs req); no crumple; resolver dips where planned.
