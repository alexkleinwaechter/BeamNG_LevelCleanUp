using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
///     Parameters for creating a BeamNG terrain file.
/// </summary>
public class TerrainCreationParameters
{
    /// <summary>
    ///     Terrain size (must be power of 2: 256, 512, 1024, 2048, 4096, 8192, 16384)
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    ///     Maximum terrain height in world units
    /// </summary>
    public float MaxHeight { get; set; }

    /// <summary>
    ///     Path to 16-bit grayscale heightmap image file.
    ///     Use this OR HeightmapImage, not both.
    /// </summary>
    public string? HeightmapPath { get; set; }

    /// <summary>
    ///     16-bit grayscale heightmap image (for advanced scenarios where image is already loaded).
    ///     Use this OR HeightmapPath, not both.
    ///     If both are provided, HeightmapImage takes precedence.
    ///     Note: When using HeightmapImage directly, caller is responsible for disposal.
    /// </summary>
    public Image<L16>? HeightmapImage { get; set; }

    /// <summary>
    ///     List of material definitions. Each material has a name and optional layer image path.
    ///     Order matters - index in list = material index in terrain file.
    ///     First material (index 0) is used as default/fallback where no other material is defined.
    /// </summary>
    public List<MaterialDefinition> Materials { get; set; } = new();

    /// <summary>
    ///     Optional: Include layer texture data in the output file (currently not used by BeamNG)
    /// </summary>
    public bool IncludeLayerTextureData { get; set; } = false;

    /// <summary>
    ///     Optional: Name of the terrain (used for file naming conventions)
    ///     Default is "theTerrain" if not specified.
    /// </summary>
    public string TerrainName { get; set; } = "theTerrain";

    /// <summary>
    ///     Meters per pixel for the terrain (used for road smoothing calculations).
    ///     Default is 2.0 (typical for BeamNG: 1024 terrain = 2048 meters).
    ///     This defines the world-space scale of the terrain.
    /// </summary>
    public float MetersPerPixel { get; set; } = 2.0f;

    /// <summary>
    ///     Enable cross-material junction harmonization.
    ///     When enabled AND multiple materials have road smoothing, the system will detect
    ///     where roads from different materials (e.g., highway and local road) meet and
    ///     harmonize their elevations for smooth transitions.
    ///     This is a GLOBAL setting that applies to all road materials.
    ///     Individual materials still control their own within-material junction harmonization
    ///     via JunctionHarmonizationParameters.EnableJunctionHarmonization.
    ///     Default: true
    /// </summary>
    public bool EnableCrossMaterialHarmonization { get; set; } = true;

    /// <summary>
    ///     When true, flips the material processing order for road network building.
    ///     By default (true), materials at the top of the list (index 0) get higher priority
    ///     for junction harmonization. When false, materials at the bottom get higher priority.
    ///     This does NOT affect texture painting order (last material still wins for overlaps).
    ///     Default: true (top material = highest priority for road smoothing)
    /// </summary>
    public bool FlipMaterialProcessingOrder { get; set; } = false;

    /// <summary>
    ///     Path to a GeoTIFF heightmap file.
    ///     Use this as an alternative to HeightmapPath for importing elevation data with geographic coordinates.
    ///     When set, the GeoTIFF will be read and the bounding box will be extracted.
    ///     Priority: HeightmapImage > HeightmapPath > GeoTiffPath
    /// </summary>
    public string? GeoTiffPath { get; set; }

    /// <summary>
    ///     Path to a directory containing multiple GeoTIFF tiles to combine.
    ///     When set, all .tif, .tiff, and .geotiff files in the directory will be combined.
    ///     Use this for terrain data that spans multiple tiles (e.g., SRTM tiles).
    ///     Priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory > XyzPath
    /// </summary>
    public string? GeoTiffDirectory { get; set; }

    /// <summary>
    ///     Path to an XYZ ASCII elevation data file.
    ///     GDAL 3.10+ reads this format natively (space/tab/semicolon separated X Y Z columns).
    ///     Requires XyzEpsgCode to be set for coordinate transformation (XYZ files lack embedded CRS).
    ///     Priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory > XyzPath
    /// </summary>
    public string? XyzPath { get; set; }

    /// <summary>
    ///     Paths to multiple XYZ ASCII elevation tiles to combine.
    ///     Uses GeoTiffCombiner to stitch tiles into a single dataset before heightmap generation.
    ///     Priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory > XyzFilePaths > XyzPath
    /// </summary>
    public string[]? XyzFilePaths { get; set; }

