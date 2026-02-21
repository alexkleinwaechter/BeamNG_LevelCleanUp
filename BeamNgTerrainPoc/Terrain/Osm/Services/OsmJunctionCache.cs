using System.Globalization;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Caches OSM junction query results to disk to avoid repeated API calls.
/// Uses the same caching strategy as <see cref="OsmQueryCache"/>, including
/// a filename-based bbox index for efficient containing-bbox lookups.
/// </summary>
public class OsmJunctionCache
{
    /// <summary>
    /// Cache version. Increment this when the serialization format changes
    /// to automatically invalidate old caches.
    /// </summary>
    private const int CacheVersion = 1;

    private readonly Dictionary<string, OsmJunctionQueryResult> _memoryCache = new();
    private readonly string _cacheDirectory;
    private readonly TimeSpan _cacheExpiry;

    /// <summary>
    /// In-memory index of cached bounding boxes, built from filenames (no deserialization needed).
    /// </summary>
    private List<CacheBboxEntry>? _bboxIndex;
    private readonly object _indexLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a new cache with default settings.
    /// </summary>
    public OsmJunctionCache() : this(null, TimeSpan.FromDays(7))
    {
    }

    /// <summary>
    /// Creates a new cache with custom settings.
    /// </summary>
    /// <param name="cacheDirectory">Directory to store cache files. Null for default location.</param>
    /// <param name="cacheExpiry">How long cached results are valid.</param>
    public OsmJunctionCache(string? cacheDirectory, TimeSpan cacheExpiry)
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
        return $"osm_junctions_v{CacheVersion}_{bbox.MinLatitude:F4}_{bbox.MinLongitude:F4}_{bbox.MaxLatitude:F4}_{bbox.MaxLongitude:F4}";
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
    public async Task<OsmJunctionQueryResult?> GetAsync(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);

        // 1. Try exact match first (fastest path)
        var exactResult = await GetExactMatchAsync(bbox);
        if (exactResult != null)
            return exactResult;

        // 2. Check if any cached bbox CONTAINS the requested one
        var containingResult = await GetFromContainingCacheAsync(bbox);
        if (containingResult != null)
            return containingResult;

