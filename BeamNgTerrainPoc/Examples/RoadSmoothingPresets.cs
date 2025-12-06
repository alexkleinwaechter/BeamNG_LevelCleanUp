// Road Smoothing Parameter Presets
// Copy the appropriate preset into your Program.cs based on your terrain type
//
// IMPORTANT PARAMETER RELATIONSHIPS (validated by TerrainMaterialSettings):
// 
// 1. GlobalLevelingStrength vs TerrainAffectedRangeMeters:
//    - GlobalLevelingStrength > 0.5 requires TerrainAffectedRangeMeters >= 15m
//    - GlobalLevelingStrength > 0.3 requires TerrainAffectedRangeMeters >= 12m
//    - For narrow blend zones (steep terrain beside road), use GlobalLevelingStrength = 0
//
// 2. CrossSectionIntervalMeters:
//    - Should be <= (RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3
//    - Smaller values = more detail but slower
//
// 3. SmoothingMaskExtensionMeters:
//    - Should be >= CrossSectionIntervalMeters * 2 to eliminate staircase artifacts
//
// 4. SmoothingWindowSize and SmoothingKernelSize:
//    - Should be ODD numbers for symmetric smoothing

using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Examples;

/// <summary>
///     Pre-configured road smoothing parameter presets for different terrain types.
///     These settings are validated against parameter dependency rules.
///     
///     KEY DESIGN PRINCIPLES:
///     - Road surface within RoadWidthMeters is PERFECTLY FLAT (no terrain bleed-through)
///     - Butterworth filter ensures maximally flat elevation along road length
///     - Post-processing only smooths transitions, never curves the road surface itself
///     - For steep terrain beside roads: use small TerrainAffectedRangeMeters + GlobalLevelingStrength=0
/// </summary>
public static class RoadSmoothingPresets
{
    /// <summary>
    ///     RECOMMENDED: Terrain-following smooth roads with Butterworth filter.
    ///     Creates smooth roads that gently follow terrain elevation without massive cutting/filling.
    ///     Road surface is perfectly flat across its width, with smooth longitudinal elevation changes.
    ///     
    ///     Best for: General-purpose roads that need to integrate naturally with terrain.
    ///     Processing time: ~3-4 seconds for 4096x4096
    ///     Quality: Professional highway standard with natural terrain integration
    /// </summary>
    public static RoadSmoothingParameters TerrainFollowingSmooth => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 12.0f,
        // CrossSectionIntervalMeters validation: (8/2 + 12) / 3 = 5.3m max, using 0.5m ?
        CrossSectionIntervalMeters = 0.5f,
        
        // SLOPE CONSTRAINTS
        RoadMaxSlopeDegrees = 4.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING SMOOTHING
        // SmoothingMaskExtensionMeters validation: >= 0.5 * 2 = 1.0m, using 1.5m ?
        // Note: Extension should NOT exceed into road surface to avoid curving the flat road
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7, // Odd number ?
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 1.5f, // Only smooth blend zone edges, not road surface
        SmoothingIterations = 1,

