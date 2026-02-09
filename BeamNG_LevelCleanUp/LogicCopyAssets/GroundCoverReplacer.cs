using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyAssets;

/// <summary>
///     Handles replacing groundcover Types and materials while preserving target identity
/// </summary>
public class GroundCoverReplacer
{
    private readonly GroundCoverDependencyHelper _dependencyHelper;

    // Track which groundcovers to replace (by target material key -> source materials)
    // The key can be target material name OR internalName (user input). We'll normalize to internalName when writing.
    private readonly Dictionary<string, List<MaterialJson>> _groundCoversToReplace;
    private readonly string _levelNameCopyFrom;
    private readonly string _namePath;
    private readonly Dictionary<string, JsonNode> _parsedGroundCovers;
    private List<string> _allGroundCoverJsonLines;

    public GroundCoverReplacer(
        GroundCoverDependencyHelper dependencyHelper,
        string namePath)
    {
        _dependencyHelper = dependencyHelper;
        _namePath = namePath;
        _groundCoversToReplace = new Dictionary<string, List<MaterialJson>>();
        _parsedGroundCovers = new Dictionary<string, JsonNode>();
    }

    public GroundCoverReplacer(
        GroundCoverDependencyHelper dependencyHelper,
        string namePath,
        string levelNameCopyFrom)
        : this(dependencyHelper, namePath)
    {
        _levelNameCopyFrom = levelNameCopyFrom;
    }

    /// <summary>
    ///     Loads scanned groundcover JSON lines from BeamFileReader
    /// </summary>
    public void LoadGroundCoverJsonLines(List<string> groundCoverJsonLines)
    {
        _allGroundCoverJsonLines = groundCoverJsonLines ?? new List<string>();
        ParseAllGroundCovers();
    }

    /// <summary>
    ///     Loads scanned materials from BeamFileReader for material lookup
    ///     ///
    /// </summary>
    public void LoadMaterialsJsonCopy(List<MaterialJson> materialsJsonCopy)
    {
        _dependencyHelper.LoadMaterialsJsonCopy(materialsJsonCopy);
    }

    /// <summary>
    ///     Marks groundcovers for replacement based on terrain material replacement
    ///     targetMaterialKey may be a material name OR internalName. We'll resolve to internalName later.
    /// </summary>
    public void ReplaceGroundCoversForTerrainMaterial(string targetMaterialKey, List<MaterialJson> sourceMaterials)
    {
        if (string.IsNullOrEmpty(targetMaterialKey) || sourceMaterials == null || !sourceMaterials.Any()) return;

        _groundCoversToReplace[targetMaterialKey] = sourceMaterials;
    }

