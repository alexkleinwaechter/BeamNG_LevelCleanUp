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
    ///     No-blend §4 banking match — runoff length over which a terminating road warps its banking
    ///     onto the through road's tilted surface at a junction, expressed as a multiple of the
    ///     terminating road's painted <see cref="UnifiedCrossSection.SurfaceWidth" />.
    ///     Used by <c>UnifiedRoadSmoother.MatchTerminatingBankingToThroughSurface</c> on the affine
    ///     ThroughRoad path: zone = SurfaceWidth × this. Larger = gentler superelevation runoff (banking
    ///     transitions back to natural over a longer approach). The single tunable for this feature.
    ///     Exposed in the UI (Side-Road Transitions) + preset. Default 3.
    /// </summary>
    public float BankingRunoffSurfaceWidthMultiplier { get; set; } = 3f;

    /// <summary>
    ///     No-blend connector grade ramp — MINIMUM length (m) of the seam-aware weld that makes a
    ///     terminating connector co-planar with the through road's surface where their painted surfaces
    ///     actually meet (the through-road EDGE, not the junction center). Used by
    ///     <c>UnifiedRoadSmoother.EaseConnectorGradeToThroughSurface</c> on the affine ThroughRoad path,
    ///     AFTER the §4 banking match. The connector centerline follows the through surface plane across
    ///     the through road's half-width (apron), then a C1 weld eases back onto the connector's natural
    ///     §3 profile. The weld length is sized ADAPTIVELY from the grade break
    ///     (<c>UnifiedRoadSmoother.ConnectorWeldGradeChangePerMeter</c>) — this knob only sets the floor, so
    ///     steep junctions automatically get the longer transition they need (2026-07-19 junction-edge-step
    ///     fix: the old fixed 6 m weld, anchored at the junction center, was consumed inside the through
    ///     road's ~5 m half-width and left a 0.3–0.7 m step at the road edge). The seam Z, far-junction Z,
    ///     and connector body beyond the weld stay fixed. 0 = disabled (library/test kill switch only).
    ///     NOT exposed in the UI or presets (2026-07-19): the weld is always on and self-sizing — the app
    ///     always runs with this default, and preset importers deliberately ignore old stored values.
    ///     Default 6.
    /// </summary>
    public float ConnectorGradeRampLengthMeters { get; set; } = 6f;

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
    ///     Surface-protection margin (m) added to Pass 1's painted-surface half-width when
    ///     <see cref="EnableSurfaceWidthProtection" /> is on. Pass 1 stamps the protected (flat,
    ///     road-elevation, unsmoothed) zone at <c>SurfaceWidth/2 + this</c> instead of exactly
    ///     <c>SurfaceWidth/2</c>. A hard <c>SurfaceWidth/2</c> boundary leaves chord-slivers on the
    ///     convex edge of curved/junction segments (consecutive surface quads chord across the curve);
    ///     those slivers fall to Pass 2's smoothing corridor and dip below an elevated road's surface,
    ///     reading as a "bite" scooped out of the road edge. A small margin (≈ half a meter) closes the
    ///     slivers and pushes the blend start just past the visible edge, giving a thin flat shoulder
    ///     instead of a bitten edge. Keep small — it widens every road's protected/flat zone by this
    ///     amount, so the embankment blend simply starts this much further out. 0 = legacy hard boundary.
    ///     not for ui, no control needed
    /// </summary>
    public float SurfaceProtectionMarginMeters { get; set; } = 1f;

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
    ///     No-blend path only: tilt the roundabout ring as a terrain-following plane instead of keeping it
    ///     a flat horizontal disk. Helps on GENTLE terrain (tilt under the 6% cap → ring hugs the ground
    ///     with near-zero embankment and neighboring connectors stay at similar Z). On STEEP terrain the
    ///     cap forces a big residual embankment AND spreads neighboring connector mouths to different Z,
    ///     so the flat junction-fill disks no longer match the tilted ring → visible steps at the mouths.
    ///     Default false (flat ring): the flush-seam §3/§4 connector technique still applies, and the flat
    ///     fills match the flat ring. Enable per map when the roundabout sits on gentle ground.
    ///     Default: false
    /// </summary>
    public bool EnableTiltedRoundaboutPlane { get; set; } = false;

    /// <summary>
    ///     Maximum tilt (Querneigung) of the terrain-following roundabout ring plane, as a gradient
    ///     (rise/run). Civil absolute limit is 6% → 0.06. Terrain demanding more becomes unavoidable
    ///     cut/fill. Only used when <see cref="EnableTiltedRoundaboutPlane" /> is set. This is a gradient
    ///     (rise/run); the UI exposes it in degrees (0–15°, shown only when tilt enabled) and converts via
    ///     <c>tan</c> at the build boundary, so 6° → ≈0.1051 here. The default matches the UI default of 6°.
    ///     Default: ≈0.1051 (tan 6°)
    /// </summary>
    public float RoundaboutMaxPlaneTilt { get; set; } = 0.1051042f; // tan(6°)

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