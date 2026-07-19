using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// The output of <see cref="BridgeElevationPlanner"/> (plan doc 14 §4): per merged-corridor bridge span, the
/// required deck elevation and its section pins; per grade-separated crossing, the resolved outcome (who moves).
/// Consumed by Phase C (deck pins → <c>UnifiedCrossSection.PinnedElevation</c>) and Phase D (lower-road dips).
/// </summary>
public sealed class BridgeElevationPlan
{
    /// <summary>One entry per bridge span the planner saw.</summary>
    public IReadOnlyList<SpanDeckPlan> Spans { get; init; } = [];

    /// <summary>One entry per grade-separated crossing under a span (the rule-engine outcome).</summary>
    public IReadOnlyList<CrossingPlan> Crossings { get; init; } = [];

    /// <summary>
    /// Doc 28: the dip-decided underpass clusters (Step B). Every member crossing was resolved as a
    /// coherent <see cref="BridgeElevationAction.DipLowerRoad"/> (raises suppressed); the dip appliers
    /// (pre-smooth pin emitter / post-solve resolver) merge each cluster's wells into ONE smooth envelope
    /// well (Step C). Empty when <c>EnablePriorityDistribution</c> is off or no cluster qualified.
    /// </summary>
    public IReadOnlyList<UnderpassClusterPlan> UnderpassClusters { get; init; } = [];

    /// <summary>True when there is nothing to do (no spans — flag off / no structure segments).</summary>
    public bool IsEmpty => Spans.Count == 0 && Crossings.Count == 0;
}

/// <summary>
/// One coherent underpass (doc 28): a cluster of grade-separated crossings on the same lower road (station
/// gap ≤ <c>UnderpassClusterGapMeters</c>) that the rule engine resolved as ONE bounded dip — the road
/// passes under ALL the cluster's bridges in a single smooth well; none of the bridges raises for it.
/// </summary>
public sealed record UnderpassClusterPlan
{
    /// <summary>The dipped lower road.</summary>
    public required int LowerSplineId { get; init; }

    /// <summary>The member crossings (each planned as a coherent DipLowerRoad), in station order.</summary>
    public required IReadOnlyList<GradeSeparatedCrossing> Crossings { get; init; }

    /// <summary>The bridges cleared by the underpass (distinct upper spline ids, for observability).</summary>
    public required IReadOnlyList<int> UpperSplineIds { get; init; }

    /// <summary>First member crossing's station along the lower road (m).</summary>
    public required float StartStation { get; init; }

    /// <summary>Last member crossing's station along the lower road (m).</summary>
    public required float EndStation { get; init; }

    /// <summary>Deepest planned dip across the cluster (m), after the cap.</summary>
    public required float MaxDipMeters { get; init; }

    /// <summary>Worst accepted under-clearance (m) where the cap bit (Step D); 0 when nothing was capped.</summary>
    public required float CappedResidualMeters { get; init; }
}

/// <summary>
/// The deck-elevation decision for one bridge span. When <see cref="IsRaised"/>, <see cref="Pins"/> carries one
/// <see cref="DeckPin"/> per span cross-section at <see cref="RequiredDeckZ"/> for Phase C to apply; an un-raised
/// span (pure lower-road dip / nothing to clear) has no pins and lets the smoother follow the approaches.
/// </summary>
public sealed class SpanDeckPlan
{
    public required int OwnerSplineId { get; init; }
    public required int SpanId { get; init; }
    public required float StartDistance { get; init; }
    public required float EndDistance { get; init; }

    /// <summary>The span's effective vertical layer (the deck's layer, typically ≥ 1).</summary>
    public required int Layer { get; init; }

    /// <summary>The flat deck elevation the span is pinned to (when raised), else the approach level.</summary>
    public required float RequiredDeckZ { get; init; }

    /// <summary>True ⇒ the deck is lifted above the approaches and must be pinned (Rule 1 / veto / split / terrain).</summary>
    public required bool IsRaised { get; init; }

    public required float ApproachZLeft { get; init; }
    public required float ApproachZRight { get; init; }

    /// <summary>
    /// The generic ramp-test threshold C (= the typed <c>RoadClearanceMeters</c>, doc 17 §4a). The
    /// authoritative per-crossing budget is <see cref="CrossingPlan.RequiredSeparationMeters"/>
    /// (kind clearance + structural depth).
    /// </summary>
    public required float ClearanceUsed { get; init; }

    /// <summary>§3.2 structural deck depth used in the typed separation budget.</summary>
    public float StructuralDepthMeters { get; init; }

    /// <summary>Per-section deck pins (empty when not raised). Phase C sets <c>PinnedElevation</c> from these.</summary>
    public IReadOnlyList<DeckPin> Pins { get; init; } = [];
}

