using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using BeamNG_LevelCleanUp.Utils;
using BeamNgTerrainPoc.Terrain.Export;
using Grille.BeamNG.IO.Binary;

namespace BeamNG_LevelCleanUp.LogicBasecolorManager;

public class BaseColorModeApplier
{
    private readonly TerrainPbrMapBuilder _mapBuilder = new();

    public void Apply(
        string levelPath,
        string levelName,
        string materialsJsonPath,
        TerrainV9Binary terrain,
        IReadOnlyCollection<CopyAsset> materials,
        MtSettings settings,
        bool generateHeight,
        double normalStrength,
        int aoRadius,
        double aoIntensity,
        BasecolorOverlayOptions? overlayOptions,
        MaterialBorderBlendOptions? borderBlendOptions)
    {
        var terrainFolder = Path.GetDirectoryName(materialsJsonPath) ?? Path.Join(levelPath, "art", "terrains");
        var terrainSize = checked((int)terrain.Size);
        var backdropDirectory = Path.Combine(levelPath, "art", "shapes", TerrainBackdropExporter.GroupName);
        var backdropTexturePath = Directory.Exists(backdropDirectory)
            ? Path.Combine(backdropDirectory, TerrainBackdropExporter.BaseColorFileName)
            : null;
        var maps = _mapBuilder.BuildMaps(
            terrain,
            materials,
            terrainFolder,
            generateHeight,
            normalStrength,
            aoRadius,
            aoIntensity,
            overlayOptions,
            borderBlendOptions,
            backdropTexturePath);
        var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(materialsJsonPath);

        foreach (var property in jsonNode.AsObject().ToList())
        {
            if (property.Value is not JsonObject materialObject || materialObject["class"]?.ToString() != "TerrainMaterial")
                continue;

            SetTexture(materialObject, "baseColorBaseTex", maps.BaseColorPath, levelPath, levelName, terrainSize);
            SetTexture(materialObject, "normalBaseTex", maps.NormalPath, levelPath, levelName, terrainSize);
            SetTexture(materialObject, "aoBaseTex", maps.AoPath, levelPath, levelName, terrainSize);
            SetTexture(materialObject, "heightBaseTex", maps.HeightPath, levelPath, levelName, terrainSize);
            SetTexture(materialObject, "roughnessBaseTex", maps.RoughnessPath, levelPath, levelName, terrainSize);
        }

        File.WriteAllText(materialsJsonPath, jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        new PbrUpgradeHandler(materialsJsonPath, levelName, levelPath).EnsureTerrainMaterialTextureSetSize(terrainSize);
        DeletePaintModePlaceholders(terrainFolder);

        if (backdropTexturePath != null && File.Exists(backdropTexturePath))
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Updated the visual terrain backdrop with a {TerrainBackdropExporter.MaximumTextureSize}px BaseColor texture.");

        settings.CurrentMode = BasecolorMode.BaseColorMode;
        settings.BasecolorModeSettings.Materials = materials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
        settings.BasecolorModeSettings.MergedTextureSize = terrainSize;
        settings.BasecolorModeSettings.GenerateHeight = generateHeight;
        settings.BasecolorModeSettings.NormalStrength = normalStrength;
        settings.BasecolorModeSettings.AoRadius = aoRadius;
        settings.BasecolorModeSettings.AoIntensity = aoIntensity;
        settings.Save(levelPath);

        PubSubChannel.SendMessage(PubSubMessageType.Info, $"BaseColor Mode activated. Generated merged {terrainSize}x{terrainSize} terrain PBR maps.");
    }

    private static void SetTexture(JsonObject materialObject, string propertyName, string filePath, string levelPath, string levelName, int size)
    {
        materialObject[propertyName] = ToBeamNgPath(levelPath, levelName, filePath);
        materialObject[propertyName + "Size"] = size;
    }

    private static string ToBeamNgPath(string levelPath, string levelName, string filePath)
    {
        var relativePath = Path.GetRelativePath(levelPath, filePath).Replace('\\', '/');
        return $"/levels/{levelName}/{relativePath}";
    }

    private static void DeletePaintModePlaceholders(string terrainFolder)
    {
        foreach (var file in Directory.GetFiles(terrainFolder, "#base_*.png", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Could not delete Paint Mode placeholder {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }
}
