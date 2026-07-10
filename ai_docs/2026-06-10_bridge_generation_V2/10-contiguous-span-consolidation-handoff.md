# Doc 10 — Consolidate contiguous bridge spans before bridge generation (handoff prompt)

**Date:** 2026-07-06 · **Status:** IMPLEMENTED 2026-07-06 (see §7) — awaiting manhattan regen + render ·
**Branch:** `feature/bridge_embankment_containment` @ `69fdc5e` (725 tests green)
**Read this alone — self-contained.** Follow-up to doc 09 (natural-profile anchor, VALIDATED) after
render review of the deck-footprint raise guard (`69fdc5e`).

---

## 0. The prompt (user, 2026-07-06)

> We have a merge rule logic for osm splines in our code. When I take a look at manhattan bridges:
> bridge_432550784, bridge_808611547, bridge_46177679 as an example — if the splines would be merged
> before generating the bridges we would benefit with smooth transitions and less terrain terraforming.
>
> [Render:] We still get unacceptable spikes which shouldn't be there. Even on road surface, which
> shouldn't be possible because we have a parameter for that [= road-surface protection].

Render (user screenshot 18:4x): the big elevated interchange still shows massive needle-wall spike
clusters between/beside the ramps, including on road surfaces. The doc-09 anchor + deck raise guard
did NOT remove them — this doc explains why they can't, and what will.

## 1. THE FINDING — the OSM-way merge worked; span consolidation did NOT

Log `…\manhattan\MT_TerrainGeneration\logs\Log_TerrainGen_4096_20260706_184210_Info.txt`
(anchor ON — 14 `[BRIDGE-CLEAR]` lines prove it; over-max 5 547 = the known DEM-negative clamp only):

```
[WAY-MAP] spline=14 type=trunk prio=9000 len=1808m bridge structureSegs=11
         ways=46116280,46177116,46177152,298734351,432550256,432550257,808611020,808611027,…
```

The 12 OSM ways WERE merged into one spline (14). But it carries **ELEVEN structure segments**, and
their stations are **perfectly contiguous** — each span ends exactly where the next begins:

```
432550784 [   0.0,  329.5]   ← user's example 1
808611547 [ 329.5,  364.9]   ← user's example 2
46177679  [ 364.9, 1418.8]   ← user's example 3
432550783 [1418.8, 1487.0]
808611570 [1487.0, 1510.6]
808611565 [1510.6, 1575.1]
298734878 [1575.1, 1600.7]
808611564 [1600.7, 1628.3]
808611554 [1628.3, 1658.7]
46116807  [1658.7, 1690.0]
46177643  [1690.0, 1714.5]
```

One physically continuous **1714.5 m viaduct**, generated as **11 separate decks with 10 internal
fake "abutments"**. Every internal boundary gets, per side: an abutment-overlap tongue
(`BridgeAbutmentOverlapStamper`, raises terrain to deck level over corridor width), a deck end wall
(mesh), per-span `RefineSpans` re-curve (grade kinks at the seams), on-deck junction pins, and
excavator strips. Map-wide this run: `[BRIDGE-OVERLAP] spans=67 cellsRaised=23321 maxLift=2,00m`,
`[BRIDGE-EXCAVATE] bridges=67 cellsLowered=50160 maxCut=80,89m` — mid-viaduct terrain sculpted up to
deck level and cut 80 m against protection-mask holes. The 2 m `AbutmentOverlapMaxLiftMeters` cap
skips cells with a bigger gap and the owner-raster truncation leaves per-cell holes → the
raised/skipped/cut patchwork IS the needle-wall spike field in the render, including apparent
"road surface" spikes at mask boundaries.

**Consolidating the spans removes ~10 of 11 terraforming sites on this spline** (2 real abutments
instead of 22 ends). That is the user's "merge before generating" — smoother transitions AND less
terraforming, attacking the artifact at its source instead of guarding it cell by cell.

