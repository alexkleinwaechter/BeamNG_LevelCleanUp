using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Logic;

/// <summary>
///     Generates info.json file for new BeamNG levels
/// </summary>
public class InfoJsonGenerator
{
    /// <summary>
    ///     Creates a basic info.json file for a new level
    /// </summary>
    /// <param name="targetLevelNamePath">Full path to the target level folder (e.g., .../levels/my_map)</param>
    /// <param name="levelDisplayName">Display name for the level</param>
    /// <param name="description">Level description</param>
    /// <param name="country">Country where the level is set</param>
    /// <param name="region">Region within the country</param>
    /// <param name="biome">Biome type (e.g., Desert, Forest)</param>
    /// <param name="roads">Road types available</param>
    /// <param name="suitableFor">What the level is suitable for</param>
    /// <param name="features">Special features of the level</param>
    /// <param name="authors">Level author(s)</param>
    public static void CreateInfoJson(
        string targetLevelNamePath,
        string levelDisplayName,
        string description = null,
        string country = null,
        string region = null,
        string biome = null,
        string roads = null,
        string suitableFor = null,
        string features = null,
        string authors = null)
    {
        try
        {
            var levelPathName = Path.GetFileName(targetLevelNamePath);

            // Use provided values or defaults
            var finalDescription = !string.IsNullOrWhiteSpace(description)
                ? description
                : $"{levelDisplayName} - Created with BeamNG Mapping Tools";
            var finalAuthors = !string.IsNullOrWhiteSpace(authors)
                ? authors
                : "BeamNG Mapping Tools";

            // Build the info.json object with all properties
            var infoDict = new Dictionary<string, object>
            {
                { "title", levelDisplayName },
                { "description", finalDescription },
                { "previews", new[] { $"{levelPathName}_preview.jpg" } },
                { "size", new[] { 1024, 1024 } },
                { "defaultSpawnPointName", "spawn_default_MT" },
                {
                    "spawnPoints", new[]
                    {
                        new Dictionary<string, string>
                        {
                            { "translationId", "Default Spawnpoint" },
                            { "objectname", "spawn_default_MT" },
                            { "preview", $"{levelPathName}_preview.jpg" }
                        }
                    }
                },
                { "authors", finalAuthors }
            };

            // Add optional properties only if they have values
            if (!string.IsNullOrWhiteSpace(country))
                infoDict["country"] = country;
            if (!string.IsNullOrWhiteSpace(region))
                infoDict["region"] = region;
            if (!string.IsNullOrWhiteSpace(biome))
                infoDict["biome"] = biome;
            if (!string.IsNullOrWhiteSpace(roads))
                infoDict["roads"] = roads;
            if (!string.IsNullOrWhiteSpace(suitableFor))
                infoDict["suitablefor"] = suitableFor;
            if (!string.IsNullOrWhiteSpace(features))
                infoDict["features"] = features;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(infoDict, options);
            var infoJsonPath = Path.Join(targetLevelNamePath, "info.json");

            File.WriteAllText(infoJsonPath, json);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Created info.json for {levelDisplayName}");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error creating info.json: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Creates a basic mainLevel.lua initialization script
    /// </summary>
    /// <param name="targetLevelNamePath">Full path to the target level folder</param>
    public static void CreateMainLevelLua(string targetLevelNamePath)
    {
        try
        {
            var luaContent = @"-- Level initialization script
-- This file is executed when the level is loaded

local M = {}

local function onClientStartMission()
    -- Level startup code here
end

local function onSerialize()
    return {}
end

local function onDeserialized(data)
end

M.onClientStartMission = onClientStartMission
M.onSerialize = onSerialize
M.onDeserialized = onDeserialized

return M
";
            var luaPath = Path.Join(targetLevelNamePath, "mainLevel.lua");
            File.WriteAllText(luaPath, luaContent);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Created mainLevel.lua initialization script");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not create mainLevel.lua: {ex.Message}");
        }
    }
}