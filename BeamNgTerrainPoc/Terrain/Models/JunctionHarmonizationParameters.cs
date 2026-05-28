namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Parameters for junction and endpoint elevation harmonization.
///     Controls how road elevations are blended at intersections and endpoints
///     to eliminate discontinuities.
/// </summary>
public class JunctionHarmonizationParameters
{
    // ========================================
    // MASTER ENABLE
    // ========================================

    /// <summary>
    ///     Enable junction elevation harmonization.
    ///     When enabled, road elevations at intersections and endpoints are smoothed
    ///     to eliminate discontinuities.
    ///     Default: true
    /// </summary>
    public bool EnableJunctionHarmonization { get; set; } = true;

    // ========================================
    // PHASE 1.9 — JUNCTION ELEVATION PINNING
    // ========================================

    /// <summary>
    ///     W1 — primary pinning feature. When true, JunctionElevationPinner runs between
    ///     Phase 1.8 (junction detection) and Phase 2 (network smoothing). It writes
    ///     HarmonizedElevation for Endpoint/T/Y/X/Complex junctions so terminating
    ///     roads ramp into a fixed Z and continuous roads slope across it untouched.
    ///     Requires <see cref="EnableJunctionHarmonization" /> = true (otherwise junction
    ///     detection itself doesn't run, leaving the pinner with nothing to act on).
    ///     Default: true (opt-in until Steps 1-3 pass on validation maps).
    ///     not for ui, no control needed
    /// </summary>
    public bool EnablePhase19JunctionPinning { get; set; } = true;

    /// <summary>
    ///     W2 — AASHTO §4.1.5 grade-skip rule (Wang 2011). When the natural Phase-2
    ///     grade and the pinned-junction grade differ by ≤ GradeSkipThresholdPercent,
    ///     the Hermite ramp is skipped on this leg (no kink possible, no benefit).
    ///     Default: false. Permanent toggle even after Step 4.
    ///     obsolete should be removed
    /// </summary>
    public bool EnableHermiteGradeSkip { get; set; } = false;

    /// <summary>
    ///     W2 threshold in percent. Default 0.5 % per AASHTO Green Book.
    ///     obsolete should be removed with its code
    /// </summary>
    public float GradeSkipThresholdPercent { get; set; } = 0.5f;

    /// <summary>
    ///     W3 — AASHTO §4.1.5 class-dependent max-grade clamp (Wang 2011). After Hermite
    ///     ramp samples are placed, clamp any segment grade that exceeds the class-keyed
    ///     maximum (motorway 3 %, primary/secondary 5 %, residential/service 7 %, anything
    ///     else 9 %). Belt-and-braces over R7 (slope kink in steep terrain).
    ///     Default: false. Permanent toggle even after Step 4.
    ///     obsolete should be removed with its code
    /// </summary>
    public bool EnableMaxGradeClamp { get; set; } = false;

    /// <summary>
    ///     Phase A — parabolic junction blend. When true, BlendSplineProfile uses a
    ///     parabolic profile substitution inside each end's blend zone instead of the
    ///     legacy h00-weighted additive delta correction. Eliminates the "up-then-down"
    ///     overshoot at terminating-road junction endpoints on steep terrain (R7 kink).
    ///     Two-end overlap (short splines) still uses the existing h00 combination.
    ///     Known limitation: bumps at overlapping splines with significant elevation
    ///     differences remain — caused by Step 5b mid-spline propagation overlay, not
    ///     by the blend zone itself. Phase A.5 addresses overlap taper separately.
    ///     Default: true (enabled after Phase A franco_same_prio validation).
    /// </summary>
    public bool EnableParabolicJunctionBlend { get; set; } = false;

    /// <summary>
    ///     Phase D — when true, BlendSplineProfileParabolic and
    ///     BlendShortConnectorCompositional write Hermite-h00-blended
    ///     BankAngleRadians through the blend zone, mirroring the legacy h00
    ///     path's symmetric (elevation, bank) behavior. When false, the
    ///     parabolic paths leave BankAngleRadians at its pre-blend (natural)
    ///     value — historical pre-fix behavior which produces cross-slope
    ///     wedge artefacts at primary-vs-terminating bank mismatches. Default
    ///     true; the flag exists only as a regression escape hatch.
    /// </summary>
    public bool EnableParabolicBankBlend { get; set; } = true;

