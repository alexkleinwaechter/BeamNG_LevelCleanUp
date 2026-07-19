# Doc 16 — Bridge-over-bridge crossings must clear each other (handoff / prompt)

**Date:** 2026-07-08 · **Status:** HANDOFF PROMPT — designed here, **NOT implemented**. Next session
starts from this doc. **Branch:** `feature/bridge_embankment_containment`.
**Read this alone — self-contained.** Follow-up to doc 09 §9.3 (the 14 real `[BRIDGE-CLEAR]`
deficits) and doc 15 (seamless *merging* decks). Doc 15 was about two decks that FUSE into one
roadway; this doc is about two decks that CROSS at different heights and must not touch.

---

## 0. The prompt (user, 2026-07-08)

> Make bridge-over-bridge crossings clear each other.

Today `EnableBridgeBridge` is **detection-only**: it emits `[BRIDGE-BRIDGE] … detection only, R6
multi-level deferred` at [NetworkJunctionDetector.cs:794](../../BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs#L794)
and [:1056](../../BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs#L1056) and changes
no geometry. The goal is a real, in-solver resolution so a flyover deck and the deck beneath it keep
the required vertical gap.

## 1. Why bridge-over-bridge is its own problem (not just "priority distribution again")

At an ordinary crossing the lower member is a road pinned to the ground, so the only levers are
*raise the deck* or *dip the lower road into the terrain*. At a bridge-over-bridge crossing **both
members are decks**, which changes everything:

1. **Both are movable in Z.** The lower member is not terrain-bound — its deck can go up *or* down.
   The current planner does not use this: when the lower member is a bridge it takes the
   `RaiseBridgeVeto` branch and treats the lower deck as **immovable**, lifting the upper deck by the
   whole deficit ([BridgeElevationPlanner.cs:449-461](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs#L449-L461)).
   That is only correct when the lower deck genuinely cannot move; usually the separation should be
   **shared** between raising the upper deck and lowering the lower deck, by priority — the deck-vs-deck
   analogue of §3.5.
2. **You cannot "dip" a deck into the ground.** `ApplyLowerRoadDips`
   ([TerrainCreator.cs:433](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L433)) carves a terrain
   well — meaningless for a lower member that is itself an excluded, lifted deck. Lowering the lower
   member has to be a **deck-profile pin** (its own `TargetElevation` / floor), solved pre-smooth,
   NOT a terrain carve.
3. **The deficit is usually at a span END.** Bridge-over-bridge happens at interchanges — flyover
   ramps crossing a trunk viaduct near the ramp's abutment. That is exactly where the interior-arch
   floor cannot lift: `AssertCrossingClearances` even *skips* the central band because "RefineSpans
   arch owns this crossing," and only warns for crossings OUTSIDE it
   ([GradeSeparationResolver.cs:1222](../../BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs#L1222)).
   So the classic doc-04 "end-of-span clearance has no delivery mechanism" gap is precisely the
   bridge-over-bridge gap.
4. **No post-solve raise is allowed.** Under `EnableNaturalProfileAnchor` the post-solve
   `ApplyApproachRaiseRamps` is skipped and replaced by the read-only `AssertCrossingClearances`
   ([TerrainCreator.cs:417-424](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L417-L424)). User
   doctrine: **nothing post-solve may write road/deck elevations; post-solve shapes bare terrain
   only.** So the resolution MUST be pre-solve/in-solver — a planned pin the smoother/RefineSpans
   deliver — never a late correction.

**Net:** the machinery exists to DECIDE (priority tables) and to DETECT (grade-sep crossings), but
there is (a) no deck-vs-deck *sharing* rule, (b) no *delivery* of a span-end deck lift/drop, and
(c) no *solve-order guarantee + cycle handling* for a stack of ≥2 bridges. The result is doc 09's 14
honest `[BRIDGE-CLEAR]` warnings on Manhattan, some NEGATIVE (`upper=60 lower=72 clearance=-0.60m` —
the flyover deck sits BELOW the deck it crosses).

## 2. What already exists — reuse, don't rebuild

- **Detection + data.** `NetworkJunctionDetector.TryClassifyGradeSeparation` records a
  `GradeSeparatedCrossing` ([GradeSeparatedCrossing.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/GradeSeparatedCrossing.cs))
  with `UpperSplineId/LowerSplineId`, `UpperLayer/LowerLayer` (the OSM `layer` tag = stack order),
  `UpperPriority/LowerPriority`, `UpperIsBridge/LowerIsBridge`, `IsBridgeOverBridge`, and the crossing
  XY. Both the sampler path (:782) and the footprint fallback (:1030) populate it. This is the input.
- **The `LowerIsBridge` branch already exists but is a stub.** At
  [BridgeElevationPlanner.cs:449-461](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs#L449-L461)
  a lower bridge → `RaiseBridgeVeto`, `DeckTargetZ = ob.Z + sep`, **but only when
  `EnableSpanSolveOrder` is on**, and one-directionally (upper takes 100%). Replace this branch's
  policy, keep its plumbing.
- **Span solve order + deck carry** (`EnableSpanSolveOrder`,
  [BridgeElevationPlanner.cs:64/252/450](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs#L64))
  already plans high-priority spans first and carries a pinned deck into later spans as a fixed
  obstacle (that is where `ob.Z` for a lower bridge comes from). This is the ordering backbone — the
  resolution must run *within* it so the lower deck's Z is final when the upper reads it.
- **Priority tables** — `ClassStepFor` / `RaiseShareFor`
  ([BridgeRuleSystemOptions.cs:329/371](../../BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs#L329))
  give the raise/lower share from Δp. Reuse verbatim for deck-vs-deck.
- **Delivery hooks** — `PlanFloorConstraints` → `RefineSpans` arch
  ([TerrainCreator.cs:379/388](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L379)) lifts the deck
  interior; the doc-14 landing-anchor path (`BridgeProfileSolver`, `EnableDeckToDeckContinuity`) already
  pins a span END to a target Z. The span-end lift/drop should extend one of these, not invent a third.
- **Acceptance test** — `AssertCrossingClearances`
  ([GradeSeparationResolver.cs:1200](../../BeamNgTerrainPoc/Terrain/Export/GradeSeparationResolver.cs#L1200))
  already computes final deck-vs-lower clearance and counts shortfalls. Extend it to tag
  bridge-over-bridge rows; the target is "0 unresolved bridge-over-bridge shortfalls (or each residual
  is a deliberately-logged reduced-clearance decision)."

## 3. Design direction

**a. Make `EnableBridgeBridge` a resolution gate, not a log.** Keep the `[BRIDGE-BRIDGE]` line, but
when on, route `IsBridgeOverBridge` crossings into the new deck-vs-deck policy below (independent of
`EnableSpanSolveOrder`, though it still needs the ordering — see (d)). Off ⇒ byte-identical (today's
detection-only log). Decide whether it implies/enables the ordering or asserts it as a prerequisite.

**b. Deck-vs-deck mutual distribution (the core policy).** Replace the one-directional veto-raise
with a shared move: `raiseShare = RaiseShareFor(stepUpper − stepLower)`; raise the upper deck by
`raiseShare · deficit` and LOWER the lower deck by `(1 − raiseShare) · deficit`, both as pre-solve
deck pins/floors. A motorway flyover over a minor ramp raises little and pushes the ramp deck down
most of the way; equal classes split 50/50. The lower deck's drop is a `DeckTargetZ` reduction fed
through the same span pinning the upper raise uses — never a terrain dip.

**c. Deliver at span ends (the hard part — coordinate with doc 04's open item).** Because the crossing
is usually outside the arch band, extend the delivery so a bridge-over-bridge crossing near a span end
pins that span END up/down to its target (mirror the doc-14 `EnableDeckToDeckContinuity` end-anchor
machinery, which already sets a span end to a sampled Z + grade). This is the same missing lever as
doc 04 §4-A "end-of-span clearance," but delivered PRE-solve as a pin, satisfying the anchor doctrine.
If both a span end lift and the partner's end drop are needed, they compose through the solve order.

**d. Solve order + cycles.** Order the stack by OSM `layer` (the tag is exactly the crossing order),
tie-broken by priority then longer span, so each deck is final before the deck above reads it — extend
`EnableSpanSolveOrder`'s ordering to key on the bridge-over-bridge layer relation, not just owner
priority. Detect cycles (A over B over A via different crossings — physically impossible but mappable);
log-warn and fall back to first-solved-wins, like doc 14's circular-landing guard.

**e. Feasibility + honest escalation.** Raising the upper deck steepens its approaches; lowering the
lower deck erodes ITS clearance over whatever IT crosses (cascade). Run the moves through
`EnableRampFeasibility`'s slope/cut limits and a cascade re-check; when the mutual move is infeasible,
escalate to reduced clearance and keep the `[BRIDGE-CLEAR]` warning (accepted deficit) — **never** a
post-solve raise. A logged, bounded reduced-clearance is a success; a silent dam is not.

## 4. Cautions

- **Doctrine:** in-solver only; nothing post-solve writes deck/road Z; flag-off byte-identical; commit
  per step; build + full test suite green before each commit.
- **Do not regress legitimate stacked decks.** Doc 15 relies on genuinely-stacked crossings staying
  put (the `MaxLandingAnchorZGapMeters = 6` classifier; e.g. spline 51 under the Brooklyn deck). A
  bridge-over-bridge crossing whose gap is ALREADY ≥ required must stay untouched (`AlreadyClears`).
  Only crossings with a real deficit get moved.
- **Lowering a deck can invalidate a doc-15 seamless overlap or a doc-14 landing** if that same deck
  also MERGES with a third deck elsewhere. Resolve merges (landing anchors) as authoritative first;
  a deck that is a landing target must not be lowered out from under its ramp. Re-read the doc-14/15
  landing records before moving any deck.
- **`layer` tag is not height.** It encodes local order, not metric elevation; use it only for solve
  ORDER, take the actual Z from the solved/carried deck section (`ob.Z`), as the stub already does.
- **Natural Profile Anchor stays on.** The anchor already lets Priority Distribution's dips and
  pre-solve deck raises through and only drops the post-solve top-up (see doc 15 handoff notes / the
  `anchorOn` gate). This work lives entirely on the pre-solve side, so it composes with the anchor —
  verify the moved decks are final before `AssertCrossingClearances` runs.
- **Terrain:** nothing here writes terrain. The excavator/overlap stampers read the final deck Z for
  free once the pins move.

## 5. Verification recipe

1. **Diagnostics first (baseline, flag off):** regen Manhattan 4096; capture the current
   `[BRIDGE-CLEAR]` set (doc 09 §9.3: 14 shortfalls, incl. negative `upper=60 lower=72
   clearance=-0.60m`, `upper=134` short vs 6 roads). Tag which are `IsBridgeOverBridge`.
2. **Flag on:** every bridge-over-bridge `[BRIDGE-CLEAR]` shortfall either clears (clearance ≥
   required) or is replaced by an explicit `[BRIDGE-BRIDGE] reduced-clearance …` decision line — no
   NEGATIVE clearances remain (a deck below the deck it crosses is never acceptable).
3. **No regressions:** road-under-bridge `[BRIDGE-CLEAR]` counts unchanged or better; doc-14/15
   landings/overlaps unchanged (`DeckSeamDiagnostic` / `overlapMaxGap` same); `[DAM-REPORT]`
   not worse; flag-off byte-identical; full suite green (extend with a two-deck-stack fixture:
   flat approaches, a flyover deck crossing a trunk deck near the flyover's end → shared move clears
   it; and a 3-deck stack composing through solve order).
4. **Render (user judges):** at the interchange, drive the trunk deck under the flyover and the
   flyover over the trunk — a visible, consistent gap; no deck clipping through another; approaches
   still smooth (no new kink from the span-end pin).

Log dir: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\MT_TerrainGeneration\logs\`.
Related: doc 04 §4 (end-of-span clearance delivery, the shared hard part) · doc 09 §9.3 (the deficits
+ the anchor doctrine) · doc 14 (`EnableDeckToDeckContinuity` end-anchor — the delivery hook to mirror)
· doc 15 (seamless *merging* decks — the "don't regress legitimate stacks / landings" constraint).
Key files: `BridgeElevationPlanner.cs` (classification, ~L449), `GradeSeparatedCrossing.cs` (the data),
`NetworkJunctionDetector.cs` (~L782/L1030 detection), `BridgeProfileSolver.cs` (span-end anchor
delivery), `GradeSeparationResolver.AssertCrossingClearances` (~L1200 acceptance test),
`TerrainCreator.cs` (~L417 anchor gate, ~L433 dips).
