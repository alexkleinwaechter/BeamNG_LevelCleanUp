using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Type of OSM element.
/// </summary>
public enum OsmFeatureType
{
    Node,
    Way,
    Relation
}

/// <summary>
/// Type of geometry represented by an OSM feature.
/// </summary>
public enum OsmGeometryType
{
    Point,
    LineString,
    Polygon
}

/// <summary>
/// Represents a processed OSM feature with resolved geometry.
/// </summary>
public class OsmFeature
{
    public long Id { get; set; }
    
    /// <summary>
    /// The original OSM element type.
    /// </summary>
    public OsmFeatureType FeatureType { get; set; }
    
    /// <summary>
    /// The geometry type derived from the feature.
    /// </summary>
    public OsmGeometryType GeometryType { get; set; }
    
    /// <summary>
    /// Resolved coordinates of the feature geometry.
    /// For Points: single coordinate
    /// For LineStrings: ordered list of coordinates
    /// For Polygons: closed ring of coordinates (first == last)
    /// </summary>
    public List<GeoCoordinate> Coordinates { get; set; } = new();
    
    /// <summary>
    /// OSM tags associated with this feature.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// For multi-polygons, stores inner rings (holes).
    /// The main Coordinates list is the outer ring.
    /// </summary>
    public List<List<GeoCoordinate>>? InnerRings { get; set; }

    /// <summary>
    /// For multi-part features (e.g., disjoint forest areas).
    /// Each part is a separate polygon/line that belongs to this feature.
    /// </summary>
    public List<List<GeoCoordinate>>? Parts { get; set; }
    
    /// <summary>
    /// Gets a human-readable display name derived from tags.
    /// </summary>
    public string DisplayName
    {
        get
        {
            // Try common name tags in order of preference
            if (Tags.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
                return name;
            if (Tags.TryGetValue("ref", out var refTag) && !string.IsNullOrEmpty(refTag))
                return refTag;
            if (Tags.TryGetValue("description", out var desc) && !string.IsNullOrEmpty(desc))
                return desc;
            
            // Fall back to category + type
            var category = Category;
            var typeValue = GetPrimaryTagValue();
            if (!string.IsNullOrEmpty(typeValue))
                return $"{category}: {typeValue}";
            
            return $"{FeatureType} #{Id}";
        }
    }
    
    /// <summary>
    /// Gets the primary category of this feature (highway, landuse, natural, etc.).
    /// </summary>
    public string Category
    {
        get
        {
            // Check for primary category tags in order of importance
            string[] categoryTags = ["highway", "landuse", "natural", "building", "waterway", 
                                     "railway", "amenity", "leisure", "boundary", "place"];
            
            foreach (var tag in categoryTags)
            {
                if (Tags.ContainsKey(tag))
                    return tag;
            }
            
            return "other";
        }
    }
    
    /// <summary>
    /// Gets the sub-category value (e.g., "primary" for highway=primary).
    /// </summary>
    public string SubCategory => GetPrimaryTagValue();
    
    private string GetPrimaryTagValue()
    {
        string[] categoryTags = ["highway", "landuse", "natural", "building", "waterway", 
                                 "railway", "amenity", "leisure", "boundary", "place"];
        
        foreach (var tag in categoryTags)
        {
            if (Tags.TryGetValue(tag, out var value))
                return value;
        }
        
        return string.Empty;
    }
    
    /// <summary>
    /// Checks if this feature represents a road (highway category).
    /// </summary>
    public bool IsRoad => Category == "highway" && GeometryType == OsmGeometryType.LineString;
    
    /// <summary>
    /// Checks if this feature represents a landuse area.
    /// </summary>
    public bool IsLanduse => Category == "landuse" && GeometryType == OsmGeometryType.Polygon;
    
    /// <summary>
    /// Checks if this feature represents a natural area (forest, water, etc.).
    /// </summary>
    public bool IsNatural => Category == "natural" && GeometryType == OsmGeometryType.Polygon;
    
    /// <summary>
    /// Checks if this feature represents a waterway.
    /// </summary>
    public bool IsWaterway => Category == "waterway";
    
    /// <summary>
    /// Checks if this feature is part of a roundabout (junction=roundabout tag).
    /// Roundabout segments need special handling as they are often split into
    /// multiple ways at connecting road intersections.
    /// </summary>
    public bool IsRoundabout => 
        Tags.TryGetValue("junction", out var junction) && 
        junction.Equals("roundabout", StringComparison.OrdinalIgnoreCase);
    
    public override string ToString()
    {
        return $"{GeometryType} - {DisplayName} ({Coordinates.Count} coords)";
    }
}
