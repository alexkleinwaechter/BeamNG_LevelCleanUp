// Road Smoothing Parameter Presets
// Organized into two categories:
// - PNG presets: Optimized for skeleton extraction from layer masks (use SmoothInterpolated splines)
// - OSM presets: Optimized for pre-built splines from vector data (use LinearControlPoints)
//
// IMPORTANT PARAMETER RELATIONSHIPS:
// 
// 1. PNG vs OSM differences:
//    - PNG uses SmoothInterpolated splines (smooth curves from jagged skeleton)
//    - OSM uses LinearControlPoints splines (accurate geometry from clean vectors)
//    - PNG benefits from lower DensifyMaxSpacingPixels (1.5f) for better path following
//    - PNG needs SkeletonDilationRadius = 0 for clean skeleton
//
// 2. GlobalLevelingStrength vs TerrainAffectedRangeMeters:
//    - GlobalLevelingStrength > 0.5 requires TerrainAffectedRangeMeters >= 15m
//    - For narrow blend zones (steep terrain beside road), use GlobalLevelingStrength = 0
//
// 3. SmoothingWindowSize and SmoothingKernelSize:
//    - Should be ODD numbers for symmetric smoothing

using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Examples;

/// <summary>
///     Pre-configured road smoothing parameter presets for different road types.
///     
///     PRESET CATEGORIES:
///     - PNG presets: For skeleton extraction from layer masks (use SmoothInterpolated for smooth curves)
///     - OSM presets: For pre-built splines from OSM vector data (use LinearControlPoints for accuracy)
///     
///     ROAD TYPES (5 each for PNG and OSM):
///     - Highway: Wide roads with aggressive smoothing (8-10m)
///     - RuralRoad: General-purpose roads for mixed terrain (7m)
///     - MountainRoad: Narrow roads optimized for hairpins and steep terrain (6m)
///     - DirtRoad: Rustic unpaved roads with minimal smoothing (4-5m)
///     - RacingCircuit: Ultra-precise roads for racing tracks (10m)
/// </summary>
public static class RoadSmoothingPresets
{
    #region ========== PNG PRESETS (Skeleton Extraction from Layer Masks) ==========

    /// <summary>
    ///     PNG HIGHWAY: Professional highway-quality roads from PNG layer masks (8m surface, 16m corridor).
    ///     Uses SmoothInterpolated splines to create smooth curves from skeleton extraction.
    ///     Key features:
    ///     - 8m painted surface for 2-lane highways
    ///     - 16m elevation smoothing corridor for safe vehicle handling
    ///     - Aggressive Butterworth smoothing (order 4, window 301)
    ///     - SmoothInterpolated splines reduce jagged edges from skeleton
    ///     - Low DensifyMaxSpacingPixels (1.5) for accurate path following
    ///     Best for: Highway PNG masks, main roads from layer maps.
    /// </summary>
    public static RoadSmoothingParameters PngHighway => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Wide highway with wider smoothing corridor
        RoadWidthMeters = 16.0f,
        RoadSurfaceWidthMeters = 8.0f,
        TerrainAffectedRangeMeters = 10.0f,
        CrossSectionIntervalMeters = 0.4f,

        // SLOPE CONSTRAINTS
        RoadMaxSlopeDegrees = 6.0f,
        SideMaxSlopeDegrees = 35.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7,
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 1.5f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // PNG-SPECIFIC: Use SmoothInterpolated to reduce skeleton jaggedness
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,
            
            // Skeleton extraction parameters
            SkeletonDilationRadius = 0, // Clean skeleton for PNG
            DensifyMaxSpacingPixels = 1.5f, // Better path following for PNG
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 100.0f,
            BridgeEndpointMaxDistancePixels = 40.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting
            SplineTension = 0.2f,
            SplineContinuity = 0.7f,
            SplineBias = 0.0f,

