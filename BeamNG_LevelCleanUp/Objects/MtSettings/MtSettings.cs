using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Objects.MtSettings;

public enum BasecolorMode
{
    None = 0,
    PaintMode = 1,
    BaseColorMode = 2
}

public class MtSettings
{
    private const string SettingsFileName = "MT_settings.json";

    [JsonPropertyName("CurrentMode")]
    public BasecolorMode CurrentMode { get; set; } = BasecolorMode.None;

    [JsonPropertyName("PaintModeSettings")]
    public MtPaintModeSettings PaintModeSettings { get; set; } = new();

    [JsonPropertyName("BasecolorModeSettings")]
    public MtBasecolorModeSettings BasecolorModeSettings { get; set; } = new();

    [JsonPropertyName("GeoReferenceSettings")]
    public MtGeoReferenceSettings GeoReferenceSettings { get; set; } = new();

    public static MtSettings? Load(string levelRoot)
    {
        try
        {
            var settingsPath = GetSettingsPath(levelRoot);
            if (!File.Exists(settingsPath))
                return null;

            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<MtSettings>(json, CreateSerializerOptions());
        }
        catch
        {
            return null;
        }
    }

    public void Save(string levelRoot)
    {
        var settingsPath = GetSettingsPath(levelRoot);
        var json = JsonSerializer.Serialize(this, CreateSerializerOptions());
        File.WriteAllText(settingsPath, json);
    }

    public static string GetSettingsPath(string levelRoot) => Path.Join(levelRoot, SettingsFileName);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = BeamJsonOptions.GetJsonSerializerOptions();
        options.PropertyNamingPolicy = null;
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public class MtGeoReferenceSettings
{
    [JsonPropertyName("HasGeoReference")]
    public bool HasGeoReference { get; set; }

    [JsonPropertyName("HeightmapSourceType")]
    public string HeightmapSourceType { get; set; } = string.Empty;

    [JsonPropertyName("ProjectionName")]
    public string ProjectionName { get; set; } = string.Empty;

    [JsonPropertyName("ProjectionWkt")]
    public string ProjectionWkt { get; set; } = string.Empty;

    [JsonPropertyName("SourceElevationPath")]
    public string SourceElevationPath { get; set; } = string.Empty;

    [JsonPropertyName("SourceElevationPaths")]
    public List<string> SourceElevationPaths { get; set; } = new();

    [JsonPropertyName("TerrainMinLongitude")]
    public double TerrainMinLongitude { get; set; }

    [JsonPropertyName("TerrainMinLatitude")]
    public double TerrainMinLatitude { get; set; }

    [JsonPropertyName("TerrainMaxLongitude")]
    public double TerrainMaxLongitude { get; set; }

    [JsonPropertyName("TerrainMaxLatitude")]
    public double TerrainMaxLatitude { get; set; }

    [JsonPropertyName("TerrainCenterLongitude")]
    public double TerrainCenterLongitude { get; set; }

    [JsonPropertyName("TerrainCenterLatitude")]
    public double TerrainCenterLatitude { get; set; }

    [JsonPropertyName("SourceNativeMinX")]
    public double SourceNativeMinX { get; set; }

    [JsonPropertyName("SourceNativeMinY")]
    public double SourceNativeMinY { get; set; }

    [JsonPropertyName("SourceNativeMaxX")]
    public double SourceNativeMaxX { get; set; }

    [JsonPropertyName("SourceNativeMaxY")]
    public double SourceNativeMaxY { get; set; }

    [JsonPropertyName("SourceGeoTransform")]
    public double[] SourceGeoTransform { get; set; } = [];

    [JsonPropertyName("TerrainMetersPerPixel")]
    public double TerrainMetersPerPixel { get; set; }

    [JsonPropertyName("TerrainSize")]
    public int TerrainSize { get; set; }

    [JsonPropertyName("SavedAtUtc")]
    public DateTime SavedAtUtc { get; set; }
}

public class MtPaintModeSettings
{
    [JsonPropertyName("Materials")]
    public List<MtTerrainMaterialSetting> Materials { get; set; } = new();
}

public class MtBasecolorModeSettings
{
    [JsonPropertyName("Materials")]
    public List<MtTerrainMaterialSetting> Materials { get; set; } = new();

    [JsonPropertyName("MergedTextureSize")]
    public int MergedTextureSize { get; set; }

    [JsonPropertyName("GenerateHeight")]
    public bool GenerateHeight { get; set; }

    [JsonPropertyName("NormalStrength")]
    public double NormalStrength { get; set; } = 1.0;

    [JsonPropertyName("AoRadius")]
    public int AoRadius { get; set; } = 2;

