# Doc 28 — Coherent underpass resolution (handoff + design)

**Date:** 2026-07-01 · **Branch:** `feature/bridge_merged_corridor` (tip `bfa55e0`)
**Status:** IMPLEMENTED + VERIFIED IN-GAME 2026-07-02 on `feature/bridge_coherent_underpass` (§8,
PR #128) — user confirmed the winningen 164 underpass renders acceptably after §8.2's fixes.
Read this alone — self-contained.

---

## 0. TL;DR

Roads that pass under a **cluster of bridges** (a motorway interchange) get **fragmented,
conflicting per-crossing elevation decisions** → an undrivable washboard/staircase road. The fix
is a **coherent underpass** pass: group grade-separated crossings **by the lower road**, and when a
road passes under a cluster, dip it as **one smooth bounded underpass** clearing all bridges at
once (suppressing the individual bridge raises), instead of independent per-crossing wells that
fight each other.

Two smaller fixes already SHIPPED on this branch and remain (do not revert):
- **Conservative infeasible-dip** (merge `bfa55e0`): a Rule-1 span dips a strictly-lower-priority
  under-road only when its raise is infeasible. Gated on `EnableRampFeasibility`.
- **M2 observability trace**: dipped spans emit `[BRIDGE-PLAN] WARN … dipped lower-priority road …`.

One larger fix was tried and **REVERTED**: the "always dip lower-priority" aggressive rule
(reflog `fb95892`/`431e85a`). It flattened the motorway but introduced the 164 staircase — the very
symptom this doc's coherent pass is meant to solve properly.

---

## 1. The problem (winningen evidence)

Road **164** (prio 8001) is crossed by **five bridges within ~50 m** (an interchange):

| Upper bridge (span) | Prio vs 164 | Per-crossing decision (aggressive run `215921`) |
|---|---|---|
| `360` | 10002 > 8001 | dip 164 by **6.12 m** → 138.5 |
| `361` (28536652) | 10002 > 8001 | dip 164 by **5.86 m** → 138.9 |
| `47` (28536669) | lower than 164 | **raise 47's own deck** (arch, `g0=39%`), leaves 164 |
| `42` | lower than 164 | **raise 42's own deck**, leaves 164 |
| `366` | — | (also present) |

Plus a residual local carve: `[GRADE-SEP] … upper=361 lower=164 clearance=5,01m < required=6,13m
— A7 local carve residual=1,12m (239 sections)`. Over ~50 m road 164 is yanked down to 138 under
360/361, left at ~144 under 47/42, then carved again → the washboard the user photographed
(`bridge_28536669`). **Undrivable.**

Root of the fragmentation: the planner plans **each bridge span independently** — no span knows
what its siblings decided for the shared lower road.

---

## 2. Why "trust the real bridge elevation" is impossible (investigation, 2026-07-01)

A full read of the elevation pipeline (`BridgeElevationPlanner`, `NetworkJunctionDetector`, OSM
parsing, deck-mesh export) established the constraints. **This is the key context — don't re-derive.**

1. **No real bridge height exists in the data.** OSM gives `bridge=yes` and `layer=*` (relative
   stacking only) — no absolute height / `maxheight` is read anywhere. `StructureSegment.OsmTags`
   is carried but never consulted for elevation. Deck elevation is **100% inferred** from the
   bridge's abutment/approach road elevations (DEM ground level) + a forced clearance budget.
   - `ob.Z` (lower obstacle) and the deck/approach reference both come from `SectionZ`
     (`BridgeElevationPlanner.cs:847-860`): solved `TargetElevation` (usually NaN at plan time) →
     A0 estimate (`EnableEarlyElevationEstimate`) → raw DEM. Neither consults bridge-ness.
2. **The deck mesh has NO minimum-height-above-terrain floor.** `BridgeDeckDaeExporter` uses the
   solved `TargetElevation` (`CenterZ`) verbatim. So "leave both at natural elevation"
   (`AlreadyClears`) makes the deck mesh sit **on** the road, not above it — also broken.
3. **We DO reliably know "the upper is a real bridge"** — `span.Seg` (a `StructureSegment`,
   `IsBridge == true`) is in scope everywhere in `PlanSpan`; `crossing.UpperIsBridge` is a
   redundant confirmation. But knowing it gives no *height* to place the deck at.
4. **Short spans can't arch drivably.** A 29 m OSM span gaining 6 m clearance mid-span = ~40 % hump.
   So clearance must come from raising approaches (embankment) or dipping the road — no free lunch.
5. **Deeper truth:** the DEM has road 164 at grade (~144 m) when in reality it sits in a cut under
   the motorway. With that input, no planner rule produces a clean flyover without moving something.

**Consequence:** the design does NOT try to read a real height. It makes the *dip* (the only
drivable option for these short at-grade spans) **coherent** instead of fragmented.

### Hook points (from the investigation)

- **Planner, `PlanSpan`** — the raise/dip decision and deck pins are built here, per span,
  independently. `span.Seg`, the full `obstacles` list (each wrapping `GradeSeparatedCrossing`
  with `UpperIsBridge`/`LowerIsBridge`/`LowerPriority`/`UpperPriority`/`HasLowerSpline`) are
  available. Suppressing a bridge's raise MUST happen here (raises bake into pins pre-solve).
  - `ClassifyNonRampCrossing` (`:386-398`) owns the `AlreadyClears`/raise/dip/split outcomes for
    non-ramp spans. Rule-1 ramp spans use the separate `IsDippable` path (`:214-220`).
- **`GradeSeparationResolver.ApplyLowerRoadDips`** — applies dips against the FINAL solved deck Z,
  per crossing. The smooth-envelope merge extends here (final deck Zs known post-solve).

The two-sided nature (suppress raises in the planner + merge the dip in the resolver) is why this
is a cross-cutting feature, not a one-block edit.

---

## 3. Decided design — coherent underpass

**Gate:** `EnablePriorityDistribution` (on in the winningen preset).

**Step A — Cluster grouping.** Group grade-separated crossings by `LowerSplineId`. A *cluster* =
crossings on the same lower road whose along-road stations are within a window (**≤ ~120 m gap**,
dip-ramp-length scale). 164's five crossings collapse to one cluster.

**Step B — One dip-or-raise decision per cluster** (not per crossing):
- If the lower road is **strictly lower priority than the highest-priority bridge** in the cluster
  → the road dips as the underpass; **all** bridges in the cluster (incl. lower-priority ones like
  47/42) are cleared by that dip and their individual raises are **SUPPRESSED**. Kills the
  mixed-priority fragmentation.
- Else (road outranks every bridge) → bridges raise, road stays flat (today's behavior).

**Step C — Dip profile = smooth lower envelope.** Across the cluster the road follows
`min over bridges of (deckZ_i − clearance_i)` at each station, eased back to natural grade beyond
the cluster ends → one continuous, drivable well. No staircase.

**Step D — Bound (DECIDED):**
- Max underpass dip = **~6 m** (deeper cap, so 164's ~6 m need clears fully via dip alone).
  Consider a new `MaxUnderpassDipMeters` (default 6) or reuse/raise `MaxCutDepthMeters`.
- Over-cap behavior = **cap + warn, stay coherent.** If the deepest requirement exceeds the cap,
  dip to the cap, keep the smooth underpass, and **log residual under-clearance** — do NOT raise
  the bridges for the residual (raising reintroduces embankments/fragmentation). Road stays
  drivable; some bridges may have slightly less than full clearance (acceptable, logged).

**Observability:** emit a per-cluster `[BRIDGE-PLAN]`/`[GRADE-SEP]` line naming the lower road, the
bridges cleared, the dip depth, and any capped residual — so a regen is auditable (consistent with
the M2 trace already shipped).

---

## 4. Implementation sketch (starting points, verify before coding)

1. **Aggregate crossings by lower road.** A new pass (planner-side, after obstacles are collected
   across spans, or a pre-pass over `network`'s grade-separated crossings) that builds clusters
   keyed by `LowerSplineId` + station proximity.
2. **Per-cluster decision + raise suppression.** For a dip cluster, mark every bridge span over it
   so `PlanSpan`/`ClassifyNonRampCrossing`/the Rule-1 `IsDippable` path emits **no raise** for that
   crossing (the road will clear it). Emit `DipLowerRoad` (or a new coherent-dip record) carrying
   the cluster's envelope target for the lower road.
3. **Smooth envelope in the resolver.** Extend `GradeSeparationResolver.ApplyLowerRoadDips` to,
   per lower spline, merge the cluster's dip targets into ONE well spanning first→last crossing,
   depth = envelope `min(deckZ_i − clearance_i)`, eased to natural grade beyond the ends, capped at
   the max-underpass-dip, residual logged.
4. **Tests (TDD).** Build a 3-bridge-over-one-road cluster fixture (mixed priorities) and assert:
   one coherent dip, all bridges cleared, lower-priority bridges NOT raised, dip ≤ cap, over-cap
   warns. Reuse `BridgeRampFeasibilityTests` / `BridgeSpanSolveOrderTests` helper patterns
   (`BuildScenario`, `BuildNetworkWithJunctions(a,b,c)`, `underPriority`).

Watch for: existing tests that assume per-crossing raises for clustered lower-priority bridges may
flip (expected — update to the coherent outcome, as the aggressive-dip work already did for
`BridgeSpanSolveOrderTests.PinnedDeckBelow_IsNeverDipped_EvenByDistribution`).

---

## 5. Current repo state (2026-07-01)

- Branch `feature/bridge_merged_corridor` @ `bfa55e0` — conservative infeasible-dip + M2 trace
  merged; **682/682 tests green**; **not pushed** (ahead of origin).
- Aggressive "always dip" reverted (in reflog `fb95892`/`431e85a` if the priority-dip machinery is
  wanted as a starting point — it already has `EnablePriorityDistribution` gating, `IsDippable`,
  the full-deficit dip, and the conditional Warning trace).
- Docs: spec `docs/superpowers/specs/2026-07-01-rule1-infeasible-dip-lower-priority-design.md`,
  plan `docs/superpowers/plans/2026-07-01-rule1-infeasible-dip-lower-priority.md`, handoff
  `ai_docs/2026-06-03_bridge_generation/27-branch-handoff-approach-ramp-and-dip-rule.md`.

## 6. Out of scope / deferred

- **Railway bridge tall embankments** (the first screenshot) — user deferred; separate issue.
- **Reading a real bridge deck elevation** — impossible from current data (§2); do not attempt.
- **Doc-27 approach-ramp governor / hump clamp** (`b954ad9`/`cfde286`/`50ef2e8`) — redundant with
  this branch's V2 rework; deliberately NOT ported (see doc 27 handoff analysis).

## 8. Implementation (2026-07-02, branch `feature/bridge_coherent_underpass`)

As designed in §3, gated on `EnablePriorityDistribution`. Concrete shape:

- **Step A/B (planner)** — `BridgeElevationPlanner.BuildUnderpassClusterSeeds` groups the network's
  road-under-bridge crossings by `LowerSplineId` + station window (`UnderpassClusterGapMeters`, default
  120 m); clusters need ≥ 2 crossings AND `LowerPriority < max(UpperPriority)` to dip. Member crossings
  are excluded from every deck-raise requirement (incl. the ramp/infeasibility tests) and resolved via
  `BuildCoherentDip` in BOTH the Rule-1 and non-ramp branches. Clusters surface on
  `BridgeElevationPlan.UnderpassClusters` (`UnderpassClusterPlan`).
- **Step D (cap)** — new `MaxUnderpassDipMeters` (default 6). Over-cap: dip to the cap and REDUCE the
  crossing's `RequiredSeparationMeters` by the residual (mirrors R4 step 7) so the A7 verify never carves
  the accepted shortfall back in fragments; `CrossingPlan.AcceptedResidualMeters` + a `Warning` record it.
- **Step C (merged well)** — `UnderpassWellProfile` (linear depth between crossing stations, `(1−u)²(1+2u)`
  eased end ramps). Emitted pre-smooth by `UnifiedRoadSmoother.PinUnderpassClusterWell` (dip-as-pin/sparse;
  per-crossing wells skip cluster members; falls back per-crossing when an end is boxed or — legacy mode —
  a junction sits inside the cluster; sparse re-pins interior junctions DOWN via the generalized
  `LowerJunctionsInWell`). Applied post-solve by `GradeSeparationResolver.ApplyLowerRoadDips` (active
  path: cluster members deferred and merged into one `ApplyWell` envelope against the final deck Zs).
- **Observability** — per-cluster `[BRIDGE-PLAN] coherent underpass: lower=… bridges=… maxDip=…
  [cappedResidual=…]` (smoother) and `[GRADE-SEP] coherent underpass …` (resolver active path), plus the
  per-crossing `coherent underpass:` warnings in the existing WARN trace.
- **Tests** — `BridgeCoherentUnderpassTests` (8): mixed-priority cluster dips all/raises none, plan
  surface, cap+warn+reduced separation, gap window, gate off, road-outranks-all, pin-emitter envelope
  (anti-washboard interior assertions), resolver active-path envelope. 690/690 green.

### 8.1 First winningen render (2026-07-02, log `…_190011`)

The pass fired correctly: ONE well on 164 `[245.5,278.5]m` (360/361/47, maxDip 6.00, capped residuals
0.12/0.03 accepted), a second cluster on road 47 (under 361/366, incl. a junction re-pinned down), and
bridge **42 is NOT part of the interchange** — it crosses 164 at (123.7, 560.7), ~1.3 km away (doc §1's
"five bridges" was off; 366 crosses road 47, not 164). Remaining kink under `bridge_28536669`:
the envelope targets descend across the cluster so the well vertex lands at 47's crossing (the last
point), and the flat 60 m default exit ramp recovered 6 m at ~15 % peak grade right under the deck
(plus a 0.19 m A7 carve — the sparse deck settled ~0.2 m under the plan reference). **Fix shipped:**
end ramps are now depth/class-sized (`UnderpassWellProfile.ClassRampLengthMeters`, §3.3 slope —
primary 5 % ⇒ 120 m for 6 m, room-clamped), and the interior interpolation is smoothstep-eased (zero
depth-slope at every crossing station). Single-crossing wells keep the 60 m default (out of scope).

### 8.2 Second winningen render (2026-07-02, log `…_192655`) — the real bump

The 120 m class-sized ramps applied (`ramps=120,0/120,0m`) but the bump under `bridge_28536669`
persisted. The user's screenshot showed it: a RAGGED-EDGED raised patch stamped ACROSS 164's lanes,
one deck-corridor wide — a raster terrain write, not a road-profile artifact. Root cause chain:
**(1)** sparse mode keeps each span's 3 m abutment tongue zones as ordinary (non-excluded) road, so
they enter `RoadSurfaceOwnerRaster`; **(2)** the raster was FIRST-WRITER-WINS, and spline 47 < 164 in
iteration order, so 47's tongue zone STOLE ownership of 164's lane cells at the crossing;
**(3)** `BridgeAbutmentOverlapStamper` then legally stamped deck-level terrain across the underpass
road (`[BRIDGE-OVERLAP] maxLift=9.99m`). Fixes shipped:
- **Priority-aware surface ownership** — on overlap the higher-priority spline owns the cell (164
  keeps its lanes; ties keep first-writer for junction determinism).
- **Seam-only tongue lift cap** — new `AbutmentOverlapMaxLiftMeters` (2 m): a cell needing more lift
  is not a raster seam but genuinely low ground under an elevated deck end (an underpass well near an
  abutment) — skipped, never walled up to deck level.
- **Absolute-Z engineered well interior** (hardening, same session): the well bottom is a smoothstep
  curve through the crossing targets; the estimate base (A0 smoothed DEM — noisy exactly under real
  interchanges) is sampled only AT the crossings and blended back in over the end ramps, so DEM
  artifacts can no longer reach the hard-pinned bottom.

## 7. Build / test

```
dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true
dotnet test  BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true
```
Rebuild the **host app** (`BeamNG_LevelCleanUp`), not just the lib, before an in-game regen — the
app runs a built binary. Verify on winningen: road 164 should be ONE smooth underpass under the
360/361/47/42/366 cluster, drivable, with the motorway/lower bridges flat.
