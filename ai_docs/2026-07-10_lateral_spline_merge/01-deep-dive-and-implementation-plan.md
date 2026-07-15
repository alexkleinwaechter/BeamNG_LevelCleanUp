# Lateral Spline Merge — Deep Dive and Implementation Plan

**Date:** 2026-07-10
**Branch:** `feature/combine_lanes_to_spline`
**Status:** Phases 1-3 IMPLEMENTED on this branch (2026-07-10): `MergeSplinesLaterally` on
`DecalRoadLayerSet` + checkbox, flag threading through both orchestrators into
`ConvertLinesToSplines`, `LateralCarriagewayMerger` (pair detection, centerline averaging, lane/tag
synthesis, structure-span union with original-endpoint anchors, residual tails), 11 unit tests.
Full suite green (863). Phase 4 (Winningen validation) pending — needs a real regeneration run.
Note vs. plan: the shorter path's structure spans are anchored via their ORIGINAL way endpoint
coords, so the existing V2 0.3a reprojection restores exact stations downstream; a no-lanes-tag
partner contributes OSM's oneway default of 1 lane (the "0 lanes" fallback is unreachable for real
candidates since `oneway=yes` implies ≥1 lane in `OsmLaneInfo`).

## 1. Problem

OSM models dual carriageways (motorways, many trunks/primaries) as **two separate oneway ways**, one per
direction. Our pipeline turns each direction into its own `RoadSpline`. Each spline gets its elevation
profile sampled, smoothed and bridge-solved **independently**, so the two parallel carriageways drift
apart vertically. At bridges this becomes a visible multi-meter step between the twin decks (see the
Winningen screenshot: the two A61 decks meet the abutment at clearly different heights).

**Concrete example (Winningen run, log `Log_TerrainGen_4096_20260710_121507_Info.txt`):**

- Way `132678377` → spline **366**, way `1448505388` → spline **365**. Both are the **A61**
  (`highway=motorway`, `oneway=yes`, `ref=A 61`, `lanes=2` each — verified against live OSM data).
- Both splines are ~5.88 km long with 3 bridge segments each; the big viaduct is physically **one
  bridge**: 365 span `26269667` at stations [1961.8, 2922.0] m, 366 span `26269664` at
  [2943.2, 3902.1] m (366 runs the opposite direction, so stations are mirrored).
- Solved deck profiles (`[BRIDGE-PROFILE] apply`, log lines 18934/18937):
  - 365: z0=128.66 → z1=141.76, grades −4.8 % / +0.8 %, sag-cap f=0.12, minClear=4.1 m
  - 366: z0=137.40 → z1=123.57, grades **−16.2 % / +32.5 %**, sag-cap f=0.02 (≈ pure chord), minClear=0.4 m
- Pairing the physical bridge ends: **5.09 m step** at one abutment, **4.36 m** at the other, with
  different curve shapes in between (mild cubic vs. near-chord).
- `[DAM-REPORT]`: 365 was lifted **+5.78 m** above its natural profile exactly at the viaduct start;
  366 has no entry at all (stayed within 0.5 m of natural) — one deck got raised by its own solve, the
  twin didn't.
- Knock-on: the doc-19 **pier planner placed 0/16 piers** on the 960 m viaduct — 13/16 bays were
  rejected with "footprint validation hit lower deck (span 26269664)" because the twin deck is treated
  purely as an obstacle.
- Where coupling *does* exist it works: the two 8 m crossover spans at junction 693 land on each
  other's decks via `[BRIDGE-B2B]` and finish with zGap=0.00.

## 2. Deep dive: how the code works today

### 2.1 OSM → RoadSpline pipeline

| Stage | Where | Data out |
|---|---|---|
| A. Fetch + parse | `OsmGeoJsonParser.cs` (~100-105, 179-183, 284-287) | `OsmQueryResult` (`OsmFeature` list + route relations). **All tags survive verbatim** (`oneway`, `lanes`, `ref`, …); way node IDs kept in `OsmFeature.NodeIds` |
| B. Per-material orchestration | `TerrainGenerationOrchestrator.ProcessOsmRoadMaterialAsync` (828-973) | calls C/D, stores result in `roadParams.PreBuiltSplines` (941), rasterizes layer map with uniform material width (961) |
| C. Roundabout wrapper | `OsmGeometryProcessor.ConvertLinesToSplinesWithRoundabouts` (1351) | rings handled, rest → D |
| D. **ConvertLinesToSplines** | `OsmGeometryProcessor.cs:714` | see below |
| E. Network build | `UnifiedRoadNetworkBuilder.BuildNetwork` (51) | `ParameterizedRoadSpline` + layer-set resolution (128-140) + `RoadWidthProfile` (344-408) + cross-sections |

