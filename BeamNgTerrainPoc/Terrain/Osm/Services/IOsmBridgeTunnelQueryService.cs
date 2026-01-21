using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Service for querying bridge and tunnel data from OSM via Overpass API.
/// 
/// Bridges and tunnels are tagged on OSM ways (not nodes) with tags like:
/// - bridge=yes (or bridge=viaduct, bridge=cantilever, etc.)
/// - tunnel=yes (or tunnel=building_passage, tunnel=culvert)
/// 
/// This service queries these ways within a bounding box and returns structured
/// data that can be matched to road splines for terrain processing.
/// </summary>
public interface IOsmBridgeTunnelQueryService
{
    /// <summary>
    /// Queries all bridges and tunnels within a bounding box.
    /// This is the most common query method, returning all structure types.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing all bridge/tunnel structures.</returns>
    Task<OsmBridgeTunnelQueryResult> QueryBridgesAndTunnelsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries only bridges within a bounding box.
    /// Use when tunnel handling is disabled or not needed.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing only bridge structures.</returns>
    Task<OsmBridgeTunnelQueryResult> QueryBridgesAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries only tunnels within a bounding box.
    /// Use when bridge handling is disabled or not needed.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing only tunnel structures.</returns>
    Task<OsmBridgeTunnelQueryResult> QueryTunnelsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries structures of specific types within a bounding box.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="types">Array of structure types to include.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result containing structures of the specified types.</returns>
    Task<OsmBridgeTunnelQueryResult> QueryByTypesAsync(
        GeoBoundingBox bbox,
        StructureType[] types,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the underlying cache for advanced operations (e.g., clearing, statistics).
    /// </summary>
    OsmBridgeTunnelCache Cache { get; }

    /// <summary>
    /// Invalidates the cache for a specific bounding box.
    /// </summary>
    /// <param name="bbox">The bounding box to invalidate.</param>
    void InvalidateCache(GeoBoundingBox bbox);

    /// <summary>
    /// Clears all cached bridge/tunnel data.
    /// </summary>
    void ClearCache();
}
