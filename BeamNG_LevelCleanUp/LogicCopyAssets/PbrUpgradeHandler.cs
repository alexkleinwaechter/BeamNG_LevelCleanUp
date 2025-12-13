using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles upgrading terrain materials to PBR (Physically Based Rendering) format
///     This includes adding the TerrainMaterialTextureSet configuration to the target level
/// </summary>
public class PbrUpgradeHandler
{
    private readonly string _targetLevelName;
    private readonly string _targetLevelPath;
    private readonly string _targetMaterialsJsonPath;

    public PbrUpgradeHandler(string targetMaterialsJsonPath, string targetLevelName, string targetLevelPath)
    {
        _targetMaterialsJsonPath = targetMaterialsJsonPath;
        _targetLevelName = targetLevelName;
        _targetLevelPath = targetLevelPath;
    }

    /// <summary>
    ///     Adds or updates the TerrainMaterialTextureSet configuration in the target level's materials.json file
    ///     This enables PBR terrain materials by defining the texture sizes for base, detail, and macro textures
    /// </summary>
    /// <param name="baseTexSize">The size for base textures (default: 1024)</param>
    /// <param name="detailTexSize">The size for detail textures (default: 1024)</param>
    /// <param name="macroTexSize">The size for macro textures (default: 1024)</param>
    public void AddTerrainMaterialTextureSet(int baseTexSize = 1024, int detailTexSize = 1024, int macroTexSize = 1024)
    {
        try
        {
            // Use the provided materials file path
            var targetFile = new FileInfo(_targetMaterialsJsonPath);

            if (!targetFile.Exists)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Target materials file not found: {targetFile.FullName}. Cannot add TerrainMaterialTextureSet.");
                return;
            }

            // Load the existing JSON
            var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetFile.FullName);

            // Generate the key name using the target level name
            var textureSetKey = $"{_targetLevelName}TerrainMaterialTextureSet";

