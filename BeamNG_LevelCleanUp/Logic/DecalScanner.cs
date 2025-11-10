using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

public class DecalScanner
{
    private readonly List<Asset> _assets = new();
    private readonly List<FileInfo> _mainDecalsJson;
    private readonly List<FileInfo> _managedDecalData;

    public DecalScanner(List<Asset> assets, List<FileInfo> mainDecalsJson, List<FileInfo> managedDecalData)
    {
        _assets = assets;
        _mainDecalsJson = mainDecalsJson;
        _managedDecalData = managedDecalData;
    }

    private string _missiongroupPath { get; set; }
    private string _levelPath { get; set; }
    private List<string> _decalNames { get; } = new();
    private List<string> _materialNames { get; } = new();

    public void ScanDecals()
    {
        RetrieveUsedDecalNames();
        foreach (var file in _managedDecalData)
            if (file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                SetMaterialsJson(file);
            else
                SetMaterialsCs(file);

        foreach (var name in _materialNames.Distinct())
            _assets.Add(new Asset
            {
                Class = "Decal",
                Material = name
            });
    }

    private void RetrieveUsedDecalNames()
    {
        foreach (var file in _mainDecalsJson)
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(file.FullName);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
            {
                var instances = jsonObject.RootElement.GetProperty("instances");
                var x = instances.EnumerateObject();
                foreach (var instance in x)
                    //PubSubChannel.SendMessage(PubSubMessageType.Info, $"Scan Decal {instance.Name}", true);
                    _decalNames.Add(instance.Name);
            }
        }
    }

    internal void SetMaterialsJson(FileInfo file)
    {
        try
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(file.FullName);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                foreach (var managedDecalData in jsonObject.RootElement.EnumerateObject())
                {
                    var decalData =
                        managedDecalData.Value.Deserialize<ManagedDecalData>(BeamJsonOptions
                            .GetJsonSerializerOptions());
                    _materialNames.Add(decalData.Material);
                }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error DecalScanner {file.FullName}. {ex.Message}");
        }
    }

    private void SetMaterialsCs(FileInfo file)
    {
        foreach (var decalName in _decalNames.Distinct())
        {
            var hit = false;
            foreach (var line in File.ReadAllLines(file.FullName))
            {
                var search = $"({decalName})";
                if (line.ToUpperInvariant().Contains(search.ToUpperInvariant())) hit = true;
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