    /// <summary>
    ///     EPSG code for the coordinate reference system of the XYZ file(s).
    ///     Required when using XyzPath or XyzFilePaths. Common values:
    ///     - 25832: ETRS89 / UTM zone 32N (Germany, most common)
    ///     - 25833: ETRS89 / UTM zone 33N (Eastern Germany)
    ///     - 32632: WGS 84 / UTM zone 32N
    /// </summary>
    public int XyzEpsgCode { get; set; } = 25832;

    /// <summary>
    ///     Geographic bounding box of the terrain.
    ///     Automatically populated when importing from GeoTIFF.
    ///     Can be used for OSM Overpass API queries to fetch roads, buildings, etc.
    /// </summary>
    public GeoBoundingBox? GeoBoundingBox { get; set; }

    /// <summary>
    ///     Minimum elevation from GeoTIFF data (in meters).
    ///     Automatically populated when importing from GeoTIFF.
    ///     This is the base elevation - terrain heights are relative to this.
    /// </summary>
    public double? GeoTiffMinElevation { get; set; }

    /// <summary>
    ///     Maximum elevation from GeoTIFF data (in meters).
    ///     Automatically populated when importing from GeoTIFF.
    /// </summary>
    public double? GeoTiffMaxElevation { get; set; }

    /// <summary>
    ///     Base height (Z position) for the terrain in world units.
    ///     When importing from GeoTIFF, this should be set to the minimum elevation
    ///     so the terrain sits at the correct world height.
    ///     Default is 0.
    /// </summary>
    public float TerrainBaseHeight { get; set; } = 0.0f;

    /// <summary>
    ///     When true and importing from GeoTIFF with MaxHeight=0, the TerrainBaseHeight
    ///     will be automatically set to the minimum elevation from the GeoTIFF.
    ///     Default is true.
    /// </summary>
    public bool AutoSetBaseHeightFromGeoTiff { get; set; } = true;

    // ========================================
    // GEOTIFF CROP SETTINGS
    // ========================================

    /// <summary>
    ///     When true, crop the GeoTIFF to the specified region before resizing.
    /// </summary>
    public bool CropGeoTiff { get; set; } = false;

    /// <summary>
    ///     X offset in pixels from the left edge of the original GeoTIFF for cropping.
    /// </summary>
    public int CropOffsetX { get; set; } = 0;

    /// <summary>
    ///     Y offset in pixels from the top edge of the original GeoTIFF for cropping.
    /// </summary>
    public int CropOffsetY { get; set; } = 0;

    /// <summary>
    ///     Width of the cropped region in pixels.
    /// </summary>
    public int CropWidth { get; set; } = 0;

    /// <summary>
    ///     Height of the cropped region in pixels.
    /// </summary>
    public int CropHeight { get; set; } = 0;

    // ========================================
    // ROAD MESH DAE EXPORT
    // ========================================

    /// <summary>
    ///     When true, exports the road network as a 3D mesh in DAE (Collada) format.
    ///     The DAE file can be imported into BeamNG as a TSStatic for visual road surfaces.
    ///     Default: false
    /// </summary>
    public bool ExportRoadMeshDae { get; set; } = true;

    /// <summary>
    ///     Output path for the road mesh DAE file.
    ///     If not specified, defaults to "{TerrainName}_roads.dae" in the output directory.
    /// </summary>
    public string? RoadMeshDaeOutputPath { get; set; }

    /// <summary>
    ///     UV repeat distance in meters along the road (U axis) for road mesh texturing.
    ///     Default: 10m means the texture repeats every 10 meters along the road.
    /// </summary>
    public float RoadMeshTextureRepeatMeters { get; set; } = 10f;

    /// <summary>
    ///     When true, generates road meshes as separate DAE files per material.
    ///     When false (default), all roads are combined into a single DAE file.
    /// </summary>
    public bool ExportRoadMeshPerMaterial { get; set; } = false;

    /// <summary>
    ///     When true, includes shoulder geometry alongside the road surface.
    ///     Default: false
    /// </summary>
    public bool RoadMeshIncludeShoulders { get; set; } = false;

    /// <summary>
    ///     Shoulder width in meters (if shoulders are enabled).
    ///     Default: 1.5m
    /// </summary>
    public float RoadMeshShoulderWidthMeters { get; set; } = 1.5f;

    // ========================================
    // BRIDGE/TUNNEL STRUCTURE CONFIGURATION
    // ========================================

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

