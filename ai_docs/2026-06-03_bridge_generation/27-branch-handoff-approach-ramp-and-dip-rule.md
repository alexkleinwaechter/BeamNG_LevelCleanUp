# Doc 27 — Branch handoff: approach-ramp governor + the priority-dip rule

**Date:** 2026-07-01 · **From branch:** `feature/bridge-approach-ramp-governor` (created off the **wrong** base,
`24d593d`) · **Target branch:** `feature/bridge_merged_corridor` (where this work belongs).
**Purpose:** everything needed to carry this work onto the correct branch and continue, without re-reading the
whole session. Self-contained — read this alone.

---

## 0. TL;DR

On `feature/bridge-approach-ramp-governor` there are **5 commits** (+ this doc) fixing the winningen bridge
"ramp destroys the road over a long distance" report, all **code-calculated, no new user parameters**, **508
tests green**. They were committed on the wrong base and need to move to `feature/bridge_merged_corridor`.

**One thing is decided but NOT yet implemented:** the merged planner must dip strictly-lower-priority under-roads
again (§4 below). Pick that up first on the correct branch.

---

## 1. How to move these commits to `feature/bridge_merged_corridor`

Commits to move (oldest→newest):

```
b954ad9  feat(bridge): doc 25 — code-calculated approach-ramp governor
cfde286  fix(bridge): clamp deck-cubic tangents to the grade governor (kill the hump)
50ef2e8  fix(bridge): widen abutment-fill lateral feather with height (kill the walls)
c36a222  docs(bridge): doc 26 — approach-ramp follow-ups + open-issue backlog
927cab0  diag(bridge): name dipped roads + the crossing behind a tight minClear
(+ this doc 27, committed on top)
```

First check the relationship:

```
git merge-base feature/bridge-approach-ramp-governor feature/bridge_merged_corridor
git log --oneline --left-right --graph feature/bridge-approach-ramp-governor...feature/bridge_merged_corridor
```

- **If `24d593d` is an ancestor of `feature/bridge_merged_corridor`** → rebase cleanly:
  ```
  git rebase --onto feature/bridge_merged_corridor 24d593d feature/bridge-approach-ramp-governor
  ```
- **Otherwise** → cherry-pick onto the target:
  ```
  git checkout feature/bridge_merged_corridor
  git cherry-pick b954ad9 cfde286 50ef2e8 c36a222 927cab0 <doc27-hash>
  ```

Likely-touched files (watch for conflicts if the merged-corridor branch changed them):
`OptimizedElevationSmoother.cs`, `BridgeProfileSolver.cs`, `BridgeAbutmentFiller.cs`, `GradeSeparationResolver.cs`,
`TerrainCreationParameters.cs` (untouched here), plus the tests + `ai_docs/…/25,26,27`.

> **Rebuild the host app** after moving — the winningen regen `212453` proved the app runs a *built* binary; a
> `dotnet build` of `BeamNgTerrainPoc` alone is not enough, the app (`BeamNG_LevelCleanUp`) must pick it up.

---

## 2. What each commit does (so a cherry-pick is legible)

### `b954ad9` — approach-ramp governor (doc 25 §1–5)
**File:** `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs`.
**Problem:** the chain box filter blurred a pinned deck into the merged corridor over a **fixed ±window/2**
(`SmoothingWindowSize=301` ≈ ±75 m) regardless of raise height → the connected road changed elevation over a
long, raise-independent distance. `MergeStructuresIntoCorridor=false` avoided it only by taking the bridge out of
the shared chain.
**Fix:** after `HardHoldPins`, run `ApplyBridgeApproachRamps(cs, smoothed, naturalSmoothed, 6f)`:
- capture a **pin-free** filtered profile (`rawNatural` → `naturalSmoothed`) alongside the pinned one;
- for each approach section outward from a deck-run boundary, write the grade-bounded ramp
  `deckZ − grade·distance` where it sits above natural; restore natural beyond it.
- Ramp length is therefore **`L = raise / 6%`** (`BridgeApproachRampGradePct = 6f`, the COMBRI motorway max —
  code constant). Wired into **both** `CalculateChainElevations` (iter 0) and `ReSmoothChainFromExistingElevations`
  (iter 1+). Flat deck (`§5b–d`) untouched.
**Tests:** `BridgeApproachRampTests` (+4); `BridgeDeckPinTests.BoxLowPass_…` assertion updated to the ~6 % grade.