    /// <summary>
    ///     Phase A.5 — propagation/overlap taper. When true, propagated mid-spline
    ///     influences applied in <see cref="UnifiedJunctionProfileBlender.ApplyUnifiedProfiles" />
    ///     Step 5b are weight-tapered toward zero at any directly-anchored junction's
    ///     anchor node whose blend zone they overlap. Eliminates the j126-style cliff
    ///     where a propagated influence from a far-side junction overrides a
    ///     parabolic-blended end zone. Taper is C¹ smoothstep on the geometric
    ///     distance ratio; it never references terrain grade.
    ///     Default: false (opt-in until validation on franco_same_prio passes).
    /// </summary>
    public bool EnablePropagationOverlapTaper { get; set; } = true;

    /// <summary>
    ///     Phase A.8.2 — surface-pass priority override. When true, Pass 1 of
    ///     <see cref="BeamNgTerrainPoc.Terrain.Algorithms.Blending.RoadMaskBuilder" />
    ///     resolves contested pixels (where two splines' painted-surface polygons
    ///     geometrically overlap at a junction) by letting the strictly-higher-Priority
    ///     spline take ownership, instead of the legacy width-first first-writer-wins.
    ///     Pass 2 (corridor stamps) is unaffected and remains width-first.
    ///     Fixes the T-junction surface-vs-surface bump where a wider terminating side
    ///     road's pinned-up elevation contaminates a higher-priority through road.
    ///     Default: false (opt-in until validation on franco_same_prio passes).
    ///     not for ui, no control needed
    /// </summary>
    public bool EnableSurfacePriorityOverride { get; set; } = true;

    /// <summary>
    ///     Phase A.8 — painted-road-width protection. When true,
    ///     <see cref="BeamNgTerrainPoc.Terrain.Algorithms.Blending.RoadMaskBuilder.BuildCombinedMaskWithElevation" />
    ///     runs as a two-pass rasterizer: Pass 1 stamps each spline's painted-surface polygon
    ///     only (no smoothing margin, no edge protection buffer), widest-surface-first; Pass 2
    ///     extends with the corridor + edge buffer into pixels not yet claimed by Pass 1.
    ///     Result: each spline's painted-surface pixels carry that spline's own banking-aware
    ///     elevation even when a wider adjacent spline's corridor geometrically overlaps.
    ///     Default: false (opt-in until franco_same_prio validation passes).
    ///     not for ui, no control needed
    /// </summary>
    public bool EnableSurfaceWidthProtection { get; set; } = true;

    /// <summary>
    ///     Phase B.1 — AASHTO K-value cap on adaptive blend distance. When true,
    ///     <see cref="UnifiedJunctionProfileBlender" /> computes the K-cap
    ///     L_cap = K(speed, sag/crest) · |chordGrade − junctionGrade| × 100 and
    ///     returns min(adaptiveSlopeBased, L_cap). K is a ceiling derived from
    ///     stopping-sight-distance geometry for the spline's effective design
    ///     speed (OSM road type when present, else material DesignSpeedKmh
    ///     override, else residential default). Terrain grade always wins —
    ///     the cap never extends L. Default: false (opt-in until validation).
    /// </summary>
    public bool EnableAashtoBlendDistanceCap { get; set; } = true;

    /// <summary>
    ///     Phase B.4 — dead-end terrain-slope match. When true,
    ///     <c>ComputeEndpointConstraints</c> samples the natural terrain gradient
    ///     at the endpoint position (projected onto the spline tangent) and uses
    ///     it as the constraint slope instead of the hardcoded 0f. When true,
    ///     Step 6 (<c>ApplyEndpointTapering</c>) is also skipped because the
    ///     blender's parabolic/cubic path now produces the slope-matched profile
    ///     directly — running the legacy taper would override and undo it.
    ///     Eliminates the "flat platform → ramp" artefact at dead ends on
    ///     sloped terrain. Default: false (opt-in).
    /// </summary>
    public bool EnableEndpointTerrainSlopeMatch { get; set; } = true;

