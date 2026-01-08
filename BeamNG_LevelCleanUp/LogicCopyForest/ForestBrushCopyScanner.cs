using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;

namespace BeamNG_LevelCleanUp.LogicCopyForest;

/// <summary>
/// Scans source level for forest brushes and builds copy asset list.
/// Parses main.forestbrushes4.json (NDJSON format) and art/forest/managedItemData.json.
/// </summary>
public class ForestBrushCopyScanner
{
    private readonly string _sourceLevelPath;
    private readonly string _targetLevelPath;
    private readonly List<CopyAsset> _copyAssets;

    public ForestBrushCopyScanner(string sourceLevelPath, string targetLevelPath, List<CopyAsset> copyAssets)
    {
        _sourceLevelPath = sourceLevelPath;
        _targetLevelPath = targetLevelPath;
        _copyAssets = copyAssets;
    }

    /// <summary>
    /// Scans source level and adds forest brush CopyAssets to the list
    /// </summary>
    public void ScanForestBrushes()
    {
        // 1. Parse main.forestbrushes4.json (NDJSON)
        var brushesPath = FindForestBrushesFile(_sourceLevelPath);
        if (string.IsNullOrEmpty(brushesPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "No main.forestbrushes4.json found in source level - no forest brushes to copy");
            return;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found forest brushes file: {Path.GetFileName(brushesPath)}");

        var brushes = ParseForestBrushesNdjson(brushesPath);
        if (!brushes.Any())
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "No ForestBrush entries found in main.forestbrushes4.json");
            return;
        }

        // 2. Parse art/forest/managedItemData.json
        var itemDataPath = FindManagedItemDataFile(_sourceLevelPath);
        var itemDataMap = ParseManagedItemData(itemDataPath);

        // 3. Get existing brushes in target for duplicate detection
        var targetBrushNames = GetTargetBrushNames();

        // 4. Build copy assets for each brush
        foreach (var brush in brushes)
        {
            var copyAsset = new CopyAsset
            {
                Identifier = Guid.NewGuid(),
                Name = brush.InternalName,
                CopyAssetType = CopyAssetType.ForestBrush,
                Duplicate = targetBrushNames.Contains(brush.Name, StringComparer.OrdinalIgnoreCase) ||
                            targetBrushNames.Contains(brush.InternalName, StringComparer.OrdinalIgnoreCase),
                DuplicateFrom = targetBrushNames.Contains(brush.Name, StringComparer.OrdinalIgnoreCase) ||
                                targetBrushNames.Contains(brush.InternalName, StringComparer.OrdinalIgnoreCase)
                    ? "Target Level"
                    : null,
                TargetPath = _targetLevelPath,
                ForestBrushInfo = brush
            };

            // Calculate total size from all referenced shapes
            var totalSize = 0L;
            foreach (var elementRef in brush.ReferencedItemDataNames)
            {
                if (itemDataMap.TryGetValue(elementRef, out var itemData))
                {
                    totalSize += GetShapeFileSize(itemData.ShapeFile);
                }
            }

            copyAsset.SizeMb = Math.Round(totalSize / 1024.0 / 1024.0, 2);

            _copyAssets.Add(copyAsset);
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found {brushes.Count} forest brushes with {itemDataMap.Count} item data definitions");
    }

