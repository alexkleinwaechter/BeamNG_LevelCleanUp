using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class LevelRenamer
{
    internal void EditInfoJson(string namePath, string newName, LevelInfoModel levelInfo = null)
    {
        var _infoJsonPath = Path.Join(namePath, "info.json");
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Read info.json");
        try
        {
            var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(_infoJsonPath);
            jsonNode["title"] = newName;

            // Only set isAuxiliary to false if the field exists in the JSON
            if (jsonNode.AsObject().ContainsKey("isAuxiliary"))
            {
                jsonNode["isAuxiliary"] = false;
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Set isAuxiliary to false in info.json");
            }

            // Update metadata fields if provided
            if (levelInfo != null)
            {
                SetOrRemoveField(jsonNode, "description", levelInfo.Description);
                SetOrRemoveField(jsonNode, "country", levelInfo.Country);
                SetOrRemoveField(jsonNode, "region", levelInfo.Region);
                SetOrRemoveField(jsonNode, "biome", levelInfo.Biome);
                SetOrRemoveField(jsonNode, "roads", levelInfo.Roads);
                SetOrRemoveField(jsonNode, "suitablefor", levelInfo.SuitableFor);
                SetOrRemoveField(jsonNode, "features", levelInfo.Features);
                SetOrRemoveField(jsonNode, "authors", levelInfo.Authors);
            }

            File.WriteAllText(_infoJsonPath, jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
        }
        catch (Exception ex)
        {
            throw new Exception($"Stopped! Error {_infoJsonPath}. {ex.Message}");
        }
    }

    private static void SetOrRemoveField(System.Text.Json.Nodes.JsonNode jsonNode, string fieldName, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            jsonNode[fieldName] = value;
        else
            jsonNode.AsObject().Remove(fieldName);
    }

    internal void ReplaceInFile(string filePath, string searchText, string replaceText)
    {
        var text = File.ReadAllText(filePath);
        text = text.Replace($"levels{searchText}", $"levels{replaceText}", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(filePath, text);
    }
}