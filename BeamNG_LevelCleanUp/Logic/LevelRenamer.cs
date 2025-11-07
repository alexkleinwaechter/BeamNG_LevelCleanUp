using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class LevelRenamer
    {
        internal LevelRenamer()
        {
        }

        internal void EditInfoJson(string namePath, string newName)
        {
            var _infoJsonPath = Path.Join(namePath, "info.json");
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read info.json");
            try
            {
                var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(_infoJsonPath);
                jsonNode["title"] = newName;
                
                // Only set isAuxiliary to false if the field exists in the JSON
                if (jsonNode.AsObject().ContainsKey("isAuxiliary"))
                {
                    jsonNode["isAuxiliary"] = false;
                    PubSubChannel.SendMessage(PubSubMessageType.Info, $"Set isAuxiliary to false in info.json");
                }
                
                File.WriteAllText(_infoJsonPath, jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));
            }
            catch (Exception ex)
            {
                throw new Exception($"Stopped! Error {_infoJsonPath}. {ex.Message}");
            }
        }

        internal void ReplaceInFile(string filePath, string searchText, string replaceText)
        {
            string text = File.ReadAllText(filePath);
            text = text.Replace($"levels{searchText}", $"levels{replaceText}", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(filePath, text);
        }
    }
}
