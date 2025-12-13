using BeamNgTerrainPoc.Terrain.Logging;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Reads GeoTIFF elevation data and converts it to a format suitable for BeamNG terrain creation.
/// Handles GDAL initialization, elevation extraction, and conversion to 16-bit heightmap.
/// </summary>
public class GeoTiffReader
{
    private static bool _gdalInitialized;
    private static readonly object _initLock = new();

    /// <summary>
    /// Initializes GDAL library. Called automatically on first use.
    /// Uses MaxRev.Gdal.Core for automatic configuration of GDAL_DATA and PROJ_LIB paths.
    /// </summary>
    public static void InitializeGdal()
    {
        lock (_initLock)
        {
            if (_gdalInitialized) return;

            try
            {
                // MaxRev.Gdal.Core handles all environment variable setup (GDAL_DATA, PROJ_LIB)
                GdalBase.ConfigureAll();
                Gdal.AllRegister();
                _gdalInitialized = true;
                TerrainLogger.Info("GDAL initialized successfully via MaxRev.Gdal.Core");
            }
            catch (Exception ex)
            {
                TerrainLogger.Error($"Failed to initialize GDAL: {ex.Message}");
                throw new InvalidOperationException("GDAL initialization failed. Ensure GDAL native libraries are installed.", ex);
            }
        }
    }

    /// <summary>
    /// Reads a GeoTIFF file and returns the heightmap data with geographic metadata.
    /// </summary>
    /// <param name="geoTiffPath">Path to the GeoTIFF file</param>
    /// <param name="targetSize">Optional target size to resize the heightmap to (must be power of 2). 
    /// If null, the original size is kept.</param>
    /// <returns>Import result containing heightmap image and bounding box</returns>
    public GeoTiffImportResult ReadGeoTiff(string geoTiffPath, int? targetSize = null)
    {
        return ReadGeoTiff(geoTiffPath, targetSize, null, null, null, null);
    }

    /// <summary>
    /// Reads a GeoTIFF file with optional cropping and returns the heightmap data with geographic metadata.
    /// </summary>
    /// <param name="geoTiffPath">Path to the GeoTIFF file</param>
    /// <param name="targetSize">Optional target size to resize the heightmap to (must be power of 2)</param>
    /// <param name="cropOffsetX">X offset in pixels from the left edge (null = no crop)</param>
    /// <param name="cropOffsetY">Y offset in pixels from the top edge (null = no crop)</param>
    /// <param name="cropWidth">Width of the cropped region in pixels</param>
    /// <param name="cropHeight">Height of the cropped region in pixels</param>
    /// <returns>Import result containing heightmap image and bounding box</returns>
    public GeoTiffImportResult ReadGeoTiff(
        string geoTiffPath, 
        int? targetSize,
        int? cropOffsetX,
        int? cropOffsetY,
        int? cropWidth,
        int? cropHeight)
    {
        if (!File.Exists(geoTiffPath))
            throw new FileNotFoundException($"GeoTIFF file not found: {geoTiffPath}");

        InitializeGdal();

        TerrainLogger.Info($"Reading GeoTIFF: {geoTiffPath}");

        using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
        if (dataset == null)
            throw new InvalidOperationException($"Failed to open GeoTIFF: {geoTiffPath}");

        // Check if cropping is requested
        bool shouldCrop = cropOffsetX.HasValue && cropOffsetY.HasValue && 
                          cropWidth.HasValue && cropHeight.HasValue &&
                          cropWidth.Value > 0 && cropHeight.Value > 0;

        if (shouldCrop)
        {
            return ReadFromDatasetCropped(
                dataset, 
                geoTiffPath, 
                targetSize,
                cropOffsetX!.Value, 
                cropOffsetY!.Value, 
                cropWidth!.Value, 
                cropHeight!.Value);
        }

        return ReadFromDataset(dataset, geoTiffPath, targetSize);
    }

    /// <summary>
    /// Reads heightmap data from an already opened GDAL dataset.
    /// </summary>
    private GeoTiffImportResult ReadFromDataset(Dataset dataset, string? sourcePath = null, int? targetSize = null)
    {
        // Get raster dimensions
        int width = dataset.RasterXSize;
        int height = dataset.RasterYSize;

        TerrainLogger.Info($"GeoTIFF dimensions: {width}x{height}");
        
        // Warn if dimensions are not power of 2
        if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
        {
            TerrainLogger.Warning($"GeoTIFF dimensions ({width}x{height}) are not power of 2. " +
                                  "BeamNG requires power-of-2 terrain sizes (256, 512, 1024, 2048, 4096, 8192, 16384).");
            
            if (targetSize.HasValue)
            {
                TerrainLogger.Info($"Will resize to target size: {targetSize.Value}x{targetSize.Value}");
            }
            else
            {
                // Auto-select nearest power of 2
                var suggestedSize = GetNearestPowerOfTwo(Math.Max(width, height));
                TerrainLogger.Info($"Consider setting terrain size to {suggestedSize} for best results.");
            }
        }

        // Get geotransform (geographic metadata)
        var geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);

        // geoTransform:
        // [0] = top-left X (longitude)
        // [1] = pixel width (X resolution)
        // [2] = row rotation (usually 0)
        // [3] = top-left Y (latitude)
        // [4] = column rotation (usually 0)
        // [5] = pixel height (Y resolution, usually negative)

        double pixelSizeX = Math.Abs(geoTransform[1]);
        double pixelSizeY = Math.Abs(geoTransform[5]);

        // Calculate bounding box
        double minX = geoTransform[0]; // West
        double maxY = geoTransform[3]; // North
        double maxX = minX + geoTransform[1] * width; // East
        double minY = maxY + geoTransform[5] * height; // South (geoTransform[5] is negative)

        var boundingBox = new GeoBoundingBox(minX, minY, maxX, maxY);
        TerrainLogger.Info($"Bounding box: {boundingBox}");

        // Get projection
        string? projection = dataset.GetProjection();

