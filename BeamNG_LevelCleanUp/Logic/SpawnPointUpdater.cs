using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Logic;

/// <summary>
///     Updates SpawnSphere positions in MissionGroup items.level.json files
///     based on terrain generation results.
/// </summary>
public static class SpawnPointUpdater
{
    /// <summary>
    ///     The name of the spawn point we create/update in the Create Level wizard.
    /// </summary>
    public const string DefaultSpawnName = "spawn_default_MT";

    /// <summary>
    ///     Updates the SpawnSphere named "spawn_default_MT" in the MissionGroup with the given spawn point.
    /// </summary>
    /// <param name="levelPath">Path to the level folder</param>
    /// <param name="spawnPoint">The spawn point data to apply</param>
    /// <returns>True if the spawn point was updated successfully</returns>
    public static bool UpdateSpawnPoint(string levelPath, SpawnPointSuggestion spawnPoint)
    {
        if (spawnPoint == null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, "No spawn point provided to update");
            return false;
        }

        try
        {
            // Find all items.level.json files in MissionGroup
            var missionGroupPath = Path.Join(levelPath, "main", "MissionGroup");
            if (!Directory.Exists(missionGroupPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"MissionGroup folder not found at {missionGroupPath}");
                return false;
            }

            var itemsFiles = Directory.GetFiles(missionGroupPath, "items.level.json", SearchOption.AllDirectories);

            if (itemsFiles.Length == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, "No items.level.json files found in MissionGroup");
                return false;
            }

            // Search for the SpawnSphere in each file
            foreach (var itemsFile in itemsFiles)
            {
                if (UpdateSpawnPointInFile(itemsFile, spawnPoint))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Updated spawn point '{DefaultSpawnName}' at ({spawnPoint.X:F1}, {spawnPoint.Y:F1}, {spawnPoint.Z:F1})");
                    
                    if (spawnPoint.IsOnRoad)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Spawn point is on road: {spawnPoint.SourceMaterialName}");
                    }
                    
