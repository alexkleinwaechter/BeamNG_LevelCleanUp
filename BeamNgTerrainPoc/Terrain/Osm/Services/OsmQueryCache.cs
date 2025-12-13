using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Caches OSM query results to disk to avoid repeated API calls.
/// </summary>
public class OsmQueryCache
{
    /// <summary>
    /// Cache version. Increment this when the serialization format changes
    /// to automatically invalidate old caches.
    /// </summary>
    private const int CacheVersion = 2; // v2: Added [JsonConstructor] to GeoBoundingBox/GeoCoordinate
    
    private readonly Dictionary<string, OsmQueryResult> _memoryCache = new();
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
    public OsmQueryCache() : this(null, TimeSpan.FromDays(7))
    {
    }
    
    /// <summary>
    /// Creates a new cache with custom settings.
    /// </summary>
    /// <param name="cacheDirectory">Directory to store cache files. Null for default location.</param>
    /// <param name="cacheExpiry">How long cached results are valid.</param>
    public OsmQueryCache(string? cacheDirectory, TimeSpan cacheExpiry)
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
        // Use a consistent precision for cache keys, include version to invalidate old caches
        return $"osm_v{CacheVersion}_{bbox.MinLatitude:F4}_{bbox.MinLongitude:F4}_{bbox.MaxLatitude:F4}_{bbox.MaxLongitude:F4}";
    }
    
    /// <summary>
    /// Gets the cache file path for a bounding box.
    /// </summary>
    private string GetCacheFilePath(string cacheKey)
    {
        return Path.Combine(_cacheDirectory, $"{cacheKey}.json");
    }
    
    /// <summary>
    /// Tries to get a cached result for a bounding box.
    /// </summary>
    /// <param name="bbox">The bounding box to look up.</param>
    /// <returns>Cached result if found and valid, null otherwise.</returns>
    public async Task<OsmQueryResult?> GetAsync(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);
        
        // Check memory cache first
        if (_memoryCache.TryGetValue(cacheKey, out var memoryResult))
        {
            if (DateTime.UtcNow - memoryResult.QueryTime < _cacheExpiry)
            {
                TerrainLogger.Info($"OSM cache hit (memory): {cacheKey}");
                return memoryResult;
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
            var result = JsonSerializer.Deserialize<OsmQueryResult>(json, JsonOptions);
            
            if (result != null)
            {
                result.IsFromCache = true;
                _memoryCache[cacheKey] = result;
                TerrainLogger.Info($"OSM cache hit (disk): {cacheKey}");
                return result;
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to read OSM cache: {ex.Message}");
            // Delete corrupted cache file
            try { File.Delete(filePath); } catch { }
        }
        
        return null;
    }
    
    /// <summary>
    /// Stores a query result in the cache.
    /// </summary>
    public async Task SetAsync(GeoBoundingBox bbox, OsmQueryResult result)
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
            TerrainLogger.Info($"OSM result cached: {cacheKey}");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to write OSM cache: {ex.Message}");
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
                TerrainLogger.Info($"OSM cache invalidated: {cacheKey}");
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to delete OSM cache: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Clears all cached data.
    /// </summary>
    public void ClearAll()
    {
        _memoryCache.Clear();
        
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "osm_*.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            TerrainLogger.Info($"OSM cache cleared: {files.Length} files deleted");
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to clear OSM cache: {ex.Message}");
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
            var files = Directory.GetFiles(_cacheDirectory, "osm_*.json");
            diskCount = files.Length;
            diskSize = files.Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            // Ignore
        }
        
        return (memoryCount, diskCount, diskSize);
    }
}
