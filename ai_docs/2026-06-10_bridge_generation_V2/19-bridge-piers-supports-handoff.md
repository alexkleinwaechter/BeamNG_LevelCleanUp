# Doc 19 — Bridge supports (piers) for long spans, styled by OSM structure type (handoff / design)

**Date:** 2026-07-09 · **Status:** **IMPLEMENTED (Phases 1–3)** on `feature/bridge_piers`,
2026-07-09 — Phase 1 planner+keep-out `e03f5c8`, Phase 2 ColumnPier meshes `4a3b67d`, Phase 3 UI
knobs `7554d0d`; 852 tests green, flag `EnableBridgePiers` default **off** (byte-identical).
Deviations from the design: none material — archetype selection/viaduct rhythm/trestle alias/
suspension suppression shipped in Phase 1 (planner-side, tested there); a final footprint-ring
validation backs the s-interval keep-out so skewed building/lower-deck geometry can never leak; the
`SurfaceFootprintIndex` sweep of §3b.1 was replaced by the OSM road-feature interval walk (same
robustness net, no DecalRoad-side dependency at export time). **NOT done:** Phase 4 (pylons,
`bridge:support=*` nodes, straddle piers — own doc) and the §8.2/§8.3 Manhattan log/render
validation (user regen required). Original design below, kept verbatim.
**Original branch note:** `feature/bridge_embankment_containment`.
**Read this alone — self-contained.** Picks up decision **B-4 "Piers — deferred"** from
`../2026-06-03_bridge_generation/09-bridge-deck-mesh-spec.md` and the corpus in
`../2026-06-03_bridge_generation/09a-bridge-cross-section-research-corpus.md` §6. Builds on the
deck/parapet/end-stamp solids (`975c175`, `a73106d`: parapets ≥ 0.4 m watertight; end stamps follow
the soffit).

---

## 0. The prompt (user, 2026-07-09)

> Follow-up for bridge supports if bridges are longer. Perhaps optically designed, derived by OSM
> structure type of the bridges (see Key:bridge and Key:bridge:structure on the OSM wiki).
> **Important: if the bridge crosses roads or railway or buildings we set, the support is not
> allowed to be placed exactly on the obstacle!** All the data is available as OSM layers from the
> Overpass query. It is a bigger task.

Two requirements, one hard constraint:

