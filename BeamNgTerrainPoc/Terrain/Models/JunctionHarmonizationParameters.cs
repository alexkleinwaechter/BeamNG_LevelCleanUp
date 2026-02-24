namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Parameters for junction and endpoint elevation harmonization.
///     Controls how road elevations are blended at intersections and endpoints
///     to eliminate discontinuities.
/// </summary>
public class JunctionHarmonizationParameters
{
    // ========================================
    // GLOBAL/PER-MATERIAL SETTINGS
    // ========================================

    /// <summary>
    ///     When true, uses global junction settings from TerrainCreationParameters.
    ///     When false, uses the values specified in this instance.
    ///     This only affects JunctionDetectionRadiusMeters and JunctionBlendDistanceMeters.
    ///     Other settings (blend function, endpoint taper, etc.) are always per-material.
    ///     Default: true (use global settings)
    /// </summary>
    public bool UseGlobalSettings { get; set; } = true;

    // ========================================
    // MASTER ENABLE
    // ========================================

    /// <summary>
    ///     Enable junction elevation harmonization.
    ///     When enabled, road elevations at intersections and endpoints are smoothed
    ///     to eliminate discontinuities.
    ///     Default: true
    /// </summary>
    public bool EnableJunctionHarmonization { get; set; } = true;

    // ========================================
    // JUNCTION DETECTION
    // ========================================

    /// <summary>
    ///     Maximum distance (in meters) between a path endpoint and another road to detect a junction.
    ///     This should be small - just enough to account for the road width + small tolerance.
    ///     For T-junctions: An endpoint touching the side of another road will be detected
    ///     if the distance is within this radius.
    ///     Typical values:
    ///     - 5-8m: Narrow roads (single lane)
    ///     - 8-12m: Standard roads (DEFAULT - covers ~8m road width + tolerance)
    ///     - 12-15m: Wide roads (highways)
    ///     Default: 10.0
    /// </summary>
    public float JunctionDetectionRadiusMeters { get; set; } = 5.0f;

    // ========================================
    // JUNCTION BLENDING
    // ========================================

    /// <summary>
    ///     Distance (in meters) over which to blend from junction elevation back to path elevation.
    ///     This affects the SIDE ROAD that joins the main road - the side road's elevation
    ///     will smoothly transition from the main road's elevation back to its own calculated elevation.
    ///     Typical values:
    ///     - 15-25m: Tight blending (urban roads)
    ///     - 25-40m: Standard blending (DEFAULT)
    ///     - 40-60m: Smooth blending (highways)
    ///     Default: 30.0
    /// </summary>
    public float JunctionBlendDistanceMeters { get; set; } = 30.0f;

    /// <summary>
    ///     Blend function type for junction transitions.
    ///     Default: Cosine (smooth S-curve)
    /// </summary>
    public JunctionBlendFunctionType BlendFunctionType { get; set; } = JunctionBlendFunctionType.Cosine;

    // ========================================
    // ROUNDABOUT SETTINGS
    // ========================================

    /// <summary>
    ///     When true, automatically detect and handle roundabouts from OSM data.
    ///     Roundabout segments (tagged with junction=roundabout) are merged into
    ///     single ring splines, and connecting roads form T-junctions with the ring.
    ///     Default: true
    /// </summary>
    public bool EnableRoundaboutDetection { get; set; } = true;

    /// <summary>
    ///     When true, automatically trim connecting roads that overlap with roundabout rings.
    ///     This removes the high-angle segments that create quirky splines and elevation spikes.
    ///     Problem: OSM roads often share multiple nodes with roundabouts, creating:
    ///     - High-angle turns where the road follows the circular path
    ///     - Weird elevation changes
    ///     - Quirky spline geometry with bumps and jumps
    ///     Solution: Cut roads at the FIRST point where they touch the roundabout
    ///     and delete the portion that overlaps with/follows the ring.
    ///     STRONGLY RECOMMENDED to keep enabled.
    ///     Default: true
    /// </summary>
    public bool EnableRoundaboutRoadTrimming { get; set; } = true;

    /// <summary>
    ///     Detection radius for roundabout connections (in meters).
    ///     Roads within this distance of a roundabout ring are considered connected.
    ///     Typical values:
    ///     - 5-8m: Tight detection (may miss some connections)
    ///     - 8-12m: Standard detection (DEFAULT)
    ///     - 12-15m: Loose detection (may catch unrelated roads)
    ///     Default: 10.0
    /// </summary>
    public float RoundaboutConnectionRadiusMeters { get; set; } = 10.0f;

