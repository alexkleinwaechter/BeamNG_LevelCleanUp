using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Terrain.Services.DecalRoad;

/// <summary>
/// Writes the BeamNGWaypoint objects backing generated AI waypoint segments to BeamNG's
/// MissionGroup scene hierarchy as NDJSON. The matching map.json segments are written by
/// <see cref="AiMapJsonWriter"/>.
///
/// Output structure:
///   main/MissionGroup/items.level.json       ← SimGroup "MT_waypoints" entry
///   main/MissionGroup/MT_waypoints/
///     items.level.json                        ← BeamNGWaypoint NDJSON lines
///
/// The game reads only name, position and scale (radius = max scale component,
/// ge/map.lua getSceneWaypointRadius); rotation is irrelevant and not written.
/// </summary>
public class AiWaypointSceneWriter
{
    public const string GroupName = "MT_waypoints";

    /// <summary>
    /// Writes all waypoints of the given segments to the level directory.
    /// </summary>
    /// <returns>Number of BeamNGWaypoint objects written.</returns>
    public int WriteAll(IReadOnlyList<GeneratedAiWaypointSegment> segments, string levelPath)
    {
        var waypointCount = segments.Sum(s => s.Waypoints.Count);
        if (waypointCount == 0) return 0;

        var missionGroupPath = Path.Combine(levelPath, "main", "MissionGroup");
        var parentItemsPath = Path.Combine(missionGroupPath, "items.level.json");
        var groupDir = Path.Combine(missionGroupPath, GroupName);

        DecalRoadSceneWriter.EnsureSimGroupInParent(parentItemsPath, GroupName, "MissionGroup");

        var items = new List<JsonDict>(waypointCount);
        foreach (var segment in segments)
        foreach (var wp in segment.Waypoints)
        {
            var dict = new JsonDict();
            dict["name"] = wp.Name;
            dict["class"] = "BeamNGWaypoint";
            dict["persistentId"] = Guid.NewGuid().ToString();
            dict["__parent"] = GroupName;
            dict["position"] = new float[] { wp.Position.X, wp.Position.Y, wp.Position.Z };
            dict["scale"] = new float[] { wp.Radius, wp.Radius, wp.Radius };
            items.Add(dict);
        }

        Directory.CreateDirectory(groupDir);
        var itemsPath = Path.Combine(groupDir, "items.level.json");
        SimItemsJsonSerializer.Save(itemsPath, items);

        Console.WriteLine(
            $"AiWaypointSceneWriter: Wrote {items.Count} waypoints for {segments.Count} segments to {groupDir}");
        return items.Count;
    }

    /// <summary>
    /// Removes existing MT_waypoints directory for re-generation.
    /// </summary>
    public static void CleanPrevious(string levelPath)
    {
        var groupDir = Path.Combine(levelPath, "main", "MissionGroup", GroupName);
        if (Directory.Exists(groupDir))
            Directory.Delete(groupDir, recursive: true);
    }
}
