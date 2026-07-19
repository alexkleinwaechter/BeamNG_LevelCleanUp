# Plan — Bridge Deck Elevation, Rising Ramps & a Grade-Separation Rule Engine

**Date:** 2026-06-08
**Branch:** `feature/bridges`
**Status:** PLAN — investigation complete, **adversarially reviewed (two independent code-grounded passes, §17)**,
no code written yet. The mechanism below already incorporates the review corrections.
**Reads with:** `11-merged-corridor-bridge-continuity-plan.md` (the merged-corridor refactor this builds on),
`13-merged-corridor-debugging-handoff.md` (the suspect list), `07`/`05` (grade-sep + profile solver history).
**Memory:** `merged_corridor_bridge_plan`.

---

## 0. TL;DR

On the `_generated_terrain` render (log `Log_TerrainGen_4096_20260608_211502`), the big interchange flyover
(corridor **spline 394**, priority 8002, 522 m, bridge span [129.9, 414.6] m) and its approach ramps are
**flattened to ground/river level (~2.2 m)** instead of standing as an elevated viaduct with rising ramps; and
the terrain that pokes above the buried deck is carved into a **ditch** beside the span. Root cause is a cluster,
all merged-corridor specific:

1. **Grade-separation detection is dead on merged corridors.** `recorded 0 grade-separated crossing(s)` even
   though 394 flies over splines 390/391/393. `TryClassifyGradeSeparation` keys off **whole-spline**
   `Layer`/`IsBridge`, which are meaningless when the bridge is an interior arc-range of a corridor.
2. **Nothing elevates the deck.** `BridgeProfileSolver.ApplyToSpan` runs *after* smoothing and anchors the span
   to its in-spline ramp neighbours, which the chain low-pass already pinned to terrain level (`z0=2.24,
   z1=2.54`). There is **no mechanism to lift the deck above terrain or to build rising approach ramps.**
3. **The ditch is downstream of #2.** `minClear=-8.7 m` ⇒ the flat deck is buried where terrain rises to ~11 m;
   `BridgeDeckExcavator` shaves the 8.7 m that pokes above it (`cellsLowered=9979 maxCut=3.41 m`). Elevate the
   deck and the carve disappears.

Plus two user-reported regressions: **bridges vanished from the junction-harmonization debug image**, and roads
running **under** a bridge can be mis-detected as **at-grade crossings** with it (mid-spline-crossing detection
is unreliable).

**The fix (user-chosen approach):** decide each bridge span's **required deck elevation up front** (from
clearance over whatever is under it), assert it as an **elevation constraint that the smoother honours**, and let
the existing smoother + affine leveling **build the rising ramps** from the (correctly harmonized, low)
interchange ends up to the elevated deck — i.e. *play nice* with harmonization rather than fight it. Who moves at
a crossing (raise the bridge / dip the lower road / split) is decided by a small, explicit **height-adjustment
rule engine**. Robust **span-footprint** geometry replaces the fragile mid-spline-crossing test for "what is
under the bridge," and guarantees those roads are never harmonized as at-grade junctions with the deck.

Everything is **flag-gated** behind the existing `MergeStructuresIntoCorridor` and stays output-neutral with the
flag off. No dead code: each retired path is deleted in the same phase that replaces it.

> **Honesty note from review (§17):** "assert it as a constraint the smoother honours" is more invasive than the
> first draft implied. The pin must be respected by **four** elevation passes (box filter, re-smooth iterations,
> affine leveler, max-slope clamp), and the abutment ramp the box filter produces is a ±75 m blend, not a clean
> ramp. The profile solver re-curves the deck afterward. The mechanism in §7 reflects these corrections.

---

## 1. Confirmed diagnosis (this session)

Evidence from the generation log + code trace:

| Fact | Source |
|------|--------|
| 394 is priority 8002, 522 m, span [129.9, 414.6] m (285 m), 570 excluded sections | log L541, L682 |
| 394 has mid-spline crossings with 390, 391, 393 — **all also priority 8002** | log L650-652, L2196-2220 |
| 394's elevation *at those crossings* is 1.23 / 2.32 / −0.69 m (river/ground level) | log L6890-6897 |
| `recorded 0 grade-separated crossing(s)` | log L653 |
| Deck solved `z0=2.24 z1=2.54 g0=-11.4% g1=1.9% sag-capped(f=0.19) minClear=-8.7m` | log L22042 |
| Excavator carved `cellsLowered=9979 maxCut=3.41m` | log L22046 |

**Code mechanism, step by step:**

- The crossing classifier `NetworkJunctionDetector.cs:765` → `TryClassifyGradeSeparation:834-863` decides
  upper/lower from `splineA.Layer`/`splineA.IsBridge`. On corridor 394 these are the **merge-base way's**
  values, not the interior span's → the 390/391/393 crossings are recorded as plain `MidSplineCrossing`
  junctions (`:780-806`), i.e. **at-grade**. No `GradeSeparatedCrossing` is produced, so `GradeSeparationResolver`
  has nothing to act on.
- The chain elevation solve `OptimizedElevationSmoother.CalculateChainElevations:662-714` samples terrain at
  **every** section (including the excluded span — they are *not* filtered out of the chain,
  `ConcatenateChainCrossSections:766`), box-low-passes the whole corridor, and writes `TargetElevation`. The
  deck + ramps follow terrain to ~2 m.
- After smoothing, `BridgeProfileSolver.ApplyToSpan:306-477` fits the span to `roadBefore[^1]`/`roadAfter[0]`
  (the flat ramps) ⇒ flat deck. It can sag-cap but **cannot lift the deck above its anchors.**
- `BridgeDeckExcavator.Excavate:84-128` lowers every footprint cell whose terrain is above the (buried) deck ⇒
  the ditch.

