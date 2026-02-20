using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNG_LevelCleanUp.BlazorUI.Components;

/// <summary>
///     Heightmap source type for terrain generation.
/// </summary>
public enum HeightmapSourceType
{
    Png,
    GeoTiffFile,
    GeoTiffDirectory
}

/// <summary>
///     Result of importing a BeamNG terrain preset file.
///     Contains the extracted settings that should be applied to the terrain generation page.
/// </summary>
public class TerrainPresetResult
{
    /// <summary>
    ///     The terrain name from the preset.
    /// </summary>
    public string? TerrainName { get; set; }

    /// <summary>
    ///     The maximum height (from heightScale in preset).
    /// </summary>
    public float? MaxHeight { get; set; }

    /// <summary>
    ///     Meters per pixel (from squareSize in preset).
    /// </summary>
    public float? MetersPerPixel { get; set; }

    /// <summary>
    ///     Terrain base height (from pos.z in preset).
    /// </summary>
    public float? TerrainBaseHeight { get; set; }

    /// <summary>
    ///     Resolved path to the heightmap file.
    /// </summary>
    public string? HeightmapPath { get; set; }

    /// <summary>
    ///     Resolved path to the hole map file.
    /// </summary>
    public string? HoleMapPath { get; set; }

    /// <summary>
    ///     Number of layer maps that were successfully assigned to materials.
    /// </summary>
    public int AssignedLayerMapsCount { get; set; }

    // ========== NEW: Heightmap Source Configuration ==========

    /// <summary>
    ///     The type of heightmap source (PNG, GeoTIFF file, or GeoTIFF directory).
    /// </summary>
    public HeightmapSourceType? HeightmapSourceType { get; set; }

    /// <summary>
    ///     Path to GeoTIFF file (when HeightmapSourceType is GeoTiffFile).
    /// </summary>
    public string? GeoTiffPath { get; set; }

    /// <summary>
    ///     Path to GeoTIFF tiles directory (when HeightmapSourceType is GeoTiffDirectory).
    /// </summary>
    public string? GeoTiffDirectory { get; set; }

    // ========== NEW: Terrain Generation Options ==========

    /// <summary>
    ///     Whether to update the TerrainBlock in MissionGroup items.level.json.
    /// </summary>
    public bool? UpdateTerrainBlock { get; set; }

    /// <summary>
    ///     Whether to enable cross-material harmonization for road smoothing.
    /// </summary>
    public bool? EnableCrossMaterialHarmonization { get; set; }

    /// <summary>
    ///     Whether to enable crossroad to T-junction conversion.
    ///     When enabled, mid-spline crossings are converted to T-junctions for proper elevation harmonization.
    /// </summary>
    public bool? EnableCrossroadToTJunctionConversion { get; set; }

    /// <summary>
    ///     Whether to enable extended OSM junction detection.
    ///     When enabled, queries OSM for junction hints to improve detection accuracy.
    /// </summary>
    public bool? EnableExtendedOsmJunctionDetection { get; set; }

    /// <summary>
    ///     Global junction detection radius in meters.
    ///     Used when a material's UseGlobalJunctionSettings is true.
    /// </summary>
    public float? GlobalJunctionDetectionRadiusMeters { get; set; }

    /// <summary>
    ///     Global junction blend distance in meters.
    ///     Used when a material's UseGlobalJunctionSettings is true.
    /// </summary>
    public float? GlobalJunctionBlendDistanceMeters { get; set; }

    /// <summary>
    ///     When true, bridges are excluded from terrain smoothing and material painting.
    ///     When false, bridge ways are treated as normal roads (legacy behavior).
    /// </summary>
    public bool? ExcludeBridgesFromTerrain { get; set; }

    /// <summary>
    ///     When true, tunnels are excluded from terrain smoothing and material painting.
    ///     When false, tunnel ways are treated as normal roads (legacy behavior).
    /// </summary>
    public bool? ExcludeTunnelsFromTerrain { get; set; }

    /// <summary>
    ///     Whether to enable building generation.
    /// </summary>
    public bool? EnableBuildings { get; set; }

    /// <summary>
    ///     Whether to enable building clustering (merging nearby buildings into combined DAE files).
    /// </summary>
    public bool? EnableBuildingClustering { get; set; }

    /// <summary>
    ///     Grid cell size in meters for building clustering.
    /// </summary>
    public float? BuildingClusterCellSize { get; set; }

    /// <summary>
    ///     Maximum LOD level to include in building DAE files (0, 1, or 2).
    /// </summary>
    public int? MaxBuildingLodLevel { get; set; }

    /// <summary>
    ///     LOD bias multiplier for building exports.
    /// </summary>
    public float? BuildingLodBias { get; set; }

    /// <summary>
    ///     Pixel-size cull threshold for the nulldetail node in building DAE files.
    /// </summary>
    public int? NullDetailPixelSize { get; set; }

