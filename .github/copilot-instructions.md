# BeamNG Tools for Mapbuilders - Copilot Instructions

## Project Overview

**BeamNG_LevelCleanUp** is a Windows desktop application for BeamNG.drive modders and mapbuilders. It provides tools to manage, optimize, and transform BeamNG map files (levels). The application is built with .NET 9 using Windows Forms and Blazor WebView for the UI.

### Main Features

1. **Map Shrinker** - Removes unused files and assets from BeamNG maps to reduce deployment size
2. **Rename Map** - Renames map files and internal references for map customization
3. **Copy Assets** - Copies decalroads, decals, and Collada (DAE) assets between maps
4. **Copy/Replace Terrain Materials** - Transfers terrain materials with groundcover management between maps
5. **Convert TSStatic to Forest Items** - Converts static mesh placements to more efficient forest items

## Technology Stack

- **Framework**: .NET 9 (net9.0-windows10.0.17763.0)
- **UI**: Windows Forms hosting Blazor WebView
- **Component Library**: MudBlazor v8
- **Target Platform**: Windows only
- **Architecture**: Windows desktop application (not ASP.NET)

## Solution Structure

```
BeamNG_LevelCleanUp.sln
├── BeamNG_LevelCleanUp/           # Main application
│   ├── BlazorUI/                  # Blazor components and pages
│   │   ├── Pages/                 # Main feature pages (MapShrink, CopyTerrains, etc.)
│   │   ├── Components/            # Reusable UI components
│   │   └── State/                 # Application state management
│   ├── Logic/                     # Core scanning and processing logic
│   ├── LogicCopyAssets/           # Asset copying functionality
│   ├── LogicConvertForest/        # Forest item conversion
│   ├── Objects/                   # Data models and DTOs
│   ├── Utils/                     # Helper utilities
│   ├── Communication/             # PubSub messaging system
│   └── wwwroot/                   # Static web assets
├── etsmaterialgen/                # ETS material generation utility
├── meta4downloader/               # Meta4 file download utility
└── ImportTerrainDefaults/         # Terrain defaults import utility
```

## Build Requirements

### Prerequisites
- **Windows OS** - This is a Windows-only application
- **.NET 9 SDK** - Specified in `global.json`
- **Microsoft WebView2 Runtime** - Required for Blazor WebView

### Building on Windows
```bash
dotnet restore
dotnet build
dotnet publish -c Release
```

### Building on Non-Windows (Linux/macOS)
The project cannot be fully built on non-Windows systems because it targets Windows-specific frameworks. However, for static analysis:

```bash
# Enable cross-compilation targeting
dotnet restore /p:EnableWindowsTargeting=true
dotnet build /p:EnableWindowsTargeting=true
```

**Note**: This allows code analysis but won't produce a runnable executable.

## Testing

**Important**: There are currently no automated tests in this repository. The application is tested manually through:
1. Loading sample BeamNG map ZIP files
2. Verifying file scanning and detection
3. Testing asset copying between maps
4. Building deployment ZIP files

When adding new features:
- Test with vanilla BeamNG maps (e.g., `driver_training`, `west_coast_usa`)
- Test with custom/modded maps
- Verify ZIP file handling with various compression levels
- Check for proper cleanup of temporary files

## Code Conventions

### C# Style
- Use implicit usings (enabled in project)
- Use nullable reference types (enabled)
- Use file-scoped namespaces
- Use `var` for local variables where type is obvious
- Follow standard .NET naming conventions

### Blazor Components
- Pages are in `BlazorUI/Pages/` with `.razor` extension
- Use MudBlazor v8 components (see `copilot-instructions-mudblazor-migration.md` in root directory for v6 to v8 migration patterns)
- Use `@inject` for dependency injection
- Handle exceptions with `ErrorBoundary` components
- Use `PubSubChannel` for cross-component messaging

### BeamNG File Handling
- BeamNG levels are distributed as ZIP files
- Level structure: `levels/{levelname}/main/`, `levels/{levelname}/art/`
- Materials are stored in `.materials.json` files
- Scene data is in `.level.json` files under `MissionGroup/`
- Use `ZipFileHandler` for extraction/compression
- Handle encoding issues (UTF-8, CP850, CP437)

### Error Handling
- Use `PubSubChannel.SendMessage()` for user notifications
- Categories: `PubSubMessageType.Info`, `Warning`, `Error`
- Log errors to files with `WriteLogFile()` method
- Always provide cleanup for extracted ZIP contents

## Key Classes and Patterns

### BeamFileReader
Central class for reading and analyzing BeamNG level files:
- Scans for materials, meshes, textures, and scene objects
- Builds dependency graph of assets
- Identifies unused files for deletion

