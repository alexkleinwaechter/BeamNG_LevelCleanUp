using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using BeamNgTerrainPoc.Terrain.ColorExtraction;
using Grille.BeamNG.IO.Binary;

namespace BeamNG_LevelCleanUp.LogicBasecolorManager;

public class BasecolorManagerService
{
    private readonly TerrainPbrMapBuilder _mapBuilder = new();

    public BasecolorManagerLoadResult LoadLevel(string folder)
    {
        try
        {
            var validation = ValidateLevelFolder(folder);
            if (!validation.Success)
                return BasecolorManagerLoadResult.Fail(validation.ErrorMessage ?? "Selected folder is not a valid BeamNG level.");

            var levelPath = validation.LevelPath;
            var levelName = new BeamFileReader(levelPath, null).GetLevelName();
            var materialsJsonPath = TerrainTextureHelper.FindTerrainMaterialsJsonPath(levelPath);
            if (string.IsNullOrWhiteSpace(materialsJsonPath) || !File.Exists(materialsJsonPath))
                return BasecolorManagerLoadResult.Fail("No terrain materials JSON file was found in the selected level.");

            var terFilePath = FindTerrainTerFile(levelPath);
            if (string.IsNullOrWhiteSpace(terFilePath))
                return BasecolorManagerLoadResult.Fail("No .ter terrain file was found in the selected level.");

            var terrain = LayerMaskReader.ReadTerrainBinary(terFilePath);
            var terrainSize = checked((int)terrain.Size);
            var jsonTerrainSize = TerrainTextureHelper.GetTerrainSizeFromJson(levelPath);
            if (jsonTerrainSize.HasValue && jsonTerrainSize.Value != terrainSize)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Terrain .ter size ({terrainSize}) differs from terrain.json size ({jsonTerrainSize.Value}). BaseColor Mode will use the .ter size.");
            }

            var settings = MtSettings.Load(levelPath);
            var firstLoad = settings == null;
            if (settings == null)
            {
                var extractedMaterials = ScanAndExtractMaterials(levelPath, materialsJsonPath);
                settings = new MtSettings
                {
                    CurrentMode = BasecolorMode.None,
                    PaintModeSettings = new MtPaintModeSettings
                    {
                        Materials = extractedMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList()
                    },
                    BasecolorModeSettings = new MtBasecolorModeSettings
                    {
                        Materials = extractedMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList(),
                        MergedTextureSize = terrainSize,
                        GenerateHeight = false,
                        NormalStrength = 1.0,
                        AoRadius = 2,
                        AoIntensity = 1.0
                    }
                };
            }

            EnsureModeMaterialLists(settings, levelPath, materialsJsonPath, terrainSize);
            if (settings.CurrentMode == BasecolorMode.PaintMode && HasUsableMaterialSettings(settings.PaintModeSettings.Materials))
                settings.BasecolorModeSettings.Materials = settings.PaintModeSettings.Materials.Select(Clone).ToList();

            var paintMaterials = settings.PaintModeSettings.Materials.Select(x => x.ToCopyAsset()).ToList();
            var basecolorMaterials = settings.BasecolorModeSettings.Materials.Select(x => x.ToCopyAsset()).ToList();
            var previewDataUri = _mapBuilder.BuildPreviewDataUri(terrain, basecolorMaterials, CreateOverlayOptions(settings));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                firstLoad
                    ? $"Loaded {paintMaterials.Count} terrain materials from {levelName}; colors and roughness were extracted from existing terrain textures."
                    : $"Loaded Basecolor Manager settings for {levelName}.");

