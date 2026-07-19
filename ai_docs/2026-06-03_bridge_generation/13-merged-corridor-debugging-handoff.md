# Handoff — Debugging the Merged-Corridor Bridge Refactor

**Date:** 2026-06-08
**Branch:** `feature/bridges`
**State:** Phases 1–6 of the merged-corridor refactor are implemented, committed, 438 tests green, default flipped **ON**.
First in-game render looked good for plan-view continuity, but the user reports **several severe bugs** to be
described next session. This doc primes the debugger with the architecture, the **prime suspects** (consumers
that were NOT converted to per-span and misfire on merged corridors), and how to isolate merged-vs-legacy.

Copy everything below the line into a fresh session, then paste the bug descriptions where marked.

---

## PROMPT

You are debugging the "merged-corridor bridge" refactor on branch `feature/bridges` in
`d:\Source\beamng_mapping_pro` (project `BeamNgTerrainPoc`, .NET 9, build with `-p:EnableWindowsTargeting=true`).
**Read first:** `ai_docs/2026-06-03_bridge_generation/11-merged-corridor-bridge-continuity-plan.md` (the plan,
§2 architecture, §6 phase status) and skim this doc. Memory: `merged_corridor_bridge_plan`.

### What the refactor does (one paragraph)
Bridges used to be held out of spline-merging and rebuilt as isolated splines (a separate curve from the road →
plan-view kink). The refactor merges a bridge INTO its through-road corridor like any other way, remembers the
bridge as an arc-length sub-range (`StructureSegment`, the twin of `LaneSegment`), smooths the whole corridor as
one road (continuity by construction), excludes ONLY the bridge sub-range from terrain, and builds one deck per
span from the merged, smoothed sub-range. Gated by `MergeStructuresIntoCorridor` (now default **true**; the UI
checkbox "Merge Bridges Into Corridor" toggles it; flag-off = byte-identical legacy separated-spline behaviour).

### Pipeline (merged mode)
```
OSM ways → [merge: bridges included, layer anti-merge guard] → corridor splines, each carrying StructureSegment[]
  → cross-section gen → [UnifiedRoadSmoother.MarkStructureExclusions tags span sections: IsExcluded + StructureSpanId]
  → elevation smoothing/chaining over the WHOLE corridor
  → [BridgeProfileSolver.ApplyToSpan: per span, fit G0+G1 curve to in-spline neighbours, capture BridgeSpanSnapshot]
  → terrain stamp/paint (skips IsExcluded span sections) ; grade-sep dip ; excavator
  → deck export (1 deck/span from snapshot) ; DecalRoads (OverObjects on span nodes)
```

### Commits (this refactor, on `feature/bridges`)
`9184e8c` P1 data model · `1fa0840` P2 flag+layer guard · `4f2b995` P3 exclusion+StructureSpanId ·
`6a51bf1` P4 solver-to-spans+snapshot · `c7ab2fe` P5 consumers · `4b6923b` P6 default-on ·
`2bc593f` docs(F2)+perf. To A/B test, set `MergeStructuresIntoCorridor` false (UI checkbox or
`TerrainGenerationState.MergeStructuresIntoCorridor` default) and regenerate — that's the exact legacy behaviour.

---

### ⚠ PRIME SUSPECTS — whole-spline `IsBridge` consumers that were NOT converted (most likely root causes)

**The core hazard:** on a merged corridor, the whole-spline `spline.IsBridge` / `spline.IsTunnel` flag is just the
value of the **merge-base way** (`pm.IsBridge` from `OsmGeometryProcessor`), so it's effectively **random** — a
road→bridge→road corridor may have `IsBridge=true` or `false`. Phase 5 converted the deck/excavator/grade-sep/
material-painter/corridor-builder/DecalRoad/harmonizer/TerrainCreator consumers to key off the per-section
`StructureSpanId` instead. But these **active** consumers still branch on whole-spline `IsBridge` and were
**missed** — audit each against merged corridors first:

1. **`NetworkJunctionDetector.cs:845-846,859-860`** — grade-separated upper/lower is chosen by
   `splineA.IsBridge != splineB.IsBridge`. On merged corridors `IsBridge` is unreliable, so a bridge-over-road
   crossing can be **mis-detected as an at-grade MidSplineCrossing** (no grade separation → road doesn't dip,
   bridge doesn't clear). Evidence: the franco_same_prio render logged only **1** grade-separated crossing vs
   **20** mid-spline crossings — strongly suspect several real bridge-over-road crossings were missed here.
   **Fix direction:** decide upper/lower from whether the crossing XY falls inside a bridge **span**
   (nearest cross-section `StructureSpanId >= 0` and/or `Layer`), not whole-spline `IsBridge`. (Mirror the
   `GradeSeparationResolver.IsGeneratedDeckAt` helper added in P5.)

2. **`NetworkElevationGraph.cs:213` (+ consumers :376,387)** — `ElevationEdge.IsBridge = spline.IsBridge` is set
   whole-spline; chain building treats bridge edges specially (`chain.Segments.Any(s => s.Edge.IsBridge)`,
   `_edges.Where(e => e.IsBridge || e.IsTunnel)`). A merged corridor edge flagged `IsBridge` could make the
   smoother mishandle the WHOLE corridor's elevation chain (this is the active elevation path —
   `UnifiedRoadSmoother.cs:1200` builds this graph). **Top suspect for elevation/profile bugs.** The bridge
   profile is now solved per-span by `BridgeProfileSolver.ApplyToSpan` AFTER chaining, so the edge-level
   `IsBridge` special-casing may be wrong/redundant in merged mode — verify what it does and whether a merged
   corridor edge should ever be `IsBridge`.

