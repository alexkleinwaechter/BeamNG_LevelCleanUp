using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Calculates the exact surface elevation where a secondary road's edges
/// should connect to a primary road's surface at T-junctions.
/// 
/// This class centralizes the calculation of junction surface constraints,
/// accounting for both banking (lateral tilt) and longitudinal slope (grade).
/// 
/// The calculated constraints are stored on the terminating cross-section's
/// ConstrainedLeftEdgeElevation and ConstrainedRightEdgeElevation properties,
/// which override the normal edge elevation calculations from banking.
/// </summary>
public static class JunctionSurfaceCalculator
{
    /// <summary>
    /// Calculates the constrained edge elevations for a terminating cross-section
    /// based on the primary road's surface at the connection point.
    /// </summary>
    /// <param name="terminatingCS">The cross-section that terminates at the junction</param>
    /// <param name="primaryCS">The cross-section of the primary (continuous) road at/near the junction</param>
    /// <param name="primarySlope">Longitudinal slope of the primary road (rise/run)</param>
    /// <returns>Tuple of (leftEdgeElevation, rightEdgeElevation) on the primary surface</returns>
    public static (float leftEdge, float rightEdge) CalculateConstrainedEdgeElevations(
        UnifiedCrossSection terminatingCS,
        UnifiedCrossSection primaryCS,
        float primarySlope)
    {
        // Calculate where the terminating road's left and right edges project onto the primary road
        var halfWidth = terminatingCS.EffectiveRoadWidth / 2.0f;
        var leftEdgePos = terminatingCS.CenterPoint - terminatingCS.NormalDirection * halfWidth;
        var rightEdgePos = terminatingCS.CenterPoint + terminatingCS.NormalDirection * halfWidth;
        
        // Get surface elevation at each edge position
        var leftElev = GetPrimarySurfaceElevation(leftEdgePos, primaryCS, primarySlope);
        var rightElev = GetPrimarySurfaceElevation(rightEdgePos, primaryCS, primarySlope);
        
        return (leftElev, rightElev);
    }
    
    /// <summary>
    /// Gets the primary road's surface elevation at a given world position.
    /// Accounts for both banking (lateral tilt) and longitudinal slope.
    /// </summary>
    /// <param name="worldPos">World position to query</param>
    /// <param name="primaryCS">The cross-section of the primary road</param>
    /// <param name="primarySlope">Longitudinal slope of the primary road (rise/run)</param>
    /// <returns>Surface elevation at the given position</returns>
    public static float GetPrimarySurfaceElevation(
        Vector2 worldPos,
        UnifiedCrossSection primaryCS,
        float primarySlope)
    {
        // Calculate offset from primary road center
        var toPoint = worldPos - primaryCS.CenterPoint;
        var lateralOffset = Vector2.Dot(toPoint, primaryCS.NormalDirection);
        var longitudinalOffset = Vector2.Dot(toPoint, primaryCS.TangentDirection);
        
        // Start with centerline elevation
        var elevation = primaryCS.TargetElevation;
        
        // Add banking contribution (lateral)
        if (MathF.Abs(primaryCS.BankAngleRadians) > 0.0001f)
        {
            elevation += lateralOffset * MathF.Sin(primaryCS.BankAngleRadians);
        }
        
        // Add slope contribution (longitudinal)
        if (MathF.Abs(primarySlope) > 0.0001f)
        {
            elevation += longitudinalOffset * primarySlope;
        }
        
        return elevation;
    }
    
    /// <summary>
    /// Calculates the longitudinal slope of a road from neighboring cross-sections.
    /// Uses cross-sections before and after the center index to compute a local gradient.
    /// </summary>
    /// <param name="splineSections">All cross-sections for the spline, ordered by distance</param>
    /// <param name="centerIndex">Index of the cross-section to calculate slope at</param>
    /// <param name="sampleRadius">How many cross-sections before/after to sample (default: 3)</param>
    /// <returns>Slope as rise/run (tangent of the angle), or 0 if cannot be calculated</returns>
    public static float CalculateLocalSlope(
        List<UnifiedCrossSection> splineSections,
        int centerIndex,
        int sampleRadius = 3)
    {
        var prevIdx = Math.Max(0, centerIndex - sampleRadius);
        var nextIdx = Math.Min(splineSections.Count - 1, centerIndex + sampleRadius);
        
        if (prevIdx == nextIdx)
            return 0f;
            
        var cs1 = splineSections[prevIdx];
        var cs2 = splineSections[nextIdx];
        
        var distance = Vector2.Distance(cs1.CenterPoint, cs2.CenterPoint);
        if (distance < 0.1f)
            return 0f;
            
        return (cs2.TargetElevation - cs1.TargetElevation) / distance;
    }
    
