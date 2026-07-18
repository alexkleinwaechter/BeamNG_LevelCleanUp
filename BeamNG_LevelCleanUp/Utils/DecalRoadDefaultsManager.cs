using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Services.DecalRoad;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Manages the AppData decalroad-defaults.json file.
/// Creates from hardcoded defaults on first run, loads/saves user modifications.
/// On every load, changes in the hardcoded code defaults (new road types, new layers,
/// new or changed fields) are merged into the user file via <see cref="DecalRoadDefaultsMerger"/>.
/// A baseline snapshot of the code defaults is kept alongside the user file so user
/// overrides can be told apart from stale defaults.
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
        var defaults = DecalRoadDefaultLayerSets.GetDefaults();
        var currentJson = JsonSerializer.Serialize(defaults, JsonOptions);

        if (!File.Exists(path))
        {
            File.WriteAllText(path, currentJson);
            WriteBaseline(currentJson);
            return defaults;
        }

        try
        {
            var userText = File.ReadAllText(path);
            if (JsonNode.Parse(userText) is not JsonObject userRoot)
                throw new JsonException("Root of decalroad-defaults.json is not an object.");

            // Re-parse the serialized defaults so user/baseline/current values compare
            // with identical JSON backing (numbers, enum strings).
            var currentRoot = JsonNode.Parse(currentJson)!.AsObject();
            var baselineRoot = TryReadBaseline();

            if (DecalRoadDefaultsMerger.Merge(userRoot, baselineRoot, currentRoot))
                File.WriteAllText(path, userRoot.ToJsonString(JsonOptions));

            if (baselineRoot is null || !JsonNode.DeepEquals(baselineRoot, currentRoot))
                WriteBaseline(currentJson);

            return userRoot.Deserialize<Dictionary<string, DecalRoadLayerSet>>(JsonOptions)
                   ?? defaults;
        }
        catch (JsonException)
        {
            // Corrupted file — recreate from hardcoded defaults
            File.WriteAllText(path, currentJson);
            WriteBaseline(currentJson);
            return defaults;
        }
    }

    public static void Save(Dictionary<string, DecalRoadLayerSet> layerSets)
    {
        var json = JsonSerializer.Serialize(layerSets, JsonOptions);
        File.WriteAllText(AppPaths.DecalRoadDefaultsPath, json);
    }

    private static JsonObject? TryReadBaseline()
    {
        var path = AppPaths.DecalRoadDefaultsBaselinePath;
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteBaseline(string currentDefaultsJson)
    {
        try
        {
            File.WriteAllText(AppPaths.DecalRoadDefaultsBaselinePath, currentDefaultsJson);
        }
        catch (IOException)
        {
            // Baseline is an optimization — without it the next merge simply
            // falls back to presence-based merging. Never block loading on it.
        }
    }
}
