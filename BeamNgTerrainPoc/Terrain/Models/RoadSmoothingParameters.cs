using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Main parameters for road smoothing algorithm applied to heightmaps.
///     Contains COMMON parameters for all approaches, plus approach-specific sub-parameters.
/// </summary>
public class RoadSmoothingParameters
{
    // ========================================
    // PAINT-ONLY MODE
    // ========================================

    /// <summary>
    ///     When true, this material is in paint-only mode: material painting and master spline export
    ///     happen, but ALL elevation modification is skipped (no smoothing, no blending, no post-processing).
    ///     The spline system (PNG or OSM) still runs to extract road geometry for painting.
    /// </summary>
    public bool PaintOnlyMode { get; set; }

    // ========================================
    // SPLINE PARAMETERS
    // ========================================

    /// <summary>
    ///     Spline-specific parameters for road extraction and smoothing.
    ///     Null = use defaults. Set this to customize spline extraction and smoothing.
    /// </summary>
    public SplineRoadParameters? SplineParameters { get; set; }

    /// <summary>
    ///     Junction and endpoint harmonization parameters.
    ///     Controls how road elevations are blended at intersections and endpoints
    ///     to eliminate discontinuities between road segments.
    ///     Null = use defaults. Set this to customize junction blending behavior.
    /// </summary>
    public JunctionHarmonizationParameters? JunctionHarmonizationParameters { get; set; }

    // ========================================
    // COMMON ROAD GEOMETRY (All Approaches)
    // ========================================

    /// <summary>
    ///     Width of the road surface in meters.
    ///     This is the area that will be completely flattened to target elevation.
    ///     Used for elevation smoothing corridor width.
    ///     Default: 8.0 (typical 2-lane road)
    /// </summary>
    public float RoadWidthMeters { get; set; } = 8.0f;

    /// <summary>
    ///     Width of the road surface for material/layer map drawing in meters.
    ///     This controls how wide the terrain material (e.g., asphalt texture) is painted.
    ///     When null or 0, defaults to RoadWidthMeters for backward compatibility.
    ///     Use cases:
    ///     - Set smaller than RoadWidthMeters to paint narrow road on wider smoothed corridor
    ///     - Set larger than RoadWidthMeters to paint wider texture with narrower smoothing
    ///     Example: RoadWidthMeters=20m (elevation corridor), RoadSurfaceWidthMeters=8m (painted asphalt)
    ///     Default: null (uses RoadWidthMeters)
    /// </summary>
    public float? RoadSurfaceWidthMeters { get; set; }

    /// <summary>
    ///     Gets the effective road surface width for material drawing.
    ///     Returns RoadSurfaceWidthMeters if set and > 0, otherwise falls back to RoadWidthMeters.
    /// </summary>
    public float EffectiveRoadSurfaceWidthMeters =>
        RoadSurfaceWidthMeters.HasValue && RoadSurfaceWidthMeters.Value > 0
            ? RoadSurfaceWidthMeters.Value
            : RoadWidthMeters;

    public float? MasterSplineWidthMeters { get; set; }

    public float EffectiveMasterSplineWidthMeters => MasterSplineWidthMeters ?? EffectiveRoadSurfaceWidthMeters;

    /// <summary>
    ///     Distance from road edge to blend terrain in meters.
    ///     This creates the embankment/transition zone between road and natural terrain.
    ///     Total terrain impact width = RoadWidthMeters + (TerrainAffectedRangeMeters × 2)
    ///     Example: 8m road + (12m × 2) = 32m total width
    ///     Typical values:
    ///     - 8-12m: Narrow mountain road (tight integration)
    ///     - 12-15m: Standard highway (realistic)
    ///     - 20-30m: Wide highway or when using high GlobalLevelingStrength
    ///     Default: 12.0
    /// </summary>
    public float TerrainAffectedRangeMeters { get; set; } = 12.0f;

    /// <summary>
    ///     Buffer distance (in meters) beyond the road edge that is protected
    ///     from other roads' blend zones. Higher values prevent edge damage
    ///     from lower-priority roads meeting this road.
    ///     This creates a "protection zone" around each road that other roads'
    ///     blend zones cannot modify. Useful at intersections where a dirt road
    ///     meets a paved highway - prevents the dirt road's blend from damaging
    ///     the highway edge.
    ///     Typical values:
    ///     - 0.0m: No extra protection (only road core is protected)
    ///     - 2.0m: Default - good for most cases
    ///     - 5.0m+: Larger protection zone for wide roads or aggressive terrain
    ///     Note: Higher values may cause visible "steps" where protection zone ends.
    ///     Default: 2.0
    /// </summary>
    public float RoadEdgeProtectionBufferMeters { get; set; } = 2.0f;

