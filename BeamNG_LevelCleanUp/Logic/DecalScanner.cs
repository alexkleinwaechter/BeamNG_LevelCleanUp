﻿using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;

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
            foreach (var file in _managedDecalData)
            {
                if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    SetMaterialsJson(file);
                }
                else
                {
                    SetMaterialsCs(file);
                }
            }
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
                JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(file.FullName), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    JsonElement instances = jsonObject.RootElement.GetProperty("instances");
                    var x = instances.EnumerateObject();
                    foreach (var instance in x)
                    {
                        //PubSubChannel.SendMessage(false, $"Scan Decal {instance.Name}", true);
                        _decalNames.Add(instance.Name);
                    }
                }
            }
        }

        internal void SetMaterialsJson(FileInfo file)
        {
            JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
            try
            {
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(file.FullName), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var managedDecalData in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var decalData = managedDecalData.Value.Deserialize<ManagedDecalData>(BeamJsonOptions.GetJsonSerializerOptions());
                            _materialNames.Add(decalData.Material);
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

        private void SetMaterialsCs(FileInfo file)
        {
            foreach (var decalName in _decalNames.Distinct())
            {
                var hit = false;
                foreach (string line in File.ReadAllLines(file.FullName))
                {
                    var search = $"({decalName})";
                    if (line.ToUpperInvariant().Contains(search.ToUpperInvariant()))
                    {
                        hit = true;
                    }
                    if (hit && line.ToUpperInvariant().Contains("material =", StringComparison.OrdinalIgnoreCase))
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
