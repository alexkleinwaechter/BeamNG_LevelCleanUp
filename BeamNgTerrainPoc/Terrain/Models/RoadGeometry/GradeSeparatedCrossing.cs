using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// A point where two road splines cross in plan view but are vertically separated — one rides above the
/// other (a bridge over a road, or two roads on different OSM <c>layer</c>s). This is NOT an at-grade
/// intersection, so <see cref="Algorithms.NetworkJunctionDetector"/> records it here instead of emitting a
/// (false) <see cref="JunctionType.MidSplineCrossing"/> junction — fixing the long-standing latent bug
/// where grade-separated crossings were harmonized as if the roads met at grade.
///
/// Feature E-A (see <c>ai_docs/2026-06-03_bridge_generation/07-bridge-height-spec.md</c> §6). The crossing
/// is classified at detection time from <c>Layer</c> / <c>IsBridge</c>; the solved elevations and the
/// under/over resolution are filled in later by <c>GradeSeparationResolver</c> after the bridge profile
/// solver has run (the upper deck's final Z is only known then).
/// </summary>
public sealed class GradeSeparatedCrossing
{
    /// <summary>Spline id of the road/bridge that rides ABOVE at this crossing.</summary>
    public required int UpperSplineId { get; init; }

    /// <summary>
    /// Spline id of the road that passes BELOW at this crossing (the dip candidate, D-3a).
    /// <b>−1 for a synthetic rail/water crossing</b> (V2 A1): rail lines and waterways are not road
    /// splines — they exist only as OSM features (<c>BridgeObstacleSet</c>), so the planner emits a
    /// crossing with no lower spline. Such a crossing can never be dipped.
    /// </summary>
    public required int LowerSplineId { get; init; }

    /// <summary>True when the lower member is a real road spline (false for synthetic rail/water).</summary>
    public bool HasLowerSpline => LowerSplineId >= 0;

    /// <summary>
    /// Self-crossing (<c>EnableHiddenCrossingDetection</c>): the upper spline's own ground leg passes under
    /// this span (a switchback/hairpin), and this is that leg's along-spline station on the UPPER spline.
    /// <see cref="LowerSplineId"/> stays −1 — the lower member can never be resolved by XY because the
    /// deck sits at the same plan-view point; consumers that need the leg's solved Z resolve it by this
    /// station instead (<c>GradeSeparationResolver.ResolveSelfLowerZ</c>). NaN for every other crossing.
    /// </summary>
    public float SelfLowerStationMeters { get; init; } = float.NaN;

    /// <summary>True when this is a self-crossing (see <see cref="SelfLowerStationMeters"/>).</summary>
    public bool HasSelfLowerStation => !float.IsNaN(SelfLowerStationMeters);

    /// <summary>Plan-view crossing point (midpoint of the two closest cross-sections).</summary>
    public required Vector2 CrossingXY { get; init; }

    public required int UpperLayer { get; init; }
    public required int LowerLayer { get; init; }

    /// <summary>Priority cascade value of the upper member (motorway 100 … path 25). Tiebreaker + veto.</summary>
    public required int UpperPriority { get; init; }
    public required int LowerPriority { get; init; }

    public required bool UpperIsBridge { get; init; }
    public required bool LowerIsBridge { get; init; }

    // ── V2 A1 obstacle typing (plan doc 01, spec §2/§3.1) ──

    /// <summary>
    /// What the lower member IS (V2 A1 obstacle typing). <see cref="BridgeObstacleKind.Road"/> for every
    /// detector-built road-vs-road crossing; Rail/Water for the planner's synthetic feature crossings.
    /// Drives the per-kind clearance envelope (A2) and the §3.5 distribution (rail/water are never lowered).
    /// </summary>
    public BridgeObstacleKind LowerKind { get; init; } = BridgeObstacleKind.Road;

    /// <summary>Rail only: catenary clearance applies unless OSM says `electrified=no` (spec §3.1).</summary>
    public bool LowerElectrified { get; init; }

    /// <summary>Water only: `boat=yes` / `CEMT=*` / canal / width ≥ threshold (spec §3.1).</summary>
    public bool LowerNavigable { get; init; }

    /// <summary>
    /// OSM highway class of the upper member (e.g. "motorway", "primary_link"), for the §3.5 Δp class-step
    /// quantization (review P0-4: Δp must come from <c>BridgeRuleSystemOptions.ClassStepFor</c>, never from
    /// the composite stored priorities). Null when the spline is not OSM-sourced.
    /// </summary>
    public string? UpperOsmClass { get; init; }

    /// <summary>OSM highway class of the lower member (see <see cref="UpperOsmClass"/>). Null for rail/water.</summary>
    public string? LowerOsmClass { get; init; }

    /// <summary>
    /// V2 plan 0.5 placeholder — bridge over bridge (spec R6 multi-level, DEFERRED). Detection only: the
    /// rule engine currently treats the lower deck like any other obstacle (it is never dipped because rail
    /// of the §3.5 table — a bridge cannot be lowered); pairwise layer-ordered resolution (bottom-up per
    /// OSM <c>layer</c>) is a later planning pass. Logged as <c>[BRIDGE-BRIDGE]</c> when
    /// <c>BridgeRuleSystemOptions.EnableBridgeBridge</c> is on.
    /// </summary>
    public bool IsBridgeOverBridge => UpperIsBridge && LowerIsBridge;

    // ── Filled by GradeSeparationResolver (post-solve diagnostics / outcome) ──

    /// <summary>Solved upper centerline Z at the crossing (m). NaN until resolved. D-1: road-vs-road.</summary>
    public float UpperZ { get; set; } = float.NaN;

    /// <summary>Solved lower centerline Z at the crossing (m). NaN until resolved.</summary>
    public float LowerZ { get; set; } = float.NaN;

    /// <summary>Road-vs-road vertical clearance at the crossing (m). NaN until resolved.</summary>
    public float ClearanceMeters => UpperZ - LowerZ;

    /// <summary>How the crossing was resolved. <see cref="GradeSeparationAction.Pending"/> until processed.</summary>
    public GradeSeparationAction Action { get; set; } = GradeSeparationAction.Pending;

    /// <summary>Depth (m) the lower road was dipped, when <see cref="Action"/> is DippedLowerRoad.</summary>
    public float AppliedDipMeters { get; set; }
}

/// <summary>How a <see cref="GradeSeparatedCrossing"/> was resolved (D-3a).</summary>
public enum GradeSeparationAction
{
    /// <summary>Not yet processed.</summary>
    Pending,

    /// <summary>Road-vs-road clearance already met the minimum — nothing changed.</summary>
    AlreadyClears,

    /// <summary>The lower road was dipped locally (eased) to clear the upper member.</summary>
    DippedLowerRoad,

    /// <summary>Priority veto: the lower road outranks the bridge, so the bridge was raised instead.</summary>
    RaisedBridge,

    /// <summary>Veto but the upper member is not a generated bridge deck — left untouched (out of E-A scope).</summary>
    NoOpNoBridge
}
