namespace BeamNG.Procedural3D.RoadMesh;

using System.Numerics;

/// <summary>
/// Represents a cross-section of the road at a specific point.
/// This mirrors the data from the road smoothing pipeline.
/// </summary>
public class RoadCrossSection
{
    /// <summary>
    /// Position on the road centerline (X, Y in world coordinates).
    /// </summary>
    public Vector2 CenterPoint { get; set; }

    /// <summary>
    /// Elevation at the road center.
    /// </summary>
    public float CenterElevation { get; set; }

    /// <summary>
    /// Unit vector along the road direction.
    /// </summary>
    public Vector2 TangentDirection { get; set; }

    /// <summary>
    /// Unit vector perpendicular to the road (points to the right, matching terrain pipeline convention).
    /// </summary>
    public Vector2 NormalDirection { get; set; }

    /// <summary>
    /// Road width at this cross-section.
    /// </summary>
    public float WidthMeters { get; set; }

    /// <summary>
    /// Banking angle in radians.
    /// Positive bank angle = outer edge of curve is higher (superelevation for left curves).
    /// Convention matches the terrain pipeline where positive bank raises the right side.
    /// </summary>
    public float BankAngleRadians { get; set; }

    /// <summary>
    /// Distance along the road from start (for UV mapping).
    /// </summary>
    public float DistanceAlongRoad { get; set; }

    /// <summary>
    /// Optional: Left edge elevation (if constrained).
    /// </summary>
    public float? LeftEdgeElevation { get; set; }

    /// <summary>
    /// Optional: Right edge elevation (if constrained).
    /// </summary>
    public float? RightEdgeElevation { get; set; }

    /// <summary>
    /// Calculates the 3D position of the left edge of the road.
    /// Left edge is opposite to NormalDirection (which points right).
    /// </summary>
    public Vector3 GetLeftEdgePosition()
    {
        float halfWidth = WidthMeters / 2f;
        // Left edge is in the negative normal direction (normal points right)
        Vector2 leftPoint2D = CenterPoint - NormalDirection * halfWidth;
        float leftElevation = LeftEdgeElevation ?? CalculateLeftEdgeElevation(halfWidth);
        return new Vector3(leftPoint2D.X, leftPoint2D.Y, leftElevation);
    }

    /// <summary>
    /// Calculates the 3D position of the right edge of the road.
    /// Right edge is in the NormalDirection.
    /// </summary>
    public Vector3 GetRightEdgePosition()
    {
        float halfWidth = WidthMeters / 2f;
        // Right edge is in the positive normal direction
        Vector2 rightPoint2D = CenterPoint + NormalDirection * halfWidth;
        float rightElevation = RightEdgeElevation ?? CalculateRightEdgeElevation(halfWidth);
        return new Vector3(rightPoint2D.X, rightPoint2D.Y, rightElevation);
    }

    /// <summary>
    /// Calculates the 3D position of the road center.
    /// </summary>
    public Vector3 GetCenterPosition()
    {
        return new Vector3(CenterPoint.X, CenterPoint.Y, CenterElevation);
    }

    /// <summary>
    /// Calculates left edge elevation based on banking angle.
    /// Matches terrain pipeline: for positive bank angle, left edge is lower.
    /// </summary>
    /// <param name="halfWidth">Half the road width in meters.</param>
    private float CalculateLeftEdgeElevation(float halfWidth)
    {
        // elevation delta = halfWidth * sin(bankAngle)
        // For positive bank angle (left curve), right side is higher, left side is lower
        var elevationDelta = halfWidth * MathF.Sin(BankAngleRadians);
        return CenterElevation - elevationDelta;
    }

    /// <summary>
    /// Calculates right edge elevation based on banking angle.
    /// Matches terrain pipeline: for positive bank angle, right side is higher.
    /// </summary>
    /// <param name="halfWidth">Half the road width in meters.</param>
    private float CalculateRightEdgeElevation(float halfWidth)
    {
        // elevation delta = halfWidth * sin(bankAngle)
        // For positive bank angle (left curve), right side is higher
        var elevationDelta = halfWidth * MathF.Sin(BankAngleRadians);
        return CenterElevation + elevationDelta;
    }
}
