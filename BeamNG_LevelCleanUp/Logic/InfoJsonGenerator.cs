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
    public static void CreateInfoJson(string targetLevelNamePath, string levelDisplayName)
    {
        try
        {
            var levelPathName = Path.GetFileName(targetLevelNamePath);
            
            var infoJson = new
            {
                title = levelDisplayName,
                description = $"{levelDisplayName} - Created with BeamNG Mapping Tools",
                previews = new[] { $"{levelPathName}_preview.jpg" },
                size = new[] { 1024, 1024 },
                defaultSpawnPointName = "spawn_default",
                spawnPoints = Array.Empty<object>(),
                authors = "BeamNG Mapping Tools"
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(infoJson, options);
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
