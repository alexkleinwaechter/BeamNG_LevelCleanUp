using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.Export;
using Grille.BeamNG.IO.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeamNG_LevelCleanUp.LogicBasecolorManager;

public class TerrainPbrMapBuilder
{
    private const byte HoleMaterialIndex = 255;
    private const int PreviewMaxSize = 512;
    public const int LargePreviewMaxSize = 1024;

    public TerrainPbrMapResult BuildMaps(
        TerrainV9Binary terrain,
        IReadOnlyCollection<CopyAsset> materials,
        string terrainFolder,
        bool generateHeight,
        double normalStrength,
        int aoRadius,
        double aoIntensity,
        BasecolorOverlayOptions? overlayOptions = null,
        MaterialBorderBlendOptions? borderBlendOptions = null,
        string? backdropTexturePath = null)
    {
        Directory.CreateDirectory(terrainFolder);

        var size = checked((int)terrain.Size);
        var materialLookup = CreateMaterialLookup(materials);
        var baseColorPath = Path.Join(terrainFolder, "MT_basecolor.png");
        var normalPath = Path.Join(terrainFolder, "MT_basecolor_nm.png");
        var aoPath = Path.Join(terrainFolder, "MT_basecolor_ao.png");
        var roughnessPath = Path.Join(terrainFolder, "MT_basecolor_r.png");
        var heightPath = Path.Join(terrainFolder, "MT_basecolor_h.png");

        WriteMergedBaseColor(
            terrain,
            materialLookup,
            baseColorPath,
            size,
            overlayOptions,
            borderBlendOptions,
            backdropTexturePath);
        WriteNormal(terrain, normalPath, size, normalStrength);
        WriteAo(terrain, aoPath, size, Math.Max(1, aoRadius), aoIntensity);
        WriteRoughness(terrain, materialLookup, roughnessPath, size, overlayOptions, borderBlendOptions);
        WriteHeight(terrain, heightPath, size, generateHeight);

        return new TerrainPbrMapResult(baseColorPath, normalPath, aoPath, roughnessPath, heightPath);
    }

