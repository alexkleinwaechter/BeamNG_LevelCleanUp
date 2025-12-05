// Road Smoothing Parameter Presets
// Copy the appropriate preset into your Program.cs based on your terrain type

using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Examples;

/// <summary>
/// Pre-configured road smoothing parameter presets for different terrain types.
/// These are battle-tested settings that produce professional results.
/// </summary>
public static class RoadSmoothingPresets
{
    /// <summary>
    /// RECOMMENDED: Terrain-following smooth roads with Butterworth filter.
    /// Creates smooth roads that gently follow terrain elevation without massive cutting/filling.
    /// Uses optimized EDT-based blending for fast, professional results.
    /// Processing time: ~3-4 seconds for 4096x4096 (optimized!)
    /// Quality: Professional highway standard with natural terrain integration
    /// </summary>
    public static RoadSmoothingParameters TerrainFollowingSmooth => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        // COMMON PARAMETERS
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 12.0f,
        CrossSectionIntervalMeters = 0.5f,
        RoadMaxSlopeDegrees = 4.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING (eliminates staircase artifacts)
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7,
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 6.0f,
        SmoothingIterations = 1,
        
        // SPLINE-SPECIFIC PARAMETERS
        SplineParameters = new SplineRoadParameters
        {
            // Skeletonization
            SkeletonDilationRadius = 0,           // No dilation for cleanest skeleton
            
            // Junction handling - disabled for continuous curves
            PreferStraightThroughJunctions = false,  // Only enable for road networks with intersections
            JunctionAngleThreshold = 45.0f,          // (Unused when PreferStraightThroughJunctions=false)
            MinPathLengthPixels = 50.0f,
            
            // Butterworth filter for maximally flat passband
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            SmoothingWindowSize = 201,
            
            // Terrain-following mode (no global leveling)
            GlobalLevelingStrength = 0.0f,
            
            // Connectivity
            BridgeEndpointMaxDistancePixels = 40.0f,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            
            // Spline fitting
            SplineTension = 0.2f,
            SplineContinuity = 0.7f,
            SplineBias = 0.0f
        }
    };
    
    /// <summary>
    /// ULTRA-AGGRESSIVE: For very mountainous/hilly terrain with large elevation changes.
    /// Creates glass-smooth roads that completely override underlying terrain bumps.
    /// Uses global leveling to force all roads to similar elevation.
    /// Processing time: ~3-4 seconds for 4096x4096 (optimized!)
    /// Quality: Professional civil engineering grade
    /// WARNING: Requires wide blend zone to prevent disconnected segments!
    /// </summary>
    public static RoadSmoothingParameters MountainousUltraSmooth => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        // COMMON PARAMETERS - Need wider blend for global leveling!
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 20.0f,       // WIDE blend needed!
        CrossSectionIntervalMeters = 0.4f,        // Dense to prevent gaps
        RoadMaxSlopeDegrees = 2.0f,
        SideMaxSlopeDegrees = 25.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - Heavy for ultra-smooth result
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 9,                  // Larger kernel for mountainous terrain
        SmoothingSigma = 2.0f,                    // More aggressive
        SmoothingMaskExtensionMeters = 8.0f,
        SmoothingIterations = 2,                  // Multiple passes for very smooth result
        
        // SPLINE-SPECIFIC PARAMETERS
        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            SmoothingWindowSize = 251,
            
            // Global leveling ENABLED for flat network
            GlobalLevelingStrength = 0.90f,       // Strong (not extreme to avoid artifacts)
            
            PreferStraightThroughJunctions = true,
            JunctionAngleThreshold = 45.0f,
            MinPathLengthPixels = 50.0f,
            
            BridgeEndpointMaxDistancePixels = 40.0f,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            
            SplineTension = 0.2f,
            SplineContinuity = 0.7f,
            SplineBias = 0.0f
        }
    };
    
    /// <summary>
    /// AGGRESSIVE: Balanced settings for moderately hilly terrain.
    /// Creates very smooth roads with good performance.
    /// Processing time: ~3 seconds for 4096x4096 (optimized!)
    /// Quality: High-quality highway standard
    /// </summary>
    public static RoadSmoothingParameters HillyAggressive => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 15.0f,
        CrossSectionIntervalMeters = 0.5f,
        RoadMaxSlopeDegrees = 5.0f,
        SideMaxSlopeDegrees = 28.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - Medium smoothing
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7,
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 6.0f,
        SmoothingIterations = 1,
        
        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = true,
            ButterworthFilterOrder = 3,
            SmoothingWindowSize = 151,
            GlobalLevelingStrength = 0.50f,       // Moderate leveling
            
            PreferStraightThroughJunctions = true,
            JunctionAngleThreshold = 45.0f,
            MinPathLengthPixels = 50.0f,
            
            BridgeEndpointMaxDistancePixels = 40.0f,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.75f,
            
            SplineTension = 0.3f,
            SplineContinuity = 0.5f,
            SplineBias = 0.0f
        }
    };
    
    /// <summary>
    /// MODERATE: For relatively flat terrain with gentle hills.
    /// Creates smooth roads while preserving natural elevation flow.
    /// Processing time: ~2-3 seconds for 4096x4096 (optimized!)
    /// Quality: Good quality local road standard
    /// </summary>
    public static RoadSmoothingParameters FlatModerate => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 10.0f,
        CrossSectionIntervalMeters = 1.0f,
        RoadMaxSlopeDegrees = 6.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - Light smoothing for flat terrain
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,                  // Smaller kernel preserves detail
        SmoothingSigma = 1.0f,                    // Light smoothing
        SmoothingMaskExtensionMeters = 4.0f,
        SmoothingIterations = 1,
        
        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = false,         // Gaussian is fine for flat terrain
            SmoothingWindowSize = 51,
            GlobalLevelingStrength = 0.0f,
            
            PreferStraightThroughJunctions = true,
            JunctionAngleThreshold = 45.0f,
            MinPathLengthPixels = 40.0f,
            
            BridgeEndpointMaxDistancePixels = 30.0f,
            DensifyMaxSpacingPixels = 2.5f,
            SimplifyTolerancePixels = 1.0f,
            
            SplineTension = 0.4f,
            SplineContinuity = 0.3f,
            SplineBias = 0.0f
        }
    };
    
    /// <summary>
    /// FAST: For quick testing or when processing time is critical.
    /// Uses DirectMask approach (robust, handles intersections well).
    /// Processing time: ~2-3 seconds for 4096x4096
    /// Quality: Good for game development iteration and complex intersections
    /// </summary>
    public static RoadSmoothingParameters FastTesting => new()
    {
        Approach = RoadSmoothingApproach.DirectMask,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 15.0f,
        CrossSectionIntervalMeters = 2.0f,
        RoadMaxSlopeDegrees = 8.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        DirectMaskParameters = new DirectMaskRoadParameters
        {
            SmoothingWindowSize = 10,
            RoadPixelSearchRadius = 3,
            UseButterworthFilter = false          // Simple moving average for speed
        }
    };
    
    /// <summary>
    /// EXTREME NUCLEAR: For when nothing else works.
    /// Maximum possible smoothing - roads will be EXTREMELY flat.
    /// Uses global leveling + wide blend zones.
    /// Processing time: ~3-4 seconds for 4096x4096 (optimized!)
    /// Quality: Perfectly smooth but may look artificial
    /// </summary>
    public static RoadSmoothingParameters ExtremeNuclear => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 25.0f,       // Wide blend for global leveling
        CrossSectionIntervalMeters = 0.3f,        // Dense to prevent dots
        RoadMaxSlopeDegrees = 1.0f,
        SideMaxSlopeDegrees = 20.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - MAXIMUM smoothing
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 11,                 // Maximum kernel size
        SmoothingSigma = 3.0f,                    // Very aggressive
        SmoothingMaskExtensionMeters = 10.0f,     // Smooth entire blend zone
        SmoothingIterations = 3,                  // Multiple passes
        
        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = true,
            ButterworthFilterOrder = 6,           // Maximum flatness
            SmoothingWindowSize = 401,
            GlobalLevelingStrength = 0.95f,       // Extreme leveling
            
            PreferStraightThroughJunctions = true,
            JunctionAngleThreshold = 30.0f,
            MinPathLengthPixels = 60.0f,
            
            BridgeEndpointMaxDistancePixels = 50.0f,
            DensifyMaxSpacingPixels = 1.0f,
            SimplifyTolerancePixels = 0.25f,
            
            SplineTension = 0.1f,
            SplineContinuity = 0.9f,
            SplineBias = 0.0f
        }
    };
    
    /// <summary>
    /// HIGHWAY: Professional highway-quality roads (8m wide).
    /// Creates smooth terrain-following roads suitable for main highways.
    /// - 8 meters wide (standard 2-lane highway)
    /// - Gentle 6° max slope
    /// - Large smoothing window for professional quality
    /// Processing time: ~3-4 seconds for 4096x4096
    /// Quality: Professional highway standard
    /// </summary>
    public static RoadSmoothingParameters Highway => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        // ROAD GEOMETRY - Highway (8m wide)
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 6.0f,        // Shoulder for smooth transition
        CrossSectionIntervalMeters = 0.5f,        // High quality sampling
        
        // SLOPE CONSTRAINTS - Gentle highway grades
        RoadMaxSlopeDegrees = 6.0f,               // Highway standard
        SideMaxSlopeDegrees = 45.0f,              // Standard embankment
        
        // BLENDING
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - Eliminates staircase artifacts
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Box,
        SmoothingKernelSize = 7,                  // Medium smoothing
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 3.0f,      // Smooth into shoulder
        SmoothingIterations = 1,
        
        // DEBUG VISUALIZATION
        ExportSmoothedHeightmapWithOutlines = true,
        
        // SPLINE-SPECIFIC SETTINGS
        SplineParameters = new SplineRoadParameters
        {
            // Skeletonization
            SkeletonDilationRadius = 0,
            
            // Junction handling - disabled for continuous curves
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 100.0f,
            
            // Connectivity & path extraction
            BridgeEndpointMaxDistancePixels = 40.0f,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,
            
            // Spline curve fitting
            SplineTension = 0.2f,                 // Loose for smooth curves
            SplineContinuity = 0.7f,              // Very smooth corners
            SplineBias = 0.0f,
            
            // Elevation smoothing
            SmoothingWindowSize = 401,            // ~150m smoothing window
            UseButterworthFilter = true,          // Butterworth filter
            ButterworthFilterOrder = 4,           // Aggressive flatness
            GlobalLevelingStrength = 0.0f,        // Terrain-following
            
            // Debug output
            ExportSplineDebugImage = true,
            ExportSkeletonDebugImage = true,
            ExportSmoothedElevationDebugImage = true
        }
    };
    
    /// <summary>
    /// MOUNTAIN ROAD: Narrow steep roads for mountainous terrain (6m wide).
    /// Creates terrain-following roads suitable for winding mountain passes.
    /// - 6 meters wide (narrower than highways)
    /// - Steeper 8° max slope allowed
    /// - Tighter curves for mountainous terrain
    /// Processing time: ~3-4 seconds for 4096x4096
    /// Quality: Authentic mountain road character
    /// </summary>
    public static RoadSmoothingParameters MountainRoad => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        // ROAD GEOMETRY - Narrow mountain road (6m wide)
        RoadWidthMeters = 6.0f,                   // Narrower road
        TerrainAffectedRangeMeters = 8.0f,        // Tighter shoulder (road hugs terrain)
        CrossSectionIntervalMeters = 0.5f,        // High quality sampling
        
        // SLOPE CONSTRAINTS - Steeper for mountains
        RoadMaxSlopeDegrees = 8.0f,               // Steeper mountain grade
        SideMaxSlopeDegrees = 35.0f,              // Steeper embankment
        
        // BLENDING
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - Light smoothing to preserve mountain character
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,                  // Lighter smoothing
        SmoothingSigma = 1.0f,                    // Less aggressive
        SmoothingMaskExtensionMeters = 4.0f,      // Smaller extension
        SmoothingIterations = 1,
        
        // SPLINE-SPECIFIC SETTINGS
        SplineParameters = new SplineRoadParameters
        {
            // Skeletonization
            SkeletonDilationRadius = 0,
            
            // Junction handling
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 50.0f,          // Allow shorter segments
            
            // Connectivity & path extraction
            BridgeEndpointMaxDistancePixels = 30.0f,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,
            
            // Spline curve fitting - tighter for mountain curves
            SplineTension = 0.3f,                 // Tighter following
            SplineContinuity = 0.5f,              // Allow sharper corners
            SplineBias = 0.0f,
            
            // Elevation smoothing
            SmoothingWindowSize = 201,            // ~100m smoothing window
            UseButterworthFilter = true,          // Butterworth filter
            ButterworthFilterOrder = 3,           // Less aggressive
            GlobalLevelingStrength = 0.0f,        // Follow terrain closely
            
            // Debug output
            ExportSplineDebugImage = true,
            ExportSkeletonDebugImage = true,
            ExportSmoothedElevationDebugImage = true
        }
    };
    
    /// <summary>
    /// DIRT ROAD: Rustic dirt roads with minimal smoothing (5m wide).
    /// Creates natural-looking unpaved roads that follow terrain closely.
    /// - 5 meters wide (narrow, rustic)
    /// - Minimal smoothing (preserve natural terrain character)
    /// - Higher tolerance for bumps and irregularities
    /// Processing time: ~2-3 seconds for 4096x4096
    /// Quality: Authentic unpaved road character
    /// </summary>
    public static RoadSmoothingParameters DirtRoad => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,
        
        // ROAD GEOMETRY - Narrow dirt road (5m wide)
        RoadWidthMeters = 5.0f,                   // Narrow dirt road
        TerrainAffectedRangeMeters = 6.0f,        // Minimal shoulder
        CrossSectionIntervalMeters = 0.75f,       // Standard quality (faster)
        
        // SLOPE CONSTRAINTS - Relaxed for dirt roads
        RoadMaxSlopeDegrees = 10.0f,              // Allow steep sections
        SideMaxSlopeDegrees = 40.0f,              // Natural embankment
        
        // BLENDING
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // POST-PROCESSING SMOOTHING - Minimal smoothing to preserve rustic character
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,                  // Light smoothing only
        SmoothingSigma = 0.8f,                    // Very gentle
        SmoothingMaskExtensionMeters = 3.0f,      // Minimal extension
        SmoothingIterations = 1,
        
        // SPLINE-SPECIFIC SETTINGS
        SplineParameters = new SplineRoadParameters
        {
            // Skeletonization
            SkeletonDilationRadius = 0,
            
            // Junction handling
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 40.0f,          // Allow short segments
            
            // Connectivity & path extraction
            BridgeEndpointMaxDistancePixels = 25.0f,
            DensifyMaxSpacingPixels = 2.0f,       // Less dense
            SimplifyTolerancePixels = 0.75f,      // More simplification
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,
            
            // Spline curve fitting - preserve character
            SplineTension = 0.4f,                 // Follow terrain more closely
            SplineContinuity = 0.3f,              // Allow natural bumps
            SplineBias = 0.0f,
            
            // Elevation smoothing
            SmoothingWindowSize = 51,             // ~40m smoothing window
            UseButterworthFilter = false,         // Use simple Gaussian
            ButterworthFilterOrder = 2,           // Not used (UseButterworthFilter=false)
            GlobalLevelingStrength = 0.0f,        // Follow terrain very closely
            
            // Debug output
            ExportSplineDebugImage = true,
            ExportSkeletonDebugImage = true,
            ExportSmoothedElevationDebugImage = true
        }
    };
}

