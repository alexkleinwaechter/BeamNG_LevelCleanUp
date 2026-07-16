using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Lidar;

/// <summary>
/// Searches The National Map for USGS 3DEP Lidar Point Cloud products and
/// downloads classified LAZ tiles into a persistent, resumable cache.
/// </summary>
public sealed class UsgsLidarDownloadService : IDisposable
{
    public const string ProductsEndpoint = "https://tnmaccess.nationalmap.gov/api/v1/products";
    public const int MaximumSearchProducts = 5000;

    // TNMAccess takes roughly 20 seconds to build a 100-product LPC page for
    // dense areas. Asking for 1,000 products regularly exceeds its gateway
    // timeout even though the same query succeeds when paged more conservatively.
    private const int PageSize = 100;
    private const int MaximumRetries = 4;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public UsgsLidarDownloadService(HttpClient? httpClient = null) :
        this(httpClient, Task.Delay)
    {
    }

    internal UsgsLidarDownloadService(
        HttpClient? httpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public async Task<UsgsLidarSearchResult> SearchAsync(
        GeoBoundingBox bounds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBounds(bounds);
        var products = new Dictionary<string, UsgsLidarProduct>(StringComparer.OrdinalIgnoreCase);
        var totalAvailable = 0;
        var offset = 0;

        while (offset < MaximumSearchProducts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(offset == 0
                ? "Searching USGS 3DEP classified LAZ tiles..."
                : $"Reading USGS results {offset + 1:N0} and later...");

            var page = await FetchSearchPageWithRetryAsync(bounds, offset, progress, cancellationToken);
            totalAvailable = Math.Max(totalAvailable, page.TotalAvailable);

            foreach (var product in page.Products)
            {
                var key = !string.IsNullOrWhiteSpace(product.SourceId)
                    ? product.SourceId
                    : product.DownloadUri.AbsoluteUri;
                products.TryAdd(key, product);
            }

            if (page.RawItemCount == 0 || offset + page.RawItemCount >= totalAvailable)
                break;

            offset += page.RawItemCount;
        }

        var ordered = products.Values
            .OrderBy(product => product.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(product => product.DownloadUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new UsgsLidarSearchResult(
            totalAvailable,
            ordered,
            totalAvailable > MaximumSearchProducts);
    }

    public static GeoBoundingBox BuildSquareBounds(
        double centerLatitude,
        double centerLongitude,
        double sideMeters)
    {
        if (centerLatitude is < -85 or > 85)
            throw new ArgumentOutOfRangeException(nameof(centerLatitude));
        if (centerLongitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(centerLongitude));
        if (!double.IsFinite(sideMeters) || sideMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(sideMeters));

        const double metersPerDegreeLatitude = 111_320.0;
        var halfSide = sideMeters / 2.0;
        var latitudeDelta = halfSide / metersPerDegreeLatitude;
        var longitudeScale = metersPerDegreeLatitude * Math.Cos(centerLatitude * Math.PI / 180.0);
        var longitudeDelta = halfSide / longitudeScale;

        return new GeoBoundingBox(
            centerLongitude - longitudeDelta,
            centerLatitude - latitudeDelta,
            centerLongitude + longitudeDelta,
            centerLatitude + latitudeDelta);
    }

    private async Task<UsgsLidarSearchPage> FetchSearchPageWithRetryAsync(
        GeoBoundingBox bounds,
        int offset,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        HttpStatusCode? lastStatusCode = null;

        for (var attempt = 1; attempt <= MaximumRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? serverRetryDelay = null;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildSearchUri(bounds, PageSize, offset));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BeamNG-LevelCleanUp", "1.0"));
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    return ParseSearchResponse(json, bounds);
                }

                lastStatusCode = response.StatusCode;
                if (!IsTransientStatus(response.StatusCode))
                    response.EnsureSuccessStatusCode();

                lastError = new HttpRequestException(
                    $"USGS returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
                serverRetryDelay = GetRetryDelay(response, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                lastError = new TimeoutException("The USGS catalog request timed out.", ex);
                lastStatusCode = null;
            }
            catch (UsgsLidarCatalogException ex)
            {
                // TNMAccess sometimes returns HTTP 200 with an error payload while
                // its ScienceBase inventory is busy. Treat that as transient too.
                lastError = ex;
                lastStatusCode = null;
            }
            catch (HttpRequestException ex) when (!ex.StatusCode.HasValue || IsTransientStatus(ex.StatusCode.Value))
            {
                lastError = ex;
                lastStatusCode = ex.StatusCode;
            }

            if (attempt >= MaximumRetries)
                break;

            var reason = lastStatusCode.HasValue
                ? $"{(int)lastStatusCode.Value} {lastStatusCode.Value}"
                : lastError?.Message ?? "a temporary error";
            progress?.Report(
                $"USGS catalog returned {reason}. Retrying results {offset + 1:N0}+ " +
                $"({attempt + 1}/{MaximumRetries})...");
            await _delayAsync(serverRetryDelay ?? GetRetryDelay(null, attempt), cancellationToken);
        }

        throw new HttpRequestException(
            $"The USGS catalog stayed unavailable after {MaximumRetries} attempts. " +
            "No LAZ files were downloaded; retry the search in a few minutes.",
            lastError,
            lastStatusCode);
    }

    public async Task<UsgsLidarDownloadResult> DownloadAsync(
        IEnumerable<UsgsLidarProduct> products,
        string cacheRoot,
        IProgress<UsgsLidarDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
            throw new ArgumentException("A cache directory is required.", nameof(cacheRoot));

        var selected = products
            .GroupBy(product => product.DownloadUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one USGS LAZ tile.");

        Directory.CreateDirectory(cacheRoot);
        var downloaded = 0;
        var reused = 0;
        var paths = new List<string>(selected.Count);

        for (var index = 0; index < selected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var product = selected[index];
            var destinationPath = Path.Combine(cacheRoot, GetCacheFileName(product));

            if (IsValidLasFile(destinationPath, product.SizeInBytes))
            {
                reused++;
                paths.Add(destinationPath);
                progress?.Report(new UsgsLidarDownloadProgress(
                    index + 1, selected.Count, reused, downloaded,
                    $"Reused cached USGS LAZ tile {index + 1:N0}/{selected.Count:N0}"));
                continue;
            }

            await DownloadProductAsync(product, destinationPath, cancellationToken);
            downloaded++;
            paths.Add(destinationPath);
            progress?.Report(new UsgsLidarDownloadProgress(
                index + 1, selected.Count, reused, downloaded,
                $"Downloaded USGS LAZ tile {index + 1:N0}/{selected.Count:N0}"));
        }

        return new UsgsLidarDownloadResult(paths, reused, downloaded);
    }

    internal static Uri BuildSearchUri(GeoBoundingBox bounds, int maximum, int offset)
    {
        ValidateBounds(bounds);
        if (maximum is < 1 or > PageSize)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var bbox = string.Join(",",
            Format(bounds.MinLongitude),
            Format(bounds.MinLatitude),
            Format(bounds.MaxLongitude),
            Format(bounds.MaxLatitude));
        var query = string.Join("&",
            $"bbox={Uri.EscapeDataString(bbox)}",
            $"datasets={Uri.EscapeDataString("Lidar Point Cloud (LPC)")}",
            $"prodFormats={Uri.EscapeDataString("LAS,LAZ")}",
            "outputFormat=JSON",
            $"max={maximum}",
            $"offset={offset}");
        return new Uri($"{ProductsEndpoint}?{query}");
    }

    internal static UsgsLidarSearchPage ParseSearchResponse(string json, GeoBoundingBox requestedBounds)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.String)
        {
            var message = errorElement.GetString();
            throw new UsgsLidarCatalogException(
                string.IsNullOrWhiteSpace(message)
                    ? "The USGS catalog returned an error response."
                    : $"The USGS catalog returned an error: {message}");
        }

        var total = root.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : 0;
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            if (total > 0)
                throw new UsgsLidarCatalogException("The USGS catalog response did not include its product list.");
            return new UsgsLidarSearchPage(total, 0, Array.Empty<UsgsLidarProduct>());
        }

        var products = new List<UsgsLidarProduct>();
        foreach (var item in items.EnumerateArray())
        {
            var downloadUrl = FirstString(item, "downloadLazURL", "downloadURL");
            if (string.IsNullOrWhiteSpace(downloadUrl) &&
                item.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Object)
                downloadUrl = FirstString(urls, "LAZ", "LAS");
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps)
                continue;

            if (!TryReadBounds(item, out var productBounds) || !Intersects(requestedBounds, productBounds))
                continue;

            products.Add(new UsgsLidarProduct(
                FirstString(item, "sourceId") ?? string.Empty,
                FirstString(item, "title") ?? Path.GetFileName(downloadUri.LocalPath),
                downloadUri,
                TryReadInt64(item, "sizeInBytes"),
                productBounds,
                FirstString(item, "publicationDate", "lastUpdated")));
        }

