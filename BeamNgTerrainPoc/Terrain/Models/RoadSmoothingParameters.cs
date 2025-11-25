namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Approach to use for road smoothing
/// </summary>
public enum RoadSmoothingApproach
{
    /// <summary>
    /// Direct road mask approach - simple, robust, works with intersections.
    /// Grid-aligned sampling (may have slight tilt on curves).
    /// Best for: Complex road networks, intersections, general use.
    /// </summary>
    DirectMask,
    
    /// <summary>
    /// Spline-based approach - uses centerline extraction and smooth splines.
    /// Perpendicular sampling (level on curves).
    /// Best for: Simple curved roads without intersections.
    /// Note: May fail on complex road networks!
    /// </summary>
    SplineBased
}

/// <summary>
/// Parameters for road smoothing algorithm applied to heightmaps.
/// Defines how roads should be flattened and blended into surrounding terrain.
/// </summary>
public class RoadSmoothingParameters
{
    /// <summary>
    /// Width of the road in meters.
    /// Default: 8.0 (typical 2-lane road)
    /// </summary>
    public float RoadWidthMeters { get; set; } = 8.0f;
    
    /// <summary>
    /// Distance from road edge to blend terrain in meters.
    /// This is the range where the road transitions smoothly into natural terrain.
    /// Default: 15.0
    /// </summary>
    public float TerrainAffectedRangeMeters { get; set; } = 15.0f;
    
    /// <summary>
    /// Maximum allowed road slope in degrees.
    /// Prevents unrealistic steepness on the road surface.
    /// Default: 8.0 (typical highway maximum)
    /// </summary>
    public float RoadMaxSlopeDegrees { get; set; } = 8.0f;
    
    /// <summary>
    /// Maximum slope for embankments/sides in degrees.
    /// Controls how sharply the terrain transitions from road to environment.
    /// Default: 30.0 (stable embankment)
    /// </summary>
    public float SideMaxSlopeDegrees { get; set; } = 30.0f;
    
    /// <summary>
    /// Paths to layer maps for areas to exclude from road smoothing (e.g., water, bridges).
    /// White pixels (255) in these layers indicate areas where road smoothing should NOT occur.
    /// </summary>
    public List<string>? ExclusionLayerPaths { get; set; }
    
    /// <summary>
    /// Distance between cross-section samples in meters.
    /// Smaller values = more accurate but slower processing.
    /// Default: 2.0
    /// </summary>
    public float CrossSectionIntervalMeters { get; set; } = 2.0f;
    
    /// <summary>
    /// Window size for longitudinal smoothing in meters.
    /// Affects how smooth the road is along its length.
    /// Default: 20.0
    /// </summary>
    public float LongitudinalSmoothingWindowMeters { get; set; } = 20.0f;
    
    /// <summary>
    /// Type of blend function to use for terrain transitions.
    /// Default: Cosine (smoothest)
    /// </summary>
    public BlendFunctionType BlendFunctionType { get; set; } = BlendFunctionType.Cosine;
    
    /// <summary>
    /// Which approach to use for road smoothing
    /// Default: DirectMask (most robust)
    /// </summary>
    public RoadSmoothingApproach Approach { get; set; } = RoadSmoothingApproach.DirectMask;
    
    /// <summary>
    /// If false, skip terrain blending (debug mode: only extract geometry/elevations)
    /// </summary>
    public bool EnableTerrainBlending { get; set; } = true;
    
    /// <summary>
    /// Export spline debug image (only for SplineBased). Shows centerline and road width.
    /// </summary>
    public bool ExportSplineDebugImage { get; set; } = false;
    
    /// <summary>
    /// Optional output directory for debug images. If null or empty uses working directory.
    /// </summary>
    public string? DebugOutputDirectory { get; set; }
    
    // DEBUG OPTIONS
    /// <summary>Export a skeleton debug image (raw skeleton, ordered path, densified points)</summary>
    public bool ExportSkeletonDebugImage { get; set; } = false;
    /// <summary>Use graph-based ordering instead of greedy nearest neighbor.</summary>
    public bool UseGraphOrdering { get; set; } = true;
    /// <summary>Maximum spacing (pixels) after densification. Larger gaps will be filled with intermediate points.</summary>
    public float DensifyMaxSpacingPixels { get; set; } = 1.0f;
    /// <summary>Maximum neighbor link distance (pixels) allowed when building adjacency graph.</summary>
    public float OrderingNeighborRadiusPixels { get; set; } = 2.5f;
    /// <summary>Maximum distance (pixels) to bridge gaps between skeleton endpoints.</summary>
    public float BridgeEndpointMaxDistancePixels { get; set; } = 4.0f;
    
    /// <summary>
    /// Exports a second spline debug image showing the final calculated elevations color-coded.
    /// Blue = lowest, Red = highest elevations.
    /// </summary>
    public bool ExportSmoothedElevationDebugImage { get; set; } = false;
    
    /// <summary>
    /// Validates the parameters and returns any errors.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (RoadWidthMeters <= 0)
            errors.Add("RoadWidthMeters must be greater than 0");
            
        if (TerrainAffectedRangeMeters < 0)
            errors.Add("TerrainAffectedRangeMeters must be greater than or equal to 0");
            
        if (RoadMaxSlopeDegrees < 0 || RoadMaxSlopeDegrees > 90)
            errors.Add("RoadMaxSlopeDegrees must be between 0 and 90");
            
        if (SideMaxSlopeDegrees < 0 || SideMaxSlopeDegrees > 90)
            errors.Add("SideMaxSlopeDegrees must be between 0 and 90");
            
        if (CrossSectionIntervalMeters <= 0)
            errors.Add("CrossSectionIntervalMeters must be greater than 0");
            
        if (LongitudinalSmoothingWindowMeters <= 0)
            errors.Add("LongitudinalSmoothingWindowMeters must be greater than 0");
        
        if (DensifyMaxSpacingPixels <= 0)
            errors.Add("DensifyMaxSpacingPixels must be greater than 0");
            
        if (OrderingNeighborRadiusPixels < 1f)
            errors.Add("OrderingNeighborRadiusPixels must be at least 1");
            
        if (BridgeEndpointMaxDistancePixels < 0)
            errors.Add("BridgeEndpointMaxDistancePixels must be greater than or equal to 0");
            
        return errors;
    }
}
