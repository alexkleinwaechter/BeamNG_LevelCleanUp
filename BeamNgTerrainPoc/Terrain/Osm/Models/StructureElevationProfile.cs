namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Types of elevation curves for bridge and tunnel structures.
/// Each curve type represents a different vertical profile shape.
/// </summary>
public enum StructureElevationCurveType
{
    /// <summary>
    /// Flat profile - constant grade from entry to exit.
    /// Used for short bridges/tunnels where a simple straight line works.
    /// </summary>
    Linear,

    /// <summary>
    /// Smooth parabolic curve (sag or crest).
    /// Used for medium-length bridges where drainage requires a slight sag.
    /// </summary>
    Parabolic,

    /// <summary>
    /// S-curve for tunnels - descent, level, ascent.
    /// Used when tunnels need to dip below terrain to maintain clearance.
    /// </summary>
    SCurve,

    /// <summary>
    /// Symmetric arch for long bridges.
    /// Used for cable-stayed or suspension bridges where the center is elevated.
    /// </summary>
    Arch
}

/// <summary>
/// Defines the elevation profile for a bridge or tunnel structure.
/// This profile determines the vertical positioning of the structure independent
/// of the underlying terrain, enabling proper elevation transitions and clearances.
/// </summary>
/// <remarks>
/// <para>
/// Bridge profiles typically have:
/// - Short bridges (&lt;50m): Linear profile between entry/exit
/// - Medium bridges (50-200m): Slight sag curve for drainage
/// - Long bridges (&gt;200m): Arch profile for cable-stayed/suspension designs
/// </para>
/// <para>
/// Tunnel profiles must ensure:
/// - Adequate clearance below terrain surface
/// - Achievable grades for vehicle traffic
/// - Smooth transitions at portals
/// </para>
/// </remarks>
public class StructureElevationProfile
{
    /// <summary>
    /// Entry point elevation in meters (where structure meets road at start).
    /// This elevation should match the connecting road's elevation at the junction.
    /// </summary>
    public float EntryElevation { get; set; }

    /// <summary>
    /// Exit point elevation in meters (where structure meets road at end).
    /// This elevation should match the connecting road's elevation at the junction.
    /// </summary>
    public float ExitElevation { get; set; }

    /// <summary>
    /// Total length of the structure in meters.
    /// Used to calculate normalized position along the structure (0.0 to 1.0).
    /// </summary>
    public float LengthMeters { get; set; }

    /// <summary>
    /// Type of elevation curve to use.
    /// Determines how elevation is interpolated between entry and exit points.
    /// </summary>
    public StructureElevationCurveType CurveType { get; set; } = StructureElevationCurveType.Linear;

    /// <summary>
    /// Minimum vertical clearance in meters.
    /// <list type="bullet">
    /// <item>For tunnels: minimum distance from terrain surface to tunnel ceiling (default 5m).</item>
    /// <item>For bridges: minimum clearance above obstacle (water, road, etc.).</item>
    /// </list>
    /// </summary>
    public float MinimumClearanceMeters { get; set; } = 5.0f;

    /// <summary>
    /// Terrain elevations sampled along the structure centerline.
    /// These samples are used for tunnel depth calculations to ensure
    /// the tunnel maintains adequate clearance below terrain at all points.
    /// The array index corresponds to normalized distance (0 to N-1 maps to 0.0 to 1.0).
    /// </summary>
    public float[]? TerrainElevationsAlongPath { get; set; }

    /// <summary>
    /// Calculated lowest point elevation for the structure in meters.
    /// <list type="bullet">
    /// <item>For tunnels: ensures this is at least <see cref="MinimumClearanceMeters"/> 
    /// plus tunnel height below terrain surface.</item>
    /// <item>For bridges: typically the lower of entry/exit elevations (or lowest sag point).</item>
    /// </list>
    /// </summary>
    public float CalculatedLowestPointElevation { get; set; }

    /// <summary>
    /// Calculated highest point elevation for the structure in meters.
    /// <list type="bullet">
    /// <item>For bridges with arch profiles: the peak of the arch.</item>
    /// <item>For linear profiles: the higher of entry/exit elevations.</item>
    /// </list>
    /// </summary>
    public float CalculatedHighestPointElevation { get; set; }

    /// <summary>
    /// Whether the profile calculation was successful and valid.
    /// If false, the structure may have conflicting constraints
    /// (e.g., required tunnel depth vs. max grade).
    /// </summary>
    public bool IsValid { get; set; } = true;

    /// <summary>
    /// Optional validation message explaining any issues with the profile.
    /// </summary>
    public string? ValidationMessage { get; set; }

    /// <summary>
    /// Maximum grade (slope) as a percentage (e.g., 6.0 for 6% grade).
    /// Used to validate that the profile doesn't exceed safe/comfortable grades.
    /// </summary>
    public float MaxGradePercent { get; set; }