3. **`OsmGeometryProcessor.RasterizeSplinesToLayerMap:529-538`** — builds the material **layer-map mask** and
   skips the **whole spline** when `spline.IsBridge && excludeBridges`. On a merged corridor this either paints
   the material mask under the bridge (IsBridge=false) or drops the whole corridor from the mask (IsBridge=true).
   This is a SEPARATE path from `MaterialPainter` (which P5 did convert). Convert to per-sample span skip (reuse
   the `MaterialPainter.GetExcludableSpanRanges` idea), or confirm whether this mask is still authoritative.

4. **`NetworkJunctionHarmonizer.cs:232-233`** — P5 guarded this with `!MergeStructuresIntoCorridor`; re-verify
   it actually neutralises correctly (a merged corridor endpoint must NOT be excluded from harmonisation).

5. **`StructureElevationCalculator.cs` (399,862,1236,1245,1297,1302)** — heavy whole-spline `IsBridge`/`IsTunnel`
   logic, BUT a grep for `new StructureElevationCalculator` / `StructureElevationCalculator.` finds **no
   call-site** in `BeamNgTerrainPoc` — it appears to be **dead/legacy** (superseded by `UnifiedRoadSmoother`).
   Confirm it's unused before spending time here; if used, it's a major suspect.

### Other known issue classes (verify against the reported bugs)

- **Chaikin arc-length approximation (P3/P4).** `StructureSegment.StartDistance/EndDistance` are summed from the
  **pre-Chaikin** path points (`OsmGeometryProcessor.PropagatePathStructureSegmentsToSpline`), but cross-sections
  are sampled from the **post-Chaikin** spline. So the marked span (and thus the deck + the terrain hole) can be
  **shifted/resized by a few % of the distance-to-the-bridge** relative to the true OSM bridge. The deck and the
  hole stay consistent with each other (both key off `StructureSpanId`), but both can be off from where the
  bridge "should" be. This matches the same approximation `LaneSegment` already uses. If a bug is "deck/hole in
  the wrong place" or "wrong length", this is the cause — fix by re-resolving the span distances against the
  final spline arc-length (project the bridge way's endpoint node positions onto the spline), not pre-Chaikin sums.
- **Adjacent / closely-spaced spans.** In `BridgeProfileSolver.ApplyToSpan`, the approach anchors are the
  in-spline neighbours just outside the span (`roadBefore[^1]` / `roadAfter[0]`). If two bridges are separated by
  a tiny connector, an anchor (or its grade window) can fall on the OTHER span's deck sections → wrong endpoint
  Z/grade. Check corridors with multiple spans (franco: splines 221, 338, 341 had 2–3 spans each).
- **Layer anti-merge guard (P2).** `NodeBasedPathConnector` refuses a merge across different `Layer` unless a
  shared OSM node exists. If real bridge↔approach pairs DON'T share a node (cropped, or OSM tags layer oddly),
  the bridge won't merge → it stays a separate spline with a whole-span `StructureSegment` (handled as an
  isolated span). Check for bridges that failed to merge when they should have.
- **Deck identity collision.** Deck file = `bridge_{SpanId}.dae`, `SpanId = StructureSegment.SpanId` =
  `hash(sorted OSM way-ids) & 0x7FFFFFFF`. Two distinct spans colliding → one `.dae` overwrites the other → a
  missing deck. Unlikely but cheap to rule out (grep the deck dir for fewer files than spans marked).

### Diagnostics (in the generation log `…/MT_TerrainGeneration/logs/Log_TerrainGen_*_Info.txt`)
- `Marked bridge span <SpanId> on spline <id> [start,end]m as excluded (<N> cross-sections)` — every span, its
  arc-range, and section count (DETAIL level — enable detail logging).
- `[BRIDGE-PROFILE] apply spline=<id> OVERRIDE=yes curve=… L=…m z0/z1 g0/g1 bulge seamKink minClear …` — per span
  solve. `seamKink` high or `minClear` very negative ⇒ profile/clearance problems.
- `[BRIDGE-EXCAVATE] bridges=… cellsLowered=… maxCut=…m` — terrain shaving under decks.
- `GradeSeparatedCrossing: upper spline … over lower spline …` and `… recorded N grade-separated crossing(s)` —
  **compare N against how many bridges actually fly over roads** (suspect #1).
- `Bridge deck export: <N> deck(s) written … 0 skipped` — deck count should equal spans marked.

### Commands
```
dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj -p:EnableWindowsTargeting=true
dotnet test  BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -p:EnableWindowsTargeting=true
# focused: --filter "FullyQualifiedName~Bridge|FullyQualifiedName~Structure|FullyQualifiedName~Osm|FullyQualifiedName~Merge"
```

### The structural fix that retires the whole bug class
The root cause of suspects #1–#3 is that **whole-spline `IsBridge` is meaningless on a merged corridor**. Plan
§4.1/§7 already calls for making `RoadSpline.IsBridge`/`IsTunnel` **derived from `StructureSegments`** (and
auditing every consumer) during the retirement phase. If the reported bugs cluster on whole-spline `IsBridge`
misuse, doing that derivation + a full `IsBridge`/`ShouldGenerateDeck` consumer audit is likely the real fix —
not per-bug patches. Keep flag-off (legacy) working until validated.

---

### >>> PASTE THE REPORTED BUGS HERE <<<
(For each: what's wrong visually/behaviourally, which map, and a screenshot/log excerpt if possible. Then we
A/B against legacy `MergeStructuresIntoCorridor=false` to confirm it's merged-specific, locate it among the
suspects above, write a failing test, fix, re-render.)