    /// <summary>
    ///     Phase B.2 — short connector compositional blend. When true, the
    ///     <c>startBlendDist + endBlendDist > roadLength</c> branch of
    ///     <see cref="UnifiedJunctionProfileBlender" />.<c>BlendSplineProfileParabolic</c>
    ///     replaces the legacy h00 fall-through with a per-end parabolic (or cubic
    ///     when B.3 is on) profile blend, weighted by <see cref="OverlapTaper" />.
    ///     Each end's profile dominates near its own anchor; the two compose
    ///     smoothly in the overlap region. Default: false (opt-in).
    /// </summary>
    public bool EnableShortConnectorBlend { get; set; } = true;

    /// <summary>
    ///     Phase B.3 — blend-zone-end C1 continuity via 4-constraint cubic. When
    ///     true, <c>BlendSplineProfileParabolic</c>'s single-end branches use
    ///     <see cref="CubicJunctionProfile" />.Sample with mNaturalAtL read from
    ///     the natural Phase-2 slope at d=L+ε. Eliminates the slope kink where
    ///     the parabolic seam meets the natural profile. Guarded: when the
    ///     sample point is inside another junction's claim (detected via
    ///     <c>SplineClaimedZones.HasOtherClaimNear</c>), falls back to the
    ///     3-constraint <see cref="ParabolicJunctionProfile" /> for that side so
    ///     prior junction harmonization is preserved. Default: false (opt-in).
    /// </summary>
    public bool EnableBlendZoneEndC1 { get; set; } = false;

    /// <summary>
    ///     Phase C — stretch the blend distance L so the parabola's emergent slope at
    ///     d=L matches the natural Phase-2 slope just past the blend zone. Solves
    ///     L_target = 2·(zNaturalAtL − zJunction) / (mNaturalAtL + mJunction) per side,
    ///     using the natural-at-L sample collected by <c>BlendSplineProfileParabolic</c>,
    ///     then re-samples natural elevation at the stretched L so the parabola lands
    ///     on the actual natural profile. Eliminates the slope-kink artefact that
    ///     B.3 (rejected) attacked by curving harder in fixed L; instead extends
    ///     length so the existing parabola eases into natural grade. Clamped by:
    ///     L_current floor (never shorten), AASHTO K-cap ceiling (when B.1 on),
    ///     and the opposite-end blend distance (hard geometric ceiling, never run
    ///     into the other junction). Default: false (opt-in until validation).
    /// </summary>
    public bool EnableBlendDistanceStretchToMatchSlope { get; set; } = true;

    /// <summary>
    ///     Phase B diagnostics. When true, <see cref="UnifiedJunctionProfileBlender" />
    ///     emits two CSVs into MT_TerrainGeneration at the end of ApplyUnifiedProfiles:
    ///     <list type="bullet">
    ///         <item>
    ///             phase_b_short_connectors.csv — one row per spline that has
    ///             both end constraints, with overlap_m = max(0, startBlendDist + endBlendDist − roadLength).
    ///         </item>
    ///         <item>
    ///             phase_b_slope_mismatch.csv — one row per blend-zone end,
    ///             comparing the parabolic-slope-at-L to the natural-grade-at-L+ε
    ///             (used to characterise the B.3 symptom magnitude on real data).
    ///         </item>
    ///     </list>
    ///     Side-effect-free; safe to run with any combination of B.1/B.2/B.3.
    ///     Default: false.
    /// </summary>
    public bool EnablePhaseBDiagnostics { get; set; } = false;

    // ========================================
    // PHASE B.1 — DESIGN SPEED FOR K-VALUE CAP
    // ========================================

    /// <summary>
    ///     Phase B.1 — material-level design speed override (km/h) used by the
    ///     AASHTO K-value cap when the spline has NO OSM road type (PNG pipeline).
    ///     When OSM data is present, the spline's <c>OsmRoadType</c> determines
    ///     design speed and this value is IGNORED. Null = falls back to residential
    ///     default (30 km/h). Set via the per-material editor in
    ///     <c>TerrainMaterialSettings.razor</c>; round-tripped through preset
    ///     export/import as <c>junctionHarmonization.designSpeedKmh</c>.
    /// </summary>
    public int? DesignSpeedKmh { get; set; }

    // ========================================
    // JUNCTION DETECTION
    // ========================================

