# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**BeamNG Tools for Mapbuilders** is a Windows desktop application for BeamNG.drive modders and mapbuilders. Built with .NET 9, Windows Forms, and Blazor WebView, it provides tools to manage, optimize, and transform BeamNG map files (levels).

Main features: Map Shrinker (remove unused assets), Rename Map, Copy Assets, Copy/Replace Terrain Materials (with groundcover), Convert TSStatic to Forest Items, and Generate Terrain from OSM/GeoTIFF data.

## Build Commands

```bash
# Restore dependencies
dotnet restore

# Build entire solution
dotnet build

# Build main application only
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj

# Publish release (for distribution)
dotnet publish BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj -c Release -r win-x64

# Build installer (Windows only)
build-installer.cmd
```

**Important**: This is a Windows-only application targeting `net9.0-windows10.0.17763.0`. On non-Windows systems for static analysis only:
```bash
dotnet restore /p:EnableWindowsTargeting=true
dotnet build /p:EnableWindowsTargeting=true
```

## Testing

**No automated test suite exists.** Manual testing workflow:
1. Load sample BeamNG map ZIP files (test with vanilla maps like `driver_training`, `west_coast_usa`)
2. Verify file scanning and detection logic
3. Test asset copying between maps
4. Build and verify deployment ZIP files
5. Check proper cleanup of temporary files in `%LocalAppData%\BeamNG_LevelCleanUp\temp`

## Solution Structure

```
BeamNG_LevelCleanUp.sln
├── BeamNG_LevelCleanUp/           # Main Windows Forms + Blazor WebView application
│   ├── BlazorUI/                  # Blazor components and pages (UI layer)
│   │   ├── Pages/                 # Main feature pages (MapShrink, CopyTerrains, GenerateTerrain, etc.)
│   │   ├── Components/            # Reusable UI components
│   │   └── State/                 # Feature-specific state objects (TerrainGenerationState, etc.)
│   ├── Logic/                     # Core map analysis and scanning logic
│   ├── LogicCopyAssets/           # Asset copying orchestration
│   ├── LogicConvertForest/        # TSStatic to forest item conversion
│   ├── LogicCopyForest/           # Forest brush copying
│   ├── Objects/                   # Data models and DTOs
│   ├── Utils/                     # Helper utilities
│   ├── Communication/             # PubSub messaging (Logic → UI)
│   └── Services/                  # Blazor DI services (Viewer3D, TerrainGenerationOrchestrator)
├── Grille.BeamNG.Lib/             # Core BeamNG file format library (JSON/binary serialization)
│   ├── IO/Text/                   # JSON parsers for BeamNG's relaxed JSON dialect
│   ├── IO/Binary/                 # Binary terrain file (.ter) serialization
│   ├── SceneTree/                 # Scene object models (Asset, MaterialJson, etc.)
│   └── Imaging/                   # Image processing utilities
├── BeamNgTerrainPoc/              # Procedural terrain generation library (OSM/GeoTIFF processing)
│   ├── Terrain/                   # Road network analysis, coordinate transformation, heightmap generation
│   └── Examples/                  # Usage examples
├── BeamNG.Procedural3D/           # 3D mesh generation (roads, bridges, tunnels - experimental)
├── etsmaterialgen/                # ETS2/ATS material generation utility
├── meta4downloader/               # Meta4 file download utility
├── ImportTerrainDefaults/         # Terrain defaults import utility
└── Grille.BeamNG.Lib_Tests/       # Test project (minimal coverage)
```

## High-Level Architecture

### UI Framework Integration

**Hybrid Windows Forms + Blazor WebView:**
- `Program.cs`: Async initialization with splash screen → `Form1.cs` (Windows Forms host) initializes services
- Services initialized on startup:
  - `AppPaths.Initialize()` - Sets up centralized temp directory (`%LocalAppData%\BeamNG_LevelCleanUp\temp`)
  - `GameDirectoryService.Initialize()` - Auto-detects BeamNG.drive installation (saved settings or Steam detection)
- Windows Forms provides native window management and folder dialogs
- Blazor WebView hosts the entire UI as web components (MudBlazor v8 for Material Design UI)

