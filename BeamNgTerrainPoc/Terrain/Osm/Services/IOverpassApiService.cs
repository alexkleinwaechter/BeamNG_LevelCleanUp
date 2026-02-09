using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Interface for querying OSM data via the Overpass API.
/// </summary>
public interface IOverpassApiService
{
    /// <summary>
    /// Queries all features within a bounding box.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with parsed features.</returns>
    Task<OsmQueryResult> QueryAllFeaturesAsync(GeoBoundingBox bbox, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Queries features matching specific tag filters within a bounding box.
    /// </summary>
    /// <param name="bbox">The geographic bounding box to query.</param>
    /// <param name="tagFilters">Dictionary of tag keys to required values (null value means any value).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with parsed features.</returns>
    Task<OsmQueryResult> QueryByTagsAsync(
        GeoBoundingBox bbox, 
        Dictionary<string, string?> tagFilters, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the raw XML response from an Overpass query (for debugging).
    /// </summary>
    Task<string> ExecuteRawQueryAsync(string query, CancellationToken cancellationToken = default);
}
