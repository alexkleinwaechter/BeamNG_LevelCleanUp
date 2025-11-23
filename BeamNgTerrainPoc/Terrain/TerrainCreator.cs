using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Processing;
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
            
            Console.WriteLine("Terrain file created successfully!");
            
            // Display statistics
            var fileInfo = new FileInfo(outputPath);
            Console.WriteLine($"File size: {fileInfo.Length:N0} bytes");
            Console.WriteLine($"Terrain size: {parameters.Size}x{parameters.Size}");
            Console.WriteLine($"Max height: {parameters.MaxHeight}");
            Console.WriteLine($"Materials: {materialNames.Count}");
            
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
}