    /// <summary>
    ///     Distance between cross-section samples in meters.
    ///     Smaller values = more accurate but slower processing.
    ///     IMPORTANT: Should be ? (RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3 to avoid gaps!
    ///     Typical values:
    ///     - 0.25-0.5m: Ultra-high detail (racing circuits)
    ///     - 0.5-1.0m: High detail (highways)
    ///     - 1.0-2.0m: Standard detail (local roads)
    ///     Default: 0.5
    /// </summary>
    public float CrossSectionIntervalMeters { get; set; } = 0.5f;

    /// <summary>
    ///     Window size for longitudinal smoothing in meters.
    ///     Affects how smooth the road is along its length direction.
    ///     Default: 20.0
    /// </summary>
    public float LongitudinalSmoothingWindowMeters { get; set; } = 20.0f;

    // ========================================
    // SLOPE CONSTRAINTS (All Approaches)
    // ========================================

    /// <summary>
    ///     Enable enforcement of the maximum road slope constraint.
    ///     When enabled, road elevations are adjusted to ensure no segment exceeds RoadMaxSlopeDegrees.
    ///     Default: false (disabled - road follows smoothed terrain)
    /// </summary>
    public bool EnableMaxSlopeConstraint { get; set; } = false;

    /// <summary>
    ///     Maximum allowed road surface slope in degrees.
    ///     Only enforced when EnableMaxSlopeConstraint = true.
    ///     Prevents unrealistic steepness on the road itself.
    ///     Typical values:
    ///     - 1-2°: Racing circuit (ultra-flat)
    ///     - 4-6°: Highway standard
    ///     - 8-10°: Mountain road (steep but driveable)
    ///     Default: 4.0
    /// </summary>
    public float RoadMaxSlopeDegrees { get; set; } = 4.0f;

    /// <summary>
    ///     Maximum slope for embankments/sides in degrees.
    ///     Controls how sharply terrain transitions from road edge to natural terrain.
    ///     Typical values:
    ///     - 20-25°: Gentle embankment (1:2.5 ratio)
    ///     - 30°: Standard embankment (1:1.7 ratio)
    ///     - 35-40°: Steep embankment (1:1.2 ratio)
    ///     Default: 30.0
    /// </summary>
    public float SideMaxSlopeDegrees { get; set; } = 30.0f;

    // ========================================
    // BLENDING (All Approaches)
    // ========================================

    /// <summary>
    ///     Type of blend function to use for terrain transitions.
    ///     Default: Cosine (smoothest)
    /// </summary>
    public BlendFunctionType BlendFunctionType { get; set; } = BlendFunctionType.Cosine;

    /// <summary>
    ///     If false, skip terrain blending (debug mode: only extract geometry/elevations).
    ///     Default: true
    /// </summary>
    public bool EnableTerrainBlending { get; set; } = true;

    // ========================================
    // POST-PROCESSING SMOOTHING (All Approaches)
    // ========================================

    /// <summary>
    ///     Enable post-processing smoothing to eliminate staircase artifacts on the road surface.
    ///     Applies a Gaussian blur or similar smoothing algorithm masked to the road and shoulder areas.
    ///     This addresses the visible interval-based staircase effect that can occur after blending.
    ///     Default: false (disabled for backward compatibility)
    /// </summary>
    public bool EnablePostProcessingSmoothing { get; set; } = false;

    /// <summary>
    ///     Type of smoothing filter to apply in post-processing.
    ///     Default: Gaussian (best quality, slower)
    /// </summary>
    public PostProcessingSmoothingType SmoothingType { get; set; } = PostProcessingSmoothingType.Gaussian;

    /// <summary>
    ///     Kernel size for the smoothing filter in pixels.
    ///     Larger values = smoother but slower.
    ///     Typical values:
    ///     - 3-5: Light smoothing (preserve detail)
    ///     - 7-9: Medium smoothing (good balance)
    ///     - 11-15: Heavy smoothing (very smooth roads)
    ///     Default: 7
    /// </summary>
    public int SmoothingKernelSize { get; set; } = 7;

