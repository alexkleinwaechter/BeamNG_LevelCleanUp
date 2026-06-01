# DecalRoad Generation — Design Specification

## Context

BeamNG Mapping Pro generates terrains with smooth roads from OSM/GeoTIFF data. The current pipeline produces heightmaps with material-painted roads and exports master splines for the BeamNG road editor. However, the generated roads lack visual detail — no road markings, edge lines, or edge blends.

This feature adds automatic DecalRoad generation to produce road markings (center lines, lane dividers), edge lines, and edge blend decals. DecalRoads are BeamNG's textured strip objects projected onto terrain along a spline path.

The core challenge: OSM-derived road splines frequently overlap at junctions. Markings must be interrupted at junction zones to avoid visual artifacts. The road network must be considered holistically, not per-spline in isolation.

## Architecture: Spline-First

Generate DecalRoads directly from `ParameterizedRoadSpline` objects in the `UnifiedRoadNetwork`. Each spline is processed individually with its resolved layer set. Junction zones are detected and markings are interrupted (cut) at junction boundaries.

**Phase 1** (this spec): Simple junction interruption — markings stop at a circular exclusion zone around each junction.
**Phase 2** (future): Contour-based junction edges — compute merged road surface contours for smooth curved edge markings at junctions.

### Data Flow

```
UnifiedRoadNetwork (splines + junctions + cross-sections)
    │
    ├─ JunctionInterruptionRuleBuilder
    │    Build per-spline rules: classify terminating vs continuous,
    │    determine side via dot product of approach × normal
    │
    ├─ DecalRoadLayerSetResolver
    │    Cascade: OSM type override → material fallback → AppData defaults
    │
    ├─ DecalRoadGenerator (per spline × per layer)
    │    1. Resolve layer set for spline
    │    2. Determine lane count (OSM tags → layer set default)
    │    3. Fetch cross-sections via GetCrossSectionsForSpline() (same data as MasterSplineExporter)
    │    4. Get per-spline interruption rules from JunctionInterruptionRuleBuilder
    │    5. For each layer:
    │       a. Expand: IsMirrored → left+right, IsPerLane → per lane boundary
    │       b. Sub-sample cross-sections at desired node spacing
    │       c. Apply lateral offset: centerPoint + normalDirection × position × 0.5 × roadWidth
    │       d. Get Z elevation from cross-section TargetElevation (smoothed), heightmap fallback
    │       e. Transform to BeamNG world coords (BeamNgCoordinateTransformer)
    │       f. Apply junction-aware interruption (terminating: cut all, continuous: cut matching-side edge line only)
    │       g. Chunk into ≤100-node segments (BeamNG limit)
    │       h. Create DecalRoad objects
    │
    └─ DecalRoadSceneWriter
         Write NDJSON to MT_decalroads/ hierarchy
```

## Data Models

### DecalRoadLayerDefinition

A single decorative or functional layer (e.g., "center line", "left edge blend", "AI navigation road"):

| Property | Type | Description |
|----------|------|-------------|
| Name | string | Display name ("Center Line", "Left Edge Blend") |
| LayerType | enum | CenterLine, LaneMarking, EdgeLine, EdgeBlend, TreadMarks, AIRoad, Custom |
| IsEnabled | bool | Toggle on/off without deleting |
| Material | string | BeamNG material name ("m_line_white_discontinue") |
| Width | float | Width in meters (0.2m for line, 2.0m for edge blend) |
| TextureLength | float | Texture repeat distance (default 10.0m) |
| RenderPriority | int | Draw order, higher = on top (default 10) |
| Position | float | Lateral position: -1.0 = left edge, 0.0 = center, +1.0 = right edge |
| IsTrackWidth | bool | If true, width scales with road width |
| IsMirrored | bool | Auto-generate symmetric layer on opposite side |
| IsPerLane | bool | Replicate between each lane boundary |
| FadeIn | float | Fade at road start (meters) |
| FadeOut | float | Fade at road end (meters) |
| DistanceFade | float[2] | Camera distance fade [start, end] |
| InterruptAtJunctions | bool | Cut this layer at junction zones (default true for edges/markings) |
| Drivability | float | BeamNG drivability value. -1.0 = non-drivable (visual only), 1.0 = fully drivable (AI navigation). Only relevant for AIRoad layers. |
| LanesLeft | int | Number of left-direction lanes for AI pathfinding (default 1). Only relevant for AIRoad layers. |
| LanesRight | int | Number of right-direction lanes for AI pathfinding (default 1). Only relevant for AIRoad layers. |
| OneWay | bool | Whether the AI road is one-way (default false). Only relevant for AIRoad layers. |
| FlipDirection | bool | Reverse the road direction for AI (default false). Only relevant for AIRoad layers. |

