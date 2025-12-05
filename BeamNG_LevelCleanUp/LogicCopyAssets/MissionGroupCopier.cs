using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of MissionGroup data and associated files for Create Level wizard
/// </summary>
public class MissionGroupCopier
{
    private readonly List<Asset> _missionGroupAssets;
    private readonly string _sourceLevelPath;
    private readonly string _sourceLevelNamePath;
    private readonly string _targetLevelPath;
    private readonly string _targetLevelNamePath;
    private readonly string _targetLevelName;
    private readonly List<MaterialJson> _sourceMaterials;

    public MissionGroupCopier(
        List<Asset> missionGroupAssets,
        string sourceLevelPath,
        string sourceLevelNamePath,
        string targetLevelPath,
        string targetLevelNamePath,
        string targetLevelName,
        List<MaterialJson> sourceMaterials = null)
    {
        _missionGroupAssets = missionGroupAssets;
        _sourceLevelPath = sourceLevelPath;
        _sourceLevelNamePath = sourceLevelNamePath;
        _targetLevelPath = targetLevelPath;
        _targetLevelNamePath = targetLevelNamePath;
        _targetLevelName = targetLevelName;
        _sourceMaterials = sourceMaterials ?? new List<MaterialJson>();
    }

    /// <summary>
    ///     Copies all MissionGroup data and associated files to the target level
    /// </summary>
    public void CopyMissionGroupData()
    {
        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Copying MissionGroup data...");

            // 1. Create target directory structure
            CreateDirectoryStructure();

            // 2. Copy referenced files
            CopyReferencedFiles();

            // 3. Copy referenced materials (cubemaps, moon materials, flare types)
            CopyReferencedMaterials();

            // 4. Write MissionGroup items to target
            WriteMissionGroupItems();

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Successfully copied {_missionGroupAssets.Count} MissionGroup objects");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error copying MissionGroup data: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Creates the necessary directory structure in the target level
    /// </summary>
    private void CreateDirectoryStructure()
    {
        var directories = new[]
        {
            Path.Join(_targetLevelNamePath, "main", "MissionGroup", "Level_object"),
            Path.Join(_targetLevelNamePath, "art", "skies"),
            Path.Join(_targetLevelNamePath, "art", "terrains")
        };

        foreach (var dir in directories)
        {
            Directory.CreateDirectory(dir);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Created directory: {dir}", true);
        }
    }

    /// <summary>
    ///     Copies materials referenced by MissionGroup objects
    ///     (cubemaps, moon materials, flare types, etc.)
    /// </summary>
    private void CopyReferencedMaterials()
    {
        if (_sourceMaterials == null || !_sourceMaterials.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "No source materials available for referenced material copying", true);
            return;
        }

        var materialCopier = new ReferencedMaterialCopier(
            _sourceLevelPath,
            new DirectoryInfo(_sourceLevelNamePath).Name,
            _targetLevelNamePath,
            _targetLevelName,
            _sourceMaterials);

        var copiedMaterials = materialCopier.CopyReferencedMaterials(_missionGroupAssets);

        if (copiedMaterials.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Copied {copiedMaterials.Count} referenced material(s): {string.Join(", ", copiedMaterials)}");
        }
    }

    /// <summary>
    ///     Copies all files referenced by MissionGroup assets
    /// </summary>
    private void CopyReferencedFiles()
    {
        var copiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in _missionGroupAssets)
        {
            // TerrainBlock: Copy .ter file and .terrain.json
            if (!string.IsNullOrEmpty(asset.TerrainFile))
            {
                CopyTerrainFiles(asset.TerrainFile, copiedFiles);
            }

            // ScatterSky: Copy gradient files
            CopyGradientFile(asset.AmbientScaleGradientFile, copiedFiles);
            CopyGradientFile(asset.ColorizeGradientFile, copiedFiles);
            CopyGradientFile(asset.FogScaleGradientFile, copiedFiles);
            CopyGradientFile(asset.NightFogGradientFile, copiedFiles);
            CopyGradientFile(asset.NightGradientFile, copiedFiles);
            CopyGradientFile(asset.SunScaleGradientFile, copiedFiles);

            // CloudLayer: Copy texture files
            if (!string.IsNullOrEmpty(asset.Texture))
            {
                CopyTextureFile(asset.Texture, copiedFiles);
            }

            // Material references will be handled separately by MaterialCopier
            // (FlareType, NightCubemap, GlobalEnviromentMap, MoonMat)
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Copied {copiedFiles.Count} referenced file(s)");
    }