## 2. The consolidation logic EXISTS — find out why it didn't fire

`BeamNgTerrainPoc/Terrain/Models/RoadGeometry/StructureSegmentOps.cs`:

- `Consolidate(segments)` (line ~71) — "joins adjacent or overlapping spans of identical
  **type+layer** into one (so two contiguous bridge ways become a single continuous bridge span)".
  Contiguity test is on POINT INDICES: `cur.StartPointIndex <= prev.EndPointIndex + 1`.
- Called from `MergeSegments` (pairwise path merge) and `LaneSegmentOps` has the analogue.
- Seeding/propagation: `PropagatePathStructureSegmentsToSpline` (see `[BRIDGE-REPROJECT]` / V2 plan
  0.3a station re-projection, `StructureSegment.OriginalStart/EndPoint`).

**Hypotheses to root-cause (in order):**

1. **Layer mismatch.** Consolidate requires `cur.Layer == prev.Layer`. The interchange ways carry
   different OSM `layer` tags along the viaduct (span 46177679 is layer **3**; crossing detection saw
   `upper spline 14 span 46177679 (layer 3) over lower spline 1 (layer 1)` — spline 1 is itself a
   bridge: `[BRIDGE-BRIDGE] span 46177679 over bridge 1 … detection only`). If adjacent spans differ
   in layer (2 vs 3 …), they never join even though the deck is continuous.
2. **Point-index gaps.** After Chaikin/merge index remapping, adjacent ways may end up
   `StartPointIndex > prev.EndPointIndex + 1` (1-point tolerance too strict), or segments are seeded
   per-way AFTER the pairwise merges so the final whole-spline list is never `Consolidate`d again.
3. **Bridge structure-type mismatch** (`BridgeStructureType` differs per way?) — Consolidate keeps
   the first, only `??=`s later ones, but the join condition itself is only type+layer, so this is
   unlikely; verify anyway.

Add a one-line file-only diagnostic if needed (e.g. log adjacent same-type spans that FAILED to
join and which condition failed) — regen manhattan once, read, then design.

## 3. The task

1. **Root-cause** why spline 14 keeps 11 contiguous spans (hypotheses §2) — evidence first, no fixes
   before the mechanism is proven (log diagnostic + one regen is cheap).
2. **Design + implement consolidation of contiguous bridge spans for deck generation.** Expected
   shape (validate against findings): merge contiguous same-type spans ACROSS layer differences for
   the deck/terraforming machinery, while preserving crossing/grade-separation semantics (see
   cautions). Alternatively: a final whole-spline `Consolidate` pass with a station-based (not
   point-index) contiguity tolerance. Prefer the smallest change that makes spline 14 → 1 span.
3. **Regen manhattan + render**: interchange needle walls gone or drastically reduced; the three
   named bridges become ONE continuous deck with smooth internal transitions.

## 4. Cautions (why layer exists — don't break these)

- **Layer feeds grade separation.** Span layer is used for bridge-over-bridge/crossing
  classification (`GradeSeparatedCrossing`, effective-layer logic in `TagStructureSpans`, doc 14
  Phase A). Spline 14's spans cross OTHER bridges (spline 1, layers 3/1 at (1553.9, 934.3)) and
  roads at (2149.5, 291.8). If consolidation flattens layer to one value, crossing classification
  must still see the RIGHT relative layers at each crossing station. Options: keep per-station layer
  info (max? original sub-ranges?) on the merged span, or consolidate only for the terraforming/deck
  consumers. Decide with evidence.
- **Span ids are keys.** `StructureSpanId` groups cross-sections everywhere (planner spans, deck
  mesh export, `BridgeAbutmentOverlapStamper` deck groups, `BridgeDeckFootprintRaster`, snapshots,
  `[BRIDGE-*]` logs). A merged span keeps ONE id (first/lowest?) — grep consumers for assumptions.
