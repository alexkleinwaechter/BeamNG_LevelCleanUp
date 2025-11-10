using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles replacing existing terrain materials while preserving target identity
/// </summary>
public class TerrainMaterialReplacer
{
    private readonly FileCopyHandler _fileCopyHandler;
    private readonly GroundCoverReplacer _groundCoverReplacer;
    private readonly string _levelNameCopyFrom;
    private readonly string _levelPathCopyFrom;
    private readonly PathConverter _pathConverter;
    private readonly string _targetLevelPath;
    private int? _baseTextureSize;

    public TerrainMaterialReplacer(
        PathConverter pathConverter,
        FileCopyHandler fileCopyHandler,
        GroundCoverReplacer groundCoverReplacer,
        string levelPathCopyFrom,
        string targetLevelPath)
    {
        _pathConverter = pathConverter;
        _fileCopyHandler = fileCopyHandler;
        _groundCoverReplacer = groundCoverReplacer;
        _levelPathCopyFrom = levelPathCopyFrom;
        _targetLevelPath = targetLevelPath;

        // Extract level name from the path
        if (!string.IsNullOrEmpty(levelPathCopyFrom))
        {
            var dirInfo = new DirectoryInfo(levelPathCopyFrom);
            _levelNameCopyFrom = dirInfo.Parent?.Name;
        }
    }

    public bool Replace(CopyAsset item)
    {
        if (item.TargetPath == null || string.IsNullOrEmpty(item.ReplaceTargetMaterialName))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "Replace operation requires target path and material name. Skipping.");
            return true; // Skip, not an error
        }

        Directory.CreateDirectory(item.TargetPath);

        // Load terrain size with fallback logic
        if (!_baseTextureSize.HasValue) _baseTextureSize = TerrainTextureHelper.LoadBaseTextureSize(_targetLevelPath);

        // Target is always art/terrains/main.materials.json
        var targetJsonPath = Path.Join(item.TargetPath, "main.materials.json");
        var targetJsonFile = new FileInfo(targetJsonPath);

        foreach (var material in item.Materials)
            if (!ReplaceTerrainMaterial(material, targetJsonFile, item.BaseColorHex, item.GetRoughnessValue(),
                    item.ReplaceTargetMaterialName))
                return false;

        // After replacing terrain materials, replace related groundcovers
        if (_groundCoverReplacer != null)
            _groundCoverReplacer.ReplaceGroundCoversForTerrainMaterial(
                item.ReplaceTargetMaterialName,
                item.Materials);

        return true;
    }

    private bool ReplaceTerrainMaterial(
        MaterialJson material,
        FileInfo targetJsonFile,
        string baseColorHex,
        int roughnessValue,
        string replaceTargetMaterialName)
    {
        try
        {
            // Validate target file exists
            if (!targetJsonFile.Exists)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot replace material '{replaceTargetMaterialName}' - target file does not exist. Skipping.");
                return true; // Skip, not fatal
            }

            // Load source material
            var jsonString = File.ReadAllText(material.MatJsonFileLocation);
            var sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);

            // Find source material by matching either "name" or "internalName" property
            var sourceMaterialNode = sourceJsonNode.AsObject()
                .FirstOrDefault(x =>
                {
                    var nameProp = x.Value["name"]?.ToString();
                    var internalNameProp = x.Value["internalName"]?.ToString();

                    // Match if either name or internalName matches material.Name
                    return (!string.IsNullOrEmpty(nameProp) && nameProp == material.Name) ||
                           (!string.IsNullOrEmpty(internalNameProp) && internalNameProp == material.Name);
                });

            if (sourceMaterialNode.Value == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Source material '{material.Name}' not found in {material.MatJsonFileLocation}. Skipping.");
                return true; // Skip
            }

            // Load target material
            var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

            // Find target material by matching either "name" or "internalName" property
            var targetMaterialNode = targetJsonNode.AsObject()
                .FirstOrDefault(x =>
                {
                    var nameProp = x.Value["name"]?.ToString();
                    var internalNameProp = x.Value["internalName"]?.ToString();

                    // Match if either name or internalName matches replaceTargetMaterialName
                    return (!string.IsNullOrEmpty(nameProp) && nameProp == replaceTargetMaterialName) ||
                           (!string.IsNullOrEmpty(internalNameProp) && internalNameProp == replaceTargetMaterialName);
                });

            if (targetMaterialNode.Value == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot find target material '{replaceTargetMaterialName}' to replace. Skipping.");
                return true; // Skip, not fatal
            }

            // Parse source material
            var sourceMaterialObj = JsonNode.Parse(sourceMaterialNode.Value.ToJsonString());
            if (sourceMaterialObj == null) return true;

            // Keep target material's key, name, internalName, persistentId, class
            var targetKey = targetMaterialNode.Key;
            var targetName = targetMaterialNode.Value["name"]?.ToString() ?? replaceTargetMaterialName;
            var targetInternalName = targetMaterialNode.Value["internalName"]?.ToString() ?? targetName;
            var targetGuid = targetMaterialNode.Value["persistentId"]?.ToString() ?? Guid.NewGuid().ToString();
            var targetClass = targetMaterialNode.Value["class"]?.ToString() ?? "TerrainMaterial";

            // Remove preserved properties from source material
            var sourceProperties = sourceMaterialObj.AsObject().ToList();
            foreach (var prop in sourceProperties)
                if (prop.Key == "name" || prop.Key == "internalName" || prop.Key == "persistentId" ||
                    prop.Key == "class")
                    sourceMaterialObj.AsObject().Remove(prop.Key);

            // Set preserved target properties
            sourceMaterialObj["name"] = targetName;
            sourceMaterialObj["internalName"] = targetInternalName;
            sourceMaterialObj["persistentId"] = targetGuid;
            sourceMaterialObj["class"] = targetClass;

            // Copy texture files and update paths (with custom base color and roughness)
            TerrainTextureHelper.CopyTerrainTextures(
                material,
                sourceMaterialObj,
                targetJsonFile.Directory.FullName,
                baseColorHex,
                roughnessValue,
                _baseTextureSize,
                _pathConverter,
                _fileCopyHandler,
                _levelNameCopyFrom);

            // Write to target file (replace mode)
            WriteReplacedTerrainMaterial(targetJsonFile, targetKey, sourceMaterialObj, replaceTargetMaterialName);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Successfully replaced terrain material '{replaceTargetMaterialName}' with '{material.Name}'");

            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error replacing terrain material '{replaceTargetMaterialName}': {ex.Message}");
            return false;
        }
    }

    private void WriteReplacedTerrainMaterial(
        FileInfo targetJsonFile,
        string targetKey,
        JsonNode materialObj,
        string replaceTargetMaterialName)
    {
        var toText = materialObj.ToJsonString();
        var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

        // Remove old entry with the same key
        if (targetJsonNode.AsObject().ContainsKey(targetKey))
        {
            targetJsonNode.AsObject().Remove(targetKey);
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Removed existing material '{replaceTargetMaterialName}' (key: {targetKey})");
        }

        // Add new material with the same key
        targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(targetKey, JsonNode.Parse(toText)));
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Replaced material with key '{targetKey}'");

        File.WriteAllText(targetJsonFile.FullName,
            targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
    }
}