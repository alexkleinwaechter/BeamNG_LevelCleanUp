using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
///     Defines how banking behaves at/near a junction for a cross-section.
/// </summary>
public enum JunctionBankingBehavior
{
    /// <summary>
    ///     Normal banking based on curvature (no junction nearby).
    /// </summary>
    Normal,

    /// <summary>
    ///     This road has highest priority at the junction - maintain full banking.
    ///     Lower-priority roads will adapt to us.
    /// </summary>
    MaintainBanking,

    /// <summary>
    ///     This road has lower priority - adapt edge elevations to match
    ///     the higher-priority road's banked surface.
    /// </summary>
    AdaptToHigherPriority,

    /// <summary>
    ///     Equal priority roads meeting - both reduce banking to flat.
    ///     Also used for endpoints (dead ends).
    /// </summary>
    SuppressBanking
}

/// <summary>
///     Cross-section with reference to its source spline and per-spline parameters.
///     Extends the base CrossSection concept with owner tracking and effective parameter values.
/// </summary>
public class UnifiedCrossSection
{
    /// <summary>
    ///     World coordinates of the cross-section center point (on the road centerline).
    /// </summary>
    public Vector2 CenterPoint { get; set; }

    /// <summary>
    ///     Unit vector perpendicular to the road direction (points to the right).
    /// </summary>
    public Vector2 NormalDirection { get; set; }

    /// <summary>
    ///     Unit vector along the road direction (tangent).
    /// </summary>
    public Vector2 TangentDirection { get; set; }

    /// <summary>
    ///     Calculated target elevation for this cross-section in world units.
    ///     Initialized to NaN to distinguish "not set" from valid zero elevation.
    /// </summary>
    public float TargetElevation { get; set; } = float.NaN;

    /// <summary>
    ///     Whether this cross-section is in an exclusion zone (e.g., over water).
    ///     If true, no smoothing is applied at this location.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    ///     Global index of this cross-section across all splines in the network.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    ///     Reference to the owning parameterized spline.
    ///     Links back to the ParameterizedRoadSpline that generated this cross-section.
    /// </summary>
    public int OwnerSplineId { get; set; }

    /// <summary>
    ///     Index within the owning spline (monotonic along the spline path).
    /// </summary>
    public int LocalIndex { get; set; }

    /// <summary>
    ///     Distance along the spline from its start point in meters.
    /// </summary>
    public float DistanceAlongSpline { get; set; }

    /// <summary>
    ///     Effective road width for THIS cross-section in meters (from spline's params).
    ///     Cached from the owning spline's RoadWidthMeters for fast access during blending.
    /// </summary>
    public float EffectiveRoadWidth { get; set; }

    /// <summary>
    ///     Effective blend range for THIS cross-section in meters (from spline's params).
    ///     Cached from the owning spline's TerrainAffectedRangeMeters for fast access during blending.
    /// </summary>
    public float EffectiveBlendRange { get; set; }

    /// <summary>
    ///     Priority inherited from the owning spline.
    ///     Used for resolving conflicts in overlapping blend zones.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    ///     Whether this cross-section comes from an OSM source (true) or PNG layer map (false).
    ///     Used to select the appropriate elevation interpolation strategy:
    ///     - OSM roads use inverse-distance weighted interpolation (smooth for vector data)
    ///     - PNG roads use single nearest-neighbor (more robust for skeleton-extracted paths)
    /// </summary>
    public bool IsFromOsmSource { get; set; }

    /// <summary>
    ///     Whether this is the first cross-section of its spline (start endpoint).
    /// </summary>
    public bool IsSplineStart { get; set; }

    /// <summary>
    ///     Whether this is the last cross-section of its spline (end endpoint).
    /// </summary>
    public bool IsSplineEnd { get; set; }

    /// <summary>
    ///     Original elevation sampled from terrain before any modifications.
    ///     Used for endpoint tapering and blend calculations.
    /// </summary>
    public float OriginalTerrainElevation { get; set; } = float.NaN;

    /// <summary>
    ///     Whether this cross-section was blended by the RoundaboutElevationHarmonizer.
    ///     When true, Phase 3 endpoint tapering should NOT overwrite the elevation,
    ///     because the roundabout harmonizer has already set a smooth transition.
    /// </summary>
    public bool IsRoundaboutBlended { get; set; }

    // ========================================
    // BANKING (SUPERELEVATION) PROPERTIES
    // ========================================

    /// <summary>
    ///     Curvature at this cross-section (1/radius in 1/meters).
    ///     Positive = curving left, Negative = curving right.
    ///     Calculated from adjacent cross-sections.
    /// </summary>
    public float Curvature { get; set; }

    /// <summary>
    ///     Bank angle at this cross-section in radians.
    ///     Positive = right side higher than left (outer edge higher for left curve).
    /// </summary>
    public float BankAngleRadians { get; set; }

    /// <summary>
    ///     3D normal vector after banking applied.
    ///     Z component indicates tilt from horizontal.
    ///     For flat road: (0, 0, 1)
    ///     For banked road: rotated around tangent axis by BankAngleRadians
    /// </summary>
    public Vector3 BankedNormal3D { get; set; } = new(0, 0, 1);

    /// <summary>
    ///     Elevation at the left road edge (meters).
    ///     LeftEdgeElevation = TargetElevation - (RoadWidth/2 * sin(BankAngle))
    /// </summary>
    public float LeftEdgeElevation { get; set; } = float.NaN;

