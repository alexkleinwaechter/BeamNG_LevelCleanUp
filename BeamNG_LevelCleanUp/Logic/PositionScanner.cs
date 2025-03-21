using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Automation.Peers;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class PositionScanner
    {
        private decimal _xOffset;
        private decimal _yOffset;
        private decimal _zOffset;
        private string _missiongroupPath { get; set; }
        private List<string> _excludeFiles = new List<string>();

        internal PositionScanner(decimal xOffset, decimal yOffset, decimal zOffset, string missiongroupPath, List<string> excludeFiles)
        {
            _xOffset = xOffset;
            _yOffset = yOffset;
            _zOffset = zOffset;
            _missiongroupPath = missiongroupPath;
            _excludeFiles = excludeFiles;
        }

        internal void ScanMissionGroupFile()
        {
            var linecounter = 0;
            List<string> modifiedJsonLines = new List<string>();
            var lines = File.ReadAllLines(_missiongroupPath);
            foreach (string line in lines)
            {
                linecounter++;
                try
                {
                    using JsonDocument doc = JsonUtils.GetValidJsonDocumentFromString(line, _missiongroupPath);
                    if (doc.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                    {
                        
                        var mutableJson = new Dictionary<string, object>();

                        foreach (var property in doc.RootElement.EnumerateObject())
                        {
                            if (property.Name == "class" && (property.Value.GetString() == "TSStatic" || property.Value.GetString() == "Prefab"))
                            {
                                if (!doc.RootElement.TryGetProperty("position", out _))
                                {
                                    mutableJson["position"] = new List<decimal> { _xOffset, _yOffset, _zOffset };
                                }
                            }

                            if (property.Name == "position" || property.Name == "pos")
                            {
                                var values = property.Value.Deserialize<List<decimal>>();
                                if (values != null && values.Count == 3)
                                {
                                    values[0] += _xOffset;
                                    values[1] += _yOffset;
                                    values[2] += _zOffset;
                                    mutableJson[property.Name] = values;
                                }
                            }
                            else if (property.Name == "nodes")
                            {
                                var nodes = property.Value.Deserialize<List<List<decimal>>>();
                                if (nodes != null && nodes.Any())
                                {
                                    foreach (var values in nodes)
                                    {
                                        if (values != null && values.Count >= 3)
                                        {
                                            values[0] += _xOffset;
                                            values[1] += _yOffset;
                                            values[2] += _zOffset;
                                        }
                                    }

                                    mutableJson[property.Name] = nodes;
                                }
                            }
                            else
                            {
                                mutableJson[property.Name] = property.Value;
                            }
                        }

                        modifiedJsonLines.Add(JsonSerializer.Serialize(mutableJson, BeamJsonOptions.GetJsonSerializerOneLineOptions()));
                        //var asset = doc.RootElement.Deserialize<Asset>(BeamJsonOptions.GetJsonSerializerOptions());
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {_missiongroupPath}. {ex.Message}. jsonLine:{line}");

                }
            }
            try
            {
                File.WriteAllLines(_missiongroupPath, modifiedJsonLines);
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {_missiongroupPath}. {ex.Message}.");
            }
        }
        internal void ScanDecals()
        {
            var json = File.ReadAllText(_missiongroupPath);
            try
            {
                using JsonDocument doc = JsonUtils.GetValidJsonDocumentFromString(json, _missiongroupPath);
                if (doc.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(json))
                {
                    var mutableJson = new Dictionary<string, object>();
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Name == "instances")
                        {
                            var updatedInstances = new Dictionary<string, List<List<decimal>>>();
                            foreach (var instance in property.Value.EnumerateObject())
                            {
                                var allvalues = instance.Value.Deserialize<List<List<decimal>>>();
                                if (allvalues != null && allvalues.Any())
                                {
                                    foreach (var values in allvalues)
                                    {
                                        if (values != null && values.Count >= 6)
                                        {
                                            values[3] += _xOffset;
                                            values[4] += _yOffset;
                                            values[5] += _zOffset;
                                        }
                                    }
                                }
                                updatedInstances[instance.Name] = allvalues;
                            }
                            mutableJson[property.Name] = updatedInstances;
                        }
                        else
                        {
                            mutableJson[property.Name] = property.Value;
                        }
                    }

                    File.WriteAllText(_missiongroupPath, JsonSerializer.Serialize(mutableJson, BeamJsonOptions.GetJsonSerializerOneLineOptions()));
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {_missiongroupPath}. {ex.Message}.");

            }
        }
    }
}
