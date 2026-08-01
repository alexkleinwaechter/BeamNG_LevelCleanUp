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
        // OSM tile usage policy allows at most 2 parallel connections.
        new("OSM", "osm", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", MaxParallelDownloads: 2),
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
        var cachePath = GetCachePath(Path.Join(tileRoot, "cache"), provider, normalizedDate);
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
        var cachePath = GetCachePath(Path.Join(tileRoot, "cache"), provider, normalizedDate);
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

    /// <summary>
    /// Legacy entry point for the terrain overlay: builds an <see cref="OverlayRequest"/> from the
    /// saved terrain georeference settings and delegates to <see cref="EnsureOverlayImageAsync(OverlayRequest)"/>.
    /// Kept so existing call sites (BasecolorManager) compile untouched.
    /// </summary>
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

        // OverlayRequest has no HasGeoReference concept (a raw bbox request doesn't need one) — this
        // is the one entry point that does, so the full old MtGeoReferenceSettings validation
        // (including the HasGeoReference flag) still needs to run here before delegating.
        if (!HasUsableGeoReference(geoReferenceSettings))
            throw new InvalidOperationException("The level does not have usable WGS84 georeference settings for map tile fetching.");

        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        var request = new OverlayRequest(
            Wgs84Bounds: new GeoBoundingBox(
                geoReferenceSettings.TerrainMinLongitude,
                geoReferenceSettings.TerrainMinLatitude,
                geoReferenceSettings.TerrainMaxLongitude,
                geoReferenceSettings.TerrainMaxLatitude),
            MetersPerPixel: geoReferenceSettings.TerrainMetersPerPixel,
            NativeGeoTransform: CanWarpFromNativeGeoReference(geoReferenceSettings) ? geoReferenceSettings.SourceGeoTransform : null,
            NativeRasterWidth: geoReferenceSettings.SourceRasterWidth,
            NativeRasterHeight: geoReferenceSettings.SourceRasterHeight,
            ProjectionWkt: geoReferenceSettings.ProjectionWkt,
            OutputSize: outputSize,
            OutputPath: Path.Join(tileRoot, GetFinalImageName(provider, normalizedDate)),
            TileCacheRoot: Path.Join(tileRoot, "cache"),
            ProviderName: providerName,
            ImageryDate: imageryDate);

        return await EnsureOverlayImageAsync(request);
    }

    /// <summary>
    /// Fetches (or reuses) a map tile overlay for an arbitrary WGS84 bounding box, warping it to
    /// either the box's linear extent (bbox-only) or a native raster's geotransform when supplied.
    /// This is the shared machinery behind both the terrain overlay (see the legacy overload above)
    /// and the backdrop texture baker.
    /// </summary>
    public async Task<MapTileOverlayResult> EnsureOverlayImageAsync(OverlayRequest request)
    {
        var provider = ResolveProvider(request.ProviderName);
        if (!TryNormalizeImageryDate(provider, request.ImageryDate, out var normalizedDate))
            throw new InvalidOperationException($"Select a valid imagery date for {provider.Name} before fetching.");

        var outputDirectory = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var finalPath = request.OutputPath;
        var finalImageName = Path.GetFileName(finalPath);
        var fingerprintJson = BuildWarpFingerprintJson(request);
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

        ValidateRequestBounds(request);
        var zoom = ChooseZoom(request.Wgs84Bounds.Center.Latitude, request.MetersPerPixel);
        var (minTileX, maxTileX, minTileY, maxTileY) = GetTileSpan(request.Wgs84Bounds, zoom);
        var tileCount = (maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Fetching {provider.Name} overlay at zoom {zoom} ({tileCount} tiles). Existing cached tiles are reused.");

        var fallbackTileCount = 0;
        var cachePath = GetCachePath(request.TileCacheRoot, provider, normalizedDate);
        using var mosaic = new Image<Rgba32>((maxTileX - minTileX + 1) * TileSize, (maxTileY - minTileY + 1) * TileSize);

        // Downloads run in parallel (bounded per provider); compositing stays on this
        // thread because ImageSharp images are not safe for concurrent mutation.
        var inFlight = new Queue<(int X, int Y, Task<LoadedTile> Tile)>();
        async Task DrawOldestInFlightTileAsync()
        {
            var (x, y, tileTask) = inFlight.Dequeue();
            using var tile = await tileTask;
            if (tile.UsedFallback)
                fallbackTileCount++;

            var destX = (x - minTileX) * TileSize;
            var destY = (y - minTileY) * TileSize;
            mosaic.Mutate(ctx => ctx.DrawImage(tile.Image, new SixLabors.ImageSharp.Point(destX, destY), 1f));
        }

        var maxParallelDownloads = Math.Max(1, requestProvider.MaxParallelDownloads);
        for (var y = minTileY; y <= maxTileY; y++)
        for (var x = minTileX; x <= maxTileX; x++)
        {
            inFlight.Enqueue((x, y, LoadTileAsync(cachePath, requestProvider, zoom, x, y)));
            if (inFlight.Count >= maxParallelDownloads)
                await DrawOldestInFlightTileAsync();
        }

        while (inFlight.Count > 0)
            await DrawOldestInFlightTileAsync();

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

        var west = LonLatToWorldPixel(request.Wgs84Bounds.MinLongitude, request.Wgs84Bounds.MaxLatitude, zoom);
        var east = LonLatToWorldPixel(request.Wgs84Bounds.MaxLongitude, request.Wgs84Bounds.MinLatitude, zoom);
        var mosaicOriginX = minTileX * TileSize;
        var mosaicOriginY = minTileY * TileSize;
        using var output = CanWarpFromNativeGeoReference(request)
            ? CreateWarpedOverlay(mosaic, request, zoom, mosaicOriginX, mosaicOriginY, request.OutputSize)
            : CreateBoundingBoxOverlay(mosaic, west, east, mosaicOriginX, mosaicOriginY, request.OutputSize);
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

    /// <summary>
    /// Counts the tiles a fetch for <paramref name="bounds"/> would need at the zoom level
    /// <see cref="ChooseZoom"/> picks for <paramref name="metersPerPixel"/>. Used by the cost
    /// estimator before committing to a download.
    /// </summary>
    public static int CountTilesForBounds(GeoBoundingBox bounds, double metersPerPixel)
    {
        var zoom = ChooseZoom(bounds.Center.Latitude, metersPerPixel);
        var (minTileX, maxTileX, minTileY, maxTileY) = GetTileSpan(bounds, zoom);
        return (maxTileX - minTileX + 1) * (maxTileY - minTileY + 1);
    }

    /// <summary>
    /// Public wrapper around the private <see cref="ChooseZoom"/> zoom-selection math, for callers
    /// that need to know which on-disk cache folder (<c>.../cache/{slug}/{zoom}/...</c>) a fetch for
    /// <paramref name="bounds"/> would use without performing one — e.g. the backdrop cost estimator,
    /// which counts already-cached tiles at that zoom before subtracting them from a download estimate.
    /// </summary>
    public static int ChooseZoomForBounds(GeoBoundingBox bounds, double metersPerPixel) =>
        ChooseZoom(bounds.Center.Latitude, metersPerPixel);

    /// <summary>
    /// Resolves the on-disk tile cache directory a fetch for <paramref name="providerName"/> /
    /// <paramref name="imageryDate"/> at <paramref name="zoom"/> would read/write, routing through the
    /// SAME <see cref="GetCachePath"/> + <see cref="TryNormalizeImageryDate"/> logic
    /// <see cref="EnsureOverlayImageAsync(OverlayRequest)"/> uses — so date-suffixed providers (e.g.
    /// ArcGIS Wayback) get the correct extra <c>{date}</c> path segment instead of a caller re-deriving
    /// (and potentially under-nesting) the layout itself. Used by the backdrop cost estimator to count
    /// already-cached tiles before subtracting them from a download estimate. An unparsable/missing
    /// <paramref name="imageryDate"/> for a date-supporting provider falls back to the non-dated path
    /// (a safe under-count — <c>Directory.Exists</c> simply returns false — rather than throwing).
    /// </summary>
    public static string GetProviderCacheDirectory(string levelPath, string providerName, string? imageryDate, int zoom)
    {
        var provider = ResolveProvider(providerName);
        TryNormalizeImageryDate(provider, imageryDate, out var normalizedDate);
        var cacheRoot = Path.Join(levelPath, "MT_Tiles", "cache");
        return Path.Join(GetCachePath(cacheRoot, provider, normalizedDate), zoom.ToString(CultureInfo.InvariantCulture));
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

    private static bool CanWarpFromNativeGeoReference(OverlayRequest request)
    {
        return request.NativeGeoTransform is { Length: 6 } &&
               request.NativeRasterWidth > 0 &&
               request.NativeRasterHeight > 0 &&
               !string.IsNullOrWhiteSpace(request.ProjectionWkt);
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
        OverlayRequest request,
        int zoom,
        int mosaicOriginX,
        int mosaicOriginY,
        int outputSize)
    {
        GeoTiffReader.InitializeGdal();

        using var nativeToWgs84 = CreateNativeToWgs84Transformation(request.ProjectionWkt!);
        using var output = new Image<Rgba32>(outputSize, outputSize);
        var geoTransform = request.NativeGeoTransform!;
        var sourceWidth = request.NativeRasterWidth;
        var sourceHeight = request.NativeRasterHeight;

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

    /// <summary>
    /// Computes the inclusive tile-index span covering <paramref name="bounds"/> at zoom
    /// <paramref name="zoom"/>. Shared by the fetch loop and <see cref="CountTilesForBounds"/>.
    /// </summary>
    private static (int MinTileX, int MaxTileX, int MinTileY, int MaxTileY) GetTileSpan(GeoBoundingBox bounds, int zoom)
    {
        var northWest = LonLatToTile(bounds.MinLongitude, bounds.MaxLatitude, zoom);
        var southEast = LonLatToTile(bounds.MaxLongitude, bounds.MinLatitude, zoom);
        return (
            Math.Min(northWest.X, southEast.X),
            Math.Max(northWest.X, southEast.X),
            Math.Min(northWest.Y, southEast.Y),
            Math.Max(northWest.Y, southEast.Y));
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

    /// <summary>
    /// Resolves the on-disk tile cache folder for a provider/date under a given cache root.
    /// <paramref name="cacheRoot"/> is already the full "…/MT_Tiles/cache" path (shared across
    /// terrain and backdrop requests); callers derive it from the level path.
    /// </summary>
    private static string GetCachePath(string cacheRoot, MapTileProvider provider, string? normalizedDate)
    {
        var providerCache = Path.Join(cacheRoot, provider.Slug);
        return provider.SupportsHistoricalDate ? Path.Join(providerCache, normalizedDate!) : providerCache;
    }

    private static string GetWarpFingerprintPath(string finalPath) => finalPath + ".meta.json";

    private static string BuildWarpFingerprintJson(OverlayRequest request)
    {
        var bounds = request.Wgs84Bounds;
        var fingerprint = new WarpFingerprint(
            1,
            bounds.MinLongitude,
            bounds.MinLatitude,
            bounds.MaxLongitude,
            bounds.MaxLatitude,
            bounds.Center.Latitude,
            request.MetersPerPixel,
            request.NativeGeoTransform ?? [],
            request.NativeRasterWidth,
            request.NativeRasterHeight,
            request.ProjectionWkt ?? string.Empty,
            request.OutputSize);
        var json = JsonSerializer.Serialize(fingerprint);

        // ExtraFingerprint (e.g. an adjustment hash) is intentionally NOT a WarpFingerprint field:
        // adding a field would change the serialized JSON for every overlay, including terrain
        // overlays that must keep byte-identical sidecars. Append it only when present.
        return string.IsNullOrEmpty(request.ExtraFingerprint) ? json : json + "|" + request.ExtraFingerprint;
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

    /// <summary>
    /// Sanity-checks the request's WGS84 bounds and derived center latitude before the fetch
    /// commits to a zoom level. Mirrors <see cref="HasUsableGeoReference"/>'s geometry checks
    /// (min &lt; max, center within Web Mercator range) without the settings-specific
    /// <c>HasGeoReference</c> flag — a raw bbox request has no such flag. The terrain legacy
    /// overload already checks <see cref="HasUsableGeoReference"/> (flag included) itself before
    /// building a request; a future backdrop caller is expected to do the equivalent check against
    /// its own "is georeferenced" state before calling this generic overload.
    /// </summary>
    private static void ValidateRequestBounds(OverlayRequest request)
    {
        var bounds = request.Wgs84Bounds;
        var centerLatitude = bounds.Center.Latitude;
        var isUsable = bounds.MinLongitude < bounds.MaxLongitude &&
                       bounds.MinLatitude < bounds.MaxLatitude &&
                       centerLatitude >= -WebMercatorMaxLatitude &&
                       centerLatitude <= WebMercatorMaxLatitude;
        if (!isUsable)
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

/// <summary>
/// Describes a map tile overlay fetch for an arbitrary WGS84 bounding box. Both the legacy
/// terrain overlay adapter and the backdrop texture baker build one of these and call
/// <see cref="MapTileOverlayService.EnsureOverlayImageAsync(OverlayRequest)"/>.
/// </summary>
/// <param name="Wgs84Bounds">The area to cover, in WGS84 degrees.</param>
/// <param name="MetersPerPixel">Drives <c>ChooseZoom</c>; center latitude comes from <see cref="Wgs84Bounds"/>.Center.</param>
/// <param name="NativeGeoTransform">Native raster geotransform for pixel-accurate warping; null selects the bbox-only linear warp (spec §10).</param>
/// <param name="NativeRasterWidth">Native raster width in pixels, paired with <see cref="NativeGeoTransform"/>.</param>
/// <param name="NativeRasterHeight">Native raster height in pixels, paired with <see cref="NativeGeoTransform"/>.</param>
/// <param name="ProjectionWkt">Native raster projection WKT; required alongside <see cref="NativeGeoTransform"/> for the native warp path.</param>
/// <param name="OutputSize">Square output size in pixels (power of two).</param>
/// <param name="OutputPath">Full path of the final PNG.</param>
/// <param name="TileCacheRoot">Raw tile cache root, e.g. "{level}\MT_Tiles\cache" — shared with the terrain overlay.</param>
/// <param name="ProviderName">Map tile provider name (see <see cref="MapTileOverlayService.Providers"/>).</param>
/// <param name="ImageryDate">Requested imagery date for providers that support historical dates.</param>
/// <param name="ExtraFingerprint">
/// Extra cache-invalidation input (e.g. an adjustment hash) appended to the warp fingerprint so a
/// change forces a rebuild from the tile cache. Not a <c>WarpFingerprint</c> record field — adding
/// a field there would change the serialized JSON for every overlay, including terrain overlays
/// that must keep byte-identical sidecars.
/// </param>
public sealed record OverlayRequest(
    GeoBoundingBox Wgs84Bounds,
    double MetersPerPixel,
    double[]? NativeGeoTransform,
    int NativeRasterWidth,
    int NativeRasterHeight,
    string? ProjectionWkt,
    int OutputSize,
    string OutputPath,
    string TileCacheRoot,
    string ProviderName,
    string? ImageryDate,
    string? ExtraFingerprint = null);

public sealed record MapTileProvider(
    string Name,
    string Slug,
    string UrlTemplate,
    bool SupportsHistoricalDate = false,
    int MaxParallelDownloads = 8)
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