    [JsonPropertyName("AoIntensity")]
    public double AoIntensity { get; set; } = 1.0;

    [JsonPropertyName("EnableMaterialBorderBlend")]
    public bool EnableMaterialBorderBlend { get; set; }

    [JsonPropertyName("MaterialBorderBlendRadius")]
    public double MaterialBorderBlendRadius { get; set; } = 2.5;

    [JsonPropertyName("OverlaySettings")]
    public MtBasecolorOverlaySettings OverlaySettings { get; set; } = new();

    [JsonPropertyName("OsmLayerBlendExceptions")]
    public List<MtOsmLayerBlendException> OsmLayerBlendExceptions { get; set; } = new();
}

public class MtOsmLayerBlendException
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ImagePath")]
    public string ImagePath { get; set; } = string.Empty;

    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("AffectedBlendMultiplier")]
    public double AffectedBlendMultiplier { get; set; }

    [JsonPropertyName("OverrideBaseColor")]
    public bool OverrideBaseColor { get; set; }

    [JsonPropertyName("BaseColorHex")]
    public string BaseColorHex { get; set; } = "#808080";

    [JsonPropertyName("BaseColorStrength")]
    public double BaseColorStrength { get; set; } = 1.0;

    [JsonPropertyName("OverrideRoughness")]
    public bool OverrideRoughness { get; set; }

    [JsonPropertyName("RoughnessValue")]
    public int RoughnessValue { get; set; } = 128;

    [JsonPropertyName("RoughnessStrength")]
    public double RoughnessStrength { get; set; } = 1.0;
}

public class MtBasecolorOverlaySettings
{
    [JsonPropertyName("SelectedImagePath")]
    public string SelectedImagePath { get; set; } = string.Empty;

    [JsonPropertyName("SelectedTileProvider")]
    public string SelectedTileProvider { get; set; } = string.Empty;

    [JsonPropertyName("CachedTileImagePath")]
    public string CachedTileImagePath { get; set; } = string.Empty;

    [JsonPropertyName("UseTileProvider")]
    public bool UseTileProvider { get; set; }

    [JsonPropertyName("GlobalBlend")]
    public double GlobalBlend { get; set; }

    [JsonPropertyName("Brightness")]
    public double Brightness { get; set; }

    [JsonPropertyName("Contrast")]
    public double Contrast { get; set; }

    [JsonPropertyName("Saturation")]
    public double Saturation { get; set; }
}

public class MtTerrainMaterialSetting
{
    [JsonPropertyName("InternalName")]
    public string InternalName { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("BaseColorHex")]
    public string BaseColorHex { get; set; } = "#808080";

    [JsonPropertyName("RoughnessPreset")]
    public TerrainRoughnessPreset RoughnessPreset { get; set; } = TerrainRoughnessPreset.DirtRoad;

    [JsonPropertyName("RoughnessValue")]
    public int RoughnessValue { get; set; } = 128;

    [JsonPropertyName("CalculatedRoughnessValue")]
    public int CalculatedRoughnessValue { get; set; } = -1;

    [JsonPropertyName("BaseColorOverlayBlend")]
    public double BaseColorOverlayBlend { get; set; }

    public static MtTerrainMaterialSetting FromCopyAsset(CopyAsset asset)
    {
        return new MtTerrainMaterialSetting
        {
            InternalName = asset.TerrainMaterialInternalName ?? string.Empty,
            Name = asset.TerrainMaterialName ?? asset.Name ?? string.Empty,
            BaseColorHex = string.IsNullOrWhiteSpace(asset.BaseColorHex) ? "#808080" : asset.BaseColorHex,
            RoughnessPreset = asset.RoughnessPreset,
            RoughnessValue = asset.RoughnessValue,
            CalculatedRoughnessValue = asset.CalculatedRoughnessValue,
            BaseColorOverlayBlend = Math.Clamp(asset.BaseColorOverlayBlend, 0.0, 1.0)
        };
    }

    public CopyAsset ToCopyAsset()
    {
        return new CopyAsset
        {
            CopyAssetType = CopyAssetType.Terrain,
            Name = string.IsNullOrWhiteSpace(Name) ? InternalName : Name,
            TerrainMaterialName = string.IsNullOrWhiteSpace(Name) ? InternalName : Name,
            TerrainMaterialInternalName = InternalName,
            BaseColorHex = string.IsNullOrWhiteSpace(BaseColorHex) ? "#808080" : BaseColorHex,
            RoughnessPreset = RoughnessPreset,
            RoughnessValue = RoughnessValue,
            CalculatedRoughnessValue = CalculatedRoughnessValue,
            BaseColorOverlayBlend = Math.Clamp(BaseColorOverlayBlend, 0.0, 1.0)
        };
    }
}
