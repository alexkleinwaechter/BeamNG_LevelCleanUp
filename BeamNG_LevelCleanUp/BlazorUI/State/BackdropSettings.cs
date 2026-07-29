namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>UI/state POCO for backdrop generation (spec §5). Selection in combined-GeoTIFF source pixels.</summary>
public class BackdropSettings
{
    public bool Enabled { get; set; }                       // default FALSE (spec D8) — never change this default
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox? BoundingBox { get; set; }  // derived WGS84
    // App defaults for band/tolerances diverge from the core/spec §15 values (200 / 0.5 / 8.0) by
    // user decision 2026-07-28: the rossfeldpanorama bake measured 1.78 GB of chunk DAEs at the old
    // defaults (~92 MB per million triangles), dominated by the far tolerance on rugged alpine
    // terrain and the forced-full-res edge band. 50 / 1.0 / 16.0 target a fraction of that out of
    // the box (band lowered 100→50 same day after the validated rossfeld bake used 50); core stays
    // caller-driven.
    public double EdgeBandMeters { get; set; } = 50;
    public double MaxVerticalErrorNearMeters { get; set; } = 1.0;
    public double MaxVerticalErrorFarMeters { get; set; } = 16.0;
    // 2048 (not spec §15's 2000) so power-of-two terrain spans partition into exactly dyadic chunk
    // widths (8192 → 4×2048 instead of 5×1638): chunk borders land on the dyadic split lattice
    // (BackdropEdgeSubdivider.DyadicMid) and a near chunk at 1 m/px fills its 2048 texture exactly.
    public double ChunkTargetMeters { get; set; } = 2048;
    public double TexelDensityNearMPerPx { get; set; } = 1.0;
    public int MaxChunkTextureSize { get; set; } = 2048;
    public int MaxFarRasterDimension { get; set; } = 8192;
    public bool SeamSkirt { get; set; } = true;
    // Default OFF (user decision 2026-07-28): drivability is scene-level only (TSStatic
    // collisionType "Visible Mesh Final" — the game builds physics from the visual mesh; the DAEs
    // never embed collision geometry, so this costs no disk either way). Off skips the game's
    // load-time physics build — scenery-only is the fast default; core default stays true
    // (caller-driven).
    public bool CollisionMesh { get; set; }
    public bool HasSelection => Width > 0 && Height > 0;
}
