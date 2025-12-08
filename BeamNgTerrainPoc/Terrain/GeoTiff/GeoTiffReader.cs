using BeamNgTerrainPoc.Terrain.Logging;
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
    /// </summary>
    public static void InitializeGdal()
    {
        lock (_initLock)
        {
            if (_gdalInitialized) return;

            try
            {
                GdalConfiguration.ConfigureGdal();
                Gdal.AllRegister();
                _gdalInitialized = true;
                TerrainLogger.Info("GDAL initialized successfully");
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
        if (!File.Exists(geoTiffPath))
            throw new FileNotFoundException($"GeoTIFF file not found: {geoTiffPath}");

        InitializeGdal();

        TerrainLogger.Info($"Reading GeoTIFF: {geoTiffPath}");

        using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
        if (dataset == null)
            throw new InvalidOperationException($"Failed to open GeoTIFF: {geoTiffPath}");

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
        TerrainLogger.Info($"Overpass query bbox: {boundingBox.ToOverpassBBox()}");

        // Get projection
        string? projection = dataset.GetProjection();

        // Read elevation data from first band
        var band = dataset.GetRasterBand(1);
        var elevationData = new double[width * height];
        band.ReadRaster(0, 0, width, height, elevationData, width, height, 0, 0);

        // Find min/max elevation
        double minElevation = double.MaxValue;
        double maxElevation = double.MinValue;

        foreach (var elevation in elevationData)
        {
            if (elevation < minElevation) minElevation = elevation;
            if (elevation > maxElevation) maxElevation = elevation;
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
    /// <returns>16-bit grayscale heightmap image in standard top-down format</returns>
    private Image<L16> ConvertToHeightmap(
        double[] elevationData,
        int width,
        int height,
        double minElevation,
        double maxElevation)
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
    /// <returns>Tuple of (width, height, bounding box)</returns>
    public (int Width, int Height, GeoBoundingBox BoundingBox) GetGeoTiffInfo(string geoTiffPath)
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

        return (width, height, boundingBox);
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
}
