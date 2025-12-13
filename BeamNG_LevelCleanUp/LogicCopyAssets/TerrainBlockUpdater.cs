using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles updating TerrainBlock entries in MissionGroup items.level.json files
/// </summary>
public static class TerrainBlockUpdater
{
    /// <summary>
    ///     Updates the TerrainBlock entry in the level's items.level.json file with new terrain parameters.
    /// </summary>
    /// <param name="levelPath">Path to the unpacked level folder</param>
    /// <param name="terrainName">Name of the terrain (e.g., "theTerrain")</param>
    /// <param name="terrainSize">Terrain size in pixels (power of 2)</param>
    /// <param name="maxHeight">Maximum terrain height in meters</param>
    /// <param name="terrainBaseHeight">Z position offset for the terrain</param>
    /// <param name="metersPerPixel">Meters per pixel (terrain scale), default 1.0</param>
    /// <returns>True if the TerrainBlock was successfully updated</returns>
    public static bool UpdateTerrainBlock(
        string levelPath,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight = 0f,
        float metersPerPixel = 1.0f)
    {
        try
        {
            var levelName = new DirectoryInfo(levelPath).Name;
            var itemsFilePath = FindTerrainBlockFile(levelPath);

            if (string.IsNullOrEmpty(itemsFilePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No items.level.json file with TerrainBlock found");
                return false;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Updating TerrainBlock in: {itemsFilePath}");

            var lines = File.ReadAllLines(itemsFilePath);
            var updatedLines = new List<string>();
            var terrainBlockUpdated = false;

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

                    if (root.TryGetProperty("class", out var classProperty) &&
                        classProperty.GetString() == "TerrainBlock")
                    {
                        var updatedLine = UpdateTerrainBlockJson(
                            line,
                            levelName,
                            terrainName,
                            terrainSize,
                            maxHeight,
                            terrainBaseHeight,
                            metersPerPixel);

                        updatedLines.Add(updatedLine);
                        terrainBlockUpdated = true;

                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Updated TerrainBlock: name={terrainName}, size={terrainSize}, maxHeight={maxHeight}, squareSize={metersPerPixel}");
                    }
                    else
                    {
                        updatedLines.Add(line);
                    }
                }
                catch (JsonException)
                {
                    updatedLines.Add(line);
                }
            }

            if (terrainBlockUpdated)
            {
                File.WriteAllLines(itemsFilePath, updatedLines);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "TerrainBlock updated successfully");
                return true;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No TerrainBlock found in items.level.json");
            return false;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error updating TerrainBlock: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Creates a new TerrainBlock entry and adds it to the level's items.level.json file.
    /// </summary>
    public static bool CreateTerrainBlock(
        string levelPath,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight = 0f,
        float metersPerPixel = 1.0f)
    {
        try
        {
            var levelName = new DirectoryInfo(levelPath).Name;
            // CRITICAL: TerrainBlock position is in METERS, not pixels!
            // For an 8192 terrain with squareSize=2, the corner is at (-8192, -8192) meters
            var halfSizeMeters = (terrainSize / 2.0f) * metersPerPixel;
            var position = new[] { -halfSizeMeters, -halfSizeMeters, terrainBaseHeight };

            var terrainBlock = new Dictionary<string, object>
            {
                { "name", terrainName },
                { "class", "TerrainBlock" },
                { "persistentId", Guid.NewGuid().ToString() },
                { "__parent", "Level_object" },
                { "position", position },
                { "baseTexSize", terrainSize },
                { "materialTextureSet", $"{levelName}TerrainMaterialTextureSet" },
                { "maxHeight", maxHeight },
                { "squareSize", metersPerPixel },
                { "minimapImage", "" },
                { "terrainFile", $"/levels/{levelName}/{terrainName}.ter" }
            };

            var terrainBlockJson =
                JsonSerializer.Serialize(terrainBlock, BeamJsonOptions.GetJsonSerializerOneLineOptions());

            var itemsFilePath = FindTerrainBlockFile(levelPath);

            if (string.IsNullOrEmpty(itemsFilePath))
            {
                itemsFilePath = Path.Join(levelPath, "main", "MissionGroup", "Level_object", "items.level.json");
                Directory.CreateDirectory(Path.GetDirectoryName(itemsFilePath)!);
            }

            var lines = File.Exists(itemsFilePath)
                ? File.ReadAllLines(itemsFilePath).ToList()
                : new List<string>();

            lines.Add(terrainBlockJson);
            File.WriteAllLines(itemsFilePath, lines);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Created TerrainBlock: name={terrainName}, size={terrainSize}, maxHeight={maxHeight}, squareSize={metersPerPixel}");

            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error creating TerrainBlock: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Updates or creates a TerrainBlock in the level.
    /// </summary>
    public static bool UpdateOrCreateTerrainBlock(
        string levelPath,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight = 0f,
        float metersPerPixel = 1.0f)
    {
        if (UpdateTerrainBlock(levelPath, terrainName, terrainSize, maxHeight, terrainBaseHeight, metersPerPixel))
            return true;

        return CreateTerrainBlock(levelPath, terrainName, terrainSize, maxHeight, terrainBaseHeight, metersPerPixel);
    }

    /// <summary>
    ///     Finds the items.level.json file containing a TerrainBlock entry.
    /// </summary>
    private static string? FindTerrainBlockFile(string levelPath)
    {
        var searchPaths = new[]
        {
            Path.Join(levelPath, "main", "MissionGroup", "Level_object"),
            Path.Join(levelPath, "main", "MissionGroup"),
            Path.Join(levelPath, "main")
        };

        foreach (var searchPath in searchPaths)
        {
            if (!Directory.Exists(searchPath))
                continue;

            var itemsFiles = Directory.GetFiles(searchPath, "items.level.json", SearchOption.AllDirectories);

            foreach (var itemsFile in itemsFiles)
                try
                {
                    var lines = File.ReadAllLines(itemsFile);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("class", out var classProperty) &&
                                classProperty.GetString() == "TerrainBlock")
                                return itemsFile;
                        }
                        catch (JsonException)
                        {
                        }
                    }
                }
                catch
                {
                }
        }

        var defaultPath = Path.Join(levelPath, "main", "MissionGroup", "Level_object", "items.level.json");
        if (File.Exists(defaultPath))
            return defaultPath;

        return null;
    }

    /// <summary>
    ///     Updates a TerrainBlock JSON line with new values.
    /// </summary>
    private static string UpdateTerrainBlockJson(
        string jsonLine,
        string levelName,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight,
        float metersPerPixel)
    {
        var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonLine);
        if (jsonDict == null)
            return jsonLine;

        // CRITICAL: TerrainBlock position is in METERS, not pixels!
        // For an 8192 terrain with squareSize=2, the corner is at (-8192, -8192) meters
        var halfSizeMeters = (terrainSize / 2.0f) * metersPerPixel;
        var position = new[] { -halfSizeMeters, -halfSizeMeters, terrainBaseHeight };

        jsonDict["name"] = JsonSerializer.SerializeToElement(terrainName);
        jsonDict["baseTexSize"] = JsonSerializer.SerializeToElement(terrainSize);
        jsonDict["maxHeight"] = JsonSerializer.SerializeToElement(maxHeight);
        jsonDict["squareSize"] = JsonSerializer.SerializeToElement(metersPerPixel);
        jsonDict["minimapImage"] = JsonSerializer.SerializeToElement("");
        jsonDict["terrainFile"] = JsonSerializer.SerializeToElement($"/levels/{levelName}/{terrainName}.ter");
        jsonDict["position"] = JsonSerializer.SerializeToElement(position);
        jsonDict["persistentId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString());

        return JsonSerializer.Serialize(jsonDict, BeamJsonOptions.GetJsonSerializerOneLineOptions());
    }
}
