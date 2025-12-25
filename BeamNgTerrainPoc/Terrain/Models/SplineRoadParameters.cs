namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Parameters specific to Spline-based road smoothing approach.
/// This approach extracts road centerlines and creates smooth splines with perpendicular cross-sections.
/// Best for: Simple curved roads, racing circuits, highways WITHOUT complex intersections.
/// </summary>
public class SplineRoadParameters
{
    // ========================================
    // SPLINE INTERPOLATION TYPE
    // ========================================
    
    /// <summary>
    /// Controls how splines are interpolated between control points.
    /// SmoothInterpolated: Uses Akima/cubic spline for smooth curves
    /// LinearControlPoints: Uses linear interpolation for accurate source geometry adherence (default)
    /// Default: LinearControlPoints
    /// </summary>
    public SplineInterpolationType SplineInterpolationType { get; set; } = SplineInterpolationType.LinearControlPoints;
    
    // ========================================
    // SPLINE EXTRACTION & ORDERING
    // ========================================
    
    /// <summary>
    /// Use graph-based ordering instead of greedy nearest neighbor for skeleton paths.
    /// Graph-based is more robust for complex skeletons.
    /// Default: true
    /// </summary>
    public bool UseGraphOrdering { get; set; } = true;
    
    /// <summary>
    /// Maximum spacing (pixels) after densification. Larger gaps will be filled with intermediate points.
    /// Higher values = fewer control points = less sensitivity to skeleton noise = fewer spikes.
    /// Lower values = more control points = follows skeleton more closely = may amplify noise.
    /// Default: 2.0 (good balance between accuracy and spike prevention)
    /// </summary>
    public float DensifyMaxSpacingPixels { get; set; } = 2.0f;
    
    /// <summary>
    /// Maximum neighbor link distance (pixels) when building adjacency graph.
    /// Default: 2.5
    /// </summary>
    public float OrderingNeighborRadiusPixels { get; set; } = 2.5f;
    
    /// <summary>
    /// Maximum distance (pixels) to bridge gaps between skeleton endpoints.
    /// Helps connect nearly-touching road segments.
    /// Default: 30.0
    /// </summary>
    public float BridgeEndpointMaxDistancePixels { get; set; } = 30.0f;
    
    /// <summary>
    /// Tolerance for path simplification (in pixels). Lower values preserve more detail.
    /// 0 = no simplification (keeps all points)
    /// 1-2 = gentle simplification (removes minor jitter)
    /// 5+ = aggressive simplification (straighter paths)
    /// Default: 0.5
    /// </summary>
    public float SimplifyTolerancePixels { get; set; } = 0.5f;
    
    // ========================================
    // JUNCTION HANDLING
    // ========================================
    
    /// <summary>
    /// When true, prefer paths that continue straight through junctions rather than taking sharp turns.
    /// Helps extract main roads without following every branch at intersections.
    /// Default: false
    /// </summary>
    public bool PreferStraightThroughJunctions { get; set; } = false;
    
    /// <summary>
    /// Maximum angle change (in degrees) to consider a path "straight through" a junction.
    /// Only used when PreferStraightThroughJunctions is true.
    /// Default: 45.0
    /// </summary>
    public float JunctionAngleThreshold { get; set; } = 45.0f;
    
    /// <summary>
    /// Minimum path length (in pixels) to keep. Shorter paths are filtered out.
    /// Helps remove small fragments, parking lots, or driveways.
    /// Default: 20.0
    /// </summary>
    public float MinPathLengthPixels { get; set; } = 20.0f;
    
    // ========================================
    // SKELETONIZATION PREPROCESSING
    // ========================================
    
    /// <summary>
    /// Dilation radius (in pixels) applied to road mask before skeletonization.
    /// Helps bridge small gaps and improve connectivity.
    /// 
    /// 0 = no dilation (cleanest skeleton, may miss disconnected fragments)
    /// 1 = minimal dilation (RECOMMENDED - good balance, minimal tail artifacts)
    /// 2 = moderate dilation (better connectivity, minor blobs at curves)
    /// 3 = heavy dilation (maximum connectivity, SIGNIFICANT tail artifacts at hairpins)
    /// 
    /// Default: 1
    /// </summary>
    public int SkeletonDilationRadius { get; set; } = 1;
    
    // ========================================
    // SPLINE CURVE FITTING
    // ========================================
    
    /// <summary>
    /// Spline tension parameter (0-1). Controls how tightly spline follows control points.
    /// 0 = very loose, smooth (may deviate from path)
    /// 0.5 = balanced
    /// 1 = very tight (follows path closely but may be less smooth)
    /// Default: 0.3
    /// </summary>
    public float SplineTension { get; set; } = 0.3f;
    
    /// <summary>
    /// Spline continuity parameter (-1 to 1). Controls corner sharpness.
    /// -1 = sharp corners
    /// 0 = balanced
    /// 1 = very smooth corners
    /// Default: 0.5
    /// </summary>
    public float SplineContinuity { get; set; } = 0.5f;
    
