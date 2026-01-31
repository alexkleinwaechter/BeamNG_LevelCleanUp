# BeamNG Terrain (.ter) File Creator - Implementation Instructions

## Project Summary
Create a tool to generate BeamNG.drive terrain files (*.ter format, version 9) within the **BeamNgTerrainPoc** project, which can later be referenced in the main **BeamNG_LevelCleanUp** application.

**Target C# Version**: 13.0  
**Target .NET Version**: .NET 9

---

## Core Requirements

### Input Parameters
The terrain creation method should accept:

1. **Terrain Size** (int): Power of 2 values (256, 512, 1024, 2048, 4096, 8192, 16384)
   - Must be square dimensions
   - Validated as power of 2

2. **Maximum Height** (float): Maximum terrain height in world units
   - Used to scale the 16-bit heightmap values (0-65535) to actual world heights

3. **Heightmap Image**: 16-bit grayscale PNG image
   - ImageSharp pixel format: `L16`
   - Dimensions must match terrain size
   - Values 0-65535 represent height from 0 to maxHeight

4. **Material Definitions** (List<MaterialDefinition>): Ordered list of terrain materials
   - Each material has:
     - **MaterialName** (string, required): Name of the terrain material
     - **LayerImage** (Image<L8>, optional): 8-bit grayscale PNG mask
       - White pixels (255) = material is present
       - Black pixels (0) = material is absent
       - If null, material can still be used but won't have automatic placement
   - **Order is critical!** Index in list = material index in layer map
   - Examples: 
     ```csharp
     new MaterialDefinition("grass", grassLayerImage),
     new MaterialDefinition("dirt", dirtLayerImage),
     new MaterialDefinition("rock", null) // No layer image, use manually or as fallback
     ```

### Output
- Binary `.ter` file following BeamNG version 9 format specification
- Compatible with BeamNG.drive terrain system

### Important Notes on Material Layer Images

**Material layer images are OPTIONAL** because:
- The binary format only stores material indices (0-255), not images
- Images are only used to automatically determine which material goes where
- You can have materials without images and assign indices manually
- Materials without images can serve as fallback materials (index 0 default)

**Use cases for materials without layer images**:
1. **Default/fallback material**: First material (index 0) used where no other material is defined
2. **Manual assignment**: Programmatically assign material indices without using image masks
3. **Placeholder materials**: Define material names for future use without placement data
4. **Simplified workflows**: Single-material terrains don't need layer images at all

**Example scenarios**:

```csharp
// Scenario 1: Simple single-material terrain
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass") // Entire terrain is grass
}

// Scenario 2: Default material + specific placements
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass"),           // Default - used everywhere else
    new MaterialDefinition("road", roadMask),  // Only on roads
    new MaterialDefinition("water", riverMask) // Only in rivers
}

// Scenario 3: All materials defined with images
Materials = new List<MaterialDefinition>
{
    new MaterialDefinition("grass", grassMask),
    new MaterialDefinition("dirt", dirtMask),
    new MaterialDefinition("rock", rockMask)
}
```

---

## BeamNG .ter File Format (Version 9) - Binary Specification

### File Structure (Sequential)
```
1. Header (5 bytes)
2. Heightmap Data (Width × Height × 2 bytes)
3. Layer Map / Material Indices (Width × Height bytes)
4. Layer Texture Data (Width × Height bytes) - OPTIONAL, can be zeros
5. Material Names (Variable length)
```

### Detailed Section Breakdown

#### 1. Header (5 bytes)
- **Byte 0**: Version number (unsigned byte) = `0x09` for version 9
- **Bytes 1-4**: Terrain size (unsigned 32-bit integer, little-endian)
  - Example: 1024 ? `00 04 00 00`

#### 2. Heightmap Data (Width × Height × 2 bytes)
- Array of unsigned 16-bit integers (ushort), little-endian
- Range: 0 to 65535
- Ordering: **Row-major from bottom-left to top-right**
  - Start at bottom-left corner (0,0)
  - Read left to right across row
  - Move up to next row
  - End at top-right corner
