// BeamNG Terrain Creator - Example Usage

using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Processing;
using BeamNgTerrainPoc.Terrain.Validation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

Console.WriteLine("=== BeamNG Terrain Creator ===");
Console.WriteLine();

// Check command line arguments for custom usage
if (args.Length > 0)
{
    Console.WriteLine($"Arguments: {string.Join(", ", args)}");
    
    // Check for specific test mode
    if (args[0].Equals("complex", StringComparison.OrdinalIgnoreCase) || 
        args[0].Equals("multi", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Running complex multi-material terrain creation...");
        Console.WriteLine();
        await CreateTerrainWithMultipleMaterials();
        Console.WriteLine();
        Console.WriteLine("Done!");
        return;
    }
}

Console.WriteLine("Running simple test terrain creation...");
Console.WriteLine("(Use 'dotnet run -- complex' to run complex multi-material example)");
Console.WriteLine();

// Example: Create a simple test terrain with generated data
await CreateSimpleTestTerrain();

Console.WriteLine();
Console.WriteLine("Done!");

// ====================================================================================
// HELPER METHODS
// ====================================================================================

static async Task CreateSimpleTestTerrain()
{
    Console.WriteLine("--- Creating Simple Test Terrain ---");
    
    var creator = new TerrainCreator();
    
    // Create a simple 256x256 terrain for testing
    int size = 256;
    float maxHeight = 100.0f;
    
    Console.WriteLine($"Generating test data ({size}x{size})...");
    
    // Generate a simple heightmap (gradient from 0 to max)
    var heightmap = CreateTestHeightmap(size, maxHeight);
    
    // Create a simple material setup with one default material
    var parameters = new TerrainCreationParameters
    {
        Size = size,
        MaxHeight = maxHeight,
        HeightmapImage = heightmap,
        Materials = new List<MaterialDefinition>
        {
            new MaterialDefinition("grass") // Single material, entire terrain uses this
        }
    };
    
    // Create output directory in temp folder
    var outputDir = Path.Combine(Path.GetTempPath(), "BeamNG_TerrainTest");
    var outputPath = Path.Combine(outputDir, "test_terrain.ter");
    
    Console.WriteLine($"Output path: {outputPath}");
    Console.WriteLine();
    
    // Create terrain file
    var success = await creator.CreateTerrainFileAsync(outputPath, parameters);
    
    if (success)
    {
        Console.WriteLine();
        Console.WriteLine($"✓ Test terrain created successfully!");
        Console.WriteLine($"  Location: {outputPath}");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("✗ Failed to create test terrain.");
    }
    
    // Dispose images
    heightmap.Dispose();
}

static Image<L16> CreateTestHeightmap(int size, float maxHeight)
{
    var image = new Image<L16>(size, size);
    
    // Create a simple gradient heightmap
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            // Create a gradient from bottom-left (low) to top-right (high)
            float normalizedHeight = (x + y) / (2.0f * size);
            ushort pixelValue = (ushort)(normalizedHeight * 65535f);
            image[x, y] = new L16(pixelValue);
        }
    }
    
    return image;
}

// ====================================================================================
// ADVANCED EXAMPLES (commented out - uncomment to use with real image files)
// ====================================================================================


