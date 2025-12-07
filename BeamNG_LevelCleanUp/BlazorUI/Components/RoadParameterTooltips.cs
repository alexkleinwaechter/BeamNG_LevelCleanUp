namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
/// Static class containing tooltip content for all road smoothing parameters.
/// Extracted from ROAD_SMOOTHING_PARAMETERS_GUIDE.md to keep TerrainMaterialSettings.razor clean.
/// </summary>
public static class RoadParameterTooltips
{
    // ========================================
    // PRIMARY PARAMETERS
    // ========================================

    public const string RoadWidthMeters = """
        Default: 8.0 | Range: 2.0 to 50.0
        Status: ? ACTIVELY USED

        Width of the road surface in meters. The road surface within this width will be completely flattened.

        Example values:
        • 4.0 - Narrow mountain road or bike path
        • 8.0 - Standard 2-lane road (default)
        • 12.0 - Wide 3-lane highway
        • 16.0 - 4-lane highway
        """;

    public const string TerrainAffectedRangeMeters = """
        Default: 12.0 | Range: 0.0 to 50.0
        Status: ? ACTIVELY USED

        Distance from road edge where terrain blends. Think of it as the "shoulder" or "embankment" distance.

        Total impact width = RoadWidthMeters + (TerrainAffectedRangeMeters × 2)
        Example: 8m road + (12m × 2) = 32m total width affected

        Example values:
        • 6.0 - Tight mountain road
        • 12.0 - Standard highway shoulder (default)
        • 20.0 - Wide highway with gentle embankments
        • 25.0 - Extra wide for global leveling

        ?? If using GlobalLevelingStrength > 0.5, increase to 20-25m!
        """;

    public const string EnableMaxSlopeConstraint = """
        Default: false
        Status: ? ACTIVELY USED

        Enable enforcement of the maximum road slope constraint.
        
        When ENABLED:
        • Road elevations are adjusted to ensure no segment exceeds RoadMaxSlopeDegrees
        • Uses forward-backward passes to "shave off" excessive slopes
        • Creates flatter roads but may disconnect from terrain
        
        When DISABLED (default):
        • Road follows smoothed terrain elevation naturally
        • RoadMaxSlopeDegrees value is ignored
        • More natural terrain integration
        
        Recommended:
        • Enable for racing circuits requiring strict slope limits
        • Enable for ultra-flat artificial road networks
        • Disable for natural mountain roads that follow terrain
        """;

    public const string RoadMaxSlopeDegrees = """
        Default: 4.0 | Range: 1.0 to 45.0
        Status: ?? CONDITIONALLY USED (only when EnableMaxSlopeConstraint = true)

        Maximum steepness allowed on the road surface itself. Think of it as the "incline warning" on highway signs.
        
        ?? Only enforced when EnableMaxSlopeConstraint is enabled!

        Example values:
        • 1.0 - Ultra-flat race track
        • 4.0 - Highway standard (default)
        • 8.0 - Mountain road
        • 12.0 - Steep mountain pass
        """;

    public const string SideMaxSlopeDegrees = """
        Default: 30.0 | Range: 15.0 to 60.0
        Status: ? ACTIVELY USED

        Maximum embankment (road shoulder) slope. The transition zone from road edge to natural terrain.

        • 25° - Gentle embankment (1:2.5 ratio)
        • 30° - Standard embankment (1:1.7 ratio)
        • 40° - Steep embankment (1:1.2 ratio)
        """;

    // ========================================
    // ALGORITHM SETTINGS
    // ========================================

    public const string Approach = """
        Default: DirectMask
        Status: ? ACTIVELY USED

        Choose the smoothing method:

        • DirectMask - Like a paint roller. Simple, works everywhere, handles complex road intersections well. Good for city streets.
        • Spline - Like a precision airbrush. Creates super smooth curves, perfect for highways and race tracks. Not recommended for complex intersections.
        """;

    public const string BlendFunction = """
        Default: Cosine
        Status: ? ACTIVELY USED

        The "smoothing curve" used to blend the road into terrain:

        • Linear - Sharp, straight blend (like a ruler edge)
        • Cosine - Smooth, natural blend (RECOMMENDED)
        • Cubic - Very smooth (S-curve)
        • Quintic - Extra smooth (even smoother S-curve)
        """;

