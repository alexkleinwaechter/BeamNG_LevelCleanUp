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
    /// SPLINE-BASED approach - RECOMMENDED for smooth roads (highways, racing circuits).
    /// Uses centerline extraction + global Euclidean Distance Transform (EDT) + analytical blending.
    /// 
    /// FEATURES:
    /// - Perpendicular sampling (perfectly level on curves)
    /// - Exact EDT (Felzenszwalb & Huttenlocher) in O(W*H)
    /// - Prefix-sum elevation smoothing in O(N)
    /// - No per-pixel cross-section iteration
    /// 
    /// PERFORMANCE: Fast, scales linearly with terrain size (3s for 4096x4096).
    /// QUALITY: Eliminates jagged edges, stairs, blocky artifacts.
    /// 
    /// Best for: Curved roads, highways, racing circuits.
    /// Note: Use DirectMask for complex intersections.
    /// </summary>
    Spline
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
    /// Enable enforcement of the maximum road slope constraint.
    /// When enabled, road elevations are adjusted to ensure no segment exceeds RoadMaxSlopeDegrees.
    /// Default: false (disabled - road follows smoothed terrain)
    /// </summary>
    public bool EnableMaxSlopeConstraint { get; set; } = false;

    /// <summary>
    /// Maximum allowed road surface slope in degrees.
    /// Only enforced when EnableMaxSlopeConstraint = true.
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
    // POST-PROCESSING SMOOTHING (All Approaches)
    // ========================================

    /// <summary>
    /// Enable post-processing smoothing to eliminate staircase artifacts on the road surface.
    /// Applies a Gaussian blur or similar smoothing algorithm masked to the road and shoulder areas.
    /// This addresses the visible interval-based staircase effect that can occur after blending.
    /// Default: false (disabled for backward compatibility)
    /// </summary>
    public bool EnablePostProcessingSmoothing { get; set; } = false;

    /// <summary>
    /// Type of smoothing filter to apply in post-processing.
    /// Default: Gaussian (best quality, slower)
    /// </summary>
    public PostProcessingSmoothingType SmoothingType { get; set; } = PostProcessingSmoothingType.Gaussian;

    /// <summary>
    /// Kernel size for the smoothing filter in pixels.
    /// Larger values = smoother but slower.
    /// Typical values:
    /// - 3-5: Light smoothing (preserve detail)
    /// - 7-9: Medium smoothing (good balance)
    /// - 11-15: Heavy smoothing (very smooth roads)
    /// Default: 7
    /// </summary>
    public int SmoothingKernelSize { get; set; } = 7;

    /// <summary>
    /// Sigma value for Gaussian blur (standard deviation).
    /// Higher values = more aggressive smoothing.
    /// Typical values:
    /// - 0.5-1.0: Light smoothing
    /// - 1.0-2.0: Medium smoothing
    /// - 2.0-4.0: Heavy smoothing
    /// Default: 1.5
    /// </summary>
    public float SmoothingSigma { get; set; } = 1.5f;

    /// <summary>
    /// Extension of the smoothing mask beyond the road edge in meters.
    /// This applies smoothing to the shoulder/blend zone as well to ensure continuity.
    /// Typical values:
    /// - 2-4m: Smooth only near road edge
    /// - 4-8m: Smooth into shoulder zone
    /// - 8-12m: Smooth entire blend zone
    /// Default: 6.0 (reaches into shoulder)
    /// </summary>
    public float SmoothingMaskExtensionMeters { get; set; } = 6.0f;

    /// <summary>
    /// Number of smoothing iterations to apply.
    /// More iterations = smoother result but slower.
    /// Default: 1
    /// </summary>
    public int SmoothingIterations { get; set; } = 1;

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

    /// <summary>
    /// Export the smoothed heightmap as a grayscale image with road outlines overlaid.
    /// Shows:
    /// - Smoothed heightmap as grayscale background (black=low, white=high)
    /// - Thin cyan outline at road edges (± roadWidth/2)
    /// - Thin magenta outline at terrain blending edges (± roadWidth/2 + terrainAffectedRange)
    /// 
    /// Only works with Spline approach (requires distance field).
    /// Output file: smoothed_heightmap_with_road_outlines.png
    /// Default: false
    /// </summary>
    public bool ExportSmoothedHeightmapWithOutlines { get; set; } = false;

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
        get => Approach == RoadSmoothingApproach.Spline
            ? GetSplineParameters().UseButterworthFilter
            : GetDirectMaskParameters().UseButterworthFilter;
        set
        {
            if (Approach == RoadSmoothingApproach.Spline)
                GetSplineParameters().UseButterworthFilter = value;
            else
                GetDirectMaskParameters().UseButterworthFilter = value;
        }
    }

    public int ButterworthFilterOrder
    {
        get => Approach == RoadSmoothingApproach.Spline
            ? GetSplineParameters().ButterworthFilterOrder
            : GetDirectMaskParameters().ButterworthFilterOrder;
        set
        {
            if (Approach == RoadSmoothingApproach.Spline)
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

        // Validate post-processing smoothing parameters
        if (EnablePostProcessingSmoothing)
        {
            if (SmoothingKernelSize < 3 || SmoothingKernelSize % 2 == 0)
                errors.Add("SmoothingKernelSize must be an odd number >= 3");

            if (SmoothingSigma <= 0)
                errors.Add("SmoothingSigma must be greater than 0");

            if (SmoothingMaskExtensionMeters < 0)
                errors.Add("SmoothingMaskExtensionMeters must be >= 0");

            if (SmoothingIterations < 1)
                errors.Add("SmoothingIterations must be >= 1");
        }

        // Validate approach-specific parameters
        if (Approach == RoadSmoothingApproach.Spline && SplineParameters != null)
        {
            errors.AddRange(SplineParameters.Validate());
        }
        else if (Approach == RoadSmoothingApproach.DirectMask && DirectMaskParameters != null)
        {
            errors.AddRange(DirectMaskParameters.Validate());
        }

        // Warn about problematic combinations (Spline approach only)
        if (Approach == RoadSmoothingApproach.Spline)
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
