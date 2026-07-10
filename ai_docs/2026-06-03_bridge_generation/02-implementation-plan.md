# Implementation Plan — Simple Bridge Deck (v1)

**Date:** 2026-06-03
**Depends on:** `00-findings-and-decisions.md`, `01-spec-simple-bridge-deck.md`
**Branch:** cut from `develop` (e.g. `feature/bridge_deck_generation`).

Ordering is deliberate: **verify the data first**, then build geometry, then wire into the pipeline,
then materials/scene, then validate in-game. Each step is independently buildable/testable.

---

## Step 0 — Verification spike (no production code) — ✅ DONE (2026-06-03)
**Goal:** prove the deck elevation source is real before building on it.

**Outcome (PASS):** `BeamNgTerrainPoc.Tests/Elevation/BridgeDeckElevationSpikeTests.cs`. With bridge
cross-sections marked `IsExcluded`, the chain solve still populates `TargetElevation` (73.73–81.02 m
over a valley floor at 60 m), `EffectiveRoadWidth`=8 m is usable, deck floats ~15 m above terrain.
Banking is flat (`BankAngleRadians`=0) and `Left/RightEdgeElevation` are NaN — so the deck builder
derives edges from `center ± width/2` at `TargetElevation`. → Proceed with cross-sections as the deck
source (D2); `ElevationProfile` stays the fallback. Full result table in `00-findings-and-decisions.md` §4b.


- On a real run of a map with a known bridge, log per-bridge-spline: cross-section count,
  `TargetElevation` min/max (assert not `NaN`), `EffectiveRoadWidth`, `BankAngleRadians` range, and
  whether the bridge ended up in an elevation chain.
- Confirm the first/last bridge cross-section Z ≈ adjacent approach-road Z (reuse the assertion style in
  `BridgeElevationChainingTests`).
- **Decision gate:** if `TargetElevation` is populated → proceed with cross-sections as the source
  (D2). If frequently unset → promote the `ElevationProfile` fallback to primary. Record the outcome in
  `00-findings-and-decisions.md`.

**Done when:** we know, with evidence, that excluded bridge cross-sections carry usable geometry.

---

## Step 1 — Include excluded cross-sections in world-coord conversion — ✅ DONE (2026-06-03)
**Files:** `BeamNgTerrainPoc/Terrain/Export/CrossSectionConverter.cs`

- Added an opt-in `includeExcluded` param (default `false`) to `ConvertPath` and
  `ConvertPathToWorldCoordinates`, plus a dedicated
  `ConvertSplineToWorldCoordinates(network, splineId, …)` that passes `includeExcluded: true` for a single
  (bridge) spline. Existing DecalRoad/road-mesh callers are untouched (default keeps dropping excluded
  sections). NaN-elevation / invalid-position sections are still filtered even when including excluded.
- Tests: `BeamNgTerrainPoc.Tests/Export/CrossSectionConverterBridgeTests.cs` — proves the excluded bridge
  spline yields a non-empty world-coord list via the opt-in path, and that the default path still drops it.

**Done when:** an excluded bridge spline converts to a full world-coordinate cross-section list. ✅

---

## Step 2 — Bridge deck mesh builder — ✅ DONE (2026-06-03, pending in-game/3D-viewer visual check)
**New:** `BeamNgTerrainPoc/Terrain/Export/BridgeDeckDaeExporter.cs` (modeled on `RoadNetworkDaeExporter`)

- Input: `UnifiedRoadNetwork`, **shapes output directory** (caller composes `art/shapes/MT_bridges`),
  `terrainSizePixels`, `metersPerPixel`, `terrainBaseHeight`, material name (defaults to
  `bridge_deck_placeholder`), optional `RoadMeshOptions`.
- `ShouldGenerateDeck(spline)` = `IsBridge && Parameters.ExcludeBridgesFromTerrain` (D1). Bridges only;
  tunnels deferred (D7).
- For each qualifying bridge spline:
  1. World cross-sections via `CrossSectionConverter.ConvertSplineToWorldCoordinates` (Step 1, includes
     excluded). The flat-pane edges are produced automatically by `RoadCrossSection` (null edge elevs +
     bank 0 ⇒ center ± width/2 at center elevation) — no special handling needed.
    2. `RoadMeshBuilder` → one ribbon `Mesh` (material = placeholder, no shoulders/curbs/end-caps).
    3. `ColladaExporter.Export(BeamNgDaeScene, "{shapesDir}/bridge_{splineId}.dae")` with building-style
      `base00/start01`, visible deck LOD node, material-less `Colmesh-1`, and `collision-1` marker.
