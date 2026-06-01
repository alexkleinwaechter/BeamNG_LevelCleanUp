using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using BeamNG_LevelCleanUp.Utils;
using SixLabors.ImageSharp;

namespace BeamNG_LevelCleanUp.LogicBasecolorManager;

public class PaintModeApplier
{
    private const int PaintTextureSize = 1024;

    public void Apply(string levelPath, string levelName, string materialsJsonPath, IReadOnlyCollection<CopyAsset> materials, MtSettings settings)
    {
        var terrainFolder = Path.GetDirectoryName(materialsJsonPath) ?? Path.Join(levelPath, "art", "terrains");
        Directory.CreateDirectory(terrainFolder);

        var materialLookup = CreateMaterialLookup(materials);
        var generator = new TerrainTextureGenerator(terrainFolder, PaintTextureSize);
        var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(materialsJsonPath);

        foreach (var property in jsonNode.AsObject().ToList())
        {
            if (property.Value is not JsonObject materialObject || materialObject["class"]?.ToString() != "TerrainMaterial")
                continue;

            var materialKey = GetMaterialKey(materialObject, property.Key);
            var material = materialLookup.GetValueOrDefault(materialKey) ?? materialLookup.GetValueOrDefault(property.Key);
            if (material == null)
                continue;

            var baseColorPath = GenerateAndValidate(generator, material.BaseColorHex, "#base_color", TextureType.RGBA);
            var normalPath = GenerateAndValidate(generator, "#8080FF", "#base_nm", TextureType.Normal);
            var aoPath = GenerateAndValidate(generator, "#FFFFFF", "#base_ao", TextureType.Grayscale);
            var heightPath = GenerateAndValidate(generator, "#000000", "#base_h", TextureType.Grayscale);
            var roughnessPath = GenerateAndValidate(generator, "#EFEFEF", "#base_r", TextureType.Grayscale, material.GetRoughnessValue());

            SetTexture(materialObject, "baseColorBaseTex", baseColorPath, levelPath, levelName, PaintTextureSize);
            SetTexture(materialObject, "normalBaseTex", normalPath, levelPath, levelName, PaintTextureSize);
            SetTexture(materialObject, "aoBaseTex", aoPath, levelPath, levelName, PaintTextureSize);
            SetTexture(materialObject, "heightBaseTex", heightPath, levelPath, levelName, PaintTextureSize);
            SetTexture(materialObject, "roughnessBaseTex", roughnessPath, levelPath, levelName, PaintTextureSize);
        }

        File.WriteAllText(materialsJsonPath, jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        new PbrUpgradeHandler(materialsJsonPath, levelName, levelPath).EnsureTerrainMaterialTextureSetSize(PaintTextureSize);

        settings.CurrentMode = BasecolorMode.PaintMode;
        settings.PaintModeSettings.Materials = materials.Select(MtTerrainMaterialSetting.FromCopyAsset).ToList();
        settings.Save(levelPath);

        PubSubChannel.SendMessage(PubSubMessageType.Info, "Paint Mode activated. Terrain materials now use 1024px per-material base placeholders.");
    }

    private static Dictionary<string, CopyAsset> CreateMaterialLookup(IEnumerable<CopyAsset> materials)
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

    private static string GenerateAndValidate(
        TerrainTextureGenerator generator,
        string hexColor,
        string baseFileName,
        TextureType textureType,
        int? grayscaleValue = null)
    {
        var outputPath = generator.GenerateSolidColorPng(hexColor, baseFileName, textureType, customGreyscaleValue: grayscaleValue);
        if (SixLabors.ImageSharp.Image.Identify(outputPath) is { Width: PaintTextureSize, Height: PaintTextureSize })
            return outputPath;

        File.Delete(outputPath);
        return generator.GenerateSolidColorPng(hexColor, baseFileName, textureType, customGreyscaleValue: grayscaleValue);
    }

    private static string GetMaterialKey(JsonObject materialObject, string fallback)
    {
        return materialObject["internalName"]?.ToString()
               ?? materialObject["name"]?.ToString()
               ?? fallback;
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
}
