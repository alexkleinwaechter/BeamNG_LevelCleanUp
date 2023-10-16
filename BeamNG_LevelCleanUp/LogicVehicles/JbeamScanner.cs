using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.LogicVehicles
{
    public class JbeamScanner
    {
        private List<Asset> _assets = new List<Asset>();
        private List<FileInfo> _jbeams;
        public JbeamScanner(List<Asset> assets, List<FileInfo> jbeams)
        {
            _assets = assets;
            _jbeams = jbeams;
        }
        public void ScanJbeams()
        {
            foreach (var file in _jbeams)
            {
                using JsonDocument jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(file.FullName);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    foreach (var child in jsonObject.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var jbeam = child.Value.Deserialize<Jbeam>(BeamJsonOptions.GetJsonSerializerOptions());
                            //JsonElement globalSkin;
                            //jsonObject.RootElement.TryGetProperty("globalSkin", out globalSkin);

                            //JsonElement slotType;
                            //jsonObject.RootElement.TryGetProperty("slotType", out slotType);
                            if (!string.IsNullOrEmpty(jbeam.globalSkin) || !string.IsNullOrEmpty(jbeam.skinName))
                            {
                            }

                        }
                        catch (Exception ex)
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error while reading Jbeam {file.FullName}: {ex.Message}", true);
                        }
                    }
                }
            }
        }
    }
}