1. **Long decks must get supports.** Today a 300 m viaduct is a floating box — deck, parapets and
   two end abutment stamps, nothing between (`BridgeRuleSystemOptions.cs:196`: "We model no
   piers…"). Everything under the ends is doc-08 embankment/dam terrain; everything mid-span is air.
2. **Supports are styled from OSM tags** — `bridge=*` / `bridge:structure=*` pick the archetype
   (columns vs. viaduct rhythm vs. pylons vs. none).
3. **Hard constraint:** a pier footprint must **never** stand on anything the bridge crosses —
   roads, railways, or the buildings we generate. Placement must move or drop a pier, never overlap.

## 1. Why this is now cheap to start (the data already exists)

Nearly all the machinery a pier generator needs was built for other V2 features — this task is
mostly *composition*, plus one new mesh builder and one new placement algorithm:

- **Per-span geometry + tags in one object.** `BridgeSpanSnapshot`
  ([BridgeSpanSnapshot.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/BridgeSpanSnapshot.cs))
  carries stations (`Center`, `Tangent`, `Normal`, `Width`, `CenterZ`, `LeftEdgeZ`, `RightEdgeZ`,
  `DistanceAlongSpline`) **plus the raw per-span OSM tag bag** (`OsmTags`, filled at
  [BridgeProfileSolver.cs:951](../../BeamNgTerrainPoc/Terrain/Export/BridgeProfileSolver.cs#L951))
  and `OsmWayIds`. It is exactly what the deck exporter already consumes in
  `BridgeDeckDaeExporter.ExportFromSpans` — the pier generator hangs off the same loop.
- **Structure type is already plumbed.** `OsmFeature.BridgeStructureType`
  ([OsmFeature.cs:291-313](../../BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs#L291)) captures
  `bridge:structure=*` verbatim, with a fallback mapping of distinctive `bridge=*` values
  (viaduct/cantilever/suspension/movable/aqueduct); it flows through `PathWithMetadata` →
  `StructureSegment.BridgeStructureType` → spans. Values not promoted (`trestle`, `boardwalk`,
  `bridge:support`) are still readable from the span's `OsmTags`. **Zero new Overpass/parse work.**
- **Obstacle geometry is already indexed.** `BridgeObstacleSet`
  ([BridgeObstacles.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/BridgeObstacles.cs)) is a
  64 m-grid spatial index over OSM rail/water/road polylines and polygons (`QueryAabb`,
  `ContainsPoint` ray-cast), built once per generation
  ([TerrainGenerationOrchestrator.cs:1048](../../BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs#L1048))
  and shared via `RoadSmoothingParameters.BridgeObstacles`. Its feeder
  `BridgeObstacleClassifier.FindCrossings`
  ([BridgeObstacleClassifier.cs:183-239](../../BeamNgTerrainPoc/Terrain/Osm/Processing/BridgeObstacleClassifier.cs#L183))
  already computes *which features cross a span footprint and where* — the keep-out list is the
  same computation with intervals instead of midpoints.
- **Road-vs-road crossings are solved data.** `UnifiedRoadNetwork.GradeSeparatedCrossings`
  ([GradeSeparatedCrossing.cs](../../BeamNgTerrainPoc/Terrain/Models/RoadGeometry/GradeSeparatedCrossing.cs))
  records every grade-separated crossing with plan XY, kind (road/rail/water incl. synthetic),
  layers and solved Z — detection at
  [NetworkJunctionDetector.cs:782/1034](../../BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs#L782).
- **Footprint tests exist twice.** `BridgeSpanFootprint.Contains`
  ([BridgeSpanFootprint.cs:135-151](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeSpanFootprint.cs#L135))
  for deck footprints; `SurfaceFootprintIndex.CheckPoint` (z-aware road-surface containment,
  [SurfaceFootprintIndex.cs:87-103](../../BeamNgTerrainPoc/Terrain/Services/DecalRoad/SurfaceFootprintIndex.cs#L87))
  for road surfaces.
- **Ground elevation:** `heightMap2D` is in scope at the export call site
  ([TerrainCreator.cs:523](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L523)) — passing it into
  `ExportBridgeDecksAsync` is a one-parameter change. Sampling precedent:
  `BridgeElevationPlanner.SampleTerrain`
  ([BridgeElevationPlanner.cs:1054](../../BeamNgTerrainPoc/Terrain/Algorithms/BridgeElevationPlanner.cs#L1054)).
  Because the excavator/dam stampers run **before** export
  ([TerrainCreator.cs:457](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L457) vs. :523),
  export-time sampling reads the FINAL carved ground — which is what piers must stand on.
- **Scene/DAE:** piers go **into the existing per-span DAE** — `BeamNgDaeScene.LodLevels` takes a
  mesh list and `CloneAsCollisionMesh` clones whatever is there, so `BridgeSceneWriter` and the
  TSStatic wiring need **no changes**. Same authoring constraint as the deck: direct BeamNG-DAE
  path, no coordinate conversion — every face authored with outward flat normals in Z-up via the
  `AddFace` pattern ([BridgeDeckMeshBuilder.cs:16-23](../../BeamNG.Procedural3D/RoadMesh/BridgeDeckMeshBuilder.cs#L16)).

**The two genuinely new pieces:** (a) a keep-out/placement algorithm (forbidden intervals along the
span axis), (b) a pier mesh builder (+ archetype selection). And one small extension: buildings.

## 2. OSM taxonomy → support archetypes

From the OSM wiki (Key:bridge, Key:bridge:structure, Key:bridge:support — fetched 2026-07-09):

**`bridge=*`** (documented values): `yes` (generic, ~95%+ of all bridge ways), `viaduct` (distant
second), `aqueduct`, `boardwalk`, `cantilever`, `covered`, `low_water_crossing`, `movable`,
`trestle`, and discouraged legacy `cable-stayed`. Wiki guidance: refine with
`bridge:structure=*` / `bridge:movable=*`.

**`bridge:structure=*`** (sparsely tagged — a small minority of bridges): `beam` (most common when
present), `arch`, `truss`, `suspension`, `cable-stayed`, `simple-suspension`, `floating`,
`humpback`, `clapper`.

**`bridge:support=*`** (approved key, on separate NODES/areas — pier positions mapped explicitly):
`pier`, `abutment`, `pylon`, `lift_pier`, `pivot_pier`. Present for only a tiny fraction of
bridges → can *override* procedural placement later, never replace it. Not currently fetched as
nodes (§6 Phase 4).

**Archetype table** (decision order: `bridge:structure` → `bridge=` value → default; read
`BridgeStructureType` first, fall back to `OsmTags["bridge"]`):

| Tag signal | Archetype | v-phase |
|---|---|---|
| *untagged* / `yes` / `beam` / `truss` / `cantilever` / `low_water_crossing` / `covered` / `clapper` | **`ColumnPier`** — round/octagonal column(s) + pier cap, spacing ~`PierSpacingMeters` | **v1** |
| `viaduct` / `aqueduct` | **`ViaductPier`** — same columns but strict regular rhythm across the whole span (equal bays, symmetric), slightly heavier cap | **v1** (a spacing mode of ColumnPier) |
| `trestle` / `boardwalk` | **`TrestleBent`** — closely-spaced slender bents (v1: alias to ColumnPier with small spacing + thin columns) | v1 alias |
| `suspension` / `cable-stayed` / `simple-suspension` | **`Pylon`** — tall towers, NO intermediate columns in the main span. v1: **NoSupports** (do no harm — a wrong forest of columns under the Brooklyn Bridge is worse than none); pylon meshes + cables are their own follow-up doc | deferred (v3) |
| `floating` | **`NoSupports`** | v1 (trivial) |
| `movable` (`bridge:movable=*`) | **`NoSupports` inside the movable span**; v1: treat whole span as NoSupports (movable spans are short) | v1 (trivial) |
| `arch` / `humpback` | v1: ColumnPier (safe); real arch ribs deferred | v1 fallback |

Priorities per the wiki prevalence: **ColumnPier must look right — it covers ≥95% of real data.**
Viaduct rhythm is the visible second. Everything else is aliasing plus the NoSupports guard for
suspension-class bridges (Manhattan has exactly those — East River bridges must NOT get columns in
the river … until pylons exist).

## 3. Placement algorithm (the core, with the hard keep-out constraint)

All in **terrain-local meters, plan view**, per `BridgeSpanSnapshot`, before world conversion —
same space as `BridgeObstacleSet` and the station polyline. Piers are placed at *stations* along the
span axis (arc-length `s`), then built from the station's interpolated deck geometry.

### 3a. Candidate stations

1. Skip the span entirely when `spanLength < MinSpanLengthForPiersMeters` (default **35 m**, corpus
   §6: "spawn piers only when a span exceeds ~35–40 m") or archetype is NoSupports.
2. Usable interval: `[EndStampLengthMeters + PierEndMarginMeters, spanLength − …]` — the end zones
   already have abutment stamps/embankments (doc 06/08); a merge end
   (`Start/EndContinuesOntoDeck`) contributes no abutment but is under the trunk deck — same
   margin applies.
3. Nominal count `k = round(usable / PierSpacingMeters)` (default **30 m**, corpus: 20–40 m);
   ColumnPier: evenly spread `k` piers; ViaductPier: exact equal bays (`usable/(k+1)`).
4. Drop candidates where the deck is too low: `deckSoffitZ(s) − groundZ(s) < MinPierHeightMeters`
   (default **2.5 m**) — a low deck sits on doc-08 embankment, a stub pier there is clutter. Ground
   = heightmap sample at the station center (post-carve, see §1).

### 3b. Forbidden intervals (the hard constraint)

Build a set of forbidden arc-intervals `F = ⋃ [s_i − m_i, s_i + m_i]` along the span axis, one per
crossed obstacle. Sources, all already materialized:

1. **Network roads** (any spline the deck crosses, incl. ramps/streets): walk
   `GradeSeparatedCrossings` where this span's spline is the upper member → crossing XY → project
   onto the station polyline → `s_i`. Margin `m_i = lowerRoadHalfWidth + pierHalfExtent +
   PierClearanceMarginMeters`. Lower road half-width from the lower spline's cross-sections at the
   crossing (exact, not a class default). *Robustness:* additionally sweep the pier footprint
   against `SurfaceFootprintIndex.CheckPoint` at ground Z (§3c) — crossings that the detector
   classified another way (or missed) must still repel piers.
2. **Railways / waterways** (OSM features not in the road network): the
   `BridgeObstacleClassifier.FindCrossings` pattern, but returning the full **inside-run interval**
   per feature instead of the midpoint — the 2 m polyline sampling against the span footprint
   already computes the runs
   ([BridgeObstacleClassifier.cs:183-239](../../BeamNgTerrainPoc/Terrain/Osm/Processing/BridgeObstacleClassifier.cs#L183)).
   Margin: rail = half of rail corridor default (6 m raster width → 3 m) + pier extent + clearance;
   navigable water (`Navigable`) forbids the whole wet interval (no columns in a shipping channel);
   non-navigable water is **allowed** (real piers stand in rivers) — v1 keeps it allowed but logs it.
3. **Buildings we generate**: extend `BridgeObstacleClassifier.ClassifyFeature`
   ([BridgeObstacleClassifier.cs:44-67](../../BeamNgTerrainPoc/Terrain/Osm/Processing/BridgeObstacleClassifier.cs#L44))
   with a new `BridgeObstacleKind.Building` for `building`/`building:part` polygons (currently it
   deliberately returns null — buildings are not *clearance*-relevant, but they ARE *pier*-relevant).
   The polygons ride the existing `BridgeObstacleFeature.ContainsPoint`/`QueryAabb` machinery for
   free, and they are the SAME Overpass polygons `OsmBuildingParser` builds the visible buildings
   from — so keep-out matches what actually stands in the level. Forbidden interval = stations whose
   pier footprint intersects the polygon + `PierClearanceMarginMeters`.
   *Gate:* building keep-out applies regardless of whether building generation ran (mapped-but-not-
   generated buildings still mark plausible future content; the cost is a slid pier).
4. **Other bridge decks below**: a pier must not land on a lower deck (bridge-over-bridge,
   doc 16). Forbid stations whose ground-level footprint lies inside another span's
   `BridgeSpanFootprint` whose deck Z is BELOW ours at that point — reuse the doc-15 partner scan
   (`ComputeSpanBounds` prefilter + footprint test in
   [BridgeDeckDaeExporter.cs](../../BeamNgTerrainPoc/Terrain/Export/BridgeDeckDaeExporter.cs)).
   (A pier THROUGH a lower deck is doc 16's clearance bug wearing a new hat.)

`pierHalfExtent` = half the pier's plan diagonal (column diameter or bent width, incl. cap
overhang) — the footprint, not the centerline, must clear the obstacle.

### 3c. Slide-or-skip resolution

For each nominal pier station `s`:

1. If `s ∉ F` → place.
2. Else slide to the **nearest allowed** `s'` with `|s' − s| ≤ MaxPierSlideMeters` (default
   **12 m**, ~half a bay) — prefer the side that keeps bays more even; ViaductPier slides the whole
   rhythm if one obstacle shifts a single bay beyond tolerance (keep the repetition optic).
3. No allowed position in range → **skip the pier** and log
   `[PIER] span {SpanId} bay {i}: blocked by {kind} @ s={s:F1}m — skipped (bay grows to {len}m)`.
   The deck is a rigid solved body; a longer unsupported bay is purely optical. **Never** place on
   the obstacle — the constraint is absolute, the spacing is not.
4. Two piers that slide toward each other closer than `MinPierGapMeters` (default 8 m) → merge to
   one at the midpoint (if allowed) or keep the first.

Determinism: the whole computation is a pure function of the snapshot + obstacle set — same run,
same piers; `SpanId` keys any diagnostics.

### 3d. Pier standing surface

Ground Z = post-carve heightmap at the final station (§1 ordering). Sample the 4 column-footprint
corners, take the **minimum**, embed the column `PierGroundEmbedMeters` (default **1.5 m**) below
that — on cross-slopes the column disappears into ground instead of floating on the downhill side
(same trick as building foundations). No terrain writes — the pier meets the terrain as-is
(doctrine: post-solve shapes bare terrain only; a pier is a mesh, not a terrain edit).

## 4. Pier geometry (v1 `ColumnPier`, corpus 09a §6)

Per placed station, built from interpolated deck geometry (`center`, `normal`, banked
`LeftEdgeZ/RightEdgeZ` → soffit via `ComputeDeckThicknessMeters` — the same single source of truth
the deck/excavator use):

- **Pier cap** (the head beam): box under the soffit, plan `capWidth = deckWidth −
  2·CapSideInsetMeters (0.5)` across × `CapLengthMeters (1.5)` along, `CapDepthMeters (1.0)` deep.
  Its top face **follows the banked soffit** — reuse the end-stamp soffit-following construction
  from `a73106d` (top edges on the soffit plane between stations; watertight; outward normals).
- **Columns**: `deckWidth < TwinColumnThresholdMeters (10)` → one centered column; else two at
  ±`0.3·deckWidth` along the normal. Octagonal prism (8 sides — reads round at distance, 10 quads
  per column), `ColumnDiameterMeters (1.2)`, from cap bottom to `groundZ − embed`. Very tall piers
  (> ~25 m) may taper 1.2 → 1.6 m at base (single extra ring, optional).
- **Trestle alias**: spacing 12 m, diameter 0.5, twin columns always, no cap taper.
- Watertight solids, every face through the `AddFace` outward-winding helper; appended to the span's
  mesh list → the collision clone picks them up automatically (piers are drivable-into).
- Same placeholder material (`eca_bld_concrete`) v1; a dedicated pier material is a later polish.

Cost estimate: a 300 m viaduct ≈ 9 piers ≈ 9 × (cap 6 faces + 2 columns × 10 faces) ≈ 230 quads —
noise next to the deck itself.

## 5. Parameters & flags

New block on `BridgeDeckProfile` (mesh side) + `BridgeRuleSystemOptions`/preset plumbing
(`bridgeRules` JSON, PascalCase) mirroring the existing knob pattern
([TerrainCreator.cs:1309](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L1309), preset
import/export, `TerrainGenerationState`):

| Knob | Default | Meaning |
|---|---|---|
| `EnableBridgePiers` | **false** | Master gate. Off ⇒ byte-identical output (doctrine). |
| `MinSpanLengthForPiersMeters` | 35 | Below this: abutments only. |
| `PierSpacingMeters` | 30 | Nominal bay length (viaduct: exact rhythm). |
| `MinPierHeightMeters` | 2.5 | Soffit-to-ground below this → no pier (embankment zone). |
| `PierClearanceMarginMeters` | 3.0 | Extra keep-out beyond obstacle + pier footprint. |
| `MaxPierSlideMeters` | 12 | Slide search radius before skipping. |
| `PierGroundEmbedMeters` | 1.5 | Column embed below lowest footprint corner. |
| `ColumnDiameterMeters` / `TwinColumnThresholdMeters` / cap dims | 1.2 / 10 / (1.5, 1.0, 0.5) | §4 geometry. |

Archetype override knob (optional v2): preset-level `BridgePierStyle = auto|columns|none` for users
whose region data mis-tags structures.

## 6. Phased implementation plan (commit per phase, tests green, flag-off byte-identical)

- **Phase 1 — keep-out service + diagnostics (no meshes).** `BridgePierPlanner` (new,
  `Terrain/Export/`): per span → archetype, candidate stations, forbidden intervals (§3b sources 1,
  2, 4), slide-or-skip → `List<PierPlan>` (station s, plan XY, deck/soffit/ground Z, columns,
  skip reasons). Extend `BridgeObstacleClassifier` with `Building` kind (source 3) — **verify the
  obstacle set is reachable at export time** (it lives on `RoadSmoothingParameters.BridgeObstacles`;
  pass it + `heightMap2D` into `ExportBridgeDecksAsync` at
  [TerrainCreator.cs:523](../../BeamNgTerrainPoc/Terrain/TerrainCreator.cs#L523)). Log every
  decision as `[PIER]` lines (placed/slid/skipped + blocking kind). Unit tests: interval algebra,
  slide, skip, building polygon, lower-deck exclusion. *Deliverable: Manhattan log review only.*
- **Phase 2 — ColumnPier meshes.** `BridgePierMeshBuilder` (new, `BeamNG.Procedural3D/RoadMesh/`),
  cap + columns per §4, appended into the span DAE in `ExportFromSpans`. Fixture tests: counts,
  watertightness (cap/bottom/outward normals — parapet-test patterns), banked-deck cap, tall-pier
  ground embed. *Deliverable: render check on a long straight viaduct.*
- **Phase 3 — archetype selection + viaduct rhythm + suppression.** Tag decision order §2
  (`BridgeStructureType` → `OsmTags["bridge"]`), NoSupports for suspension-class/floating/movable,
  trestle alias, preset knobs + UI (A4 pattern). *Deliverable: Manhattan regen — East River
  suspension bridges get NO columns; highway viaducts get rhythm; nothing stands on a street,
  track, or building.*
- **Phase 4 (later, own doc) — pylons & explicit supports.** Suspension/cable-stayed pylon + cable
  meshes; fetch `bridge:support=*` nodes from Overpass (add to `BuildAllFeaturesQuery` node section)
  as exact pier-position overrides; straddle/portal piers (hammerhead over a road when both sides
  are blocked); arch ribs.

## 7. Cautions

- **Ordering is load-bearing:** pier planning needs FINAL deck Z (post `RefineSpans`, post doc-14/15
  landings — the snapshot guarantees this) and FINAL ground (post excavator/dams → sample at export
  time, not before). Do not move the snapshot capture.
- **Do no harm on suspension bridges** (§2): until pylons exist, suspension-class tags must yield
  NoSupports — Manhattan's render acceptance depends on it.
- **Never a terrain write** from the pier stage; a pier that meets ugly ground gets a longer embed,
  not a terrain patch (doc-24 lesson: terrain fill vs mesh was decided for abutments — piers are
  deliberately mesh-only).
- **Non-navigable water:** allowed but logged v1; if renders show piers mid-river looking wrong,
  flip to forbidden per-preset — the interval machinery doesn't care.
- **Merged corridors:** obstacle way-id self-exclusion must use the span's full `OsmWayIds` set
  (the classifier's `ignoreOsmWayIds` parameter exists for exactly this) or the bridge's own ways
  count as "roads under the bridge" and forbid every pier.
- **Performance:** obstacle set is prebuilt + 64 m-indexed; per-span work is O(stations + crossings).
  The doc-15 partner scan is already run per span — share its `spanBounds` dictionary.
- **DecalRoads/AI paths are unaffected** — piers are TSStatic collision inside the existing DAE; no
  items.level.json changes at all.

## 8. Verification recipe

1. **Unit fixtures:** road crossing mid-bay → pier slides ≥ margin; two obstacles bracketing a bay
   → skip + `[PIER]` log; building polygon under half the span → all piers on the other half;
   lower deck below → excluded; span < 35 m → nothing; suspension tag → nothing; viaduct → equal
   bays; flag off → deck DAEs byte-identical.
2. **Log review (Phase 1, Manhattan):** every `[PIER]` line's XY spot-checked against the OSM layer
   PNGs (`osm_layer/*.png` — the rasters the user referenced — overlay pier XY on
   `building_*_polygon.png` / `railway_rail_linestring.png` for a visual audit without booting the
   game).
3. **Render (user judges):** long viaducts supported at a believable rhythm; no column on any
   street/track/building; no columns under East River bridges; piers meet ground on slopes without
   floating; drive-into collision works.
4. **No regressions:** deck/parapet/stamp geometry unchanged with flag on (piers are additive
   meshes); `[DAM-REPORT]` byte-stable; full suite green.

Log dir: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\manhattan\MT_TerrainGeneration\logs\`.
Related: doc 09a §6 (dimensions corpus) · doc 09 B-4 (the deferral) · doc 10-e-b §G (OsmTags styling
note) · doc 13/15/16 (obstacle + footprint + stacked-deck machinery reused here) · doc 08
(embankment ends piers must not duplicate). Key files: `BridgeDeckDaeExporter.cs` (integration
point), `BridgeObstacleClassifier.cs` / `BridgeObstacles.cs` (keep-out sources),
`GradeSeparatedCrossing.cs` (road crossings), `BridgeSpanFootprint.cs` (deck-below test),
`BridgeDeckMeshBuilder.cs` (AddFace/soffit-following patterns to reuse), `TerrainCreator.cs:523`
(pass heightmap + obstacles), `OverpassApiService.cs:413` (Phase-4 `bridge:support` nodes).
