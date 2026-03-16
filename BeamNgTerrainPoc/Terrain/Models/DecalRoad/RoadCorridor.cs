using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// A single sampled point along a road corridor, used for overlap checks.
/// </summary>
public readonly record struct CorridorSection(
    Vector2 Center,
    Vector2 Normal,
    float DistanceAlongSpline);

/// <summary>
/// Represents a road's surface corridor for overlap checking.
/// The corridor extends CorridorHalfWidth on each side of the centerline
/// along the entire length of the sampled sections.
/// CorridorHalfWidth is the maximum outer extent of any enabled DecalRoad layer,
/// computed as: max(|position| * 0.5 * roadWidth + nodeWidth / 2) + margin.
/// </summary>
public class RoadCorridor
{
    public required int SplineId { get; init; }
    public required float RoadWidth { get; init; }
    public required float CorridorHalfWidth { get; init; }
    public required List<CorridorSection> Sections { get; init; }
}