### DecalRoadLayerSet

A named collection of layers for a road type:

| Property | Type | Description |
|----------|------|-------------|
| Name | string | "Highway Default", "Residential Road", "Dirt Track" |
| IsEnabled | bool | Master toggle for entire set |
| DefaultLaneCount | int | Fallback when OSM lanes tag missing |
| DefaultLaneWidth | float | Standard lane width assumption (default 3.5m) |
| Layers | List\<DecalRoadLayerDefinition\> | Ordered list of layers |

### Override Resolution Cascade

For each `ParameterizedRoadSpline`:

1. Check `spline.OsmRoadType` → look up in **project preset's `osmLayerSets`** → if found: **use it**
2. Else: check `spline.MaterialName` → look up in **project preset's `materialLayerSets`** → if found: **use it**
3. Else: check `spline.OsmRoadType` → look up in **AppData `decalroad-defaults.json`** → if found: **use it**
4. Else: **no DecalRoads** generated for this spline

## Lane Count Determination

**Data flow**: `OsmFeature.Tags["lanes"]` → carried through path assembly → stored on `RoadSpline` → propagated to `ParameterizedRoadSpline.OsmTags` during `UnifiedRoadNetworkBuilder`. The `OsmTags` dictionary on `ParameterizedRoadSpline` is a subset of the source feature's tags relevant to road rendering (lanes, surface, oneway, etc.), populated when the spline is created from OSM data. For PNG-sourced splines, `OsmTags` is null.

**Resolution order**:
1. Check `spline.OsmTags?["lanes"]` — parsed as int from the existing `OsmFeature.Tags` dictionary (convenience property `OsmFeature.Lanes` added as computed getter over `Tags`)
2. If missing or spline is PNG-sourced, use `DecalRoadLayerSet.DefaultLaneCount`
3. Default lane counts per OSM road type (in AppData defaults):
   - motorway: 4, trunk: 4, primary: 2, secondary: 2
   - tertiary: 2, unclassified: 2, residential: 2, service: 1, track: 1
   - path/footway/cycleway: 1

**Road width for lateral offset**: Uses `spline.Parameters.EffectiveMasterSplineWidthMeters` (cascade: MasterSplineWidth → RoadSurfaceWidth → RoadWidth). MasterSplineWidth is intentionally narrower than RoadSurfaceWidth to account for terrain material dither resolution (~1m/px). DecalRoad edge blends are designed to visually improve the dither area at road edges. The layer set's `DefaultLaneWidth` is only used for lane count estimation when OSM data is missing.

**Centerline alignment**: DecalRoadGenerator uses the same unified cross-section data as MasterSplineExporter (`network.GetCrossSectionsForSpline()`). This ensures DecalRoad centerlines exactly match the exported master splines. Cross-section `CenterPoint` and `NormalDirection` provide position and lateral offset direction; `TargetElevation` (smoothed/harmonized by the unified pipeline) provides Z elevation with raw heightmap fallback. This approach replaced the original raw `spline.Spline.SampleByDistance()` sampling which produced path deviations on curves due to elevation mismatches affecting BeamNG's `improvedSpline` 3D interpolation.

Lane marking positions are calculated as:
```
For N total lanes, lane boundaries at positions:
  boundary[i] = -1.0 + (2.0 * i / N)  for i in 1..N-1
```

**Bridge/tunnel handling**: Splines with `IsBridge = true` or `IsTunnel = true` are skipped for DecalRoad generation by default. DecalRoads project onto terrain surface, which would produce incorrect results on elevated structures. A per-layer-set `IncludeStructures` flag can be added later if needed.

