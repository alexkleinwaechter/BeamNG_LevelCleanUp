using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class MissionGroupScanner
    {
        private string _missiongroupPath { get; set; }
        private string _levelPath { get; set; }
        private List<Asset> _assets = new List<Asset>();
        private List<string> _excludeFiles = new List<string>();
        internal MissionGroupScanner(string missiongroupPath, string levelPath, List<Asset> assets, List<string> excludeFiles)
        {
            _missiongroupPath = missiongroupPath;
            _assets = assets;
            _levelPath = levelPath;
            _excludeFiles = excludeFiles;
        }

        internal void ScanMissionGroupFile()
        {
            foreach (string line in File.ReadAllLines(_missiongroupPath))
            {
                JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
                try
                {
                    using JsonDocument jsonObject = JsonDocument.Parse(line, docOptions);
                    if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                    {
                        var asset = jsonObject.RootElement.Deserialize<Asset>(BeamJsonOptions.Get());
                        PubSubChannel.SendMessage(false, $"Read MissionGroup of class {asset.Class}", true);
                        if (asset.Class == "Prefab" && !string.IsNullOrEmpty(asset.Filename))
                        {
                            //if (asset.Filename.Contains("turbine_blades")) Debugger.Break();
                            var prefabScanner = new PrefabScanner(_assets, _levelPath);
                            prefabScanner.AddPrefabDaeFiles(asset.Filename);
                            continue;
                        }
                        if (!string.IsNullOrEmpty(asset.GlobalEnviromentMap))
                        {
                            asset.Material = asset.GlobalEnviromentMap;
                        }
                        if (!string.IsNullOrEmpty(asset.Texture))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.Texture, false));
                        }
                        if (!string.IsNullOrEmpty(asset.Cubemap))
                        {
                            asset.Material = asset.Cubemap;
                        }
                        if (!string.IsNullOrEmpty(asset.FoamTex))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.FoamTex, false));
                        }
                        if (!string.IsNullOrEmpty(asset.RippleTex))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.RippleTex, false));
                        }
                        if (!string.IsNullOrEmpty(asset.DepthGradientTex))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.DepthGradientTex, false));
                        }
                        AddAsset(asset);
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(true, $"Error {_missiongroupPath}. {ex.Message}. jsonLine:{line}");

                }
            }
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