    /// <summary>
    ///     Writes all marked groundcover replacements
    ///     Dynamically finds the target vegetation file
    /// </summary>
    public void WriteAllGroundCoverReplacements()
    {
        if (!_groundCoversToReplace.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "WriteAllGroundCoverReplacements called but no replacements queued.");
            return;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Processing {_groundCoversToReplace.Count} groundcover replacement(s)...");

        // Dynamically find the target vegetation file
        var targetGcFile = VegetationFileHelper.FindTargetVegetationFile(_namePath);

        if (targetGcFile == null || !targetGcFile.Exists)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No vegetation file found in target level. Cannot replace groundcovers.");
            return;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Using target vegetation file: {targetGcFile.FullName}");

        var targetMaterialsFile = new FileInfo(Path.Join(_namePath, "art", "terrains", "main.materials.json"));

        // Build lookup for target material name->internalName and internalName->internalName
        var targetNameToInternal = BuildTargetNameToInternalNameMap(targetMaterialsFile);

        // Load existing groundcovers from target
        var existingGroundCovers = LoadExistingGroundCovers(targetGcFile);
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Loaded {existingGroundCovers.Count} existing entries from target vegetation file.");

        var newGroundCovers = new List<JsonNode>();
        var deletedGroundCoverNames = new HashSet<string>();
        var modifiedGroundCoversMap = new Dictionary<string, JsonNode>();

        // Resolve all replaced material names to their INTERNAL names
        var allReplacedMaterialInternalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Group replacements by source material to avoid duplicating groundcovers
        var sourceToTargetsMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _groundCoversToReplace)
        {
            var targetMaterialKey = kvp.Key;
            var sourceMaterials = kvp.Value;

            // Resolve target internalName
            var targetInternalName = targetNameToInternal.TryGetValue(targetMaterialKey, out var resolvedInternal)
                ? resolvedInternal
                : targetMaterialKey;

            allReplacedMaterialInternalNames.Add(targetInternalName);

            // Get source internal name
            var sourceInternalName = sourceMaterials.FirstOrDefault()?.InternalName ?? sourceMaterials.First().Name;

            if (!sourceToTargetsMap.ContainsKey(sourceInternalName))
                sourceToTargetsMap[sourceInternalName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            sourceToTargetsMap[sourceInternalName].Add(targetInternalName);
        }

        if (!allReplacedMaterialInternalNames.Any())
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No replaced material internal names collected. Skipping modification phase.");
        else
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Replaced material internal names: {string.Join(", ", allReplacedMaterialInternalNames)}");

        // Process each unique source material and create groundcovers for its target materials
        foreach (var sourceEntry in sourceToTargetsMap)
        {
            var sourceInternalName = sourceEntry.Key;
            var targetInternalNames = sourceEntry.Value;

            // Find SOURCE groundcovers that reference the SOURCE internal name
            var sourceGroundCovers = FindSourceGroundCoversByLayer(sourceInternalName);

            if (!sourceGroundCovers.Any())
            {
                // SCENARIO B: Source has NO groundcovers
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Source material '{sourceInternalName}' has no groundcovers. Target materials {string.Join(", ", targetInternalNames)} will have their layers removed.");
            }
            else
            {
                // SCENARIO A: Source HAS groundcovers - copy for each TARGET material separately
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Found {sourceGroundCovers.Count} source groundcover(s) for material '{sourceInternalName}'. Creating copies for {targetInternalNames.Count} target material(s).");

                // For each target material, create separate groundcover copies
                foreach (var targetInternalName in targetInternalNames)
                foreach (var sourceGroundCover in sourceGroundCovers)
                {
                    var sourceGroundCoverName = sourceGroundCover["name"]?.ToString();
                    if (string.IsNullOrEmpty(sourceGroundCoverName)) continue;

                    // Create a unique copy for THIS target material
                    var newGroundCover = CopySourceGroundCoverForSingleTarget(
                        sourceGroundCover,
                        sourceGroundCoverName,
                        targetInternalName,
                        sourceInternalName);

                    if (newGroundCover != null)
                    {
                        newGroundCovers.Add(newGroundCover);
                    }
                }
            }
        }

        // Process existing groundcovers to remove replaced layers
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Processing existing groundcovers to remove replaced layers...");

        foreach (var existingEntry in existingGroundCovers.ToList())
        {
            var gcName = existingEntry.Key;
            var gcNode = existingEntry.Value;

            if (gcNode["class"]?.ToString() != "GroundCover") continue;

            // Remove Types that reference any replaced material
            var (modified, shouldDelete) = RemoveReplacedLayers(gcNode, allReplacedMaterialInternalNames);

            if (shouldDelete)
            {
                deletedGroundCoverNames.Add(gcName);
            }
            else if (modified)
            {
                modifiedGroundCoversMap[gcName] = gcNode;
            }
        }

        // Write changes back to file
        if (newGroundCovers.Any() || deletedGroundCoverNames.Any() || modifiedGroundCoversMap.Any())
            WriteGroundCoverChangesToFile(targetGcFile, existingGroundCovers, newGroundCovers,
                modifiedGroundCoversMap.Values.ToList(), deletedGroundCoverNames.ToList());
        else
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No groundcover changes to write.");

        _groundCoversToReplace.Clear();
    }

