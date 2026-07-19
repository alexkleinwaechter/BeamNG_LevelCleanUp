using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using static BeamNG_LevelCleanUp.BlazorUI.Components.TerrainMaterialSettings;

namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>
///     Centralized state container for the Terrain Generation page.
///     Consolidates all form fields and computed properties to reduce code-behind complexity.
/// </summary>
public class TerrainGenerationState
{
    // ========================================
    // WORKING DIRECTORY & LEVEL INFO
    // ========================================

    public string WorkingDirectory { get; set; } = string.Empty;
    public string LevelName { get; set; } = string.Empty;
    public bool HasWorkingDirectory { get; set; }
    public bool HasExistingTerrainSettings { get; set; }

    // ========================================
    // TERRAIN PARAMETERS
    // ========================================

    public string TerrainName { get; set; } = "theTerrain";
    public int TerrainSize { get; set; } = 2048;
    public float MaxHeight { get; set; }
    public float MetersPerPixel { get; set; } = 1.0f;
    public float TerrainBaseHeight { get; set; }
    public bool UpdateTerrainBlock { get; set; } = true;
    public bool EnableCrossMaterialHarmonization { get; set; } = true;
    public HydraulicErosionSettings HydraulicErosion { get; set; } = new();

    /// <summary>
    ///     When true, flips the material processing order for road network building.
    ///     By default (true), materials at the top of the list (index 0) get higher priority
    ///     for junction harmonization. When false, materials at the bottom get higher priority.
    ///     This does NOT affect texture painting order (last material still wins for overlaps).
    ///     Default: true (top material = highest priority for road smoothing)
    /// </summary>
    public bool FlipMaterialProcessingOrder { get; set; }

    // ========================================
    // BRIDGE/TUNNEL STRUCTURE CONFIGURATION
    // ========================================

    /// <summary>
    ///     UI label "Generate Bridges". When true, bridges are excluded from terrain smoothing and
    ///     material painting and are built as elevated decks instead. When false, bridge ways are
    ///     treated as normal roads (legacy behavior). Default: false.
    /// </summary>
    public bool ExcludeBridgesFromTerrain { get; set; } = false;

    /// <summary>
    ///     UI label "Generate Tunnels". When true, tunnel spans are excluded from terrain
    ///     smoothing/painting and built as drivable tube meshes with portal terrain holes instead
    ///     (tunnel plan 2026-07-18). When false, tunnel ways are treated as normal surface roads
    ///     (legacy behavior). Default: false.
    /// </summary>
    public bool ExcludeTunnelsFromTerrain { get; set; } = false;

    /// <summary>
    ///     When true, bridges/tunnels merge INTO the through-road corridor (remembering the bridge arc-range)
    ///     so the corridor is smoothed as one road and the deck is built from that merged, smoothed sub-range —
    ///     the "merged-corridor bridge" continuity fix (plan doc 11). Always true in the app since 2026-07
    ///     (checkbox removed, preset values ignored); false = legacy separate-spline behavior, code-only.
    /// </summary>
    public bool MergeStructuresIntoCorridor { get; set; } = true;

    /// <summary>
    ///     Max distance (meters) a bridge deck may bow below the endpoint chord before the vertical
    ///     curve is blended toward the chord (the sag-vs-seam-kink lever). No grade clamping.
    ///     Default: 1.0m.
    /// </summary>
    public float BridgeMaxSagBelowChordMeters { get; set; } = 1.0f;

    /// <summary>
    ///     How far (meters) terrain poking above a bridge deck is shaved below the deck surface
    ///     (keeps the deck the visible driving surface, avoids z-fighting). Default: 0.05m.
    /// </summary>
    public float BridgeDeckUndercutMeters { get; set; } = 0.05f;

    /// <summary>
    ///     Bridge deck structural thickness as a fraction of the span (thickness = ratio × span), clamped
    ///     to the Min/Max below. Drives both the excavator soffit and the 3D deck mesh. Default: 0.05.
    /// </summary>
    public float BridgeDeckThicknessSpanRatio { get; set; } = 0.05f;

