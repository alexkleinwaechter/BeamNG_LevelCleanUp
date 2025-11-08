using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;
using System.Text.Json;

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
        private readonly GroundCoverCopier _groundCoverCopier;
        private readonly string _levelPathCopyFrom;
        private int? _terrainSize;

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

        public bool Copy(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return true;
            }

            Directory.CreateDirectory(item.TargetPath);

            // Try to get terrain size from terrain.json if not already loaded
            if (!_terrainSize.HasValue && !string.IsNullOrEmpty(_levelPathCopyFrom))
            {
                _terrainSize = GetTerrainSizeFromJson(_levelPathCopyFrom);
            }

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

            // After copying terrain materials, collect related groundcovers
            // (they will be written once at the end by the caller in AssetCopy)
            if (_groundCoverCopier != null)
            {
                _groundCoverCopier.CollectGroundCoversForTerrainMaterials(item.Materials);
            }

            return true;
        }

        /// <summary>
        /// Reads the terrain size from the *.terrain.json file
        /// </summary>
        private int? GetTerrainSizeFromJson(string levelPath)
        {
            try
            {
                // Find the terrain.json file in the level directory
                var terrainFiles = Directory.GetFiles(levelPath, "*.terrain.json", SearchOption.AllDirectories);

                if (terrainFiles.Length == 0)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"No *.terrain.json file found in {levelPath}. Using default terrain size 2048.");
                    return 2048; // Default fallback
                }

                var terrainFile = terrainFiles[0];
                using JsonDocument jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(terrainFile);

                if (jsonDoc.RootElement.TryGetProperty("squareSize", out JsonElement sizeElement))
                {
                    var size = sizeElement.GetInt32();
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                                 $"Terrain size from {Path.GetFileName(terrainFile)}: {size}x{size}");
                    return size;
                }
                else
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                     $"No 'squareSize' property found in {terrainFile}. Using default 2048.");
                    return 2048;
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                 $"Error reading terrain size: {ex.Message}. Using default 2048.");
                return 2048;
            }
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

                // Copy texture files and update paths (with baseColorBaseTex replacement)
                CopyTerrainTextures(material, materialObj, targetJsonFile.Directory.FullName);

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

        private void CopyTerrainTextures(MaterialJson material, JsonNode materialObj, string targetTerrainFolder)
        {
            TerrainTextureGenerator? textureGenerator = null;

            // Initialize texture generator if we have terrain size
            if (_terrainSize.HasValue)
            {
                textureGenerator = new TerrainTextureGenerator(targetTerrainFolder, _terrainSize.Value);
            }

            foreach (var matFile in material.MaterialFiles)
            {
                var originalPath = matFile.OriginalJsonPath;

                // Check if this is a texture that should be replaced with generated dummy
                if (textureGenerator != null &&
                    TerrainTextureGenerator.IsReplaceableTexture(matFile.MapType))
                {
                    var textureProps = TerrainTextureGenerator.GetTextureProperties(matFile.MapType);
                    if (textureProps != null)
                    {
                        // Generate replacement PNG
                        var generatedPngPath = textureGenerator.GenerateSolidColorPng(
                            textureProps.HexColor,
                            textureProps.FileName,
                            textureProps.Type);

                        // Update the path in the material JSON to point to the generated PNG
                        var newPath = _pathConverter.GetBeamNgJsonFileName(generatedPngPath);
                        UpdateTexturePathsInMaterial(materialObj, originalPath, newPath);

                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                           $"Replaced {matFile.MapType} with generated texture: {Path.GetFileName(generatedPngPath)}");

                        // Also update the corresponding size property if it exists
                        var sizePropertyName = matFile.MapType + "Size";
                        if (materialObj[sizePropertyName] != null)
                        {
                            materialObj[sizePropertyName] = _terrainSize.Value;
                        }

                        continue; // Skip normal file copy for this texture
                    }
                }

                // Normal texture copy for all other textures
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
                var newNormalPath = _pathConverter.GetBeamNgJsonFileName(targetFullName);
                UpdateTexturePathsInMaterial(materialObj, originalPath, newNormalPath);
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