    public string BuildPreviewDataUri(
        TerrainV9Binary terrain,
        IReadOnlyCollection<CopyAsset> materials,
        BasecolorOverlayOptions? overlayOptions = null,
        MaterialBorderBlendOptions? borderBlendOptions = null,
        int? maxSize = PreviewMaxSize)
    {
        var size = checked((int)terrain.Size);
        var previewSize = maxSize.HasValue ? Math.Min(size, Math.Max(1, maxSize.Value)) : size;
        var step = Math.Max(1, size / previewSize);
        var materialLookup = CreateMaterialLookup(materials);
        using var overlay = LoadOverlayImage(overlayOptions, previewSize);
        using var maskSet = LoadMaskSet(overlayOptions, previewSize);

        using var image = new Image<Rgba32>(previewSize, previewSize);
        for (var y = 0; y < previewSize; y++)
        {
            var sourceY = Math.Min(size - 1, y * step);
            for (var x = 0; x < previewSize; x++)
            {
                var sourceX = Math.Min(size - 1, x * step);
                var index = ToBeamNgTextureIndex(size, sourceX, sourceY);
                image[x, y] = GetBaseMaterialColor(terrain, materialLookup, index);
            }
        }

        for (var y = 0; y < previewSize; y++)
        for (var x = 0; x < previewSize; x++)
        {
            var sourceX = Math.Min(size - 1, x * step);
            var sourceY = Math.Min(size - 1, y * step);
            var index = ToBeamNgTextureIndex(size, sourceX, sourceY);
            image[x, y] = ApplyBaseColorOverlays(terrain, materialLookup, index, image[x, y], overlay, maskSet, x, y, overlayOptions);
        }

        ApplyMaterialBorderBlend(image, step, borderBlendOptions);

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}";
    }

    public string BuildLargePreviewDataUri(TerrainV9Binary terrain, IReadOnlyCollection<CopyAsset> materials, BasecolorOverlayOptions? overlayOptions = null, MaterialBorderBlendOptions? borderBlendOptions = null)
    {
        return BuildPreviewDataUri(terrain, materials, overlayOptions, borderBlendOptions, LargePreviewMaxSize);
    }

    private static Dictionary<string, CopyAsset> CreateMaterialLookup(IReadOnlyCollection<CopyAsset> materials)
    {
        var lookup = new Dictionary<string, CopyAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in materials)
        {
            if (!string.IsNullOrWhiteSpace(material.TerrainMaterialInternalName))
                lookup[material.TerrainMaterialInternalName] = material;
            if (!string.IsNullOrWhiteSpace(material.TerrainMaterialName))
                lookup.TryAdd(material.TerrainMaterialName, material);
            if (!string.IsNullOrWhiteSpace(material.Name))
                lookup.TryAdd(material.Name, material);
        }

        return lookup;
    }

    private static void WriteMergedBaseColor(
        TerrainV9Binary terrain,
        Dictionary<string, CopyAsset> materials,
        string path,
        int size,
        BasecolorOverlayOptions? overlayOptions,
        MaterialBorderBlendOptions? borderBlendOptions,
        string? backdropTexturePath)
    {
        using var overlay = LoadOverlayImage(overlayOptions, size);
        using var maskSet = LoadMaskSet(overlayOptions, size);
        using var image = new Image<Rgba32>(size, size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            image[x, y] = GetBaseMaterialColor(terrain, materials, ToBeamNgTextureIndex(size, x, y));

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var index = ToBeamNgTextureIndex(size, x, y);
            image[x, y] = ApplyBaseColorOverlays(terrain, materials, index, image[x, y], overlay, maskSet, x, y, overlayOptions);
        }

        ApplyMaterialBorderBlend(image, 1, borderBlendOptions);

        OverwritePng(image, path);
        if (!string.IsNullOrWhiteSpace(backdropTexturePath))
        {
            using var backdrop = image.Clone(context => context.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(
                    TerrainBackdropExporter.MaximumTextureSize,
                    TerrainBackdropExporter.MaximumTextureSize),
                Sampler = KnownResamplers.Lanczos3
            }));
            OverwritePng(backdrop, backdropTexturePath);
        }
    }

    private static void WriteRoughness(
        TerrainV9Binary terrain,
        Dictionary<string, CopyAsset> materials,
        string path,
        int size,
        BasecolorOverlayOptions? overlayOptions,
        MaterialBorderBlendOptions? borderBlendOptions)
    {
        using var maskSet = LoadMaskSet(overlayOptions, size);
        using var image = new Image<L8>(size, size);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var index = ToBeamNgTextureIndex(size, x, y);
            if (terrain.MaterialData[index] == HoleMaterialIndex)
            {
                image[x, y] = new L8(0);
                continue;
            }

            var material = GetMaterial(terrain, materials, index);
            var roughness = material?.GetRoughnessValue() ?? 128;
            image[x, y] = new L8((byte)Math.Clamp(roughness, 0, 255));
        }

        if (maskSet != null)
        {
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var index = ToBeamNgTextureIndex(size, x, y);
                if (terrain.MaterialData[index] == HoleMaterialIndex)
                    continue;

                var roughness = maskSet.ApplyRoughness(image[x, y].PackedValue, x, y);
                image[x, y] = new L8((byte)Math.Clamp(Math.Round(roughness), 0, 255));
            }
        }

        ApplyRoughnessBorderBlend(image, 1, borderBlendOptions);

        OverwritePng(image, path);
    }

    private static void WriteNormal(TerrainV9Binary terrain, string path, int size, double normalStrength)
    {
        using var image = new Image<Rgba32>(size, size);
        var strength = Math.Max(0.0, normalStrength);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var left = GetRotatedHeight(terrain, size, x - 1, y);
            var right = GetRotatedHeight(terrain, size, x + 1, y);
            var up = GetRotatedHeight(terrain, size, x, y - 1);
            var down = GetRotatedHeight(terrain, size, x, y + 1);

            var dx = (right - left) / 65535.0 * 16.0 * strength;
            var dy = (down - up) / 65535.0 * 16.0 * strength;
            var nx = -dx;
            var ny = -dy;
            var nz = 1.0;
            var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length <= 0)
                length = 1;

            nx /= length;
            ny /= length;
            nz /= length;

            image[x, y] = new Rgba32(
                (byte)Math.Clamp((nx * 0.5 + 0.5) * 255.0, 0, 255),
                (byte)Math.Clamp((ny * 0.5 + 0.5) * 255.0, 0, 255),
                (byte)Math.Clamp((nz * 0.5 + 0.5) * 255.0, 0, 255),
                255);
        }

        OverwritePng(image, path);
    }

    private static void WriteAo(TerrainV9Binary terrain, string path, int size, int radius, double intensity)
    {
        using var image = new Image<L8>(size, size);
        var safeIntensity = Math.Max(0.0, intensity);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var center = GetRotatedHeight(terrain, size, x, y);
            long total = 0;
            var count = 0;

            for (var offsetY = -radius; offsetY <= radius; offsetY++)
            for (var offsetX = -radius; offsetX <= radius; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0)
                    continue;

                total += GetRotatedHeight(terrain, size, x + offsetX, y + offsetY);
                count++;
            }

            var average = count == 0 ? center : total / (double)count;
            var concavity = Math.Max(0.0, (average - center) / 65535.0 * 64.0 * safeIntensity);
            var value = (byte)Math.Clamp(255.0 * (1.0 - Math.Min(1.0, concavity)), 0, 255);
            image[x, y] = new L8(value);
        }

        OverwritePng(image, path);
    }

    private static void WriteHeight(TerrainV9Binary terrain, string path, int size, bool generateHeight)
    {
        using var image = new Image<L8>(size, size);

        if (!generateHeight)
        {
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                image[x, y] = new L8(0);

            OverwritePng(image, path);
            return;
        }

        var min = terrain.HeightData.Min();
        var max = terrain.HeightData.Max();
        var range = Math.Max(1, max - min);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var value = terrain.HeightData[ToBeamNgTextureIndex(size, x, y)];
            image[x, y] = new L8((byte)Math.Clamp((value - min) / (double)range * 255.0, 0, 255));
        }

        OverwritePng(image, path);
    }

    private static ushort GetHeight(TerrainV9Binary terrain, int size, int x, int y)
    {
        var clampedX = Math.Clamp(x, 0, size - 1);
        var clampedY = Math.Clamp(y, 0, size - 1);
        return terrain.HeightData[clampedY * size + clampedX];
    }

    private static ushort GetRotatedHeight(TerrainV9Binary terrain, int size, int x, int y)
    {
        return terrain.HeightData[ToBeamNgTextureIndex(size, x, y)];
    }

    private static int ToBeamNgTextureIndex(int size, int x, int y)
    {
        var clampedX = Math.Clamp(x, 0, size - 1);
        var clampedY = Math.Clamp(y, 0, size - 1);
        var terrainX = clampedX;
        var terrainY = size - 1 - clampedY;
        return terrainY * size + terrainX;
    }

    private static Rgba32 GetBaseMaterialColor(TerrainV9Binary terrain, Dictionary<string, CopyAsset> materials, int index)
    {
        if (terrain.MaterialData[index] == HoleMaterialIndex)
            return new Rgba32(0, 0, 0, 0);

        var material = GetMaterial(terrain, materials, index);
        return ParseColor(material?.BaseColorHex ?? "#808080");
    }

    private static Rgba32 ApplyBaseColorOverlays(
        TerrainV9Binary terrain,
        Dictionary<string, CopyAsset> materials,
        int index,
        Rgba32 materialColor,
        Image<Rgba32>? overlay,
        MaskExceptionSet? maskSet,
        int x,
        int y,
        BasecolorOverlayOptions? overlayOptions)
    {
        if (terrain.MaterialData[index] == HoleMaterialIndex)
            return new Rgba32(0, 0, 0, 0);

        var material = GetMaterial(terrain, materials, index);
        materialColor = maskSet?.ApplyBaseColor(materialColor, x, y) ?? materialColor;
        if (overlay == null || overlayOptions == null)
            return materialColor;

        var materialBlend = Math.Clamp(material?.BaseColorOverlayBlend ?? 1.0, 0.0, 1.0);
        var overlayPixel = overlay[Math.Clamp(x, 0, overlay.Width - 1), Math.Clamp(y, 0, overlay.Height - 1)];
        var exceptionMultiplier = maskSet?.GetMultiplier(x, y) ?? 1.0;
        var blend = Math.Clamp(materialBlend * exceptionMultiplier * (overlayPixel.A / 255.0), 0.0, 1.0);
        if (blend <= 0)
            return materialColor;

        return new Rgba32(
            (byte)Math.Clamp(materialColor.R + (overlayPixel.R - materialColor.R) * blend, 0, 255),
            (byte)Math.Clamp(materialColor.G + (overlayPixel.G - materialColor.G) * blend, 0, 255),
            (byte)Math.Clamp(materialColor.B + (overlayPixel.B - materialColor.B) * blend, 0, 255),
            255);
    }

    private static Image<Rgba32>? LoadOverlayImage(BasecolorOverlayOptions? overlayOptions, int size)
    {
        if (overlayOptions == null || string.IsNullOrWhiteSpace(overlayOptions.ImagePath) || !File.Exists(overlayOptions.ImagePath))
            return null;

        var image = SixLabors.ImageSharp.Image.Load<Rgba32>(overlayOptions.ImagePath);
        if (image.Width != size || image.Height != size)
            image.Mutate(ctx => ctx.Resize(size, size));

        ApplyOverlayAdjustments(image, overlayOptions);
        return image;
    }

    private static void ApplyOverlayAdjustments(Image<Rgba32> image, BasecolorOverlayOptions overlayOptions)
    {
        var brightness = Math.Clamp(overlayOptions.Brightness, -1.0, 1.0);
        var contrastFactor = Math.Max(0.0, 1.0 + Math.Clamp(overlayOptions.Contrast, -1.0, 1.0));
        var saturationFactor = Math.Max(0.0, 1.0 + Math.Clamp(overlayOptions.Saturation, -1.0, 1.0));

        if (Math.Abs(brightness) < 0.0001 &&
            Math.Abs(contrastFactor - 1.0) < 0.0001 &&
            Math.Abs(saturationFactor - 1.0) < 0.0001)
            return;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    var red = pixel.R / 255.0;
                    var green = pixel.G / 255.0;
                    var blue = pixel.B / 255.0;

                    red = (red - 0.5) * contrastFactor + 0.5 + brightness;
                    green = (green - 0.5) * contrastFactor + 0.5 + brightness;
                    blue = (blue - 0.5) * contrastFactor + 0.5 + brightness;

                    var luma = red * 0.299 + green * 0.587 + blue * 0.114;
                    red = luma + (red - luma) * saturationFactor;
                    green = luma + (green - luma) * saturationFactor;
                    blue = luma + (blue - luma) * saturationFactor;

                    row[x] = new Rgba32(
                        (byte)Math.Clamp(red * 255.0, 0, 255),
                        (byte)Math.Clamp(green * 255.0, 0, 255),
                        (byte)Math.Clamp(blue * 255.0, 0, 255),
                        pixel.A);
                }
            }
        });
    }

    private static void ApplyMaterialBorderBlend(
        Image<Rgba32> image,
        int step,
        MaterialBorderBlendOptions? options)
    {
        if (!TryGetBorderBlendRadius(options, step, out var radius))
            return;

        image.Mutate(ctx => ctx.GaussianBlur((float)radius));
    }

    private static void ApplyRoughnessBorderBlend(
        Image<L8> image,
        int step,
        MaterialBorderBlendOptions? options)
    {
        if (!TryGetBorderBlendRadius(options, step, out var radius))
            return;

        image.Mutate(ctx => ctx.GaussianBlur((float)radius));
    }

    private static bool TryGetBorderBlendRadius(MaterialBorderBlendOptions? options, int step, out double radius)
    {
        radius = 0;

        if (options is not { Enabled: true } || options.Radius <= 0)
            return false;

        radius = Math.Clamp(options.Radius / Math.Max(1, step), 0.0, 5.0);
        return radius > 0;
    }

    private static MaskExceptionSet? LoadMaskSet(BasecolorOverlayOptions? overlayOptions, int size)
    {
        if (overlayOptions?.MaskExceptions == null || overlayOptions.MaskExceptions.Count == 0)
            return null;

        var masks = new List<LoadedMaskException>();
        foreach (var exception in overlayOptions.MaskExceptions)
        {
            if (string.IsNullOrWhiteSpace(exception.ImagePath))
                continue;

            if (!File.Exists(exception.ImagePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, $"OSM layer blend exception mask not found and will be ignored: {exception.Name}");
                continue;
            }

            try
            {
                var image = SixLabors.ImageSharp.Image.Load<Rgba32>(exception.ImagePath);
                if (image.Width != size || image.Height != size)
                {
                    image.Mutate(ctx => ctx.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(size, size),
                        Sampler = KnownResamplers.NearestNeighbor
                    }));
                }

                masks.Add(new LoadedMaskException(
                    image,
                    Math.Clamp(exception.AffectedBlendMultiplier, 0.0, 1.0),
                    exception.OverrideBaseColor,
                    ParseColor(exception.BaseColorHex),
                    Math.Clamp(exception.BaseColorStrength, 0.0, 1.0),
                    exception.OverrideRoughness,
                    Math.Clamp(exception.RoughnessValue, 0, 255),
                    Math.Clamp(exception.RoughnessStrength, 0.0, 1.0)));
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, $"Could not load OSM layer blend exception mask {exception.Name}: {ex.Message}");
            }
        }

        return masks.Count == 0 ? null : new MaskExceptionSet(masks);
    }

    private static CopyAsset? GetMaterial(TerrainV9Binary terrain, Dictionary<string, CopyAsset> materials, int index)
    {
        var materialIndex = terrain.MaterialData[index];
        if (materialIndex == HoleMaterialIndex || materialIndex >= terrain.MaterialNames.Length)
            return null;

        var materialName = terrain.MaterialNames[materialIndex];
        return materials.GetValueOrDefault(materialName);
    }

    private static Rgba32 ParseColor(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
            hexColor = "#808080";
        if (!hexColor.StartsWith("#"))
            hexColor = "#" + hexColor;
        if (hexColor.Length != 7)
            hexColor = "#808080";

        try
        {
            var r = Convert.ToByte(hexColor.Substring(1, 2), 16);
            var g = Convert.ToByte(hexColor.Substring(3, 2), 16);
            var b = Convert.ToByte(hexColor.Substring(5, 2), 16);
            return new Rgba32(r, g, b, 255);
        }
        catch
        {
            return new Rgba32(128, 128, 128, 255);
        }
    }

    private static Rgba32 LerpColor(Rgba32 from, Rgba32 to, double amount)
    {
        var blend = Math.Clamp(amount, 0.0, 1.0);
        return new Rgba32(
            (byte)Math.Clamp(from.R + (to.R - from.R) * blend, 0, 255),
            (byte)Math.Clamp(from.G + (to.G - from.G) * blend, 0, 255),
            (byte)Math.Clamp(from.B + (to.B - from.B) * blend, 0, 255),
            from.A);
    }

    private static void OverwritePng<TPixel>(Image<TPixel> image, string path)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (File.Exists(path))
            File.Delete(path);
        image.SaveAsPng(path);
    }
}