    /// <summary>
    ///     Maximum distance (in meters) between a path endpoint and another road to detect a junction.
    ///     This should be small - just enough to account for the road width + small tolerance.
    ///     For T-junctions: An endpoint touching the side of another road will be detected
    ///     if the distance is within this radius.
    ///     Typical values:
    ///     - 5-8m: Narrow roads (single lane)
    ///     - 8-12m: Standard roads (DEFAULT - covers ~8m road width + tolerance)
    ///     - 12-15m: Wide roads (highways)
    ///     Default: 10.0
    /// </summary>
    public float JunctionDetectionRadiusMeters { get; set; } = 5.0f;

    // ========================================
    // JUNCTION BLENDING
    // ========================================

    /// <summary>
    ///     Minimum distance (in meters) over which to blend from junction elevation back to path elevation.
    ///     This affects the SIDE ROAD that joins the main road - the side road's elevation
    ///     will smoothly transition from the main road's elevation back to its own calculated elevation.
    ///     On steep terrain, the actual blend distance automatically increases beyond this minimum
    ///     to keep the ramp slope within the road's max slope limit (or 6° default).
    ///     Formula: max(this, elevDiff / tan(maxSlopeDeg)).
    ///     Typical values:
    ///     - 15-25m: Tight blending (urban roads)
    ///     - 25-40m: Standard blending
    ///     - 40-60m: Smooth blending (DEFAULT)
    ///     - 60-100m: Very smooth blending (highways)
    ///     Default: 50.0
    /// </summary>
    public float JunctionBlendDistanceMeters { get; set; } = 50.0f;

    /// <summary>
    ///     Blend function type for junction transitions.
    ///     Default: CubicHermiteC1 (C1-continuous, matches slope at blend boundary)
    /// </summary>
    public JunctionBlendFunctionType BlendFunctionType { get; set; } = JunctionBlendFunctionType.CubicHermiteC1;

    // ========================================
    // MULTI-WAY JUNCTION BLENDING
    // ========================================

    /// <summary>
    ///     Enable dominant road detection at multi-way junctions (Y, CrossRoads, Complex).
    ///     When enabled, junctions where one road is significantly wider or higher priority
    ///     are treated as multi-T-junctions: the dominant road passes through unmodified,
    ///     other roads get edge-anchored constraints matching the dominant road's surface.
    ///     When disabled, all multi-way junctions use the legacy priority-weighted average.
    ///     Default: true
    /// </summary>
    public bool EnableMultiWayDominantRoadDetection { get; set; } = true;

    /// <summary>
    ///     Width ratio threshold for dominant road detection.
    ///     A road is considered dominant if its width >= this ratio × the average width of other roads.
    ///     Typical values:
    ///     - 1.3: Aggressive (detects slight width differences)
    ///     - 1.5: Standard (DEFAULT)
    ///     - 2.0: Conservative (only very clear dominance)
    ///     Default: 1.5
    /// </summary>
    public float DominantRoadWidthRatio { get; set; } = 1.5f;

    // ========================================
    // ROUNDABOUT SETTINGS
    // ========================================

    /// <summary>
    ///     When true, automatically detect and handle roundabouts from OSM data.
    ///     Roundabout segments (tagged with junction=roundabout) are merged into
    ///     single ring splines, and connecting roads form T-junctions with the ring.
    ///     Default: true
    /// </summary>
    public bool EnableRoundaboutDetection { get; set; } = true;

    /// <summary>
    ///     When true, automatically trim connecting roads that overlap with roundabout rings.
    ///     This removes the high-angle segments that create quirky splines and elevation spikes.
    ///     Problem: OSM roads often share multiple nodes with roundabouts, creating:
    ///     - High-angle turns where the road follows the circular path
    ///     - Weird elevation changes
    ///     - Quirky spline geometry with bumps and jumps
    ///     Solution: Cut roads at the FIRST point where they touch the roundabout
    ///     and delete the portion that overlaps with/follows the ring.
    ///     STRONGLY RECOMMENDED to keep enabled.
    ///     Default: true
    /// </summary>
    public bool EnableRoundaboutRoadTrimming { get; set; } = true;

