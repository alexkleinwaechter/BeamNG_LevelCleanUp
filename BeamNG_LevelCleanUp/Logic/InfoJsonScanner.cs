using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using System.Text.Json;

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
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Read info.json");
            try
            {
                using JsonDocument jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_infoJsonPath);
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
                    if (jsonObject.RootElement.TryGetProperty("minimap", out JsonElement minimap))
                    {
                        var minimapList = minimap.Deserialize<List<Minimap>>(BeamJsonOptions.GetJsonSerializerOptions());
                        if (minimapList != null)
                        {
                            _exludeFiles.AddRange(minimapList.Select(x => PathResolver.ResolvePath(_levelPath, x.file, false)));
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
