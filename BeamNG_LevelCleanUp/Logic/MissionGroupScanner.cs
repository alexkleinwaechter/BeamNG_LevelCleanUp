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
        internal MissionGroupScanner(string missiongroupPath, string levelPath, List<Asset> assets)
        {
            _missiongroupPath = missiongroupPath;
            _assets = assets;
            _levelPath = levelPath;
        }


        internal void ScanMissionGroupFile()
        {
            foreach (string line in File.ReadLines(_missiongroupPath))
            {
                JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
                var jsonObject = JsonDocument.Parse(line, docOptions).RootElement;
                if (jsonObject.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                {
                    try
                    {
                        var asset = jsonObject.Deserialize<Asset>(BeamJsonOptions.Get());
                        if (asset.ShapeName != null && asset.ShapeName.Equals("/levels/ellern_map/art/shapes/custom/gas_station_petronas_c/gas_station_petronas.dae", StringComparison.InvariantCultureIgnoreCase)) Debugger.Break();
                        if (!string.IsNullOrEmpty(asset?.ShapeName)) {
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
                    catch (Exception ex)
                    {
                    }
                }
            }
        }
    }
}
