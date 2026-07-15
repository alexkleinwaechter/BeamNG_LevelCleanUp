using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
///     One AI navigation waypoint: a BeamNGWaypoint scene object. The game reads only name,
///     position and scale (radius = max scale component, see ge/map.lua getSceneWaypointRadius);
///     rotation is irrelevant for the navgraph and therefore not modeled.
/// </summary>
public readonly record struct AiWaypoint(string Name, Vector3 Position, float Radius);

/// <summary>
///     An AI waypoint path replacing the AI DecalRoad over one bridge or tunnel stretch of a spline.
///     Written as BeamNGWaypoint objects (MissionGroup) plus a segment entry in the level-root
///     map.json. The game chains the named waypoints into navgraph edges and fuses the endpoint
///     waypoints with the adjacent ground AI DecalRoad nodes by radius overlap
///     (ge/map.lua mergeOverlappingNodes), so no explicit stitching is required.
/// </summary>
public class GeneratedAiWaypointSegment
{
    /// <summary>map.json segment key, e.g. "MT_bridge_012_00". Must be unique per level.</summary>
    public required string Name { get; init; }

    /// <summary>Ordered waypoints; edge direction follows list order (flipped by FlipDirection).</summary>
    public required List<AiWaypoint> Waypoints { get; init; }

    public float Drivability { get; init; } = 1.0f;
    public bool OneWay { get; init; }
    public bool FlipDirection { get; init; }

    /// <summary>When false, LanesLeft/LanesRight are written to map.json (autoLanes: false).</summary>
    public bool AutoLanes { get; init; } = true;

    public int LanesLeft { get; init; }
    public int LanesRight { get; init; }
    public bool GatedRoad { get; init; }

    /// <summary>Source spline, for diagnostics.</summary>
    public int SplineId { get; init; }

    /// <summary>True for tunnel stretches, false for bridge decks (naming/diagnostics only).</summary>
    public bool IsTunnel { get; init; }
}
