using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Logging;
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
///     Main API for creating BeamNG terrain (.ter) files.
/// </summary>
public class TerrainCreator
{
    private readonly MultiMaterialRoadSmoother _multiMaterialSmoother;

    public TerrainCreator()
    {
        _multiMaterialSmoother = new MultiMaterialRoadSmoother();
    }

    /// <summary>
    ///     Creates a BeamNG terrain file from the provided parameters (async version).
    /// </summary>
    /// <param name="outputPath">Path where the .ter file will be saved</param>
    /// <param name="parameters">Terrain creation parameters</param>
    /// <returns>True if terrain was created successfully, false otherwise</returns>
    public async Task<bool> CreateTerrainFileAsync(
        string outputPath,
        TerrainCreationParameters parameters)
    {
        // 1. Validate inputs
        TerrainLogger.Info("Validating parameters...");
        var validation = TerrainValidator.Validate(parameters);

        if (!validation.IsValid)
        {
            TerrainLogger.Error("Validation failed:");
            foreach (var error in validation.Errors) TerrainLogger.Error($"  {error}");
            return false;
        }

        // Show warnings
        foreach (var warning in validation.Warnings) TerrainLogger.Warning($"  {warning}");

        Image<L16>? heightmapImage = null;
        var shouldDisposeHeightmap = false;

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
                TerrainLogger.Info($"Loading heightmap from: {parameters.HeightmapPath}");
                heightmapImage = Image.Load<L16>(parameters.HeightmapPath);
                shouldDisposeHeightmap = true; // We loaded it, we dispose it
            }
            else
            {
                TerrainLogger.Error("No heightmap provided (HeightmapImage or HeightmapPath required)");
                return false;
            }

            // 3. Process heightmap
            TerrainLogger.Info("Processing heightmap...");
            var heights = HeightmapProcessor.ProcessHeightmap(
                heightmapImage,
                parameters.MaxHeight);

            // 3a. Apply road smoothing if road materials exist
            SmoothingResult? smoothingResult = null;
            if (parameters.Materials.Any(m => m.RoadParameters != null))
            {
                TerrainLogger.Info("Applying road smoothing...");

                smoothingResult = ApplyRoadSmoothing(
                    heights,
                    parameters.Materials,
                    parameters.MetersPerPixel,
                    parameters.Size,
                    parameters.EnableCrossMaterialHarmonization);

                if (smoothingResult != null)
                {
                    heights = ConvertTo1DArray(smoothingResult.ModifiedHeightMap);
                    TerrainLogger.Info("Road smoothing completed successfully!");
                }
            }

            // 4. Process material layers
            TerrainLogger.Info("Processing material layers...");
            var materialIndices = MaterialLayerProcessor.ProcessMaterialLayers(
                parameters.Materials,
                parameters.Size);

            // 5. Create Grille.BeamNG.Lib Terrain object
            TerrainLogger.Info("Building terrain data structure...");
            var materialNames = parameters.Materials
                .Select(m => m.MaterialName)
                .ToList();

            var terrain = new Grille.BeamNG.Terrain(
                parameters.Size,
                materialNames);

            // 6. Fill terrain data
            TerrainLogger.Info("Filling terrain data...");
            for (var i = 0; i < terrain.Data.Length; i++)
                terrain.Data[i] = new TerrainData
                {
                    Height = heights[i],
                    Material = materialIndices[i],
                    IsHole = false
                };

            // 7. Save using Grille.BeamNG.Lib
            TerrainLogger.Info($"Writing terrain file to {outputPath}...");

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

            // Save synchronously (the Save method is synchronous)
            await Task.Run(() => terrain.Save(outputPath, parameters.MaxHeight));

            // 8. Write terrain.json metadata file
            TerrainLogger.Info("Writing terrain.json metadata file...");
            await WriteTerrainJsonAsync(outputPath, parameters);

            // 7a. Save modified heightmap if road smoothing was applied
            if (smoothingResult != null)
            {
                TerrainLogger.Info("Saving modified heightmap...");
                SaveModifiedHeightmap(
                    smoothingResult.ModifiedHeightMap,
                    outputPath,
                    parameters.MaxHeight,
                    parameters.Size);
            }

            TerrainLogger.Info("Terrain file created successfully!");

            // Display statistics
            var fileInfo = new FileInfo(outputPath);
            TerrainLogger.Info($"File size: {fileInfo.Length:N0} bytes");
            TerrainLogger.Info($"Terrain size: {parameters.Size}x{parameters.Size}");
            TerrainLogger.Info($"Max height: {parameters.MaxHeight}");
            TerrainLogger.Info($"Materials: {materialNames.Count}");

            // Display road smoothing statistics if available
            if (smoothingResult != null) DisplaySmoothingStatistics(smoothingResult.Statistics);