- Conversion formula:
  ```csharp
  ushort u16height = (ushort)(heightValue / maxHeight * 65535f);
  ```

#### 3. Layer Map / Material Indices (Width × Height bytes)
- Array of unsigned bytes
- Each byte = material index (0 to MaterialCount-1)
- Index references position in Material Names list
- Value `255 (0xFF)` = hole in terrain (special case)
- Default value when no material layer matches: `0`

#### 4. Layer Texture Data (Width × Height bytes) - OPTIONAL
- Array of unsigned bytes
- Currently **not used by BeamNG.drive**
- Can be:
  - All zeros (recommended)
  - Omitted entirely (size = 0)
- Reserved for future advanced blending functionality

#### 5. Material Names (Variable length)
- **Material Count** (4 bytes): Unsigned 32-bit integer, little-endian
- **For each material**:
  - **Length byte** (1 byte): String length (no null terminator)
  - **Name bytes**: ASCII characters

Example (2 materials: "grass", "dirt"):
```
02 00 00 00     // 2 materials
05              // Length of "grass"
67 72 61 73 73  // ASCII "grass"
04              // Length of "dirt"
64 69 72 74     // ASCII "dirt"
```

### File Size Calculation
```
Total Size = 5 (Header)
           + (Size² × 2) (Heightmap)
           + (Size²) (Layer Map)
           + (Size²) (Layer Texture, if included)
           + 4 (Material Count)
           + ?(1 + MaterialNameLength) for each material
```

Example (1024×1024, 3 materials "grass"(5), "dirt"(4), "rock"(4)):
```
Total = 5 + (1048576×2) + 1048576 + 1048576 + 4 + (1+5)+(1+4)+(1+4)
      = 5 + 2097152 + 1048576 + 1048576 + 4 + 6 + 5 + 5
      = 4,196,329 bytes
```

---

## Available Resources in Solution

### 1. **Grille.BeamNG.Lib** (PRIMARY DEPENDENCY)
**Location**: `Grille.BeamNG.Lib` project

**Key Classes**:
- `Terrain` - Main terrain representation class
- `TerrainSerializer` - Binary serialization router
- `TerrainV9Serializer` - Version 9 specific binary writer/reader
- `TerrainDataBuffer` - Holds terrain data (height + material per pixel)
- `TerrainData` struct - Single terrain point data

**Critical Implementation Details**:
```csharp
// From TerrainV9Serializer.cs - This is how to write a .ter file
public static void Serialize(Stream stream, Terrain terrain, float maxHeight)
{
    using var bw = new BinaryViewWriter(stream);
    
    bw.WriteByte(9); // Version
    bw.WriteUInt32((uint)terrain.Size); // Size
    
    // Write heightmap
    for (int i = 0; i < data.Length; i++)
    {
        var u16height = GetU16Height(data[i].Height, maxHeight);
        bw.WriteUInt16(u16height);
    }
    
    // Write layer map
    for (int i = 0; i < data.Length; i++)
    {
        var material = data[i].IsHole ? byte.MaxValue : (byte)data[i].Material;
        bw.WriteByte(material);
    }
    
    // Write material names
    bw.WriteMaterialNames(terrain.MaterialNames);
}

// Height conversion
public static ushort GetU16Height(float height, float maxHeight)
{
    float u16max = ushort.MaxValue;
    float u16height = height / maxHeight * u16max;
    if (u16height > u16max) u16height = u16max;
    return (ushort)u16height;
}
```

**Terrain Class Structure**:
```csharp
public class Terrain
{
    public TerrainDataBuffer Data { get; set; }
    public string[] MaterialNames { get; set; }
    public int Width => Data.Width;
    public int Height => Data.Height;
    public int Size { get; } // Throws if not square
    
    // Constructor
    public Terrain(int size, IList<string> materialNames);
    
    // Save method
    public void Save(String path, float maxHeight = 1);
}

public struct TerrainData
{
    public float Height { get; set; }
    public int Material { get; set; }
    public bool IsHole { get; set; }
}
```

