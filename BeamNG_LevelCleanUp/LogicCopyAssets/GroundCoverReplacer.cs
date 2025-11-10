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

        var targetGcFile = new FileInfo(Path.Join(_namePath, "main", "MissionGroup", "Level_object", "vegetation",
            "items.level.json"));
        if (!targetGcFile.Exists)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Target groundcover file not found at {targetGcFile.FullName}. Skipping groundcover replacement.");
            return;
        }

        var targetMaterialsFile = new FileInfo(Path.Join(_namePath, "art", "terrains", "main.materials.json"));

        // Build lookup for target material name->internalName and internalName->internalName
        var targetNameToInternal = BuildTargetNameToInternalNameMap(targetMaterialsFile);

        // Load existing groundcovers from target
        var existingGroundCovers = LoadExistingGroundCovers(targetGcFile);
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Loaded {existingGroundCovers.Count} existing entries from target vegetation file.");

        var newGroundCovers = new List<JsonNode>();
        var deletedGroundCoverNames = new List<string>();
        var modifiedGroundCovers = new List<JsonNode>();

        // Resolve all replaced material names to their INTERNAL names (what GroundCover.Types.layer uses)
        var allReplacedMaterialInternalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _groundCoversToReplace.Keys)
            if (targetNameToInternal.TryGetValue(key, out var internalName))
                allReplacedMaterialInternalNames.Add(internalName);
            else
                // Fallback: if not found, assume key is already internalName
                allReplacedMaterialInternalNames.Add(key);

        if (!allReplacedMaterialInternalNames.Any())
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "No replaced material internal names collected. Skipping modification phase.");
        else
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Replaced material internal names: {string.Join(", ", allReplacedMaterialInternalNames)}");

        foreach (var kvp in _groundCoversToReplace)
        {
            var targetMaterialKey = kvp.Key; // Could be name or internalName
            var sourceMaterials = kvp.Value;

            // Resolve target internalName
            var targetInternalName = targetNameToInternal.TryGetValue(targetMaterialKey, out var resolvedInternal)
                ? resolvedInternal
                : targetMaterialKey;

            // Resolve SOURCE internal name (used in source groundcovers)
            var sourceInternalName = sourceMaterials.FirstOrDefault()?.InternalName ?? sourceMaterials.First().Name;

            // Find SOURCE groundcovers that reference the SOURCE internal name (via layer property)
            var sourceGroundCovers = FindSourceGroundCoversByLayer(sourceInternalName);

            if (!sourceGroundCovers.Any())
            {
                // SCENARIO B: Source has NO groundcovers -> Remove layers from existing groundcovers
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Source material '{sourceInternalName}' has no groundcovers. Removing '{targetInternalName}' layers from existing groundcovers.");
            }
            else
            {
                // SCENARIO A: Source HAS groundcovers -> Copy them with level suffix
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Found {sourceGroundCovers.Count} source groundcover(s) for material '{sourceInternalName}'. Copying to target with level suffix.");

                foreach (var sourceGroundCover in sourceGroundCovers)
                {
                    var sourceGroundCoverName = sourceGroundCover["name"]?.ToString();
                    if (string.IsNullOrEmpty(sourceGroundCoverName)) continue;

                    // Copy source groundcover with level suffix (like in Add mode)
                    var newGroundCover = CopySourceGroundCover(
                        sourceGroundCover,
                        sourceGroundCoverName,
                        targetInternalName,
                        sourceInternalName);

                    if (newGroundCover != null)
                    {
                        newGroundCovers.Add(newGroundCover);
                        var newName = newGroundCover["name"]?.ToString();
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Copied groundcover '{sourceGroundCoverName}' as '{newName}' (layer: {sourceInternalName} → {targetInternalName})");
                    }
                }
            }
        }

        // Now process ALL existing groundcovers to remove/modify layers for replaced materials
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Processing existing groundcovers to remove replaced layers...");

        foreach (var existingGC in existingGroundCovers.ToList())
        {
            var gcNode = existingGC.Value;
            var gcName = gcNode["name"]?.ToString();

            if (string.IsNullOrEmpty(gcName) ||
                gcNode["class"]?.ToString() != "GroundCover") continue; // Skip non-groundcover entries

            // Remove Types that reference any replaced material INTERNAL name
            var (modified, shouldDelete) = RemoveReplacedLayers(gcNode, allReplacedMaterialInternalNames);

            if (shouldDelete)
            {
                // All layers removed -> delete entire groundcover
                deletedGroundCoverNames.Add(gcName);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Deleted groundcover '{gcName}' (all layers referenced replaced materials)");
            }
            else if (modified)
            {
                // Some layers removed -> keep modified groundcover
                modifiedGroundCovers.Add(gcNode);
            }
        }

        // Write changes back to file
        if (newGroundCovers.Any() || deletedGroundCoverNames.Any() || modifiedGroundCovers.Any())
            WriteGroundCoverChangesToFile(targetGcFile, existingGroundCovers, newGroundCovers, modifiedGroundCovers,
                deletedGroundCoverNames);
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
            var gcName = groundCover["name"]?.ToString() ?? "unknown";
            var remainingCount = types.Count;

            if (remainingCount == 0)
            {
                // All Types removed -> should delete entire groundcover
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Groundcover '{gcName}': Removed all {removedCount} layer(s) referencing replaced materials (will delete entire groundcover)");
                return (true, true);
            }

            // Some Types removed, but others remain
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Groundcover '{gcName}': Removed {removedCount} layer(s) referencing replaced materials ({remainingCount} layer(s) remaining)");
            return (true, false);
        }

        return (false, false); // No changes
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

            // Generate new name with level suffix (consistent with Add mode)
            var newName = $"{sourceGroundCoverName}_{_levelNameCopyFrom}";
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
            // Apply new groundcovers
            foreach (var newGC in newGroundCovers)
            {
                var name = newGC["name"]?.ToString();
                if (!string.IsNullOrEmpty(name)) existingGroundCovers[name] = newGC;
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

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Updated groundcovers: {newGroundCovers.Count} added, {modifiedGroundCovers.Count} modified (layers removed), {deletedGroundCoverNames.Count} deleted in {targetFile.FullName}");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error writing groundcover changes: {ex.Message}");
        }
    }
}