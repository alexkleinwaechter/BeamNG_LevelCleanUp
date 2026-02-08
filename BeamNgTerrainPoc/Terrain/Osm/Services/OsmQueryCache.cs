using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Services;

/// <summary>
/// Caches OSM query results to disk (GZip-compressed) with an in-memory tier.
/// Use the <see cref="Shared"/> instance to benefit from the memory cache across calls.
/// </summary>
public class OsmQueryCache
{
    /// <summary>
    /// Cache version. Increment this when the serialization format changes
    /// to automatically invalidate old caches.
    /// v2: Added [JsonConstructor] to GeoBoundingBox/GeoCoordinate
    /// v3: GZip-compressed cache files, bbox index
    /// </summary>
    private const int CacheVersion = 3;

    /// <summary>
    /// File extension for compressed cache files.
    /// </summary>
    private const string CompressedExtension = ".json.gz";

    /// <summary>
    /// File extension for legacy uncompressed cache files.
    /// </summary>
    private const string LegacyExtension = ".json";

    /// <summary>
    /// Shared singleton instance. Use this from all call sites to benefit from
    /// the in-memory cache tier across the entire application session.
    /// </summary>
    public static OsmQueryCache Shared { get; } = new();

    /// <summary>
    /// Event raised when any OSM cache is modified (added, invalidated, or cleared).
    /// Useful for UI components that need to refresh when cache state changes.
    /// </summary>
    public static event Action? CacheChanged;

    private readonly ConcurrentDictionary<string, OsmQueryResult> _memoryCache = new();
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
    /// Prefer using <see cref="Shared"/> instead of creating new instances.
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
    /// Gets the cache file path for a bounding box (GZip-compressed).
    /// </summary>
    private string GetCacheFilePath(string cacheKey)
    {
        return Path.Combine(_cacheDirectory, $"{cacheKey}{CompressedExtension}");
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
            _memoryCache.TryRemove(cacheKey, out _);
        }

        // Check disk cache (GZip-compressed)
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

            var result = await ReadCompressedCacheFileAsync(filePath);

