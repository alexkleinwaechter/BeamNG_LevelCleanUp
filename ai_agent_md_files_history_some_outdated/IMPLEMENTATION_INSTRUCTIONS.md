# ColorExtraction Feature - Implementation Instructions

## Overview

This document provides step-by-step instructions for implementing the **ColorExtraction** feature in the `BeamNgTerrainPoc` library. The feature extracts weighted average colors from BeamNG terrain materials by:

1. Reading layer masks from a `.ter` binary file
2. Applying each mask to its corresponding basecolor texture (PNG)
3. Computing the weighted average RGB color from masked pixels
4. Returning a dictionary mapping material names to hex color strings

## Target Location

```
BeamNgTerrainPoc/
??? Terrain/
    ??? ColorExtraction/
        ??? TerrainColorExtractor.cs      # Main public API
        ??? LayerMaskReader.cs            # Reads .ter file and extracts masks
        ??? MaskedColorCalculator.cs      # Calculates average color from mask + texture
        ??? Models/
            ??? ColorExtractionResult.cs  # Result data models
```

## Dependencies

- **Grille.BeamNG.Lib** (project reference) - For `TerrainV9Serializer` and `TerrainV9Binary`
- **SixLabors.ImageSharp** (already in BeamNgTerrainPoc.csproj) - For PNG texture reading

## Namespace Convention

All files should use namespace: `BeamNgTerrainPoc.Terrain.ColorExtraction`
Models subfolder uses: `BeamNgTerrainPoc.Terrain.ColorExtraction.Models`

---

## Step 1: Create Models/ColorExtractionResult.cs

**Purpose**: Define data models for extraction results.

### File: `BeamNgTerrainPoc/Terrain/ColorExtraction/Models/ColorExtractionResult.cs`

```csharp
namespace BeamNgTerrainPoc.Terrain.ColorExtraction.Models;

/// <summary>
/// Result of color extraction for a single material.
/// </summary>
/// <param name="MaterialName">Name of the terrain material</param>
/// <param name="HexColor">Extracted average color in #RRGGBB format, or null if extraction failed</param>
/// <param name="PixelCount">Number of terrain pixels covered by this material</param>
/// <param name="CoveragePercent">Percentage of total terrain covered by this material (0.0 to 100.0)</param>
public record MaterialColorResult(
    string MaterialName,
    string? HexColor,
    int PixelCount,
    float CoveragePercent
);

/// <summary>
/// Complete result of terrain color extraction operation.
/// </summary>
/// <param name="Colors">Dictionary mapping material name to hex color (#RRGGBB)</param>
/// <param name="TerrainSize">Size of the terrain (width = height, always square)</param>
/// <param name="Details">Detailed results for each material including coverage statistics</param>
public record ColorExtractionSummary(
    Dictionary<string, string> Colors,
    uint TerrainSize,
    IReadOnlyList<MaterialColorResult> Details
);
```

### Key Points:
- Use C# 12 records for immutable data
- `HexColor` is nullable because extraction can fail (missing texture, no coverage)
- `CoveragePercent` helps identify dominant materials

---

## Step 2: Create LayerMaskReader.cs

**Purpose**: Read the `.ter` file and extract per-material layer masks.

### File: `BeamNgTerrainPoc/Terrain/ColorExtraction/LayerMaskReader.cs`

### Required Using Statements:
```csharp
using Grille.BeamNG.IO.Binary;
```

### Class Structure:

```csharp
namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Reads BeamNG terrain (.ter) files and extracts layer masks for each material.
/// </summary>
public static class LayerMaskReader
{
    /// <summary>
    /// Reads layer masks from a terrain .ter file.
    /// Each material gets a boolean mask where true = pixel belongs to this material.
    /// </summary>
    /// <param name="terFilePath">Absolute path to the .ter file</param>
    /// <returns>Dictionary mapping material name to its layer mask (bool array, row-major, Size*Size length)</returns>
    /// <exception cref="FileNotFoundException">If .ter file doesn't exist</exception>
    /// <exception cref="InvalidDataException">If .ter file is invalid</exception>
    public static Dictionary<string, bool[]> ReadLayerMasks(string terFilePath)
    {
        // Implementation steps:
        // 1. Open file stream
        // 2. Deserialize using TerrainV9Serializer.Deserialize(stream)
        // 3. Get MaterialNames array and MaterialData array from binary
        // 4. For each material name, create bool[] where mask[i] = (MaterialData[i] == materialIndex)
        // 5. Skip material index 255 (terrain holes)
        // 6. Return dictionary
    }

    /// <summary>
    /// Gets terrain metadata without loading full mask data.
    /// </summary>
    /// <param name="terFilePath">Path to the .ter file</param>
    /// <returns>Tuple of (terrain size, material names array)</returns>
    public static (uint Size, string[] MaterialNames) ReadTerrainInfo(string terFilePath)
    {
        // Same as ReadLayerMasks but only return metadata
    }
}
```

