# Rule-1 infeasible-raise → dip lower-priority under-roads

**Date:** 2026-07-01
**Branch:** `feature/bridge-rule1-dip-lower-priority` (off `feature/bridge_merged_corridor`)
**File touched:** `BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs` (+ tests)

## Problem

Real OSM motorway bridges are being *raised* to clear lower-priority roads that pass
beneath them, when the lower road should instead be *dipped* under the existing bridge.

Observed on the winningen regen (`Log_TerrainGen_4096_20260701_201529`):

| Span | Owner spline (prio) | Crosses (prio) | Planner decision |
|---|---|---|---|
| `28536654` | 360 (**10002**) | spline 164 (**8001**) | `action=RaiseBridge plannedDip=0,00` |
| `28536652` | 361 (**10002**) | spline 164 (**8001**) | `action=RaiseBridge plannedDip=0,00` |

The motorway deck arches up ~1.3 m (`[BRIDGE-PROFILE] arch=1,30m/1,36m`) to clear a
strictly-lower-priority road (164), and the planner even logs that the required approach
ramp is too steep:

```
[BRIDGE-PLAN] WARN upper=47 lower=164 (Road): Rule-1 raise exceeds absolute ramp slope for the approach length
```

### Root cause

The spans are classified as **Rule-1** ("ramped viaduct") in
`BridgeElevationPlanner.PlanSpan` (the `if (isRamp)` branch, lines ~197–220). Rule-1
*"raises the whole deck to clear everything; leaves every road under it alone"* — it emits
`RaiseBridge` for every obstacle and **never dips**, independent of priority. The
priority/dip logic lives in `ClassifyNonRampCrossing` (the `else` branch), which Rule-1
spans never reach. Ramp-feasibility is computed but, for Rule-1, *"never blocks a viaduct —
it only warns"* — so an infeasible raise happens anyway.

There is no user parameter that changes this: the `isRamp` threshold is code
(`liftFull >= clearance c`), and the flags that would dip (`EnablePriorityDistribution`,
`EnableDipAsPin`) are bypassed for Rule-1. This is a code-behavior fix.

## Decision

When a Rule-1 raise is **infeasible** (exceeds the absolute ramp slope for the available
approach length) and the span carries at least one **dippable** obstacle, dip the
lower-priority road(s) instead of force-raising the deck. Conservative: feasible Rule-1
viaducts are unchanged.

Decisions made during brainstorming:
- **Trigger:** only when the raise is *infeasible* (not "always dip lower priority"). Keeps
  the flyover behavior for feasible spans; minimal regression surface.
- **Burden split:** **no raise, dip the full deficit.** The deck holds the approach chord;
  the lower road dips the entire clearance deficit. (When the span also carries a
  non-dippable obstacle, the deck still rises for *that* obstacle and the dippable roads dip
  against the raised deck.)
- **Gate:** reuse the existing `EnableRampFeasibility` flag — the dip becomes the action for
  the warning that flag already emits. No new flag. Already ON in the winningen preset, so it
  activates for testing; byte-identical when the flag is off.

## Design

All changes are inside the `if (isRamp)` block of `PlanSpan`. Downstream machinery
(`isRaised`/`deckZ`/pin emission, `ReconcileDipAgainstDeck`, and
`GradeSeparationResolver`'s post-solve dip application) already handles un-raised,
dip-emitting spans and needs no changes.

### Trigger (all must hold)

1. `rules?.EnableRampFeasibility == true` (`feasibility`), **and**
2. `approachesBothSides` and `raiseAboveApproaches > raiseMaxAbs + Eps` — the existing
   condition that today produces `rampWarning` (line ~207), **and**
3. at least one obstacle in the span is **dippable**.

If (3) is false, behavior is unchanged: full Rule-1 raise + warning.

### Dippable obstacle

An obstacle `ob` is dippable when **all** hold:
- `ob.Crossing.HasLowerSpline` (there is a road to dip),
- `ob.Crossing.LowerKind == BridgeObstacleKind.Road` (rail/water are never lowered),
- `ob.Crossing.LowerPriority < ob.Crossing.UpperPriority` (strictly lower priority), and
- not a bridge-under: `!(rules.EnableSpanSolveOrder && ob.Crossing.LowerIsBridge)` — mirrors
  the veto guard at line ~335.

Everything else (rail, water, equal-or-higher priority, bridge-under, terrain) is
**non-dippable**.

### Behavior when triggered

- Recompute the deck requirement (`spanPinZ` / `spanLift` / `requiredDeckZFull` /
  `liftFull`) from **non-dippable obstacles + terrain only**. If there are no non-dippable
  obstacles, `spanPinZ` stays `-inf` ⇒ `isRaised == false` ⇒ the deck holds the approach
  chord (motorway stays flat) and no pins are emitted.
- **Dippable** obstacles → emit
  `Action = DipLowerRoad`, `LowerRoadTargetZ = DeckRefAt(ob) - SeparationFor(ob)`,
  `DipDepthMeters = deficit` (same shape as `ClassifyNonRampCrossing`'s dip, using the
  per-station `DeckRefAt(ob)` for graded consistency).
- **Non-dippable** obstacles → emit `Action = RaiseBridge` exactly as today
  (`DeckTargetZ = graded ? DeckRefAt(ob) + liftFull : spanPinZ`), carrying `rampWarning` if
  the reduced raise is still infeasible.

The existing `ReconcileDipAgainstDeck` pass (lines ~264–268) then re-fits each dip against
the final shared deck Z at its station — so if a non-dippable obstacle raised the deck, the
dips shrink accordingly for free.

### Winningen outcome

Spans `28536652` / `28536654` carry only the lower-priority road 164 (nothing
non-dippable): deck stays flat, 164 dips the full deficit. The motorway is no longer raised.

## Testing (TDD, `BeamNgTerrainPoc.Tests/Elevation/BridgeRampFeasibilityTests.cs`)

Built on the existing `BuildScenario(upperClass, lowerClass, rules)` helper + short
approach lengths to force `isRamp` + infeasibility.

1. `Rule1_Infeasible_LowerPriorityRoad_Dips` — motorway Rule-1 span, short approaches, over a
   lower-priority road, raise > absolute slope → `Action == DipLowerRoad`, span deck **not
   raised**. (The winningen case.) *Starts red.*
2. `Rule1_Infeasible_NoDippableObstacle_StillRaises` — same geometry but the under-member is
   **rail** (or equal/higher priority) → `Action == RaiseBridge`, warning present. Unchanged.
3. `Rule1_Feasible_LowerPriorityRoad_StillRaises` — Rule-1 span with ample approach length
   (feasible) → `RaiseBridge`. Confirms only *infeasible* spans dip.
4. `Rule1_Infeasible_MixedObstacles_RaisesForRail_DipsRoad` — span over both rail and a
   lower-priority road → deck raises for the rail, road gets `DipLowerRoad`. *Starts red.*
5. `Rule1_Infeasible_FeasibilityFlagOff_StillRaises` — `EnableRampFeasibility=false` →
   byte-identical old behavior (full raise, no warning).

## Out of scope

- The "always dip strictly-lower priority" full doc-27 §4 rewrite (feasible spans still
  raise here).
- Reading a real OSM bridge's true deck elevation into the DEM estimate (the deeper reason
  the planner mis-sees motorway ≈ under-road at ground level).
- The railway-bridge tall-embankment case (`bridge_28536654` railway sibling) — separate
  issue, deferred by user.
- Any change to `RoadClearanceMeters` / structural-depth defaults.
