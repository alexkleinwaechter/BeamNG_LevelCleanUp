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
    private readonly PaintModeApplier _paintModeApplier = new();
    private readonly BaseColorModeApplier _baseColorModeApplier = new();

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
            var previewDataUri = _mapBuilder.BuildPreviewDataUri(terrain, basecolorMaterials, CreateOverlayOptions(settings), CreateMaterialBorderBlendOptions(settings));

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

    /// <summary>
    /// Non-UI pipeline behind the page's "Reset &amp; Rebake" action (spec §10): reloads the terrain
    /// from disk, restores the per-material Paint Mode textures on the fresh layers (so a failure
    /// later leaves the level in the known-good layered state), refreshes the tile overlay via the
    /// page-supplied callback (it needs page-computed provider/date properties), re-syncs the
    /// BaseColor materials from Paint Mode (or from the material lists), re-bakes the merged BaseColor
    /// PBR maps, and finally rebakes every backdrop chunk texture from the shared tile cache (no-op
    /// when the level has no backdrop). The caller keeps the busy-operation wrapper, UI-state
    /// assignment, preview rebuild, staleness update and snackbar.
    /// </summary>
    public async Task<ResetRebakeResult> ResetAndRebakeAsync(ResetRebakeInputs inputs, Func<Task>? refreshOverlayAsync = null)
    {
        TerrainV9Binary terrain = null!;
        var terrainSize = 0;
        // Tracks the "current" BaseColor material list through the pipeline. Starts as the caller's
        // list, but SyncBasecolorMaterialsFromPaintMode below returns a brand-new list rather than
        // mutating this one in place (see its doc comment) - reassigning this local is how that swap
        // propagates through the rest of the method.
        var basecolorMaterials = inputs.BasecolorMaterials;

        // Step 1+2: restore the per-material Paint Mode textures on freshly loaded terrain layers,
        // so a failure later in the pipeline leaves the level in the known-good layered state.
        await Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(inputs.TerrainFilePath) && File.Exists(inputs.TerrainFilePath))
            {
                terrain = LayerMaskReader.ReadTerrainBinary(inputs.TerrainFilePath);
            }
            else if (inputs.FallbackTerrain != null)
            {
                // Mirrors the page's old ReloadTerrainFromDisk(), which silently kept whatever terrain
                // was already loaded in memory when the .ter path was blank or the file had vanished,
                // instead of throwing.
                terrain = inputs.FallbackTerrain;
            }
            else
            {
                throw new InvalidOperationException(
                    "No terrain file was found on disk and no in-memory terrain is available to fall back to.");
            }

            terrainSize = checked((int)terrain.Size);
            // The page's own _terrainSize field isn't refreshed until this method returns (see
            // ResetRebakeResult), but refreshOverlayAsync below runs before that and needs the
            // just-reloaded size (the whole point of "reload from disk" is picking up a .ter that
            // may have changed size since the page was loaded) - bridge it via the settings object,
            // which both sides already share.
            inputs.Settings.BasecolorModeSettings.MergedTextureSize = terrainSize;
            UpdateSettingsFromMaterialLists(inputs.Settings, inputs.PaintMaterials, basecolorMaterials);
            _paintModeApplier.Apply(inputs.LevelPath, inputs.LevelName, inputs.MaterialsJsonPath, inputs.PaintMaterials, inputs.Settings);
        });

        // Step 3: refresh the tile overlay (page-supplied - needs page-computed provider/date properties).
        if (refreshOverlayAsync != null)
            await refreshOverlayAsync();

        // Step 4: bake the BaseColor maps from the fresh state.
        await Task.Run(() =>
        {
            if (PaintModeHasUsableMaterialSettings(inputs.PaintMaterials))
                basecolorMaterials = SyncBasecolorMaterialsFromPaintMode(inputs.PaintMaterials, basecolorMaterials, inputs.Settings);
            else
                UpdateSettingsFromMaterialLists(inputs.Settings, inputs.PaintMaterials, basecolorMaterials);

            if (HasActiveBasecolorOverlay(inputs.Settings))
            {
                EnsureDefaultOverlayBlend(inputs.Settings, basecolorMaterials);
                UpdateSettingsFromMaterialLists(inputs.Settings, inputs.PaintMaterials, basecolorMaterials);
            }

            _baseColorModeApplier.Apply(
                inputs.LevelPath,
                inputs.LevelName,
                inputs.MaterialsJsonPath,
                inputs.TerrainFilePath,
                terrain,
                basecolorMaterials,
                inputs.Settings,
                inputs.Settings.BasecolorModeSettings.GenerateHeight,
                inputs.Settings.BasecolorModeSettings.NormalStrength,
                inputs.Settings.BasecolorModeSettings.AoRadius,
                inputs.Settings.BasecolorModeSettings.AoIntensity,
                CreateOverlayOptions(inputs.Settings),
                CreateMaterialBorderBlendOptions(inputs.Settings));
        });

        // NEW step: rebake backdrop chunk textures from the shared tile cache, if a backdrop exists (spec §10).
        await RebakeBackdropTexturesAsync(inputs.LevelPath, inputs.Settings);

        return new ResetRebakeResult(terrain, terrainSize) { BasecolorMaterials = basecolorMaterials };
    }

    /// <summary>Rebakes every backdrop chunk texture from the shared tile cache (spec §10). Returns count, 0 when no backdrop.</summary>
    public async Task<int> RebakeBackdropTexturesAsync(string levelPath, MtSettings settings)
    {
        // Cross-page stale-settings hazard: this page's in-memory settings may have been loaded
        // before a backdrop was generated (or generated later from another page in the same
        // session), leaving BackdropSettings null here even though the level actually has one on
        // disk. Graft the disk copy's block onto the passed-in object before the gate below, so
        // the rebake actually runs and any later save from this object preserves it.
        if (settings.BackdropSettings == null)
        {
            var onDisk = MtSettings.Load(levelPath);
            if (onDisk?.BackdropSettings != null)
                settings.BackdropSettings = onDisk.BackdropSettings;
        }

        if (settings.BackdropSettings is not { Enabled: true })
            return 0;
        var count = await new BackdropTextureBaker().BakeAllChunksAsync(levelPath, settings);
        settings.Save(levelPath);
        return count;
    }

    public string BuildPreview(TerrainV9Binary terrain, IReadOnlyCollection<CopyAsset> materials, MtSettings settings)
    {
        return _mapBuilder.BuildPreviewDataUri(terrain, materials, CreateOverlayOptions(settings), CreateMaterialBorderBlendOptions(settings));
    }

    public string BuildLargePreview(TerrainV9Binary terrain, IReadOnlyCollection<CopyAsset> materials, MtSettings settings)
    {
        return _mapBuilder.BuildLargePreviewDataUri(terrain, materials, CreateOverlayOptions(settings), CreateMaterialBorderBlendOptions(settings));
    }

    public static int GetLargePreviewSize(int terrainSize)
    {
        return Math.Min(Math.Max(1, terrainSize), TerrainPbrMapBuilder.LargePreviewMaxSize);
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
            ? new BasecolorOverlayOptions(
                hasOverlayImage ? imagePath : string.Empty,
                Math.Clamp(overlaySettings.GlobalBlend, 0.0, 1.0),
                maskExceptions,
                Math.Clamp(overlaySettings.Brightness, -1.0, 1.0),
                Math.Clamp(overlaySettings.Contrast, -1.0, 1.0),
                Math.Clamp(overlaySettings.Saturation, -1.0, 1.0))
            : null;
    }

    public static MaterialBorderBlendOptions CreateMaterialBorderBlendOptions(MtSettings settings)
    {
        var basecolorSettings = settings.BasecolorModeSettings;
        return new MaterialBorderBlendOptions(
            basecolorSettings.EnableMaterialBorderBlend,
            Math.Clamp(basecolorSettings.MaterialBorderBlendRadius, 0.0, 5.0));
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
        settings.BasecolorModeSettings.MaterialBorderBlendRadius = Math.Clamp(settings.BasecolorModeSettings.MaterialBorderBlendRadius, 0.0, 5.0);
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

    /// <summary>
    /// Copies the paint/basecolor material lists into <paramref name="settings"/>. Pure data plumbing
    /// between <see cref="CopyAsset"/> and <see cref="MtTerrainMaterialSetting"/> - no UI state. Also
    /// used by <c>BasecolorManager.razor.cs</c> as the single source of truth for its own no-arg
    /// <c>UpdateSettingsFromMaterialLists()</c> delegate.
    /// </summary>
    public static void UpdateSettingsFromMaterialLists(MtSettings settings, List<CopyAsset> paintMaterials, List<CopyAsset> basecolorMaterials)
    {
        settings.BasecolorModeSettings.OsmLayerBlendExceptions ??= new List<MtOsmLayerBlendException>();
        settings.PaintModeSettings.Materials = paintMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
        settings.BasecolorModeSettings.Materials = basecolorMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
    }

    /// <summary>
    /// Builds a fresh BaseColor material list synced from <paramref name="paintMaterials"/> (preserving
    /// each material's per-material overlay blend by name/internal-name match), and flushes it into
    /// <paramref name="settings"/>. Returns the new list instead of mutating
    /// <paramref name="basecolorMaterials"/> in place: the page's <c>_basecolorMaterials</c> field can
    /// be enumerated live by a <c>MudTable</c> render while this runs on a background thread (e.g.
    /// Reset &amp; Rebake's <c>Task.Run</c>) - structurally changing a list (Clear/Add) while something
    /// else enumerates it throws "Collection was modified". The caller must assign the returned list
    /// back to its own field/local - that reference reassignment is atomic and safe under a concurrent
    /// read. Also used by <c>BasecolorManager.razor.cs</c> as the single source of truth for its own
    /// no-arg <c>SyncBasecolorMaterialsFromPaintMode()</c> delegate.
    /// </summary>
    public static List<CopyAsset> SyncBasecolorMaterialsFromPaintMode(List<CopyAsset> paintMaterials, IReadOnlyCollection<CopyAsset> basecolorMaterials, MtSettings settings)
    {
        var existingOverlayBlends = basecolorMaterials.ToDictionary(GetMaterialKey, x => x.BaseColorOverlayBlend, StringComparer.OrdinalIgnoreCase);
        var updated = paintMaterials
            .Select(MtTerrainMaterialSetting.FromCopyAsset)
            .Select(x => x.ToCopyAsset())
            .ToList();
        foreach (var material in updated)
        {
            if (existingOverlayBlends.TryGetValue(GetMaterialKey(material), out var blend))
                material.BaseColorOverlayBlend = blend;
        }

        settings.BasecolorModeSettings.Materials = updated
            .Select(MtTerrainMaterialSetting.FromCopyAsset)
            .ToList();

        return updated;
    }

    private static bool PaintModeHasUsableMaterialSettings(List<CopyAsset> paintMaterials)
    {
        return HasUsableMaterialSettings(paintMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset));
    }

    /// <summary>
    /// Whether an overlay image (tile-provider cache hit or manually selected file) is currently active
    /// and present on disk. Also used by <c>BasecolorManager.razor.cs</c> as the single source of truth
    /// for its own no-arg <c>HasActiveBasecolorOverlay</c> delegate.
    /// </summary>
    public static bool HasActiveBasecolorOverlay(MtSettings settings)
    {
        var path = GetActiveBasecolorOverlayPath(settings);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    /// <summary>
    /// Also used by <c>BasecolorManager.razor.cs</c> as the single source of truth for its own no-arg
    /// <c>GetActiveBasecolorOverlayPath()</c> delegate.
    /// </summary>
    public static string GetActiveBasecolorOverlayPath(MtSettings settings)
    {
        var overlaySettings = settings.BasecolorModeSettings.OverlaySettings;
        return overlaySettings.UseTileProvider
            ? overlaySettings.CachedTileImagePath
            : overlaySettings.SelectedImagePath;
    }

    /// <summary>
    /// The renderer only honors the per-material blends; the global slider merely copies its value into
    /// them. GlobalBlend can be &gt; 0 from a previous session while all material blends are 0 (they
    /// reset whenever basecolor materials are re-synced from Paint Mode) - re-apply it, otherwise the
    /// active overlay is invisible in the rebaked BaseColor maps. Also used by
    /// <c>BasecolorManager.razor.cs</c> as the single source of truth for its own no-arg
    /// <c>EnsureDefaultOverlayBlend()</c> delegate (which still runs its own <c>EnsureOverlayDefaults()</c>
    /// null-guarding first - that step isn't part of this shared helper).
    /// </summary>
    public static void EnsureDefaultOverlayBlend(MtSettings settings, List<CopyAsset> basecolorMaterials)
    {
        if (basecolorMaterials.Any(x => x.BaseColorOverlayBlend > 0))
            return;

        var overlaySettings = settings.BasecolorModeSettings.OverlaySettings;
        var globalPercent = (int)Math.Round(Math.Clamp(overlaySettings.GlobalBlend, 0.0, 1.0) * 100.0);
        var blend = Math.Clamp(globalPercent > 0 ? globalPercent : 50, 0, 100) / 100.0;
        overlaySettings.GlobalBlend = blend;
        foreach (var material in basecolorMaterials)
            material.BaseColorOverlayBlend = blend;
    }

    private static string GetMaterialKey(CopyAsset asset)
    {
        return asset.TerrainMaterialInternalName
               ?? asset.TerrainMaterialName
               ?? asset.Name
               ?? asset.Identifier.ToString();
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

/// <summary>Inputs for <see cref="BasecolorManagerService.ResetAndRebakeAsync"/> (spec §10).</summary>
public sealed record ResetRebakeInputs(
    string LevelPath, string LevelName, string MaterialsJsonPath, string TerrainFilePath,
    List<CopyAsset> PaintMaterials, List<CopyAsset> BasecolorMaterials, MtSettings Settings)
{
    /// <summary>
    /// The page's current in-memory terrain, used as a fallback when <see cref="TerrainFilePath"/> is
    /// blank or the file no longer exists on disk - mirrors the page's old
    /// <c>ReloadTerrainFromDisk()</c>, which silently kept whatever terrain was already loaded instead
    /// of throwing. Optional; if both this and the on-disk read are unavailable, the pipeline throws
    /// (there is nothing left to bake from).
    /// </summary>
    public TerrainV9Binary? FallbackTerrain { get; init; }
}

/// <summary>Result of <see cref="BasecolorManagerService.ResetAndRebakeAsync"/> - the freshly reloaded terrain.</summary>
public sealed record ResetRebakeResult(TerrainV9Binary Terrain, int TerrainSize)
{
    /// <summary>
    /// The BaseColor material list after Reset &amp; Rebake's material-sync step. May be a brand-new
    /// list object (Paint Mode had usable settings, so the pipeline re-synced from it) or the same list
    /// the caller passed in (nothing changed) - the caller must always assign this back to its own
    /// field/local, since <see cref="BasecolorManagerService.SyncBasecolorMaterialsFromPaintMode"/> no
    /// longer mutates the input list in place.
    /// </summary>
    public List<CopyAsset> BasecolorMaterials { get; init; } = new();
}