    /// <summary>
    ///     Lower clamp (meters) for the span-ratio bridge deck thickness. Default: 0.45m.
    /// </summary>
    public float BridgeDeckThicknessMinMeters { get; set; } = 0.45f;

    /// <summary>
    ///     Upper clamp (meters) for the span-ratio bridge deck thickness. Default: 1.2m.
    /// </summary>
    public float BridgeDeckThicknessMaxMeters { get; set; } = 1.2f;

    /// <summary>
    ///     Parapet (side barrier) height (meters) on the 3D bridge deck mesh. 0 disables parapets. Default: 0.9m.
    /// </summary>
    public float BridgeParapetHeightMeters { get; set; } = 0.9f;

    /// <summary>
    ///     How far the solid bridge end-stamp/abutment block drops below the deck soffit, in meters. Default: 1.0m.
    /// </summary>
    public float BridgeAbutmentDepthMeters { get; set; } = 1.0f;

    /// <summary>
    ///     Bridge Rule System configuration (V2 plan doc 01). The rules are always on in the app (only the
    ///     tunables and the pier toggle are user-facing); preset import re-enables them likewise. The
    ///     orchestrator threads this single instance onto TerrainCreationParameters and every road
    ///     material's RoadSmoothingParameters.
    /// </summary>
    public BridgeRuleSystemOptions BridgeRules { get; set; } = BridgeRuleSystemOptions.CreateWithAllRulesEnabled();

    /// <summary>
    ///     Tunnel rule system configuration (tunnel plan 2026-07-18). Like <see cref="BridgeRules" />,
    ///     the rules are always on in the app (only the tunables are user-facing; the "Generate
    ///     Tunnels" switch gates the whole feature via <see cref="ExcludeTunnelsFromTerrain" />);
    ///     preset import re-enables them likewise. Threaded as one shared instance onto
    ///     TerrainCreationParameters and every road material's RoadSmoothingParameters.
    /// </summary>
    public TunnelRuleSystemOptions TunnelRules { get; set; } = TunnelRuleSystemOptions.CreateWithAllRulesEnabled();

    /// <summary>
    ///     When true, disables spline merging (each OSM way becomes a separate spline).
    ///     For testing only — merging is needed for smooth road continuity.
    /// </summary>
    public bool DisableSplineMerging { get; set; } = false;

    // ========================================
    // DECALROAD SETTINGS
    // ========================================

    /// <summary>
    ///     Enable DecalRoad generation during terrain creation.
    /// </summary>
    public bool EnableDecalRoads { get; set; } = true;

    /// <summary>
    ///     DecalRoad generation settings (node spacing, junction margin, layer sets).
    ///     Populated from preset or defaults.
    /// </summary>
    public DecalRoadSettings? DecalRoadSettings { get; set; }

    /// <summary>
    ///     Cached UnifiedRoadNetwork from last terrain generation.
    ///     Used for standalone DecalRoad re-generation.
    ///     Lost when navigating away from page.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public UnifiedRoadNetwork? CachedNetwork { get; set; }

    /// <summary>
    ///     Cached heightmap from last terrain generation.
    ///     Used for standalone DecalRoad re-generation.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float[,]? CachedHeightMap { get; set; }

    // ========================================
    // BUILDING GENERATION
    // ========================================
    public bool EnableBuildings { get; set; }
    public List<OsmFeatureSelection> SelectedBuildingFeatures { get; set; } = new();

    /// <summary>
    ///     When true, nearby buildings are merged into combined DAE files to reduce draw calls.
    /// </summary>
    public bool EnableBuildingClustering { get; set; } = true;