        // SPLINE-SPECIFIC PARAMETERS
        SplineParameters = new SplineRoadParameters
        {
            SkeletonDilationRadius = 0,
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 45.0f,
            MinPathLengthPixels = 50.0f,

            // Butterworth filter for maximally flat road surface
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            SmoothingWindowSize = 201, // Odd number ?

            // GlobalLevelingStrength = 0 allows narrow blend zones without disconnection
            GlobalLevelingStrength = 0.0f,

            BridgeEndpointMaxDistancePixels = 40.0f,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,

            SplineTension = 0.2f,
            SplineContinuity = 0.7f,
            SplineBias = 0.0f
        }
    };

    /// <summary>
    ///     MOUNTAINOUS WITH GLOBAL LEVELING: For networks where all roads should be at similar elevation.
    ///     Creates flat road surfaces that are pulled toward a network-average elevation.
    ///     
    ///     REQUIRES wide TerrainAffectedRangeMeters (?20m) to prevent disconnected "dotted" roads!
    ///     Use this when you want roads to ignore terrain and form a flat network.
    ///     
    ///     Best for: Race tracks, industrial areas, or artificial road networks.
    ///     Processing time: ~3-4 seconds for 4096x4096
    ///     Quality: Ultra-smooth artificial road network
    ///     
    ///     ?? NOT for natural mountain roads - use MountainRoad preset instead!
    /// </summary>
    public static RoadSmoothingParameters MountainousUltraSmooth => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Wide blend required for GlobalLevelingStrength > 0.5!
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 22.0f, // >= 20m for GlobalLevelingStrength 0.5 ?
        // CrossSectionIntervalMeters validation: (8/2 + 22) / 3 = 8.7m max, using 0.4m ?
        CrossSectionIntervalMeters = 0.4f,
        
        RoadMaxSlopeDegrees = 2.0f,
        SideMaxSlopeDegrees = 25.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Smooth blend zone only
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 9, // Odd number ?
        SmoothingSigma = 2.0f,
        SmoothingMaskExtensionMeters = 1.0f, // Minimal - don't curve the flat road surface
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4, // Order 4 is aggressive but stable
            SmoothingWindowSize = 251, // Odd number ?

            // GlobalLevelingStrength 0.5 is the MAXIMUM for 22m blend zone
            // Higher values risk disconnected roads even with wide blend
            GlobalLevelingStrength = 0.5f, // Reduced from 0.9 to stay in safe range

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
    ///     HILLY TERRAIN: Balanced settings for moderately hilly terrain.
    ///     Roads follow terrain elevation but with smooth transitions.
    ///     
    ///     Best for: Rolling hills, countryside roads.
    ///     Processing time: ~3 seconds for 4096x4096
    ///     Quality: High-quality highway standard
    /// </summary>
    public static RoadSmoothingParameters HillyAggressive => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 15.0f, // >= 15m for GlobalLevelingStrength 0.4 ?
        // CrossSectionIntervalMeters validation: (8/2 + 15) / 3 = 6.3m max, using 0.5m ?
        CrossSectionIntervalMeters = 0.5f,
        
        RoadMaxSlopeDegrees = 5.0f,
        SideMaxSlopeDegrees = 28.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7, // Odd number ?
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 1.5f, // >= CrossSectionInterval * 2 ?
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = true,
            ButterworthFilterOrder = 3,
            SmoothingWindowSize = 151, // Odd number ?
            
            // GlobalLevelingStrength 0.4 is safe with 15m blend zone
            GlobalLevelingStrength = 0.4f, // Reduced from 0.5 to stay safely under warning threshold

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
    ///     FLAT TERRAIN: Light smoothing for relatively flat terrain with gentle hills.
    ///     Preserves natural elevation flow while ensuring smooth road surface.
    ///     
    ///     Best for: Plains, coastal areas, gentle rolling terrain.
    ///     Processing time: ~2-3 seconds for 4096x4096
    ///     Quality: Good quality local road standard
    /// </summary>
    public static RoadSmoothingParameters FlatModerate => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 10.0f,
        // CrossSectionIntervalMeters validation: (8/2 + 10) / 3 = 4.7m max, using 0.75m ?
        CrossSectionIntervalMeters = 0.75f,
        
        RoadMaxSlopeDegrees = 6.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Light smoothing
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5, // Odd number ?
        SmoothingSigma = 1.0f,
        SmoothingMaskExtensionMeters = 1.5f, // >= 0.75 * 2 = 1.5m ?
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // Box filter is fine for flat terrain (Butterworth not needed)
            UseButterworthFilter = false,
            SmoothingWindowSize = 51, // Odd number ?
            GlobalLevelingStrength = 0.0f, // No leveling needed for flat terrain

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
    ///     FAST TESTING: Quick testing with DirectMask approach.
    ///     Robust, handles intersections well, faster than Spline.
    ///     
    ///     Best for: Development iteration, complex road networks with many intersections.
    ///     Processing time: ~2-3 seconds for 4096x4096
    ///     Quality: Good for testing, may have minor artifacts on curves
    /// </summary>
    public static RoadSmoothingParameters FastTesting => new()
    {
        Approach = RoadSmoothingApproach.DirectMask,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 12.0f,
        // CrossSectionIntervalMeters validation: (8/2 + 12) / 3 = 5.3m max, using 1.5m ?
        CrossSectionIntervalMeters = 1.5f,
        
        RoadMaxSlopeDegrees = 8.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Light to preserve speed
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Box, // Faster than Gaussian
        SmoothingKernelSize = 5, // Odd number ?
        SmoothingSigma = 1.0f,
        SmoothingMaskExtensionMeters = 3.0f, // >= 1.5 * 2 = 3.0m ?
        SmoothingIterations = 1,

        DirectMaskParameters = new DirectMaskRoadParameters
        {
            SmoothingWindowSize = 11, // Odd number ?
            RoadPixelSearchRadius = 3,
            UseButterworthFilter = false // Simple moving average for speed
        }
    };

    /// <summary>
    ///     EXTREME NUCLEAR: Maximum possible smoothing for very difficult terrain.
    ///     Roads will be EXTREMELY flat - may look artificial.
    ///     
    ///     REQUIRES very wide TerrainAffectedRangeMeters (?25m) for global leveling!
    ///     
    ///     Best for: When nothing else works, artificial environments.
    ///     Processing time: ~3-4 seconds for 4096x4096
    ///     Quality: Perfectly smooth but artificial-looking
    /// </summary>
    public static RoadSmoothingParameters ExtremeNuclear => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Very wide blend for extreme leveling
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 30.0f, // >= 25m for GlobalLevelingStrength 0.5 ?
        // CrossSectionIntervalMeters validation: (8/2 + 30) / 3 = 11.3m max, using 0.25m ?
        CrossSectionIntervalMeters = 0.25f,
        
        RoadMaxSlopeDegrees = 1.0f,
        SideMaxSlopeDegrees = 20.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Heavy but only on blend zone
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 11, // Odd number ?
        SmoothingSigma = 2.5f,
        SmoothingMaskExtensionMeters = 1.0f, // Minimal - preserve flat road surface
        SmoothingIterations = 2,

        SplineParameters = new SplineRoadParameters
        {
            UseButterworthFilter = true,
            ButterworthFilterOrder = 5, // High order for maximum flatness
            SmoothingWindowSize = 401, // Odd number ?
            
            // Even with 30m blend, keep leveling at 0.5 maximum
            GlobalLevelingStrength = 0.5f, // Reduced from 0.95 - higher causes artifacts

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
    ///     HIGHWAY: Professional highway-quality roads (8m wide).
    ///     Creates perfectly flat road surfaces that follow terrain elevation smoothly.
    ///     
    ///     Key features:
    ///     - Road surface is FLAT across its 8m width (no terrain bleed-through)
    ///     - Smooth elevation changes along road length (Butterworth filter)
    ///     - Moderate blend zone for natural integration
    ///     
    ///     Best for: Main highways, well-maintained roads.
    ///     Processing time: ~3-4 seconds for 4096x4096
    ///     Quality: Professional highway standard
    /// </summary>
    public static RoadSmoothingParameters Highway => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Standard 2-lane highway
        RoadWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 10.0f, // Moderate blend for natural look
        // CrossSectionIntervalMeters validation: (8/2 + 10) / 3 = 4.7m max, using 0.5m ?
        CrossSectionIntervalMeters = 0.5f,

        // SLOPE CONSTRAINTS
        RoadMaxSlopeDegrees = 6.0f,
        SideMaxSlopeDegrees = 35.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Smooth blend zone transitions only
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7, // Odd number ?
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 1.5f, // >= 0.5 * 2 = 1.0m ?, but don't extend into road
        SmoothingIterations = 1,

        // DEBUG VISUALIZATION
        ExportSmoothedHeightmapWithOutlines = false,

        SplineParameters = new SplineRoadParameters
        {
            SkeletonDilationRadius = 0,
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 100.0f,

            BridgeEndpointMaxDistancePixels = 40.0f,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            SplineTension = 0.2f,
            SplineContinuity = 0.7f,
            SplineBias = 0.0f,

            // Elevation smoothing - aggressive for perfectly flat road surface
            SmoothingWindowSize = 301, // Odd number ?, ~150m smoothing window
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f, // Terrain-following, no global leveling

            ExportSplineDebugImage = false,
            ExportSkeletonDebugImage = false,
            ExportSmoothedElevationDebugImage = false
        }
    };

    /// <summary>
    ///     MOUNTAIN ROAD: Narrow roads for steep mountainous terrain (6m wide).
    ///     Creates perfectly flat road surfaces with STEEP terrain falling away on sides.
    ///     Optimized for tight hairpin turns and switchbacks.
    ///     
    ///     Key features:
    ///     - Narrow 6m road width for authentic mountain passes
    ///     - SMALL TerrainAffectedRangeMeters (4m) for steep terrain beside road
    ///     - GlobalLevelingStrength = 0 (required for narrow blend zones)
    ///     - Strong Butterworth filter ensures flat road despite terrain changes
    ///     - Tighter spline fitting for accurate hairpin tracking
    ///     
    ///     Best for: Mountain passes, winding cliff roads, steep hillside routes, hairpin turns.
    ///     Processing time: ~3-4 seconds for 4096x4096
    ///     Quality: Authentic mountain road with steep embankments
    /// </summary>
    public static RoadSmoothingParameters MountainRoad => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Narrow road with steep sides
        RoadWidthMeters = 6.0f,
        TerrainAffectedRangeMeters = 4.0f, // SMALL for steep terrain beside road
        // CrossSectionIntervalMeters validation: (6/2 + 4) / 3 = 2.3m max, using 0.3m ?
        CrossSectionIntervalMeters = 0.3f, // Dense sampling for hairpin accuracy

        // SLOPE CONSTRAINTS - Steep grades allowed
        RoadMaxSlopeDegrees = 10.0f, // Steep mountain grade
        SideMaxSlopeDegrees = 50.0f, // Very steep embankment for mountain character
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Minimal to preserve steep embankments
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5, // Odd number ?, small to preserve detail
        SmoothingSigma = 0.8f, // Light sigma
        SmoothingMaskExtensionMeters = 1.0f, // >= 0.3 * 2 = 0.6m ?, minimal extension
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            SkeletonDilationRadius = 0,
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 30.0f, // Allow short segments for hairpins

            // HIGH PRECISION path extraction for tight curves
            BridgeEndpointMaxDistancePixels = 30.0f,
            DensifyMaxSpacingPixels = 1.0f, // Dense points for accurate curves
            SimplifyTolerancePixels = 0.25f, // Preserve hairpin shape detail
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // TIGHT SPLINE FITTING for hairpins
            // Higher tension = follows control points more closely
            // Lower continuity = allows sharper direction changes
            SplineTension = 0.5f, // Tight following (was 0.3)
            SplineContinuity = 0.2f, // Allow sharp corners (was 0.5)
            SplineBias = 0.0f,

            // CRITICAL: Strong Butterworth filter ensures FLAT road surface
            // even with narrow blend zone and steep terrain
            SmoothingWindowSize = 101, // Odd number ?, smaller for responsive curves
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4, // Aggressive for flat road surface
            
            // MUST be 0 for narrow TerrainAffectedRangeMeters!
            GlobalLevelingStrength = 0.0f, // Terrain-following, no global leveling

            ExportSplineDebugImage = false,
            ExportSkeletonDebugImage = false,
            ExportSmoothedElevationDebugImage = false
        }
    };

    /// <summary>
    ///     RACING CIRCUIT: Ultra-precise spline for racing tracks with tight hairpins.
    ///     Creates perfectly flat, glass-smooth road surfaces optimized for tight turns.
    ///     
    ///     Key features:
    ///     - Wide 10m road width for racing (allows overtaking)
    ///     - Ultra-dense cross-section sampling (0.25m)
    ///     - Maximum spline precision for hairpin accuracy
    ///     - Very tight spline following with sharp corner capability
    ///     - Heavy post-processing for glass-smooth surface
    ///     
    ///     Spline parameters optimized for:
    ///     - Hairpin turns (180° direction change)
    ///     - Chicanes (quick left-right sequences)
    ///     - Sweeping curves that must be followed precisely
    ///     
    ///     Best for: Racing circuits, karting tracks, autocross courses.
    ///     Processing time: ~4-5 seconds for 4096x4096 (high precision)
    ///     Quality: Professional racing circuit standard
    /// </summary>
    public static RoadSmoothingParameters RacingCircuit => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Wide racing surface
        RoadWidthMeters = 10.0f, // Wide for racing
        TerrainAffectedRangeMeters = 8.0f, // Moderate runoff area
        // CrossSectionIntervalMeters validation: (10/2 + 8) / 3 = 4.3m max, using 0.25m ?
        CrossSectionIntervalMeters = 0.25f, // Ultra-dense for maximum precision

        // SLOPE CONSTRAINTS - Racing standard (nearly flat)
        RoadMaxSlopeDegrees = 3.0f, // Gentle racing grade
        SideMaxSlopeDegrees = 25.0f, // Runoff areas slope gently
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Heavy for glass-smooth surface
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 9, // Odd number ?
        SmoothingSigma = 2.0f, // Aggressive smoothing
        SmoothingMaskExtensionMeters = 1.0f, // >= 0.25 * 2 = 0.5m ?
        SmoothingIterations = 2, // Multiple passes for perfection

        SplineParameters = new SplineRoadParameters
        {
            SkeletonDilationRadius = 0,
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 20.0f, // Keep even short track segments

            // MAXIMUM PRECISION path extraction
            BridgeEndpointMaxDistancePixels = 50.0f, // Connect track sections
            DensifyMaxSpacingPixels = 0.75f, // Very dense control points
            SimplifyTolerancePixels = 0.1f, // Almost no simplification
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // HAIRPIN-OPTIMIZED SPLINE FITTING
            // SplineTension: 0.5-0.6 follows the centerline tightly without cutting corners
            // SplineContinuity: 0.1-0.2 allows sharp direction changes at hairpins
            SplineTension = 0.55f, // Very tight following
            SplineContinuity = 0.15f, // Allow very sharp corners
            SplineBias = 0.0f, // Symmetric curves

            // Elevation smoothing - large window for perfectly flat track
            SmoothingWindowSize = 201, // Odd number ?
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f, // Follow terrain (most tracks have elevation changes)

            ExportSplineDebugImage = false,
            ExportSkeletonDebugImage = false,
            ExportSmoothedElevationDebugImage = false
        }
    };

    /// <summary>
    ///     DIRT ROAD: Rustic unpaved roads with minimal smoothing (5m wide).
    ///     Creates natural-looking roads that follow terrain closely.
    ///     Road surface is flat but preserves some terrain character.
    ///     
    ///     Key features:
    ///     - Narrow 5m width for rustic character
    ///     - Minimal smoothing preserves natural bumps
    ///     - Box filter instead of Butterworth for organic feel
    ///     
    ///     Best for: Forest trails, rural roads, unpaved paths.
    ///     Processing time: ~2-3 seconds for 4096x4096
    ///     Quality: Authentic unpaved road character
    /// </summary>
    public static RoadSmoothingParameters DirtRoad => new()
    {
        Approach = RoadSmoothingApproach.Spline,
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Narrow dirt road
        RoadWidthMeters = 5.0f,
        TerrainAffectedRangeMeters = 3.0f, // Minimal shoulder for rustic look
        // CrossSectionIntervalMeters validation: (5/2 + 3) / 3 = 1.8m max, using 0.5m ?
        CrossSectionIntervalMeters = 0.5f,

        // SLOPE CONSTRAINTS - Relaxed for natural character
        RoadMaxSlopeDegrees = 12.0f, // Allow steep sections
        SideMaxSlopeDegrees = 45.0f, // Natural embankment
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Very light
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 3, // Odd number ?, minimal smoothing
        SmoothingSigma = 0.5f, // Very light
        SmoothingMaskExtensionMeters = 1.0f, // >= 0.5 * 2 = 1.0m ?
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            SkeletonDilationRadius = 0,
            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f,
            MinPathLengthPixels = 40.0f,

            BridgeEndpointMaxDistancePixels = 25.0f,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.75f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting - looser for natural character
            SplineTension = 0.4f,
            SplineContinuity = 0.3f,
            SplineBias = 0.0f,

            // Box filter for organic feel (intentionally not Butterworth)
            SmoothingWindowSize = 31, // Odd number ?, small window preserves bumps
            UseButterworthFilter = false,
            ButterworthFilterOrder = 2, // Not used
            GlobalLevelingStrength = 0.0f, // Follow terrain closely

            ExportSplineDebugImage = false,
            ExportSkeletonDebugImage = false,
            ExportSmoothedElevationDebugImage = false
        }
    };
}