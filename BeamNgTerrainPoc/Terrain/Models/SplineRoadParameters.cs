namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Parameters specific to Spline-based road smoothing approach.
///     This approach extracts road centerlines and creates smooth splines with perpendicular cross-sections.
///     Best for: Simple curved roads, racing circuits, highways WITHOUT complex intersections.
/// </summary>
public class SplineRoadParameters
{
    // ========================================
    // BANKING (SUPERELEVATION) PARAMETERS
    // ========================================

    /// <summary>
    ///     Banking (superelevation) parameters for curved roads.
    ///     Null = banking disabled.
    /// </summary>
    public BankingParameters? Banking { get; set; }

    // ========================================
    // SPLINE INTERPOLATION TYPE
    // ========================================

    /// <summary>
    ///     Controls how splines are interpolated between control points.
    ///     SmoothInterpolated: Uses Akima/cubic spline for smooth curves (default, best for PNG skeleton extraction)
    ///     LinearControlPoints: Uses linear interpolation for accurate source geometry adherence (best for OSM vector data)
    ///     Default: SmoothInterpolated (PNG skeleton-extracted paths benefit from smooth interpolation to reduce jagged edges)
    /// </summary>
    public SplineInterpolationType SplineInterpolationType { get; set; } = SplineInterpolationType.SmoothInterpolated;

    // ========================================
    // SPLINE EXTRACTION & ORDERING
    // ========================================

    /// <summary>
    ///     Maximum spacing (pixels) after densification. Larger gaps will be filled with intermediate points.
    ///     Higher values = fewer control points = less sensitivity to skeleton noise = fewer spikes.
    ///     Lower values = more control points = follows skeleton more closely = may amplify noise.
    ///     Default: 1.5 (better path following accuracy for PNG sources)
    /// </summary>
    public float DensifyMaxSpacingPixels { get; set; } = 1.5f;

    /// <summary>
    ///     Maximum distance (pixels) to bridge gaps between skeleton endpoints.
    ///     Helps connect nearly-touching road segments.
    ///     Default: 30.0
    /// </summary>
    public float BridgeEndpointMaxDistancePixels { get; set; } = 40.0f;

    /// <summary>
    ///     Tolerance for path simplification (in pixels). Lower values preserve more detail.
    ///     0 = no simplification (keeps all points)
    ///     1-2 = gentle simplification (removes minor jitter)
    ///     5+ = aggressive simplification (straighter paths)
    ///     Default: 0.5
    /// </summary>
    public float SimplifyTolerancePixels { get; set; } = 0.5f;

    // ========================================
    // JUNCTION HANDLING
    // ========================================

    /// <summary>
    ///     When true, prefer paths that continue straight through junctions rather than taking sharp turns.
    ///     Helps extract main roads without following every branch at intersections.
    ///     Default: false
    /// </summary>
    public bool PreferStraightThroughJunctions { get; set; } = false;

    /// <summary>
    ///     Maximum angle change (in degrees) to consider a path "straight through" a junction.
    ///     Only used when PreferStraightThroughJunctions is true.
    ///     Default: 45.0
    /// </summary>
    public float JunctionAngleThreshold { get; set; } = 90.0f;

    /// <summary>
    ///     Minimum path length (in pixels) to keep. Shorter paths are filtered out.
    ///     Helps remove small fragments, parking lots, or driveways.
    ///     Default: 20.0
    /// </summary>
    public float MinPathLengthPixels { get; set; } = 0f;

    // ========================================
    // SKELETONIZATION PREPROCESSING
    // ========================================

    /// <summary>
    ///     Dilation radius (in pixels) applied to road mask before skeletonization.
    ///     Helps bridge small gaps and improve connectivity.
    ///     0 = no dilation (cleanest skeleton, may miss disconnected fragments)
    ///     1 = minimal dilation (RECOMMENDED - good balance, minimal tail artifacts)
    ///     2 = moderate dilation (better connectivity, minor blobs at curves)
    ///     3 = heavy dilation (maximum connectivity, SIGNIFICANT tail artifacts at hairpins)
    ///     Default: 1
    /// </summary>
    public int SkeletonDilationRadius { get; set; } = 1;

