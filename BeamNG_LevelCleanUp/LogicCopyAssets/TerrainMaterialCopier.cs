using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Handles copying of terrain materials with special naming and path handling
    /// </summary>
    public class TerrainMaterialCopier
    {
        private readonly PathConverter _pathConverter;
        private readonly FileCopyHandler _fileCopyHandler;
        private readonly string _levelNameCopyFrom;

        public TerrainMaterialCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler, string levelNameCopyFrom)
        {
            _pathConverter = pathConverter;
            _fileCopyHandler = fileCopyHandler;
            _levelNameCopyFrom = levelNameCopyFrom;
        }

        public bool Copy(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return true;
            }

            Directory.CreateDirectory(item.TargetPath);

            // Target is always art/terrains/main.materials.json
            var targetJsonPath = Path.Join(item.TargetPath, "main.materials.json");
            var targetJsonFile = new FileInfo(targetJsonPath);

            foreach (var material in item.Materials)
            {
                if (!CopyTerrainMaterial(material, targetJsonFile))
                {
                    return false;
                }
            }

            return true;
        }

        private bool CopyTerrainMaterial(MaterialJson material, FileInfo targetJsonFile)
        {
            try
            {
                var jsonString = File.ReadAllText(material.MatJsonFileLocation);
                var sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);

                var sourceMaterialNode = sourceJsonNode.AsObject()
             .FirstOrDefault(x => x.Value["name"]?.ToString() == material.Name);

                if (sourceMaterialNode.Value == null)
                {
                    return true;
                }

                // Generate new GUID and names for the terrain material
                var (newKey, newMaterialName, newInternalName, newGuid) = GenerateTerrainMaterialNames(
         sourceMaterialNode.Key,
              material.Name,
           material.InternalName
            );

                // Parse and update the material JSON
                var materialObj = JsonNode.Parse(sourceMaterialNode.Value.ToJsonString());
                if (materialObj == null)
                {
                    return true;
                }

                UpdateTerrainMaterialMetadata(materialObj, newMaterialName, newInternalName, newGuid);

                // Copy texture files and update paths
                CopyTerrainTextures(material, materialObj);

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

        private void UpdateTerrainMaterialMetadata(JsonNode materialObj, string newName, string newInternalName, string newGuid)
        {
            materialObj["name"] = newName;

            if (materialObj["internalName"] != null)
            {
                materialObj["internalName"] = newInternalName;
            }

            materialObj["persistentId"] = newGuid;
        }

        private void CopyTerrainTextures(MaterialJson material, JsonNode materialObj)
        {
            foreach (var matFile in material.MaterialFiles)
            {
                var targetFullName = _pathConverter.GetTerrainTargetFileName(matFile.File.FullName);
                if (string.IsNullOrEmpty(targetFullName))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetFullName));
                    _fileCopyHandler.CopyFile(matFile.File.FullName, targetFullName);
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Filepath error for terrain texture {material.Name}. Exception:{ex.Message}");
                }

                // Update texture path in the material JSON
                var originalPath = matFile.OriginalJsonPath;
                var newPath = _pathConverter.GetBeamNgJsonFileName(targetFullName);

                UpdateTexturePathsInMaterial(materialObj, originalPath, newPath);
            }
        }

        private void UpdateTexturePathsInMaterial(JsonNode materialNode, string oldPath, string newPath)
        {
            if (materialNode is JsonObject obj)
            {
                foreach (var prop in obj.ToList())
                {
                    if (prop.Value != null)
                    {
                        if (prop.Value is JsonValue jsonValue)
                        {
                            try
                            {
                                var strValue = jsonValue.GetValue<string>();
                                if (!string.IsNullOrEmpty(strValue) && strValue.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    obj[prop.Key] = newPath;
                                }
                            }
                            catch
                            {
                                // Not a string value, skip
                            }
                        }
                        else
                        {
                            UpdateTexturePathsInMaterial(prop.Value, oldPath, newPath);
                        }
                    }
                }
            }
            else if (materialNode is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] != null)
                    {
                        if (arr[i] is JsonValue jsonValue)
                        {
                            try
                            {
                                var strValue = jsonValue.GetValue<string>();
                                if (!string.IsNullOrEmpty(strValue) && strValue.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    arr[i] = JsonValue.Create(newPath);
                                }
                            }
                            catch
                            {
                                // Not a string value, skip
                            }
                        }
                        else
                        {
                            UpdateTexturePathsInMaterial(arr[i], oldPath, newPath);
                        }
                    }
                }
            }
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
                }
                        );
                File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
            else
            {
                var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);

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