**Ruled out:** `NetworkElevationGraph`'s `edge.IsBridge` (handoff suspect #2) is **diagnostic-only** — every use
is a log line (`:163-169`, `:376-396`); it does not mishandle the chain. So the flattening is **not** from
edge-level bridge special-casing. (Both reviewers independently confirmed.)

**Why this is merged-specific (vs. legacy):** with the flag off, the bridge is its own spline with
`IsBridge=true` everywhere, so (a) the crossings classify as grade-separated and (b) the legacy solver had a
whole-spline deck to act on. The merge made the bridge an interior span and left the elevation decision and the
grade-sep detection keyed on flags that no longer describe it.

### 1a. A/B legacy render — DONE 2026-06-08 (the decisive confirmation)

The user regenerated the same map with `MergeStructuresIntoCorridor=false`
(log `Log_TerrainGen_4096_20260608_224716`). The same flyover is now the **separate bridge spline 417**
(layer 1, 571 CS). Side-by-side:

| Metric | **Legacy (merge OFF)** | **Merged (merge ON)** |
|---|---|---|
| grade-separated crossings | **5 recorded** | **0** |
| crossing classification | `upper 417 (layer 1, bridge=True) over lower 419/420/422 (layer 0)` | mis-read as at-grade `MidSplineCrossing` |
| under-roads dipped | **5 roads, maxDip 4.86 m** (`[GRADE-SEP] resolve dippedRoads=5`) | none |
| deck z0 / z1 | **6.41 / 5.50 m** | 2.24 / 2.54 m |
| excavator | `cellsLowered=1305 maxCut=3.41 m` | `cellsLowered=9979` |
| minClear | −5.7 m | −8.7 m |

**Three conclusions that re-shape this plan:**

1. **Grade separation is established by `Layer`, NOT `IsBridge`.** Legacy logged `upper 417 (layer 1) over lower
   (layer 0)` — `TryClassifyGradeSeparation`'s **`splineA.Layer != splineB.Layer`** branch fired first. On the
   merged corridor the whole-spline `Layer` is the merge-base way's (the layer-0 approach), so the test fails.
   **`StructureSegment.Layer` already carries the span's `layer=1`** (seeded `= feature.Layer`,
   `OsmGeometryProcessor.cs:809`). ⇒ The detection fix is simply: use each spline's **effective layer at the
   crossing** = the containing span's `StructureSegment.Layer` if the crossing is inside a span, else the
   whole-spline `Layer`. This is cleaner and more faithful than the draft's "span membership decides upper/lower"
   (§5 updated accordingly). It also restores the 5 under-road dips automatically.

2. **The deck really is ~4 m lower in merged (6.41 vs 2.24), and it is NOT a chain-shape difference.** The chain
   diagnostic shows legacy chains the bridge with its ramps just like the merged corridor
   (`Chain 3: 424→417[B]→423`, degree-2 continuations) — so both low-pass the same 285 m of river terrain. The
   gap therefore comes from **per-spline affine leveling on three short splines (legacy) vs one 522 m affine on
   the merged corridor**, and from `ApplyToBridge`'s junction-contributor lookup vs `ApplyToSpan`'s in-spline
   neighbour. This confirms the deck-elevation work is needed (§6/§7) and that it is an *affine/anchor* problem,
   not a low-pass-window problem.

3. **Even legacy is imperfect — it still buries the deck (minClear −5.7 m) and excavates 1305 cells.** Legacy
   "looks right" only *relatively*: the deck is 4 m higher and the under-roads are dipped, so it reads as
   grade-separated. Neither mode actually lifts the deck to clear the terrain/obstacle. So this plan's
   **deck-Z-from-clearance + terrain clearance (§4, §6)** is genuinely *new value beyond legacy*, not just a
   regression revert — which is the right target given the user wants a clean elevated viaduct, no ditch.

---

## 2. Why "fix grade-sep detection" alone is not enough

⚠ **Sequencing consequence of the A/B (important):** restoring grade-sep detection on its own would dip the
under-roads (good) **but the merged deck is still at ~2.2 m** (the affine/anchor problem, conclusion 2), so the
dips would dig the mainlines into a deep hole *beneath a ground-level deck* — arguably worse-looking than today.
**The deck-height fix (§6/§7) and the detection fix (§5) must land together** (or the deck fix first). Phase A
(detection) is still the right first *code* step because it is isolated and testable, but it must not be shipped
to users alone without Phase C/D.


Even with detection fixed, `GradeSeparationResolver` only ever **holds the deck Z and dips the lower road**, or —
under the *priority veto* (`:74`) — arches the deck *interior* while keeping the abutments at ramp level. For 394
the crossings are **equal priority** (8002 = 8002), so the veto never fires; the resolver would **dip the 4 km
mainlines 390/393 under the flat deck** — wrong, and it still never elevates the deck or builds rising ramps.
(`IsGeneratedDeckAt:171` only blocks dipping a deck-lower, not a plain road, so the mainlines *would* be dipped.)

The missing capability is exactly what the user described: **set the deck's required Z first, then let the
smoother grow the ramps to it.** That is an elevation-pipeline change, plus a decision layer (the rule engine)
for who moves. This plan delivers both.

---

## 3. Current vs. target pipeline

### 3.1 Current order (single generate pass), inside `UnifiedRoadSmoother`

```
build network → detect junctions (Phase 1.8) → Phase 1.9 JunctionElevationPinner (junction Z pins)
→ Phase 2.0 CalculateNetworkElevations:
     MarkStructureExclusions (sets IsExcluded + StructureSpanId)   ← span tags first appear HERE
     → CalculateChainElevations (terrain low-pass → TargetElevation)   ← deck+ramps go flat here
     → affine endpoint leveling (BuildEndpointTargetLookup → ApplyAffineLeveling)
→ Phase 2.5 banking → Phase 2.6 roundabouts
→ Phase 3 junction harmonization + unified profile blending
   + post-loop RetargetTerminatingRoadsToSettledThrough (re-affine) / banking-match / connector-grade / edge
→ (iterates up to 3×; iterations 1+ use ReSmoothChainFromExistingElevations)
→ export junction debug image (UnifiedRoadSmoother.cs:452)   ← BridgeSpans NOT yet populated here
→ Phase 4 terrain blending (stamp; skips IsExcluded) → Phase 5 material paint
```

