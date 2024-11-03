using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class HeightScanner
    {
        private double _heightOffset;
        private string _missiongroupPath { get; set; }
        private List<string> _excludeFiles = new List<string>();

        internal HeightScanner(double heightOffset, string missiongroupPath, List<string> excludeFiles)
        {
            _heightOffset = heightOffset;
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
                        var mutableJson = new JsonObject();
                        bool lastLine = linecounter == lines.Length;
                        foreach (var property in doc.RootElement.EnumerateObject())
                        {
                            if (property.Name == "position")
                            {
                                var values = property.Value.Deserialize<List<double>>();
                                if (values != null && values.Count == 3)
                                {
                                    values[2] += _heightOffset;
                                    mutableJson.Add(property.Name, JsonSerializer.Serialize(values)); 
                                }
                            }
                            //else if (property.Name == "nodes")
                            //{
                            //    var values = property.Value.Deserialize<List<double>>();
                            //    mutableJson.Add(property.Name, JsonSerializer.Serialize(new[] { 4, 5, 6 })); // Example modification
                            //}
                            else
                            {
                                mutableJson.Add(property.Name, property.Value.ToString());
                            }
                        }
                        if (!lastLine)
                        {
                            modifiedJsonLines.Add(JsonSerializer.Serialize(mutableJson, BeamJsonOptions.GetJsonSerializerOneLineOptions()) + ",");
                        }
                        else
                        {
                            modifiedJsonLines.Add(JsonSerializer.Serialize(mutableJson, BeamJsonOptions.GetJsonSerializerOneLineOptions()));
                        }

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
    }
}
