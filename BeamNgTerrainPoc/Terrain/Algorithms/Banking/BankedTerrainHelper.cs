using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
/// Static helper methods for banking-aware terrain blending.
/// 
/// This class provides utilities for calculating banked road elevations at arbitrary
/// world positions. It keeps banking logic out of the large UnifiedTerrainBlender class.
/// 
/// Key methods:
/// - GetBankedElevation: Gets elevation considering banking at a world position
/// - HasBanking: Checks if a cross-section has significant banking
/// - GetBankedSegmentElevation: Gets elevation for a road segment polygon corner
/// </summary>
public static class BankedTerrainHelper
{
    /// <summary>
    /// Threshold for considering banking as "significant".
    /// Below this angle (in radians), banking is treated as zero.
    /// </summary>
    private const float BankingThreshold = 0.0001f;

    /// <summary>
    /// Gets the elevation at a world position considering road banking and junction constraints.
    /// </summary>
    /// <param name="cs">The nearest cross-section</param>
    /// <param name="worldPos">World position to query</param>
    /// <returns>Elevation considering banking and junction constraints, or TargetElevation if no banking</returns>
    public static float GetBankedElevation(UnifiedCrossSection cs, Vector2 worldPos)
    {
        if (float.IsNaN(cs.TargetElevation))
            return float.NaN;

        // Calculate lateral offset to determine if we're on left or right side
        var lateralOffset = CalculateLateralOffset(worldPos, cs);
        var halfWidth = cs.EffectiveRoadWidth / 2.0f;

        // Check for junction constraints - they override banking calculations
        // This is critical for smooth T-junction transitions where the terminating road
        // must match the primary road's sloped surface
        if (cs.HasJunctionConstraint)
        {
            // Interpolate between left and right constrained elevations based on lateral position
            var leftElev = cs.ConstrainedLeftEdgeElevation ?? GetUnconstrainedEdgeElevation(cs, halfWidth, isRight: false);
            var rightElev = cs.ConstrainedRightEdgeElevation ?? GetUnconstrainedEdgeElevation(cs, halfWidth, isRight: true);
            
            // Normalize lateral offset to [0, 1] where 0 = left edge, 1 = right edge
            var t = (lateralOffset + halfWidth) / (2 * halfWidth);
            t = Math.Clamp(t, 0f, 1f);
            
            return Lerp(leftElev, rightElev, t);
        }

        // No junction constraints - use standard banking calculation
        if (!HasBanking(cs))
            return cs.TargetElevation;

        return BankedElevationCalculator.GetElevationAtOffset(cs, lateralOffset);
    }
    
