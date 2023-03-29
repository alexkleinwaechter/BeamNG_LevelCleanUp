using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Logic
{
    internal class InfoJsonScanner
    {
        private string _infoJsonPath { get; set; }
        private string _levelPath { get; set; }
        private List<string> _exludeFiles = new List<string>();
        internal InfoJsonScanner(string infoJsonPath, string levelPath)
        {
            _infoJsonPath = infoJsonPath;
            _levelPath = levelPath;
        }
        internal List<string> GetExcludeFiles()
        {
            PubSubChannel.SendMessage(false, $"Read info.json");
            JsonDocumentOptions docOptions = BeamJsonOptions.GetJsonDocumentOptions();
            try
            {
                using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(_infoJsonPath), docOptions);
                if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
                {
                    if (jsonObject.RootElement.TryGetProperty("previews", out JsonElement previews))
                    {
                        var previewList = previews.Deserialize<List<string>>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (previewList != null)
                        {
                            _exludeFiles.AddRange(previewList.Select(x => PathResolver.ResolvePath(_levelPath, x, false)));
                        }
                    }
                    if (jsonObject.RootElement.TryGetProperty("spawnPoints", out JsonElement spawnpoints))
                    {
                        var spawnpointlist = spawnpoints.Deserialize<List<SpawnPoints>>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (spawnpointlist != null)
                        {
                            _exludeFiles.AddRange(spawnpointlist.Select(x => PathResolver.ResolvePath(_levelPath, x.Preview, false)));
                        }
                    }
                    if (jsonObject.RootElement.TryGetProperty("gasStationPoints", out JsonElement gasStationPoints))
                    {
                        var gasStationPointList = gasStationPoints.Deserialize<List<SpawnPoints>>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (gasStationPointList != null)
                        {
                            _exludeFiles.AddRange(gasStationPointList.Select(x => PathResolver.ResolvePath(_levelPath, x.Preview, false)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Stopped! Error {_infoJsonPath}. {ex.Message}");
            }
            return _exludeFiles;
        }
    }
}