        TerrainLogger.Info($"OSM junction cache miss: no exact or containing match for {cacheKey}");
        return null;
    }

    /// <summary>
    /// Tries to get an exact cache match for a bounding box.
    /// </summary>
    private async Task<OsmJunctionQueryResult?> GetExactMatchAsync(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);

        // Check memory cache first
        if (_memoryCache.TryGetValue(cacheKey, out var memoryResult))
        {
            if (DateTime.UtcNow - memoryResult.QueryTime < _cacheExpiry)
            {
                TerrainLogger.Info($"OSM junction cache hit (memory, exact): {cacheKey}");
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
                InvalidateBboxIndex();
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var result = JsonSerializer.Deserialize<OsmJunctionQueryResult>(json, JsonOptions);

            if (result != null)
            {
                result.IsFromCache = true;
                _memoryCache[cacheKey] = result;
                TerrainLogger.Info($"OSM junction cache hit (disk, exact): {cacheKey}");
                return result;
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to read OSM junction cache: {ex.Message}");
            // Delete corrupted cache file
            try { File.Delete(filePath); InvalidateBboxIndex(); } catch { }
        }

        return null;
    }

    /// <summary>
    /// Checks if any cached bounding box contains the requested one.
    /// Uses an in-memory bbox index built from filenames to avoid deserializing all files.
    /// </summary>
    private async Task<OsmJunctionQueryResult?> GetFromContainingCacheAsync(GeoBoundingBox requestedBbox)
    {
        // Check memory cache for containing bbox
        foreach (var (_, cachedResult) in _memoryCache)
        {
            if (cachedResult.BoundingBox != null &&
                cachedResult.BoundingBox.Contains(requestedBbox) &&
                DateTime.UtcNow - cachedResult.QueryTime < _cacheExpiry)
            {
                var filtered = FilterJunctionsToBbox(cachedResult, requestedBbox);
                TerrainLogger.Info($"OSM junction cache hit (memory, containing): filtered {filtered.Junctions.Count} junctions from larger cached region");
                return filtered;
            }
        }

        // Use bbox index to find containing cache on disk (no full deserialization needed)
        var index = GetOrBuildBboxIndex();

        foreach (var entry in index)
        {
            if (entry.BBox.Contains(requestedBbox) &&
                DateTime.UtcNow - entry.LastWriteUtc <= _cacheExpiry)
            {
                try
                {
                    // Only deserialize the one matching file
                    var json = await File.ReadAllTextAsync(entry.FilePath);
                    var cachedResult = JsonSerializer.Deserialize<OsmJunctionQueryResult>(json, JsonOptions);

                    if (cachedResult?.BoundingBox != null &&
                        cachedResult.BoundingBox.Contains(requestedBbox))
                    {
                        var filtered = FilterJunctionsToBbox(cachedResult, requestedBbox);
                        TerrainLogger.Info($"OSM junction cache hit (disk, containing via index): filtered {filtered.Junctions.Count} junctions from larger cached region");

                        // Also add to memory cache for faster subsequent lookups
                        var cacheKey = GetCacheKey(cachedResult.BoundingBox);
                        _memoryCache[cacheKey] = cachedResult;

                        return filtered;
                    }
                }
                catch (Exception ex)
                {
                    TerrainLogger.Warning($"Error reading indexed junction cache file {Path.GetFileName(entry.FilePath)}: {ex.Message}");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Filters cached junctions to only those within the requested bounding box.
    /// </summary>
    private static OsmJunctionQueryResult FilterJunctionsToBbox(OsmJunctionQueryResult cachedResult, GeoBoundingBox requestedBbox)
    {
        var filteredJunctions = cachedResult.Junctions
            .Where(j => requestedBbox.Contains(j.Location))
            .ToList();

        return new OsmJunctionQueryResult
        {
            BoundingBox = requestedBbox,
            Junctions = filteredJunctions,
            QueryTime = cachedResult.QueryTime,
            IsFromCache = true,
            TotalNodesQueried = filteredJunctions.Count
        };
    }

    /// <summary>
    /// Creates a copy with IsFromCache set to true.
    /// </summary>
    private static OsmJunctionQueryResult CloneWithCacheFlag(OsmJunctionQueryResult result)
    {
        return new OsmJunctionQueryResult
        {
            BoundingBox = result.BoundingBox,
            Junctions = result.Junctions,
            QueryTime = result.QueryTime,
            IsFromCache = true,
            TotalNodesQueried = result.TotalNodesQueried
        };
    }

    /// <summary>
    /// Stores a query result in the cache.
    /// </summary>
    public async Task SetAsync(GeoBoundingBox bbox, OsmJunctionQueryResult result)
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

            var fileInfo = new FileInfo(filePath);
            TerrainLogger.Info($"OSM junction result cached: {cacheKey} ({result.Junctions.Count} junctions)");

            // Add to bbox index
            AddToBboxIndex(filePath, bbox, fileInfo.LastWriteTimeUtc);

            OsmQueryCache.RaiseCacheChanged();
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to write OSM junction cache: {ex.Message}");
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
                InvalidateBboxIndex();
                TerrainLogger.Info($"OSM junction cache invalidated: {cacheKey}");
                OsmQueryCache.RaiseCacheChanged();
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to delete OSM junction cache: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Clears all cached junction data.
    /// </summary>
    public void ClearAll()
    {
        _memoryCache.Clear();
        InvalidateBboxIndex();

        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "osm_junctions_*.json");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            TerrainLogger.Info($"OSM junction cache cleared: {files.Length} files deleted");
            if (files.Length > 0)
                OsmQueryCache.RaiseCacheChanged();
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to clear OSM junction cache: {ex.Message}");
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
            var files = Directory.GetFiles(_cacheDirectory, "osm_junctions_*.json");
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
            var files = Directory.GetFiles(_cacheDirectory, "osm_junctions_*.json");
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
                InvalidateBboxIndex();
                TerrainLogger.Info($"OSM junction cache cleanup: {deletedCount} expired files deleted");
                OsmQueryCache.RaiseCacheChanged();
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to cleanup OSM junction cache: {ex.Message}");
        }

        return deletedCount;
    }

    #region Bbox Index

    private record CacheBboxEntry(string FilePath, GeoBoundingBox BBox, DateTime LastWriteUtc);

    /// <summary>
    /// Gets or lazily builds the bbox index from cache filenames.
    /// No file deserialization is needed — bbox is parsed from the filename.
    /// </summary>
    private List<CacheBboxEntry> GetOrBuildBboxIndex()
    {
        lock (_indexLock)
        {
            if (_bboxIndex != null)
                return _bboxIndex;

            _bboxIndex = new List<CacheBboxEntry>();

            try
            {
                var pattern = $"osm_junctions_v{CacheVersion}_*.json";
                var files = Directory.GetFiles(_cacheDirectory, pattern);

                foreach (var filePath in files)
                {
                    var bbox = ParseBboxFromFileName(filePath);
                    if (bbox != null)
                    {
                        var lastWrite = new FileInfo(filePath).LastWriteTimeUtc;
                        _bboxIndex.Add(new CacheBboxEntry(filePath, bbox, lastWrite));
                    }
                }

                if (_bboxIndex.Count > 0)
                    TerrainLogger.Info($"Built OSM junction cache bbox index: {_bboxIndex.Count} entries");
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Error building junction bbox index: {ex.Message}");
            }

            return _bboxIndex;
        }
    }

    /// <summary>
    /// Adds a new entry to the bbox index (called after writing a cache file).
    /// </summary>
    private void AddToBboxIndex(string filePath, GeoBoundingBox bbox, DateTime lastWriteUtc)
    {
        lock (_indexLock)
        {
            _bboxIndex ??= new List<CacheBboxEntry>();
            _bboxIndex.RemoveAll(e => e.FilePath == filePath);
            _bboxIndex.Add(new CacheBboxEntry(filePath, bbox, lastWriteUtc));
        }
    }

    /// <summary>
    /// Forces the bbox index to be rebuilt on next access.
    /// </summary>
    private void InvalidateBboxIndex()
    {
        lock (_indexLock)
        {
            _bboxIndex = null;
        }
    }

    /// <summary>
    /// Parses a bounding box from a junction cache filename.
    /// Expected format: osm_junctions_v1_{minLat:F4}_{minLon:F4}_{maxLat:F4}_{maxLon:F4}.json
    /// </summary>
    /// <remarks>
    /// The filename stores coordinates at F4 precision. To prevent false negatives in the
    /// containing-bbox index pre-filter, we expand by half the F4 step (0.00005° ≈ 5.5m).
    /// False positives are harmless — the full-precision Contains() check after file
    /// deserialization is the definitive gate.
    /// </remarks>
    private static GeoBoundingBox? ParseBboxFromFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath); // removes .json

        // Split: ["osm", "junctions", "v1", "48.1234", "11.5678", "48.5678", "12.1234"]
        var parts = name.Split('_');
        if (parts.Length < 7) return null;

        // Last 4 parts are the bbox coordinates: minLat, minLon, maxLat, maxLon
        if (double.TryParse(parts[^4], CultureInfo.InvariantCulture, out var minLat) &&
            double.TryParse(parts[^3], CultureInfo.InvariantCulture, out var minLon) &&
            double.TryParse(parts[^2], CultureInfo.InvariantCulture, out var maxLat) &&
            double.TryParse(parts[^1], CultureInfo.InvariantCulture, out var maxLon))
        {
            // Expand by half the F4 precision step to compensate for rounding.
            // This ensures the index bbox is always >= the actual cached bbox.
            const double halfStep = 0.00005;
            return new GeoBoundingBox(minLon - halfStep, minLat - halfStep, maxLon + halfStep, maxLat + halfStep);
        }

        return null;
    }

    #endregion
}