            return new BasecolorManagerLoadResult
            {
                Success = true,
                LevelPath = levelPath,
                LevelName = levelName,
                MaterialsJsonPath = materialsJsonPath,
                TerrainFilePath = terFilePath,
                Terrain = terrain,
                TerrainSize = terrainSize,
                Settings = settings,
                PaintMaterials = paintMaterials,
                BasecolorMaterials = basecolorMaterials,
                PreviewDataUri = previewDataUri
            };
        }
        catch (Exception ex)
        {
            return BasecolorManagerLoadResult.Fail(ex.InnerException != null ? $"{ex.Message} {ex.InnerException.Message}" : ex.Message);
        }
    }

    public void SaveSettings(string levelPath, MtSettings settings, IReadOnlyCollection<CopyAsset> paintMaterials, IReadOnlyCollection<CopyAsset> basecolorMaterials)
    {
        settings.PaintModeSettings.Materials = paintMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
        settings.BasecolorModeSettings.Materials = basecolorMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
        settings.Save(levelPath);
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Basecolor Manager settings saved.");
    }

    public string BuildPreview(TerrainV9Binary terrain, IReadOnlyCollection<CopyAsset> materials, MtSettings settings)
    {
        return _mapBuilder.BuildPreviewDataUri(terrain, materials, CreateOverlayOptions(settings));
    }

    public static BasecolorOverlayOptions? CreateOverlayOptions(MtSettings settings)
    {
        var overlaySettings = settings.BasecolorModeSettings.OverlaySettings;
        var imagePath = overlaySettings.UseTileProvider
            ? overlaySettings.CachedTileImagePath
            : overlaySettings.SelectedImagePath;

        var hasOverlayImage = !string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath);

        var maskExceptions = new List<BasecolorMaskBlendExceptionOptions>();
        foreach (var exception in (settings.BasecolorModeSettings.OsmLayerBlendExceptions ?? new List<MtOsmLayerBlendException>()).Where(x => x.Enabled))
        {
            if (string.IsNullOrWhiteSpace(exception.ImagePath))
                continue;

            if (!File.Exists(exception.ImagePath))
            {
                var name = string.IsNullOrWhiteSpace(exception.Name) ? exception.ImagePath : exception.Name;
                PubSubChannel.SendMessage(PubSubMessageType.Warning, $"OSM layer blend exception mask not found and will be ignored: {name}");
                continue;
            }

            maskExceptions.Add(new BasecolorMaskBlendExceptionOptions(
                string.IsNullOrWhiteSpace(exception.Name) ? Path.GetFileNameWithoutExtension(exception.ImagePath) : exception.Name,
                exception.ImagePath,
                Math.Clamp(exception.AffectedBlendMultiplier, 0.0, 1.0),
                exception.OverrideBaseColor,
                string.IsNullOrWhiteSpace(exception.BaseColorHex) ? "#808080" : exception.BaseColorHex,
                Math.Clamp(exception.BaseColorStrength, 0.0, 1.0),
                exception.OverrideRoughness,
                Math.Clamp(exception.RoughnessValue, 0, 255),
                Math.Clamp(exception.RoughnessStrength, 0.0, 1.0)));
        }

        return hasOverlayImage || maskExceptions.Count > 0
            ? new BasecolorOverlayOptions(hasOverlayImage ? imagePath : string.Empty, Math.Clamp(overlaySettings.GlobalBlend, 0.0, 1.0), maskExceptions)
            : null;
    }

    private static void EnsureModeMaterialLists(MtSettings settings, string levelPath, string materialsJsonPath, int terrainSize)
    {
        if (settings.PaintModeSettings == null)
            settings.PaintModeSettings = new MtPaintModeSettings();
        if (settings.BasecolorModeSettings == null)
            settings.BasecolorModeSettings = new MtBasecolorModeSettings();
        settings.GeoReferenceSettings ??= new MtGeoReferenceSettings();
        settings.BasecolorModeSettings.OverlaySettings ??= new MtBasecolorOverlaySettings();
        settings.BasecolorModeSettings.OsmLayerBlendExceptions ??= new List<MtOsmLayerBlendException>();

        if (!settings.PaintModeSettings.Materials.Any() && settings.BasecolorModeSettings.Materials.Any())
            settings.PaintModeSettings.Materials = settings.BasecolorModeSettings.Materials.Select(Clone).ToList();
        if (!settings.BasecolorModeSettings.Materials.Any() && settings.PaintModeSettings.Materials.Any())
            settings.BasecolorModeSettings.Materials = settings.PaintModeSettings.Materials.Select(Clone).ToList();
        if (!settings.PaintModeSettings.Materials.Any() && !settings.BasecolorModeSettings.Materials.Any())
        {
            var extractedMaterials = ScanAndExtractMaterials(levelPath, materialsJsonPath);
            settings.PaintModeSettings.Materials = extractedMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
            settings.BasecolorModeSettings.Materials = extractedMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
        }

        if (settings.BasecolorModeSettings.MergedTextureSize <= 0)
            settings.BasecolorModeSettings.MergedTextureSize = terrainSize;
        if (settings.BasecolorModeSettings.NormalStrength <= 0)
            settings.BasecolorModeSettings.NormalStrength = 1.0;
        if (settings.BasecolorModeSettings.AoRadius <= 0)
            settings.BasecolorModeSettings.AoRadius = 2;
        if (settings.BasecolorModeSettings.AoIntensity <= 0)
            settings.BasecolorModeSettings.AoIntensity = 1.0;
    }

    private static MtTerrainMaterialSetting Clone(MtTerrainMaterialSetting setting)
    {
        return new MtTerrainMaterialSetting
        {
            InternalName = setting.InternalName,
            Name = setting.Name,
            BaseColorHex = setting.BaseColorHex,
            RoughnessPreset = setting.RoughnessPreset,
            RoughnessValue = setting.RoughnessValue,
            CalculatedRoughnessValue = setting.CalculatedRoughnessValue,
            BaseColorOverlayBlend = Math.Clamp(setting.BaseColorOverlayBlend, 0.0, 1.0)
        };
    }

    public static bool HasUsableMaterialSettings(IEnumerable<MtTerrainMaterialSetting> materials)
    {
        return materials.Any(material =>
            !string.IsNullOrWhiteSpace(material.InternalName) &&
            !string.IsNullOrWhiteSpace(material.BaseColorHex) &&
            !material.BaseColorHex.Equals("#808080", StringComparison.OrdinalIgnoreCase));
    }

    private static List<CopyAsset> ScanAndExtractMaterials(string levelPath, string materialsJsonPath)
    {
        var materials = new List<MaterialJson>();
        var copyAssets = new List<CopyAsset>();
        var sourceLevelsPath = GetSourceLevelsPath(levelPath);
        var scanner = new TerrainCopyScanner(materialsJsonPath, sourceLevelsPath, levelPath, materials, copyAssets);
        scanner.ScanTerrainMaterials();

        TerrainCopyScanner.ExtractTerrainMaterialColors(levelPath, copyAssets);
        TerrainCopyScanner.ExtractTerrainMaterialRoughness(levelPath, copyAssets);

        foreach (var asset in copyAssets.Where(x => x.CopyAssetType == CopyAssetType.Terrain))
        {
            if (!asset.HasCalculatedRoughness)
            {
                asset.RoughnessPreset = CopyAsset.DetectRoughnessPresetFromName(asset.TerrainMaterialName ?? asset.Name);
                asset.RoughnessValue = asset.GetRoughnessValue();
            }
        }

        return copyAssets.Where(x => x.CopyAssetType == CopyAssetType.Terrain).ToList();
    }

    private static string GetSourceLevelsPath(string levelPath)
    {
        var parent = Directory.GetParent(levelPath);
        return parent != null && parent.Name.Equals("levels", StringComparison.OrdinalIgnoreCase)
            ? parent.FullName
            : levelPath;
    }

    private static LevelValidationResult ValidateLevelFolder(string folder)
    {
        var levelPath = ZipFileHandler.GetNamePath(folder);
        if (string.IsNullOrWhiteSpace(levelPath))
        {
            var infoJsonPath = Path.Join(folder, "info.json");
            if (File.Exists(infoJsonPath))
                levelPath = folder;
            else
                return new LevelValidationResult(false, string.Empty,
                    "Selected folder does not appear to be a valid BeamNG level. Please select a folder containing info.json.");
        }

        return new LevelValidationResult(true, levelPath, null);
    }

    private static string? FindTerrainTerFile(string levelPath)
    {
        var topLevelFiles = Directory.GetFiles(levelPath, "*.ter", SearchOption.TopDirectoryOnly);
        var theTerrain = topLevelFiles.FirstOrDefault(x => Path.GetFileName(x).Equals("theTerrain.ter", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(theTerrain))
            return theTerrain;
        if (topLevelFiles.Length > 0)
            return topLevelFiles[0];

        var allFiles = Directory.GetFiles(levelPath, "*.ter", SearchOption.AllDirectories);
        return allFiles.FirstOrDefault(x => Path.GetFileName(x).Equals("theTerrain.ter", StringComparison.OrdinalIgnoreCase))
               ?? allFiles.FirstOrDefault();
    }

    private record LevelValidationResult(bool Success, string LevelPath, string? ErrorMessage);
}

public class BasecolorManagerLoadResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string LevelPath { get; init; } = string.Empty;
    public string LevelName { get; init; } = string.Empty;
    public string MaterialsJsonPath { get; init; } = string.Empty;
    public string TerrainFilePath { get; init; } = string.Empty;
    public TerrainV9Binary Terrain { get; init; } = new();
    public int TerrainSize { get; init; }
    public MtSettings Settings { get; init; } = new();
    public List<CopyAsset> PaintMaterials { get; init; } = new();
    public List<CopyAsset> BasecolorMaterials { get; init; } = new();
    public string PreviewDataUri { get; init; } = string.Empty;

    public static BasecolorManagerLoadResult Fail(string errorMessage)
    {
        return new BasecolorManagerLoadResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}
