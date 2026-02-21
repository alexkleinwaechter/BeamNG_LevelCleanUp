# OSM2World Style Configuration System — Implementation Plan

## Context

The building material system in `BuildingMaterialLibrary.RegisterDefaultMaterials()` is entirely hard-coded. OSM2World uses a `standard.properties` file with locale includes that users can customize. We need a similar externalized JSON configuration system so that:

1. Material definitions are user-editable without recompiling
2. ALL OSM2World properties are preserved (materials, settings, models, traffic signs, locales)
3. BeamNG-specific properties extend the OSM2World data
4. JSON files are generated automatically when the OSM2World style package is downloaded
5. `BuildingMaterialLibrary` loads from JSON when available, falls back to hard-coded defaults

**Bugs/gaps found:**
1. **ROOF_METAL**: Uses `MetalPlates006` which does NOT exist in the OSM2World cc0textures package. Fix to `CorrugatedSteel005`.
2. **Facade materials use solid-color placeholders** instead of the real textures from the style package:
   - `DOOR_DEFAULT` (=OSM2World `ENTRANCE_DEFAULT`): should use `textures/DE19F1FreisingDoor00005_small.png` (locale-specific single file)
   - `DOOR_GARAGE` (=OSM2World `GARAGE_DOOR`): should use `textures/DE20F1GarageDoor00001.jpg` (locale-specific, colorable)
   - `WINDOW_SINGLE` (=OSM2World `SINGLE_WINDOW`): should use `textures/custom/Window/` (PBR: Window_Color/Normal/Displacement/ORM.png)
   - `WINDOW_GLASS` (=OSM2World `GLASS`): should use `textures/custom/Glass/` (PBR: Glass_Color/Normal/ORM/Metalness/Roughness)
   - `WINDOW_FRAME`: stays solid-color (OSM2World uses `PLASTIC` which is also flat WHITE)
3. **Missing material**: `BUILDING_WINDOWS` (facade-wide window texture band for LOD1) not in our library. Uses `textures/custom/Windows/` (PBR: Windows_Color/Normal/Displacement/ORM.png)
4. **Missing material**: `GLASS_TRANSPARENT` (transparent glass variant with `Glass_Transparent_Color.png` alternate)
5. **Texture path types**: Three different texture source patterns need handling:
   - **cc0textures dirs**: `./textures/cc0textures/FolderName/` (PBR maps with consistent `_Color/_Normal/_ORM` naming)
   - **custom dirs**: `./textures/custom/Name/` (PBR maps with `Name_Color/Normal/ORM` naming)
   - **single files**: `./textures/SomeFile.png` or `./textures/SomeFile.jpg` (standalone textures, often locale-specific)

---

## Output Files (generated at runtime)

```
%LocalAppData%\BeamNG_LevelCleanUp\
├── osm2world-style.json                      # Main config (~60 materials + settings + models)
└── osm2world-style-locales/
    ├── DE-defaults.json                      # DE: drivingSide, sign mappings, 4 sign materials
    ├── DE-trafficSigns.json                  # DE: ~677 sign materials, sign defaults
    ├── PL-defaults.json                      # PL: drivingSide, sign mappings, 4 sign materials
    └── PL-trafficSigns.json                  # PL: ~291 sign materials
```

---

## New Files (all in `BeamNgTerrainPoc/Terrain/StyleConfig/`)

### Step 1: Model Classes

**1a. `StyleTextureLayer.cs`** — One texture layer within a material:
```
Properties: Index, Dir, File, ColorFile, Width, Height, WidthPerEntity, HeightPerEntity,
CoordFunction, Colorable, Wrap, Padding, Transparency, Color, Type, Text, Font,
RelativeFontSize, TextColor
```

**1b. `BeamNgMaterialExtension.cs`** — Our BeamNG-specific additions:
```
Properties: MaterialName, IsRoofMaterial, InstanceDiffuse, DefaultColor (float[3]),
Opacity, DoubleSided, ColorMapFile, NormalMapFile, OrmMapFile
```

**1c. `StyleMaterial.cs`** — One material definition:
```
Properties: Color (#hex), Transparency (TRUE/BINARY), DoubleSided (bool),
TextureLayers (List<StyleTextureLayer>), Beamng (BeamNgMaterialExtension?)
```