### ZipFileHandler
Handles ZIP file operations with encoding detection:
```csharp
// Extract with automatic encoding detection
var levelPath = ZipFileHandler.ExtractToDirectory(zipPath, "_unpacked");

// Build deployment file
ZipFileHandler.BuildDeploymentFile(path, levelName, CompressionLevel.Optimal);
```

### Asset Copying
Located in `LogicCopyAssets/`:
- `AssetCopy` - Orchestrates copying operations
- `TerrainMaterialCopier` - Copies terrain materials with GUID generation
- `GroundCoverCopier` - Handles groundcover vegetation
- `MaterialCopier` - General material file copying

### PubSub Communication
For async messaging between components. The channel is internal but accessed directly within application code (see `MapShrink.razor` for example usage):
```csharp
// Send message (public method)
PubSubChannel.SendMessage(PubSubMessageType.Info, "Processing...");

// Consume in Blazor component (internal channel access pattern used throughout the app)
while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
{
    var msg = await PubSubChannel.ch.Reader.ReadAsync();
    // Handle message based on msg.MessageType
}
```

## Common Tasks

### Adding a New Page
1. Create `.razor` file in `BlazorUI/Pages/`
2. Add `@page "/RouteName"` directive
3. Add navigation entry in `MyNavMenu.razor`
4. Use MudBlazor components for UI consistency
5. Inject `ISnackbar` and `IDialogService` for notifications

### Adding Material Property Support
When BeamNG adds new material properties:
- Material scanning is dynamic in `TerrainCopyScanner.cs`
- Properties ending with "Tex" or "Map" are auto-detected as textures
- No code changes needed for standard texture properties

### Handling JSON Files
BeamNG uses non-standard JSON (trailing commas, comments):
```csharp
// Use JsonUtils for BeamNG-compatible parsing
var jsonContent = JsonUtils.ReadJsonFromFile(filePath);
```

## Known Issues and Workarounds

### ZIP Encoding Issues
- Some ZIP files use non-UTF-8 encoding (7-Zip creates CP850)
- `ZipFileHandler.DetectZipEncoding()` automatically handles this
- Falls back through: UTF-8 -> CP850 -> CP437 -> Latin1

### CDAE Files
- Compiled DAE files (`.cdae`) cannot be parsed for material extraction
- Workaround: Keep original `.dae` files alongside `.cdae` in maps
- Tool will use `.dae` if present, otherwise report limitation

### WebView2 Runtime
- Required at runtime but not bundled
- Users must install from Microsoft if not present
- Windows 11 and recent Windows 10 have it pre-installed

### Cross-Platform Building
- Project targets `net9.0-windows10.0.17763.0`
- Use `/p:EnableWindowsTargeting=true` for CI on Linux
- Binary won't run but code can be analyzed/linted

## Development Workflow

1. **Clone and open** in Visual Studio 2022+ or VS Code with C# extension
2. **Restore packages**: `dotnet restore`
3. **Build**: `dotnet build`
4. **Run**: Start from IDE (requires Windows) or `dotnet run`
5. **Test manually** with BeamNG map files

## File Format Reference

### Level Directory Structure
```
levels/{levelname}/
├── info.json                      # Level metadata (title, description, spawn points)
├── main.materials.json            # General materials (non-terrain)
├── main.decals.json               # Decal instance data
├── main.forestbrushes4.json       # Forest brush definitions (NDJSON format)
├── mainLevel.lua                  # Level initialization script
├── theTerrain.ter                 # Binary terrain heightmap data
├── theTerrain.terrain.json        # Terrain configuration
├── {levelname}_preview.jpg        # Level preview images
├── {levelname}_minimap.png        # Minimap image
├── art/                           # Art assets
│   ├── terrains/                  # Terrain textures and materials
│   │   ├── main.materials.json    # Terrain materials (TerrainMaterial class)
│   │   └── *.png                  # Texture files (_b, _nm, _r, _h, _ao suffixes)
│   ├── shapes/                    # 3D models (.dae, .cdae)
│   ├── decals/                    # Decal textures
│   ├── road/                      # Road materials
│   ├── forest/                    # Forest item definitions
│   ├── prefabs/                   # Prefab files (.prefab)
│   ├── skies/                     # Skybox textures
│   ├── cubemaps/                  # Environment cubemaps
│   └── water/                     # Water materials
├── forest/                        # Forest item instance data
│   └── *.forest4.json             # Forest placement data (NDJSON format)
├── main/                          # Scene hierarchy
│   └── MissionGroup/              # Root scene group
│       ├── items.level.json       # Group objects (NDJSON format)
│       └── {subgroup}/            # Nested groups (DecalRoads, StaticObjects, etc.)
│           └── items.level.json   # Subgroup objects
└── quickrace/                     # Quick race track definitions (optional)
```

