using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Cross-section with reference to its source spline and per-spline parameters.
/// Extends the base CrossSection concept with owner tracking and effective parameter values.
/// </summary>
public class UnifiedCrossSection
{
    /// <summary>
    /// World coordinates of the cross-section center point (on the road centerline).
    /// </summary>
    public Vector2 CenterPoint { get; set; }

    /// <summary>
    /// Unit vector perpendicular to the road direction (points to the right).
    /// </summary>
    public Vector2 NormalDirection { get; set; }

    /// <summary>
    /// Unit vector along the road direction (tangent).
    /// </summary>
    public Vector2 TangentDirection { get; set; }

    /// <summary>
    /// Calculated target elevation for this cross-section in world units.
    /// Initialized to NaN to distinguish "not set" from valid zero elevation.
    /// </summary>
    public float TargetElevation { get; set; } = float.NaN;

    /// <summary>
    /// Whether this cross-section is in an exclusion zone (e.g., over water).
    /// If true, no smoothing is applied at this location.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// Global index of this cross-section across all splines in the network.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Reference to the owning parameterized spline.
    /// Links back to the ParameterizedRoadSpline that generated this cross-section.
    /// </summary>
    public int OwnerSplineId { get; set; }

    /// <summary>
    /// Index within the owning spline (monotonic along the spline path).
    /// </summary>
    public int LocalIndex { get; set; }

    /// <summary>
    /// Distance along the spline from its start point in meters.
    /// </summary>
    public float DistanceAlongSpline { get; set; }

    /// <summary>
    /// Effective road width for THIS cross-section in meters (from spline's params).
    /// Cached from the owning spline's RoadWidthMeters for fast access during blending.
    /// </summary>
    public float EffectiveRoadWidth { get; set; }

    /// <summary>
    /// Effective blend range for THIS cross-section in meters (from spline's params).
    /// Cached from the owning spline's TerrainAffectedRangeMeters for fast access during blending.
    /// </summary>
    public float EffectiveBlendRange { get; set; }

    /// <summary>
    /// Priority inherited from the owning spline.
    /// Used for resolving conflicts in overlapping blend zones.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether this is the first cross-section of its spline (start endpoint).
    /// </summary>
    public bool IsSplineStart { get; set; }

    /// <summary>
    /// Whether this is the last cross-section of its spline (end endpoint).
    /// </summary>
    public bool IsSplineEnd { get; set; }

    /// <summary>
    /// Original elevation sampled from terrain before any modifications.
    /// Used for endpoint tapering and blend calculations.
    /// </summary>
    public float OriginalTerrainElevation { get; set; } = float.NaN;

    /// <summary>
    /// Creates a UnifiedCrossSection from a SplineSample.
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
            IsExcluded = false
        };
    }

    /// <summary>
    /// Converts this UnifiedCrossSection to a standard CrossSection for compatibility.
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
    /// Creates a UnifiedCrossSection from a standard CrossSection.
    /// Note: Some unified-specific fields will need to be set separately.
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
