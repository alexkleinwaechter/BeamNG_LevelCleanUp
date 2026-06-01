using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     Disk-based cache for OSM map tiles. Downloads tiles via HttpClient with parallel requests,
///     caches them to disk, and serves cached tiles as base64 data URIs for Blazor img elements.
/// </summary>
public sealed class OsmTileCacheService : IDisposable
{
    /// <summary>
    ///     Shared singleton instance.
    /// </summary>
    public static readonly OsmTileCacheService Shared = new();

    private static readonly TimeSpan TileExpiry = TimeSpan.FromDays(30);
    private const int MaxConcurrentDownloads = 6;
    private const string TileServerUrl = "https://tile.openstreetmap.org";

    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _downloadSemaphore = new(MaxConcurrentDownloads);

    /// <summary>
    ///     In-memory cache mapping "zoom/x/y" to data URI (base64-encoded PNG).
    ///     WebView2 blocks file:// URIs, so we serve cached tiles as data URIs.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _dataUriCache = new();

    private OsmTileCacheService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG_LevelCleanUp",
            "OsmCache",
            "Tiles");
        Directory.CreateDirectory(_cacheDirectory);

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "BeamNG-MappingPro/1.0 (tile cache; contact: github.com/alexkleinwaechter/BeamNG_LevelCleanUp)");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    ///     Gets the tile key for cache lookups.
    /// </summary>
    private static string GetTileKey(int zoom, int x, int y) => $"{zoom}/{x}/{y}";

    /// <summary>
    ///     Gets the file path for a cached tile.
    /// </summary>
    private string GetTilePath(int zoom, int x, int y)
    {
        var zoomDir = Path.Combine(_cacheDirectory, zoom.ToString());
        var xDir = Path.Combine(zoomDir, x.ToString());
        return Path.Combine(xDir, $"{y}.png");
    }

    /// <summary>
    ///     Gets the URL to use for an img src — either a cached data URI or the remote tile URL.
    ///     Tiles cached on disk are served as data URIs (WebView2 blocks file:// protocol).
    ///     Triggers background download if not cached.
    /// </summary>
    public string GetTileUrl(int zoom, int x, int y)
    {
        var key = GetTileKey(zoom, x, y);

        // Check in-memory data URI cache first (fastest path)
        if (_dataUriCache.TryGetValue(key, out var dataUri))
            return dataUri;

        // Check disk cache — load into memory as data URI
        var tilePath = GetTilePath(zoom, x, y);
        if (File.Exists(tilePath) && !IsExpired(tilePath))
        {
            var uri = FileToDataUri(tilePath);
            if (uri != null)
            {
                _dataUriCache[key] = uri;
                return uri;
            }
        }

        // Not cached — trigger async download, return remote URL as immediate fallback
        _ = DownloadTileAsync(zoom, x, y);
        return $"{TileServerUrl}/{zoom}/{x}/{y}.png";
    }

    /// <summary>
    ///     Downloads a single tile, saves to disk, and populates the data URI cache.
    /// </summary>
    private async Task DownloadTileAsync(int zoom, int x, int y)
    {
        var key = GetTileKey(zoom, x, y);
        var tilePath = GetTilePath(zoom, x, y);

        await _downloadSemaphore.WaitAsync();
        try
        {
            // Double-check after acquiring semaphore
            if (_dataUriCache.ContainsKey(key))
                return;

            // Check disk again (another thread may have written it)
            if (File.Exists(tilePath) && !IsExpired(tilePath))
            {
                var uri = FileToDataUri(tilePath);
                if (uri != null)
                    _dataUriCache[key] = uri;
                return;
            }

            var url = $"{TileServerUrl}/{zoom}/{x}/{y}.png";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
                return;

            var bytes = await response.Content.ReadAsByteArrayAsync();

            // Save to disk for persistence across sessions
            var dir = Path.GetDirectoryName(tilePath)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(tilePath, bytes);

            // Also cache as data URI for immediate use
            _dataUriCache[key] = "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            // Silently fail — tile will use remote URL fallback
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    /// <summary>
    ///     Checks if a cached tile file has expired.
    /// </summary>
    private static bool IsExpired(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return DateTime.UtcNow - fileInfo.LastWriteTimeUtc > TileExpiry;
    }

    /// <summary>
    ///     Reads a file and returns it as a base64 data URI, or null on failure.
    /// </summary>
    private static string? FileToDataUri(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Gets cache statistics.
    /// </summary>
    public (int cachedTiles, long totalSizeBytes) GetCacheStats()
    {
        if (!Directory.Exists(_cacheDirectory))
            return (0, 0);

        var files = Directory.GetFiles(_cacheDirectory, "*.png", SearchOption.AllDirectories);
        var totalSize = files.Sum(f => new FileInfo(f).Length);
        return (files.Length, totalSize);
    }

    /// <summary>
    ///     Clears all cached tiles.
    /// </summary>
    public void ClearCache()
    {
        _dataUriCache.Clear();
        if (Directory.Exists(_cacheDirectory))
        {
            try
            {
                Directory.Delete(_cacheDirectory, true);
                Directory.CreateDirectory(_cacheDirectory);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _downloadSemaphore.Dispose();
    }
}
