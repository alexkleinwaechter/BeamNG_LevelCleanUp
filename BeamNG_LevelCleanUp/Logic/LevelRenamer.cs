using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class LevelRenamer
    {
        internal LevelRenamer()
        {
        }

        internal void EditInfoJson(string namePath, string newName) {
            var _infoJsonPath = Path.Join(namePath, "info.json");
            PubSubChannel.SendMessage(false, $"Read info.json");
            try
            {
                JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
                var jsonNode = JsonNode.Parse(File.ReadAllText(_infoJsonPath), null, docOptions);
                jsonNode["title"] = newName;
                File.WriteAllText(_infoJsonPath, jsonNode.ToJsonString()); 
            }
            catch (Exception ex)
            {
                throw new Exception($"Stopped! Error {_infoJsonPath}. {ex.Message}");
            }
        }

        internal void ReplaceInFile(string filePath, string searchText, string replaceText) {
            string text = File.ReadAllText(filePath);
            text = text.Replace(searchText, replaceText, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(filePath, text);
        }
    }
}
