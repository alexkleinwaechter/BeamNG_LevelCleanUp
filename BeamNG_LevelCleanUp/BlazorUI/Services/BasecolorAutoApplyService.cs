using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicBasecolorManager;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     Post-generation BaseColor Mode automation (backdrop follow-up doc 06): downloads the
///     satellite tile overlay for the terrain, pins the overlay blend of every road-smoothing /
///     road-painting material to 0 (no satellite bleed into the road system), sets all other
///     materials to the user's non-road blend, activates BaseColor Mode (merged PBR map bake) and
///     rebakes backdrop chunk textures when a backdrop exists. Built entirely on the Basecolor
///     Manager's public pipeline so a later manual session on that page sees consistent state.
///     Runs only when <c>TerrainGenerationState.EffectiveTexturingMode</c> is BaseColorMode; all
///     failures are warn-only — the terrain generation run itself never fails here.
/// </summary>
public class BasecolorAutoApplyService
{
    private const string DefaultTileProvider = "Google Satelite Only";

    public async Task<bool> ApplyAsync(TerrainGenerationState state)
    {
        if (string.IsNullOrWhiteSpace(state.WorkingDirectory) || !Directory.Exists(state.WorkingDirectory))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "[BASECOLOR-AUTO] Skipped: the level folder is not available.");
            return false;
        }

        var service = new BasecolorManagerService();
        var load = await Task.Run(() => service.LoadLevel(state.WorkingDirectory)).ConfigureAwait(false);
        if (!load.Success)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"[BASECOLOR-AUTO] Skipped: {load.ErrorMessage}");
            return false;
        }

        var settings = load.Settings;
        if (!MapTileOverlayService.HasUsableGeoReference(settings.GeoReferenceSettings))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "[BASECOLOR-AUTO] Skipped: BaseColor Mode automation needs a georeferenced elevation source " +
                "(GeoTIFF/XYZ). Use the Basecolor Manager manually for PNG heightmaps.");
            return false;
        }

        var roadMaterialNames = ApplyOverlayBlends(state, load, settings);

        // Fetch (or reuse) the warped satellite overlay for the terrain extent. Same pipeline as
        // the Basecolor Manager's Fetch button; shares the MT_Tiles cache with the backdrop baker.
        var overlaySettings = settings.BasecolorModeSettings.OverlaySettings;
        var provider = ResolveProviderName(overlaySettings);
        var imageryDate = string.IsNullOrWhiteSpace(overlaySettings.TileImageryDate)
            ? null
            : overlaySettings.TileImageryDate;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"[BASECOLOR-AUTO] Downloading satellite tiles ({provider}) for the terrain...");
        var overlayResult = await new MapTileOverlayService()
            .EnsureOverlayImageAsync(load.LevelPath, settings.GeoReferenceSettings, provider, load.TerrainSize, imageryDate)
            .ConfigureAwait(false);

        overlaySettings.CachedTileImagePath = overlayResult.ImagePath;
        overlaySettings.UseTileProvider = true;
        // Write the provider back explicitly: the backdrop baker stamps LastBakeProvider with its
        // fallback while an empty setting would stay empty, which trips the manager's staleness check.
        overlaySettings.SelectedTileProvider = provider;

        // Bake the merged terrain PBR maps and switch the level to BaseColor Mode (saves settings).
        await Task.Run(() => new BaseColorModeApplier().Apply(
            load.LevelPath,
            load.LevelName,
            load.MaterialsJsonPath,
            load.TerrainFilePath,
            load.Terrain,
            load.BasecolorMaterials,
            settings,
            settings.BasecolorModeSettings.GenerateHeight,
            settings.BasecolorModeSettings.NormalStrength,
            settings.BasecolorModeSettings.AoRadius,
            settings.BasecolorModeSettings.AoIntensity,
            BasecolorManagerService.CreateOverlayOptions(settings),
            BasecolorManagerService.CreateMaterialBorderBlendOptions(settings))).ConfigureAwait(false);

        // Keep the backdrop consistent (no-op without one): refreshes chunk textures from the shared
        // tile cache and re-stamps LastTextureBakeUtc/LastBakeProvider so no staleness banner fires.
        await service.RebakeBackdropTexturesAsync(load.LevelPath, settings).ConfigureAwait(false);

        var roadSummary = roadMaterialNames.Count > 0
            ? $"{roadMaterialNames.Count} road material(s) pinned to 0% ({string.Join(", ", roadMaterialNames)})"
            : "no road materials to pin";
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"[BASECOLOR-AUTO] BaseColor Mode activated: satellite blend " +
            $"{Math.Clamp(state.Texturing.NonRoadOverlayBlendPercent, 0, 100)}% for non-road materials, {roadSummary}.");
        return true;
    }

    /// <summary>
    ///     Pins every material selected for road smoothing or road painting on the GenerateTerrain
    ///     page to overlay blend 0 and sets all others to the user's non-road blend. Returns the
    ///     pinned material names for the summary message.
    /// </summary>
    private static List<string> ApplyOverlayBlends(
        TerrainGenerationState state, BasecolorManagerLoadResult load, MtSettings settings)
    {
        var roadKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in state.TerrainMaterials.Where(m => m.IsRoadMaterial || m.EnableRoadPainting))
        {
            if (!string.IsNullOrWhiteSpace(material.InternalName))
                roadKeys.Add(material.InternalName);
            if (!string.IsNullOrWhiteSpace(material.MaterialName))
                roadKeys.Add(material.MaterialName);
        }

        var nonRoadBlend = Math.Clamp(state.Texturing.NonRoadOverlayBlendPercent, 0, 100) / 100.0;
        var pinned = new List<string>();
        foreach (var material in load.BasecolorMaterials)
        {
            if (IsRoadMaterial(material, roadKeys))
            {
                material.BaseColorOverlayBlend = 0.0;
                pinned.Add(material.TerrainMaterialInternalName ?? material.Name ?? string.Empty);
            }
            else
            {
                material.BaseColorOverlayBlend = nonRoadBlend;
            }
        }

        // Safety net: a road material missing from the level's Basecolor list would hit the PBR
        // baker's lookup fallback of blend 1.0 — full satellite on exactly the material we must
        // protect. Append it with defaults instead (gray base color; tune in the Basecolor Manager).
        var knownKeys = new HashSet<string>(
            load.BasecolorMaterials.SelectMany(KeysOf), StringComparer.OrdinalIgnoreCase);
        foreach (var material in state.TerrainMaterials.Where(m => m.IsRoadMaterial || m.EnableRoadPainting))
        {
            if (string.IsNullOrWhiteSpace(material.InternalName) || knownKeys.Contains(material.InternalName))
                continue;

            var appended = new MtTerrainMaterialSetting
            {
                InternalName = material.InternalName,
                Name = string.IsNullOrWhiteSpace(material.MaterialName) ? material.InternalName : material.MaterialName
            }.ToCopyAsset();
            appended.BaseColorOverlayBlend = 0.0;
            load.BasecolorMaterials.Add(appended);
            pinned.Add(material.InternalName);
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"[BASECOLOR-AUTO] Road material '{material.InternalName}' was missing from the Basecolor settings " +
                "and was added with a neutral base color — review it in the Basecolor Manager.");
        }

        // The renderer only honors per-material blends; the global slider is a copy-down
        // convenience — keep it showing the value the automation actually applied.
        settings.BasecolorModeSettings.OverlaySettings.GlobalBlend = nonRoadBlend;
        BasecolorManagerService.UpdateSettingsFromMaterialLists(settings, load.PaintMaterials, load.BasecolorMaterials);
        return pinned;
    }

    private static bool IsRoadMaterial(CopyAsset material, HashSet<string> roadKeys)
    {
        return (!string.IsNullOrWhiteSpace(material.TerrainMaterialInternalName) && roadKeys.Contains(material.TerrainMaterialInternalName))
               || (!string.IsNullOrWhiteSpace(material.TerrainMaterialName) && roadKeys.Contains(material.TerrainMaterialName))
               || (!string.IsNullOrWhiteSpace(material.Name) && roadKeys.Contains(material.Name));
    }

    private static IEnumerable<string> KeysOf(CopyAsset material)
    {
        if (!string.IsNullOrWhiteSpace(material.TerrainMaterialInternalName))
            yield return material.TerrainMaterialInternalName;
        if (!string.IsNullOrWhiteSpace(material.TerrainMaterialName))
            yield return material.TerrainMaterialName;
        if (!string.IsNullOrWhiteSpace(material.Name))
            yield return material.Name;
    }

    /// <summary>
    ///     Reuses the provider a previous Basecolor Manager session selected, falling back to the
    ///     default. A date-capable provider (ArcGIS Wayback) without a stored imagery date would
    ///     throw in the tile service — fall back to the default provider instead.
    /// </summary>
    private static string ResolveProviderName(MtBasecolorOverlaySettings overlaySettings)
    {
        var provider = string.IsNullOrWhiteSpace(overlaySettings.SelectedTileProvider)
            ? DefaultTileProvider
            : overlaySettings.SelectedTileProvider;

        var providerRecord = MapTileOverlayService.Providers
            .FirstOrDefault(x => x.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (providerRecord is { SupportsHistoricalDate: true } && string.IsNullOrWhiteSpace(overlaySettings.TileImageryDate))
            return DefaultTileProvider;

        return provider;
    }
}
