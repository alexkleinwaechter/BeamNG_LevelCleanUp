using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
///     A road segment with attached smoothing parameters.
///     Enables per-spline parameter application in a unified network.
///     This class bridges the gap between material-based configuration and
///     material-agnostic processing, allowing the road network to be processed
///     as a whole while respecting per-road parameters.
/// </summary>
public class ParameterizedRoadSpline
{
    /// <summary>
    ///     The geometric spline data (positions, tangents, normals).
    /// </summary>
    public required RoadSpline Spline { get; init; }

    /// <summary>
    ///     Smoothing parameters from the originating material.
    ///     These parameters control road width, blend range, slope constraints, etc.
    /// </summary>
    public required RoadSmoothingParameters Parameters { get; init; }

    /// <summary>
    ///     Source material name (for debugging and material painting).
    ///     This preserves the material association for the final painting phase
    ///     where terrain textures are applied.
    /// </summary>
    public required string MaterialName { get; init; }

    /// <summary>
    ///     Priority for junction conflicts (higher = wins).
    ///     When two roads meet at a junction, the higher-priority road's elevation
    ///     takes precedence, and the lower-priority road adapts to match.
    ///     Priority sources (in order of preference):
    ///     1. OSM road classification (motorway=100, primary=80, residential=50, etc.)
    ///     2. RoadWidthMeters (wider = higher priority)
    ///     3. Material order in UI (higher index = higher priority, consistent with texture painting)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    ///     Unique identifier for cross-referencing.
    ///     Used to link cross-sections back to their source spline and
    ///     for spatial indexing in junction detection.
    /// </summary>
    public required int SplineId { get; init; }

    /// <summary>
    ///     Optional OSM road type for priority calculation.
    ///     Values like "motorway", "primary", "residential", etc.
    ///     Null if extracted from PNG layer map.
    /// </summary>
    public string? OsmRoadType { get; init; }

    /// <summary>
    ///     Optional display name for the road (e.g., from OSM name tag).
    ///     Used for debugging and BeamNG export.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    ///     Total length of the spline in meters.
    ///     Cached for quick access during junction detection.
    /// </summary>
    public float TotalLengthMeters => Spline.TotalLength;

    /// <summary>
    ///     Gets the start position of the spline.
    /// </summary>
    public Vector2 StartPoint => Spline.GetPointAtDistance(0);

    /// <summary>
    ///     Gets the end position of the spline.
    /// </summary>
    public Vector2 EndPoint => Spline.GetPointAtDistance(Spline.TotalLength);

    /// <summary>
    ///     Calculates the priority based on OSM road type.
    ///     Returns a default of 0 if no OSM type is available.
    /// </summary>
    /// <returns>Priority value (0-100, higher = more important)</returns>
    public static int GetOsmPriority(string? osmRoadType)
    {
        if (string.IsNullOrEmpty(osmRoadType))
            return 0;

        // OSM road type priorities based on the implementation plan
        // Motorways & Trunk: highest priority
        // Primary & Secondary: high priority
        // Tertiary & Local: medium priority
        // Paths/Tracks: low priority
        // Special: lowest priority

        return osmRoadType.ToLowerInvariant() switch
        {
            // Motorways & Trunk (priority 90-100)
            "motorway" => 100,
            "motorway_link" => 95,
            "trunk" => 90,
            "trunk_link" => 85,

            // Primary & Secondary (priority 70-80)
            "primary" => 80,
            "primary_link" => 78,
            "secondary" => 75,
            "secondary_link" => 73,

            // Tertiary & Local (priority 40-60)
            "tertiary" => 60,
            "tertiary_link" => 58,
            "residential" => 55,
            "unclassified" => 50,
            "service" => 45,
            "living_street" => 40,

            // Paths/Tracks (priority 10-30)
            "track" => 30,
            "path" => 25,
            "footway" => 20,
            "cycleway" => 20,
            "bridleway" => 15,
            "pedestrian" => 15,
            "steps" => 10,

            // Special (priority 1-5)
            "busway" => 5,
            "raceway" => 5,
            "proposed" => 1,

            // Unknown types
            _ => 35 // Default to below tertiary but above paths
        };
    }

    /// <summary>
    ///     Calculates priority based on road width in meters.
    ///     Wider roads get higher priority.
    /// </summary>
    /// <param name="roadWidthMeters">Road width in meters</param>
    /// <returns>Priority value (0-100)</returns>
    public static int GetWidthBasedPriority(float roadWidthMeters)
    {
        // Scale priority: 4m road = 40, 8m road = 60, 12m road = 80, 20m+ road = 100
        return (int)Math.Clamp(roadWidthMeters * 5, 10, 100);
    }

    /// <summary>
    ///     Calculates the effective priority for this spline using the cascade:
    ///     1. OSM road type (if available)
    ///     2. Road width
    ///     3. Material order (higher index = higher priority as tiebreaker)
    /// </summary>
    /// <param name="materialOrderIndex">Material index from UI order (0 = first/top, higher = later/bottom)</param>
    /// <returns>Final priority value</returns>
    public int CalculateEffectivePriority(int materialOrderIndex)
    {
        // If we have OSM road type, use it as primary source
        var osmPriority = GetOsmPriority(OsmRoadType);
        if (osmPriority > 0)
            // OSM priority is authoritative, but add material order index as tiebreaker
            // Higher material index = higher priority (consistent with texture painting where last wins)
            return osmPriority * 100 + materialOrderIndex;

        // Fall back to width-based priority
        var widthPriority = GetWidthBasedPriority(Parameters.RoadWidthMeters);

        // Combine with material order (higher index = higher priority, consistent with texture painting)
        return widthPriority * 100 + materialOrderIndex;
    }
}