Inside stage D:

1. **Step 1 (739-829):** each way → cropped/deduped point list → `PathWithMetadata` carrying `Points`,
   `StartNodeId`/`EndNodeId`, `OsmWayId`, full tag copy, `LaneSegments` (from
   `OsmLaneInfo.TryParse` — `oneway`, `lanes`, `lanes:forward/backward`, `width`, `est_width`),
   `StructureSegments` for bridge/tunnel ways.
2. **Steps 3-4 (884-904): longitudinal chaining** (details in 2.2).
3. **Steps 5/6 (906-1018):** Chaikin corner-cut ×2 → `new RoadSpline(...)` with metadata copied
   (`OsmRoadType`, `OsmWayIds = AllWayIds`, `IsBridge`, …); lane segments and structure segments
   re-anchored from point index to arc distance (`PropagatePathLaneSegmentsToSpline` 1122,
   `PropagatePathStructureSegmentsToSpline` 1154).

### 2.2 Longitudinal joining (the existing "merge splines" step)

Two tiers, both **end-to-end only**, both on `List<PathWithMetadata>`:

- **Tier 0 — `RouteRelationAssembler` (51):** stitches consecutive members of a `type=route` relation,
  but only when they **share an OSM node ID** at the connecting endpoints. Has an explicit
  **oneway U-turn guard** (`IsBlockedOnewayUturn`, 380, deflection >120°) precisely because a route
  relation contains *both* carriageways of a dual carriageway and blind stitching would hairpin through
  the carriageway tip node.
- **Tiers 1-3 — `NodeBasedPathConnector.Connect` (74):** greedy angle-first endpoint matching,
  partitioned by exact highway type; merge requires shared node ID or ≤1 m proximity with a cropped
  end; guards block reversal merges of oneways (363-366), layer mismatches (375), junction throats
  (423), oneway U-turns >120° (435-444).

**What survives a merge:** geometry concatenated; `Tags`/`OsmWayId` = base path's only
(`PathWithMetadata.cs:45-49`); `AllWayIds` = union; `LaneSegments`/`StructureSegments` merged
direction-aware via `LaneSegmentOps`/`StructureSegmentOps`.

**Key insight:** the oneway guards deliberately keep the two carriageways apart, so after chaining each
direction is typically **one long clean antiparallel chain** — exactly the input a lateral pairing step
wants. Every `[MERGE-BLOCK] oneway U-turn` rejection is literally a carriageway tip node (where the
directions split/rejoin) — natural trim points for a merged centerline.

### 2.3 Why parallel splines drift in elevation

Per-spline elevation is decided in `UnifiedRoadSmoother.SmoothAllRoads`:

1. Network build → per-spline cross-sections (~0.5 m).
2. Junction detection + pinning (246, 263).
3. **Phase 1.85 bridge planning** (`ApplyBridgeDeckPins` 1237): `EarlyRoadElevationEstimator` A0
   estimate; `BridgeElevationPlanner.Plan` allocates clearance budgets **per spline**
   (`SoftDeckRiseMeters`, junction raises, dip wells).
4. **Phase 2 chain smoothing** (`CalculateNetworkElevations` 2275): chains are formed by
   `NetworkElevationGraph` from **endpoint** adjacency only; each chain samples terrain at its **own
   centerline**, low-passes, applies pins, slope-clamps; then per-spline affine junction leveling
   (2409-2418).
5. Phase 3 junction harmonization — acts **only at junctions**.
6. **`BridgeProfileSolver.RefineSpans`** (from `TerrainCreator.cs:389`): each span fitted with a cubic
   anchored at *its own spline's* neighbour sections just outside the span (580-634), sag-capped
   toward the chord.

**All existing cross-spline coupling is end-point based** (chain concatenation, junctions,
deck-landings via `BridgeToBridgeContinuity`); none couples two parallel non-intersecting decks — in
fact `BridgeToBridgeContinuity.OnDeckMarginMeters = 1.0` is deliberately tuned so *"parallel twin decks
(~10 m apart) must not suppress each other's true shore abutments"*. Mid-spline crossing junctions
(`NetworkJunctionDetector.cs:644`) are skipped for pairs already connected anywhere (757-764) — 365/366
are connected at junction 693, so they get zero mid-span coupling. **Parallel drift is the expected
output of the architecture, not a bug in one pass.**

