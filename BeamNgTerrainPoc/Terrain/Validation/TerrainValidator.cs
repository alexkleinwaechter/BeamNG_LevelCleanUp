using BeamNgTerrainPoc.Terrain.Models;
using System.IO;

namespace BeamNgTerrainPoc.Terrain.Validation;

/// <summary>
/// Validates terrain creation parameters before processing.
/// </summary>
public static class TerrainValidator
{
    /// <summary>
    /// Validates terrain creation parameters.
    /// </summary>
    /// <param name="parameters">Parameters to validate</param>
    /// <returns>Validation result with errors and warnings</returns>
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
        
        // 3. Heightmap must be provided (either image, path, GeoTIFF, or XYZ)
        bool hasHeightmapSource =
            parameters.HeightmapImage != null ||
            !string.IsNullOrWhiteSpace(parameters.HeightmapPath) ||
            !string.IsNullOrWhiteSpace(parameters.GeoTiffPath) ||
            !string.IsNullOrWhiteSpace(parameters.GeoTiffDirectory) ||
            !string.IsNullOrWhiteSpace(parameters.XyzPath) ||
            (parameters.XyzFilePaths != null && parameters.XyzFilePaths.Length > 0);

        if (!hasHeightmapSource)
        {
            result.Errors.Add("Heightmap is required (provide HeightmapImage, HeightmapPath, GeoTiffPath, GeoTiffDirectory, or XYZ files)");
            result.IsValid = false;
        }
        
        // Validate heightmap path exists if provided
        if (!string.IsNullOrWhiteSpace(parameters.HeightmapPath) && !File.Exists(parameters.HeightmapPath))
        {
            result.Errors.Add($"Heightmap file not found: {parameters.HeightmapPath}");
            result.IsValid = false;
        }
        
        // Validate GeoTIFF path exists if provided
        if (!string.IsNullOrWhiteSpace(parameters.GeoTiffPath) && !File.Exists(parameters.GeoTiffPath))
        {
            result.Errors.Add($"GeoTIFF file not found: {parameters.GeoTiffPath}");
            result.IsValid = false;
        }
        
        // Validate GeoTIFF directory exists if provided
        if (!string.IsNullOrWhiteSpace(parameters.GeoTiffDirectory) && !Directory.Exists(parameters.GeoTiffDirectory))
        {
            result.Errors.Add($"GeoTIFF directory not found: {parameters.GeoTiffDirectory}");
            result.IsValid = false;
        }

        // Validate XYZ path exists if provided
        if (!string.IsNullOrWhiteSpace(parameters.XyzPath) && !File.Exists(parameters.XyzPath))
        {
            result.Errors.Add($"XYZ file not found: {parameters.XyzPath}");
            result.IsValid = false;
        }

        // Validate XYZ file paths exist if provided
        if (parameters.XyzFilePaths != null)
        {
            foreach (var xyzFile in parameters.XyzFilePaths)
            {
                if (!string.IsNullOrWhiteSpace(xyzFile) && !File.Exists(xyzFile))
                {
                    result.Errors.Add($"XYZ file not found: {xyzFile}");
                    result.IsValid = false;
                }
            }
        }

        // Heightmap dimensions must match size (only if image is provided directly)
        if (parameters.HeightmapImage != null)
        {
            if (parameters.HeightmapImage.Width != parameters.Size || 
                parameters.HeightmapImage.Height != parameters.Size)
            {
                result.Errors.Add($"Heightmap dimensions ({parameters.HeightmapImage.Width}x{parameters.HeightmapImage.Height}) don't match terrain size ({parameters.Size}x{parameters.Size})");
                result.IsValid = false;
            }
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
        
        // 6. Validate layer image paths exist (if provided)
        if (parameters.Materials != null)
        {
            for (int i = 0; i < parameters.Materials.Count; i++)
            {
                var layerImagePath = parameters.Materials[i].LayerImagePath;
                if (!string.IsNullOrWhiteSpace(layerImagePath) && !File.Exists(layerImagePath))
                {
                    result.Errors.Add(
                        $"Layer image file not found for material '{parameters.Materials[i].MaterialName}': {layerImagePath}");
                    result.IsValid = false;
                }
            }
        }
        
        // 7. Max height must be positive (unless using GeoTIFF/XYZ which auto-calculates)
        bool isUsingGeoSource = !string.IsNullOrWhiteSpace(parameters.GeoTiffPath) ||
                                !string.IsNullOrWhiteSpace(parameters.GeoTiffDirectory) ||
                                !string.IsNullOrWhiteSpace(parameters.XyzPath) ||
                                (parameters.XyzFilePaths != null && parameters.XyzFilePaths.Length > 0);
        if (parameters.MaxHeight <= 0 && !isUsingGeoSource)
        {
            result.Errors.Add("MaxHeight must be positive (or use GeoTIFF/XYZ import which auto-calculates height)");
            result.IsValid = false;
        }

        // Warning if using geo source without explicit MaxHeight
        if (parameters.MaxHeight <= 0 && isUsingGeoSource)
        {
            result.Warnings.Add("MaxHeight not specified - will use elevation range from elevation data");
        }
        
        // Warnings
        if (parameters.Size > 8192)
        {
            result.Warnings.Add("Size > 8192 may cause memory issues");
        }
        
        // Warning if no layer images provided
        if (parameters.Materials != null)
        {
            int materialsWithoutImages = parameters.Materials.Count(m => string.IsNullOrWhiteSpace(m.LayerImagePath));
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
    
    /// <summary>
    /// Checks if a number is a power of 2.
    /// Useful for validating terrain sizes and other power-of-2 requirements.
    /// </summary>
    /// <param name="x">The number to check</param>
    /// <returns>True if x is a power of 2, false otherwise</returns>
    public static bool IsPowerOfTwo(int x)
    {
        return (x > 0) && ((x & (x - 1)) == 0);
    }
}