### Core Domain Logic Flow

**The application follows a consistent pattern across all features:**

1. **Extract**: Unzip BeamNG level(s) to temp directory (`_unpacked`, `_copyFrom`)
2. **Scan**: Walk directory tree and dispatch to specialized scanners based on file patterns
3. **Analyze**: Build dependency graph of assets/materials/textures
4. **Present**: Display results to user for selection
5. **Execute**: Perform operations (delete, copy, convert)
6. **Package**: Zip modified level back to deployment file

**Key orchestrators:**
- `BeamFileReader` - Main scanning orchestrator for Map Shrinker feature
- `AssetCopy` - Orchestrates terrain/asset copying across LogicCopyAssets/ classes
- `TerrainGenerationOrchestrator` - Coordinates complex terrain generation pipeline

### Dependency Scanning Pattern

**Core architectural pattern: Build complete dependency graph before operations**

```
BeamFileReader.ReadAll() orchestrates scanning:
  ├─ WalkDirectoryTree(pattern, ReadTypeEnum)
  │   ├─ MissionGroupScanner → Scans items.level.json (NDJSON format!)
  │   ├─ MaterialScanner → Scans *.materials.json for texture references
  │   ├─ DaeScanner → Scans .dae XML for material names
  │   ├─ DecalScanner → Scans main.decals.json + managedDecalData
  │   ├─ ForestScanner → Scans *.forest4.json + managedItemData
  │   └─ TerrainScanner → Scans *.terrain.json
  └─ All scanners populate shared static collections:
      BeamFileReader.Assets, .MaterialsJson, .DeleteList
```

**Critical insight**: Materials reference textures, DAE files reference materials, scene objects reference DAE files. Must resolve entire graph before copying/deleting to avoid breaking references.

### File Handling Centralization

**Three-layer abstraction for all file operations:**

1. **AppPaths** - Centralized temp directory management
   - `TempFolder` in `%LocalAppData%\BeamNG_LevelCleanUp\temp`
   - `_unpacked` subfolder for target level extraction
   - `_copyFrom` subfolder for source level extraction
   - Auto-cleanup of stale temp files on startup

2. **ZipFileHandler** - ZIP extraction with encoding detection
   - Tries UTF-8 → CP850 → CP437 → Latin1 (7-Zip creates CP850 archives)
   - Handles conflicting filenames during extraction
   - Creates deployment ZIPs with optimal compression

3. **PathResolver** - Converts BeamNG relative paths to absolute filesystem paths
   - Resolves `/levels/{levelname}/...` to actual extracted directory
   - Used by all scanner and copier classes

### Communication Between Components

**Three-tier state management:**

1. **Page-Local State** (private fields in `.razor.cs`)
   - UI bindings: `_selectedItems`, `_searchString`, `_isLoadingMap`

2. **Dedicated State Objects** (shared within feature)
   - `TerrainGenerationState` - 30+ fields for complex terrain generation form
   - `CreateLevelWizardState` - Multi-page wizard flow tracking
   - These are POCO classes, not Blazor parameters

3. **Static Global State** (BeamFileReader collections)
   - `Assets`, `MaterialsJson`, `CopyAssets` - Shared across scanners
   - **Important**: Requires explicit `Reset()` calls between operations to avoid stale data

**Cross-component messaging: PubSubChannel pattern**

```csharp
// Logic layer sends messages (Info/Warning/Error)
PubSubChannel.SendMessage(PubSubMessageType.Info, "Operation complete");

// Blazor pages consume via async channel reader
while (await PubSubChannel.ch.Reader.WaitToReadAsync())
{
    var msg = await PubSubChannel.ch.Reader.ReadAsync();
    // Add to UI lists and show Snackbar notification
}
```

This enables async communication from long-running Logic operations back to UI without coupling.

### Page ↔ Logic Relationship

**Pattern: Blazor pages are thin orchestrators, Logic folders contain domain logic**

