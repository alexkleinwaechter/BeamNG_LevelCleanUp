using BeamNgTerrainPoc.Terrain.Logging;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
///     Combines multiple GeoTIFF tiles into a single merged GeoTIFF file.
///     Useful when terrain data spans multiple tiles (e.g., SRTM or ASTER GDEM tiles).
/// </summary>
public class GeoTiffCombiner
{
    /// <summary>
    ///     Supported raster tile extensions (GeoTIFF + ESRI ASCII Grid, both GDAL-readable).
    /// </summary>
    private static readonly string[] SupportedExtensions = [".tif", ".tiff", ".geotiff", ".asc"];

    private readonly GeoTiffReader _reader = new();

    /// <summary>
    ///     Combines all GeoTIFF files in a directory into a single merged file.
    /// </summary>
    /// <param name="inputDirectory">Directory containing GeoTIFF tiles</param>
    /// <param name="outputPath">Path for the combined output file</param>
    /// <param name="overrideProjection">Optional projection WKT to write instead of the tiles' embedded CRS</param>
    /// <returns>Bounding box of the combined terrain</returns>
    public async Task<GeoBoundingBox> CombineGeoTiffsAsync(string inputDirectory, string outputPath,
        string? overrideProjection = null)
    {
        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException($"Input directory not found: {inputDirectory}");

        // Find all GeoTIFF files
        var inputFiles = FindGeoTiffFiles(inputDirectory);

        if (inputFiles.Count == 0)
            throw new InvalidOperationException(
                $"No GeoTIFF files found in '{inputDirectory}'. Supported extensions: {string.Join(", ", SupportedExtensions)}");

        TerrainLogger.Info($"Found {inputFiles.Count} GeoTIFF file(s) to combine");

        // If only one file, just copy it
        if (inputFiles.Count == 1)
        {
            TerrainLogger.Info("Single file found, copying directly");
            File.Copy(inputFiles[0], outputPath, true);

            // Stamp the override projection into the copy so downstream reads resolve the CRS
            if (!string.IsNullOrEmpty(overrideProjection))
            {
                GeoTiffReader.InitializeGdal();
                using var copiedDataset = Gdal.Open(outputPath, Access.GA_Update);
                copiedDataset?.SetProjection(overrideProjection);
                copiedDataset?.FlushCache();
            }

            var info = _reader.GetGeoTiffInfo(outputPath);
            return info.BoundingBox;
        }

        // Combine multiple files
        return await Task.Run(() => CombineFilesInternal(inputFiles, outputPath, overrideProjection));
    }

    /// <summary>
    ///     Finds all GeoTIFF files in a directory.
    /// </summary>
    private List<string> FindGeoTiffFiles(string directory)
    {
        var files = new List<string>();

        foreach (var ext in SupportedExtensions)
            files.AddRange(Directory.GetFiles(directory, $"*{ext}", SearchOption.TopDirectoryOnly));

        return files.OrderBy(f => f).ToList();
    }

    /// <summary>
    ///     Combines multiple elevation data files (GeoTIFF, XYZ, etc.) from explicit paths.
    ///     For formats without embedded CRS (like XYZ), pass overrideProjection.
    /// </summary>
    public async Task<GeoBoundingBox> CombineFilesAsync(
        string[] filePaths, string outputPath, string? overrideProjection = null)
    {
        if (filePaths.Length == 0)
            throw new ArgumentException("No files to combine.");

        TerrainLogger.Info($"Found {filePaths.Length} file(s) to combine");

        if (filePaths.Length == 1)
        {
            // Single file: just read and re-export as GeoTIFF (ensures consistent format)
            TerrainLogger.Info("Single file, converting directly");
            return await Task.Run(() =>
                CombineFilesInternal(filePaths.ToList(), outputPath, overrideProjection));
        }

        return await Task.Run(() =>
            CombineFilesInternal(filePaths.ToList(), outputPath, overrideProjection));
    }

