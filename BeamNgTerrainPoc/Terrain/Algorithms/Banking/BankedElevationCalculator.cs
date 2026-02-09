using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Banking;

/// <summary>
///     Calculates left and right road edge elevations based on banking.
///     Also provides utilities for getting elevation at any lateral offset from the road centerline.
/// </summary>
public class BankedElevationCalculator
{
    /// <summary>
    ///     Calculates edge elevations for all cross-sections based on bank angle.
    ///     Must be called AFTER TargetElevation is set and BankAngleRadians is calculated.
    /// </summary>
    /// <param name="crossSections">Cross-sections with TargetElevation and BankAngleRadians set</param>
    /// <param name="roadHalfWidth">Half the road width in meters</param>
    public void CalculateEdgeElevations(IList<UnifiedCrossSection> crossSections, float roadHalfWidth)
    {
        foreach (var cs in crossSections) CalculateEdgeElevationsForCS(cs, roadHalfWidth);
    }

    /// <summary>
    ///     Calculates edge elevations for a single cross-section.
    /// </summary>
    /// <param name="cs">The cross-section with TargetElevation and BankAngleRadians set</param>
    /// <param name="roadHalfWidth">Half the road width in meters</param>
    public void CalculateEdgeElevationsForCS(UnifiedCrossSection cs, float roadHalfWidth)
    {
        if (float.IsNaN(cs.TargetElevation))
        {
            cs.LeftEdgeElevation = float.NaN;
            cs.RightEdgeElevation = float.NaN;
            return;
        }

        // Calculate elevation delta from center to edge
        // delta = halfWidth * sin(bankAngle)
        // For small angles, sin(x) ≈ x, but we use exact calculation for accuracy
        var elevationDelta = roadHalfWidth * MathF.Sin(cs.BankAngleRadians);

        // For positive bank angle (left curve), right side is higher
        // NormalDirection points to the right, so:
        // - Positive offset from center = right side = higher for positive bank angle
        // - Negative offset from center = left side = lower for positive bank angle
        cs.LeftEdgeElevation = cs.TargetElevation - elevationDelta;
        cs.RightEdgeElevation = cs.TargetElevation + elevationDelta;
    }

    /// <summary>
    ///     Gets the elevation at a specific lateral offset from road center.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="lateralOffset">Offset from center (negative = left, positive = right)</param>
    /// <returns>Interpolated elevation at the offset</returns>
    public static float GetElevationAtOffset(UnifiedCrossSection cs, float lateralOffset)
    {
        if (float.IsNaN(cs.TargetElevation)) return float.NaN;

        // Linear interpolation based on bank angle
        // The elevation change is proportional to the offset and sin(bank angle)
        var elevationDelta = lateralOffset * MathF.Sin(cs.BankAngleRadians);
        return cs.TargetElevation + elevationDelta;
    }

    /// <summary>
    ///     Gets the elevation at a world position considering the cross-section's banking.
    ///     Projects the world position onto the cross-section line and calculates elevation.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="worldPos">World position to query (2D, elevation is returned)</param>
    /// <returns>Elevation at the world position considering banking</returns>
    public static float GetElevationAtWorldPosition(UnifiedCrossSection cs, Vector2 worldPos)
    {
        if (float.IsNaN(cs.TargetElevation)) return float.NaN;

        // Calculate lateral offset by projecting the position onto the normal direction
        var toPoint = worldPos - cs.CenterPoint;
        var lateralOffset = Vector2.Dot(toPoint, cs.NormalDirection);

        return GetElevationAtOffset(cs, lateralOffset);
    }

    /// <summary>
    ///     Interpolates elevation between two cross-sections at a given world position.
    ///     Useful for getting precise elevations between cross-section samples.
    /// </summary>
    /// <param name="cs1">First cross-section (closer to spline start)</param>
    /// <param name="cs2">Second cross-section (closer to spline end)</param>
    /// <param name="worldPos">World position to query</param>
    /// <param name="t">Interpolation parameter (0 = cs1, 1 = cs2)</param>
    /// <returns>Interpolated elevation considering banking at both cross-sections</returns>
    public static float InterpolateBankedElevation(
        UnifiedCrossSection cs1,
        UnifiedCrossSection cs2,
        Vector2 worldPos,
        float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        var elev1 = GetElevationAtWorldPosition(cs1, worldPos);
        var elev2 = GetElevationAtWorldPosition(cs2, worldPos);

        if (float.IsNaN(elev1) && float.IsNaN(elev2)) return float.NaN;

        if (float.IsNaN(elev1)) return elev2;

        if (float.IsNaN(elev2)) return elev1;

        return Lerp(elev1, elev2, t);
    }

    /// <summary>
    ///     Calculates the lateral offset from the road center for a world position.
    ///     Positive values indicate the right side of the road (in the normal direction).
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="worldPos">World position to query</param>
    /// <returns>Lateral offset in meters (negative = left, positive = right)</returns>
    public static float CalculateLateralOffset(UnifiedCrossSection cs, Vector2 worldPos)
    {
        var toPoint = worldPos - cs.CenterPoint;
        return Vector2.Dot(toPoint, cs.NormalDirection);
    }

    /// <summary>
    ///     Checks if a world position is within the road width bounds of a cross-section.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="worldPos">World position to check</param>
    /// <param name="halfWidth">Half the road width in meters</param>
    /// <returns>True if the position is within the road bounds</returns>
    public static bool IsWithinRoadBounds(UnifiedCrossSection cs, Vector2 worldPos, float halfWidth)
    {
        var lateralOffset = CalculateLateralOffset(cs, worldPos);
        return MathF.Abs(lateralOffset) <= halfWidth;
    }

