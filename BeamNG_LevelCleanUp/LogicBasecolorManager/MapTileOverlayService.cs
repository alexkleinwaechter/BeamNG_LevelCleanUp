using System.Globalization;
using System.Net.Http;
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
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static IReadOnlyList<MapTileProvider> Providers { get; } =
    [
        new("OSM", "osm", "https://tile.openstreetmap.org/{z}/{x}/{y}.png"),
        new("Google Roadmap", "google-roadmap", "https://mt0.google.com/vt/lyrs=m&hl=en&x={x}&y={y}&z={z}"),
        new("Google Terrain", "google-terrain", "https://mt0.google.com/vt/lyrs=p&hl=en&x={x}&y={y}&z={z}"),
        new("Google Satelite Only", "google-satelite-only", "https://mt0.google.com/vt/lyrs=s&hl=en&x={x}&y={y}&z={z}"),
        new("Google Hybrid", "google-hybrid", "https://mt0.google.com/vt/lyrs=y&hl=en&x={x}&y={y}&z={z}"),
        new("ArcGIS Satelite", "arcgis-satelite", "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}")
    ];

    public bool HasOverlayCache(string levelPath, string providerName)
    {
        var provider = ResolveProvider(providerName);
        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        var finalPath = Path.Join(tileRoot, provider.FinalImageName);
        var cachePath = Path.Join(tileRoot, "cache", provider.Slug);
        return File.Exists(finalPath) || Directory.Exists(cachePath);
    }

    public bool HasFinalOverlayImage(string levelPath, string providerName)
    {
        var provider = ResolveProvider(providerName);
        return File.Exists(Path.Join(levelPath, "MT_Tiles", provider.FinalImageName));
    }

    public MapTileCacheClearResult ClearOverlayCache(string levelPath, string providerName)
    {
        var provider = ResolveProvider(providerName);
        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        var finalPath = Path.Join(tileRoot, provider.FinalImageName);
        var cachePath = Path.Join(tileRoot, "cache", provider.Slug);
        var deletedItems = 0;

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
            deletedItems++;
        }

        if (Directory.Exists(cachePath))
        {
            Directory.Delete(cachePath, true);
            deletedItems++;
        }

        return new MapTileCacheClearResult(provider.Name, deletedItems);
    }

    public async Task<string> EnsureOverlayImageAsync(string levelPath, MtGeoReferenceSettings geoReferenceSettings, string providerName, int outputSize)
    {
        var provider = ResolveProvider(providerName);

        var tileRoot = Path.Join(levelPath, "MT_Tiles");
        Directory.CreateDirectory(tileRoot);

        var finalPath = Path.Join(tileRoot, provider.FinalImageName);
        if (File.Exists(finalPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Using cached map tile overlay {provider.FinalImageName}.");
            return finalPath;
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
        using var mosaic = new Image<Rgba32>((maxTileX - minTileX + 1) * TileSize, (maxTileY - minTileY + 1) * TileSize);
        for (var y = minTileY; y <= maxTileY; y++)
        for (var x = minTileX; x <= maxTileX; x++)
        {
            using var tile = await LoadTileAsync(tileRoot, provider, zoom, x, y);
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

        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Saved map tile overlay to {Path.GetFileName(finalPath)}.");
        return finalPath;
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

    private static async Task<LoadedTile> LoadTileAsync(string tileRoot, MapTileProvider provider, int z, int x, int y)
    {
        var cacheFolder = Path.Join(tileRoot, "cache", provider.Slug, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture));
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
            .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));
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

public sealed record MapTileProvider(string Name, string Slug, string UrlTemplate)
{
    public string FinalImageName => Slug + "-terrain-warp-v2.png";
}

public sealed record MapTileCacheClearResult(string ProviderName, int DeletedItems)
{
    public string Message => DeletedItems == 0
        ? $"No cached {ProviderName} tile overlay files were found."
        : $"Cleared cached {ProviderName} tile overlay files.";
}