        // Check if coordinates need transformation to WGS84
        GeoBoundingBox? wgs84BoundingBox = null;
        if (!string.IsNullOrEmpty(projection))
        {
            if (GeoBoundingBox.IsWgs84Projection(projection))
            {
                TerrainLogger.Info("GeoTIFF is in WGS84 (geographic) coordinates");
                wgs84BoundingBox = boundingBox;
            }
            else
            {
                TerrainLogger.Info("GeoTIFF is in a projected coordinate system, transforming to WGS84...");
                wgs84BoundingBox = GeoBoundingBox.TransformToWgs84(boundingBox, projection);
                
                if (wgs84BoundingBox != null)
                {
                    TerrainLogger.Info($"WGS84 bounding box: {wgs84BoundingBox}");
                }
                else
                {
                    TerrainLogger.Warning("Could not transform coordinates to WGS84. OSM features may not work correctly.");
                }
            }
        }
        else
        {
            TerrainLogger.Warning("GeoTIFF has no projection information. Assuming WGS84 coordinates.");
            wgs84BoundingBox = boundingBox.IsValidWgs84 ? boundingBox : null;
        }

        if (wgs84BoundingBox != null)
        {
            TerrainLogger.Info($"Overpass query bbox: {wgs84BoundingBox.ToOverpassBBox()}");
        }

        // Read elevation data from first band
        var band = dataset.GetRasterBand(1);
        var elevationData = new double[width * height];
        band.ReadRaster(0, 0, width, height, elevationData, width, height, 0, 0);

        // Get nodata value if available
        double nodataValue = double.MinValue;
        int hasNodata = 0;
        band.GetNoDataValue(out nodataValue, out hasNodata);
        
        bool useNodata = hasNodata != 0;
        if (useNodata)
        {
            TerrainLogger.Info($"NoData value: {nodataValue}");
        }

        // Find min/max elevation, excluding nodata values
        double minElevation = double.MaxValue;
        double maxElevation = double.MinValue;

        foreach (var elevation in elevationData)
        {
            // Skip nodata values
            if (useNodata && Math.Abs(elevation - nodataValue) < 0.001)
                continue;
            
            // Skip extreme values that are likely nodata or invalid
            if (elevation < -1000000 || elevation > 1000000)
                continue;
                
            if (elevation < minElevation) minElevation = elevation;
            if (elevation > maxElevation) maxElevation = elevation;
        }

        // Fallback if no valid values found
        if (minElevation == double.MaxValue || maxElevation == double.MinValue)
        {
            TerrainLogger.Warning("No valid elevation data found. Using defaults.");
            minElevation = 0;
            maxElevation = 100;
        }

        TerrainLogger.Info($"Elevation range: {minElevation:F1}m to {maxElevation:F1}m (range: {maxElevation - minElevation:F1}m)");

        // Convert to 16-bit heightmap image
        var heightmapImage = ConvertToHeightmap(elevationData, width, height, minElevation, maxElevation);

        // Resize if target size is specified and different from source
        if (targetSize.HasValue && (heightmapImage.Width != targetSize.Value || heightmapImage.Height != targetSize.Value))
        {
            heightmapImage = ResizeHeightmap(heightmapImage, targetSize.Value);
        }

