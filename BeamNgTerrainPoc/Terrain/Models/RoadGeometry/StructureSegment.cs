using System.Numerics;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// A contiguous bridge/tunnel sub-range of a (possibly merged) road path.
///
/// This is the structural analogue of <see cref="Models.DecalRoad.LaneSegment"/>: it rides along on a
/// <c>PathWithMetadata</c>/<see cref="RoadSpline"/> through the merge pipeline so that, after a bridge way
/// is merged INTO its through-road corridor, downstream code can still recover exactly which arc-length
/// stretch of the merged spline is the bridge (the "merged-corridor bridge" refactor, see
/// <c>ai_docs/2026-06-03_bridge_generation/11-merged-corridor-bridge-continuity-plan.md</c>).
///
/// Unlike <see cref="Models.DecalRoad.LaneSegment"/> — whose segments tile the whole path and run "until the
/// next segment" — a structure segment is an EXPLICIT [start, end] range, because a merged spline is usually
/// <c>road → bridge → road</c> (the structure does not cover the whole path).
///
/// During merging the range is tracked by point index (<see cref="StartPointIndex"/>/<see cref="EndPointIndex"/>),
/// shifted/reversed by <see cref="StructureSegmentOps"/> exactly like lane segments. At spline-creation time the
/// range is resolved to a stable arc-length anchor (<see cref="StartDistance"/>/<see cref="EndDistance"/>) —
/// arc-length survives Chaikin densification, point indices do not (mirrors
/// <c>OsmGeometryProcessor.PropagatePathLaneSegmentsToSpline</c>).
/// </summary>
public sealed class StructureSegment
{
    /// <summary>Inclusive start point index on the owning path (merge-time bookkeeping).</summary>
    public int StartPointIndex { get; set; }

    /// <summary>Inclusive end point index on the owning path (merge-time bookkeeping).</summary>
    public int EndPointIndex { get; set; }

    /// <summary>Arc-length (m) of the span start along the final spline. The stable downstream anchor.</summary>
    public float StartDistance { get; set; }

    /// <summary>Arc-length (m) of the span end along the final spline. The stable downstream anchor.</summary>
    public float EndDistance { get; set; }

    /// <summary>Bridge / Tunnel / etc.</summary>
    public StructureType Type { get; set; } = StructureType.None;

    /// <summary>Vertical layer (0 = ground, positive = elevated, negative = underground).</summary>
    public int Layer { get; set; }

    /// <summary>Bridge structure type (beam, arch, suspension, …) for deck-mesh styling. Null if unknown.</summary>
    public string? BridgeStructureType { get; set; }

    /// <summary>Full OSM tag dictionary of the source structure way(s) (E-C bag, now per-span). Null if none.</summary>
    public IReadOnlyDictionary<string, string>? OsmTags { get; set; }

    /// <summary>All OSM way IDs that contributed to this span (stable key for per-span deck identity, plan §10.2).</summary>
    public HashSet<long> OsmWayIds { get; set; } = [];

    /// <summary>
    /// Terrain-local XY of the ORIGINAL structure way's first node, captured at path seeding (before any
    /// merge/Chaikin). Coordinates are invariant under merging (only indices shift), so after the corridor is
    /// built these can be re-projected onto the FINAL spline to fix the pre-Chaikin arc-length station drift
    /// (V2 plan 0.3a, docs 11/13). Always corresponds to the <see cref="StartPointIndex"/> side — swapped by
    /// <see cref="StructureSegmentOps.ReverseSegments"/>. Null when not seeded (legacy / direct construction).
    /// </summary>
    public Vector2? OriginalStartPoint { get; set; }

    /// <summary>Terrain-local XY of the ORIGINAL structure way's last node (see <see cref="OriginalStartPoint"/>).</summary>
    public Vector2? OriginalEndPoint { get; set; }

    /// <summary>
    /// Per-station layer sub-ranges of a CONSOLIDATED span (doc 10). OSM's <c>layer</c> tag encodes only the
    /// LOCAL crossing order, so one physical deck is mapped as many ways with differing layers (Brooklyn
    /// Bridge: 3/0/3/…/1). When <see cref="StructureSegmentOps.ConsolidateByStation"/> joins such ways into
    /// one span, each contributor's [StartDistance, EndDistance, Layer] is recorded here so grade-separation
    /// classification (<c>NetworkJunctionDetector.EffectiveStructureAt</c>, <c>BridgeSpanFootprint</c>) still
    /// sees the RIGHT relative layer at every crossing station. Null on unconsolidated spans —
    /// <see cref="LayerAt"/> then falls back to <see cref="Layer"/>.
    /// </summary>
    public List<StructureLayerRange>? LayerRanges { get; set; }

    /// <summary>
    /// The span's vertical layer at an arc-length station: the containing <see cref="LayerRanges"/> entry's
    /// layer, or <see cref="Layer"/> when no ranges exist / the station lies outside all of them.
    /// </summary>
    public int LayerAt(float station)
    {
        if (LayerRanges is { Count: > 0 })
        {
            foreach (var range in LayerRanges)
            {
                if (station >= range.StartDistance && station <= range.EndDistance)
                    return range.Layer;
            }
        }

        return Layer;
    }

