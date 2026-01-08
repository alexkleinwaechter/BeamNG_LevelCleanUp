using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyForest;

/// <summary>
/// Orchestrates copying of forest brushes between levels.
/// Copies ForestBrush definitions, ForestItemData, and all associated shape files and materials.
/// Uses DaeScanner to read material names from DAE files and looks them up in MaterialsJsonCopy.
/// </summary>
public class ForestBrushCopier
{
    private readonly string _sourceLevelPath;
    private readonly string _targetLevelPath;
    private readonly PathConverter _pathConverter;
    private readonly FileCopyHandler _fileCopyHandler;
    private readonly MaterialCopier _materialCopier;
    private readonly DaeCopier _daeCopier;

    // Track what we've already copied to avoid duplicates
    private readonly HashSet<string> _copiedItemDataNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _copiedShapeFiles = new(StringComparer.OrdinalIgnoreCase);
    
    // Cache the resolved source paths to target paths for BeamNG JSON path conversion
    private readonly Dictionary<string, string> _sourceToTargetPathCache = new(StringComparer.OrdinalIgnoreCase);

    public ForestBrushCopier(
        string sourceLevelPath,
        string targetLevelPath,
        PathConverter pathConverter,
        FileCopyHandler fileCopyHandler,
        MaterialCopier materialCopier,
        DaeCopier daeCopier)
    {
        _sourceLevelPath = sourceLevelPath;
        _targetLevelPath = targetLevelPath;
        _pathConverter = pathConverter;
        _fileCopyHandler = fileCopyHandler;
        _materialCopier = materialCopier;
        _daeCopier = daeCopier;
    }

    /// <summary>
    /// Copies selected forest brushes to target level
    /// </summary>
    public bool CopyBrushes(List<CopyAsset> selectedAssets)
    {
        try
        {
            // 1. Load source managedItemData.json
            var sourceItemDataPath = FindManagedItemDataFile(_sourceLevelPath);
            var sourceItemData = LoadManagedItemData(sourceItemDataPath);

            // 2. Collect all ForestItemData to copy
            var itemDataToCopy = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            var brushesToCopy = new List<CopyAsset>();

            foreach (var asset in selectedAssets.Where(a => a.CopyAssetType == CopyAssetType.ForestBrush))
            {
                if (asset.ForestBrushInfo == null) continue;

                brushesToCopy.Add(asset);

                foreach (var itemDataName in asset.ForestBrushInfo.ReferencedItemDataNames)
                {
                    if (_copiedItemDataNames.Contains(itemDataName)) continue;

                    if (sourceItemData.TryGetValue(itemDataName, out var itemDataNode))
                    {
                        itemDataToCopy[itemDataName] = itemDataNode;
                        _copiedItemDataNames.Add(itemDataName);
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"ForestItemData '{itemDataName}' not found in source level");
                    }
                }
            }

            if (!brushesToCopy.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, "No forest brushes selected for copying");
                return true;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Copying {brushesToCopy.Count} forest brushes with {itemDataToCopy.Count} item data definitions...");

            // 3. Copy all shape files and their materials using existing DaeCopier
            _materialCopier.BeginBatch();

            foreach (var (name, itemDataNode) in itemDataToCopy)
            {
                var shapeFile = itemDataNode["shapeFile"]?.GetValue<string>();
                if (string.IsNullOrEmpty(shapeFile)) continue;

                // Strip .link extension if present - BeamNG uses .link files as redirects
                shapeFile = FileUtils.StripLinkExtension(shapeFile);

                if (_copiedShapeFiles.Contains(shapeFile)) continue;
                _copiedShapeFiles.Add(shapeFile);

                if (!CopyShapeFileWithMaterials(shapeFile, name))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Failed to copy shape file for {name}");
                    // Continue with other files, don't fail entire operation
                }
            }

            _materialCopier.EndBatch();

