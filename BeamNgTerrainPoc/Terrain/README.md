# BeamNG Terrain Creator

A .NET 9 library for creating BeamNG.drive terrain files (.ter format, version 9) from heightmap images and material layer masks.

## Quick Start

```csharp
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Create terrain creator
var creator = new TerrainCreator();

// Load heightmap (16-bit grayscale)
var heightmap = Image.Load<L16>("heightmap.png");

// Define materials with optional layer images
var parameters = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapImage = heightmap,
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass"),  // Default material (no image)
        new MaterialDefinition("dirt", Image.Load<L8>("dirt_layer.png")),
        new MaterialDefinition("rock", Image.Load<L8>("rock_layer.png"))
    }
};

// Create terrain file
await creator.CreateTerrainFileAsync("output.ter", parameters);
```

## Features

? **Flexible Material System**
- Materials can have optional layer images
- First material serves as default/fallback
- Last material with white pixel wins (priority-based)

? **Comprehensive Validation**
- Power-of-2 size checking
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
- `HeightmapImage` (Image<L16>) - 16-bit grayscale heightmap
- `Materials` (List<MaterialDefinition>) - Material definitions
- `IncludeLayerTextureData` (bool) - Optional, not used by BeamNG

### MaterialDefinition

**Constructor:**
```csharp
new MaterialDefinition(string materialName, Image<L8>? layerImage = null)
```

**Properties:**
- `MaterialName` (string) - Required material name
- `LayerImage` (Image<L8>?) - Optional layer mask (white = present, black = absent)

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
    new MaterialDefinition("grass"),           // Default
    new MaterialDefinition("road", roadMask),  // Only on roads
    new MaterialDefinition("water", riverMask) // Only in rivers
}
```

### All Materials with Images
```csharp
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass", grassMask),
    new MaterialDefinition("dirt", dirtMask),
    new MaterialDefinition("rock", rockMask)
}
```

## File Format

The library creates BeamNG terrain files in version 9 format:

```
1. Header (5 bytes) - Version + Size
2. Heightmap Data (Size² × 2 bytes) - 16-bit heights
3. Layer Map (Size² bytes) - Material indices
4. Material Names (variable) - Material count + names
```

## Dependencies

- SixLabors.ImageSharp (for image processing)
- Grille.BeamNG.Lib (for terrain serialization)

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
- Heightmap dimensions match size
- At least one material defined
- Material names are non-empty
- Layer images (if provided) match size
- Max height is positive

Warnings are shown for:
- Size > 8192 (memory concerns)
- No layer images provided
- Some materials without layer images

## Helpers

**MaterialLayerProcessor** provides helper methods:
- `CreateFullCoverageLayer(int size)` - White image for full coverage
- `CreateNoCoverageLayer(int size)` - Black image for no coverage

## Error Handling

The `CreateTerrainFileAsync` method returns `bool`:
- `true` - Terrain created successfully
- `false` - Validation failed or creation error

Detailed error messages and stack traces are printed to console.

## Example Output

```
=== BeamNG Terrain Creator ===

--- Creating Simple Test Terrain ---
Generating test data (256x256)...
Output path: C:\temp\test_terrain.ter

Validating parameters...
  WARNING: No material layer images provided - all terrain will use first material (index 0)
Processing heightmap...
Processing material layers...
Building terrain data structure...
Filling terrain data...
Writing terrain file to C:\temp\test_terrain.ter...
Terrain file created successfully!
File size: 196,623 bytes
Terrain size: 256x256
Max height: 100
Materials: 1

? Test terrain created successfully!
```

## License

Part of BeamNG_LevelCleanUp project.
