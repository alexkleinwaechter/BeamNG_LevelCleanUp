using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.Logic;

internal class TerrainScanner
{
    private readonly List<Asset> _assets = new();
    private readonly List<string> _excludeFiles = new();
    private readonly List<MaterialJson> _materialJson = new();

    internal TerrainScanner(string terrainPath, string levelPath, List<Asset> assets, List<MaterialJson> materialJson,
        List<string> excludeFiles)
    {
        _terrainPath = terrainPath;
        _assets = assets;
        _levelPath = levelPath;
        _excludeFiles = excludeFiles;
        _materialJson = materialJson;
    }

    private string _terrainPath { get; }
    private string _levelPath { get; }

    public void ScanTerrain()
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Scan Terrainfile {_terrainPath}");
        try
        {
            using var jsonObject = JsonUtils.GetValidJsonDocumentFromFilePath(_terrainPath);
            if (jsonObject.RootElement.ValueKind != JsonValueKind.Undefined)
            {
                if (jsonObject.RootElement.TryGetProperty("materials", out var materials))
                {
                    var materialList = materials.Deserialize<List<string>>(BeamJsonOptions.GetJsonSerializerOptions());
                    if (materialList != null)
                        foreach (var name in materialList.Distinct())
                        {
                            _assets.Add(new Asset
                            {
                                Class = "Terrain",
                                Material = name
                            });
                            var matJson = _materialJson.Where(x => x.InternalName == name);
                            foreach (var item in matJson)
                                _assets.Add(new Asset
                                {
                                    Class = "Terrain",
                                    Material = item.Name
                                });
                        }
                }

                if (jsonObject.RootElement.TryGetProperty("datafile", out var dataFile))
                {
                    var result = dataFile.Deserialize<string>(BeamJsonOptions.GetJsonSerializerOptions());
                    if (result != null) _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, result, false));
                }

                if (jsonObject.RootElement.TryGetProperty("heightmapImage", out var heightmapImage))
                {
                    var result = heightmapImage.Deserialize<string>(BeamJsonOptions.GetJsonSerializerOptions());
                    if (result != null) _excludeFiles.Add(PathResolver.ResolvePath(_levelPath, result, false));
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Stopped! Error {_terrainPath}. {ex.Message}");
        }
    }
}