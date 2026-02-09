using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Unified cache manager for all OSM-related caches.
/// Provides a single interface for managing road data and junction caches.
/// This enables coordinated cache operations across both data types.
/// </summary>
public class OsmCacheManager
{
    private readonly OsmQueryCache _roadCache;
    private readonly OsmJunctionCache _junctionCache;

    /// <summary>
    /// Creates a new cache manager with default caches.
    /// Both caches use the same default directory and expiry settings.
    /// </summary>
    public OsmCacheManager()
    {
        _roadCache = OsmQueryCache.Shared;
        _junctionCache = new OsmJunctionCache();
    }

    /// <summary>
    /// Creates a new cache manager with custom settings.
    /// </summary>
    /// <param name="cacheDirectory">Shared directory for all cache files. Null for default location.</param>
    /// <param name="cacheExpiry">How long cached results are valid.</param>
    public OsmCacheManager(string? cacheDirectory, TimeSpan cacheExpiry)
    {
        _roadCache = new OsmQueryCache(cacheDirectory, cacheExpiry);
        _junctionCache = new OsmJunctionCache(cacheDirectory, cacheExpiry);
    }

    /// <summary>
    /// Creates a new cache manager with pre-existing cache instances.
    /// </summary>
    /// <param name="roadCache">The road data cache to use.</param>
    /// <param name="junctionCache">The junction cache to use.</param>
    public OsmCacheManager(OsmQueryCache roadCache, OsmJunctionCache junctionCache)
    {
        _roadCache = roadCache ?? throw new ArgumentNullException(nameof(roadCache));
        _junctionCache = junctionCache ?? throw new ArgumentNullException(nameof(junctionCache));
    }

    /// <summary>
    /// Gets the road data cache.
    /// </summary>
    public OsmQueryCache RoadCache => _roadCache;

    /// <summary>
    /// Gets the junction cache.
    /// </summary>
    public OsmJunctionCache JunctionCache => _junctionCache;

    /// <summary>
    /// Gets the shared cache directory path.
    /// </summary>
    public string CacheDirectory => _roadCache.CacheDirectory;

    /// <summary>
    /// Gets the cache expiry duration.
    /// </summary>
    public TimeSpan CacheExpiry => _roadCache.CacheExpiry;

    /// <summary>
    /// Invalidates all cached data for a specific bounding box.
    /// This removes both road data and junction caches for the region.
    /// </summary>
    /// <param name="bbox">The bounding box to invalidate.</param>
    public void InvalidateAll(GeoBoundingBox bbox)
    {
        TerrainLogger.Info($"Invalidating all OSM caches for bbox: {bbox}");
        _roadCache.Invalidate(bbox);
        _junctionCache.Invalidate(bbox);
    }

    /// <summary>
    /// Clears all cached OSM data (both roads and junctions).
    /// </summary>
    public void ClearAll()
    {
        TerrainLogger.Info("Clearing all OSM caches...");
        _roadCache.ClearAll();
        _junctionCache.ClearAll();
    }

    /// <summary>
    /// Cleans up expired cache files for all cache types.
    /// </summary>
    /// <returns>Total number of files deleted.</returns>
    public int CleanupAllExpired()
    {
        TerrainLogger.Info("Cleaning up expired OSM cache files...");
        var roadDeleted = _roadCache.CleanupExpired();
        var junctionDeleted = _junctionCache.CleanupExpired();
        var total = roadDeleted + junctionDeleted;
        
        if (total > 0)
        {
            TerrainLogger.Info($"OSM cache cleanup complete: {total} expired files deleted ({roadDeleted} road, {junctionDeleted} junction)");
        }
        
        return total;
    }

    /// <summary>
    /// Gets combined statistics for all caches.
    /// </summary>
    /// <returns>Statistics containing memory and disk usage for all caches.</returns>
    public OsmCacheStatistics GetStats()
    {
        var roadStats = _roadCache.GetStats();
        var junctionStats = _junctionCache.GetStats();

        return new OsmCacheStatistics
        {
            RoadCacheMemoryCount = roadStats.memoryCount,
            RoadCacheDiskCount = roadStats.diskCount,
            RoadCacheDiskSizeBytes = roadStats.diskSizeBytes,
            JunctionCacheMemoryCount = junctionStats.memoryCount,
            JunctionCacheDiskCount = junctionStats.diskCount,
            JunctionCacheDiskSizeBytes = junctionStats.diskSizeBytes
        };
    }

    /// <summary>
    /// Checks if cached data exists for a bounding box (either road or junction data).
    /// </summary>
    /// <param name="bbox">The bounding box to check.</param>
    /// <returns>True if any cached data exists for the region.</returns>
    public async Task<bool> HasCachedDataAsync(GeoBoundingBox bbox)
    {
        var roadData = await _roadCache.GetAsync(bbox);
        var junctionData = await _junctionCache.GetAsync(bbox);
        
        return roadData != null || junctionData != null;
    }

    /// <summary>
    /// Pre-warms the cache by loading disk-cached data for a bounding box into memory.
    /// This can improve performance when making multiple queries to the same region.
    /// </summary>
    /// <param name="bbox">The bounding box to pre-warm.</param>
    public async Task PreWarmAsync(GeoBoundingBox bbox)
    {
        TerrainLogger.Info($"Pre-warming OSM cache for bbox: {bbox}");
        
        // Simply attempt to get cached data - this loads from disk to memory if available
        await _roadCache.GetAsync(bbox);
        await _junctionCache.GetAsync(bbox);
    }
}

/// <summary>
/// Statistics about OSM cache usage.
/// </summary>
public class OsmCacheStatistics
{
    /// <summary>
    /// Number of road data entries in memory cache.
    /// </summary>
    public int RoadCacheMemoryCount { get; set; }

    /// <summary>
    /// Number of road data files on disk.
    /// </summary>
    public int RoadCacheDiskCount { get; set; }

    /// <summary>
    /// Total size of road cache files on disk in bytes.
    /// </summary>
    public long RoadCacheDiskSizeBytes { get; set; }

    /// <summary>
    /// Number of junction entries in memory cache.
    /// </summary>
    public int JunctionCacheMemoryCount { get; set; }

    /// <summary>
    /// Number of junction files on disk.
    /// </summary>
    public int JunctionCacheDiskCount { get; set; }

    /// <summary>
    /// Total size of junction cache files on disk in bytes.
    /// </summary>
    public long JunctionCacheDiskSizeBytes { get; set; }

    /// <summary>
    /// Total entries in memory across all caches.
    /// </summary>
    public int TotalMemoryCount => RoadCacheMemoryCount + JunctionCacheMemoryCount;

    /// <summary>
    /// Total files on disk across all caches.
    /// </summary>
    public int TotalDiskCount => RoadCacheDiskCount + JunctionCacheDiskCount;

    /// <summary>
    /// Total disk space used by all caches in bytes.
    /// </summary>
    public long TotalDiskSizeBytes => RoadCacheDiskSizeBytes + JunctionCacheDiskSizeBytes;

    /// <summary>
    /// Human-readable string representation of total disk size.
    /// </summary>
    public string TotalDiskSizeFormatted
    {
        get
        {
            var bytes = TotalDiskSizeBytes;
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        }
    }

    public override string ToString()
    {
        return $"OSM Cache: {TotalMemoryCount} in memory, {TotalDiskCount} on disk ({TotalDiskSizeFormatted})";
    }
}