    /// <summary>
    ///     Selected building features at global level.
    /// </summary>
    public List<OsmFeatureReference>? SelectedBuildingFeatures { get; set; }

    /// <summary>
    ///     Terrain size in pixels (e.g., 1024, 2048, 4096).
    /// </summary>
    public int? TerrainSize { get; set; }

    // ========== NEW: Crop/Selection Settings (for GeoTIFF) ==========

    /// <summary>
    ///     Crop offset X in source pixels.
    /// </summary>
    public int? CropOffsetX { get; set; }

    /// <summary>
    ///     Crop offset Y in source pixels.
    /// </summary>
    public int? CropOffsetY { get; set; }

    /// <summary>
    ///     Crop width in source pixels.
    /// </summary>
    public int? CropWidth { get; set; }

    /// <summary>
    ///     Crop height in source pixels.
    /// </summary>
    public int? CropHeight { get; set; }

    // ========== NEW: GeoTIFF Metadata (for validation/UI) ==========

    /// <summary>
    ///     Original GeoTIFF width in pixels.
    /// </summary>
    public int? GeoTiffOriginalWidth { get; set; }

    /// <summary>
    ///     Original GeoTIFF height in pixels.
    /// </summary>
    public int? GeoTiffOriginalHeight { get; set; }

    /// <summary>
    ///     GeoTIFF projection/CRS name.
    /// </summary>
    public string? GeoTiffProjectionName { get; set; }

    /// <summary>
    ///     Native pixel size in meters (average of X and Y).
    /// </summary>
    public float? NativePixelSizeMeters { get; set; }

    // ========== NEW: Per-Material OSM Feature Selections ==========

    /// <summary>
    ///     Per-material layer source settings.
    ///     Key: Material internal name, Value: Layer source settings including OSM features.
    /// </summary>
    public Dictionary<string, MaterialLayerSettings>? MaterialLayerSettings { get; set; }
}

/// <summary>
///     Per-material layer source configuration for preset export/import.
/// </summary>
public class MaterialLayerSettings
{
    /// <summary>
    ///     The type of layer source (None, PngFile, or OsmFeatures).
    /// </summary>
    public LayerSourceType LayerSourceType { get; set; }

    /// <summary>
    ///     Selected OSM features (references only, data will be re-fetched on import).
    /// </summary>
    public List<OsmFeatureReference>? OsmFeatureSelections { get; set; }

    /// <summary>
    ///     Material order in the terrain layer stack.
    /// </summary>
    public int? Order { get; set; }

    /// <summary>
    ///     Path to the layer map PNG file.
    /// </summary>
    public string? LayerMapPath { get; set; }

    /// <summary>
    ///     Whether this material has road smoothing enabled.
    /// </summary>
    public bool IsRoadMaterial { get; set; }

    /// <summary>
    ///     Road smoothing settings (only populated if IsRoadMaterial is true).
    /// </summary>
    public RoadSmoothingSettings? RoadSmoothing { get; set; }
}

/// <summary>
///     Road smoothing settings for preset export/import.
/// </summary>
public class RoadSmoothingSettings
{
    // Preset selection
    public string? SelectedPreset { get; set; }

    // Primary parameters
    public float RoadWidthMeters { get; set; } = 8.0f;
    public float? RoadSurfaceWidthMeters { get; set; }
    public float TerrainAffectedRangeMeters { get; set; } = 6.0f;

    /// <summary>
    ///     Buffer distance beyond road edge protected from other roads' blend zones.
    /// </summary>
    public float RoadEdgeProtectionBufferMeters { get; set; } = 2.0f;

    public bool EnableMaxSlopeConstraint { get; set; }
    public float RoadMaxSlopeDegrees { get; set; } = 6.0f;
    public float SideMaxSlopeDegrees { get; set; } = 45.0f;

    // Algorithm settings
    public string BlendFunctionType { get; set; } = "Cosine";
    public float CrossSectionIntervalMeters { get; set; } = 0.5f;
    public bool EnableTerrainBlending { get; set; } = true;

    // Spline parameters
    public SplineParametersSettings? SplineParameters { get; set; }

    // Post-processing
    public PostProcessingSettings? PostProcessing { get; set; }

    // Junction harmonization
    public JunctionHarmonizationSettings? JunctionHarmonization { get; set; }
}