        return new GeoTiffImportResult
        {
            HeightmapImage = heightmapImage,
            BoundingBox = boundingBox,
            Wgs84BoundingBox = wgs84BoundingBox,
            MinElevation = minElevation,
            MaxElevation = maxElevation,
            PixelSizeX = pixelSizeX,
            PixelSizeY = pixelSizeY,
            Projection = projection,
            SourcePath = sourcePath
        };
    }

    /// <summary>
    /// Reads heightmap data from an already opened GDAL dataset with cropping applied.
    /// The crop is applied BEFORE resizing to target size.
    /// </summary>
    private GeoTiffImportResult ReadFromDatasetCropped(
        Dataset dataset, 
        string? sourcePath,
        int? targetSize,
        int cropOffsetX,
        int cropOffsetY,
        int cropWidth,
        int cropHeight)
    {
        // Get full raster dimensions
        int fullWidth = dataset.RasterXSize;
        int fullHeight = dataset.RasterYSize;

        TerrainLogger.Info($"GeoTIFF dimensions: {fullWidth}x{fullHeight}");
        TerrainLogger.Info($"Cropping to: offset ({cropOffsetX}, {cropOffsetY}), size {cropWidth}x{cropHeight}");

        // Validate crop region
        if (cropOffsetX < 0 || cropOffsetY < 0 ||
            cropOffsetX + cropWidth > fullWidth ||
            cropOffsetY + cropHeight > fullHeight)
        {
            throw new ArgumentException(
                $"Crop region ({cropOffsetX},{cropOffsetY},{cropWidth}x{cropHeight}) exceeds image bounds ({fullWidth}x{fullHeight})");
        }

        // Get geotransform (geographic metadata)
        var geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);

        double pixelSizeX = Math.Abs(geoTransform[1]);
        double pixelSizeY = Math.Abs(geoTransform[5]);

        // Calculate CROPPED bounding box (geographic coordinates for the cropped region)
        // geoTransform[0] = top-left X, geoTransform[3] = top-left Y
        // geoTransform[1] = pixel width, geoTransform[5] = pixel height (negative)
        double croppedMinX = geoTransform[0] + cropOffsetX * geoTransform[1];
        double croppedMaxY = geoTransform[3] + cropOffsetY * geoTransform[5]; // geoTransform[5] is negative
        double croppedMaxX = croppedMinX + cropWidth * geoTransform[1];
        double croppedMinY = croppedMaxY + cropHeight * geoTransform[5];

        var boundingBox = new GeoBoundingBox(croppedMinX, croppedMinY, croppedMaxX, croppedMaxY);
        TerrainLogger.Info($"Cropped bounding box: {boundingBox}");

        // Get projection
        string? projection = dataset.GetProjection();

        // Check if coordinates need transformation to WGS84
        GeoBoundingBox? wgs84BoundingBox = null;
        if (!string.IsNullOrEmpty(projection))
        {
            if (GeoBoundingBox.IsWgs84Projection(projection))
            {
                TerrainLogger.Info("GeoTIFF is in WGS84 (geographic) coordinates");
                wgs84BoundingBox = boundingBox;
            }
            else
            {
                TerrainLogger.Info("GeoTIFF is in a projected coordinate system, transforming to WGS84...");
                wgs84BoundingBox = GeoBoundingBox.TransformToWgs84(boundingBox, projection);
                
                if (wgs84BoundingBox != null)
                {
                    TerrainLogger.Info($"WGS84 bounding box: {wgs84BoundingBox}");
                }
                else
                {
                    TerrainLogger.Warning("Could not transform coordinates to WGS84. OSM features may not work correctly.");
                }
            }
        }
        else
        {
            TerrainLogger.Warning("GeoTIFF has no projection information. Assuming WGS84 coordinates.");
            wgs84BoundingBox = boundingBox.IsValidWgs84 ? boundingBox : null;
        }

        if (wgs84BoundingBox != null)
        {
            TerrainLogger.Info($"Overpass query bbox: {wgs84BoundingBox.ToOverpassBBox()}");
        }

        // Read ONLY the cropped region's elevation data
        var band = dataset.GetRasterBand(1);
        var elevationData = new double[cropWidth * cropHeight];
        band.ReadRaster(cropOffsetX, cropOffsetY, cropWidth, cropHeight, elevationData, cropWidth, cropHeight, 0, 0);

        // Get nodata value if available
        double nodataValue = double.MinValue;
        int hasNodata = 0;
        band.GetNoDataValue(out nodataValue, out hasNodata);
        
        bool useNodata = hasNodata != 0;
        if (useNodata)
        {
            TerrainLogger.Info($"NoData value: {nodataValue}");
        }

        // Find min/max elevation in the cropped region, excluding nodata values
        double minElevation = double.MaxValue;
        double maxElevation = double.MinValue;

        foreach (var elevation in elevationData)
        {
            // Skip nodata values
            if (useNodata && Math.Abs(elevation - nodataValue) < 0.001)
                continue;
            
            // Skip extreme values that are likely nodata or invalid
            if (elevation < -1000000 || elevation > 1000000)
                continue;
                
            if (elevation < minElevation) minElevation = elevation;
            if (elevation > maxElevation) maxElevation = elevation;
        }

        // Fallback if no valid values found
        if (minElevation == double.MaxValue || maxElevation == double.MinValue)
        {
            TerrainLogger.Warning("No valid elevation data found in cropped region. Using defaults.");
            minElevation = 0;
            maxElevation = 100;
        }

        TerrainLogger.Info($"Cropped elevation range: {minElevation:F1}m to {maxElevation:F1}m (range: {maxElevation - minElevation:F1}m)");

        // Convert to 16-bit heightmap image
        var heightmapImage = ConvertToHeightmap(elevationData, cropWidth, cropHeight, minElevation, maxElevation);

        // Resize if target size is specified and different from cropped source
        if (targetSize.HasValue && (heightmapImage.Width != targetSize.Value || heightmapImage.Height != targetSize.Value))
        {
            heightmapImage = ResizeHeightmap(heightmapImage, targetSize.Value);
        }

        return new GeoTiffImportResult
        {
            HeightmapImage = heightmapImage,
            BoundingBox = boundingBox,
            Wgs84BoundingBox = wgs84BoundingBox,
            MinElevation = minElevation,
            MaxElevation = maxElevation,
            PixelSizeX = pixelSizeX,
            PixelSizeY = pixelSizeY,
            Projection = projection,
            SourcePath = sourcePath
        };
    }

    /// <summary>
    /// Converts raw elevation data to a 16-bit grayscale heightmap image.
    /// The output image is in standard image format (top-down), matching what HeightmapProcessor expects.
    /// HeightmapProcessor will handle the Y-flip to convert to BeamNG's bottom-up format.
    /// </summary>
    /// <param name="elevationData">Raw elevation values in row-major order (top-left to bottom-right, as read by GDAL)</param>
    /// <param name="width">Image width in pixels</param>
    /// <param name="height">Image height in pixels</param>
    /// <param name="minElevation">Minimum elevation value</param>
    /// <param name="maxElevation">Maximum elevation value</param>
    /// <param name="nodataValue">NoData value to treat as minElevation (optional)</param>
    /// <returns>16-bit grayscale heightmap image in standard top-down format</returns>
    private Image<L16> ConvertToHeightmap(
        double[] elevationData,
        int width,
        int height,
        double minElevation,
        double maxElevation,
        double? nodataValue = null)
    {
        var heightmapImage = new Image<L16>(width, height);
        double elevationRange = maxElevation - minElevation;

        // Handle case where all elevations are the same
        if (elevationRange < 0.001)
        {
            TerrainLogger.Warning("Elevation range is nearly zero. Heightmap will be flat.");
            elevationRange = 1.0;
        }

        // GDAL reads GeoTIFF data in row-major order: top-left to bottom-right
        // This is the same as standard image format (ImageSharp)
        // HeightmapProcessor.ProcessHeightmap() will flip Y to convert to BeamNG's bottom-up format
        // So we write the data directly without any Y-flip here

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Direct mapping: GDAL row-major order matches ImageSharp's top-down format
                int sourceIndex = y * width + x;
                double elevation = elevationData[sourceIndex];

                // Handle nodata values - treat as minimum elevation (will appear as "holes" or low areas)
                if (nodataValue.HasValue && Math.Abs(elevation - nodataValue.Value) < 0.001)
                {
                    elevation = minElevation;
                }
                
                // Clamp extreme values
                if (elevation < -1000000 || elevation > 1000000)
                {
                    elevation = minElevation;
                }

                // Normalize to 0-1 range
                double normalized = (elevation - minElevation) / elevationRange;

                // Convert to 16-bit value
                ushort pixelValue = (ushort)Math.Clamp(normalized * 65535.0, 0, 65535);

                heightmapImage[x, y] = new L16(pixelValue);
            }
        }

        return heightmapImage;
    }

    /// <summary>
    /// Gets basic information about a GeoTIFF file without fully loading the elevation data.
    /// </summary>
    /// <param name="geoTiffPath">Path to the GeoTIFF file</param>
    /// <returns>Tuple of (width, height, bounding box in native CRS)</returns>
    public (int Width, int Height, GeoBoundingBox BoundingBox) GetGeoTiffInfo(string geoTiffPath)
    {
        var extendedInfo = GetGeoTiffInfoExtended(geoTiffPath);
        return (extendedInfo.Width, extendedInfo.Height, extendedInfo.BoundingBox);
    }

    /// <summary>
    /// Gets extended information about a GeoTIFF file including WGS84 bounding box.
    /// </summary>
    /// <param name="geoTiffPath">Path to the GeoTIFF file</param>
    /// <returns>Extended info including both native and WGS84 bounding boxes</returns>
    public GeoTiffInfoResult GetGeoTiffInfoExtended(string geoTiffPath)
    {
        if (!File.Exists(geoTiffPath))
            throw new FileNotFoundException($"GeoTIFF file not found: {geoTiffPath}");

        InitializeGdal();

        using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
        if (dataset == null)
            throw new InvalidOperationException($"Failed to open GeoTIFF: {geoTiffPath}");

        int width = dataset.RasterXSize;
        int height = dataset.RasterYSize;

        var geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);

        double minX = geoTransform[0];
        double maxY = geoTransform[3];
        double maxX = minX + geoTransform[1] * width;
        double minY = maxY + geoTransform[5] * height;

        var boundingBox = new GeoBoundingBox(minX, minY, maxX, maxY);
        
        // Get projection and transform to WGS84 if needed
        string? projection = dataset.GetProjection();
        GeoBoundingBox? wgs84BoundingBox = null;
        
        if (!string.IsNullOrEmpty(projection))
        {
            if (GeoBoundingBox.IsWgs84Projection(projection))
            {
                TerrainLogger.Detail("GeoTIFF is in WGS84 (geographic) coordinates");
                wgs84BoundingBox = boundingBox;
            }
            else
            {
                TerrainLogger.Detail("GeoTIFF is in a projected coordinate system, transforming to WGS84...");
                wgs84BoundingBox = GeoBoundingBox.TransformToWgs84(boundingBox, projection);
                
                if (wgs84BoundingBox != null)
                {
                    TerrainLogger.Detail($"WGS84 bounding box: {wgs84BoundingBox}");
                }
                else
                {
                    TerrainLogger.DetailWarning("Could not transform coordinates to WGS84. OSM features may not work correctly.");
                }
            }
        }
        else
        {
            TerrainLogger.DetailWarning("GeoTIFF has no projection information. Assuming WGS84 if coordinates are valid.");
            wgs84BoundingBox = boundingBox.IsValidWgs84 ? boundingBox : null;
        }

        // Try to get elevation statistics efficiently
        double? minElevation = null;
        double? maxElevation = null;
        try
        {
            var band = dataset.GetRasterBand(1);
            var minMax = new double[2];
            band.ComputeRasterMinMax(minMax, 0); // 0 = exact computation
            minElevation = minMax[0];
            maxElevation = minMax[1];
            TerrainLogger.Detail($"Elevation range: {minElevation:F1}m to {maxElevation:F1}m (range: {maxElevation - minElevation:F1}m)");
        }
        catch (Exception ex)
        {
            TerrainLogger.DetailWarning($"Could not compute elevation statistics: {ex.Message}");
        }

        return new GeoTiffInfoResult
        {
            Width = width,
            Height = height,
            BoundingBox = boundingBox,
            Wgs84BoundingBox = wgs84BoundingBox,
            Projection = projection,
            GeoTransform = geoTransform,
            MinElevation = minElevation,
            MaxElevation = maxElevation
        };
    }

    /// <summary>
    /// Gets elevation statistics for a specific cropped region of the GeoTIFF.
    /// This is used when cropping an oversized GeoTIFF to get accurate min/max for the cropped area.
    /// </summary>
    /// <param name="geoTiffPath">Path to the GeoTIFF file</param>
    /// <param name="offsetX">X offset in pixels from the left edge</param>
    /// <param name="offsetY">Y offset in pixels from the top edge</param>
    /// <param name="cropWidth">Width of the cropped region in pixels</param>
    /// <param name="cropHeight">Height of the cropped region in pixels</param>
    /// <returns>Tuple of (minElevation, maxElevation) for the cropped region</returns>
    public (double MinElevation, double MaxElevation) GetCroppedElevationRange(
        string geoTiffPath,
        int offsetX, int offsetY,
        int cropWidth, int cropHeight)
    {
        if (!File.Exists(geoTiffPath))
            throw new FileNotFoundException($"GeoTIFF file not found: {geoTiffPath}");

        InitializeGdal();

        using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
        if (dataset == null)
            throw new InvalidOperationException($"Failed to open GeoTIFF: {geoTiffPath}");

        int fullWidth = dataset.RasterXSize;
        int fullHeight = dataset.RasterYSize;

        // Validate crop region
        if (offsetX < 0 || offsetY < 0 ||
            offsetX + cropWidth > fullWidth ||
            offsetY + cropHeight > fullHeight)
        {
            throw new ArgumentException(
                $"Crop region ({offsetX},{offsetY},{cropWidth}x{cropHeight}) exceeds image bounds ({fullWidth}x{fullHeight})");
        }

        TerrainLogger.Info($"Reading elevation data for cropped region: offset ({offsetX},{offsetY}), size {cropWidth}x{cropHeight}");

        // Read only the cropped region's elevation data
        var band = dataset.GetRasterBand(1);
        var elevationData = new double[cropWidth * cropHeight];
        band.ReadRaster(offsetX, offsetY, cropWidth, cropHeight, elevationData, cropWidth, cropHeight, 0, 0);

        // Get nodata value if available
        double nodataValue = double.MinValue;
        int hasNodata = 0;
        band.GetNoDataValue(out nodataValue, out hasNodata);
        
        bool useNodata = hasNodata != 0;
        if (useNodata)
        {
            TerrainLogger.Info($"NoData value: {nodataValue}");
        }

        // Find min/max elevation in the cropped region, excluding nodata values
        double minElevation = double.MaxValue;
        double maxElevation = double.MinValue;
        int validCount = 0;

        foreach (var elevation in elevationData)
        {
            // Skip nodata values and obviously invalid values
            if (useNodata && Math.Abs(elevation - nodataValue) < 0.001)
                continue;
            
            // Skip extreme values that are likely nodata or invalid
            if (elevation < -1000000 || elevation > 1000000)
                continue;
                
            if (elevation < minElevation) minElevation = elevation;
            if (elevation > maxElevation) maxElevation = elevation;
            validCount++;
        }

        // Fallback if no valid values found
        if (validCount == 0 || minElevation == double.MaxValue || maxElevation == double.MinValue)
        {
            TerrainLogger.Warning("No valid elevation data found in cropped region. Using defaults.");
            minElevation = 0;
            maxElevation = 100;
        }

        TerrainLogger.Info($"Cropped region elevation range: {minElevation:F1}m to {maxElevation:F1}m (range: {maxElevation - minElevation:F1}m, valid pixels: {validCount})");

        return (minElevation, maxElevation);
    }

    /// <summary>
    /// Resizes a heightmap image to the target size using bicubic interpolation.
    /// Bicubic is preferred for elevation data as it produces smooth results without sharp artifacts.
    /// </summary>
    private Image<L16> ResizeHeightmap(Image<L16> source, int targetSize)
    {
        TerrainLogger.Info($"Resizing heightmap from {source.Width}x{source.Height} to {targetSize}x{targetSize}");

        // Clone and resize in place
        var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetSize, targetSize),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic // Bicubic is good for elevation data - smooth without ringing
        }));

        // Dispose the original
        source.Dispose();

        TerrainLogger.Info($"Heightmap resized to {resized.Width}x{resized.Height}");
        return resized;
    }

    /// <summary>
    /// Checks if a number is a power of 2.
    /// </summary>
    private static bool IsPowerOfTwo(int x)
    {
        return x > 0 && (x & (x - 1)) == 0;
    }

    /// <summary>
    /// Gets the nearest power of 2 that is >= the input value.
    /// </summary>
    private static int GetNearestPowerOfTwo(int value)
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
    /// Gets combined information about multiple GeoTIFF tiles in a directory.
    /// Calculates the combined dimensions, bounding box, and GeoTransform for all tiles.
    /// </summary>
    /// <param name="directoryPath">Directory containing GeoTIFF tiles</param>
    /// <returns>Combined info with total dimensions, bounding box, validation result, etc.</returns>
    public GeoTiffDirectoryInfoResult GetGeoTiffDirectoryInfoExtended(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        InitializeGdal();

        // Find all GeoTIFF files
        var tiffFiles = Directory.GetFiles(directoryPath, "*.tif", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(directoryPath, "*.tiff", SearchOption.TopDirectoryOnly))
            .Concat(Directory.GetFiles(directoryPath, "*.geotiff", SearchOption.TopDirectoryOnly))
            .Distinct()
            .OrderBy(f => f)
            .ToList();

        if (tiffFiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"No GeoTIFF files found in '{directoryPath}'. " +
                "Supported extensions: .tif, .tiff, .geotiff");
        }

        TerrainLogger.Info($"Analyzing {tiffFiles.Count} GeoTIFF tile(s) in directory...");

        var tiles = new List<GeoTiffTileInfo>();
        var warnings = new List<string>();

        // Track combined bounds and metadata
        double nativeMinX = double.MaxValue, nativeMinY = double.MaxValue;
        double nativeMaxX = double.MinValue, nativeMaxY = double.MinValue;
        double wgs84MinLon = double.MaxValue, wgs84MinLat = double.MaxValue;
        double wgs84MaxLon = double.MinValue, wgs84MaxLat = double.MinValue;
        bool hasValidWgs84 = false;

        double? globalMinElevation = null;
        double? globalMaxElevation = null;

        // Reference values from first tile for consistency checking
        string? firstProjection = null;
        string? firstProjectionName = null;
        double firstPixelSizeX = 0;
        double firstPixelSizeY = 0;
        GeoTiffValidationResult? validationResult = null;

        // Enable suppressed logging for bulk operations (>10 tiles)
        var previousSuppressState = TerrainLogger.SuppressDetailedLogging;
        TerrainLogger.SuppressDetailedLogging = tiffFiles.Count > 10;

        try
        {
            var processedCount = 0;
            
            foreach (var tiffFile in tiffFiles)
            {
                try
                {
                    var tileInfo = GetGeoTiffInfoExtendedQuiet(tiffFile);

                    // Validate first tile and capture reference values
                    if (firstProjection == null)
                    {
                        validationResult = ValidateGeoTiff(tiffFile);
                        firstProjection = tileInfo.Projection;
                        firstProjectionName = tileInfo.ProjectionName;
                        
                        if (tileInfo.GeoTransform != null)
                        {
                            firstPixelSizeX = Math.Abs(tileInfo.GeoTransform[1]);
                            firstPixelSizeY = Math.Abs(tileInfo.GeoTransform[5]);
                        }
                    }
                    else
                    {
                        // Check for consistency with first tile
                        if (tileInfo.GeoTransform != null)
                        {
                            var pixelSizeX = Math.Abs(tileInfo.GeoTransform[1]);
                            var pixelSizeY = Math.Abs(tileInfo.GeoTransform[5]);
                            
                            // Allow small tolerance for floating point differences
                            if (Math.Abs(pixelSizeX - firstPixelSizeX) > 0.001 ||
                                Math.Abs(pixelSizeY - firstPixelSizeY) > 0.001)
                            {
                                warnings.Add($"Tile '{Path.GetFileName(tiffFile)}' has different pixel size " +
                                           $"({pixelSizeX:F4}x{pixelSizeY:F4}) than first tile ({firstPixelSizeX:F4}x{firstPixelSizeY:F4})");
                            }
                        }
                    }

                    // Track native bounding box
                    nativeMinX = Math.Min(nativeMinX, tileInfo.BoundingBox.MinLongitude);
                    nativeMinY = Math.Min(nativeMinY, tileInfo.BoundingBox.MinLatitude);
                    nativeMaxX = Math.Max(nativeMaxX, tileInfo.BoundingBox.MaxLongitude);
                    nativeMaxY = Math.Max(nativeMaxY, tileInfo.BoundingBox.MaxLatitude);

                    // Track WGS84 bounding box
                    if (tileInfo.Wgs84BoundingBox != null)
                    {
                        wgs84MinLon = Math.Min(wgs84MinLon, tileInfo.Wgs84BoundingBox.MinLongitude);
                        wgs84MinLat = Math.Min(wgs84MinLat, tileInfo.Wgs84BoundingBox.MinLatitude);
                        wgs84MaxLon = Math.Max(wgs84MaxLon, tileInfo.Wgs84BoundingBox.MaxLongitude);
                        wgs84MaxLat = Math.Max(wgs84MaxLat, tileInfo.Wgs84BoundingBox.MaxLatitude);
                        hasValidWgs84 = true;
                    }

                    // Track elevation range
                    if (tileInfo.MinElevation.HasValue)
                    {
                        globalMinElevation = globalMinElevation.HasValue
                            ? Math.Min(globalMinElevation.Value, tileInfo.MinElevation.Value)
                            : tileInfo.MinElevation.Value;
                    }
                    if (tileInfo.MaxElevation.HasValue)
                    {
                        globalMaxElevation = globalMaxElevation.HasValue
                            ? Math.Max(globalMaxElevation.Value, tileInfo.MaxElevation.Value)
                            : tileInfo.MaxElevation.Value;
                    }

                    tiles.Add(new GeoTiffTileInfo
                    {
                        FilePath = tiffFile,
                        Width = tileInfo.Width,
                        Height = tileInfo.Height,
                        BoundingBox = tileInfo.BoundingBox,
                        Wgs84BoundingBox = tileInfo.Wgs84BoundingBox,
                        MinElevation = tileInfo.MinElevation,
                        MaxElevation = tileInfo.MaxElevation
                    });
                    
                    processedCount++;
                    
                    // Report progress every 100 tiles or at completion
                    if (processedCount % 100 == 0 || processedCount == tiffFiles.Count)
                    {
                        TerrainLogger.Info($"Analyzed {processedCount}/{tiffFiles.Count} tiles...");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not read tile '{Path.GetFileName(tiffFile)}': {ex.Message}");
                }
            }

            if (tiles.Count == 0)
            {
                throw new InvalidOperationException("No valid GeoTIFF tiles could be read from the directory.");
            }

            // Calculate combined dimensions from total extent / pixel size
            int combinedWidth = firstPixelSizeX > 0
                ? (int)Math.Round((nativeMaxX - nativeMinX) / firstPixelSizeX)
                : tiles.Sum(t => t.Width);
            int combinedHeight = firstPixelSizeY > 0
                ? (int)Math.Round((nativeMaxY - nativeMinY) / firstPixelSizeY)
                : tiles.Sum(t => t.Height);

            // Create combined GeoTransform
            double[]? combinedGeoTransform = null;
            if (firstPixelSizeX > 0 && firstPixelSizeY > 0)
            {
                combinedGeoTransform = new double[6]
                {
                    nativeMinX,           // Origin X (left edge)
                    firstPixelSizeX,      // Pixel width
                    0,                    // Rotation X
                    nativeMaxY,           // Origin Y (top edge)
                    0,                    // Rotation Y
                    -firstPixelSizeY      // Pixel height (negative for top-down)
                };
            }

            // Build native and WGS84 bounding boxes
            var nativeBoundingBox = new GeoBoundingBox(nativeMinX, nativeMinY, nativeMaxX, nativeMaxY);
            
            GeoBoundingBox? wgs84BoundingBox = null;
            if (hasValidWgs84)
            {
                wgs84BoundingBox = new GeoBoundingBox(wgs84MinLon, wgs84MinLat, wgs84MaxLon, wgs84MaxLat);
            }

            // Determine OSM availability
            bool canFetchOsm = wgs84BoundingBox?.IsValidWgs84 == true;
            string? osmBlockedReason = null;
            if (!canFetchOsm)
            {
                osmBlockedReason = wgs84BoundingBox == null
                    ? "Could not determine WGS84 coordinates - coordinate transformation failed."
                    : "WGS84 coordinates are invalid (outside -90/90 lat, -180/180 lon range).";
            }

            TerrainLogger.Info($"Combined tile dimensions: {combinedWidth}x{combinedHeight}");
            TerrainLogger.Info($"Combined native extent: X[{nativeMinX:F2} - {nativeMaxX:F2}], Y[{nativeMinY:F2} - {nativeMaxY:F2}]");
            if (wgs84BoundingBox != null)
            {
                TerrainLogger.Info($"Combined WGS84 bbox: {wgs84BoundingBox}");
            }
            if (globalMinElevation.HasValue && globalMaxElevation.HasValue)
            {
                TerrainLogger.Info($"Combined elevation range: {globalMinElevation:F1}m to {globalMaxElevation:F1}m");
            }
            
            // Log warnings summary instead of all warnings
            if (warnings.Count > 0)
            {
                if (warnings.Count <= 5)
                {
                    foreach (var warning in warnings)
                    {
                        TerrainLogger.Warning(warning);
                    }
                }
                else
                {
                    TerrainLogger.Warning($"{warnings.Count} warnings encountered during tile analysis. See logs for details.");
                    foreach (var warning in warnings)
                    {
                        TerrainLogger.Detail(warning);
                    }
                }
            }

            return new GeoTiffDirectoryInfoResult
            {
                TileCount = tiles.Count,
                CombinedWidth = combinedWidth,
                CombinedHeight = combinedHeight,
                NativeBoundingBox = nativeBoundingBox,
                Wgs84BoundingBox = wgs84BoundingBox,
                CombinedGeoTransform = combinedGeoTransform,
                Projection = firstProjection,
                ProjectionName = firstProjectionName,
                MinElevation = globalMinElevation,
                MaxElevation = globalMaxElevation,
                Tiles = tiles,
                ValidationResult = validationResult,
                CanFetchOsmData = canFetchOsm,
                OsmBlockedReason = osmBlockedReason,
                Warnings = warnings
            };
        }
        finally
        {
            // Restore previous suppression state
            TerrainLogger.SuppressDetailedLogging = previousSuppressState;
        }
    }

    /// <summary>
    /// Gets extended information about a GeoTIFF file without logging to UI.
    /// Used internally for bulk tile analysis to avoid UI spam.
    /// </summary>
    private GeoTiffInfoResult GetGeoTiffInfoExtendedQuiet(string geoTiffPath)
    {
        if (!File.Exists(geoTiffPath))
            throw new FileNotFoundException($"GeoTIFF file not found: {geoTiffPath}");

        InitializeGdal();

        using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
        if (dataset == null)
            throw new InvalidOperationException($"Failed to open GeoTIFF: {geoTiffPath}");

        int width = dataset.RasterXSize;
        int height = dataset.RasterYSize;

        var geoTransform = new double[6];
        dataset.GetGeoTransform(geoTransform);

        double minX = geoTransform[0];
        double maxY = geoTransform[3];
        double maxX = minX + geoTransform[1] * width;
        double minY = maxY + geoTransform[5] * height;

        var boundingBox = new GeoBoundingBox(minX, minY, maxX, maxY);
        
        // Get projection and transform to WGS84 if needed
        string? projection = dataset.GetProjection();
        GeoBoundingBox? wgs84BoundingBox = null;
        
        if (!string.IsNullOrEmpty(projection))
        {
            if (GeoBoundingBox.IsWgs84Projection(projection))
            {
                wgs84BoundingBox = boundingBox;
            }
            else
            {
                wgs84BoundingBox = GeoBoundingBox.TransformToWgs84(boundingBox, projection);
            }
        }
        else
        {
            wgs84BoundingBox = boundingBox.IsValidWgs84 ? boundingBox : null;
        }

        // Try to get elevation statistics efficiently
        double? minElevation = null;
        double? maxElevation = null;
        try
        {
            var band = dataset.GetRasterBand(1);
            var minMax = new double[2];
            band.ComputeRasterMinMax(minMax, 0);
            minElevation = minMax[0];
            maxElevation = minMax[1];
        }
        catch
        {
            // Silently ignore - elevation stats are optional
        }

        return new GeoTiffInfoResult
        {
            Width = width,
            Height = height,
            BoundingBox = boundingBox,
            Wgs84BoundingBox = wgs84BoundingBox,
            Projection = projection,
            GeoTransform = geoTransform,
            MinElevation = minElevation,
            MaxElevation = maxElevation
        };
    }

    /// <summary>
    /// Validates a GeoTIFF file for terrain generation and OSM integration.
    /// This performs comprehensive checks and returns detailed diagnostic information.
    /// </summary>
    /// <param name="geoTiffPath">Path to the GeoTIFF file</param>
    /// <returns>Validation result with errors, warnings, and diagnostic info</returns>
    public GeoTiffValidationResult ValidateGeoTiff(string geoTiffPath)
    {
        var diagnostics = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();

        try
        {
            if (!File.Exists(geoTiffPath))
            {
                return GeoTiffValidationResult.Failure($"GeoTIFF file not found: {geoTiffPath}");
            }

            InitializeGdal();
            diagnostics.Add($"File: {Path.GetFileName(geoTiffPath)}");

            using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
            if (dataset == null)
            {
                return GeoTiffValidationResult.Failure($"Failed to open GeoTIFF: {geoTiffPath}");
            }

            // Get dimensions
            int width = dataset.RasterXSize;
            int height = dataset.RasterYSize;
            diagnostics.Add($"Dimensions: {width} x {height} pixels");

            // Get geotransform
            var geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);

            double pixelSizeX = Math.Abs(geoTransform[1]);
            double pixelSizeY = Math.Abs(geoTransform[5]);
            diagnostics.Add($"Pixel size: {pixelSizeX:F6} x {pixelSizeY:F6} (native units)");

            // Calculate bounding box in native CRS
            double minX = geoTransform[0];
            double maxY = geoTransform[3];
            double maxX = minX + geoTransform[1] * width;
            double minY = maxY + geoTransform[5] * height;

            var nativeBbox = new GeoBoundingBox(minX, minY, maxX, maxY);
            diagnostics.Add($"Native bounding box: MinX={minX:F2}, MinY={minY:F2}, MaxX={maxX:F2}, MaxY={maxY:F2}");

            // Get and analyze projection
            string? projection = dataset.GetProjection();
            string projectionName = "Unknown";
            bool isProjectedCrs = false;
            GeoBoundingBox? wgs84Bbox = null;

            if (string.IsNullOrEmpty(projection))
            {
                warnings.Add("GeoTIFF has NO projection/CRS information embedded.");
                diagnostics.Add("Projection: NONE (missing)");
                
                // Check if native coordinates look like valid WGS84
                if (nativeBbox.IsValidWgs84)
                {
                    diagnostics.Add("Native coordinates appear to be valid WGS84 (lat/lon in degrees)");
                    wgs84Bbox = nativeBbox;
                }
                else
                {
                    errors.Add("Cannot determine coordinate system - coordinates don't look like lat/lon degrees.");
                    diagnostics.Add($"Coordinate range check: Lat should be -90 to 90, Lon should be -180 to 180");
                    diagnostics.Add($"Actual: MinLat={minY:F2}, MaxLat={maxY:F2}, MinLon={minX:F2}, MaxLon={maxX:F2}");
                }
            }
            else
            {
                diagnostics.Add($"Projection WKT length: {projection.Length} characters");

                // Parse projection details
                try
                {
                    var srs = new OSGeo.OSR.SpatialReference(null);
                    var wktCopy = projection;
                    if (srs.ImportFromWkt(ref wktCopy) == 0)
                    {
                        // Get projection name
                        projectionName = srs.GetAttrValue("PROJCS", 0) ?? 
                                        srs.GetAttrValue("GEOGCS", 0) ?? 
                                        "Unknown";
                        
                        // Check if projected or geographic
                        isProjectedCrs = srs.IsProjected() == 1;
                        var isGeographic = srs.IsGeographic() == 1;
                        
                        diagnostics.Add($"Projection name: {projectionName}");
                        diagnostics.Add($"CRS Type: {(isProjectedCrs ? "PROJECTED (coordinates in meters/feet)" : isGeographic ? "GEOGRAPHIC (coordinates in degrees)" : "UNKNOWN")}");
                        
                        // Get authority code
                        var authCode = srs.GetAuthorityCode("PROJCS") ?? srs.GetAuthorityCode("GEOGCS");
                        var authName = srs.GetAuthorityName("PROJCS") ?? srs.GetAuthorityName("GEOGCS");
                        if (!string.IsNullOrEmpty(authCode))
                        {
                            diagnostics.Add($"Authority: {authName}:{authCode}");
                        }

                        // Validate the coordinates make sense for the CRS type
                        if (isProjectedCrs)
                        {
                            // For projected CRS, native coordinates should be large numbers (meters)
                            double extentX = Math.Abs(maxX - minX);
                            double extentY = Math.Abs(maxY - minY);
                            
                            diagnostics.Add($"Native extent: {extentX:F2} x {extentY:F2} (should be in meters)");
                            
                            // Sanity check: typical terrain is 1km to 100km
                            if (extentX < 100 || extentY < 100)
                            {
                                warnings.Add($"Terrain extent ({extentX:F0}m x {extentY:F0}m) seems very small for a projected CRS.");
                            }
                            else if (extentX > 1000000 || extentY > 1000000)
                            {
                                warnings.Add($"Terrain extent ({extentX:F0}m x {extentY:F0}m) seems very large.");
                            }

                            // Transform to WGS84
                            diagnostics.Add("Attempting coordinate transformation to WGS84...");
                            wgs84Bbox = GeoBoundingBox.TransformToWgs84(nativeBbox, projection);
                            
                            if (wgs84Bbox != null)
                            {
                                diagnostics.Add($"WGS84 bounding box: Lat {wgs84Bbox.MinLatitude:F6} to {wgs84Bbox.MaxLatitude:F6}, Lon {wgs84Bbox.MinLongitude:F6} to {wgs84Bbox.MaxLongitude:F6}");
                                
                                if (!wgs84Bbox.IsValidWgs84)
                                {
                                    errors.Add("Transformed coordinates are outside valid WGS84 range!");
                                    diagnostics.Add($"Valid range: Lat -90 to 90, Lon -180 to 180");
                                    wgs84Bbox = null;
                                }
                            }
                            else
                            {
                                errors.Add("Coordinate transformation to WGS84 FAILED.");
                                diagnostics.Add("This usually means the projection is not properly defined or GDAL cannot handle it.");
                            }
                        }
                        else if (isGeographic)
                        {
                            // For geographic CRS, coordinates should be in degrees
                            if (nativeBbox.IsValidWgs84)
                            {
                                wgs84Bbox = nativeBbox;
                                diagnostics.Add("Geographic CRS with valid coordinates - no transformation needed.");
                            }
                            else
                            {
                                // This is the suspicious case: claims to be geographic but coordinates are wrong
                                errors.Add("GeoTIFF claims to be in geographic coordinates (degrees) but values are out of range!");
                                diagnostics.Add($"Expected: Lat -90 to 90, Lon -180 to 180");
                                diagnostics.Add($"Actual: MinLat={minY:F2}, MaxLat={maxY:F2}, MinLon={minX:F2}, MaxLon={maxX:F2}");
                                diagnostics.Add("This GeoTIFF has INCORRECT or CORRUPTED projection metadata.");
                                diagnostics.Add("The file may actually be in a projected CRS (like UTM) with wrong metadata.");
                            }
                        }
                    }
                    else
                    {
                        warnings.Add("Could not parse projection WKT.");
                        diagnostics.Add("GDAL failed to import the projection WKT string.");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Error analyzing projection: {ex.Message}");
                    diagnostics.Add($"Exception: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Final validation
            bool isValid = errors.Count == 0;
            bool canFetchOsm = wgs84Bbox?.IsValidWgs84 == true;

            string? osmBlockedReason = null;
            if (!canFetchOsm)
            {
                if (wgs84Bbox == null)
                {
                    osmBlockedReason = "Could not determine WGS84 coordinates - coordinate transformation failed.";
                }
                else
                {
                    osmBlockedReason = "WGS84 coordinates are invalid (outside -90/90 lat, -180/180 lon range).";
                }
            }

            // Log all diagnostics to the error log
            TerrainLogger.Info("=== GeoTIFF Validation Report ===");
            foreach (var diag in diagnostics)
            {
                TerrainLogger.Info($"  {diag}");
            }
            foreach (var warn in warnings)
            {
                TerrainLogger.Warning($"  WARNING: {warn}");
            }
            foreach (var err in errors)
            {
                TerrainLogger.Error($"  ERROR: {err}");
            }
            if (osmBlockedReason != null)
            {
                TerrainLogger.Warning($"  OSM DATA BLOCKED: {osmBlockedReason}");
            }
            TerrainLogger.Info("=================================");

            return new GeoTiffValidationResult
            {
                IsValid = isValid,
                CanFetchOsmData = canFetchOsm,
                Errors = errors,
                Warnings = warnings,
                DiagnosticInfo = diagnostics,
                OsmBlockedReason = osmBlockedReason,
                NativeBoundingBox = nativeBbox,
                Wgs84BoundingBox = wgs84Bbox,
                ProjectionName = projectionName,
                IsProjectedCrs = isProjectedCrs
            };
        }
        catch (Exception ex)
        {
            TerrainLogger.Error($"GeoTIFF validation failed: {ex.Message}");
            TerrainLogger.Error($"Stack trace: {ex.StackTrace}");
            
            return new GeoTiffValidationResult
            {
                IsValid = false,
                CanFetchOsmData = false,
                Errors = [$"Validation failed: {ex.Message}"],
                DiagnosticInfo = diagnostics,
                OsmBlockedReason = $"Validation error: {ex.Message}"
            };
        }
    }
}
