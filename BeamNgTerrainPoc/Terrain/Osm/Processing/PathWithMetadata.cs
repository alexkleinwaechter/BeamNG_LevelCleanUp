using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
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

    /// <summary>
    /// Bridge/tunnel sub-ranges of this path (the "merged-corridor bridge" refactor — plan doc 11).
    /// Empty for a non-structure path. Seeded once at original path creation (for a structure feature)
    /// and merged/reversed through path connection via <see cref="StructureSegmentOps"/>, exactly like
    /// <see cref="LaneSegments"/>. Lets a merged corridor remember which arc-range is a bridge.
    /// </summary>
    public List<StructureSegment> StructureSegments { get; set; } = [];

    /// <summary>
    /// True when the road piece at the given endpoint is a oneway, resolved from the lane
    /// segment covering that endpoint. Merged paths keep only the merge BASE path's Tags, so the
    /// whole-path oneway tag is unreliable after merges — a chain seeded from a bidirectional way
    /// hides the oneway carriageways merged into it (the B416 dual-carriageway ring bug).
    /// LaneSegments survive merges per-piece, so the covering segment tells the truth.
    /// Falls back to the path-level oneway tag when no lane segments exist (hand-built paths).
    /// </summary>
    /// <param name="atEnd">True to inspect the last point's piece, false for the first point's.</param>
    public bool IsOnewayAtEndpoint(bool atEnd)
    {
        var index = atEnd ? Points.Count - 1 : 0;
        LaneSegment? covering = null;
        foreach (var segment in LaneSegments)
        {
            if (segment.StartPointIndex <= index &&
                (covering == null || segment.StartPointIndex > covering.StartPointIndex))
                covering = segment;
        }

        // A sentinel segment (TotalLanes=0, no lane/oneway tags on the source way) correctly
        // reports IsOneWay=false — do NOT fall back to the base tags in that case.
        if (covering != null)
            return covering.LaneInfo.IsOneWay;

        return HasOnewayTag(Tags);
    }

    /// <summary>
    /// True when the tag dictionary marks a oneway road (yes/true/1/-1).
    /// </summary>
    public static bool HasOnewayTag(Dictionary<string, string> tags)
    {
        return tags.TryGetValue("oneway", out var value) &&
               (value == "yes" || value == "true" || value == "1" || value == "-1");
    }

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
