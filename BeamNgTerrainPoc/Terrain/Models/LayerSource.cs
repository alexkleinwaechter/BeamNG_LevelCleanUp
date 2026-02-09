using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Type of layer source for material placement.
/// </summary>
public enum LayerSourceType
{
    /// <summary>
    /// No layer source specified.
    /// </summary>
    None,
    
    /// <summary>
    /// Layer map from a PNG file.
    /// </summary>
    PngFile,
    
    /// <summary>
    /// Layer derived from selected OSM features.
    /// </summary>
    OsmFeatures
}

/// <summary>
/// Represents a source for material layer data.
/// Can be either a PNG file or a set of OSM features.
/// </summary>
public class LayerSource
{
    /// <summary>
    /// The type of layer source.
    /// </summary>
    public LayerSourceType SourceType { get; set; } = LayerSourceType.None;
    
    /// <summary>
    /// Path to PNG file (when SourceType is PngFile).
    /// </summary>
    public string? PngFilePath { get; set; }
    
    /// <summary>
    /// Selected OSM features (when SourceType is OsmFeatures).
    /// </summary>
    public List<OsmFeatureSelection>? SelectedOsmFeatures { get; set; }
    
    /// <summary>
    /// Creates an empty layer source.
    /// </summary>
    public static LayerSource None() => new() { SourceType = LayerSourceType.None };
    
    /// <summary>
    /// Creates a PNG file layer source.
    /// </summary>
    public static LayerSource FromPng(string path) => new()
    {
        SourceType = LayerSourceType.PngFile,
        PngFilePath = path
    };
    
    /// <summary>
    /// Creates an OSM features layer source.
    /// </summary>
    public static LayerSource FromOsm(List<OsmFeatureSelection> features) => new()
    {
        SourceType = LayerSourceType.OsmFeatures,
        SelectedOsmFeatures = features
    };
    
    /// <summary>
    /// Whether this layer source has valid data.
    /// </summary>
    public bool HasData => SourceType switch
    {
        LayerSourceType.PngFile => !string.IsNullOrEmpty(PngFilePath),
        LayerSourceType.OsmFeatures => SelectedOsmFeatures?.Count > 0,
        _ => false
    };
    
    /// <summary>
    /// Gets a description of this layer source.
    /// </summary>
    public string Description => SourceType switch
    {
        LayerSourceType.PngFile => $"PNG: {Path.GetFileName(PngFilePath)}",
        LayerSourceType.OsmFeatures => $"OSM: {SelectedOsmFeatures?.Count ?? 0} features",
        _ => "None"
    };
}
