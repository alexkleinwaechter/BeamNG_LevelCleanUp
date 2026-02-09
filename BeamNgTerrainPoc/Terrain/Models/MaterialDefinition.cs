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
    
    /// <summary>
    /// Optional alternative layer source (OSM features, etc.).
    /// When set, this takes precedence over LayerImagePath for OSM-based sources.
    /// </summary>
    public LayerSource? LayerSource { get; set; }
    
    /// <summary>
    /// Optional road smoothing parameters.
    /// If set, this material is treated as a road and the heightmap will be modified
    /// to create smooth, level road surfaces with proper blending into terrain.
    /// </summary>
    public RoadSmoothingParameters? RoadParameters { get; set; }
    
    /// <summary>
    /// Whether this material has any layer source defined (PNG or OSM).
    /// </summary>
    public bool HasLayerSource => 
        !string.IsNullOrEmpty(LayerImagePath) || 
        (LayerSource?.HasData == true);
    
    public MaterialDefinition(string materialName, string? layerImagePath = null, RoadSmoothingParameters? roadParameters = null)
    {
        MaterialName = materialName;
        LayerImagePath = layerImagePath;
        RoadParameters = roadParameters;
    }
    
    /// <summary>
    /// Creates a material definition with an OSM layer source.
    /// </summary>
    public MaterialDefinition(string materialName, LayerSource layerSource, RoadSmoothingParameters? roadParameters = null)
    {
        MaterialName = materialName;
        LayerSource = layerSource;
        RoadParameters = roadParameters;
    }
}
