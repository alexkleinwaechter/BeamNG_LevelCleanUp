// Road Smoothing Parameter Presets
// OSM presets: Optimized for pre-built splines from vector data (use SmoothInterpolated + Chaikin densification)
//
// IMPORTANT PARAMETER RELATIONSHIPS:
//
// 1. OSM pipeline:
//    - Uses SmoothInterpolated splines (Akima/cubic interpolation)
//    - OSM paths are pre-smoothed with Chaikin corner-cutting to densify sparse nodes
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
///     PRESET CATEGORIES:
///     - OSM presets: For pre-built splines from OSM vector data (use SmoothInterpolated + Chaikin densification)
///     ROAD TYPES (5 for OSM):
///     - Highway: Wide roads with aggressive smoothing (10m)
///     - RuralRoad: General-purpose roads for mixed terrain (7m)
///     - MountainRoad: Narrow roads optimized for hairpins and steep terrain (6m)
///     - DirtRoad: Rustic unpaved roads with minimal smoothing (4-5m)
///     - RacingCircuit: Ultra-precise roads for racing tracks (10m)
/// </summary>
public static class RoadSmoothingPresets
{
    #region ========== OSM PRESETS (Pre-built Splines from Vector Data) ==========

    /// <summary>
    ///     OSM HIGHWAY: Major roads from OpenStreetMap (10m surface, 20m smoothing corridor).
    ///     Uses SmoothInterpolated with Chaikin-densified control points for smooth curves.
    ///     Key features:
    ///     - Wide 10m painted surface for major highways
    ///     - 20m elevation smoothing corridor for safe vehicle handling
    ///     - Akima interpolation produces smooth curves through densified OSM nodes
    ///     - Large junction blending for interchange areas
    ///     Best for: OSM motorway, trunk, and primary roads.
    /// </summary>
    public static RoadSmoothingParameters OsmHighway => new()
    {
        // ROAD GEOMETRY - Wide highway with wider smoothing corridor
        RoadWidthMeters = 14.0f,
        RoadSurfaceWidthMeters = 10.0f,
        TerrainAffectedRangeMeters = 12.0f,
        CrossSectionIntervalMeters = 0.4f,

        // SLOPE CONSTRAINTS - Highway grade
        RoadMaxSlopeDegrees = 5.0f,
        SideMaxSlopeDegrees = 30.0f,
        BlendFunctionType = BlendFunctionType.Exponential,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM: SmoothInterpolated for smooth curves (Chaikin densification applied upstream)
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,

            // Not used for OSM but set reasonable defaults
            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 0.0f, // OSM: no min length filter - keep all paths
            BridgeEndpointMaxDistancePixels = 50.0f,
            // Spline fitting

            // Very strong elevation smoothing
            SmoothingWindowSize = 601,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 5.0f,
            JunctionBlendDistanceMeters = 60.0f,
            BlendFunctionType = JunctionBlendFunctionType.CubicHermiteC1
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
        // ROAD GEOMETRY
        RoadWidthMeters = 9.0f,
        RoadSurfaceWidthMeters = 7.0f,
        TerrainAffectedRangeMeters = 10.0f,
        CrossSectionIntervalMeters = 0.4f,

        // SLOPE CONSTRAINTS
        RoadMaxSlopeDegrees = 8.0f,
        SideMaxSlopeDegrees = 50.0f,
        BlendFunctionType = BlendFunctionType.Exponential,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM: SmoothInterpolated for smooth curves (Chaikin densification applied upstream)
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,

            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 0.0f, // OSM: no min length filter - keep all paths
            BridgeEndpointMaxDistancePixels = 40.0f,
            // Spline fitting - balanced

            // Strong elevation smoothing
            SmoothingWindowSize = 401,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 5.0f,
            JunctionBlendDistanceMeters = 50.0f,
            BlendFunctionType = JunctionBlendFunctionType.CubicHermiteC1
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
        // ROAD GEOMETRY - Narrow road with steep sides
        RoadWidthMeters = 6.0f,
        RoadSurfaceWidthMeters = 5.0f,
        TerrainAffectedRangeMeters = 30.0f,
        CrossSectionIntervalMeters = 0.3f,

        // SLOPE CONSTRAINTS - Steep grades allowed
        RoadMaxSlopeDegrees = 10.0f,
        SideMaxSlopeDegrees = 70.0f,
        BlendFunctionType = BlendFunctionType.Exponential,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM: SmoothInterpolated for smooth curves (Chaikin densification applied upstream)
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,

            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.5f,
            MinPathLengthPixels = 0.0f, // OSM: no min length filter - keep all paths
            BridgeEndpointMaxDistancePixels = 30.0f,
            // Tighter spline fitting for curves

            // Moderate elevation smoothing
            SmoothingWindowSize = 401,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 5.0f,
            JunctionBlendDistanceMeters = 50.0f,
            BlendFunctionType = JunctionBlendFunctionType.CubicHermiteC1
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
        // ROAD GEOMETRY - Narrow track
        RoadWidthMeters = 5.0f,
        RoadSurfaceWidthMeters = 4.0f,
        TerrainAffectedRangeMeters = 10.0f,
        CrossSectionIntervalMeters = 0.5f,

        // SLOPE CONSTRAINTS - Relaxed for natural paths
        RoadMaxSlopeDegrees = 15.0f,
        SideMaxSlopeDegrees = 80.0f,
        BlendFunctionType = BlendFunctionType.Exponential,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM: SmoothInterpolated for smooth curves (Chaikin densification applied upstream)
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,

            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 1.0f,
            MinPathLengthPixels = 0.0f, // OSM: no min length filter - keep all paths
            BridgeEndpointMaxDistancePixels = 20.0f,
            // Spline fitting - allows sharp corners

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
            JunctionDetectionRadiusMeters = 5.0f,
            JunctionBlendDistanceMeters = 50.0f,
            BlendFunctionType = JunctionBlendFunctionType.CubicHermiteC1
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
        // ROAD GEOMETRY - Wide racing surface
        RoadWidthMeters = 20.0f,
        RoadSurfaceWidthMeters = 10.0f,
        TerrainAffectedRangeMeters = 20f,
        CrossSectionIntervalMeters = 0.25f,

        // SLOPE CONSTRAINTS - Racing standard
        EnableMaxSlopeConstraint = true,
        RoadMaxSlopeDegrees = 3.0f,
        SideMaxSlopeDegrees = 25.0f,
        BlendFunctionType = BlendFunctionType.Exponential,

        // POST-PROCESSING
        EnablePostProcessingSmoothing = true,
        SmoothingType = PostProcessingSmoothingType.Gaussian,
        SmoothingKernelSize = 5,
        SmoothingSigma = 0.8f,
        SmoothingMaskExtensionMeters = 1f,
        SmoothingIterations = 1,

        SplineParameters = new SplineRoadParameters
        {
            // OSM: SmoothInterpolated for smooth curves (Chaikin densification applied upstream)
            SplineInterpolationType = SplineInterpolationType.SmoothInterpolated,

            SkeletonDilationRadius = 0,
            DensifyMaxSpacingPixels = 2.0f,
            SimplifyTolerancePixels = 0.3f,
            MinPathLengthPixels = 0.0f, // OSM: no min length filter - keep all paths
            BridgeEndpointMaxDistancePixels = 50.0f,
            // Tight spline fitting for precision

            // Elevation smoothing
            SmoothingWindowSize = 601,
            UseButterworthFilter = true,
            ButterworthFilterOrder = 4,
            GlobalLevelingStrength = 0.0f,

            PreferStraightThroughJunctions = false,
            JunctionAngleThreshold = 90.0f
        },

        JunctionHarmonizationParameters = new JunctionHarmonizationParameters
        {
            EnableJunctionHarmonization = true,
            JunctionDetectionRadiusMeters = 5.0f,
            JunctionBlendDistanceMeters = 60.0f,
            BlendFunctionType = JunctionBlendFunctionType.CubicHermiteC1
        }
    };

    #endregion
}