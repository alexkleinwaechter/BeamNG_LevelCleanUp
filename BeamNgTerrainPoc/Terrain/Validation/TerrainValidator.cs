using BeamNgTerrainPoc.Terrain.Models;

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
        
        // 3. Heightmap must be provided
        if (parameters.HeightmapImage == null)
        {
            result.Errors.Add("Heightmap image is required");
            result.IsValid = false;
        }
        // Heightmap dimensions must match size
        else if (parameters.HeightmapImage.Width != parameters.Size || 
                 parameters.HeightmapImage.Height != parameters.Size)
        {
            result.Errors.Add($"Heightmap dimensions ({parameters.HeightmapImage.Width}x{parameters.HeightmapImage.Height}) don't match terrain size ({parameters.Size}x{parameters.Size})");
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
    
    /// <summary>
    /// Checks if a number is a power of 2.
    /// </summary>
    private static bool IsPowerOfTwo(int x)
    {
        return (x > 0) && ((x & (x - 1)) == 0);
    }
}
