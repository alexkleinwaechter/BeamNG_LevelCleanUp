using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
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
                        if (!string.IsNullOrEmpty(asset?.ShapeName)) {
                            var daeScanner = new DaeScanner(_levelPath, asset.ShapeName);
                            asset.MaterialsDae = daeScanner.GetMaterials();
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
