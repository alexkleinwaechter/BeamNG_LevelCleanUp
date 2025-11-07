using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    public class AssetCopy
    {
        private List<Guid> _identifier { get; set; }
        private List<CopyAsset> _assetsToCopy = new List<CopyAsset>();
        private string namePath;
        private string levelName;
        private string levelNameCopyFrom;
        private bool stopFaultyFile = false;

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList)
        {
            _identifier = identifier;
            _assetsToCopy = copyAssetList.Where(x => identifier.Contains(x.Identifier)).ToList();
        }

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, string namePath) : this(identifier, copyAssetList)
        {
            this.namePath = namePath;
        }

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, string namePath, string levelName) : this(identifier, copyAssetList, namePath)
        {
            this.levelName = levelName;
        }

        public AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList, string namePath, string levelName, string levelNameCopyFrom) : this(identifier, copyAssetList, namePath, levelName)
        {
            this.levelNameCopyFrom = levelNameCopyFrom;
        }

        public void Copy()
        {
            foreach (var item in _assetsToCopy)
            {
                switch (item.CopyAssetType)
                {
                    case CopyAssetType.Road:
                        CopyRoad(item);
                        break;
                    case CopyAssetType.Decal:
                        CopyManagedDecal(item);
                        CopyDecal(item);
                        break;
                    case CopyAssetType.Dae:
                        CopyDae(item);
                        break;
                    case CopyAssetType.Terrain:
                        CopyTerrain(item);
                        break;
                    default:
                        break;
                }
                if (stopFaultyFile)
                {
                    break;
                }
            }
            if (!stopFaultyFile)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, $"Done! Assets copied. Build your deployment file now.");
            }
            stopFaultyFile = false;
        }

        private void CopyRoad(CopyAsset item)
        {
            CopyMaterials(item);
        }

        private void CopyDecal(CopyAsset item)
        {
            CopyMaterials(item);
        }

        private void CopyManagedDecal(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return;
            }
            Directory.CreateDirectory(item.TargetPath);
            var targetJsonPath = Path.Join(item.TargetPath, "managedDecalData.json");
            var targetJsonFile = new FileInfo(targetJsonPath);
            if (!targetJsonFile.Exists)
            {
                var jsonObject = new JsonObject(
                new[]
                    {
                          KeyValuePair.Create<string, JsonNode?>(item.DecalData.Name, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(item.DecalData,BeamJsonOptions.GetJsonSerializerOptions()))),
                    }
                );
                File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
            else
            {
                var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);
                if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == item.DecalData.Name))
                {
                    targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(item.DecalData.Name, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(item.DecalData, BeamJsonOptions.GetJsonSerializerOptions()))));
                }
                File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
        }

        private void CopyDae(CopyAsset item)
        {
            Directory.CreateDirectory(item.TargetPath);

            var daeFullName = GetTargetFileName(item.DaeFilePath);
            if (string.IsNullOrEmpty(daeFullName))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(daeFullName));
                BeamFileCopy(item.DaeFilePath, daeFullName);
                //Path.ChangeExtension(daeFullName, ".CDAE")
                try
                {
                    BeamFileCopy(Path.ChangeExtension(item.DaeFilePath, ".cdae"), Path.ChangeExtension(daeFullName, ".cdae"));
                }
                catch (Exception)
                {
                    //ignore
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Filepath error for daefile {daeFullName}. Exception:{ex.Message}");
            }

            CopyMaterials(item);
        }

        private void CopyTerrain(CopyAsset item)
        {
            CopyTerrainMaterials(item);
        }

        private void CopyTerrainMaterials(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return;
            }
            Directory.CreateDirectory(item.TargetPath);

            // Target is always art/terrains/main.materials.json
            var targetJsonPath = Path.Join(item.TargetPath, "main.materials.json");
            var targetJsonFile = new FileInfo(targetJsonPath);

            foreach (var material in item.Materials)
            {
                try
                {
                    var jsonString = File.ReadAllText(material.MatJsonFileLocation);
                    JsonNode sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);
                    
                    var sourceMaterialNode = sourceJsonNode.AsObject().FirstOrDefault(x => x.Value["name"]?.ToString() == material.Name);
                    if (sourceMaterialNode.Value == null)
                    {
                        continue;
                    }

                    // Generate new GUID for the terrain material
                    var newGuid = Guid.NewGuid().ToString();
                    
                    // Extract the base name (remove original GUID from key if present)
                    var originalKey = sourceMaterialNode.Key;
                    string baseName;
                    
                    // Check if the key contains a GUID pattern (name-guid format)
                    var keyParts = originalKey.Split('-');
                    if (keyParts.Length > 1 && Guid.TryParse(string.Join("-", keyParts.Skip(1)), out _))
                    {
                        // Key has format "Name-GUID", extract just the name part
                        baseName = keyParts[0];
                    }
                    else
                    {
                        // No GUID in key, use the whole key as base name
                        baseName = originalKey;
                    }
                    
                    // Also extract base name from the "name" property if it has GUID
                    var materialNameParts = material.Name.Split('-');
                    if (materialNameParts.Length > 1 && Guid.TryParse(string.Join("-", materialNameParts.Skip(1)), out _))
                    {
                        baseName = materialNameParts[0];
                    }
                    else
                    {
                        baseName = material.Name;
                    }
                    
                    // Suffix the material names
                    // key: BaseName_LevelName-NewGUID
                    // name: BaseName_LevelName_NewGUID
                    // internalName: BaseInternalName_LevelName (or BaseName_LevelName if no internalName)
                    var baseInternalName = !string.IsNullOrEmpty(material.InternalName) ? material.InternalName : baseName;
                    var newInternalName = $"{baseInternalName}_{levelNameCopyFrom}";
                    var newMaterialName = $"{baseName}_{levelNameCopyFrom}_{newGuid}";
                    var newKey = $"{baseName}_{levelNameCopyFrom}-{newGuid}";

                    // Parse the material JSON to update it
                    var materialObj = JsonNode.Parse(sourceMaterialNode.Value.ToJsonString());
                    if (materialObj != null)
                    {
                        // Update the required fields
                        materialObj["name"] = newMaterialName;
                        if (materialObj["internalName"] != null)
                        {
                            materialObj["internalName"] = newInternalName;
                        }
                        materialObj["persistentId"] = newGuid;

                        // Update all texture paths in the material
                        foreach (var matFile in material.MaterialFiles)
                        {
                            var targetFullName = GetTerrainTargetFileName(matFile.File.FullName);
                            if (string.IsNullOrEmpty(targetFullName))
                            {
                                continue;
                            }
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(targetFullName));
                                BeamFileCopy(matFile.File.FullName, targetFullName);
                            }
                            catch (Exception ex)
                            {
                                PubSubChannel.SendMessage(PubSubMessageType.Error, 
                                    $"Filepath error for terrain texture {material.Name}. Exception:{ex.Message}");
                            }

                            // Update texture path in the material JSON (dynamically for all properties)
                            var originalPath = GetBeamNgJsonFileName(matFile.File.FullName);
                            var newPath = GetBeamNgJsonFileName(targetFullName);
                            
                            UpdateTexturePathsInMaterial(materialObj, originalPath, newPath);
                        }

                        var toText = materialObj.ToJsonString();

                        // Write to target main.materials.json
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
                            
                            // Check if material with this key already exists
                            if (!targetJsonNode.AsObject().Any(x => x.Key == newKey))
                            {
                                try
                                {
                                    targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(newKey, JsonNode.Parse(toText)));
                                }
                                catch (Exception)
                                {
                                    throw;
                                }
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
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error, 
                        $"Terrain materials.json {material.MatJsonFileLocation} can't be parsed. Exception:{ex.Message}");
                    stopFaultyFile = true;
                    break;
                }
            }
        }

        private void UpdateTexturePathsInMaterial(JsonNode materialNode, string oldPath, string newPath)
        {
            // Recursively update all texture paths in the material JSON
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

        private void CopyMaterials(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return;
            }
            Directory.CreateDirectory(item.TargetPath);
            foreach (var material in item.Materials)
            {
                try
                {
                    var jsonString = File.ReadAllText(material.MatJsonFileLocation);
                    JsonNode sourceJsonNode;
                    sourceJsonNode = JsonUtils.GetValidJsonNodeFromString(jsonString, material.MatJsonFileLocation);
                    _ = sourceJsonNode.AsObject().FirstOrDefault(x => x.Value["name"]?.ToString() == material.Name);
                    var sourceMaterialNode = sourceJsonNode.AsObject().FirstOrDefault(x => x.Value["name"]?.ToString() == material.Name);
                    if (sourceMaterialNode.Value == null)
                    {
                        continue;
                    }
                    var toText = sourceMaterialNode.Value.ToJsonString();
                    var targetJsonPath = Path.Join(item.TargetPath, Path.GetFileName(material.MatJsonFileLocation));
                    var targetJsonFile = new FileInfo(targetJsonPath);
                    foreach (var matFile in material.MaterialFiles)
                    {
                        var targetFullName = GetTargetFileName(matFile.File.FullName);
                        if (string.IsNullOrEmpty(targetFullName))
                        {
                            continue;
                        }
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(targetFullName));
                            BeamFileCopy(matFile.File.FullName, targetFullName);
                        }
                        catch (Exception ex)
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Filepath error for material {material.Name}. Exception:{ex.Message}");
                        }

                        toText = toText.Replace(GetBeamNgJsonFileName(matFile.File.FullName), GetBeamNgJsonFileName(targetFullName), StringComparison.OrdinalIgnoreCase);
                    }
                    if (!targetJsonFile.Exists)
                    {
                        var jsonObject = new JsonObject(
                        new[]
                            {
                          KeyValuePair.Create<string, JsonNode?>(material.Name, JsonNode.Parse(toText)),
                            }
                        );
                        File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
                    }
                    else
                    {
                        var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetJsonFile.FullName);
                        if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == material.Name))
                        {
                            try
                            {
                                targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(material.Name, JsonNode.Parse(toText)));
                            }
                            catch (Exception)
                            {
                                throw;
                            }
                        }
                        File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
                    }

                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error, $"materials.json {material.MatJsonFileLocation} can't be parsed. Exception:{ex.Message}");
                    stopFaultyFile = true;
                    break;
                }
            }
        }

        private void BeamFileCopy(string sourceFile, string targetFile)
        {
            try
            {
                File.Copy(sourceFile, targetFile, true);
            }
            catch (Exception)
            {
                var fileParts = sourceFile.Split(@"\levels\");
                if (fileParts.Count() == 2)
                {
                    var thisLevelName = fileParts[1].Split(@"\").FirstOrDefault() ?? string.Empty;
                    var beamDir = Path.Join(Steam.GetBeamInstallDir(), Constants.BeamMapPath, thisLevelName);
                    var beamZip = beamDir + ".zip";
                    if (new FileInfo(beamZip).Exists && !thisLevelName.Equals(levelNameCopyFrom, StringComparison.InvariantCultureIgnoreCase))
                    {
                        var extractPath = fileParts[0];
                        var filePathEnd = fileParts[1];

                        // Check if we're looking for a .link file
                        if (sourceFile.EndsWith(".link", StringComparison.OrdinalIgnoreCase))
                        {
                            var destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                            if (destinationFilePath != null)
                            {
                                File.Copy(destinationFilePath, targetFile, true);
                                return;
                            }
                        }

                        //to Do: check if filepath has image extension, if not attach png
                        var imageextensions = new List<string> { ".dds", ".png", ".jpg", ".jpeg", ".link" };
                        if (!imageextensions.Any(x => filePathEnd.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                        {
                            filePathEnd = filePathEnd + ".png";
                        }

                        var destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        if (destinationFilePath2 == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd, ".dds");
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            // Try .link version of .dds
                            filePathEnd = filePathEnd + ".link";
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd.Replace(".link", ""), ".png");
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            // Try .link version of .png
                            filePathEnd = filePathEnd + ".link";
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd.Replace(".link", ""), ".jpg");
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            // Try .link version of .jpg
                            filePathEnd = filePathEnd + ".link";
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd.Replace(".link", ""), ".jpeg");
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 == null)
                        {
                            // Try .link version of .jpeg
                            filePathEnd = filePathEnd + ".link";
                            destinationFilePath2 = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath2 != null)
                        {
                            targetFile = Path.ChangeExtension(targetFile, Path.GetExtension(destinationFilePath2));
                            File.Copy(destinationFilePath2, targetFile, true);
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }
        }

        private string GetTargetFileName(string sourceName)
        {
            var fileName = Path.GetFileName(sourceName);
            var dir = Path.GetDirectoryName(sourceName);
            var targetParts = dir.ToLowerInvariant().Split($@"\levels\{levelNameCopyFrom}\".ToLowerInvariant());
            if (targetParts.Count() < 2)
            {
                //PubSubChannel.SendMessage(PubSubMessageType.Error, $"Filepath error in {sourceName}. Exception:no levels folder in path.");
                targetParts = dir.ToLowerInvariant().Split($@"\levels\".ToLowerInvariant());
                if (targetParts.Count() == 2)
                {
                    int pos = targetParts[1].IndexOf(@"\");
                    if (pos >= 0)
                    {
                        targetParts[1] = targetParts[1].Remove(0, pos);
                    }
                }
            }
            return Path.Join(namePath, targetParts.Last(), $"{Constants.MappingToolsPrefix}{levelNameCopyFrom}", fileName);
        }

        private string GetTerrainTargetFileName(string sourceName)
        {
            var fileName = Path.GetFileName(sourceName);
            // All terrain textures go directly to art/terrains folder
            return Path.Join(namePath, Constants.Terrains, fileName);
        }

        private string GetBeamNgJsonFileName(string windowsFileName)
        {
            var targetParts = windowsFileName.ToLowerInvariant().Split($@"\levels\".ToLowerInvariant());
            if (targetParts.Count() < 2)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Filepath error in {windowsFileName}. Exception:no levels folder in path.");
                return string.Empty;
            }
            return Path.ChangeExtension(Path.Join("levels", targetParts.Last()).Replace(@"\", "/"), null);
        }
    }
}
