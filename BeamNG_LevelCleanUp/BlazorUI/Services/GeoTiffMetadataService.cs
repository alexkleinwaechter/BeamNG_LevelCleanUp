using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using OSGeo.GDAL;

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
    ///     Gets the elevation range for a cropped region directly from GeoTIFF tiles,
    ///     without creating a combined file. Only reads tiles that overlap the crop region.
    /// </summary>
    public async Task<(double? Min, double? Max)> GetCroppedElevationRangeFromTilesAsync(
        string sourceDirectory,
        int offsetX, int offsetY, int cropWidth, int cropHeight)
    {
        return await Task.Run(() =>
        {
            var inputFiles = Directory.GetFiles(sourceDirectory)
                .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".geotiff", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            // Set up TerrainLogger to forward warnings to PubSub so they appear in the UI
            var previousHandler = BeamNgTerrainPoc.Terrain.Logging.TerrainLogger.GetCurrentHandler();
            BeamNgTerrainPoc.Terrain.Logging.TerrainLogger.SetLogHandler((level, message) =>
            {
                // Forward to previous handler if any
                previousHandler?.Invoke(level, message);

                // Also send warnings/errors to PubSub for UI visibility
                if (level == BeamNgTerrainPoc.Terrain.Logging.TerrainLogLevel.Warning)
                    PubSubChannel.SendMessage(PubSubMessageType.Warning, message);
                else if (level == BeamNgTerrainPoc.Terrain.Logging.TerrainLogLevel.Error)
                    PubSubChannel.SendMessage(PubSubMessageType.Error, message);
            });

            try
            {
                var combiner = new GeoTiffCombiner();
                var (min, max) = combiner.GetCroppedElevationRangeFromTiles(
                    inputFiles, offsetX, offsetY, cropWidth, cropHeight);

                // Send a PubSub warning if elevation looks suspicious
                if (min == 0 && max == 100)
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        "Elevation defaults used (0-100m). GeoTIFF tiles may be corrupted or missing in the crop region.");
                else if (min == 0 && max > 0)
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Base elevation is 0m (range: 0-{max:F0}m). " +
                        "Some tiles may be missing or contain nodata. Check the Messages panel for details.");

                return ((double?)min, (double?)max);
            }
            finally
            {
                // Restore previous handler
                BeamNgTerrainPoc.Terrain.Logging.TerrainLogger.SetLogHandler(previousHandler);
            }
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
    ///     Combines multiple GeoTIFF tiles and crops directly to the selection in a single pass.
    ///     Only reads pixels from tiles that overlap the crop region — much faster than combining all first.
    /// </summary>
    public async Task<string> CombineAndCropDirectAsync(
        string sourceDirectory,
        int offsetX, int offsetY, int cropWidth, int cropHeight)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"cropped_geotiff_{Guid.NewGuid():N}.tif");

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Reducing GeoTIFF tiles directly to selection ({cropWidth}x{cropHeight}px)...");

        await Task.Run(() =>
        {
            var combiner = new GeoTiffCombiner();
            var inputFiles = Directory.GetFiles(sourceDirectory)
                .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".geotiff", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();

            combiner.CombineAndCropDirect(inputFiles, outputPath,
                offsetX, offsetY, cropWidth, cropHeight);
        });

        var fileSize = new FileInfo(outputPath).Length / (1024.0 * 1024.0);
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"GeoTIFF tiles reduced to {cropWidth}x{cropHeight}px ({fileSize:F1} MB)");

        return outputPath;
    }

    /// <summary>
    ///     Combines multiple XYZ files and crops directly to the selection in a single pass.
    /// </summary>
    public async Task<string> CombineXyzAndCropDirectAsync(
        string[] xyzFilePaths, int epsgCode,
        int offsetX, int offsetY, int cropWidth, int cropHeight)
    {
        // XYZ files need to be combined first (they lack GDAL-native tile structure),
        // then crop the combined result
        var combinedPath = await CombineXyzTilesAsync(xyzFilePaths, epsgCode);
        try
        {
            var croppedPath = await CropGeoTiffToFileAsync(combinedPath,
                offsetX, offsetY, cropWidth, cropHeight);
            return croppedPath;
        }
        finally
        {
            try { if (File.Exists(combinedPath)) File.Delete(combinedPath); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    ///     Crops a GeoTIFF file to the specified pixel region and saves it as a new file.
    ///     The output file has an adjusted geotransform so its origin matches the crop region.
    /// </summary>
    /// <param name="sourceGeoTiffPath">Path to the source GeoTIFF file</param>
    /// <param name="offsetX">X offset in pixels from the left edge</param>
    /// <param name="offsetY">Y offset in pixels from the top edge</param>
    /// <param name="cropWidth">Width of the crop region in pixels</param>
    /// <param name="cropHeight">Height of the crop region in pixels</param>
    /// <returns>Path to the cropped GeoTIFF temp file</returns>
    public async Task<string> CropGeoTiffToFileAsync(
        string sourceGeoTiffPath, int offsetX, int offsetY, int cropWidth, int cropHeight)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"cropped_geotiff_{Guid.NewGuid():N}.tif");

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Reducing GeoTIFF to selection ({cropWidth}x{cropHeight}px)...");

        await Task.Run(() =>
        {
            GeoTiffReader.InitializeGdal();

            using var sourceDataset = Gdal.Open(sourceGeoTiffPath, Access.GA_ReadOnly);
            if (sourceDataset == null)
                throw new InvalidOperationException($"Could not open source GeoTIFF: {sourceGeoTiffPath}");

            var sourceGeoTransform = new double[6];
            sourceDataset.GetGeoTransform(sourceGeoTransform);

            var projection = sourceDataset.GetProjection();
            var bandCount = sourceDataset.RasterCount;
            var dataType = sourceDataset.GetRasterBand(1).DataType;

            // Compute adjusted geotransform: shift origin to crop start
            var croppedGeoTransform = new double[6];
            croppedGeoTransform[0] = sourceGeoTransform[0] + offsetX * sourceGeoTransform[1]; // new origin X
            croppedGeoTransform[1] = sourceGeoTransform[1]; // pixel width (unchanged)
            croppedGeoTransform[2] = sourceGeoTransform[2]; // rotation X (unchanged)
            croppedGeoTransform[3] = sourceGeoTransform[3] + offsetY * sourceGeoTransform[5]; // new origin Y
            croppedGeoTransform[4] = sourceGeoTransform[4]; // rotation Y (unchanged)
            croppedGeoTransform[5] = sourceGeoTransform[5]; // pixel height (unchanged)

            // Create output dataset
            var driver = Gdal.GetDriverByName("GTiff");
            using var outputDataset = driver.Create(
                outputPath, cropWidth, cropHeight, bandCount, dataType, null);

            outputDataset.SetGeoTransform(croppedGeoTransform);

            if (!string.IsNullOrEmpty(projection))
                outputDataset.SetProjection(projection);

            // Copy each band from the crop region
            for (var bandIndex = 1; bandIndex <= bandCount; bandIndex++)
            {
                var inputBand = sourceDataset.GetRasterBand(bandIndex);
                var outputBand = outputDataset.GetRasterBand(bandIndex);

                var buffer = new double[cropWidth * cropHeight];
                inputBand.ReadRaster(offsetX, offsetY, cropWidth, cropHeight,
                    buffer, cropWidth, cropHeight, 0, 0);
                outputBand.WriteRaster(0, 0, cropWidth, cropHeight,
                    buffer, cropWidth, cropHeight, 0, 0);
            }

            outputDataset.FlushCache();
        });

        var fileSize = new FileInfo(outputPath).Length / (1024.0 * 1024.0);
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"GeoTIFF reduced to {cropWidth}x{cropHeight}px ({fileSize:F1} MB)");

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

            return $"{arcSecX:F1}\" × {arcSecY:F1}\" (~{approxMetersX:F0}m × {approxMetersY:F0}m)";
        }

        return $"{pixelSizeX:F2}m × {pixelSizeY:F2}m";
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