    /// <summary>
    ///     Convenience property to check if any structure exclusion is enabled.
    ///     Returns true if bridges OR tunnels are excluded from terrain processing.
    /// </summary>
    public bool ExcludeStructuresFromTerrain => ExcludeBridgesFromTerrain || ExcludeTunnelsFromTerrain;

    /// <summary>
    ///     When true, bridge/tunnel ways are merged INTO their through-road corridor (like any other way)
    ///     instead of being held out as isolated splines, and the bridge sub-range is remembered as a
    ///     <see cref="RoadGeometry.StructureSegment" /> arc-range. The corridor is then smoothed as one road so
    ///     plan-view + elevation are continuous by construction; only the bridge sub-range is excluded from
    ///     terrain stamping, and the deck is built from that merged, smoothed sub-range. This is the
    ///     "merged-corridor bridge" refactor (plan doc 11) and the real fix for the plan-view kink / "short
    ///     bridge stamped between roads" artifact. When false, bridges stay separate (legacy behaviour).
    ///     Default: true (the structural continuity fix is now the default; toggle off for legacy).
    /// </summary>
    public bool MergeStructuresIntoCorridor { get; set; } = true;

    /// <summary>
    ///     Max distance (meters) the solved bridge deck may bow <b>below</b> the straight chord
    ///     between its endpoints before the vertical curve is blended toward that chord. This is the
    ///     U-vs-seam-kink lever for <see cref="BridgeProfileSolver" />: lower = flatter span + larger
    ///     abutment kink; higher = more sag + smaller kink. No grade is clamped — only the curve family
    ///     is blended. Maps to <c>BridgeProfileSolver.RefineSpans(maxSagBelowChordMeters)</c>.
    ///     Default: 1.0m (matches <see cref="BridgeProfileSolver.DefaultMaxSagBelowChordMeters" />).
    /// </summary>
    public float BridgeMaxSagBelowChordMeters { get; set; } = 1.0f;

    /// <summary>
    ///     How far (meters) terrain that pokes above a bridge deck is shaved below the deck surface by
    ///     <see cref="BridgeDeckExcavator" />. The deck stays the visible driving surface; a small
    ///     undercut avoids z-fighting between the deck mesh and the terrain. Maps to
    ///     <c>BridgeDeckExcavator.Excavate(undercutMeters)</c>.
    ///     Default: 0.05m (matches <see cref="BridgeDeckExcavator.DefaultUndercutMeters" />).
    /// </summary>
    public float BridgeDeckUndercutMeters { get; set; } = 0.05f;

    /// <summary>
    ///     Bridge deck structural thickness as a fraction of the bridge span: thickness = ratio × span,
    ///     then clamped to <see cref="BridgeDeckThicknessMinMeters" />..<see cref="BridgeDeckThicknessMaxMeters" />.
    ///     Drives both the soffit the <see cref="BridgeDeckExcavator" /> daylights under and the 3D deck mesh.
    ///     Default: 0.05 (matches <c>BridgeDeckProfile.DeckThicknessSpanRatio</c>).
    /// </summary>
    public float BridgeDeckThicknessSpanRatio { get; set; } = 0.05f;

    /// <summary>
    ///     Lower clamp (meters) for the span-ratio bridge deck thickness, so short spans still get a
    ///     plausible structural depth. Maps to <c>BridgeDeckProfile.DeckThicknessMinMeters</c>.
    ///     Default: 0.45m.
    /// </summary>
    public float BridgeDeckThicknessMinMeters { get; set; } = 0.45f;

    /// <summary>
    ///     Upper clamp (meters) for the span-ratio bridge deck thickness, so long spans don't grow an
    ///     unrealistically deep deck. Maps to <c>BridgeDeckProfile.DeckThicknessMaxMeters</c>.
    ///     Default: 1.2m.
    /// </summary>
    public float BridgeDeckThicknessMaxMeters { get; set; } = 1.2f;

    /// <summary>
    ///     Parapet (side barrier) height (meters) on the 3D bridge deck mesh. 0 disables parapets.
    ///     Maps to <c>BridgeDeckProfile.ParapetHeightMeters</c>.
    ///     Default: 0.9m.
    /// </summary>
    public float BridgeParapetHeightMeters { get; set; } = 0.9f;

    /// <summary>
    ///     How far the solid bridge end-stamp/abutment block drops below the deck soffit, in meters.
    ///     Maps to BridgeDeckProfile.AbutmentDepthMeters.
    /// </summary>
    public float BridgeAbutmentDepthMeters { get; set; } = 1.0f;

