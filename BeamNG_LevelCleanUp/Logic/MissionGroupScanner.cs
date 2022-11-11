﻿using BeamNG_LevelCleanUp.Communication;
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

        private string ResolvePath(string resourcePath)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return Path.Join(_levelPath, resourcePath.Replace(toReplaceDelim, delim));

            //char delim = '\\';
            //return string.Join(
            //    new string(delim, 1),
            //    _levelPath.Split(delim).Concat(_daePath.Split(delim)).Distinct().ToArray())
            //    .Replace("\\\\", "\\");
        }

        internal async Task ScanMissionGroupFile(CancellationToken token)
        {
            foreach (string line in await File.ReadAllLinesAsync(_missiongroupPath, token))
            {
                JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
                using JsonDocument jsonObject = JsonDocument.Parse(line, docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                {
                    try
                    {
                        var asset = jsonObject.RootElement.Deserialize<Asset>(BeamJsonOptions.Get());
                        PubSubChannel.SendMessage(false, $"Read MissionGroup of class {asset.Class}", true);
                        if (asset.Class == "Prefab" && !string.IsNullOrEmpty(asset.Filename))
                        {
                            AddPrefabDaeFiles(asset);
                            continue;
                        }
                        if (!string.IsNullOrEmpty(asset.GlobalEnviromentMap))
                        {
                            asset.Material = asset.GlobalEnviromentMap;
                        }
                        if (!string.IsNullOrEmpty(asset.Texture))
                        {
                            _excludeFiles.Add(ResolvePath(asset.Texture));
                        }
                        if (!string.IsNullOrEmpty(asset.Cubemap))
                        {
                            asset.Material = asset.Cubemap;
                        }
                        if (!string.IsNullOrEmpty(asset.FoamTex))
                        {
                            _excludeFiles.Add(ResolvePath(asset.FoamTex));
                        }
                        if (!string.IsNullOrEmpty(asset.RippleTex))
                        {
                            _excludeFiles.Add(ResolvePath(asset.RippleTex));
                        }
                        if (!string.IsNullOrEmpty(asset.DepthGradientTex))
                        {
                            _excludeFiles.Add(ResolvePath(asset.DepthGradientTex));
                        }
                        AddAsset(asset);
                    }
                    catch (Exception ex)
                    {
                    }
                }
            }
        }

        private void AddAsset(Asset? asset)
        {
            //if (asset.ShapeName != null && asset.ShapeName.Equals("/levels/east_coast_rework/art/shapes/rails/track_straight_long.dae", StringComparison.InvariantCultureIgnoreCase)) Debugger.Break();
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

        private void AddPrefabDaeFiles(Asset currentPrefabMissionGroupAsset)
        {
            var file = new FileInfo(ResolvePath(currentPrefabMissionGroupAsset.Filename));
            if (file.Exists)
            {
                var shapeNames = GetShapeNames(file);
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
                }
            }
        }

        private List<string> GetShapeNames(FileInfo file)
        {
            List<string> shapeNames = new List<string>();
            foreach (string line in File.ReadLines(file.FullName))
            {
                if (line.ToLowerInvariant().Contains("shapename ="))
                {
                    var nameParts = line.Split('"');
                    if (nameParts.Length > 1)
                    {
                        shapeNames.Add(nameParts[1]);
                    }
                }
            }
            return shapeNames;
        }
    }
}