    /// <summary>
    /// Applies edge constraints to a terminating cross-section based on the primary road surface.
    /// This is the main entry point for setting junction surface constraints.
    /// </summary>
    /// <param name="terminatingCS">The cross-section that terminates at the junction</param>
    /// <param name="primaryCS">The cross-section of the primary (continuous) road at/near the junction</param>
    /// <param name="primarySlope">Longitudinal slope of the primary road (rise/run)</param>
    public static void ApplyEdgeConstraints(
        UnifiedCrossSection terminatingCS,
        UnifiedCrossSection primaryCS,
        float primarySlope)
    {
        var (leftEdge, rightEdge) = CalculateConstrainedEdgeElevations(
            terminatingCS,
            primaryCS,
            primarySlope);
        
        terminatingCS.ConstrainedLeftEdgeElevation = leftEdge;
        terminatingCS.ConstrainedRightEdgeElevation = rightEdge;
        
        // Also update TargetElevation to the average (for centerline consistency)
        // This ensures the centerline elevation is consistent with the constrained edges
        terminatingCS.TargetElevation = (leftEdge + rightEdge) / 2f;
    }
    
    /// <summary>
    /// Interpolates edge constraints between a junction-constrained cross-section and an unconstrained one.
    /// Used when propagating constraints along the terminating road with distance falloff.
    /// </summary>
    /// <param name="constrainedCS">The cross-section at the junction with constraints set</param>
    /// <param name="unconstrainedCS">The cross-section further from the junction</param>
    /// <param name="weight">Blend weight (0 = use unconstrained, 1 = use constrained)</param>
    /// <returns>Interpolated (leftEdge, rightEdge) values, or (NaN, NaN) if no constraints</returns>
    public static (float? leftEdge, float? rightEdge) InterpolateConstraints(
        UnifiedCrossSection constrainedCS,
        UnifiedCrossSection unconstrainedCS,
        float weight)
    {
        if (!constrainedCS.HasJunctionConstraint)
            return (null, null);
        
        float? interpolatedLeft = null;
        float? interpolatedRight = null;
        
        if (constrainedCS.ConstrainedLeftEdgeElevation.HasValue)
        {
            var constrainedLeft = constrainedCS.ConstrainedLeftEdgeElevation.Value;
            var unconstrainedLeft = GetUnconstrainedEdgeElevation(unconstrainedCS, isRightEdge: false);
            
            if (!float.IsNaN(unconstrainedLeft))
            {
                interpolatedLeft = constrainedLeft * weight + unconstrainedLeft * (1f - weight);
            }
            else
            {
                interpolatedLeft = constrainedLeft;
            }
        }
        
        if (constrainedCS.ConstrainedRightEdgeElevation.HasValue)
        {
            var constrainedRight = constrainedCS.ConstrainedRightEdgeElevation.Value;
            var unconstrainedRight = GetUnconstrainedEdgeElevation(unconstrainedCS, isRightEdge: true);
            
            if (!float.IsNaN(unconstrainedRight))
            {
                interpolatedRight = constrainedRight * weight + unconstrainedRight * (1f - weight);
            }
            else
            {
                interpolatedRight = constrainedRight;
            }
        }
        
        return (interpolatedLeft, interpolatedRight);
    }
    
    /// <summary>
    /// Calculates edge constraints for a cross-section by projecting its edges onto the primary road surface.
    /// This is used for surface-following constraint propagation where each cross-section gets its own
    /// calculated constraint based on where its edges actually project onto the primary road.
    /// 
    /// This is different from InterpolateConstraints which just blends between fixed values.
    /// CalculateSurfaceFollowingConstraints calculates fresh values for each cross-section position.
    /// </summary>
    /// <param name="terminatingCS">The cross-section on the terminating road to calculate constraints for</param>
    /// <param name="primaryCS">The cross-section of the primary (continuous) road near the junction</param>
    /// <param name="primarySlope">Longitudinal slope of the primary road (rise/run)</param>
    /// <param name="weight">Blend weight (0 = use unconstrained, 1 = use fully constrained to primary surface)</param>
    /// <returns>Interpolated (leftEdge, rightEdge) values blended with the cross-section's natural edge elevations</returns>
    public static (float? leftEdge, float? rightEdge) CalculateSurfaceFollowingConstraints(
        UnifiedCrossSection terminatingCS,
        UnifiedCrossSection primaryCS,
        float primarySlope,
        float weight)
    {
        if (weight < 0.001f)
            return (null, null);
        
        // Calculate where this cross-section's edges project onto the primary road surface
        var (primaryLeftElev, primaryRightElev) = CalculateConstrainedEdgeElevations(
            terminatingCS, primaryCS, primarySlope);
        
        // Get the natural (unconstrained) edge elevations for this cross-section
        var naturalLeftElev = GetUnconstrainedEdgeElevation(terminatingCS, isRightEdge: false);
        var naturalRightElev = GetUnconstrainedEdgeElevation(terminatingCS, isRightEdge: true);
        
        float? interpolatedLeft = null;
        float? interpolatedRight = null;
        
        if (!float.IsNaN(naturalLeftElev))
        {
            interpolatedLeft = primaryLeftElev * weight + naturalLeftElev * (1f - weight);
        }
        else
        {
            interpolatedLeft = primaryLeftElev;
        }
        
        if (!float.IsNaN(naturalRightElev))
        {
            interpolatedRight = primaryRightElev * weight + naturalRightElev * (1f - weight);
        }
        else
        {
            interpolatedRight = primaryRightElev;
        }
        
        return (interpolatedLeft, interpolatedRight);
    }
    
