using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Statistics and results from matching bridge/tunnel structures to road splines.
/// </summary>
public class BridgeTunnelMatchResult
{
    /// <summary>
    /// Total number of bridge/tunnel structures processed.
    /// </summary>
    public int TotalStructures { get; set; }

    /// <summary>
    /// Number of bridges that were successfully matched to splines.
    /// </summary>
    public int MatchedBridges { get; set; }

    /// <summary>
    /// Number of tunnels that were successfully matched to splines.
    /// </summary>
    public int MatchedTunnels { get; set; }

    /// <summary>
    /// Number of structures that could not be matched to any spline.
    /// </summary>
    public int UnmatchedStructures { get; set; }

    /// <summary>
    /// List of structures that could not be matched (for debugging/logging).
    /// </summary>
    public List<OsmBridgeTunnel> UnmatchedList { get; set; } = [];

    /// <summary>
    /// List of matched structures with their corresponding spline IDs.
    /// </summary>
    public List<BridgeTunnelMatch> MatchedList { get; set; } = [];

    /// <summary>
    /// Total number of matched structures.
    /// </summary>
    public int TotalMatched => MatchedBridges + MatchedTunnels;

    /// <summary>
    /// Match success rate as a percentage (0-100).
    /// </summary>
    public float MatchSuccessRate => TotalStructures > 0
        ? (float)TotalMatched / TotalStructures * 100f
        : 0f;

    public override string ToString()
    {
        return $"BridgeTunnelMatchResult: {TotalMatched}/{TotalStructures} matched " +
               $"({MatchSuccessRate:F1}%) - {MatchedBridges} bridges, {MatchedTunnels} tunnels, " +
               $"{UnmatchedStructures} unmatched";
    }
}

/// <summary>
/// Represents a successful match between an OSM bridge/tunnel and a road spline.
/// </summary>
public class BridgeTunnelMatch
{
    /// <summary>
    /// The matched OSM bridge/tunnel structure.
    /// </summary>
    public required OsmBridgeTunnel Structure { get; init; }

    /// <summary>
    /// ID of the matched road spline.
    /// </summary>
    public int SplineId { get; set; }

    /// <summary>
    /// Average distance between structure geometry and spline centerline (in meters).
    /// Lower values indicate better geometric alignment.
    /// </summary>
    public float AverageDistanceMeters { get; set; }

    /// <summary>
    /// Whether the match was made using OSM way ID (true) or geometric matching (false).
    /// Way ID matching is more reliable when available.
    /// </summary>
    public bool MatchedByWayId { get; set; }

    /// <summary>
    /// Overlap percentage: how much of the structure overlaps with the spline (0-100).
    /// </summary>
    public float OverlapPercent { get; set; }

    public override string ToString()
    {
        var matchType = MatchedByWayId ? "WayID" : "Geometric";
        return $"Match[{matchType}]: {Structure.StructureType} -> Spline {SplineId} " +
               $"(dist: {AverageDistanceMeters:F1}m, overlap: {OverlapPercent:F0}%)";
    }
}