### 2. **BeamNgTerrainPoc/BeamNG Folder**
Contains reference implementations:
- `LevelExporter.cs` - Shows how to use Grille.BeamNG.Lib
- `ImageProjector.cs` - Image manipulation utilities
- `SolidColorResource.cs` - Resource handling

### 3. **GeoTiff2BeamNG Project**
**Location**: `GeoTiff2BeamNG/BeamNGTerrainFileBuilder.cs`

**Key Insight - Material Layer Processing**:
```csharp
// From WriteLayerMap method - shows basic layer map writing
private static void WriteLayerMap(BinaryWriter binaryWriter, double[,] heightArray)
{
    var longitudes = heightArray.GetLength(0);
    var latitudes = heightArray.GetLength(1);
    
    // Iterate bottom-left to top-right
    var longitudeCounter = 0;
    var latitudeCounter = 0;
    
    while (latitudeCounter < latitudes)
    {
        byte theByte = 0; // Material index
        binaryWriter.Write(theByte);
        
        longitudeCounter++;
        if (longitudeCounter > longitudes - 1) 
        {
            longitudeCounter = 0;
            latitudeCounter++;
        }
    }
}
```

**Note**: This implementation only writes zeros (material index 0). We need to implement proper material layer blending.

### 4. **BeamNG_LevelCleanUp Project**
Shows terrain material handling patterns:
- `TerrainTextureGenerator.cs` - Generates placeholder terrain textures
- `TerrainTextureHelper.cs` - Terrain texture utilities
- Pattern for handling terrain materials and textures

---

## Implementation Plan

### Phase 1: Project Structure Setup

**Create folder structure in BeamNgTerrainPoc**:
```
BeamNgTerrainPoc/
??? Terrain/
?   ??? TerrainCreator.cs          // Main API class
?   ??? Models/
?   ?   ??? MaterialDefinition.cs
?   ?   ??? TerrainCreationParameters.cs
?   ?   ??? ValidationResult.cs
?   ??? Processing/
?   ?   ??? HeightmapProcessor.cs
?   ?   ??? MaterialLayerProcessor.cs
?   ??? Validation/
?       ??? TerrainValidator.cs
```

**Class Responsibilities**:
1. `TerrainCreator` - Main entry point, orchestrates creation
2. `MaterialDefinition` - Material name + optional layer image
3. `TerrainCreationParameters` - Input parameter container
4. `HeightmapProcessor` - Converts L16 image to height data
5. `MaterialLayerProcessor` - Processes layer images to material indices (handles optional images)
6. `TerrainValidator` - Validates all inputs before processing

### Phase 2: Data Models

#### MaterialDefinition.cs
```csharp
public class MaterialDefinition
{
    /// <summary>
    /// Name of the terrain material (required)
    /// </summary>
    public string MaterialName { get; set; }
    
    /// <summary>
    /// Optional layer image for automatic material placement.
    /// If null, material can still be used but won't have automatic placement.
    /// White pixels (255) = material present, Black pixels (0) = material absent
    /// </summary>
    public Image<L8>? LayerImage { get; set; }
    
    public MaterialDefinition(string materialName, Image<L8>? layerImage = null)
    {
        MaterialName = materialName;
        LayerImage = layerImage;
    }
}
```

#### TerrainCreationParameters.cs
```csharp
public class TerrainCreationParameters
{
    public int Size { get; set; }
    public float MaxHeight { get; set; }
    public Image<L16> HeightmapImage { get; set; }
    
    /// <summary>
    /// List of material definitions. Each material has a name and optional layer image.
    /// Order matters - index in list = material index in terrain file.
    /// First material (index 0) is used as default/fallback where no other material is defined.
    /// </summary>
    public List<MaterialDefinition> Materials { get; set; }
    
    // Optional
    public bool IncludeLayerTextureData { get; set; } = false;
}
```

