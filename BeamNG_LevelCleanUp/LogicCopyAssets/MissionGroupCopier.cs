using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of MissionGroup data and associated files for Create Level wizard.
///     This class properly recreates the MissionGroup hierarchy from the source level,
///     maintaining the folder structure and parent relationships.
/// </summary>
public class MissionGroupCopier
{
    private readonly List<Asset> _missionGroupAssets;
    private readonly string _sourceLevelNamePath;
    private readonly string _sourceLevelPath;
    private readonly List<MaterialJson> _sourceMaterials;
    private readonly string _targetLevelName;
    private readonly string _targetLevelNamePath;
    private readonly string _targetLevelPath;
    private readonly FileCopyHandler _fileCopyHandler;

    /// <summary>
    ///     Tracks SimGroup definitions parsed from source level.
    ///     Key: SimGroup name (e.g., "sky", "terrain", "vegetation")
    ///     Value: Tuple of (parent name, original JSON line)
    /// </summary>
    private readonly Dictionary<string, (string Parent, string OriginalJson)> _simGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Maps class types to their expected parent SimGroup names based on source level structure.
    ///     Populated during source level parsing.
    /// </summary>
    private readonly Dictionary<string, string> _classToParentMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Stores assets extracted from main.level.json (hierarchical JSON format).
    ///     This is the legacy format that some levels still use alongside or instead of items.level.json.
    /// </summary>
    private readonly List<Asset> _mainLevelJsonAssets = new();

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
        
