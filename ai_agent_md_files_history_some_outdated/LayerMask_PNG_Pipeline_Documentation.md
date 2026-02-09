# LayerMask PNG Pipeline Documentation

## Overview

This document describes the complete journey of a layer mask PNG file through the terrain generation pipeline, from UI selection in the `GenerateTerrain` page to its final encoding in the `.ter` binary file.

## Pipeline Summary

```
???????????????????????????????????????????????????????????????????????????????
?                              UI LAYER                                        ?
?  GenerateTerrain.razor.cs                                                   ?
?  ?? TerrainMaterialItemExtended.LayerMapPath (string)                       ?
?  ?? TerrainMaterialSettings.razor.cs (file picker component)               ?
???????????????????????????????????????????????????????????????????????????????
                                      ?
                                      ?
???????????????????????????????????????????????????????????????????????????????
?                         PARAMETERS LAYER                                     ?
?  MaterialDefinition (BeamNgTerrainPoc\Terrain\Models\)                      ?
?  ?? MaterialName: string                                                    ?
?  ?? LayerImagePath: string? (path to PNG)                                   ?
?                                                                             ?
?  TerrainCreationParameters                                                  ?
?  ?? Materials: List<MaterialDefinition>                                     ?
???????????????????????????????????????????????????????????????????????????????
                                      ?
                                      ?
???????????????????????????????????????????????????????????????????????????????
?                         TERRAIN CREATOR                                      ?
?  TerrainCreator.cs                                                          ?
?  ?? Validates parameters                                                    ?
?  ?? Loads heightmap (PNG, GeoTIFF file, or GeoTIFF directory)              ?
?  ?? Processes heightmap ? float[] heights                                   ?
?  ?? Calls MaterialLayerProcessor.ProcessMaterialLayers()                    ?
?  ?   ?? Returns byte[] materialIndices                                      ?
?  ?? Creates Grille.BeamNG.Terrain object                                    ?
?  ?? Fills TerrainData[] with heights and material indices                   ?
?  ?? Calls terrain.Save() ? writes .ter file                                 ?
???????????????????????????????????????????????????????????????????????????????
                                      ?
                                      ?
???????????????????????????????????????????????????????????????????????????????
?                    MATERIAL LAYER PROCESSOR                                  ?
?  MaterialLayerProcessor.cs                                                  ?
?  ?? Loads each PNG layer image as Image<L8>                                 ?
?  ?? Validates image dimensions match terrain size                           ?
?  ?? For each pixel, determines winning material (highest index with white) ?
?  ?? Returns byte[] with material index for each terrain point              ?
???????????????????????????????????????????????????????????????????????????????
                                      ?
                                      ?
???????????????????????????????????????????????????????????????????????????????
?                       GRILLE.BEAMNG.LIB                                      ?
?  Terrain.cs + TerrainV9Serializer.cs                                        ?
?  ?? Terrain.Data: TerrainDataBuffer (Height + Material + IsHole per pixel)  ?
?  ?? Terrain.MaterialNames: string[]                                         ?
?  ?? Serializes to .ter binary format (version 9)                            ?
???????????????????????????????????????????????????????????????????????????????
```

---

## Detailed Component Walkthrough

### 1. UI Layer: GenerateTerrain Page

**File:** `BeamNG_LevelCleanUp\BlazorUI\Pages\GenerateTerrain.razor.cs`

The terrain generation page maintains a list of terrain materials scanned from the level:

```csharp
private readonly List<TerrainMaterialSettings.TerrainMaterialItemExtended> _terrainMaterials = new();
```

Each material is represented by `TerrainMaterialItemExtended` which contains:

```csharp
public class TerrainMaterialItemExtended
{
    public int Order { get; set; }                    // Position in material list (0 = default)
    public string MaterialName { get; set; }          // Display name
    public string InternalName { get; set; }          // Internal BeamNG name
    public string JsonKey { get; set; }               // Key in materials.json
    public string? LayerMapPath { get; set; }         // PATH TO LAYER MASK PNG
    public bool HasLayerMap => !string.IsNullOrEmpty(LayerMapPath);
    public bool IsRoadMaterial { get; set; }          // Enable road smoothing
    // ... road smoothing parameters ...
}
```

**Layer Map Selection:**

The `TerrainMaterialSettings.razor.cs` component provides a file picker:

