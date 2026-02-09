namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents an OSM node (point with coordinates).
/// </summary>
public class OsmNode
{
    public long Id { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Represents an OSM way (ordered list of node references forming a line or polygon).
/// </summary>
public class OsmWay
{
    public long Id { get; set; }
    public List<long> NodeRefs { get; set; } = new();
    public Dictionary<string, string> Tags { get; set; } = new();
    
    /// <summary>
    /// A way is closed (polygon) if the first and last node refs are the same.
    /// </summary>
    public bool IsClosed => NodeRefs.Count >= 3 && NodeRefs[0] == NodeRefs[^1];
}

/// <summary>
/// Represents an OSM relation (collection of nodes, ways, and other relations).
/// </summary>
public class OsmRelation
{
    public long Id { get; set; }
    public List<OsmMember> Members { get; set; } = new();
    public Dictionary<string, string> Tags { get; set; } = new();
}

/// <summary>
/// Represents a member of an OSM relation.
/// </summary>
public class OsmMember
{
    /// <summary>
    /// Type of the member: "node", "way", or "relation".
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// Reference ID of the member.
    /// </summary>
    public long Ref { get; set; }
    
    /// <summary>
    /// Role of the member in the relation (e.g., "outer", "inner", "forward").
    /// </summary>
    public string Role { get; set; } = string.Empty;
}