#### ValidationResult.cs
```csharp
public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
```

### Phase 3: Validation (TerrainValidator.cs)

**Validation Rules**:
```csharp
public static ValidationResult Validate(TerrainCreationParameters parameters)
{
    var result = new ValidationResult { IsValid = true };
    
    // 1. Size must be power of 2
    if (!IsPowerOfTwo(parameters.Size))
    {
        result.Errors.Add($"Size {parameters.Size} is not a power of 2");
        result.IsValid = false;
    }
    
    // 2. Size must be in valid range (256-16384)
    if (parameters.Size < 256 || parameters.Size > 16384)
    {
        result.Errors.Add($"Size {parameters.Size} out of range (256-16384)");
        result.IsValid = false;
    }
    
    // 3. Heightmap dimensions must match size
    if (parameters.HeightmapImage.Width != parameters.Size || 
        parameters.HeightmapImage.Height != parameters.Size)
    {
        result.Errors.Add("Heightmap dimensions don't match terrain size");
        result.IsValid = false;
    }
    
    // 4. Must have at least one material
    if (parameters.Materials == null || parameters.Materials.Count == 0)
    {
        result.Errors.Add("At least one material is required");
        result.IsValid = false;
    }
    
    // 5. All material names must be non-empty
    if (parameters.Materials != null)
    {
        for (int i = 0; i < parameters.Materials.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(parameters.Materials[i].MaterialName))
            {
                result.Errors.Add($"Material at index {i} has empty name");
                result.IsValid = false;
            }
        }
    }
    
    // 6. All layer images (if provided) must match terrain size
    if (parameters.Materials != null)
    {
        for (int i = 0; i < parameters.Materials.Count; i++)
        {
            var layerImage = parameters.Materials[i].LayerImage;
            if (layerImage != null)
            {
                if (layerImage.Width != parameters.Size || layerImage.Height != parameters.Size)
                {
                    result.Errors.Add(
                        $"Layer image for material '{parameters.Materials[i].MaterialName}' " +
                        $"dimensions ({layerImage.Width}x{layerImage.Height}) don't match terrain size ({parameters.Size}x{parameters.Size})");
                    result.IsValid = false;
                }
            }
        }
    }
    
    // 7. Max height must be positive
    if (parameters.MaxHeight <= 0)
    {
        result.Errors.Add("MaxHeight must be positive");
        result.IsValid = false;
    }
    
    // Warnings
    if (parameters.Size > 8192)
    {
        result.Warnings.Add("Size > 8192 may cause memory issues");
    }
    
    // Warning if no layer images provided
    if (parameters.Materials != null)
    {
        int materialsWithoutImages = parameters.Materials.Count(m => m.LayerImage == null);
        if (materialsWithoutImages == parameters.Materials.Count)
        {
            result.Warnings.Add("No material layer images provided - all terrain will use first material (index 0)");
        }
        else if (materialsWithoutImages > 0)
        {
            result.Warnings.Add($"{materialsWithoutImages} material(s) have no layer image and won't be auto-placed");
        }
    }
    
    return result;
}

private static bool IsPowerOfTwo(int x)
{
    return (x > 0) && ((x & (x - 1)) == 0);
}
```

### Phase 4: Heightmap Processing (HeightmapProcessor.cs)

**Convert L16 image to height values**:
```csharp
public static float[] ProcessHeightmap(
    Image<L16> heightmapImage, 
    float maxHeight)
{
    int size = heightmapImage.Width;
    var heights = new float[size * size];
    
    // ImageSharp images are top-down (0,0 = top-left)
    // BeamNG expects bottom-up (0,0 = bottom-left)
    // We need to flip vertically during read
    
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            // Read from top-down image
            var pixel = heightmapImage[x, y];
            
            // Convert to 0-1 range
            float normalizedHeight = pixel.PackedValue / 65535f;
            
            // Scale to world height
            float worldHeight = normalizedHeight * maxHeight;
            
            // Write to bottom-up array (flip Y)
            int flippedY = size - 1 - y;
            int index = flippedY * size + x;
            heights[index] = worldHeight;
        }
    }
    
    return heights;
}
```

