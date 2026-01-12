namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
///     Static class containing tooltip content for all road smoothing parameters.
///     Extracted from ROAD_SMOOTHING_PARAMETERS_GUIDE.md to keep TerrainMaterialSettings.razor clean.
///     NOTE: Use only ASCII characters to avoid encoding issues in Blazor WebView hosted in WinForms.
/// </summary>
public static class RoadParameterTooltips
{
    // ========================================
    // SPLINE INTERPOLATION TYPE
    // ========================================

    public const string SplineInterpolationType = """
                                                  Default: Smooth Interpolated
                                                  Status: ACTIVELY USED

                                                  Controls how road centerlines are interpolated between control points.
                                                  This affects both elevation smoothing AND material painting consistency.

                                                  Options:
                                                  - Smooth Interpolated (default) - Uses Akima/cubic spline interpolation
                                                    for smooth, natural curves. Best for highways, racing circuits, and
                                                    scenic roads where smooth curves are important.
                                                    Trade-off: May deviate slightly from original skeleton/OSM path.

                                                  - Linear Control Points - Uses linear interpolation between original
                                                    control points. Best for accurate adherence to source geometry
                                                    (skeleton extraction or OSM vectors).
                                                    Trade-off: Less smooth curves, may have visible segments at corners.

                                                  ===
                                                  WHY SMOOTH INTERPOLATED IS THE DEFAULT (best for PNG sources):
                                                  ===
                                                  PNG layer masks require skeleton extraction to find road centerlines.
                                                  This process produces jagged, pixelated paths with many small direction
                                                  changes at the pixel level. Smooth Interpolated applies curve smoothing
                                                  to these raw skeleton points, creating natural-looking roads without
                                                  the staircase artifacts that would result from connecting pixels with
                                                  straight lines.

                                                  -> PNG source: Use Smooth Interpolated (default) for best results

                                                  ===
                                                  OSM SOURCES - BOTH OPTIONS WORK WELL:
                                                  ===
                                                  OSM (OpenStreetMap) provides clean vector geometry with accurate road
                                                  positions already defined by mappers. Since the source data is already
                                                  smooth and accurate, both interpolation types produce good results:

                                                  - Linear Control Points - Follows the exact OSM geometry faithfully.
                                                    Recommended when you want roads to match real-world positions precisely.
                                                    The built-in OSM presets use this setting by default.

                                                  - Smooth Interpolated - Adds extra curve smoothing to OSM geometry.
                                                    Use if OSM data has minor kinks you want to smooth out, or if you
                                                    prefer smoother aesthetic curves over geographic accuracy.
                                                    May cause roads to deviate slightly from real-world positions.

                                                  -> OSM source: Either works; Linear for accuracy, Smooth for aesthetics

                                                  ===
                                                  QUICK REFERENCE:
                                                  ===
                                                  - PNG layer map -> Smooth Interpolated (smooths jagged skeleton pixels)
                                                  - OSM roads -> Linear Control Points (preserves accurate geometry)
                                                                 OR Smooth Interpolated (if you want extra smoothing)

                                                  [!] IMPORTANT: This setting ensures elevation smoothing and material
                                                  painting use the SAME spline path. If roads appear to "cut corners"
                                                  in the terrain but not in the painted texture, try Linear Control Points.
                                                  """;

    // ========================================
    // PRIMARY PARAMETERS
    // ========================================

    public const string RoadWidthMeters = """
                                          Default: 8.0 | Range: 2.0 to 50.0
                                          Status: ACTIVELY USED

                                          Width of the road surface in meters. This is the ELEVATION SMOOTHING corridor width.
                                          The terrain within this width will be completely flattened to target elevation.

                                          Example values:
                                          - 4.0 - Narrow mountain road or bike path
                                          - 8.0 - Standard 2-lane road (default)
                                          - 12.0 - Wide 3-lane highway
                                          - 20.0 - Wide smoothing corridor for safety margin

                                          Tip: Use a wider Road Width for the smoothing corridor, and a narrower
                                          Road Surface Width for the actual painted material (e.g., asphalt texture).
                                          """;