### `cfde286` — clamp deck-cubic tangents (the hump)
**File:** `BeamNgTerrainPoc/Terrain/Export/BridgeProfileSolver.cs`, `ApplyToSpan`.
**Problem:** the deck cubic used the **steep natural approach grade** (spline 21 `g0=29.4%`, 15 `23.5%`) as its
Hermite end tangents and overshot **2–3 m above the flat pin** (a hump).
**Fix:** before `SelectCurve`, clamp `g0/g1` to `±maxDeckGradePercent` (6 %) → `g0Curve/g1Curve`, used **only** for
the curve. Deck still passes through `z0/z1`; the **unclamped** grades are kept for the honest `seamKink`/`maxGrade`
diagnostics. Hump on a 60 m span over a ±25 % approach: **~3.5 m → <1 m**.
**Test:** `BridgeSpanProfileTests.SteepNaturalApproachGrade_DoesNotHumpTheFlatDeck`.

### `50ef2e8` — widen abutment-fill lateral feather (the walls)
**File:** `BeamNgTerrainPoc/Terrain/Export/BridgeAbutmentFiller.cs`, `FillFromAbutment`.
**Problem:** the fill scaled its **longitudinal** taper with height but kept a **fixed 4 m lateral feather** → a
5 m fill got a ~51° striped side wall.
**Fix:** `effectiveLatFalloff = Clamp(fillHeight / DefaultMaxFillSlope, requested, DefaultMaxFillLengthMeters)`, used
in place of the fixed `lateralFalloffMeters` in the lateral loop → a 5 m fill grades its sides to ~1:2.5 (~12.5 m).
A 0 feather still disables the band.
**Test:** `BridgeAbutmentFillerTests.TallFill_LateralFeather_WidensWithHeight_NotAVerticalWall`.
**Scope caveat:** only softens the fill within **≤30 m of each abutment**. The *longer* ramp embankment is the
**general road→terrain blend** (`SmoothingMaskExtensionMeters`, `UnifiedTerrainBlender`), untouched — see §5.

### `c36a222` — doc 26 (open-issue backlog). `927cab0` — diagnostics only (see §3, §6).

---

## 3. Measured results (winningen regens)

| | `200023` (pre) | `205620` (ramp gov.) | `212453` (stale binary¹) |
|---|---|---|---|
| Raised-span grades | 350–845 % | **6 %** | 6 % |
| Max seam kink | 82.5° | 46.0° | 45.5° |
| Curves | 12c/3p/1ch | 15c/1p | **19c/0p** |
| Long viaduct (158/360) | 845 %, kink 60°, bulge 4.2 | 25.5 %, kink 10°, bulge 0.04 | 6 %, bulge 2.5 |