    /// <summary>
    ///     Copies terrain file (.ter) and its configuration (.terrain.json)
    /// </summary>
    private void CopyTerrainFiles(string terrainFilePath, HashSet<string> copiedFiles)
    {
        try
        {
            // Resolve the source path
            var sourceTerrainPath = PathResolver.ResolvePath(_sourceLevelPath, terrainFilePath, false);

            if (File.Exists(sourceTerrainPath))
            {
                // Copy .ter file
                var fileName = Path.GetFileName(terrainFilePath);
                var targetPath = Path.Join(_targetLevelNamePath, fileName);
                File.Copy(sourceTerrainPath, targetPath, true);
                copiedFiles.Add(terrainFilePath);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copied terrain file: {fileName}", true);

                // Copy corresponding .terrain.json file
                var terrainJsonPath = terrainFilePath.Replace(".ter", ".terrain.json");
                var sourceTerrainJsonPath = PathResolver.ResolvePath(_sourceLevelPath, terrainJsonPath, false);

                if (File.Exists(sourceTerrainJsonPath))
                {
                    var targetJsonPath = Path.Join(_targetLevelNamePath, Path.GetFileName(terrainJsonPath));

                    // Read, update paths, and write .terrain.json
                    var jsonContent = File.ReadAllText(sourceTerrainJsonPath);
                    var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;
                    jsonContent = jsonContent.Replace($"/levels/{sourceLevelName}/", $"/levels/{_targetLevelName}/");

                    File.WriteAllText(targetJsonPath, jsonContent);
                    copiedFiles.Add(terrainJsonPath);

                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Copied terrain config: {Path.GetFileName(terrainJsonPath)}", true);
                }
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Terrain file not found: {terrainFilePath}");
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error copying terrain file {terrainFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Copies a gradient file (PNG)
    /// </summary>
    private void CopyGradientFile(string gradientFilePath, HashSet<string> copiedFiles)
    {
        if (string.IsNullOrEmpty(gradientFilePath) || copiedFiles.Contains(gradientFilePath))
            return;

        try
        {
            var sourcePath = PathResolver.ResolvePath(_sourceLevelPath, gradientFilePath, false);

            if (File.Exists(sourcePath))
            {
                var targetPath = Path.Join(_targetLevelNamePath, "art", "skies", Path.GetFileName(gradientFilePath));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, true);
                copiedFiles.Add(gradientFilePath);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copied gradient: {Path.GetFileName(gradientFilePath)}", true);
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not copy gradient file {gradientFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Copies a texture file (DDS, PNG, etc.)
    /// </summary>
    private void CopyTextureFile(string textureFilePath, HashSet<string> copiedFiles)
    {
        if (string.IsNullOrEmpty(textureFilePath) || copiedFiles.Contains(textureFilePath))
            return;

        try
        {
            var sourcePath = PathResolver.ResolvePath(_sourceLevelPath, textureFilePath, false);

            if (File.Exists(sourcePath))
            {
                // Extract just the filename and put it in art/skies folder
                var fileName = Path.GetFileName(textureFilePath);
                var targetPath = Path.Join(_targetLevelNamePath, "art", "skies", fileName);

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Copy(sourcePath, targetPath, true);
                copiedFiles.Add(textureFilePath);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copied texture: {fileName}", true);
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not copy texture file {textureFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Writes MissionGroup items to items.level.json in target level
    /// </summary>
    private void WriteMissionGroupItems()
    {
        try
        {
            var missionGroupPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "Level_object", "items.level.json");
            var lines = new List<string>();

            // Get source level name for path replacement
            var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;

            // Read the original MissionGroup items.level.json files from source
            // BeamNG stores these in subdirectories under Level_object/
            var sourceMissionGroupPath = Path.Join(_sourceLevelNamePath, "main", "MissionGroup");
            var sourceLevelObjectPath = Path.Join(sourceMissionGroupPath, "Level_object");

            // Check if Level_object directory exists
            if (!Directory.Exists(sourceLevelObjectPath))
            {
                // Fallback: try direct MissionGroup path
                sourceLevelObjectPath = sourceMissionGroupPath;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Reading source MissionGroup from: {sourceLevelObjectPath}", true);

            // Find all items.level.json files in subdirectories
            var itemsFiles = Directory.GetFiles(sourceLevelObjectPath, "items.level.json", SearchOption.AllDirectories);

            if (itemsFiles.Length == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No items.level.json files found in source MissionGroup");
                return;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found {itemsFiles.Length} items.level.json file(s) in source MissionGroup", true);

            // Filter to only include lines for our allowed classes
            var allowedClasses = new HashSet<string>
            {
                "LevelInfo",
                "TerrainBlock",
                "TimeOfDay",
                "CloudLayer",
                "ScatterSky",
                "ForestWindEmitter",
                "Forest"
            };

            int totalLines = 0;
            int processedCount = 0;
            int skippedCount = 0;

            // Read from all items.level.json files
            foreach (var itemsFile in itemsFiles)
            {
                var sourceLines = File.ReadAllLines(itemsFile);
                totalLines += sourceLines.Length;

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Reading {sourceLines.Length} lines from {Path.GetFileName(Path.GetDirectoryName(itemsFile))}/items.level.json", true);

                foreach (var line in sourceLines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        // Parse as JsonDocument to preserve all original fields
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        // Check if this is one of our allowed classes
                        if (root.TryGetProperty("class", out var classProperty))
                        {
                            var className = classProperty.GetString();
                            if (!allowedClasses.Contains(className))
                            {
                                skippedCount++;
                                continue; // Skip classes we don't want to copy
                            }

                            processedCount++;
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                $"Processing {className} object", true);
                        }
                        else
                        {
                            continue; // Skip if no class property
                        }

                        // Convert to mutable dictionary
                        var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                        if (jsonDict == null)
                            continue;

                        // Update path fields - replace source level name with target level name
                        UpdatePathField(jsonDict, "terrainFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "texture", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "ambientScaleGradientFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "colorizeGradientFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "fogScaleGradientFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "nightFogGradientFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "nightGradientFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "sunScaleGradientFile", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "flareType", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "nightCubemap", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "globalEnviromentMap", sourceLevelName, _targetLevelName);
                        UpdatePathField(jsonDict, "moonMat", sourceLevelName, _targetLevelName);

                        // Special handling for CloudLayer texture - update to new location in art/skies
                        if (jsonDict.TryGetValue("class", out var classElement) &&
                            classElement.GetString() == "CloudLayer" &&
                            jsonDict.TryGetValue("texture", out var textureElement))
                        {
                            var texturePath = textureElement.GetString();
                            if (!string.IsNullOrEmpty(texturePath))
                            {
                                var textureFileName = Path.GetFileName(texturePath);
                                var newTexturePath = $"/levels/{_targetLevelName}/art/skies/{textureFileName}";
                                jsonDict["texture"] = JsonSerializer.SerializeToElement(newTexturePath);

                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    $"Updated CloudLayer texture: {textureFileName}", true);
                            }
                        }
                        jsonDict["__parent"] = JsonSerializer.SerializeToElement("Level_object");
                        // Serialize back to JSON (one line, preserving all original fields)
                        var updatedJson = JsonSerializer.Serialize(jsonDict, BeamJsonOptions.GetJsonSerializerOneLineOptions());
                        lines.Add(updatedJson);
                    }
                    catch (JsonException ex)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"Could not parse MissionGroup line: {ex.Message}");
                        continue;
                    }
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Read {totalLines} total lines, processed {processedCount} objects, skipped {skippedCount} objects");

            if (lines.Count == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No allowed classes found in source files");
                return;
            }

            // Write all lines to single file
            File.WriteAllLines(missionGroupPath, lines);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Wrote {lines.Count} MissionGroup items to: {missionGroupPath}");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error writing MissionGroup items: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Updates a path field in the JSON dictionary if it exists
    /// </summary>
    private void UpdatePathField(Dictionary<string, JsonElement> jsonDict, string fieldName, string oldLevelName, string newLevelName)
    {
        if (jsonDict.TryGetValue(fieldName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                // Replace old level name with new level name in path
                var updatedValue = value.Replace($"/levels/{oldLevelName}/", $"/levels/{newLevelName}/");
                jsonDict[fieldName] = JsonSerializer.SerializeToElement(updatedValue);
            }
        }
    }
}
