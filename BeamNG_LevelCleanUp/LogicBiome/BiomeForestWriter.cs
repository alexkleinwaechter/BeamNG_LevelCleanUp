using System.Text.Json;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.Biome;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNG_LevelCleanUp.LogicBiome;

/// <summary>
/// Writes sampled placements as a forest4.json NDJSON file owned by one biome layer,
/// and makes sure a Forest scene object exists (without it the game renders nothing).
/// </summary>
public static class BiomeForestWriter
{
    /// <summary>
    /// Readable owned-file name: material/source name plus a short id suffix for uniqueness,
    /// e.g. "forest/MT_biome_Grass2_cc21923a.forest4.json". The manifest stores the actual
    /// path per layer, so older GUID-named files remain deletable.
    /// </summary>
    public static string GetForestFileRelativePath(string sourceKey, string layerId)
    {
        var idSuffix = layerId.Length > 8 ? layerId[..8] : layerId;
        return $"forest/MT_biome_{SanitizeFileNamePart(sourceKey)}_{idSuffix}.forest4.json";
    }

    /// <summary>All biome-owned forest files share this prefix — the orphan sweep relies on it.</summary>
    public const string OwnedFilePrefix = "MT_biome_";

    private static string SanitizeFileNamePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "layer";

        var chars = value.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();
        var sanitized = new string(chars);
        return sanitized.Length > 40 ? sanitized[..40] : sanitized;
    }

    /// <summary>
    /// Ensures a Forest scene object exists in the MissionGroup. Preferred insertion point
    /// is the items.level.json that already holds the TerrainBlock (its parent group is
    /// guaranteed valid); fallback is the level_object/vegetation layout the level
    /// creation wizard uses.
    /// </summary>
    public static void EnsureForestSceneObject(string levelPath)
    {
        var mainPath = Path.Join(levelPath, "main");
        if (!Directory.Exists(mainPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "Level has no main/ folder — cannot ensure a Forest scene object; placed items may not render.");
            return;
        }

        string? terrainBlockFile = null;
        string? terrainBlockParent = null;

        foreach (var itemsFile in Directory.GetFiles(mainPath, "items.level.json", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadAllLines(itemsFile))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line, BeamJsonOptions.GetJsonDocumentOptions());
                    if (!doc.RootElement.TryGetProperty("class", out var cls))
                        continue;
                    var className = cls.GetString();
                    if (className == "Forest")
                    {
                        return; // already present — nothing to do
                    }
                    if (className == "TerrainBlock" && terrainBlockFile == null)
                    {
                        terrainBlockFile = itemsFile;
                        terrainBlockParent = doc.RootElement.TryGetProperty("__parent", out var parent)
                            ? parent.GetString()
                            : null;
                    }
                }
                catch (JsonException)
                {
                    // skip malformed lines
                }
            }
        }

        string targetFile;
        string? parentName;
        if (terrainBlockFile != null)
        {
            targetFile = terrainBlockFile;
            parentName = terrainBlockParent;
        }
        else
        {
            targetFile = Path.Join(mainPath, "MissionGroup", "level_object", "vegetation", "items.level.json");
            parentName = "vegetation";
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        }

        var forestLine = BuildForestObjectLine(parentName);
        if (File.Exists(targetFile))
        {
            var content = File.ReadAllText(targetFile);
            var separator = content.Length == 0 || content.EndsWith('\n') ? string.Empty : Environment.NewLine;
            File.AppendAllText(targetFile, separator + forestLine + Environment.NewLine);
        }
        else
        {
            File.WriteAllText(targetFile, forestLine + Environment.NewLine);
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Created Forest scene object (\"theForest\") in {Path.GetFileName(Path.GetDirectoryName(targetFile))}/items.level.json.");
    }

    private static string BuildForestObjectLine(string? parentName)
    {
        var parentProperty = string.IsNullOrEmpty(parentName)
            ? string.Empty
            : $"\"__parent\":\"{parentName}\",";
        return "{\"name\":\"theForest\",\"class\":\"Forest\"," + parentProperty +
               $"\"persistentId\":\"{Guid.NewGuid().ToString().ToLowerInvariant()}\"," +
               "\"position\":[0,0,0],\"rotationMatrix\":[1,0,0,0,1,0,0,0,1],\"scale\":[1,1,1],\"lodReflectScalar\":2}";
    }
}