¹ `212453` was generated **without rebuilding the app**, so the hump (`cfde286`) + wall (`50ef2e8`) fixes are NOT
in it (spline 21 still `bulge=3.28m`; per-abutment `cellsRaised` didn't rise). **Their effect is unverified in-game
— confirm on the first rebuilt regen.**

---

## 4. DECIDED, NOT YET IMPLEMENTED — the priority-dip rule (do this first)

**User decision (2026-07-01):** the merged planner's absolute *"always raise, never dip, regardless of priority"*
(doc 20 §5d) is **wrong**. The correct rule:

- **A strictly LOWER-priority under-road is DIPPED** under the deck.
- **An equal-or-higher-priority road is never dipped — the deck raises to clear it.**

Priority is road class — **known reliably pre-smoothing** (unlike the approach-elevation gate §5c/§5d tried and
dropped). Semantics: **higher priority number = more important** (log `prio 3000/10001`; the resolver's veto is
`LowerPriority > UpperPriority`).

**Where the old absolute rule lives (to change):** `BridgeElevationPlanner.PlanSpan`
(`BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs`) currently emits **only** `RaiseBridge`/
`AlreadyClears`. Change the per-obstacle loop (the block after the `AlreadyClears` early-out) to:

```csharp
var raiseDeck = ob.Crossing.LowerPriority >= ob.Crossing.UpperPriority; // equal-or-higher → raise
if (raiseDeck)
{
    var target = ob.Z + c;
    spanPinZ = MathF.Max(spanPinZ, target);
    spanCrossings.Add(new CrossingPlan { Crossing = ob.Crossing, Action = RaiseBridge,
        DeckTargetZ = target, ObstacleZ = ob.Z, NaturalDeckZ = ob.NaturalDeckZ, Deficit = deficit });
}
else // strictly lower priority → dip the road under the (un-raised-for-it) deck
{
    spanCrossings.Add(new CrossingPlan { Crossing = ob.Crossing, Action = DipLowerRoad,
        LowerRoadTargetZ = ob.NaturalDeckZ - c, DipDepthMeters = deficit,
        ObstacleZ = ob.Z, NaturalDeckZ = ob.NaturalDeckZ, Deficit = deficit });
}
```

`isRaised = anyRaise (RaiseBridge only) || globalRaise` stays correct (a dip-only span isn't raised).
**Downstream already handles it:** `GradeSeparationResolver.ApplyLowerRoadDips` reads the plan and dips
`DipLowerRoad`/`Split` crossings against the **final stamped deck Z** (its eased well, junction-clamped — not the
old ugly gouge). Nothing else needs wiring; the `DipLowerRoad`/`Split` enum + resolver dip path already exist
(kept for legacy mode).

**Tests to update** (they encode the OLD §5d absolute rule and WILL fail — that is expected):
- `BridgeElevationPlannerTests.BothApproachesRise_LowerPriorityRoad_RaisesBridge_NeverDips` → rename/assert
  **`Action == DipLowerRoad`** (under priority 50 < corridor 8002).
- `BridgeElevationPlannerTests.DescendingSpan_LowerPriority_StillRaises_NeverDips` → assert **`DipLowerRoad`**.
- These stay RAISE (equal/higher, unchanged): `…_EqualPriority_…`, `…_HigherPriorityRoad_…`, `RiverBridge_…`,
  `DescendingSpan_EqualPriority_…`, `Rule1_Ramp_…` (under default prio 8002 == corridor 8002 → raise).

> **Naming nit:** the diagnostic field `SpanDeckPlan.RaiseAloneRule1` / `[BRIDGE-PLAN] raiseAloneRule1=` now means
> "any crossing raised the deck" — consider renaming when you touch it.

---

## 5. Open issues (from doc 26, still open)

- **3a — long viaduct deck sags below a crossing** (`spline=360/361 minClear=−7.0/−7.3m`, `sag-capped`). The
  clearance metric iterates `plan.Crossings`, so it's a **recorded** crossing whose lower road's **final**
  (post-smoothing) Z ended up **above** the pinned deck (planner pins off the **pre-smoothing** DEM). The new
  `[BRIDGE-CLEAR]` diagnostic (§6) names the binding crossing — decide if it's a real clip-through (raise more /
  re-pin post-smoothing) or a false alarm (the "lower" road is genuinely above → exclude by the layer tie-break).
- **3b — the 3 "dipped" roads** may now be **correct** under the priority rule (§4) if they are lower-priority.
  The new `[GRADE-SEP] DIP … prio X/Y` diagnostic confirms. Only a dip of an *equal/higher*-priority road is a bug.
- **3c — long embankment walls beyond the ≤30 m abutment zone** — the general road→terrain blend
  (`SmoothingMaskExtensionMeters`, `UnifiedTerrainBlender`), affects every road. Broader change, do only if the
  regen shows walls persist past the abutment after `50ef2e8`.
- **3d — spline 21 residual seam** — after the tangent clamp the *bulge* is gone but a real grade kink remains
  (29 % road vs 6 % deck edge). Complete fix = ease the **approach road** grade to the deck near the abutment even
  when the span isn't raised (extend the ramp governor). Low priority.

---

## 6. Diagnostics added (`927cab0`) — read these in the next regen

- `[GRADE-SEP] DIP upper=<id> lower=<id> prio U/L upperDeck=<bool> upperZ= lowerZ= clear= dip=` — one per dipped
  road. Confirms each dip is a legit lower-priority road-vs-deck (or road-vs-road), not an equal/higher road.
- `[BRIDGE-CLEAR] spline=<id> span=<id> binding lower=<id> lowerZ= deckZ= prio U/L lowerLayer= clear=` — logged
  when a span's `minClear < 5 m`; names the crossing behind the tight/negative clearance (issue 3a).

Both are **file-only, no behavior change**. (A never-dip-under-deck *guard* was tried in this commit's WIP and
**reverted** — it contradicted the §4 decision; do not reintroduce it.)

---

## 7. Build / test

```
dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true
dotnet test  BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true
```
Current: **508 green** on this branch. After §4, expect the 2 named tests to need updating to `DipLowerRoad`.

---

## 8. Standing constraints (unchanged, honor on the new branch)

- `MergeStructuresIntoCorridor` stays **ON** (plan-view continuity R1 — doc 23). `merge=false` fixes symptoms but
  loses that, by user decision.
- The **flat deck** (§5b–§5d pins) is sacrosanct — these fixes shape the **approach** and the **terrain**, never
  the deck pins. §4 changes *who moves at a crossing*, not the deck shape.
- Grade/slope targets are **code constants** (6 % deck/ramp grade, `DefaultMaxFillSlope` for the fill) per the
  user's "calculated by code, not a user parameter" directive.
- **No grade clamp** on the deck (warn, don't clamp) — the standing no-grade-clamp feedback.
