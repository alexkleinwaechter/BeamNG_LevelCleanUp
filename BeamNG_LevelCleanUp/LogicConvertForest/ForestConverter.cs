using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BeamNG_LevelCleanUp.LogicConvertForest
{
    public class ForestConverter
    {
        private readonly List<Asset> _assetsToConvert;
        private List<ForestInfo> _forestInfoList = new List<ForestInfo>();
        private readonly string _namePath;
        private readonly string _forestManagedItemsRealtivePath = "art/forest/managedItemData.json";
        private readonly string _forestItemRealtivePath = "forest";
        private FileInfo _forestManagedItemsFileInfo;
        private JsonDocumentOptions _docOptions = BeamJsonOptions.GetJsonDocumentOptions();

        public ForestConverter(List<Asset> assetsToConvert,
            List<ForestInfo> forestInfoList, string namePath)
        {
            _assetsToConvert = assetsToConvert.OrderBy(o => o.DaePath).ToList();
            _forestInfoList = forestInfoList.OrderBy(o => o.DaePath).ToList();
            _namePath = namePath;
            var forestManagedItemItemPath = PathResolver.ResolvePath(_namePath, _forestManagedItemsRealtivePath, false);
            _forestManagedItemsFileInfo = new FileInfo(forestManagedItemItemPath);
        }

        public void Convert()
        {
            foreach (var item in _assetsToConvert)
            {
                _forestManagedItemsFileInfo = new FileInfo(_forestManagedItemsFileInfo.FullName);
                var existsAsForest = _forestInfoList.Where(a => a.DaePath != null && a.DaePath.Equals(item.DaePath, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (existsAsForest == null)
                {
                    AddForestType(item);
                    existsAsForest = _forestInfoList.Where(a => a.DaePath != null && a.DaePath.Equals(item.DaePath, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
                }
                AddForestItem(item, existsAsForest);
            }
        }

        private void AddForestItem(Asset asset, ForestInfo forestInfo)
        {
            var forestItem = new Forest
            {
                type = forestInfo.ForestTypeName,
                pos = asset.Position,
                rotationMatrix = asset.RotationMatrix,
                scale = asset.Scale == null? 1 : asset.Scale.First(),
            };

            var jsonString = JsonSerializer.SerializeToNode(forestItem)?
                .ToJsonString(BeamJsonOptions.GetJsonSerializerOneLineOptions());

            if (!forestInfo.UsedInFiles.Any())
            {
                var fileName = Path.Join(_namePath, _forestItemRealtivePath, forestInfo.ForestTypeName + ".forest4.json");
                Directory.CreateDirectory(Directory.GetParent(fileName).FullName);
                File.WriteAllText(fileName, jsonString);
                forestInfo.UsedInFiles.Add(fileName);
            }
            else
            {
                var fileName = forestInfo.UsedInFiles.First();
                File.AppendAllText(fileName, "\r\n" + jsonString);
            }
        }

        private void AddForestType(Asset asset)
        {
            var forestType = new ManagedForestData
            {
                name = Path.GetFileNameWithoutExtension(asset.DaePath),
                internalName = Path.GetFileNameWithoutExtension(asset.DaePath),
                persistentId = Guid.NewGuid().ToString(),
                @class = "TSForestItemData",
                shapeFile = asset.ShapeName,
                translucentBlendOp = asset.TranslucentBlendOp,
            };

            if (!_forestManagedItemsFileInfo.Exists)
            {
                var jsonObject = new JsonObject(
                   new[]
                       {
                              KeyValuePair.Create<string, JsonNode?>(forestType.internalName, JsonSerializer.SerializeToNode(forestType))
                       }
               );
                Directory.CreateDirectory(Directory.GetParent(_forestManagedItemsFileInfo.FullName).FullName);
                File.WriteAllText(_forestManagedItemsFileInfo.FullName, jsonObject.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
            else
            {
                var targetJsonNode = JsonNode.Parse(File.ReadAllText(_forestManagedItemsFileInfo.FullName), null, _docOptions);
                if (!targetJsonNode.AsObject().Any(x => x.Value["internalName"]?.ToString() == forestType.internalName))
                {
                    try
                    {
                        targetJsonNode.AsObject().Add(KeyValuePair.Create<string, JsonNode?>(forestType.internalName, JsonSerializer.SerializeToNode(forestType)));
                        File.WriteAllText(_forestManagedItemsFileInfo.FullName, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
                    }
                    catch (Exception ex)
                    {
                        throw;
                    }
                }
            }

            var forestInfo = new ForestInfo
            {
                DaePath = asset.DaePath,
                ForestTypeName = forestType.internalName,
                FileOrigin = _forestManagedItemsFileInfo.FullName,
                UsedInFiles = new List<string>(),
            };
            _forestInfoList.Add(forestInfo);
        }
    }
}