    public const string CrossSectionIntervalMeters = """
        Default: 0.5 | Range: 0.25 to 5.0
        Status: ? ACTIVELY USED

        How often the algorithm "measures" the road. Smaller = more measurements = smoother but slower.

        Should be ? (RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3 to avoid gaps!

        Example values:
        • 0.25 - Ultra-high quality racing circuit (slow)
        • 0.5 - High quality highway (default)
        • 1.0 - Standard quality local road
        • 2.0 - Low quality (may show gaps)

        ?? Auto-adjusted if too high to prevent gaps.
        """;

    public const string EnableTerrainBlending = """
        Default: true
        Status: ? ACTIVELY USED

        If enabled, the road will blend into terrain. If disabled, only extracts road geometry without modifying the heightmap (debug mode).
        """;

    // ========================================
    // SPLINE: CURVE FITTING
    // ========================================

    public const string SplineTension = """
        Default: 0.3 | Range: 0.0 to 1.0
        Status: ? ACTIVELY USED

        Controls how tightly the spline curve follows the skeleton points:

        • 0.0 - Very loose (smooth but may cut corners)
        • 0.5 - Balanced
        • 1.0 - Very tight (follows closely but may be jagged)

        Recommended by road type:
        • 0.1-0.2 - Ultra-smooth highways
        • 0.2-0.3 - General purpose (default)
        • 0.5-0.55 - Hairpins, racing circuits with sharp turns
        """;

    public const string SplineContinuity = """
        Default: 0.5 | Range: -1.0 to 1.0
        Status: ? ACTIVELY USED

        Controls corner smoothness:

        • -1.0 - Sharp corners (allows kinks, direction changes)
        • 0.0 - Balanced
        • 1.0 - Very smooth corners (may miss sharp turns)

        Recommended by road type:
        • -0.5 to 0.0 - City streets, chicanes
        • 0.0 to 0.2 - Hairpin turns, switchbacks
        • 0.5 to 0.8 - Highways, gentle curves
        """;

    public const string SplineBias = """
        Default: 0.0 | Range: -1.0 to 1.0
        Status: ? ACTIVELY USED

        Controls curve direction bias:

        • -1.0 - Bias toward previous point
        • 0.0 - Neutral, symmetric (RECOMMENDED)
        • 1.0 - Bias toward next point

        Keep at 0.0 unless you have a specific artistic reason.
        """;

    // ========================================
    // SPLINE: PATH EXTRACTION
    // ========================================

    public const string SkeletonDilationRadius = """
        Default: 1 | Range: 0 to 5
        Status: ? ACTIVELY USED

        Dilation radius (pixels) applied to road mask before skeletonization. Helps bridge small gaps and improve connectivity.

        ? 0 - No dilation (cleanest skeleton, hairpin-friendly, may miss disconnected fragments)
        ? 1 - Minimal dilation (default, good balance, minimal tail artifacts)
        ? 2 - Moderate dilation (better connectivity, minor blobs at curves)
        ? 3+ - Heavy dilation (maximum connectivity, SIGNIFICANT tail artifacts at hairpins)

        ?? For tight hairpin turns, use 0 to avoid "tail" artifacts that mess up curves!
        """;

    public const string UseGraphOrdering = """
        Default: true
        Status: ? ACTIVELY USED

        Use smart graph-based algorithm to order skeleton points. More robust for complex road networks than simple nearest-neighbor.

        Recommendation: Always use true.
        """;

    public const string DensifyMaxSpacingPixels = """
        Default: 1.5 | Range: 0.5 to 5.0
        Status: ? ACTIVELY USED

        Maximum spacing between skeleton points. If two consecutive points are farther apart, intermediate points are inserted.

        • 0.5 - Very dense (ultra-smooth, slower)
        • 1.5 - Balanced (default)
        • 3.0 - Sparse (faster but less smooth)
        """;

    public const string SimplifyTolerancePixels = """
        Default: 0.5 | Range: 0.0 to 5.0
        Status: ? ACTIVELY USED

        Removes redundant points without changing path shape.

        • 0.0 - Keep all points (most accurate)
        • 0.5 - Minimal simplification (default)
        • 2.0 - Moderate simplification
        • 5.0 - Heavy simplification (straighter paths)
        """;