                    return true;
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"SpawnSphere '{DefaultSpawnName}' not found in any MissionGroup file");
            return false;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error updating spawn point: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Updates the SpawnSphere in a single items.level.json file.
    /// </summary>
    private static bool UpdateSpawnPointInFile(string filePath, SpawnPointSuggestion spawnPoint)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var modified = false;
            var updatedLines = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    updatedLines.Add(line);
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Check if this is our SpawnSphere
                    if (root.TryGetProperty("class", out var classElement) &&
                        classElement.GetString() == "SpawnSphere" &&
                        root.TryGetProperty("name", out var nameElement) &&
                        string.Equals(nameElement.GetString(), DefaultSpawnName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Found it! Update position and rotation
                        var updatedLine = UpdateSpawnSphereJson(line, spawnPoint);
                        updatedLines.Add(updatedLine);
                        modified = true;
                    }
                    else
                    {
                        updatedLines.Add(line);
                    }
                }
                catch (JsonException)
                {
                    // Not valid JSON, keep as-is
                    updatedLines.Add(line);
                }
            }

            if (modified)
            {
                File.WriteAllLines(filePath, updatedLines);
            }

            return modified;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error processing {Path.GetFileName(filePath)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Updates the position and rotationMatrix in a SpawnSphere JSON line.
    /// </summary>
    private static string UpdateSpawnSphereJson(string jsonLine, SpawnPointSuggestion spawnPoint)
    {
        // Parse to dictionary for modification
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonLine);
        if (dict == null)
            return jsonLine;

        // Update position
        dict["position"] = JsonSerializer.SerializeToElement(spawnPoint.ToPositionArray());

        // Update rotationMatrix
        dict["rotationMatrix"] = JsonSerializer.SerializeToElement(spawnPoint.RotationMatrix);

        // Serialize back to single line
        var options = BeamJsonOptions.GetJsonSerializerOneLineOptions();
        return JsonSerializer.Serialize(dict, options);
    }

    /// <summary>
    ///     Checks if a SpawnSphere with the default name exists in the level's MissionGroup.
    /// </summary>
    /// <param name="levelPath">Path to the level folder</param>
    /// <returns>True if spawn_default_MT exists</returns>
    public static bool SpawnPointExists(string levelPath)
    {
        try
        {
            var missionGroupPath = Path.Join(levelPath, "main", "MissionGroup");
            if (!Directory.Exists(missionGroupPath))
                return false;

            var itemsFiles = Directory.GetFiles(missionGroupPath, "items.level.json", SearchOption.AllDirectories);

            foreach (var itemsFile in itemsFiles)
            {
                var lines = File.ReadAllLines(itemsFile);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("class", out var classElement) &&
                            classElement.GetString() == "SpawnSphere" &&
                            root.TryGetProperty("name", out var nameElement) &&
                            string.Equals(nameElement.GetString(), DefaultSpawnName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (JsonException)
                    {
                        // Ignore parse errors
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Creates a new SpawnSphere named "spawn_default_MT" in the MissionGroup's PlayerDropPoints folder.
    ///     If the PlayerDropPoints folder doesn't exist, it will be created.
    /// </summary>
    /// <param name="levelPath">Path to the level folder</param>
    /// <param name="spawnPoint">Optional spawn point data. If null, creates at default position (0, 0, 100)</param>
    /// <returns>True if the spawn point was created successfully</returns>
    public static bool CreateSpawnPoint(string levelPath, SpawnPointSuggestion? spawnPoint = null)
    {
        try
        {
            var missionGroupPath = Path.Join(levelPath, "main", "MissionGroup");
            
            // If MissionGroup doesn't exist, we can't create a spawn point
            if (!Directory.Exists(missionGroupPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"MissionGroup folder not found at {missionGroupPath}. Cannot create spawn point.");
                return false;
            }

            // Create PlayerDropPoints folder if it doesn't exist
            var playerDropPointsPath = Path.Join(missionGroupPath, "PlayerDropPoints");
            if (!Directory.Exists(playerDropPointsPath))
            {
                Directory.CreateDirectory(playerDropPointsPath);
                
                // Also need to add PlayerDropPoints SimGroup entry to MissionGroup/items.level.json
                EnsurePlayerDropPointsSimGroup(missionGroupPath);
            }

            var itemsFilePath = Path.Join(playerDropPointsPath, "items.level.json");

            // Build position and rotation from spawn point data or use defaults
            double[] position = spawnPoint != null 
                ? spawnPoint.ToPositionArray() 
                : [0.0, 0.0, 100.0];
            
            double[] rotationMatrix = spawnPoint?.RotationMatrix ?? [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0];

            // Create SpawnSphere dictionary matching the format used in MissionGroupCopier
            var spawnSphere = new Dictionary<string, object>
            {
                { "name", DefaultSpawnName },
                { "class", "SpawnSphere" },
                { "persistentId", Guid.NewGuid().ToString() },
                { "__parent", "PlayerDropPoints" },
                { "position", position },
                { "autoplaceOnSpawn", "0" },
                { "dataBlock", "SpawnSphereMarker" },
                { "enabled", "1" },
                { "homingCount", "0" },
                { "indoorWeight", "1" },
                { "isAIControlled", "0" },
                { "lockCount", "0" },
                { "outdoorWeight", "1" },
                { "radius", 1 },
                { "rotationMatrix", rotationMatrix },
                { "sphereWeight", "1" }
            };

            var spawnJson = JsonSerializer.Serialize(spawnSphere, BeamJsonOptions.GetJsonSerializerOneLineOptions());

            // Append to existing file or create new one
            if (File.Exists(itemsFilePath))
            {
                // Read existing content and append
                var existingContent = File.ReadAllText(itemsFilePath);
                if (!existingContent.EndsWith(Environment.NewLine) && !string.IsNullOrEmpty(existingContent))
                {
                    existingContent += Environment.NewLine;
                }
                File.WriteAllText(itemsFilePath, existingContent + spawnJson + Environment.NewLine);
            }
            else
            {
                // Create new file
                File.WriteAllText(itemsFilePath, spawnJson + Environment.NewLine);
            }

            var positionStr = spawnPoint != null 
                ? $"({spawnPoint.X:F1}, {spawnPoint.Y:F1}, {spawnPoint.Z:F1})" 
                : "(0, 0, 100)";
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Created spawn point '{DefaultSpawnName}' at {positionStr}");

            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error creating spawn point: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Ensures the PlayerDropPoints SimGroup entry exists in MissionGroup/items.level.json.
    /// </summary>
    private static void EnsurePlayerDropPointsSimGroup(string missionGroupPath)
    {
        try
        {
            var itemsFilePath = Path.Join(missionGroupPath, "items.level.json");
            
            if (!File.Exists(itemsFilePath))
            {
                // Create the file with PlayerDropPoints entry
                var playerDropPointsEntry = new Dictionary<string, object>
                {
                    { "name", "PlayerDropPoints" },
                    { "class", "SimGroup" },
                    { "persistentId", Guid.NewGuid().ToString() },
                    { "__parent", "MissionGroup" }
                };
                var json = JsonSerializer.Serialize(playerDropPointsEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions());
                File.WriteAllText(itemsFilePath, json + Environment.NewLine);
                return;
            }

            // Check if PlayerDropPoints already exists
            var lines = File.ReadAllLines(itemsFilePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    
                    if (root.TryGetProperty("name", out var nameElement) &&
                        string.Equals(nameElement.GetString(), "PlayerDropPoints", StringComparison.OrdinalIgnoreCase))
                    {
                        // Already exists
                        return;
                    }
                }
                catch (JsonException)
                {
                    // Ignore parse errors
                }
            }

            // Add PlayerDropPoints entry
            var newEntry = new Dictionary<string, object>
            {
                { "name", "PlayerDropPoints" },
                { "class", "SimGroup" },
                { "persistentId", Guid.NewGuid().ToString() },
                { "__parent", "MissionGroup" }
            };
            var newJson = JsonSerializer.Serialize(newEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions());
            File.AppendAllText(itemsFilePath, newJson + Environment.NewLine);
            
            PubSubChannel.SendMessage(PubSubMessageType.Info, 
                "Added PlayerDropPoints SimGroup to MissionGroup");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not ensure PlayerDropPoints SimGroup: {ex.Message}");
        }
    }
}
