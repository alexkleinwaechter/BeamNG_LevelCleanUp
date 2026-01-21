using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Contains the results of an OSM junction query, including both explicitly tagged
/// junctions and geometrically detected intersections.
/// </summary>
public class OsmJunctionQueryResult
{
    /// <summary>
    /// The bounding box that was queried.
    /// </summary>
    public GeoBoundingBox BoundingBox { get; set; } = null!;

    /// <summary>
    /// All detected junctions within the bounding box.
    /// </summary>
    public List<OsmJunction> Junctions { get; set; } = [];

    /// <summary>
    /// When the query was executed.
    /// </summary>
    public DateTime QueryTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this result was loaded from cache.
    /// </summary>
    public bool IsFromCache { get; set; }

    /// <summary>
    /// Total number of OSM nodes queried (for statistics).
    /// </summary>
    public int TotalNodesQueried { get; set; }

    // ===== Statistics =====

    /// <summary>
    /// Count of junctions that were explicitly tagged in OSM
    /// (motorway_junction, traffic_signals, stop, give_way, etc.).
    /// </summary>
    public int ExplicitJunctionCount => Junctions.Count(j =>
        j.Type != OsmJunctionType.TJunction &&
        j.Type != OsmJunctionType.CrossRoads &&
        j.Type != OsmJunctionType.ComplexJunction &&
        j.Type != OsmJunctionType.Unknown);

    /// <summary>
    /// Count of junctions detected from geometric intersection analysis
    /// (T-junctions, crossroads, complex junctions).
    /// </summary>
    public int GeometricJunctionCount => Junctions.Count - ExplicitJunctionCount;

    /// <summary>
    /// Count of motorway junctions (exits/interchanges).
    /// </summary>
    public int MotorwayJunctionCount => Junctions.Count(j => j.Type == OsmJunctionType.MotorwayJunction);

    /// <summary>
    /// Count of traffic signal controlled junctions.
    /// </summary>
    public int TrafficSignalCount => Junctions.Count(j => j.Type == OsmJunctionType.TrafficSignals);

    /// <summary>
    /// Count of T-junctions (3-way intersections).
    /// </summary>
    public int TJunctionCount => Junctions.Count(j => j.Type == OsmJunctionType.TJunction);

    /// <summary>
    /// Count of crossroads (4-way intersections).
    /// </summary>
    public int CrossRoadsCount => Junctions.Count(j => j.Type == OsmJunctionType.CrossRoads);

    /// <summary>
    /// Count of complex junctions (5+ way intersections).
    /// </summary>
    public int ComplexJunctionCount => Junctions.Count(j => j.Type == OsmJunctionType.ComplexJunction);

    // ===== Filtering =====

    /// <summary>
    /// Gets all explicitly tagged junctions (not geometric detections).
    /// </summary>
    public IEnumerable<OsmJunction> ExplicitJunctions => Junctions.Where(j => j.IsExplicitlyTagged);

    /// <summary>
    /// Gets all geometrically detected junctions.
    /// </summary>
    public IEnumerable<OsmJunction> GeometricJunctions => Junctions.Where(j => !j.IsExplicitlyTagged);

    /// <summary>
    /// Gets junctions of a specific type.
    /// </summary>
    /// <param name="type">The junction type to filter by.</param>
    /// <returns>Junctions matching the specified type.</returns>
    public IEnumerable<OsmJunction> GetByType(OsmJunctionType type) => Junctions.Where(j => j.Type == type);

    /// <summary>
    /// Gets junctions of any of the specified types.
    /// </summary>
    /// <param name="types">The junction types to filter by.</param>
    /// <returns>Junctions matching any of the specified types.</returns>
    public IEnumerable<OsmJunction> GetByTypes(params OsmJunctionType[] types)
    {
        var typeSet = new HashSet<OsmJunctionType>(types);
        return Junctions.Where(j => typeSet.Contains(j.Type));
    }

    public override string ToString()
    {
        return $"OsmJunctionQueryResult: {Junctions.Count} junctions " +
               $"({ExplicitJunctionCount} explicit, {GeometricJunctionCount} geometric) " +
               $"in {BoundingBox}, cached={IsFromCache}";
    }
}
