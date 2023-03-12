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
                        //PubSubChannel.SendMessage(false, $"Read MissionGroup of class {asset.Class}", true);
                        if (asset.Class == "Prefab" && !string.IsNullOrEmpty(asset.Filename))
                        {
                            //if (asset.Filename.Contains("turbine_blades")) Debugger.Break();
                            var prefabScanner = new PrefabScanner(_assets, _levelPath);
                            prefabScanner.AddPrefabDaeFiles(asset.Filename);
                            continue;
                        }
                        if (!string.IsNullOrEmpty(asset.Texture))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.Texture, false));
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
                        if (!string.IsNullOrEmpty(asset.ColorizeGradientFile))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.ColorizeGradientFile, false));
                        }
                        if (!string.IsNullOrEmpty(asset.AmbientScaleGradientFile))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.AmbientScaleGradientFile, false));
                        }
                        if (!string.IsNullOrEmpty(asset.FogScaleGradientFile))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.FogScaleGradientFile, false));
                        }
                        if (!string.IsNullOrEmpty(asset.NightFogGradientFile))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.NightFogGradientFile, false));
                        }
                        if (!string.IsNullOrEmpty(asset.NightGradientFile))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.NightGradientFile, false));
                        }
                        if (!string.IsNullOrEmpty(asset.SunScaleGradientFile))
                        {
                            _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, asset.SunScaleGradientFile, false));
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
            //if (asset.Types.Any()) Debugger.Break();
            if (!string.IsNullOrEmpty(asset?.ShapeName))
            {
                ScanDae(asset, asset?.ShapeName);
            }
            foreach (var assetType in asset?.Types)
            {
                if (!string.IsNullOrEmpty(assetType.ShapeFilename))
                {
                    var newAsset = new Asset();
                    newAsset.Name = "AssetType";
                    ScanDae(newAsset, assetType.ShapeFilename);
                    _assets.Add(newAsset);
                }
            }
            _assets.Add(asset);
        }

        private void ScanDae(Asset? asset, string shapeName)
        {
            var daeScanner = new DaeScanner(_levelPath, shapeName);
            asset.DaeExists = daeScanner.Exists();
            if (asset.DaeExists.HasValue && asset.DaeExists.Value == true)
            {
                asset.DaePath = daeScanner.ResolvedPath();
                asset.MaterialsDae = daeScanner.GetMaterials();
            }
        }
    }
}