    /// <summary>
    /// Parses the NDJSON format main.forestbrushes4.json file
    /// </summary>
    private List<ForestBrushInfo> ParseForestBrushesNdjson(string filePath)
    {
        var brushes = new Dictionary<string, ForestBrushInfo>(StringComparer.OrdinalIgnoreCase);
        var elements = new List<(string parentName, ForestBrushElementInfo element)>();

        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line, BeamJsonOptions.GetJsonDocumentOptions());
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("class", out var classProperty))
                        continue;

                    var objClass = classProperty.GetString();

                    if (objClass == "ForestBrush")
                    {
                        var brush = new ForestBrushInfo
                        {
                            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            InternalName = root.TryGetProperty("internalName", out var iname)
                                ? iname.GetString() ?? ""
                                : "",
                            PersistentId = root.TryGetProperty("persistentId", out var pid)
                                ? pid.GetString() ?? ""
                                : "",
                            ParentName = root.TryGetProperty("__parent", out var parent)
                                ? parent.GetString() ?? ""
                                : "",
                            // Store raw JSON to preserve unknown properties
                            RawJson = line
                        };

                        // Some brushes have forestItemData directly (single-item brush)
                        if (root.TryGetProperty("forestItemData", out var fid))
                        {
                            var fidValue = fid.GetString();
                            if (!string.IsNullOrEmpty(fidValue))
                            {
                                brush.DirectForestItemData = fidValue;
                                brush.ReferencedItemDataNames.Add(fidValue);
                            }
                        }

                        // Use Name as key since elements reference by name
                        if (!string.IsNullOrEmpty(brush.Name))
                            brushes[brush.Name] = brush;
                    }
                    else if (objClass == "ForestBrushElement")
                    {
                        var element = new ForestBrushElementInfo
                        {
                            InternalName = root.TryGetProperty("internalName", out var iname)
                                ? iname.GetString() ?? ""
                                : "",
                            PersistentId = root.TryGetProperty("persistentId", out var pid)
                                ? pid.GetString() ?? ""
                                : "",
                            ForestItemDataRef = root.TryGetProperty("forestItemData", out var fid)
                                ? fid.GetString() ?? ""
                                : "",
                            ParentBrushName = root.TryGetProperty("__parent", out var parent)
                                ? parent.GetString() ?? ""
                                : "",
                            // Store raw JSON to preserve unknown properties
                            RawJson = line
                        };

                        // Parse optional properties (for display purposes)
                        if (root.TryGetProperty("scaleMin", out var smin) &&
                            smin.ValueKind == JsonValueKind.Number)
                            element.ScaleMin = smin.GetSingle();

                        if (root.TryGetProperty("scaleMax", out var smax) &&
                            smax.ValueKind == JsonValueKind.Number)
                            element.ScaleMax = smax.GetSingle();

                        if (root.TryGetProperty("probability", out var prob) &&
                            prob.ValueKind == JsonValueKind.Number)
                            element.Probability = prob.GetSingle();

                        if (root.TryGetProperty("sinkMin", out var skmin) &&
                            skmin.ValueKind == JsonValueKind.Number)
                            element.SinkMin = skmin.GetSingle();

                        if (root.TryGetProperty("sinkMax", out var skmax) &&
                            skmax.ValueKind == JsonValueKind.Number)
                            element.SinkMax = skmax.GetSingle();

                        if (root.TryGetProperty("slopeMin", out var slmin) &&
                            slmin.ValueKind == JsonValueKind.Number)
                            element.SlopeMin = slmin.GetSingle();

                        if (root.TryGetProperty("slopeMax", out var slmax) &&
                            slmax.ValueKind == JsonValueKind.Number)
                            element.SlopeMax = slmax.GetSingle();

                        if (root.TryGetProperty("elevationMin", out var elmin) &&
                            elmin.ValueKind == JsonValueKind.Number)
                            element.ElevationMin = elmin.GetSingle();

                        if (root.TryGetProperty("elevationMax", out var elmax) &&
                            elmax.ValueKind == JsonValueKind.Number)
                            element.ElevationMax = elmax.GetSingle();

                        if (root.TryGetProperty("rotationRange", out var rot) &&
                            rot.ValueKind == JsonValueKind.Number)
                            element.RotationRange = rot.GetInt32();

                        elements.Add((element.ParentBrushName, element));
                    }
                    // SimGroup (ForestBrushGroup) - just skip, we create our own when copying
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Failed to parse forest brush line: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to read forest brushes file: {ex.Message}");
            return new List<ForestBrushInfo>();
        }

        // Link elements to brushes
        foreach (var (parentName, element) in elements)
        {
            if (brushes.TryGetValue(parentName, out var brush))
            {
                brush.Elements.Add(element);
                if (!string.IsNullOrEmpty(element.ForestItemDataRef) &&
                    !brush.ReferencedItemDataNames.Contains(element.ForestItemDataRef,
                        StringComparer.OrdinalIgnoreCase))
                {
                    brush.ReferencedItemDataNames.Add(element.ForestItemDataRef);
                }
            }
        }

        return brushes.Values.ToList();
    }

    /// <summary>
    /// Parses managedItemData.json to get ForestItemData definitions
    /// </summary>
    private Dictionary<string, ForestItemDataInfo> ParseManagedItemData(string filePath)
    {
        var result = new Dictionary<string, ForestItemDataInfo>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "managedItemData.json not found - forest item data will be incomplete");
            return result;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json, BeamJsonOptions.GetJsonDocumentOptions());

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                try
                {
                    var itemData = new ForestItemDataInfo
                    {
                        Name = prop.Name,
                        InternalName = prop.Value.TryGetProperty("internalName", out var iname)
                            ? iname.GetString() ?? prop.Name
                            : prop.Name,
                        PersistentId = prop.Value.TryGetProperty("persistentId", out var pid)
                            ? pid.GetString() ?? ""
                            : "",
                        ShapeFile = prop.Value.TryGetProperty("shapeFile", out var sf)
                            ? sf.GetString() ?? ""
                            : "",
                        Class = prop.Value.TryGetProperty("class", out var cls)
                            ? cls.GetString() ?? "TSForestItemData"
                            : "TSForestItemData"
                    };

                    // Parse physics properties
                    if (prop.Value.TryGetProperty("radius", out var radius) &&
                        radius.ValueKind == JsonValueKind.Number)
                        itemData.Radius = radius.GetSingle();

                    if (prop.Value.TryGetProperty("mass", out var mass) &&
                        mass.ValueKind == JsonValueKind.Number)
                        itemData.Mass = mass.GetSingle();

                    if (prop.Value.TryGetProperty("rigidity", out var rigidity) &&
                        rigidity.ValueKind == JsonValueKind.Number)
                        itemData.Rigidity = rigidity.GetSingle();

                    if (prop.Value.TryGetProperty("dampingCoefficient", out var damping) &&
                        damping.ValueKind == JsonValueKind.Number)
                        itemData.DampingCoefficient = damping.GetSingle();

                    if (prop.Value.TryGetProperty("tightnessCoefficient", out var tightness) &&
                        tightness.ValueKind == JsonValueKind.Number)
                        itemData.TightnessCoefficient = tightness.GetSingle();

                    // Parse wind animation properties
                    if (prop.Value.TryGetProperty("windScale", out var windScale) &&
                        windScale.ValueKind == JsonValueKind.Number)
                        itemData.WindScale = windScale.GetSingle();

                    if (prop.Value.TryGetProperty("branchAmp", out var branchAmp) &&
                        branchAmp.ValueKind == JsonValueKind.Number)
                        itemData.BranchAmp = branchAmp.GetSingle();

                    if (prop.Value.TryGetProperty("trunkBendScale", out var trunkBend) &&
                        trunkBend.ValueKind == JsonValueKind.Number)
                        itemData.TrunkBendScale = trunkBend.GetSingle();

                    if (prop.Value.TryGetProperty("detailAmp", out var detailAmp) &&
                        detailAmp.ValueKind == JsonValueKind.Number)
                        itemData.DetailAmp = detailAmp.GetSingle();

                    if (prop.Value.TryGetProperty("detailFreq", out var detailFreq) &&
                        detailFreq.ValueKind == JsonValueKind.Number)
                        itemData.DetailFreq = detailFreq.GetSingle();

                    // Store raw JSON for preserving unknown fields during copy
                    itemData.RawJsonText = prop.Value.GetRawText();

                    result[prop.Name] = itemData;
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Error parsing ForestItemData '{prop.Name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to parse managedItemData.json: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Finds the main.forestbrushes4.json file in a level
    /// </summary>
    private string FindForestBrushesFile(string levelPath)
    {
        // Try common locations
        var candidates = new[]
        {
            Path.Combine(levelPath, "main.forestbrushes4.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        // Search recursively
        try
        {
            var files = Directory.GetFiles(levelPath, "main.forestbrushes4.json", SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the managedItemData.json file in a level
    /// </summary>
    private string FindManagedItemDataFile(string levelPath)
    {
        // Try common locations
        var candidates = new[]
        {
            Path.Combine(levelPath, "art", "forest", "managedItemData.json"),
            Path.Combine(levelPath, "forest", "managedItemData.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        // Search recursively
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

    /// <summary>
    /// Gets brush names already in the target level for duplicate detection
    /// </summary>
    private HashSet<string> GetTargetBrushNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var brushesPath = FindForestBrushesFile(_targetLevelPath);

        if (string.IsNullOrEmpty(brushesPath) || !File.Exists(brushesPath))
            return names;

        try
        {
            foreach (var line in File.ReadAllLines(brushesPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line, BeamJsonOptions.GetJsonDocumentOptions());
                    if (doc.RootElement.TryGetProperty("class", out var cls) &&
                        cls.GetString() == "ForestBrush")
                    {
                        if (doc.RootElement.TryGetProperty("name", out var name))
                            names.Add(name.GetString() ?? "");

                        if (doc.RootElement.TryGetProperty("internalName", out var iname))
                            names.Add(iname.GetString() ?? "");
                    }
                }
                catch
                {
                    /* ignore parse errors */
                }
            }
        }
        catch
        {
            /* ignore file read errors */
        }

        return names;
    }

    /// <summary>
    /// Calculates the approximate size of a shape file and its materials
    /// </summary>
    private long GetShapeFileSize(string shapeFile)
    {
        if (string.IsNullOrEmpty(shapeFile)) return 0;

        try
        {
            var fullPath = PathResolver.ResolvePath(_sourceLevelPath, shapeFile, true);
            if (!File.Exists(fullPath)) return 0;

            var size = new FileInfo(fullPath).Length;

            // Add .cdae file size if present
            var cdaePath = Path.ChangeExtension(fullPath, ".cdae");
            if (File.Exists(cdaePath))
            {
                size += new FileInfo(cdaePath).Length;
            }

            // Add materials file size estimate
            var materialsPath = Path.ChangeExtension(fullPath, ".materials.json");
            if (File.Exists(materialsPath))
            {
                size += new FileInfo(materialsPath).Length;
            }

            // Also check for main.materials.json in same directory
            var mainMaterialsPath = Path.Combine(Path.GetDirectoryName(fullPath) ?? "", "main.materials.json");
            if (File.Exists(mainMaterialsPath))
            {
                size += new FileInfo(mainMaterialsPath).Length / 10; // Estimate only portion is relevant
            }

            return size;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the dictionary of ForestItemData from the source level.
    /// Used by ForestBrushCopier for the actual copy operation.
    /// </summary>
    public Dictionary<string, ForestItemDataInfo> GetSourceItemData()
    {
        var itemDataPath = FindManagedItemDataFile(_sourceLevelPath);
        return ParseManagedItemData(itemDataPath);
    }
}
