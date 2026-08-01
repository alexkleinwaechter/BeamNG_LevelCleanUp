# Backdrop Generation — Design (Variant 1)

Date: 2026-07-27
Status: **Approved design** (brainstorming complete, implementation plan pending)
Branch: `feature/backdrop`

## 1. Feature definition

A **backdrop** is a simplified, satellite-textured 3D ring of landscape around the playable terrain,
generated from an **extended GeoTIFF tile selection minus the terrain selection**. It is exported as
chunked DAE assets placed in the scene tree so the player can drive off the terrain edge and continue
driving on the backdrop.

Two feature variants were identified:

- **Variant 1 (this design):** no road smoothing, no road painting, no DecalRoads. Satellite texture
  only, adaptive mesh simplification, full collision.
- **Variant 2 (sketch only, §12):** road smoothing, optional road painting into the backdrop texture,
  DecalRoads with per-layerset backdrop opt-in.

## 2. Decisions made (with rationale)

| # | Decision | Choice | Rationale |
|---|----------|--------|-----------|
| D1 | Scope | Variant 1 now, architecture prepared for Variant 2 | V2 is much larger; V1 hooks (importance map, optional RoadNetwork input) prevent rework |
| D2 | Mesh simplification | Pure C# adaptive restricted quadtree (no CGAL) | No native dependency/toolchain/installer burden; heightmap-grid meshing is the standard terrain-LOD approach; an importance map covers both the edge band (V1) and road corridors (V2). No decimation code exists in the solution today — this is greenfield either way |
| D3 | Backdrop box | Free rectangle, any aspect ratio, must fully contain the terrain box, clamped to loaded tile mosaic | Guarantees the "drive off the edge" goal; asymmetric margins allowed |
| D4 | Collision | Full collision (colmesh clone per chunk) | Adaptive mesh is already coarse far out; chunking keeps physics broadphase happy |
| D5 | BaseColor interlock | Auto rebake together | Backdrop settings live in `MT_settings.json`; BaseColorManager bakes/Reset-&-Rebakes terrain + backdrop textures with one shared tile cache and one shared look |
| D6 | Scale limit | None — cost estimator + warnings in UI | User decides; UI shows estimated triangles / texture memory / tile downloads |
| D7 | Architecture | Hybrid: integrated UI, standalone core | UI + in-run stage on GenerateTerrain page; core `BackdropGenerator` consumes only explicit inputs so it can be re-run standalone and the texture can be rebaked without regenerating the mesh |
| D8 | Optionality | Backdrop is fully optional | Off by default (UI checkbox); library defaults off ⇒ existing pipelines/tests stay byte-identical (same discipline as bridges/tunnels) |

## 3. Current-state findings this design builds on

(From codebase exploration 2026-07-27; line numbers as of `feature/backdrop` branch point.)

### Selection UI
- The selection "map" is a hand-rolled OSM raster-tile mosaic behind a draggable `<div>` — no Leaflet.
  The terrain selection box is **fixed-size, move-only, square**, drawn in **combined-GeoTIFF source
  pixel space**; size = `TerrainSize × MetersPerPixel / nativePixelSize`.
- Selection math is **duplicated** between `BlazorUI/Components/CropAnchorSelector.razor.cs` (821 lines)
  and `CropAnchorSelectorDialog.razor.cs` (629 lines): `CalculateSelectionSizePixels`,
  `RecalculateSelectionBoundingBox`, `ClampOffsets`, drag handlers.
- Selection is persisted as `CropResult` (source-pixel `OffsetX/Y`, `CropWidth/Height`, `TargetSize`,
  WGS84 `CroppedBoundingBox`); presets store **source-pixel offsets**, not lat/lon
  (`TerrainPresetExporter.razor` "cropSettings" block).
- `state.EffectiveBoundingBox` (`TerrainGenerationState.cs:309`) is the single source of truth for the
  covered geographic area.

### BaseColorManager
- Reusable plain classes in `BeamNG_LevelCleanUp/LogicBasecolorManager/`:
  `MapTileOverlayService` (tile download + warp to terrain space), `TerrainPbrMapBuilder` (bake),
  `BaseColorModeApplier`, `BasecolorManagerService`.
