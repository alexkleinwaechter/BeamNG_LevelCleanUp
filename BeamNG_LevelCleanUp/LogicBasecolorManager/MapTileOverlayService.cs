using System.Globalization;
using System.Net.Http;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
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

    public async Task<string> EnsureOverlayImageAsync(string levelPath, MtGeoReferenceSettings geoReferenceSettings, string providerName, int outputSize)
    {
        var provider = Providers.FirstOrDefault(x => x.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase))
                       ?? Providers.First(x => x.Name.Equals("Google Satelite Only", StringComparison.OrdinalIgnoreCase));

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

        using var mosaic = new Image<Rgba32>((maxTileX - minTileX + 1) * TileSize, (maxTileY - minTileY + 1) * TileSize);
        for (var y = minTileY; y <= maxTileY; y++)
        for (var x = minTileX; x <= maxTileX; x++)
        {
            using var tile = await LoadTileAsync(tileRoot, provider, zoom, x, y);
            var destX = (x - minTileX) * TileSize;
            var destY = (y - minTileY) * TileSize;
            mosaic.Mutate(ctx => ctx.DrawImage(tile, new SixLabors.ImageSharp.Point(destX, destY), 1f));
        }

        var west = LonLatToWorldPixel(geoReferenceSettings.TerrainMinLongitude, geoReferenceSettings.TerrainMaxLatitude, zoom);
        var east = LonLatToWorldPixel(geoReferenceSettings.TerrainMaxLongitude, geoReferenceSettings.TerrainMinLatitude, zoom);
        var mosaicOriginX = minTileX * TileSize;
        var mosaicOriginY = minTileY * TileSize;
        var cropLeft = Math.Clamp((int)Math.Floor(west.X - mosaicOriginX), 0, mosaic.Width - 1);
        var cropTop = Math.Clamp((int)Math.Floor(west.Y - mosaicOriginY), 0, mosaic.Height - 1);
        var cropRight = Math.Clamp((int)Math.Ceiling(east.X - mosaicOriginX), cropLeft + 1, mosaic.Width);
        var cropBottom = Math.Clamp((int)Math.Ceiling(east.Y - mosaicOriginY), cropTop + 1, mosaic.Height);
        var crop = new SixLabors.ImageSharp.Rectangle(cropLeft, cropTop, cropRight - cropLeft, cropBottom - cropTop);

        using var output = mosaic.Clone(ctx => ctx.Crop(crop).Resize(outputSize, outputSize));
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

    private static async Task<Image<Rgba32>> LoadTileAsync(string tileRoot, MapTileProvider provider, int z, int x, int y)
    {
        var cacheFolder = Path.Join(tileRoot, "cache", provider.Slug, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(cacheFolder);
        var cachePath = Path.Join(cacheFolder, y.ToString(CultureInfo.InvariantCulture) + ".img");

        byte[] bytes;
        if (File.Exists(cachePath))
        {
            bytes = await File.ReadAllBytesAsync(cachePath);
        }
        else
        {
            var url = BuildTileUrl(provider.UrlTemplate, z, x, y);
            bytes = await HttpClient.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(cachePath, bytes);
        }

        var image = SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
        if (image.Width == TileSize && image.Height == TileSize)
            return image;

        image.Mutate(ctx => ctx.Resize(TileSize, TileSize));
        return image;
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
}

public sealed record MapTileProvider(string Name, string Slug, string UrlTemplate)
{
    public string FinalImageName => Slug + ".png";
}