### Phase 5: Material Layer Processing (MaterialLayerProcessor.cs)

**CRITICAL ALGORITHM - Material Layer Blending**:

The material layer processing handles optional layer images. If a material has no layer image, it won't be auto-placed.

```csharp
public static byte[] ProcessMaterialLayers(
    List<MaterialDefinition> materials,
    int size)
{
    var materialIndices = new byte[size * size];
    
    // Initialize all to material 0 (default/fallback)
    Array.Fill(materialIndices, (byte)0);
    
    // Get only materials that have layer images
    var materialsWithImages = materials
        .Select((mat, index) => new { Material = mat, Index = index })
        .Where(x => x.Material.LayerImage != null)
        .ToList();
    
    // If no materials have images, return all zeros (material index 0)
    if (materialsWithImages.Count == 0)
    {
        return materialIndices;
    }
    
    // Process bottom-up (BeamNG coordinate system)
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            // BeamNG array index (bottom-left origin)
            int flippedY = size - 1 - y;
            int arrayIndex = flippedY * size + x;
            
            // Find which material should be at this pixel
            // IMPORTANT: Process layers in REVERSE order
            // Last material layer with white pixel wins (highest priority)
            for (int i = materialsWithImages.Count - 1; i >= 0; i--)
            {
                var matWithImage = materialsWithImages[i];
                var pixel = matWithImage.Material.LayerImage![x, y]; // ! because we filtered for non-null
                
                // White pixel (255) means material is present
                if (pixel.PackedValue > 127) // Threshold at mid-gray
                {
                    materialIndices[arrayIndex] = (byte)matWithImage.Index;
                    break; // Found highest priority material
                }
            }
        }
    }
    
    return materialIndices;
}
```

**Alternative: Generate full coverage for material without image**:
If you want the first material to have full coverage when no image is provided:

```csharp
public static byte[] ProcessMaterialLayersWithDefaultCoverage(
    List<MaterialDefinition> materials,
    int size)
{
    // If first material has no layer image, it gets full coverage by default
    // (since array is initialized to 0)
    
    var materialIndices = new byte[size * size];
    Array.Fill(materialIndices, (byte)0);
    
    // Process only materials with images (skip index 0 if it has no image)
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            int flippedY = size - 1 - y;
            int arrayIndex = flippedY * size + x;
            
            // Process in reverse order (highest priority last)
            for (int matIndex = materials.Count - 1; matIndex >= 0; matIndex--)
            {
                var material = materials[matIndex];
                
                // Skip materials without layer images
                if (material.LayerImage == null)
                    continue;
                
                var pixel = material.LayerImage[x, y];
                
                if (pixel.PackedValue > 127)
                {
                    materialIndices[arrayIndex] = (byte)matIndex;
                    break;
                }
            }
            // If no material matched, keeps default value (0)
        }
    }
    
    return materialIndices;
}
```

**Helper: Generate full-coverage layer image**:
```csharp
public static Image<L8> CreateFullCoverageLayer(int size)
{
    var image = new Image<L8>(size, size);
    var white = new L8(255);
    
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            image[x, y] = white;
        }
    }
    
    return image;
}

public static Image<L8> CreateNoCoverageLayer(int size)
{
    var image = new Image<L8>(size, size);
    // Default L8 is black (0), so just create empty image
    return image;
}
```

### Phase 6: Main Creator Class (TerrainCreator.cs)

**Complete implementation using Grille.BeamNG.Lib**:

```csharp
public class TerrainCreator
{
    public async Task<bool> CreateTerrainFileAsync(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        // 1. Validate inputs
        var validation = TerrainValidator.Validate(parameters);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                Console.WriteLine($"ERROR: {error}");
            return false;
        }
        
        // Show warnings
        foreach (var warning in validation.Warnings)
            Console.WriteLine($"WARNING: {warning}");
        
        // 2. Process heightmap
        Console.WriteLine("Processing heightmap...");
        var heights = HeightmapProcessor.ProcessHeightmap(
            parameters.HeightmapImage,
            parameters.MaxHeight);
        
        // 3. Process material layers
        Console.WriteLine("Processing material layers...");
        var materialIndices = MaterialLayerProcessor.ProcessMaterialLayers(
            parameters.Materials,
            parameters.Size);
        
        // 4. Create Grille.BeamNG.Lib Terrain object
        Console.WriteLine("Building terrain data structure...");
        var materialNames = parameters.Materials
            .Select(m => m.MaterialName)
            .ToList();
        
        var terrain = new Terrain(
            parameters.Size,
            materialNames);
        
        // 5. Fill terrain data
        for (int i = 0; i < terrain.Data.Length; i++)
        {
            terrain.Data[i] = new TerrainData
            {
                Height = heights[i],
                Material = materialIndices[i],
                IsHole = false
            };
        }
        
        // 6. Save using Grille.BeamNG.Lib
        Console.WriteLine($"Writing terrain file to {outputPath}...");
        terrain.Save(outputPath, parameters.MaxHeight);
        
        Console.WriteLine("Terrain file created successfully!");
        return true;
    }
    
    // Synchronous version
    public bool CreateTerrainFile(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        return CreateTerrainFileAsync(outputPath, parameters).Result;
    }
}
```

### Phase 7: Example Usage in Program.cs

```csharp
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

Console.WriteLine("BeamNG Terrain Creator");

// Example 1: Create terrain with all materials having layer images
var creator = new TerrainCreator();

// Load images
var heightmap = Image.Load<L16>(@"D:\temp\heightmap.png");
var grassLayer = Image.Load<L8>(@"D:\temp\grass_layer.png");
var dirtLayer = Image.Load<L8>(@"D:\temp\dirt_layer.png");
var rockLayer = Image.Load<L8>(@"D:\temp\rock_layer.png");

// Setup parameters with all materials having images
var parameters = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapImage = heightmap,
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", grassLayer),
        new MaterialDefinition("dirt", dirtLayer),
        new MaterialDefinition("rock", rockLayer)
    },
    IncludeLayerTextureData = false
};

// Create terrain file
var success = await creator.CreateTerrainFileAsync(
    @"D:\temp\output\myTerrain.ter",
    parameters);

if (success)
{
    Console.WriteLine("Terrain created successfully!");
}

// Dispose images
heightmap.Dispose();
grassLayer.Dispose();
dirtLayer.Dispose();
rockLayer.Dispose();

Console.WriteLine();
Console.WriteLine("---");
Console.WriteLine();

// Example 2: Create terrain with some materials without layer images
var heightmap2 = Image.Load<L16>(@"D:\temp\heightmap2.png");
var mainLayer = Image.Load<L8>(@"D:\temp\main_material.png");

var parameters2 = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapImage = heightmap2,
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass"),              // No image - fallback/default material
        new MaterialDefinition("asphalt", mainLayer), // Has image - will be placed
        new MaterialDefinition("dirt"),               // No image - available but not auto-placed
        new MaterialDefinition("concrete")            // No image - available but not auto-placed
    }
};

success = await creator.CreateTerrainFileAsync(
    @"D:\temp\output\myTerrain2.ter",
    parameters2);

// Dispose
heightmap2.Dispose();
mainLayer.Dispose();

Console.WriteLine();
Console.WriteLine("---");
Console.WriteLine();

// Example 3: Minimal terrain with single material (no layer image needed)
var heightmap3 = Image.Load<L16>(@"D:\temp\heightmap3.png");

var parameters3 = new TerrainCreationParameters
{
    Size = 512,
    MaxHeight = 200.0f,
    HeightmapImage = heightmap3,
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass") // Single material, entire terrain will use this
    }
};

success = await creator.CreateTerrainFileAsync(
    @"D:\temp\output\simple_terrain.ter",
    parameters3);

heightmap3.Dispose();

Console.WriteLine("Done!");
```