### Implementation Details:

1. **File Reading Pattern**:
   ```csharp
   using var stream = File.OpenRead(terFilePath);
   var binary = TerrainV9Serializer.Deserialize(stream);
   ```

2. **Material Index Handling**:
   - `binary.MaterialData` is a `byte[]` where each byte is a material index
   - `binary.MaterialNames` is a `string[]` of material names
   - Index into `MaterialNames` using the byte value from `MaterialData`
   - **IMPORTANT**: Material index `255` (0xFF) indicates a terrain hole - skip these pixels

3. **Mask Generation**:
   ```csharp
   var masks = new Dictionary<string, bool[]>();
   for (int matIndex = 0; matIndex < binary.MaterialNames.Length; matIndex++)
   {
       var materialName = binary.MaterialNames[matIndex];
       var mask = new bool[binary.MaterialData.Length];
       for (int i = 0; i < binary.MaterialData.Length; i++)
       {
           mask[i] = binary.MaterialData[i] == matIndex;
       }
       masks[materialName] = mask;
   }
   ```

---

## Step 3: Create MaskedColorCalculator.cs

**Purpose**: Calculate weighted average color from a texture using a layer mask.

### File: `BeamNgTerrainPoc/Terrain/ColorExtraction/MaskedColorCalculator.cs`

### Required Using Statements:
```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
```

### Class Structure:

```csharp
namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Calculates average colors from textures using layer masks.
/// </summary>
public static class MaskedColorCalculator
{
    /// <summary>
    /// Calculates the weighted average color of a texture within the masked area.
    /// </summary>
    /// <param name="texturePath">Path to the basecolor texture PNG</param>
    /// <param name="mask">Boolean mask array (Size*Size, row-major, BeamNG coords: bottom-left origin)</param>
    /// <param name="terrainSize">Size of the terrain (width = height)</param>
    /// <returns>Hex color string (#RRGGBB), or null if no pixels matched or texture not found</returns>
    public static string? CalculateAverageColor(string texturePath, bool[] mask, uint terrainSize)
    {
        // Implementation steps:
        // 1. Check if texture file exists, return null if not
        // 2. Load texture as Rgba32
        // 3. For each terrain pixel where mask[i] == true:
        //    a. Calculate terrain X,Y from index: x = i % size, y = i / size
        //    b. Handle texture tiling: texX = x % textureWidth, texY = y % textureHeight
        //    c. Handle Y-flip: BeamNG uses bottom-left origin, images use top-left
        //       For BeamNG: y=0 is bottom. For image: y=0 is top.
        //       When we sample texture for terrain pixel (x, terrainY):
        //       - terrainY in mask is already in BeamNG coords (0 = bottom)
        //       - Texture y should be flipped: imageY = (textureHeight - 1) - (terrainY % textureHeight)
        //    d. Accumulate R, G, B values
        // 4. Calculate average RGB
        // 5. Convert to hex string
    }

    /// <summary>
    /// Converts RGB byte values to hex color string.
    /// </summary>
    /// <param name="r">Red component (0-255)</param>
    /// <param name="g">Green component (0-255)</param>
    /// <param name="b">Blue component (0-255)</param>
    /// <returns>Hex color in #RRGGBB format</returns>
    public static string ToHexColor(byte r, byte g, byte b)
    {
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>
    /// Counts the number of true values in a mask.
    /// </summary>
    public static int CountMaskedPixels(bool[] mask)
    {
        return mask.Count(m => m);
    }
}
```

### Implementation Details:

1. **Texture Loading**:
   ```csharp
   if (!File.Exists(texturePath))
       return null;
   
   using var image = Image.Load<Rgba32>(texturePath);
   ```