static async Task CreateTerrainWithMultipleMaterials()
{
    Console.WriteLine("--- Creating Terrain with Multiple Materials ---");
    Console.WriteLine();
    
    // This method demonstrates complex terrain creation with real-world data:
    // - 4096x4096 heightmap (16.7 million pixels)
    // - 25 different terrain materials with layer masks
    // - Automatic material ordering based on layer index
    // - Proper material name extraction from filenames
    // - Output: ~48MB .ter file ready for BeamNG.drive
    
    var creator = new TerrainCreator();
    
    // Source directory with all terrain files
    string sourceDir = @"D:\temp\TestMappingTools\_import";
    
    // Terrain name (can be changed to match your terrain)
    string terrainName = "theTerrain";
    
    try
    {
        Console.WriteLine($"Loading terrain data from: {sourceDir}");
        Console.WriteLine($"Terrain name: {terrainName}");
        Console.WriteLine();
        
        // Load heightmap (using terrain name in filename)
        string heightmapPath = Path.Combine(sourceDir, $"{terrainName}_heightmap.png");
        if (!File.Exists(heightmapPath))
        {
            Console.WriteLine($"ERROR: Heightmap not found at {heightmapPath}");
            return;
        }
        
        Console.WriteLine("Loading heightmap...");
        var heightmap = Image.Load<L16>(heightmapPath);
        Console.WriteLine($"  Heightmap size: {heightmap.Width}x{heightmap.Height}");
        
        // Find and parse all layer map files (using terrain name pattern)
        var layerMapFiles = Directory.GetFiles(sourceDir, $"{terrainName}_layerMap_*.png")
            .Select(path => new
            {
                Path = path,
                FileName = Path.GetFileName(path),
                ParsedInfo = ParseLayerMapFileName(Path.GetFileName(path), terrainName)
            })
            .Where(x => x.ParsedInfo != null)
            .OrderBy(x => x.ParsedInfo!.Value.Index)
            .ToList();
        
        Console.WriteLine($"Found {layerMapFiles.Count} layer map files");
        Console.WriteLine();
        
        if (layerMapFiles.Count == 0)
        {
            Console.WriteLine("WARNING: No layer map files found. Creating terrain with single default material.");
        }
        
        // Create material definitions
        var materials = new List<MaterialDefinition>();
        
        foreach (var layerFile in layerMapFiles)
        {
            var info = layerFile.ParsedInfo!.Value;
            Console.WriteLine($"Loading layer {info.Index}: {info.MaterialName}");
            
            try
            {
                var layerImage = Image.Load<L8>(layerFile.Path);
                materials.Add(new MaterialDefinition(info.MaterialName, layerImage));
                Console.WriteLine($"  ✓ Loaded layer image ({layerImage.Width}x{layerImage.Height})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Failed to load layer image: {ex.Message}");
                // Add material without image
                materials.Add(new MaterialDefinition(info.MaterialName));
            }
        }
        
        // If no materials were loaded, add a default one
        if (materials.Count == 0)
        {
            Console.WriteLine("Adding default material: grass");
            materials.Add(new MaterialDefinition("grass"));
        }
        
        Console.WriteLine();
        Console.WriteLine($"Total materials: {materials.Count}");
        Console.WriteLine();
        
        // Determine terrain size from heightmap
        int terrainSize = heightmap.Width;
        
        // Validate size is power of 2
        if (!TerrainValidator.IsPowerOfTwo(terrainSize))
        {
            Console.WriteLine($"ERROR: Heightmap size {terrainSize} is not a power of 2");
            heightmap.Dispose();
            foreach (var mat in materials.Where(m => m.LayerImage != null))
            {
                mat.LayerImage?.Dispose();
            }
            return;
        }
        
        // Create terrain parameters
        var parameters = new TerrainCreationParameters
        {
            Size = terrainSize,
            MaxHeight = 500.0f, // Adjust as needed for your terrain
            HeightmapImage = heightmap,
            Materials = materials,
            TerrainName = terrainName
        };
        
        // Create output path (using terrain name)
        string outputDir = @"D:\temp\TestMappingTools\_output";
        Directory.CreateDirectory(outputDir);
        string outputPath = Path.Combine(outputDir, $"{terrainName}.ter");
        
        Console.WriteLine($"Output path: {outputPath}");
        Console.WriteLine();
        
        // Create terrain file
        Console.WriteLine("Creating terrain file...");
        var success = await creator.CreateTerrainFileAsync(outputPath, parameters);
        
        Console.WriteLine();
        
        if (success)
        {
            Console.WriteLine("✓ Terrain with multiple materials created successfully!");
            Console.WriteLine($"  Location: {outputPath}");
            
            // Display summary
            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine();
            Console.WriteLine("Summary:");
            Console.WriteLine($"  Terrain name: {terrainName}");
            Console.WriteLine($"  File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / (1024.0 * 1024.0):F2} MB)");
            Console.WriteLine($"  Terrain size: {terrainSize}x{terrainSize}");
            Console.WriteLine($"  Max height: {parameters.MaxHeight}");
            Console.WriteLine($"  Total materials: {materials.Count}");
            Console.WriteLine($"  Materials with layer images: {materials.Count(m => m.LayerImage != null)}");
            
            Console.WriteLine();
            Console.WriteLine("Material list:");
            for (int i = 0; i < materials.Count; i++)
            {
                string hasImage = materials[i].LayerImage != null ? "✓" : "✗";
                Console.WriteLine($"  [{i}] {hasImage} {materials[i].MaterialName}");
            }
        }
        else
        {
            Console.WriteLine("✗ Failed to create terrain.");
        }
        
        // Dispose images
        heightmap.Dispose();
        foreach (var mat in materials.Where(m => m.LayerImage != null))
        {
            mat.LayerImage?.Dispose();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.WriteLine($"Stack trace: {ex.StackTrace}");
    }
}

/// <summary>
/// Parses layer map filename to extract index and material name.
/// Format: {terrainName}_layerMap_[index]_[materialName].png
/// </summary>
/// <param name="fileName">The filename to parse</param>
/// <param name="terrainName">The name of the terrain (e.g., "theTerrain")</param>
/// <returns>Tuple with Index and MaterialName, or null if parsing fails</returns>
static (int Index, string MaterialName)? ParseLayerMapFileName(string fileName, string terrainName)
{
    try
    {
        // Remove extension
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        
        // Expected format: {terrainName}_layerMap_[index]_[materialName]
        string prefix = $"{terrainName}_layerMap_";

        if (!nameWithoutExt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        
        // Remove prefix
        string remainder = nameWithoutExt.Substring(prefix.Length);
        
        // Find first underscore after index
        int underscoreIndex = remainder.IndexOf('_');
        if (underscoreIndex == -1)
            return null;
        
        // Extract index and material name
        string indexStr = remainder.Substring(0, underscoreIndex);
        string materialName = remainder.Substring(underscoreIndex + 1);
        
        if (!int.TryParse(indexStr, out int index))
            return null;
        
        return (index, materialName);
    }
    catch
    {
        return null;
    }
}
