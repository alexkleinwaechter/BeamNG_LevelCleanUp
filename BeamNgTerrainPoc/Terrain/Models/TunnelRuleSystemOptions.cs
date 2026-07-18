namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Central configuration for tunnel generation (plan ai_docs/2026-07-18_tunnel_generation/01),
///     mirroring the <see cref="BridgeRuleSystemOptions" /> discipline: every rule is independently
///     flag-gated and all flags default OFF, so library consumers and tests keep the byte-identical
///     baseline; the app opts in via <see cref="EnableAllRules" /> (forced at preset import too).
///     One shared instance is threaded onto <see cref="TerrainCreationParameters" /> AND every
///     material's <see cref="RoadSmoothingParameters" /> — solver and stamping passes must agree.
///     <para>Only <c>StructureType.Tunnel</c> segments are subject to these rules —
///     <c>BuildingPassage</c> (handled by building wall cutouts) and <c>Culvert</c> (too small to
///     drive) are always skipped.</para>
/// </summary>
public class TunnelRuleSystemOptions
{
    // ========================================
    // FEATURE FLAGS (all default OFF = byte-identical baseline)
    // ========================================

    /// <summary>
    ///     Portal-anchored G0+G1 floor profile: override each tunnel span's chain-solved elevations
    ///     (which today follow smoothed terrain OVER the mountain) with a Hermite between the solved
    ///     approach elevations/grades just outside the span, and capture the span into
    ///     <c>network.TunnelSpans</c> (the frozen source for mesh/holes/decals).
    /// </summary>
    public bool EnableTunnelProfile { get; set; }

    /// <summary>Generate the tunnel tube mesh + DAE + scene TSStatic per span (Phase 3).</summary>
    public bool EnableTunnelMesh { get; set; }

    /// <summary>Cut terrain holes (material 255) at portal mouths / tube-piercing cells (Phase 4).</summary>
    public bool EnablePortalHoles { get; set; }

    /// <summary>
    ///     Stamp the terrain across the portal apron (the span-end stretch that stays ordinary
    ///     stamped road) to the solved road surface, so terrain and tunnel-floor mesh meet at the
    ///     same Z at the portal mouth — no lip, no step (Phase 2c).
    /// </summary>
    public bool EnablePortalAprons { get; set; }

    /// <summary>
    ///     v2 depth rule (not shipped in v1): where the portal-anchored profile leaves less than
    ///     <see cref="TunnelMinCoverMeters" /> of terrain above the outer shell outside the portal
    ///     zones, blend in an S-curve sag to gain depth. v1 leaves shallow stretches to the hole
    ///     cutter instead (never wrong geometrically).
    /// </summary>
    public bool EnableTunnelDepthProfile { get; set; }

    /// <summary>
    ///     Banking follow-up (doc 03): honor the Phase 2.5 chain bank THROUGH tunnel spans instead
    ///     of flattening it — the floor slab is sheared vertically between the banked edge
    ///     elevations (walls stay plumb, the lining does not roll), apron terrain, portal collar and
    ///     hole silhouette follow, and the portal seam is bank-continuous with the approach road.
    ///     Off ⇒ the v1 zeroing path (flat tunnels) runs unchanged.
    /// </summary>
    public bool EnableTunnelBanking { get; set; }

    /// <summary>Turns on every tunnel rule. Product default for the UI (like bridge rules since 2026-07).</summary>
    public void EnableAllRules()
    {
        EnableTunnelProfile = true;
        EnableTunnelMesh = true;
        EnablePortalHoles = true;
        EnablePortalAprons = true;
        EnableTunnelBanking = true;
        // EnableTunnelDepthProfile stays off — v2 rule, not shipped in v1.
    }

    /// <summary>Fresh options with every rule enabled (see <see cref="EnableAllRules" />).</summary>
    public static TunnelRuleSystemOptions CreateWithAllRulesEnabled()
    {
        var options = new TunnelRuleSystemOptions();
        options.EnableAllRules();
        return options;
    }