    public const string BridgeEndpointMaxDistancePixels = """
        Default: 30.0 | Range: 10.0 to 100.0
        Status: ? ACTIVELY USED

        If two skeleton endpoints are closer than this, they'll be connected to bridge the gap.

        • 20.0 - Conservative bridging
        • 30.0 - Balanced (default)
        • 50.0 - Aggressive bridging (may connect unrelated fragments)
        """;

    public const string MinPathLengthPixels = """
        Default: 20.0 | Range: 10.0 to 200.0
        Status: ? ACTIVELY USED

        Discard paths shorter than this length. Helps remove parking lots, driveways, and small fragments.

        • 20.0 - Minimal filtering
        • 50.0 - Standard filtering
        • 100.0 - Aggressive filtering (only major roads)
        """;

    public const string OrderingNeighborRadiusPixels = """
        Default: 2.5 | Range: 1.0 to 10.0
        Status: ? ACTIVELY USED

        Points within this distance are considered neighbors when building the ordering graph.

        • 1.5 - Tight (only immediate neighbors)
        • 2.5 - Balanced (default)
        • 5.0 - Wide (connects distant points, slower)
        """;

    // ========================================
    // SPLINE: JUNCTION HANDLING
    // ========================================

    public const string PreferStraightThroughJunctions = """
        Default: false
        Status: ? ACTIVELY USED

        At intersections, prefer continuing straight rather than taking sharp turns. Extracts the "main road" through intersections.

        ?? Should be FALSE for simple curved roads without intersections!
        Only enable for actual road networks.
        """;

    public const string JunctionAngleThreshold = """
        Default: 45.0 | Range: 15.0 to 90.0
        Status: ?? CONDITIONALLY USED

        Only used when PreferStraightThroughJunctions is enabled. Defines what angle change is considered "straight through."

        • 30° - Very strict (only nearly-straight)
        • 45° - Balanced (default)
        • 60° - Loose (allows gentle curves)
        """;

    // ========================================
    // SPLINE: ELEVATION SMOOTHING
    // ========================================

    public const string SplineSmoothingWindowSize = """
        Default: 101 | Range: 11 to 501 (must be odd)
        Status: ? ACTIVELY USED

        Number of cross-section samples to average for elevation smoothing.

        • 51 - Minimal smoothing (follows terrain closely)
        • 101 - Balanced (default)
        • 201 - Heavy smoothing (highway quality)
        • 301 - Very heavy smoothing (ultra-smooth race track)

        Window size in meters ? SmoothingWindowSize × CrossSectionIntervalMeters
        """;

    public const string SplineUseButterworthFilter = """
        Default: true
        Status: ? ACTIVELY USED

        • Butterworth - Professional quality, maximally flat, sharper cutoff (RECOMMENDED)
        • Disabled - Simple Gaussian averaging, softer transitions

        Butterworth is like a professional audio equalizer (precise, flat response).
        """;

    public const string SplineButterworthFilterOrder = """
        Default: 3 | Range: 1 to 8
        Status: ? ACTIVELY USED (when Butterworth enabled)

        Filter "aggressiveness":
        • 1-2 - Gentle smoothing
        • 3-4 - Aggressive smoothing (RECOMMENDED)
        • 5-6 - Maximum flatness (may introduce subtle ringing)
        """;

    public const string GlobalLevelingStrength = """
        Default: 0.0 | Range: 0.0 to 1.0
        Status: ? ACTIVELY USED

        How much to "level" the road to a global average elevation:

        • 0.0 - Terrain-following (road goes up/down with terrain) - RECOMMENDED
        • 0.5 - Balanced (moderate leveling)
        • 0.9 - Strong leveling (forces road network to similar elevation)

        ?? CRITICAL: If > 0.5, you MUST increase TerrainAffectedRangeMeters to 20-25m or you'll get "dotted road" artifacts!
        """;

    // ========================================
    // DIRECTMASK PARAMETERS
    // ========================================

    public const string DirectMaskSmoothingWindowSize = """
        Default: 10 | Range: 5 to 100
        Status: ? ACTIVELY USED (DirectMask approach)

        Number of elevation samples to average when smoothing road elevation.

        • 5 - Minimal smoothing
        • 10 - Balanced (default)
        • 20 - Heavy smoothing
        • 50 - Very heavy smoothing
        """;