            // 4. Write/merge managedItemData.json in target
            if (!MergeManagedItemData(itemDataToCopy))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Failed to merge managedItemData.json - some item data may be missing");
            }

            // 5. Write/merge main.forestbrushes4.json in target
            if (!MergeForestBrushes(brushesToCopy))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    "Failed to merge forest brushes file");
                return false;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Successfully copied {brushesToCopy.Count} forest brushes and {itemDataToCopy.Count} item definitions");

            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error copying forest brushes: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Loads managedItemData.json as JsonNode dictionary for flexible manipulation
    /// </summary>
    private Dictionary<string, JsonNode> LoadManagedItemData(string filePath)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return result;

        try
        {
            var json = File.ReadAllText(filePath);
            var node = JsonNode.Parse(json);

            if (node is JsonObject jsonObj)
            {
                foreach (var prop in jsonObj)
                {
                    if (prop.Value != null)
                        result[prop.Key] = prop.Value.DeepClone();
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to load managedItemData.json: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Copies a shape file (.dae/.cdae) and its associated materials using the existing DaeCopier pattern.
    /// Uses DaeScanner to read material names from the DAE file, then looks them up in MaterialsJsonCopy.
    /// </summary>
    private bool CopyShapeFileWithMaterials(string shapeFile, string itemDataName)
    {
        try
        {
            // Resolve source path from BeamNG path to physical path
            var sourceShapePath = PathResolver.ResolvePath(_sourceLevelPath, shapeFile, true);
            if (!File.Exists(sourceShapePath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Shape file not found: {shapeFile}");
                return true; // Continue with other files
            }

            // Calculate target path using PathConverter
            var targetShapePath = _pathConverter.GetTargetFileName(sourceShapePath);
            
            // Cache the mapping for later use when converting BeamNG paths
            _sourceToTargetPathCache[sourceShapePath] = targetShapePath;

            // Use DaeScanner to get material names from the DAE file (same pattern as BeamFileReader.CopyDae)
            var daeScanner = new DaeScanner(_sourceLevelPath, sourceShapePath, true);
            var daeMaterials = daeScanner.GetMaterials();

            // Look up materials in MaterialsJsonCopy (populated by ReadSourceMaterialsJson)
            var materialsJson = BeamFileReader.MaterialsJsonCopy
                .Where(m => daeMaterials.Select(x => x.MaterialName.ToUpper()).Contains(m.Name.ToUpper()))
                .Distinct()
                .ToList();

            if (!materialsJson.Any() && daeMaterials.Any())
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"No materials found in MaterialsJsonCopy for shape file {Path.GetFileName(sourceShapePath)}. " +
                    $"DAE references: {string.Join(", ", daeMaterials.Select(m => m.MaterialName))}");
            }

            // Create a CopyAsset with the materials - this follows the same pattern as CopyDae in BeamFileReader
            var targetPath = Path.GetDirectoryName(targetShapePath) ?? 
                Path.Join(_targetLevelPath, "art", "forest", $"{Constants.MappingToolsPrefix}{PathResolver.LevelNameCopyFrom}");

            var copyAsset = new CopyAsset
            {
                CopyAssetType = CopyAssetType.Dae,
                Name = Path.GetFileName(sourceShapePath),
                Materials = materialsJson,
                MaterialsDae = daeMaterials,
                TargetPath = targetPath,
                DaeFilePath = sourceShapePath
            };

            // Use DaeCopier to copy the DAE file and its materials
            var success = _daeCopier.Copy(copyAsset);

            if (success)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"  Copied shape: {Path.GetFileName(sourceShapePath)} with {materialsJson.Count} material(s)");
            }

            return success;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error copying shape file {shapeFile}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Merges ForestItemData into target's managedItemData.json
    /// </summary>
    private bool MergeManagedItemData(Dictionary<string, JsonNode> itemDataToCopy)
    {
        if (!itemDataToCopy.Any()) return true;

        try
        {
            // Ensure art/forest directory exists
            var targetDir = Path.Combine(_targetLevelPath, "art", "forest");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, "managedItemData.json");

            // Load or create target
            JsonNode targetNode;
            if (File.Exists(targetPath))
            {
                var json = File.ReadAllText(targetPath);
                targetNode = JsonNode.Parse(json) ?? new JsonObject();
            }
            else
            {
                targetNode = new JsonObject();
            }

            if (targetNode is not JsonObject targetObj)
            {
                targetObj = new JsonObject();
            }

            // Merge item data
            foreach (var (name, itemData) in itemDataToCopy)
            {
                if (!targetObj.ContainsKey(name))
                {
                    // Clone and update
                    var itemCopy = itemData.DeepClone();

                    if (itemCopy is JsonObject itemObj)
                    {
                        // Generate new persistentId
                        itemObj["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant();

                        // Update shapeFile path to target level using PathConverter
                        if (itemObj["shapeFile"] is JsonValue shapeValue)
                        {
                            var oldPath = shapeValue.GetValue<string>();
                            var newPath = ConvertShapeFilePath(oldPath);
                            itemObj["shapeFile"] = newPath;
                        }
                    }

                    targetObj.Add(name, itemCopy);
                }
            }

            // Write merged data
            var options = BeamJsonOptions.GetJsonSerializerOptions();
            File.WriteAllText(targetPath, targetObj.ToJsonString(options));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Merged {itemDataToCopy.Count} ForestItemData entries into managedItemData.json");

            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error merging managedItemData.json: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Converts a BeamNG shapeFile path from source level to target level.
    /// Uses the cached source-to-target path mapping from the copy operation.
    /// </summary>
    private string ConvertShapeFilePath(string sourceBeamNgPath)
    {
        if (string.IsNullOrEmpty(sourceBeamNgPath)) return sourceBeamNgPath;

        try
        {
            // Strip .link extension if present
            var cleanPath = FileUtils.StripLinkExtension(sourceBeamNgPath);
            
            // Resolve the source BeamNG path to a physical path
            var sourcePhysicalPath = PathResolver.ResolvePath(_sourceLevelPath, cleanPath, true);
            
            // Check if we have this in our cache from when we copied the file
            if (_sourceToTargetPathCache.TryGetValue(sourcePhysicalPath, out var targetPhysicalPath))
            {
                // Convert the physical target path to BeamNG JSON format
                var targetBeamNgPath = _pathConverter.GetBeamNgJsonPathOrFileName(targetPhysicalPath, removeExtension: false);
                
                // Ensure it starts with / for BeamNG absolute path format
                if (!string.IsNullOrEmpty(targetBeamNgPath) && !targetBeamNgPath.StartsWith("/"))
                {
                    targetBeamNgPath = "/" + targetBeamNgPath;
                }
                
                return targetBeamNgPath;
            }
            
            // Fallback: If not in cache, use PathConverter to compute the target path
            var computedTargetPath = _pathConverter.GetTargetFileName(sourcePhysicalPath);
            var computedBeamNgPath = _pathConverter.GetBeamNgJsonPathOrFileName(computedTargetPath, removeExtension: false);
            
            if (!string.IsNullOrEmpty(computedBeamNgPath) && !computedBeamNgPath.StartsWith("/"))
            {
                computedBeamNgPath = "/" + computedBeamNgPath;
            }
            
            return computedBeamNgPath;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error converting shape file path {sourceBeamNgPath}: {ex.Message}");
            return sourceBeamNgPath;
        }
    }

    /// <summary>
    /// Merges ForestBrush definitions into target's main.forestbrushes4.json (NDJSON format).
    /// Uses RawJson from source to preserve unknown properties, only updating persistentId and __parent.
    /// </summary>
    private bool MergeForestBrushes(List<CopyAsset> brushesToCopy)
    {
        try
        {
            var targetPath = Path.Combine(_targetLevelPath, "main.forestbrushes4.json");

            // Read existing lines
            var existingLines = new List<string>();
            var existingBrushNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasForestBrushGroup = false;

            if (File.Exists(targetPath))
            {
                foreach (var line in File.ReadAllLines(targetPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    existingLines.Add(line);

                    try
                    {
                        using var doc = JsonDocument.Parse(line, BeamJsonOptions.GetJsonDocumentOptions());
                        if (doc.RootElement.TryGetProperty("class", out var cls))
                        {
                            var classStr = cls.GetString();

                            if (classStr == "ForestBrush")
                            {
                                if (doc.RootElement.TryGetProperty("name", out var name))
                                    existingBrushNames.Add(name.GetString() ?? "");

                                if (doc.RootElement.TryGetProperty("internalName", out var iname))
                                    existingBrushNames.Add(iname.GetString() ?? "");
                            }

                            if (classStr == "SimGroup" &&
                                doc.RootElement.TryGetProperty("name", out var gname) &&
                                gname.GetString() == "ForestBrushGroup")
                            {
                                hasForestBrushGroup = true;
                            }
                        }
                    }
                    catch
                    {
                        /* ignore parse errors */
                    }
                }
            }

            // Generate new lines for brushes and elements
            var newLines = new List<string>();
            var jsonOptions = BeamJsonOptions.GetJsonSerializerOneLineOptions();

            // Add ForestBrushGroup if not exists
            if (!hasForestBrushGroup)
            {
                var groupObj = new JsonObject
                {
                    ["name"] = "ForestBrushGroup",
                    ["class"] = "SimGroup",
                    ["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant()
                };
                newLines.Add(groupObj.ToJsonString(jsonOptions));
            }

            foreach (var asset in brushesToCopy)
            {
                var brush = asset.ForestBrushInfo;
                if (brush == null) continue;

                // Skip if already exists
                if (existingBrushNames.Contains(brush.Name) || existingBrushNames.Contains(brush.InternalName))
                    continue;

                // Use RawJson to preserve unknown properties, updating only persistentId and __parent
                var brushLine = CreateBrushLineFromRawJson(brush, jsonOptions);
                newLines.Add(brushLine);

                // Create element lines from RawJson
                foreach (var element in brush.Elements)
                {
                    var elementLine = CreateElementLineFromRawJson(element, brush.Name, jsonOptions);
                    newLines.Add(elementLine);
                }
            }

            // Combine and write (existing lines first, then new lines)
            var allLines = existingLines.Concat(newLines);
            File.WriteAllLines(targetPath, allLines);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Added {brushesToCopy.Count} brushes to main.forestbrushes4.json");

            return true;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error merging forest brushes: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates a brush JSON line from RawJson, preserving unknown properties and only updating persistentId.
    /// Falls back to creating from known properties if RawJson is not available.
    /// </summary>
    private string CreateBrushLineFromRawJson(ForestBrushInfo brush, JsonSerializerOptions jsonOptions)
    {
        if (!string.IsNullOrEmpty(brush.RawJson))
        {
            try
            {
                // Parse the raw JSON and update only the fields that need to change
                var node = JsonNode.Parse(brush.RawJson);
                if (node is JsonObject obj)
                {
                    // Generate new persistentId
                    obj["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant();
                    // Ensure __parent is set to ForestBrushGroup
                    obj["__parent"] = "ForestBrushGroup";
                    return obj.ToJsonString(jsonOptions);
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Failed to parse raw JSON for brush '{brush.Name}', using fallback: {ex.Message}");
            }
        }

        // Fallback: create from known properties (loses unknown properties)
        var brushObj = new JsonObject
        {
            ["name"] = brush.Name,
            ["internalName"] = brush.InternalName,
            ["class"] = "ForestBrush",
            ["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant(),
            ["__parent"] = "ForestBrushGroup"
        };

        // If brush has direct forestItemData reference (single-item brush)
        if (!string.IsNullOrEmpty(brush.DirectForestItemData) && brush.Elements.Count == 0)
        {
            brushObj["forestItemData"] = brush.DirectForestItemData;
        }

        return brushObj.ToJsonString(jsonOptions);
    }

    /// <summary>
    /// Creates an element JSON line from RawJson, preserving unknown properties and updating persistentId/__parent.
    /// Falls back to creating from known properties if RawJson is not available.
    /// </summary>
    private string CreateElementLineFromRawJson(ForestBrushElementInfo element, string parentBrushName, JsonSerializerOptions jsonOptions)
    {
        if (!string.IsNullOrEmpty(element.RawJson))
        {
            try
            {
                // Parse the raw JSON and update only the fields that need to change
                var node = JsonNode.Parse(element.RawJson);
                if (node is JsonObject obj)
                {
                    // Generate new persistentId
                    obj["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant();
                    // Update __parent to reference the new brush name
                    obj["__parent"] = parentBrushName;
                    return obj.ToJsonString(jsonOptions);
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Failed to parse raw JSON for element '{element.InternalName}', using fallback: {ex.Message}");
            }
        }

        // Fallback: create from known properties (loses unknown properties)
        var elemObj = new JsonObject
        {
            ["internalName"] = element.InternalName,
            ["class"] = "ForestBrushElement",
            ["persistentId"] = Guid.NewGuid().ToString().ToLowerInvariant(),
            ["__parent"] = parentBrushName,
            ["forestItemData"] = element.ForestItemDataRef
        };

        // Add optional properties if set
        if (element.ScaleMin.HasValue) elemObj["scaleMin"] = element.ScaleMin.Value;
        if (element.ScaleMax.HasValue) elemObj["scaleMax"] = element.ScaleMax.Value;
        if (element.Probability.HasValue) elemObj["probability"] = element.Probability.Value;
        if (element.SinkMin.HasValue) elemObj["sinkMin"] = element.SinkMin.Value;
        if (element.SinkMax.HasValue) elemObj["sinkMax"] = element.SinkMax.Value;
        if (element.SlopeMin.HasValue) elemObj["slopeMin"] = element.SlopeMin.Value;
        if (element.SlopeMax.HasValue) elemObj["slopeMax"] = element.SlopeMax.Value;
        if (element.ElevationMin.HasValue) elemObj["elevationMin"] = element.ElevationMin.Value;
        if (element.ElevationMax.HasValue) elemObj["elevationMax"] = element.ElevationMax.Value;
        if (element.RotationRange.HasValue) elemObj["rotationRange"] = element.RotationRange.Value;

        return elemObj.ToJsonString(jsonOptions);
    }

    /// <summary>
    /// Finds the managedItemData.json file in a level
    /// </summary>
    private string FindManagedItemDataFile(string levelPath)
    {
        var candidates = new[]
        {
            Path.Combine(levelPath, "art", "forest", "managedItemData.json"),
            Path.Combine(levelPath, "forest", "managedItemData.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            var files = Directory.GetFiles(levelPath, "managedItemData.json", SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
