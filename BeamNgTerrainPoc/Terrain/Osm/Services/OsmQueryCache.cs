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
    /// Tolerance for matching requested bounding boxes against cached bounding boxes.
    /// Covers tiny GeoTIFF/crop recalculation and filename rounding differences.
    /// 0.0002° is roughly 22m latitude, less longitude depending on latitude.
    /// </summary>
    public const double BboxContainmentToleranceDegrees = 0.0002;

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
    /// Per-key file locks to prevent concurrent read/write/delete races on the same cache file.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

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
        return string.Format(
            CultureInfo.InvariantCulture,
            "osm_v{0}_{1:F4}_{2:F4}_{3:F4}_{4:F4}",
            CacheVersion,
            bbox.MinLatitude,
            bbox.MinLongitude,
            bbox.MaxLatitude,
            bbox.MaxLongitude);
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
    /// First tries exact match, then checks if any cached bbox contains the requested one,
    /// then checks whether multiple cached bboxes together cover the requested one.
    /// </summary>
    /// <param name="bbox">The bounding box to look up.</param>
    /// <returns>Cached result if found and valid, null otherwise.</returns>
    public async Task<OsmQueryResult?> GetAsync(GeoBoundingBox bbox)
    {
        var cacheKey = GetCacheKey(bbox);

        // 1. Try exact match first (fastest path)
        var exactResult = await GetExactMatchAsync(bbox);
        if (exactResult != null)
            return exactResult;

        // 2. Check if any cached bbox CONTAINS the requested one
        //    This allows reusing cached data when user shrinks the crop region
        var containingResult = await GetFromContainingCacheAsync(bbox);
        if (containingResult != null)
            return containingResult;

        // 3. Check if multiple cached bboxes together cover the requested bbox.
        //    This handles large terrain runs that were cached as chunks, then later
        //    queried as a smaller map crossing chunk boundaries.
        var coveringResult = await GetFromCoveringCachesAsync(bbox);
        if (coveringResult != null)
            return coveringResult;

        TerrainLogger.Info($"OSM cache miss: no exact, containing, or covering match for {cacheKey}");
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

        // Check disk cache (GZip-compressed) with per-key lock
        var filePath = GetCacheFilePath(cacheKey);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var fileLock = GetFileLock(cacheKey);
        await fileLock.WaitAsync();
        try
        {
            // Re-check after acquiring lock (file may have been deleted)
            if (!File.Exists(filePath))
                return null;

            var fileInfo = new FileInfo(filePath);
            if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > _cacheExpiry)
            {
                // Expired, delete file
                SafeDeleteFile(filePath);
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
            try { SafeDeleteFile(filePath); InvalidateBboxIndex(); } catch { }
        }
        finally
        {
            fileLock.Release();
        }

        return null;
    }

    /// <summary>
    /// Checks if multiple cached bounding boxes together cover the requested one.
    /// This is useful when a previous large map was cached as chunk files and a later
    /// smaller map falls across chunk boundaries, so no single chunk contains it.
    /// </summary>
    private async Task<OsmQueryResult?> GetFromCoveringCachesAsync(GeoBoundingBox requestedBbox)
    {
        var now = DateTime.UtcNow;
        var candidates = new Dictionary<string, OsmQueryResult>();

        foreach (var (cacheKey, cachedResult) in _memoryCache)
        {
            if (cachedResult.BoundingBox != null &&
                IntersectsWithTolerance(cachedResult.BoundingBox, requestedBbox) &&
                now - cachedResult.QueryTime < _cacheExpiry)
            {
                candidates[cacheKey] = cachedResult;
            }
        }

        var index = GetOrBuildBboxIndex();
        foreach (var entry in index)
        {
            if (!IntersectsWithTolerance(entry.BBox, requestedBbox) ||
                now - entry.LastWriteUtc > _cacheExpiry)
                continue;

            var entryFileName = Path.GetFileNameWithoutExtension(
                Path.GetFileNameWithoutExtension(entry.FilePath));

            if (candidates.ContainsKey(entryFileName))
                continue;

            var entryLock = GetFileLock(entryFileName);
            await entryLock.WaitAsync();
            try
            {
                if (!File.Exists(entry.FilePath))
                    continue;

                var cachedResult = await ReadCompressedCacheFileAsync(entry.FilePath);
                if (cachedResult?.BoundingBox == null ||
                    !IntersectsWithTolerance(cachedResult.BoundingBox, requestedBbox))
                    continue;

                candidates[entryFileName] = cachedResult;
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Error reading covering cache file {Path.GetFileName(entry.FilePath)}: {ex.Message}");
            }
            finally
            {
                entryLock.Release();
            }
        }

        if (candidates.Count < 2)
            return null;

        var candidateResults = candidates.Values.ToList();
        if (!CoversBbox(candidateResults.Select(c => c.BoundingBox), requestedBbox))
            return null;

        var filteredResults = candidateResults
            .Select(result => FilterFeaturesToBbox(result, requestedBbox))
            .ToList();
        var merged = OsmQueryResult.MergeChunks(filteredResults, requestedBbox);
        merged.IsFromCache = true;
        merged.QueryTime = filteredResults.Min(result => result.QueryTime);

        _memoryCache[GetCacheKey(requestedBbox)] = merged;

        TerrainLogger.Info(
            $"OSM cache hit (covering set): merged {candidateResults.Count} cached regions into " +
            $"{merged.Features.Count} features for requested bbox");

        return merged;
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
                ContainsWithTolerance(cachedResult.BoundingBox, requestedBbox) &&
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
            if (ContainsWithTolerance(entry.BBox, requestedBbox) &&
                DateTime.UtcNow - entry.LastWriteUtc <= _cacheExpiry)
            {
                // Derive cache key from the indexed file's bbox for per-key locking
                var entryFileName = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(entry.FilePath)); // strip .json.gz
                var entryLock = GetFileLock(entryFileName);
                await entryLock.WaitAsync();
                try
                {
                    if (!File.Exists(entry.FilePath))
                        continue;

                    // Only deserialize the one matching file
                    var cachedResult = await ReadCompressedCacheFileAsync(entry.FilePath);

                    if (cachedResult?.BoundingBox != null &&
                        ContainsWithTolerance(cachedResult.BoundingBox, requestedBbox))
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
                finally
                {
                    entryLock.Release();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether one bounding box contains another, allowing small coordinate drift.
    /// </summary>
    public static bool ContainsWithTolerance(
        GeoBoundingBox container,
        GeoBoundingBox requested,
        double toleranceDegrees = BboxContainmentToleranceDegrees)
    {
        return requested.MinLongitude >= container.MinLongitude - toleranceDegrees &&
               requested.MaxLongitude <= container.MaxLongitude + toleranceDegrees &&
               requested.MinLatitude >= container.MinLatitude - toleranceDegrees &&
               requested.MaxLatitude <= container.MaxLatitude + toleranceDegrees;
    }

    /// <summary>
    /// Checks whether two bounding boxes intersect, allowing small coordinate drift.
    /// </summary>
    private static bool IntersectsWithTolerance(
        GeoBoundingBox first,
        GeoBoundingBox second,
        double toleranceDegrees = BboxContainmentToleranceDegrees)
    {
        return first.MinLongitude <= second.MaxLongitude + toleranceDegrees &&
               first.MaxLongitude >= second.MinLongitude - toleranceDegrees &&
               first.MinLatitude <= second.MaxLatitude + toleranceDegrees &&
               first.MaxLatitude >= second.MinLatitude - toleranceDegrees;
    }

    /// <summary>
    /// Checks whether the union of cached bounding boxes covers the requested bounding box.
    /// The grid-cell test is exact for axis-aligned rectangles because cells are split at
    /// every relevant cached/requested bbox edge.
    /// </summary>
    private static bool CoversBbox(IEnumerable<GeoBoundingBox> cachedBboxes, GeoBoundingBox requestedBbox)
    {
        var boxes = cachedBboxes.ToList();
        if (boxes.Count == 0)
            return false;

        var xEdges = new SortedSet<double> { requestedBbox.MinLongitude, requestedBbox.MaxLongitude };
        var yEdges = new SortedSet<double> { requestedBbox.MinLatitude, requestedBbox.MaxLatitude };

        foreach (var box in boxes)
        {
            AddClampedEdge(xEdges, box.MinLongitude, requestedBbox.MinLongitude, requestedBbox.MaxLongitude);
            AddClampedEdge(xEdges, box.MaxLongitude, requestedBbox.MinLongitude, requestedBbox.MaxLongitude);
            AddClampedEdge(yEdges, box.MinLatitude, requestedBbox.MinLatitude, requestedBbox.MaxLatitude);
            AddClampedEdge(yEdges, box.MaxLatitude, requestedBbox.MinLatitude, requestedBbox.MaxLatitude);
        }

        var xValues = xEdges.ToList();
        var yValues = yEdges.ToList();
        for (var xIndex = 0; xIndex < xValues.Count - 1; xIndex++)
        {
            var minLon = xValues[xIndex];
            var maxLon = xValues[xIndex + 1];
            if (maxLon - minLon <= double.Epsilon)
                continue;

            var sampleLon = (minLon + maxLon) / 2.0;
            for (var yIndex = 0; yIndex < yValues.Count - 1; yIndex++)
            {
                var minLat = yValues[yIndex];
                var maxLat = yValues[yIndex + 1];
                if (maxLat - minLat <= double.Epsilon)
                    continue;

                var sampleLat = (minLat + maxLat) / 2.0;
                var sample = new GeoBoundingBox(sampleLon, sampleLat, sampleLon, sampleLat);
                if (!boxes.Any(box => ContainsWithTolerance(box, sample)))
                    return false;
            }
        }

        return true;
    }

    private static void AddClampedEdge(SortedSet<double> edges, double value, double min, double max)
    {
        if (value <= min || value >= max)
            return;

        edges.Add(value);
    }

    /// <summary>
    /// Filters cached features to only those that intersect the requested bounding box.
    /// </summary>
    private static OsmQueryResult FilterFeaturesToBbox(OsmQueryResult cachedResult, GeoBoundingBox requestedBbox)
    {
        var filteredFeatures = cachedResult.Features
            .Where(f => FeatureIntersectsBbox(f, requestedBbox))
            .ToList();
        var filteredWayIds = filteredFeatures
            .Where(feature => feature.FeatureType == OsmFeatureType.Way)
            .Select(feature => feature.Id)
            .ToHashSet();
        var filteredRouteRelations = cachedResult.RouteRelations
            .Where(relation => relation.Members.Any(member => filteredWayIds.Contains(member.WayId)))
            .ToList();

        return new OsmQueryResult
        {
            BoundingBox = requestedBbox,
            Features = filteredFeatures,
            RouteRelations = filteredRouteRelations,
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

        // Store in memory (ConcurrentDictionary — thread-safe)
        _memoryCache[cacheKey] = result;

        // Store to disk (GZip-compressed) with per-key lock
        var fileLock = GetFileLock(cacheKey);
        await fileLock.WaitAsync();
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
        finally
        {
            fileLock.Release();
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
            var fileLock = GetFileLock(cacheKey);
            fileLock.Wait();
            try
            {
                SafeDeleteFile(filePath);
                InvalidateBboxIndex();
                TerrainLogger.Info($"OSM cache invalidated: {cacheKey}");
                CacheChanged?.Invoke();
            }
            catch (Exception ex)
            {
                TerrainLogger.Warning($"Failed to delete OSM cache: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
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
                SafeDeleteFile(file);
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
                        SafeDeleteFile(file);
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
    /// Clears only the in-memory cache tier, releasing deserialized OSM data from RAM.
    /// Disk cache remains intact. Call this after terrain generation completes
    /// to free memory while keeping the disk cache for future runs.
    /// </summary>
    public void ClearMemoryCache()
    {
        var count = _memoryCache.Count;
        _memoryCache.Clear();
        if (count > 0)
            TerrainLogger.Info($"OSM memory cache cleared: {count} entries released");
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
    /// Writes to a temporary file first, then atomically moves it into place
    /// to prevent readers from seeing partially-written data.
    /// </summary>
    private static async Task WriteCompressedCacheFileAsync(string filePath, OsmQueryResult result)
    {
        var tempPath = filePath + ".tmp";
        try
        {
            await using (var fileStream = File.Create(tempPath))
            await using (var gzipStream = new GZipStream(fileStream, CompressionLevel.Fastest))
            {
                await JsonSerializer.SerializeAsync(gzipStream, result, JsonOptions);
            }

            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            // Clean up temp file on failure
            try { File.Delete(tempPath); } catch { }
            throw;
        }
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
    /// <remarks>
    /// The filename stores coordinates at F4 precision (4 decimal places), while regenerated
    /// GeoTIFF/crop bounding boxes can differ by tiny amounts. To prevent false negatives in
    /// the containing-bbox index pre-filter, expand by the same tolerance used for cache matching.
    /// False positives are harmless because the deserialized bounding box is checked again.
    /// </remarks>
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
        if (TryParseFileNameCoordinate(parts[^4], out var minLat) &&
            TryParseFileNameCoordinate(parts[^3], out var minLon) &&
            TryParseFileNameCoordinate(parts[^2], out var maxLat) &&
            TryParseFileNameCoordinate(parts[^1], out var maxLon))
        {
            return new GeoBoundingBox(
                minLon - BboxContainmentToleranceDegrees,
                minLat - BboxContainmentToleranceDegrees,
                maxLon + BboxContainmentToleranceDegrees,
                maxLat + BboxContainmentToleranceDegrees);
        }

        return null;
    }

    private static bool TryParseFileNameCoordinate(string value, out double coordinate)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out coordinate))
            return true;

        // Backward compatibility for cache files written before keys were culture-invariant.
        return double.TryParse(
            value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out coordinate);
    }

    #endregion

    #region File Helpers

    /// <summary>
    /// Gets or creates a per-key SemaphoreSlim for file-level synchronization.
    /// </summary>
    private SemaphoreSlim GetFileLock(string cacheKey)
    {
        return _fileLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Safely deletes a file, ignoring FileNotFoundException (already deleted by another thread).
    /// </summary>
    private static void SafeDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (FileNotFoundException)
        {
            // Already deleted by another thread — not an error
        }
        catch (DirectoryNotFoundException)
        {
            // Directory already removed — not an error
        }
    }

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
