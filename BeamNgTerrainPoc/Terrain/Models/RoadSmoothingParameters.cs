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
    /// Best for: Simple curved roads, highways, racing circuits WITHOUT complex intersections.
    /// Note: May fail on complex road networks!
    /// </summary>
    SplineBased,
    
    /// <summary>
    /// OPTIMIZED distance field approach - RECOMMENDED for smooth roads.
    /// Uses global Euclidean Distance Transform (EDT) + analytical blending + O(N) elevation smoothing.
    /// 
    /// PERFORMANCE: ~15x faster than old upsampling approach (3s vs 45s for 4096x4096).
    /// ALGORITHM:
    /// - Exact EDT (Felzenszwalb & Huttenlocher) in O(W*H)
    /// - Prefix-sum elevation smoothing in O(N) instead of O(N*W)
    /// - No per-pixel cross-section iteration
    /// 
    /// QUALITY: Eliminates jagged edges, stairs, blocky artifacts.
    /// Best for: All smooth road scenarios - highways, racing circuits, curved roads.
    /// Produces professional results faster than SplineBased.
    /// </summary>
    ImprovedSpline
}

/// <summary>
/// Main parameters for road smoothing algorithm applied to heightmaps.
/// Contains COMMON parameters for all approaches, plus approach-specific sub-parameters.
/// </summary>
public class RoadSmoothingParameters
{
    // ========================================
    // APPROACH SELECTION
    // ========================================
    
    /// <summary>
    /// Which approach to use for road smoothing.
    /// Default: DirectMask (most robust, handles intersections)
    /// </summary>
    public RoadSmoothingApproach Approach { get; set; } = RoadSmoothingApproach.DirectMask;
    
    // ========================================
    // APPROACH-SPECIFIC PARAMETERS
    // ========================================
    
    /// <summary>
    /// Spline-specific parameters (only used when Approach = SplineBased).
    /// Null = use defaults. Set this to customize spline extraction and smoothing.
    /// </summary>
    public SplineRoadParameters? SplineParameters { get; set; }
    
    /// <summary>
    /// DirectMask-specific parameters (only used when Approach = DirectMask).
    /// Null = use defaults. Set this to customize direct mask sampling.
    /// </summary>
    public DirectMaskRoadParameters? DirectMaskParameters { get; set; }
    
    // ========================================
    // COMMON ROAD GEOMETRY (All Approaches)
    // ========================================
    
    /// <summary>
    /// Width of the road surface in meters.
    /// This is the area that will be completely flattened to target elevation.
    /// Default: 8.0 (typical 2-lane road)
    /// </summary>
    public float RoadWidthMeters { get; set; } = 8.0f;
    
    /// <summary>
    /// Distance from road edge to blend terrain in meters.
    /// This creates the embankment/transition zone between road and natural terrain.
    /// 
    /// Total terrain impact width = RoadWidthMeters + (TerrainAffectedRangeMeters × 2)
    /// Example: 8m road + (12m × 2) = 32m total width
    /// 
    /// Typical values:
    /// - 8-12m: Narrow mountain road (tight integration)
    /// - 12-15m: Standard highway (realistic)
    /// - 20-30m: Wide highway or when using high GlobalLevelingStrength
    /// 
    /// Default: 12.0
    /// </summary>
    public float TerrainAffectedRangeMeters { get; set; } = 12.0f;
    
    /// <summary>
    /// Distance between cross-section samples in meters.
    /// Smaller values = more accurate but slower processing.
    /// 
    /// IMPORTANT: Should be ? (RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3 to avoid gaps!
    /// 
    /// Typical values:
    /// - 0.25-0.5m: Ultra-high detail (racing circuits)
    /// - 0.5-1.0m: High detail (highways)
    /// - 1.0-2.0m: Standard detail (local roads)
    /// 
    /// Default: 0.5
    /// </summary>
    public float CrossSectionIntervalMeters { get; set; } = 0.5f;
    
    /// <summary>
    /// Window size for longitudinal smoothing in meters.
    /// Affects how smooth the road is along its length direction.
    /// Default: 20.0
    /// </summary>
    public float LongitudinalSmoothingWindowMeters { get; set; } = 20.0f;
    
    // ========================================
    // SLOPE CONSTRAINTS (All Approaches)
    // ========================================
    
    /// <summary>
    /// Maximum allowed road surface slope in degrees.
    /// Prevents unrealistic steepness on the road itself.
    /// 
    /// Typical values:
    /// - 1-2°: Racing circuit (ultra-flat)
    /// - 4-6°: Highway standard
    /// - 8-10°: Mountain road (steep but driveable)
    /// 
    /// Default: 4.0
    /// </summary>
    public float RoadMaxSlopeDegrees { get; set; } = 4.0f;
    
