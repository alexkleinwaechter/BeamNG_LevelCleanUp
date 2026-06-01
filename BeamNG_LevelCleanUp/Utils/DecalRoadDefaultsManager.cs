using System.Text.Json;
using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Manages the AppData decalroad-defaults.json file.
/// Creates from hardcoded defaults on first run, loads/saves user modifications.
/// </summary>
public static class DecalRoadDefaultsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Dictionary<string, DecalRoadLayerSet> Load()
    {
        var path = AppPaths.DecalRoadDefaultsPath;

        if (!File.Exists(path))
        {
            var defaults = DecalRoadDefaultLayerSets.GetDefaults();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, DecalRoadLayerSet>>(json, JsonOptions)
                   ?? DecalRoadDefaultLayerSets.GetDefaults();
        }
        catch (JsonException)
        {
            // Corrupted file — recreate from hardcoded defaults
            var defaults = DecalRoadDefaultLayerSets.GetDefaults();
            Save(defaults);
            return defaults;
        }
    }

    public static void Save(Dictionary<string, DecalRoadLayerSet> layerSets)
    {
        var json = JsonSerializer.Serialize(layerSets, JsonOptions);
        File.WriteAllText(AppPaths.DecalRoadDefaultsPath, json);
    }
}
