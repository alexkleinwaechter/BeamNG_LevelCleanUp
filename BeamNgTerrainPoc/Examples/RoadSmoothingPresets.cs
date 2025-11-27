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
    /// Uses Butterworth low-pass filter for maximally flat surface with minimal bumps.
    /// Processing time: ~15-20 minutes for 4096x4096
    /// Quality: Professional highway standard with natural terrain integration
    /// </summary>
    public static RoadSmoothingParameters TerrainFollowingSmooth => new()
    {
        Approach = RoadSmoothingApproach.SplineBased,
        EnableTerrainBlending = true,
        
        // COMMON PARAMETERS
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 12.0f,
        CrossSectionIntervalMeters = 0.5f,
        RoadMaxSlopeDegrees = 4.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
        // SPLINE-SPECIFIC PARAMETERS
        SplineParameters = new SplineRoadParameters
        {
            // Butterworth filter for maximally flat passband
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            SmoothingWindowSize = 201,
            
            // Terrain-following mode (no global leveling)
            GlobalLevelingStrength = 0.0f,
            
            // Junction handling
            PreferStraightThroughJunctions = true,
            JunctionAngleThreshold = 45.0f,
            MinPathLengthPixels = 50.0f,
            
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
    /// Processing time: ~22-28 minutes for 4096x4096
    /// Quality: Professional civil engineering grade
    /// WARNING: Requires wide blend zone to prevent disconnected segments!
    /// </summary>
    public static RoadSmoothingParameters MountainousUltraSmooth => new()
    {
        Approach = RoadSmoothingApproach.SplineBased,
        EnableTerrainBlending = true,
        
        // COMMON PARAMETERS - Need wider blend for global leveling!
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 20.0f,       // WIDE blend needed!
        CrossSectionIntervalMeters = 0.4f,        // Dense to prevent gaps
        RoadMaxSlopeDegrees = 2.0f,
        SideMaxSlopeDegrees = 25.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
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
    /// Processing time: ~12-15 minutes for 4096x4096
    /// Quality: High-quality highway standard
    /// </summary>
    public static RoadSmoothingParameters HillyAggressive => new()
    {
        Approach = RoadSmoothingApproach.SplineBased,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 15.0f,
        CrossSectionIntervalMeters = 0.5f,
        RoadMaxSlopeDegrees = 5.0f,
        SideMaxSlopeDegrees = 28.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
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
    /// Processing time: ~8-10 minutes for 4096x4096
    /// Quality: Good quality local road standard
    /// </summary>
    public static RoadSmoothingParameters FlatModerate => new()
    {
        Approach = RoadSmoothingApproach.SplineBased,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 10.0f,
        CrossSectionIntervalMeters = 1.0f,
        RoadMaxSlopeDegrees = 6.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
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
    /// Processing time: ~3-5 minutes for 4096x4096
    /// Quality: Good for game development iteration
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
    /// Processing time: ~30-40 minutes for 4096x4096
    /// Quality: Perfectly smooth but may look artificial
    /// </summary>
    public static RoadSmoothingParameters ExtremeNuclear => new()
    {
        Approach = RoadSmoothingApproach.SplineBased,
        EnableTerrainBlending = true,
        
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 25.0f,       // Wide blend for global leveling
        CrossSectionIntervalMeters = 0.3f,        // Dense to prevent dots
        RoadMaxSlopeDegrees = 1.0f,
        SideMaxSlopeDegrees = 20.0f,
        BlendFunctionType = BlendFunctionType.Cosine,
        
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
}

// USAGE IN PROGRAM.CS:
/*

// Instead of manually configuring parameters, use a preset:
if (info.MaterialName.Contains("GROUNDMODEL_ASPHALT1", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Configuring road smoothing for layer {info.Index}");
    
    // Choose the appropriate preset:
    roadParameters = RoadSmoothingPresets.MountainousUltraSmooth;  // ? Your current terrain
    
    // OR customize a preset:
    roadParameters = RoadSmoothingPresets.HillyAggressive;
    roadParameters.SmoothingWindowSize = 151;  // Tweak specific values
    roadParameters.DebugOutputDirectory = @"C:\temp\TestMappingTools\_output";
    roadParameters.ExportSplineDebugImage = true;
    roadParameters.ExportSmoothedElevationDebugImage = true;
    roadParameters.ExportSkeletonDebugImage = true;
}

*/
