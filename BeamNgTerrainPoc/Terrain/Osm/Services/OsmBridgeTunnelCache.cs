using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Caches OSM bridge and tunnel query results to disk to avoid repeated API calls.
/// Uses the same caching strategy as <see cref="OsmJunctionCache"/>.
/// 
/// Features:
/// - Memory cache for fast repeated access
/// - Disk cache for persistence across sessions
/// - "Containing bbox" optimization: can return filtered results from a larger cached region
/// - Automatic expiry after configured time (default 7 days)
/// </summary>
public class OsmBridgeTunnelCache
{
    /// <summary>
    /// Cache version. Increment this when the serialization format changes
    /// to automatically invalidate old caches.
    /// </summary>
    private const int CacheVersion = 1;

    private readonly Dictionary<string, OsmBridgeTunnelQueryResult> _memoryCache = new();
    private readonly string _cacheDirectory;
    private readonly TimeSpan _cacheExpiry;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a new cache with default settings.
    /// </summary>
    public OsmBridgeTunnelCache() : this(null, TimeSpan.FromDays(7))
    {
    }

    /// <summary>
    /// Creates a new cache with custom settings.
    /// </summary>
    /// <param name="cacheDirectory">Directory to store cache files. Null for default location.</param>
    /// <param name="cacheExpiry">How long cached results are valid.</param>
    public OsmBridgeTunnelCache(string? cacheDirectory, TimeSpan cacheExpiry)
    {
        _cacheExpiry = cacheExpiry;
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG_LevelCleanUp",
            "OsmCache");

        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <summary>
    /// Gets a cache key for a bounding box.
    /// </summary>
    public string GetCacheKey(GeoBoundingBox bbox)
    {
        return $"osm_bridge_tunnel_v{CacheVersion}_{bbox.MinLatitude:F4}_{bbox.MinLongitude:F4}_{bbox.MaxLatitude:F4}_{bbox.MaxLongitude:F4}";
    }

    /// <summary>
    /// Gets the cache file path for a cache key.
    /// </summary>
    private string GetCacheFilePath(string cacheKey)
    {
        return Path.Combine(_cacheDirectory, $"{cacheKey}.json");
    }

    /// <summary>
    /// Tries to get a cached result for a bounding box.
    /// First tries exact match, then checks if any cached bbox contains the requested one.
    /// </summary>
    /// <param name="bbox">The bounding box to look up.</param>
    /// <returns>Cached result if found and valid, null otherwise.</returns>
    public async Task<OsmBridgeTunnelQueryResult?> GetAsync(GeoBoundingBox bbox)
    {
        // 1. Try exact match first (fastest path)
        var exactResult = await GetExactMatchAsync(bbox);
        if (exactResult != null)
            return exactResult;

        // 2. Check if any cached bbox CONTAINS the requested one
        var containingResult = await GetFromContainingCacheAsync(bbox);
        if (containingResult != null)
            return containingResult;

        return null;
    }

    /// <summary>
    /// Tries to get an exact cache match for a bounding box.
    /// </summary>
    private async Task<OsmBridgeTunnelQueryResult?> GetExactMatchAsync(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);

        // Check memory cache first
        if (_memoryCache.TryGetValue(cacheKey, out var memoryResult))
        {
            if (DateTime.UtcNow - memoryResult.QueryTime < _cacheExpiry)
            {
                TerrainLogger.Info($"OSM bridge/tunnel cache hit (memory, exact): {cacheKey}");
                return CloneWithCacheFlag(memoryResult);
            }

            // Expired, remove from memory
            _memoryCache.Remove(cacheKey);
        }

