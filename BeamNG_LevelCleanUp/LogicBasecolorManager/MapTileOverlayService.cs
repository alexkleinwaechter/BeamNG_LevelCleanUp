using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using OSGeo.OSR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeamNG_LevelCleanUp.LogicBasecolorManager;

public class MapTileOverlayService
{
    private const int TileSize = 256;
    private const double WebMercatorMaxLatitude = 85.05112878;
    private const string WaybackConfigUrl = "https://s3-us-west-2.amazonaws.com/config.maptiles.arcgis.com/waybackconfig.json";
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim WaybackConfigLock = new(1, 1);
    private static IReadOnlyList<WaybackRelease>? _waybackReleases;

    public static IReadOnlyList<MapTileProvider> Providers { get; } =
    [
        new("OSM", "osm", "https://tile.openstreetmap.org/{z}/{x}/{y}.png"),
        new("Google Roadmap", "google-roadmap", "https://mt0.google.com/vt/lyrs=m&hl=en&x={x}&y={y}&z={z}"),
        new("Google Terrain", "google-terrain", "https://mt0.google.com/vt/lyrs=p&hl=en&x={x}&y={y}&z={z}"),
        new("Google Satelite Only", "google-satelite-only", "https://mt0.google.com/vt/lyrs=s&hl=en&x={x}&y={y}&z={z}"),
        new("Google Hybrid", "google-hybrid", "https://mt0.google.com/vt/lyrs=y&hl=en&x={x}&y={y}&z={z}"),
        new("ArcGIS Satelite", "arcgis-satelite", "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"),
        new("ArcGIS Wayback (dated)", "arcgis-wayback", string.Empty, true)
    ];

    public bool HasOverlayCache(string levelPath, string providerName, string? imageryDate = null)
    {
        var provider = ResolveProvider(providerName);
        if (!TryNormalizeImageryDate(provider, imageryDate, out var normalizedDate))
            return false;

        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        var finalPath = Path.Join(tileRoot, GetFinalImageName(provider, normalizedDate));
        var cachePath = GetCachePath(tileRoot, provider, normalizedDate);
        return File.Exists(finalPath) || Directory.Exists(cachePath);
    }

    public bool HasFinalOverlayImage(string levelPath, string providerName, string? imageryDate = null)
    {
        var provider = ResolveProvider(providerName);
        return TryNormalizeImageryDate(provider, imageryDate, out var normalizedDate) &&
               File.Exists(Path.Join(levelPath, "MT_Tiles", GetFinalImageName(provider, normalizedDate)));
    }

    public string GetFinalOverlayPath(string levelPath, string providerName, string? imageryDate = null)
    {
        var provider = ResolveProvider(providerName);
        return TryNormalizeImageryDate(provider, imageryDate, out var normalizedDate)
            ? Path.Join(levelPath, "MT_Tiles", GetFinalImageName(provider, normalizedDate))
            : string.Empty;
    }

    public MapTileCacheClearResult ClearOverlayCache(string levelPath, string providerName, string? imageryDate = null)
    {
        var provider = ResolveProvider(providerName);
        if (!TryNormalizeImageryDate(provider, imageryDate, out var normalizedDate))
            return new MapTileCacheClearResult(provider.Name, 0);

        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        var finalPath = Path.Join(tileRoot, GetFinalImageName(provider, normalizedDate));
        var cachePath = GetCachePath(tileRoot, provider, normalizedDate);
        var deletedItems = 0;

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
            deletedItems++;
        }

