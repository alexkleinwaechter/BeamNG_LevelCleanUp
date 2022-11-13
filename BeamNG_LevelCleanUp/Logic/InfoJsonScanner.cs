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

        private string ResolvePath(string resourcePath)
        {
            char toReplaceDelim = '/';
            char delim = '\\';
            return Path.Join(_levelPath, resourcePath.Replace(toReplaceDelim, delim));

            //char delim = '\\';
            //return string.Join(
            //    new string(delim, 1),
            //    _levelPath.Split(delim).Concat(_daePath.Split(delim)).Distinct().ToArray())
            //    .Replace("\\\\", "\\");
        }

        internal List<string> GetExcludeFiles()
        {
            PubSubChannel.SendMessage(false, $"Read info.json");
            JsonDocumentOptions docOptions = new JsonDocumentOptions { AllowTrailingCommas = true };
            using JsonDocument jsonObject = JsonDocument.Parse(File.ReadAllText(_infoJsonPath), docOptions);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
            {
                try
                {
                    if (jsonObject.RootElement.TryGetProperty("previews", out JsonElement previews))
                    {
                        var previewList = previews.Deserialize<List<string>>(BeamJsonOptions.Get());
                        if (previewList != null)
                        {
                            _exludeFiles.AddRange(previewList.Select(x => ResolvePath(x)));
                        }
                    }
                    if (jsonObject.RootElement.TryGetProperty("spawnPoints", out JsonElement spawnpoints))
                    {
                        var spawnpointlist = spawnpoints.Deserialize<List<SpawnPoints>>(BeamJsonOptions.Get());
                        if (spawnpointlist != null)
                        {
                            _exludeFiles.AddRange(spawnpointlist.Select(x => ResolvePath(x.Preview)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw;
                }
            }
            return _exludeFiles;
        }
    }
}