**1d. `StyleGlobalSettings.cs`** — All global settings from standard.properties:
```
Properties: Locale, CreateTerrain, BackgroundColor, ExportAlpha, TreesPerSquareMeter,
UseBuildingColors, UseBillboards, RenderUnderground, ForceUnbufferedPNGRendering,
CanvasLimit, Msaa, DrivingSide
```

**1e. `Osm2WorldStyleConfig.cs`** — Root model for `osm2world-style.json`:
```
Properties: Schema, Version, GeneratedFrom, GeneratedAt,
Settings (StyleGlobalSettings), Materials (Dict<string, StyleMaterial>),
Models (Dict<string, List<string>>)
```

**1f. `Osm2WorldLocaleConfig.cs`** — Root model for locale defaults JSON:
```
Properties: Schema, Version, Locale, GeneratedFrom,
Settings (Dict<string,string>), TrafficSignMappings (Dict<string,string>),
Materials (Dict<string, StyleMaterial>)
```

**1g. `Osm2WorldTrafficSignsConfig.cs`** — Root model for traffic signs JSON:
```
Properties: Schema, Version, Locale, GeneratedFrom,
TrafficSignDefaults (Dict<string,float>),
TrafficSignProperties (Dict<string, Dict<string,object>>),
Materials (Dict<string, StyleMaterial>)
```

### Step 2: Properties Parser

**2. `Osm2WorldPropertiesParser.cs`** — Parses `.properties` files into structured data.

Key challenge: material names contain underscores (e.g., `ROAD_MARKING_ARROW_THROUGH_RIGHT`).

**Regex strategy** (two-pass, matched from right):
```
Pass 1: ^material_(.+)_(texture(\d+)_(.+))$   → texture layer properties
Pass 2: ^material_(.+)_(color|transparency|doubleSided)$  → material-level properties
Pass 3: ^trafficSign_(.+)_(material|numPosts|defaultHeight)$  → sign mappings
Pass 4: ^model_(.+)$  → model definitions
Else:   global settings
```

The greedy `(.+)` captures the longest possible material name before the known suffix anchor `_texture\d+_`.

Output: `PropertiesParseResult` with GlobalSettings, Materials (as `ParsedMaterial` dict), Models, TrafficSignMappings, IncludeDirectives.

Each `ParsedMaterial` has: Color, Transparency, DoubleSided, TextureLayers (Dict<int, Dict<string,string>>).

**Critical**: Use `CultureInfo.InvariantCulture` for float parsing (`.` decimal separator).

### Step 3: Generator

**3. `StyleConfigGenerator.cs`** — Orchestrates: parse → enhance → write JSON.

```csharp
public StyleConfigGenerationResult Generate()
{
    // 1. Parse standard.properties from downloaded OSM2World package
    // 2. Convert ParsedMaterial → StyleMaterial for all ~60 materials
    // 3. Apply BeamNG extensions to known materials (28 building materials)
    // 4. Write osm2world-style.json
    // 5. Parse each locale file (DE-defaults, DE-trafficSigns, PL-defaults, PL-trafficSigns)
    // 6. Write locale JSON files
}
```

Contains static `BeamNgExtensions` dictionary mapping 28 material keys to `BeamNgMaterialExtension` objects. This is the externalized version of `RegisterDefaultMaterials()`.

**JSON serialization**: `PropertyNamingPolicy = CamelCase`, `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull` (keeps traffic sign JSONs compact).

Returns `StyleConfigGenerationResult` with: Success, ErrorMessage, MainMaterialCount, TrafficSignMaterialCount, etc.

### Step 4: Loader

**4. `StyleConfigLoader.cs`** — Reads JSON at runtime for `BuildingMaterialLibrary`.

```csharp
public static Osm2WorldStyleConfig? LoadMainConfig(string configPath)
public static BuildingMaterialDefinition? ToMaterialDefinition(string key, StyleMaterial mat)
public static List<BuildingMaterialDefinition> LoadBuildingMaterials(string configPath)
```

`ToMaterialDefinition` converts JSON material → `BuildingMaterialDefinition`:
- Derives `TextureFolder` from first texture layer's `dir` (e.g., `./textures/cc0textures/Bricks029` → `"Bricks029"`)
- Derives `TextureScaleU/V` from first texture layer's `width/height`
- Maps `BeamNgMaterialExtension` properties → `BuildingMaterialDefinition` properties

---

## Modified Files

### Step 5: `BuildingMaterialLibrary.cs`
Path: `BeamNgTerrainPoc/Terrain/Building/BuildingMaterialLibrary.cs`