## Junction Interruption (Phase 1) — Simple Circular Exclusion

**Current implementation**: Uses simple circular exclusion zones centered on junction positions. All interrupt-eligible layers (`InterruptAtJunctions = true`) are removed uniformly within the exclusion radius for all roads.

**Exclusion zone** per junction:
- Center: `junction.Position`
- Radius: `max(contributing road widths) / 2 + junctionExclusionMarginMeters`
- Default margin: 5.0m (configurable)

**Interruption logic** per positioned layer:
1. Walk the sampled node sequence
2. For each node, check if it falls inside any junction exclusion zone
3. Split the node sequence into continuous segments (outside zones)
4. Each segment becomes a separate DecalRoad
5. Segments shorter than a minimum length (e.g., 3 nodes) are discarded

Layers with `InterruptAtJunctions = false` skip this step (edge blends, AI roads).

### TODO: Junction-Aware Interruption (Phase 1b)

> **Status**: Not yet implemented. An initial attempt was made but rolled back due to unresolved issues. The problems below need to be solved before re-implementing.

**Goal**: Replace uniform circular exclusion with per-spline, per-junction rules that distinguish terminating vs continuous roads:
1. **Terminating roads**: all visual layers (markings, edge blends) should stop at the main road's edge
2. **Continuous roads**: only the edge line and edge blend on the side where the terminating road connects should have a gap; opposite side remains intact

**Known problems from first implementation attempt**:

1. **Side determination is unreliable**: Dot-product of approach direction with cross-section normal gives wrong side in many geometries — curved roads, junctions with acute angles, splines with varying direction. A proximity-based approach (checking if lateral offset moves node closer to or farther from junction) was tried but still produced incorrect results on some junction types.

2. **Edge blends from terminating roads overlap onto main road**: When `InterruptAtJunctions = false` is removed to let the rule system handle everything, edge blends that should be preserved on continuous roads also get cut. The rule system needs a reliable way to distinguish "this edge blend is on the junction side" from "this edge blend is on the opposite side".

3. **Cutback radius calculation**: The radius needs to account for both the continuous road's width AND the terminating road's width (edge blends extend at position ~1.1 beyond the road edge), especially at angled junctions.

4. **Junction centroid offset**: The junction `Position` (centroid of all contributors) can be significantly offset from the continuous road's centerline, distorting distance-based checks.

**Possible approaches for future implementation**:
- Road-surface mask/polygon: build 2D polygons of each road's surface area and check overlap geometrically
- Per-node overlap check: for each node of road A, check if it falls within road B's width corridor
- Improved side detection using the actual offset node positions relative to all nearby road splines

## AI Road Layer (NPC Traffic Navigation)

The `AIRoad` layer type generates invisible DecalRoad objects using the `road_invisible` material. These roads are used by BeamNG's AI traffic system for NPC vehicle pathfinding. They follow the road centerline (Position = 0.0) and use the full road width.

**Key properties** for AI road DecalRoad entries:
- `material`: `"road_invisible"` — invisible texture, no visual impact
- `drivability`: `1.0` — marks road as fully drivable for AI
- `lanesLeft`: derived from lane count (default 1)
- `lanesRight`: derived from lane count (default 1)
- `oneWay`: from OSM `oneway` tag or layer definition (default false)
- `flipDirection`: `false` (default)
- `gatedRoad`: `false` (default)
- `autoJunction`: `true` — lets BeamNG auto-detect junction connections
- `useSubdivisions`: `true` — enables smooth AI path interpolation

**Lane assignment for AI roads**:
- For 2-lane roads: `lanesLeft=1, lanesRight=1`
- For 4-lane roads: `lanesLeft=2, lanesRight=2`
- For one-way roads (OSM `oneway=yes`): all lanes on one side (`lanesLeft=0, lanesRight=N`)
- General formula: `lanesLeft = ceil(N/2), lanesRight = floor(N/2)` unless one-way

**AI roads are NOT interrupted at junctions** (`InterruptAtJunctions = false`). BeamNG's `autoJunction` handles junction connections for AI pathfinding.

