using System.Text.Json;
using System.Text.Json.Nodes;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Creates or updates the level-root map.json with the generated AI waypoint segments
/// (ge/map.lua "manual road segments"). Segments whose key starts with "MT_" are owned by the
/// generator and replaced on every run; all other (hand-authored) segments are preserved
/// verbatim. The game watches map.json and hot-reloads the navgraph on change.
/// </summary>
public class AiMapJsonWriter
{
    /// <summary>Key prefix marking segments owned (and replaced) by the generator.</summary>
    public const string ManagedSegmentPrefix = "MT_";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Merges the given segments into {levelPath}/map.json. Always call this — with an empty list
    /// it removes stale generated segments (deleting the file when nothing else remains).
    /// </summary>
    /// <returns>Number of generated segments written, or -1 if an existing file was unparseable
    /// (the file is then left untouched to protect hand-authored content).</returns>
    public static int Write(IReadOnlyList<GeneratedAiWaypointSegment> segments, string levelPath)
    {
        var mapJsonPath = Path.Combine(levelPath, "map.json");

        // Preserve hand-authored segments from an existing file.
        var foreignSegments = new List<(string Key, JsonNode Value)>();
        if (File.Exists(mapJsonPath))
        {
            JsonNode? existing;
            try
            {
                existing = JsonNode.Parse(File.ReadAllText(mapJsonPath), documentOptions: ParseOptions);
            }
            catch (JsonException ex)
            {
                Console.WriteLine(
                    $"AiMapJsonWriter: existing {mapJsonPath} is not parseable ({ex.Message}) — " +
                    "leaving it untouched, generated AI segments were NOT written");
                return -1;
            }

            if (existing?["segments"] is JsonObject existingSegments)
                foreach (var (key, value) in existingSegments)
                    if (value != null && !key.StartsWith(ManagedSegmentPrefix, StringComparison.Ordinal))
                        foreignSegments.Add((key, value.DeepClone()));
        }

        if (segments.Count == 0 && foreignSegments.Count == 0)
        {
            // Nothing left to describe — remove a file that only contained our stale segments.
            if (File.Exists(mapJsonPath))
            {
                File.Delete(mapJsonPath);
                Console.WriteLine($"AiMapJsonWriter: removed {mapJsonPath} (no segments remain)");
            }

            return 0;
        }

        var segmentsObject = new JsonObject();
        foreach (var (key, value) in foreignSegments)
            segmentsObject[key] = value;

        foreach (var segment in segments.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var nodes = new JsonArray();
            foreach (var wp in segment.Waypoints)
                nodes.Add(wp.Name);

            var entry = new JsonObject
            {
                ["nodes"] = nodes,
                ["drivability"] = segment.Drivability,
                ["oneWay"] = segment.OneWay,
                ["flipDirection"] = segment.FlipDirection
            };

            // Same contract as the AI DecalRoad writer: lane counts only when explicitly derived.
            if (!segment.AutoLanes)
            {
                entry["autoLanes"] = false;
                entry["lanesLeft"] = segment.LanesLeft;
                entry["lanesRight"] = segment.LanesRight;
            }

            if (segment.GatedRoad)
                entry["gatedRoad"] = true;

            segmentsObject[segment.Name] = entry;
        }

        var root = new JsonObject { ["segments"] = segmentsObject };
        File.WriteAllText(mapJsonPath, root.ToJsonString(WriteOptions));

        Console.WriteLine(
            $"AiMapJsonWriter: wrote {segments.Count} generated + {foreignSegments.Count} preserved " +
            $"segment(s) to {mapJsonPath}");
        return segments.Count;
    }
}
