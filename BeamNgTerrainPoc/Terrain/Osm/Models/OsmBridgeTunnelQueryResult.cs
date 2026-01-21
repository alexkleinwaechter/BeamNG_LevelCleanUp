using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Result of querying bridges and tunnels from OSM.
/// </summary>
public class OsmBridgeTunnelQueryResult
{
    /// <summary>List of all bridge/tunnel structures found.</summary>
    public List<OsmBridgeTunnel> Structures { get; set; } = [];

    /// <summary>The bounding box that was queried.</summary>
    public GeoBoundingBox? BoundingBox { get; set; }

    /// <summary>When this result was queried/cached.</summary>
    public DateTime QueryTime { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this result came from cache.</summary>
    public bool IsFromCache { get; set; }

    // ===== Statistics =====

    /// <summary>Number of bridges found.</summary>
    public int BridgeCount => Structures.Count(s => s.StructureType == StructureType.Bridge);

    /// <summary>Number of tunnels found (includes building passages).</summary>
    public int TunnelCount => Structures.Count(s =>
        s.StructureType == StructureType.Tunnel ||
        s.StructureType == StructureType.BuildingPassage);

    /// <summary>Number of culverts found.</summary>
    public int CulvertCount => Structures.Count(s => s.StructureType == StructureType.Culvert);

    /// <summary>Total number of structures found.</summary>
    public int TotalCount => Structures.Count;

    // ===== Filtering =====

    /// <summary>
    /// Gets all bridge structures.
    /// </summary>
    public IEnumerable<OsmBridgeTunnel> Bridges => Structures.Where(s => s.IsBridge);

    /// <summary>
    /// Gets all tunnel structures (including building passages).
    /// </summary>
    public IEnumerable<OsmBridgeTunnel> Tunnels => Structures.Where(s => s.IsTunnel);

    /// <summary>
    /// Gets all culvert structures.
    /// </summary>
    public IEnumerable<OsmBridgeTunnel> Culverts => Structures.Where(s => s.IsCulvert);

    /// <summary>
    /// Gets structures of a specific type.
    /// </summary>
    /// <param name="type">The structure type to filter by.</param>
    /// <returns>Structures matching the specified type.</returns>
    public IEnumerable<OsmBridgeTunnel> GetByType(StructureType type) =>
        Structures.Where(s => s.StructureType == type);

    /// <summary>
    /// Gets structures for a specific highway type.
    /// </summary>
    /// <param name="highwayType">The OSM highway type (e.g., "motorway", "primary").</param>
    /// <returns>Structures on roads matching the specified highway type.</returns>
    public IEnumerable<OsmBridgeTunnel> GetByHighwayType(string highwayType) =>
        Structures.Where(s => s.HighwayType == highwayType);

    /// <summary>
    /// Gets structures at a specific layer level.
    /// </summary>
    /// <param name="layer">The layer level (0 = ground, positive = elevated, negative = underground).</param>
    /// <returns>Structures at the specified layer.</returns>
    public IEnumerable<OsmBridgeTunnel> GetByLayer(int layer) =>
        Structures.Where(s => s.Layer == layer);

    /// <summary>
    /// Gets elevated structures (layer > 0 or bridges).
    /// </summary>
    public IEnumerable<OsmBridgeTunnel> ElevatedStructures =>
        Structures.Where(s => s.Layer > 0 || s.IsBridge);

    /// <summary>
    /// Gets underground structures (layer < 0 or tunnels).
    /// </summary>
    public IEnumerable<OsmBridgeTunnel> UndergroundStructures =>
        Structures.Where(s => s.Layer < 0 || s.IsTunnel);

    /// <summary>
    /// Returns a summary string of the query result.
    /// </summary>
    public override string ToString()
    {
        var cacheInfo = IsFromCache ? " (from cache)" : "";
        return $"OsmBridgeTunnelQueryResult: {BridgeCount} bridges, {TunnelCount} tunnels, {CulvertCount} culverts{cacheInfo}";
    }
}