    /// <summary>
    ///     Tolerance for determining if a road point is "on" the roundabout ring (in meters).
    ///     Points within this distance of the ring radius are considered overlapping
    ///     and will be trimmed when EnableRoundaboutRoadTrimming is true.
    ///     Typical values:
    ///     - 1.0m: Tight tolerance (only trim points very close to the ring)
    ///     - 2.0m: Standard tolerance (DEFAULT)
    ///     - 3.0m: Loose tolerance (more aggressive trimming)
    ///     Default: 2.0
    /// </summary>
    public float RoundaboutOverlapToleranceMeters { get; set; } = 2.0f;

    /// <summary>
    ///     When true, force uniform elevation around roundabout rings.
    ///     The elevation is calculated as the weighted average of terrain elevation
    ///     at the ring position and connecting road elevations. All connecting roads
    ///     are blended toward this single elevation, which may cause artificial
    ///     bumps or dips for roads that naturally approach at different elevations.
    ///     When false, allow gradual elevation changes around the ring following
    ///     the natural terrain. Each connecting road blends toward the local ring
    ///     elevation at its specific connection point, avoiding artificial elevation
    ///     changes. This is more appropriate for roundabouts on sloped terrain.
    ///     Default: true
    /// </summary>
    public bool ForceUniformRoundaboutElevation { get; set; } = true;

    /// <summary>
    ///     Distance (in meters) over which to blend connecting road elevation toward the roundabout ring.
    ///     This controls how smoothly roads transition to match the roundabout's uniform elevation.
    ///     If not set (null), uses JunctionBlendDistanceMeters as the default.
    ///     Capped at 75% of road length to avoid affecting the far end of the road.
    ///     Typical values:
    ///     - 25-35m: Tight transition (urban roundabouts)
    ///     - 40-60m: Standard transition (DEFAULT)
    ///     - 60-100m: Smooth transition (highway roundabouts, sloped terrain)
    ///     Default: 50.0
    /// </summary>
    public float? RoundaboutBlendDistanceMeters { get; set; } = 50.0f;

    /// <summary>
    ///     Gets the effective roundabout blend distance.
    ///     Returns RoundaboutBlendDistanceMeters if set, otherwise JunctionBlendDistanceMeters.
    /// </summary>
    public float EffectiveRoundaboutBlendDistanceMeters =>
        RoundaboutBlendDistanceMeters ?? JunctionBlendDistanceMeters;

    // ========================================
    // DEBUG OPTIONS
    // All debug images are always exported to the MT_TerrainGeneration folder.
    // ========================================

    /// <summary>
    ///     Export debug image showing detected junctions and blend zones.
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportJunctionDebugImage { get; set; } = true;

    /// <summary>
    ///     Export debug image showing roundabout detection and road trimming.
    ///     The debug image shows:
    ///     - Original road paths in gray (semi-transparent) for comparison
    ///     - Roundabout rings in yellow
    ///     - Connection/trim points marked with circles (white outline, green fill)
    ///     - Trimmed/deleted road portions in red
    ///     - Connecting roads (after trimming) in cyan
    ///     - Roundabout centers marked with crosshairs
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportRoundaboutDebugImage { get; set; } = true;

    /// <summary>
    ///     Validates the junction harmonization parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (JunctionDetectionRadiusMeters <= 0)
            errors.Add("JunctionDetectionRadiusMeters must be greater than 0");

        if (JunctionBlendDistanceMeters <= 0)
            errors.Add("JunctionBlendDistanceMeters must be greater than 0");

        if (RoundaboutConnectionRadiusMeters <= 0)
            errors.Add("RoundaboutConnectionRadiusMeters must be greater than 0");

        if (RoundaboutOverlapToleranceMeters <= 0)
            errors.Add("RoundaboutOverlapToleranceMeters must be greater than 0");

        return errors;
    }
}

/// <summary>
///     Type of blend function for junction transitions.
/// </summary>
public enum JunctionBlendFunctionType
{
    /// <summary>
    ///     Linear interpolation - simple but may have visible transition points.
    /// </summary>
    Linear,

    /// <summary>
    ///     Cosine interpolation - smooth S-curve, good balance of smoothness and performance.
    /// </summary>
    Cosine,

    /// <summary>
    ///     Cubic Hermite (smoothstep) - very smooth with zero first derivative at endpoints.
    /// </summary>
    Cubic,

    /// <summary>
    ///     Quintic (smootherstep) - extremely smooth with zero first and second derivatives.
    ///     Best quality but slightly more computation.
    /// </summary>
    Quintic
}