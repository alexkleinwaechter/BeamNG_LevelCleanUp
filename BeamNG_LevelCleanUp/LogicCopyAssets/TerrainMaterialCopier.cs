using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles copying of terrain materials with special naming and path handling (Add mode only)
    /// </summary>
    public class TerrainMaterialCopier
    {
        private readonly PathConverter _pathConverter;
        private readonly FileCopyHandler _fileCopyHandler;
        private readonly string _levelNameCopyFrom;
        private readonly GroundCoverCopier _groundCoverCopier;
        private readonly string _levelPathCopyFrom;
        private readonly string _targetLevelPath;
        private int? _baseTextureSize;

        public TerrainMaterialCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler, string levelNameCopyFrom, GroundCoverCopier groundCoverCopier)
        {
            _pathConverter = pathConverter;
            _fileCopyHandler = fileCopyHandler;
            _levelNameCopyFrom = levelNameCopyFrom;
            _groundCoverCopier = groundCoverCopier;
        }

        public TerrainMaterialCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler, string levelNameCopyFrom, GroundCoverCopier groundCoverCopier, string levelPathCopyFrom)
            : this(pathConverter, fileCopyHandler, levelNameCopyFrom, groundCoverCopier)
        {
            _levelPathCopyFrom = levelPathCopyFrom;
        }

        public TerrainMaterialCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler, string levelNameCopyFrom, GroundCoverCopier groundCoverCopier, string levelPathCopyFrom, string targetLevelPath)
            : this(pathConverter, fileCopyHandler, levelNameCopyFrom, groundCoverCopier, levelPathCopyFrom)
        {
            _targetLevelPath = targetLevelPath;
        }

        public bool Copy(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return true;
            }

            Directory.CreateDirectory(item.TargetPath);

            // Load terrain size with fallback logic
            if (!_baseTextureSize.HasValue)
            {
                _baseTextureSize = TerrainTextureHelper.LoadBaseTextureSize(_targetLevelPath);
            }

            // Target is always art/terrains/main.materials.json
            var targetJsonPath = Path.Join(item.TargetPath, "main.materials.json");
            var targetJsonFile = new FileInfo(targetJsonPath);

            foreach (var material in item.Materials)
            {
                if (!CopyTerrainMaterial(material, targetJsonFile, item.BaseColorHex, item.GetRoughnessValue()))
                {
                    return false;
                }
            }

            // After copying terrain materials, collect related groundcovers
            // (they will be written once at the end by the caller in AssetCopy)
            if (_groundCoverCopier != null)
            {
                _groundCoverCopier.CollectGroundCoversForTerrainMaterials(item.Materials);
            }

            return true;
        }

        private bool CopyTerrainMaterial(MaterialJson material, FileInfo targetJsonFile, string baseColorHex = "#808080", int roughnessValue = 150)
        {
            try
            {
                var jsonString = File.ReadAllText(material.MatJsonFileLocation);
                var sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);

                // Find the material by matching either "name" or "internalName" property
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
                    return true;
                }

                // Generate new GUID and names for the terrain material
                var (newKey, newMaterialName, newInternalName, newGuid) = GenerateTerrainMaterialNames(
                sourceMaterialNode.Key,
                material.Name,
                material.InternalName);

                // Parse and update the material JSON
                var materialObj = JsonNode.Parse(sourceMaterialNode.Value.ToJsonString());
                if (materialObj == null)
                {
                    return true;
                }

                UpdateTerrainMaterialMetadata(materialObj, newMaterialName, newInternalName, newGuid);

                // Copy texture files and update paths (with custom base color and roughness)
                TerrainTextureHelper.CopyTerrainTextures(
                    material,
                    materialObj,
                    targetJsonFile.Directory.FullName,
                    baseColorHex,
                    roughnessValue,
                    _baseTextureSize,
                    _pathConverter,
                    _fileCopyHandler,
                    _levelNameCopyFrom);

                // Write to target file
                WriteTerrainMaterialJson(targetJsonFile, newKey, materialObj);

                return true;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
 $"Terrain materials.json {material.MatJsonFileLocation} can't be parsed. Exception:{ex.Message}");
                return false;
            }
        }

        private void UpdateTerrainMaterialMetadata(JsonNode materialObj, string newName, string newInternalName, string newGuid)
        {
            materialObj["name"] = newName;

            if (materialObj["internalName"] != null)
            {
                materialObj["internalName"] = newInternalName;
            }

            materialObj["persistentId"] = newGuid;
        }

        private (string newKey, string newMaterialName, string newInternalName, string newGuid)
     GenerateTerrainMaterialNames(string originalKey, string materialName, string internalName)
        {
            var newGuid = Guid.NewGuid().ToString();

            // Extract base name from the original key (remove GUID if present)
            var keyParts = originalKey.Split('-');
            string baseName;

            if (keyParts.Length > 1 && Guid.TryParse(string.Join("-", keyParts.Skip(1)), out _))
            {
                baseName = keyParts[0];
            }
            else
            {
                baseName = originalKey;
            }

            // Also extract base name from the "name" property if it has GUID
            var materialNameParts = materialName.Split('-');
            if (materialNameParts.Length > 1 && Guid.TryParse(string.Join("-", materialNameParts.Skip(1)), out _))
            {
                baseName = materialNameParts[0];
            }
            else
            {
                baseName = materialName;
            }

            var baseInternalName = !string.IsNullOrEmpty(internalName) ? internalName : baseName;
            var newInternalName = $"{baseInternalName}_{_levelNameCopyFrom}";
            var newMaterialName = $"{baseName}_{_levelNameCopyFrom}";
            var newKey = $"{baseName}_{_levelNameCopyFrom}";

            return (newKey, newMaterialName, newInternalName, newGuid);
        }

        private void WriteTerrainMaterialJson(FileInfo targetJsonFile, string newKey, JsonNode materialObj)
        {
            var toText = materialObj.ToJsonString();

            if (!targetJsonFile.Exists)
            {
                var jsonObject = new JsonObject(
           new[]
    {
            KeyValuePair.Create<string, JsonNode?>(newKey, JsonNode.Parse(toText))
       });
                File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
            else
            {
                var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

                // ADD MODE: Only add if key doesn't exist
                if (!targetJsonNode.AsObject().Any(x => x.Key == newKey))
                {
                    targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(newKey, JsonNode.Parse(toText)));
                }
                else
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                               $"Terrain material key {newKey} already exists in target, skipping.");
                }

                File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
        }
    }
}