    /// <summary>
    ///     Grid cell size in meters for building clustering.
    ///     All buildings whose centroid falls within the same grid cell are merged into one DAE.
    ///     Larger values = fewer draw calls but coarser LOD grouping.
    ///     Recommended: 100-200m.
    /// </summary>
    public float BuildingClusterCellSize { get; set; } = 128f;

    /// <summary>
    ///     Maximum LOD level to include in exported building DAE files.
    ///     0 = LOD0 only (walls + roof, no windows — fastest, lowest quality)
    ///     1 = LOD0 + LOD1 (adds textured window quads)
    ///     2 = LOD0 + LOD1 + LOD2 (adds full 3D windows, doors, frames — highest quality)
    /// </summary>
    public int MaxBuildingLodLevel { get; set; } = 2;

    /// <summary>
    ///     LOD bias multiplier for building exports. Default 1.0.
    ///     Controls when LOD transitions occur relative to camera distance.
    ///     Values &gt; 1 = detail drops sooner (better performance).
    ///     Values &lt; 1 = detail retained longer (better visuals at distance).
    /// </summary>
    public float BuildingLodBias { get; set; } = 1.0f;

    /// <summary>
    ///     Pixel-size cull threshold for the nulldetail node in building DAE files.
    ///     When the object is smaller than this many pixels on screen, it is not rendered.
    ///     0 = no nulldetail node (object always rendered). Default 0.
    /// </summary>
    public int NullDetailPixelSize { get; set; }

    public HashSet<long> GetSelectedBuildingFeatureIds() =>
        SelectedBuildingFeatures.Select(f => f.FeatureId).ToHashSet();

    // ========================================
    // HEIGHTMAP SOURCE
    // ========================================

    public HeightmapSourceType HeightmapSourceType { get; set; } = HeightmapSourceType.Png;
    public string? HeightmapPath { get; set; }
    public string? GeoTiffPath { get; set; }
    public string? GeoTiffDirectory { get; set; }
    public string? XyzPath { get; set; }
    public string[]? XyzFilePaths { get; set; }
    public int XyzEpsgCode { get; set; } = 25832;
    public int? XyzDetectedEpsg { get; set; }

    /// <summary>
    ///     Per-tile bounding boxes for filtering tiles before combine operations.
    ///     Populated during GeoTIFF/XYZ metadata import.
    /// </summary>
    public List<TileBoundsInfo>? TileBoundsInfo { get; set; }

    // ========================================
    // GEOTIFF METADATA
    // ========================================

    public GeoBoundingBox? GeoBoundingBox { get; set; }
    public GeoBoundingBox? GeoTiffNativeBoundingBox { get; set; }
    public string? GeoTiffProjectionName { get; set; }
    public string? GeoTiffProjectionWkt { get; set; }
    public double[]? GeoTiffGeoTransform { get; set; }
    public int GeoTiffOriginalWidth { get; set; }
    public int GeoTiffOriginalHeight { get; set; }
    public double? GeoTiffMinElevation { get; set; }
    public double? GeoTiffMaxElevation { get; set; }

    // ========================================
    // CROP SETTINGS
    // ========================================

    public CropAnchor CropAnchor { get; set; } = CropAnchor.Center;
    public CropResult? CropResult { get; set; }

    /// <summary>
    ///     Cached combined GeoTIFF path for directory mode (avoids re-combining on every crop change).
    /// </summary>
    public string? CachedCombinedGeoTiffPath { get; set; }

    // ========================================
    // OSM DATA AVAILABILITY
    // ========================================

    public bool CanFetchOsmData { get; set; }
    public string? OsmBlockedReason { get; set; }
    public GeoTiffValidationResult? GeoTiffValidationResult { get; set; }

    /// <summary>
    ///     Cached OSM query result from the current page session.
    ///     Reused when generating again with the same or smaller effective bounding box.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public OsmQueryResult? CachedOsmQueryResult { get; set; }

    // ========================================
    // TERRAIN MATERIALS
    // ========================================

    public List<TerrainMaterialItemExtended> TerrainMaterials { get; } = new();