    public const string RoadSurfaceWidthMeters = """
                                                 Default: Same as Road Width | Range: 0.0 to 50.0
                                                 Status: ACTIVELY USED (OSM mode and informational for PNG mode)

                                                 Width of the PAINTED MATERIAL on the terrain in meters.
                                                 This controls how wide the terrain texture (e.g., asphalt) is drawn.

                                                 When empty or 0: Uses Road Width value (backward compatible).

                                                 Use cases:
                                                 - Road Width=20m, Surface Width=8m - Wide smoothed corridor, narrow painted road
                                                 - Road Width=8m, Surface Width=12m - Narrow smoothing, wide texture

                                                 Example values:
                                                 - 4.0 - Single lane / trail
                                                 - 7.0 - Typical 2-lane road
                                                 - 8.0 - Standard road (default)
                                                 - 12.0 - Wide highway

                                                 OSM Mode: This width is used to rasterize the layer map from OSM lines.
                                                 PNG Mode: Informational only - your PNG already defines the painted width.
                                                 """;

    public const string TerrainAffectedRangeMeters = """
                                                     Default: 12.0 | Range: 0.0 to 50.0
                                                     Status: ACTIVELY USED

                                                     Distance from road edge where terrain blends. Think of it as the "shoulder" or "embankment" distance.

                                                     Total impact width = RoadWidthMeters + (TerrainAffectedRangeMeters x 2)
                                                     Example: 8m road + (12m x 2) = 32m total width affected

                                                     Example values:
                                                     - 6.0 - Tight mountain road
                                                     - 12.0 - Standard highway shoulder (default)
                                                     - 20.0 - Wide highway with gentle embankments
                                                     - 25.0 - Extra wide for global leveling

                                                     [!] If using GlobalLevelingStrength > 0.5, increase to 20-25m!
                                                     """;

    public const string EnableMaxSlopeConstraint = """
                                                   Default: false
                                                   Status: ACTIVELY USED

                                                   Enable enforcement of the maximum road slope constraint.

                                                   When ENABLED:
                                                   - Road elevations are adjusted to ensure no segment exceeds RoadMaxSlopeDegrees
                                                   - Uses forward-backward passes to "shave off" excessive slopes
                                                   - Creates flatter roads but may disconnect from terrain

                                                   When DISABLED (default):
                                                   - Road follows smoothed terrain elevation naturally
                                                   - RoadMaxSlopeDegrees value is ignored
                                                   - More natural terrain integration

                                                   Recommended:
                                                   - Enable for racing circuits requiring strict slope limits
                                                   - Enable for ultra-flat artificial road networks
                                                   - Disable for natural mountain roads that follow terrain
                                                   """;

    public const string RoadMaxSlopeDegrees = """
                                              Default: 4.0 | Range: 1.0 to 45.0
                                              Status: CONDITIONALLY USED (only when EnableMaxSlopeConstraint = true)

                                              Maximum steepness allowed on the road surface itself. Think of it as the "incline warning" on highway signs.

                                              [!] Only enforced when EnableMaxSlopeConstraint is enabled!

                                              Example values:
                                              - 1.0 - Ultra-flat race track
                                              - 4.0 - Highway standard (default)
                                              - 8.0 - Mountain road
                                              - 12.0 - Steep mountain pass
                                              """;

    public const string SideMaxSlopeDegrees = """
                                              Default: 30.0 | Range: 15.0 to 60.0
                                              Status: ACTIVELY USED

                                              Maximum embankment (road shoulder) slope. The transition zone from road edge to natural terrain.

                                              - 25 deg - Gentle embankment (1:2.5 ratio)
                                              - 30 deg - Standard embankment (1:1.7 ratio)
                                              - 40 deg - Steep embankment (1:1.2 ratio)
                                              """;