```csharp
private async Task SelectLayerMap()
{
    string? selectedPath = null;
    var staThread = new Thread(() =>
    {
        using var dialog = new OpenFileDialog();
        dialog.Filter = "PNG Images (*.png)|*.png|All Files (*.*)|*.*";
        dialog.Title = $"Select Layer Map for {Material.InternalName}";
        if (dialog.ShowDialog() == DialogResult.OK) selectedPath = dialog.FileName;
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();

    if (!string.IsNullOrEmpty(selectedPath))
    {
        Material.LayerMapPath = selectedPath;
        await OnMaterialChanged.InvokeAsync(Material);
    }
}
```

---

### 2. Parameters Layer: MaterialDefinition

**File:** `BeamNgTerrainPoc\Terrain\Models\MaterialDefinition.cs`

When the user clicks "Generate Terrain", the UI builds `MaterialDefinition` objects:

```csharp
public class MaterialDefinition
{
    /// <summary>
    /// Name of the terrain material (required)
    /// </summary>
    public string MaterialName { get; set; }
    
    /// <summary>
    /// Optional path to layer image file for automatic material placement.
    /// If null or empty, material can still be used but won't have automatic placement.
    /// White pixels (255) = material present, Black pixels (0) = material absent.
    /// Supported formats: PNG, BMP, JPG, etc. (8-bit grayscale recommended)
    /// </summary>
    public string? LayerImagePath { get; set; }
    
    /// <summary>
    /// Optional road smoothing parameters.
    /// </summary>
    public RoadSmoothingParameters? RoadParameters { get; set; }
    
    public MaterialDefinition(string materialName, string? layerImagePath = null, 
                              RoadSmoothingParameters? roadParameters = null)
    {
        MaterialName = materialName;
        LayerImagePath = layerImagePath;
        RoadParameters = roadParameters;
    }
}
```

**Conversion in GenerateTerrain.razor.cs:**

```csharp
// In ExecuteTerrainGeneration()
var orderedMaterials = _terrainMaterials.OrderBy(m => m.Order).ToList();
var materialDefinitions = new List<MaterialDefinition>();

foreach (var mat in orderedMaterials)
{
    RoadSmoothingParameters? roadParams = null;
    if (mat.IsRoadMaterial)
        roadParams = mat.BuildRoadSmoothingParameters(debugPath);

    materialDefinitions.Add(new MaterialDefinition(
        mat.InternalName,
        mat.LayerMapPath,    // <-- LAYER MAP PATH PASSED HERE
        roadParams));
}

var parameters = new TerrainCreationParameters
{
    Size = _terrainSize,
    MaxHeight = _maxHeight,
    Materials = materialDefinitions,
    // ... other parameters
};
```

---

### 3. TerrainCreator: Orchestration

**File:** `BeamNgTerrainPoc\Terrain\TerrainCreator.cs`

The `CreateTerrainFileAsync` method orchestrates the entire process:

```csharp
public async Task<bool> CreateTerrainFileAsync(string outputPath, TerrainCreationParameters parameters)
{
    // 1. Validate inputs
    var validation = TerrainValidator.Validate(parameters);
    if (!validation.IsValid) return false;

    // 2. Load heightmap (from PNG, GeoTIFF file, or GeoTIFF directory)
    Image<L16>? heightmapImage = LoadHeightmap(parameters);

    // 3. Process heightmap to float[] heights
    var heights = HeightmapProcessor.ProcessHeightmap(heightmapImage, parameters.MaxHeight);

    // 3a. Apply road smoothing if road materials exist
    if (parameters.Materials.Any(m => m.RoadParameters != null))
    {
        var smoothingResult = ApplyRoadSmoothing(heights, parameters.Materials, ...);
        if (smoothingResult != null)
            heights = ConvertTo1DArray(smoothingResult.ModifiedHeightMap);
    }

    // 4. PROCESS MATERIAL LAYERS (LAYER MASK PNGs) ? KEY STEP
    var materialIndices = MaterialLayerProcessor.ProcessMaterialLayers(
        parameters.Materials,
        parameters.Size);

    // 5. Create Grille.BeamNG.Lib Terrain object
    var materialNames = parameters.Materials.Select(m => m.MaterialName).ToList();
    var terrain = new Grille.BeamNG.Terrain(parameters.Size, materialNames);

    // 6. Fill terrain data with heights AND material indices
    for (var i = 0; i < terrain.Data.Length; i++)
    {
        terrain.Data[i] = new TerrainData
        {
            Height = heights[i],
            Material = materialIndices[i],  // <-- FROM LAYER MASK PROCESSING
            IsHole = false
        };
    }

    // 7. Save to .ter file
    await Task.Run(() => terrain.Save(outputPath, parameters.MaxHeight));

    // 8. Write terrain.json metadata
    await WriteTerrainJsonAsync(outputPath, parameters);

    return true;
}
```