**Changes:**
1. Constructor: try `StyleConfigLoader.LoadMainConfig()` first, iterate materials with BeamNG extensions
2. Add `GetStyleConfigPath()` → `%LocalAppData%\BeamNG_LevelCleanUp\osm2world-style.json`
3. Add `Osm2WorldToInternalKeyMap` to remap OSM2World keys (SINGLE_WINDOW→WINDOW_SINGLE, etc.)
4. Change `Register()` calls in `RegisterDefaultMaterials()` to `RegisterIfMissing()` using `_materials.TryAdd()` (JSON-loaded materials take precedence, hard-coded fill gaps)
5. Fix `ROOF_METAL` texture: `MetalPlates006` → `CorrugatedSteel005` (in both hard-coded defaults AND the BeamNG extension mapping)
6. **Update facade materials** to use real textures from style package:
   - `WINDOW_SINGLE`: `TextureFolder = "Window"`, ColorMapFile = `Window_Color.color.png`, NormalMapFile = `Window_Normal.normal.png`, OrmMapFile = `Window_ORM.data.png`
   - `WINDOW_GLASS`: `TextureFolder = "Glass"`, ColorMapFile = `Glass_Color.color.png`, NormalMapFile = `Glass_Normal.normal.png`, OrmMapFile = `Glass_ORM.data.png`
   - `DOOR_DEFAULT`: `TextureFolder = null`, ColorMapFile = `DE19F1FreisingDoor00005_small.color.png` (single file, converted from source)
   - `DOOR_GARAGE`: `TextureFolder = null`, ColorMapFile = `DE20F1GarageDoor00001.color.png` (single file, converted from source)
   - `WINDOW_FRAME`: stays as-is (solid-color placeholder, OSM2World PLASTIC is also flat WHITE)
7. **Add new materials** to `RegisterDefaultMaterials()`:
   - `BUILDING_WINDOWS`: TextureFolder = "Windows", Windows_Color/Normal/ORM, DefaultColor = (255,230,140)
   - `GLASS_TRANSPARENT`: TextureFolder = "Glass", same textures as WINDOW_GLASS but transparency=TRUE, doubleSided=true
8. **Extend `FindFileInDirs`** to also search `textures/custom/{textureFolder}/` path (currently only searches flat and `textures/cc0textures/{textureFolder}/`)
9. **Handle single-file textures** in `FindFileInDirs`: for files without a TextureFolder, search directly in `textures/` subdirectory of source dirs
10. **Update `AddFacadeMaterials`**: include BUILDING_WINDOWS and GLASS_TRANSPARENT in used materials when buildings have windows

### Step 6: `AppPaths.cs`
Path: `BeamNG_LevelCleanUp/Utils/AppPaths.cs`

**Add:**
```csharp
public static string StyleConfigFile => Path.Combine(AppDataFolder, "osm2world-style.json");
public static string StyleLocalesFolder => Path.Combine(AppDataFolder, "osm2world-style-locales");
```

### Step 7: `Osm2WorldStyleDownloadDialog.razor`
Path: `BeamNG_LevelCleanUp/BlazorUI/Components/Osm2WorldStyleDownloadDialog.razor`

**After ZIP extraction succeeds** (before `_downloadComplete = true`):
1. Update status: "Generating style configuration..."
2. `await Task.Run(() => new StyleConfigGenerator(targetFolder, AppPaths.SettingsFolder).Generate())`
3. Log result (non-fatal if generation fails — textures still available)

---

## BeamNG Extension Mapping (28 materials — was 26, adding BUILDING_WINDOWS + GLASS_TRANSPARENT)

### Wall Materials (12)
| Material Key | TextureFolder | MaterialName | DefaultColor | Notes |
|---|---|---|---|---|
| BUILDING_DEFAULT | Plaster002 | mtb_plaster | 220,210,190 | instanceDiffuse=true |
| BRICK | Bricks029 | mtb_brick | 165,85,60 | |
| CONCRETE | Concrete034 | mtb_concrete | 170,170,165 | |
| WOOD_WALL | WoodSiding008 | mtb_wood_wall | 160,120,75 | |
| GLASS_WALL | Facade005 | mtb_glass_wall | 230,230,230 | |
| CORRUGATED_STEEL | CorrugatedSteel005 | mtb_corrugated_steel | 160,165,170 | |
| ADOBE | Ground026 | mtb_adobe | 180,150,110 | |
| SANDSTONE | Bricks008 | mtb_sandstone | 210,190,150 | |
| STONE | Bricks008 | mtb_stone | 160,160,155 | |
| STEEL | Metal002 | mtb_steel | 180,185,190 | |
| TILES | Tiles036 | mtb_tiles | 200,200,195 | |
| MARBLE | Marble001 | mtb_marble | 230,230,225 | |