| Page | Logic Entry Point | Relationship |
|------|------------------|--------------|
| `MapShrink.razor` | `BeamFileReader.ReadAll()` | Scans entire level → populates static collections → page displays results |
| `CopyAssets.razor` | `ReadAssetsForCopy()` → `AssetCopy.Copy()` | Orchestrates specialized copiers (roads, decals, DAE, materials) |
| `CopyTerrains.razor` | `ReadTerrainMaterialsForCopy()` | Uses `TerrainMaterialCopier` + `GroundCoverCopier` |
| `CopyForestBrushes.razor` | `ReadForestBrushesForCopy()` | Uses `ForestBrushCopier` |
| `ConvertToForest.razor` | `ConvertToForest()` | Uses `ForestConverter` to generate forest JSON |
| `GenerateTerrain.razor` | `TerrainGenerationOrchestrator` | Delegates to BeamNgTerrainPoc library for OSM/GeoTIFF processing |

Pages manage UI state and user interaction. Logic classes perform file I/O, parsing, and business rules.

## BeamNG File Format Reference

### Critical Implementation Details

**NDJSON Format (Newline Delimited JSON):**
BeamNG uses NDJSON for scene files (`items.level.json`), forest files (`.forest4.json`), and forest brushes (`.forestbrushes4.json`). **This is NOT standard JSON arrays!**

```
Each line is a separate JSON object (no commas, no array brackets):
{"name":"Group1","class":"SimGroup","persistentId":"guid","__parent":"MissionGroup"}
{"class":"TSStatic","persistentId":"guid","__parent":"Group1","position":[0,0,0]}
```

Read with: `foreach (var line in File.ReadAllLines(path)) { var obj = JsonDocument.Parse(line); }`

**Relaxed JSON Dialect:**
BeamNG's `.materials.json` files use non-standard JSON (trailing commas, comments). Use `JsonUtils.GetValidJsonDocumentFromString()` or `JsonRepairUtils` package, not `System.Text.Json` directly.

**Path Conventions:**
- All paths in BeamNG files are forward-slash separated: `/levels/levelname/art/shapes/mesh.dae`
- Absolute paths start with `/levels/`
- Relative paths within level folders don't start with `/`

**Material Naming:**
- Standard materials: `MaterialName` (internalName: `MaterialName`)
- Terrain materials: `MaterialName-{guid}` (internalName: `MaterialName`)
- PersistentId must be unique GUID per material instance

### Level Directory Structure

```
levels/{levelname}/
├── info.json                      # Level metadata (title, spawn points)
├── main.materials.json            # General materials (NOT terrain materials)
├── main.decals.json               # Decal instance data
├── main.forestbrushes4.json       # Forest brush definitions (NDJSON)
├── mainLevel.lua                  # Level initialization script
├── theTerrain.ter                 # Binary terrain heightmap (float height + byte material per pixel)
├── theTerrain.terrain.json        # Terrain config (material list, size, datafile reference)
├── art/
│   ├── terrains/
│   │   ├── main.materials.json    # Terrain materials (class: "TerrainMaterial")
│   │   └── *.png                  # Textures (_b, _nm, _r, _h, _ao suffixes for PBR)
│   ├── shapes/                    # 3D models (.dae Collada, .cdae compiled)
│   ├── decals/                    # Decal textures
│   ├── road/                      # Road materials
│   ├── forest/                    # Forest item definitions
│   └── prefabs/                   # Prefab files (.prefab)
├── forest/
│   └── *.forest4.json             # Forest placement data (NDJSON)
└── main/
    └── MissionGroup/              # Scene hierarchy root
        ├── items.level.json       # Group objects (NDJSON)
        └── {subgroup}/            # Nested groups (DecalRoads, StaticObjects, etc.)
            └── items.level.json
```

### Common Object Classes

- **SimGroup** - Container for organizing objects hierarchically
- **TSStatic** - Static 3D mesh (references `.dae`/`.cdae` via `shapeName` property)
- **Prefab** - Prefab instance (references `.prefab` via `filename` property)
- **DecalRoad** - Road/marking decal with node path array
- **TerrainBlock** - Terrain reference (`terrainFile` points to `.terrain.json`)
- **ForestBrushElement** - Forest brush configuration (part of forest item system)
- **SpawnSphere** - Player spawn point