2. **Coordinate Mapping with Scaling (NOT Tiling)**:
   The `baseColorBaseTex` texture may be smaller than the terrain (e.g., 1024x1024 texture for 2048x2048 terrain).
   It is **scaled** to cover the entire terrain, not tiled. Each texture pixel covers multiple terrain pixels.
   
   ```csharp
   int size = (int)terrainSize;
   int textureWidth = image.Width;
   int textureHeight = image.Height;
   
   // Calculate scale factors
   float scaleX = (float)textureWidth / size;
   float scaleY = (float)textureHeight / size;
   
   for (int i = 0; i < mask.Length; i++)
   {
       if (!mask[i]) continue;
       
       int terrainX = i % size;
       int terrainY = i / size;  // BeamNG: 0 = bottom row
       
       // Scale terrain coordinates to texture coordinates
       int texX = (int)(terrainX * scaleX);
       int texY = (int)(terrainY * scaleY);
       
       // Clamp to valid bounds
       texX = Math.Clamp(texX, 0, textureWidth - 1);
       texY = Math.Clamp(texY, 0, textureHeight - 1);
       
       // Flip Y for image coordinate (image: 0 = top, BeamNG: 0 = bottom)
       int imageY = (textureHeight - 1) - texY;
       
       var pixel = image[texX, imageY];
       // Accumulate pixel.R, pixel.G, pixel.B
   }
   ```

3. **Use Double Precision for Accumulation**:
   ```csharp
   double sumR = 0, sumG = 0, sumB = 0;
   long count = 0;
   
   // ... accumulate ...
   
   if (count == 0) return null;
   
   byte avgR = (byte)(sumR / count);
   byte avgG = (byte)(sumG / count);
   byte avgB = (byte)(sumB / count);
   ```

---

## Step 4: Create TerrainColorExtractor.cs

**Purpose**: Main public API that orchestrates the extraction process.

### File: `BeamNgTerrainPoc/Terrain/ColorExtraction/TerrainColorExtractor.cs`

### Required Using Statements:
```csharp
using BeamNgTerrainPoc.Terrain.ColorExtraction.Models;
using BeamNgTerrainPoc.Terrain.Logging;
```

### Class Structure:

```csharp
namespace BeamNgTerrainPoc.Terrain.ColorExtraction;

/// <summary>
/// Extracts weighted average colors from BeamNG terrain materials.
/// Main entry point for the ColorExtraction feature.
/// </summary>
public static class TerrainColorExtractor
{
    /// <summary>
    /// Extracts weighted average colors for terrain materials.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTextures">Dictionary mapping material name to basecolor texture path (PNG)</param>
    /// <returns>Dictionary mapping material name to hex color (#RRGGBB)</returns>
    /// <remarks>
    /// Materials not found in the terrain file are skipped with a warning.
    /// Materials with missing texture files are skipped with a warning.
    /// Materials with 0% coverage return empty string.
    /// </remarks>
    public static Dictionary<string, string> ExtractColors(
        string terFilePath,
        Dictionary<string, string> materialTextures)
    {
        // Implementation:
        // 1. Call ExtractColorsDetailed
        // 2. Return just the Colors dictionary
    }

    /// <summary>
    /// Extracts colors with detailed statistics for each material.
    /// </summary>
    /// <param name="terFilePath">Path to the terrain .ter file</param>
    /// <param name="materialTextures">Dictionary mapping material name to basecolor texture path</param>
    /// <returns>Complete extraction summary with colors and statistics</returns>
    public static ColorExtractionSummary ExtractColorsDetailed(
        string terFilePath,
        Dictionary<string, string> materialTextures)
    {
        // Implementation steps:
        // 1. Validate terFilePath exists
        // 2. Read layer masks using LayerMaskReader.ReadLayerMasks()
        // 3. Get terrain info for size
        // 4. For each entry in materialTextures:
        //    a. Check if material exists in terrain masks
        //    b. If not, log warning and skip
        //    c. Get the mask for this material
        //    d. Count pixels and calculate coverage percent
        //    e. If coverage > 0, calculate average color using MaskedColorCalculator
        //    f. Create MaterialColorResult
        // 5. Build and return ColorExtractionSummary
    }
}
```

### Implementation Details:

1. **Error Handling Pattern**:
   ```csharp
   if (!File.Exists(terFilePath))
   {
       TerrainLogger.Error($"Terrain file not found: {terFilePath}");
       throw new FileNotFoundException("Terrain file not found", terFilePath);
   }
   ```

2. **Material Matching**:
   - Material names in `materialTextures` should match names in the `.ter` file exactly
   - Case-sensitive comparison
   - Log warnings for mismatches but don't throw

3. **Logging**:
   ```csharp
   TerrainLogger.Info($"Extracting colors from terrain: {Path.GetFileName(terFilePath)}");
   TerrainLogger.Info($"Processing {materialTextures.Count} material textures...");
   
   // For each material:
   TerrainLogger.Detail($"  {materialName}: {coveragePercent:F1}% coverage -> {hexColor}");
   
   // Warnings:
   TerrainLogger.Warning($"Material '{materialName}' not found in terrain file");
   TerrainLogger.Warning($"Texture not found for '{materialName}': {texturePath}");
   ```