### 2.4 Width: source and consumers

Per-spline width comes from `UnifiedRoadNetworkBuilder.BuildWidthProfile` (344-408) with precedence
OSM `width=` (if `UseOsmWidthTag`) → `est_width=` → `TotalLanes × DefaultLaneWidth` → layer-set default
(2 × 3.5 m). Consumers: smoothing corridors, `RoadMaskBuilder`, banking (edge Δh = halfWidth·sin(bank)),
junction sizing (`NetworkJunctionDetector.cs:569`), `MaterialPainter`, DecalRoad generation, bridge deck
mesh (`CrossSectionConverter` → `BridgeDeckDaeExporter`), pier planner. **No hard max width exists
anywhere** — failure modes at ~2× width are visual/structural, not exceptions (see §6).

## 3. Decision: merge to one spline vs. couple two splines

### Option A — merge the pair into ONE wider spline (recommended; this feature)

Every observed drift symptom disappears **by construction**: one terrain sample line, one chain
low-pass, one planner budget (union of both crossing sets), one `RefineSpans` cubic, one pair of
abutment Zs. Also fixes the pier deadlock (no twin deck to collide with) and halves spline count on
motorways. Costs: lose independent horizontal alignments (only matters where carriageways genuinely
diverge — braids, split grades), crossovers between the carriageways need re-homing as junctions on the
merged spline, and several width consumers need review (§6).

### Option B — keep two splines, couple their elevations

Would require building from scratch: a span-pairing detector (projected station overlap +
antiparallel heading + lateral distance), a shared vertical profile imposed on both spans
(planner-level shared budget + shared abutment anchor as a third anchor source in
`BridgeProfileSolver.ApplyToSpan` 602-681), approach re-reconciliation on both splines, and solve-order
integration (`OrderSpansByLandingDependencies` 1924). Fixes only bridges — the carriageways still
drift on embankments, and the pier obstacle problem remains.

**Verdict:** Option A. Option B is the fallback if Option A's horizontal-alignment loss proves
unacceptable at complex interchanges.

## 4. Design

### 4.1 Insertion point

Inside `OsmGeometryProcessor.ConvertLinesToSplines`, **after** `NodeBasedPathConnector.Connect`
(Step 4, ~line 904) and **before** Chaikin/`RoadSpline` creation (Step 5). Rationale:

- After chaining, each carriageway is one long path per direction (oneway guards keep them separate) —
  clean pairing input. Before chaining they are dozens of fragments split at different chainages.
- `PathWithMetadata` still has everything needed: `LaneSegments` (per-arc oneway + lane counts),
  `AllWayIds` (→ ref/name lookup against `lineFeatures`, still in scope), node IDs, structure segments.
- The merged path then flows through **unchanged** Chaikin + spline creation + lane/structure
  propagation — no downstream API changes.
- After `RoadSpline` creation would be too late: Chaikin applied, control points effectively immutable,
  segments already arc-anchored per spline.
- Before the connector would be wrong: a merged bidirectional path would dodge the oneway U-turn guard
  and change longitudinal chaining behavior.

New class suggestion: `BeamNgTerrainPoc\Terrain\Osm\Processing\LateralCarriagewayMerger.cs` with
`List<PathWithMetadata> Merge(List<PathWithMetadata> connectedPaths, ...)`.

### 4.2 Pair detection

Candidate pair (P1, P2) qualifies when **all** hold:

1. Both are oneway over the overlapping range (`LaneSegments` / `IsOnewayAtEndpoint` machinery).
2. Same highway partition (guaranteed by the connector's grouping — but do NOT pair
   `motorway` with `motorway_link`).
3. **Antiparallel**: sampled tangents dot < −0.85 (reuse the 30 m `GetDirectionPoint` lookback).
4. **Laterally close**: median centerline separation within a configurable window
   (default ~4–30 m), sampled by projecting the shorter path onto the longer
   (polyline-level equivalent of `RoadSpline.GetClosestDistanceTo`).
5. **High mutual overlap**: e.g. ≥70 % of the shorter path projects inside the longer one.
6. Tie-breakers / confidence boosters (not hard requirements): shared route relation
   (`BuildWayRelationMap` pattern — both A61 carriageways share the relation), matching `ref`/`name`
   via `AllWayIds` → original feature tags.