- **Fallback handling:** if a bridge yields < 2 usable cross-sections (unchained / NaN elevation) it is
  **skipped with a warning** in the result (never crashes, never emits a NaN deck — acceptance §6.5). The
  `ElevationProfile`/terrain fallback is left as a follow-up; skip-with-warning is the v1 behavior.
- Returns `BridgeDeckExportResult` with `Decks` (`SplineId`, `DaeFileName`, `OutputPath`, vert/tri counts),
  `BridgesSkipped`, and `Warnings` — feeds the scene writer (Step 3).
- No chunking (D5): exactly one mesh + one file per bridge.
- Tests: `BeamNgTerrainPoc.Tests/Export/BridgeDeckDaeExporterTests.cs` — one `.dae` per bridge with a
  ribbon mesh (2 verts/CS, 2 tris/segment), BeamNG DAE hierarchy + separate `Colmesh-1`, non-bridge roads
  excluded, unchained bridge skipped+warned.

**Done when:** running it on a bridge map writes correct `.dae` files (inspect in the 3D viewer /
HelixToolkit) with the deck at the right elevation and width. (Code + unit tests ✅; visual check pending
once the pipeline hook lands in Step 5.)

---

## Step 3 — Bridge scene writer (TSStatic + SimGroup) — ✅ DONE (2026-06-03)
**New:** `BeamNgTerrainPoc/Terrain/Export/BridgeSceneWriter.cs` (modeled on `BuildingSceneWriter`)

- `EnsureSimGroupInParent(parentItemsPath, "MissionGroup")` — idempotently declares the `MT_bridges`
  SimGroup in `main/MissionGroup/items.level.json`.
- `WriteSceneItems(decks, outputPath, shapePath)` — one `TSStatic` per `BridgeDeckExportItem` at position
  `(0,0,0)` (world-coord mesh), identity rotation, `isRenderEnabled=true`, `useInstanceRenderData=true`,
  new GUID, `shapeName = shapePath + bridge_{splineId}.dae`. NDJSON via `SimItemsJsonSerializer`.
- Tests: `BeamNgTerrainPoc.Tests/Export/BridgeSceneWriterTests.cs` — SimGroup added + idempotent, one
  TSStatic per deck at origin pointing at its dae, shapePath trailing-slash handling.

**Done when:** the level has a `MT_bridges` group and one `TSStatic` per bridge pointing at its dae. ✅

---

## Step 4 — Placeholder material — ✅ DONE (2026-06-03)
**Reuse:** building material-writing path (`ArtItemsJsonSerializer`).

