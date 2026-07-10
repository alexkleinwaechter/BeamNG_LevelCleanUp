# Handoff — Implement the Bridge Elevation Rule Engine & Rising Ramps

**Date:** 2026-06-08 · **Branch:** `feature/bridges` · **Memory:** `merged_corridor_bridge_plan`

Copy everything below the line into a fresh session to start implementation. The plan is written, adversarially
reviewed, and A/B-validated; this primes the implementer with the decisions already locked and the gotchas the
review surfaced.

---

## PROMPT

You are implementing a bridge-elevation fix in `d:\Source\beamng_mapping_pro` (project `BeamNgTerrainPoc`, .NET 9).
Build: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true`.
Test: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true`
(focused: `--filter "FullyQualifiedName~Bridge|~Structure|~GradeSep|~Merge"`).

**READ FIRST (in order):** `ai_docs/2026-06-03_bridge_generation/14-bridge-elevation-rule-engine-and-rising-ramps-plan.md`
(THE PLAN — §1a A/B results, §4 rule engine, §4a/§5/§6/§7 mechanism, §8 consumer audit, §11 phases, §13 open
decisions, §17 review log). Then skim `11-merged-corridor-bridge-continuity-plan.md` (the merged-corridor
refactor this builds on) and the memory `merged_corridor_bridge_plan`.

### The bug (one paragraph)
After the merged-corridor refactor (bridges are now interior arc-ranges of a through-road corridor, default ON),
an interchange flyover (corridor spline 394, prio 8002, span [129.9,414.6]m) and its ramps render **flattened to
~2.2 m** instead of an elevated viaduct with rising ramps, and terrain poking above the buried deck is carved into
a **ditch** (the ditch is purely downstream of the low deck). Root cause: whole-spline `IsBridge`/`Layer` are
meaningless on a merged corridor, so (1) grade-separation detection misses every bridge-over-road crossing
(`recorded 0`), and (2) nothing elevates the deck — `BridgeProfileSolver.ApplyToSpan` runs after smoothing and
anchors the span to its terrain-following ramp neighbours.

### A/B already done — the decisive facts (plan §1a)
Legacy render (`MergeStructuresIntoCorridor=false`, log `Log_TerrainGen_4096_20260608_224716`) vs merged
(`..._211502`):
- **Grade separation fires via `Layer`, not `IsBridge`.** Legacy: `upper 417 (layer 1) over lower 419/420/422
  (layer 0)`, 5 crossings, 5 under-roads dipped (maxDip 4.86 m). Merged: 0. **`StructureSegment.Layer` already
  carries the span's `layer=1`** (`= feature.Layer`, `OsmGeometryProcessor.cs:809`); the merged *spline*.Layer is
  the merge-base (layer 0). FIX = use **effective layer at the crossing** (span's `StructureSegment.Layer` if the
  XY is inside a span, else whole-spline `Layer`) in `TryClassifyGradeSeparation` (`NetworkJunctionDetector.cs:834`).
- **Deck is ~4 m lower in merged (6.41 vs 2.24).** NOT chain-dragging — legacy chains bridge+ramps identically
  (`Chain 3: 424→417[B]→423`). It's per-spline affine on 3 short splines vs one 522 m affine + `ApplyToBridge`
  junction lookup vs `ApplyToSpan` neighbour.
- **Even legacy buries the deck (minClear −5.7 m, carves 1305 cells).** So the target (clear the obstacle) is
  *better than legacy*; this is not a pure revert.
- **Sequencing:** the detection fix and the deck-height fix MUST ship together — detection alone would dip the
  mainlines into a deep hole under a still-flat 2.2 m deck.

### Chosen approach (locked)
Decide each span's required deck Z up front (clearance over under-roads + terrain), constrain it through the
corridor's smoothing so the existing smoother BUILDS the rising ramps (play nice with harmonization), and use a
small **rule engine** (`BridgeElevationPlanner`) to decide who moves at each crossing: Rule 1 ramp ⇒ raise the
bridge & leave under-roads; Rule 2 no-ramp ⇒ lower-priority loses (dip / veto-raise); Rule 3 equal ⇒ configurable
50/50 split. Robust **span-footprint** spatial query replaces the unreliable mid-spline-crossing test for
"what's under the bridge," and under-deck roads become `GradeSeparatedCrossing`s, never at-grade junctions.
Flag-gated behind `MergeStructuresIntoCorridor`; flag-off stays byte-identical; NO dead code.