- Couplings that block arbitrary-bbox reuse:
  - `EnsureOverlayImageAsync` hardcodes `{levelPath}\MT_Tiles` (`MapTileOverlayService.cs:42/69/105`).
  - Input contract is the persistence DTO `MtGeoReferenceSettings` (`:96/216/225/479`).
  - Output filename `{slug}-terrain-warp-v2.png` fixed per provider.
- The warp has two paths: precise per-pixel inverse mapping via geotransform + WKT
  (`CreateWarpedOverlay`, `:250`), and a bbox-only linear fallback (`CreateBoundingBoxOverlay`, `:233`).
- Warp fingerprint sidecar (`{final}.png.meta.json`) triggers auto-rewarp when georeference/size change;
  raw z/x/y tiles are cached in `MT_Tiles\cache\{slug}\{z}\{x}\{y}.img` and reused.
- Reset & Rebake orchestration currently lives in `BasecolorManager.razor.cs:204-253` (page-locked).
- Georeference is written once after successful generation by
  `GenerateTerrain.razor.cs:2804 SaveGeoReferenceSettingsAfterGeneration` into `MT_settings.json`.

### DAE infrastructure
- `BeamNG.Procedural3D/Exporters/ColladaExporter.cs` + `BeamNgDaeScene` (LOD levels, `Colmesh-1`
  collision, `nulldetail`, digit-to-letter name mangling). Used by bridges/tunnels/buildings.
- Bridge/tunnel convention: mesh baked in **world coordinates**, TSStatic at `(0,0,0)` identity
  rotation, SimGroup `MT_bridges`/`MT_tunnels` under MissionGroup, idempotent SimGroup upsert +
  clean-and-rewrite on regen (`BeamNgTerrainPoc/Terrain/Export/BridgeSceneWriter.cs`).
- Textured materials pattern (baseColorMap etc. + textures folder) exists in
  `BeamNgTerrainPoc/Terrain/Building/BuildingSceneWriter.cs:346` — bridges only write untextured
  placeholders.
- **No mesh decimation exists anywhere in the solution.** No chunk-size/vertex limits enforced;
  buildings have the only spatial chunking (`BuildingClusterer`, grid cells, one DAE per cell).
- Heightmap conventions: working form `float[y,x]`, row-major, **y = 0 at south edge**; world origin at
  terrain **center**, X=East, Y=North, Z=Up; `world Z = heightRelative + TerrainBaseHeight`;
  heights relative in `[0, MaxHeight]` (`HeightmapProcessor.cs:17`, `BeamNgCoordinateTransformer.cs`).

### Pipeline
- `TerrainGenerationOrchestrator.ExecuteInternalAsync` (`BlazorUI/Services/…:97`) has a clean seam
  after `CreateTerrainFileAsync`; all optional stages are boolean-gated on state.
- `MT_TerrainGeneration/` debug folder is **wiped at the start of every full run**
  (`ClearDebugFolder`, orchestrator `:104`).
- `CachedHeightMap`/`CachedNetwork` on state already support standalone DecalRoad regeneration —
  the same mechanism serves standalone backdrop regeneration in-session.
- Preset save/load: `TerrainPresetExporter.razor` / `TerrainPresetImporter.razor` /
  `TerrainPresetResult.cs` / `GenerateTerrain.razor.cs:2104 OnPresetImported` (deferred crop-offset
  apply pattern `ApplyPendingCropOffsets`).

## 4. Architecture overview

```
BeamNG_LevelCleanUp (app layer)
├── BlazorUI/Pages/GenerateTerrain.razor          ← thin wiring only (panel tag + box params)
├── BlazorUI/Components/BackdropSettingsPanel     ← NEW: all backdrop UI
├── BlazorUI/Components/CropAnchorSelector(+Dialog) ← MOD: second resizable box
├── BlazorUI/Components/SelectionGeometry         ← NEW: shared selection math (de-duplication)
├── BlazorUI/State/TerrainGenerationState         ← MOD: nested BackdropSettings POCO
├── BlazorUI/Services/BackdropOrchestrator        ← NEW: app-side orchestration (in-run + standalone)
├── LogicBasecolorManager/MapTileOverlayService   ← MOD: OverlayRequest overload (arbitrary bbox)
├── LogicBasecolorManager/BackdropTextureBaker    ← NEW: per-chunk satellite textures
├── LogicBasecolorManager/BasecolorManagerService ← MOD: backdrop rebake + Reset&Rebake extraction
└── Objects/MtSettings/MtSettings                 ← MOD: MtBackdropSettings block

BeamNgTerrainPoc (core library — no app-layer references)
└── Terrain/Backdrop/                             ← NEW namespace
    ├── BackdropGenerationParameters              ← explicit input contract
    ├── BackdropGenerator                         ← entry point, orchestrates the below
    ├── BackdropHeightField                       ← band raster + far raster loader
    ├── BackdropChunkPlanner                      ← chunk grid, per-chunk bboxes/filenames
    ├── BackdropQuadtreeMesher                    ← restricted quadtree, importance map, seam snap
    └── BackdropSceneWriter                       ← DAE + materials.json + items.level.json
```