**Width**: AI roads use the full road width (`Position = 0.0`, `IsTrackWidth = true`) so the navigable area matches the terrain-smoothed road surface.

**Default inclusion**: AI road layers are included by default for all paved road types (motorway through residential). Excluded for track/path/footway by default.

## File Output Structure

DecalRoads are written to the BeamNG level's MissionGroup hierarchy:

```
main/MissionGroup/
├── items.level.json              ← add SimGroup "MT_decalroads" entry
└── MT_decalroads/
    ├── items.level.json          ← SimGroup entries for each spline
    ├── Asphalt_001/
    │   └── items.level.json      ← DecalRoad NDJSON lines for this spline's layers
    ├── Asphalt_002/
    │   └── items.level.json
    ├── Primary_003/
    │   └── items.level.json
    └── ...
```

Each spline gets its own SimGroup subfolder named like the spline (e.g., `Asphalt_001`, `Primary_003`). All DecalRoad layers for that spline are written as NDJSON lines in that subfolder's `items.level.json`.

**SimGroup entry** (in parent's items.level.json):
```json
{"class":"SimGroup","persistentId":"<guid>","__parent":"MissionGroup","name":"MT_decalroads"}
```

**Per-spline SimGroup** (in MT_decalroads/items.level.json):
```json
{"class":"SimGroup","persistentId":"<guid>","__parent":"MT_decalroads","name":"Asphalt_001"}
```

**DecalRoad entry — visual layer** (in spline subfolder's items.level.json):
```json
{"class":"DecalRoad","persistentId":"<guid>","__parent":"Asphalt_001","name":"Asphalt_001_EdgeLine_L_001","material":"m_line_white","textureLength":10.0,"breakAngle":3.0,"renderPriority":10,"startEndFade":[0,0],"distanceFade":[1000,1500],"drivability":-1.0,"improvedSpline":true,"position":[x,y,z],"nodes":[[x,y,z,width],...]}
```

**DecalRoad entry — AI road layer** (additional pathfinding properties):
```json
{"class":"DecalRoad","persistentId":"<guid>","__parent":"Asphalt_001","name":"Asphalt_001_AIRoad_C_001","material":"road_invisible","textureLength":10.0,"breakAngle":3.0,"renderPriority":1,"startEndFade":[0,0],"distanceFade":[1000,1500],"drivability":1.0,"improvedSpline":true,"autoLanes":true,"lanesLeft":1,"lanesRight":1,"oneWay":false,"flipDirection":false,"gatedRoad":false,"autoJunction":true,"useSubdivisions":true,"position":[x,y,z],"nodes":[[x,y,z,width],...]}
```

**Naming convention**: `{SplineName}_{LayerName}_{Side}_{ChunkIndex:D3}`
- Side: `L` (left), `R` (right), `C` (center), or omitted for non-mirrored
- Example: `Asphalt_001_EdgeLine_L_001`, `Primary_003_LaneMarking_C_001`

**Writer**: `DecalRoadSceneWriter` follows `BuildingSceneWriter` pattern, using `SimItemsJsonSerializer.Save()` for NDJSON.

## Preset Integration

New `decalRoadSettings` section added to existing terrain preset `_appSettings`:

```json
{
  "_appSettings": {
    "version": "3.0",
    "...existing settings...",
    "decalRoadSettings": {
      "enabled": true,
      "nodeSpacingMeters": 2.0,
      "junctionExclusionMarginMeters": 5.0,
      "materialLayerSets": {
        "Asphalt": { "name": "Asphalt", "isEnabled": true, "defaultLaneCount": 2, "defaultLaneWidth": 3.5, "layers": [...] },
        "DirtRoad": { "...": "..." }
      },
      "osmLayerSets": {
        "motorway": { "...": "..." },
        "primary": { "...": "..." },
        "residential": { "...": "..." }
      }
    }
  }
}
```

**Backward compatibility**: Presets without `decalRoadSettings` default to disabled. Version bumped to 3.0. Import logic handles missing section gracefully.

## AppData Default Layer Sets

**Location**: `%LocalAppData%\BeamNG_LevelCleanUp\decalroad-defaults.json`

Created on first run if missing. Contains default `DecalRoadLayerSet` definitions per OSM road type. Users can edit this file to change global defaults. Project presets override these.

If the file is corrupted or deleted, the app recreates it from hardcoded fallback constants.

**Default layer sets** (shipped):

| OSM Type | Lanes | Layers |
|----------|-------|--------|
| motorway | 4 | Edge lines, lane markings (dashed), edge blend 1+2, AI road |
| trunk | 4 | Edge lines, lane markings (dashed), edge blend 1+2, AI road |
| primary | 2 | Edge lines, center dashed, edge blend 1+2, AI road |
| secondary | 2 | Edge lines, center dashed, edge blend 1, AI road |
| tertiary | 2 | Edge lines only, AI road |
| unclassified | 2 | Edge lines only, AI road (same as tertiary) |
| residential | 2 | Minimal (edge blend only), AI road |
| service | 1 | Edge blend only |
| track | 1 | Dirt edge blend only |
| path/footway | — | Disabled by default |

## UI Components

### 1. DecalRoad summary in TerrainMaterialSettings

Compact inline section within each material's expandable panel (below road smoothing settings):
- Checkbox: "Enable DecalRoad Layers"
- Summary chip: "4 layers active"
- Compact layer list (name + material preview)
- "Edit Layers..." button opens the full editor dialog

### 2. DecalRoadLayerSetEditor (MudDialog)

Full editor dialog for a single `DecalRoadLayerSet`:
- Lane count and lane width fields at top
- Drag-drop reorderable list of layers
- Each layer row: enable toggle, name, material, width, position, flags (mirror/per-lane/interrupt)
- Add/remove layer buttons
- "Load Default" button to reset from AppData defaults
- Layer type presets (quick-add: "Add Edge Lines", "Add Lane Markings", etc.)

### 3. DecalRoadOsmOverrides (panel in GenerateTerrain.razor)

Separate panel below Materials section (only shown when OSM source is used):
- Grid of cards, one per OSM road type present in the current terrain
- Each card shows: road type name, lane count, layer count, customization status
- Click card → opens DecalRoadLayerSetEditor for that road type
- "Load All Defaults" button resets all to AppData defaults
- Dimmed cards = using AppData defaults, green = project-customized

## Pipeline Integration

### Automatic generation (during terrain generation)

In `TerrainGenerationOrchestrator`, after master spline export:
1. Check if DecalRoad generation is enabled
2. Pass `UnifiedRoadNetwork`, heightmap, and resolved layer sets to `DecalRoadGenerator`
3. Write output via `DecalRoadSceneWriter`

### Standalone re-generation

"Re-generate DecalRoads" button in GenerateTerrain.razor:
- Available after terrain generation has completed (network data cached)
- Allows changing layer set configurations and regenerating DecalRoads without re-running the full terrain pipeline
- Clears previous DecalRoad output (deletes `MT_decalroads/` folder) before writing new

**Caching**: The `UnifiedRoadNetwork` and finalized heightmap (`float[,]`) are cached on `GenerateTerrain.razor.cs` as private fields after terrain generation completes. These are held in memory for the session. If the user navigates away from the page and returns, the cache is lost and the button is disabled until terrain is regenerated. The `TerrainGenerationState` object already survives page lifetime, so we store the cached references there.

## New Files

| File | Location | Purpose |
|------|----------|---------|
| DecalRoadLayerDefinition.cs | BeamNgTerrainPoc/Terrain/Models/DecalRoad/ | Single layer data model |
| DecalRoadLayerSet.cs | BeamNgTerrainPoc/Terrain/Models/DecalRoad/ | Layer set collection model |
| JunctionInterruptionRule.cs | BeamNgTerrainPoc/Terrain/Models/DecalRoad/ | Per-spline junction rule record + InterruptionSide enum |
| DecalRoadLayerSetResolver.cs | BeamNgTerrainPoc/Terrain/Services/DecalRoad/ | Override cascade resolution |
| DecalRoadGenerator.cs | BeamNgTerrainPoc/Terrain/Services/DecalRoad/ | Core generation engine |
| JunctionInterrupter.cs | BeamNgTerrainPoc/Terrain/Services/DecalRoad/ | Rule-based junction-aware segment splitting |
| JunctionInterruptionRuleBuilder.cs | BeamNgTerrainPoc/Terrain/Services/DecalRoad/ | Builds per-spline rules from network junctions |
| DecalRoadSceneWriter.cs | BeamNgTerrainPoc/Terrain/Services/DecalRoad/ | NDJSON scene file writer |
| DecalRoadDefaultsManager.cs | BeamNG_LevelCleanUp/Utils/ | AppData default JSON management |
| DecalRoadLayerSetEditor.razor | BlazorUI/Components/ | Layer set editor dialog |
| DecalRoadOsmOverrides.razor | BlazorUI/Components/ | OSM override panel |

## Modified Files

| File | Changes |
|------|---------|
| TerrainGenerationOrchestrator.cs | Add DecalRoad generation step after master spline export |
| TerrainMaterialSettings.razor | Add DecalRoad layer summary + edit button |
| TerrainMaterialItemExtended | Add DecalRoad layer set properties |
| TerrainPresetExporter.razor | Export decalRoadSettings to preset JSON |
| TerrainPresetImporter.razor | Import decalRoadSettings from preset JSON |
| TerrainPresetResult.cs | Add DecalRoad settings properties |
| GenerateTerrain.razor | Add OSM override panel, re-generate button, enable checkbox |
| GenerateTerrain.razor.cs | Add DecalRoad state, cached network, re-generation logic |
| AppPaths.cs | Add DecalRoadDefaultsPath property |
| OsmFeature.cs | Add computed `Lanes` property (getter that parses existing `Tags["lanes"]`) |
| ParameterizedRoadSpline.cs | Add `OsmTags` dictionary (populated from source OsmFeature.Tags during network construction) |
| UnifiedRoadNetworkBuilder or equivalent | Propagate OsmFeature.Tags subset to ParameterizedRoadSpline.OsmTags |

## Reused Infrastructure

| What | Where | How Used |
|------|-------|----------|
| BeamNgCoordinateTransformer.TerrainToWorld() | BeamNgTerrainPoc/Terrain/Utils/ | Terrain→world coordinate conversion |
| RoadSpline.SampleByDistance() | BeamNgTerrainPoc/Terrain/Models/ | Spline centerline sampling |
| RoadSpline.GetNormalAtDistance() | BeamNgTerrainPoc/Terrain/Models/ | Binormal for lateral offset |
| SimItemsJsonSerializer.Save() | Grille.BeamNG.Lib/IO/Text/ | NDJSON file writing |
| BuildingSceneWriter pattern | BeamNgTerrainPoc/Terrain/Building/ | SimGroup hierarchy creation |
| UnifiedRoadNetwork.Junctions | BeamNgTerrainPoc/Terrain/Models/ | Junction detection data |
| NetworkJunction.GetContinuousRoads/GetTerminatingRoads | BeamNgTerrainPoc/Terrain/Models/ | Road role classification at junctions |
| JunctionContributor.IsContinuous/IsEndpoint | BeamNgTerrainPoc/Terrain/Models/ | Terminating vs continuous road detection |
| UnifiedCrossSection.NormalDirection | BeamNgTerrainPoc/Terrain/Models/ | Side determination via dot product |
| MasterSplineExporter sampling | BeamNgTerrainPoc/Terrain/Services/ | Heightmap elevation lookup pattern |

## Verification

1. **Unit**: Test DecalRoadLayerSetResolver cascade with mock data (OSM override, material fallback, AppData default, no match)
2. **Unit**: Test JunctionInterrupter splits node sequences correctly at exclusion zones
3. **Unit**: Test lane position calculation for various lane counts
4. **Integration**: Generate terrain with OSM data, verify DecalRoad NDJSON is valid JSON per line
5. **Integration**: Load generated level in BeamNG editor, verify DecalRoads appear on roads
6. **Integration**: Verify junction exclusion — markings should stop near intersections
7. **Preset round-trip**: Export preset with DecalRoad settings, reimport, verify settings preserved
8. **AppData defaults**: Delete defaults file, restart app, verify it's recreated
9. **Backward compat**: Load v2.0 preset (no decalRoadSettings), verify DecalRoads disabled gracefully