Partial overlap: merge only the overlapping station range; split the non-overlapping remainders back
out as their own paths (v1 may simply skip pairs with low overlap instead of splitting).

### 4.3 Merged path construction

- **Centerline:** resample P2 onto P1 by projection, average the paired points; trim ends at the last
  mutually-projectable stations (the U-turn throat nodes where carriageways rejoin mark these
  naturally).
- **Lane info:** synthesize a single `OsmLaneInfo`: `IsOneWay=false`,
  `LanesForward` = lanes of the with-direction side, `LanesBackward` = lanes of the other side,
  `TotalLanes` = sum. Width: if both sides carry OSM `width=`, sum them (+ median gap knob);
  otherwise let `BuildWidthProfile` derive `TotalLanes × DefaultLaneWidth`.
- **Tags:** copy base tags but **remove/override `oneway`** (stale `oneway=yes` would leak via
  `RoadSpline.OsmTags` to any raw-tag reader, e.g. AI road derivation).
- **`AllWayIds`:** union of both sides (keeps `[WAY-MAP]` diagnostics and ref lookup working).
- **Node IDs:** set `Start/EndNodeId = null` (system already tolerates this for cropped paths;
  junction detection is geometric).
- **StructureSegments:** re-project both sides' spans onto the merged centerline using the existing
  `OriginalStartPoint/EndPoint` reprojection machinery (OsmGeometryProcessor.cs:1154-1208), then union
  overlapping spans. **v1 safe mode:** if reprojection looks risky, take the longer side's spans and
  reproject only those.
- **Logging:** `[LATERAL-MERGE] paired path A (ways=…) + path B (ways=…) sep=12.3m overlap=94% → merged
  (lanes 2+2)` — same style as `[MERGE-BLOCK]`/`[WAY-MAP]`.

### 4.4 Setting and plumbing

- Property: `public bool MergeSplinesLaterally { get; set; } = false;` on
  `BeamNgTerrainPoc\Terrain\Models\DecalRoad\DecalRoadLayerSet.cs` (next to `UseOsmWidthTag`).
  Default false ⇒ old presets/AppData JSON deserialize safely; pipeline is byte-identical when off
  (matches the `disableSplineMerging` convention). The already-drafted checkbox in
  `DecalRoadLayerSetEditor.razor:48-52` binds it directly; no code-behind change needed.
  Suggested label/tooltip: "Merge dual carriageways into one road (combines parallel oneway
  ways — e.g. motorway directions — into a single wider spline; fixes elevation drift between
  twin bridge decks)".
- Per-OSM-type is the right granularity (dual carriageways are a motorway/trunk/primary phenomenon),
  and `DecalRoadLayerSet` already carries geometry-shaping knobs (`DefaultLaneWidth`,
  `SmoothingCorridorMargin`, `UseOsmWidthTag`), so the checkbox location is fine.
- **Gap to bridge:** layer sets are currently not available inside
  `ProcessOsmRoadMaterialAsync`/`OsmGeometryProcessor`. The orchestrator already loads AppData defaults
  (`TerrainGenerationOrchestrator.cs:1037`) and has `state.DecalRoadSettings`; resolve the flag per
  way's `highway` tag with `DecalRoadLayerSetResolver.Resolve` and pass a
  `Func<string, bool> shouldMergeLaterally` (or a resolved set) into `ConvertLinesToSplines`, following
  the existing `excludeBridges`/`disableSplineMerging` argument precedent (896-901).
- **Twin path:** `TerrainAnalysisOrchestrator.cs:390` builds `PreBuiltSplines` the same way — thread
  the flag there too or analysis and generation diverge.
- **Both carriageways must resolve the same flag value:** guaranteed when both ways share one `highway`
  value; evaluate the flag on the *pair*, requiring both sides' resolved layer sets to have it on.

## 5. What the merge buys downstream (for free)

- `DirectionDivider` (double yellow) renders at the correct direction boundary
  (`position = −1 + 2·LanesBackward/TotalLanes`, `DecalRoadGenerator.cs:839`) once `TotalLanes ≥ 3` and
  `IsOneWay=false` — merged 2+2 motorway gets a proper divider automatically.
- AI road derivation (`DeriveAIRoadProperties`, 519-530, 1142-1145) produces one two-way AI road
  (lanesLeft/lanesRight from `LanesBackward/Forward`) instead of two oneway roads.
- Pier planner no longer sees a twin deck obstacle → piers actually get placed on twin viaducts.
- Terrain smoothing corridor, junction zones and material paint act on one coherent corridor.

