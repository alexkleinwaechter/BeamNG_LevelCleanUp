# BeamNG Terrain Creator

A .NET 9 library for creating BeamNG.drive terrain files (.ter format, version 9) from heightmap images and material layer masks.

## Quick Start

```csharp
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;

// Create terrain creator
var creator = new TerrainCreator();

// Define materials with optional layer image paths
var parameters = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapPath = "heightmap.png",  // Path to 16-bit grayscale heightmap
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass"),                    // Default material (no image)
        new MaterialDefinition("dirt", "dirt_layer.png"),   // With layer mask
        new MaterialDefinition("rock", "rock_layer.png")    // With layer mask
    },
    TerrainName = "myTerrain" // Optional: specify terrain name
};

// Create terrain file - images loaded and disposed automatically
await creator.CreateTerrainFileAsync("output.ter", parameters);
```

**Key Benefits:**
- ? No ImageSharp dependency in your code
- ? Automatic image loading from file paths
- ? Automatic disposal - no memory leaks
- ? File validation before processing

## Features

? **File-Path Based API**
- Pass file paths instead of Image objects
- Images loaded and disposed internally
- No manual resource management needed

? **Flexible Material System**
- Materials can have optional layer images
- First material serves as default/fallback
- Last material with white pixel wins (priority-based)

? **Comprehensive Validation**
- Power-of-2 size checking
- File existence validation
- Dimension matching
- Material name validation
- Helpful warnings for potential issues

? **Coordinate System Handling**
- Automatic Y-axis flip from ImageSharp to BeamNG
- Bottom-left origin for terrain data

? **Built on Grille.BeamNG.Lib**
- Uses proven binary serialization
- Proper version 9 format support
- No manual binary writing needed

## API Reference

### TerrainCreator

Main class for creating terrain files.

**Methods:**
- `CreateTerrainFileAsync(string outputPath, TerrainCreationParameters parameters)` - Async version
- `CreateTerrainFile(string outputPath, TerrainCreationParameters parameters)` - Sync version

### TerrainCreationParameters

**Properties:**
- `Size` (int) - Terrain size, must be power of 2 (256-16384)
- `MaxHeight` (float) - Maximum terrain height in world units
- `HeightmapPath` (string?) - Path to 16-bit grayscale heightmap PNG file (recommended)
- `HeightmapImage` (Image<L16>?) - Pre-loaded heightmap (advanced scenarios only)
- `Materials` (List<MaterialDefinition>) - Material definitions
- `IncludeLayerTextureData` (bool) - Optional, not used by BeamNG
- `TerrainName` (string) - Optional terrain name (default: "theTerrain")

**Note:** Either `HeightmapPath` or `HeightmapImage` must be provided (not both). Using `HeightmapPath` is recommended.

### MaterialDefinition

**Constructor:**
```csharp
new MaterialDefinition(string materialName, string? layerImagePath = null)
```

**Properties:**
- `MaterialName` (string) - Required material name
- `LayerImagePath` (string?) - Optional path to layer mask PNG (white = present, black = absent)

**Layer Image Format:**
- 8-bit grayscale PNG
- White pixels (255) = material present
- Black pixels (0) = material absent
- Must match terrain size

## Use Cases

### Single Material Terrain
```csharp
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass") // Entire terrain is grass
}
```

### Default + Overlays
```csharp
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass"),                   // Default (no layer image)
    new MaterialDefinition("road", "road_mask.png"),   // Only on roads
    new MaterialDefinition("water", "river_mask.png")  // Only in rivers
}
```

### All Materials with Layer Images
```csharp
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass", "grass_layer.png"),
    new MaterialDefinition("dirt", "dirt_layer.png"),
    new MaterialDefinition("rock", "rock_layer.png")
}
```

