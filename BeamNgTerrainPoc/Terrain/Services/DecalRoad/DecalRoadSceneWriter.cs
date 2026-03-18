using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Writes GeneratedDecalRoad objects to BeamNG's MissionGroup scene hierarchy as NDJSON.
///
/// Output structure:
///   main/MissionGroup/items.level.json         ← SimGroup "MT_decalroads" entry
///   main/MissionGroup/MT_decalroads/
///     items.level.json                          ← per-spline SimGroup entries
///     {SplineName}/items.level.json             ← DecalRoad NDJSON lines
/// </summary>
public class DecalRoadSceneWriter
{
    public const string GroupName = "MT_decalroads";

    /// <summary>
    /// Writes all generated DecalRoads to the level directory.
    /// </summary>
    /// <param name="decalRoads">Generated DecalRoad objects.</param>
    /// <param name="levelPath">Path to the level's root directory
    /// (e.g., .../levels/myLevel).</param>
    /// <returns>Number of DecalRoad objects written.</returns>
    public int WriteAll(IReadOnlyList<GeneratedDecalRoad> decalRoads, string levelPath)
    {
        if (decalRoads.Count == 0) return 0;

        var missionGroupPath = Path.Combine(levelPath, "main", "MissionGroup");
        var parentItemsPath = Path.Combine(missionGroupPath, "items.level.json");
        var groupDir = Path.Combine(missionGroupPath, GroupName);

        // 1. Ensure MT_decalroads SimGroup exists in parent
        EnsureSimGroupInParent(parentItemsPath, "MissionGroup");

        // 2. Group DecalRoads by parent spline group
        var bySpline = decalRoads.GroupBy(d => d.ParentGroupName).ToList();

        // 3. Write per-spline SimGroup entries in MT_decalroads/items.level.json
        var splineGroupItems = new List<JsonDict>();
        foreach (var group in bySpline)
        {
            var dict = new JsonDict();
            dict["class"] = "SimGroup";
            dict["name"] = group.Key;
            dict["persistentId"] = Guid.NewGuid().ToString();
            dict["__parent"] = GroupName;
            splineGroupItems.Add(dict);
        }

        var groupItemsPath = Path.Combine(groupDir, "items.level.json");
        Directory.CreateDirectory(groupDir);
        SimItemsJsonSerializer.Save(groupItemsPath, splineGroupItems);

        // 4. Write DecalRoad entries per spline subfolder
        int totalWritten = 0;
        foreach (var group in bySpline)
        {
            var splineDir = Path.Combine(groupDir, group.Key);
            Directory.CreateDirectory(splineDir);

            var items = new List<JsonDict>();
            foreach (var dr in group)
            {
                items.Add(CreateDecalRoadEntry(dr));
                totalWritten++;
            }

            var itemsPath = Path.Combine(splineDir, "items.level.json");
            SimItemsJsonSerializer.Save(itemsPath, items);
        }

        Console.WriteLine(
            $"DecalRoadSceneWriter: Wrote {totalWritten} DecalRoads in {bySpline.Count} groups to {groupDir}");
        return totalWritten;
    }

    /// <summary>
    /// Removes existing MT_decalroads directory for re-generation.
    /// </summary>
    public static void CleanPrevious(string levelPath)
    {
        var groupDir = Path.Combine(levelPath, "main", "MissionGroup", GroupName);
        if (Directory.Exists(groupDir))
            Directory.Delete(groupDir, recursive: true);
    }

    /// <summary>
    /// Ensures a SimGroup entry for MT_decalroads exists in the parent items.level.json.
    /// If a SimGroup with matching name already exists, it is left untouched (idempotent).
    /// </summary>
    private void EnsureSimGroupInParent(string parentItemsPath, string parentGroupName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(parentItemsPath)!);

        var lines = File.Exists(parentItemsPath)
            ? File.ReadAllLines(parentItemsPath).ToList()
            : new List<string>();

        // Check if a SimGroup with our name already exists
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("class", out var cls) && cls.GetString() == "SimGroup" &&
                    root.TryGetProperty("name", out var name) && name.GetString() == GroupName)
                    return; // Already exists
            }
            catch (JsonException) { }
        }

        // Append the SimGroup entry
        var entry = new Dictionary<string, object>
        {
            { "name", GroupName },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "__parent", parentGroupName }
        };
        lines.Add(JsonSerializer.Serialize(entry));
        File.WriteAllLines(parentItemsPath, lines);

        Console.WriteLine($"DecalRoadSceneWriter: Added '{GroupName}' SimGroup to {parentItemsPath}");
    }

    private static JsonDict CreateDecalRoadEntry(GeneratedDecalRoad dr)
    {
        var dict = new JsonDict();
        dict["class"] = "DecalRoad";
        dict["persistentId"] = Guid.NewGuid().ToString();
        dict["__parent"] = dr.ParentGroupName;
        dict["name"] = dr.Name;
        dict["material"] = dr.Material;
        dict["improvedSpline"] = dr.ImprovedSpline;
        dict["smoothness"] = dr.Smoothness;
        dict["detail"] = dr.Detail;
        dict["overObjects"] = dr.OverObjects;
        dict["textureLength"] = dr.TextureLength;
        dict["renderPriority"] = dr.RenderPriority;
        dict["startEndFade"] = dr.StartEndFade;
        dict["distanceFade"] = dr.DistanceFade;
        dict["position"] = new float[] { dr.Position.X, dr.Position.Y, dr.Position.Z };

        // AI road pathfinding properties
        if (dr.IsAIRoad)
        {
            dict["drivability"] = dr.Drivability;
            dict["autoLanes"] = dr.AutoLanes;
            dict["lanesLeft"] = dr.LanesLeft;
            dict["lanesRight"] = dr.LanesRight;
            dict["oneWay"] = dr.OneWay;
            dict["flipDirection"] = dr.FlipDirection;
            dict["gatedRoad"] = false;
            dict["autoJunction"] = true;
            dict["useSubdivisions"] = true;
        }

        // Nodes: array of [x, y, z, width] arrays
        dict["nodes"] = dr.Nodes.Select(n => (object)n).ToArray();

        return dict;
    }
}