---

### 4. MaterialLayerProcessor: PNG Processing

**File:** `BeamNgTerrainPoc\Terrain\Processing\MaterialLayerProcessor.cs`

This is where the layer mask PNGs are actually loaded and processed:

```csharp
public static class MaterialLayerProcessor
{
    /// <summary>
    /// Processes material layer images and generates material indices for each terrain point.
    /// 
    /// PRIORITY RULES:
    /// - Materials are processed in order of their index (0, 1, 2...)
    /// - For each pixel, the HIGHEST index material with a white pixel wins
    /// - Materials without layer images don't claim any pixels
    /// - Material at index 0 is the fallback (fills all unclaimed pixels)
    /// </summary>
    public static byte[] ProcessMaterialLayers(List<MaterialDefinition> materials, int size)
    {
        var materialIndices = new byte[size * size];
        
        // Initialize all to material 0 (default/fallback)
        Array.Fill(materialIndices, (byte)0);
        
        // Log material order for debugging
        for (int i = 0; i < materials.Count; i++)
        {
            var mat = materials[i];
            var hasLayer = !string.IsNullOrWhiteSpace(mat.LayerImagePath);
            TerrainLogger.Info($"  [{i}] {mat.MaterialName} - " +
                $"{(hasLayer ? "has layer map" : "NO layer map (won't claim pixels)")}");
        }
        
        // Build list of (materialIndex, image) pairs
        var loadedImages = new List<(int MaterialIndex, Image<L8> Image)>();
        
        try
        {
            for (int i = 0; i < materials.Count; i++)
            {
                var mat = materials[i];
                
                // Skip materials without layer images
                if (string.IsNullOrWhiteSpace(mat.LayerImagePath))
                    continue;
                
                try
                {
                    // LOAD THE LAYER MASK PNG
                    var image = Image.Load<L8>(mat.LayerImagePath!);
                    
                    // Validate size matches terrain size
                    if (image.Width != size || image.Height != size)
                    {
                        TerrainLogger.Warning($"Layer image for '{mat.MaterialName}' " +
                            $"has size {image.Width}x{image.Height} but terrain is {size}x{size}. Skipping.");
                        image.Dispose();
                        continue;
                    }
                    
                    // Store with the ORIGINAL material index
                    loadedImages.Add((i, image));
                }
                catch (Exception ex)
                {
                    TerrainLogger.Warning($"Failed to load layer image for '{mat.MaterialName}': {ex.Message}");
                }
            }
            
            if (loadedImages.Count == 0)
            {
                TerrainLogger.Info("No layer images loaded - all pixels will use material index 0");
                return materialIndices;
            }
            
            // PROCESS EACH PIXEL
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // BeamNG array index (bottom-left origin)
                    int flippedY = size - 1 - y;
                    int arrayIndex = flippedY * size + x;
                    
                    // Find which material should be at this pixel
                    // Process in REVERSE order (highest index first = highest priority)
                    for (int i = loadedImages.Count - 1; i >= 0; i--)
                    {
                        var (materialIndex, image) = loadedImages[i];
                        var pixel = image[x, y];
                        
                        // White pixel (>127) means material present
                        if (pixel.PackedValue > 127)
                        {
                            materialIndices[arrayIndex] = (byte)materialIndex;
                            break; // Found highest priority material
                        }
                    }
                    // If no material claimed this pixel, it stays at 0 (fallback)
                }
            }
        }
        finally
        {
            // Always dispose loaded images
            foreach (var (_, image) in loadedImages)
                image.Dispose();
        }
        
        return materialIndices;
    }
}
```

**Key Processing Rules:**

| Rule | Description |
|------|-------------|
| **Threshold** | Pixel value > 127 = material present (white), ? 127 = absent (black) |
| **Priority** | Higher material index wins when multiple materials claim same pixel |
| **Fallback** | Material index 0 is used for any unclaimed pixels |
| **No Layer** | Materials without layer images cannot claim any pixels |
| **Coordinate Flip** | Y-axis is flipped for BeamNG (bottom-left origin) |

---

### 5. HeightmapProcessor: Height Data Processing

**File:** `BeamNgTerrainPoc\Terrain\Processing\HeightmapProcessor.cs`

The heightmap is processed in parallel with material layers:

