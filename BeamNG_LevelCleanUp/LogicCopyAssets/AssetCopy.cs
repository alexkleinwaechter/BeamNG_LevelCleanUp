﻿using BeamNG_LevelCleanUp.Communication;
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

                        //to Do: check if filepath has image extension, if not attach png
                        var imageextensions = new List<string> { ".dds", ".png", ".jpg", ".jpeg" };
                        if (!imageextensions.Any(x => filePathEnd.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                        {
                            filePathEnd = filePathEnd + ".png";
                        }

                        var destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        if (destinationFilePath == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd, ".dds");
                            destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd, ".png");
                            destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd, ".jpg");
                            destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath == null)
                        {
                            filePathEnd = Path.ChangeExtension(filePathEnd, ".jpeg");
                            destinationFilePath = ZipReader.ExtractFile(beamZip, extractPath, filePathEnd);
                        }

                        if (destinationFilePath != null)
                        {
                            targetFile = Path.ChangeExtension(targetFile, Path.GetExtension(destinationFilePath));
                            File.Copy(destinationFilePath, targetFile, true);
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
