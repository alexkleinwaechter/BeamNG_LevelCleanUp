using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Processing;
using BeamNgTerrainPoc.Terrain.Services;
using BeamNgTerrainPoc.Terrain.Validation;
using Grille.BeamNG;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain;

/// <summary>
/// Main API for creating BeamNG terrain (.ter) files.
/// </summary>
public class TerrainCreator
{
    private readonly RoadSmoothingService _roadSmoothingService;
    
    public TerrainCreator()
    {
        _roadSmoothingService = new RoadSmoothingService();
    }
    
    /// <summary>
    /// Creates a BeamNG terrain file from the provided parameters (async version).
    /// </summary>
    /// <param name="outputPath">Path where the .ter file will be saved</param>
    /// <param name="parameters">Terrain creation parameters</param>
    /// <returns>True if terrain was created successfully, false otherwise</returns>
    public async Task<bool> CreateTerrainFileAsync(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        // 1. Validate inputs
        Console.WriteLine("Validating parameters...");
        var validation = TerrainValidator.Validate(parameters);
        
        if (!validation.IsValid)
        {
            Console.WriteLine("Validation failed:");
            foreach (var error in validation.Errors)
            {
                Console.WriteLine($"  ERROR: {error}");
            }
            return false;
        }
        
        // Show warnings
        foreach (var warning in validation.Warnings)
        {
            Console.WriteLine($"  WARNING: {warning}");
        }
        
        Image<L16>? heightmapImage = null;
        bool shouldDisposeHeightmap = false;
        
        try
        {
            // 2. Load or use heightmap
            if (parameters.HeightmapImage != null)
            {
                heightmapImage = parameters.HeightmapImage;
                shouldDisposeHeightmap = false; // Caller owns this
            }
            else if (!string.IsNullOrWhiteSpace(parameters.HeightmapPath))
            {
                Console.WriteLine($"Loading heightmap from: {parameters.HeightmapPath}");
                heightmapImage = Image.Load<L16>(parameters.HeightmapPath);
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
            }
            else
            {
                Console.WriteLine("ERROR: No heightmap provided (HeightmapImage or HeightmapPath required)");
                return false;
            }
            
            // 3. Process heightmap
            Console.WriteLine("Processing heightmap...");
            var heights = HeightmapProcessor.ProcessHeightmap(
                heightmapImage,
                parameters.MaxHeight);
            
            // 3a. Apply road smoothing if road materials exist
            SmoothingResult? smoothingResult = null;
            if (parameters.Materials.Any(m => m.RoadParameters != null))
            {
                Console.WriteLine("Applying road smoothing...");
                
                smoothingResult = ApplyRoadSmoothing(
                    heights, 
                    parameters.Materials, 
                    parameters.MetersPerPixel,
                    parameters.Size);
                
                if (smoothingResult != null)
                {
                    heights = ConvertTo1DArray(smoothingResult.ModifiedHeightMap);
                    Console.WriteLine("Road smoothing completed successfully!");
                }
            }
            
            // 4. Process material layers
            Console.WriteLine("Processing material layers...");
            var materialIndices = MaterialLayerProcessor.ProcessMaterialLayers(
                parameters.Materials,
                parameters.Size);
            
            // 5. Create Grille.BeamNG.Lib Terrain object
            Console.WriteLine("Building terrain data structure...");
            var materialNames = parameters.Materials
                .Select(m => m.MaterialName)
                .ToList();
            
            var terrain = new Grille.BeamNG.Terrain(
                parameters.Size,
                materialNames);
            
            // 6. Fill terrain data
            Console.WriteLine("Filling terrain data...");
            for (int i = 0; i < terrain.Data.Length; i++)
            {
                terrain.Data[i] = new TerrainData
                {
                    Height = heights[i],
                    Material = materialIndices[i],
                    IsHole = false
                };
            }
            
            // 7. Save using Grille.BeamNG.Lib
            Console.WriteLine($"Writing terrain file to {outputPath}...");
            
            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }
            
            // Save synchronously (the Save method is synchronous)
            await Task.Run(() => terrain.Save(outputPath, parameters.MaxHeight));
            
            // 7a. Save modified heightmap if road smoothing was applied
            if (smoothingResult != null)
            {
                Console.WriteLine("Saving modified heightmap...");
                SaveModifiedHeightmap(
                    smoothingResult.ModifiedHeightMap,
                    outputPath,
                    parameters.MaxHeight,
                    parameters.Size);
            }
            
            Console.WriteLine("Terrain file created successfully!");
            
            // Display statistics
            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine($"File size: {fileInfo.Length:N0} bytes");
            Console.WriteLine($"Terrain size: {parameters.Size}x{parameters.Size}");
            Console.WriteLine($"Max height: {parameters.MaxHeight}");
            Console.WriteLine($"Materials: {materialNames.Count}");
            
            // Display road smoothing statistics if available
            if (smoothingResult != null)
            {
                DisplaySmoothingStatistics(smoothingResult.Statistics);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: Failed to create terrain file: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            return false;
        }
        finally
        {
            // Dispose heightmap if we loaded it
            if (shouldDisposeHeightmap && heightmapImage != null)
            {
                heightmapImage.Dispose();
            }
        }
    }
    