### Complex Multi-Material Terrain
```csharp
var parameters = new TerrainCreationParameters
{
    Size = 4096,
    MaxHeight = 500.0f,
    HeightmapPath = @"D:\terrain\heightmap.png",
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", @"D:\terrain\grass_layer.png"),
        new MaterialDefinition("dirt", @"D:\terrain\dirt_layer.png"),
        new MaterialDefinition("rock", @"D:\terrain\rock_layer.png"),
        new MaterialDefinition("sand", @"D:\terrain\sand_layer.png"),
        // ... up to 255 materials
    },
    TerrainName = "myComplexTerrain"
};

await creator.CreateTerrainFileAsync(@"D:\output\terrain.ter", parameters);
```

## Advanced: Using Pre-Loaded Images

For advanced scenarios where you need to manipulate images before creating terrain:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Load and modify image yourself
var heightmap = Image.Load<L16>("heightmap.png");
// ... perform custom operations on heightmap ...

var parameters = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapImage = heightmap,  // Use pre-loaded image
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass")
    }
};

await creator.CreateTerrainFileAsync("terrain.ter", parameters);

// YOU are responsible for disposal when using HeightmapImage
heightmap.Dispose();
```

?? **Note:** When using `HeightmapImage` directly, you must dispose it yourself. Using `HeightmapPath` is recommended for most use cases.

## File Format

The library creates BeamNG terrain files in version 9 format:

```
1. Header (5 bytes) - Version + Size
2. Heightmap Data (Size² × 2 bytes) - 16-bit heights
3. Layer Map (Size² bytes) - Material indices
4. Material Names (variable) - Material count + names
```

## Dependencies

- **SixLabors.ImageSharp** - Used internally for image processing (not required in your code)
- **Grille.BeamNG.Lib** - For terrain serialization

## Performance

| Terrain Size | Expected Time | Memory Usage |
|-------------|---------------|--------------|
| 256×256     | < 0.1s        | < 10 MB      |
| 1024×1024   | < 1s          | < 50 MB      |
| 2048×2048   | < 3s          | < 150 MB     |
| 4096×4096   | < 10s         | < 500 MB     |

## Validation

The validator checks:
- Size is power of 2
- Size is in range (256-16384)
- Heightmap file exists (when using HeightmapPath)
- Heightmap dimensions match size (when using HeightmapImage)
- At least one material defined
- Material names are non-empty
- Layer image files exist (when paths provided)
- Max height is positive

Warnings are shown for:
- Size > 8192 (memory concerns)
- No layer images provided
- Some materials without layer images

## Error Handling

The `CreateTerrainFileAsync` method returns `bool`:
- `true` - Terrain created successfully
- `false` - Validation failed or creation error

Detailed error messages and stack traces are printed to console.

Common errors:
- File not found (heightmap or layer images)
- Invalid terrain size (not power of 2)
- Image size mismatch
- Invalid file format

## Example Output

```
=== BeamNG Terrain Creator ===

--- Creating Terrain with Multiple Materials ---
Loading terrain data from: D:\terrain_data
Terrain name: myTerrain

Found heightmap: D:\terrain_data\heightmap.png
Found 25 layer map files

Adding layer 0: grass
Adding layer 1: dirt
Adding layer 2: rock
...

Total materials: 25

Output path: D:\output\myTerrain.ter

Creating terrain file...
Validating parameters...
Loading heightmap from: D:\terrain_data\heightmap.png
Processing heightmap...
Processing material layers...
Building terrain data structure...
Filling terrain data...
Writing terrain file to D:\output\myTerrain.ter...
Terrain file created successfully!
File size: 50,332,057 bytes
Terrain size: 4096x4096
Max height: 500
Materials: 25

? Terrain with multiple materials created successfully!
  Location: D:\output\myTerrain.ter

Summary:
  Terrain name: myTerrain
  File size: 50,332,057 bytes (48.00 MB)
  Terrain size: 4096x4096
  Max height: 500
  Total materials: 25

Material list:
  [0] ? grass
  [1] ? dirt
  [2] ? rock
  ...
```

## See Also

- **API_USAGE_GUIDE.md** - Comprehensive API documentation with examples
- **BeamNG.drive Terrain Format** - Official format documentation

## License

Part of BeamNG_LevelCleanUp project.