**Dependency direction (hard constraint):** `BeamNgTerrainPoc` must not reference
`BeamNG_LevelCleanUp`. Therefore mesh/DAE/scene generation lives in the core library, while
**texture baking lives in the app layer** (it needs `MapTileOverlayService`). The seam between them is
the **chunk plan**: `BackdropGenerator` emits chunk definitions (world rects, WGS84 bboxes, texture
filenames); `BackdropTextureBaker` consumes the plan. DAE + materials.json reference texture *paths*
that are written afterwards — order-independent.

### Data flow (in-run)

```
GenerateTerrain "Generate" click (EnableBackdrop = true)
  → TerrainGenerationOrchestrator.ExecuteInternalAsync
      … existing stages …
      → CreateTerrainFileAsync            (produces final OutputHeightMap)
      → [NEW gated stage] BackdropOrchestrator.GenerateAsync(state, outputHeightMap)
          → BackdropGenerator.Generate(parameters)        (core: rasters → mesh → DAEs → scene)
          → BackdropTextureBaker.BakeAllChunksAsync(plan) (app: satellite textures per chunk)
          → MtSettings: write MtBackdropSettings          (contract for BaseColorManager rebake)
```

### Data flow (standalone regen / rebake)

- **Regenerate Backdrop** button: in-session uses `state.CachedHeightMap`; cross-session reconstructs
  the heightmap from the level `.ter` + `MaxHeight`/`TerrainBaseHeight` (from state or
  `MT_settings.json`). Wipes only `MT_backdrop` outputs + `MT_TerrainGeneration/backdrop/`.
- **BaseColorManager bake / Reset & Rebake**: reads `MtBackdropSettings`, re-warps every chunk texture
  from the shared tile cache with current provider/date/adjustments, overwrites textures **in place**
  (same filenames ⇒ no DAE/materials.json rewrite). Mesh untouched.

## 5. Selection UI + data model

### Data model

```csharp
// BlazorUI/State — nested on TerrainGenerationState as `Backdrop`
class BackdropSettings
{
    bool Enabled;                    // default false (D8)
    // Selection in combined-GeoTIFF source pixels (same space as CropResult):
    int OffsetX, OffsetY, Width, Height;
    GeoBoundingBox? BoundingBox;     // derived WGS84, recomputed like CroppedBoundingBox
    double EdgeBandMeters = 200;     // full-resolution band width at the terrain seam
    double MaxVerticalErrorNearMeters = 0.5;   // quadtree error tolerance at band edge
    double MaxVerticalErrorFarMeters  = 8.0;   // tolerance at the outer backdrop edge (lerped)
    double ChunkTargetMeters = 2000; // chunk grid cell target size
    double TexelDensityNearMPerPx = 1.0;       // texture density target near terrain
    int    MaxChunkTextureSize = 2048;         // per-chunk texture clamp (pow2)
    int    MaxFarRasterDimension = 8192;       // far raster cap
    bool   SeamSkirt = true;         // vertical flange at the terrain boundary (§7)
}
```

- `TerrainGenerationState.Reset()` must reset `Backdrop` (explicit checklist item — `Reset()`
  enumerates every field manually).
- Validation (before generation and live in the UI): the backdrop rect must **contain** the terrain
  crop rect and lie within the combined tile mosaic bounds. A side with zero margin simply produces
  no ring on that side (allowed). A side with a margin between 0 and `EdgeBandMeters` gets a UI
  warning (the full-resolution band cannot fully fit there; the band is clipped to the available
  margin). At least one side must have a margin > 0, otherwise generation is skipped with a
  validation error.

### UI