### Terrain Material Properties (PBR)

Terrain materials use Physically Based Rendering with these texture slots:
- `baseColorDetailTex` - Base color/albedo (_b suffix)
- `normalDetailTex` - Normal map (_nm suffix)
- `roughnessDetailTex` - Roughness map (_r suffix)
- `heightDetailTex` - Height/displacement map (_h suffix)
- `aoDetailTex` - Ambient occlusion map (_ao suffix)
- `groundmodelName` - Physics ground model (e.g., "GROUNDMODEL_ASPHALT1")

**Dynamic property detection**: `TerrainCopyScanner` auto-detects any property ending in "Tex" or "Map" as a texture reference. No code changes needed when BeamNG adds new texture properties.

## Key Implementation Patterns

### Adding New Scanners

When adding support for new BeamNG file types:

1. Create scanner class in `Logic/` folder
2. Implement scanning logic, populate `BeamFileReader.Assets` or `BeamFileReader.MaterialsJson`
3. Add `WalkDirectoryTree()` call in `BeamFileReader.ReadAll()` with appropriate file pattern
4. If needed, add new `ReadTypeEnum` value for the scanner type

Example: `DaeScanner` extracts material names from Collada XML to resolve asset dependencies.

### Adding Material/Asset Copying Features

Follow the orchestrator pattern:

1. Create copier class in `LogicCopyAssets/` (e.g., `TerrainMaterialCopier`)
2. Implement `Copy()` method that:
   - Reads source files
   - Generates new GUIDs for persistent IDs (use `Guid.NewGuid()`)
   - Updates material names if needed (e.g., suffix for "Add Mode")
   - Copies dependency files (textures, meshes)
   - Writes modified JSON to target level
3. Add batch mode support for JSON file writing (accumulate changes, write once)
4. Create corresponding page in `BlazorUI/Pages/` that calls copier

**Critical**: Always maintain the relationship between material names, persistent IDs, and file references when copying.

### Handling JSON Files

```csharp
// For BeamNG's relaxed JSON (comments, trailing commas)
var jsonContent = JsonUtils.GetValidJsonDocumentFromString(fileContent);

// For standard JSON
using var doc = JsonDocument.Parse(fileContent);

// For NDJSON (items.level.json, forest files)
foreach (var line in File.ReadAllLines(path))
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    var obj = JsonDocument.Parse(line);
    // Process object
}
```

### Working with Terrain Files

```csharp
// Read binary terrain data (.ter files)
var terrain = Grille.BeamNG.Lib.IO.Binary.TerrainSerializer.Deserialize(stream);
// terrain.Data[x, y].Height (float)
// terrain.Data[x, y].Material (byte index into terrain.json material list)

// Write terrain data
Grille.BeamNG.Lib.IO.Binary.TerrainSerializer.Serialize(terrain, stream);
```

### Wizard Flow Pattern

Multi-step wizards use shared state objects:

1. Create state class (e.g., `CreateLevelWizardState` in `BlazorUI/State/`)
2. Pages access via static accessor: `CreateLevel.GetWizardState()`
3. Each wizard page updates specific properties and step flags
4. Final page triggers operation using accumulated state
5. Reset state when starting new wizard session

## Common Development Tasks

### Adding a New Page

1. Create `.razor` file in `BlazorUI/Pages/`
2. Add `@page "/RouteName"` directive at top
3. Add navigation entry in `BlazorUI/Components/MyNavMenu.razor`
4. Use MudBlazor v8 components for UI consistency
5. Inject `ISnackbar` for notifications and `IDialogService` for modal dialogs
6. Add PubSubChannel message consumer if calling Logic layer operations

### Debugging File Scanning Issues

Enable verbose logging in scanners:

```csharp
// In scanner classes, use PubSubChannel for debugging
PubSubChannel.SendMessage(PubSubMessageType.Info, $"Scanning file: {filePath}");
```

Check `AppPaths.TempFolder` contents to verify extraction:
```bash
%LocalAppData%\BeamNG_LevelCleanUp\temp\_unpacked\
```

### Testing Material Copying