        var fingerprintPath = GetWarpFingerprintPath(finalPath);
        if (File.Exists(fingerprintPath))
            File.Delete(fingerprintPath);

        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, true);
            deletedItems++;
        }

        var cacheLabel = provider.SupportsHistoricalDate ? $"{provider.Name} {normalizedDate}" : provider.Name;
        return new MapTileCacheClearResult(cacheLabel, deletedItems);
    }

    public async Task<MapTileOverlayResult> EnsureOverlayImageAsync(
        string levelPath,
        MtGeoReferenceSettings geoReferenceSettings,
        string providerName,
        int outputSize,
        string? imageryDate = null)
    {
        var provider = ResolveProvider(providerName);
        if (!TryNormalizeImageryDate(provider, imageryDate, out var normalizedDate))
            throw new InvalidOperationException($"Select a valid imagery date for {provider.Name} before fetching.");

        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        Directory.CreateDirectory(tileRoot);

        var finalImageName = GetFinalImageName(provider, normalizedDate);
        var finalPath = Path.Join(tileRoot, finalImageName);
        var fingerprintJson = BuildWarpFingerprintJson(geoReferenceSettings, outputSize);
        if (File.Exists(finalPath))
        {
            if (WarpFingerprintMatches(finalPath, fingerprintJson))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Using cached map tile overlay {finalImageName}.");
                return new MapTileOverlayResult(finalPath, provider.Name, normalizedDate, null, true);
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Cached overlay {finalImageName} no longer matches the terrain georeference or size and will be rebuilt. Already downloaded tiles are reused.");
        }

        WaybackRelease? waybackRelease = null;
        var requestProvider = provider;
        if (provider.SupportsHistoricalDate)
        {
            var requestedDate = DateOnly.ParseExact(normalizedDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            waybackRelease = await ResolveWaybackReleaseAsync(requestedDate);
            requestProvider = provider with { UrlTemplate = waybackRelease.TileUrlTemplate };

            var releaseMessage = waybackRelease.ReleaseDate == requestedDate
                ? $"Using ArcGIS Wayback release {waybackRelease.ReleaseDate:yyyy-MM-dd}."
                : waybackRelease.ReleaseDate < requestedDate
                    ? $"No ArcGIS Wayback release exists on {requestedDate:yyyy-MM-dd}; using the latest release on or before it: {waybackRelease.ReleaseDate:yyyy-MM-dd}."
                    : $"The requested date {requestedDate:yyyy-MM-dd} predates the Wayback archive; using its earliest release: {waybackRelease.ReleaseDate:yyyy-MM-dd}.";
            PubSubChannel.SendMessage(PubSubMessageType.Info, releaseMessage);
        }

        ValidateGeoReference(geoReferenceSettings);
        var zoom = ChooseZoom(geoReferenceSettings.TerrainCenterLatitude, geoReferenceSettings.TerrainMetersPerPixel);
        var northWest = LonLatToTile(geoReferenceSettings.TerrainMinLongitude, geoReferenceSettings.TerrainMaxLatitude, zoom);
        var southEast = LonLatToTile(geoReferenceSettings.TerrainMaxLongitude, geoReferenceSettings.TerrainMinLatitude, zoom);

        var minTileX = Math.Min(northWest.X, southEast.X);
        var maxTileX = Math.Max(northWest.X, southEast.X);
        var minTileY = Math.Min(northWest.Y, southEast.Y);
        var maxTileY = Math.Max(northWest.Y, southEast.Y);
        var tileCount = (maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Fetching {provider.Name} overlay at zoom {zoom} ({tileCount} tiles). Existing cached tiles are reused.");

        var fallbackTileCount = 0;
        var cachePath = GetCachePath(tileRoot, provider, normalizedDate);
        using var mosaic = new Image<Rgba32>((maxTileX - minTileX + 1) * TileSize, (maxTileY - minTileY + 1) * TileSize);
        for (var y = minTileY; y <= maxTileY; y++)
        for (var x = minTileX; x <= maxTileX; x++)
        {
            using var tile = await LoadTileAsync(cachePath, requestProvider, zoom, x, y);
            if (tile.UsedFallback)
                fallbackTileCount++;

            var destX = (x - minTileX) * TileSize;
            var destY = (y - minTileY) * TileSize;
            mosaic.Mutate(ctx => ctx.DrawImage(tile.Image, new SixLabors.ImageSharp.Point(destX, destY), 1f));
        }

        if (fallbackTileCount > 0)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"{provider.Name} did not return {fallbackTileCount} of {tileCount} tile(s). Transparent fallback was used for missing areas.");
        }

        if (fallbackTileCount == tileCount)
        {
            throw new InvalidOperationException(
                $"{provider.Name} did not return any map tiles for this area. Check the georeference bounds, try another provider, or clear the cache and retry later.");
        }

        var west = LonLatToWorldPixel(geoReferenceSettings.TerrainMinLongitude, geoReferenceSettings.TerrainMaxLatitude, zoom);
        var east = LonLatToWorldPixel(geoReferenceSettings.TerrainMaxLongitude, geoReferenceSettings.TerrainMinLatitude, zoom);
        var mosaicOriginX = minTileX * TileSize;
        var mosaicOriginY = minTileY * TileSize;
        using var output = CanWarpFromNativeGeoReference(geoReferenceSettings)
            ? CreateWarpedOverlay(mosaic, geoReferenceSettings, zoom, mosaicOriginX, mosaicOriginY, outputSize)
            : CreateBoundingBoxOverlay(mosaic, west, east, mosaicOriginX, mosaicOriginY, outputSize);
        if (File.Exists(finalPath))
            File.Delete(finalPath);
        output.SaveAsPng(finalPath);
        File.WriteAllText(GetWarpFingerprintPath(finalPath), fingerprintJson);

        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Saved map tile overlay to {Path.GetFileName(finalPath)}.");
        return new MapTileOverlayResult(
            finalPath,
            provider.Name,
            normalizedDate,
            waybackRelease?.ReleaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public static bool HasUsableGeoReference(MtGeoReferenceSettings settings)
    {
        return settings.HasGeoReference &&
               settings.TerrainMinLongitude < settings.TerrainMaxLongitude &&
               settings.TerrainMinLatitude < settings.TerrainMaxLatitude &&
               settings.TerrainCenterLatitude >= -WebMercatorMaxLatitude &&
               settings.TerrainCenterLatitude <= WebMercatorMaxLatitude;
    }

    private static bool CanWarpFromNativeGeoReference(MtGeoReferenceSettings settings)
    {
        return settings.SourceGeoTransform is { Length: 6 } &&
               settings.SourceRasterWidth > 0 &&
               settings.SourceRasterHeight > 0 &&
               !string.IsNullOrWhiteSpace(settings.ProjectionWkt);
    }

    private static Image<Rgba32> CreateBoundingBoxOverlay(
        Image<Rgba32> mosaic,
        PixelCoordinate west,
        PixelCoordinate east,
        int mosaicOriginX,
        int mosaicOriginY,
        int outputSize)
    {
        var cropLeft = Math.Clamp((int)Math.Floor(west.X - mosaicOriginX), 0, mosaic.Width - 1);
        var cropTop = Math.Clamp((int)Math.Floor(west.Y - mosaicOriginY), 0, mosaic.Height - 1);
        var cropRight = Math.Clamp((int)Math.Ceiling(east.X - mosaicOriginX), cropLeft + 1, mosaic.Width);
        var cropBottom = Math.Clamp((int)Math.Ceiling(east.Y - mosaicOriginY), cropTop + 1, mosaic.Height);
        var crop = new SixLabors.ImageSharp.Rectangle(cropLeft, cropTop, cropRight - cropLeft, cropBottom - cropTop);

        return mosaic.Clone(ctx => ctx.Crop(crop).Resize(outputSize, outputSize));
    }

    private static Image<Rgba32> CreateWarpedOverlay(
        Image<Rgba32> mosaic,
        MtGeoReferenceSettings settings,
        int zoom,
        int mosaicOriginX,
        int mosaicOriginY,
        int outputSize)
    {
        GeoTiffReader.InitializeGdal();

        using var nativeToWgs84 = CreateNativeToWgs84Transformation(settings.ProjectionWkt);
        using var output = new Image<Rgba32>(outputSize, outputSize);
        var geoTransform = settings.SourceGeoTransform;
        var sourceWidth = settings.SourceRasterWidth;
        var sourceHeight = settings.SourceRasterHeight;

        for (var y = 0; y < outputSize; y++)
        for (var x = 0; x < outputSize; x++)
        {
            var sourcePixelX = (x + 0.5) * sourceWidth / outputSize;
            var sourcePixelY = (y + 0.5) * sourceHeight / outputSize;
            var native = PixelToNative(geoTransform, sourcePixelX, sourcePixelY);
            var lonLat = TransformNativeToWgs84(nativeToWgs84, native.X, native.Y);

            if (lonLat.Latitude < -WebMercatorMaxLatitude || lonLat.Latitude > WebMercatorMaxLatitude)
            {
                output[x, y] = new Rgba32(0, 0, 0, 0);
                continue;
            }

            var worldPixel = LonLatToWorldPixel(lonLat.Longitude, lonLat.Latitude, zoom);
            output[x, y] = SampleBilinear(mosaic, worldPixel.X - mosaicOriginX, worldPixel.Y - mosaicOriginY);
        }

        return output.Clone();
    }

    private static CoordinateTransformation? CreateNativeToWgs84Transformation(string projectionWkt)
    {
        if (GeoBoundingBox.IsWgs84Projection(projectionWkt))
            return null;

        var sourceSrs = new SpatialReference(null);
        var wkt = projectionWkt;
        if (sourceSrs.ImportFromWkt(ref wkt) != 0)
            throw new InvalidOperationException("Could not parse the saved terrain projection WKT for tile overlay reprojection.");

        var targetSrs = new SpatialReference(null);
        targetSrs.ImportFromEPSG(4326);
        sourceSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        targetSrs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        return new CoordinateTransformation(sourceSrs, targetSrs);
    }

    private static (double X, double Y) PixelToNative(double[] geoTransform, double pixelX, double pixelY)
    {
        return (
            geoTransform[0] + pixelX * geoTransform[1] + pixelY * geoTransform[2],
            geoTransform[3] + pixelX * geoTransform[4] + pixelY * geoTransform[5]);
    }

    private static (double Longitude, double Latitude) TransformNativeToWgs84(
        CoordinateTransformation? nativeToWgs84,
        double nativeX,
        double nativeY)
    {
        if (nativeToWgs84 == null)
            return (nativeX, nativeY);

        double[] point = [nativeX, nativeY, 0];
        nativeToWgs84.TransformPoint(point);
        return (point[0], point[1]);
    }

    private static Rgba32 SampleBilinear(Image<Rgba32> image, double x, double y)
    {
        if (x < 0 || y < 0 || x > image.Width - 1 || y > image.Height - 1)
            return new Rgba32(0, 0, 0, 0);

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(x0 + 1, image.Width - 1);
        var y1 = Math.Min(y0 + 1, image.Height - 1);
        var fx = x - x0;
        var fy = y - y0;

        var top = Lerp(image[x0, y0], image[x1, y0], fx);
        var bottom = Lerp(image[x0, y1], image[x1, y1], fx);
        return Lerp(top, bottom, fy);
    }

    private static Rgba32 Lerp(Rgba32 from, Rgba32 to, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return new Rgba32(
            (byte)Math.Clamp(Math.Round(from.R + (to.R - from.R) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(from.G + (to.G - from.G) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(from.B + (to.B - from.B) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(from.A + (to.A - from.A) * t), 0, 255));
    }

    private static async Task<LoadedTile> LoadTileAsync(string cacheRoot, MapTileProvider provider, int z, int x, int y)
    {
        var cacheFolder = Path.Join(cacheRoot, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(cacheFolder);
        var cachePath = Path.Join(cacheFolder, y.ToString(CultureInfo.InvariantCulture) + ".img");

        if (File.Exists(cachePath))
        {
            try
            {
                return new LoadedTile(LoadTileImage(await File.ReadAllBytesAsync(cachePath)), false);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cached tile {provider.Name}/{z}/{x}/{y} could not be read and will be fetched again: {ex.Message}");
                File.Delete(cachePath);
            }
        }

        try
        {
            var url = BuildTileUrl(provider.UrlTemplate, z, x, y);
            using var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return CreateFallbackTile();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(cachePath, bytes);
            return new LoadedTile(LoadTileImage(bytes), false);
        }
        catch (HttpRequestException)
        {
            return CreateFallbackTile();
        }
        catch (TaskCanceledException)
        {
            return CreateFallbackTile();
        }
    }

    private static Image<Rgba32> LoadTileImage(byte[] bytes)
    {
        var image = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
        if (image.Width == TileSize && image.Height == TileSize)
            return image;

        image.Mutate(ctx => ctx.Resize(TileSize, TileSize));
        return image;
    }

    private static LoadedTile CreateFallbackTile()
    {
        return new LoadedTile(new Image<Rgba32>(TileSize, TileSize), true);
    }

    private static MapTileProvider ResolveProvider(string providerName)
    {
        return Providers.FirstOrDefault(x => x.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase))
               ?? Providers.First(x => x.Name.Equals("Google Satelite Only", StringComparison.OrdinalIgnoreCase));
    }

    private static int ChooseZoom(double latitude, double terrainMetersPerPixel)
    {
        var safeMetersPerPixel = terrainMetersPerPixel > 0 ? terrainMetersPerPixel : 1.0;
        var latRad = Math.Clamp(latitude, -WebMercatorMaxLatitude, WebMercatorMaxLatitude) * Math.PI / 180.0;
        var exactZoom = Math.Log2(Math.Cos(latRad) * 156543.03392804097 / safeMetersPerPixel);
        return Math.Clamp((int)Math.Ceiling(exactZoom), 1, 19);
    }

    private static TileCoordinate LonLatToTile(double longitude, double latitude, int z)
    {
        var worldPixel = LonLatToWorldPixel(longitude, latitude, z);
        return new TileCoordinate(
            (int)Math.Floor(worldPixel.X / TileSize),
            (int)Math.Floor(worldPixel.Y / TileSize));
    }

    private static PixelCoordinate LonLatToWorldPixel(double longitude, double latitude, int z)
    {
        var clampedLatitude = Math.Clamp(latitude, -WebMercatorMaxLatitude, WebMercatorMaxLatitude);
        var latRad = clampedLatitude * Math.PI / 180.0;
        var scale = TileSize * Math.Pow(2, z);
        var x = (longitude + 180.0) / 360.0 * scale;
        var y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * scale;
        return new PixelCoordinate(x, y);
    }

    private static string BuildTileUrl(string template, int z, int x, int y)
    {
        return template
            .Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture))
            .Replace("{level}", z.ToString(CultureInfo.InvariantCulture))
            .Replace("{col}", x.ToString(CultureInfo.InvariantCulture))
            .Replace("{row}", y.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryNormalizeImageryDate(MapTileProvider provider, string? imageryDate, out string? normalizedDate)
    {
        normalizedDate = null;
        if (!provider.SupportsHistoricalDate)
            return true;

        if (!DateOnly.TryParseExact(imageryDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
            return false;

        normalizedDate = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private static string GetFinalImageName(MapTileProvider provider, string? normalizedDate)
    {
        return provider.SupportsHistoricalDate
            ? $"{provider.Slug}-{normalizedDate}-terrain-warp-v2.png"
            : provider.FinalImageName;
    }

    private static string GetCachePath(string tileRoot, MapTileProvider provider, string? normalizedDate)
    {
        var providerCache = Path.Join(tileRoot, "cache", provider.Slug);
        return provider.SupportsHistoricalDate ? Path.Join(providerCache, normalizedDate!) : providerCache;
    }

    private static string GetWarpFingerprintPath(string finalPath) => finalPath + ".meta.json";

    private static string BuildWarpFingerprintJson(MtGeoReferenceSettings settings, int outputSize)
    {
        var fingerprint = new WarpFingerprint(
            1,
            settings.TerrainMinLongitude,
            settings.TerrainMinLatitude,
            settings.TerrainMaxLongitude,
            settings.TerrainMaxLatitude,
            settings.TerrainCenterLatitude,
            settings.TerrainMetersPerPixel,
            settings.SourceGeoTransform ?? [],
            settings.SourceRasterWidth,
            settings.SourceRasterHeight,
            settings.ProjectionWkt ?? string.Empty,
            outputSize);
        return JsonSerializer.Serialize(fingerprint);
    }

    private static bool WarpFingerprintMatches(string finalPath, string expectedFingerprintJson)
    {
        var fingerprintPath = GetWarpFingerprintPath(finalPath);
        if (!File.Exists(fingerprintPath))
            return false;

        try
        {
            return string.Equals(File.ReadAllText(fingerprintPath), expectedFingerprintJson, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<WaybackRelease> ResolveWaybackReleaseAsync(DateOnly requestedDate)
    {
        var releases = await GetWaybackReleasesAsync();
        var release = releases.FirstOrDefault(candidate => candidate.ReleaseDate <= requestedDate)
                      ?? releases.LastOrDefault();

        return release ?? throw new InvalidOperationException("ArcGIS Wayback did not return any dated imagery releases.");
    }

    private static async Task<IReadOnlyList<WaybackRelease>> GetWaybackReleasesAsync()
    {
        if (_waybackReleases != null)
            return _waybackReleases;

        await WaybackConfigLock.WaitAsync();
        try
        {
            if (_waybackReleases != null)
                return _waybackReleases;

            using var response = await HttpClient.GetAsync(WaybackConfigUrl);
            response.EnsureSuccessStatusCode();
            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(responseStream);
            var releases = new List<WaybackRelease>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object ||
                    !property.Value.TryGetProperty("itemTitle", out var titleElement) ||
                    !property.Value.TryGetProperty("itemURL", out var urlElement))
                    continue;

                var title = titleElement.GetString() ?? string.Empty;
                var tileUrlTemplate = urlElement.GetString() ?? string.Empty;
                const string marker = "Wayback ";
                var markerIndex = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                var dateStart = markerIndex < 0 ? -1 : markerIndex + marker.Length;
                if (dateStart < 0 || title.Length < dateStart + 10 || string.IsNullOrWhiteSpace(tileUrlTemplate))
                    continue;

                var dateText = title.Substring(dateStart, 10);
                if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var releaseDate))
                    continue;

                releases.Add(new WaybackRelease(releaseDate, tileUrlTemplate));
            }

            _waybackReleases = releases
                .OrderByDescending(release => release.ReleaseDate)
                .ToArray();
            return _waybackReleases;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new InvalidOperationException("Could not load the ArcGIS Wayback imagery-date catalog. Check the internet connection and try again.", ex);
        }
        finally
        {
            WaybackConfigLock.Release();
        }
    }

    private static void ValidateGeoReference(MtGeoReferenceSettings settings)
    {
        if (!HasUsableGeoReference(settings))
            throw new InvalidOperationException("The level does not have usable WGS84 georeference settings for map tile fetching.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BeamNG-LevelCleanUp/1.0 BasecolorManager");
        return client;
    }

    private readonly record struct TileCoordinate(int X, int Y);
    private readonly record struct PixelCoordinate(double X, double Y);
    private sealed record WaybackRelease(DateOnly ReleaseDate, string TileUrlTemplate);

    private sealed record WarpFingerprint(
        int Version,
        double MinLongitude,
        double MinLatitude,
        double MaxLongitude,
        double MaxLatitude,
        double CenterLatitude,
        double MetersPerPixel,
        double[] GeoTransform,
        int RasterWidth,
        int RasterHeight,
        string ProjectionWkt,
        int OutputSize);

    private sealed class LoadedTile : IDisposable
    {
        public LoadedTile(Image<Rgba32> image, bool usedFallback)
        {
            Image = image;
            UsedFallback = usedFallback;
        }

        public Image<Rgba32> Image { get; }
        public bool UsedFallback { get; }

        public void Dispose()
        {
            Image.Dispose();
        }
    }
}

public sealed record MapTileProvider(string Name, string Slug, string UrlTemplate, bool SupportsHistoricalDate = false)
{
    public string FinalImageName => Slug + "-terrain-warp-v2.png";
}

public sealed record MapTileOverlayResult(
    string ImagePath,
    string ProviderName,
    string? RequestedDate,
    string? ResolvedReleaseDate,
    bool ReusedFinalImage = false);

public sealed record MapTileCacheClearResult(string ProviderName, int DeletedItems)
{
    public string Message => DeletedItems == 0
        ? $"No cached {ProviderName} tile overlay files were found."
        : $"Cleared cached {ProviderName} tile overlay files.";
}
