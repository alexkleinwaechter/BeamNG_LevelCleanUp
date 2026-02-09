namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents a user's selection of an OSM feature for use as a layer source.
/// This is a lightweight reference that can be serialized for presets.
/// </summary>
public class OsmFeatureSelection
{
    /// <summary>
    /// The OSM element ID.
    /// </summary>
    public long FeatureId { get; set; }
    
    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    
    /// <summary>
    /// The geometry type of this feature.
    /// </summary>
    public OsmGeometryType GeometryType { get; set; }
    
    /// <summary>
    /// The category (highway, landuse, natural, etc.).
    /// </summary>
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// The sub-category value (e.g., "primary" for highway=primary).
    /// </summary>
    public string SubCategory { get; set; } = string.Empty;
    
    /// <summary>
    /// Subset of tags useful for display/filtering.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
    
    /// <summary>
    /// Creates a selection from a full OsmFeature.
    /// </summary>
    public static OsmFeatureSelection FromFeature(OsmFeature feature)
    {
        return new OsmFeatureSelection
        {
            FeatureId = feature.Id,
            DisplayName = feature.DisplayName,
            GeometryType = feature.GeometryType,
            Category = feature.Category,
            SubCategory = feature.SubCategory,
            Tags = new Dictionary<string, string>(feature.Tags)
        };
    }
    
    public override string ToString() => DisplayName;
}
