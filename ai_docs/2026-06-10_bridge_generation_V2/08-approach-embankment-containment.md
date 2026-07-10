# Doc 08 — Containing bridge elevation influence on surrounding roads ("Damm" problem)

**Date:** 2026-07-02 · **Status:** §5 diagnostics D1–D3 IMPLEMENTED (`2a7ce63`); regen pair analyzed
+ mechanism attributed (§7); C3 takes 1 (pre-smooth pins, `5fd27a8`) and 2 (post-solve flatten+carve,
`b92bc7a`, reverted `afbd983`) **both FAILED on render — see §7b/§7c lessons**; **C3 take 3
IMPLEMENTED** (`a7791ba`): IN-SOLVER decayed affine correction at bridge-raised junctions (§7c),
707/707 green — awaiting render verification. C1 (raise sanity gate for the 30–64 % track spans)
and C2 (junction-raise budget) still open, in that order.
**Branch context:** `feature/bridge_coherent_underpass` @ `bc0fa16` (PR #128, based on
`feature/bridge_merged_corridor`). Read this alone — self-contained handoff for a new session.

---

## 0. TL;DR

With bridge generation ON, **normal roads around bridges are lifted far beyond the bridge itself**
and render as if built on embankments/dams ("die Straßen sehen aus, als ob sie auf einem Damm gebaut
wären"). The bridge's own deck raise is legitimate; the damage is the **propagation**: junction
raises transplant deck-end elevations onto side-road networks (up to **+7.5 m** per junction in the
winningen comparison run), the box filter spreads span-wide soft rises into the approaches, chain
smoothing carries raised junction elevations along contributor roads with no decay budget, and the
wide lateral terrain blend turns every raised road into a broad dam. Goal for the next session:
**keep the elevation range of normal (non-bridge) roads as close to the DEM as possible** — contain
raises to the bridge corridor + short, class-slope-bounded transitions.

Reference case from the user: OSM way **23464911** — a `motorway_link` (A61 exit toward
"Aussichtspunkt Moseltal", one-way, 11 nodes), i.e. a RAMP near the big Winningen A61 viaduct
(splines 360/361, spans 26269667/26269664), NOT itself a bridge. With bridges on it reads as built
on a dam. NOTE: the way id does not appear in the logs — way→spline mapping is not logged (gap, see
§5 D1).

## 1. Evidence — winningen log comparison

Logs (same map, same preset, bridge generation toggled):
`C:\Users\alexander.kleinwaech\AppData\Local\BeamNG\BeamNG.drive\current\levels\winningen\log_comparison\`
- `Log_TerrainGen_with bridges.txt` (19:00 run — NOTE: predates the `71c7e04` tongue-cap/raster
  fixes; regen before re-judging the raster patches, but the RAISE mechanics below are unchanged)
- `Log_TerrainGen_without bridges.txt` (18:52 run — contains **zero** `[BRIDGE-*]`/`[GRADE-SEP]`
  lines; 754 junctions vs 733 with bridges, since grade-separated crossings replace false junctions)

With bridges, mode `sparse-soft` (`spans=19 raised=9 softPinnedSections=2597 junctionRaises=9
dipPinnedSections=821`):

**(a) Junction raises up to +7.5 m** (`RaiseJunctionsAlongApproachRamps`, doc 05 §4.2) — junctions
inside a raised span's approach-ramp run are re-pinned UP to the ramp line; the junction blender
then pulls **all contributor roads** toward the new elevation:

```
junction-raise junction=30  (TJunction)        spline=15  d=14,5m z=161,47->167,32 (deckEnd=167,32)  +5,85
junction-raise junction=250 (TJunction)        spline=15  d=15,6m z=157,61->163,42 (deckEnd=165,00)  +5,81
junction-raise junction=42  (TJunction)        spline=21  d=23,1m z=146,58->152,78 (deckEnd=158,20)  +6,20
junction-raise junction=697 (MidSplineCrossing) spline=21 d=7,0m  z=159,05->166,58 (deckEnd=166,62)  +7,53
junction-raise junction=168 (TJunction)        spline=129 d=7,4m  z=32,15->37,60  (deckEnd=37,84)    +5,45
```

**(b) Deck ends demand absurd approach grades** (`[BRIDGE-PROFILE] apply`) — the deck's solved ends
sit metres above the natural approaches; every connected road must climb to them:

```
spline=8  L=41m  g0=62,3% g1=-39,0% seamKink=23,6/29,6deg
spline=15 L=50m  g0=30,5% g1=-51,5% arch=2,92m
spline=21 L=58m  g0=64,4% g1=-14,6%
spline=78 L=108m g0=53,2% g1=-44,2%
```
30–64 % "ramps" cannot exist as roads — they materialize as short cliffs at the abutment plus
LONG lifted runs on the connected roads (the dam), because the smoother distributes the climb.

**(c) Span-wide soft rises** — `softPinnedSections=2597`: every raised span carries one uniform
`SoftDeckRiseMeters` on all its sections, deliberately sized to survive the ~150 m box filter and
"pull BOTH approaches up" (Amendment 03 v3). Effective: the raise bleeds ~a filter window beyond
each abutment on the bridge's own road — which is often exactly a ramp like way 23464911.

**(d) Terrain fill/stamps around raised roads** — every lifted section stamps its heightmap
footprint across `road width + TerrainAffectedRangeMeters` with a smoothstep flank; a +6 m road at
6 m affected range is a ~20 m-wide dam. Plus `[BRIDGE-OVERLAP] cellsRaised=6138 maxLift=9,99m`
(tongues; since `71c7e04` capped at `AbutmentOverlapMaxLiftMeters` = 2 m and priority-owned).

**(e) No decay budget on side roads** — once a junction is raised+pinned, contributor chains are
solved toward it by the generic smoother; nothing bounds how far the lift carries along a side road
before returning to the DEM.

## 2. Mechanism inventory (code hook points)

| # | Mechanism | Where | Effect radius |
|---|---|---|---|
| M1 | Junction raise to ramp line | `UnifiedRoadSmoother.RaiseJunctionsAlongApproachRamps` | all contributor roads of the junction, unbounded via chains |
| M2 | Soft deck rise, span-wide uniform | `BridgeElevationPlanner.BuildUniformSoftPins` → `SoftDeckRiseMeters`, consumed in `ApplySoftShapingToRaw` | ~box-filter window (~150 m) past each abutment |
| M3 | Deck-end targets themselves (the raise demand) | `BridgeElevationPlanner.PlanSpan` (Rule 1 / veto / split), `BridgeProfileSolver.RefineSpans` | sets the elevation the network must reach |
| M4 | Post-solve uniform span raise + embankment fill | `GradeSeparationResolver.ApplyApproachRaiseRamps` (`[BRIDGE-RAMP]`; 0 in this run) | approach runs + heightmap fill |
| M5 | Lateral terrain blend of lifted sections | Phase-4 stamping, `TerrainAffectedRangeMeters` | ~road width + 2× affected range |
| M6 | Abutment overlap tongue | `BridgeAbutmentOverlapStamper` — capped/priority-guarded since `71c7e04` | local (3 m ends) |
| M7 | Chain smoothing spreading pinned junction Z | `OptimizedElevationSmoother` / junction blender | whole chains |

## 3. Root framing

The clearance requirement is a point constraint (deck over obstacle). Today every metre of raise is
**exported to the road network** at full value: deck end → approach ramp → junction pin → all
contributors → chains. There is no *budget* that says "a normal road may deviate from its natural
(estimate/DEM) profile by at most X m, decaying at the class slope". The winningen A61 viaduct area
shows the worst case: several short link/ramp bridges (splines 8/15/21/42/78/129) each raise their
neighborhood, and the ramps between them (e.g. way 23464911) end up permanently elevated.

## 4. Candidate directions (to design in the next session — NOT decided)

- **C1 — Raise sanity gate:** a span whose required approach grade exceeds the absolute class slope
  by a large factor (g0 30–64 % vs table 5–14 %!) should not raise at all — extend the Rule-1
  infeasible→dip fallback / reduced-clearance escalation (R4 step 7) to these sparse-path cases.
  Several of the 9 raised spans look like candidates for dip/split/reduced clearance instead.
- **C2 — Junction-raise budget:** cap `RaiseJunctionsAlongApproachRamps` (e.g. max lift, or
  class-slope × distance-to-deck-end). Deficit beyond the cap: let the deck END sag (RefineSpans
  already skips near-abutment floors — "end deficits are approach territory") or take reduced
  clearance + warn. junction 697's +7.53 m at d=7 m is indefensible as a road.
- **C3 — Side-road decay pins:** after raising a junction, pin an eased class-slope descent back to
  the natural profile on every NON-bridge contributor (the exact mirror of the doc-28 merged-well
  `LowerJunctionsInWell` / `UnderpassWellProfile` machinery, direction up instead of down). This
  bounds the dam length on ramps/side roads deterministically.
- **C4 — Soft-rise taper:** `BuildUniformSoftPins` gives the full lift to every span section; add a
  taper before the abutments (or transport the rise only over the span interior) so the box filter
  bleeds less into the approaches. Watch doc 04/05 history: the uniform form was chosen because
  per-crossing humps diluted — the taper must keep mid-span clearance.
- **C5 — Narrower dam flanks:** for sections lifted > X m above the estimate, stamp embankment
  flanks at `SideSlopeRunPerRise` (B1-style) instead of the wide `TerrainAffectedRangeMeters`
  smoothstep, or scale the affected range down with lift height. Purely visual width of the dam.
- **C6 — Prefer dips more broadly:** doc 28's coherent underpass already suppresses raises for
  outranked cluster roads; consider extending priority distribution so single crossings under
  link-class bridges also dip/split rather than raise (`EnablePriorityDistribution` currently
  splits, but the winningen run still raised 9 spans).

Suggested order: **D1/D2 diagnostics first** (§5), then C1+C2 (kill the demand), then C3 (bound the
propagation), then C5 (visual width). C4 is the riskiest (history of renders #5/#10).

## 5. Diagnostics to build FIRST (the current logs cannot quantify the problem)

**ALL THREE IMPLEMENTED** on `feature/bridge_embankment_containment` @ `2a7ce63` (2026-07-02):

- **D1 — way→spline mapping in the log:** ✅ `[WAY-MAP]` — one line per spline at the end of
  `UnifiedRoadNetworkBuilder.BuildNetwork` (spline id, OSM type, priority, length, structure-seg
  count, sorted way ids). File-only; grep the way id to find its spline.
- **D2 — per-spline deviation report:** ✅ `[DAM-REPORT]` — `RoadElevationDeviationReport.Emit`,
  called in `TerrainCreator` right after the post-solve resolver passes (the last elevation
  writers), in BOTH bridge and no-bridge runs. Per spline (ranked by worst deviation, ≥0.5 m gets a
  line, capped at 150 lines with the drop count stated): signed `maxDev` + station, `meanAbs`,
  length > 1 m / > 3 m, nearest span id (+`(raised)`) with distance, way ids; totals line at the end.
  The A0 estimate is now built once per run in `SmoothAllRoads` (bridges on or off) and stashed on
  `UnifiedRoadNetwork.EarlyElevationEstimate`; the bridge planner reuses it (values unchanged).
  Deck sections (`IsExcluded`/`StructureSpanId`) are skipped — the report measures leakage onto
  NORMAL roads only.
- **D3 — junction-raise summary:** ✅ the `[BRIDGE-PLAN] junction-raise` line now ends with
  `contributors=N[splineId:pPriority,…]` (descending priority).

**Next session:** regen winningen twice (§6) with the rebuilt HOST app, then rank/design C1+C2 from
the two `[DAM-REPORT]` blocks; way 23464911's spline is found via `[WAY-MAP]`.

## 6. Repro / verification recipe

1. Regen winningen twice (bridge generation on/off), 4096 preset — logs land in
   `…\winningen\MT_TerrainGeneration\logs\`. IMPORTANT: rebuild the HOST app (`BeamNG_LevelCleanUp`)
   first; the "with" comparison log predates the `71c7e04` fixes.
2. Compare the D2 report (once built) per spline; way 23464911's spline must show a near-zero
   deviation delta between runs when the containment works.
3. Visual checks: the A61 viewpoint exit ramp (way 23464911) and the side roads at junctions
   30/250/42/697/168 (splines 15/21/129) — currently the worst raises.
4. Guard rails: 697/697 tests green on the branch; doc 28's coherent underpass (road 164) must stay
   intact; `BridgePriorityDistributionTests`, `BridgeSparseFloorConstraintTests`,
   `BridgeJunctionRoomWideningTests` cover the machinery being touched.

## 7. Regen results 2026-07-02 (20:39 with / 20:41 without, D1–D3 live) — mechanism ATTRIBUTED

Fresh log pair in `…\winningen\log_comparison\` (both post-`2a7ce63`, host rebuilt). Totals:

| run | splines >1m | splines >3m | len >1m | len >3m |
|---|---|---|---|---|
| without bridges | 212 | 68 | 37 702 m | 6 321 m |
| with bridges | 215 | **87** | 38 589 m | **7 872 m** |

Bridge damage ≈ **+19 splines / +1.55 km of road deviating > 3 m**. The baseline deviation is
general smoothing on steep Mosel terrain — the *delta* is the dam problem.

**Ranked dam diff (|maxDev| with − without), top of the list — ONE span dominates:**

```
281 service       +14,05  (14,55 vs <0,5)  len>3m +308m  span 296027401(raised) d=63m
282 service       +13,42                   len>3m  +41m  span 296027401
283 service       +13,27                   len>3m  +42m  span 296027401
284 service       +13,21                   len>3m +103m  span 296027401
364 motorway_link +11,15  (12,85 vs 1,70)  len>3m +354m  span 296027401 d=24m
 42 track          +9,22                                 span 29070228(raised)
  8 track          +8,84  (11,27 vs 2,43)                span 29284913(raised)
 78 track          +7,29  (deck own road)                span 296027401
129 track          +5,78                                 span 100020223(raised)
367 motorway_link  +4,66  ( 6,34 vs 1,68)  len>3m +253m  span 296027401
362 motorway_link  +1,08  ( 3,17 vs −2,09) len>3m   +6m  span 296027401  ← way 23464911
```

The user-reported ramp (way 23464911 → **spline 362** via `[WAY-MAP]`) is real but MILD (+3.2 m);
the perceived dam at the viewpoint exit is the whole 296027401 neighborhood.

**Attribution of the worst cluster (296027401):** span on **track spline 78 bridging OVER the A61
carriageway (spline 361)** at (2153, 2653); deck z0=147.65, demanded approach grades g0=53.2 %,
g1=−44.2 %. T-junction **#533** at (2166, 2633) — ~23 m from the crossing, ON/AT the deck — joins
service loop 281 (start=end node 254114890) and motorway_link 364; its final Z ≈ 148 = deck level,
but **no `junction-raise` line exists for it**: the M1 pass only walks junctions on the approach-ramp
run OUTSIDE the span (`d < −0.01 → skip`), so an on-deck junction is invisible to it. The junction
inherits deck Z through the ordinary junction blender (M7) once the soft rise (M2) puts the corridor
sections there — and every contributor is then solved to ~148 with **zero decay budget**: 281 sits
at deck level around its ENTIRE 308 m loop (meanAbs 13.77), 364 carries it 354 m.

**Design consequence — the doc-§4 order is REVISED by the data:**

1. **C3 first** (side-road decay containment) — it is the only candidate that bounds the observed
   worst cluster, because the raise there does NOT come from M1 (so a C2 cap never fires) and the
   deck raise itself is legitimate (a real overpass must clear the A61). Scope: (a) junctions ON a
   raised span (or its ramp run) that M1 skips get pinned to the deck/ramp line explicitly; (b) every
   non-corridor contributor of a deck-anchored junction gets an eased class-slope descent pinned back
   to the natural (A0) profile — the upward mirror of `ApplyLowerRoadDipPins`' well machinery.
2. **C1** for the four track spans demanding 30–64 % grades (8, 15, 21, 78) — but note the lower
   road is often the HIGHER-priority motorway, so the dip fallback is frequently forbidden; the
   realistic escalation is reduced clearance / no-raise+warn.
3. **C2** stays useful for the M1-raised junctions (697's +7.53 m at d=7 m) but is no longer the
   headline fix.

## 7b. C3 take 1 render FAILURE (2026-07-02 21:05 regen) — the lesson

The first C3 cut (`5fd27a8`) pinned eased decay wells PRE-smooth on side roads at
`A0 estimate + delta·w` (hard `PinnedElevation`, BFS cascade, ramps ≤300 m). Render: **WORSE** —
massive sharp-walled embankments (user screenshot), totals `len>3m` 7 872 → **11 096 m**, spline 83
(track) −21.2 m, 364 (motorway_link) **+24.5 m** (was +12.9).

**Root cause:** on steep Mosel terrain the solver's LEGITIMATE profile sits up to ±8 m off the A0
estimate (the no-bridge baseline showed tracks at −8.6 m). Hard pre-smooth pins anchored to
estimate-based absolute Z inject that mismatch as forced steps; the box filter + affine correction
oscillate reconciling them (overshoot above AND below). **Never hard-pin side roads to
estimate-based absolute Z before smoothing** — the doc-04/05 render-history warning (#5/#10)
applies to any pre-smooth absolute-Z shaping of roads whose profile the solver owns.

**Take 2 (`b92bc7a`), per user direction ("ease in/out within a common-sense distance, only roads
connected to the bridge"):** POST-solve, lowering-only `GradeSeparationResolver.FlattenSideRoadDams`
(runs after `ApplyLowerRoadDips`): seeds at junctions with a corridor contributor ≤150 m from a
raised span; each side road keeps its junction Z (no seam) and is lowered along the eased well
shape back to `estimate + 1 m floor` within ≤150 m (class slope where shorter); junctions on a
lowered run cascade with the retained lift as cap; drops carved into the stamped heightmap via the
owner-guarded `ApplyWell`. Base = actual solved profile (zero estimate-error feedback), nothing
runs after it that could fight back. Pre-smooth remains ONLY `PinOnDeckJunctions` (explicit deck-Z
for on-deck junctions — identical to blender inheritance, but logged + §4.4-protected).

## 7c. C3 take 2 render FAILURE (21:29 regen) + take 3 — the ROOT mechanism, fixed in the solver

**Take 2 failed on render** despite better totals (`len>3m` 7 348 m): the post-solve lowering ran
AFTER Phase-4 stamping, and the owner-guarded heightmap carve cannot touch pixels owned by
NEIGHBOURING roads — lowered roads left vertical sheared cliffs against adjacent roads (user
screenshot, service area). Lesson: **post-correction after stamping is the wrong layer, full stop**
(doc 06 already recorded that Phase-4 stamps early); the road profile must be right BEFORE
stamping. Reverted in `afbd983`.

**Full docs review + code trace (3 agents) then identified the exact propagation machine, and it is
neither the blender nor the chains — it is the affine junction leveling:**

- `BuildEndpointTargetLookup` (Phase 2, every iteration) + `RetargetTerminatingRoadsToSettledThrough`
  (§3 post-loop, up to 8 passes) re-derive each junction's Z from the THROUGH road's solved
  elevation (`ThroughRoadJunctionElevation.Compute`) — at a bridgehead that is the corridor's
  raised Z — and affine-target every TERMINATING contributor endpoint to it.
- `AffineJunctionLeveler.Apply` then spreads the endpoint error over the road's **entire length by
  design** (its doc: grade ≈ error/length, "no embankment ramp forms" — true for cm errors). For a
  +14 m bridgehead error this IS the dam: service loop 281 (both ends at the same junction) ⇒
  constant +14 over the whole loop; link 364 (one end) ⇒ linear +12.85 over the full 405 m. Both
  match the §7 dam report exactly. `IsPinned` never protects here — the target is re-derived from
  the corridor each pass. (The `ApplyEndpointAnchoring` exponential-decay anchoring is dead code.)

**Take 3 (`a7791ba`) — the surgical in-solver fix:** decay the affine correction at bridge-raised
junctions only. `UnifiedRoadNetwork.BridgeRaisedJunctions` stashes the M1-raised + on-deck-pinned
set (`ApplyBridgeDeckPins`, sparse only); both affine sites flag those junctions' terminating
endpoints; `AffineJunctionLeveler.Apply` takes optional per-end decay lengths — that end's
correction follows the eased `(1−u)²(1+2u)` weight (1 at the junction with zero slope, 0 in value
AND slope at the run end) with run = clamp(|e|/classSlope, 60, 300) m, fired only when |e| ≥ 1.5 m.
Ordinary junctions and both-null calls are byte-identical legacy.

Why this can't repeat §7b/§7c: it runs INSIDE the solve (Phase-4 stamps matching terrain — no
post-carve, no cliffs); it anchors only to solver values (junction target vs solved endpoint — the
A0 estimate never enters); it adds no pins (nothing for the filter/affine to fight — it IS the
affine); curvature of the road body is preserved outside the decay run. The side road climbs to the
bridgehead over a class-slope ramp (a real, wanted embankment near the bridge) and keeps its own
solved profile beyond — "ease in, ease out, within a common-sense distance, only on
bridge-connected roads."

Verification: regen → `[DAM-REPORT]` should show 281–284/364/367 with short `len>3m` runs near the
junction only; grep `[BRIDGE-PLAN] affine-decay` for the applied runs. NOTE `PinOnDeckJunctions`
stays (it feeds the raised set + dip protection); `FlattenSideRoadDams` is gone.

**VERIFIED by 22:02 regen (log `…220254_Info.txt`):** decay fired on exactly the dam roads
(281 both ends, 364/367/15/84; runs 60–178 m, shrinking per pass as the solve converges). Totals
`len>3m` **7 004 m** (was 7 872 pre-containment, 11 096 take 1; no-bridge baseline 6 321) — the
bridge-caused excess dropped from +1 551 m to **+683 m** with no destroyed roads. Per spline:
281 meanAbs 13.77→**3.49**, `len>3m` 308→108 m (max still +13.3 AT the junction — the legitimate
bridgehead climb); 364 meanAbs 6.95→**2.73**, `len>3m` 354→99 m; 282 +13.9→+2.2 (`len>3m` 0);
367 253→59 m; way 23464911 (spline 362) −1.98/`len>3m` 0 — **fully contained**. The remaining
excess is concentrated at the C1 candidates (track bridges 8/42: +11.3/+9.7 near their own raised
spans) — the next lever is killing those demands, not more containment. (Track 24's −8.6 exists in
the no-bridge baseline too — general smoothing, not bridge damage.)

## 8. Related docs

- Doc 05 (this folder) — pre-smooth junction room widening: introduced M1 (junction raises) and the
  junction-lowering mirror the C3 idea builds on.
- Doc 03/04 (this folder) — sparse floor constraints + clearance catch-up: why soft rises are
  uniform (C4's constraint) and why end deficits are "approach territory" (C2's lever).
- `ai_docs/2026-06-03_bridge_generation/28-coherent-underpass-resolution.md` — the coherent
  underpass (dip-side twin of this problem) incl. §8 render-debug history; `UnderpassWellProfile`
  is the reusable engineered-profile + class-slope-ramp helper for C3.
- Deferred review findings on PR #128 (stale accepted residual, uncapped fallback dips, sparse-mode
  coupling) — adjacent, do not mix into this work.