    /// <summary>True when any tunnel rule is active (cheap pipeline gate).</summary>
    public bool AnyEnabled =>
        EnableTunnelProfile || EnableTunnelMesh || EnablePortalHoles ||
        EnablePortalAprons || EnableTunnelDepthProfile;

    // ========================================
    // TUBE GEOMETRY (m)
    // ========================================

    /// <summary>Interior height floor→ceiling apex (semi-ellipse arch apex).</summary>
    public float TunnelInteriorHeightMeters { get; set; } = 5.0f;

    /// <summary>Wall thickness interior surface → exterior shell.</summary>
    public float TunnelWallThicknessMeters { get; set; } = 0.6f;

    /// <summary>Lateral clearance roadway edge → interior wall, per side.</summary>
    public float TunnelSideClearanceMeters { get; set; } = 1.0f;

    /// <summary>Segments of the ceiling arch semi-ellipse (mesh tessellation).</summary>
    public int TunnelCeilingArchSegments { get; set; } = 8;

    // ========================================
    // ELEVATION RULES
    // ========================================

    /// <summary>
    ///     Minimum terrain cover above the ceiling OUTER shell for the v2 depth rule
    ///     (<see cref="EnableTunnelDepthProfile" />); also the shallow-warn threshold in v1 logs.
    /// </summary>
    public float TunnelMinCoverMeters { get; set; } = 5.0f;

    /// <summary>
    ///     Max longitudinal grade (%) of the tunnel floor profile. NEVER clamped — exceeding it
    ///     logs a [TUNNEL] grade warning (grade violations mean bad OSM/DEM input; surfaced, not
    ///     hidden — the project rejects grade-clamp mitigations).
    /// </summary>
    public float TunnelMaxGradePercent { get; set; } = 6.0f;

    // ========================================
    // PORTALS
    // ========================================

    /// <summary>
    ///     Span-end stretch (m) that stays ordinary stamped ground road (exclusion shrink), so the
    ///     road runs INTO the portal on real terrain — mirrors the bridge AbutmentOverlapMeters.
    /// </summary>
    public float PortalApronMeters { get; set; } = 3.0f;

    /// <summary>Phase 4: minimum hole length (m) cut inward from each portal mouth.</summary>
    public float PortalHoleMinLengthMeters { get; set; } = 8.0f;

    /// <summary>Phase 4: lateral hole margin (m) beyond the tube outer shell (portal-mouth zone only —
    /// the interior clip zone tracks the shell silhouette exactly).</summary>
    public float PortalHoleLateralMarginMeters { get; set; } = 1.0f;

    /// <summary>
    ///     Portal headwall extrusion (m): how far the portal face plate extends beyond the outer shell
    ///     — left, right and up — to mask the terrain hole's ragged 1 m-raster edge around the mouth
    ///     (tunneljena render 2026-07-18: the 1 m flare left the hole peeking out all around). Must
    ///     exceed <see cref="PortalHoleLateralMarginMeters" /> + one cell dilation + raster jitter.
    /// </summary>
    public float PortalHeadwallFlareMeters { get; set; } = 5.0f;

    /// <summary>
    ///     Portal headwall thickness (m), extruded INTO the mountain from the portal plane. The
    ///     collar is a massive watertight solid (front, back, rim and underside faces all outward) —
    ///     a one-sided plate reads invisible from behind/above through the hole edges.
    /// </summary>
    public float PortalHeadwallDepthMeters { get; set; } = 10.0f;

    /// <summary>
    ///     Drivable floor slab (m) extended OUT of each portal toward the ground road, a hair below
    ///     the road surface — the safety surface over the imprecise 1 m hole edge at the mouth
    ///     (terrain cells there may be holed; the tongue catches the wheels). 0 disables.
    /// </summary>
    public float PortalFloorTongueMeters { get; set; } = 6.0f;
}
