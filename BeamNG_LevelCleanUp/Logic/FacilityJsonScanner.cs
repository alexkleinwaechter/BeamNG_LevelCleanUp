using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class FacilityJsonScanner
{
    private readonly List<string> _exludeFiles = new();

    internal FacilityJsonScanner(string facilityJsonPath, string levelPath)
    {
        _facilityJsonPath = facilityJsonPath;
        _levelPath = levelPath;
    }

    private string _facilityJsonPath { get; }
    private string _levelPath { get; }

    internal List<string> GetExcludeFiles()
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Read facilities.json");
        try
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_facilityJsonPath);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                foreach (var facility in jsonObject.RootElement.EnumerateObject())
                {
                    var facilityArray =
                        facility.Value.Deserialize<Facility[]>(BeamJsonOptions.GetJsonSerializerOptions());
                    foreach (var facilityData in facilityArray)
                        if (facilityData != null && !string.IsNullOrWhiteSpace(facilityData.preview))
                            _exludeFiles.Add(PathResolver.ResolvePath(_levelPath, facilityData.preview, false));
                }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error DecalScanner {_facilityJsonPath}. {ex.Message}");
        }

        return _exludeFiles;
    }
}