    /// <summary>
    /// Maximum slope for embankments/sides in degrees.
    /// Controls how sharply terrain transitions from road edge to natural terrain.
    /// 
    /// Typical values:
    /// - 20-25°: Gentle embankment (1:2.5 ratio)
    /// - 30°: Standard embankment (1:1.7 ratio)
    /// - 35-40°: Steep embankment (1:1.2 ratio)
    /// 
    /// Default: 30.0
    /// </summary>
    public float SideMaxSlopeDegrees { get; set; } = 30.0f;
    
    // ========================================
    // BLENDING (All Approaches)
    // ========================================
    
    /// <summary>
    /// Type of blend function to use for terrain transitions.
    /// Default: Cosine (smoothest)
    /// </summary>
    public BlendFunctionType BlendFunctionType { get; set; } = BlendFunctionType.Cosine;
    
    /// <summary>
    /// If false, skip terrain blending (debug mode: only extract geometry/elevations).
    /// Default: true
    /// </summary>
    public bool EnableTerrainBlending { get; set; } = true;
    
    // ========================================
    // EXCLUSION ZONES (All Approaches)
    // ========================================
    
    /// <summary>
    /// Paths to layer maps for areas to exclude from road smoothing.
    /// White pixels (255) in these layers indicate areas where smoothing should NOT occur.
    /// </summary>
    public List<string>? ExclusionLayerPaths { get; set; }
    
    // ========================================
    // DEBUG OUTPUT (All Approaches)
    // ========================================
    
    /// <summary>
    /// Optional output directory for debug images. If null uses working directory.
    /// </summary>
    public string? DebugOutputDirectory { get; set; }
    
    // ========================================
    // BACKWARD COMPATIBILITY PROPERTIES
    // THESE PROVIDE DIRECT ACCESS TO SUB-PARAMETERS FOR SIMPLER API
    // ========================================
    
    #region Backward Compatibility - Spline Properties
    
    public bool UseGraphOrdering
    {
        get => GetSplineParameters().UseGraphOrdering;
        set => GetSplineParameters().UseGraphOrdering = value;
    }
    
    public float DensifyMaxSpacingPixels
    {
        get => GetSplineParameters().DensifyMaxSpacingPixels;
        set => GetSplineParameters().DensifyMaxSpacingPixels = value;
    }
    
    public float OrderingNeighborRadiusPixels
    {
        get => GetSplineParameters().OrderingNeighborRadiusPixels;
        set => GetSplineParameters().OrderingNeighborRadiusPixels = value;
    }
    
    public float BridgeEndpointMaxDistancePixels
    {
        get => GetSplineParameters().BridgeEndpointMaxDistancePixels;
        set => GetSplineParameters().BridgeEndpointMaxDistancePixels = value;
    }
    
    public bool PreferStraightThroughJunctions
    {
        get => GetSplineParameters().PreferStraightThroughJunctions;
        set => GetSplineParameters().PreferStraightThroughJunctions = value;
    }
    
    public float JunctionAngleThreshold
    {
        get => GetSplineParameters().JunctionAngleThreshold;
        set => GetSplineParameters().JunctionAngleThreshold = value;
    }
    
    public float MinPathLengthPixels
    {
        get => GetSplineParameters().MinPathLengthPixels;
        set => GetSplineParameters().MinPathLengthPixels = value;
    }
    
    public float SimplifyTolerancePixels
    {
        get => GetSplineParameters().SimplifyTolerancePixels;
        set => GetSplineParameters().SimplifyTolerancePixels = value;
    }
    
    public float SplineTension
    {
        get => GetSplineParameters().SplineTension;
        set => GetSplineParameters().SplineTension = value;
    }
    
    public float SplineContinuity
    {
        get => GetSplineParameters().SplineContinuity;
        set => GetSplineParameters().SplineContinuity = value;
    }
    
    public float SplineBias
    {
        get => GetSplineParameters().SplineBias;
        set => GetSplineParameters().SplineBias = value;
    }
    
    public int SmoothingWindowSize
    {
        get => GetSplineParameters().SmoothingWindowSize;
        set => GetSplineParameters().SmoothingWindowSize = value;
    }
    
    public bool UseButterworthFilter
    {
        get => Approach == RoadSmoothingApproach.SplineBased 
            ? GetSplineParameters().UseButterworthFilter 
            : GetDirectMaskParameters().UseButterworthFilter;
        set
        {
            if (Approach == RoadSmoothingApproach.SplineBased)
                GetSplineParameters().UseButterworthFilter = value;
            else
                GetDirectMaskParameters().UseButterworthFilter = value;
        }
    }
    