            // Check if the key already exists
            if (jsonNode.AsObject().ContainsKey(textureSetKey))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"TerrainMaterialTextureSet '{textureSetKey}' already exists. Skipping PBR upgrade.");
                return;
            }

            // Create the TerrainMaterialTextureSet object
            var textureSetNode = new JsonObject
            {
                ["name"] = textureSetKey,
                ["class"] = "TerrainMaterialTextureSet",
                ["baseTexSize"] = new JsonArray(baseTexSize, baseTexSize),
                ["detailTexSize"] = new JsonArray(detailTexSize, detailTexSize),
                ["macroTexSize"] = new JsonArray(macroTexSize, macroTexSize)
            };

            // Add the TerrainMaterialTextureSet to the JSON
            jsonNode.AsObject().Add(textureSetKey, textureSetNode);

            // Write the updated JSON back to the file
            File.WriteAllText(targetFile.FullName,
                jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Successfully added TerrainMaterialTextureSet '{textureSetKey}' with sizes: base={baseTexSize}, detail={detailTexSize}, macro={macroTexSize}");

            // Now update the TerrainBlock's materialTextureSet property
            UpdateTerrainBlockMaterialTextureSet(textureSetKey);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to add TerrainMaterialTextureSet: {ex.Message}");
        }
    }

    /// <summary>
    ///     Updates the materialTextureSet property in TerrainBlock objects found in items.level.json files
    /// </summary>
    private void UpdateTerrainBlockMaterialTextureSet(string textureSetName)
    {
        try
        {
            // Find all items.level.json files (same pattern as CopyGroundCovers)
            var levelJsonFiles = new List<FileInfo>();
            var dirInfo = new DirectoryInfo(_targetLevelPath);

            if (!dirInfo.Exists)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Target level path not found: {_targetLevelPath}");
                return;
            }

            // Search for items.level.json files
            FindItemsLevelJsonFiles(dirInfo, levelJsonFiles);

            if (!levelJsonFiles.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No items.level.json files found in target level.");
                return;
            }

            var updatedCount = 0;

            foreach (var levelJsonFile in levelJsonFiles)
            {
                // Check if this file contains TerrainBlock entries
                if (!File.Exists(levelJsonFile.FullName))
                    continue;

                var fileContent = File.ReadAllText(levelJsonFile.FullName);
                if (!fileContent.Contains("\"class\":\"TerrainBlock\"") &&
                    !fileContent.Contains("\"class\": \"TerrainBlock\""))
                    continue;

                // Process the file
                if (UpdateTerrainBlockInFile(levelJsonFile.FullName, textureSetName))
                    updatedCount++;
            }

            if (updatedCount > 0)
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Updated materialTextureSet to '{textureSetName}' in {updatedCount} file(s)");
            else
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    "No TerrainBlock objects found to update.");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to update TerrainBlock materialTextureSet: {ex.Message}");
        }
    }

    /// <summary>
    ///     Recursively finds all items.level.json files
    /// </summary>
    private void FindItemsLevelJsonFiles(DirectoryInfo root, List<FileInfo> results)
    {
        try
        {
            // Get files in current directory
            var files = root.GetFiles("items.level.json", SearchOption.TopDirectoryOnly);
            results.AddRange(files);

            // Recurse into subdirectories
            foreach (var subDir in root.GetDirectories()) FindItemsLevelJsonFiles(subDir, results);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (DirectoryNotFoundException)
        {
            // Skip directories that don't exist
        }
    }

    /// <summary>
    ///     Updates the materialTextureSet property in TerrainBlock objects within a single file
    ///     File format is multiline JSON (one JSON object per line, NO PRETTY PRINTING)
    /// </summary>
    private bool UpdateTerrainBlockInFile(string filePath, string textureSetName)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var updated = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Skip empty lines
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    // Parse the JSON line
                    using var jsonDoc = JsonDocument.Parse(line);
                    var root = jsonDoc.RootElement;

                    // Check if this is a TerrainBlock
                    if (root.TryGetProperty("class", out var classElement) &&
                        classElement.GetString() == "TerrainBlock")
                    {
                        // Parse as JsonNode for modification
                        var jsonNode = JsonNode.Parse(line);
                        if (jsonNode == null)
                            continue;

                        // Update or add the materialTextureSet property
                        jsonNode["materialTextureSet"] = textureSetName;

                        // CRITICAL: Write as single-line JSON (no pretty printing)
                        var jsonOptions = new JsonSerializerOptions
                        {
                            WriteIndented = false // Single line output
                        };
                        lines[i] = jsonNode.ToJsonString(jsonOptions);
                        updated = true;

                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Updated TerrainBlock in {Path.GetFileName(filePath)}: materialTextureSet = '{textureSetName}'");
                    }
                }
                catch (JsonException)
                {
                    // Skip lines that aren't valid JSON
                }
            }

            // Write back to file if we made changes
            if (updated)
            {
                File.WriteAllLines(filePath, lines);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error updating file {filePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Checks if the target level already has a TerrainMaterialTextureSet configured
    /// </summary>
    public bool HasTerrainMaterialTextureSet()
    {
        try
        {
            var targetFile = new FileInfo(_targetMaterialsJsonPath);

            if (!targetFile.Exists)
                return false;

            var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetFile.FullName);

            // Check if any object has class "TerrainMaterialTextureSet"
            foreach (var prop in jsonNode.AsObject())
                if (prop.Value?["class"]?.ToString() == "TerrainMaterialTextureSet")
                    return true;

            return false;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error checking for TerrainMaterialTextureSet: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Updates the baseTexSize in an existing TerrainMaterialTextureSet to match the target terrain size.
    ///     The baseTexSize must match the terrain size for proper rendering.
    /// </summary>
    /// <param name="terrainSize">The target terrain size (power of 2, e.g., 1024, 2048, 4096)</param>
    /// <returns>True if the update was successful, false otherwise</returns>
    public bool UpdateTerrainMaterialTextureSetSize(int terrainSize)
    {
        try
        {
            var targetFile = new FileInfo(_targetMaterialsJsonPath);

            if (!targetFile.Exists)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Target materials file not found: {targetFile.FullName}. Cannot update TerrainMaterialTextureSet.");
                return false;
            }

            // Load the existing JSON
            var jsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetFile.FullName);
            var updated = false;

            // Find and update all TerrainMaterialTextureSet objects
            foreach (var prop in jsonNode.AsObject().ToList())
            {
                if (prop.Value?["class"]?.ToString() != "TerrainMaterialTextureSet")
                    continue;

                var textureSetNode = prop.Value;

                // Get current baseTexSize for comparison
                var currentBaseTexSize = 0;
                if (textureSetNode["baseTexSize"] is JsonArray currentArray && currentArray.Count > 0)
                {
                    currentBaseTexSize = currentArray[0]?.GetValue<int>() ?? 0;
                }

                // Only update if the size is different
                if (currentBaseTexSize == terrainSize)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"TerrainMaterialTextureSet '{prop.Key}' already has correct baseTexSize: {terrainSize}x{terrainSize}");
                    continue;
                }

                // Update baseTexSize to match terrain size
                textureSetNode["baseTexSize"] = new JsonArray(terrainSize, terrainSize);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Updated TerrainMaterialTextureSet '{prop.Key}' baseTexSize: {currentBaseTexSize}x{currentBaseTexSize} -> {terrainSize}x{terrainSize}");

                updated = true;
            }

            if (updated)
            {
                // Write the updated JSON back to the file
                File.WriteAllText(targetFile.FullName,
                    jsonNode.ToJsonString(BeamJsonOptions.GetJsonSerializerOptions()));

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Successfully updated TerrainMaterialTextureSet baseTexSize to {terrainSize}x{terrainSize}");
            }

            return updated;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to update TerrainMaterialTextureSet size: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Adds or updates the TerrainMaterialTextureSet to ensure baseTexSize matches the terrain size.
    ///     If no TerrainMaterialTextureSet exists, creates one. If it exists, updates the baseTexSize.
    /// </summary>
    /// <param name="terrainSize">The target terrain size (power of 2)</param>
    public void EnsureTerrainMaterialTextureSetSize(int terrainSize)
    {
        if (HasTerrainMaterialTextureSet())
        {
            // Update existing
            UpdateTerrainMaterialTextureSetSize(terrainSize);
        }
        else
        {
            // Create new with correct size
            AddTerrainMaterialTextureSet(terrainSize, terrainSize, terrainSize);
        }
    }
}