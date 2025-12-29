using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles copying of GroundCover objects with material and DAE dependencies
///     Copies entire groundcovers and suffixes all layer names
/// </summary>
public class GroundCoverCopier
{
    // Track already copied DAE files to avoid duplicates
    private readonly HashSet<string> _copiedDaeFiles;
    private readonly DaeCopier _daeCopier;
    private readonly FileCopyHandler _fileCopyHandler;

    // Track which groundcovers to copy (by name)
    private readonly HashSet<string> _groundCoversToCopy;
    private readonly string _levelNameCopyFrom;
    private readonly MaterialCopier _materialCopier;

    // Cache for materials to avoid repeated FirstOrDefault searches
    private readonly Dictionary<string, MaterialJson> _materialLookup;
    private readonly string _namePath;

    // Cache parsed groundcovers for fast lookup (avoids re-parsing)
    private readonly Dictionary<string, JsonNode> _parsedGroundCovers;
    private readonly PathConverter _pathConverter;
    private readonly string _targetLevelName;
    private List<string> _allGroundCoverJsonLines;
    private List<MaterialJson> _materialsJsonCopy;

    public GroundCoverCopier(PathConverter pathConverter, FileCopyHandler fileCopyHandler,
        MaterialCopier materialCopier, DaeCopier daeCopier, string levelNameCopyFrom, string namePath)
    {
        _pathConverter = pathConverter;
        _fileCopyHandler = fileCopyHandler;
        _materialCopier = materialCopier;
        _daeCopier = daeCopier;
        _levelNameCopyFrom = levelNameCopyFrom;
        _namePath = namePath;
        _targetLevelName = Path.GetFileName(namePath);
        _allGroundCoverJsonLines = new List<string>();
        _groundCoversToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _parsedGroundCovers = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        _materialLookup = new Dictionary<string, MaterialJson>(StringComparer.OrdinalIgnoreCase);
        _copiedDaeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Loads scanned groundcover JSON lines from BeamFileReader
    /// </summary>
    public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
    {
        _allGroundCoverJsonLines = groundCoverJsonLines ?? new List<string>();

        // Pre-parse all groundcovers once for fast lookup
        _parsedGroundCovers.Clear();
        foreach (var jsonLine in _allGroundCoverJsonLines)
            try
            {
                var jsonNode = JsonNode.Parse(jsonLine);
                if (jsonNode == null) continue;

                var groundCoverName = jsonNode["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(groundCoverName)) _parsedGroundCovers[groundCoverName] = jsonNode;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Error pre-parsing groundcover: {ex.Message}");
            }
    }

    /// <summary>
    ///     Loads scanned materials from BeamFileReader for material lookup
    /// </summary>
    public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
    {
        _materialsJsonCopy = materialsJsonCopy ?? new List<MaterialJson>();

        // Build fast lookup dictionary for materials
        _materialLookup.Clear();
        foreach (var material in _materialsJsonCopy)
            if (!string.IsNullOrEmpty(material.Name))
                _materialLookup[material.Name] = material;
    }

    /// <summary>
    ///     PHASE 1: Collect groundcovers that reference the given terrain materials
    ///     Marks entire groundcovers for copying if any of their Types reference the terrain materials
    /// </summary>
    public void CollectGroundCoversForTerrainMaterials(List<MaterialJson> terrainMaterials)
    {
        if (!_parsedGroundCovers.Any()) return;

        // Build lookup with BOTH original AND suffixed internal names
        // Groundcovers may reference either the original name or the suffixed name
        var terrainInternalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var material in terrainMaterials)
            if (!string.IsNullOrEmpty(material.InternalName))
            {
                // Add original internal name
                terrainInternalNames.Add(material.InternalName);

                // Add suffixed internal name (what the material will be renamed to)
                var suffixedName = $"{material.InternalName}_{_levelNameCopyFrom}";
                terrainInternalNames.Add(suffixedName);
            }

        if (!terrainInternalNames.Any()) return;

        var newGroundCovers = 0;

        foreach (var kvp in _parsedGroundCovers)
            try
            {
                var groundCoverName = kvp.Key;
                var jsonNode = kvp.Value;

                // Skip if already marked
                if (_groundCoversToCopy.Contains(groundCoverName)) continue;

                var typesArray = jsonNode["Types"] as JsonArray;
                if (typesArray == null) continue;

                // Check if ANY Type references our terrain materials
                var hasMatchingLayer = false;
                foreach (var typeNode in typesArray)
                {
                    if (typeNode == null) continue;

                    var layer = typeNode["layer"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(layer) && terrainInternalNames.Contains(layer))
                    {
                        hasMatchingLayer = true;
                        break;
                    }
                }

                if (hasMatchingLayer)
                {
                    _groundCoversToCopy.Add(groundCoverName);
                    newGroundCovers++;
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Error collecting groundcover: {ex.Message}");
            }

        if (newGroundCovers > 0)
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Collected {newGroundCovers} groundcover(s) for copying");
    }

    /// <summary>
    ///     PHASE 2: Write all collected groundcovers to the target file
    ///     Copies entire groundcovers with all Types, suffixing all layer names
    ///     Dynamically finds the target vegetation file or creates one at the default location
    /// </summary>
    public void WriteAllGroundCovers()
    {
        if (!_groundCoversToCopy.Any()) return;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Copying {_groundCoversToCopy.Count} groundcover(s)...");

        // Clear DAE file tracking for this batch
        _copiedDaeFiles.Clear();

        // Dynamically find the target vegetation file
        var targetFile = VegetationFileHelper.FindTargetVegetationFile(_namePath);

        if (targetFile == null)
        {
            // No existing vegetation file found - create at default location
            var defaultPath = VegetationFileHelper.GetDefaultVegetationFilePath(_namePath);
            targetFile = new FileInfo(defaultPath);

            // Create directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(defaultPath));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"No existing vegetation file found. Creating new file at: {defaultPath}");
        }

        // Load existing groundcovers from target
        var existingGroundCovers = LoadExistingGroundCovers(targetFile);

        var created = 0;
        var merged = 0;

        foreach (var groundCoverName in _groundCoversToCopy)
            try
            {
                // Find the original groundcover from pre-parsed cache (O(1) lookup)
                if (!_parsedGroundCovers.TryGetValue(groundCoverName, out var originalJsonLine))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Groundcover '{groundCoverName}' not found in cache");
                    continue;
                }

                var newName = $"gc_{groundCoverName}_{_levelNameCopyFrom}";

                // Copy dependencies (materials and DAE files)
                // Returns the new material name if a material was copied
                var newMaterialName = CopyGroundCoverDependencies(originalJsonLine, groundCoverName);

                // Create the final groundcover with all Types suffixed
                var finalGroundCover = BuildFinalGroundCover(originalJsonLine, groundCoverName, newMaterialName);

                if (existingGroundCovers.ContainsKey(newName))
                {
                    // Replace existing
                    existingGroundCovers[newName] = finalGroundCover;
                    merged++;
                }
                else
                {
                    // Add new
                    existingGroundCovers[newName] = finalGroundCover;
                    created++;
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Error writing groundcover '{groundCoverName}': {ex.Message}");
            }

        // Write all groundcovers to file
        WriteAllGroundCoversToFile(targetFile, existingGroundCovers.Values);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Groundcover copy complete: {created} created, {merged} updated in {targetFile.FullName}");

        // Clear for next operation
        _groundCoversToCopy.Clear();
    }

    /// <summary>
    ///     Builds the final groundcover JSON with all Types and suffixed layer names
    /// </summary>
    private JsonNode BuildFinalGroundCover(JsonNode originalGroundCover, string originalName, string newMaterialName)
    {
        // Clone efficiently by using the JsonNode copy constructor pattern
        var result = JsonNode.Parse(originalGroundCover.ToJsonString());

        // Update name with prefix and suffix to avoid collision with TerrainMaterial names
        // TerrainMaterial "Grass2" becomes "Grass2_italy"
        // GroundCover "Grass2" becomes "gc_Grass2_italy" (different name!)
        result["name"] = $"gc_{originalName}_{_levelNameCopyFrom}";

        // Update persistentId with new GUID
        result["persistentId"] = Guid.NewGuid().ToString();

        // Update material name if it was copied with a new name
        if (!string.IsNullOrEmpty(newMaterialName) && result["material"] != null) result["material"] = newMaterialName;

        // Update all Types: suffix layer names and update shapeFilename paths
        var typesArray = result["Types"] as JsonArray;
        if (typesArray != null)
            foreach (var typeNode in typesArray)
            {
                if (typeNode == null) continue;

                // Suffix layer name (even if material not copied - harmless)
                var layer = typeNode["layer"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(layer)) typeNode["layer"] = $"{layer}_{_levelNameCopyFrom}";

                // Update shapeFilename path
                var shapeFilename = typeNode["shapeFilename"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(shapeFilename))
                {
                    // Strip .link extension for the JSON path - BeamNG references without .link
                    var cleanShapeFilename = FileUtils.StripLinkExtension(shapeFilename);
                    var fileName = Path.GetFileName(cleanShapeFilename);
                    var newPath =
                        $"/levels/{_targetLevelName}/art/shapes/groundcover/MT_{_levelNameCopyFrom}/{fileName}";
                    typeNode["shapeFilename"] = newPath;
                }
            }

        return result;
    }

    /// <summary>
    ///     Loads existing groundcovers from the target file
    /// </summary>
    private Dictionary<string, JsonNode> LoadExistingGroundCovers(FileInfo targetFile)
    {
        var result = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);

        if (!targetFile.Exists) return result;

        try
        {
            var lines = File.ReadAllLines(targetFile.FullName);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var jsonNode = JsonNode.Parse(line);
                    if (jsonNode == null) continue;

                    var name = jsonNode["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name)) result[name] = jsonNode;
                }
                catch
                {
                    // Skip malformed lines
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error loading existing groundcovers: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    ///     Writes all groundcovers to the target file
    /// </summary>
    private void WriteAllGroundCoversToFile(FileInfo targetFile, IEnumerable<JsonNode> groundCovers)
    {
        try
        {
            var lines = groundCovers
                .Select(gc => gc.ToJsonString(BeamJsonOptions.GetJsonSerializerOneLineOptions()))
                .ToList();

            File.WriteAllLines(targetFile.FullName, lines);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error writing groundcovers to file: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    ///     Copies materials and DAE files referenced by a groundcover
    ///     Returns the new material name if a material was copied, null otherwise
    /// </summary>
    private string CopyGroundCoverDependencies(JsonNode groundCoverNode, string groundCoverName)
    {
        string newMaterialName = null;

        // Copy groundcover's material (if it has one)
        var materialName = groundCoverNode["material"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(materialName) && _materialsJsonCopy != null)
            newMaterialName = CopyGroundCoverMaterial(materialName, groundCoverName);

        // Copy DAE files from all Types
        var typesArray = groundCoverNode["Types"] as JsonArray;
        if (typesArray != null)
        {
            var daeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var typeNode in typesArray)
            {
                if (typeNode == null) continue;

                var shapeFilename = typeNode["shapeFilename"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(shapeFilename)) daeFiles.Add(shapeFilename);
            }

            foreach (var daeFile in daeFiles) CopyGroundCoverDaeFile(daeFile);
        }

        return newMaterialName;
    }

    /// <summary>
    ///     Copies a material referenced by a groundcover with level suffix
    ///     Returns the new material name with suffix
    /// </summary>
    private string CopyGroundCoverMaterial(string materialName, string groundCoverName)
    {
        // Use fast dictionary lookup instead of FirstOrDefault on list
        if (!_materialLookup.TryGetValue(materialName, out var material)) return null;

        // Create new material name with level suffix
        var newMaterialName = $"{materialName}_{_levelNameCopyFrom}";

        var materialCopyAsset = new CopyAsset
        {
            CopyAssetType = CopyAssetType.Terrain,
            Name = material.Name,
            Materials = new List<MaterialJson> { material },
            TargetPath = Path.Join(_namePath, Constants.GroundCover,
                $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}")
        };

        // Copy material with new name (uses MaterialCopier's new parameter)
        _materialCopier.Copy(materialCopyAsset, newMaterialName);

        return newMaterialName;
    }

    /// <summary>
    ///     Copies a DAE file referenced by groundcover
    /// </summary>
    private void CopyGroundCoverDaeFile(string daeFilePath)
    {
        try
        {
            // Skip if already copied this DAE file
            if (_copiedDaeFiles.Contains(daeFilePath)) return;

            var fileName = Path.GetFileName(daeFilePath);
            var sourcePath = PathResolver.ResolvePath(PathResolver.LevelPathCopyFrom, daeFilePath, true);
            var targetPath = Path.Join(_namePath, "art", "shapes", "groundcover",
                $"{Constants.MappingToolsPrefix}{_levelNameCopyFrom}");
            Directory.CreateDirectory(targetPath);

            var targetFilePath = Path.Join(targetPath, fileName);

            _fileCopyHandler.CopyFile(sourcePath, targetFilePath);

            // Mark as copied
            _copiedDaeFiles.Add(daeFilePath);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to copy DAE file {daeFilePath}: {ex.Message}");
        }
    }
}