    /// <summary>
    ///     Detection radius for roundabout connections (in meters).
    ///     Roads within this distance of a roundabout ring are considered connected.
    ///     Typical values:
    ///     - 5-8m: Tight detection (may miss some connections)
    ///     - 8-12m: Standard detection (DEFAULT)
    ///     - 12-15m: Loose detection (may catch unrelated roads)
    ///     Default: 10.0
    /// </summary>
    public float RoundaboutConnectionRadiusMeters { get; set; } = 10.0f;

    /// <summary>
    ///     Tolerance for determining if a road point is "on" the roundabout ring (in meters).
    ///     Points within this distance of the ring radius are considered overlapping
    ///     and will be trimmed when EnableRoundaboutRoadTrimming is true.
    ///     Typical values:
    ///     - 1.0m: Tight tolerance (only trim points very close to the ring)
    ///     - 2.0m: Standard tolerance (DEFAULT)
    ///     - 3.0m: Loose tolerance (more aggressive trimming)
    ///     Default: 2.0
    /// </summary>
    public float RoundaboutOverlapToleranceMeters { get; set; } = 2.0f;

    /// <summary>
    ///     When true, force uniform elevation around roundabout rings.
    ///     The elevation is calculated as the weighted average of terrain elevation
    ///     at the ring position and connecting road elevations. All connecting roads
    ///     are blended toward this single elevation, which may cause artificial
    ///     bumps or dips for roads that naturally approach at different elevations.
    ///     When false, allow gradual elevation changes around the ring following
    ///     the natural terrain. Each connecting road blends toward the local ring
    ///     elevation at its specific connection point, avoiding artificial elevation
    ///     changes. This is more appropriate for roundabouts on sloped terrain.
    ///     Default: true
    /// </summary>
    public bool ForceUniformRoundaboutElevation { get; set; } = true;

    /// <summary>
    ///     Distance (in meters) over which to blend connecting road elevation toward the roundabout ring.
    ///     This controls how smoothly roads transition to match the roundabout's uniform elevation.
    ///     If not set (null), uses JunctionBlendDistanceMeters as the default.
    ///     Capped at 75% of road length to avoid affecting the far end of the road.
    ///     Typical values:
    ///     - 25-35m: Tight transition (urban roundabouts)
    ///     - 40-60m: Standard transition (DEFAULT)
    ///     - 60-100m: Smooth transition (highway roundabouts, sloped terrain)
    ///     Default: 50.0
    /// </summary>
    public float? RoundaboutBlendDistanceMeters { get; set; } = 50.0f;

    /// <summary>
    ///     Gets the effective roundabout blend distance.
    ///     Returns RoundaboutBlendDistanceMeters if set, otherwise JunctionBlendDistanceMeters.
    /// </summary>
    public float EffectiveRoundaboutBlendDistanceMeters =>
        RoundaboutBlendDistanceMeters ?? JunctionBlendDistanceMeters;

    // ========================================
    // IDW WEIGHT MODIFIER FOR TERRAIN BLENDING (WI-8)
    // ========================================

    /// <summary>
    ///     Enable junction-aware IDW weight modification for terrain blending (Phase 4).
    ///     When enabled, terminating roads near junctions get reduced IDW weights,
    ///     letting the continuous road's elevation profile dominate the junction area
    ///     in blend zones. Only affects OSM roads (PNG roads use single-spline interpolation).
    ///     Default: true
    /// </summary>
    public bool EnableJunctionIdwFiltering { get; set; } = true;

    /// <summary>
    ///     Minimum IDW weight modifier for terminating road cross-sections at the junction point.
    ///     0.0 = fully suppress terminating road's influence at the junction.
    ///     0.3 = retain 30% influence (some blending for smoother transitions).
    ///     1.0 = effectively disabled (no suppression).
    ///     Only affects cross-sections from roads that TERMINATE at a junction;
    ///     continuous roads always keep modifier = 1.0.
    ///     Typical values:
    ///     - 0.05-0.1: Strong suppression (DEFAULT - nearly suppress, retain small influence)
    ///     - 0.2-0.4: Moderate suppression (gentler blending)
    ///     - 0.5-0.8: Light suppression
    ///     Default: 0.1
    /// </summary>
    public float MinTerminatingIdwWeight { get; set; } = 0.1f;

