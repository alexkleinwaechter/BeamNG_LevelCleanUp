using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNG_LevelCleanUp.BlazorUI.Services;

/// <summary>
///     Service for reading and managing GeoTIFF metadata.
///     Encapsulates all GeoTIFF-related operations to reduce page complexity.
/// </summary>
public class GeoTiffMetadataService
{
    /// <summary>
    ///     Reads GeoTIFF metadata from a single file.
    /// </summary>
    public async Task<GeoTiffMetadataResult> ReadFromFileAsync(string geoTiffPath)
    {
        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();

            // Validate first
            var validationResult = reader.ValidateGeoTiff(geoTiffPath);
            LogValidationResult(validationResult);

            // Read extended info
            var info = reader.GetGeoTiffInfoExtended(geoTiffPath);
            var suggestedTerrainSize = GetNearestPowerOfTwo(Math.Max(info.Width, info.Height));

            LogMetadataInfo(info, suggestedTerrainSize);

            return new GeoTiffMetadataResult
            {
                Wgs84BoundingBox = info.Wgs84BoundingBox,
                NativeBoundingBox = info.BoundingBox,
                ProjectionName = info.ProjectionName,
                ProjectionWkt = info.Projection,
                GeoTransform = info.GeoTransform,
                OriginalWidth = info.Width,
                OriginalHeight = info.Height,
                MinElevation = info.MinElevation,
                MaxElevation = info.MaxElevation,
                SuggestedTerrainSize = suggestedTerrainSize,
                CanFetchOsmData = validationResult.CanFetchOsmData,
                OsmBlockedReason = validationResult.OsmBlockedReason,
                ValidationResult = validationResult
            };
        });
    }

    /// <summary>
    ///     Reads GeoTIFF metadata from a directory of tiles.
    /// </summary>
    public async Task<GeoTiffMetadataResult> ReadFromDirectoryAsync(string geoTiffDirectory, IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();

            GeoTiffDirectoryInfoResult dirInfo;
            try
            {
                dirInfo = reader.GetGeoTiffDirectoryInfoExtended(geoTiffDirectory, progress);
            }
            catch (InvalidOperationException ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, ex.Message);
                return new GeoTiffMetadataResult();
            }

            // Log validation warnings
            foreach (var warning in dirInfo.Warnings)
                PubSubChannel.SendMessage(PubSubMessageType.Warning, $"GeoTIFF Tiles: {warning}");

            if (dirInfo.ValidationResult != null) LogValidationResult(dirInfo.ValidationResult);

            var suggestedTerrainSize = GetNearestPowerOfTwo(Math.Max(dirInfo.CombinedWidth, dirInfo.CombinedHeight));

            // Log combined info
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found {dirInfo.TileCount} GeoTIFF tile(s), combined size {dirInfo.CombinedWidth}x{dirInfo.CombinedHeight}px");

            return new GeoTiffMetadataResult
            {
                Wgs84BoundingBox = dirInfo.Wgs84BoundingBox,
                NativeBoundingBox = dirInfo.NativeBoundingBox,
                ProjectionName = dirInfo.ProjectionName,
                ProjectionWkt = dirInfo.Projection,
                GeoTransform = dirInfo.CombinedGeoTransform,
                OriginalWidth = dirInfo.CombinedWidth,
                OriginalHeight = dirInfo.CombinedHeight,
                MinElevation = dirInfo.MinElevation,
                MaxElevation = dirInfo.MaxElevation,
                SuggestedTerrainSize = suggestedTerrainSize,
                CanFetchOsmData = dirInfo.CanFetchOsmData,
                OsmBlockedReason = dirInfo.OsmBlockedReason,
                ValidationResult = dirInfo.ValidationResult
            };
        });
    }

    /// <summary>
    ///     Reads combined metadata from multiple XYZ ASCII elevation tiles.
    ///     Iterates all tiles to compute combined dimensions, bounding box, and elevation range.
    ///     Mirrors <see cref="ReadFromDirectoryAsync"/> but for XYZ files.
    /// </summary>
    public async Task<GeoTiffMetadataResult> ReadFromXyzFilesAsync(string[] xyzPaths, int epsgCode, IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();

            GeoTiffDirectoryInfoResult dirInfo;
            try
            {
                dirInfo = reader.GetXyzFilesInfoExtended(xyzPaths, epsgCode, progress);
            }
            catch (InvalidOperationException ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning, ex.Message);
                return new GeoTiffMetadataResult();
            }

            foreach (var warning in dirInfo.Warnings)
                PubSubChannel.SendMessage(PubSubMessageType.Warning, $"XYZ Tiles: {warning}");

            var suggestedTerrainSize = GetNearestPowerOfTwo(Math.Max(dirInfo.CombinedWidth, dirInfo.CombinedHeight));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Found {dirInfo.TileCount} XYZ tile(s), combined size {dirInfo.CombinedWidth}x{dirInfo.CombinedHeight}px");

            return new GeoTiffMetadataResult
            {
                Wgs84BoundingBox = dirInfo.Wgs84BoundingBox,
                NativeBoundingBox = dirInfo.NativeBoundingBox,
                ProjectionName = dirInfo.ProjectionName,
                ProjectionWkt = dirInfo.Projection,
                GeoTransform = dirInfo.CombinedGeoTransform,
                OriginalWidth = dirInfo.CombinedWidth,
                OriginalHeight = dirInfo.CombinedHeight,
                MinElevation = dirInfo.MinElevation,
                MaxElevation = dirInfo.MaxElevation,
                SuggestedTerrainSize = suggestedTerrainSize,
                CanFetchOsmData = dirInfo.CanFetchOsmData,
                OsmBlockedReason = dirInfo.OsmBlockedReason
            };
        });
    }

    /// <summary>
    ///     Reads metadata from an XYZ ASCII elevation file.
    ///     Uses GDAL's native XYZ driver with the provided EPSG code for coordinate transformation.
    /// </summary>
    public async Task<GeoTiffMetadataResult> ReadFromXyzFileAsync(string xyzPath, int epsgCode)
    {
        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();
            var info = reader.GetXyzInfoExtended(xyzPath, epsgCode);
            var suggestedTerrainSize = GetNearestPowerOfTwo(Math.Max(info.Width, info.Height));

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"XYZ: {info.Width}x{info.Height}px, EPSG:{epsgCode}");
            if (info.MinElevation.HasValue && info.MaxElevation.HasValue)
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Elevation: {info.MinElevation:F1}m to {info.MaxElevation:F1}m");

            return new GeoTiffMetadataResult
            {
                Wgs84BoundingBox = info.Wgs84BoundingBox,
                NativeBoundingBox = info.BoundingBox,
                ProjectionName = info.ProjectionName,
                ProjectionWkt = info.Projection,
                GeoTransform = info.GeoTransform,
                OriginalWidth = info.Width,
                OriginalHeight = info.Height,
                MinElevation = info.MinElevation,
                MaxElevation = info.MaxElevation,
                SuggestedTerrainSize = suggestedTerrainSize,
                CanFetchOsmData = info.Wgs84BoundingBox?.IsValidWgs84 == true,
                OsmBlockedReason = info.Wgs84BoundingBox?.IsValidWgs84 != true
                    ? "XYZ file requires valid EPSG code for WGS84 coordinate transformation"
                    : null
            };
        });
    }

    /// <summary>
    ///     Gets the elevation range for a cropped region of a GeoTIFF.
    /// </summary>
    public async Task<(double? Min, double? Max)> GetCroppedElevationRangeAsync(
        string geoTiffPath,
        int offsetX,
        int offsetY,
        int cropWidth,
        int cropHeight)
    {
        return await Task.Run(() =>
        {
            var reader = new GeoTiffReader();
            return reader.GetCroppedElevationRange(geoTiffPath, offsetX, offsetY, cropWidth, cropHeight);
        });
    }

    /// <summary>
    ///     Combines multiple GeoTIFF tiles into a single file.
    /// </summary>
    public async Task<string> CombineGeoTiffTilesAsync(string sourceDirectory)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"combined_geotiff_{Guid.NewGuid():N}.tif");

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Combining GeoTIFF tiles (one-time operation)...");

        var combiner = new GeoTiffCombiner();
        await combiner.CombineGeoTiffsAsync(sourceDirectory, outputPath);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "GeoTIFF tiles combined. Subsequent crop changes will be fast.");

        return outputPath;
    }

    /// <summary>
    ///     Combines multiple XYZ ASCII tiles into a single GeoTIFF file with the provided EPSG projection.
    /// </summary>
    public async Task<string> CombineXyzTilesAsync(string[] xyzFilePaths, int epsgCode)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"combined_xyz_{Guid.NewGuid():N}.tif");

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Combining {xyzFilePaths.Length} XYZ tiles (one-time operation)...");

        // Build projection WKT from EPSG code
        var srs = new OSGeo.OSR.SpatialReference(null);
        if (srs.ImportFromEPSG(epsgCode) != 0)
            throw new ArgumentException($"Invalid EPSG code: {epsgCode}");
        srs.ExportToWkt(out var projectionWkt, null);

        var combiner = new GeoTiffCombiner();
        await combiner.CombineFilesAsync(xyzFilePaths, outputPath, projectionWkt);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "XYZ tiles combined. Subsequent crop changes will be fast.");

        return outputPath;
    }

    /// <summary>
    ///     Checks if a GeoTIFF uses a geographic (lat/lon) coordinate system.
    /// </summary>
    public bool IsGeographicCrs(double[]? geoTransform)
    {
        if (geoTransform == null) return false;
        var pixelSizeX = Math.Abs(geoTransform[1]);
        return pixelSizeX < 0.1;
    }

    /// <summary>
    ///     Gets the average native pixel size in meters.
    /// </summary>
    public float GetNativePixelSizeAverage(double[]? geoTransform, GeoBoundingBox? geoBoundingBox)
    {
        if (geoTransform == null) return 1.0f;

        var pixelSizeX = Math.Abs(geoTransform[1]);
        var pixelSizeY = Math.Abs(geoTransform[5]);

        if (IsGeographicCrs(geoTransform))
        {
            var centerLat = geoBoundingBox != null
                ? (geoBoundingBox.MinLatitude + geoBoundingBox.MaxLatitude) / 2.0
                : 35.0;

            var metersPerDegreeLon = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
            var metersPerDegreeLat = 111320.0;

            var metersX = pixelSizeX * metersPerDegreeLon;
            var metersY = pixelSizeY * metersPerDegreeLat;

            return (float)((metersX + metersY) / 2.0);
        }

        return (float)((pixelSizeX + pixelSizeY) / 2.0);
    }

    /// <summary>
    ///     Gets the native pixel size description for display.
    /// </summary>
    public string GetNativePixelSizeDescription(double[]? geoTransform, GeoBoundingBox? geoBoundingBox)
    {
        if (geoTransform == null) return "Unknown";

        var pixelSizeX = Math.Abs(geoTransform[1]);
        var pixelSizeY = Math.Abs(geoTransform[5]);

        if (IsGeographicCrs(geoTransform))
        {
            var arcSecX = pixelSizeX * 3600;
            var arcSecY = pixelSizeY * 3600;

            var centerLat = geoBoundingBox != null
                ? (geoBoundingBox.MinLatitude + geoBoundingBox.MaxLatitude) / 2.0
                : 35.0;
            var metersPerDegree = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
            var approxMetersX = pixelSizeX * metersPerDegree;
            var approxMetersY = pixelSizeY * 111320.0;

            return $"{arcSecX:F1}\" � {arcSecY:F1}\" (~{approxMetersX:F0}m � {approxMetersY:F0}m)";
        }

        return $"{pixelSizeX:F2}m � {pixelSizeY:F2}m";
    }

    /// <summary>
    ///     Gets the real-world width in kilometers.
    /// </summary>
    public double GetRealWorldWidthKm(double[]? geoTransform, int width, GeoBoundingBox? geoBoundingBox)
    {
        if (geoTransform == null || width == 0) return 0;

        if (IsGeographicCrs(geoTransform))
        {
            var degreesWidth = Math.Abs(geoTransform[1]) * width;
            var centerLat = geoBoundingBox != null
                ? (geoBoundingBox.MinLatitude + geoBoundingBox.MaxLatitude) / 2.0
                : 35.0;
            var metersPerDegree = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
            return degreesWidth * metersPerDegree / 1000.0;
        }

        return Math.Abs(geoTransform[1]) * width / 1000.0;
    }

    /// <summary>
    ///     Gets the real-world height in kilometers.
    /// </summary>
    public double GetRealWorldHeightKm(double[]? geoTransform, int height, GeoBoundingBox? geoBoundingBox)
    {
        if (geoTransform == null || height == 0) return 0;

        if (IsGeographicCrs(geoTransform))
        {
            var degreesHeight = Math.Abs(geoTransform[5]) * height;
            return degreesHeight * 111.32;
        }

        return Math.Abs(geoTransform[5]) * height / 1000.0;
    }

    private void LogValidationResult(GeoTiffValidationResult validationResult)
    {
        if (!validationResult.IsValid)
            foreach (var error in validationResult.Errors)
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"GeoTIFF Validation: {error}");

        foreach (var warning in validationResult.Warnings)
            PubSubChannel.SendMessage(PubSubMessageType.Warning, $"GeoTIFF: {warning}");

        if (!validationResult.CanFetchOsmData && !string.IsNullOrEmpty(validationResult.OsmBlockedReason))
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"OSM road data will NOT be available: {validationResult.OsmBlockedReason}");
    }

    private void LogMetadataInfo(GeoTiffInfoResult info, int suggestedTerrainSize)
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"GeoTIFF: {info.Width}x{info.Height}px, terrain size will be {suggestedTerrainSize}");
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Projection: {info.ProjectionName}");
    }

    public static int GetNearestPowerOfTwo(int value)
    {
        if (value <= 256) return 256;
        if (value <= 512) return 512;
        if (value <= 1024) return 1024;
        if (value <= 2048) return 2048;
        if (value <= 4096) return 4096;
        if (value <= 8192) return 8192;
        return 16384;
    }

    /// <summary>
    ///     Result of reading GeoTIFF metadata.
    /// </summary>
    public class GeoTiffMetadataResult
    {
        public GeoBoundingBox? Wgs84BoundingBox { get; init; }
        public GeoBoundingBox? NativeBoundingBox { get; init; }
        public string? ProjectionName { get; init; }
        public string? ProjectionWkt { get; init; }
        public double[]? GeoTransform { get; init; }
        public int OriginalWidth { get; init; }
        public int OriginalHeight { get; init; }
        public double? MinElevation { get; init; }
        public double? MaxElevation { get; init; }
        public int? SuggestedTerrainSize { get; init; }
        public bool CanFetchOsmData { get; init; }
        public string? OsmBlockedReason { get; init; }
        public GeoTiffValidationResult? ValidationResult { get; init; }
    }
}