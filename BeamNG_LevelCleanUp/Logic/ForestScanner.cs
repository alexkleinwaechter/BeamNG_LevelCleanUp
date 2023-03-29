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
        private List<string> _forestTypeNames { get; set; } = new List<string>();
        private List<string> _shapeNames { get; set; } = new List<string>();

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
            foreach (var shapeName in _shapeNames.Distinct())
            {
                AddAsset(new Asset
                {
                    Class = "TSStatic",
                    ShapeName = shapeName,
                });
            }
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
                            _forestTypeNames.Add(asset.Type);
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
                            _shapeNames.Add(name);
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
            foreach (var typeName in _forestTypeNames.Distinct())
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
                            _shapeNames.Add(name);
                            hit = false;
                        }
                    }
                }
            }
        }
    }
}