public record TerrainPbrMapResult(
    string BaseColorPath,
    string NormalPath,
    string AoPath,
    string RoughnessPath,
    string HeightPath);

public sealed record BasecolorOverlayOptions(
    string ImagePath,
    double GlobalBlend,
    IReadOnlyList<BasecolorMaskBlendExceptionOptions> MaskExceptions,
    double Brightness,
    double Contrast,
    double Saturation);

public sealed record MaterialBorderBlendOptions(
    bool Enabled,
    double Radius);

public sealed record BasecolorMaskBlendExceptionOptions(
    string Name,
    string ImagePath,
    double AffectedBlendMultiplier,
    bool OverrideBaseColor,
    string BaseColorHex,
    double BaseColorStrength,
    bool OverrideRoughness,
    int RoughnessValue,
    double RoughnessStrength);

internal sealed class MaskExceptionSet : IDisposable
{
    private readonly IReadOnlyList<LoadedMaskException> _masks;

    public MaskExceptionSet(IReadOnlyList<LoadedMaskException> masks)
    {
        _masks = masks;
    }

    public double GetMultiplier(int x, int y)
    {
        var multiplier = 1.0;
        foreach (var mask in _masks)
        {
            var maskAmount = GetMaskAmount(mask, x, y);
            var rowMultiplier = 1.0 + (mask.AffectedBlendMultiplier - 1.0) * maskAmount;
            multiplier = Math.Min(multiplier, rowMultiplier);
        }

        return Math.Clamp(multiplier, 0.0, 1.0);
    }