Use the `CopyTerrains.razor` page with test maps:
1. Load vanilla map as source (e.g., `west_coast_usa`)
2. Load custom map as target
3. Select terrain materials to copy
4. Check output in target map's `art/terrains/main.materials.json`
5. Verify texture files copied to `art/terrains/` folder
6. Check `theTerrain.terrain.json` material list updated

## Known Issues and Workarounds

### ZIP Encoding
Some ZIP files use non-UTF-8 encoding (7-Zip creates CP850). `ZipFileHandler.DetectZipEncoding()` automatically handles fallback through UTF-8 → CP850 → CP437 → Latin1.

### CDAE Files
Compiled DAE files (`.cdae`) cannot be parsed for material extraction. Workaround: Keep original `.dae` files alongside `.cdae` in maps. Tool will use `.dae` if present.

### WebView2 Runtime
Required at runtime but not bundled with installer. Windows 11 and recent Windows 10 have it pre-installed. Users on older systems must install from https://go.microsoft.com/fwlink/p/?LinkId=2124703.

### Cross-Platform Building
Project targets `net9.0-windows10.0.17763.0`. Use `/p:EnableWindowsTargeting=true` for CI on Linux for code analysis only - binary won't run.

### Static Collections State
`BeamFileReader` uses static collections (`Assets`, `MaterialsJson`) for performance. **Always call `BeamFileReader.Reset()` between operations** to clear stale data from previous map scans.

## MudBlazor v8 Patterns

**This project uses MudBlazor v8.** See `ai_agent_md_files_history_some_outdated/copilot-instructions-mudblazor-migration.md` for migration patterns from v6 if working with older code examples.

Common patterns:
```razor
@* File upload *@
<MudFileUpload T="IBrowserFile" FilesChanged="HandleFileUpload">
    <ActivatorContent>
        <MudButton StartIcon="@Icons.Material.Filled.Upload">Upload</MudButton>
    </ActivatorContent>
</MudFileUpload>

@* Data table with selection *@
<MudDataGrid T="MaterialItem" Items="@_materials" MultiSelection="true"
             @bind-SelectedItems="_selectedMaterials">
    <Columns>
        <SelectColumn T="MaterialItem" />
        <PropertyColumn Property="x => x.Name" />
    </Columns>
</MudDataGrid>

@* Snackbar notifications *@
@inject ISnackbar Snackbar
Snackbar.Add("Operation complete", Severity.Success);
```

## Special Features

### Terrain Generation from OSM/GeoTIFF
The `GenerateTerrain.razor` page uses `BeamNgTerrainPoc` library to generate BeamNG levels from real-world data. Key capabilities:

- Download OSM road network data via Overpass API
- Import GeoTIFF heightmap tiles
- Coordinate transformation (WGS84 → local terrain coordinates)
- Road network analysis (junction detection, banking, elevation smoothing)
- Material painting along roads
- Automatic bridge/tunnel elevation handling (experimental)

**Pipeline flow:**
`TerrainGenerationOrchestrator` → Material processing → OSM fetch → Coordinate transform → Heightmap generation → Road creation → Post-processing

### 3D Viewer
The application includes a 3D model viewer using HelixToolkit.Wpf.SharpDX:
- `Viewer3DService` manages viewer windows
- Supports Collada (.dae) model display
- Uses WPF windows hosted in Windows Forms application

### Auto-Update System
Uses `AutoUpdater.NET` package:
- Checks `https://raw.githubusercontent.com/alexkleinwaechter/BeamNG_LevelCleanUp/master/BeamNG_LevelCleanUp/AutoUpdater.xml` on startup
- Prompts user if new version available
- Handles download and installation

## Branch Strategy

Current branch: `feature/bridge_tunnel_splines`
Main development branch: `develop`

When creating PRs, target `develop` branch.

## Implementation Plans and Documentation

The `ai_agent_md_files_history_some_outdated/` directory contains historical implementation plans and design documents. These may be outdated but provide context for design decisions. Notable documents:

- Bridge/tunnel implementation plans
- Road smoothing parameter guides
- Terrain generation pipeline documentation
- UI wizard implementation guides

These files are historical references - verify current implementation before relying on them.