    /// <summary>
    ///     Sigma value for Gaussian blur (standard deviation).
    ///     Higher values = more aggressive smoothing.
    ///     Typical values:
    ///     - 0.5-1.0: Light smoothing
    ///     - 1.0-2.0: Medium smoothing
    ///     - 2.0-4.0: Heavy smoothing
    ///     Default: 1.5
    /// </summary>
    public float SmoothingSigma { get; set; } = 1.5f;

    /// <summary>
    ///     Extension of the smoothing mask beyond the road edge in meters.
    ///     This applies smoothing to the shoulder/blend zone as well to ensure continuity.
    ///     Typical values:
    ///     - 2-4m: Smooth only near road edge
    ///     - 4-8m: Smooth into shoulder zone
    ///     - 8-12m: Smooth entire blend zone
    ///     Default: 6.0 (reaches into shoulder)
    /// </summary>
    public float SmoothingMaskExtensionMeters { get; set; } = 6.0f;

    /// <summary>
    ///     Number of smoothing iterations to apply.
    ///     More iterations = smoother result but slower.
    ///     Default: 1
    /// </summary>
    public int SmoothingIterations { get; set; } = 1;

    // ========================================
    // EXCLUSION ZONES (All Approaches)
    // ========================================

    /// <summary>
    ///     Paths to layer maps for areas to exclude from road smoothing.
    ///     White pixels (255) in these layers indicate areas where smoothing should NOT occur.
    /// </summary>
    public List<string>? ExclusionLayerPaths { get; set; }

    /// <summary>
    ///     When true, bridges are excluded from terrain smoothing and material painting.
    ///     When false, bridge ways are treated as normal roads (legacy behavior).
    ///     This prevents bridge structures from modifying the terrain beneath them,
    ///     as bridges should be elevated above the terrain on support structures.
    ///     Default: true (bridges are excluded)
    /// </summary>
    public bool ExcludeBridgesFromTerrain { get; set; } = false;

    /// <summary>
    ///     When true, tunnels are excluded from terrain smoothing and material painting.
    ///     When false, tunnel ways are treated as normal roads (legacy behavior).
    ///     This prevents tunnel structures from modifying the terrain surface,
    ///     as tunnels should pass through the terrain without surface modification.
    ///     Default: true (tunnels are excluded)
    /// </summary>
    public bool ExcludeTunnelsFromTerrain { get; set; } = false;

    // ========================================
    // PRE-BUILT SPLINES (OSM Integration)
    // ========================================

    /// <summary>
    ///     Pre-built road splines from external sources (e.g., OSM Overpass API).
    ///     When set, these splines are used directly instead of extracting from layer map.
    ///     This bypasses skeleton extraction and path finding for road materials.
    /// </summary>
    public List<RoadSpline>? PreBuiltSplines { get; set; }

    /// <summary>
    ///     Names corresponding to each pre-built spline (e.g., OSM feature display names).
    ///     Used when exporting to BeamNG master splines JSON format.
    ///     Index matches PreBuiltSplines list. If null or shorter than PreBuiltSplines,
    ///     missing names will be auto-generated.
    /// </summary>
    public List<string>? PreBuiltSplineNames { get; set; }

    /// <summary>
    ///     When true, the service should use PreBuiltSplines instead of extracting from layer map.
    /// </summary>
    public bool UsePreBuiltSplines => PreBuiltSplines?.Count > 0;

    /// <summary>
    ///     Roundabout processing result from OsmGeometryProcessor.ConvertLinesToSplinesWithRoundabouts().
    ///     Contains information about detected roundabouts, their ring splines, and connection points.
    ///     Used by NetworkJunctionDetector for proper roundabout junction detection.
    ///     Null when roundabout detection was not performed or no roundabouts were found.
    /// </summary>
    public RoundaboutMerger.RoundaboutProcessingResult? RoundaboutProcessingResult { get; set; }

    // ========================================
    // TERRAIN CONTEXT (for export operations)
    // ========================================

    /// <summary>
    ///     Base height (Z position) for the terrain in world units.
    ///     This offset is added to all Z coordinates when exporting splines to BeamNG format.
    ///     Should match TerrainCreationParameters.TerrainBaseHeight.
    ///     Default: 0
    /// </summary>
    public float TerrainBaseHeight { get; set; } = 0.0f;