- **Station re-projection (0.3a)** uses per-way `OriginalStart/EndPoint`; Consolidate takes the
  outermost — verify the merged span's reprojected [start,end] still spans the whole viaduct.
- **Doc-28 coherent underpass + doc-09 anchor must stay intact**: `BridgePriorityDistributionTests`,
  `BridgeSparseFloorConstraintTests`, `BridgeJunctionRoomWideningTests`, `NaturalProfileAnchorTests`,
  `BridgeDeckFootprintRasterTests` are the guard rails. 725 tests green before you start.
- **One long span changes planner economics**: 1714 m span → structural depth span/20 is CLAMPED
  (`StructuralDepthMaxMeters` 2.0) — fine; RefineSpans re-curves one long deck (grade governor
  warn-only); `[BRIDGE-CLEAR]` may re-rank. Watch, don't pre-optimize.

## 5. Context you inherit (don't re-litigate)

- **Doc 09 anchor is VALIDATED** (Manhattan over-max 115 215 → 5 547; runaway raises 6 → 0; flag
  `EnableNaturalProfileAnchor` set in the preset JSON `bridgeRules` node —
  `d:\temp\TestMappingTools\__preset_Manhattan\theTerrain2_terrainPreset.json`; NO UI checkbox).
  Post-solve raise pass is skipped under the anchor; `[BRIDGE-CLEAR]` is a read-only assertion.
  **User doctrine: nothing post-solve may write road/deck elevations; post-solve shapes bare terrain
  only.**
- **Deck raise guard `69fdc5e`** (`BridgeDeckFootprintRaster.CanRaise`, raising passes only) is in —
  necessary but insufficient for the interchange; span fragmentation floods it.
- **Open, NOT this session's scope** (park unless the render says otherwise): §9.3 bridge-over-bridge
  clearance deficits (14 `[BRIDGE-CLEAR]`, planner work, `EnableBridgeBridge` detection-only);
  doc 09 Phase 4 (`RefineSpans` fold into `SmoothAllRoads` — decided IN, not done); Phase 5 C3
  through-road anchor; 1.5 m inflation-threshold tuning; `[DAM-REPORT]` spline 157 `maxDev=+126`
  near span 890491277 — SUSPECTED A0-artifact (negative DEM near water making the estimate garbage,
  user: pre-save clamp exists for DEM minus-values), verify A0 there before believing it's a real dam.

## 6. Verification recipe

1. Rebuild `BeamNG_LevelCleanUp`, regen manhattan 4096 (preset above).
2. Log: `[WAY-MAP] spline=14 … structureSegs=` **11 → 1** (or the few justified by real gaps);
   `Marked bridge span … on spline 14` lines collapse to one `[0, 1714.5]`;
   `[BRIDGE-OVERLAP] spans=` ~67 → ~57 fewer; `maxCut` ≪ 80.89; over-max stays ≈ 5 547 (DEM only).
3. Render at the interchange (user judges): needle walls gone, bridge_432550784→46177679 one smooth
   deck, no internal bumps at former span seams, roads below drivable.
4. 725+ tests green; the §4 guard-rail suites specifically.