    public const string RoadEdgeProtectionBuffer = """
                                                   Default: 2.0 | Range: 0.0 to 20.0
                                                   Status: ACTIVELY USED

                                                   Distance (in meters) beyond the road edge that is protected from other roads' blend zones.
                                                   Higher values prevent lower-priority roads from damaging this road's edges at intersections.

                                                   This creates a "protection zone" around each road that other roads' blend zones cannot modify.
                                                   Useful when a dirt road meets a paved highway - prevents the dirt road's blend from damaging
                                                   the highway edge.

                                                   Example values:
                                                   - 0.0m - No extra protection (only road core is protected)
                                                   - 2.0m - Default, good for most cases
                                                   - 5.0m - Larger protection for wide roads
                                                   - 10.0m+ - Aggressive protection (may cause visible steps)

                                                   Increase if you see edge artifacts where roads meet. Higher values may cause visible "steps"
                                                   where the protection zone ends.
                                                   """;

    // ========================================
    // ALGORITHM SETTINGS
    // ========================================

    public const string BlendFunction = """
                                        Default: Cosine
                                        Status: ACTIVELY USED

                                        The "smoothing curve" used to blend the road into terrain:

                                        - Linear - Sharp, straight blend (like a ruler edge)
                                        - Cosine - Smooth, natural blend (RECOMMENDED)
                                        - Cubic - Very smooth (S-curve)
                                        - Quintic - Extra smooth (even smoother S-curve)
                                        """;

    public const string CrossSectionIntervalMeters = """
                                                     Default: 0.5 | Range: 0.25 to 5.0
                                                     Status: ACTIVELY USED

                                                     How often the algorithm "measures" the road. Smaller = more measurements = smoother but slower.

                                                     Should be (RoadWidthMeters/2 + TerrainAffectedRangeMeters) / 3 to avoid gaps!

                                                     Example values:
                                                     - 0.25 - Ultra-high quality racing circuit (slow)
                                                     - 0.5 - High quality highway (default)
                                                     - 1.0 - Standard quality local road
                                                     - 2.0 - Low quality (may show gaps)

                                                     [!] Auto-adjusted if too high to prevent gaps.
                                                     """;

    public const string EnableTerrainBlending = """
                                                Default: true
                                                Status: ACTIVELY USED

                                                If enabled, the road will blend into terrain. If disabled, only extracts road geometry without modifying the heightmap (debug mode).
                                                """;

    // ========================================
    // SPLINE: CURVE FITTING
    // ========================================

    public const string SplineTension = """
                                        Default: 0.3 | Range: 0.0 to 1.0
                                        Status: ACTIVELY USED

                                        Controls how tightly the spline curve follows the skeleton points:

                                        - 0.0 - Very loose (smooth but may cut corners)
                                        - 0.5 - Balanced
                                        - 1.0 - Very tight (follows closely but may be jagged)

                                        Recommended by road type:
                                        - 0.1-0.2 - Ultra-smooth highways
                                        - 0.2-0.3 - General purpose (default)
                                        - 0.5-0.55 - Hairpins, racing circuits with sharp turns
                                        """;

    public const string SplineContinuity = """
                                           Default: 0.5 | Range: -1.0 to 1.0
                                           Status: ACTIVELY USED

                                           Controls corner smoothness:

                                           - -1.0 - Sharp corners (allows kinks, direction changes)
                                           - 0.0 - Balanced
                                           - 1.0 - Very smooth corners (may miss sharp turns)

                                           Recommended by road type:
                                           - -0.5 to 0.0 - City streets, chicanes
                                           - 0.0 to 0.2 - Hairpin turns, switchbacks
                                           - 0.5 to 0.8 - Highways, gentle curves
                                           """;

    public const string SplineBias = """
                                     Default: 0.0 | Range: -1.0 to 1.0
                                     Status: ACTIVELY USED

                                     Controls curve direction bias:

                                     - -1.0 - Bias toward previous point
                                     - 0.0 - Neutral, symmetric (RECOMMENDED)
                                     - 1.0 - Bias toward next point

                                     Keep at 0.0 unless you have a specific artistic reason.
                                     """;

    // ========================================
    // SPLINE: PATH EXTRACTION
    // ========================================

