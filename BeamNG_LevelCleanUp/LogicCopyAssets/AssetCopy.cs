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
                    default:
                        break;
                }
            }

            PubSubChannel.SendMessage(false, $"Done! Assets copied.");
        }

        private void CopyRoad(CopyAsset item)
        {
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            var sourceJsonNode = JsonNode.Parse(File.ReadAllText(item.SourceMaterialJsonPath), null, docOptions);
            Directory.CreateDirectory(item.TargetPath);
            foreach (var material in item.Materials)
            {
                var sourceMaterialNode = sourceJsonNode.AsObject().First(x => x.Value["name"]?.ToString() == material.Name);
                var toText = sourceMaterialNode.Value.ToJsonString();
                var targetJsonPath = Path.Join(item.TargetPath, Path.GetFileName(item.SourceMaterialJsonPath));
                var targetJsonFile = new FileInfo(targetJsonPath);
                foreach (var matFile in material.MaterialFiles)
                {
                    var targetFullName = GetTargetFileName(matFile.File.FullName);
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
                          KeyValuePair.Create<string, JsonNode?>(item.Name, JsonNode.Parse(toText)),
                        }
                    );
                    File.WriteAllText(targetJsonFile.FullName, jsonObject.ToJsonString(BeamJsonOptions.Get()));
                }
                else
                {
                    var targetJsonNode = JsonNode.Parse(File.ReadAllText(targetJsonFile.FullName), null, docOptions);
                    if (!targetJsonNode.AsObject().Any(x => x.Value["name"]?.ToString() == material.Name))
                    {
                        targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(item.Name, JsonNode.Parse(toText)));
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
            return Path.ChangeExtension(Path.Join("levels", targetParts.Last()).Replace(@"\","/"), null);
        }
    }
}