    /// <summary>
    ///     Distance (in meters) over which the IDW weight modifier tapers from 1.0 to MinTerminatingIdwWeight.
    ///     If null, uses the junction blend distance (same as elevation harmonization).
    ///     Typical values: same as or slightly larger than JunctionBlendDistanceMeters.
    ///     Default: null (use junction blend distance)
    /// </summary>
    public float? IdwFilterTaperDistanceMeters { get; set; } = null;

    // ========================================
    // DEBUG OPTIONS
    // All debug images are always exported to the MT_TerrainGeneration folder.
    // ========================================

    /// <summary>
    ///     Export debug image showing detected junctions and blend zones.
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportJunctionDebugImage { get; set; } = true;

    /// <summary>
    ///     Export debug image showing roundabout detection and road trimming.
    ///     The debug image shows:
    ///     - Original road paths in gray (semi-transparent) for comparison
    ///     - Roundabout rings in yellow
    ///     - Connection/trim points marked with circles (white outline, green fill)
    ///     - Trimmed/deleted road portions in red
    ///     - Connecting roads (after trimming) in cyan
    ///     - Roundabout centers marked with crosshairs
    ///     Default: true (always export debug images to MT_TerrainGeneration folder)
    /// </summary>
    public bool ExportRoundaboutDebugImage { get; set; } = true;

    // ========================================
    // EFFECTIVE VALUE METHODS
    // ========================================

    /// <summary>
    ///     Gets the effective junction blend distance.
    ///     Returns JunctionBlendDistanceMeters directly.
    /// </summary>
    /// <param name="roadWidthMeters">Unused, kept for API compatibility.</param>
    public float GetEffectiveBlendDistance(float roadWidthMeters)
    {
        return JunctionBlendDistanceMeters;
    }

    /// <summary>
    ///     Gets the effective roundabout blend distance.
    ///     Returns RoundaboutBlendDistanceMeters if set, otherwise JunctionBlendDistanceMeters.
    /// </summary>
    /// <param name="roadWidthMeters">Unused, kept for API compatibility.</param>
    public float GetEffectiveRoundaboutBlendDistance(float roadWidthMeters)
    {
        return RoundaboutBlendDistanceMeters ?? JunctionBlendDistanceMeters;
    }

    /// <summary>
    ///     Validates the junction harmonization parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (JunctionDetectionRadiusMeters <= 0)
            errors.Add("JunctionDetectionRadiusMeters must be greater than 0");

        if (JunctionBlendDistanceMeters <= 0)
            errors.Add("JunctionBlendDistanceMeters must be greater than 0");

        if (RoundaboutConnectionRadiusMeters <= 0)
            errors.Add("RoundaboutConnectionRadiusMeters must be greater than 0");

        if (RoundaboutOverlapToleranceMeters <= 0)
            errors.Add("RoundaboutOverlapToleranceMeters must be greater than 0");

        if (MinTerminatingIdwWeight < 0 || MinTerminatingIdwWeight > 1.0f)
            errors.Add("MinTerminatingIdwWeight must be between 0.0 and 1.0");

        if (IdwFilterTaperDistanceMeters.HasValue && IdwFilterTaperDistanceMeters.Value <= 0)
            errors.Add("IdwFilterTaperDistanceMeters must be greater than 0");

        return errors;
    }
}

/// <summary>
///     Type of blend function for junction transitions.
/// </summary>
public enum JunctionBlendFunctionType
{
    /// <summary>
    ///     Linear interpolation - simple but may have visible transition points.
    /// </summary>
    Linear,

    /// <summary>
    ///     Cosine interpolation - smooth S-curve, good balance of smoothness and performance.
    /// </summary>
    Cosine,

    /// <summary>
    ///     Cubic Hermite (smoothstep) - very smooth with zero first derivative at endpoints.
    /// </summary>
    Cubic,

    /// <summary>
    ///     Quintic (smootherstep) - extremely smooth with zero first and second derivatives.
    ///     Best quality but slightly more computation.
    /// </summary>
    Quintic,

    /// <summary>
    ///     Cubic Hermite interpolation matching elevation AND slope at both endpoints.
    ///     Guarantees C1 continuity (no slope discontinuity at blend boundary).
    ///     This is the recommended blend function for the smoothest results.
    /// </summary>
    CubicHermiteC1
}