4. **Coverage Calculation**:
   ```csharp
   int totalPixels = (int)(terrainSize * terrainSize);
   int maskedPixels = MaskedColorCalculator.CountMaskedPixels(mask);
   float coveragePercent = (float)maskedPixels / totalPixels * 100f;
   ```

---

## Step 5: Verification

After implementing all files:

1. **Build the Project**:
   ```bash
   dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj
   ```

2. **Verify No Compilation Errors**

3. **Check Namespace Consistency**:
   - All files in `ColorExtraction/` use `BeamNgTerrainPoc.Terrain.ColorExtraction`
   - Models use `BeamNgTerrainPoc.Terrain.ColorExtraction.Models`

---

## Example Usage

```csharp
using BeamNgTerrainPoc.Terrain.ColorExtraction;

// Simple extraction
// Note: The texture path should point to the baseColorBaseTex property from main.materials.json
var terPath = @"C:\levels\myLevel\theTerrain.ter";
var materialTextures = new Dictionary<string, string>
{
    // Map internal material name -> baseColorBaseTex path
    ["grass"] = @"C:\levels\myLevel\art\terrains\#base_color_#4A7B3A.png",
    ["asphalt"] = @"C:\levels\myLevel\art\terrains\#base_color_#505050.png",
    ["dirt"] = @"C:\levels\myLevel\art\terrains\#base_color_#8B6914.png"
};

var colors = TerrainColorExtractor.ExtractColors(terPath, materialTextures);
// Result: { "grass": "#4A7B3A", "asphalt": "#505050", "dirt": "#8B6914" }

// Detailed extraction with statistics
var summary = TerrainColorExtractor.ExtractColorsDetailed(terPath, materialTextures);
foreach (var detail in summary.Details)
{
    Console.WriteLine($"{detail.MaterialName}: {detail.HexColor} ({detail.CoveragePercent:F1}%)");
}
```

## Integration with BeamNG_LevelCleanUp

When integrating with the CopyTerrains feature, the `TerrainCopyScanner.ExtractTerrainMaterialColors()` method:

1. Finds the `.ter` file in the source level
2. For each terrain material `CopyAsset`:
   - Extracts the `internalName` (matches material names in `.ter` file)
   - Finds the `baseColorBaseTex` property from `MaterialFiles` (MapType == "baseColorBaseTex")
   - Maps `internalName` ? `baseColorBaseTex` file path
3. Calls `TerrainColorExtractor.ExtractColors()` with the mapping
4. Updates each `CopyAsset.BaseColorHex` with the extracted color

**Key Property**: `baseColorBaseTex` - This is the terrain-sized base color texture that gets sampled.

---

## Coordinate System Reference

### BeamNG Terrain (.ter file)
- **Origin**: Bottom-left corner
- **Array Layout**: Row-major, `index = y * size + x`
- **Y=0**: Bottom row of terrain
- **Size**: Always square (Size × Size)

### ImageSharp (PNG textures)
- **Origin**: Top-left corner
- **Y=0**: Top row of image

### Conversion Formula
When sampling texture for terrain pixel at `(terrainX, terrainY)`:
```csharp
// Handle texture tiling
int texX = terrainX % textureWidth;
int texY = terrainY % textureHeight;

// Flip Y axis (BeamNG bottom-up to image top-down)
int imageY = (textureHeight - 1) - texY;

// Sample pixel
var pixel = image[texX, imageY];
```

---

## File Creation Order

Execute in this order to avoid compilation errors:

1. **Create folder structure**: `BeamNgTerrainPoc/Terrain/ColorExtraction/Models/`
2. **Create**: `Models/ColorExtractionResult.cs` (no dependencies)
3. **Create**: `LayerMaskReader.cs` (depends on Grille.BeamNG.Lib)
4. **Create**: `MaskedColorCalculator.cs` (depends on SixLabors.ImageSharp)
5. **Create**: `TerrainColorExtractor.cs` (depends on all above)
6. **Build and verify**

---

## Notes for AI Agent

- Use C# 12 features (records, file-scoped namespaces)
- Target .NET 9
- Follow existing code style in `BeamNgTerrainPoc` project
- Use `TerrainLogger` for all logging (matches existing pattern)
- All public methods should have XML documentation comments
- Use nullable reference types (`string?` where appropriate)
- Handle edge cases gracefully (missing files, empty masks, etc.)