    public const string SkeletonDilationRadius = """
                                                 Default: 1 | Range: 0 to 5
                                                 Status: ACTIVELY USED

                                                 Dilation radius (pixels) applied to road mask before skeletonization. Helps bridge small gaps and improve connectivity.

                                                 0 - No dilation (cleanest skeleton, hairpin-friendly, may miss disconnected fragments)
                                                 1 - Minimal dilation (default, good balance, minimal tail artifacts)
                                                 2 - Moderate dilation (better connectivity, minor blobs at curves)
                                                 3+ - Heavy dilation (maximum connectivity, SIGNIFICANT tail artifacts at hairpins)

                                                 [!] For tight hairpin turns, use 0 to avoid "tail" artifacts that mess up curves!
                                                 """;

    public const string UseGraphOrdering = """
                                           Default: true
                                           Status: ACTIVELY USED

                                           Use smart graph-based algorithm to order skeleton points. More robust for complex road networks than simple nearest-neighbor.

                                           Recommendation: Always use true.
                                           """;

    public const string DensifyMaxSpacingPixels = """
                                                  Default: 1.5 | Range: 0.5 to 5.0
                                                  Status: ACTIVELY USED

                                                  Maximum spacing between skeleton points. If two consecutive points are farther apart, intermediate points are inserted.

                                                  - 0.5 - Very dense (ultra-smooth, slower)
                                                  - 1.5 - Balanced (default)
                                                  - 3.0 - Sparse (faster but less smooth)
                                                  """;

    public const string SimplifyTolerancePixels = """
                                                  Default: 0.5 | Range: 0.0 to 5.0
                                                  Status: ACTIVELY USED

                                                  Removes redundant points without changing path shape.

                                                  - 0.0 - Keep all points (most accurate)
                                                  - 0.5 - Minimal simplification (default)
                                                  - 2.0 - Moderate simplification
                                                  - 5.0 - Heavy simplification (straighter paths)
                                                  """;

    public const string BridgeEndpointMaxDistancePixels = """
                                                          Default: 30.0 | Range: 10.0 to 100.0
                                                          Status: ACTIVELY USED

                                                          If two skeleton endpoints are closer than this, they'll be connected to bridge the gap.

                                                          - 20.0 - Conservative bridging
                                                          - 30.0 - Balanced (default)
                                                          - 50.0 - Aggressive bridging (may connect unrelated fragments)
                                                          """;

    public const string MinPathLengthPixels = """
                                              Default: 20.0 | Range: 10.0 to 200.0
                                              Status: ACTIVELY USED

                                              Discard paths shorter than this length. Helps remove parking lots, driveways, and small fragments.

                                              - 20.0 - Minimal filtering
                                              - 50.0 - Standard filtering
                                              - 100.0 - Aggressive filtering (only major roads)
                                              """;

    public const string OrderingNeighborRadiusPixels = """
                                                       Default: 2.5 | Range: 1.0 to 10.0
                                                       Status: ACTIVELY USED

                                                       Points within this distance are considered neighbors when building the ordering graph.

                                                       - 1.5 - Tight (only immediate neighbors)
                                                       - 2.5 - Balanced (default)
                                                       - 5.0 - Wide (connects distant points, slower)
                                                       """;

    // ========================================
    // SPLINE: JUNCTION HANDLING
    // ========================================

    public const string PreferStraightThroughJunctions = """
                                                         Default: false
                                                         Status: ACTIVELY USED

                                                         At intersections, prefer continuing straight rather than taking sharp turns. Extracts the "main road" through intersections.

                                                         [!] Should be FALSE for simple curved roads without intersections!
                                                         Only enable for actual road networks.
                                                         """;

    public const string JunctionAngleThreshold = """
                                                 Default: 45.0 | Range: 15.0 to 90.0
                                                 Status: CONDITIONALLY USED

                                                 Only used when PreferStraightThroughJunctions is enabled. Defines what angle change is considered "straight through."

                                                 - 30 deg - Very strict (only nearly-straight)
                                                 - 45 deg - Balanced (default)
                                                 - 60 deg - Loose (allows gentle curves)
                                                 """;

    // ========================================
    // SPLINE: ELEVATION SMOOTHING
    // ========================================