### Roof Materials (8)
| Material Key | TextureFolder | MaterialName | DefaultColor | Notes |
|---|---|---|---|---|
| ROOF_DEFAULT | RoofingTiles010 | mtb_roof_tiles | 140,55,40 | |
| ROOF_TILES | RoofingTiles010 | mtb_roof_tiles | 140,55,40 | |
| ROOF_METAL | **CorrugatedSteel005** | mtb_roof_metal | 120,125,130 | **BUG FIX: was MetalPlates006** |
| SLATE | RoofingTiles003 | mtb_slate | 90,95,100 | |
| THATCH_ROOF | ThatchedRoof001A | mtb_thatch | 170,150,90 | |
| COPPER_ROOF | RoofingTiles010 | mtb_copper_roof | 195,219,185 | |
| WOOD_ROOF | Wood026 | mtb_wood_roof | 160,120,75 | |
| GLASS_ROOF | Facade005 | mtb_glass_roof | 230,230,230 | |

### Facade Materials (7 — was 5, adding BUILDING_WINDOWS + GLASS_TRANSPARENT)

**Key insight**: OSM2World `standard.properties` defines REAL textures for these. Our mapping to OSM2World names:
- Our `DOOR_DEFAULT` = OSM2World `ENTRANCE_DEFAULT`
- Our `DOOR_GARAGE` = OSM2World `GARAGE_DOOR`
- Our `WINDOW_SINGLE` = OSM2World `SINGLE_WINDOW`
- Our `WINDOW_GLASS` = OSM2World `GLASS` (opaque) / `GLASS_TRANSPARENT` (transparent variant)
- Our `WINDOW_FRAME` = OSM2World `PLASTIC` (stays flat WHITE, no texture in style package)

| Material Key | OSM2World Name | MaterialName | TextureSource | DefaultColor | Notes |
|---|---|---|---|---|---|
| WINDOW_SINGLE | SINGLE_WINDOW | mtb_window_single | `custom/Window/` | 255,255,255 | **NEW: use PBR textures** |
| WINDOW_FRAME | PLASTIC | mtb_window_frame | *(none — solid color)* | 255,255,255 | stays placeholder |
| WINDOW_GLASS | GLASS | mtb_window_glass | `custom/Glass/` | 230,230,230 | **NEW: use PBR textures**, opacity=0.5, doubleSided |
| DOOR_DEFAULT | ENTRANCE_DEFAULT | mtb_door_default | `DE19F1FreisingDoor00005_small.png` | 51,0,0 | **single file, locale-specific** |
| DOOR_GARAGE | GARAGE_DOOR | mtb_door_garage | `DE20F1GarageDoor00001.jpg` | 255,255,255 | **single file, locale-specific, colorable** |
| BUILDING_WINDOWS | BUILDING_WINDOWS | mtb_building_windows | `custom/Windows/` | 255,230,140 | **NEW MATERIAL** — facade window band (LOD1) |
| GLASS_TRANSPARENT | GLASS_TRANSPARENT | mtb_glass_transparent | `custom/Glass/` | 230,230,230 | **NEW MATERIAL** — transparent=TRUE, doubleSided |

**Texture deployment changes in `BuildingMaterialLibrary`:**
- `FindFileInDirs` now searches: flat, `textures/cc0textures/{folder}/`, `textures/custom/{folder}/`, and `textures/{fileName}` (for single-file textures)
- `Osm2WorldToInternalKeyMap` remaps OSM2World keys to internal keys at JSON load time

---

## JSON Schema Examples