    // ========================================
    // UI STATE
    // ========================================

    public bool IsGenerating { get; set; }
    public bool IsLoading { get; set; }

    // ========================================
    // MESSAGES & LOGS
    // ========================================

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Messages { get; } = new();

    // ========================================
    // COMPUTED PROPERTIES
    // ========================================

    /// <summary>
    ///     Gets the effective bounding box for OSM queries.
    ///     Returns the cropped bounding box if cropping is enabled, otherwise returns the full bounding box.
    ///     This MUST be used for all OSM-related operations to ensure correct geographic extent.
    /// </summary>
    public GeoBoundingBox? EffectiveBoundingBox =>
        CropResult is { NeedsCropping: true, CroppedBoundingBox: not null }
            ? CropResult.CroppedBoundingBox
            : GeoBoundingBox;

    /// <summary>
    ///     Gets the output path for the terrain file.
    /// </summary>
    public string GetOutputPath()
    {
        if (string.IsNullOrEmpty(WorkingDirectory))
            return "Not set";
        return Path.Combine(WorkingDirectory, $"{TerrainName}.ter");
    }

    /// <summary>
    ///     Gets the debug output directory path.
    /// </summary>
    public string GetDebugPath()
    {
        if (string.IsNullOrEmpty(WorkingDirectory))
            return "Not set";
        return Path.Combine(WorkingDirectory, "MT_TerrainGeneration");
    }

    /// <summary>
    ///     Gets the working directory title for display.
    /// </summary>
    public string GetWorkingDirectoryTitle()
    {
        if (!string.IsNullOrEmpty(LevelName))
            return $"Working Directory > {LevelName}";
        if (!string.IsNullOrEmpty(WorkingDirectory))
            return $"Working Directory > {WorkingDirectory}";
        return "Select Level Folder";
    }

    /// <summary>
    ///     Checks if terrain generation can proceed.
    /// </summary>
    public bool CanGenerate()
    {
        var hasValidHeightmapSource = HeightmapSourceType switch
        {
            HeightmapSourceType.Png => !string.IsNullOrEmpty(HeightmapPath) && File.Exists(HeightmapPath),
            HeightmapSourceType.GeoTiffFile => !string.IsNullOrEmpty(GeoTiffPath) && File.Exists(GeoTiffPath),
            HeightmapSourceType.GeoTiffDirectory => !string.IsNullOrEmpty(GeoTiffDirectory) &&
                                                    Directory.Exists(GeoTiffDirectory),
            HeightmapSourceType.XyzFile => ((!string.IsNullOrEmpty(XyzPath) && File.Exists(XyzPath)) ||
                                            (XyzFilePaths is { Length: > 0 })) &&
                                           XyzEpsgCode > 0,
            _ => false
        };

        return hasValidHeightmapSource &&
               TerrainMaterials.Any() &&
               !string.IsNullOrEmpty(TerrainName);
    }

    /// <summary>
    ///     Gets the helper text for meters per pixel field.
    /// </summary>
    public string GetMetersPerPixelHelperText()
    {
        var terrainSizeKm = MetersPerPixel * TerrainSize / 1000f;
        return $"Terrain = {terrainSizeKm:F1}km × {terrainSizeKm:F1}km in-game";
    }

    /// <summary>
    ///     Gets the heightmap source description for display.
    /// </summary>
    public string GetHeightmapSourceDescription()
    {
        return HeightmapSourceType switch
        {
            HeightmapSourceType.Png => "16-bit grayscale PNG heightmap",
            HeightmapSourceType.GeoTiffFile => "Single GeoTIFF elevation file with geographic coordinates",
            HeightmapSourceType.GeoTiffDirectory => "Directory with multiple GeoTIFF tiles to combine",
            HeightmapSourceType.XyzFile => "XYZ ASCII elevation file (georeferenced grid data)",
            _ => "Unknown"
        };
    }

