using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents a junction detected from OpenStreetMap data.
/// Includes both explicitly tagged junctions (motorway exits, traffic signals)
/// and geometric intersections (where multiple roads share a node).
/// </summary>
public class OsmJunction
{
    /// <summary>
    /// OSM node ID for reference.
    /// </summary>
    public long OsmNodeId { get; set; }

    /// <summary>
    /// Geographic coordinates (lat/lon) of the junction.
    /// </summary>
    public GeoCoordinate Location { get; set; } = null!;

    /// <summary>
    /// World position in meters (after coordinate transformation).
    /// Set during processing when geo coordinates are converted to local terrain coordinates.
    /// </summary>
    public Vector2 PositionMeters { get; set; }

    /// <summary>
    /// Classified junction type from OSM tags.
    /// </summary>
    public OsmJunctionType Type { get; set; } = OsmJunctionType.Unknown;

    /// <summary>
    /// Name of the junction (if tagged, e.g., motorway exit name).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Reference number (e.g., exit number for motorway_junction).
    /// </summary>
    public string? Reference { get; set; }

    /// <summary>
    /// Number of roads meeting at this junction (from way_cnt query).
    /// A value of 3 indicates a T-junction, 4 indicates crossroads, etc.
    /// </summary>
    public int ConnectedRoadCount { get; set; }

    /// <summary>
    /// Raw OSM tags for additional information.
    /// Useful for accessing non-standard tags or debugging.
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Whether this junction was detected from explicit OSM tags (true)
    /// or from geometric intersection analysis (false).
    /// </summary>
    public bool IsExplicitlyTagged => Type != OsmJunctionType.TJunction &&
                                       Type != OsmJunctionType.CrossRoads &&
                                       Type != OsmJunctionType.ComplexJunction &&
                                       Type != OsmJunctionType.Unknown;

    /// <summary>
    /// Gets a human-readable display name for this junction.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Name))
                return Name;
            if (!string.IsNullOrEmpty(Reference))
                return $"Junction {Reference}";
            return $"{Type} (Node {OsmNodeId})";
        }
    }

    public override string ToString()
    {
        var roadCountInfo = ConnectedRoadCount > 0 ? $", {ConnectedRoadCount} roads" : "";
        return $"OsmJunction[{Type}] at ({Location.Latitude:F5}, {Location.Longitude:F5}){roadCountInfo}: {DisplayName}";
    }
}

/// <summary>
/// Classifies the type of junction based on OSM tags or geometric analysis.
/// </summary>
public enum OsmJunctionType
{
    /// <summary>
    /// Junction type could not be determined.
    /// </summary>
    Unknown = 0,

    // ===== Explicitly tagged junction types (from OSM tags) =====

    /// <summary>
    /// Motorway junction/exit (highway=motorway_junction).
    /// Typically named and numbered interchange exits.
    /// </summary>
    MotorwayJunction,

    /// <summary>
    /// Traffic signals at intersection (highway=traffic_signals).
    /// Controlled intersection with lights.
    /// </summary>
    TrafficSignals,

    /// <summary>
    /// Stop sign at intersection (highway=stop).
    /// Requires vehicles to come to a complete stop.
    /// </summary>
    Stop,

    /// <summary>
    /// Give way / yield sign at intersection (highway=give_way).
    /// Requires vehicles to yield to cross traffic.
    /// </summary>
    GiveWay,

    /// <summary>
    /// Mini roundabout (highway=mini_roundabout).
    /// Small roundabout that can be driven over, represented as a single node.
    /// </summary>
    MiniRoundabout,

    /// <summary>
    /// Turning circle at road end (highway=turning_circle).
    /// Widened area at the end of a road to allow vehicles to turn around.
    /// </summary>
    TurningCircle,

    /// <summary>
    /// Pedestrian crossing (highway=crossing).
    /// Where pedestrians can cross the road.
    /// </summary>
    Crossing,

    // ===== Geometric junction types (from way_cnt analysis) =====

    /// <summary>
    /// T-junction (3 ways meeting).
    /// Detected geometrically where 3 road segments share a node.
    /// </summary>
    TJunction,

    /// <summary>
    /// Crossroads (4 ways meeting).
    /// Detected geometrically where 4 road segments share a node.
    /// </summary>
    CrossRoads,

    /// <summary>
    /// Complex junction (5+ ways meeting).
    /// Detected geometrically where 5 or more road segments share a node.
    /// </summary>
    ComplexJunction
}