    public int ButterworthFilterOrder
    {
        get => Approach == RoadSmoothingApproach.SplineBased 
            ? GetSplineParameters().ButterworthFilterOrder 
            : GetDirectMaskParameters().ButterworthFilterOrder;
        set
        {
            if (Approach == RoadSmoothingApproach.SplineBased)
                GetSplineParameters().ButterworthFilterOrder = value;
            else
                GetDirectMaskParameters().ButterworthFilterOrder = value;
        }
    }
    
    public float GlobalLevelingStrength
    {
        get => GetSplineParameters().GlobalLevelingStrength;
        set => GetSplineParameters().GlobalLevelingStrength = value;
    }
    
    public bool ExportSplineDebugImage
    {
        get => GetSplineParameters().ExportSplineDebugImage;
        set => GetSplineParameters().ExportSplineDebugImage = value;
    }
    
    public bool ExportSkeletonDebugImage
    {
        get => GetSplineParameters().ExportSkeletonDebugImage;
        set => GetSplineParameters().ExportSkeletonDebugImage = value;
    }
    
    public bool ExportSmoothedElevationDebugImage
    {
        get => GetSplineParameters().ExportSmoothedElevationDebugImage;
        set => GetSplineParameters().ExportSmoothedElevationDebugImage = value;
    }
    
    #endregion
    
    #region Backward Compatibility - DirectMask Properties
    
    public int RoadPixelSearchRadius
    {
        get => GetDirectMaskParameters().RoadPixelSearchRadius;
        set => GetDirectMaskParameters().RoadPixelSearchRadius = value;
    }
    
    #endregion
    
    // ========================================
    // HELPER METHODS
    // ========================================
    
    /// <summary>
    /// Gets or creates the SplineParameters object (auto-creates with defaults if null).
    /// </summary>
    public SplineRoadParameters GetSplineParameters()
    {
        return SplineParameters ??= new SplineRoadParameters();
    }
    
    /// <summary>
    /// Gets or creates the DirectMaskParameters object (auto-creates with defaults if null).
    /// </summary>
    public DirectMaskRoadParameters GetDirectMaskParameters()
    {
        return DirectMaskParameters ??= new DirectMaskRoadParameters();
    }
    
    /// <summary>
    /// Validates all parameters and returns any errors.
    /// Includes validation for approach-specific parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        // Validate common parameters
        if (RoadWidthMeters <= 0)
            errors.Add("RoadWidthMeters must be greater than 0");
            
        if (TerrainAffectedRangeMeters < 0)
            errors.Add("TerrainAffectedRangeMeters must be >= 0");
            
        if (RoadMaxSlopeDegrees < 0 || RoadMaxSlopeDegrees > 90)
            errors.Add("RoadMaxSlopeDegrees must be between 0 and 90");
            
        if (SideMaxSlopeDegrees < 0 || SideMaxSlopeDegrees > 90)
            errors.Add("SideMaxSlopeDegrees must be between 0 and 90");
            
        if (CrossSectionIntervalMeters <= 0)
            errors.Add("CrossSectionIntervalMeters must be greater than 0");
            
        if (LongitudinalSmoothingWindowMeters <= 0)
            errors.Add("LongitudinalSmoothingWindowMeters must be greater than 0");
        
        // Validate approach-specific parameters
        if (Approach == RoadSmoothingApproach.SplineBased && SplineParameters != null)
        {
            errors.AddRange(SplineParameters.Validate());
        }
        else if (Approach == RoadSmoothingApproach.DirectMask && DirectMaskParameters != null)
        {
            errors.AddRange(DirectMaskParameters.Validate());
        }
        
        // Warn about problematic combinations (SplineBased only)
        if (Approach == RoadSmoothingApproach.SplineBased)
        {
            var splineParams = GetSplineParameters();
            
            // Warn about dotted roads risk
            if (splineParams.GlobalLevelingStrength > 0.5f && TerrainAffectedRangeMeters < 15.0f)
            {
                errors.Add($"WARNING: High GlobalLevelingStrength ({splineParams.GlobalLevelingStrength:F2}) " +
                          $"with small TerrainAffectedRangeMeters ({TerrainAffectedRangeMeters}m) " +
                          $"may create disconnected road segments! Recommend: TerrainAffectedRangeMeters ? 20m " +
                          $"OR GlobalLevelingStrength ? 0.5");
            }
            
            // Warn about insufficient cross-section density
            float totalImpactRadius = (RoadWidthMeters / 2.0f) + TerrainAffectedRangeMeters;
            float recommendedMaxInterval = totalImpactRadius / 3.0f;
            if (CrossSectionIntervalMeters > recommendedMaxInterval)
            {
                errors.Add($"WARNING: CrossSectionIntervalMeters ({CrossSectionIntervalMeters}m) may cause gaps! " +
                          $"Recommend: ? {recommendedMaxInterval:F2}m for {totalImpactRadius:F1}m impact radius");
            }
        }
            
        return errors;
    }
}