/// <summary>
///     Spline parameters for road smoothing preset.
/// </summary>
public class SplineParametersSettings
{
    public string SplineInterpolationType { get; set; } = "SmoothInterpolated";
    public float Tension { get; set; } = 0.2f;
    public float Continuity { get; set; } = 0.7f;
    public float Bias { get; set; }
    public bool UseGraphOrdering { get; set; } = true;
    public bool PreferStraightThroughJunctions { get; set; }
    public float DensifyMaxSpacingPixels { get; set; } = 1.5f;
    public float SimplifyTolerancePixels { get; set; } = 0.5f;
    public float BridgeEndpointMaxDistancePixels { get; set; } = 40.0f;
    public float MinPathLengthPixels { get; set; } = 0f;
    public float JunctionAngleThreshold { get; set; } = 90.0f;
    public float OrderingNeighborRadiusPixels { get; set; } = 2.5f;
    public int SkeletonDilationRadius { get; set; }
    public int SmoothingWindowSize { get; set; } = 301;
    public bool UseButterworthFilter { get; set; } = true;
    public int ButterworthFilterOrder { get; set; } = 4;
    public float GlobalLevelingStrength { get; set; }

    /// <summary>
    ///     Banking (superelevation) settings for curved roads.
    /// </summary>
    public BankingSettingsPreset? Banking { get; set; }
}

/// <summary>
///     Banking (superelevation) settings for preset export/import.
/// </summary>
public class BankingSettingsPreset
{
    public bool EnableAutoBanking { get; set; }
    public float MaxBankAngleDegrees { get; set; } = 8.0f;
    public float BankStrength { get; set; } = 0.5f;
    public float AutoBankFalloff { get; set; } = 0.6f;
    public float CurvatureToBankScale { get; set; } = 500.0f;
    public float MinCurveRadiusForMaxBank { get; set; } = 50.0f;
    public float BankTransitionLengthMeters { get; set; } = 30.0f;
}

/// <summary>
///     Post-processing settings for road smoothing preset.
/// </summary>
public class PostProcessingSettings
{
    public bool Enabled { get; set; } = true;
    public string SmoothingType { get; set; } = "Gaussian";
    public int KernelSize { get; set; } = 7;
    public float Sigma { get; set; } = 1.5f;
    public int Iterations { get; set; } = 1;
    public float MaskExtensionMeters { get; set; } = 6.0f;
}

/// <summary>
///     Junction harmonization settings for road smoothing preset.
/// </summary>
public class JunctionHarmonizationSettings
{
    /// <summary>
    ///     When true, uses global junction settings. When false, uses per-material values.
    /// </summary>
    public bool UseGlobalSettings { get; set; } = true;

    public bool EnableJunctionHarmonization { get; set; } = true;
    public float JunctionDetectionRadiusMeters { get; set; } = 5.0f;
    public float JunctionBlendDistanceMeters { get; set; } = 30.0f;
    public string BlendFunctionType { get; set; } = "Cosine";
    public bool EnableEndpointTaper { get; set; } = true;
    public float EndpointTaperDistanceMeters { get; set; } = 30.0f;
    public float EndpointTerrainBlendStrength { get; set; } = 1f;

    // Roundabout settings
    public bool EnableRoundaboutDetection { get; set; } = true;
    public bool EnableRoundaboutRoadTrimming { get; set; } = true;
    public float RoundaboutConnectionRadiusMeters { get; set; } = 10.0f;
    public float RoundaboutOverlapToleranceMeters { get; set; } = 2.0f;
    public bool ForceUniformRoundaboutElevation { get; set; } = true;
    public float? RoundaboutBlendDistanceMeters { get; set; } = 50.0f;
}

/// <summary>
///     Lightweight reference to an OSM feature for preset storage.
///     Contains only the data needed to identify and re-fetch the feature.
/// </summary>
public class OsmFeatureReference
{
    /// <summary>
    ///     The OSM element ID.
    /// </summary>
    public long FeatureId { get; set; }

    /// <summary>
    ///     Human-readable display name.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     The category (highway, landuse, natural, etc.).
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    ///     The sub-category value (e.g., "primary" for highway=primary).
    /// </summary>
    public string SubCategory { get; set; } = string.Empty;

    /// <summary>
    ///     The geometry type of this feature.
    /// </summary>
    public OsmGeometryType GeometryType { get; set; }

    /// <summary>
    ///     Creates a reference from a full OsmFeatureSelection.
    /// </summary>
    public static OsmFeatureReference FromSelection(OsmFeatureSelection selection)
    {
        return new OsmFeatureReference
        {
            FeatureId = selection.FeatureId,
            DisplayName = selection.DisplayName,
            Category = selection.Category,
            SubCategory = selection.SubCategory,
            GeometryType = selection.GeometryType
        };
    }

    /// <summary>
    ///     Converts back to OsmFeatureSelection (without full tags - those need to be re-fetched).
    /// </summary>
    public OsmFeatureSelection ToSelection()
    {
        return new OsmFeatureSelection
        {
            FeatureId = FeatureId,
            DisplayName = DisplayName,
            Category = Category,
            SubCategory = SubCategory,
            GeometryType = GeometryType,
            Tags = new Dictionary<string, string>()
        };
    }
}