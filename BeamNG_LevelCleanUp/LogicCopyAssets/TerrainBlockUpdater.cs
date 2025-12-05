using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
/// Handles updating TerrainBlock entries in MissionGroup items.level.json files
/// </summary>
public static class TerrainBlockUpdater
{
    /// <summary>
    /// Updates the TerrainBlock entry in the level's items.level.json file with new terrain parameters.
    /// </summary>
    /// <param name="levelPath">Path to the unpacked level folder</param>
    /// <param name="terrainName">Name of the terrain (e.g., "theTerrain")</param>
    /// <param name="terrainSize">Terrain size in pixels (power of 2)</param>
    /// <param name="maxHeight">Maximum terrain height in meters</param>
    /// <param name="terrainBaseHeight">Z position offset for the terrain</param>
    /// <returns>True if the TerrainBlock was successfully updated</returns>
    public static bool UpdateTerrainBlock(
        string levelPath,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight = 0f)
    {
        try
        {
            // Get level name from path
            var levelName = new DirectoryInfo(levelPath).Name;
            
            // Find the items.level.json file containing TerrainBlock
            var itemsFilePath = FindTerrainBlockFile(levelPath);
            
            if (string.IsNullOrEmpty(itemsFilePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No items.level.json file with TerrainBlock found");
                return false;
            }
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Updating TerrainBlock in: {itemsFilePath}");
            
            // Read all lines from the file
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
                    // Try to parse as JSON
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    
                    // Check if this is a TerrainBlock
                    if (root.TryGetProperty("class", out var classProperty) &&
                        classProperty.GetString() == "TerrainBlock")
                    {
                        // Update the TerrainBlock
                        var updatedLine = UpdateTerrainBlockJson(
                            line, 
                            levelName, 
                            terrainName, 
                            terrainSize, 
                            maxHeight, 
                            terrainBaseHeight);
                        
                        updatedLines.Add(updatedLine);
                        terrainBlockUpdated = true;
                        
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Updated TerrainBlock: name={terrainName}, size={terrainSize}, maxHeight={maxHeight}");
                    }
                    else
                    {
                        // Keep the line as-is
                        updatedLines.Add(line);
                    }
                }
                catch (JsonException)
                {
                    // Not valid JSON, keep as-is
                    updatedLines.Add(line);
                }
            }
            
            if (terrainBlockUpdated)
            {
                // Write the updated file
                File.WriteAllLines(itemsFilePath, updatedLines);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "TerrainBlock updated successfully");
                return true;
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No TerrainBlock found in items.level.json");
                return false;
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error updating TerrainBlock: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Creates a new TerrainBlock entry and adds it to the level's items.level.json file.
    /// </summary>
    /// <param name="levelPath">Path to the unpacked level folder</param>
    /// <param name="terrainName">Name of the terrain (e.g., "theTerrain")</param>
    /// <param name="terrainSize">Terrain size in pixels (power of 2)</param>
    /// <param name="maxHeight">Maximum terrain height in meters</param>
    /// <param name="terrainBaseHeight">Z position offset for the terrain</param>
    /// <returns>True if the TerrainBlock was successfully created</returns>
    public static bool CreateTerrainBlock(
        string levelPath,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight = 0f)
    {
        try
        {
            var levelName = new DirectoryInfo(levelPath).Name;
            
            // Calculate position (centered on origin)
            float halfSize = terrainSize / 2.0f;
            var position = new[] { -halfSize, -halfSize, terrainBaseHeight };
            
            // Create TerrainBlock dictionary
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
                { "minimapImage", "" },
                { "terrainFile", $"/levels/{levelName}/{terrainName}.ter" }
            };
            
            // Serialize to JSON
            var terrainBlockJson = JsonSerializer.Serialize(terrainBlock, BeamJsonOptions.GetJsonSerializerOneLineOptions());
            
            // Find or create the items.level.json file
            var itemsFilePath = FindTerrainBlockFile(levelPath);
            
            if (string.IsNullOrEmpty(itemsFilePath))
            {
                // Create the file path
                itemsFilePath = Path.Join(levelPath, "main", "MissionGroup", "Level_object", "items.level.json");
                Directory.CreateDirectory(Path.GetDirectoryName(itemsFilePath)!);
            }
            
            // Append the TerrainBlock entry
            var lines = File.Exists(itemsFilePath) 
                ? File.ReadAllLines(itemsFilePath).ToList() 
                : new List<string>();
            
            lines.Add(terrainBlockJson);
            File.WriteAllLines(itemsFilePath, lines);
            
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Created TerrainBlock: name={terrainName}, size={terrainSize}, maxHeight={maxHeight}");
            
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
    /// Updates or creates a TerrainBlock in the level.
    /// If a TerrainBlock exists, it will be updated. Otherwise, a new one will be created.
    /// </summary>
    public static bool UpdateOrCreateTerrainBlock(
        string levelPath,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight = 0f)
    {
        // First try to update existing
        if (UpdateTerrainBlock(levelPath, terrainName, terrainSize, maxHeight, terrainBaseHeight))
        {
            return true;
        }
        
        // If no existing TerrainBlock found, create a new one
        return CreateTerrainBlock(levelPath, terrainName, terrainSize, maxHeight, terrainBaseHeight);
    }
    
    /// <summary>
    /// Finds the items.level.json file containing a TerrainBlock entry.
    /// </summary>
    private static string? FindTerrainBlockFile(string levelPath)
    {
        // Search in common locations
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
            {
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
                            {
                                return itemsFile;
                            }
                        }
                        catch (JsonException)
                        {
                            continue;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        
        // If no TerrainBlock found, return the default expected path
        var defaultPath = Path.Join(levelPath, "main", "MissionGroup", "Level_object", "items.level.json");
        if (File.Exists(defaultPath))
            return defaultPath;
        
        return null;
    }
    
    /// <summary>
    /// Updates a TerrainBlock JSON line with new values.
    /// </summary>
    private static string UpdateTerrainBlockJson(
        string jsonLine,
        string levelName,
        string terrainName,
        int terrainSize,
        float maxHeight,
        float terrainBaseHeight)
    {
        // Parse the existing JSON
        var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonLine);
        if (jsonDict == null)
            return jsonLine;
        
        // Calculate position (centered on origin)
        float halfSize = terrainSize / 2.0f;
        var position = new[] { -halfSize, -halfSize, terrainBaseHeight };
        
        // Update fields
        jsonDict["name"] = JsonSerializer.SerializeToElement(terrainName);
        jsonDict["baseTexSize"] = JsonSerializer.SerializeToElement(terrainSize);
        jsonDict["maxHeight"] = JsonSerializer.SerializeToElement(maxHeight);
        jsonDict["minimapImage"] = JsonSerializer.SerializeToElement("");
        jsonDict["terrainFile"] = JsonSerializer.SerializeToElement($"/levels/{levelName}/{terrainName}.ter");
        jsonDict["position"] = JsonSerializer.SerializeToElement(position);
       
        // Generate new persistentId
        jsonDict["persistentId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString());
        
        // Serialize back to single-line JSON
        return JsonSerializer.Serialize(jsonDict, BeamJsonOptions.GetJsonSerializerOneLineOptions());
    }
}
