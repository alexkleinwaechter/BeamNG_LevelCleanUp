namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Contains combined information about multiple GeoTIFF tiles in a directory.
/// This provides accurate metadata for the combined result, not just the first tile.
/// </summary>
public class GeoTiffDirectoryInfoResult
{
    /// <summary>
    /// Number of tiles found in the directory.
    /// </summary>
    public int TileCount { get; init; }

    /// <summary>
    /// Combined width of all tiles in pixels (calculated from total geographic extent / pixel size).
    /// </summary>
    public int CombinedWidth { get; init; }

    /// <summary>
    /// Combined height of all tiles in pixels (calculated from total geographic extent / pixel size).
    /// </summary>
    public int CombinedHeight { get; init; }

    /// <summary>
    /// The combined bounding box in the native coordinate reference system.
    /// For projected CRS (UTM, etc.), these are NOT lat/lon values.
    /// </summary>
    public required GeoBoundingBox NativeBoundingBox { get; init; }

    /// <summary>
    /// The combined bounding box transformed to WGS84 (EPSG:4326) coordinates.
    /// This is the bounding box to use for Overpass API queries.
    /// May be null if transformation failed or tiles have no projection info.
    /// </summary>
    public GeoBoundingBox? Wgs84BoundingBox { get; init; }

    /// <summary>
    /// The combined 6-element geotransform array representing the merged result.
    /// [0] = top-left X (minX), [1] = pixel width, [2] = row rotation (0),
    /// [3] = top-left Y (maxY), [4] = column rotation (0), [5] = pixel height (negative)
    /// </summary>
    public double[]? CombinedGeoTransform { get; init; }

    /// <summary>
    /// The projection/CRS of the tiles in WKT format (from first tile).
    /// All tiles should have the same projection for proper combination.
    /// </summary>
    public string? Projection { get; init; }

    /// <summary>
    /// Human-readable projection name (e.g., "WGS 84 / UTM zone 32N").
    /// </summary>
    public string? ProjectionName { get; init; }

    /// <summary>
    /// Minimum elevation value across all tiles (in meters or source units).
    /// May be null if elevation statistics could not be computed.
    /// </summary>
    public double? MinElevation { get; init; }

    /// <summary>
    /// Maximum elevation value across all tiles (in meters or source units).
    /// May be null if elevation statistics could not be computed.
    /// </summary>
    public double? MaxElevation { get; init; }

    /// <summary>
    /// Information about each individual tile.
    /// </summary>
    public List<GeoTiffTileInfo> Tiles { get; init; } = [];

    /// <summary>
    /// Validation result from the first tile (represents the directory's validity).
    /// </summary>
    public GeoTiffValidationResult? ValidationResult { get; init; }

    /// <summary>
    /// Whether OSM data can be fetched for these tiles.
    /// </summary>
    public bool CanFetchOsmData { get; init; }

    /// <summary>
    /// Reason OSM data cannot be fetched (if applicable).
    /// </summary>
    public string? OsmBlockedReason { get; init; }

    /// <summary>
    /// Warnings about the tile set (e.g., inconsistent resolutions).
    /// </summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>
    /// Whether the tiles are in a geographic (lat/lon) coordinate system.
    /// </summary>
    public bool IsGeographic => Wgs84BoundingBox != null &&
                                Math.Abs(NativeBoundingBox.MinLongitude - Wgs84BoundingBox.MinLongitude) < 0.0001;

    /// <summary>
    /// Whether the tiles have valid WGS84 coordinates available for OSM queries.
    /// </summary>
    public bool HasValidWgs84Coordinates => Wgs84BoundingBox?.IsValidWgs84 == true;

    /// <summary>
    /// Gets the native pixel size in X direction (from GeoTransform).
    /// </summary>
    public double NativePixelSizeX => CombinedGeoTransform != null ? Math.Abs(CombinedGeoTransform[1]) : 0;

    /// <summary>
    /// Gets the native pixel size in Y direction (from GeoTransform).
    /// </summary>
    public double NativePixelSizeY => CombinedGeoTransform != null ? Math.Abs(CombinedGeoTransform[5]) : 0;
}

/// <summary>
/// Information about an individual GeoTIFF tile within a directory.
/// </summary>
public class GeoTiffTileInfo
{
    /// <summary>
    /// File path of the tile.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// File name of the tile.
    /// </summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>
    /// Width of this tile in pixels.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Height of this tile in pixels.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Native bounding box of this tile.
    /// </summary>
    public required GeoBoundingBox BoundingBox { get; init; }

    /// <summary>
    /// WGS84 bounding box of this tile (if available).
    /// </summary>
    public GeoBoundingBox? Wgs84BoundingBox { get; init; }

    /// <summary>
    /// Minimum elevation in this tile.
    /// </summary>
    public double? MinElevation { get; init; }

    /// <summary>
    /// Maximum elevation in this tile.
    /// </summary>
    public double? MaxElevation { get; init; }
}
