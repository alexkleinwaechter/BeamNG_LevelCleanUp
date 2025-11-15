using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
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

    // Batch processing caches
    private readonly Dictionary<string, JsonNode> _sourceJsonCache = new();
    private readonly string _targetLevelPath;
    private int? _baseTextureSize;
    private JsonNode _targetJsonCache;
    private FileInfo _targetJsonFile;

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
            _levelNameCopyFrom = PathResolver.LevelNameCopyFrom;
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
        _targetJsonFile = new FileInfo(targetJsonPath);

        // Initialize batch processing - load target JSON once
        InitializeBatch();

        foreach (var material in item.Materials)
            if (!ReplaceTerrainMaterial(material, _targetJsonFile, item.BaseColorHex, item.GetRoughnessValue(),
                    item.ReplaceTargetMaterialName))
            {
                FlushBatch(); // Ensure we write what we have so far
                return false;
            }

        // Flush all cached writes to disk
        FlushBatch();

        // After replacing terrain materials, replace related groundcovers
        if (_groundCoverReplacer != null)
            _groundCoverReplacer.ReplaceGroundCoversForTerrainMaterial(
                item.ReplaceTargetMaterialName,
                item.Materials);

        return true;
    }

    /// <summary>
    ///     Initializes batch processing by loading the target JSON file once
    /// </summary>
    private void InitializeBatch()
    {
        _sourceJsonCache.Clear();

        if (_targetJsonFile != null && _targetJsonFile.Exists)
            _targetJsonCache = JsonUtils.GetValidJsonNodeFromFilePath(_targetJsonFile.FullName);
        else
            _targetJsonCache = new JsonObject();
    }

    /// <summary>
    ///     Flushes the cached target JSON to disk
    /// </summary>
    private void FlushBatch()
    {
        if (_targetJsonCache != null && _targetJsonFile != null)
            File.WriteAllText(_targetJsonFile.FullName,
                _targetJsonCache.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));

        _sourceJsonCache.Clear();
        _targetJsonCache = null;
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
            if (_targetJsonCache == null || _targetJsonCache.AsObject().Count == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot replace material '{replaceTargetMaterialName}' - target file does not exist. Skipping.");
                return true; // Skip, not fatal
            }

            // Use cached source JSON if available
            JsonNode sourceJsonNode;
            if (_sourceJsonCache.TryGetValue(material.MatJsonFileLocation, out var cachedSource))
            {
                sourceJsonNode = cachedSource;
            }
            else
            {
                var jsonString = File.ReadAllText(material.MatJsonFileLocation);
                sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);
                _sourceJsonCache[material.MatJsonFileLocation] = sourceJsonNode;
            }

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

            // Find target material in cached JSON by matching either "name" or "internalName" property
            var targetMaterialNode = _targetJsonCache.AsObject()
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

            // Write to cached target JSON instead of writing to disk immediately
            WriteReplacedTerrainMaterialBatch(targetKey, sourceMaterialObj, replaceTargetMaterialName);

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

    /// <summary>
    ///     Batch-aware version that updates cached JSON instead of writing to disk
    /// </summary>
    private void WriteReplacedTerrainMaterialBatch(
        string targetKey,
        JsonNode materialObj,
        string replaceTargetMaterialName)
    {
        var toText = materialObj.ToJsonString();

        // Remove old entry with the same key from cached JSON
        if (_targetJsonCache.AsObject().ContainsKey(targetKey))
        {
            _targetJsonCache.AsObject().Remove(targetKey);
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Removed existing material '{replaceTargetMaterialName}' (key: {targetKey})");
        }

        // Add new material with the same key to cached JSON
        _targetJsonCache.AsObject().Add(KeyValuePair.Create(targetKey, JsonNode.Parse(toText)));
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Replaced material with key '{targetKey}'");
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
        targetJsonNode.AsObject().Add(KeyValuePair.Create(targetKey, JsonNode.Parse(toText)));
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Replaced material with key '{targetKey}'");

        File.WriteAllText(targetJsonFile.FullName,
            targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
    }
}