---

## Testing Strategy

### Test Cases

#### Test 1: Minimal Terrain (256×256, 1 material, no layer image)
```csharp
var heightmap = CreateFlatHeightmap(256, 32768); // Mid-height

var parameters = new TerrainCreationParameters
{
    Size = 256,
    MaxHeight = 100.0f,
    HeightmapImage = heightmap,
    Materials = new List<MaterialDefinition> 
    { 
        new MaterialDefinition("grass") // No layer image - entire terrain uses this material
    }
};
```

Expected file size: `5 + (256²×2) + (256²) + 4 + (1+5) = 196,622 bytes`

#### Test 2: Medium Terrain (1024×1024, 3 materials with images)
```csharp
var parameters = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapImage = LoadHeightmap("test_1024.png"),
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass", LoadLayer("grass.png")),
        new MaterialDefinition("dirt", LoadLayer("dirt.png")),
        new MaterialDefinition("rock", LoadLayer("rock.png"))
    }
};
```

#### Test 3: Mixed materials (some with images, some without)
```csharp
var parameters = new TerrainCreationParameters
{
    Size = 1024,
    MaxHeight = 500.0f,
    HeightmapImage = LoadHeightmap("test_1024.png"),
    Materials = new List<MaterialDefinition>
    {
        new MaterialDefinition("grass"),                      // Fallback material (no image)
        new MaterialDefinition("asphalt", LoadLayer("asphalt.png")), // Has placement
        new MaterialDefinition("dirt"),                       // Available but not placed
        new MaterialDefinition("rock", LoadLayer("rock.png"))        // Has placement
    }
};
```

#### Test 4: Large Terrain (4096×4096, 5+ materials)
Test memory handling and performance.

#### Test 5: Validation Tests
- Wrong size (not power of 2)
- Mismatched dimensions
- Wrong image format
- Empty material list
- Materials with empty names
- Negative max height
- Layer image dimension mismatch

---

## Next Steps for Implementation

### Immediate Actions
1. ? Create instruction document (this file)
2. ? Create folder structure in BeamNgTerrainPoc
3. ? Implement data models (Phase 2)
4. ? Implement validation (Phase 3)
5. ? Implement heightmap processor (Phase 4)
6. ? Implement material layer processor (Phase 5)
7. ? Implement main creator class (Phase 6)
8. ? Add example usage to Program.cs (Phase 7)
9. ? Write unit tests
10. ? Test with BeamNG.drive

### Success Criteria
- ? Can create .ter file from PNG inputs
- ? File loads successfully in BeamNG.drive editor
- ? Terrain height matches heightmap
- ? Materials appear in correct locations
- ? No crashes or memory issues
- ? Code is clean and well-documented

### Implementation Status
**PHASE 1-7 COMPLETE** ?

The terrain creator has been successfully implemented and tested:
- All classes created in proper folder structure
- Data models implemented with MaterialDefinition supporting optional layer images
- Comprehensive validation with errors and warnings
- Heightmap processing with Y-axis flip for BeamNG coordinate system
- Material layer processing supporting optional images
- Main TerrainCreator class using Grille.BeamNG.Lib
- Example usage in Program.cs with test terrain generation

**Test Results**:
- Successfully created 256×256 test terrain
- File size: 196,623 bytes (matches expected calculation)
- No compilation errors or runtime crashes
- Clean console output with progress messages

**Next Steps**:
1. Test with BeamNG.drive editor to verify format compatibility
2. Add unit tests for validation and processing logic
3. Test with larger terrains (1024×1024, 4096×4096)
4. Test with multiple materials and layer images
5. Performance testing for large terrains
