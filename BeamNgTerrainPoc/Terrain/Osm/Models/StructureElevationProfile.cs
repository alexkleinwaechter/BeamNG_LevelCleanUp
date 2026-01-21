namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Types of elevation curves for bridges and tunnels.
/// </summary>
public enum StructureElevationCurveType
{
    /// <summary>Flat profile - constant grade from entry to exit.</summary>
    Linear,

    /// <summary>Smooth parabolic curve (sag or crest).</summary>
    Parabolic,

    /// <summary>S-curve for tunnels - descent, level, ascent.</summary>
    SCurve,

    /// <summary>Symmetric arch for long bridges.</summary>
    Arch
}

/// <summary>
/// Defines the elevation profile for a bridge or tunnel structure.
/// Bridges and tunnels have independent elevation profiles that don't follow terrain.
/// </summary>
public class StructureElevationProfile
{
    /// <summary>
    /// Entry point elevation in meters (where structure meets road at start).
    /// </summary>
    public float EntryElevation { get; set; }

    /// <summary>
    /// Exit point elevation in meters (where structure meets road at end).
    /// </summary>
    public float ExitElevation { get; set; }

    /// <summary>
    /// Total length of the structure in meters.
    /// </summary>
    public float LengthMeters { get; set; }

    /// <summary>
    /// Type of elevation curve to use.
    /// </summary>
    public StructureElevationCurveType CurveType { get; set; } = StructureElevationCurveType.Linear;

    /// <summary>
    /// For tunnels: minimum clearance below terrain surface (default 5m).
    /// For bridges: minimum clearance above obstacle (water, road, etc.).
    /// </summary>
    public float MinimumClearanceMeters { get; set; } = 5.0f;

    /// <summary>
    /// Terrain elevations sampled along the structure centerline.
    /// Used for tunnel depth calculations to ensure adequate clearance.
    /// </summary>
    public float[]? TerrainElevationsAlongPath { get; set; }

    /// <summary>
    /// Calculated lowest point elevation for the structure.
    /// For tunnels: ensures this is at least MinimumClearanceMeters below terrain.
    /// For bridges: the lowest deck elevation (accounting for sag).
    /// </summary>
    public float CalculatedLowestPointElevation { get; set; }

    /// <summary>
    /// Calculated highest point elevation for the structure.
    /// For arch bridges: the peak of the arch.
    /// For tunnels: typically the entry or exit elevation.
    /// </summary>
    public float CalculatedHighestPointElevation { get; set; }

    /// <summary>
    /// Whether this profile required going deeper than a linear interpolation would provide.
    /// Only applicable to tunnels that need to dip below terrain peaks.
    /// </summary>
    public bool RequiredDepthAdjustment { get; set; }

    /// <summary>
    /// The actual grade percentage at the steepest point.
    /// Used for validation against maximum allowed grades.
    /// </summary>
    public float MaxGradePercent { get; set; }

    /// <summary>
    /// Whether this is a bridge elevation profile.
    /// </summary>
    public bool IsBridge { get; set; }

    /// <summary>
    /// Whether this is a tunnel elevation profile.
    /// </summary>
    public bool IsTunnel { get; set; }

    /// <summary>
    /// Gets the average elevation of entry and exit points.
    /// </summary>
    public float AverageEndpointElevation => (EntryElevation + ExitElevation) / 2f;

    /// <summary>
    /// Gets the elevation difference between entry and exit.
    /// Positive means exit is higher than entry.
    /// </summary>
    public float ElevationDelta => ExitElevation - EntryElevation;

    /// <summary>
    /// Gets the average grade (slope) of the structure as a percentage.
    /// </summary>
    public float AverageGradePercent => LengthMeters > 0
        ? Math.Abs(ElevationDelta) / LengthMeters * 100f
        : 0f;

    public override string ToString()
    {
        var type = IsBridge ? "Bridge" : IsTunnel ? "Tunnel" : "Unknown";
        return $"StructureElevationProfile[{type}] {CurveType}: " +
               $"Entry={EntryElevation:F1}m, Exit={ExitElevation:F1}m, " +
               $"Length={LengthMeters:F1}m, Grade={AverageGradePercent:F1}%";
    }
}