    public Rgba32 ApplyBaseColor(Rgba32 baseColor, int x, int y)
    {
        var color = baseColor;
        foreach (var mask in _masks.Where(mask => mask.OverrideBaseColor))
        {
            var amount = GetMaskAmount(mask, x, y) * mask.BaseColorStrength;
            if (amount <= 0)
                continue;

            color = LerpColor(color, mask.BaseColor, amount);
        }

        return color;
    }

    public double ApplyRoughness(double roughness, int x, int y)
    {
        var value = roughness;
        foreach (var mask in _masks.Where(mask => mask.OverrideRoughness))
        {
            var amount = GetMaskAmount(mask, x, y) * mask.RoughnessStrength;
            if (amount <= 0)
                continue;

            value += (mask.RoughnessValue - value) * amount;
        }

        return Math.Clamp(value, 0.0, 255.0);
    }

    public void Dispose()
    {
        foreach (var mask in _masks)
            mask.Image.Dispose();
    }

    private static double GetLuminance(Rgba32 pixel)
    {
        return pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114;
    }

    private static double GetMaskAmount(LoadedMaskException mask, int x, int y)
    {
        var pixel = mask.Image[Math.Clamp(x, 0, mask.Image.Width - 1), Math.Clamp(y, 0, mask.Image.Height - 1)];
        return Math.Clamp(GetLuminance(pixel) / 255.0, 0.0, 1.0);
    }

    private static Rgba32 LerpColor(Rgba32 from, Rgba32 to, double amount)
    {
        var blend = Math.Clamp(amount, 0.0, 1.0);
        return new Rgba32(
            (byte)Math.Clamp(from.R + (to.R - from.R) * blend, 0, 255),
            (byte)Math.Clamp(from.G + (to.G - from.G) * blend, 0, 255),
            (byte)Math.Clamp(from.B + (to.B - from.B) * blend, 0, 255),
            from.A);
    }
}

internal sealed record LoadedMaskException(
    Image<Rgba32> Image,
    double AffectedBlendMultiplier,
    bool OverrideBaseColor,
    Rgba32 BaseColor,
    double BaseColorStrength,
    bool OverrideRoughness,
    int RoughnessValue,
    double RoughnessStrength);
