# Critical Review — Bridge Rule System Plan (doc 01)

**Date:** 2026-06-10
**Branch:** `feature/bridge_merged_corridor` @ `adeeb9a` ("Phase D"), clean slate done
(6-file abutment-wall diff restored; doc 24 marked superseded; plan persisted as doc 01).
**Method:** plan + spec (`Bridge_Rule_System_EN.md`) + docs 11/16 read; four code-verification
passes over the actual branch (pipeline/pins, OSM data flow, lateral drift, planner/resolver state).

**Verdict: the architecture is right** (constraint-feed into the existing smoother, bridges as
early first-class entities, §3.5 raise+dip). But the plan as written has **two blocking gaps that
would silently defeat Phase A**, one phase-0 claim that is factually wrong about the code, and a
handful of underspecified spots. Fix the plan first; the design itself survives review.

---

## P0-1 (BLOCKING) — `BridgeProfileSolver.ApplyToSpan` still discards pins on THIS branch, and the plan never touches it

Doc 16 §3 (the headline bug: "RefineSpans discards the deck pin; the flyover sags to a chord")
was fixed **only on the parked `feature/bridges` branch** (doc 17 tangent-to-plateau, later §5b
flat BuildPins). On `feature/bridge_merged_corridor` @ `adeeb9a`:

- `BridgeProfileSolver.cs` contains **zero** references to `PinnedElevation` (verified by grep).
- `ApplyToSpan` runs post-smoothing (RefineSpans, `[BRIDGE-PROFILE] apply` in doc 16's log) and
  **overwrites every span section's `TargetElevation`** from the approach anchors.

So: Phase A computes the BridgePlan, pins deck + dips, the smoother honours them through all 3
iterations — and then RefineSpans throws the deck away and re-fits a chord to the approaches.
Exactly doc 16 §3, reproduced by construction. **The plan's Phase A table and "Critical files"
list never mention `BridgeProfileSolver`.**

**Required amendment (new step, suggested A6.5):** decide RefineSpans' fate for planner-pinned
spans, deliberately — do not re-walk the parked branch's path (tangent-to-plateau → arch →
flat-pins) by accident:

- **Recommended:** for spans whose sections carry `PinnedElevation`, **skip the ApplyToSpan
  elevation override entirely** (keep snapshot capture + edge recompute). Rationale: the locked
  V2 design says the SMOOTHER solves continuity from pins; a post-smooth re-curve is the same
  "separate late pass" the architecture decision rejects. The smoother's hard-hold + exemption
  mask already produced the held deck + smoothed ramps.
- Fallback: make ApplyToSpan honour the pin as a floor (`max(curve, pin)` — the parked §5b shape)
  if the G1 abutment easing turns out to be needed after the first render.
- Also inherit doc 16 §3b onto the radar: the approach ramp may not fully **reach** the pin at the
  abutment (box-filter blend), leaving a step. The plan's Phase B "level-area blend" stamps
  terrain — it cannot fix a road-profile step. Keep §3b as a known-judge-from-render item with
  the doc-16 options (extend hard-hold one section into the approach, or accept a steeper final
  ramp segment).

Also verified (good news): the `RoadMaskBuilder` gap guard (doc 16 §2 embankment-under-deck fix)
**is** present on this branch (`RoadMaskBuilder.cs:343`).

## P0-2 (BLOCKING) — the "early road-elevation estimate" the plan reads obstacle Z from **does not exist**

Locked decision: "obstacle Z is read from the early road-elevation estimate the junction phase
already establishes — NOT the raw DEM." Verified against code:

- At Phase 1.85, **every cross-section's `TargetElevation` is NaN** (Phase 2 hasn't run).
- `BridgeElevationPlanner.SectionZ()` (BridgeElevationPlanner.cs:302–306) falls back to
  **raw DEM sampling** whenever `TargetElevation` is NaN — i.e. always, at 1.85.
- Phase 1.9 `JunctionElevationPinner` pins **junction** elevations only; it does not establish a
  road-elevation field.

So the plan's premise has no substrate, and no A-step builds one. Without an amendment, Phase A
ships with the exact §5a vulnerability the locked decision was written to avoid.

**Required amendment (new step, suggested A0):** define the estimator concretely. Options:

- **Recommended — cheap explicit pre-estimate at 1.8b:** per spline, sample the **centerline**
  DEM along arc-length and run a light 1D longitudinal low-pass (reuse the existing box-filter
  machinery on a throwaway profile; do NOT write TargetElevation). Centerline-at-the-crossing-
  station sampling already avoids the §5a failure mode (that failure read span-AVERAGE approach
  Z including embankment banks); the longitudinal smooth removes single-cell DEM noise. Keep the
  plan's station-local deficit (`Obstacle.NaturalDeckZ` pattern) on top.