```csharp
public static class HeightmapProcessor
{
    /// <summary>
    /// Converts a 16-bit grayscale heightmap image to height values.
    /// </summary>
    public static float[] ProcessHeightmap(Image<L16> heightmapImage, float maxHeight)
    {
        int size = heightmapImage.Width;
        var heights = new float[size * size];
        
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var pixel = heightmapImage[x, y];
                
                // Convert to 0-1 range (16-bit = 0-65535)
                float normalizedHeight = pixel.PackedValue / 65535f;
                
                // Scale to world height
                float worldHeight = normalizedHeight * maxHeight;
                
                // Write to bottom-up array (flip Y for BeamNG coordinate system)
                int flippedY = size - 1 - y;
                int index = flippedY * size + x;
                heights[index] = worldHeight;
            }
        }
        
        return heights;
    }
}
```

---

### 6. Grille.BeamNG.Lib: Terrain Object

**Files:** 
- `Grille.BeamNG.Lib\Terrain.cs`
- `Grille.BeamNG.Lib\Terrain_Types.cs`

The `Terrain` class holds all terrain data:

```csharp
public class Terrain
{
    /// <summary>
    /// Buffer containing all terrain data (Height, Material, IsHole per pixel)
    /// </summary>
    public TerrainDataBuffer Data { get; set; }
    
    /// <summary>
    /// List of material names in index order
    /// </summary>
    public string[] MaterialNames { get; set; }
    
    public int Size { get; }  // Width = Height (must be square)
    
    public Terrain(int size, IList<string> materialNames)
    {
        Data = new TerrainDataBuffer(size, size);
        MaterialNames = materialNames.ToArray();
    }
    
    public void Save(string path, float maxHeight = 1)
    {
        using var stream = File.Create(path);
        Serialize(stream, maxHeight);
    }
    
    public void Serialize(Stream stream, float maxHeight = 1)
    {
        TerrainSerializer.Serialize(stream, this, maxHeight);
    }
}
```

**TerrainData Structure:**

```csharp
public struct TerrainData
{
    public float Height { get; set; }    // World-space height
    public int Material { get; set; }    // Index into MaterialNames array
    public bool IsHole { get; set; }     // Terrain hole (renders as void)
}
```

---

### 7. TerrainV9Serializer: Binary .ter File Creation

**File:** `Grille.BeamNG.Lib\IO\Binary\TerrainV9Serializer.cs`

The final step serializes terrain data to the BeamNG .ter binary format:

```csharp
public static class TerrainV9Serializer
{
    public static void Serialize(Stream stream, Terrain terrain, float maxHeight)
    {
        using var bw = new BinaryViewWriter(stream);
        var data = terrain.Data;

        // Write header
        bw.WriteByte(9);                         // Version = 9
        bw.WriteUInt32((uint)terrain.Size);      // Terrain size (e.g., 2048)

        // Write height data (ushort per pixel)
        for (int i = 0; i < data.Length; i++)
        {
            var u16height = GetU16Height(data[i].Height, maxHeight);
            bw.WriteUInt16(u16height);
        }

        // Write material data (byte per pixel) ? LAYER MASK DATA GOES HERE
        for (int i = 0; i < data.Length; i++)
        {
            var material = data[i].IsHole ? byte.MaxValue : (byte)data[i].Material;
            bw.WriteByte(material);
        }

        // Write material names (null-terminated strings)
        bw.WriteMaterialNames(terrain.MaterialNames);
    }
    
    /// <summary>
    /// Converts world height to 16-bit value for .ter file
    /// </summary>
    public static ushort GetU16Height(float height, float maxHeight)
    {
        float u16max = ushort.MaxValue;  // 65535
        float u16height = height / maxHeight * u16max;
        if (u16height > u16max)
            u16height = u16max;
        return (ushort)u16height;
    }
}
```

**.ter File Binary Format (Version 9):**

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 1 byte | Version | Always 9 for current format |
| 1 | 4 bytes | Size | Terrain dimension (uint32, e.g., 2048) |
| 5 | Size² × 2 bytes | Heights | 16-bit height values (ushort per pixel) |
| 5 + Size²×2 | Size² × 1 byte | Materials | **Material indices (byte per pixel)** |
| End | Variable | Material Names | Null-terminated material name strings |

**Material Index Values:**
- `0` to `254`: Valid material index (maps to `MaterialNames[index]`)
- `255`: Terrain hole (renders as void)

---

## Complete Data Flow Example

### Example: 2048×2048 Terrain with 3 Materials

**Input:**
```
Materials (in order):
  [0] "Grass" - no layer map (fallback)
  [1] "Asphalt" - has road_layer.png (white = road)
  [2] "Dirt" - has dirt_layer.png (white = dirt)
```

**Processing:**

1. **Initialize**: All pixels set to material index `0` (Grass)

