using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

public class TerrainCopyScanner
{
    public TerrainCopyScanner(string terrainMaterialsJsonPath, string levelPathCopyFrom, string namePath,
        List<MaterialJson> materialsJsonCopy, List<CopyAsset> copyAssets)
    {
        _terrainMaterialsJsonPath = terrainMaterialsJsonPath;
        _levelPathCopyFrom = levelPathCopyFrom;
        _namePath = namePath;
        _materialsJsonCopy = materialsJsonCopy;
        _copyAssets = copyAssets;
    }

    private string _terrainMaterialsJsonPath { get; }
    private string _levelPathCopyFrom { get; }
    private string _namePath { get; }
    private List<MaterialJson> _materialsJsonCopy { get; }
    private List<CopyAsset> _copyAssets { get; }

    public void ScanTerrainMaterials()
    {
        try
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_terrainMaterialsJsonPath);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                foreach (var child in jsonObject.RootElement.EnumerateObject())
                    try
                    {
                        var material =
                            child.Value.Deserialize<MaterialJson>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (material != null && material.Class == "TerrainMaterial")
                        {
                            material.MatJsonFileLocation = _terrainMaterialsJsonPath;

                            if (string.IsNullOrEmpty(material.Name) && !string.IsNullOrEmpty(material.InternalName))
                                material.Name = material.InternalName;

                            // Scan for texture files dynamically from all properties
                            material.MaterialFiles = new List<MaterialFile>();
                            ScanTextureFilesFromProperties(child.Value, material);

                            _materialsJsonCopy.Add(material);

                            // Create CopyAsset for terrain material
                            var copyAsset = new CopyAsset
                            {
                                CopyAssetType = CopyAssetType.Terrain,
                                Name = material.Name,
                                TerrainMaterialName = material.Name,
                                TerrainMaterialInternalName = material.InternalName,
                                Materials = new List<MaterialJson> { material },
                                SourceMaterialJsonPath = _terrainMaterialsJsonPath,
                                TargetPath = Path.Join(_namePath, Constants.Terrains)
                            };

                            copyAsset.SizeMb = Math.Round(
                                material.MaterialFiles.Sum(x => x.File.Exists ? x.File.Length : 0) / 1024f / 1024f, 2);

                            _copyAssets.Add(copyAsset);
                        }
                    }
                    catch (Exception ex)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"Error reading terrain material {child.Name}: {ex.Message}");
                    }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error scanning terrain materials from {_terrainMaterialsJsonPath}: {ex.Message}");
        }
    }

    private void ScanTextureFilesFromProperties(JsonElement materialElement, MaterialJson material)
    {
        // Dynamically scan all properties that might contain texture paths
        // Properties ending with "Tex" or "Map" are likely texture references
        foreach (var prop in materialElement.EnumerateObject())
            try
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var propName = prop.Name;
                    var propValue = prop.Value.GetString();

                    // Check if this looks like a texture path (contains "levels/" or ends with image extension)
                    if (!string.IsNullOrEmpty(propValue) &&
                        (propName.EndsWith("Tex", StringComparison.OrdinalIgnoreCase) ||
                         propName.EndsWith("Map", StringComparison.OrdinalIgnoreCase)))
                    {
                        var fi = new FileInfo(PathResolver.ResolvePath(_levelPathCopyFrom, propValue, false));
                        if (!fi.Exists) fi = FileUtils.ResolveImageFileName(fi.FullName);

                        material.MaterialFiles.Add(new MaterialFile
                        {
                            File = fi,
                            MapType = propName,
                            Missing = !fi.Exists,
                            OriginalJsonPath = propValue // Store the original JSON path
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Error scanning property {prop.Name} for material {material.Name}: {ex.Message}");
            }
    }

    /// <summary>
    ///     Scans the target level's terrain materials for the replacement dropdown
    ///     Dynamically finds the actual terrain materials file instead of using hardcoded paths
    ///     Returns a list of terrain material names available in the target
    /// </summary>
    public static List<string> GetTargetTerrainMaterials(string namePath)
    {
        var targetMaterials = new List<string>();

        try
        {
            // Dynamically find terrain materials file (same pattern as CopyTerrainMaterials in BeamFileReader)
            var terrainPath = Path.Join(namePath, "art", "terrains");
            if (!Directory.Exists(terrainPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"No terrains folder found in target level at {terrainPath}.");
                return targetMaterials;
            }

            // Search for materials.json files that contain TerrainMaterial
            var materialFiles = Directory.GetFiles(terrainPath, "*.materials.json", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(terrainPath, "materials.json", SearchOption.AllDirectories))
                .Distinct()
                .ToList();

            foreach (var materialFile in materialFiles)
            {
                // Check if this file contains TerrainMaterial entries
                if (!File.Exists(materialFile))
                    continue;

                var jsonContent = File.ReadAllText(materialFile);
                if (!jsonContent.Contains("\"class\": \"TerrainMaterial\"") &&
                    !jsonContent.Contains("\"class\":\"TerrainMaterial\""))
                    continue;

                // This file has terrain materials, scan it
                using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(materialFile);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                    foreach (var child in jsonObject.RootElement.EnumerateObject())
                        try
                        {
                            var material =
                                child.Value.Deserialize<MaterialJson>(BeamJsonOptions.GetJsonSerializerOptions());
                            if (material != null && material.Class == "TerrainMaterial")
                            {
                                var materialName = !string.IsNullOrEmpty(material.Name)
                                    ? material.Name
                                    : material.InternalName;
                                if (!string.IsNullOrEmpty(materialName) && !targetMaterials.Contains(materialName))
                                    targetMaterials.Add(materialName);
                            }
                        }
                        catch (Exception ex)
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                                $"Error reading target terrain material {child.Name}: {ex.Message}");
                        }
            }

            if (!targetMaterials.Any())
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "No terrain materials found in target level.");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not load target terrain materials: {ex.Message}");
        }

        return targetMaterials.OrderBy(x => x).ToList();
    }
}