using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
///     Fixed constraint at a junction endpoint for a specific road.
///     Represents the exact (elevation, slope, bankAngle) that a road must
///     achieve at a junction point. Computed once per junction+road pair,
///     then used by UnifiedJunctionProfileBlender to create smooth profiles.
/// </summary>
public record JunctionEndpointConstraint
{
    /// <summary>
    ///     Centerline elevation the road must hit at the junction (meters).
    ///     For T-junctions: primary road surface elevation at connection point.
    ///     For Y/X/Complex: negotiated average elevation.
    ///     For Endpoints: terrain elevation.
    /// </summary>
    public required float Elevation { get; init; }

    /// <summary>
    ///     Longitudinal slope (rise/run) the road must have at the junction.
    ///     For T-junctions: primary road's local slope at connection point.
    ///     For Y/X/Complex: 0 (let natural slope take over).
    ///     For Endpoints: 0 (terrain slope).
    /// </summary>
    public required float Slope { get; init; }

    /// <summary>
    ///     Bank angle (radians) the road must have at the junction.
    ///     For T-junctions: arcsin((rightEdge - leftEdge) / roadWidth) from primary surface.
    ///     For Y/X/Complex: 0 (flatten at equal-priority junctions).
    ///     For Endpoints: 0 (flatten at dead ends).
    /// </summary>
    public required float BankAngleRadians { get; init; }

    /// <summary>
    ///     Whether this constraint is at the start of the spline (true) or the end (false).
    /// </summary>
    public required bool IsSplineStart { get; init; }

    /// <summary>
    ///     The junction that generated this constraint (for diagnostics).
    /// </summary>
    public required NetworkJunction Junction { get; init; }

    /// <summary>
    ///     Distance (meters) from the junction endpoint where the Hermite transition begins.
    ///     Within this zone, the constraint applies at full strength (weight=1.0).
    ///     For T-junctions: primary road half-width (the overlap zone where the terminating
    ///     road sits on the primary road).
    ///     For other junction types: 0 (transition starts immediately).
    ///     This ensures the road matches the primary surface all the way to the road edge,
    ///     not just at the center.
    /// </summary>
    public float FlatZoneDistance { get; init; }

    /// <summary>
    ///     Tangent direction (unit vector) of the primary road at this T-junction.
    ///     Used by BlendSplineProfile to compute spatially-varying elevation deltas
    ///     that track the primary road's slope within the flat zone.
    ///     Only set for T-junction constraints; null for other junction types.
    /// </summary>
    public Vector2? PrimaryTangentDirection { get; init; }

    /// <summary>
    ///     Bank angle (radians) of the PRIMARY road at this T-junction.
    ///     Used by BlendSplineProfile to account for the primary road's lateral tilt
    ///     when computing per-CS elevation deltas within the flat zone.
    ///     Different from BankAngleRadians which is the target bank for the terminating road.
    /// </summary>
    public float PrimaryBankAngleRadians { get; init; }

    /// <summary>
    ///     Distance (meters) over which the Hermite correction decays from full strength to zero,
    ///     measured from the edge of the flat zone (not from the junction center).
    ///     The Hermite h00 basis function transitions from 1→0 within this distance, with
    ///     zero slope at both ends (C1 continuous). Beyond this distance, the road follows
    ///     the terrain-following profile from Phase 2 with no junction correction.
    ///     Adaptive: may be extended beyond the configured minimum when the elevation gap
    ///     between junction and terrain-following profile requires a gentler ramp.
    /// </summary>
    public float BlendDistanceMeters { get; init; } = 30f;

    /// <summary>
    ///     Whether this constraint was propagated from a neighboring short spline
    ///     rather than computed directly at this spline's own junction.
    ///     Propagated constraints have lower priority than direct constraints.
    /// </summary>
    public bool IsPropagated { get; init; }

    /// <summary>
    ///     SplineId of the short segment this constraint was propagated through.
    ///     Only set when IsPropagated is true. Used for diagnostics/logging.
    /// </summary>
    public int? PropagatedThroughSplineId { get; init; }
}