    /// <summary>
    /// Spline bias parameter (-1 to 1). Controls curve direction bias.
    /// -1 = bias toward previous point
    /// 0 = neutral (symmetric)
    /// 1 = bias toward next point
    /// Default: 0.0
    /// </summary>
    public float SplineBias { get; set; } = 0.0f;
    
    // ========================================
    // ELEVATION SMOOTHING
    // ========================================
    
    /// <summary>
    /// Window size for elevation smoothing (number of cross-sections).
    /// Larger values create smoother elevation transitions along the road.
    /// Recommend: 101-301 for highway quality, 51-101 for local roads.
    /// Default: 101
    /// </summary>
    public int SmoothingWindowSize { get; set; } = 101;
    
    /// <summary>
    /// Use Butterworth low-pass filter instead of Gaussian for elevation smoothing.
    /// Butterworth provides maximally flat passband (smoother roads) with sharper cutoff.
    /// Recommended: true for professional highway quality
    /// Default: true
    /// </summary>
    public bool UseButterworthFilter { get; set; } = true;
    
    /// <summary>
    /// Butterworth filter order (higher = sharper cutoff, flatter passband).
    /// Range: 1-8
    /// 1-2 = gentle smoothing
    /// 3-4 = aggressive smoothing (recommended)
    /// 5-6 = maximum flatness (may introduce slight ringing)
    /// Default: 3
    /// </summary>
    public int ButterworthFilterOrder { get; set; } = 3;
    
    /// <summary>
    /// Strength of global road network leveling (0-1).
    /// 0   = DISABLED - roads follow local terrain (DEFAULT - RECOMMENDED)
    /// 0.3 = light leveling (gentle adjustment toward network average)
    /// 0.5 = moderate leveling (roads pulled halfway to average)
    /// 0.85 = strong leveling (roads mostly at same elevation)
    /// 
    /// ?? WARNING: Values > 0.5 require WIDER TerrainAffectedRangeMeters (20m+) to prevent dotted roads!
    /// </summary>
    public float GlobalLevelingStrength { get; set; } = 0.0f;
    
    // ========================================
    // DEBUG OUTPUT
    // ========================================
    
    /// <summary>
    /// Export spline debug image showing centerline, road width, and cross-sections.
    /// Useful for verifying spline extraction quality.
    /// Default: false
    /// </summary>
    public bool ExportSplineDebugImage { get; set; } = false;
    
    /// <summary>
    /// Export skeleton debug image (raw skeleton, ordered paths, densified points).
    /// Useful for debugging centerline extraction.
    /// Default: false
    /// </summary>
    public bool ExportSkeletonDebugImage { get; set; } = false;
    
    /// <summary>
    /// Export smoothed elevation debug image showing final calculated elevations color-coded.
    /// Blue = lowest, Red = highest elevations.
    /// Default: false
    /// </summary>
    public bool ExportSmoothedElevationDebugImage { get; set; } = false;
    
    /// <summary>
    /// Validates the spline-specific parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (DensifyMaxSpacingPixels <= 0)
            errors.Add("DensifyMaxSpacingPixels must be greater than 0");
            
        if (OrderingNeighborRadiusPixels < 1f)
            errors.Add("OrderingNeighborRadiusPixels must be at least 1");
            
        if (BridgeEndpointMaxDistancePixels < 0)
            errors.Add("BridgeEndpointMaxDistancePixels must be >= 0");
        
        if (JunctionAngleThreshold < 0 || JunctionAngleThreshold > 180)
            errors.Add("JunctionAngleThreshold must be between 0 and 180 degrees");
        
        if (MinPathLengthPixels < 0)
            errors.Add("MinPathLengthPixels must be >= 0");
        
        if (SimplifyTolerancePixels < 0)
            errors.Add("SimplifyTolerancePixels must be >= 0");
        
        if (SplineTension < 0 || SplineTension > 1)
            errors.Add("SplineTension must be between 0 and 1");
        
        if (SplineContinuity < -1 || SplineContinuity > 1)
            errors.Add("SplineContinuity must be between -1 and 1");
        
        if (SplineBias < -1 || SplineBias > 1)
            errors.Add("SplineBias must be between -1 and 1");
        
        if (SmoothingWindowSize < 1)
            errors.Add("SmoothingWindowSize must be at least 1");
        
        if (GlobalLevelingStrength < 0 || GlobalLevelingStrength > 1)
            errors.Add("GlobalLevelingStrength must be between 0 and 1");
        
        if (ButterworthFilterOrder < 1 || ButterworthFilterOrder > 8)
            errors.Add("ButterworthFilterOrder must be between 1 and 8");
        
        if (SkeletonDilationRadius < 0 || SkeletonDilationRadius > 5)
            errors.Add("SkeletonDilationRadius must be between 0 and 5");
            
        return errors;
    }
}