/// <summary>
/// A single span cross-section pinned to a deck elevation (Phase C consumes the <see cref="Section"/>).
/// <paramref name="SoftRiseMeters"/> (Amendment 03 v3, sparse mode only): the clearance rise above the
/// deck chord at this station — transported RELATIVE so the smoother can re-anchor the chord on the real
/// approaches (estimate offsets cancel; a hump that reaches the span end keeps its full rise). 0 for the
/// hard-pin builders.
/// </summary>
public readonly record struct DeckPin(
    UnifiedCrossSection Section, int SectionIndex, float DistanceAlongSpline, float DeckZ,
    float SoftRiseMeters = 0f);

/// <summary>The rule-engine outcome for one grade-separated crossing under a span.</summary>
public sealed record CrossingPlan
{
    public required GradeSeparatedCrossing Crossing { get; init; }
    public required BridgeElevationAction Action { get; init; }

    /// <summary>Target deck-top Z at the crossing (Raise / RaiseBridgeVeto / Split). NaN otherwise.</summary>
    public float DeckTargetZ { get; init; } = float.NaN;

    /// <summary>Target lower-road Z at the crossing (DipLowerRoad / Split). NaN otherwise.</summary>
    public float LowerRoadTargetZ { get; init; } = float.NaN;

    /// <summary>How far the lower road is dipped (DipLowerRoad / Split); 0 otherwise.</summary>
    public float DipDepthMeters { get; init; }

    /// <summary>
    /// The vertical separation budget S this crossing was resolved against (A2): with obstacle typing on,
    /// <c>ClearanceFor(kind) + StructuralDepthMeters(span)</c>; legacy = the base clearance C. The A7
    /// post-smooth verify re-checks the final surfaces against this value. A4's R4-step-7 escalation may
    /// REDUCE it (reduced road clearance) — see <see cref="Warning"/>.
    /// </summary>
    public float RequiredSeparationMeters { get; init; }

    /// <summary>
    /// Non-null when the A4 feasibility pass had to escalate (absolute slopes / hard cut / reduced
    /// clearance / over-steep last resort) or a Rule-1 raise exceeds the absolute ramp slope. Logged by
    /// <c>ApplyBridgeDeckPins</c> as <c>[BRIDGE-PLAN] WARN</c>.
    /// </summary>
    public string? Warning { get; init; }

    /// <summary>
    /// Doc 28 Step D: under-clearance (m) ACCEPTED where the coherent-underpass dip cap bit — the
    /// separation budget was reduced by this amount instead of raising the bridges. 0 otherwise.
    /// </summary>
    public float AcceptedResidualMeters { get; init; }

    /// <summary>
    /// The obstacle Z the planner decided against (A0 estimate / stale TargetElevation / DEM). A7 logs
    /// estimate-vs-final per crossing so the estimator's accuracy is measurable on a render. NaN when
    /// unknown.
    /// </summary>
    public float ObstacleZEstimate { get; init; } = float.NaN;
}

/// <summary>Who moves at a grade-separated crossing (plan doc 14 §4.2).</summary>
public enum BridgeElevationAction
{
    /// <summary>Rule 1 — the deck is raised to clear; the lower road is left alone.</summary>
    RaiseBridge,

    /// <summary>Rule 2 — the lower-priority road is dipped under the (un-raised) deck.</summary>
    DipLowerRoad,

    /// <summary>Rule 3 — equal priority: the deck is raised and the road dipped, sharing the deficit.</summary>
    Split,

    /// <summary>Rule 2 veto — the lower road outranks the bridge, so the deck is raised over it instead.</summary>
    RaiseBridgeVeto,

    /// <summary>The deck (raised, or at approach level) already clears this obstacle — no move.</summary>
    AlreadyClears,
}

/// <summary>Tunable inputs for <see cref="BridgeElevationPlanner"/> (plan doc 14 §10).</summary>
public sealed record BridgeElevationPlannerOptions
{
    /// <summary>Rule-3 raise/dip split: fraction of the deficit taken by raising the deck (0..1). §10.</summary>
    public float GradeSepSplitRatio { get; init; } = 0.5f;

    /// <summary>
    /// A0 (V2 review P0-2): early road-elevation estimate per cross-section, consulted when
    /// <c>TargetElevation</c> is not yet solved (Phase 1.85 runs pre-smoothing) BEFORE falling back to a raw
    /// DEM sample. Built by <see cref="EarlyRoadElevationEstimator"/> (smoothed centerline DEM — raw pre-smooth
    /// DEM ≈ embankment banks, the parked §5a misfire). Return NaN for "no estimate". Null ⇒ raw-DEM fallback.
    /// </summary>
    public Func<UnifiedCrossSection, float>? EarlyElevation { get; init; }
}
