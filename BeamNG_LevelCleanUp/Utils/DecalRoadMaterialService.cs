using System.IO.Compression;
using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Provides decalroad material names from two sources:
/// 1. BeamNG game defaults: streamed from art_shapes.zip (no extraction)
/// 2. Level-local: materials tagged "RoadAndPath" from the current level
/// </summary>
public static class DecalRoadMaterialService
{
    // Cached game materials (static — game content doesn't change during session)
    private static List<DecalRoadMaterialInfo>? _cachedGameMaterials;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns all decalroad materials: game defaults + level-local.
    /// Game materials are cached for the session. Level materials are scanned fresh.
    /// </summary>
    public static List<DecalRoadMaterialInfo> GetAllMaterials(string? levelPath = null)
    {
        var result = new List<DecalRoadMaterialInfo>();
        result.AddRange(GetGameMaterials());
        if (!string.IsNullOrEmpty(levelPath))
            result.AddRange(GetLevelMaterials(levelPath));
        return result;
    }

    /// <summary>
    /// Returns game decalroad materials from art_shapes.zip.
    /// Cached after first load.
    /// </summary>
    public static List<DecalRoadMaterialInfo> GetGameMaterials()
    {
        lock (_lock)
        {
            if (_cachedGameMaterials != null)
                return _cachedGameMaterials;

            _cachedGameMaterials = LoadGameMaterialsFromZip();
            return _cachedGameMaterials;
        }
    }

    /// <summary>
    /// Scans level directory for materials tagged "RoadAndPath".
    /// Not cached — call when level changes.
    /// </summary>
    public static List<DecalRoadMaterialInfo> GetLevelMaterials(string levelPath)
    {
        var result = new List<DecalRoadMaterialInfo>();
        try
        {
            var matFiles = Directory.GetFiles(levelPath, "*.materials.json", SearchOption.AllDirectories);

            foreach (var matFile in matFiles)
            {
                try
                {
                    var jsonDoc = JsonUtils.GetValidJsonDocumentFromFilePath(matFile);
                    var options = BeamJsonOptions.GetJsonSerializerOptions();

                    foreach (var property in jsonDoc.RootElement.EnumerateObject())
                    {
                        try
                        {
                            var material = property.Value.Deserialize<MaterialJson>(options);
                            if (material == null) continue;

                            if (string.IsNullOrEmpty(material.Name))
                                material.Name = property.Name;
                            if (string.IsNullOrEmpty(material.InternalName))
                                material.InternalName = property.Name;

                            material.MatJsonFileLocation = matFile;

                            // Fallback: if Stages is null, texture props may be at root level
                            if (material.Stages == null || material.Stages.Count == 0)
                            {
                                var stage = property.Value.Deserialize<MaterialStage>(options);
                                if (stage != null)
                                    material.Stages = [stage];
                            }

                            if (!material.IsRoadAndPath) continue;

                            string? baseColorMap = null;
                            if (material.Stages?.Count > 0)
                            {
                                var stage = material.Stages[0];
                                baseColorMap = stage.BaseColorMap ?? stage.ColorMap ?? stage.DiffuseMap;
                            }

                            result.Add(new DecalRoadMaterialInfo
                            {
                                Name = material.InternalName ?? material.Name,
                                Source = DecalRoadMaterialSource.Level,
                                BaseColorMap = baseColorMap,
                                Tags = material.MaterialTags,
                                MaterialJson = material
                            });
                        }
                        catch
                        {
                            // Skip malformed material entries
                        }
                    }
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Failed to scan materials from {matFile}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to scan level materials at {levelPath}: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Streams main.materials.json from art_shapes.zip and parses decalroad materials.
    /// </summary>
    private static List<DecalRoadMaterialInfo> LoadGameMaterialsFromZip()
    {
        var result = new List<DecalRoadMaterialInfo>();

        try
        {
            var installDir = GameDirectoryService.GetInstallDirectory();
            if (string.IsNullOrEmpty(installDir))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Cannot load game decalroad materials: BeamNG install directory not configured.");
                return result;
            }

            var zipPath = Path.Combine(installDir, "content", "art_shapes.zip");
            if (!File.Exists(zipPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot find art_shapes.zip at: {zipPath}");
                return result;
            }

            const string entryPath = "art/shapes/common/decalroads/main.materials.json";

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(entryPath);
            if (entry == null)
            {
                // Try case-insensitive search
                entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));
            }

            if (entry == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Cannot find {entryPath} in art_shapes.zip");
                return result;
            }

            // Read JSON from zip stream
            string jsonString;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream))
            {
                jsonString = reader.ReadToEnd();
            }

            // Parse with BeamNG's relaxed JSON handling
            var jsonDoc = JsonUtils.GetValidJsonDocumentFromString(jsonString, $"art_shapes.zip/{entryPath}");
            var options = BeamJsonOptions.GetJsonSerializerOptions();

            foreach (var property in jsonDoc.RootElement.EnumerateObject())
            {
                try
                {
                    var material = property.Value.Deserialize<MaterialJson>(options);
                    if (material == null) continue;

                    if (string.IsNullOrEmpty(material.Name))
                        material.Name = property.Name;
                    if (string.IsNullOrEmpty(material.InternalName))
                        material.InternalName = property.Name;

                    // Fallback: if Stages is null, texture props may be at root level
                    if (material.Stages == null || material.Stages.Count == 0)
                    {
                        var stage = property.Value.Deserialize<MaterialStage>(options);
                        if (stage != null)
                            material.Stages = [stage];
                    }

                    // Extract base color map for display/preview
                    string? baseColorMap = null;
                    if (material.Stages?.Count > 0)
                    {
                        var stage = material.Stages[0];
                        baseColorMap = stage.BaseColorMap ?? stage.ColorMap ?? stage.DiffuseMap;
                    }

                    // Mark as game asset (no filesystem path)
                    material.MatJsonFileLocation = string.Empty;

                    result.Add(new DecalRoadMaterialInfo
                    {
                        Name = material.InternalName ?? material.Name,
                        Source = DecalRoadMaterialSource.Game,
                        BaseColorMap = baseColorMap,
                        Tags = material.MaterialTags,
                        MaterialJson = material
                    });
                }
                catch
                {
                    // Skip malformed material entries
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Loaded {result.Count} decalroad materials from art_shapes.zip");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Failed to load game decalroad materials: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Clears the cached game materials (e.g., if game directory changes).
    /// </summary>
    public static void ClearCache()
    {
        lock (_lock)
        {
            _cachedGameMaterials = null;
        }
    }
}