            return true;
        }
        catch (Exception ex)
        {
            TerrainLogger.Error($"Failed to create terrain file: {ex.Message}");
            TerrainLogger.Error($"Stack trace: {ex.StackTrace}");
            return false;
        }
        finally
        {
            // Dispose heightmap if we loaded it
            if (shouldDisposeHeightmap && heightmapImage != null) heightmapImage.Dispose();
        }
    }

    /// <summary>
    ///     Creates a BeamNG terrain file from the provided parameters (synchronous version).
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
        int size,
        bool enableCrossMaterialHarmonization)
    {
        // Convert 1D heightmap to 2D (already flipped by HeightmapProcessor)
        var heightMap2D = ConvertTo2DArray(heightMap1D, size);

        // Use the multi-material smoother for cross-material junction harmonization
        var result = _multiMaterialSmoother.SmoothAllRoads(
            heightMap2D,
            materials,
            metersPerPixel,
            size,
            enableCrossMaterialHarmonization);

        return result;
    }

    private float[,] ConvertTo2DArray(float[] array1D, int size)
    {
        var array2D = new float[size, size];

        // array1D is already flipped by HeightmapProcessor (bottom-up)
        // Just unpack it into 2D with same orientation
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            array2D[y, x] = array1D[y * size + x];

        return array2D;
    }

    private float[] ConvertTo1DArray(float[,] array2D)
    {
        var size = array2D.GetLength(0);
        var array1D = new float[size * size];

        // Pack 2D into 1D maintaining the orientation
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            array1D[y * size + x] = array2D[y, x];

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

            // modifiedHeights is in BeamNG orientation (bottom-up)
            // ImageSharp expects top-down, so flip Y when writing
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                // Flip Y to convert from bottom-up (BeamNG) to top-down (ImageSharp)
                var flippedY = size - 1 - y;

                // Convert height (0.0 to maxHeight) to 16-bit value (0 to 65535)
                var normalizedHeight = modifiedHeights[y, x] / maxHeight;
                var pixelValue = (ushort)Math.Clamp(normalizedHeight * 65535f, 0, 65535);

                heightmapImage[x, flippedY] = new L16(pixelValue);
            }

            heightmapImage.SaveAsPng(heightmapOutputPath);
            TerrainLogger.Info($"Saved modified heightmap to: {heightmapOutputPath}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to save modified heightmap: {ex.Message}");
            // Don't throw - this is optional output
        }
    }

    private void DisplaySmoothingStatistics(SmoothingStatistics stats)
    {
        TerrainLogger.Info("=== Road Smoothing Statistics ===");
        TerrainLogger.Info($"Pixels modified: {stats.PixelsModified:N0}");
        TerrainLogger.Info($"Max road slope: {stats.MaxRoadSlope:F2}°");
        TerrainLogger.Info($"Max discontinuity: {stats.MaxDiscontinuity:F3}m");
        TerrainLogger.Info($"Cut volume: {stats.TotalCutVolume:F2} m³");
        TerrainLogger.Info($"Fill volume: {stats.TotalFillVolume:F2} m³");
        TerrainLogger.Info($"Constraints met: {stats.MeetsAllConstraints}");

        if (stats.ConstraintViolations.Any())
        {
            TerrainLogger.Warning("Constraint violations:");
            foreach (var violation in stats.ConstraintViolations) TerrainLogger.Warning($"  - {violation}");
        }

        TerrainLogger.Info("================================");
    }

    /// <summary>
    ///     Writes the terrain.json metadata file alongside the .ter file.
    /// </summary>
    private async Task WriteTerrainJsonAsync(string terFilePath, TerrainCreationParameters parameters)
    {
        try
        {
            // Extract level name from the output path
            // Expected structure: .../levels/levelname/theTerrain.ter
            var levelName = ExtractLevelName(terFilePath);

            // Create the metadata
            var metadata = TerrainJsonMetadata.FromParameters(parameters, levelName);

            // Build the output path for terrain.json
            var outputDir = Path.GetDirectoryName(terFilePath)!;
            var terrainJsonPath = Path.Combine(outputDir, $"{parameters.TerrainName}.terrain.json");

            // Serialize with pretty printing
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var jsonContent = JsonSerializer.Serialize(metadata, jsonOptions);
            await File.WriteAllTextAsync(terrainJsonPath, jsonContent);

            TerrainLogger.Info($"Wrote terrain metadata to: {terrainJsonPath}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to write terrain.json: {ex.Message}");
            // Don't throw - this is optional output, the .ter file is the critical one
        }
    }

    /// <summary>
    ///     Extracts the level name from a file path.
    ///     Looks for "levels" folder and takes the folder name after it.
    ///     Falls back to parent folder name if "levels" pattern not found.
    /// </summary>
    private string ExtractLevelName(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
                return "unknown";

            // Split path and look for "levels" folder
            var parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            for (var i = 0; i < parts.Length - 1; i++)
                if (parts[i].Equals("levels", StringComparison.OrdinalIgnoreCase))
                    return parts[i + 1];

            // Fallback: use the immediate parent folder name
            return new DirectoryInfo(directory).Name;
        }
        catch
        {
            return "unknown";
        }
    }
}