### osm2world-style.json (main config)
```json
{
  "schema": "osm2world-style-schema-v1",
  "version": 1,
  "generatedFrom": "standard.properties",
  "generatedAt": "2026-02-21T15:30:00Z",
  "settings": {
    "locale": "DE",
    "createTerrain": true,
    "backgroundColor": "#000000",
    "treesPerSquareMeter": 0.02,
    "useBuildingColors": true,
    "useBillboards": true
  },
  "materials": {
    "BRICK": {
      "textureLayers": [{
        "index": 0,
        "dir": "./textures/cc0textures/Bricks029",
        "width": 1.4,
        "height": 1.4,
        "heightPerEntity": 0.1
      }],
      "beamng": {
        "materialName": "mtb_brick",
        "isRoofMaterial": false,
        "defaultColor": [165, 85, 60],
        "colorMapFile": "Bricks029_Color.color.png",
        "normalMapFile": "Bricks029_Normal.normal.png",
        "ormMapFile": "Bricks029_ORM.data.png"
      }
    },
    "CHAIN_LINK_FENCE": {
      "transparency": "TRUE",
      "doubleSided": true,
      "textureLayers": [{
        "index": 0,
        "dir": "./textures/cc0textures/Fence006",
        "width": 1.0,
        "height": 1.0
      }]
    }
  },
  "models": {
    "CAR": ["./models/car/car.gltf", "./models/car/car_hatchback.gltf"]
  }
}
```

### DE-defaults.json (locale)
```json
{
  "schema": "osm2world-locale-schema-v1",
  "locale": "DE",
  "settings": { "drivingSide": "right" },
  "trafficSignMappings": {
    "SIGN_CITY_LIMIT": "SIGN_DE_310",
    "SIGN_STOP": "SIGN_DE_206"
  },
  "materials": {
    "SIGN_MAXWIDTH": {
      "transparency": "BINARY",
      "textureLayers": [
        { "index": 0, "file": "./textures/signs-DE/regulation_signs/264.png", "width": 0.84, "height": 0.84 },
        { "index": 1, "type": "text", "text": "%{maxwidth}m", "font": "DIN 1451 Mittelschrift,PLAIN" }
      ]
    }
  }
}
```

### DE-trafficSigns.json (traffic signs catalogue)
```json
{
  "schema": "osm2world-trafficsigns-schema-v1",
  "locale": "DE",
  "trafficSignDefaults": { "defaultTrafficSignHeight": 2.6, "standardPoleRadius": 0.038 },
  "trafficSignProperties": { "SIGN_DE_600_30": { "numPosts": "2", "defaultHeight": "0.5" } },
  "materials": {
    "SIGN_DE_101": {
      "transparency": "BINARY",
      "textureLayers": [{ "index": 0, "file": "./textures/signs-DE/danger_signs/101.svg", "width": 0.9, "height": 0.78 }]
    }
  }
}
```

---

## Implementation Order

```
Step 1: Model classes (1a-1g)         — 7 new files, no dependencies
Step 2: Properties parser             — 1 new file, depends on Step 1
Step 3: Generator                     — 1 new file, depends on Steps 1+2
Step 4: Loader                        — 1 new file, depends on Step 1
Step 5: BuildingMaterialLibrary mod   — depends on Step 4
Step 6: AppPaths mod                  — independent
Step 7: Download dialog mod           — depends on Steps 3+6
Step 8: Copy plan to ai_docs/        — save this plan as ai_docs/osm2world_style_config_plan.md
```

Total: **10 new files**, **3 modified files**, **5 generated JSON files** at runtime, **28 BeamNG-enhanced materials** (was 26).

---

## Verification

1. **Build**: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj` and full solution build
2. **Manual test**: Place the OSM2World-default-style folder in `%LocalAppData%\BeamNG_LevelCleanUp\`, run the generator, verify JSON output matches standard.properties content
3. **Spot-check**: Verify BRICK material in JSON has `dir: ./textures/cc0textures/Bricks029`, width 1.4, height 1.4, and beamng section with `mtb_brick`
4. **Traffic signs**: Verify DE-trafficSigns.json contains ~677 material entries, each with correct transparency/texture properties
5. **Loader**: Verify `BuildingMaterialLibrary` loads from JSON when present, falls back to hard-coded when absent
6. **ROOF_METAL fix**: Verify the generated JSON uses `CorrugatedSteel005` not `MetalPlates006`
7. **Download flow**: Use the Osm2WorldStyleDownloadDialog, verify JSON files are generated after extraction
8. **Facade textures**: Verify WINDOW_SINGLE has `dir: ./textures/custom/Window`, DOOR_DEFAULT has `file: ./textures/DE19F1FreisingDoor00005_small.png`
9. **New materials**: Verify BUILDING_WINDOWS and GLASS_TRANSPARENT appear in generated JSON with BeamNG extensions
10. **Custom texture path**: Verify `FindFileInDirs` resolves `textures/custom/Window/Window_Color.png` correctly from style package