- **Second box** in `CropAnchorSelector` and `CropAnchorSelectorDialog`: a resizable rectangle
  (8 drag handles: 4 corners + 4 edges) rendered around the move-only terrain square. Dragging the
  body moves it; handles resize it; live clamping enforces containment + mosaic bounds. Fullscreen
  dialog additionally gets 4 numeric S/W/N/E fields for the backdrop box (mirroring the terrain bbox
  fields).
- **De-duplication:** extract the already-duplicated selection math into a plain class
  `SelectionGeometry` (pixel↔WGS84 conversion, clamping, containment tests, style computation
  helpers) used by both components — targeted cleanup, not a rewrite of the components.
- **`BackdropSettingsPanel.razor`** hosts everything else: enable switch, band width, error
  tolerances, chunk size, texture density/clamp, cost estimate, Generate/Regenerate/Remove buttons.
  `GenerateTerrain.razor` gains only the panel tag + two parameters on the selector components.
- **Cost estimator** (lives with the panel, pure functions):
  - Triangles ≈ band area × 2 / (mpp²) + far-field estimate from a fast coarse-raster error probe.
  - Texture memory = Σ chunkTexSize² × 4 bytes (uncompressed upper bound; note in UI).
  - Tile downloads = slippy tiles covering backdrop at chosen zoom minus already-cached count.
  - Yellow warning / red warning thresholds (e.g. > 2 M / > 8 M triangles, > 256 MB / > 1 GB texture);
    generation is never blocked (D6).

## 6. Height data: two rasters

| Raster | Extent | Resolution | Purpose |
|--------|--------|-----------|---------|
| **Band raster** | ring of `EdgeBandMeters` around the terrain rect | terrain `MetersPerPixel` | seam-exact, full-detail edge |
| **Far raster** | whole backdrop rect | capped at `MaxFarRasterDimension` (default 8192) per side | everything beyond the band |

Both are read from the selected GeoTIFF tiles via the existing `GeoTiffCombiner` /
`GeoTiffReader.ReadGeoTiff(crop…)` path (nodata preserved). The band ring is thin, so memory stays
modest even for very large backdrops (e.g. 8 km terrain, 200 m band, 1 m/px ≈ 4 × (8000×200) floats
≈ 26 MB). The mesher samples the band raster inside the band, the far raster outside; both bilinear.

**Nodata:** filled by edge-extension (nearest valid sample), with a warning reporting the nodata
percentage. A chunk that is 100 % nodata is skipped entirely (warning).

## 7. Seam correctness (the hardest problem)

The terrain edge heights are **not** the raw DEM: they passed through 16-bit quantization, bicubic
stretch-resampling to power-of-2, spike fixes, and optionally hydraulic erosion / (V2) road smoothing.
Sampling raw DEM at the seam **will** produce visible steps. Therefore:

1. **Seam snap:** mesh vertices on the terrain boundary (distance 0) take their heights **exactly**
   from the final terrain output heightmap edge rows/columns (`OutputHeightMap`), sampled at terrain
   pixel positions. The band is forced to full subdivision, so backdrop seam vertices coincide with
   terrain pixel corners.
2. **Band blend:** for `0 < d < EdgeBandMeters`, height = lerp(terrain-edge-consistent value, raw DEM,
   smoothstep(d / EdgeBandMeters)). "Terrain-edge-consistent value" extends the snap by blending the
   *difference* (terrainEdge − demAtSeam) outward, so the whole delta field fades across the band —
   not just the boundary line.
