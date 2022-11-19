using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class PrefabScanner
    {
        private List<Asset> _assets = new List<Asset>();
        private string _levelPath { get; set; }
        internal PrefabScanner(List<Asset> assets, string levelPath)
        {
            _assets = assets;
            _levelPath = levelPath;
        }

        internal void AddPrefabDaeFiles(string prefabFileName)
        {
            var file = new FileInfo(PathResolver.ResolvePath(_levelPath, prefabFileName, false));
            AddPrefabDaeFiles(file);
        }
        internal void AddPrefabDaeFiles(FileInfo file)
        {
            var shapeNames = new List<string>();
            if (file.Exists)
            {
                shapeNames = file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? GetShapeNamesJson(file) : GetShapeNamesCs(file);
                var counter = 0;
                foreach (var shapeName in shapeNames)
                {
                    counter++;
                    var asset = new Asset
                    {
                        Name = $"{file.Name}_{counter}",
                        Class = "TSStatic",
                        ShapeName = shapeName
                    };
                    AddAsset(asset);
                    PubSubChannel.SendMessage(false, $"Read Prefab asset {asset.Name}", true);
                }
            }
        }

        private List<string> GetShapeNamesCs(FileInfo file)
        {
            List<string> shapeNames = new List<string>();
            foreach (string line in File.ReadLines(file.FullName))
            {
                if (line.ToUpperInvariant().Contains("shapename ="))
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
                            name = Path.Join(file.Directory.FullName, name);
                        }
                        shapeNames.Add(name);
                    }
                }
                if (line.ToUpperInvariant().Contains("material ="))
                {
                    var nameParts = line.Split('"');
                    if (nameParts.Length > 1)
                    {
                        _assets.Add(new Asset
                        {
                            Class = "Decal",
                            Material = nameParts[1],
                        });
                    }
                }
            }
            return shapeNames;
        }

        private List<string> GetShapeNamesJson(FileInfo file)
        {
            List<string> shapeNames = new List<string>();
            foreach (string line in File.ReadAllLines(file.FullName))
            {
                JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
                try
                {
                    using JsonDocument jsonObject = JsonDocument.Parse(line, docOptions);
                    if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                    {
                        var asset = jsonObject.RootElement.Deserialize<Asset>(BeamJsonOptions.Get());
                        if (!string.IsNullOrEmpty(asset.ShapeName))
                        {
                            shapeNames.Add(asset.ShapeName);
                        }
                        if (!string.IsNullOrEmpty(asset.Material))
                        {
                            _assets.Add(asset);
                        }
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(true, $"Error {file.FullName}. {ex.Message}.");
                }
            }
            return shapeNames;
        }
        private void AddAsset(Asset? asset)
        {
            //if (asset.ShapeName != null && asset.ShapeName.Equals("/levels/east_coast_rework/art/shapes/rails/track_straight_long.dae", StringComparison.OrdinalIgnoreCase)) Debugger.Break();
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
    }
}
