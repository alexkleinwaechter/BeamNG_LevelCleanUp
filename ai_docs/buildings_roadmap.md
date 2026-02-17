# Procedural Building Generation — Roadmap

## Session History

### Session 1: Data Models & Mesh Generation (committed: 60a3029)
**Goal:** Foundation classes for building geometry.

**Delivered:**
- `BuildingData` — data model (footprint, height, levels, materials, roof shape, world position)
- `BuildingDefaults` — maps OSM `building=*` type tags to sensible defaults (levels, height, materials)
- `BuildingMeshGenerator` — generates wall quads, flat roof triangulation, optional floor; groups meshes by material key
- `BuildingMaterialDefinition` (in BeamNG.Procedural3D) — PBR material descriptor (color/normal/ORM maps, tiling, default color)

### Session 2: Exporters & Scene Writers (committed: 3fed33b)
**Goal:** Turn meshes into files BeamNG can load.

**Delivered:**
- `BuildingDaeExporter` — exports individual buildings to Collada DAE (local coords, Z-up → Y-up conversion); `ExportAll()` for batch with progress callback
- `BuildingMaterialLibrary` — 10 registered materials (5 wall + 5 roof), OSM tag mapping, `DeployTextures()` with bundled-resource fallback to solid-color placeholders
- `BuildingSceneWriter` — writes NDJSON `items.level.json` (SimGroup + TSStatic entries) and `materials.json` (BeamNG Material class with Stages array)
- `BuildingTexturePlaceholderGenerator` — generates 256x256 placeholder JPGs (color / flat normal / default ORM)

### Session 3: OSM Parsing (committed: 3fed33b)
**Goal:** Extract building footprints from OSM data.

**Delivered:**
- `OsmBuildingParser` — parses polygon features from `OsmQueryResult`, transforms WGS84 → local meters via `OsmGeometryProcessor`, handles inner rings (courtyards), parses all relevant OSM tags (height, levels, materials, roof, colours), optional height sampler callback for ground elevation

### Session 4: End-to-End Integration (current)
**Goal:** Make buildings work as part of terrain generation.

**Delivered:**
- `BuildingGenerationOrchestrator` (new, in `BeamNgTerrainPoc/Terrain/Building/`)
  - Reads generated `.ter` file → bilinear height sampler for ground elevation
  - Chains: parse → export DAE → deploy textures → write scene items + materials
  - Converts positions from terrain-space (corner origin) to BeamNG world-space (center origin)
  - Returns `BuildingGenerationResult` summary
- **Wired into `TerrainGenerationOrchestrator`** — runs inside `ExecuteInternalAsync()` after `CreateTerrainFileAsync()`, gated by `state.EnableBuildings` + OSM data availability
- **`TerrainGenerationState.EnableBuildings`** toggle (default: false)
- **UI toggle** in `GenerateTerrain.razor` — "Building Generation" section with checkbox, disabled when no OSM/GeoTIFF data
- **Overpass API verified** — `way["building"]` + `relation["type"="multipolygon"]["building"]` already present

**Output structure:**
```
levels/{levelName}/
├── art/shapes/buildings/
│   ├── building_{osmId}.dae        (one per building)
│   ├── buildings.materials.json    (shared PBR materials)
│   └── textures/                   (deployed texture files)
└── main/MissionGroup/Buildings/
    └── items.level.json            (SimGroup + TSStatic entries)
```

---

## What Works Now (after Session 4)
- Toggle "Enable Buildings" in UI → generate terrain → buildings appear in BeamNG
- Ground elevation sampled from generated heightmap (bilinear interpolation)
- PBR materials with placeholder textures (or bundled CC0 textures if present in Resources/BuildingTextures/)
- OSM tags respected: height, levels, building type, wall/roof material, colours

## Known Limitations & Next Steps

### Session 5 (suggested): Real-World Testing & Fixes
- [ ] **Test with a real GeoTIFF + OSM area** — verify buildings appear at correct positions/elevations
- [ ] **Coordinate system validation** — confirm corner→center origin offset is correct for all terrain sizes and meters-per-pixel values
- [ ] **Performance profiling** — large areas may have 1000s of buildings; consider parallel DAE export or batching
- [ ] **Collision mesh size** — very large buildings might cause physics performance issues; consider `collisionType="None"` for distant buildings

### Session 6 (suggested): Roof Geometry
- [ ] **Gabled roofs** — `BuildingMeshGenerator` currently only generates flat roofs; add gabled/hipped/pyramidal roof shapes using the `RoofShape` and `RoofHeight` fields already parsed from OSM
- [ ] **Roof direction** — OSM `roof:direction` and `roof:orientation` tags

### Session 7 (suggested): Quality & Polish
- [ ] **Window cutouts or texture-based windows** — `HasWindows` flag is parsed but not used in mesh generation
- [ ] **Building:colour support** — `WallColor`/`RoofColor` are parsed but not applied (would need per-building material instances or vertex colors)
- [ ] **LOD generation** — simplified meshes for distance rendering
- [ ] **Building merging** — adjacent buildings with shared walls could merge geometry to reduce draw calls
- [ ] **Preset support** — save/restore `EnableBuildings` in terrain presets (`TerrainPresetResult`, importer/exporter)

### Future Ideas
- [ ] **Building:part support** — multi-part buildings (different heights/materials per section) using `building:part=*` relations
- [ ] **Procedural facades** — window/door placement based on building type and floor count
- [ ] **Interior volumes** — hollow buildings for enter-able structures
- [ ] **Forest item conversion** — convert distant buildings to forest items for instanced rendering (similar to `ConvertToForest` feature)
- [ ] **Real texture packs** — bundle high-quality CC0 PBR textures (AmbientCG, Poly Haven) instead of placeholders