    // ========================================
    // ELEVATION SMOOTHING
    // ========================================

    /// <summary>
    ///     Window size for elevation smoothing (number of cross-sections).
    ///     Larger values create smoother elevation transitions along the road.
    ///     Recommend: 101-301 for highway quality, 51-101 for local roads.
    ///     Default: 101
    /// </summary>
    public int SmoothingWindowSize { get; set; } = 301;

    /// <summary>
    ///     Use Butterworth low-pass filter instead of Gaussian for elevation smoothing.
    ///     Butterworth provides maximally flat passband (smoother roads) with sharper cutoff.
    ///     Recommended: true for professional highway quality
    ///     Default: true
    /// </summary>
    public bool UseButterworthFilter { get; set; } = true;

    /// <summary>
    ///     Butterworth filter order (higher = sharper cutoff, flatter passband).
    ///     Range: 1-8
    ///     1-2 = gentle smoothing
    ///     3-4 = aggressive smoothing (recommended)
    ///     5-6 = maximum flatness (may introduce slight ringing)
    ///     Default: 3
    /// </summary>
    public int ButterworthFilterOrder { get; set; } = 4;

    /// <summary>
    ///     Strength of global road network leveling (0-1).
    ///     0   = DISABLED - roads follow local terrain (DEFAULT - RECOMMENDED)
    ///     0.3 = light leveling (gentle adjustment toward network average)
    ///     0.5 = moderate leveling (roads pulled halfway to average)
    ///     0.85 = strong leveling (roads mostly at same elevation)
    ///     ?? WARNING: Values > 0.5 require WIDER TerrainAffectedRangeMeters (20m+) to prevent dotted roads!
    /// </summary>
    public float GlobalLevelingStrength { get; set; } = 0.0f;

    // ========================================
    // DEBUG OUTPUT
    // All debug images are always exported to the MT_TerrainGeneration folder.
    // ========================================

    /// <summary>
    ///     Export spline debug image showing centerline, road width, and cross-sections.
    ///     Useful for verifying spline extraction quality.
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportSplineDebugImage { get; set; } = true;

    /// <summary>
    ///     Export skeleton debug image (raw skeleton, ordered paths, densified points).
    ///     Useful for debugging centerline extraction.
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportSkeletonDebugImage { get; set; } = true;

    /// <summary>
    ///     Export smoothed elevation debug image showing final calculated elevations color-coded.
    ///     Blue = lowest, Red = highest elevations.
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportSmoothedElevationDebugImage { get; set; } = true;

    /// <summary>
    ///     Gets banking parameters, creating defaults if not set.
    /// </summary>
    public BankingParameters GetBankingParameters()
    {
        return Banking ??= new BankingParameters();
    }

    /// <summary>
    ///     Validates the spline-specific parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (DensifyMaxSpacingPixels <= 0)
            errors.Add("DensifyMaxSpacingPixels must be greater than 0");

        if (BridgeEndpointMaxDistancePixels < 0)
            errors.Add("BridgeEndpointMaxDistancePixels must be >= 0");

        if (JunctionAngleThreshold < 0 || JunctionAngleThreshold > 180)
            errors.Add("JunctionAngleThreshold must be between 0 and 180 degrees");

        if (MinPathLengthPixels < 0)
            errors.Add("MinPathLengthPixels must be >= 0");

        if (SimplifyTolerancePixels < 0)
            errors.Add("SimplifyTolerancePixels must be >= 0");

        if (SmoothingWindowSize < 1)
            errors.Add("SmoothingWindowSize must be at least 1");

        if (GlobalLevelingStrength < 0 || GlobalLevelingStrength > 1)
            errors.Add("GlobalLevelingStrength must be between 0 and 1");

        if (ButterworthFilterOrder < 1 || ButterworthFilterOrder > 8)
            errors.Add("ButterworthFilterOrder must be between 1 and 8");

        if (SkeletonDilationRadius < 0 || SkeletonDilationRadius > 5)
            errors.Add("SkeletonDilationRadius must be between 0 and 5");

        // Validate banking parameters if set
        if (Banking != null) errors.AddRange(Banking.Validate());

        return errors;
    }
}