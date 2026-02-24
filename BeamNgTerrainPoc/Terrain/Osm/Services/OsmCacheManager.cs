using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Cache manager for OSM road data cache.
/// Provides a single interface for managing cached Overpass API responses.
/// </summary>
public class OsmCacheManager
{
    private readonly OsmQueryCache _roadCache;

    /// <summary>
    /// Creates a new cache manager with the default shared cache.
    /// </summary>
    public OsmCacheManager()
    {
        _roadCache = OsmQueryCache.Shared;
    }

    /// <summary>
    /// Creates a new cache manager with custom settings.
    /// </summary>
    /// <param name="cacheDirectory">Directory for cache files. Null for default location.</param>
    /// <param name="cacheExpiry">How long cached results are valid.</param>
    public OsmCacheManager(string? cacheDirectory, TimeSpan cacheExpiry)
    {
        _roadCache = new OsmQueryCache(cacheDirectory, cacheExpiry);
    }

    /// <summary>
    /// Creates a new cache manager with a pre-existing cache instance.
    /// </summary>
    /// <param name="roadCache">The road data cache to use.</param>
    public OsmCacheManager(OsmQueryCache roadCache)
    {
        _roadCache = roadCache ?? throw new ArgumentNullException(nameof(roadCache));
    }

    /// <summary>
    /// Gets the road data cache.
    /// </summary>
    public OsmQueryCache RoadCache => _roadCache;

    /// <summary>
    /// Gets the shared cache directory path.
    /// </summary>
    public string CacheDirectory => _roadCache.CacheDirectory;

    /// <summary>
    /// Gets the cache expiry duration.
    /// </summary>
    public TimeSpan CacheExpiry => _roadCache.CacheExpiry;

    /// <summary>
    /// Invalidates cached data for a specific bounding box.
    /// </summary>
    /// <param name="bbox">The bounding box to invalidate.</param>
    public void InvalidateAll(GeoBoundingBox bbox)
    {
        TerrainLogger.Info($"Invalidating OSM cache for bbox: {bbox}");
        _roadCache.Invalidate(bbox);
    }

    /// <summary>
    /// Clears all cached OSM data.
    /// </summary>
    public void ClearAll()
    {
        TerrainLogger.Info("Clearing all OSM caches...");
        _roadCache.ClearAll();
    }

    /// <summary>
    /// Cleans up expired cache files.
    /// </summary>
    /// <returns>Number of files deleted.</returns>
    public int CleanupAllExpired()
    {
        TerrainLogger.Info("Cleaning up expired OSM cache files...");
        var deleted = _roadCache.CleanupExpired();

        if (deleted > 0)
        {
            TerrainLogger.Info($"OSM cache cleanup complete: {deleted} expired files deleted");
        }

        return deleted;
    }

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    /// <returns>Statistics containing memory and disk usage.</returns>
    public OsmCacheStatistics GetStats()
    {
        var roadStats = _roadCache.GetStats();

        return new OsmCacheStatistics
        {
            MemoryCount = roadStats.memoryCount,
            DiskCount = roadStats.diskCount,
            DiskSizeBytes = roadStats.diskSizeBytes
        };
    }

    /// <summary>
    /// Checks if cached data exists for a bounding box.
    /// </summary>
    /// <param name="bbox">The bounding box to check.</param>
    /// <returns>True if cached data exists for the region.</returns>
    public async Task<bool> HasCachedDataAsync(GeoBoundingBox bbox)
    {
        var roadData = await _roadCache.GetAsync(bbox);
        return roadData != null;
    }

    /// <summary>
    /// Pre-warms the cache by loading disk-cached data for a bounding box into memory.
    /// </summary>
    /// <param name="bbox">The bounding box to pre-warm.</param>
    public async Task PreWarmAsync(GeoBoundingBox bbox)
    {
        TerrainLogger.Info($"Pre-warming OSM cache for bbox: {bbox}");
        await _roadCache.GetAsync(bbox);
    }
}

/// <summary>
/// Statistics about OSM cache usage.
/// </summary>
public class OsmCacheStatistics
{
    /// <summary>
    /// Number of entries in memory cache.
    /// </summary>
    public int MemoryCount { get; set; }

    /// <summary>
    /// Number of cache files on disk.
    /// </summary>
    public int DiskCount { get; set; }

    /// <summary>
    /// Total size of cache files on disk in bytes.
    /// </summary>
    public long DiskSizeBytes { get; set; }

    /// <summary>
    /// Human-readable string representation of disk size.
    /// </summary>
    public string DiskSizeFormatted
    {
        get
        {
            var bytes = DiskSizeBytes;
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }

    public override string ToString()
    {
        return $"OSM Cache: {MemoryCount} in memory, {DiskCount} on disk ({DiskSizeFormatted})";
    }
}
