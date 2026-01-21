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
    
    /// <summary>
    /// Event raised when any OSM cache is modified (added, invalidated, or cleared).
    /// Useful for UI components that need to refresh when cache state changes.
    /// </summary>
    public static event Action? CacheChanged;
    
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
    /// First tries exact match, then checks if any cached bbox contains the requested one.
    /// </summary>
    /// <param name="bbox">The bounding box to look up.</param>
    /// <returns>Cached result if found and valid, null otherwise.</returns>
    public async Task<OsmQueryResult?> GetAsync(GeoBoundingBox bbox)
    {
        // 1. Try exact match first (fastest path)
        var exactResult = await GetExactMatchAsync(bbox);
        if (exactResult != null)
            return exactResult;
        
        // 2. Check if any cached bbox CONTAINS the requested one
        //    This allows reusing cached data when user shrinks the crop region
        var containingResult = await GetFromContainingCacheAsync(bbox);
        if (containingResult != null)
            return containingResult;
        
        return null;
    }
    
    /// <summary>
    /// Tries to get an exact cache match for a bounding box.
    /// </summary>
    private async Task<OsmQueryResult?> GetExactMatchAsync(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);
        
        // Check memory cache first
        if (_memoryCache.TryGetValue(cacheKey, out var memoryResult))
        {
            if (DateTime.UtcNow - memoryResult.QueryTime < _cacheExpiry)
            {
                TerrainLogger.Info($"OSM cache hit (memory, exact): {cacheKey}");
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
                TerrainLogger.Info($"OSM cache hit (disk, exact): {cacheKey}");
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
    /// Checks if any cached bounding box contains the requested one.
    /// If found, filters features to only those intersecting the requested bbox.
    /// </summary>
    private async Task<OsmQueryResult?> GetFromContainingCacheAsync(GeoBoundingBox requestedBbox)
    {
        // Check memory cache for containing bbox
        foreach (var (_, cachedResult) in _memoryCache)
        {
            if (cachedResult.BoundingBox != null && 
                cachedResult.BoundingBox.Contains(requestedBbox) &&
                DateTime.UtcNow - cachedResult.QueryTime < _cacheExpiry)
            {
                var filtered = FilterFeaturesToBbox(cachedResult, requestedBbox);
                TerrainLogger.Info($"OSM cache hit (memory, containing): filtered {filtered.Features.Count} features from larger cached region");
                return filtered;
            }
        }
        
        // Check disk cache for containing bbox
        try
        {
            var cacheFiles = Directory.GetFiles(_cacheDirectory, "osm_v*.json");
            foreach (var filePath in cacheFiles)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _cacheExpiry)
                        continue; // Skip expired
                    
                    var json = await File.ReadAllTextAsync(filePath);
                    var cachedResult = JsonSerializer.Deserialize<OsmQueryResult>(json, JsonOptions);
                    
                    if (cachedResult?.BoundingBox != null && 
                        cachedResult.BoundingBox.Contains(requestedBbox))
                    {
                        // Found a containing cache! Filter and return
                        var filtered = FilterFeaturesToBbox(cachedResult, requestedBbox);
                        TerrainLogger.Info($"OSM cache hit (disk, containing): filtered {filtered.Features.Count} features from larger cached region");
                        
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
    /// Filters cached features to only those that intersect the requested bounding box.
    /// </summary>
    private static OsmQueryResult FilterFeaturesToBbox(OsmQueryResult cachedResult, GeoBoundingBox requestedBbox)
    {
        var filteredFeatures = cachedResult.Features
            .Where(f => FeatureIntersectsBbox(f, requestedBbox))
            .ToList();
        
        return new OsmQueryResult
        {
            BoundingBox = requestedBbox,
            Features = filteredFeatures,
            QueryTime = cachedResult.QueryTime,
            IsFromCache = true,
            NodeCount = filteredFeatures.Count,
            WayCount = cachedResult.WayCount,
            RelationCount = cachedResult.RelationCount
        };
    }
    
    /// <summary>
    /// Checks if a feature intersects with a bounding box.
    /// A feature intersects if any of its coordinates are within the bbox.
    /// </summary>
    private static bool FeatureIntersectsBbox(OsmFeature feature, GeoBoundingBox bbox)
    {
        // Check if any coordinate is within the bbox
        if (feature.Coordinates.Any(coord => bbox.Contains(coord)))
            return true;
        
        // For line features, also check if the line crosses the bbox even if no point is inside
        // This handles cases where a road passes through the bbox without any vertex inside it
        if (feature.GeometryType == OsmGeometryType.LineString && feature.Coordinates.Count >= 2)
        {
            for (var i = 0; i < feature.Coordinates.Count - 1; i++)
            {
                if (LineSegmentIntersectsBbox(feature.Coordinates[i], feature.Coordinates[i + 1], bbox))
                    return true;
            }
        }
        
        return false;
    }
    
    /// <summary>
    /// Checks if a line segment intersects a bounding box.
    /// </summary>
    private static bool LineSegmentIntersectsBbox(GeoCoordinate p1, GeoCoordinate p2, GeoBoundingBox bbox)
    {
        // Quick reject: if both points are on the same side of any bbox edge, no intersection
        if ((p1.Longitude < bbox.MinLongitude && p2.Longitude < bbox.MinLongitude) ||
            (p1.Longitude > bbox.MaxLongitude && p2.Longitude > bbox.MaxLongitude) ||
            (p1.Latitude < bbox.MinLatitude && p2.Latitude < bbox.MinLatitude) ||
            (p1.Latitude > bbox.MaxLatitude && p2.Latitude > bbox.MaxLatitude))
            return false;
        
        // Line segment might intersect - for simplicity, we'll be conservative and return true
        // A more precise check would test intersection with each bbox edge, but this is sufficient
        // for our use case (we'd rather include a feature than miss it)
        return true;
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
            CacheChanged?.Invoke();
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
                CacheChanged?.Invoke();
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
            if (files.Length > 0)
                CacheChanged?.Invoke();
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
            var files = Directory.GetFiles(_cacheDirectory, "osm_*.json");
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
                TerrainLogger.Info($"OSM cache cleanup: {deletedCount} expired files deleted");
                CacheChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to cleanup OSM cache: {ex.Message}");
        }
        
        return deletedCount;
    }
    
    /// <summary>
    /// Raises the CacheChanged event manually. Useful when external code modifies the cache.
    /// </summary>
    public static void RaiseCacheChanged()
    {
        CacheChanged?.Invoke();
    }
}