    /// <summary>
    /// Gets the unconstrained edge elevation for banking calculations.
    /// </summary>
    private static float GetUnconstrainedEdgeElevation(UnifiedCrossSection cs, float halfWidth, bool isRight)
    {
        // Check pre-calculated edge elevations first
        if (isRight && !float.IsNaN(cs.RightEdgeElevation))
            return cs.RightEdgeElevation;
        if (!isRight && !float.IsNaN(cs.LeftEdgeElevation))
            return cs.LeftEdgeElevation;
        
        // Calculate from banking
        var elevationDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);
        return isRight
            ? cs.TargetElevation + elevationDelta
            : cs.TargetElevation - elevationDelta;
    }

    /// <summary>
    /// Calculates the lateral offset from road center for a world position.
    /// Positive offset = right side of road (in normal direction).
    /// Negative offset = left side of road (opposite to normal direction).
    /// </summary>
    public static float CalculateLateralOffset(Vector2 worldPos, UnifiedCrossSection cs)
    {
        var toPoint = worldPos - cs.CenterPoint;
        return Vector2.Dot(toPoint, cs.NormalDirection);
    }

    /// <summary>
    /// Checks if a cross-section has significant banking applied.
    /// </summary>
    public static bool HasBanking(UnifiedCrossSection cs)
    {
        return MathF.Abs(cs.BankAngleRadians) > BankingThreshold;
    }

    /// <summary>
    /// Gets the elevation at a road edge position considering banking.
    /// Junction constraints take priority over calculated values.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="isRightEdge">True for right edge, false for left edge</param>
    /// <returns>Edge elevation considering banking and junction constraints</returns>
    public static float GetEdgeElevation(UnifiedCrossSection cs, bool isRightEdge)
    {
        if (float.IsNaN(cs.TargetElevation))
            return float.NaN;

        // Check for explicit junction constraints first - they override all other calculations
        // These are set by junction harmonization when a road terminates at a higher-priority road
        if (isRightEdge && cs.ConstrainedRightEdgeElevation.HasValue)
            return cs.ConstrainedRightEdgeElevation.Value;
        if (!isRightEdge && cs.ConstrainedLeftEdgeElevation.HasValue)
            return cs.ConstrainedLeftEdgeElevation.Value;

        // If edge elevations have been calculated, use them
        if (isRightEdge)
        {
            if (!float.IsNaN(cs.RightEdgeElevation))
                return cs.RightEdgeElevation;
        }
        else
        {
            if (!float.IsNaN(cs.LeftEdgeElevation))
                return cs.LeftEdgeElevation;
        }

        // Fall back to calculation if edge elevations not set
        if (!HasBanking(cs))
            return cs.TargetElevation;

        var halfWidth = cs.EffectiveRoadWidth / 2.0f;
        var elevationDelta = halfWidth * MathF.Sin(cs.BankAngleRadians);

        return isRightEdge
            ? cs.TargetElevation + elevationDelta
            : cs.TargetElevation - elevationDelta;
    }

    /// <summary>
    /// Gets the elevation for a road segment polygon corner.
    /// Used when filling road segment polygons in the protection mask.
    /// </summary>
    /// <param name="cs1">First cross-section of the segment</param>
    /// <param name="cs2">Second cross-section of the segment</param>
    /// <param name="isRightSide">True for right side corners, false for left</param>
    /// <param name="t">Interpolation parameter (0 = cs1, 1 = cs2)</param>
    /// <returns>Interpolated elevation for the polygon corner</returns>
    public static float GetSegmentCornerElevation(
        UnifiedCrossSection cs1,
        UnifiedCrossSection cs2,
        bool isRightSide,
        float t)
    {
        var elev1 = GetEdgeElevation(cs1, isRightSide);
        var elev2 = GetEdgeElevation(cs2, isRightSide);

        if (float.IsNaN(elev1) && float.IsNaN(elev2))
            return float.NaN;

        if (float.IsNaN(elev1))
            return elev2;

        if (float.IsNaN(elev2))
            return elev1;

        return Lerp(elev1, elev2, t);
    }

    /// <summary>
    /// Gets the elevation at a position within a road segment, considering banking and junction constraints.
    /// Interpolates between the two cross-sections and applies banking offset or junction constraints.
    /// </summary>
    /// <param name="cs1">First cross-section</param>
    /// <param name="cs2">Second cross-section</param>
    /// <param name="worldPos">World position to query</param>
    /// <returns>Banked elevation at the position</returns>
    public static float GetBankedElevationInSegment(
        UnifiedCrossSection cs1,
        UnifiedCrossSection cs2,
        Vector2 worldPos)
    {
        // Calculate interpolation parameter based on distance along segment
        var segmentDir = cs2.CenterPoint - cs1.CenterPoint;
        var segmentLength = segmentDir.Length();

        if (segmentLength < 0.001f)
        {
            // Degenerate segment - use first cross-section
            return GetBankedElevation(cs1, worldPos);
        }

        var toPoint = worldPos - cs1.CenterPoint;
        var t = Vector2.Dot(toPoint, segmentDir / segmentLength) / segmentLength;
        t = Math.Clamp(t, 0f, 1f);

        // Interpolate the cross-section properties
        var interpolatedCenter = Vector2.Lerp(cs1.CenterPoint, cs2.CenterPoint, t);
        var interpolatedNormal = Vector2.Normalize(
            Vector2.Lerp(cs1.NormalDirection, cs2.NormalDirection, t));

        // Calculate lateral offset from interpolated centerline
        var toPointFromInterpolated = worldPos - interpolatedCenter;
        var lateralOffset = Vector2.Dot(toPointFromInterpolated, interpolatedNormal);

        // Check for junction constraints - if either cross-section has them, use edge-based interpolation
        // This is critical for smooth T-junction transitions where edge constraints override banking
        if (cs1.HasJunctionConstraint || cs2.HasJunctionConstraint)
        {
            // Get edge elevations (which respect junction constraints via GetEdgeElevation)
            var leftElev1 = GetEdgeElevation(cs1, isRightEdge: false);
            var rightElev1 = GetEdgeElevation(cs1, isRightEdge: true);
            var leftElev2 = GetEdgeElevation(cs2, isRightEdge: false);
            var rightElev2 = GetEdgeElevation(cs2, isRightEdge: true);
            
            // Interpolate edge elevations along the segment
            var leftElev = Lerp(leftElev1, leftElev2, t);
            var rightElev = Lerp(rightElev1, rightElev2, t);
            
            // Interpolate across the road width based on lateral position
            var halfWidth = Lerp(cs1.EffectiveRoadWidth, cs2.EffectiveRoadWidth, t) / 2.0f;
            var lateralT = (lateralOffset + halfWidth) / (2 * halfWidth);
            lateralT = Math.Clamp(lateralT, 0f, 1f);
            
            return Lerp(leftElev, rightElev, lateralT);
        }

        // Standard banking calculation (no junction constraints)
        var interpolatedElevation = Lerp(cs1.TargetElevation, cs2.TargetElevation, t);
        var interpolatedBankAngle = Lerp(cs1.BankAngleRadians, cs2.BankAngleRadians, t);

        // Apply banking offset to interpolated elevation
        var elevationDelta = lateralOffset * MathF.Sin(interpolatedBankAngle);
        return interpolatedElevation + elevationDelta;
    }

    /// <summary>
    /// Calculates the average elevation for a road segment considering banking.
    /// Used for flat/average elevation needs in terrain blending.
    /// </summary>
    /// <param name="cs1">First cross-section</param>
    /// <param name="cs2">Second cross-section</param>
    /// <returns>Average center elevation of the segment</returns>
    public static float GetSegmentAverageElevation(UnifiedCrossSection cs1, UnifiedCrossSection cs2)
    {
        if (float.IsNaN(cs1.TargetElevation) && float.IsNaN(cs2.TargetElevation))
            return float.NaN;

        if (float.IsNaN(cs1.TargetElevation))
            return cs2.TargetElevation;

        if (float.IsNaN(cs2.TargetElevation))
            return cs1.TargetElevation;

        return (cs1.TargetElevation + cs2.TargetElevation) / 2.0f;
    }

    /// <summary>
    /// Gets the banking-aware elevation for a pixel within the road core.
    /// This version is optimized for use in the protection mask builder where
    /// we have pixel coordinates and need to determine banked elevation.
    /// Handles junction constraints which take priority over banking calculations.
    /// </summary>
    /// <param name="cs1">First cross-section of the segment</param>
    /// <param name="cs2">Second cross-section of the segment</param>
    /// <param name="pixelPos">Pixel position (in world coordinates)</param>
    /// <returns>Banked elevation at the pixel position</returns>
    public static float GetBankedElevationForPixel(
        UnifiedCrossSection cs1,
        UnifiedCrossSection cs2,
        Vector2 pixelPos)
    {
        // Check if either cross-section has junction constraints - these require full interpolation
        // because the edge elevations may be constrained differently than banking would calculate
        if (cs1.HasJunctionConstraint || cs2.HasJunctionConstraint)
        {
            return GetBankedElevationInSegment(cs1, cs2, pixelPos);
        }
        
        // Check if either cross-section has banking
        if (!HasBanking(cs1) && !HasBanking(cs2))
        {
            // No banking and no junction constraints - use simple average
            return GetSegmentAverageElevation(cs1, cs2);
        }

        // Use the full interpolation for banked segments
        return GetBankedElevationInSegment(cs1, cs2, pixelPos);
    }

    /// <summary>
    /// Checks if a road segment has any banking or junction constraints that require
    /// per-pixel elevation calculation (either cross-section has banking or constraints).
    /// </summary>
    public static bool SegmentHasBanking(UnifiedCrossSection cs1, UnifiedCrossSection cs2)
    {
        return HasBanking(cs1) || HasBanking(cs2) || 
               cs1.HasJunctionConstraint || cs2.HasJunctionConstraint;
    }

    /// <summary>
    /// Gets the maximum bank angle in a segment (for validation/debugging).
    /// </summary>
    public static float GetMaxBankAngleInSegment(UnifiedCrossSection cs1, UnifiedCrossSection cs2)
    {
        return MathF.Max(MathF.Abs(cs1.BankAngleRadians), MathF.Abs(cs2.BankAngleRadians));
    }

    /// <summary>
    /// Calculates the elevation difference across the road width due to banking.
    /// </summary>
    /// <param name="bankAngleRadians">Bank angle in radians</param>
    /// <param name="roadWidth">Full road width in meters</param>
    /// <returns>Elevation difference between left and right edges</returns>
    public static float CalculateElevationDifference(float bankAngleRadians, float roadWidth)
    {
        return roadWidth * MathF.Sin(bankAngleRadians);
    }

    /// <summary>
    /// Linear interpolation between two values.
    /// </summary>
    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }
}
