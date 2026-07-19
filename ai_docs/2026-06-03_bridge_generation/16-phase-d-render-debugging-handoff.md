# Handoff — Phase D first-render debugging (the deck still doesn't stand up)

**Date:** 2026-06-09 (written end-of-session for tomorrow)
**Branch:** `feature/bridges`
**State:** Phase A `37a655e` + B `e427f87` + C `54b3b81` committed. **Phase D + the terrain-embankment fix are
implemented this session but NOT committed.** 476 tests green.
**Reads with:** `14-bridge-elevation-rule-engine-and-rising-ramps-plan.md` (the plan), `15-...-implementation-handoff.md`.
**Memory:** `merged_corridor_bridge_plan`.

---

## 0. TL;DR

First in-game render of the planner→pin→dip path (`_generated_terrain`, log
`Log_TerrainGen_4096_20260609_005444`). Two things are now provably **working**, two are **broken**:

| | Status |
|---|---|
| Grade-sep detection on the merged corridor | ✅ `upper 394 (layer 1) over 390/391/393` — **3 crossings** recorded (was 0) |
| Phase D dip gating | ✅ `dippedRoads=0 bridgeRaised=4` — Rule-1 under-roads correctly left alone |
| **Terrain stamped under the deck (the embankment screenshot)** | 🔧 **root-caused + fixed this session, NOT re-rendered** |
| **The flyover deck still doesn't stand up** | ❌ **OPEN — the headline bug for tomorrow** |

The flyover (spline **394**, span `[129.9, 414.6] m` = 285 m) ends up as a **flat chord at z≈13.5 m with
`minClear=2.0 m`**, not the elevated viaduct the planner pinned. Root cause is identified (below) but **not yet
fixed**.

---

## 1. The render under analysis

- **Log:** `…/_generated_terrain/MT_TerrainGeneration/logs/Log_TerrainGen_4096_20260609_005444_Info.txt`
  (+ `_Warnings.txt`, `_Timing.txt`). 4096², ~62 s.
- This render contains Phase A/B/C **and** the Phase-D wiring (the `[BRIDGE-PLAN]` line proves the planner ran),
  but **NOT** the terrain-embankment fix (that was written after this render).
- Phase order confirmed from timestamps: `[BRIDGE-PLAN]` at **17.2 s** (Phase 1.85 pin, pre-smoothing) →
  `[BRIDGE-PROFILE] apply` at **54.6 s** (RefineSpans, post-smoothing) → `[GRADE-SEP] resolve` →
  `[BRIDGE-EXCAVATE]`.

Key log lines:

```
L653  DetectMidSplineCrossings … recorded 4 grade-separated crossing(s)
L650  GradeSeparatedCrossing: upper spline 394 (layer 1, bridge=True, prio 8002) over lower 391 (layer 0, prio 8002)
L651  …394 over 390 …    L652  …394 over 393 …
L662  [BRIDGE-PLAN] spans=17 raised=16 pinnedSections=1100 crossings=4
L683  Marked bridge span 28536900 on spline 394 [129,9,414,6]m as excluded (570 cross-sections)
L16976 [BRIDGE-PROFILE] apply spline=394 OVERRIDE=yes curve=Chord L=285,5m z0=13,83 z1=13,24 g0=11,8% g1=-15,0%
        bulge=0,00m seamKink=6,9/8,4deg minClear=2,0m start=conn end=conn [overshoot guard → chord; LOW CLEARANCE 2,0m < 5,0m]
L16980 [GRADE-SEP] resolve crossings=4 dippedRoads=0 maxDip=0,00m cellsLowered=0 bridgeRaised=4 alreadyClear=0 minClear=5,0m
L16981 [BRIDGE-EXCAVATE] bridges=3 cellsLowered=77 maxCut=3,24m undercut=0,10m
```

---

## 2. ✅ Regression #1 — terrain terraformed under the deck (the screenshot). ROOT-CAUSED + FIXED, needs re-render.

**Symptom (user screenshot):** the lifted deck sits on a tall, smooth, graded **embankment** of terrain — the
bridge spline terraformed the ground it spans. The rule it broke: *a bridge spline must not change terrain*
(painting was already correct).

**Root cause (a latent bug *exposed* by the lifted deck, not introduced by Phase D):**
`RoadMaskBuilder.BuildCombinedMaskWithElevation` → `RasterizeSplinePolygons`
(`Terrain/Algorithms/Blending/RoadMaskBuilder.cs`) filters the **excluded** span cross-sections out of the
per-spline list (`:110`), then stitches a corridor quad between every **list-consecutive** pair (`for i;
sections[i]→[i+1]`, `:331`) and interpolates their `TargetElevation` into the heightmap — with **no gap check**.
So the last pre-span section and the first post-span section became "consecutive" and rasterized **one
deck-height quad across the entire 285 m deck**. Pre-Phase-C the abutments were at ~2 m so the quad was invisible;
the lifted deck turned it into a ~14 m embankment.

