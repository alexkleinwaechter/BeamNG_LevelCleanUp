# Plan: OSM2World Feature Gap Analysis & Porting Roadmap

## Context

The project has a working building generation pipeline (Sessions 1-4) that produces flat-roofed box buildings from OSM data. OSM2World is a mature open-source Java project with a much richer building system. This plan identifies **what we already have**, **what OSM2World offers**, and **what we can port** — prioritized by visual impact and implementation complexity.

The OSM2World asset library (`OSM2World-default-style`) will be used at `%LocalAppData%\BeamNG_LevelCleanUp\OSM2World-default-style\textures\` when available, with existing placeholder textures as fallback.

---

## Feature Comparison Checklist

### A. Roof Shapes

OSM2World supports 14 roof types. We currently only generate **flat** roofs (all others are parsed from OSM tags but ignored in mesh generation).

| Roof Shape | OSM2World | Ours | Complexity | Visual Impact | Priority |
|---|---|---|---|---|---|
| flat | Yes | **Yes** | - | - | Done |
| gabled | Yes | No | Medium | **High** (very common in residential) | **P1** |
| hipped | Yes | No | Medium | **High** (very common) | **P1** |
| pyramidal | Yes | No | Low | Medium | **P2** |
| skillion | Yes | No | Low | Medium (sheds, modern) | **P2** |
| half-hipped | Yes | No | Medium | Medium | **P3** |
| gambrel | Yes | No | Medium | Low (barns, regional) | **P3** |
| mansard | Yes | No | High | Medium (European cities) | **P3** |
| dome | Yes | No | Medium | Low (rare) | **P4** |
| cone | Yes | No | Medium | Low (towers) | **P4** |
| onion | Yes | No | High | Low (churches, rare) | **P4** |
| round | Yes | No | Medium | Low (cylindrical) | **P4** |
| chimney | Yes | No | Low | Low (special) | **P4** |
| complex | Yes | No | Very High | Low (requires explicit OSM mapping) | Skip |

**Key Java sources for porting:**
- `OSM2World/core/src/main/java/org/osm2world/world/modules/building/roof/Roof.java` — base class + factory
- `GabledRoof.java`, `HippedRoof.java`, `PyramidalRoof.java`, `SkillionRoof.java` etc. — individual types
- Ridge direction logic in `RoofWithRidge.java` — shared by gabled/hipped/gambrel/mansard

### B. Material & Texture System

| Feature | OSM2World | Ours | Gap |
|---|---|---|---|
| PBR textures (Color/Normal/ORM) | Yes | **Yes** | - |
| Texture tiling (meters-per-repeat) | Yes | **Yes** (TextureScaleU/V) | - |
| OSM material tag mapping | Yes (~15 materials) | **Yes** (12 wall + 9 roof) | Phase 1 complete |
| Colorable textures (building:colour tint) | Yes | No | Need vertex color or per-instance material |
| widthPerEntity/heightPerEntity snapping | Yes | No | Integer repeat alignment |
| Texture padding (mipmap bleed prevention) | Yes | No | Nice-to-have |
| STRIP_WALL UV coord function | Yes | **Yes** (walls use cumulative distance) | - |
| STRIP_FIT_HEIGHT UV function | Yes | No | Roof UVs need this for sloped surfaces |
| SLOPED_TRIANGLES UV function | Yes | No | Needed for non-flat roofs |
| GLOBAL_X_Z UV function | Yes | **Partial** (roof/floor use planar XY) | - |
| Multi-layer textures | Yes (up to 32) | No (1 layer) | BeamNG uses single-layer PBR anyway |
| Properties-file material config | Yes | No (hardcoded) | Could load from standard.properties |

**Registered Materials (after Phase 1 implementation):**

Wall materials: BUILDING_DEFAULT (Plaster002), BRICK (Bricks029), CONCRETE (Concrete034), WOOD_WALL (WoodSiding008), GLASS_WALL (Facade005), CORRUGATED_STEEL (CorrugatedSteel005), ADOBE (Ground026), SANDSTONE (Bricks008), STONE (Bricks008), STEEL (Metal002), TILES (Tiles036), MARBLE (Marble001)

Roof materials: ROOF_DEFAULT (RoofingTiles010), ROOF_TILES (RoofingTiles010), ROOF_METAL (MetalPlates006), SLATE (RoofingTiles003), THATCH_ROOF (ThatchedRoof001A), COPPER_ROOF (RoofingTiles010), WOOD_ROOF (Wood026), GLASS_ROOF (Facade005)

**Texture Source Strategy (implemented):**
1. Primary: Load from `%LocalAppData%\BeamNG_LevelCleanUp\OSM2World-default-style\textures\cc0textures\{FolderName}\`
2. Fallback: Existing `Resources/BuildingTextures/` bundled textures
3. Last resort: Placeholder generation (solid colors)

### C. Geometry & Wall Generation

| Feature | OSM2World | Ours | Notes |
|---|---|---|---|
| Simple wall quads | Yes | **Yes** | - |
| Wall holes (courtyards) | Yes | **Yes** | Inner rings generate inward walls |
| Wall splitting at corners | Yes | No | OSM2World splits walls into segments at ~180deg turns |
| Per-wall material/color | Yes | No | All walls share one material |
| Building passages (tunnels) | Yes | No | Rare in practice |
| Degenerate edge filtering | Yes | **Yes** (< 0.01m) | - |

### D. Windows & Doors

| Feature | OSM2World | Ours | Complexity | Priority |
|---|---|---|---|---|
| Window texture on walls | Yes (BUILDING_WINDOWS material) | No | Low | **P2** |
| Per-floor window rows | Yes | No | Medium | P3 |
| Geometry windows (3D frames) | Yes (LOD4) | No | High | P4 |
| Window shapes (circle, semicircle) | Yes | No | High | P4 |
| Hinged doors | Yes | No | Medium | P3 |
| Garage doors | Yes | No | Medium | P3 |
| Explicit mapped windows/doors | Yes (from OSM nodes) | No | High | P4 |

### E. Building Structure

| Feature | OSM2World | Ours | Notes |
|---|---|---|---|
| building:part multi-part | Yes | No | Compose complex buildings from multiple parts |
| min_height / min_level | Yes | **Yes** | Elevated building parts supported |
| Level/height calculation | Yes (full LevelAndHeightData) | **Partial** (simple height x levels) | |
| Roof height from angle | Yes (`roof:angle` -> height) | No | Trigonometric calculation |
| Ridge direction from tags | Yes (`roof:direction`, `roof:orientation`) | No | Needed for gabled/hipped |
| Geometric part detection | Yes (overlap analysis) | No | Auto-detect parts within building |
| Relation-based parts | Yes (type=building relation) | No | |

### F. Advanced / Future Features

| Feature | OSM2World | Ours | Priority |
|---|---|---|---|
| LOD levels (4 detail tiers) | Yes | No | P4 |
| Indoor rendering | Yes | No | Skip (not useful for BeamNG) |
| Roof attachments (solar panels) | Yes | No | P4 |
| Transparent materials (glass) | Yes | No | P3 |
| Double-sided rendering | Yes | No | P3 |
| Color parsing (CSS names + hex) | Yes | **Yes** | Already parsed |
| Smooth normals (interpolation) | Yes | No (flat only) | P3 |

---

## Recommended Implementation Phases

### Phase 1: Texture Library Integration + Missing Materials (DONE)
- [x] Add OSM2World-default-style as texture source (fallback chain)
- [x] Register ~11 new materials (CORRUGATED_STEEL, SLATE, WOOD_ROOF, STONE, SANDSTONE, ADOBE, THATCH_ROOF, STEEL, TILES, MARBLE, COPPER_ROOF)
- [x] Update `BuildingMaterialDefinition` to reference OSM2World texture folder names
- [x] Update `DeployTextures()` to check OSM2World-default-style first
- [x] Update OSM tag mappings for all new materials
- [x] Update `BuildingDefaults` (chimney, parking types)
- [x] Texture scales aligned with OSM2World's standard.properties

### Phase 2: Gabled + Hipped Roofs (Medium effort, highest visual impact)
- Port `RoofWithRidge.java` base class (ridge direction calculation)
- Port `GabledRoof.java` — most common residential roof
- Port `HippedRoof.java` — most common alternative
- Add SLOPED_TRIANGLES UV mapping for non-flat roof surfaces
- Update `BuildingMeshGenerator` with roof dispatch

### Phase 3: More Roof Types + Building:colour
- Port Pyramidal, Skillion (common, simpler shapes)
- Port Half-hipped, Gambrel, Mansard (less common but add variety)
- Implement building:colour tinting (vertex colors or per-building material clones)

### Phase 4: Windows & Polish
- Add BUILDING_WINDOWS texture material for walls-with-windows
- Implement per-floor window texture rows
- Add garage door detection for garage building types

### Phase 5: Building Parts & Advanced
- building:part multi-part building support
- Dome/Cone/Onion roofs
- LOD generation

---

## Key Files to Modify

| Our File | Change |
|---|---|
| `BeamNgTerrainPoc/Terrain/Building/BuildingMaterialLibrary.cs` | Add OSM2World texture source, register new materials |
| `BeamNG.Procedural3D/Building/BuildingMaterialDefinition.cs` | Add TextureFolder property for OSM2World lookup |
| `BeamNG.Procedural3D/Building/BuildingMeshGenerator.cs` | Add roof type dispatch + non-flat roof generators |
| `BeamNG.Procedural3D/Building/BuildingData.cs` | Add RoofDirection, RoofAngle properties |
| `BeamNgTerrainPoc/Terrain/Building/OsmBuildingParser.cs` | Parse roof:direction, roof:angle, roof:orientation tags |
| `BeamNG.Procedural3D/Building/BuildingDefaults.cs` | Add chimney type, update defaults to match OSM2World |

**Key OSM2World Java sources to port from:**
| Java File | What to port |
|---|---|
| `roof/RoofWithRidge.java` | Ridge direction calculation, cap/side polygon logic |
| `roof/GabledRoof.java` | Gabled roof geometry |
| `roof/HippedRoof.java` | Hipped roof geometry |
| `roof/PyramidalRoof.java` | Single-apex roof |
| `roof/SkillionRoof.java` | Sloped-plane roof |
| `roof/FlatRoof.java` | Reference for interface contract |
| `BuildingDefaults.java` | Updated defaults table |
| `BuildingPart.java` | Material selection logic, color application |

## Verification

After Phase 1 (textures):
- Build the project, generate a terrain with buildings enabled
- Verify new material textures appear correctly on buildings
- Test with OSM2World-default-style folder missing -> fallback to placeholders

After Phase 2 (roofs):
- Generate terrain in an area with mapped `roof:shape` tags (European cities like Germany have good coverage)
- Visually verify gabled and hipped roofs render correctly
- Check UV mapping on sloped surfaces (textures should tile naturally, not stretch)
