using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    public class DecalScanner
    {
        private string _missiongroupPath { get; set; }
        private string _levelPath { get; set; }
        private List<Asset> _assets = new List<Asset>();
        private List<FileInfo> _mainDecalsJson;
        private List<FileInfo> _managedDecalData;
        private List<string> _decalNames { get; set; } = new List<string>();
        private List<string> _materialNames { get; set; } = new List<string>();

        public DecalScanner(List<Asset> assets, List<FileInfo> mainDecalsJson, List<FileInfo> managedDecalData)
        {
            _assets = assets;
            _mainDecalsJson = mainDecalsJson;
            _managedDecalData = managedDecalData;
        }

        public void ScanDecals()
        {
            RetrieveUsedDecalNames();
            SetMaterials();
            foreach (var name in _materialNames.Distinct())
            {
                _assets.Add(new Asset
                {
                    Class = "Decal",
                    Material = name,
                });
            }
        }

        private void RetrieveUsedDecalNames()
        {
            foreach (var file in _mainDecalsJson)
            {
                JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
                var jsonObject = JsonDocument.Parse(File.ReadAllText(file.FullName), docOptions).RootElement;
                if (jsonObject.ValueKind != JsonValueKind.Undefined)
                {
                    JsonElement instances = jsonObject.GetProperty("instances");
                    var x = instances.EnumerateObject();
                    foreach (var instance in x)
                    {
                        _decalNames.Add(instance.Name);
                    }
                }
            }
        }

        private void SetMaterials()
        {
            foreach (var file in _managedDecalData)
            {
                foreach (var decalName in _decalNames.Distinct())
                {
                    var hit = false;
                    foreach (string line in File.ReadLines(file.FullName))
                    {
                        var search = $"({decalName})";
                        if (line.ToLowerInvariant().Contains(search.ToLowerInvariant()))
                        {
                            hit = true;
                        }
                        if (hit && line.ToLowerInvariant().Contains("material ="))
                        {
                            var nameParts = line.Split('"');
                            if (nameParts.Length > 1)
                            {
                                _materialNames.Add(nameParts[1]);
                                break;
                            }
                        }
                    }
                }
            }
        }
    }
}