- Alternative — re-order: run the planner after smoothing iteration 0 (TargetElevation real),
  pins re-asserted on iterations 1–2. Rejected as primary: only 2 honoured iterations remain,
  convergence may fire early, and it breaks "bridges are early entities, peer to junctions."
- Either way, **A7 (post-smooth verify + bounded local carve) stays as the backstop** and its
  log line should report estimate-vs-final delta per crossing so the estimator's accuracy is
  measurable on the first render.

Note the chicken-and-egg is **benign for the dip target itself**: the dip pin derives from the
pinned deck Z (authoritative by construction), `dipZ = deckZ − clearance − structuralDepth`. The
estimate-quality risk is confined to (a) the raise/dip *need* (deficit) and (b) ramp-length
feasibility against the eventual approach profile — both A7-verifiable.

## P0-3 (BLOCKING for 0.4) — OSM Features never reach the smoother, and the raster fallback is impossible as stated

Verified data flow: `OsmQueryResult.Features` **does** retain railway/waterway/natural=water with
full geometry + tags (Overpass query fetches them; parser keeps them; orchestrator caches them).
But:

- **No path to the smoother exists.** `UnifiedRoadSmoother.SmoothAllRoads` and
  `TerrainCreator.ApplyRoadSmoothing` receive heightmap + materials only; `UnifiedRoadNetwork`
  carries no features. New plumbing required: thread `OsmQueryResult` (or a pre-built,
  **terrain-local-projected** obstacle set) via `TerrainCreationParameters`/
  `RoadSmoothingParameters` → network. The plan's 0.4 implies this but must list it as real work.
- **Coordinate transform is missing from the plan:** Features are WGS84 `GeoCoordinate`s; bridge
  footprints are terrain-local metres. The classifier needs the coordinate transformer at build
  time — pre-project features once when building the spatial bucket.
- **The raster fallback is wrong as written:** `OsmLayerExporter` PNGs are written to disk
  **after** road smoothing runs (orchestrator order), so they do not exist at classification
  time. Either drop the fallback, or restate it as "rasterize the relevant categories in-memory
  earlier" (extra work — recommend drop for v1; the in-memory features are the primary and
  sufficient source).
- **Water-surface Z does not exist anywhere** in the pipeline (no flattening, no water level
  concept), and spec R2 needs it ("water surface + freeboard"). v1 must define it: recommend
  `waterZ = min(centerline DEM along the water feature within the span footprint)` — cheap and
  errs high (more clearance). Pin this in 0.4/A2.
- Degrade gracefully: PNG-skeleton/non-OSM spline sources have no Features at all → classifier
  returns Terrain/Road-only and logs it (the plan's low-confidence flag covers this — make the
  "no features available" case explicit).
- No spatial index exists today (confirmed) — the plan's light per-tile bucket is right; feature
  counts make O(n) borderline, bucket is cheap insurance.

## P0-4 — §3.5 Δp must be computed from quantized class steps, not from stored priorities