- `BridgeSceneWriter.WritePlaceholderMaterial(outputPath, materialName)` writes a keyed materials.json with
  one flat-color `Material` (concrete-gray `baseColorFactor`, rough, no maps). Idempotent on the material
  name and preserves any other materials already in the file (won't clobber a real material added later).
- Tests in `BridgeSceneWriterTests.cs`: named flat material written; idempotent + preserves others.

**Done when:** the deck renders shaded (not magenta/missing-material) in-game. (Code + tests ✅; visual
check pending Step 5 wiring.)

---

## Step 5 — Pipeline hook — ✅ DONE (2026-06-03)
**File:** `BeamNgTerrainPoc/Terrain/TerrainCreator.cs`

- New `ExportBridgeDecksAsync(network, outputPath, parameters, log)` runs Steps 2→4→3: clean previous
  MT_bridges output → `BridgeDeckDaeExporter.Export` (decks to `art/shapes/MT_bridges/`) →
  `WritePlaceholderMaterial` (`main.materials.json` next to the decks) → `EnsureSimGroupInParent` +
  `WriteSceneItems` (TSStatics to `main/MissionGroup/MT_bridges/`). `levelDir` = directory of the `.ter`,
  `levelName` via `ExtractLevelName`, `shapePath = /levels/{levelName}/art/shapes/MT_bridges/`. Wrapped in
  try/catch so a failure never aborts terrain generation.
- Call site is right after the DecalRoad block, gated on
  `network.Splines.Any(BridgeDeckDaeExporter.ShouldGenerateDeck)` — **independent of the
  `ExportRoadMeshDae` debug toggle** (this is a real feature, not a debug export). Runs after Phase-2.5
  banking, so decks pick up banking + edge elevations.
- With no qualifying bridge splines the block is skipped entirely → output unchanged (spec §6.4).

**Done when:** a normal terrain-generation run with bridge generation on produces decks end-to-end, and
with it off produces byte-identical output to today. (Wired + 346 tests green; in-game run pending.)

---

## Step 6 — Tests & in-game validation — ⏳ PARTIAL (2026-06-03)
- **Unit:** ✅ Step 1 converter inclusion; Step 2 mesh vertex/triangle counts + non-bridge exclusion +
  unchained-skip; Step 3/4 scene + material. 346 tests green.
- **Integration:** end-to-end on a real bridge map → assert the dae files + NDJSON entries exist. (Not
  automated — `ExportBridgeDecksAsync` is private; covered transitively by the unit tests on each writer.)
- **Manual (BeamNG):** ✅ **decks confirmed present in-game (2026-06-03).** Still TODO — **quality pass**:
  deck aligned with the road line, ends flush with the approaches (no vertical step), width correct,
  banking on curves looks right, terrain untouched beneath. User will assess after the docs are finalized.

---

## Step 7 — Junction harmonization on bridge endpoints (D9, added 2026-06-03) — ✅ DONE (2026-06-03)
**Files:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs` (+ wherever bridge endpoint
junctions are currently excluded from harmonization).

- The harmonizer now builds its cross-section lookup after junctions are known, and includes excluded bridge
  splines only when a generated bridge endpoint is connected to another spline. This lets bridge↔approach
  endpoint junctions participate without making isolated bridge endpoints look like dead-end roads.
- **Guard implemented:** isolated generated bridge endpoints stay out of the lookup, and regression coverage
  asserts bridge deck start/end elevations stay within the chain value (< 1m) after harmonization over a valley.
- Tests: `BridgeElevationChainingTests` covers connected bridge inclusion, isolated bridge exclusion, and
  no terrain-pinning regression.

**Done when:** bridge endpoints harmonize with approaches without the deck end being pulled toward terrain. ✅

---

## Step 8 — Lane markings on the deck (D8, added 2026-06-03) — ✅ DONE (2026-06-06, code/test)
**Files:** `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`,
`BeamNgTerrainPoc.Tests/DecalRoad/BridgeDecalRoadFilterTests.cs`.

- `DecalRoadGenerator` no longer skips generated bridge splines
  (`IsBridge && ExcludeBridgesFromTerrain`). Generated tunnels remain skipped.
- Generated bridge DecalRoads use the existing unified cross-section path, so excluded bridge cross-sections
  with solved `TargetElevation` become decal nodes at deck/source elevation rather than terrain heightmap Z.
- Generated bridge DecalRoads force `OverObjects = true` by carrying an `isGeneratedBridge` flag into
  `GenerateForLayerRange(...)` and setting `OverObjects = layer.OverObjects || isGeneratedBridge`.
- Existing `GeneratedDecalRoad.OverObjects` and `DecalRoadSceneWriter` serialization were already present;
  no scene model/serializer addition was needed.
- Bridge `.dae` export now provides a BeamNG `Colmesh-1` under `start01`, because `overObjects` only has an
  effect when the deck TSStatic exposes a collision mesh. v1 uses the generated deck ribbon as the colmesh.
- `RoadCorridorBuilder` and terrain painting exclusions remain unchanged, so generated bridge decks still avoid
  terrain stamping under the mesh.
- Tests: generated bridge spline yields DecalRoad output; generated bridge output has `OverObjects = true`;
  node Z follows bridge cross-section/deck elevation plus terrain base height; regular roads preserve layer-driven
  `OverObjects` true/false behavior; existing corridor exclusion tests still pass.

**Validation:** focused DecalRoad tests passed (49 tests, 2026-06-06). Manual BeamNG visual confirmation of
lane/edge markings rendering on the deck is still part of Step 6's quality pass.

---

## Out-of-scope follow-ups (track, don't build yet)
- Rename `IsExcluded` / `ExcludeBridgesFromTerrain` → "generate bridges / don't stamp terrain" (spec §7).
- Superelevated decks (real banking for excluded sections), deck thickness/sides, railings, piers,
  abutments, parametric `BridgeStructureType` geometry.
- **Tunnels** via the same machinery (D7).
- Custom/real bridge materials (replace placeholder).
- Stable per-bridge identity if `SplineId` isn't stable across runs (naming/dedup).
