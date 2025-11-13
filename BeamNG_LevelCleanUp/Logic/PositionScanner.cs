using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class PositionScanner
{
    private readonly decimal _xOffset;
    private readonly decimal _yOffset;
    private readonly decimal _zOffset;
    private List<string> _excludeFiles = new();

    internal PositionScanner(decimal xOffset, decimal yOffset, decimal zOffset, string missiongroupPath,
        List<string> excludeFiles)
    {
        _xOffset = xOffset;
        _yOffset = yOffset;
        _zOffset = zOffset;
        _missiongroupPath = missiongroupPath;
        _excludeFiles = excludeFiles;
    }

    private string _missiongroupPath { get; }

    internal void ScanMissionGroupFile()
    {
        var linecounter = 0;
        var modifiedJsonLines = new List<string>();
        var lines = File.ReadAllLines(_missiongroupPath);
        foreach (var line in lines)
        {
            linecounter++;
            try
            {
                using var doc = JsonUtils.GetValidJsonDocumentFromString(line, _missiongroupPath);
                if (doc.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(line))
                {
                    var mutableJson = new Dictionary<string, object>();

                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Name == "class" && (property.Value.GetString() == "TSStatic" ||
                                                         property.Value.GetString() == "Prefab"))
                            if (!doc.RootElement.TryGetProperty("position", out _))
                                mutableJson["position"] = new List<decimal> { _xOffset, _yOffset, _zOffset };

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
                                    if (values != null && values.Count >= 3)
                                    {
                                        values[0] += _xOffset;
                                        values[1] += _yOffset;
                                        values[2] += _zOffset;
                                    }

                                mutableJson[property.Name] = nodes;
                            }
                        }
                        else
                        {
                            mutableJson[property.Name] = property.Value;
                        }
                    }

                    modifiedJsonLines.Add(JsonSerializer.Serialize(mutableJson,
                        BeamJsonOptions.GetJsonSerializerOneLineOptions()));
                    //var asset = doc.RootElement.Deserialize<Asset>(BeamJsonOptions.GetJsonSerializerOptions());
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Error {_missiongroupPath}. {ex.Message}. jsonLine:{line}");
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
            using var doc = JsonUtils.GetValidJsonDocumentFromString(json, _missiongroupPath);
            if (doc.RootElement.ValueKind != JsonValueKind.Undefined && !string.IsNullOrEmpty(json))
            {
                var mutableJson = new Dictionary<string, object>();
                foreach (var property in doc.RootElement.EnumerateObject())
                    if (property.Name == "instances")
                    {
                        var updatedInstances = new Dictionary<string, List<List<decimal>>>();
                        foreach (var instance in property.Value.EnumerateObject())
                        {
                            var allvalues = instance.Value.Deserialize<List<List<decimal>>>();
                            if (allvalues != null && allvalues.Any())
                                foreach (var values in allvalues)
                                    if (values != null && values.Count >= 6)
                                    {
                                        values[3] += _xOffset;
                                        values[4] += _yOffset;
                                        values[5] += _zOffset;
                                    }

                            updatedInstances[instance.Name] = allvalues;
                        }

                        mutableJson[property.Name] = updatedInstances;
                    }
                    else
                    {
                        mutableJson[property.Name] = property.Value;
                    }

                File.WriteAllText(_missiongroupPath,
                    JsonSerializer.Serialize(mutableJson, BeamJsonOptions.GetJsonSerializerOneLineOptions()));
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error {_missiongroupPath}. {ex.Message}.");
        }
    }
}