Then, **after** smoothing returns, in `TerrainCreator.cs:349-393`:

```
DiagnoseSeams → GradeSep.PlanConstraints → BridgeProfileSolver.ApplyStructuralProfiles (override span; fills BridgeSpans)
→ GradeSep.ApplyLowerRoadDips → BridgeDeckExcavator.Excavate
```

Two ordering facts the review surfaced and the design must respect:
- **`StructureSpanId` is assigned only in `MarkStructureExclusions` (UnifiedRoadSmoother.cs:1138), inside
  Phase 2.0** — *after* junction detection (Phase 1.8). So any detector/planner step that needs to know "is this
  XY on a deck" cannot read `StructureSpanId` yet. **`spline.StructureSegments` (arc-ranges) and per-section
  `DistanceAlongSpline` ARE available at build time** and are the discriminator to use early (§4a).
- **`network.BridgeSpans` is populated only by the post-smoothing solver (TerrainCreator.cs:373)**, so the debug
  image (exported *inside* the smoother) cannot read it (§9).

### 3.2 Target order

Add an early **span-tagging** pass and a flag-gated **planner**, and demote the post-smoothing solver to a
*refinement*:

```
build network → detect junctions
→ NEW (Phase ~1.7) TagStructureSpans  ← hoist span tagging out of Phase 2.0 so StructureSpanId exists early
   (and so the detector + planner can use it)                                         [§4a]
→ Phase 1.8 detect mid-spline crossings  ← span-footprint: under-deck roads become GradeSeparatedCrossing,
   never at-grade junctions                                                            [§5]
→ NEW BridgeElevationPlanner (flag-gated; no-op when no span tags)  ← rule engine: required deck Z + per-crossing
   outcomes; sets PinnedElevation on span sections; records dip plans                  [§4, §6, §7]
→ Phase 2.0 CalculateChainElevations — HONOURS PinnedElevation (box filter + hard-hold)
→ affine leveling — EXEMPTS pinned sections (blended at the boundary)                  [§7, §17-F1]
→ Phase 2.5/2.6 … Phase 3 harmonization … (re-smooth iterations also honour the pin)   [§7, §17-F6]
→ export junction debug image — overlays spans from StructureSegments + GradeSeparatedCrossings  [§9]
→ Phase 4 stamp (skips span) → Phase 5 paint
```

Then in `TerrainCreator` (after smoothing), trimmed:

```
BridgeProfileSolver.RefineSpans (clean G0/G1 curve over the now-elevated, ramp-matched span; capture snapshot)
→ GradeSep.ApplyLowerRoadDips (only crossings the planner assigned "dip"/"split", against final stamped Z)  [§6]
→ BridgeDeckExcavator.Excavate (now a near-no-op — deck sits above terrain; kept as safety net)
```

The crucial inversion: **deck Z is an *input* to smoothing, not an output of it.** Continuity stays structural
(one corridor, one smoother). Harmonization is not fought — corridor *endpoints* keep their harmonized targets;
we only constrain the *interior* span and exempt it from the passes that would otherwise overwrite it.

---

## 4a. Span membership must be available before junction detection (critical ordering fix)

