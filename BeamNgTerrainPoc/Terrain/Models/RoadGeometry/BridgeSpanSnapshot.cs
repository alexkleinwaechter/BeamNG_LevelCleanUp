using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// One cross-section "station" of a captured bridge span (plan doc 11 §3). Holds the merged, smoothed
/// centerline pose plus the solved deck elevation, sampled from the SAME corridor cross-section the
/// approach road uses — so the deck's first rib and the approach's last rib are adjacent samples of one
/// centerline (plan-view + elevation continuity by construction).
/// </summary>
public sealed class BridgeStation
{
    /// <summary>Centerline position (plan-view, m).</summary>
    public Vector2 Center { get; init; }

    /// <summary>Lateral normal (unit, right-hand) of the corridor at this station.</summary>
    public Vector2 Normal { get; init; }

    /// <summary>Forward tangent (unit) of the corridor at this station.</summary>
    public Vector2 Tangent { get; init; }

    /// <summary>Effective road width at this station (m).</summary>
    public float Width { get; init; }

    /// <summary>Solved deck centerline elevation (m), after the structural-profile override.</summary>
    public float CenterZ { get; init; }

    /// <summary>Solved left (−normal) banked edge elevation (m).</summary>
    public float LeftEdgeZ { get; init; }

    /// <summary>Solved right (+normal) banked edge elevation (m).</summary>
    public float RightEdgeZ { get; init; }

    /// <summary>Arc-length of this station along the owning corridor spline (m).</summary>
    public float DistanceAlongSpline { get; init; }
}

/// <summary>
/// An immutable snapshot of a single bridge span, captured right after
/// <c>BridgeProfileSolver.RefineSpans</c> finalises the span elevation and BEFORE any heightmap
/// carve (plan doc 11 §3, option B — the captured-snapshot representation). The deck mesh, bridge DecalRoads,
/// and any future AI waypoint path are built from this — one deck per span, keyed by the OSM way-id set
/// (stable across runs, plan §10.2) — so all of them read one finalised geometry independent of pipeline
/// ordering.
/// </summary>
public sealed class BridgeSpanSnapshot
{
    /// <summary>Owning (merged corridor) spline id.</summary>
    public int SplineId { get; init; }

    /// <summary>Stable span id (<see cref="StructureSegment.SpanId" />) — matches the cross-sections' tag.</summary>
    public int SpanId { get; init; }

    /// <summary>All OSM way ids that make up this span — the stable per-span deck identity key.</summary>
    public HashSet<long> OsmWayIds { get; init; } = [];

    /// <summary>The span's OSM tag bag (E-C), for deck-mesh styling / rules. Null if none.</summary>
    public IReadOnlyDictionary<string, string>? OsmTags { get; init; }

    /// <summary>Ordered stations (one per span cross-section), from span start to span end.</summary>
    public List<BridgeStation> Stations { get; init; } = [];
}