Verified: `GetOsmPriority` returns 0–100 per class (motorway 100, trunk 90, primary 80, secondary
75, tertiary 60, residential 55, … link classes 2–5 below parent), and the priority the network
actually stores/compares is the **composite** `osmPriority * 100 + materialOrderIndex` (the
`prio 8002` in doc 16's log). Two traps for A3:

1. **Raw differences are meaningless for Δp.** Bands are non-uniform (primary−secondary = 5,
   tertiary−secondary = 15) and the composite pollutes differences with material order. A3 must
   define an explicit class→step table (spec §3.3 grouping is the natural one: motorway/trunk=4,
   primary=3, secondary/tertiary=2, residential/unclassified=1, service/track=0) and compute
   `Δp = stepUpper − stepLower` from the **OSM class**, not the stored priority. The locked
   "*_link = no special-casing" then falls out correctly only if links map to their parent's step
   (motorway_link → step 4) — state that explicitly, else a motorway over its own exit ramp gets
   Δp=0 → 50/50 split and dips the ramp at the gore. Add a test for exactly that case.
2. `GradeSeparatedCrossing.Upper/LowerPriority` carry the composite today — keep them for the
   veto/tie logic but add the class step (or OSM class) alongside in A1.

Also in A2: the planner already adds `DeckThicknessOffset(span)` into the clearance
(`BridgeElevationPlan.cs:75–76`). `ComputeStructuralDepthMeters` must **replace** that offset,
not stack on it — otherwise clearance double-counts deck depth. Call the retirement out.

---

## Review-focus risk assessments

### R-1 Dip-as-pin smoother stability (A6) — mechanism sound, two real hazards found

Verified mechanics (all good):
- Pin honouring is keyed **purely on `PinnedElevation != null`** — nothing is conditioned on
  "is a bridge span" (`ApplyPinsToRaw`/`HardHoldPins`/`BuildPinExemptMask`,
  OptimizedElevationSmoother.cs:708–825). Dip pins on a lower road will be honoured with zero
  smoother changes. Pins are **re-asserted every iteration** (hard constraints), max 3 iterations.
- Deck pin (spline A) and dip pin (spline B) usually live in different elevation chains →
  filtered independently; cross-coupling only via junction harmonization.

Hazards the plan must absorb:
1. **Junction harmonization / `UnifiedJunctionProfileBlender` does NOT check pins** (verified —
   no `PinnedElevation` reference). A dip well near a junction can be partially overwritten by
   the Phase-3 blend each iteration → pin re-assert vs blend tug-of-war → potential kink at the
   well shoulder. "Junction-in-sag ⇒ L_max=0" protects the well *bottom* but junction blend zones
   extend tens of metres while `ClampRampToJunctions` margin is 8 m. **Amendment:** either make
   the junction blender pin-aware (skip/feather pinned sections — small, surgical), or enforce
   dip-well + ramps fully clear of junction blend radii (conservative L_max). The plan's
   3-iteration survival test should run **with junction harmonization enabled** and a junction
   placed inside the blend radius of the well — that's the failure mode that matters.
2. **Double-dip:** today `ApplyLowerRoadDips` lowers `TargetElevation`+edges AND carves the
   heightmap (verified, TerrainCreator.cs:388 → cross-sections + carve). With dip-as-pin, the
   cross-section drop comes from the smoother; the demoted resolver must carve from the **pinned
   final Z** and must NOT subtract again. Add an explicit no-double-drop assertion to the A6
   tests.
3. Slope-clamp exemption is pin ± half-window; a 4–6 m well's ramps can extend past the exemption
   zone, letting the clamp flatten the ramp mid-way (kink). Mitigation: emit the eased ramp
   sections as pins too (the plan's "(1−u)²(1+2u) eased target" already implies pinning the whole
   well incl. ramps — make that explicit), so the exemption mask covers the full well.

### R-2 Early-characterization chicken-and-egg — see P0-2 (blocking). Residual risk after the
amendment is acceptable: dip targets are deck-pin-derived (exact), deficits/ramp feasibility are
estimate-based with A7 as backstop + estimate-vs-final logging.

### R-3 Lateral re-projection — right fix, but the plan misdiagnoses the symptom and bundles a risky change

Verified: the documented drift (docs 11 §4.1 note, 13 §P3/P4) is a **station (along-track) error**
— `StartDistance/EndDistance` summed over **pre-Chaikin** points
(`PropagatePathStructureSegmentsToSpline`, OsmGeometryProcessor.cs:1076–1095) while sections are
sampled from the post-Chaikin spline ("shifted/resized by a few % of distance-to-the-bridge").
No doc evidence of a *lateral* error was found.

- **Re-projection of original endpoint nodes onto the final merged spline = correct and
  sufficient for the station error.** Note `RoadSpline` has **no closest-point API** today
  (verified) — `GetClosestDistanceAndNormalAt` must be built. Pitfall: closest-point on a curvy
  spline has multiple local minima; seed the search from the existing pre-Chaikin arc-length
  estimate and search a bounded window. Cheap and robust.
- **Chaikin damping is a separate, riskier change:** `ChaikinSmooth(…, 2)` runs uniformly over
  every merged path (OsmGeometryProcessor.cs:975); damping it at joins changes the corridor
  centerline geometry (and Chaikin is currently hiding genuine OSM corner kinks at abutments).
  **Amendment: split 0.3 in two.** 0.3a = projection API + re-projection (fixes the proven
  station error), render, judge. 0.3b = Chaikin damping **only if** the render still shows a
  true lateral offset. Don't pay the geometry-change risk before the evidence demands it.
- Deck = merged curve is preserved by both (the locked continuity requirement) — re-projection
  only moves the *labelled range*, never the curve. Confirmed safe.

---

## P1 — smaller required clarifications

1. **Pin-ordering conflict (abutment that is also a junction):** the plan says
   "1.85 pin deck+dip → 1.9 pin junctions", but in code Phase 1.9 junction pinning runs *inside*
   the Phase 1.8 block, **before** 1.85 (UnifiedRoadSmoother.cs:224–263). Last-writer-wins on
   `PinnedElevation`. Decide explicitly who wins when both pin the same section (recommend:
   bridge plan defers to junction pin and treats it as a fixed boundary condition → feeds R4.5's
   junction-in-sag rule), and document the actual order in the plan.
2. **A5 constraint-carry semantics underspecified.** Today spans are enumerated per-spline, no
   global order, no carry (verified). Define what "later bridges see fixed decks as constraints"
   means concretely — minimal v1: an already-pinned section lying inside a later bridge's span
   footprint contributes its pinned Z as an obstacle (kind=Road, its clearance). That is also the
   honest placeholder for deferred R6.
3. **Byte-identical baseline must be named per flag.** Rule-system flags off ⇒ identical to
   **today's merged-corridor output** (the default-ON path), not to the legacy separated-spline
   path. The 0.3a station fix intentionally changes default output — gate it under its own flag
   (`EnableBridgeStationReprojection`) so the regen diff is attributable.
4. **B3 ordering:** "deck underside ≥1.5 m over (graded) terrain" — graded terrain only exists
   after B1 stamps it. Fix the order: compute the effective bridge range against the *predicted*
   graded profile (approach ramp + 1:1.5 batter), then stamp; don't iterate stamp↔shrink.
5. **Verification additions:** (a) the A6 survival test variant with junction harmonization ON
   (see R-1.1); (b) Δp link-road test (motorway over own _link ⇒ no dip of the ramp at the gore,
   per P0-4); (c) A7 logs estimate-vs-final obstacle-Z delta; (d) a no-double-drop dip test.
6. Spec R5 (drainage) is absent from the plan — fine to defer, but list it under "Later" so it's
   a decision, not an omission (maxCutDepth=4 m default partially stands in for it).
7. **Roundabout ring bridges** (elevated roundabout interchanges) are invisible to the whole bridge
   pipeline — ring splines are built without `IsBridge`/`Layer`/`StructureSegments`
   (`RoundaboutMerger.CreateRoundaboutRingSpline`), a pre-existing gap. Deferred to a named
   follow-up after Phase A (added to doc 01 "Later"); related 0.4 fix landed: `FindCrossings`
   takes `ignoreOsmWayIds` so a bridge's own `highway=*` way never reports as an obstacle under
   itself (A1 must pass the span's `StructureSegment.OsmWayIds`).

---

## What verified clean (no action)

- Phase ordering claim (1.7 → 1.8/1.9 → 1.85 → iterations with Phase 2/2.5/2.6/3 → 4 → 5) —
  accurate apart from the 1.85/1.9 order nuance (P1-1).
- `UnifiedCrossSection.PinnedElevation` honoured by smoother passes exactly as claimed, span-type
  agnostic, re-asserted per iteration — the constraint-feed substrate is real and ready for dips.
- Dips today DO reach DecalRoad elevations (resolver mutates `TargetElevation` before DecalRoad
  generation reads it) — so moving the dip upstream into pins keeps DecalRoads consistent for free.
- `BridgeSpanFootprint` exists with point-in-polygon + AABB; `hasMergedSpans` gating matches the
  plan's description; legacy path isolation is as described.
- `ClampRampToJunctions` exists with the shape A4 wants to generalize (8 m junction margin,
  per-side clamp).
- Config plumbing convention (0.2) matches the existing 8-site pattern; engine-first with
  hard-coded defaults is the established workflow.
- Cherry-pick targets exist on the parked branch (`BridgeAbutmentFiller` @ 822b045;
  station-local `Obstacle.NaturalDeckZ` pattern).

---

## Required plan amendments before coding (summary)

| # | Amendment | Where |
|---|---|---|
| 1 | New step **A6.5**: skip (or pin-floor) `BridgeProfileSolver.ApplyToSpan` elevation override for planner-pinned spans; keep snapshot+edges; carry doc-16 §3b as render-judged follow-up | Phase A, Critical files |
| 2 | New step **A0**: concrete early estimator (centerline DEM + longitudinal 1D smooth, station-local), A7 logs estimate-vs-final delta | Phase A |
| 3 | 0.4 rewrite: thread `OsmQueryResult` → smoother (params plumbing), pre-project WGS84→local, **drop the raster fallback**, define water-surface Z v1, graceful no-features degrade | Phase 0 |
| 4 | A3: explicit class→step table for Δp (links inherit parent step); compute from OSM class not composite priority; A2 retires `DeckThicknessOffset` (no double count) | Phase A |
| 5 | A6: junction-blender pin-awareness (or dip-clear-of-blend-zone), pin the full eased well incl. ramps, no-double-drop carve | Phase A |
| 6 | Split 0.3 → 0.3a re-projection (build closest-point API, seeded search) now; 0.3b Chaikin damping only on render evidence | Phase 0 |
| 7 | P1 list: pin-order winner at junction-abutments; A5 carry semantics; per-flag baselines; B3 ordering; added tests; R5 to "Later" | throughout |

With amendments 1–4 in place the plan is implementable as phased; 1 and 2 are the ones that would
otherwise produce a "rule engine runs, render unchanged/sagging" first milestone.
