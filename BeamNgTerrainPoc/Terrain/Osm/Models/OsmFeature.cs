using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Type of structure (bridge, tunnel, etc.) if a feature represents one.
/// </summary>
public enum StructureType
{
    /// <summary>Not a structure - normal road at ground level.</summary>
    None,

    /// <summary>Way passes over an obstacle (water, road, valley, etc.).</summary>
    Bridge,

    /// <summary>Way passes through terrain (underground).</summary>
    Tunnel,

    /// <summary>Covered passage through a building.</summary>
    BuildingPassage,

    /// <summary>Small tunnel for water drainage under road.</summary>
    Culvert
}

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
    
    // ========================================
    // STRUCTURE METADATA (Bridge/Tunnel)
    // ========================================

    /// <summary>
    /// Whether this feature represents a bridge (has bridge=* tag, excluding "no").
    /// </summary>
    public bool IsBridge
    {
        get
        {
            if (!Tags.TryGetValue("bridge", out var bridgeValue))
                return false;

            return !bridgeValue.Equals("no", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Whether this feature represents a tunnel (has tunnel=* or covered=yes tag).
    /// </summary>
    public bool IsTunnel
    {
        get
        {
            if (Tags.TryGetValue("tunnel", out var tunnelValue))
            {
                if (!tunnelValue.Equals("no", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (Tags.TryGetValue("covered", out var coveredValue))
            {
                if (coveredValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Whether this feature is any kind of elevated or underground structure.
    /// </summary>
    public bool IsStructure => IsBridge || IsTunnel;

    /// <summary>
    /// Gets the specific structure type from OSM tags.
    /// </summary>
    public StructureType GetStructureType()
    {
        if (!IsStructure)
            return StructureType.None;

        if (IsBridge)
            return StructureType.Bridge;

        if (Tags.TryGetValue("tunnel", out var tunnelValue))
        {
            return tunnelValue.ToLowerInvariant() switch
            {
                "building_passage" => StructureType.BuildingPassage,
                "culvert" => StructureType.Culvert,
                _ => StructureType.Tunnel
            };
        }

        if (Tags.TryGetValue("covered", out var coveredValue) &&
            coveredValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return StructureType.BuildingPassage;
        }

        return StructureType.Tunnel;
    }

    /// <summary>
    /// Gets the vertical layer from OSM tags (default 0).
    /// Bridges typically have positive layers, tunnels negative.
    /// </summary>
    public int Layer
    {
        get
        {
            if (Tags.TryGetValue("layer", out var layerValue) &&
                int.TryParse(layerValue, out var layer))
            {
                return layer;
            }
            return 0;
        }
    }

    /// <summary>
    /// Gets bridge structure type (beam, arch, suspension, etc.) if specified.
    /// </summary>
    public string? BridgeStructureType
    {
        get
        {
            if (Tags.TryGetValue("bridge:structure", out var structureType))
                return structureType;

            if (Tags.TryGetValue("bridge", out var bridgeValue))
            {
                return bridgeValue.ToLowerInvariant() switch
                {
                    "viaduct" => "viaduct",
                    "cantilever" => "cantilever",
                    "suspension" => "suspension",
                    "movable" => "movable",
                    "aqueduct" => "aqueduct",
                    _ => null
                };
            }

            return null;
        }
    }

    public override string ToString()
    {
        return $"{GeometryType} - {DisplayName} ({Coordinates.Count} coords)";
    }
}
