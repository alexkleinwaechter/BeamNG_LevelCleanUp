using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