        // Check disk cache
        var filePath = GetCacheFilePath(cacheKey);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _cacheExpiry)
            {
                // Expired, delete file
                File.Delete(filePath);
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var result = JsonSerializer.Deserialize<OsmBridgeTunnelQueryResult>(json, JsonOptions);

            if (result != null)
            {
                result.IsFromCache = true;
                _memoryCache[cacheKey] = result;
                TerrainLogger.Info($"OSM bridge/tunnel cache hit (disk, exact): {cacheKey}");
                return result;
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to read OSM bridge/tunnel cache: {ex.Message}");
            // Delete corrupted cache file
            try { File.Delete(filePath); } catch { }
        }

        return null;
    }

    /// <summary>
    /// Checks if any cached bounding box contains the requested one.
    /// If found, filters structures to only those within the requested bbox.
    /// </summary>
    private async Task<OsmBridgeTunnelQueryResult?> GetFromContainingCacheAsync(GeoBoundingBox requestedBbox)
    {
        // Check memory cache for containing bbox
        foreach (var (_, cachedResult) in _memoryCache)
        {
            if (cachedResult.BoundingBox != null &&
                cachedResult.BoundingBox.Contains(requestedBbox) &&
                DateTime.UtcNow - cachedResult.QueryTime < _cacheExpiry)
            {
                var filtered = FilterStructuresToBbox(cachedResult, requestedBbox);
                TerrainLogger.Info($"OSM bridge/tunnel cache hit (memory, containing): filtered {filtered.Structures.Count} structures from larger cached region");
                return filtered;
            }
        }

        // Check disk cache for containing bbox
        try
        {
            var cacheFiles = Directory.GetFiles(_cacheDirectory, "osm_bridge_tunnel_v*.json");
            foreach (var filePath in cacheFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _cacheExpiry)
                        continue; // Skip expired

                    var json = await File.ReadAllTextAsync(filePath);
                    var cachedResult = JsonSerializer.Deserialize<OsmBridgeTunnelQueryResult>(json, JsonOptions);

                    if (cachedResult?.BoundingBox != null &&
                        cachedResult.BoundingBox.Contains(requestedBbox))
                    {
                        // Found a containing cache! Filter and return
                        var filtered = FilterStructuresToBbox(cachedResult, requestedBbox);
                        TerrainLogger.Info($"OSM bridge/tunnel cache hit (disk, containing): filtered {filtered.Structures.Count} structures from larger cached region");

                        // Also add to memory cache for faster subsequent lookups
                        var cacheKey = GetCacheKey(cachedResult.BoundingBox);
                        _memoryCache[cacheKey] = cachedResult;

                        return filtered;
                    }
                }
                catch
                {
                    // Skip corrupted files
                }
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Error scanning disk cache for containing bbox: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Filters cached structures to only those within the requested bounding box.
    /// A structure is included if any of its coordinates are within the bbox.
    /// </summary>
    private static OsmBridgeTunnelQueryResult FilterStructuresToBbox(
        OsmBridgeTunnelQueryResult cachedResult,
        GeoBoundingBox requestedBbox)
    {
        var filteredStructures = cachedResult.Structures
            .Where(s => s.Coordinates.Any(c => requestedBbox.Contains(c)))
            .ToList();

        return new OsmBridgeTunnelQueryResult
        {
            BoundingBox = requestedBbox,
            Structures = filteredStructures,
            QueryTime = cachedResult.QueryTime,
            IsFromCache = true
        };
    }

    /// <summary>
    /// Creates a copy with IsFromCache set to true.
    /// </summary>
    private static OsmBridgeTunnelQueryResult CloneWithCacheFlag(OsmBridgeTunnelQueryResult result)
    {
        return new OsmBridgeTunnelQueryResult
        {
            BoundingBox = result.BoundingBox,
            Structures = result.Structures,
            QueryTime = result.QueryTime,
            IsFromCache = true
        };
    }

    /// <summary>
    /// Stores a query result in the cache.
    /// </summary>
    public async Task SetAsync(GeoBoundingBox bbox, OsmBridgeTunnelQueryResult result)
    {
        var cacheKey = GetCacheKey(bbox);

        // Store in memory
        _memoryCache[cacheKey] = result;

        // Store to disk
        try
        {
            var filePath = GetCacheFilePath(cacheKey);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);
            TerrainLogger.Info($"OSM bridge/tunnel result cached: {cacheKey} ({result.Structures.Count} structures)");
            OsmQueryCache.RaiseCacheChanged();
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to write OSM bridge/tunnel cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Invalidates the cache for a specific bounding box.
    /// </summary>
    public void Invalidate(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);

        _memoryCache.Remove(cacheKey);

        var filePath = GetCacheFilePath(cacheKey);
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
                TerrainLogger.Info($"OSM bridge/tunnel cache invalidated: {cacheKey}");
                OsmQueryCache.RaiseCacheChanged();
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to delete OSM bridge/tunnel cache: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clears all cached bridge/tunnel data.
    /// </summary>
    public void ClearAll()
    {
        _memoryCache.Clear();

        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "osm_bridge_tunnel_*.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            TerrainLogger.Info($"OSM bridge/tunnel cache cleared: {files.Length} files deleted");
            if (files.Length > 0)
                OsmQueryCache.RaiseCacheChanged();
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to clear OSM bridge/tunnel cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public (int memoryCount, int diskCount, long diskSizeBytes) GetStats()
    {
        var memoryCount = _memoryCache.Count;
        var diskCount = 0;
        long diskSize = 0;

        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "osm_bridge_tunnel_*.json");
            diskCount = files.Length;
            diskSize = files.Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            // Ignore
        }

        return (memoryCount, diskCount, diskSize);
    }

    /// <summary>
    /// Gets the cache directory path.
    /// </summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// Gets the cache expiry duration.
    /// </summary>
    public TimeSpan CacheExpiry => _cacheExpiry;

    /// <summary>
    /// Cleans up expired cache files from disk.
    /// Returns the number of files deleted.
    /// </summary>
    public int CleanupExpired()
    {
        var deletedCount = 0;

        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "osm_bridge_tunnel_*.json");
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _cacheExpiry)
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                }
                catch
                {
                    // Skip files that can't be deleted
                }
            }

            if (deletedCount > 0)
            {
                TerrainLogger.Info($"OSM bridge/tunnel cache cleanup: {deletedCount} expired files deleted");
                OsmQueryCache.RaiseCacheChanged();
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to cleanup OSM bridge/tunnel cache: {ex.Message}");
        }

        return deletedCount;
    }
}