### Level Info File (`info.json`)
```json
{
  "title": "levels.levelname.info.title",
  "description": "levels.levelname.info.description",
  "previews": ["levelname_preview.jpg"],
  "size": [1024, 1024],
  "defaultSpawnPointName": "spawn_default",
  "spawnPoints": [
    {
      "translationId": "levels.levelname.spawnpoints.spawn_name",
      "objectname": "spawn_name",
      "preview": "spawn_preview.jpg"
    }
  ]
}
```

### Materials File (`.materials.json`)
Standard JSON format with material definitions keyed by name:
```json
{
  "MaterialName": {
    "name": "MaterialName",
    "internalName": "MaterialName",
    "class": "Material",
    "persistentId": "guid-here",
    "Stages": [{ "colorMap": "/levels/levelname/texture.png" }]
  }
}
```

### Terrain Materials File (`art/terrains/main.materials.json`)
```json
{
  "MaterialName-{guid}": {
    "name": "MaterialName-{guid}",
    "internalName": "MaterialName",
    "class": "TerrainMaterial",
    "persistentId": "guid-here",
    "baseColorDetailTex": "/levels/levelname/art/terrains/texture_b.png",
    "normalDetailTex": "/levels/levelname/art/terrains/texture_nm.png",
    "roughnessDetailTex": "/levels/levelname/art/terrains/texture_r.png",
    "heightDetailTex": "/levels/levelname/art/terrains/texture_h.png",
    "aoDetailTex": "/levels/levelname/art/terrains/texture_ao.png",
    "groundmodelName": "GROUNDMODEL_ASPHALT1"
  }
}
```

### Terrain Configuration (`theTerrain.terrain.json`)
```json
{
  "datafile": "/levels/levelname/theTerrain.ter",
  "heightmapImage": "/levels/levelname/theTerrain.terrainheightmap.png",
  "size": 1024,
  "materials": ["Grass", "Asphalt", "Dirt"],
  "version": 9
}
```

### Level Files (`items.level.json`) - NDJSON Format
**Important**: These files use Newline Delimited JSON (NDJSON), not standard JSON arrays. Each line is a separate JSON object:
```json
{"name":"GroupName","class":"SimGroup","persistentId":"guid","__parent":"MissionGroup"}
{"class":"TSStatic","persistentId":"guid","__parent":"GroupName","position":[0,0,0],"shapeName":"/levels/levelname/art/shapes/mesh.dae"}
{"class":"DecalRoad","persistentId":"guid","__parent":"DecalRoads","position":[0,0,0],"material":"road_material","nodes":[[x,y,z,width],...]}
{"class":"Prefab","persistentId":"guid","__parent":"StaticObjects","position":[0,0,0],"filename":"/levels/levelname/art/prefabs/item.prefab"}
```

### Forest Files (`.forest4.json`) - NDJSON Format
Individual forest item placements, one JSON object per line:
```json
{"pos":[x,y,z],"rotationMatrix":[9 floats],"scale":1.0,"type":"tree_type_name"}
```

### Forest Brushes (`main.forestbrushes4.json`) - NDJSON Format
Forest brush element definitions:
```json
{"internalName":"brush_name","class":"ForestBrushElement","persistentId":"guid","__parent":"ParentBrush","forestItemData":"item_type","scaleMin":0.8,"scaleMax":1.2}
```

### Decals File (`main.decals.json`)
```json
{
  "header": { "name": "DecalData File", "version": 2 },
  "instances": {
    "decal_material_name": [
      [rectIdx, size, renderPriority, posX, posY, posZ, normX, normY, normZ, tanX, tanY, tanZ, uid]
    ]
  }
}
```

### Common Object Classes
- **SimGroup** - Container for organizing objects hierarchically
- **TSStatic** - Static 3D mesh (references `.dae`/`.cdae` files)
- **Prefab** - Prefab instance (references `.prefab` files)
- **DecalRoad** - Road/marking decal with node path
- **TerrainBlock** - Terrain reference
- **ForestBrushElement** - Forest brush configuration
- **SpawnSphere** - Player spawn point

## Dependencies

Key NuGet packages:
- **MudBlazor** (v8.14.0) - UI component library
- **Blazor3D** - 3D visualization components
- **SixLabors.ImageSharp** - Image processing
- **Pfim** - DDS texture reading
- **JsonRepairUtils** - Handle malformed JSON
- **AutoUpdater.NET** - Auto-update functionality

## Contributing

When making changes:
1. Follow existing code patterns
2. Use MudBlazor v8 syntax (see `copilot-instructions-mudblazor-migration.md` in root directory)
3. Handle exceptions gracefully with user feedback
4. Clean up temporary files and extracted content
5. Document new features in appropriate markdown files
6. Test with multiple map types (vanilla, custom, various sizes)
