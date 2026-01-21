using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Interface for querying junction data from OpenStreetMap via the Overpass API.
/// This service queries both explicitly tagged junctions (motorway exits, traffic signals)
/// and geometric intersections (nodes shared by multiple highway ways).
/// </summary>
public interface IOsmJunctionQueryService
{
    /// <summary>
    /// Queries all junction nodes in the bounding box from OSM.
    /// This includes both explicitly tagged junctions and geometric intersections.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing all detected junctions.</returns>
    Task<OsmJunctionQueryResult> QueryJunctionsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries junctions with specific type filtering.
    /// Use this for targeted queries when you only need certain junction types.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="types">Array of junction types to include in the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing junctions of the specified types.</returns>
    Task<OsmJunctionQueryResult> QueryJunctionsByTypeAsync(
        GeoBoundingBox bbox,
        OsmJunctionType[] types,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries only explicitly tagged junctions (no geometric detection).
    /// This is faster but may miss some intersections that aren't explicitly tagged.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing only explicitly tagged junctions.</returns>
    Task<OsmJunctionQueryResult> QueryExplicitJunctionsOnlyAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries only geometric intersections (nodes shared by 3+ highway ways).
    /// Does not include explicitly tagged junctions.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="minimumWayCount">Minimum number of ways that must share a node (default 3 for T-junctions).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing geometric intersection junctions.</returns>
    Task<OsmJunctionQueryResult> QueryGeometricIntersectionsAsync(
        GeoBoundingBox bbox,
        int minimumWayCount = 3,
        CancellationToken cancellationToken = default);
}
