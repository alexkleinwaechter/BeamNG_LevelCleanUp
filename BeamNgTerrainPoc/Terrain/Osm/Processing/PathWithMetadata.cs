using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Associates a transformed path (in meter coordinates) with its OSM metadata
/// for use in the node-based path connection algorithm.
/// 
/// Carries node IDs from the original OSM way, enabling topological connectivity
/// (shared OSM node IDs) instead of purely geometric endpoint proximity matching.
/// 
/// Structure metadata (IsBridge, IsTunnel, etc.) is carried alongside but NOT used
/// for merge decisions — protected structure paths bypass the merge algorithm entirely.
/// </summary>
internal class PathWithMetadata
{
    /// <summary>
    /// The path's control points in meter coordinates.
    /// Mutable — modified during merge operations.
    /// </summary>
    public List<Vector2> Points { get; set; }

    /// <summary>
    /// OSM node ID of the first point, or null if the start was cropped at terrain boundary.
    /// Mutable — updated when paths are merged.
    /// </summary>
    public long? StartNodeId { get; set; }

    /// <summary>
    /// OSM node ID of the last point, or null if the end was cropped at terrain boundary.
    /// Mutable — updated when paths are merged.
    /// </summary>
    public long? EndNodeId { get; set; }

    /// <summary>
    /// The original OSM way ID. Used for debugging/logging.
    /// For merged paths, reflects the first way that was used as the merge base.
    /// </summary>
    public long OsmWayId { get; init; }

    /// <summary>
    /// OSM tags from the original feature (highway, name, ref, etc.).
    /// Used for anti-merge rules (highway type mismatch, name conflict).
    /// For merged paths, contains the tags of the merge base path.
    /// </summary>
    public Dictionary<string, string> Tags { get; init; } = new();

    // ---- Structure metadata (carried for completeness, not used in merge decisions) ----

    public bool IsBridge { get; init; }
    public bool IsTunnel { get; init; }
    public StructureType StructureType { get; init; }
    public int Layer { get; init; }
    public string? BridgeStructureType { get; init; }

    /// <summary>
    /// All original OSM way IDs that have been merged into this path.
    /// Initialized with just {OsmWayId} for single-way paths.
    /// Unioned during merges so that route relation membership checks
    /// remain accurate even after Tier 0 or Tier 1-3 merges combine multiple ways.
    /// </summary>
    public HashSet<long> AllWayIds { get; set; }

    /// <summary>
    /// Per-segment lane configuration parsed from OSM tags.
    /// Empty list means no lane data available (use defaults at generation time).
    /// Segments survive merges via LaneSegmentOps.
    /// </summary>
    public List<LaneSegment> LaneSegments { get; set; } = [];

    public PathWithMetadata(
        List<Vector2> points,
        long? startNodeId,
        long? endNodeId,
        long osmWayId,
        Dictionary<string, string> tags,
        bool isBridge,
        bool isTunnel,
        StructureType structureType,
        int layer,
        string? bridgeStructureType)
    {
        Points = points;
        StartNodeId = startNodeId;
        EndNodeId = endNodeId;
        OsmWayId = osmWayId;
        Tags = tags;
        IsBridge = isBridge;
        IsTunnel = isTunnel;
        StructureType = structureType;
        Layer = layer;
        BridgeStructureType = bridgeStructureType;
        AllWayIds = new HashSet<long> { osmWayId };
    }
}
