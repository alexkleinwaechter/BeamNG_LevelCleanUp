namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Defines a terrain material with an optional layer image for automatic placement.
/// </summary>
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
    
    public MaterialDefinition(string materialName, string? layerImagePath = null)
    {
        MaterialName = materialName;
        LayerImagePath = layerImagePath;
    }
}
