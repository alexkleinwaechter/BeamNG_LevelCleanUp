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
    /// Sampled centerline points along the spline, in BeamNG world coordinates.
    /// Same spacing as DecalRoad node generation (NodeSpacingMeters).
    /// Z is the road surface elevation in the same frame as DecalRoad node Z
    /// (TargetElevation + terrainBaseHeight), so overlap checks can require
    /// vertical coplanarity — a bridge deck crossing above a road must not
    /// count as overlapping the road below.
    /// </summary>
    public required IReadOnlyList<Vector3> CenterlinePoints { get; init; }
}
