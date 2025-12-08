using BeamNgTerrainPoc.Terrain.Logging;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Combines multiple GeoTIFF tiles into a single merged GeoTIFF file.
/// Useful when terrain data spans multiple tiles (e.g., SRTM or ASTER GDEM tiles).
/// </summary>
public class GeoTiffCombiner
{
    private readonly GeoTiffReader _reader = new();

    /// <summary>
    /// Supported GeoTIFF file extensions.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".tif", ".tiff", ".geotiff"];

    /// <summary>
    /// Combines all GeoTIFF files in a directory into a single merged file.
    /// </summary>
    /// <param name="inputDirectory">Directory containing GeoTIFF tiles</param>
    /// <param name="outputPath">Path for the combined output file</param>
    /// <returns>Bounding box of the combined terrain</returns>
    public async Task<GeoBoundingBox> CombineGeoTiffsAsync(string inputDirectory, string outputPath)
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
            File.Copy(inputFiles[0], outputPath, overwrite: true);
            var info = _reader.GetGeoTiffInfo(outputPath);
            return info.BoundingBox;
        }

        // Combine multiple files
        return await Task.Run(() => CombineFilesInternal(inputFiles, outputPath));
    }

    /// <summary>
    /// Finds all GeoTIFF files in a directory.
    /// </summary>
    private List<string> FindGeoTiffFiles(string directory)
    {
        var files = new List<string>();

        foreach (var ext in SupportedExtensions)
        {
            files.AddRange(Directory.GetFiles(directory, $"*{ext}", SearchOption.TopDirectoryOnly));
        }

        return files.OrderBy(f => f).ToList();
    }

    /// <summary>
    /// Internal method to combine multiple GeoTIFF files.
    /// </summary>
    private GeoBoundingBox CombineFilesInternal(List<string> inputFiles, string outputPath)
    {
        GeoTiffReader.InitializeGdal();

        // Delete output if it exists
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // Analyze all input files to determine overall bounds
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        double[] geoTransform = new double[6];
        double pixelSizeX = 0, pixelSizeY = 0;
        int tileWidth = 0, tileHeight = 0;
        string? projection = null;
        DataType dataType = DataType.GDT_Unknown;
        int bandCount = 0;

        // First pass: get bounds from all tiles
        foreach (var file in inputFiles)
        {
            using var dataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (dataset == null)
            {
                TerrainLogger.Warning($"Could not open file, skipping: {file}");
                continue;
            }

            dataset.GetGeoTransform(geoTransform);

            // Store first tile's metadata as reference
            if (pixelSizeX == 0)
            {
                pixelSizeX = Math.Abs(geoTransform[1]);
                pixelSizeY = Math.Abs(geoTransform[5]);
                tileWidth = dataset.RasterXSize;
                tileHeight = dataset.RasterYSize;
                projection = dataset.GetProjection();
                bandCount = dataset.RasterCount;
                dataType = dataset.GetRasterBand(1).DataType;
            }
            else
            {
                // Verify tiles have consistent dimensions
                if (dataset.RasterXSize != tileWidth || dataset.RasterYSize != tileHeight)
                {
                    TerrainLogger.Warning($"Tile size mismatch in {file}. Expected {tileWidth}x{tileHeight}, got {dataset.RasterXSize}x{dataset.RasterYSize}");
                }
            }

            // Calculate tile bounds
            double tileMinX = geoTransform[0];
            double tileMaxY = geoTransform[3];
            double tileMaxX = tileMinX + geoTransform[1] * dataset.RasterXSize;
            double tileMinY = tileMaxY + geoTransform[5] * dataset.RasterYSize;

            // Update overall bounds
            minX = Math.Min(minX, tileMinX);
            minY = Math.Min(minY, tileMinY);
            maxX = Math.Max(maxX, tileMaxX);
            maxY = Math.Max(maxY, tileMaxY);
        }

        TerrainLogger.Info($"Combined extent: X[{minX:F4} - {maxX:F4}], Y[{minY:F4} - {maxY:F4}]");

        // Calculate total output dimensions
        int totalWidth = (int)Math.Round((maxX - minX) / pixelSizeX);
        int totalHeight = (int)Math.Round((maxY - minY) / pixelSizeY);

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
            minX,           // Origin X
            pixelSizeX,     // Pixel width
            0,              // Rotation X
            maxY,           // Origin Y (top)
            0,              // Rotation Y
            -pixelSizeY     // Pixel height (negative)
        ]);

        if (!string.IsNullOrEmpty(projection))
            outputDataset.SetProjection(projection);

        // Second pass: copy data from each tile
        foreach (var file in inputFiles)
        {
            using var inputDataset = Gdal.Open(file, Access.GA_ReadOnly);
            if (inputDataset == null) continue;

            var inputGeoTransform = new double[6];
            inputDataset.GetGeoTransform(inputGeoTransform);

            // Calculate offset in output image
            int xOffset = (int)Math.Round((inputGeoTransform[0] - minX) / pixelSizeX);
            int yOffset = (int)Math.Round((maxY - inputGeoTransform[3]) / pixelSizeY);

            // Clamp offsets
            xOffset = Math.Max(0, Math.Min(xOffset, totalWidth - tileWidth));
            yOffset = Math.Max(0, Math.Min(yOffset, totalHeight - tileHeight));

            TerrainLogger.Info($"Copying tile {Path.GetFileName(file)} to offset ({xOffset}, {yOffset})");

            // Copy each band
            for (int bandIndex = 1; bandIndex <= bandCount; bandIndex++)
            {
                var inputBand = inputDataset.GetRasterBand(bandIndex);
                var outputBand = outputDataset.GetRasterBand(bandIndex);

                var buffer = new double[tileWidth * tileHeight];
                inputBand.ReadRaster(0, 0, tileWidth, tileHeight, buffer, tileWidth, tileHeight, 0, 0);
                outputBand.WriteRaster(xOffset, yOffset, tileWidth, tileHeight, buffer, tileWidth, tileHeight, 0, 0);
            }
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

    /// <summary>
    /// Combines multiple GeoTIFF files and returns the import result directly.
    /// </summary>
    /// <param name="inputDirectory">Directory containing GeoTIFF tiles</param>
    /// <param name="targetSize">Optional target size to resize the combined heightmap to (must be power of 2)</param>
    /// <param name="tempDirectory">Directory for temporary files (optional, uses system temp if null)</param>
    /// <returns>Import result with combined heightmap and bounding box</returns>
    public async Task<GeoTiffImportResult> CombineAndImportAsync(
        string inputDirectory, 
        int? targetSize = null,
        string? tempDirectory = null)
    {
        tempDirectory ??= Path.GetTempPath();
        var combinedPath = Path.Combine(tempDirectory, $"combined_{Guid.NewGuid():N}.tif");

        try
        {
            await CombineGeoTiffsAsync(inputDirectory, combinedPath);
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
