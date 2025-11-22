# BeamNG Terrain Creator - API Usage Guide

## Overview
The BeamNG Terrain Creator library provides a clean, file-path-based API for creating BeamNG terrain (.ter) files. Images are loaded and managed internally, so you don't need ImageSharp in your calling code.

## Quick Start

### Basic Usage (Recommended)
```csharp
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;

var creator = new TerrainCreator();

var parameters = new TerrainCreationParameters
{
    Size = 4096,
    MaxHeight = 500.0f,
    HeightmapPath = "path/to/heightmap.png",  // ? File path, not Image object
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", "path/to/grass_layer.png"),
        new MaterialDefinition("dirt", "path/to/dirt_layer.png"),
        new MaterialDefinition("rock")  // No layer image = won't auto-place
    }
};

var success = await creator.CreateTerrainFileAsync("output/terrain.ter", parameters);
```

### Key Benefits
? **No ImageSharp dependency** in your code  
? **Automatic image loading** from file paths  
? **Automatic disposal** - no memory leaks  
? **File validation** - checks paths exist before processing  
? **Cleaner API** - less boilerplate code  

## API Reference

### TerrainCreationParameters

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Size` | `int` | Yes | Terrain size (must be power of 2: 256, 512, 1024, 2048, 4096, 8192, 16384) |
| `MaxHeight` | `float` | Yes | Maximum terrain height in world units |
| `HeightmapPath` | `string?` | Yes* | Path to 16-bit grayscale heightmap PNG file |
| `HeightmapImage` | `Image<L16>?` | Yes* | Pre-loaded heightmap (advanced scenarios only) |
| `Materials` | `List<MaterialDefinition>` | Yes | Material definitions (at least one required) |
| `TerrainName` | `string` | No | Terrain name (default: "theTerrain") |

*Either `HeightmapPath` or `HeightmapImage` must be provided (not both).

### MaterialDefinition

```csharp
// Constructor
public MaterialDefinition(string materialName, string? layerImagePath = null)
```

| Property | Type | Description |
|----------|------|-------------|
| `MaterialName` | `string` | Name of the terrain material |
| `LayerImagePath` | `string?` | Optional path to 8-bit grayscale layer mask PNG |

**Layer Image Format:**
- White pixels (255) = material present
- Black pixels (0) = material absent
- Must match terrain size

## Complete Example

```csharp
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;

async Task CreateTerrain()
{
    var creator = new TerrainCreator();
    
    // Define source data paths
    string sourceDir = @"D:\terrain_data";
    string heightmapPath = Path.Combine(sourceDir, "heightmap.png");
    
    // Create material list with layer masks
    var materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", Path.Combine(sourceDir, "grass_layer.png")),
        new MaterialDefinition("dirt", Path.Combine(sourceDir, "dirt_layer.png")),
        new MaterialDefinition("rock", Path.Combine(sourceDir, "rock_layer.png")),
        new MaterialDefinition("sand", Path.Combine(sourceDir, "sand_layer.png"))
    };
    
    // Configure parameters
    var parameters = new TerrainCreationParameters
    {
        Size = 4096,                    // 4096x4096 terrain
        MaxHeight = 500.0f,             // 500 units max elevation
        HeightmapPath = heightmapPath,  // Load from file
        Materials = materials,
        TerrainName = "myTerrain"
    };
    
    // Create terrain file
    string outputPath = @"D:\output\myTerrain.ter";
    var success = await creator.CreateTerrainFileAsync(outputPath, parameters);
    
    if (success)
    {
        Console.WriteLine($"? Terrain created: {outputPath}");
    }
    else
    {
        Console.WriteLine("? Terrain creation failed");
    }
}
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
    Size = 256,
    MaxHeight = 100.0f,
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

?? **Note:** When using `HeightmapImage` directly, you are responsible for image disposal.

## Material Layer Processing

### How Materials Are Applied

Materials are processed in **reverse order** - the **last material has highest priority**:

```csharp
var materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass", "grass_layer.png"),  // Index 0: lowest priority
    new MaterialDefinition("dirt", "dirt_layer.png"),    // Index 1: medium priority
    new MaterialDefinition("rock", "rock_layer.png")     // Index 2: highest priority
};
```

At each terrain point:
1. Check if `rock_layer.png` has white pixel ? use rock
2. If not, check if `dirt_layer.png` has white pixel ? use dirt
3. If not, check if `grass_layer.png` has white pixel ? use grass
4. If no layers have white pixel ? use material 0 (grass) as fallback

### Materials Without Layer Images

```csharp
var materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass"),      // No layer - uses as fallback only
    new MaterialDefinition("dirt", "dirt_layer.png"),
    new MaterialDefinition("rock", "rock_layer.png")
};
```

Materials without layer images:
- Can still be used in the terrain
- Won't be automatically placed
- Index 0 is used as fallback where no other materials are present

## File Formats

### Heightmap
- **Format:** 16-bit grayscale PNG (L16)
- **Size:** Must match terrain size (e.g., 4096x4096)
- **Range:** 0 (black) = lowest, 65535 (white) = MaxHeight

### Layer Masks
- **Format:** 8-bit grayscale PNG (L8)
- **Size:** Must match terrain size
- **Range:** 0 (black) = absent, 255 (white) = present
- **Threshold:** Pixels > 127 are considered "present"

## Validation

The library automatically validates:
- ? Terrain size is power of 2
- ? Terrain size in valid range (256-16384)
- ? Heightmap file exists
- ? Layer image files exist
- ? At least one material defined
- ? All material names non-empty
- ? MaxHeight is positive

Warnings:
- ?? Terrain size > 8192 (memory concerns)
- ?? No layer images provided
- ?? Missing layer images for some materials

## Error Handling

```csharp
var success = await creator.CreateTerrainFileAsync(outputPath, parameters);

if (!success)
{
    // Check console output for detailed error messages
    // Common errors:
    // - File not found
    // - Invalid terrain size
    // - Image size mismatch
    // - Invalid file format
}
```

## Migration from Old API

**Before (ImageSharp in calling code):**
```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

var heightmap = Image.Load<L16>("heightmap.png");
var grassLayer = Image.Load<L8>("grass_layer.png");
var dirtLayer = Image.Load<L8>("dirt_layer.png");

var parameters = new TerrainCreationParameters
{
    Size = 4096,
    MaxHeight = 500.0f,
    HeightmapImage = heightmap,
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", grassLayer),
        new MaterialDefinition("dirt", dirtLayer)
    }
};

await creator.CreateTerrainFileAsync("terrain.ter", parameters);

// Don't forget to dispose!
heightmap.Dispose();
grassLayer.Dispose();
dirtLayer.Dispose();
```

**After (Clean file-based API):**
```csharp
// No ImageSharp using directive needed!

var parameters = new TerrainCreationParameters
{
    Size = 4096,
    MaxHeight = 500.0f,
    HeightmapPath = "heightmap.png",  // Just pass path
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", "grass_layer.png"),
        new MaterialDefinition("dirt", "dirt_layer.png")
    }
};

await creator.CreateTerrainFileAsync("terrain.ter", parameters);

// Disposal handled automatically!
```

## Performance

- **4096x4096 terrain:** ~16.7 million pixels, processes in seconds
- **Memory:** Images loaded and disposed per-operation
- **Thread-safe:** Can run multiple operations in parallel (different instances)

## See Also

- `TERRAIN_CREATOR_IMPLEMENTATION_INSTRUCTIONS.md` - Implementation details
- `README.md` - Feature overview
- BeamNG.drive terrain format documentation