Log dirs: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\{MT_TerrainGeneration\logs,log_comparision}\`.

---

## 7. IMPLEMENTATION RECORD (2026-07-06, same day)

### 7.1 Root cause = hypothesis 1, proven with OSM ground truth

Spline 14 is the **Brooklyn Bridge**. Span ids are NOT way ids — `StructureSegment.SpanId` is a hash of
the way-id set, and for a single-way span that is `527 + wayId` (`17*31 + id.GetHashCode()`). Decoding
the log and querying the OSM API for each way's tags:

| Station [m] | Span (hash) | OSM way | `layer` tag |
|---|---|---|---|
| 0–329.5 | 432550784 | 432550257 | 3 |
| 329.5–364.9 | 808611547 | 808611020 | *(none → 0)* |
| 364.9–1418.8 | 46177679 | 46177152 | 3 |
| 1418.8–1487.0 | 432550783 | 432550256 | *(none → 0)* |
| 1487.0–1510.6 | 808611570 | 808611043 | 3 |
| 1510.6–1575.1 | 808611565 | 808611038 | *(none → 0)* |
| 1575.1–1600.7 | 298734878 | 298734351 | 1 |
| 1600.7–1628.3 | 808611564 | 808611037 | *(none → 0)* |
| 1628.3–1658.7 | 808611554 | 808611027 | 1 |
| 1658.7–1690.0 | 46116807 | 46116280 | *(none → 0)* |
| 1690.0–1714.5 | 46177643 | 46177116 | 1 |

Layers alternate `3,0,3,0,3,0,1,0,1,0,1` — **no two adjacent spans ever share a layer**, so
`Consolidate`'s `cur.Layer == prev.Layer` join condition joined ZERO pairs (11 bridge ways → 11 spans;
the 12th way 808611036 has no `bridge` tag = the non-bridge tail [1714.5, 1808]). Classic OSM tagging:
`layer` encodes only LOCAL crossing order and is omitted where nothing crosses. Hypotheses 2/3 ruled
out: shared boundary nodes make point indices/stations exactly contiguous, type is Bridge throughout.

### 7.2 The fix — station-based cross-layer join + per-sub-range layers

Flag **`EnableContiguousSpanConsolidation`** (`BridgeRuleSystemOptions`, preset `bridgeRules` node,
NO UI checkbox — doc 09 doctrine; added to the Manhattan preset). Off ⇒ byte-identical.

- `StructureSegmentOps.ConsolidateByStation(segments, tol=1.5m)` — final whole-spline pass joining
  adjacent same-TYPE spans by station regardless of layer. Way ids unioned (one stable merged SpanId),
  outermost `OriginalStart/EndPoint`, tags from first of run (as `Consolidate`), `Layer` = max
  (governing). Tolerance absorbs sub-metre reprojection seams; a real ground gap is never that small.
- `StructureSegment.LayerRanges` (`List<StructureLayerRange>` = [start,end,layer] per contributor,
  null unless a join happened) + `LayerAt(station)` — the original per-way layers survive for
  crossing classification.
- `NetworkJunctionDetector.EffectiveStructureAt` → `seg.LayerAt(cs.DistanceAlongSpline)`; and
  `BridgeSpanFootprint.BuildAll` emits ONE footprint PER layer sub-range (same SpanId) — the
  grade-separation passes see the SAME relative layers at every crossing station as before the join,
  while all deck/terraforming machinery (planner, RefineSpans, deck mesh, overlap stamper, excavator,
  footprint raster) sees ONE span.
- Call site: end of `OsmGeometryProcessor.PropagatePathStructureSegmentsToSpline` (stations final,
  post-reprojection), plumbed like `reprojectStructureStations` through both `ConvertLinesToSplines*`
  overloads and the three orchestrator call sites. `[SPAN-CONSOLIDATE]` diagnostic (falls back to
  `TerrainLogger` — `TerrainCreationLogger.Current` is null at spline-creation time, which is also why
  the `[BRIDGE-REPROJECT]` lines never appeared in the render log despite the flag being on).

### 7.3 Verification so far

735/735 tests green (725 + 10 new: `ContiguousSpanConsolidationTests` ×8 incl. end-to-end
processor-level flag on/off, `BridgeSpanFootprintTests.BuildAll_MergedSpanWithLayerRanges_…`,
`BridgeRuleSystemOptionsTests` AnyEnabled). Main app builds clean. **Outstanding: §6 regen + render**
(expect `[WAY-MAP] spline=14 … structureSegs=11 → 1`, `[BRIDGE-OVERLAP] spans≈57`, `maxCut ≪ 80.89`,
needle walls gone).