    /// <summary>
    ///     Distance between nodes in the exported master spline JSON (in meters).
    ///     Controls how many control points are generated for BeamNG's Master Spline tool.
    ///     Lower values = more nodes = finer control but cluttered UI in BeamNG editor.
    ///     Higher values = fewer nodes = cleaner editor, but less detail on curves.
    ///     Typical values:
    ///     - 5-10m: High detail (tight curves, hairpins)
    ///     - 10-20m: Standard (recommended for most roads)
    ///     - 20-50m: Low detail (long straight highways)
    ///     Default: 15.0 (good balance between detail and usability)
    /// </summary>
    public float MasterSplineNodeDistanceMeters { get; set; } = 15.0f;

    // ========================================
    // DEBUG OUTPUT (All Approaches)
    // ========================================

    /// <summary>
    ///     Optional output directory for debug images. If null uses working directory.
    /// </summary>
    public string? DebugOutputDirectory { get; set; }

    /// <summary>
    ///     Export the smoothed heightmap as a grayscale image with road outlines overlaid.
    ///     Shows:
    ///     - Smoothed heightmap as grayscale background (black=low, white=high)
    ///     - Thin cyan outline at road edges (± roadWidth/2)
    ///     - Thin magenta outline at terrain blending edges (± roadWidth/2 + terrainAffectedRange)
    ///     Only works with Spline approach (requires distance field).
    ///     Output file: smoothed_heightmap_with_road_outlines.png
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportSmoothedHeightmapWithOutlines { get; set; } = true;

    // ========================================
    // HELPER METHODS
    // ========================================

    /// <summary>
    ///     Gets or creates the SplineParameters object (auto-creates with defaults if null).
    /// </summary>
    public SplineRoadParameters GetSplineParameters()
    {
        return SplineParameters ??= new SplineRoadParameters();
    }

    /// <summary>
    ///     Gets or creates the JunctionHarmonizationParameters object (auto-creates with defaults if null).
    /// </summary>
    public JunctionHarmonizationParameters GetJunctionHarmonizationParameters()
    {
        return JunctionHarmonizationParameters ??= new JunctionHarmonizationParameters();
    }

    /// <summary>
    ///     Validates all parameters and returns any errors.
    ///     Includes validation for approach-specific parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // Validate common parameters
        if (RoadWidthMeters <= 0)
            errors.Add("RoadWidthMeters must be greater than 0");

        if (RoadSurfaceWidthMeters.HasValue && RoadSurfaceWidthMeters.Value < 0)
            errors.Add("RoadSurfaceWidthMeters must be >= 0 when specified");

        if (MasterSplineWidthMeters.HasValue && MasterSplineWidthMeters.Value < 0)
            errors.Add("MasterSplineWidthMeters must be >= 0 when specified");

        if (TerrainAffectedRangeMeters < 0)
            errors.Add("TerrainAffectedRangeMeters must be >= 0");

        if (RoadEdgeProtectionBufferMeters < 0)
            errors.Add("RoadEdgeProtectionBufferMeters must be >= 0");

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

        // Validate spline parameters
        if (SplineParameters != null) errors.AddRange(SplineParameters.Validate());

        // Validate junction harmonization parameters
        if (JunctionHarmonizationParameters != null) errors.AddRange(JunctionHarmonizationParameters.Validate());

        // Warn about problematic combinations
        {
            var splineParams = GetSplineParameters();

            // Warn about dotted roads risk
            if (splineParams.GlobalLevelingStrength > 0.5f && TerrainAffectedRangeMeters < 15.0f)
                errors.Add($"WARNING: High GlobalLevelingStrength ({splineParams.GlobalLevelingStrength:F2}) " +
                           $"with small TerrainAffectedRangeMeters ({TerrainAffectedRangeMeters}m) " +
                           $"may create disconnected road segments! Recommend: TerrainAffectedRangeMeters ? 20m " +
                           $"OR GlobalLevelingStrength ? 0.5");

            // Warn about insufficient cross-section density
            var totalImpactRadius = RoadWidthMeters / 2.0f + TerrainAffectedRangeMeters;
            var recommendedMaxInterval = totalImpactRadius / 3.0f;
            if (CrossSectionIntervalMeters > recommendedMaxInterval)
                errors.Add($"WARNING: CrossSectionIntervalMeters ({CrossSectionIntervalMeters}m) may cause gaps! " +
                           $"Recommend: ? {recommendedMaxInterval:F2}m for {totalImpactRadius:F1}m impact radius");
        }

        return errors;
    }
}