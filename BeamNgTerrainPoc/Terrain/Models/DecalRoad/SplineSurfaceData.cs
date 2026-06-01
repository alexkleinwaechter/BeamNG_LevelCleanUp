using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// Represents a road spline's physical surface footprint for overlap detection.
/// Built from the spline's sampled centerline and full road surface width.
/// </summary>
public sealed class SplineSurfaceData
{
    public required int SplineId { get; init; }

    /// <summary>
    /// Half-width of the road surface in meters (EffectiveMasterSplineWidthMeters / 2).
    /// The spatial index adds its own margin on top during overlap checks.
    /// </summary>
    public required float SurfaceHalfWidth { get; init; }

    /// <summary>
    /// Sampled 2D centerline points along the spline, in BeamNG world coordinates.
    /// Same spacing as DecalRoad node generation (NodeSpacingMeters).
    /// </summary>
    public required IReadOnlyList<Vector2> CenterlinePoints { get; init; }
}