    /// <summary>
    ///     Gets the slope (grade) of the banked road surface in the lateral direction.
    ///     This is the tangent of the bank angle.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <returns>Lateral slope (rise/run). Positive = right side higher</returns>
    public static float GetLateralSlope(UnifiedCrossSection cs)
    {
        return MathF.Tan(cs.BankAngleRadians);
    }

    /// <summary>
    ///     Gets the superelevation rate (percentage) for the banking.
    ///     This is how banking is typically specified in road design standards.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <returns>Superelevation as a percentage (e.g., 8.0 means 8%)</returns>
    public static float GetSuperelevationPercent(UnifiedCrossSection cs)
    {
        return MathF.Tan(cs.BankAngleRadians) * 100f;
    }

    /// <summary>
    ///     Calculates the elevation difference between left and right road edges.
    /// </summary>
    /// <param name="cs">The cross-section with edge elevations calculated</param>
    /// <returns>Elevation difference (right - left) in meters</returns>
    public static float GetEdgeElevationDifference(UnifiedCrossSection cs)
    {
        if (float.IsNaN(cs.LeftEdgeElevation) || float.IsNaN(cs.RightEdgeElevation)) return 0;

        return cs.RightEdgeElevation - cs.LeftEdgeElevation;
    }

    /// <summary>
    ///     Gets the world position of the left edge of the road at a cross-section.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="halfWidth">Half the road width in meters</param>
    /// <returns>3D position (X, Y, Z) of the left edge</returns>
    public static Vector3 GetLeftEdgePosition(UnifiedCrossSection cs, float halfWidth)
    {
        var pos2D = cs.CenterPoint - cs.NormalDirection * halfWidth;
        return new Vector3(pos2D.X, pos2D.Y, cs.LeftEdgeElevation);
    }

    /// <summary>
    ///     Gets the world position of the right edge of the road at a cross-section.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <param name="halfWidth">Half the road width in meters</param>
    /// <returns>3D position (X, Y, Z) of the right edge</returns>
    public static Vector3 GetRightEdgePosition(UnifiedCrossSection cs, float halfWidth)
    {
        var pos2D = cs.CenterPoint + cs.NormalDirection * halfWidth;
        return new Vector3(pos2D.X, pos2D.Y, cs.RightEdgeElevation);
    }

    /// <summary>
    ///     Gets the 3D centerline position including elevation.
    /// </summary>
    /// <param name="cs">The cross-section</param>
    /// <returns>3D position (X, Y, Z) of the road centerline</returns>
    public static Vector3 GetCenterPosition(UnifiedCrossSection cs)
    {
        return new Vector3(cs.CenterPoint.X, cs.CenterPoint.Y, cs.TargetElevation);
    }

    /// <summary>
    ///     Calculates edge elevations with adaptive blending for junction transitions.
    ///     For cross-sections that adapt to a higher-priority road, calculates edge elevations
    ///     that smoothly transition to match the banked surface of the higher-priority road.
    /// </summary>
    /// <param name="cs">The cross-section to calculate</param>
    /// <param name="roadHalfWidth">Half the road width in meters</param>
    /// <param name="findNearestHigherPriorityCS">
    ///     Function to find the nearest cross-section on the higher-priority road.
    ///     Returns null if not found.
    /// </param>
    public void CalculateAdaptiveEdgeElevations(
        UnifiedCrossSection cs,
        float roadHalfWidth,
        Func<UnifiedCrossSection, UnifiedCrossSection?> findNearestHigherPriorityCS)
    {
        if (cs.JunctionBankingBehavior != JunctionBankingBehavior.AdaptToHigherPriority ||
            !cs.HigherPrioritySplineId.HasValue)
        {
            // Normal edge elevation calculation
            CalculateEdgeElevationsForCS(cs, roadHalfWidth);
            return;
        }

        // Find the nearest cross-section on the higher-priority road
        var higherPriorityCS = findNearestHigherPriorityCS(cs);

        if (higherPriorityCS == null)
        {
            CalculateEdgeElevationsForCS(cs, roadHalfWidth);
            return;
        }

        // Calculate what elevation the higher-priority road has at our edge positions
        var leftEdgePos = cs.CenterPoint - cs.NormalDirection * roadHalfWidth;
        var rightEdgePos = cs.CenterPoint + cs.NormalDirection * roadHalfWidth;

        // Project our edge positions onto the higher-priority road's cross-section
        var targetLeftElev = GetElevationAtWorldPosition(higherPriorityCS, leftEdgePos);
        var targetRightElev = GetElevationAtWorldPosition(higherPriorityCS, rightEdgePos);

        // Calculate our normal edge elevations
        var normalLeft = cs.TargetElevation - roadHalfWidth * MathF.Sin(cs.BankAngleRadians);
        var normalRight = cs.TargetElevation + roadHalfWidth * MathF.Sin(cs.BankAngleRadians);

        // Blend between our calculated edges and the target edges
        // JunctionBankingFactor: 0 = at junction center, 1 = far from junction
        var t = cs.JunctionBankingFactor;

        if (float.IsNaN(targetLeftElev))
            cs.LeftEdgeElevation = normalLeft;
        else
            cs.LeftEdgeElevation = Lerp(targetLeftElev, normalLeft, t);

        if (float.IsNaN(targetRightElev))
            cs.RightEdgeElevation = normalRight;
        else
            cs.RightEdgeElevation = Lerp(targetRightElev, normalRight, t);
    }

    /// <summary>
    ///     Linear interpolation between two values.
    /// </summary>
    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }
}