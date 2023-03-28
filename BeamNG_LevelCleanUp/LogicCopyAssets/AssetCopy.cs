using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    internal class AssetCopy
    {
        private List<Guid> _identifier { get; set; }
        private List<CopyAsset> _assetsToCopy = new List<CopyAsset>();
        private string namePath;
        private string levelName;
        private string levelNameCopyFrom;

        internal AssetCopy(List<Guid> identifier, List<CopyAsset> copyAssetList)
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

        internal void Copy()
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
            }

            PubSubChannel.SendMessage(false, $"Done! Assets copied.");
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
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            var targetJsonFile = new FileInfo(targetJsonPath);
            if (!targetJsonFile.Exists)
            {
                var jsonObject = new JsonObject(
                new[]
                    {
                          KeyValuePair.Create<string, JsonNode?>(item.DecalData.Name, JsonNode.Parse(JsonSerializer.Serialize(item.DecalData,BeamJsonOptions.Get()))),
                    }
                );
                File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.Get()));
            }
            else
            {
                var targetJsonNode = JsonNode.Parse(File.ReadAllText(targetJsonFile.FullName), null, docOptions);
                if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == item.DecalData.Name))
                {
                    targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(item.DecalData.Name, JsonNode.Parse(JsonSerializer.Serialize(item.DecalData, BeamJsonOptions.Get()))));
                }
                File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.Get()));
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
                File.Copy(item.DaeFilePath, daeFullName, true);
                //Path.ChangeExtension(daeFullName, ".CDAE")
                try
                {
                    File.Copy(Path.ChangeExtension(item.DaeFilePath, ".cdae"), Path.ChangeExtension(daeFullName, ".cdae"), true);
                }
                catch (Exception ex)
                {
                    //ignore
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(true, $"Filepath error for daefile {daeFullName}. Exception:{ex.Message}");
            }

            CopyMaterials(item);
        }

        private void CopyMaterials(CopyAsset item)
        {
            if (item.TargetPath == null)
            {
                return;
            }
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            Directory.CreateDirectory(item.TargetPath);
            foreach (var material in item.Materials)
            {
                var sourceJsonNode = JsonNode.Parse(File.ReadAllText(material.MatJsonFileLocation), null, docOptions);
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
                        File.Copy(matFile.File.FullName, targetFullName, true);
                    }
                    catch (Exception ex)
                    {
                        PubSubChannel.SendMessage(true, $"Filepath error for material {material.Name}. Exception:{ex.Message}");
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
                    File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.Get()));
                }
                else
                {
                    var targetJsonNode = JsonNode.Parse(File.ReadAllText(targetJsonFile.FullName), null, docOptions);
                    if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == material.Name))
                    {
                        try
                        {
                            targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(material.Name, JsonNode.Parse(toText)));
                        }
                        catch (Exception ex)
                        {
                            throw;
                        }
                    }
                    File.WriteAllText(targetJsonFile.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.Get()));
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
                PubSubChannel.SendMessage(true, $"Filepath error in {sourceName}. Exception:no levels folder in path.");
                return string.Empty;
            }
            return Path.Join(namePath, targetParts.Last(), $"{Constants.MappingToolsPrefix}{levelNameCopyFrom}", fileName);
        }

        private string GetBeamNgJsonFileName(string windowsFileName)
        {
            var targetParts = windowsFileName.ToLowerInvariant().Split($@"\levels\".ToLowerInvariant());
            if (targetParts.Count() < 2)
            {
                PubSubChannel.SendMessage(true, $"Filepath error in {windowsFileName}. Exception:no levels folder in path.");
                return string.Empty;
            }
            return Path.ChangeExtension(Path.Join("levels", targetParts.Last()).Replace(@"\", "/"), null);
        }
    }
}