2. **Load Layer Maps**:
   - Skip index 0 (no layer map)
   - Load `road_layer.png` for index 1
   - Load `dirt_layer.png` for index 2

3. **Process Each Pixel** (reverse priority order):
   ```
   for each pixel (x, y):
       // Check Dirt layer first (highest index)
       if dirt_layer[x,y] > 127:
           materialIndices[pixel] = 2  // Dirt wins
       // Check Asphalt layer
       elif road_layer[x,y] > 127:
           materialIndices[pixel] = 1  // Asphalt wins
       // else: stays at 0 (Grass, the fallback)
   ```

4. **Write to .ter file**:
   ```
   [Version: 9]
   [Size: 2048]
   [Heights: 4,194,304 × 2 bytes = 8MB]
   [Materials: 4,194,304 × 1 byte = 4MB]  ? Layer mask data encoded here
   [Names: "Grass\0Asphalt\0Dirt\0"]
   ```

---

## Important Considerations

### 1. Material Order Matters

The order of materials in `_terrainMaterials` directly determines:
- The material index in the .ter file
- The priority for overlapping layer masks

**UI Reordering:** The `GenerateTerrain` page includes drag-and-drop reordering with `MudDropContainer`:

```csharp
private void RenormalizeMaterialOrder()
{
    var sorted = _terrainMaterials.OrderBy(m => m.Order).ToList();
    for (var i = 0; i < sorted.Count; i++)
        sorted[i].Order = i;
    
    _terrainMaterials.Clear();
    _terrainMaterials.AddRange(sorted);
}
```

### 2. Layer Map Image Requirements

| Requirement | Value |
|-------------|-------|
| **Format** | PNG (recommended), also supports BMP, JPG |
| **Color Mode** | 8-bit grayscale (L8) |
| **Size** | Must match terrain size exactly (e.g., 2048×2048) |
| **White (255)** | Material present at this location |
| **Black (0)** | Material absent |
| **Threshold** | Pixel > 127 = present |

### 3. Material Index 0 as Fallback

The first material (index 0) serves as the fallback:
- Does NOT need a layer map
- Automatically fills any pixels not claimed by other materials
- Should typically be the most common terrain material (grass, sand, etc.)

### 4. Materials Without Layer Maps at Index > 0

Materials without layer maps at positions > 0 will never claim any pixels. The UI warns about this and offers to reorder:

```csharp
private bool ReorderMaterialsWithoutLayerMapsToEnd()
{
    // Moves materials without layer maps (except index 0) to end of list
    // This avoids confusion with material indices
}
```

### 5. Coordinate System

- **PNG images**: Top-down (0,0 = top-left)
- **BeamNG terrain**: Bottom-up (0,0 = bottom-left)
- **Conversion**: Y is flipped during processing (`flippedY = size - 1 - y`)

---

## Debugging Tips

### Check Material Logging

`MaterialLayerProcessor` logs material information:
```
Processing 3 materials for layer assignment:
  [0] Grass - NO layer map (won't claim pixels)
  [1] Asphalt - has layer map
  [2] Dirt - has layer map
Loaded 2 layer images for processing
```

### Export Debug Images

Enable debug output in `TerrainCreationParameters`:
- Modified heightmap saved as `{terrainName}_smoothed_heightmap.png`
- Road smoothing debug images per material

### Verify Layer Map Dimensions

If layer map is skipped, check the log for size mismatch:
```
Layer image for 'Asphalt' has size 1024x1024 but terrain is 2048x2048. Skipping.
```

### Inspect .ter File

The Grille.BeamNG.Lib can also deserialize .ter files for inspection:
```csharp
var terrain = new Terrain("output.ter", maxHeight: 500);
// terrain.MaterialNames = ["Grass", "Asphalt", "Dirt"]
// terrain.Data[i].Material = 0, 1, or 2
```

---

## Related Files

| File | Purpose |
|------|---------|
| `GenerateTerrain.razor.cs` | UI page for terrain generation |
| `TerrainMaterialSettings.razor.cs` | Material settings component with layer map picker |
| `MaterialDefinition.cs` | Model for material with layer path |
| `TerrainCreationParameters.cs` | All terrain generation parameters |
| `TerrainCreator.cs` | Main terrain creation orchestrator |
| `MaterialLayerProcessor.cs` | **Layer mask PNG processing logic** |
| `HeightmapProcessor.cs` | Heightmap PNG processing |
| `Terrain.cs` | Grille.BeamNG.Lib terrain object |
| `TerrainV9Serializer.cs` | .ter binary file writer |