            // Elevation smoothing - aggressive for flat road surface
            SmoothingWindowSize = 301,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 25.0f,
            JunctionBlendDistanceMeters = 50.0f,
            BlendFunctionType = JunctionBlendFunctionType.Cosine,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 40.0f,
            EndpointTerrainBlendStrength = 0.2f
        }
    };

    /// <summary>
    ///     PNG RURAL ROAD: General-purpose roads from PNG layer masks (7m surface, 12m corridor).
    ///     Balanced settings for mixed terrain - good for countryside and suburban roads.
    ///     Key features:
    ///     - 7m painted surface (typical 2-lane rural road)
    ///     - 12m elevation smoothing corridor for safety margin
    ///     - Moderate smoothing window (201)
    ///     - SmoothInterpolated for natural-looking curves
    ///     Best for: General road PNG masks, mixed road networks.
    /// </summary>
    public static RoadSmoothingParameters PngRuralRoad => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY
        RoadWidthMeters = 12.0f,
        RoadSurfaceWidthMeters = 7.0f,
        TerrainAffectedRangeMeters = 8.0f,
        CrossSectionIntervalMeters = 0.4f,

        // SLOPE CONSTRAINTS
        RoadMaxSlopeDegrees = 8.0f,
        SideMaxSlopeDegrees = 35.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7,
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 2.0f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // PNG-SPECIFIC: Use SmoothInterpolated to reduce skeleton jaggedness
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,
            
            // Skeleton extraction parameters
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 50.0f,
            BridgeEndpointMaxDistancePixels = 40.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting - balanced
            SplineTension = 0.3f,
            SplineContinuity = 0.5f,
            SplineBias = 0.0f,

            // Elevation smoothing - moderate
            SmoothingWindowSize = 201,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 20.0f,
            JunctionBlendDistanceMeters = 40.0f,
            BlendFunctionType = JunctionBlendFunctionType.Cosine,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 30.0f,
            EndpointTerrainBlendStrength = 0.3f
        }
    };

    /// <summary>
    ///     PNG MOUNTAIN ROAD: Narrow roads for steep terrain from PNG masks (5m surface, 6m corridor).
    ///     Optimized for tight hairpin turns and switchbacks.
    ///     Key features:
    ///     - Narrow 5m painted surface for authentic mountain passes
    ///     - 6m elevation smoothing corridor (tight for steep terrain)
    ///     - Small TerrainAffectedRangeMeters (4m) for steep terrain beside road
    ///     - Tighter spline fitting (higher tension, lower continuity)
    ///     - Smaller smoothing window (201) for responsive curves
    ///     Best for: Mountain road PNG masks, winding cliff roads, hairpin turns.
    /// </summary>
    public static RoadSmoothingParameters PngMountainRoad => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Narrow road with steep sides
        RoadWidthMeters = 6.0f,
        RoadSurfaceWidthMeters = 5.0f,
        TerrainAffectedRangeMeters = 4.0f,
        CrossSectionIntervalMeters = 0.3f,

        // SLOPE CONSTRAINTS - Steep grades allowed
        RoadMaxSlopeDegrees = 10.0f,
        SideMaxSlopeDegrees = 50.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Minimal to preserve steep embankments
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1.0f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // PNG-SPECIFIC: Use SmoothInterpolated for smooth curves from skeleton
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,
            
            // Skeleton extraction parameters
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 80.0f,
            BridgeEndpointMaxDistancePixels = 30.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // TIGHT SPLINE FITTING for hairpins
            SplineTension = 0.5f,
            SplineContinuity = 0.2f,
            SplineBias = 0.0f,

            // Elevation smoothing - responsive for curves
            SmoothingWindowSize = 201,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 15.0f,
            JunctionBlendDistanceMeters = 30.0f,
            BlendFunctionType = JunctionBlendFunctionType.Quintic,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 20.0f,
            EndpointTerrainBlendStrength = 0.4f
        }
    };

    /// <summary>
    ///     PNG DIRT ROAD: Rustic unpaved roads from PNG masks (4m surface, 5m corridor).
    ///     Minimal smoothing preserves natural terrain character.
    ///     Key features:
    ///     - Narrow 4m painted surface for rustic character
    ///     - 5m elevation smoothing corridor
    ///     - Box filter instead of Butterworth for organic feel
    ///     - Small smoothing window (31) preserves bumps
    ///     Best for: Trail PNG masks, forest paths, farm roads.
    /// </summary>
    public static RoadSmoothingParameters PngDirtRoad => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Narrow dirt road
        RoadWidthMeters = 5.0f,
        RoadSurfaceWidthMeters = 4.0f,
        TerrainAffectedRangeMeters = 3.0f,
        CrossSectionIntervalMeters = 0.5f,

        // SLOPE CONSTRAINTS - Relaxed for natural character
        RoadMaxSlopeDegrees = 12.0f,
        SideMaxSlopeDegrees = 45.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Very light
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 3,
        SmoothingSigma = 0.5f,
        SmoothingMaskExtensionMeters = 1.0f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // PNG-SPECIFIC: Use SmoothInterpolated
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,
            
            // Skeleton extraction parameters
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.75f,
            MinPathLengthPixels = 40.0f,
            BridgeEndpointMaxDistancePixels = 25.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting - looser for natural character
            SplineTension = 0.4f,
            SplineContinuity = 0.3f,
            SplineBias = 0.0f,

            // Box filter for organic feel
            SmoothingWindowSize = 31,
            UseButterworthFilter = false,
            ButterworthFilterOrder = 2,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 12.0f,
            JunctionBlendDistanceMeters = 20.0f,
            BlendFunctionType = JunctionBlendFunctionType.Cosine,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 15.0f,
            EndpointTerrainBlendStrength = 0.5f
        }
    };

    /// <summary>
    ///     PNG RACING CIRCUIT: Ultra-precise tracks from PNG masks (10m surface, 14m corridor).
    ///     Optimized for tight hairpins and glass-smooth surface.
    ///     Key features:
    ///     - Wide 10m painted surface for racing
    ///     - 14m elevation smoothing corridor for smooth transitions
    ///     - Ultra-dense cross-section sampling (0.25m)
    ///     - Very tight spline following (high tension, low continuity)
    ///     - Heavy post-processing for glass-smooth surface
    ///     Best for: Racing circuit PNG masks, karting tracks.
    /// </summary>
    public static RoadSmoothingParameters PngRacingCircuit => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Wide racing surface
        RoadWidthMeters = 14.0f,
        RoadSurfaceWidthMeters = 10.0f,
        TerrainAffectedRangeMeters = 8.0f,
        CrossSectionIntervalMeters = 0.25f,

        // SLOPE CONSTRAINTS - Racing standard
        EnableMaxSlopeConstraint = true,
        RoadMaxSlopeDegrees = 3.0f,
        SideMaxSlopeDegrees = 25.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Heavy for glass-smooth surface
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 9,
        SmoothingSigma = 2.0f,
        SmoothingMaskExtensionMeters = 1.0f,
        SmoothingIterations = 2,

        SplineParameters = new SplineRoadParameters
        {
            // PNG-SPECIFIC: Use SmoothInterpolated for smooth curves
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,
            
            // Skeleton extraction parameters
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 1.5f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 60.0f,
            BridgeEndpointMaxDistancePixels = 50.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // HAIRPIN-OPTIMIZED SPLINE FITTING
            SplineTension = 0.55f,
            SplineContinuity = 0.15f,
            SplineBias = 0.0f,

            // Elevation smoothing
            SmoothingWindowSize = 201,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 20.0f,
            JunctionBlendDistanceMeters = 60.0f,
            BlendFunctionType = JunctionBlendFunctionType.Quintic,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 50.0f,
            EndpointTerrainBlendStrength = 0.15f
        }
    };

    #endregion

    #region ========== OSM PRESETS (Pre-built Splines from Vector Data) ==========

    /// <summary>
    ///     OSM HIGHWAY: Major roads from OpenStreetMap (10m surface, 20m smoothing corridor).
    ///     Uses LinearControlPoints for accurate geometry from clean OSM vectors.
    ///     Key features:
    ///     - Wide 10m painted surface for major highways
    ///     - 20m elevation smoothing corridor for safe vehicle handling
    ///     - LinearControlPoints preserves accurate OSM geometry
    ///     - Large junction blending for interchange areas
    ///     Best for: OSM motorway, trunk, and primary roads.
    /// </summary>
    public static RoadSmoothingParameters OsmHighway => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Wide highway with wider smoothing corridor
        RoadWidthMeters = 20.0f,
        RoadSurfaceWidthMeters = 10.0f,
        TerrainAffectedRangeMeters = 12.0f,
        CrossSectionIntervalMeters = 0.4f,

        // SLOPE CONSTRAINTS - Highway grade
        RoadMaxSlopeDegrees = 5.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 9,
        SmoothingSigma = 2.0f,
        SmoothingMaskExtensionMeters = 2.5f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM-SPECIFIC: Use LinearControlPoints for accurate geometry
            SplineInterpolationType = SplineInterpolationType.LinearControlPoints,
            
            // Not used for OSM but set reasonable defaults
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 100.0f,
            BridgeEndpointMaxDistancePixels = 50.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting
            SplineTension = 0.2f,
            SplineContinuity = 0.7f,
            SplineBias = 0.0f,

            // Very strong elevation smoothing
            SmoothingWindowSize = 301,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 30.0f,
            JunctionBlendDistanceMeters = 60.0f,
            BlendFunctionType = JunctionBlendFunctionType.Quintic,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 50.0f,
            EndpointTerrainBlendStrength = 0.2f
        }
    };

    /// <summary>
    ///     OSM RURAL ROAD: General-purpose roads from OpenStreetMap (7m surface, 12m corridor).
    ///     Balanced settings for residential, secondary, and tertiary roads.
    ///     Key features:
    ///     - Moderate 7m surface width (typical for mixed road networks)
    ///     - 12m elevation smoothing corridor for safety margin
    ///     - Junction harmonization for OSM intersection handling
    ///     Best for: OSM secondary, tertiary, residential, and unclassified roads.
    /// </summary>
    public static RoadSmoothingParameters OsmRuralRoad => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY
        RoadWidthMeters = 12.0f,
        RoadSurfaceWidthMeters = 7.0f,
        TerrainAffectedRangeMeters = 8.0f,
        CrossSectionIntervalMeters = 0.4f,

        // SLOPE CONSTRAINTS
        RoadMaxSlopeDegrees = 8.0f,
        SideMaxSlopeDegrees = 35.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 7,
        SmoothingSigma = 1.5f,
        SmoothingMaskExtensionMeters = 2.0f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM-SPECIFIC: Use LinearControlPoints for accurate geometry
            SplineInterpolationType = SplineInterpolationType.LinearControlPoints,
            
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 50.0f,
            BridgeEndpointMaxDistancePixels = 40.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting - balanced
            SplineTension = 0.3f,
            SplineContinuity = 0.5f,
            SplineBias = 0.0f,

            // Strong elevation smoothing
            SmoothingWindowSize = 301,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 20.0f,
            JunctionBlendDistanceMeters = 40.0f,
            BlendFunctionType = JunctionBlendFunctionType.Cosine,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 30.0f,
            EndpointTerrainBlendStrength = 0.3f
        }
    };

    /// <summary>
    ///     OSM MOUNTAIN ROAD: Narrow mountain roads from OpenStreetMap (6m wide).
    ///     Optimized for steep terrain with tight curves.
    ///     Key features:
    ///     - Narrow 6m width for authentic mountain character
    ///     - Small blend zone (4m) for steep embankments
    ///     - Tighter spline parameters for curves
    ///     Best for: OSM secondary/tertiary roads in mountainous areas.
    /// </summary>
    public static RoadSmoothingParameters OsmMountainRoad => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Narrow road with steep sides
        RoadWidthMeters = 6.0f,
        TerrainAffectedRangeMeters = 4.0f,
        CrossSectionIntervalMeters = 0.3f,

        // SLOPE CONSTRAINTS - Steep grades allowed
        RoadMaxSlopeDegrees = 10.0f,
        SideMaxSlopeDegrees = 50.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Minimal
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1.0f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM-SPECIFIC: Use LinearControlPoints for accurate geometry
            SplineInterpolationType = SplineInterpolationType.LinearControlPoints,
            
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 50.0f,
            BridgeEndpointMaxDistancePixels = 30.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Tighter spline fitting for curves
            SplineTension = 0.4f,
            SplineContinuity = 0.3f,
            SplineBias = 0.0f,

            // Moderate elevation smoothing
            SmoothingWindowSize = 201,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 15.0f,
            JunctionBlendDistanceMeters = 30.0f,
            BlendFunctionType = JunctionBlendFunctionType.Quintic,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 20.0f,
            EndpointTerrainBlendStrength = 0.4f
        }
    };

    /// <summary>
    ///     OSM DIRT ROAD: Tracks and paths from OpenStreetMap (4m wide).
    ///     Minimal smoothing for natural-looking trails.
    ///     Key features:
    ///     - Narrow 4m width for tracks/paths
    ///     - Light smoothing preserves natural terrain following
    ///     - Short paths are filtered (10m minimum)
    ///     Best for: OSM track, path, footway, and cycleway.
    /// </summary>
    public static RoadSmoothingParameters OsmDirtRoad => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Narrow track
        RoadWidthMeters = 4.0f,
        TerrainAffectedRangeMeters = 2.0f,
        CrossSectionIntervalMeters = 0.5f,

        // SLOPE CONSTRAINTS - Relaxed for natural paths
        RoadMaxSlopeDegrees = 15.0f,
        SideMaxSlopeDegrees = 50.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Light
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 3,
        SmoothingSigma = 0.5f,
        SmoothingMaskExtensionMeters = 1.0f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM-SPECIFIC: Use LinearControlPoints
            SplineInterpolationType = SplineInterpolationType.LinearControlPoints,
            
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 1.0f,
            MinPathLengthPixels = 10.0f,
            BridgeEndpointMaxDistancePixels = 20.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Spline fitting - allows sharp corners
            SplineTension = 0.4f,
            SplineContinuity = 0.2f,
            SplineBias = 0.0f,

            // Light elevation smoothing - natural feel
            SmoothingWindowSize = 51,
            UseButterworthFilter = false,
            ButterworthFilterOrder = 2,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 8.0f,
            JunctionBlendDistanceMeters = 15.0f,
            BlendFunctionType = JunctionBlendFunctionType.Cosine,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 10.0f,
            EndpointTerrainBlendStrength = 0.6f
        }
    };

    /// <summary>
    ///     OSM RACING CIRCUIT: Precision racing tracks from OpenStreetMap (10m wide).
    ///     Ultra-precise settings for racing environments.
    ///     Key features:
    ///     - Wide 10m surface for racing
    ///     - Dense cross-section sampling (0.25m)
    ///     - Tight spline fitting for precise curves
    ///     - Heavy post-processing for smooth surface
    ///     Best for: OSM raceway tags, custom racing circuits.
    /// </summary>
    public static RoadSmoothingParameters OsmRacingCircuit => new()
    {
        EnableTerrainBlending = true,

        // ROAD GEOMETRY - Wide racing surface
        RoadWidthMeters = 10.0f,
        TerrainAffectedRangeMeters = 8.0f,
        CrossSectionIntervalMeters = 0.25f,

        // SLOPE CONSTRAINTS - Racing standard
        EnableMaxSlopeConstraint = true,
        RoadMaxSlopeDegrees = 3.0f,
        SideMaxSlopeDegrees = 25.0f,
        BlendFunctionType = BlendFunctionType.Cosine,

        // POST-PROCESSING - Heavy for smooth surface
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 9,
        SmoothingSigma = 2.0f,
        SmoothingMaskExtensionMeters = 1.0f,
        SmoothingIterations = 2,

        SplineParameters = new SplineRoadParameters
        {
            // OSM-SPECIFIC: Use LinearControlPoints for accurate geometry
            SplineInterpolationType = SplineInterpolationType.LinearControlPoints,
            
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.3f,
            MinPathLengthPixels = 60.0f,
            BridgeEndpointMaxDistancePixels = 50.0f,
            UseGraphOrdering = true,
            OrderingNeighborRadiusPixels = 2.5f,

            // Tight spline fitting for precision
            SplineTension = 0.5f,
            SplineContinuity = 0.2f,
            SplineBias = 0.0f,

            // Elevation smoothing
            SmoothingWindowSize = 201,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 20.0f,
            JunctionBlendDistanceMeters = 60.0f,
            BlendFunctionType = JunctionBlendFunctionType.Quintic,
            EnableEndpointTaper = true,
            EndpointTaperDistanceMeters = 50.0f,
            EndpointTerrainBlendStrength = 0.15f
        }
    };

    #endregion
}