    public const string SplineSmoothingWindowSize = """
                                                    Default: 101 | Range: 11 to 1001 (must be odd)
                                                    Status: ACTIVELY USED

                                                    Number of cross-section samples to average for elevation smoothing.
                                                    With a higher value, you can bridge gaps which may appear in the terrain.
                                                    If you decrease CrossSectionIntervalMeters, you may need to increase this value to maintain the same smoothing window in meters.

                                                    - 51 - Minimal smoothing (follows terrain closely)
                                                    - 101 - Balanced (default)
                                                    - 201 - Heavy smoothing (highway quality)
                                                    - 301 - Very heavy smoothing (ultra-smooth race track)

                                                    Window size in meters = SmoothingWindowSize x CrossSectionIntervalMeters
                                                    """;

    public const string SplineUseButterworthFilter = """
                                                     Default: true
                                                     Status: ACTIVELY USED

                                                     - Butterworth - Professional quality, maximally flat, sharper cutoff (RECOMMENDED)
                                                     - Disabled - Simple Gaussian averaging, softer transitions

                                                     Butterworth is like a professional audio equalizer (precise, flat response).
                                                     """;

    public const string SplineButterworthFilterOrder = """
                                                       Default: 3 | Range: 1 to 8
                                                       Status: ACTIVELY USED (when Butterworth enabled)

                                                       Filter "aggressiveness":
                                                       - 1-2 - Gentle smoothing
                                                       - 3-4 - Aggressive smoothing (RECOMMENDED)
                                                       - 5-6 - Maximum flatness (may introduce subtle ringing)
                                                       """;

    public const string GlobalLevelingStrength = """
                                                 Default: 0.0 | Range: 0.0 to 1.0
                                                 Status: ACTIVELY USED

                                                 How much to "level" the road to a global average elevation:

                                                 - 0.0 - Terrain-following (road goes up/down with terrain) - RECOMMENDED
                                                 - 0.5 - Balanced (moderate leveling)
                                                 - 0.9 - Strong leveling (forces road network to similar elevation)

                                                 [!] CRITICAL: If > 0.5, you MUST increase TerrainAffectedRangeMeters to 20-25m or you'll get "dotted road" artifacts!
                                                 """;

    // ========================================
    // POST-PROCESSING SMOOTHING
    // ========================================

    public const string EnablePostProcessingSmoothing = """
                                                        Default: false
                                                        Status: ACTIVELY USED

                                                        Applies a final smoothing pass to eliminate visible "steps" or "bumps" on the road surface. Like applying a final polish after the main smoothing.

                                                        RECOMMENDED: Enable to eliminate staircase artifacts visible at high speeds.
                                                        """;

    public const string SmoothingType = """
                                        Default: Gaussian
                                        Status: ACTIVELY USED

                                        - Gaussian - Best quality, smooth and natural (RECOMMENDED)
                                        - Box - Fastest, simple averaging
                                        - Bilateral - Edge-preserving (avoids edges)
                                        """;

    public const string SmoothingKernelSize = """
                                              Default: 7 | Range: 3, 5, 7, 9, 11, 13, 15 (must be odd)
                                              Status: ACTIVELY USED

                                              Smoothing brush size in pixels:
                                              - 3 - Tiny brush (subtle smoothing)
                                              - 7 - Medium brush (RECOMMENDED)
                                              - 11 - Large brush (very smooth)

                                              [!] Must be odd number!
                                              """;

    public const string SmoothingSigma = """
                                         Default: 1.5 | Range: 0.5 to 4.0
                                         Status: ACTIVELY USED (Gaussian/Bilateral)

                                         Gaussian blur strength (brush pressure):
                                         - 0.5 - Light touch
                                         - 1.5 - Medium pressure (default)
                                         - 3.0 - Heavy pressure
                                         """;

    public const string SmoothingMaskExtensionMeters = """
                                                       Default: 6.0 | Range: 0.0 to 12.0
                                                       Status: ACTIVELY USED

                                                       How far beyond road edge to apply smoothing.

                                                       - 0.0 - Road only (may show edge seam)
                                                       - 6.0 - Road + shoulder (RECOMMENDED)
                                                       - 10.0 - Entire blend zone

                                                       Should be <= TerrainAffectedRangeMeters
                                                       """;