    /// <summary>
    ///     Bridge Rule System configuration (V2 plan doc 01): obstacle-typed clearances, §3.5 priority
    ///     raise/dip distribution, ramp feasibility, dip-as-pin, R8 stamping. Null = defaults (all rule
    ///     flags OFF ⇒ byte-identical). The orchestrator threads the same instance onto each material's
    ///     <see cref="RoadSmoothingParameters.BridgeRules" /> so planner and stamping passes agree.
    /// </summary>
    public BridgeRuleSystemOptions? BridgeRules { get; set; }

    /// <summary>Gets or creates the BridgeRuleSystemOptions (auto-creates all-flags-off defaults if null).</summary>
    public BridgeRuleSystemOptions GetBridgeRules()
    {
        return BridgeRules ??= new BridgeRuleSystemOptions();
    }

    /// <summary>
    ///     Tunnel rule system configuration (plan ai_docs/2026-07-18_tunnel_generation/01): portal-anchored
    ///     floor profile, tube mesh, portal holes/aprons. Null = defaults (all flags OFF ⇒ byte-identical).
    ///     The orchestrator threads the same instance onto each material's
    ///     <see cref="RoadSmoothingParameters.TunnelRules" /> so solver and stamping passes agree.
    /// </summary>
    public TunnelRuleSystemOptions? TunnelRules { get; set; }

    /// <summary>Gets or creates the TunnelRuleSystemOptions (auto-creates all-flags-off defaults if null).</summary>
    public TunnelRuleSystemOptions GetTunnelRules()
    {
        return TunnelRules ??= new TunnelRuleSystemOptions();
    }

    // ========================================
    // HYDRAULIC EROSION
    // ========================================

    /// <summary>
    ///     Optional hydraulic erosion pass applied after heightmap processing and before road smoothing.
    ///     When null or disabled, terrain generation keeps the existing behavior.
    /// </summary>
    public HydraulicErosionSettings? HydraulicErosion { get; set; }

    // (The dead "structure elevation profile" parameter block — orphan tunnel/bridge knobs read only
    // by the never-called StructureElevationCalculator — was deleted with it, tunnel plan Phase 7.
    // Live tunnel tunables: TunnelRuleSystemOptions; live bridge tunables: BridgeRuleSystemOptions.)

    // ========================================
    // DECALROAD GENERATION
    // ========================================

    /// <summary>
    ///     DecalRoad generation settings. When null or Enabled=false, no DecalRoads are generated.
    /// </summary>
    public DecalRoadSettings? DecalRoadSettings { get; set; }

    /// <summary>
    ///     AppData default layer sets, resolved by the orchestrator (which has access to the
    ///     BeamNG_LevelCleanUp project's DecalRoadDefaultsManager). Falls back to hardcoded
    ///     defaults if null.
    /// </summary>
    public Dictionary<string, DecalRoadLayerSet>? DecalRoadAppDataDefaults { get; set; }

    // ========================================
    // OUTPUT PROPERTIES (populated after terrain generation)
    // ========================================

    /// <summary>
    ///     Extracted spawn point from terrain generation.
    ///     Populated after road smoothing if roads were processed.
    ///     Contains position on the longest road spline, or terrain center as fallback.
    ///     This is an OUTPUT property - do not set manually.
    /// </summary>
    public SpawnPointData? ExtractedSpawnPoint { get; set; }

    /// <summary>
    ///     The unified road network produced during road smoothing.
    ///     Populated as an output after terrain generation for downstream use (DecalRoad re-generation).
    ///     This is an OUTPUT property - do not set manually.
    /// </summary>
    public UnifiedRoadNetwork? OutputNetwork { get; set; }

    /// <summary>
    ///     The final heightmap produced during terrain generation (float[y,x] row-major).
    ///     Populated as an output for downstream use (DecalRoad re-generation).
    ///     This is an OUTPUT property - do not set manually.
    /// </summary>
    public float[,]? OutputHeightMap { get; set; }

    // ========================================
    // PRE-ANALYZED NETWORK (for Analyze Settings feature)
    // ========================================

    /// <summary>
    ///     Pre-analyzed road network with junction exclusions already applied.
    ///     When set, the terrain generation will skip network building and junction detection,
    ///     instead using this pre-analyzed network directly.
    ///     This allows users to preview and modify junction exclusions before generation.
    /// </summary>
    public UnifiedRoadNetwork? PreAnalyzedNetwork { get; set; }
}