*(Review finding #3 / #1 — without this, both the detector change and the planner are silent no-ops.)*

`StructureSpanId` today is set inside Phase 2.0; detection (Phase 1.8) and the planner (pre-2.0) run earlier.
Fix: extract the **tagging** half of `MarkStructureExclusions` into an idempotent `TagStructureSpans(network)`
that runs right after network build (Phase ~1.7), setting `cs.StructureSpanId` from
`spline.StructureSegments[*].[Start/End]Distance` vs `cs.DistanceAlongSpline`. Keep `IsExcluded` marking where
it is (Phase 2.0) — or move both; either is fine as long as `StructureSpanId` precedes detection.
- Idempotent + flag-gated: with `MergeStructuresIntoCorridor` off there are no `StructureSegments` to tag, so it
  is a no-op and legacy stays byte-identical.
- All later "is this XY on a deck" tests (detector §5, planner §4, debug image §9) then read `StructureSpanId`
  (or, equivalently, the arc-range directly) — one consistent discriminator, never whole-spline `IsBridge`.

---

## 4. The height-adjustment rule engine

A small, explicit, **testable** decision layer (`BridgeElevationPlanner`, pure). It replaces the *decision* parts
of `GradeSeparationResolver.PlanConstraints` (the dip/veto/arch choice); the *mechanics* (eased lower-road dip
well, heightmap carve) are reused unchanged.

### 4.1 Inputs per span

- **Span footprint** — the polygon swept by the span's cross-section `CenterPoint ± (EffectiveRoadWidth/2)·Normal`
  (XY only; `CenterPoint`/`NormalDirection`/`EffectiveRoadWidth` are set at network build —
  `UnifiedCrossSection.FromSplineSample` — so the query is valid pre-elevation; banked widening is ignored,
  acceptable for detection). §5.
- **Obstacles under the span:**
  - **Under-roads** `U` = every non-excluded cross-section of any *other* spline whose XY falls inside the
    footprint (spatial query, §5), with its Z and priority. **Use the stamped/smoothed under-road surface where
    available, not raw terrain** (§6, review finding #5).
  - **Terrain** = the heightmap max under the footprint (so the deck also clears a hill/embankment — this is what
    fixes the `minClear=-8.7 m` hill that no road-only rule would catch). Sample only within the road half-width
    (not the full affected range) to avoid over-raising next to an unrelated tall feature (D5).
- **Approaches** — the in-spline sections immediately outside the span on each side (the ramps).
- **Required clearance** `C` = `MinBridgeClearanceMeters + deckThicknessOffset(span)` (soffit clearance, reusing
  `GradeSeparationResolver.DeckThicknessOffset:228`).

### 4.2 The rules (formalised from the user's spec)

Let `requiredDeckZ(s)` along the span = the running max of (each under-road Z + C) and (terrain max + C) over the
footprint near station `s`. For each under-road crossing, classify with **priority** (`UpperPrio` = bridge
corridor's, `LowerPrio` = under-road's) and the **ramp test**:

> **Ramp test** (the user's "Steigungsregel"): the structure clearly wants to be an elevated, ramped bridge —
> i.e. `requiredDeckZ` exceeds the approach elevation by more than `C` on **both** sides (there is genuinely
> something to clear), AND each side has approach length to build a ramp. As a *secondary* confirmation where the
> data is trustworthy, the mean grade over the last `RampDetectionLengthMeters` (default **30 m**) of each
> approach, measured on the **raw terrain** profile, is ≥ `RampDetectionMinGradePct` (default **1.5 %**).
> **Primary driver is the clearance requirement**, NOT the (possibly already-flattened) smoothed grade — see the
> circularity note §4.4 and decision D1.

- **Rule 1 — Ramp ⇒ raise the bridge, leave roads under it alone.** Set the span's deck targets to
  `requiredDeckZ` and pin them (§7). The under-roads keep their elevation. The smoother grows the rising ramps.
  *This is the 394 case and the headline fix.* ("Higher-priority/through roads don't change height.")
- **Rule 2 — No ramp ⇒ the lower-priority road loses.** `LowerPrio < UpperPrio`: dip the under-road (existing
  eased well + carve). `LowerPrio > UpperPrio` (a higher road over our corridor): raise the bridge instead
  (interior clearance pin, today's veto).
- **Rule 3 — Equal priority & not a ramp ⇒ split.** Raise the bridge by `split·deficit` and dip the under-road by
  `(1−split)·deficit` so the pair reaches `C` together. `split` default **0.5** (`GradeSepSplitRatio`).
- **Tie-break — flyover over flyover** (both corridors carry a span at the crossing, review finding #7): the
  spline whose span *contains* the crossing XY is `upper`; if both contain it, the higher-priority one is
  `upper`; if still tied, the one whose `requiredDeckZ` is greater (it has more to clear) is `upper`. Recorded so
  the other side is treated as the lower member.

Outcome per crossing is recorded (enum, mirroring `GradeSeparationAction`) for the debug image (§9) and the log.

### 4.3 Is 50/50 possible or too naive? (answering the user's question)

**Possible, and acceptable as a *fallback*** — with three caveats baked in:

1. It is the **last resort**, reached only for *equal-priority, non-ramp* true crossings. Real flyovers (incl.
   394) are caught by **Rule 1** and fully elevated without dipping anything, which is the natural look. So 50/50
   rarely fires.
2. The two halves are **not symmetric in cost**: raising the deck propagates into the approach ramps (the pinned
   smoothing), while dipping the under-road is a local eased well. The design keeps them independent (deck pin
   vs. dip plan) so each stays smooth; the split just sets each target.
3. Make the ratio a **parameter** (`GradeSepSplitRatio`, default 0.5) so it is tunable; bias toward "mostly raise
   the bridge" (e.g. 0.7) if the symmetric bend looks odd in-game (D4).

So: ship 50/50 as the configurable Rule-3 fallback; expect Rule 1 to dominate.

### 4.4 The circularity to resolve (flagged for review → resolved, D1)

Rule 1's ramp test "do the approaches rise?" is circular if measured on the **already-flattened smoothed Z**.
Resolution: drive the deck-elevation decision from the **clearance requirement** (`requiredDeckZ` from obstacles
+ terrain), which is independent of the broken elevation; use the avg-slope check only as *secondary
confirmation* on the **raw terrain** approach profile (or skip it when `requiredDeckZ` already exceeds the
approach terrain by > C). This is the single most important design point — confirmed as **D1 recommended**.

---

## 5. Robust "what is under the bridge" detection (replaces the fragile crossing test)

The user: *mid-spline crossings are unreliable and a road under the bridge must never be a junction with it.*

- **New spatial query** `BridgeSpanFootprint`: per span, build the XY footprint from the span cross-sections
  (center ± half-width along normal) and test other splines' section centers for containment (AABB + per-segment
  quad; spans are few). **Independent of mid-spline sampling** — catches every under-deck road the 100-sample
  crossing loop misses.
- **Effective layer decides upper/lower (the A/B-confirmed fix, §1a conclusion 1).** Legacy classified via
  `splineA.Layer != splineB.Layer`. Re-point `TryClassifyGradeSeparation` to each spline's **effective layer at
  the crossing** = the containing span's `StructureSegment.Layer` (seeded `= feature.Layer`,
  `OsmGeometryProcessor.cs:809`) if the crossing XY is inside a span, else the whole-spline `Layer`. The existing
  `Layer != Layer` ordering then works on merged corridors exactly as it did on legacy separate bridge splines —
  the span with the higher effective layer is `upper`. (Span membership is *how* we recover the layer; the
  *ordering* is by layer, not by membership alone — this is simpler and more faithful than the first draft.)
- **Suppress at-grade junctions under a deck.** In `NetworkJunctionDetector` before creating a `MidSplineCrossing`
  junction (`:780`), if the crossing XY is inside a bridge **span** of either spline (arc-range test on
  `StructureSegments`, available early per §4a — **not** whole-spline `IsBridge`), record the
  `GradeSeparatedCrossing` (upper/lower by effective layer above) and **skip** the at-grade junction. This fixes
  detection (suspect #1), restores the 5 under-road dips the A/B showed, and guarantees the under-road is never
  harmonized to the deck.
- **Tie-break — both sides carry a span (flyover over flyover):** if effective layers are equal, the span whose
  `requiredDeckZ` is greater (more to clear) is `upper`; if still tied, higher priority; the §4.2 rule engine then
  decides who moves.
- **Verified-neutral consumers of the suppressed junctions** (review finding #4): `NetworkJunctionHarmonizer`
  (`ComputeMidSplineCrossingElevation`), `UnifiedJunctionProfileBlender.ApplyMidSplineCrossingInfluences`
  (`:856`), the affine endpoint-target lookup, and `GradeSeparationResolver.ClampRampToJunctions` all iterate
  `network.Junctions`; removing the under-deck crossing junction is the *intended* fix there. DecalRoad/banking
  read cross-section data, not the junction object → unaffected. Regression test: a real at-grade junction *near
  but not under* a span is still created + harmonized.

Net: "no road under the bridge is a crossing with the bridge" becomes **structurally true**, on merged corridors,
keyed on span membership.

---

## 6. Deck-elevation determination & the ordering problem

The deck Z needs the under-roads' Z (for clearance), but we want it before/at smoothing, when the under-roads
aren't finalised. Resolution (corrected per review finding #5):

1. **First cut at planner time.** Compute `requiredDeckZ` from the **best available** under-road Z — the
   *stamped/smoothed* surface if the under-road has already been processed, else its terrain sample — plus the
   **terrain max** under the footprint. Pin the span to it (§7). Terrain-max is the safe driver for the 11 m hill;
   the road term is a first cut.
2. **Post-smoothing guarantee.** After the corridor + under-roads settle and are stamped, the trimmed
   `GradeSep.ApplyLowerRoadDips` reads the **final stamped** under-road Z and, for "dip"/"split" crossings, dips
   the residual amount to *guarantee* `C`. Rule-1 (raise) crossings need no post pass.

⚠ **Review correction (finding #5):** road smoothing can raise the driven surface **above** raw terrain (fill on
a dip, junction plateaus, harmonization). So a raw-terrain-only first cut can **under-estimate** clearance, the
pinned deck can be too low, and the post-dip well deeper than naive. Hence step 1 uses the stamped under-road Z
where available, and the Phase-D assertion is `minClear ≥ 0` (not "excavator exactly no-op"). If a hard guarantee
without the post-dip is wanted, take D2 (one extra smoothing iteration recomputing deck Z from settled under-road
Z) — heavier, deferred.

---

## 7. The injection mechanism — constrain the span, let the smoother build ramps

Add a per-cross-section **centerline pin** (the missing peer of the existing edge-constraint and junction-pin
mechanisms): new `UnifiedCrossSection.PinnedElevation` (`float?`, default null), set only on span sections by the
planner. Review found the pin must be honoured by **four** passes, not one — otherwise it is silently overwritten:

1. **Box low-pass** `CalculateChainElevations:681-713`: after sampling `rawElevations`, overwrite pinned sections'
   raw value with `PinnedElevation`; **and hard-hold after the filter** (`if (cs.PinnedElevation is {} p)
   smoothed[i] = p`). The hard-hold is required because the box filter at the span edge is a *symmetric blur*, not
   a ramp (review finding #2): without it the deck edge sags toward terrain and never reaches the pin.
2. **Re-smooth iterations** `ReSmoothChainFromExistingElevations:723-753` (iterations 1+): apply the same
   hard-hold every iteration (review finding #6) — else the deck drifts each pass.
3. **Affine leveler** `AffineJunctionLeveler.Apply` + post-loop `RetargetTerminatingRoadsToSettledThrough`
   (review finding #1, the headline correction): both add a per-distance correction to **every** sample including
   the pinned interior. Exempt pinned sections from the correction, **blending the correction to zero across a
   short margin approaching the pin boundary** so no step appears at the abutment (design detail → D6). *(For a
   long corridor like 394 whose endpoints are far from the span, the endpoint error — and thus the correction — is
   near zero, so in practice the exemption is mostly a safety net; it matters for spans near a corridor end.)*
4. **Max-slope clamp** `EnforceMaxSlopeConstraint:709` (on in some presets, `RoadSmoothingPresets.cs:301`):
   exempt the pinned-span neighbourhood (review secondary note) — clamping the abutment ramp would both flatten
   the deck and violate the standing **no-grade-clamp** stance (memory `feedback_no_grade_clamp`).

**Abutment ramp reality (honest framing):** the box filter blends the low approach up to the held deck over only
±`windowHalf` (~75 m at the default 301-sample window). An 11 m rise over ~75 m is ~15 % — steep. The profile
solver (7b) re-curves the deck side with G0/G1; if a longer, gentler ramp is wanted, the ramp length must be
driven explicitly (e.g. widen the blend or build a dedicated approach grade), tracked as a follow-up (D7).

**7b. Demote the solver to a refinement.** `ApplyStructuralProfiles` → `RefineSpans`: since the span is already
elevated and ramp-matched, the solver fits the clean G0+G1 curve over the span (anchored to the now-elevated
approaches) + interior arch for any Rule-2/3 local obstacle, and **captures the `BridgeSpanSnapshot`** (unchanged
role). Sag-cap stays. The deck no longer needs the solver to *find* its height — only to *smooth* it. Write-order
rule: the planner's pin is the elevation source through smoothing; `RefineSpans` is the final authority on the
exact span curve (it reads the smoothed, pinned deck + elevated approaches).

---

## 8. Consumer audit (the whole-spline `IsBridge` cleanup — no dead code)

Done **in the same phases** that touch each site (review findings #3, #6 expanded this list):

| Site | Today | This plan |
|---|---|---|
| `NetworkJunctionDetector.TryClassifyGradeSeparation:843-846` | whole-spline `Layer`/`IsBridge` | span-membership first (§5, via `StructureSegments`), `Layer` second, §4.2 tie-break |
| `GradeSeparationResolver.PlanConstraints:54` | dip/veto/arch decision | **replaced** by `BridgeElevationPlanner`; delete decision code, keep dip mechanics |
| `GradeSeparationResolver.ApplyLowerRoadDips:116` | dips all non-veto | dips only planner "dip"/"split" crossings vs final stamped Z |
| `BridgeProfileSolver.ApplyStructuralProfiles` (span branch) / `ApplyToSpan` | finds + sets deck Z | **`RefineSpans`** (§7b): smooth + snapshot only |
| `BridgeProfileSolver.ApplyToBridge:479` + legacy branch `ApplyStructuralProfiles:273-290` | legacy whole-spline solve | **Phase F deletion** (review #3 — was omitted in draft) |
| `BridgeProfileSolver.FindConnectedRoadContributor:837` | legacy junction walk | Phase F deletion |
| `BridgeProfileSolver.DiagnoseSeams:112` (whole-spline `ShouldGenerateDeck`) | read-only seam diag; no-op on merged corridors | **decide: keep as legacy-only diag or delete in Phase F** (review #3 — was orphaned) |
| `NetworkJunctionHarmonizer:232-234` | `!MergeStructuresIntoCorridor` guard | re-verify under new under-road suppression; keep |
| `NetworkElevationGraph` `IsBridge:163-396` | diagnostic only | leave (harmless) |
| `OsmGeometryProcessor.RasterizeSplinesToLayerMap:529-538` | **whole-spline `IsBridge` skip, NO merge guard** | **convert to per-span skip OR confirm mask non-authoritative** (review #6 — significant omission; suspect #3) |
| `DecalRoadGenerator.IsGeneratedBridge:148-150` | whole-spline; per-span path exists at `:361` | verify benign (merge-base `IsBridge=false` ⇒ false) or convert in Phase F (review #6) |
| `StructureElevationCalculator` (`:27`, heavy whole-spline `IsBridge`) | **dead** — no call-site in `BeamNgTerrainPoc` | delete in Phase F (review #6 — removes a perennial audit distractor) |
| `BridgeDeckExcavator` | shaves above-deck terrain | unchanged; near-no-op once deck elevated |

**Derived `IsBridge`/`IsTunnel` (plan 11 §4.1/§7):** once planner + detector + refiner key off
`StructureSegments`/`StructureSpanId`, make `RoadSpline.IsBridge`/`IsTunnel` **derived** and delete the legacy
whole-spline separation path — **Phase F, after in-game sign-off**, to keep flag-off working until validated.

---

## 9. Restore bridges in the junction-harmonization debug image

`NetworkJunctionHarmonizer.ExportJunctionDebugImage:846-950` draws `network.CrossSections` + `network.Junctions`.
Merged bridges create **no junctions**, so they vanished. ⚠ **Review correction (finding #2):** the image is
exported *inside* the smoother (`UnifiedRoadSmoother.cs:452`), where `network.BridgeSpans` is still **empty**
(populated later in `TerrainCreator`). So source the overlay from **`spline.StructureSegments`** (available after
§4a tagging), **not** `BridgeSpans`:

- Outline each bridge **span footprint** (§5) from `StructureSegments` + span cross-sections; mark its two
  abutment stations.
- Overlay each `GradeSeparatedCrossing` (now populated during detection) as a distinct marker — visibly **not**
  an at-grade junction (directly answers "show that the under-road is not a crossing with the bridge").
- Rule-outcome colouring (raised/dipped/split): the planner runs before the image export in the target order
  (§3.2) and records the outcome on each `GradeSeparatedCrossing`, so it is available. (If the per-span deck
  outcome isn't yet on the crossing object, colour by `GradeSeparationAction` only.)
- One-line legend. No new export plumbing — same PNG, more layers.

---

## 10. Parameters (UI + presets, round-tripped like the existing bridge knobs)

On `TerrainCreationParameters`/`TerrainGenerationState`, mirrored through preset Result/Exporter/Importer and the
"Bridge/Tunnel Structure Handling" panel (plan 11 Phase 2 pattern):

- `MinBridgeClearanceMeters` — already exists; reused as `C` base.
- `RampDetectionLengthMeters` (default 30) — ramp-test window.
- `RampDetectionMinGradePct` (default 1.5) — ramp-test secondary threshold.
- `GradeSepSplitRatio` (default 0.5) — Rule-3 raise/dip split.
- `BridgeTerrainClearanceEnabled` (default true) — include terrain-max in `requiredDeckZ`.

---

## 11. Phased implementation (each phase builds + tests green; flag-off byte-identical)

> Build: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`
> Test: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true`

**Phase A — Early span tagging + robust under-bridge detection + grade-sep on merged corridors.**
`TagStructureSpans` hoisted before detection (§4a); `BridgeSpanFootprint`; span-membership branch (via
`StructureSegments`) in the crossing detector; suppress at-grade junctions under a deck. Tests: a 394-like
corridor over an equal-priority road records a `GradeSeparatedCrossing` and creates **no** at-grade junction; a
road *beside* the span still makes a junction; flag-off unchanged. *Outcome:* `grade-separated > 0`; under-roads
no longer harmonized to the deck. (No elevation change yet.)

**Phase B — `BridgeElevationPlanner` (rule engine), pure + unit-tested.**
Inputs §4.1, rules §4.2, outcomes recorded; flag-/empty-span early-return. Pure over a network snapshot, no
pipeline wiring. Tests: Rule 1 (ramp ⇒ raise), Rule 2 (dip / veto-raise), Rule 3 (equal split), terrain-max
obstacle, span-vs-span tie-break. The heavily-reviewed core.

**Phase C — Pin mechanism honoured by all four passes.**
`PinnedElevation`; box filter + re-smooth hard-hold; affine exemption with boundary blend; slope-clamp exemption
(§7). Planner runs pre-Phase-2.0 and pins span deck Z (first cut §6). Tests: pinned interior ⇒ monotone rising
ramp each approach + flat-ish held deck; **pin survives 3 iterations**; **affine on a short corridor (span near an
end) does not tilt the deck**; non-bridge splines byte-identical; slope-clamp-on preset does not flatten the ramp.

**Phase D — Demote solver to `RefineSpans` + planner-driven dips + clearance guarantee.**
Solver smooths/snapshots only; `ApplyLowerRoadDips` consumes planner outcomes vs final stamped Z; delete
superseded `PlanConstraints` decision code. Tests: end-to-end merged corridor over a hill + equal-prio road ⇒
elevated deck, rising ramps, `minClear ≥ 0`, excavator `maxCut` small.

**Phase E — Debug image (from `StructureSegments`) + UI params.** §9 overlays; §10 knobs round-tripped. Tests:
preset round-trip; image smoke test asserts span overlay present.

**Phase F — Retirement (after in-game sign-off only).** Derived `IsBridge`/`IsTunnel`; delete `ApplyToBridge` +
legacy branch + `FindConnectedRoadContributor` + (decide) `DiagnoseSeams` + dead `StructureElevationCalculator`;
convert/confirm `RasterizeSplinesToLayerMap` + `DecalRoadGenerator.IsGeneratedBridge`. §8. Flag-off kept working
until validated; this is the "no dead code" close-out.

---

## 12. How to A/B and validate

- **Legacy baseline — DONE (§1a).** Confirmed: legacy detects grade-sep via `Layer` (5 crossings), dips the
  under-roads, and sits the deck ~4 m higher; but it still buries the deck (minClear −5.7 m). So the target is
  *better than legacy* (clear the obstacle), and the headline detection fix is the `Layer`-based one (§5).
- **Per-phase:** focused filter `--filter "FullyQualifiedName~Bridge|~Structure|~GradeSep|~Merge"`.
- **In-game (after Phase D):** regenerate `_generated_terrain`; confirm 394 stands as an elevated viaduct with
  rising ramps, mainlines 390/393 unchanged (Rule 1), no ditch, deck flush; debug image shows span + grade-sep
  markers.
- **Logs:** `recorded N grade-separated crossing(s)` (> 0), `[GRADE-SEP] plan … raised=…`, `[BRIDGE-PROFILE] …
  minClear ≥ 0`, `[BRIDGE-EXCAVATE] cellsLowered` small.

---

## 13. Open decisions for the user (resolve in review)

- **D1 (most important).** Ramp-test source for Rule 1: drive off the **clearance requirement** and use avg-slope
  only as secondary confirmation on raw terrain (**recommended**, avoids §4.4 circularity), vs. insisting on a
  measured rising slope (risks not firing on the very bug we're fixing).
- **D2.** Clearance ordering: pre-smoothing first-cut (stamped where available) + post-dip correction
  (**recommended**, §6), vs. a full extra smoothing iteration recomputing deck Z from settled under-road Z
  (cleaner guarantee, more cost).
- **D3.** *(Resolved by review.)* The deck pin **must** be hard-held through the filter (a soft pin sags at the
  abutment, finding #2). Kept as hard-hold; profile solver re-curves.
- **D4.** Rule-3 `GradeSepSplitRatio` = 0.5, or bias toward raising the bridge (e.g. 0.7) for flyover-like looks.
- **D5.** `BridgeTerrainClearanceEnabled` default true (needed for 394's hill); sample terrain only within the
  road half-width to avoid over-raising next to unrelated tall features.
- **D6 (new, from review #1).** Affine-vs-pin boundary: exempt pinned sections from the affine correction and
  blend the correction to zero over a short margin so no step appears at the abutment — confirm this approach (vs.
  applying the full affine to ramps only and accepting the deck is pinned absolutely).
- **D7 (new, from review #2).** Ramp length: accept the box-filter's ~75 m blend (steep for large rises) for v1,
  or drive a longer, configurable approach-ramp grade explicitly? (Follow-up candidate.)

---

## 14. Risks & mitigations (do not destroy existing work)

| Risk | Mitigation |
|---|---|
| Breaking road smoothing / affine leveling | We **add** a pin honoured by the four elevation passes with explicit exemptions (§7); non-bridge data untouched; flag-off byte-identical. Regression test: non-bridge networks unchanged + short-corridor affine-vs-pin test. |
| Breaking junction harmonization | Corridor endpoints keep harmonized targets; we never pin/move junction Z. Under-road suppression *removes* false at-grade junctions (a fix); re-verify the harmonizer bridge guard (§8) + test a real at-grade junction near (not under) a span still harmonizes. |
| Pin overwritten by a pass we missed | Reviews enumerated all four (box, re-smooth, affine+retarget, slope-clamp); Phase-C tests assert the pin survives 3 iterations and short-corridor affine. |
| Pinned deck kink at the abutment | Hard-hold + profile-solver G1 re-curve; assert `seamKink` bounded. |
| Clearance not actually guaranteed | First cut from stamped under-road Z + post-dip vs final Z; assert `minClear ≥ 0` (§6). |
| Over-raising from terrain-max | Sample only within road half-width (D5). |
| Excavator still carving | Near-no-op once deck elevated; assert small `maxCut`; keep as safety net. |
| Dead code from the inversion | Phases D/F delete superseded decision + legacy paths in-place; expanded list (§8) incl. `ApplyToBridge`, `DiagnoseSeams`, dead `StructureElevationCalculator`. "No dead code" is a Phase-F exit criterion. |
| Missed whole-spline `IsBridge` consumer | §8 now includes `RasterizeSplinesToLayerMap` (unguarded) + `DecalRoadGenerator.IsGeneratedBridge`. |
| Multi-span corridors | Planner + pins + footprint are per-span; test a 2-span corridor. |

---

## 15. Tests (new + adapted)

- **Footprint/detection:** road under a span ⇒ `GradeSeparatedCrossing`, no at-grade junction; road *beside* ⇒
  normal junction; mid-spline-sampling-miss case caught by footprint; flag-off detector unchanged.
- **Rule engine (pure):** the three rules + terrain obstacle + span-vs-span tie-break + multi-span; exact
  outcomes + `requiredDeckZ`.
- **Pin/smoother:** pinned plateau ⇒ rising ramps (monotone, bounded grade) + held deck; survives 3 iterations;
  short-corridor affine does not tilt the deck; slope-clamp-on does not flatten; non-bridge byte-identical.
- **End-to-end:** corridor over hill + equal-prio road ⇒ elevated deck, ramps rise, `minClear ≥ 0`, excavator
  small; flag-off identical.
- **Harmonization regression:** at-grade junction near a span still harmonized; corridor endpoints unchanged.
- **Debug image:** span outline + grade-sep markers present (sourced from `StructureSegments`).

---

## 16. One-paragraph rationale (for the PR body)

The merged-corridor refactor made bridges interior arc-ranges of their through-road but left the deck-elevation
decision and grade-separation detection keyed on whole-spline `IsBridge`/`Layer`, which no longer describe the
bridge — so flyovers flattened to terrain, their ramps never rose, and the buried decks were carved into ditches.
This change tags bridge spans early, finds what is under each span with a robust footprint query (not the flaky
mid-spline-crossing test), decides each span's required deck elevation from the clearance it must keep over the
roads and terrain beneath it, and constrains that elevation through the corridor's smoothing — honoured by the
low-pass, the re-smooth iterations, the affine leveler and the slope clamp — so the existing smoother grows the
rising approach ramps to meet it. A small explicit rule engine decides who moves at each crossing (raise the
bridge for a ramped structure, dip the lower-priority road, or split equally). Grade separation, the profile
solver, the excavator and the debug image are re-pointed at span membership; the deck becomes an input to
smoothing instead of a late override, so the elevated viaduct and its ramps are continuous by construction and
harmonization is never fought.

---

## 17. Review log — adversarial findings & how the plan changed

Two independent code-grounded reviews ran against the first draft. Both confirmed the diagnosis (§1) and the
"`edge.IsBridge` is diagnostic-only" ruling. They found the following defects, all now folded into §§3a–9 above:

- **F1 — Affine leveling clobbers the pin (headline).** `AffineJunctionLeveler.Apply` (+ post-loop
  `RetargetTerminatingRoadsToSettledThrough`) adds a per-distance correction to **every** sample including the
  pinned interior. The draft's "harmonization untouched, only the interior pinned" was false. **Fix:** §7 step 3 —
  exempt pinned sections, blend the correction to zero at the boundary (D6).
- **F2 — Box filter gives a symmetric blur, not a clean ramp.** At the span edge the filter averages terrain and
  pin over ±75 m, sagging the deck edge and lifting the approach. **Fix:** §7 step 1 hard-hold the pin after the
  filter; §7 honest note on the ~15 % ramp grade for large rises (D7).
- **F3 — `StructureSpanId` not set until Phase 2.0 (both reviewers, critical).** The detector (Phase 1.8) and the
  pre-2.0 planner would see only `-1` → silent no-ops; the headline fix would not fire. **Fix:** new §4a —
  `TagStructureSpans` hoisted before detection; all early sites key off `StructureSegments`/`StructureSpanId`.
- **F4 — Clearance not guaranteed by a raw-terrain first cut.** Road smoothing can lift the under-road above raw
  terrain (fill/plateau/harmonization). **Fix:** §6 uses stamped under-road Z where available + post-dip vs final
  Z; assertion relaxed to `minClear ≥ 0`.
- **F5 — Span footprint geometry pre-elevation.** `CenterPoint/Normal/Width` are set at build (OK); `BankAngle`
  isn't until Phase 2.5 (footprint is XY-only, OK). **Noted** in §4.1.
- **F6 — Pin clobbered by re-smooth iterations.** Iterations 1+ re-box-filter with no pin re-application.
  **Fix:** §7 step 2 hard-hold every iteration.
- **F7 — Slope clamp flattens the ramp** when `EnableMaxSlopeConstraint` is on (some presets). **Fix:** §7 step 4
  exempt the pinned neighbourhood (consistent with no-grade-clamp memory).
- **F8 — Debug image cannot read `BridgeSpans`** (empty at in-smoother export time). **Fix:** §9 sources spans
  from `StructureSegments`.
- **F9 — Dead-code list incomplete.** Added `ApplyToBridge` + legacy `ApplyStructuralProfiles` branch +
  `DiagnoseSeams` disposition to Phase F (§8).
- **F10 — Missing whole-spline `IsBridge` consumers.** Added `OsmGeometryProcessor.RasterizeSplinesToLayerMap`
  (unguarded — significant) + `DecalRoadGenerator.IsGeneratedBridge`; noted `StructureElevationCalculator` is
  dead (delete) (§8).
- **F11 — Span-vs-span (flyover over flyover) tie-break under-specified.** Added §4.2 tie-break.
- **F12 — Flag-off gating implicit in the diagram.** Made `TagStructureSpans` + planner early-return explicit
  (§3.2, §4a, Phase B).

**Residual design choices for the user:** D1, D2, D4, D5, D6, D7 (§13). D3 was resolved by the review (hard-hold).