    /// <summary>
    /// Creates a BeamNG terrain file from the provided parameters (synchronous version).
    /// </summary>
    /// <param name="outputPath">Path where the .ter file will be saved</param>
    /// <param name="parameters">Terrain creation parameters</param>
    /// <returns>True if terrain was created successfully, false otherwise</returns>
    public bool CreateTerrainFile(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        return CreateTerrainFileAsync(outputPath, parameters).GetAwaiter().GetResult();
    }
    
    private SmoothingResult? ApplyRoadSmoothing(
        float[] heightMap1D,
        List<MaterialDefinition> materials,
        float metersPerPixel,
        int size)
    {
        // Convert 1D heightmap to 2D
        var heightMap2D = ConvertTo2DArray(heightMap1D, size);
        
        SmoothingResult? finalResult = null;
        
        foreach (var material in materials.Where(m => m.RoadParameters != null))
        {
            if (string.IsNullOrEmpty(material.LayerImagePath))
            {
                Console.WriteLine($"Warning: Road material '{material.MaterialName}' has no layer image path");
                continue;
            }
            
            try
            {
                // Load road layer
                var roadLayer = LoadLayerImage(material.LayerImagePath, size);
                
                // Apply smoothing
                var result = _roadSmoothingService.SmoothRoadsInHeightmap(
                    heightMap2D,
                    roadLayer,
                    material.RoadParameters!,
                    metersPerPixel);
                
                heightMap2D = result.ModifiedHeightMap;
                finalResult = result;
                
                Console.WriteLine($"Applied road smoothing for material: {material.MaterialName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error smoothing road '{material.MaterialName}': {ex.Message}");
            }
        }
        
        return finalResult;
    }
    
    private byte[,] LoadLayerImage(string layerPath, int expectedSize)
    {
        using var image = Image.Load<L8>(layerPath);
        
        if (image.Width != expectedSize || image.Height != expectedSize)
        {
            throw new InvalidOperationException(
                $"Layer image size ({image.Width}x{image.Height}) does not match terrain size ({expectedSize}x{expectedSize})");
        }
        
        var layer = new byte[expectedSize, expectedSize];
        
        for (int y = 0; y < expectedSize; y++)
        {
            for (int x = 0; x < expectedSize; x++)
            {
                layer[y, x] = image[x, y].PackedValue;
            }
        }
        
        return layer;
    }
    
    private float[,] ConvertTo2DArray(float[] array1D, int size)
    {
        var array2D = new float[size, size];
        
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                array2D[y, x] = array1D[y * size + x];
            }
        }
        
        return array2D;
    }
    
    private float[] ConvertTo1DArray(float[,] array2D)
    {
        int size = array2D.GetLength(0);
        var array1D = new float[size * size];
        
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                array1D[y * size + x] = array2D[y, x];
            }
        }
        
        return array1D;
    }
    
    private void SaveModifiedHeightmap(
        float[,] modifiedHeights,
        string outputPath,
        float maxHeight,
        int size)
    {
        try
        {
            // Create output path for heightmap (same directory as .ter file)
            var outputDir = Path.GetDirectoryName(outputPath);
            var terrainName = Path.GetFileNameWithoutExtension(outputPath);
            var heightmapOutputPath = Path.Combine(outputDir!, $"{terrainName}_smoothed_heightmap.png");
            
            // Convert float heights back to 16-bit heightmap
            using var heightmapImage = new Image<L16>(size, size);
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Convert height (0.0 to maxHeight) to 16-bit value (0 to 65535)
                    float normalizedHeight = modifiedHeights[y, x] / maxHeight;
                    ushort pixelValue = (ushort)Math.Clamp(normalizedHeight * 65535f, 0, 65535);
                    heightmapImage[x, y] = new L16(pixelValue);
                }
            }
            
            heightmapImage.SaveAsPng(heightmapOutputPath);
            Console.WriteLine($"Saved modified heightmap to: {heightmapOutputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to save modified heightmap: {ex.Message}");
            // Don't throw - this is optional output
        }
    }
    
    private void DisplaySmoothingStatistics(SmoothingStatistics stats)
    {
        Console.WriteLine("\n=== Road Smoothing Statistics ===");
        Console.WriteLine($"Pixels modified: {stats.PixelsModified:N0}");
        Console.WriteLine($"Max road slope: {stats.MaxRoadSlope:F2}°");
        Console.WriteLine($"Max discontinuity: {stats.MaxDiscontinuity:F3}m");
        Console.WriteLine($"Cut volume: {stats.TotalCutVolume:F2} m³");
        Console.WriteLine($"Fill volume: {stats.TotalFillVolume:F2} m³");
        Console.WriteLine($"Constraints met: {stats.MeetsAllConstraints}");
        
        if (stats.ConstraintViolations.Any())
        {
            Console.WriteLine("Constraint violations:");
            foreach (var violation in stats.ConstraintViolations)
            {
                Console.WriteLine($"  - {violation}");
            }
        }
        
        Console.WriteLine("================================\n");
    }
}