    /// <summary>
    ///     Doc 13: this span end is a bridge-to-bridge continuation — the road continues onto another
    ///     deck (a foreign spline's span footprint, or a same-spline neighbour segment) instead of
    ///     meeting the ground. Abutment terraforming (exclusion-shrink overlap zone, overlap tongue,
    ///     excavator tongue ceiling) is suppressed at this end. Set by
    ///     <c>BridgeToBridgeContinuity.MarkContinuationEnds</c> only when
    ///     <c>BridgeRuleSystemOptions.EnableBridgeToBridgeAbutmentSuppression</c> is on.
    /// </summary>
    public bool StartContinuesOntoDeck { get; set; }

    /// <summary>See <see cref="StartContinuesOntoDeck"/> — the <see cref="EndDistance"/> side.</summary>
    public bool EndContinuesOntoDeck { get; set; }

    /// <summary>
    ///     Doc 14: WHERE the <see cref="StartDistance"/> side lands when it continues onto a FOREIGN
    ///     deck — the landed-on spline, the station along that spline nearest the landing point, and
    ///     the junction id when the landing was junction-driven. Recorded by
    ///     <c>BridgeToBridgeContinuity.MarkContinuationEnds</c> so the profile solver / diagnostics can
    ///     anchor to the trunk deck surface without re-searching. Null for ground abutments and
    ///     same-spline neighbour joints (those are chain-continuous already).
    /// </summary>
    public DeckLandingRecord? StartDeckLanding { get; set; }

    /// <summary>See <see cref="StartDeckLanding"/> — the <see cref="EndDistance"/> side.</summary>
    public DeckLandingRecord? EndDeckLanding { get; set; }

    /// <summary>
    ///     Doc 15: whether the <see cref="StartDeckLanding"/> anchor was actually APPLIED by
    ///     <c>BridgeProfileSolver.ApplyToSpan</c> (a genuine merge) — false while unsolved, and false
    ///     when the record exists but the <c>MaxLandingAnchorZGapMeters</c> plausibility cap classified
    ///     it as a plan-view crossing. Diagnostic marker of the solver's merge-vs-crossing decision
    ///     (the mesh-layer parapet openings are keyed on geometric coplanarity instead — landing-pair
    ///     gating both over- and under-opened, Manhattan render 2026-07-07).
    /// </summary>
    public bool StartDeckLandingApplied { get; set; }

    /// <summary>See <see cref="StartDeckLandingApplied"/> — the <see cref="EndDistance"/> side.</summary>
    public bool EndDeckLandingApplied { get; set; }

    public bool IsBridge => Type == StructureType.Bridge;
    public bool IsTunnel => Type == StructureType.Tunnel;

    /// <summary>
    /// Stable, reproducible id for this span derived from its sorted OSM way-id set (plan §10.2). Used to tag
    /// span cross-sections (<c>UnifiedCrossSection.StructureSpanId</c>) and to group sections into one deck per
    /// span downstream (Phases 3–5). Non-negative so it never collides with the −1 "no span" sentinel; the same
    /// way-id set always yields the same id across runs (<c>long.GetHashCode</c> is deterministic).
    /// </summary>
    public int SpanId
    {
        get
        {
            unchecked
            {
                var hash = 17;
                foreach (var wayId in OsmWayIds.OrderBy(x => x))
                    hash = hash * 31 + wayId.GetHashCode();
                return hash & 0x7FFFFFFF;
            }
        }
    }

    public StructureSegment Clone() => new()
    {
        StartPointIndex = StartPointIndex,
        EndPointIndex = EndPointIndex,
        StartDistance = StartDistance,
        EndDistance = EndDistance,
        Type = Type,
        Layer = Layer,
        BridgeStructureType = BridgeStructureType,
        OsmTags = OsmTags,
        OsmWayIds = new HashSet<long>(OsmWayIds),
        OriginalStartPoint = OriginalStartPoint,
        OriginalEndPoint = OriginalEndPoint,
        LayerRanges = LayerRanges is null ? null : [.. LayerRanges],
        StartContinuesOntoDeck = StartContinuesOntoDeck,
        EndContinuesOntoDeck = EndContinuesOntoDeck,
        StartDeckLanding = StartDeckLanding,
        EndDeckLanding = EndDeckLanding,
        StartDeckLandingApplied = StartDeckLandingApplied,
        EndDeckLandingApplied = EndDeckLandingApplied,
    };
}

/// <summary>One layer sub-range of a consolidated span (see <see cref="StructureSegment.LayerRanges"/>).</summary>
public readonly record struct StructureLayerRange(float StartDistance, float EndDistance, int Layer);

/// <summary>
/// A resolved bridge-to-bridge landing (doc 14): the span end continues onto <paramref name="DeckSplineId"/>'s
/// deck at <paramref name="DeckStation"/> (arc-length along THAT spline). <paramref name="JunctionId"/> is set
/// when the landing was resolved from a junction's contributors rather than the doc-13 radius test.
/// </summary>
public sealed record DeckLandingRecord(int DeckSplineId, float DeckStation, int? JunctionId);
