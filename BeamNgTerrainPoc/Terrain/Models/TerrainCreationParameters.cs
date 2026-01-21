using BeamNgTerrainPoc.Terrain.GeoTiff;
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
    ///     Enable crossroad to T-junction conversion.
    ///     When enabled, mid-spline crossings (where two roads cross without either terminating)
    ///     are converted to T-junctions by splitting the secondary road at the crossing point.
    ///     This enables proper elevation harmonization at crossings.
    ///     Disable this for special scenarios like overpasses/underpasses where roads should
    ///     maintain independent elevations.
    ///     Default: true
    /// </summary>
    public bool EnableCrossroadToTJunctionConversion { get; set; } = true;

    /// <summary>
    ///     Enable extended OSM junction detection.
    ///     When enabled and geographic bounding box is available, queries OSM for junction hints
    ///     (motorway exits, traffic signals, stop signs, etc.) to improve junction detection accuracy.
    ///     When disabled, only geometric junction detection is used.
    ///     Default: true
    /// </summary>
    public bool EnableExtendedOsmJunctionDetection { get; set; } = true;

    /// <summary>
    ///     Global junction detection radius in meters.
    ///     Used when a material's JunctionHarmonizationParameters.UseGlobalSettings is true.
    ///     Maximum distance between path endpoints to consider them part of the same junction.
    ///     Typical values:
    ///     - 5-8m: Narrow roads (single lane)
    ///     - 8-12m: Standard roads
    ///     - 12-15m: Wide roads (highways)
    ///     Default: 15.0
    /// </summary>
    public float GlobalJunctionDetectionRadiusMeters { get; set; } = 15.0f;

    /// <summary>
    ///     Global junction blend distance in meters.
    ///     Used when a material's JunctionHarmonizationParameters.UseGlobalSettings is true.
    ///     Distance over which to blend from junction elevation back to path elevation.
    ///     Typical values:
    ///     - 15-25m: Tight blending (urban roads)
    ///     - 25-40m: Standard blending
    ///     - 40-60m: Smooth blending (highways)
    ///     Default: 30.0
    /// </summary>
    public float GlobalJunctionBlendDistanceMeters { get; set; } = 30.0f;

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
    ///     Priority: HeightmapImage > HeightmapPath > GeoTiffPath > GeoTiffDirectory
    /// </summary>
    public string? GeoTiffDirectory { get; set; }

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
    // OUTPUT PROPERTIES (populated after terrain generation)
    // ========================================

    /// <summary>
    ///     Extracted spawn point from terrain generation.
    ///     Populated after road smoothing if roads were processed.
    ///     Contains position on the longest road spline, or terrain center as fallback.
    ///     This is an OUTPUT property - do not set manually.
    /// </summary>
    public SpawnPointData? ExtractedSpawnPoint { get; set; }

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