    /// <summary>
    ///     Elevation at the right road edge (meters).
    ///     RightEdgeElevation = TargetElevation + (RoadWidth/2 * sin(BankAngle))
    /// </summary>
    public float RightEdgeElevation { get; set; } = float.NaN;

    // ========================================
    // JUNCTION SURFACE CONSTRAINTS
    // ========================================
    // These fields allow junction harmonization to directly specify where a
    // terminating road's edges should connect to a primary road's surface.
    // When set, they OVERRIDE the calculated edge elevations from banking.

    /// <summary>
    ///     When set, this is an explicit constraint for the left edge elevation at a junction.
    ///     This overrides any calculation from TargetElevation + banking.
    ///     Set during junction harmonization when this cross-section terminates at a higher-priority road.
    /// </summary>
    public float? ConstrainedLeftEdgeElevation { get; set; }

    /// <summary>
    ///     When set, this is an explicit constraint for the right edge elevation at a junction.
    ///     This overrides any calculation from TargetElevation + banking.
    ///     Set during junction harmonization when this cross-section terminates at a higher-priority road.
    /// </summary>
    public float? ConstrainedRightEdgeElevation { get; set; }

    /// <summary>
    ///     When true, this cross-section is at or near a junction and should use constrained edge elevations.
    /// </summary>
    public bool HasJunctionConstraint => ConstrainedLeftEdgeElevation.HasValue || ConstrainedRightEdgeElevation.HasValue;

    // ========================================
    // JUNCTION BANKING CONTEXT
    // ========================================

    /// <summary>
    ///     How banking behaves at this cross-section due to nearby junctions.
    /// </summary>
    public JunctionBankingBehavior JunctionBankingBehavior { get; set; } = JunctionBankingBehavior.Normal;

    /// <summary>
    ///     Banking blending factor for junction transitions (0-1).
    ///     0 = at junction center (apply junction behavior fully)
    ///     1 = far from junction (normal banking)
    /// </summary>
    public float JunctionBankingFactor { get; set; } = 1.0f;

    /// <summary>
    ///     Distance to the nearest junction affecting this cross-section (meters).
    ///     Used for smooth transition calculations.
    /// </summary>
    public float DistanceToNearestJunction { get; set; } = float.MaxValue;

    /// <summary>
    ///     Spline ID of the higher-priority road at the nearest junction.
    ///     Only set when JunctionBankingBehavior == AdaptToHigherPriority.
    /// </summary>
    public int? HigherPrioritySplineId { get; set; }

    /// <summary>
    ///     Creates a UnifiedCrossSection from a SplineSample.
    /// </summary>
    /// <param name="sample">The spline sample to convert</param>
    /// <param name="ownerSpline">The spline that owns this cross-section</param>
    /// <param name="globalIndex">Global index in the network</param>
    /// <param name="localIndex">Local index within the spline</param>
    /// <returns>A new UnifiedCrossSection</returns>
    public static UnifiedCrossSection FromSplineSample(
        SplineSample sample,
        ParameterizedRoadSpline ownerSpline,
        int globalIndex,
        int localIndex)
    {
        return new UnifiedCrossSection
        {
            CenterPoint = sample.Position,
            NormalDirection = sample.Normal,
            TangentDirection = sample.Tangent,
            DistanceAlongSpline = sample.Distance,
            OwnerSplineId = ownerSpline.SplineId,
            Index = globalIndex,
            LocalIndex = localIndex,
            EffectiveRoadWidth = ownerSpline.Parameters.RoadWidthMeters,
            EffectiveBlendRange = ownerSpline.Parameters.TerrainAffectedRangeMeters,
            Priority = ownerSpline.Priority,
            IsExcluded = false,
            // OSM roads have OsmRoadType set, PNG roads have it null
            IsFromOsmSource = !string.IsNullOrEmpty(ownerSpline.OsmRoadType)
        };
    }

    /// <summary>
    ///     Converts this UnifiedCrossSection to a standard CrossSection for compatibility.
    /// </summary>
    /// <returns>A CrossSection with equivalent data</returns>
    public CrossSection ToCrossSection()
    {
        return new CrossSection
        {
            CenterPoint = CenterPoint,
            NormalDirection = NormalDirection,
            TangentDirection = TangentDirection,
            TargetElevation = TargetElevation,
            WidthMeters = EffectiveRoadWidth,
            IsExcluded = IsExcluded,
            Index = Index,
            PathId = OwnerSplineId, // Use spline ID as path ID
            LocalIndex = LocalIndex
        };
    }

    /// <summary>
    ///     Creates a UnifiedCrossSection from a standard CrossSection.
    ///     Note: Some unified-specific fields will need to be set separately.
    /// </summary>
    /// <param name="cs">The CrossSection to convert</param>
    /// <param name="ownerSplineId">The owning spline ID</param>
    /// <param name="blendRange">The effective blend range</param>
    /// <param name="priority">The priority value</param>
    /// <returns>A new UnifiedCrossSection</returns>
    public static UnifiedCrossSection FromCrossSection(
        CrossSection cs,
        int ownerSplineId,
        float blendRange,
        int priority)
    {
        return new UnifiedCrossSection
        {
            CenterPoint = cs.CenterPoint,
            NormalDirection = cs.NormalDirection,
            TangentDirection = cs.TangentDirection,
            TargetElevation = cs.TargetElevation,
            EffectiveRoadWidth = cs.WidthMeters,
            EffectiveBlendRange = blendRange,
            IsExcluded = cs.IsExcluded,
            Index = cs.Index,
            OwnerSplineId = ownerSplineId,
            LocalIndex = cs.LocalIndex,
            Priority = priority
        };
    }
}