            if (result != null)
            {
                result.IsFromCache = true;
                _memoryCache[cacheKey] = result;
                TerrainLogger.Info($"OSM cache hit (disk, exact): {cacheKey} ({fileInfo.Length / 1024}KB compressed)");
                return result;
            }
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to read OSM cache: {ex.Message}");
            // Delete corrupted cache file
            try { File.Delete(filePath); InvalidateBboxIndex(); } catch { }
        }

        return null;
    }

    /// <summary>
    /// Checks if any cached bounding box contains the requested one.
    /// Uses an in-memory bbox index built from filenames to avoid deserializing all files.
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
                    var cachedResult = await ReadCompressedCacheFileAsync(entry.FilePath);

                    if (cachedResult?.BoundingBox != null &&
                        cachedResult.BoundingBox.Contains(requestedBbox))
                    {
                        var filtered = FilterFeaturesToBbox(cachedResult, requestedBbox);
                        TerrainLogger.Info($"OSM cache hit (disk, containing via index): filtered {filtered.Features.Count} features from larger cached region");

                        // Also add to memory cache for faster subsequent lookups
                        var cacheKey = GetCacheKey(cachedResult.BoundingBox);
                        _memoryCache[cacheKey] = cachedResult;

                        return filtered;
                    }
                }
                catch (Exception ex)
                {
                    TerrainLogger.Warning($"Error reading indexed cache file {Path.GetFileName(entry.FilePath)}: {ex.Message}");
                }
            }
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
    /// Stores a query result in the cache (GZip-compressed on disk).
    /// </summary>
    public async Task SetAsync(GeoBoundingBox bbox, OsmQueryResult result)
    {
        var cacheKey = GetCacheKey(bbox);

        // Store in memory
        _memoryCache[cacheKey] = result;

        // Store to disk (GZip-compressed)
        try
        {
            var filePath = GetCacheFilePath(cacheKey);
            await WriteCompressedCacheFileAsync(filePath, result);

            var fileInfo = new FileInfo(filePath);
            TerrainLogger.Info($"OSM result cached: {cacheKey} ({fileInfo.Length / 1024}KB compressed)");

            // Add to bbox index
            AddToBboxIndex(filePath, bbox, fileInfo.LastWriteTimeUtc);

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

        _memoryCache.TryRemove(cacheKey, out _);

        var filePath = GetCacheFilePath(cacheKey);
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
                InvalidateBboxIndex();
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
    /// Clears all cached data (both compressed and legacy uncompressed files).
    /// </summary>
    public void ClearAll()
    {
        _memoryCache.Clear();
        InvalidateBboxIndex();

        try
        {
            var files = GetAllCacheFiles();
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
    /// Gets cache statistics (covers both compressed and legacy files).
    /// </summary>
    public (int memoryCount, int diskCount, long diskSizeBytes) GetStats()
    {
        var memoryCount = _memoryCache.Count;
        var diskCount = 0;
        long diskSize = 0;

        try
        {
            var files = GetAllCacheFiles();
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
    /// Cleans up expired cache files from disk (both compressed and legacy).
    /// Returns the number of files deleted.
    /// </summary>
    public int CleanupExpired()
    {
        var deletedCount = 0;

        try
        {
            var files = GetAllCacheFiles();
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

    #region GZip Compression

    /// <summary>
    /// Reads and decompresses a GZip-compressed cache file.
    /// </summary>
    private static async Task<OsmQueryResult?> ReadCompressedCacheFileAsync(string filePath)
    {
        await using var fileStream = File.OpenRead(filePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<OsmQueryResult>(gzipStream, JsonOptions);
    }

    /// <summary>
    /// Serializes and GZip-compresses a cache result to disk.
    /// </summary>
    private static async Task WriteCompressedCacheFileAsync(string filePath, OsmQueryResult result)
    {
        await using var fileStream = File.Create(filePath);
        await using var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest);
        await JsonSerializer.SerializeAsync(gzipStream, result, JsonOptions);
    }

    #endregion

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
                var pattern = $"osm_v{CacheVersion}_*{CompressedExtension}";
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
                    TerrainLogger.Info($"Built OSM cache bbox index: {_bboxIndex.Count} entries");
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Error building bbox index: {ex.Message}");
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
            // Ensure index is built before adding
            _bboxIndex ??= new List<CacheBboxEntry>();

            // Remove any existing entry for the same file
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
    /// Parses a bounding box from a cache filename.
    /// Expected format: osm_v3_{minLat:F4}_{minLon:F4}_{maxLat:F4}_{maxLon:F4}.json.gz
    /// </summary>
    private static GeoBoundingBox? ParseBboxFromFileName(string filePath)
    {
        var name = Path.GetFileName(filePath);

        // Remove extension(s)
        if (name.EndsWith(CompressedExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^CompressedExtension.Length];
        else if (name.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^LegacyExtension.Length];

        // Split: ["osm", "v3", "48.1234", "11.5678", "48.5678", "12.1234"]
        var parts = name.Split('_');
        if (parts.Length < 6) return null;

        // Last 4 parts are the bbox coordinates: minLat, minLon, maxLat, maxLon
        if (double.TryParse(parts[^4], CultureInfo.InvariantCulture, out var minLat) &&
            double.TryParse(parts[^3], CultureInfo.InvariantCulture, out var minLon) &&
            double.TryParse(parts[^2], CultureInfo.InvariantCulture, out var maxLat) &&
            double.TryParse(parts[^1], CultureInfo.InvariantCulture, out var maxLon))
        {
            return new GeoBoundingBox(minLon, minLat, maxLon, maxLat);
        }

        return null;
    }

    #endregion

    #region File Helpers

    /// <summary>
    /// Gets all cache files (both compressed v3+ and legacy uncompressed).
    /// </summary>
    private string[] GetAllCacheFiles()
    {
        var compressed = Directory.GetFiles(_cacheDirectory, $"osm_*{CompressedExtension}");
        var legacy = Directory.GetFiles(_cacheDirectory, $"osm_*{LegacyExtension}");
        return compressed.Concat(legacy).ToArray();
    }

    #endregion
}
