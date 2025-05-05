using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MaterialScanner
    {
        private string _matJsonPath { get; set; }
        private string _levelPath { get; set; }
        private string _namePath { get; set; }
        private List<MaterialJson> _materials = new List<MaterialJson>();
        private List<Asset> _assets = new List<Asset>();
        private List<string> _excludeFiles = new List<string>();

        internal MaterialScanner(string matJsonPath, string levelPath, string namePath, List<MaterialJson> materials, List<Asset> assets, List<string> excludeFiles)
        {
            _matJsonPath = matJsonPath;
            _materials = materials;
            _levelPath = levelPath;
            _namePath = namePath;
            _excludeFiles = excludeFiles;
            _assets = assets;
        }

        public MaterialScanner(List<MaterialJson> materials, string levelPath, string namePath)
        {
            _materials = materials;
            _levelPath = levelPath;
            _namePath = namePath;
        }

        internal void ScanMaterialsJsonFile()
        {
            try
            {
                using JsonDocument jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_matJsonPath);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var child in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var material = child.Value.Deserialize<MaterialJson>(BeamJsonOptions.GetJsonSerializerOptions());
                            material.MatJsonFileLocation = _matJsonPath;

                            //if (material.Name == "curbs_track") Debugger.Break();

                            if (string.IsNullOrEmpty(material.Name) && !string.IsNullOrEmpty(material.InternalName))
                            {
                                material.Name = material.InternalName;
                            }
                            if (material?.Stages != null)
                            {
                                var fileScanner = new MaterialFileScanner(_levelPath, material.Stages, _matJsonPath);
                                material.MaterialFiles = fileScanner.GetMaterialFiles(material.Name);
                            }
                            else
                            {
                                var stage = child.Value.Deserialize<MaterialStage>(BeamJsonOptions.GetJsonSerializerOptions());
                                if (stage != null)
                                {
                                    material.Stages = new List<MaterialStage>
                                    {
                                        stage
                                    };
                                    var fileScanner = new MaterialFileScanner(_levelPath, material.Stages, _matJsonPath);
                                    material.MaterialFiles = fileScanner.GetMaterialFiles(material.Name);
                                }
                            }
                            if (material?.CubeFace != null && material?.CubeFace.Count > 0)
                            {
                                foreach (var cf in material.CubeFace)
                                {
                                    var fi = new FileInfo(PathResolver.ResolvePath(_levelPath, cf, false));
                                    if (!fi.Exists)
                                    {
                                        fi = FileUtils.ResolveImageFileName(fi.FullName);
                                    }
                                    material.MaterialFiles.Add(new MaterialFile
                                    {
                                        File = fi,
                                        MapType = "cubeFace",
                                        Missing = !fi.Exists
                                    });
                                }
                            }
                            if (!string.IsNullOrEmpty(material.Cubemap))
                            {
                                _assets.Add(new Asset
                                {
                                    Class = "Decal",
                                    Cubemap = material.Cubemap,
                                });
                            }
                            //PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read Material {material.Name}", true);
                            //todo: Sascha debuggen mit shrinker!!
                            _materials.Add(material);

                            var temp = child.Value.EnumerateObject().ToList();
                            foreach (var item in temp)
                            {
                                if (item.Name.EndsWith("Tex"))
                                {
                                    _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, item.Value.GetString(), false));
                                }
                            }

                        }
                        catch (Exception)
                        {
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {_matJsonPath}. {ex.Message}");
            }
        }

        internal void RemoveDuplicates(bool fromJsonFiles)
        {
            var duplicateMaterials = _materials.GroupBy(x => x.Name).ToList();
            var toDelete = new List<MaterialJson>();
            foreach (var duplicate in duplicateMaterials)
            {
                //if (duplicate.Key == "boom_lift") Debugger.Break();
                if (duplicate.Count() > 1)
                {
                    var duplicates = duplicate.OrderByDescending(x => x.MaterialFiles.Count(c => c.Missing == false)).ToList();
                    var first = true;
                    if (duplicates.First().MaterialFiles.Count(c => c.Missing == false) == 0) continue;
                    duplicates.ForEach(x =>
                    {
                        if (!first)
                        {
                            try
                            {
                                toDelete.Add(x);
                                if (fromJsonFiles)
                                {
                                    var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(x.MatJsonFileLocation);
                                    targetJsonNode.AsObject().Remove(x.Name);
                                    File.WriteAllText(x.MatJsonFileLocation, targetJsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
                                }
                            }
                            catch (Exception ex)
                            {
                                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error deleting duplicating material {x.Name} from json {x.MatJsonFileLocation}: {ex.Message}", true);
                            }
                        }
                        first = false;
                    }
                    );
                }
            }
            _materials.RemoveAll(x => toDelete.Contains(x));
        }

        internal void CheckDuplicates(List<MaterialJson> sourceMaterials)
        {
            foreach (var item in _materials)
            {
                var duplicates = sourceMaterials.Where(x => x.Name.Equals(item.Name));
                if (duplicates.Any())
                {
                    item.IsDuplicate = true;
                    item.DuplicateFoundLocation.AddRange(duplicates.Select(x => x.MatJsonFileLocation));
                }
            }
        }
    }
}