    /// <summary>
    ///     Combines multiple files and returns the import result directly.
    /// </summary>
    public async Task<GeoTiffImportResult> CombineFilesAndImportAsync(
        string[] filePaths,
        string? overrideProjection = null,
        int? targetSize = null,
        int? cropOffsetX = null,
        int? cropOffsetY = null,
        int? cropWidth = null,
        int? cropHeight = null,
        string? tempDirectory = null)
    {
        tempDirectory ??= Path.GetTempPath();
        var combinedPath = Path.Combine(tempDirectory, $"combined_{Guid.NewGuid():N}.tif");

        try
        {
            await CombineFilesAsync(filePaths, combinedPath, overrideProjection);

            var shouldCrop = cropOffsetX.HasValue && cropOffsetY.HasValue &&
                             cropWidth.HasValue && cropHeight.HasValue &&
                             cropWidth.Value > 0 && cropHeight.Value > 0;

            if (shouldCrop)
            {
                TerrainLogger.Info(
                    $"Applying crop: offset ({cropOffsetX}, {cropOffsetY}), size {cropWidth}x{cropHeight}");
                return _reader.ReadGeoTiff(combinedPath, targetSize,
                    cropOffsetX, cropOffsetY, cropWidth, cropHeight);
            }

            // The combined GeoTIFF already has the projection embedded by CombineFilesInternal,
            // so no override needed when reading back.
            return _reader.ReadGeoTiff(combinedPath, targetSize);
        }
        finally
        {
            try
            {
                if (File.Exists(combinedPath))
                    File.Delete(combinedPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    ///     Internal method to combine multiple GeoTIFF files.
    /// </summary>
    private GeoBoundingBox CombineFilesInternal(
        List<string> inputFiles, string outputPath, string? overrideProjection = null)
    {
        GeoTiffReader.InitializeGdal();

        // Delete output if it exists
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // Analyze all input files to determine overall bounds
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var geoTransform = new double[6];
        double pixelSizeX = 0, pixelSizeY = 0;
        string? projection = null;
        var dataType = DataType.GDT_Unknown;
        var bandCount = 0;
        double nodataValue = 0;
        var hasNodata = 0;

        // Enable suppressed logging for bulk operations
        var previousSuppressState = TerrainLogger.SuppressDetailedLogging;
        TerrainLogger.SuppressDetailedLogging = false;

        try
        {
            // First pass: get bounds from all tiles
            TerrainLogger.Info($"Analyzing {inputFiles.Count} tiles for bounds...");
            var analyzedCount = 0;
            int? firstTileWidth = null, firstTileHeight = null;

            foreach (var file in inputFiles)
            {
                using var dataset = Gdal.Open(file, Access.GA_ReadOnly);
                if (dataset == null)
                {
                    TerrainLogger.DetailWarning($"Could not open file, skipping: {file}");
                    continue;
                }

                dataset.GetGeoTransform(geoTransform);

                // Store first tile's metadata as reference
                if (pixelSizeX == 0)
                {
                    pixelSizeX = Math.Abs(geoTransform[1]);
                    pixelSizeY = Math.Abs(geoTransform[5]);
                    firstTileWidth = dataset.RasterXSize;
                    firstTileHeight = dataset.RasterYSize;
                    projection = overrideProjection ?? dataset.GetProjection();
                    bandCount = dataset.RasterCount;
                    dataType = dataset.GetRasterBand(1).DataType;
                    dataset.GetRasterBand(1).GetNoDataValue(out nodataValue, out hasNodata);
                }
                else
                {
                    // Log info about tiles with different dimensions (common for edge tiles)
                    if (dataset.RasterXSize != firstTileWidth || dataset.RasterYSize != firstTileHeight)
                        TerrainLogger.Detail(
                            $"Tile {Path.GetFileName(file)} has different size: {dataset.RasterXSize}x{dataset.RasterYSize} (first tile: {firstTileWidth}x{firstTileHeight})");
                }

                // Calculate tile bounds
                var tileMinX = geoTransform[0];
                var tileMaxY = geoTransform[3];
                var tileMaxX = tileMinX + geoTransform[1] * dataset.RasterXSize;
                var tileMinY = tileMaxY + geoTransform[5] * dataset.RasterYSize;

                // Update overall bounds
                minX = Math.Min(minX, tileMinX);
                minY = Math.Min(minY, tileMinY);
                maxX = Math.Max(maxX, tileMaxX);
                maxY = Math.Max(maxY, tileMaxY);

                analyzedCount++;

                // Report progress every 10 tiles or at completion
                if (analyzedCount % 10 == 0 || analyzedCount == inputFiles.Count)
                    TerrainLogger.Info($"Analyzed {analyzedCount}/{inputFiles.Count} tiles...");
            }

            TerrainLogger.Info($"Combined extent: X[{minX:F4} - {maxX:F4}], Y[{minY:F4} - {maxY:F4}]");

            // Calculate total output dimensions
            var totalWidth = (int)Math.Round((maxX - minX) / pixelSizeX);
            var totalHeight = (int)Math.Round((maxY - minY) / pixelSizeY);

            TerrainLogger.Info($"Output dimensions: {totalWidth}x{totalHeight} pixels");

            // Create output dataset
            var driver = Gdal.GetDriverByName("GTiff");
            using var outputDataset = driver.Create(
                outputPath,
                totalWidth,
                totalHeight,
                bandCount,
                dataType,
                null);

            // Set output geotransform
            outputDataset.SetGeoTransform([
                minX, // Origin X
                pixelSizeX, // Pixel width
                0, // Rotation X
                maxY, // Origin Y (top)
                0, // Rotation Y
                -pixelSizeY // Pixel height (negative)
            ]);

            if (!string.IsNullOrEmpty(projection))
                outputDataset.SetProjection(projection);

            // Preserve the nodata flag so copied nodata pixels stay recognizable.
            // No Fill() here: the combined output can be huge (a whole département) and filling
            // would materialize every block; uncovered gaps keep reading as 0 (existing behavior).
            if (hasNodata != 0)
                for (var bandIndex = 1; bandIndex <= bandCount; bandIndex++)
                    outputDataset.GetRasterBand(bandIndex).SetNoDataValue(nodataValue);

            // Second pass: copy data from each tile
            TerrainLogger.Info($"Copying {inputFiles.Count} tiles to combined image...");
            var copiedCount = 0;

            foreach (var file in inputFiles)
            {
                using var inputDataset = Gdal.Open(file, Access.GA_ReadOnly);
                if (inputDataset == null) continue;

                // Get THIS tile's actual dimensions (tiles may have different sizes)
                var thisTileWidth = inputDataset.RasterXSize;
                var thisTileHeight = inputDataset.RasterYSize;

                var inputGeoTransform = new double[6];
                inputDataset.GetGeoTransform(inputGeoTransform);

                // Calculate offset in output image
                var xOffset = (int)Math.Round((inputGeoTransform[0] - minX) / pixelSizeX);
                var yOffset = (int)Math.Round((maxY - inputGeoTransform[3]) / pixelSizeY);

                // Clamp offsets using THIS tile's dimensions
                xOffset = Math.Max(0, Math.Min(xOffset, totalWidth - thisTileWidth));
                yOffset = Math.Max(0, Math.Min(yOffset, totalHeight - thisTileHeight));

                // Use Detail for per-tile messages (suppressed from UI when many tiles)
                TerrainLogger.SuppressDetailedLogging = true;
                TerrainLogger.Detail(
                    $"Copying tile {Path.GetFileName(file)} ({thisTileWidth}x{thisTileHeight}) to offset ({xOffset}, {yOffset})");

                // Copy each band
                for (var bandIndex = 1; bandIndex <= bandCount; bandIndex++)
                {
                    var inputBand = inputDataset.GetRasterBand(bandIndex);
                    var outputBand = outputDataset.GetRasterBand(bandIndex);

                    // Use THIS tile's dimensions for buffer and read/write
                    var buffer = new double[thisTileWidth * thisTileHeight];
                    inputBand.ReadRaster(0, 0, thisTileWidth, thisTileHeight, buffer, thisTileWidth, thisTileHeight, 0,
                        0);
                    outputBand.WriteRaster(xOffset, yOffset, thisTileWidth, thisTileHeight, buffer, thisTileWidth,
                        thisTileHeight, 0, 0);
                }

                copiedCount++;

                // Report progress every 10 tiles or at completion
                TerrainLogger.SuppressDetailedLogging = false;
                if (copiedCount % 10 == 0 || copiedCount == inputFiles.Count)
                    TerrainLogger.Info($"Copied {copiedCount}/{inputFiles.Count} tiles...");
            }

            outputDataset.FlushCache();

            // Calculate center-aligned bounding box for BeamNG (power of 2)
            var centerX = (minX + maxX) / 2;
            var centerY = (minY + maxY) / 2;

            // Calculate extent that fits BeamNG requirements (multiples of 2048 meters)
            var extentX = maxX - minX;
            var extentY = maxY - minY;

            // For now, return the actual bounds - caller can adjust for BeamNG requirements
            var boundingBox = new GeoBoundingBox(minX, minY, maxX, maxY);

            TerrainLogger.Info($"Combined GeoTIFF saved to: {outputPath}");
            TerrainLogger.Info($"Bounding box: {boundingBox}");

            return boundingBox;
        }
        finally
        {
            // Restore previous suppression state
            TerrainLogger.SuppressDetailedLogging = previousSuppressState;
        }
    }

    /// <summary>
    ///     Gets the elevation range for a cropped region directly from tiles,
    ///     without creating a combined file. Only reads tiles that overlap the crop region.
    /// </summary>
    public (double MinElevation, double MaxElevation) GetCroppedElevationRangeFromTiles(
        List<string> inputFiles,
        int cropOffsetX, int cropOffsetY, int cropWidth, int cropHeight)
    {
        GeoTiffReader.InitializeGdal();

        // First pass: analyze tiles for combined bounds
        double minX = double.MaxValue, maxY = double.MinValue;
        double maxX = double.MinValue, minY = double.MaxValue;
        double pixelSizeX = 0, pixelSizeY = 0;
        var unopenableTiles = new List<string>();

        foreach (var file in inputFiles)
        {
            using var dataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (dataset == null)
            {
                unopenableTiles.Add(Path.GetFileName(file));
                continue;
            }

            var gt = new double[6];
            dataset.GetGeoTransform(gt);

            if (pixelSizeX == 0)
            {
                pixelSizeX = Math.Abs(gt[1]);
                pixelSizeY = Math.Abs(gt[5]);
            }

            var tileMinX = gt[0];
            var tileMaxY = gt[3];
            minX = Math.Min(minX, tileMinX);
            maxY = Math.Max(maxY, tileMaxY);
            maxX = Math.Max(maxX, tileMinX + gt[1] * dataset.RasterXSize);
            minY = Math.Min(minY, tileMaxY + gt[5] * dataset.RasterYSize);
        }

        if (unopenableTiles.Count > 0)
            TerrainLogger.Warning(
                $"Could not open {unopenableTiles.Count} tile(s): {string.Join(", ", unopenableTiles.Take(5))}" +
                (unopenableTiles.Count > 5 ? $" and {unopenableTiles.Count - 5} more" : ""));

        if (pixelSizeX == 0)
        {
            TerrainLogger.Warning("No valid GeoTIFF tiles found. Base height will default to 0.");
            return (0, 100);
        }

        var cropRight = cropOffsetX + cropWidth;
        var cropBottom = cropOffsetY + cropHeight;
        var totalCropPixels = (long)cropWidth * cropHeight;

        var globalMin = double.MaxValue;
        var globalMax = double.MinValue;
        var validCount = 0L;
        var nodataCount = 0L;
        var zeroCount = 0L;
        var skippedTiles = 0;
        var contributingTiles = 0;
        var coveredPixels = 0L; // Total pixels covered by overlapping tiles

        // Second pass: read elevation only from overlapping tiles
        foreach (var file in inputFiles)
        {
            using var inputDataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (inputDataset == null) continue;

            var gt = new double[6];
            inputDataset.GetGeoTransform(gt);

            var tileOffsetX = (int)Math.Round((gt[0] - minX) / pixelSizeX);
            var tileOffsetY = (int)Math.Round((maxY - gt[3]) / pixelSizeY);
            var thisTileWidth = inputDataset.RasterXSize;
            var thisTileHeight = inputDataset.RasterYSize;

            var tileRight = tileOffsetX + thisTileWidth;
            var tileBottom = tileOffsetY + thisTileHeight;

            // Skip tiles with no overlap
            if (tileRight <= cropOffsetX || tileOffsetX >= cropRight ||
                tileBottom <= cropOffsetY || tileOffsetY >= cropBottom)
            {
                skippedTiles++;
                continue;
            }

            // Calculate intersection
            var isectLeft = Math.Max(tileOffsetX, cropOffsetX);
            var isectTop = Math.Max(tileOffsetY, cropOffsetY);
            var isectRight = Math.Min(tileRight, cropRight);
            var isectBottom = Math.Min(tileBottom, cropBottom);
            var isectWidth = isectRight - isectLeft;
            var isectHeight = isectBottom - isectTop;

            if (isectWidth <= 0 || isectHeight <= 0)
            {
                skippedTiles++;
                continue;
            }

            coveredPixels += (long)isectWidth * isectHeight;
            contributingTiles++;

            var readX = isectLeft - tileOffsetX;
            var readY = isectTop - tileOffsetY;

            var band = inputDataset.GetRasterBand(1);
            var buffer = new double[isectWidth * isectHeight];
            band.ReadRaster(readX, readY, isectWidth, isectHeight,
                buffer, isectWidth, isectHeight, 0, 0);

            // Check for nodata
            band.GetNoDataValue(out var nodataValue, out var hasNodata);
            var useNodata = hasNodata != 0;
            var tileValidCount = 0;
            var tileNodataCount = 0;

            foreach (var elevation in buffer)
            {
                if (useNodata && Math.Abs(elevation - nodataValue) < 0.001)
                {
                    tileNodataCount++;
                    nodataCount++;
                    continue;
                }

                if (elevation < -1000000 || elevation > 1000000)
                {
                    tileNodataCount++;
                    nodataCount++;
                    continue;
                }

                if (Math.Abs(elevation) < 0.001)
                    zeroCount++;

                if (elevation < globalMin) globalMin = elevation;
                if (elevation > globalMax) globalMax = elevation;
                tileValidCount++;
                validCount++;
            }

            // Warn about tiles with high nodata percentage
            var totalTilePixels = buffer.Length;
            if (tileNodataCount > 0 && tileValidCount == 0)
            {
                TerrainLogger.Warning(
                    $"Tile {Path.GetFileName(file)}: 100% nodata/invalid in crop overlap " +
                    $"({tileNodataCount} pixels) — tile may be corrupted or empty");
            }
            else if (tileNodataCount > totalTilePixels * 0.5)
            {
                var pct = (double)tileNodataCount / totalTilePixels * 100;
                TerrainLogger.Warning(
                    $"Tile {Path.GetFileName(file)}: {pct:F0}% nodata in crop overlap " +
                    $"({tileNodataCount}/{totalTilePixels} pixels)");
            }
        }

        // Check for coverage gaps
        if (coveredPixels < totalCropPixels)
        {
            var coveragePct = (double)coveredPixels / totalCropPixels * 100;
            var gapPixels = totalCropPixels - coveredPixels;
            TerrainLogger.Warning(
                $"Crop region only {coveragePct:F0}% covered by tiles ({gapPixels} pixels have no tile data). " +
                "Missing tiles will result in zero elevation. Base height may be incorrect.");
        }

        if (validCount == 0 || globalMin == double.MaxValue)
        {
            TerrainLogger.Warning(
                $"No valid elevation data in crop region ({contributingTiles} tiles checked, " +
                $"{nodataCount} nodata pixels). Base height defaults to 0m.");
            return (0, 100);
        }

        // Warn if suspiciously many zero-elevation pixels
        if (zeroCount > validCount * 0.3 && globalMin >= 0)
        {
            var zeroPct = (double)zeroCount / validCount * 100;
            TerrainLogger.Warning(
                $"Elevation data has {zeroPct:F0}% zero-value pixels ({zeroCount}/{validCount}). " +
                "This may indicate missing data disguised as zero elevation. " +
                $"Calculated base height: {globalMin:F1}m");
        }

        TerrainLogger.Info(
            $"Elevation from tiles: {globalMin:F1}m to {globalMax:F1}m " +
            $"({contributingTiles} tiles, {skippedTiles} skipped, " +
            $"{validCount} valid pixels, {nodataCount} nodata)");

        return (globalMin, globalMax);
    }

    /// <summary>
    ///     Combines multiple GeoTIFF tiles directly into a cropped output file.
    ///     Only reads pixels from tiles that overlap the crop region, skipping the rest entirely.
    ///     Much faster than combining all tiles first and then cropping.
    /// </summary>
    /// <param name="inputFiles">List of GeoTIFF file paths</param>
    /// <param name="outputPath">Path for the cropped output file</param>
    /// <param name="cropOffsetX">X offset in combined-image pixels</param>
    /// <param name="cropOffsetY">Y offset in combined-image pixels</param>
    /// <param name="cropWidth">Width of the crop region in pixels</param>
    /// <param name="cropHeight">Height of the crop region in pixels</param>
    /// <param name="overrideProjection">Optional projection WKT to write instead of the tiles' embedded CRS</param>
    public void CombineAndCropDirect(
        List<string> inputFiles, string outputPath,
        int cropOffsetX, int cropOffsetY, int cropWidth, int cropHeight,
        string? overrideProjection = null)
    {
        GeoTiffReader.InitializeGdal();

        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // First pass: analyze tiles for combined bounds and metadata
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var geoTransform = new double[6];
        double pixelSizeX = 0, pixelSizeY = 0;
        string? projection = null;
        var dataType = DataType.GDT_Unknown;
        var bandCount = 0;
        double nodataValue = 0;
        var hasNodata = 0;

        foreach (var file in inputFiles)
        {
            using var dataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (dataset == null) continue;

            dataset.GetGeoTransform(geoTransform);

            if (pixelSizeX == 0)
            {
                pixelSizeX = Math.Abs(geoTransform[1]);
                pixelSizeY = Math.Abs(geoTransform[5]);
                projection = overrideProjection ?? dataset.GetProjection();
                bandCount = dataset.RasterCount;
                dataType = dataset.GetRasterBand(1).DataType;
                dataset.GetRasterBand(1).GetNoDataValue(out nodataValue, out hasNodata);
            }

            var tileMinX = geoTransform[0];
            var tileMaxY = geoTransform[3];
            var tileMaxX = tileMinX + geoTransform[1] * dataset.RasterXSize;
            var tileMinY = tileMaxY + geoTransform[5] * dataset.RasterYSize;

            minX = Math.Min(minX, tileMinX);
            minY = Math.Min(minY, tileMinY);
            maxX = Math.Max(maxX, tileMaxX);
            maxY = Math.Max(maxY, tileMaxY);
        }

        if (pixelSizeX == 0)
            throw new InvalidOperationException("No valid GeoTIFF files found.");

        if (!string.IsNullOrEmpty(overrideProjection))
            TerrainLogger.Info("Direct crop: writing EPSG override projection into the output");
        else if (string.IsNullOrEmpty(projection))
            TerrainLogger.Warning(
                "Direct crop: tiles carry no embedded CRS and no EPSG override is set - output will have NO projection.");

        // Compute the crop region's geographic origin
        var cropOriginX = minX + cropOffsetX * pixelSizeX;
        var cropOriginY = maxY - cropOffsetY * pixelSizeY; // maxY is top, Y goes down

        // Create output dataset with crop dimensions
        var driver = Gdal.GetDriverByName("GTiff");
        using var outputDataset = driver.Create(
            outputPath, cropWidth, cropHeight, bandCount, dataType, null);

        outputDataset.SetGeoTransform([
            cropOriginX,   // Origin X (adjusted to crop start)
            pixelSizeX,    // Pixel width
            0,             // Rotation X
            cropOriginY,   // Origin Y (adjusted to crop start)
            0,             // Rotation Y
            -pixelSizeY    // Pixel height (negative)
        ]);

        if (!string.IsNullOrEmpty(projection))
            outputDataset.SetProjection(projection);

        // Preserve the nodata flag and pre-fill the (bounded, crop-sized) output with nodata so
        // areas no tile covers read as nodata instead of elevation 0
        if (hasNodata != 0)
            for (var bandIndex = 1; bandIndex <= bandCount; bandIndex++)
            {
                var outputBand = outputDataset.GetRasterBand(bandIndex);
                outputBand.SetNoDataValue(nodataValue);
                outputBand.Fill(nodataValue, 0);
            }

        // Second pass: only read tiles that overlap the crop region
        var skippedTiles = 0;
        var copiedTiles = 0;

        foreach (var file in inputFiles)
        {
            using var inputDataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (inputDataset == null) continue;

            var thisTileWidth = inputDataset.RasterXSize;
            var thisTileHeight = inputDataset.RasterYSize;

            var inputGeoTransform = new double[6];
            inputDataset.GetGeoTransform(inputGeoTransform);

            // Calculate this tile's position in the combined image (pixel coords)
            var tileOffsetX = (int)Math.Round((inputGeoTransform[0] - minX) / pixelSizeX);
            var tileOffsetY = (int)Math.Round((maxY - inputGeoTransform[3]) / pixelSizeY);

            // Check if this tile overlaps the crop region
            var tileRight = tileOffsetX + thisTileWidth;
            var tileBottom = tileOffsetY + thisTileHeight;
            var cropRight = cropOffsetX + cropWidth;
            var cropBottom = cropOffsetY + cropHeight;

            if (tileRight <= cropOffsetX || tileOffsetX >= cropRight ||
                tileBottom <= cropOffsetY || tileOffsetY >= cropBottom)
            {
                skippedTiles++;
                continue; // No overlap — skip entirely
            }

            // Calculate the intersection rectangle
            var isectLeft = Math.Max(tileOffsetX, cropOffsetX);
            var isectTop = Math.Max(tileOffsetY, cropOffsetY);
            var isectRight = Math.Min(tileRight, cropRight);
            var isectBottom = Math.Min(tileBottom, cropBottom);

            var isectWidth = isectRight - isectLeft;
            var isectHeight = isectBottom - isectTop;

            if (isectWidth <= 0 || isectHeight <= 0)
            {
                skippedTiles++;
                continue;
            }

            // Read position within this tile
            var readX = isectLeft - tileOffsetX;
            var readY = isectTop - tileOffsetY;

            // Write position within the output (crop-relative)
            var writeX = isectLeft - cropOffsetX;
            var writeY = isectTop - cropOffsetY;

            for (var bandIndex = 1; bandIndex <= bandCount; bandIndex++)
            {
                var inputBand = inputDataset.GetRasterBand(bandIndex);
                var outputBand = outputDataset.GetRasterBand(bandIndex);

                var buffer = new double[isectWidth * isectHeight];
                inputBand.ReadRaster(readX, readY, isectWidth, isectHeight,
                    buffer, isectWidth, isectHeight, 0, 0);
                outputBand.WriteRaster(writeX, writeY, isectWidth, isectHeight,
                    buffer, isectWidth, isectHeight, 0, 0);
            }

            copiedTiles++;
        }

        outputDataset.FlushCache();

        TerrainLogger.Info(
            $"Direct crop: {copiedTiles} tiles contributed, {skippedTiles} skipped (no overlap)");
    }

    /// <summary>
    ///     Combines multiple GeoTIFF files and returns the import result directly.
    /// </summary>
    /// <param name="inputDirectory">Directory containing GeoTIFF tiles</param>
    /// <param name="targetSize">Optional target size to resize the combined heightmap to (must be power of 2)</param>
    /// <param name="tempDirectory">Directory for temporary files (optional, uses system temp if null)</param>
    /// <returns>Import result with combined heightmap and bounding box</returns>
    public async Task<GeoTiffImportResult> CombineAndImportAsync(
        string inputDirectory,
        int? targetSize = null,
        string? tempDirectory = null,
        int? epsgOverride = null)
    {
        return await CombineAndImportAsync(
            inputDirectory,
            targetSize,
            null, null, null, null,
            tempDirectory,
            epsgOverride);
    }

    /// <summary>
    ///     Combines multiple GeoTIFF files with optional cropping and returns the import result directly.
    ///     The crop is applied to the combined result.
    /// </summary>
    /// <param name="inputDirectory">Directory containing GeoTIFF tiles</param>
    /// <param name="targetSize">Optional target size to resize the combined heightmap to (must be power of 2)</param>
    /// <param name="cropOffsetX">X offset in pixels from the left edge (null = no crop)</param>
    /// <param name="cropOffsetY">Y offset in pixels from the top edge (null = no crop)</param>
    /// <param name="cropWidth">Width of the cropped region in pixels</param>
    /// <param name="cropHeight">Height of the cropped region in pixels</param>
    /// <param name="tempDirectory">Directory for temporary files (optional, uses system temp if null)</param>
    /// <param name="epsgOverride">Optional EPSG code to use instead of the tiles' embedded CRS</param>
    /// <returns>Import result with combined (and optionally cropped) heightmap and bounding box</returns>
    public async Task<GeoTiffImportResult> CombineAndImportAsync(
        string inputDirectory,
        int? targetSize,
        int? cropOffsetX,
        int? cropOffsetY,
        int? cropWidth,
        int? cropHeight,
        string? tempDirectory = null,
        int? epsgOverride = null)
    {
        tempDirectory ??= Path.GetTempPath();
        var combinedPath = Path.Combine(tempDirectory, $"combined_{Guid.NewGuid():N}.tif");

        try
        {
            var overrideProjection = epsgOverride.HasValue
                ? GeoTiffReader.GetProjectionWktFromEpsg(epsgOverride.Value)
                : null;

            // The combined file gets the override projection embedded, so the read-back below
            // resolves the CRS without needing the override again.
            await CombineGeoTiffsAsync(inputDirectory, combinedPath, overrideProjection);

            // Apply cropping to the combined result if specified
            var shouldCrop = cropOffsetX.HasValue && cropOffsetY.HasValue &&
                             cropWidth.HasValue && cropHeight.HasValue &&
                             cropWidth.Value > 0 && cropHeight.Value > 0;

            if (shouldCrop)
            {
                TerrainLogger.Info(
                    $"Applying crop to combined tiles: offset ({cropOffsetX}, {cropOffsetY}), " +
                    $"size {cropWidth}x{cropHeight}");

                return _reader.ReadGeoTiff(
                    combinedPath,
                    targetSize,
                    cropOffsetX,
                    cropOffsetY,
                    cropWidth,
                    cropHeight);
            }

            return _reader.ReadGeoTiff(combinedPath, targetSize);
        }
        finally
        {
            // Clean up temporary file
            try
            {
                if (File.Exists(combinedPath))
                    File.Delete(combinedPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}