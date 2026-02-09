using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents a detected roundabout composed of one or more OSM ways.
/// A roundabout is a closed circular road junction where traffic flows
/// continuously around a central island.
/// </summary>
public class OsmRoundabout
{
    /// <summary>
    /// Unique identifier for this roundabout (derived from first way ID).
    /// </summary>
    public long Id { get; set; }
    
    /// <summary>
    /// All OSM way IDs that make up this roundabout.
    /// A single roundabout may be composed of multiple ways (split at connecting roads).
    /// </summary>
    public List<long> WayIds { get; set; } = [];
    
    /// <summary>
    /// The merged, ordered coordinates forming the complete roundabout ring.
    /// First point equals last point (closed ring).
    /// </summary>
    public List<GeoCoordinate> RingCoordinates { get; set; } = [];
    
    /// <summary>
    /// Approximate center point of the roundabout.
    /// Calculated as the centroid of all ring coordinates.
    /// </summary>
    public GeoCoordinate Center { get; set; } = null!;
    
    /// <summary>
    /// Approximate radius of the roundabout in meters.
    /// Calculated as average distance from center to ring points.
    /// </summary>
    public double RadiusMeters { get; set; }
    
    /// <summary>
    /// Connection points where other roads join the roundabout.
    /// These are detected by finding where non-roundabout ways share
    /// nodes with the roundabout ring.
    /// </summary>
    public List<RoundaboutConnection> Connections { get; set; } = [];
    
    /// <summary>
    /// Tags from the primary roundabout way (e.g., highway type, name).
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
    
    /// <summary>
    /// The original OSM features that make up this roundabout.
    /// Stored for reference during processing.
    /// </summary>
    public List<OsmFeature> OriginalFeatures { get; set; } = [];
    
    /// <summary>
    /// Tolerance for coordinate matching when checking if ring is closed.
    /// Approximately 0.1 meters at the equator.
    /// </summary>
    private const double CoordinateToleranceDegrees = 0.000001;
    
    /// <summary>
    /// Whether this roundabout ring is properly closed (first == last coordinate).
    /// </summary>
    public bool IsClosed => RingCoordinates.Count >= 3 && 
        Math.Abs(RingCoordinates[0].Longitude - RingCoordinates[^1].Longitude) < CoordinateToleranceDegrees &&
        Math.Abs(RingCoordinates[0].Latitude - RingCoordinates[^1].Latitude) < CoordinateToleranceDegrees;
    
    /// <summary>
    /// Whether this roundabout has at least 3 connection points (typical minimum).
    /// </summary>
    public bool HasMinimumConnections => Connections.Count >= 3;
    
    /// <summary>
    /// Gets a human-readable display name for this roundabout.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (Tags.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
                return name;
            if (Tags.TryGetValue("ref", out var refTag) && !string.IsNullOrEmpty(refTag))
                return $"Roundabout {refTag}";
            return $"Roundabout #{Id}";
        }
    }
    
    public override string ToString()
    {
        return $"{DisplayName}: {WayIds.Count} way(s), {RingCoordinates.Count} coords, " +
               $"radius ~{RadiusMeters:F1}m, {Connections.Count} connections, closed={IsClosed}";
    }
}

/// <summary>
/// Represents a connection point where a road joins a roundabout.
/// </summary>
public class RoundaboutConnection
{
    /// <summary>
    /// The OSM way ID of the connecting road.
    /// </summary>
    public long ConnectingWayId { get; set; }
    
    /// <summary>
    /// The coordinate where the connection occurs on the roundabout ring.
    /// </summary>
    public GeoCoordinate ConnectionPoint { get; set; } = null!;
    
    /// <summary>
    /// The index into the roundabout's RingCoordinates where this connection occurs.
    /// Used for efficient lookup during junction creation.
    /// </summary>
    public int RingCoordinateIndex { get; set; }
    
    /// <summary>
    /// Angle (in degrees, 0-360) around the roundabout center where this connection is.
    /// 0 = East, 90 = North, 180 = West, 270 = South.
    /// </summary>
    public double AngleDegrees { get; set; }
    
    /// <summary>
    /// Whether the connecting road is entering or exiting (based on OSM oneway if present).
    /// </summary>
    public RoundaboutConnectionDirection Direction { get; set; }
    
    /// <summary>
    /// The OsmFeature of the connecting road.
    /// </summary>
    public OsmFeature? ConnectingRoad { get; set; }
    
    /// <summary>
    /// The index in the connecting road's coordinates where the connection occurs.
    /// Used to determine if connection is at start or end of road.
    /// </summary>
    public int ConnectingRoadCoordinateIndex { get; set; }
    
    public override string ToString()
    {
        return $"Connection: Way {ConnectingWayId} at {AngleDegrees:F1}° ({Direction})";
    }
}

/// <summary>
/// Direction of traffic at a roundabout connection.
/// </summary>
public enum RoundaboutConnectionDirection
{
    /// <summary>
    /// Cannot determine direction (no oneway tag or bidirectional).
    /// </summary>
    Unknown,
    
    /// <summary>
    /// Road leads into the roundabout (entry arm).
    /// </summary>
    Entry,
    
    /// <summary>
    /// Road leads out of the roundabout (exit arm).
    /// </summary>
    Exit,
    
    /// <summary>
    /// Road is bidirectional (both entry and exit).
    /// </summary>
    Bidirectional
}