**Fix applied (this session, uncommitted), `RasterizeSplinePolygons`:**
```csharp
// adjacent kept sections differ by 1 in LocalIndex; a bigger jump = an excluded run (a bridge/tunnel span)
if (cs2.LocalIndex - cs1.LocalIndex > 1)
    continue;
```
Span stays **unmasked** ⇒ terrain natural under the deck; only the real approach-ramp sections build embankments.
No-op for normal roads (consecutive LocalIndex ⇒ byte-identical). `UnifiedTerrainBlender:89` confirmed this is the
**single** heightmap-elevation source — no second ribbon path. `BuildCombinedRoadCoreMask` (paint/core mask) draws
per-section lines and never bridges → painting was unaffected. Test `RoadMaskBuilderBridgeGapTests`.

**⚠ NOT validated in-game** — needs a fresh `_generated_terrain` render to confirm the embankment is gone and the
deck spans natural ground. **Do this first tomorrow** (it's the cheapest confirmation and unblocks judging the deck
shape).

---

## 3. ❌ Regression #2 (HEADLINE, OPEN) — `RefineSpans` discards the deck pin; the flyover sags to a chord

**Symptom:** spline 394 deck = `curve=Chord z0=13,83 z1=13,24 minClear=2,0m` (L16976). It's a flat ~13.5 m chord
clearing the terrain by only 2 m — **not** the elevated viaduct. `minClear=2.0m` means the terrain under the span
peaks at ~11.5 m, so the deck should sit at ~16.5 m to keep the 5 m clearance the planner intended.