        return new UsgsLidarSearchPage(total, items.GetArrayLength(), products);
    }

    private async Task DownloadProductAsync(
        UsgsLidarProduct product,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var partialPath = destinationPath + ".partial";
        if (File.Exists(destinationPath) && !IsValidLasFile(destinationPath, product.SizeInBytes))
            File.Delete(destinationPath);

        for (var attempt = 1; attempt <= MaximumRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                using var request = new HttpRequestMessage(HttpMethod.Get, product.DownloadUri);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BeamNG-LevelCleanUp", "1.0"));
                if (existingLength > 0)
                    request.Headers.Range = new RangeHeaderValue(existingLength, null);

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                    IsValidLasFile(partialPath, product.SizeInBytes))
                {
                    File.Move(partialPath, destinationPath, true);
                    return;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // A stale/oversized partial cannot be resumed. Restart it rather than
                    // permanently trapping every future attempt at HTTP 416.
                    if (File.Exists(partialPath)) File.Delete(partialPath);
                    if (attempt < MaximumRetries) continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                    if (append && response.Content.Headers.ContentRange?.From is { } rangeStart &&
                        rangeStart != existingLength)
                    {
                        if (File.Exists(partialPath)) File.Delete(partialPath);
                        if (attempt < MaximumRetries) continue;
                        throw new InvalidDataException(
                            $"USGS returned an invalid resume range for '{product.Title}'.");
                    }

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using (var destination = new FileStream(
                                     partialPath,
                                     append ? FileMode.Append : FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.None,
                                     1024 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken);
                    }

                    if (!IsValidLasFile(partialPath, product.SizeInBytes))
                    {
                        if (attempt < MaximumRetries)
                        {
                            await _delayAsync(GetRetryDelay(response, attempt), cancellationToken);
                            continue;
                        }

                        throw new InvalidDataException(
                            $"USGS returned an incomplete or invalid LAS/LAZ file for '{product.Title}'.");
                    }

                    File.Move(partialPath, destinationPath, true);
                    return;
                }

                if (IsTransientStatus(response.StatusCode) && attempt < MaximumRetries)
                {
                    await _delayAsync(GetRetryDelay(response, attempt), cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException) when (attempt < MaximumRetries)
            {
                await _delayAsync(GetRetryDelay(null, attempt), cancellationToken);
            }
            catch (HttpRequestException ex) when (
                attempt < MaximumRetries &&
                (!ex.StatusCode.HasValue || IsTransientStatus(ex.StatusCode.Value)))
            {
                await _delayAsync(GetRetryDelay(null, attempt), cancellationToken);
            }
            catch (HttpIOException) when (attempt < MaximumRetries)
            {
                // The partial file remains on disk and the next request resumes it.
                await _delayAsync(GetRetryDelay(null, attempt), cancellationToken);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
    }

    private static string GetCacheFileName(UsgsLidarProduct product)
    {
        var fileName = Uri.UnescapeDataString(Path.GetFileName(product.DownloadUri.LocalPath));
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = string.IsNullOrWhiteSpace(product.SourceId) ? "usgs_3dep.laz" : $"{product.SourceId}.laz";

        var invalid = Path.GetInvalidFileNameChars();
        fileName = new string(fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".laz", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".las", StringComparison.OrdinalIgnoreCase))
            extension = ".laz";
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length > 120)
            stem = stem[..120];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(product.DownloadUri.AbsoluteUri)))[..10]
            .ToLowerInvariant();
        return $"{stem}_{hash}{extension}";
    }

    private static bool IsValidLasFile(string path, long expectedSize)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 227 || expectedSize > 0 && info.Length != expectedSize)
                return false;
            Span<byte> signature = stackalloc byte[4];
            using var stream = info.OpenRead();
            return stream.Read(signature) == 4 && signature.SequenceEqual("LASF"u8);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBounds(JsonElement item, out GeoBoundingBox bounds)
    {
        bounds = null!;
        if (!item.TryGetProperty("boundingBox", out var bbox) || bbox.ValueKind != JsonValueKind.Object ||
            !TryReadDouble(bbox, "minX", out var minX) || !TryReadDouble(bbox, "minY", out var minY) ||
            !TryReadDouble(bbox, "maxX", out var maxX) || !TryReadDouble(bbox, "maxY", out var maxY))
            return false;
        bounds = new GeoBoundingBox(minX, minY, maxX, maxY);
        return bounds.IsValidWgs84 && bounds.Width > 0 && bounds.Height > 0;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }

    private static long TryReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetDouble(out value);
        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool Intersects(GeoBoundingBox left, GeoBoundingBox right) =>
        left.MinLongitude <= right.MaxLongitude && left.MaxLongitude >= right.MinLongitude &&
        left.MinLatitude <= right.MaxLatitude && left.MaxLatitude >= right.MinLatitude;

    private static void ValidateBounds(GeoBoundingBox bounds)
    {
        if (bounds is not { IsValidWgs84: true } || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("Valid WGS84 map bounds are required.", nameof(bounds));
    }

    private static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

public sealed record UsgsLidarProduct(
    string SourceId,
    string Title,
    Uri DownloadUri,
    long SizeInBytes,
    GeoBoundingBox Bounds,
    string? PublicationDate);

public sealed record UsgsLidarSearchResult(
    int TotalAvailable,
    IReadOnlyList<UsgsLidarProduct> Products,
    bool WasTruncated)
{
    public long TotalSizeInBytes => Products.Sum(product => Math.Max(0, product.SizeInBytes));
}

internal sealed record UsgsLidarSearchPage(
    int TotalAvailable,
    int RawItemCount,
    IReadOnlyList<UsgsLidarProduct> Products);

internal sealed class UsgsLidarCatalogException(string message) : Exception(message);

public sealed record UsgsLidarDownloadProgress(
    int CompletedFiles,
    int TotalFiles,
    int ReusedFiles,
    int DownloadedFiles,
    string Message);

public sealed record UsgsLidarDownloadResult(
    IReadOnlyList<string> FilePaths,
    int ReusedFiles,
    int DownloadedFiles);