    /// <summary>
    ///     Parses all groundcover JSON lines for fast lookup
    /// </summary>
    private void ParseAllGroundCovers()
    {
        _parsedGroundCovers.Clear();
        if (_allGroundCoverJsonLines == null) return;

        foreach (var line in _allGroundCoverJsonLines)
            try
            {
                var gcNode = JsonUtils.GetValidJsonNodeFromString(line, "groundcover");
                if (gcNode != null && gcNode["class"]?.ToString() == "GroundCover")
                {
                    var name = gcNode["name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) _parsedGroundCovers[name] = gcNode;
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Error parsing groundcover line: {ex.Message}");
            }
    }

    /// <summary>
    ///     Build a lookup that maps BOTH material name and internalName to the internalName.
    /// </summary>
    private Dictionary<string, string> BuildTargetNameToInternalNameMap(FileInfo targetMaterialsFile)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (targetMaterialsFile.Exists)
            {
                var targetJsonNode = JsonUtils.GetValidJsonNodeFromFilePath(targetMaterialsFile.FullName);
                foreach (var kv in targetJsonNode.AsObject())
                {
                    var value = kv.Value;
                    var nameProp = value?["name"]?.ToString();
                    var internalNameProp = value?["internalName"]?.ToString();

                    // Prefer internalName when present
                    if (!string.IsNullOrEmpty(internalNameProp))
                    {
                        if (!string.IsNullOrEmpty(nameProp))
                            map[nameProp] = internalNameProp;
                        map[internalNameProp] = internalNameProp;
                    }
                    else if (!string.IsNullOrEmpty(nameProp))
                    {
                        // Fallback: use name as internal when internal missing
                        map[nameProp] = nameProp;
                    }
                }
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Target materials file not found at {targetMaterialsFile.FullName}. Will use provided keys as-is.");
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error building material name->internalName map: {ex.Message}");
        }

        return map;
    }

    /// <summary>
    ///     Finds SOURCE groundcovers that have Types with layer matching the source material internal name
    /// </summary>
    private List<JsonNode> FindSourceGroundCoversByLayer(string sourceMaterialInternalName)
    {
        var matchingGroundCovers = new List<JsonNode>();

        foreach (var gc in _parsedGroundCovers.Values)
            if (GroundCoverReferencesLayer(gc, sourceMaterialInternalName))
                matchingGroundCovers.Add(gc);

        return matchingGroundCovers;
    }

    /// <summary>
    ///     Checks if a groundcover's Types array contains any layer matching the material name (case-insensitive)
    /// </summary>
    private bool GroundCoverReferencesLayer(JsonNode groundCover, string materialName)
    {
        if (groundCover["Types"] is not JsonArray types) return false;

        foreach (var type in types)
        {
            var layer = type?["layer"]?.ToString();
            if (!string.IsNullOrEmpty(layer) &&
                string.Equals(layer, materialName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// Removes Types (layers) that reference replaced materials from a groundcover
    /// Returns (wasModified, shouldDeleteEntireGroundcover)
    /// </summary>
    private (bool modified, bool shouldDelete) RemoveReplacedLayers(JsonNode groundCover,
        HashSet<string> replacedMaterialNames)
    {
        if (groundCover["Types"] is not JsonArray types) return (false, false);

        if (replacedMaterialNames == null || replacedMaterialNames.Count == 0) return (false, false);

        var typesToRemove = new List<JsonNode>();

        // Find all Types that reference replaced materials
        foreach (var type in types)
        {
            var layerName = type?["layer"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(layerName) && replacedMaterialNames.Contains(layerName)) typesToRemove.Add(type);
        }

        // Remove the identified Types
        foreach (var typeToRemove in typesToRemove) types.Remove(typeToRemove);

        var removedCount = typesToRemove.Count;
        if (removedCount > 0)
        {
            var remainingCount = types.Count;

            if (remainingCount == 0)
                // All Types removed -> should delete entire groundcover
                return (true, true);

            // Some Types removed, but others remain
            return (true, false);
        }

        return (false, false); // No changes
    }

    /// <summary>
    ///     Copies a source groundcover for a SINGLE target material (one-to-one replacement)
    /// </summary>
    private JsonNode CopySourceGroundCoverForSingleTarget(
        JsonNode sourceGroundCover,
        string sourceGroundCoverName,
        string targetMaterialInternalName,
        string sourceMaterialInternalName)
    {
        try
        {
            // Create a copy of source groundcover
            var newGroundCover = JsonNode.Parse(sourceGroundCover.ToJsonString());

            // Clean the source name by removing existing suffixes
            var levelSuffix = $"_{_levelNameCopyFrom}";
            var cleanSourceName = sourceGroundCoverName;

            // Remove existing level suffix if present
            if (sourceGroundCoverName.EndsWith(levelSuffix, StringComparison.OrdinalIgnoreCase))
            {
                cleanSourceName = sourceGroundCoverName.Substring(0, sourceGroundCoverName.Length - levelSuffix.Length);

                // Now check if there's a material suffix before the level suffix
                // Pattern: OriginalName_MaterialName_LevelName -> we want just "OriginalName"
                // The material name might itself contain the level suffix (e.g., "Fieldgrass_ellern_map")

                // Find the last underscore in the cleaned name
                var lastUnderscoreIndex = cleanSourceName.LastIndexOf('_');
                if (lastUnderscoreIndex > 0)
                {
                    // Get everything after the last underscore - this might be a material name
                    var potentialMaterialSuffix = cleanSourceName.Substring(lastUnderscoreIndex + 1);

                    // Remove it if it looks like it might be a material name (length > 3 characters)
                    // This heuristic should catch material names but not parts of the original name
                    if (potentialMaterialSuffix.Length > 3)
                    {
                        cleanSourceName = cleanSourceName.Substring(0, lastUnderscoreIndex);
                    }
                }
            }

            // Generate new name with prefix, target material name AND level suffix for uniqueness
            // Using gc_ prefix to avoid collision with TerrainMaterial names
            var newName = $"gc_{cleanSourceName}_{targetMaterialInternalName}_{_levelNameCopyFrom}";
            newGroundCover["name"] = newName;

            // Generate new GUID
            newGroundCover["persistentId"] = Guid.NewGuid().ToString();

            // Update layer references: source internal name -> target internal name (one-to-one)
            UpdateLayerReferences(newGroundCover, sourceMaterialInternalName, targetMaterialInternalName);

            // Copy dependencies
            var newMaterialName = _dependencyHelper.CopyGroundCoverDependencies(sourceGroundCover, newName);

            // Update material property
            if (!string.IsNullOrEmpty(newMaterialName))
                newGroundCover["material"] = newMaterialName;

            return newGroundCover;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error copying source groundcover '{sourceGroundCoverName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Copies a source groundcover with level suffix (like Add mode) and updates layer references
    /// </summary>
    private JsonNode CopySourceGroundCover(
        JsonNode sourceGroundCover,
        string sourceGroundCoverName,
        string targetMaterialInternalName,
        string sourceMaterialInternalName)
    {
        try
        {
            // Create a copy of source groundcover
            var newGroundCover = JsonNode.Parse(sourceGroundCover.ToJsonString());

            // Generate new name with gc_ prefix and level suffix (consistent with Add mode)
            // Using gc_ prefix to avoid collision with TerrainMaterial names
            var newName = $"gc_{sourceGroundCoverName}_{_levelNameCopyFrom}";
            newGroundCover["name"] = newName;

            // Generate new GUID
            newGroundCover["persistentId"] = Guid.NewGuid().ToString();

            // Update layer references: source internal name -> target internal name
            UpdateLayerReferences(newGroundCover, sourceMaterialInternalName, targetMaterialInternalName);

            // Copy dependencies (material textures and DAE files) from source
            var newMaterialName = _dependencyHelper.CopyGroundCoverDependencies(sourceGroundCover, newName);

            // Update material property to the new copied material name (if any)
            if (!string.IsNullOrEmpty(newMaterialName)) newGroundCover["material"] = newMaterialName;

            return newGroundCover;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error copying source groundcover '{sourceGroundCoverName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Updates layer references in Types array from source material name to target material name
    /// </summary>
    private void UpdateLayerReferences(JsonNode groundCover, string sourceMaterialName, string targetMaterialName)
    {
        if (groundCover["Types"] is not JsonArray types) return;

        foreach (var type in types)
            if (string.Equals(type?["layer"]?.ToString(), sourceMaterialName, StringComparison.OrdinalIgnoreCase))
                type["layer"] = targetMaterialName;
    }

    /// <summary>
    ///     Loads existing groundcovers from the target file
    /// </summary>
    private Dictionary<string, JsonNode> LoadExistingGroundCovers(FileInfo targetFile)
    {
        var existingGroundCovers = new Dictionary<string, JsonNode>();
        try
        {
            var lines = File.ReadAllLines(targetFile.FullName);
            foreach (var line in lines)
                try
                {
                    var gcNode = JsonUtils.GetValidJsonNodeFromString(line, targetFile.FullName);
                    if (gcNode != null)
                    {
                        var name = gcNode["name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            existingGroundCovers[name] = gcNode;
                        }
                        else
                        {
                            // Non-groundcover entries (Forest, ForestWindEmitter, etc.) - preserve by using GUID as key
                            var persistentId = gcNode["persistentId"]?.ToString();
                            if (!string.IsNullOrEmpty(persistentId)) existingGroundCovers[persistentId] = gcNode;
                        }
                    }
                }
                catch
                {
                    // Skip invalid lines
                }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Error loading existing groundcovers: {ex.Message}");
        }

        return existingGroundCovers;
    }

    /// <summary>
    ///     Writes groundcover changes (replacements and deletions) back to file
    /// </summary>
    private void WriteGroundCoverChangesToFile(
        FileInfo targetFile,
        Dictionary<string, JsonNode> existingGroundCovers,
        List<JsonNode> newGroundCovers,
        List<JsonNode> modifiedGroundCovers,
        List<string> deletedGroundCoverNames)
    {
        try
        {
            var replacedCount = 0;

            // Apply new groundcovers (will overwrite existing ones with same name)
            foreach (var newGC in newGroundCovers)
            {
                var name = newGC["name"]?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    if (existingGroundCovers.ContainsKey(name))
                        replacedCount++;

                    existingGroundCovers[name] = newGC;
                }
            }

            // Apply modifications (updated groundcovers with removed layers)
            foreach (var modifiedGC in modifiedGroundCovers)
            {
                var name = modifiedGC["name"]?.ToString();
                if (!string.IsNullOrEmpty(name)) existingGroundCovers[name] = modifiedGC;
            }

            // Apply deletions
            foreach (var deletedName in deletedGroundCoverNames) existingGroundCovers.Remove(deletedName);

            // Write all groundcovers to file (keep one JSON object per line)
            var lines = existingGroundCovers.Values
                .Select(gc => gc.ToJsonString(BeamJsonOptions.GetJsonSerializerOneLineOptions()))
                .ToList();

            File.WriteAllLines(targetFile.FullName, lines);

            var addedCount = newGroundCovers.Count - replacedCount;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Updated groundcovers: {addedCount} added, {replacedCount} replaced, {modifiedGroundCovers.Count} modified (layers removed), {deletedGroundCoverNames.Count} deleted in {targetFile.FullName}");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error writing groundcover changes: {ex.Message}");
        }
    }
}