    /// <summary>
    /// Gets the average grade of the structure as a percentage.
    /// Calculated as: (ExitElevation - EntryElevation) / LengthMeters * 100
    /// </summary>
    public float AverageGradePercent => 
        LengthMeters > 0 ? (ExitElevation - EntryElevation) / LengthMeters * 100f : 0f;

    /// <summary>
    /// Gets whether the structure has a significant elevation change.
    /// A threshold of 0.5% grade is used to determine if smoothing is needed.
    /// </summary>
    public bool HasSignificantElevationChange => Math.Abs(AverageGradePercent) > 0.5f;

    /// <summary>
    /// Creates a simple linear elevation profile for short structures.
    /// </summary>
    /// <param name="entryElevation">Elevation at the entry point in meters.</param>
    /// <param name="exitElevation">Elevation at the exit point in meters.</param>
    /// <param name="lengthMeters">Length of the structure in meters.</param>
    /// <returns>A linear elevation profile.</returns>
    public static StructureElevationProfile CreateLinear(
        float entryElevation,
        float exitElevation,
        float lengthMeters)
    {
        return new StructureElevationProfile
        {
            EntryElevation = entryElevation,
            ExitElevation = exitElevation,
            LengthMeters = lengthMeters,
            CurveType = StructureElevationCurveType.Linear,
            CalculatedLowestPointElevation = Math.Min(entryElevation, exitElevation),
            CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation),
            IsValid = true
        };
    }

    /// <summary>
    /// Creates a default profile when insufficient data is available.
    /// Uses linear interpolation with reasonable defaults.
    /// </summary>
    /// <param name="lengthMeters">Length of the structure in meters.</param>
    /// <param name="estimatedElevation">Best estimate of structure elevation in meters.</param>
    /// <returns>A default elevation profile.</returns>
    public static StructureElevationProfile CreateDefault(
        float lengthMeters,
        float estimatedElevation)
    {
        return new StructureElevationProfile
        {
            EntryElevation = estimatedElevation,
            ExitElevation = estimatedElevation,
            LengthMeters = lengthMeters,
            CurveType = StructureElevationCurveType.Linear,
            CalculatedLowestPointElevation = estimatedElevation,
            CalculatedHighestPointElevation = estimatedElevation,
            IsValid = true,
            ValidationMessage = "Default profile - no connecting road data available"
        };
    }

    /// <summary>
    /// Creates an S-curve elevation profile for tunnels that need to dip below terrain.
    /// The S-curve consists of: descent (25%), level (50%), ascent (25%) phases.
    /// </summary>
    /// <param name="entryElevation">Elevation at the tunnel entry in meters.</param>
    /// <param name="exitElevation">Elevation at the tunnel exit in meters.</param>
    /// <param name="lowestPointElevation">The lowest elevation in the level phase in meters.</param>
    /// <param name="lengthMeters">Length of the tunnel in meters.</param>
    /// <param name="terrainElevations">Optional terrain elevations along the path.</param>
    /// <returns>An S-curve elevation profile for the tunnel.</returns>
    public static StructureElevationProfile CreateSCurve(
        float entryElevation,
        float exitElevation,
        float lowestPointElevation,
        float lengthMeters,
        float[]? terrainElevations = null)
    {
        return new StructureElevationProfile
        {
            EntryElevation = entryElevation,
            ExitElevation = exitElevation,
            LengthMeters = lengthMeters,
            CurveType = StructureElevationCurveType.SCurve,
            CalculatedLowestPointElevation = lowestPointElevation,
            CalculatedHighestPointElevation = Math.Max(entryElevation, exitElevation),
            TerrainElevationsAlongPath = terrainElevations,
            IsValid = true
        };
    }

    /// <summary>
    /// Gets the total vertical depth change for tunnel S-curves.
    /// Measures from the higher of entry/exit to the lowest point.
    /// </summary>
    public float TotalDepthChange => CurveType == StructureElevationCurveType.SCurve
        ? Math.Max(EntryElevation, ExitElevation) - CalculatedLowestPointElevation
        : 0f;

    public override string ToString()
    {
        var curveInfo = CurveType switch
        {
            StructureElevationCurveType.Linear => "Linear",
            StructureElevationCurveType.Parabolic => $"Parabolic (sag)",
            StructureElevationCurveType.SCurve => $"S-Curve (lowest: {CalculatedLowestPointElevation:F1}m)",
            StructureElevationCurveType.Arch => $"Arch (peak: {CalculatedHighestPointElevation:F1}m)",
            _ => "Unknown"
        };

        return $"ElevationProfile [{curveInfo}]: {EntryElevation:F1}m -> {ExitElevation:F1}m over {LengthMeters:F0}m ({AverageGradePercent:F1}% grade)";
    }
}