    // ========================================
    // STATE MANAGEMENT
    // ========================================

    /// <summary>
    ///     Clears all GeoTIFF metadata fields.
    /// </summary>
    public void ClearGeoMetadata()
    {
        GeoBoundingBox = null;
        GeoTiffNativeBoundingBox = null;
        GeoTiffProjectionName = null;
        GeoTiffProjectionWkt = null;
        GeoTiffGeoTransform = null;
        GeoTiffOriginalWidth = 0;
        GeoTiffOriginalHeight = 0;
        GeoTiffMinElevation = null;
        GeoTiffMaxElevation = null;
        TileBoundsInfo = null;

        CleanupCachedCombinedGeoTiff();
    }

    /// <summary>
    ///     Cleans up the cached combined GeoTIFF file if it exists.
    /// </summary>
    public void CleanupCachedCombinedGeoTiff()
    {
        if (!string.IsNullOrEmpty(CachedCombinedGeoTiffPath))
        {
            try
            {
                if (File.Exists(CachedCombinedGeoTiffPath)) File.Delete(CachedCombinedGeoTiffPath);
            }
            catch
            {
                // Ignore cleanup errors
            }

            CachedCombinedGeoTiffPath = null;
        }
    }

    /// <summary>
    ///     Clears all messages, warnings, and errors.
    /// </summary>
    public void ClearMessages()
    {
        Errors.Clear();
        Warnings.Clear();
        Messages.Clear();
    }

    /// <summary>
    ///     Resets all state to initial values.
    /// </summary>
    public void Reset()
    {
        WorkingDirectory = string.Empty;
        LevelName = string.Empty;
        HasWorkingDirectory = false;
        HasExistingTerrainSettings = false;
        TerrainMaterials.Clear();
        ClearMessages();

        HeightmapPath = null;
        TerrainSize = 2048;
        MaxHeight = 500.0f;
        MetersPerPixel = 1.0f;
        TerrainName = "theTerrain";
        TerrainBaseHeight = 0.0f;
        UpdateTerrainBlock = true;
        EnableCrossMaterialHarmonization = true;
        ExcludeBridgesFromTerrain = false;
        ExcludeTunnelsFromTerrain = false;
        MergeStructuresIntoCorridor = true;
        BridgeMaxSagBelowChordMeters = 1.0f;
        BridgeDeckUndercutMeters = 0.05f;
        BridgeDeckThicknessSpanRatio = 0.05f;
        BridgeDeckThicknessMinMeters = 0.45f;
        BridgeDeckThicknessMaxMeters = 1.2f;
        BridgeParapetHeightMeters = 0.9f;
        BridgeAbutmentDepthMeters = 1.0f;
        BridgeRules = BridgeRuleSystemOptions.CreateWithAllRulesEnabled();
        TunnelRules = TunnelRuleSystemOptions.CreateWithAllRulesEnabled();
        HydraulicErosion = new HydraulicErosionSettings();
        FlipMaterialProcessingOrder = false;
        EnableDecalRoads = true;
        DecalRoadSettings = null;
        CachedNetwork = null;
        CachedHeightMap = null;
        EnableBuildings = false;
        EnableBuildingClustering = true;
        BuildingClusterCellSize = 128f;
        MaxBuildingLodLevel = 2;
        BuildingLodBias = 1.0f;
        SelectedBuildingFeatures = new List<OsmFeatureSelection>();

        HeightmapSourceType = HeightmapSourceType.Png;
        GeoTiffPath = null;
        GeoTiffDirectory = null;
        XyzPath = null;
        XyzFilePaths = null;
        XyzEpsgCode = 25832;
        XyzDetectedEpsg = null;
        CropAnchor = CropAnchor.Center;
        CropResult = null;
        CanFetchOsmData = false;
        OsmBlockedReason = null;
        GeoTiffValidationResult = null;
        CachedOsmQueryResult = null;

        ClearGeoMetadata();
    }
}