    public const string SmoothingIterations = """
                                              Default: 1 | Range: 1 to 5
                                              Status: ACTIVELY USED

                                              Number of smoothing passes:
                                              - 1 - Single pass (usually sufficient)
                                              - 2 - Double pass (smoother)
                                              - 3+ - Rarely needed, may blur too much

                                              [!] Each iteration multiplies processing time!
                                              """;

    // ========================================
    // DEBUG OUTPUT
    // ========================================

    public const string ExportSmoothedHeightmapWithOutlines = """
                                                              Default: false
                                                              Status: ACTIVELY USED

                                                              Saves the UNIFIED smoothed heightmap as grayscale with road outlines overlaid.
                                                              Shows the combined result of all road materials processed as a single network.

                                                              - Cyan outline - Road edges
                                                              - Magenta outline - Terrain blending edges

                                                              Output: smoothed_heightmap_with_road_outlines.png

                                                              Note: This shows the final unified network result after all materials 
                                                              have been processed together, not individual material outputs.
                                                              """;

    public const string ExportSplineDebugImage = """
                                                 Default: false
                                                 Status: ACTIVELY USED

                                                 Saves debug image showing spline centerline and road width for THIS material.
                                                 Each material's spline debug is saved to a subfolder.

                                                 - Yellow line - Road centerline
                                                 - Green lines - Road edges (cross-sections)

                                                 Output: spline_debug.png (in material-specific subfolder)

                                                 Note: This is per-material debug output. The unified network result
                                                 can be seen in the heightmap with outlines export.
                                                 """;

    public const string ExportSkeletonDebugImage = """
                                                   Default: false
                                                   Status: ACTIVELY USED (PNG layer source only)

                                                   Saves raw skeleton (road centerline) before spline fitting.
                                                   White lines on black background.

                                                   [!] PNG LAYER SOURCE ONLY - Not available for OSM splines!
                                                   OSM splines are generated directly from vector data and bypass
                                                   skeleton extraction entirely.

                                                   Helps diagnose skeleton extraction issues when using PNG layer maps.

                                                   Output: skeleton_debug.png
                                                   """;

    public const string ExportSmoothedElevationDebugImage = """
                                                            Default: false
                                                            Status: ACTIVELY USED

                                                            Saves road colored by elevation:
                                                            - Blue - Low elevation
                                                            - Green - Medium elevation
                                                            - Red - High elevation

                                                            Output: spline_smoothed_elevation_debug.png
                                                            """;

    public const string ExportJunctionDebugImage = """
                                                   Default: false
                                                   Status: ACTIVELY USED

                                                   Exports debug image showing detected junctions including CROSS-MATERIAL connections.
                                                   The unified pipeline detects junctions across all road materials.

                                                   Color coding:
                                                   - Blue regions - Elevation was lowered
                                                   - Red regions - Elevation was raised
                                                   - Green circles - Multi-path junctions (where roads meet)
                                                   - Yellow markers - Isolated endpoints (dead ends)

                                                   When cross-material harmonization is enabled, this shows where highways
                                                   connect to dirt roads, where different road types meet, etc.

                                                   Output: junction_debug.png
                                                   """;

    // ========================================
    // MASTER SPLINE EXPORT
    // ========================================

    public const string MasterSplineNodeDistanceMeters = """
                                                         Default: 15.0 | Range: 5.0 to 100.0
                                                         Status: ACTIVELY USED

                                                         Distance between nodes in the exported master_splines.json file (in meters).

                                                         This controls how many control points (nodes) are generated for BeamNG's
                                                         Master Spline tool. Each node appears as a handle in the BeamNG editor.

                                                         - Lower values = More nodes = Finer control but cluttered UI
                                                         - Higher values = Fewer nodes = Cleaner editor but less detail on curves

                                                         Recommended by road type:
                                                         - 5-10m - High detail (tight curves, hairpins, racing circuits)
                                                         - 10-20m - Standard roads (default: 15m is a good balance)
                                                         - 20-50m - Low detail (long straight highways)
                                                         - 50-100m - Minimal detail (very long straights)

                                                         [!] For BeamNG import:
                                                         The exported master_splines.json can be imported into BeamNG using the
                                                         Master Spline Tool mod. More nodes = more handles to adjust in the editor,
                                                         so choose a balance between precision and usability.
                                                         """;
}