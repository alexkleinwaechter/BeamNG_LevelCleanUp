namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents an OSM route relation (type=route, route=road).
/// 
/// Route relations group multiple OSM ways into a logical road route (e.g., "B51", "A1").
/// The member list provides explicit ordering of ways along the route, which is more
/// reliable than geometric or name-based matching for assembling long continuous splines.
/// 
/// Example Overpass response:
/// <code>
/// {
///   "type": "relation",
///   "id": 987654,
///   "tags": {"type": "route", "route": "road", "ref": "B51", "name": "Bundesstraße 51"},
///   "members": [
///     {"type": "way", "ref": 111, "role": "forward"},
///     {"type": "way", "ref": 222, "role": "forward"},
///     {"type": "way", "ref": 333, "role": ""}
///   ]
/// }
/// </code>
/// </summary>
public class RouteRelation
{
    /// <summary>
    /// The OSM relation ID.
    /// </summary>
    public long RelationId { get; set; }

    /// <summary>
    /// OSM tags on the relation (name, ref, route, network, etc.).
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Ordered list of member ways in this route relation.
    /// The order reflects the sequence defined in the relation (which may not always be
    /// correct in OSM data, but is the best available ordering information).
    /// </summary>
    public List<RouteRelationMember> Members { get; set; } = new();

    /// <summary>
    /// Route name (e.g., "Bundesstraße 51"), or null if not tagged.
    /// </summary>
    public string? Name => Tags.GetValueOrDefault("name");

    /// <summary>
    /// Route reference number (e.g., "B51", "A1"), or null if not tagged.
    /// </summary>
    public string? Ref => Tags.GetValueOrDefault("ref");

    /// <summary>
    /// The set of distinct way IDs in this relation (for fast lookup).
    /// </summary>
    public HashSet<long> MemberWayIds => new(Members.Select(m => m.WayId));

    public override string ToString()
    {
        var label = Ref ?? Name ?? $"Relation #{RelationId}";
        return $"RouteRelation {label} ({Members.Count} members)";
    }
}

/// <summary>
/// A single member way within a route relation, preserving the relation's ordering and role.
/// </summary>
public class RouteRelationMember
{
    /// <summary>
    /// The OSM way ID of this member.
    /// </summary>
    public long WayId { get; set; }

    /// <summary>
    /// The role of this member in the relation.
    /// Common values: "forward" (way direction matches route direction),
    /// "backward" (way direction is reversed), or "" (empty, unspecified direction).
    /// </summary>
    public string Role { get; set; } = "";

    public override string ToString() => $"Way {WayId} (role={Role})";
}