    public const string RoadPixelSearchRadius = """
        Default: 3 | Range: 1 to 10
        Status: ? ACTIVELY USED (DirectMask approach)

        Search distance (pixels) for finding road pixels when sampling elevation.

        • 1 - Minimal search (fast but may miss gaps)
        • 3 - Balanced (default)
        • 5 - Wide search (robust to gaps)
        """;

    public const string DirectMaskUseButterworthFilter = """
        Default: false
        Status: ? ACTIVELY USED (DirectMask approach)

        Same as Spline version. Default is false for DirectMask because it's used for fast testing.
        """;

    public const string DirectMaskButterworthFilterOrder = """
        Default: 3 | Range: 1 to 8
        Status: ?? CONDITIONALLY USED (when Butterworth enabled)

        Same as Spline version - controls filter aggressiveness.
        """;

    // ========================================
    // POST-PROCESSING SMOOTHING
    // ========================================

    public const string EnablePostProcessingSmoothing = """
        Default: false
        Status: ? ACTIVELY USED

        Applies a final smoothing pass to eliminate visible "steps" or "bumps" on the road surface. Like applying a final polish after the main smoothing.

        RECOMMENDED: Enable to eliminate staircase artifacts visible at high speeds.
        """;

    public const string SmoothingType = """
        Default: Gaussian
        Status: ? ACTIVELY USED

        • Gaussian - Best quality, smooth and natural (RECOMMENDED)
        • Box - Fastest, simple averaging
        • Bilateral - Edge-preserving (avoids edges)
        """;

    public const string SmoothingKernelSize = """
        Default: 7 | Range: 3, 5, 7, 9, 11, 13, 15 (must be odd)
        Status: ? ACTIVELY USED

        Smoothing brush size in pixels:
        • 3 - Tiny brush (subtle smoothing)
        • 7 - Medium brush (RECOMMENDED)
        • 11 - Large brush (very smooth)

        ?? Must be odd number!
        """;

    public const string SmoothingSigma = """
        Default: 1.5 | Range: 0.5 to 4.0
        Status: ? ACTIVELY USED (Gaussian/Bilateral)

        Gaussian blur strength (brush pressure):
        • 0.5 - Light touch
        • 1.5 - Medium pressure (default)
        • 3.0 - Heavy pressure
        """;

    public const string SmoothingMaskExtensionMeters = """
        Default: 6.0 | Range: 0.0 to 12.0
        Status: ? ACTIVELY USED

        How far beyond road edge to apply smoothing.

        • 0.0 - Road only (may show edge seam)
        • 6.0 - Road + shoulder (RECOMMENDED)
        • 10.0 - Entire blend zone

        Should be ? TerrainAffectedRangeMeters
        """;

    public const string SmoothingIterations = """
        Default: 1 | Range: 1 to 5
        Status: ? ACTIVELY USED

        Number of smoothing passes:
        • 1 - Single pass (usually sufficient)
        • 2 - Double pass (smoother)
        • 3+ - Rarely needed, may blur too much

        ?? Each iteration multiplies processing time!
        """;

    // ========================================
    // DEBUG OUTPUT
    // ========================================

    public const string ExportSmoothedHeightmapWithOutlines = """
        Default: false
        Status: ? ACTIVELY USED

        Saves smoothed heightmap as grayscale with road outlines overlaid:
        • Cyan outline - Road edges
        • Magenta outline - Terrain blending edges

        Output: smoothed_heightmap_with_road_outlines.png
        """;

    public const string ExportSplineDebugImage = """
        Default: false
        Status: ? ACTIVELY USED (Spline only)

        Saves debug image showing spline centerline and road width:
        • Yellow line - Road centerline
        • Green lines - Road edges (cross-sections)

        Output: spline_debug.png
        """;

    public const string ExportSkeletonDebugImage = """
        Default: false
        Status: ? ACTIVELY USED (Spline only)

        Saves raw skeleton (road centerline) before spline fitting. White lines on black background.

        Helps diagnose skeleton extraction issues.

        Output: skeleton_debug.png
        """;

    public const string ExportSmoothedElevationDebugImage = """
        Default: false
        Status: ? ACTIVELY USED (Spline only)

        Saves road colored by elevation:
        • Blue - Low elevation
        • Green - Medium elevation
        • Red - High elevation

        Output: spline_smoothed_elevation_debug.png
        """;
}