        // Initialize FileCopyHandler for cross-level file extraction
        var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;
        _fileCopyHandler = new FileCopyHandler(sourceLevelName);
    }

    /// <summary>
    ///     Copies all MissionGroup data and associated files to the target level
    /// </summary>
    public void CopyMissionGroupData()
    {
        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Copying MissionGroup data...");

            // 0. Parse main.level.json if it exists (hierarchical JSON format - legacy)
            ParseMainLevelJson();

            // 1. Parse source level hierarchy to understand SimGroup structure
            ParseSourceHierarchy();

            // 2. Create target directory structure based on source hierarchy
            CreateDirectoryStructure();

            // 3. Copy referenced files (including from main.level.json)
            CopyReferencedFiles();

            // 4. Copy referenced materials (cubemaps, moon materials, flare types)
            CopyReferencedMaterials();

            // 5. Write MissionGroup items to target with proper hierarchy
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
    ///     Parses the main.level.json file (hierarchical JSON format).
    ///     This file exists in the root of the level folder and contains the scene hierarchy
    ///     with nested "childs" arrays. It's an alternative/legacy format to items.level.json files.
    ///     Important: This file may contain references to textures from other levels.
    /// </summary>
    private void ParseMainLevelJson()
    {
        var mainLevelJsonPath = Path.Join(_sourceLevelNamePath, "main.level.json");
        if (!File.Exists(mainLevelJsonPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "No main.level.json found (using items.level.json format)", true);
            return;
        }

        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Parsing main.level.json (hierarchical format)...", true);

            var jsonContent = File.ReadAllText(mainLevelJsonPath);
            using var doc = JsonDocument.Parse(jsonContent);
            
            // Recursively extract assets from the hierarchical structure
            ExtractAssetsFromHierarchy(doc.RootElement, null);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found {_mainLevelJsonAssets.Count} assets in main.level.json", true);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not parse main.level.json: {ex.Message}");
        }
    }

    /// <summary>
    ///     Recursively extracts assets from the hierarchical main.level.json structure.
    /// </summary>
    private void ExtractAssetsFromHierarchy(JsonElement element, string parentName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        // Check if this element has a class property (is an object we care about)
        if (element.TryGetProperty("class", out var classProperty))
        {
            var className = classProperty.GetString();
            
            // Extract asset data from this element
            try
            {
                var asset = element.Deserialize<Asset>(BeamJsonOptions.GetJsonSerializerOptions());
                if (asset != null)
                {
                    asset.__parent = parentName;
                    _mainLevelJsonAssets.Add(asset);
                    
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"  Found {className}: {asset.Name ?? "(unnamed)"}", true);
                }
            }
            catch
            {
                // Ignore deserialization errors for individual elements
            }

            // Get name for child elements
            var name = element.TryGetProperty("name", out var nameProperty) 
                ? nameProperty.GetString() 
                : null;

            // Process childs array if present
            if (element.TryGetProperty("childs", out var childsProperty) && 
                childsProperty.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in childsProperty.EnumerateArray())
                {
                    ExtractAssetsFromHierarchy(child, name ?? parentName);
                }
            }
        }
    }

    /// <summary>
    ///     Parses the source level's MissionGroup hierarchy to understand SimGroup structure.
    ///     This is essential for recreating the proper folder structure in the target level.
    /// </summary>
    private void ParseSourceHierarchy()
    {
        var sourceMissionGroupPath = Path.Join(_sourceLevelNamePath, "main", "MissionGroup");
        if (!Directory.Exists(sourceMissionGroupPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Source MissionGroup directory not found: {sourceMissionGroupPath}");
            return;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Parsing source level hierarchy...", true);

        // Recursively parse all items.level.json files to build SimGroup map
        ParseDirectoryForSimGroups(sourceMissionGroupPath, "MissionGroup");

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found {_simGroups.Count} SimGroup(s) in source level", true);

        // Log the hierarchy for debugging
        foreach (var sg in _simGroups)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"  SimGroup '{sg.Key}' -> parent '{sg.Value.Parent}'", true);
        }
    }

    /// <summary>
    ///     Recursively parses a directory and its subdirectories for SimGroup definitions.
    /// </summary>
    private void ParseDirectoryForSimGroups(string directoryPath, string expectedParent)
    {
        var itemsFilePath = Path.Join(directoryPath, "items.level.json");
        if (!File.Exists(itemsFilePath))
            return;

        try
        {
            var lines = File.ReadAllLines(itemsFilePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("class", out var classProperty))
                        continue;

                    var className = classProperty.GetString();
                    var name = root.TryGetProperty("name", out var nameProperty)
                        ? nameProperty.GetString()
                        : null;
                    var parent = root.TryGetProperty("__parent", out var parentProperty)
                        ? parentProperty.GetString()
                        : expectedParent;

                    if (className == "SimGroup" && !string.IsNullOrEmpty(name))
                    {
                        // Track this SimGroup for hierarchy reconstruction
                        if (!_simGroups.ContainsKey(name))
                        {
                            _simGroups[name] = (parent, line);
                        }

                        // Check if there's a subdirectory for this SimGroup
                        var subDirPath = Path.Join(directoryPath, name);
                        if (Directory.Exists(subDirPath))
                        {
                            ParseDirectoryForSimGroups(subDirPath, name);
                        }
                    }
                    else if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(parent))
                    {
                        // Track which parent each class type belongs to
                        // This helps us know where to place objects of each class
                        if (!_classToParentMap.ContainsKey(className))
                        {
                            _classToParentMap[className] = parent;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not parse {itemsFilePath}: {ex.Message}", true);
        }

        // Also check all subdirectories
        try
        {
            foreach (var subDir in Directory.GetDirectories(directoryPath))
            {
                var subDirName = new DirectoryInfo(subDir).Name;
                ParseDirectoryForSimGroups(subDir, subDirName);
            }
        }
        catch
        {
            // Ignore directory enumeration errors
        }
    }

    /// <summary>
    ///     Gets the ancestry chain for a SimGroup (from root to the SimGroup).
    /// </summary>
    private List<string> GetSimGroupAncestry(string simGroupName)
    {
        var ancestry = new List<string>();
        var current = simGroupName;

        while (!string.IsNullOrEmpty(current) && 
               !current.Equals("MissionGroup", StringComparison.OrdinalIgnoreCase))
        {
            ancestry.Insert(0, current);
            if (_simGroups.TryGetValue(current, out var info))
            {
                current = info.Parent;
            }
            else
            {
                break;
            }
        }

        return ancestry;
    }

    /// <summary>
    ///     Creates the necessary directory structure in the target level
    /// </summary>
    private void CreateDirectoryStructure()
    {
        // Create base directories
        var baseDirectories = new[]
        {
            Path.Join(_targetLevelNamePath, "main", "MissionGroup", "level_object"),
            Path.Join(_targetLevelNamePath, "main", "MissionGroup", "PlayerDropPoints"),
            Path.Join(_targetLevelNamePath, "art", "skies"),
            Path.Join(_targetLevelNamePath, "art", "terrains")
        };

        foreach (var dir in baseDirectories)
        {
            Directory.CreateDirectory(dir);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Created directory: {dir}", true);
        }

        // Create subdirectories for level_object based on source hierarchy
        // Find all SimGroups that are children of level_object (or Level_object)
        var levelObjectChildren = _simGroups
            .Where(sg => sg.Value.Parent.Equals("level_object", StringComparison.OrdinalIgnoreCase) ||
                        sg.Value.Parent.Equals("Level_object", StringComparison.OrdinalIgnoreCase))
            .Select(sg => sg.Key)
            .ToList();

        foreach (var childName in levelObjectChildren)
        {
            var childDir = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "level_object", childName.ToLowerInvariant());
            Directory.CreateDirectory(childDir);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Created hierarchy directory: {childDir}", true);
        }

        // Create hierarchy items.level.json files
        CreateMissionGroupHierarchy();
    }

    /// <summary>
    ///     Creates the MissionGroup hierarchy with proper SimGroup entries at each level
    /// </summary>
    private void CreateMissionGroupHierarchy()
    {
        // 1. Create main/items.level.json with MissionGroup entry
        var mainItemsPath = Path.Join(_targetLevelNamePath, "main", "items.level.json");
        var missionGroupEntry = new Dictionary<string, object>
        {
            { "name", "MissionGroup" },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "enabled", "1" }
        };
        var missionGroupJson =
            JsonSerializer.Serialize(missionGroupEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions());
        File.WriteAllText(mainItemsPath, missionGroupJson + Environment.NewLine);
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Created main/items.level.json with MissionGroup entry",
            true);

        // 2. Create main/MissionGroup/items.level.json with level_object and PlayerDropPoints entries
        var missionGroupItemsPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "items.level.json");
        var lines = new List<string>();

        // level_object SimGroup (lowercase for consistency)
        var levelObjectEntry = new Dictionary<string, object>
        {
            { "name", "level_object" },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "__parent", "MissionGroup" }
        };
        lines.Add(JsonSerializer.Serialize(levelObjectEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions()));

        // PlayerDropPoints SimGroup
        var playerDropPointsEntry = new Dictionary<string, object>
        {
            { "name", "PlayerDropPoints" },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "__parent", "MissionGroup" }
        };
        lines.Add(JsonSerializer.Serialize(playerDropPointsEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions()));

        File.WriteAllLines(missionGroupItemsPath, lines);
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Created main/MissionGroup/items.level.json with level_object and PlayerDropPoints entries", true);

        // 3. Create PlayerDropPoints/items.level.json with spawn_default_MT SpawnSphere
        CreateDefaultSpawnPoint();

        // 4. Create level_object/items.level.json with SimGroup entries for each child category
        CreateLevelObjectHierarchy();
    }

    /// <summary>
    ///     Creates the level_object hierarchy with SimGroup entries for sky, terrain, vegetation, etc.
    /// </summary>
    private void CreateLevelObjectHierarchy()
    {
        var levelObjectItemsPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "level_object", "items.level.json");
        var lines = new List<string>();

        // Standard categories for level_object
        var standardCategories = new[] { "sky", "terrain", "vegetation" };

        foreach (var category in standardCategories)
        {
            var categoryDir = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "level_object", category);
            Directory.CreateDirectory(categoryDir);

            var categoryEntry = new Dictionary<string, object>
            {
                { "name", category },
                { "class", "SimGroup" },
                { "persistentId", Guid.NewGuid().ToString() },
                { "__parent", "level_object" }
            };
            lines.Add(JsonSerializer.Serialize(categoryEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions()));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Created SimGroup entry for '{category}'", true);
        }

        File.WriteAllLines(levelObjectItemsPath, lines);
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Created main/MissionGroup/level_object/items.level.json with category SimGroups", true);
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
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Copied {copiedMaterials.Count} referenced material(s): {string.Join(", ", copiedMaterials)}");
    }

    /// <summary>
    ///     Copies all files referenced by MissionGroup assets (from both items.level.json and main.level.json)
    /// </summary>
    private void CopyReferencedFiles()
    {
        var copiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process assets from items.level.json (NDJSON format)
        foreach (var asset in _missionGroupAssets)
        {
            CopyAssetReferencedFiles(asset, copiedFiles);
        }

        // Process assets from main.level.json (hierarchical format)
        foreach (var asset in _mainLevelJsonAssets)
        {
            CopyAssetReferencedFiles(asset, copiedFiles);
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Copied {copiedFiles.Count} referenced file(s)");
    }

    /// <summary>
    ///     Copies all files referenced by a single asset
    /// </summary>
    private void CopyAssetReferencedFiles(Asset asset, HashSet<string> copiedFiles)
    {
        // TerrainBlock: Copy .ter file and .terrain.json
        if (!string.IsNullOrEmpty(asset.TerrainFile)) CopyTerrainFiles(asset.TerrainFile, copiedFiles);

        // ScatterSky: Copy gradient files
        CopyGradientFile(asset.AmbientScaleGradientFile, copiedFiles);
        CopyGradientFile(asset.ColorizeGradientFile, copiedFiles);
        CopyGradientFile(asset.FogScaleGradientFile, copiedFiles);
        CopyGradientFile(asset.NightFogGradientFile, copiedFiles);
        CopyGradientFile(asset.NightGradientFile, copiedFiles);
        CopyGradientFile(asset.SunScaleGradientFile, copiedFiles);

        // CloudLayer: Copy texture files
        if (!string.IsNullOrEmpty(asset.Texture)) CopyTextureFile(asset.Texture, copiedFiles);

        // WaterPlane: Copy water-related textures
        if (!string.IsNullOrEmpty(asset.FoamTex)) CopyTextureFile(asset.FoamTex, copiedFiles, "art/water");
        if (!string.IsNullOrEmpty(asset.RippleTex)) CopyTextureFile(asset.RippleTex, copiedFiles, "art/water");
        if (!string.IsNullOrEmpty(asset.DepthGradientTex)) CopyTextureFile(asset.DepthGradientTex, copiedFiles, "art/water");

        // Material references will be handled separately by MaterialCopier
        // (FlareType, NightCubemap, GlobalEnviromentMap, MoonMat)
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
    ///     Copies a texture file (DDS, PNG, etc.), including from other levels via ZIP extraction.
    /// </summary>
    /// <param name="textureFilePath">The texture path (may reference another level)</param>
    /// <param name="copiedFiles">Set of already copied files to avoid duplicates</param>
    /// <param name="targetSubFolder">Target subfolder under level path (default: "art/skies")</param>
    private void CopyTextureFile(string textureFilePath, HashSet<string> copiedFiles, string targetSubFolder = "art/skies")
    {
        if (string.IsNullOrEmpty(textureFilePath) || copiedFiles.Contains(textureFilePath))
            return;

        try
        {
            // Extract just the filename for the target
            var fileName = Path.GetFileName(textureFilePath);
            var targetPath = Path.Join(_targetLevelNamePath, targetSubFolder, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            // First, try resolving from the source level path
            var sourcePath = PathResolver.ResolvePath(_sourceLevelPath, textureFilePath, false);

            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, targetPath, true);
                copiedFiles.Add(textureFilePath);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copied texture: {fileName}", true);
                return;
            }

            // Check if this is a cross-level reference (path starts with /levels/ or levels/)
            var normalizedPath = textureFilePath.TrimStart('/');
            if (normalizedPath.StartsWith("levels/", StringComparison.OrdinalIgnoreCase))
            {
                // Extract level name from path: levels/{levelname}/...
                var pathParts = normalizedPath.Split('/');
                if (pathParts.Length >= 3)
                {
                    var referencedLevelName = pathParts[1];
                    var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;

                    // Check if it's referencing a different level
                    if (!referencedLevelName.Equals(sourceLevelName, StringComparison.OrdinalIgnoreCase))
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Cross-level texture reference found: {textureFilePath}", true);

                        // Try to find the file in the BeamNG install directory
                        var beamInstallDir = Steam.GetBeamInstallDir();
                        if (!string.IsNullOrEmpty(beamInstallDir))
                        {
                            // Try the unpacked level directory first
                            var beamLevelPath = Path.Join(beamInstallDir, Constants.BeamMapPath, referencedLevelName);
                            var fullSourcePath = Path.Join(beamLevelPath, string.Join(Path.DirectorySeparatorChar.ToString(), pathParts.Skip(2)));

                            if (File.Exists(fullSourcePath))
                            {
                                File.Copy(fullSourcePath, targetPath, true);
                                copiedFiles.Add(textureFilePath);
                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    $"Copied cross-level texture from {referencedLevelName}: {fileName}", true);
                                return;
                            }

                            // Try using FileCopyHandler for ZIP extraction
                            try
                            {
                                _fileCopyHandler.CopyFile(fullSourcePath, targetPath);
                                copiedFiles.Add(textureFilePath);
                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    $"Extracted cross-level texture from {referencedLevelName}.zip: {fileName}", true);
                                return;
                            }
                            catch (FileNotFoundException)
                            {
                                // Fall through to warning
                            }
                        }

                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"Could not find cross-level texture: {textureFilePath} (from level '{referencedLevelName}')");
                        return;
                    }
                }
            }

            // Also check for core assets (e.g., core/art/water/foam.dds)
            if (normalizedPath.StartsWith("core/", StringComparison.OrdinalIgnoreCase))
            {
                var beamInstallDir = Steam.GetBeamInstallDir();
                if (!string.IsNullOrEmpty(beamInstallDir))
                {
                    // Try ZipAssetExtractor for core assets
                    if (ZipAssetExtractor.ExtractAssetToFile(normalizedPath, targetPath))
                    {
                        copiedFiles.Add(textureFilePath);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Extracted core asset: {fileName}", true);
                        return;
                    }
                }

                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Could not find core asset: {textureFilePath}");
                return;
            }

            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Texture file not found: {textureFilePath}");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not copy texture file {textureFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Writes MissionGroup items to items.level.json files in the proper hierarchy locations.
    ///     Objects are categorized and placed in appropriate subdirectories:
    ///     - sky: ScatterSky, TimeOfDay, CloudLayer, LevelInfo
    ///     - terrain: TerrainBlock
    ///     - vegetation: ForestWindEmitter, Forest
    ///     Note: GroundCover is NOT copied here - it's handled separately by terrain material copying.
    /// </summary>
    private void WriteMissionGroupItems()
    {
        try
        {
            // Get source level name for path replacement
            var sourceLevelName = new DirectoryInfo(_sourceLevelNamePath).Name;

            // Define the mapping of class types to their target category folders
            // Note: GroundCover is NOT included - it's handled separately by terrain material copying
            var classToCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ScatterSky", "sky" },
                { "TimeOfDay", "sky" },
                { "CloudLayer", "sky" },
                { "LevelInfo", "sky" },
                { "TerrainBlock", "terrain" },
                { "ForestWindEmitter", "vegetation" },
                { "Forest", "vegetation" }
            };

            // Dictionary to hold items grouped by their target category
            var itemsByCategory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "sky", new List<string>() },
                { "terrain", new List<string>() },
                { "vegetation", new List<string>() }
            };

            // Read the original MissionGroup items.level.json files from source
            var sourceMissionGroupPath = Path.Join(_sourceLevelNamePath, "main", "MissionGroup");
            
            // Try to find level_object or Level_object directory
            var sourceLevelObjectPath = FindLevelObjectDirectory(sourceMissionGroupPath);
            
            if (string.IsNullOrEmpty(sourceLevelObjectPath))
            {
                sourceLevelObjectPath = sourceMissionGroupPath;
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No level_object directory found, scanning entire MissionGroup folder");
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
            // Note: SpawnSphere is NOT included - we always generate a fresh one in PlayerDropPoints
            // Note: GroundCover is NOT included - it's handled separately by terrain material copying
            var allowedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "LevelInfo",
                "TerrainBlock",
                "TimeOfDay",
                "CloudLayer",
                "ScatterSky",
                "ForestWindEmitter",
                "Forest"
            };

            var totalLines = 0;
            var processedCount = 0;
            var skippedCount = 0;

            // Read from all items.level.json files
            foreach (var itemsFile in itemsFiles)
            {
                var sourceLines = File.ReadAllLines(itemsFile);
                totalLines += sourceLines.Length;

                var relativePath = Path.GetRelativePath(sourceLevelObjectPath, Path.GetDirectoryName(itemsFile) ?? "");
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Reading {sourceLines.Length} lines from {relativePath}/items.level.json", true);

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
                        if (!root.TryGetProperty("class", out var classProperty))
                            continue;

                        var className = classProperty.GetString();
                        if (string.IsNullOrEmpty(className) || !allowedClasses.Contains(className))
                        {
                            skippedCount++;
                            continue;
                        }

                        processedCount++;
                        
                        // Determine target category
                        var targetCategory = classToCategory.TryGetValue(className, out var cat) ? cat : "sky";

                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Processing {className} object -> {targetCategory}", true);

                        // Convert to mutable dictionary
                        var jsonDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                        if (jsonDict == null)
                            continue;

                        // Update path fields - replace source level name with target level name
                        UpdateAllPathFields(jsonDict, sourceLevelName, _targetLevelName);

                        // Special handling for CloudLayer texture - update to new location in art/skies
                        HandleCloudLayerTexture(jsonDict, className);

                        // Set __parent to the target category (e.g., "sky", "terrain", "vegetation")
                        jsonDict["__parent"] = JsonSerializer.SerializeToElement(targetCategory);

                        // Generate new persistentId for copied objects
                        if (jsonDict.ContainsKey("persistentId"))
                            jsonDict["persistentId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString());

                        // Serialize back to JSON (one line, preserving all original fields)
                        var updatedJson = JsonSerializer.Serialize(jsonDict,
                            BeamJsonOptions.GetJsonSerializerOneLineOptions());
                        
                        itemsByCategory[targetCategory].Add(updatedJson);
                    }
                    catch (JsonException ex)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"Could not parse MissionGroup line: {ex.Message}");
                    }
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Read {totalLines} total lines, processed {processedCount} objects, skipped {skippedCount} objects");

            // Write items to their respective category files
            var totalWritten = 0;
            foreach (var category in itemsByCategory)
            {
                if (category.Value.Count == 0)
                    continue;

                var categoryPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "level_object", category.Key, "items.level.json");
                Directory.CreateDirectory(Path.GetDirectoryName(categoryPath)!);
                File.WriteAllLines(categoryPath, category.Value);
                
                totalWritten += category.Value.Count;
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Wrote {category.Value.Count} item(s) to {category.Key}/items.level.json");
            }

            if (totalWritten == 0)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "No allowed classes found in source files");
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Total: wrote {totalWritten} MissionGroup items across {itemsByCategory.Count(c => c.Value.Count > 0)} categories");
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error writing MissionGroup items: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Finds the level_object directory in the source level, accounting for case variations.
    /// </summary>
    private string FindLevelObjectDirectory(string missionGroupPath)
    {
        if (!Directory.Exists(missionGroupPath))
            return null;

        // Try common variations
        var variations = new[] { "level_object", "Level_object", "LevelObject", "levelObject" };
        
        foreach (var variation in variations)
        {
            var path = Path.Join(missionGroupPath, variation);
            if (Directory.Exists(path))
                return path;
        }

        // Try case-insensitive search
        try
        {
            foreach (var dir in Directory.GetDirectories(missionGroupPath))
            {
                var dirName = new DirectoryInfo(dir).Name;
                if (dirName.Equals("level_object", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch
        {
            // Ignore enumeration errors
        }

        return null;
    }

    /// <summary>
    ///     Updates all known path fields in the JSON dictionary.
    /// </summary>
    private void UpdateAllPathFields(Dictionary<string, JsonElement> jsonDict, string sourceLevelName, string targetLevelName)
    {
        var pathFields = new[]
        {
            "terrainFile", "texture", "ambientScaleGradientFile", "colorizeGradientFile",
            "fogScaleGradientFile", "nightFogGradientFile", "nightGradientFile", "sunScaleGradientFile",
            "flareType", "nightCubemap", "globalEnviromentMap", "moonMat"
        };

        foreach (var field in pathFields)
        {
            UpdatePathField(jsonDict, field, sourceLevelName, targetLevelName);
        }
    }

    /// <summary>
    ///     Handles CloudLayer texture path updates.
    /// </summary>
    private void HandleCloudLayerTexture(Dictionary<string, JsonElement> jsonDict, string className)
    {
        if (!className.Equals("CloudLayer", StringComparison.OrdinalIgnoreCase))
            return;

        if (!jsonDict.TryGetValue("texture", out var textureElement))
            return;

        var texturePath = textureElement.GetString();
        if (string.IsNullOrEmpty(texturePath))
            return;

        var textureFileName = Path.GetFileName(texturePath);
        var newTexturePath = $"/levels/{_targetLevelName}/art/skies/{textureFileName}";
        jsonDict["texture"] = JsonSerializer.SerializeToElement(newTexturePath);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Updated CloudLayer texture: {textureFileName}", true);
    }

    /// <summary>
    ///     Creates a default spawn point (spawn_default_MT) in the PlayerDropPoints folder.
    ///     This is always generated fresh, not copied from the source level.
    /// </summary>
    private void CreateDefaultSpawnPoint()
    {
        try
        {
            var playerDropPointsPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "PlayerDropPoints",
                "items.level.json");

            // Create a fresh SpawnSphere with default values
            // BeamNG terrain is centered at world origin (0,0), so spawn at center
            // Position will be updated later by terrain generation if OSM roads are used
            var spawnSphere = new Dictionary<string, object>
            {
                { "name", "spawn_default_MT" },
                { "class", "SpawnSphere" },
                { "persistentId", Guid.NewGuid().ToString() },
                { "__parent", "PlayerDropPoints" },
                { "position", new[] { 0.0, 0.0, 100.0 } }, // Center of terrain (origin), will be updated
                { "autoplaceOnSpawn", "0" },
                { "dataBlock", "SpawnSphereMarker" },
                { "enabled", "1" },
                { "homingCount", "0" },
                { "indoorWeight", "1" },
                { "isAIControlled", "0" },
                { "lockCount", "0" },
                { "outdoorWeight", "1" },
                { "radius", 1 },
                { "rotationMatrix", new[] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0 } },
                { "sphereWeight", "1" }
            };

            var spawnJson = JsonSerializer.Serialize(spawnSphere, BeamJsonOptions.GetJsonSerializerOneLineOptions());
            File.WriteAllText(playerDropPointsPath, spawnJson + Environment.NewLine);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Created spawn_default_MT SpawnSphere in PlayerDropPoints");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not create default spawn point: {ex.Message}");
        }
    }

    /// <summary>
    ///     Updates a path field in the JSON dictionary if it exists
    /// </summary>
    private void UpdatePathField(Dictionary<string, JsonElement> jsonDict, string fieldName, string oldLevelName,
        string newLevelName)
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