    /// <summary>
    /// Calculates FULL surface-following constraints including the centerline elevation.
    /// This version returns both edge constraints AND the primary surface elevation at the centerline,
    /// which is critical for ensuring the entire overlap zone is flat against the primary road.
    /// 
    /// The key insight is that for smooth T-junction transitions, we need:
    /// 1. Edge elevations that match the primary surface at the edge positions
    /// 2. Centerline elevation that ALSO matches the primary surface (not just edge average)
    /// 
    /// This prevents the "jagged junction" artifact where edges are correct but the
    /// center of the terminating road doesn't properly follow the primary surface.
    /// </summary>
    /// <param name="terminatingCS">The cross-section on the terminating road</param>
    /// <param name="primaryCS">The cross-section of the primary road near the junction</param>
    /// <param name="primarySlope">Longitudinal slope of the primary road (rise/run)</param>
    /// <param name="weight">Blend weight (0 = use natural, 1 = fully follow primary surface)</param>
    /// <returns>Tuple of (leftEdge, rightEdge, centerlineElevation) all following the primary surface</returns>
    public static (float? leftEdge, float? rightEdge, float? centerline) CalculateFullSurfaceFollowingConstraints(
        UnifiedCrossSection terminatingCS,
        UnifiedCrossSection primaryCS,
        float primarySlope,
        float weight)
    {
        if (weight < 0.001f)
            return (null, null, null);
        
        // Calculate where this cross-section's edges AND CENTER project onto the primary road surface
        var halfWidth = terminatingCS.EffectiveRoadWidth / 2.0f;
        var leftEdgePos = terminatingCS.CenterPoint - terminatingCS.NormalDirection * halfWidth;
        var rightEdgePos = terminatingCS.CenterPoint + terminatingCS.NormalDirection * halfWidth;
        var centerPos = terminatingCS.CenterPoint;
        
        // Get primary surface elevation at all three positions
        var primaryLeftElev = GetPrimarySurfaceElevation(leftEdgePos, primaryCS, primarySlope);
        var primaryRightElev = GetPrimarySurfaceElevation(rightEdgePos, primaryCS, primarySlope);
        var primaryCenterElev = GetPrimarySurfaceElevation(centerPos, primaryCS, primarySlope);
        
        // Get natural (unconstrained) elevations for this cross-section
        var naturalLeftElev = GetUnconstrainedEdgeElevation(terminatingCS, isRightEdge: false);
        var naturalRightElev = GetUnconstrainedEdgeElevation(terminatingCS, isRightEdge: true);
        var naturalCenterElev = terminatingCS.TargetElevation;
        
        float? interpolatedLeft = null;
        float? interpolatedRight = null;
        float? interpolatedCenter = null;
        
        // Interpolate left edge
        if (!float.IsNaN(naturalLeftElev))
            interpolatedLeft = primaryLeftElev * weight + naturalLeftElev * (1f - weight);
        else
            interpolatedLeft = primaryLeftElev;
        
        // Interpolate right edge
        if (!float.IsNaN(naturalRightElev))
            interpolatedRight = primaryRightElev * weight + naturalRightElev * (1f - weight);
        else
            interpolatedRight = primaryRightElev;
        
        // Interpolate centerline - this is the key addition for smooth junctions
        if (!float.IsNaN(naturalCenterElev))
            interpolatedCenter = primaryCenterElev * weight + naturalCenterElev * (1f - weight);
        else
            interpolatedCenter = primaryCenterElev;
        
        return (interpolatedLeft, interpolatedRight, interpolatedCenter);
    }
    
    /// <summary>
    /// Gets the edge elevation for a cross-section ignoring any constraints.
    /// Used when interpolating between constrained and unconstrained cross-sections.
    /// </summary>
    private static float GetUnconstrainedEdgeElevation(UnifiedCrossSection cs, bool isRightEdge)
    {
        if (float.IsNaN(cs.TargetElevation))
            return float.NaN;
        
        // Check for pre-calculated edge elevations
        if (isRightEdge && !float.IsNaN(cs.RightEdgeElevation))
            return cs.RightEdgeElevation;
        if (!isRightEdge && !float.IsNaN(cs.LeftEdgeElevation))
            return cs.LeftEdgeElevation;
        
        // Calculate from banking
        var halfWidth = cs.EffectiveRoadWidth / 2.0f;
        var elevationDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
        
        return isRightEdge
            ? cs.TargetElevation + elevationDelta
            : cs.TargetElevation - elevationDelta;
    }
}