// USAGE IN PROGRAM.CS:
/*

// Instead of manually configuring parameters, use a preset based on material type:
static RoadSmoothingParameters GetRoadSmoothingParameters(string materialName, int layerIndex)
{
    // Main highways (8m wide, smooth)
    if (materialName.Equals("GROUNDMODEL_ASPHALT1", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Configuring HIGHWAY road smoothing for layer {layerIndex}");
        var preset = RoadSmoothingPresets.Highway;
        preset.DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\highway";
        return preset;
    }

    // Narrow steep roads (6m wide, mountainous)
    if (materialName.Equals("BeamNG_DriverTrainingETK_Asphalt", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Configuring MOUNTAIN road smoothing for layer {layerIndex}");
        var preset = RoadSmoothingPresets.MountainRoad;
        preset.DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\mountain";
        return preset;
    }

    // Dirt roads (5m wide, terrain-following)
    if (materialName.Equals("Dirt", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Configuring DIRT road smoothing for layer {layerIndex}");
        var preset = RoadSmoothingPresets.DirtRoad;
        preset.DebugOutputDirectory = @"d:\temp\TestMappingTools\_output\dirt";
        return preset;
    }

    // Not a road material
    return null;
}

// Available presets by terrain type:
// - RoadSmoothingPresets.Highway              // 8m wide, professional highway quality
// - RoadSmoothingPresets.MountainRoad         // 6m wide, steeper grades for mountain passes
// - RoadSmoothingPresets.DirtRoad             // 5m wide, minimal smoothing for rustic character
// - RoadSmoothingPresets.TerrainFollowingSmooth  // Butterworth filter, gentle terrain following
// - RoadSmoothingPresets.MountainousUltraSmooth  // Aggressive leveling for very hilly terrain
// - RoadSmoothingPresets.HillyAggressive         // Balanced for moderately hilly terrain
// - RoadSmoothingPresets.FlatModerate            // Light smoothing for flat terrain
// - RoadSmoothingPresets.FastTesting             // Quick testing with DirectMask approach
// - RoadSmoothingPresets.ExtremeNuclear          // Maximum smoothing for extreme cases

*/