### NON-NEGOTIABLE gotchas the review found (plan §17 — get these wrong and it silently no-ops or regresses)
1. **`StructureSpanId` is only set in Phase 2.0 (`MarkStructureExclusions`, `UnifiedRoadSmoother.cs:1138`)** —
   AFTER junction detection (Phase 1.8). You MUST hoist a `TagStructureSpans` pass to before detection (§4a), or
   derive span membership from `spline.StructureSegments` + `cs.DistanceAlongSpline` (both available at build).
   Otherwise the detector and planner see only `-1` and do nothing.
2. **The deck pin (`UnifiedCrossSection.PinnedElevation`) must be honoured by FOUR passes** or it's overwritten:
   the box low-pass (`OptimizedElevationSmoother.CalculateChainElevations:662`, hard-hold after the filter),
   the re-smooth iterations (`ReSmoothChainFromExistingElevations:723`, every iteration), the affine leveler
   (`AffineJunctionLeveler.Apply` + post-loop `RetargetTerminatingRoadsToSettledThrough`, exempt pinned sections
   with a blended boundary), and `EnforceMaxSlopeConstraint:709` (exempt pinned neighbourhood — never clamp the
   ramp; honour the no-grade-clamp memory `feedback_no_grade_clamp`).
3. **The box filter at a span edge is a symmetric blur, not a clean ramp** — hard-hold the pin on span sections;
   the profile solver re-curves; the rising ramp is only ~±75 m wide (steep for big rises — D7).
4. **Clearance first-cut must use the STAMPED under-road Z where available** (road smoothing can fill above raw
   terrain); guarantee `C` with the post-smoothing `ApplyLowerRoadDips` against final Z.
5. **The debug image is exported INSIDE the smoother (`UnifiedRoadSmoother.cs:452`) where `network.BridgeSpans`
   is EMPTY** — source the span overlay from `spline.StructureSegments`, not `BridgeSpans`.

### Phases (each builds + tests green; flag-off byte-identical) — plan §11
- **Phase A** — `TagStructureSpans` hoisted before detection; `BridgeSpanFootprint` query; **effective-layer**
  grade-sep in `TryClassifyGradeSeparation`; suppress at-grade junctions under a deck. Tests: 394-like corridor
  over a layer-0 road ⇒ `GradeSeparatedCrossing`, no at-grade junction; road beside ⇒ normal junction; flag-off
  unchanged. (No elevation change yet; do NOT ship alone — see sequencing.)
- **Phase B** — `BridgeElevationPlanner` (pure, unit-tested): inputs §4.1, rules §4.2, span-vs-span tie-break,
  flag/empty-span early-return.
- **Phase C** — `PinnedElevation` honoured by all four passes (§7 / gotcha 2); planner pins span deck Z (first
  cut §6). Tests: pinned plateau ⇒ monotone rising ramps + held deck; pin survives 3 iterations; short-corridor
  affine does not tilt the deck; slope-clamp-on preset does not flatten; non-bridge byte-identical.
- **Phase D** — demote `BridgeProfileSolver.ApplyStructuralProfiles` → `RefineSpans` (smooth + snapshot only);
  `ApplyLowerRoadDips` consumes planner outcomes vs final stamped Z; delete superseded `PlanConstraints` decision
  code. End-to-end: elevated deck, rising ramps, `minClear ≥ 0`, excavator `maxCut` small.
- **Phase E** — debug image overlay from `StructureSegments` + grade-sep markers; UI params (§10) round-tripped.
- **Phase F (after in-game sign-off only)** — derive `IsBridge`/`IsTunnel` from `StructureSegments`; delete
  `ApplyToBridge` + legacy `ApplyStructuralProfiles` branch + `FindConnectedRoadContributor` + dispose
  `DiagnoseSeams`; convert/confirm `OsmGeometryProcessor.RasterizeSplinesToLayerMap` (unguarded whole-spline
  `IsBridge`, suspect #3) + `DecalRoadGenerator.IsGeneratedBridge`; delete dead `StructureElevationCalculator`.

### Open decisions to confirm with the user before Phase C
- **D1** (recommended yes): ramp test driven by the **clearance requirement** (not measured slope), avg-slope only
  a secondary confirmation on raw terrain.
- **D6**: affine-vs-pin boundary — exempt pinned sections and blend the affine correction to zero over a short
  margin so no step at the abutment.
- **D7**: accept the ~75 m box-filter ramp for v1 or drive a longer configurable approach grade.

### Start
Begin with **Phase A** (isolated, fully testable). Write the failing test first, implement, keep flag-off
byte-identical, run the focused filter. Then B, then confirm D1/D6 with the user before C. Update the memory
`merged_corridor_bridge_plan` as phases land.