3. **Vertical datum:** `backdropZ = demElevation − terrainCropMinElevation + TerrainBaseHeight`
   (unclamped — distant peaks may exceed the terrain's `MaxHeight`, which is a feature, not a bug).
   `terrainCropMinElevation` is the min-elevation value used for the terrain's own normalization.
4. **Horizontal datum:** backdrop world XY uses the **same effective geotransform** math as
   `SaveGeoReferenceSettingsAfterGeneration` / `GetEffectiveSourceGeoTransform`
   (`GenerateTerrain.razor.cs:2883`): source pixels → native CRS → terrain-space meters → world
   (origin at terrain center). Any independent re-derivation here is exactly what creates cliffs.
5. **Seam skirt (default on):** a thin vertical flange at the terrain boundary extending ~2 m
   downward, hidden below the terrain surface. BeamNG's terrain renderer LODs independently of the
   TSStatic meshes; at distance the two tessellations can open hairline cracks — the skirt hides
   see-through pixels. (Collision clone excludes the skirt.)

## 8. Adaptive mesh (restricted quadtree)

- **Domain:** the backdrop ring (backdrop rect minus terrain rect), in world XY. The chunk grid is
  aligned so that grid lines include the terrain boundary lines (extend the terrain square's edges
  outward → up to 8 ring regions, subdivided into ~`ChunkTargetMeters` cells).
- **Refinement rule per leaf cell:** subdivide while
  `verticalError(cell) > tolerance(distanceToTerrain)` **or** `importance(cell) demands more`.
  - `verticalError` = max |bilinear raster − plane through cell corners| sampled on a small grid.
  - `tolerance` lerps `MaxVerticalErrorNearMeters → MaxVerticalErrorFarMeters` with distance from
    the terrain rect (normalized by max margin).
  - **Importance map** = generic list of contributors. V1: the edge band (forces subdivision to
    terrain mpp within `EdgeBandMeters`). V2 adds rasterized road corridors — no mesher change.
- **Crack-free:** restricted quadtree (adjacent leaf levels differ ≤ 1) + standard transition
  triangulation at level boundaries.
- **Chunk borders:** the 1D subdivision along every shared chunk edge is computed **once** per edge
  and handed to both chunks, so border vertices are bitwise identical (testable). No inter-chunk
  skirts needed.
- **Output per chunk:** one `Mesh` (positions/normals/UVs) + colmesh clone. Smooth normals from the
  raster gradient (not per-face) so lighting doesn't reveal the triangulation. UV = planar projection
  over the chunk's world rect.

## 9. DAE / scene output

```
art/shapes/MT_backdrop/
├── backdrop_{cx}_{cy}.dae          one per chunk (visual LOD + Colmesh-1 clone)
├── main.materials.json             one textured material per chunk
└── textures/
    └── backdrop_{cx}_{cy}.png      satellite texture (written/overwritten by texture baker)

main/MissionGroup/MT_backdrop/items.level.json    TSStatic per chunk
main/MissionGroup/items.level.json                idempotent SimGroup "MT_backdrop" upsert
```

- Meshes baked in world coordinates; TSStatic at `(0,0,0)`, identity rotation (bridge/tunnel
  convention). BeamNG culls per-object by bounding box ⇒ chunking gives culling for free.
- LOD pixel size via `BeamNgLodDefaults.ComputeForBounds` per chunk so distant chunks stay visible;
  single LOD level (the mesh is already adaptive), `nulldetail` sized so chunks never fully vanish
  at extreme distance (exact value an implementation detail; must be validated in-game).
- Materials: `mt_backdrop_{cx}_{cy}`, `Stages[0].baseColorMap` → texture path
  `/levels/{level}/art/shapes/MT_backdrop/textures/backdrop_{cx}_{cy}.png`, roughnessFactor 1.0,
  written idempotently-by-name (never clobbers a user-edited material) — follow
  `BuildingSceneWriter.CreateMaterialEntry`, not the untextured bridge placeholder.
- Clean-and-rewrite on regen: wipe `art/shapes/MT_backdrop/` + scene folder entries, keep the parent
  items.level.json SimGroup line (BridgeSceneWriter pattern).

## 10. Texture pipeline + BaseColorManager interlock

### OverlayRequest refactor (`MapTileOverlayService`)

New overload `EnsureOverlayImageAsync(OverlayRequest)`:

```csharp
record OverlayRequest(
    GeoBoundingBox Wgs84Bounds,
    double[]? NativeGeoTransform, string? ProjectionWkt,   // null ⇒ bbox-only linear warp
    int OutputSize,                    // square, pow2
    string OutputPath,                 // full path of the final PNG
    string TileCacheRoot,              // shared: {level}\MT_Tiles\cache
    string ProviderName, string? ImageryDate);
```

The existing `MtGeoReferenceSettings` signature becomes a thin adapter. Warp fingerprint sidecar
mechanism reused per output file. The raw z/x/y tile cache is **shared** between terrain overlay and
all backdrop chunks (same provider slug folders) — tiles download once.

### BackdropTextureBaker (app layer)

For each chunk in the plan: compute WGS84 bbox (via the chunk's native rect + geotransform),
per-chunk texture size = `clamp(pow2(chunkExtent / texelDensity(d)), 256, MaxChunkTextureSize)`
where `texelDensity(d) = TexelDensityNearMPerPx × lerp(1, 4, dNorm)` and `dNorm` is the chunk
center's distance to the terrain rect normalized by the largest backdrop margin (i.e. density
coarsens linearly to 4× near-density at the outer edge), call
`EnsureOverlayImageAsync(OverlayRequest)`, then apply the
shared brightness/contrast/saturation adjustments from `MtBasecolorModeSettings`. Failures: per-chunk
retry once, then flat-gray texture + warning — one bad chunk never fails the run.

### MtSettings contract

```jsonc
"BackdropSettings": {                      // new block in MT_settings.json
  "Enabled": true,
  "Wgs84Bounds": { ... }, "NativeRect": { ... }, "GeoTransform": [ ... ], "ProjectionWkt": "...",
  "Chunks": [ { "Cx": 0, "Cy": 1, "Wgs84Bounds": {...}, "TextureFile": "backdrop_0_1.png",
                "TextureSize": 2048 }, ... ],
  "EdgeBandMeters": 200,
  "LastBakeUtc": "...", "LastTextureBakeUtc": "..."
}
```

Written by backdrop generation; read by the BaseColorManager.

### BaseColorManager behavior

- When `BackdropSettings.Enabled` is present: **Apply BaseColor Mode** and **Reset & Rebake** also
  re-warp + re-adjust every chunk texture (progress via PubSub). Overwrites in place; mesh, DAEs and
  materials.json untouched.
- The Reset & Rebake orchestration moves from `BasecolorManager.razor.cs` into
  `BasecolorManagerService` (targeted extraction of the already-identified page coupling) so the
  backdrop step has a non-UI home.
- Staleness: the existing bake-staleness banner logic extends to the backdrop (compare
  `LastTextureBakeUtc` vs georef/provider changes).

### Known appearance caveat (recorded, not solved in V1)

Terrain basecolor = material colors **blended** with satellite per material blend factor; backdrop =
pure satellite + shared adjustments. At blend < 100 % a tint difference at the boundary is possible.
V1 mitigation: shared sliders + help note recommending high overlay blend near the terrain edge.
Sampling terrain material tint into the backdrop band is deferred.

## 11. Orchestration, presets, error handling

### Orchestrator integration

- One new gated stage in `TerrainGenerationOrchestrator.ExecuteInternalAsync` after
  `CreateTerrainFileAsync`: `if (state.Backdrop.Enabled) → BackdropOrchestrator.GenerateAsync(...)`.
  Backdrop failure does **not** fail the terrain run (warning + skip, like OSM layer export).
- `BackdropOrchestrator` (app service) builds `BackdropGenerationParameters` from state + output
  heightmap, calls the core, then the texture baker, then writes `MtBackdropSettings`.
- Standalone **Regenerate Backdrop**: cached heightmap in-session; `.ter` + metadata cross-session;
  clear error if no `.ter` exists. Wipes only backdrop outputs + `MT_TerrainGeneration/backdrop/`.
- **Remove Backdrop** button: deletes `art/shapes/MT_backdrop/`, the scene folder, the SimGroup
  entry, and the `MtBackdropSettings` block. A terrain run with backdrop disabled leaves an existing
  backdrop untouched (non-destructive default).
- Debug artifacts (`band raster PNG`, `far raster PNG`, quadtree level map, per-chunk stats) go to
  `MT_TerrainGeneration/backdrop/`; the full-run wipe behavior stays as-is.

### Presets

- `TerrainPresetExporter`: new `_appSettings.backdropSettings` block — enabled, source-pixel rect,
  band width, error tolerances, chunk target, texture density/clamps, skirt flag.
- `TerrainPresetImporter` + `TerrainPresetResult` + `OnPresetImported`: map back into
  `state.Backdrop`, using the deferred-apply pattern (`ApplyPendingCropOffsets`) because the selector
  needs GeoTIFF metadata first. Same source-pixel fragility as crop offsets; mitigated by the
  existing copy-tiles-into-preset-folder behavior.

### Error handling summary

| Case | Behavior |
|------|----------|
| Backdrop rect doesn't contain terrain rect / outside mosaic | Validation error, stage skipped, clear message |
| Nodata in rect | Edge-extension fill + warning with % |
| Fully-nodata chunk | Chunk skipped + warning |
| Tile download failure | Retry once, then flat-gray texture + warning |
| No WKT/geotransform | bbox-only linear warp + log note (accuracy degrades with distance) |
| Standalone regen without `.ter` | Clear error |
| Backdrop stage throws | Terrain run still succeeds; error surfaced via PubSub |

## 12. Variant 2 preparation (sketch — NOT implemented in V1)

- **Importance map** already accepts arbitrary contributors → V2 adds rasterized road corridors.
- `BackdropGenerationParameters` reserves an optional `RoadNetwork` input (unused in V1).
- Texture baker accepts optional overlay compositors → road painting = compositing
  `MaterialPainter` coverage masks (with material base colors) over the satellite texture per chunk.
- DecalRoads: `DecalRoadSettings` layersets get a `RenderOnBackdrop` flag; generator projects nodes
  onto the backdrop surface; bridge/tunnel selection UI extended accordingly.
- **Open problem (V2 decision, recorded):** road smoothing operates on uniform-resolution square
  rasters; the far raster is capped-res. Corridor-local resampling vs. coarse smoothing far out is
  a V2 design decision.
- OSM fetch for the backdrop bbox: `CanFetchOsmData` / Overpass area limits need re-evaluation for
  much larger areas (V2).

## 13. Testing

New `Backdrop/` suite in `BeamNgTerrainPoc.Tests` (core only — app-layer texture baking is manually
validated like the rest of the BaseColorManager):

- **Mesher invariants:** restricted quadtree (adjacent leaf levels ≤ 1), forced full-res band,
  vertical error bound holds vs. analytic heightfields (plane, ramp, sine), ring cutout exact,
  chunk-border vertex bitwise identity, deterministic output for identical inputs.
- **Seam:** synthetic terrain heightmap + offset DEM → seam vertices exactly equal terrain edge
  values; band blend monotonic; datum formula (`− cropMin + TerrainBaseHeight`) verified.
- **Scene writer:** SimGroup idempotence, TSStatic field shape, clean-and-rewrite, textured material
  entries — mirror `BridgeSceneWriterTests`.
- **Chunk planner:** grid aligned to terrain boundary lines, per-chunk bbox math, texture size
  formula.
- **Parameters validation** tests.
- Library defaults keep backdrop **off** ⇒ all existing baselines stay byte-identical.

Manual validation checklist (user, in-game): drive across the seam on all four sides (no step, no
gap), distant chunks visible, collision everywhere on the backdrop, texture look vs. terrain
basecolor, BaseColorManager provider switch rebakes backdrop, Remove Backdrop cleans fully.

## 14. Named problems & risks (explicit)

1. **Seam steps** from quantization/resampling/processing → solved by seam snap + delta blending (§7);
   the single highest-risk area, covered by dedicated tests.
2. **Horizontal misregistration** if backdrop XY math re-derives the transform differently from
   `GetEffectiveSourceGeoTransform` → mandate reuse of the same code path.
3. **Independent LOD of terrain vs. TSStatic** can open hairline cracks at distance → seam skirt.
4. **Appearance seam** at overlay blend < 100 % → recorded caveat, shared sliders, help note (§10).
5. **VRAM/tile-volume growth** with backdrop size → cost estimator + warnings, per-chunk clamps (D6).
6. **Nodata regions** (sea, missing tiles) → edge-extension + warnings (§6).
7. **Distant-chunk culling**: default TSStatic LOD sizing may hide far chunks → `BeamNgLodDefaults`
   scaling + in-game validation item.
8. **Debug-folder wipe** on full runs would eat backdrop artifacts of a standalone regen →
   subfolder discipline (`MT_TerrainGeneration/backdrop/`), standalone regen wipes only its own.
9. **Preset fragility** (source-pixel offsets) → same mitigation as crop (tile copy into preset).
10. **Project dependency direction** forbids core→app references → texture baking split into the app
    layer via the chunk-plan contract (§4).
11. **`Reset()` completeness**: `TerrainGenerationState.Reset()` enumerates fields manually — the new
    `Backdrop` object must be added or stale state leaks between sessions.

## 15. Defaults chosen (tunable, not re-asked)

| Parameter | Default |
|-----------|---------|
| Edge band width | 200 m |
| Max vertical error near/far | 0.5 m / 8 m |
| Chunk target size | 2000 m |
| Texel density near | 1 m/px |
| Max chunk texture | 2048 px |
| Far raster cap | 8192 px |
| Seam skirt | on, ~2 m |
| Warning thresholds | 2 M/8 M triangles; 256 MB/1 GB texture |