**Root cause (code fact, not inference):** `BridgeProfileSolver.ApplyToSpan` (`Terrain/Export/BridgeProfileSolver.cs:306`)
computes the deck curve from the **approach** anchors `roadBefore[^1]` / `roadAfter[0]` (the ramp sections *outside*
the span) and **overwrites** every span section's `TargetElevation` with it. **It never reads
`cs.PinnedElevation`.** So whatever the box-filter held the deck at (the planner's pinned ~16 m), RefineSpans throws
it away at 54.6 s and re-fits the deck to the approaches — which are only at **13.83 m** (z0). The `[overshoot
guard → chord]` confirms the cubic from those low, mismatched anchors bulged past the guard and fell back to a
straight chord. Net: the deck drops ~3 m below its pin and buries in the hill (`minClear 2.0m`, `seamKink 6.9/8.4°`).

This is the F2/§7b tension the plan flagged but the implementation didn't close: the plan said *"RefineSpans
re-curves the deck side with G0/G1"* assuming **the approaches are already elevated to the deck**. They are not —
see #4.

**Two coupled failures (both must be addressed):**

### 3a. RefineSpans must honour the pin
`ApplyToSpan` should not pull a pinned deck down to the approaches. Options (decide tomorrow):
- **(A) Anchor the deck to the pinned Z.** When span sections carry `PinnedElevation`, use it as the deck target:
  fit a curve that *holds the pinned plateau* and only curves the short transition near each abutment, rather than
  a single span-long cubic between the two approach anchors. Cleanest; keeps the elevated deck by construction.
- **(B) Skip RefineSpans entirely for pinned spans** — the box-filter hard-hold already produced a flat held deck;
  just capture the `BridgeSpanSnapshot` from the (pinned) span sections and leave the transition to the ramp. Least
  code, but loses the G1 abutment smoothing.
- **(C) Clamp:** after the curve, never let a span section fall below its `PinnedElevation`. Crude but safe.

Recommend **(A)** (closest to the plan's intent), with **(B)** as the low-risk fallback.

### 3b. The approach ramp doesn't reach the pinned deck at the abutment
Even with #3a fixed, `z0=13.83` shows the ramp section *immediately outside* the span only rose to ~13.8 m while
the deck wants ~16 m — a ~2.2 m **step at the abutment**. This is the box-filter symmetric-blur / Butterworth
issue (plan D7, §7 F2): the low-pass blends the low approach up to the held deck over the window but doesn't fully
reach it at the seam. So after #3a the deck stands at 16 but there's a step down to a 13.8 m ramp. Need either:
- make the box/filter ramp **reach** the pinned deck at the abutment (e.g. extend the hard-hold one section into
  the approach, or drive an explicit approach-grade up to the pin — plan D7 follow-up), or
- accept a steeper final ramp segment and let RefineSpans' abutment curve absorb it.

**Recommended sequence:** fix 3a first (deck stands up), re-render, *then* judge whether 3b's abutment step is bad
enough to need the ramp work.

---

## 4. 🔍 Instrumentation gap — we cannot see the pin Z in the log

`ApplyBridgeDeckPins` logs only the summary `[BRIDGE-PLAN] spans=17 raised=16 pinnedSections=1100`. There is **no
per-span `requiredDeckZ` / pin Z**, so we cannot directly confirm "pin was 16, RefineSpans dropped it to 13.5" vs
"the planner under-computed the pin." The code fact (§3 — RefineSpans ignores `PinnedElevation`) makes the deck
drop certain, but **add per-span logging tomorrow before fixing**, so the fix can be verified:
- In `BridgeElevationPlanner`/`ApplyBridgeDeckPins`: log per raised span `splineId, spanId, requiredDeckZ,
  approachZL/R, terrainMaxZ, clearanceUsed`.
- In `ApplyToSpan`: log whether the span had pins and the deck Z **before vs after** the override (so a pin-drop is
  obvious in one line).

This also feeds Phase E's debug PNG.

---

## 5. Known / deferred (not new regressions)

- **Deck-thickness clearance shortfall (deferred, agreed with user).** The pin uses the planner-default clearance
  `C` (no deck thickness); Rule-1 raise crossings are not post-dipped, so the **soffit** clears the under-road by
  `C − thickness` instead of `C`. Fix later by feeding `DeckThicknessOffset` into the Phase-1.85 planner pin.
  *Low priority — only relevant once §3 makes the deck stand up.*
- **`728 negative height values` fixed by PRE-SAVE SPIKE PREVENTION** (`_Warnings.txt`). Near the river (z≈0) the
  smoother/affine produced sub-zero heights that the spike guard clamped. Watch after §3; probably benign but worth
  a glance once the deck is correct.
- **Many short bridges at `LOW CLEARANCE x < 5,0m`** (splines 113/114/119/283×5/331/355/372/378/396, minClear
  1.4–4.0 m). These are short, non-grade-separated spans whose deck follows the road near terrain — the warning is
  the deck-above-terrain diagnostic, **likely expected**, not the 394 bug. Confirm a couple visually but don't
  chase unless they look wrong in-game.

---

## 6. Concrete next steps (suggested order)

1. **Commit nothing yet / or commit the two safe fixes** (terrain-mask gap guard + Phase D) so the working tree is
   clean before the §3 surgery. *(User's call — neither is in-game-validated; the mask fix is low-risk + tested.)*
2. **Re-render `_generated_terrain`** with the current uncommitted tree → confirm Regression #1 (embankment) is
   gone. Cheap, unblocks everything.
3. **Add per-span pin logging** (§4).
4. **Fix §3a** (RefineSpans honours the pin — option A or B) + a unit test (pinned span ⇒ RefineSpans keeps the deck
   ≥ pin, `minClear` not reduced). Re-render → 394 should stand at ~16 m, `minClear ≥ 0` (target ≥ 5 once 3b/thickness
   addressed).
5. **Judge §3b** (abutment step) from the render; do the ramp work only if needed.
6. Then resume **Phase E** (debug PNG + UI knobs) and **Phase F** (retire legacy).

---

## 7. Repro & where to look

- **Repro:** generate `_generated_terrain` (4096²) from the same OSM input; the interchange is corridor **394**,
  span `[129.9, 414.6] m`, over `390/391/393` (all prio 8002, layer 0) + an ~11.5 m terrain hill.
- **Code:**
  - Pin: `UnifiedRoadSmoother.ApplyBridgeDeckPins` (`:1194`) — Phase 1.85.
  - Pin honoured by 4 passes: `OptimizedElevationSmoother.CalculateChainElevations` / `ReSmoothChainFromExistingElevations`;
    `UnifiedRoadSmoother.ApplyAffineLevelingCore` / `BuildAffinePinWeights`; `EnforceMaxSlopeConstraint`.
  - **The bug:** `BridgeProfileSolver.ApplyToSpan` (`:306`) — does not read `PinnedElevation`.
  - Dips: `GradeSeparationResolver.ApplyLowerRoadDips` (`BuildPlannerActionLookup`) — working.
  - Terrain fix: `RoadMaskBuilder.RasterizeSplinePolygons` (`:331`).
- **Tests:** `BridgeDeckPinTests`, `BridgePhaseDDipTests`, `RoadMaskBuilderBridgeGapTests`, `BridgeSpanProfileTests`.

---

## 8. Open questions for the user

- §3a option **A vs B** (re-curve-to-pin vs skip-RefineSpans-for-pinned-spans)?
- §3b: invest in a proper rising approach-ramp now (plan D7), or accept a steeper abutment for v1 and re-render to
  judge?
- Commit the mask-gap + Phase-D fixes before the §3 surgery, or keep one uncommitted WIP until the deck stands up
  in-game?
