using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BeamNG_LevelCleanUp.Logic
{
    public class ForestScanner
    {
        private string _missiongroupPath { get; set; }
        private string _levelPath { get; set; }
        private List<Asset> _assets = new List<Asset>();
        private List<FileInfo> _forestJsonFiles;
        private List<FileInfo> _managedItemData;
        private List<Tuple<string, string>> _forestTypeNames { get; set; } = new List<Tuple<string, string>>();
        private List<Tuple<string, string>> _shapeNames { get; set; } = new List<Tuple<string, string>>();

        public ForestScanner(List<Asset> assets, List<FileInfo> forestJsonFiles, List<FileInfo> managedItemData, string levelPath)
        {
            _assets = assets;
            _forestJsonFiles = forestJsonFiles;
            _managedItemData = managedItemData;
            _levelPath = levelPath;
        }

        public void ScanForest()
        {
            RetrieveUsedForestTypes();
            foreach (var file in _managedItemData)
            {
                if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    GetShapNamesJson(file);
                }
                else
                {
                    GetShapNamesCs(file);
                }
            }
            foreach (var shapeName in _shapeNames.Select(_ => _.Item1).Distinct())
            {
                AddAsset(new Asset
                {
                    Class = "TSStatic",
                    ShapeName = shapeName,
                });
            }
        }

        public List<Tuple<string, string>> GetForestTypes()
        {
            return _forestTypeNames.Distinct().ToList();
        }

        public List<Tuple<string, string>> GetShapNames()
        {
            return _shapeNames.Distinct().ToList();
        }

        private void AddAsset(Asset? asset)
        {
            //if (asset.ShapeName != null && asset.ShapeName.ToUpperInvariant().Contains("skyscraper_22".ToUpperInvariant())) Debugger.Break();
            if (!string.IsNullOrEmpty(asset?.ShapeName))
            {
                var daeScanner = new DaeScanner(_levelPath, asset.ShapeName);
                asset.DaeExists = daeScanner.Exists();
                if (asset.DaeExists.HasValue && asset.DaeExists.Value == true)
                {
                    asset.DaePath = daeScanner.ResolvedPath();
                    asset.MaterialsDae = daeScanner.GetMaterials();
                }
            }
            _assets.Add(asset);
        }

        private void RetrieveUsedForestTypes()
        {
            foreach (var file in _forestJsonFiles)
            {
                foreach (string line in File.ReadAllLines(file.FullName))
                {
                    JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
                    using JsonDocument jsonObject = JsonDocument.Parse(line, docOptions);
                    if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                    {
                        var asset = jsonObject.RootElement.Deserialize<Forest>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (!string.IsNullOrEmpty(asset.Type))
                        {
                            //PubSubChannel.SendMessage(false, $"Read Foresttype {asset.Type}", true);
                            _forestTypeNames.Add(Tuple.Create(asset.Type, file.FullName));
                        }
                    }
                }
            }
        }

        internal void GetShapNamesJson(FileInfo file)
        {
            JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
            try
            {
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(file.FullName), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var managedForestData in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var forestData = managedForestData.Value.Deserialize<ManagedForestData>(BeamJsonOptions.GetJsonSerializerOptions());
                            var name = forestData.ShapeFile;
                            if (name.StartsWith("./"))
                            {
                                name = name.Remove(0, 2);
                            }
                            if (name.Count(c => c == '/') == 0)
                            {
                                name = Path.Join(Path.GetDirectoryName(file.FullName), name);
                            }
                            _shapeNames.Add(Tuple.Create(name, file.FullName));
                        }
                        catch (Exception ex)
                        {
                            throw;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(true, $"Error DecalScanner {file.FullName}. {ex.Message}");
            }
        }
        private void GetShapNamesCs(FileInfo file)
        {
            foreach (var typeName in _forestTypeNames.Select(_ => _.Item1).Distinct())
            {
                var hit = false;
                foreach (string line in File.ReadLines(file.FullName))
                {
                    var search = $"({typeName})";
                    if (line.ToUpperInvariant().Contains(search.ToUpperInvariant()))
                    {
                        //if (line.Contains("FranklinDouglasTower15flr_var2", StringComparison.OrdinalIgnoreCase)) Debugger.Break();
                        hit = true;
                    }
                    if (hit && line.ToUpperInvariant().Contains("shapefile =", StringComparison.OrdinalIgnoreCase))
                    {
                        var nameParts = line.Split('"');
                        if (nameParts.Length > 1)
                        {
                            var name = nameParts[1];
                            if (name.StartsWith("./"))
                            {
                                name = name.Remove(0, 2);
                            }
                            if (name.Count(c => c == '/') == 0)
                            {
                                name = Path.Join(Path.GetDirectoryName(file.FullName), name);
                            }
                            _shapeNames.Add(Tuple.Create(name, file.FullName));
                            hit = false;
                        }
                    }
                }
            }
        }
    }
}