## 6. Risks / follow-ups (width ≈ doubles)

| Area | Issue | Severity / action |
|---|---|---|
| Initial layer-map raster | `RasterizeSplinesToLayerMap` uses uniform `mat.RoadSurfaceWidthMeters` (orchestrator 961), not the width profile — merged roads paint too narrow until `MaterialPainter` repaints from `WidthProfile` | Low (painting is corrected later); optionally honor per-spline width in the raster |
| Bridge decks | One monolithic ~15-28 m slab instead of twin decks (`CrossSectionConverter` → `BridgeDeckDaeExporter` spans full corridor) | Acceptable v1; cosmetic twin-deck split is a future knob |
| Piers | Cap width = deckWidth − 2·inset, unbounded above; twin-column threshold 10 m ⇒ very wide caps get 2 columns at ±0.3·width | Works, tuned for ≤14 m — review `PierTwinColumnThresholdMeters`/max cap for >20 m decks |
| Banking | Edge Δh = halfWidth·sin(bank) doubles at same bank angle (real dual carriageways bank per-carriageway) | Consider reduced bank angle for merged splines |
| Junctions | Junction radii scale with corridor width — ramp (`motorway_link`) junctions onto merged carriageway grow ~2× | Watch in test renders |
| Crossovers | Physical crossover ways between the carriageways (junction 693 style) become short stubs ending mid-road of the merged spline | Geometric junction detection should absorb them; verify, else drop stubs fully inside the merged corridor |
| Braids/splits | Where carriageways genuinely diverge (independent grades around a hill, split alignments) averaging is wrong | Overlap/separation thresholds keep such sections unmerged; ends split back out |
| Median | No median layer type exists — merged road is continuous asphalt with double-yellow divider | Acceptable v1; later: median as Custom layer or new `DecalRoadLayerType` |
| Lane-count UI ceiling | `DefaultLaneCount`/`LanesLeft/Right` max = 8 — a merged 2×4 motorway hits exactly 8 | Bump max if needed |
| `BridgeTunnelSurface` texture | `road_asphalt_2lane` stretched across a doubled deck | Consider width-aware material choice later |
| Snapshot regen | "Re-generate DecalRoads" reloads baked geometry — toggling the checkbox needs **full terrain regen** to take effect | Document in tooltip |
| Structure spans | Twin bridges have independent arc anchors; reprojection onto merged centerline must be exact or clearances shift | Use existing reprojection machinery + tests |

## 7. Implementation phases

1. **Phase 1 — Model + plumbing (no behavior change):** `MergeSplinesLaterally` on
   `DecalRoadLayerSet`, checkbox (already drafted), resolver call in orchestrator, flag threaded into
   `ConvertLinesToSplines` (+ analysis orchestrator). Off ⇒ byte-identical output.
2. **Phase 2 — `LateralCarriagewayMerger`:** pair detection + centerline averaging + lane-info
   synthesis + tag/way-id handling, **skipping pairs with structure segments**. Unit tests: synthetic
   antiparallel oneway pairs (full overlap, partial overlap, near-parallel non-pair, ref mismatch,
   U-turn tip trimming, reversed-direction lane mapping).
3. **Phase 3 — Structure segment reprojection:** merge spans from both sides onto the merged
   centerline (reuse V2 reprojection), union overlapping spans. Tests against the A61 viaduct pattern
   (mirrored stations).
4. **Phase 4 — Winningen validation:** regenerate, verify ways 132678377/1448505388 land in ONE
   spline in `[WAY-MAP]`, single deck profile in `[BRIDGE-PROFILE]`, piers placed, no step at
   abutments; check junction 693 crossover behavior and ramp junctions.
5. **Phase 5 — polish knobs (optional):** median gap width, per-carriageway banking cap, twin-column
   pier tuning, raster width fix.

## 8. Open questions

1. Median gap: add configurable `LateralMergeMedianGapMeters` added to summed width (OSM pair
   centerlines are lane centers, so the raw lateral separation already implies the median — measured
   separation could set the width instead of pure lane math)?
2. Should partial-overlap pairs split-and-merge (correct) or skip (safe) in v1? → Recommend skip.
3. Merge `motorway_link` dual ramps too? → v1: no (links partition separately anyway).
4. Should detection ALSO require same `ref` when present, to avoid pairing adjacent but unrelated
   parallel roads (frontage roads)? → Recommend yes when both sides have `ref`.
