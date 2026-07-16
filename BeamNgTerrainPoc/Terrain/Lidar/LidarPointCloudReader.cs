using System.Collections;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using OSGeo.OSR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Lidar;

/// <summary>
///     Streams classified LAS/LAZ tiles and creates a ground-only digital terrain model.
/// </summary>
public sealed class LidarPointCloudReader
{
    public const float DefaultMetadataCellSizeMeters = 0.5f;

    public sealed class PointCloudInfo
    {
        public required string[] FilePaths { get; init; }
        public required GeoBoundingBox NativeBoundingBox { get; init; }
        public GeoBoundingBox? Wgs84BoundingBox { get; init; }
        public required List<TileBoundsInfo> TileBounds { get; init; }
        public string? ProjectionWkt { get; init; }
        public string? ProjectionName { get; init; }
        public int? EpsgCode { get; init; }
        public double LinearUnitToMeters { get; init; } = 1.0;
        public ulong PointCount { get; init; }
        public double HeaderMinElevationMeters { get; init; }
        public double HeaderMaxElevationMeters { get; init; }
        public int PreviewWidth { get; init; }
        public int PreviewHeight { get; init; }
        public double[] GeoTransform { get; init; } = [];
    }

    public PointCloudInfo ReadInfo(
        IEnumerable<string> filePaths,
        int epsgCode = 0,
        float metadataCellSizeMeters = DefaultMetadataCellSizeMeters)
    {
        var paths = filePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            throw new FileNotFoundException("No LAS/LAZ point-cloud files were found.");
        if (metadataCellSizeMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(metadataCellSizeMeters));

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        ulong pointCount = 0;
        var tileBounds = new List<TileBoundsInfo>(paths.Length);

        foreach (var path in paths)
        {
            using var reader = new LasZipNativeReader();
            reader.Open(path);
            var h = reader.Header;

            minX = Math.Min(minX, h.MinX);
            minY = Math.Min(minY, h.MinY);
            minZ = Math.Min(minZ, h.MinZ);
            maxX = Math.Max(maxX, h.MaxX);
            maxY = Math.Max(maxY, h.MaxY);
            maxZ = Math.Max(maxZ, h.MaxZ);
            pointCount += reader.PointCount;

            tileBounds.Add(new TileBoundsInfo
            {
                FilePath = path,
                MinX = h.MinX,
                MinY = h.MinY,
                MaxX = h.MaxX,
                MaxY = h.MaxY
            });
        }

        var detectedEpsg = epsgCode > 0 ? epsgCode : DetectCommonEpsg(paths);
        var (projectionWkt, projectionName, linearUnitToMeters) = BuildProjection(detectedEpsg);
        var nativeBounds = new GeoBoundingBox(minX, minY, maxX, maxY);
        var wgs84Bounds = projectionWkt == null
            ? null
            : GeoBoundingBox.TransformToWgs84(nativeBounds, projectionWkt);

        var nativeCellSize = metadataCellSizeMeters / linearUnitToMeters;
        var previewWidth = ClampDimension(Math.Ceiling((maxX - minX) / nativeCellSize));
        var previewHeight = ClampDimension(Math.Ceiling((maxY - minY) / nativeCellSize));

        return new PointCloudInfo
        {
            FilePaths = paths,
            NativeBoundingBox = nativeBounds,
            Wgs84BoundingBox = wgs84Bounds,
            TileBounds = tileBounds,
            ProjectionWkt = projectionWkt,
            ProjectionName = projectionName,
            EpsgCode = detectedEpsg,
            LinearUnitToMeters = linearUnitToMeters,
            PointCount = pointCount,
            HeaderMinElevationMeters = minZ * linearUnitToMeters,
            HeaderMaxElevationMeters = maxZ * linearUnitToMeters,
            PreviewWidth = previewWidth,
            PreviewHeight = previewHeight,
            GeoTransform = [minX, nativeCellSize, 0, maxY, 0, -nativeCellSize]
        };
    }

    public GeoTiffImportResult CreateGroundDtm(
        string[] filePaths,
        int epsgCode,
        int targetSize,
        float metersPerPixel,
        byte groundClassification = 2,
        bool crop = false,
        int cropOffsetX = 0,
        int cropOffsetY = 0,
        float metadataCellSizeMeters = DefaultMetadataCellSizeMeters,
        Action<string>? progress = null)
    {
        if (targetSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetSize));
        if (metersPerPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(metersPerPixel));
        if (epsgCode <= 0)
            throw new ArgumentException("A valid EPSG code is required for LAS/LAZ data.", nameof(epsgCode));

        var info = ReadInfo(filePaths, epsgCode, metadataCellSizeMeters);
        var outputExtentNative = targetSize * (double)metersPerPixel / info.LinearUnitToMeters;
        var metadataCellNative = metadataCellSizeMeters / info.LinearUnitToMeters;

        double outputMinX;
        double outputMaxY;
        if (crop)
        {
            outputMinX = info.NativeBoundingBox.MinLongitude + cropOffsetX * metadataCellNative;
            outputMaxY = info.NativeBoundingBox.MaxLatitude - cropOffsetY * metadataCellNative;
        }
        else
        {
            outputMinX = info.NativeBoundingBox.Center.Longitude - outputExtentNative / 2.0;
            outputMaxY = info.NativeBoundingBox.Center.Latitude + outputExtentNative / 2.0;
        }

        var outputMaxX = outputMinX + outputExtentNative;
        var outputMinY = outputMaxY - outputExtentNative;
        var selectedFiles = info.TileBounds
            .Where(t => Intersects(t, outputMinX, outputMinY, outputMaxX, outputMaxY))
            .Select(t => t.FilePath)
            .ToArray();

        if (selectedFiles.Length == 0)
            throw new InvalidOperationException("The selected terrain square does not intersect any LAS/LAZ tile.");

        progress?.Invoke($"Scanning ground-class points in {selectedFiles.Length} LAS/LAZ tile(s)...");
        var groundStats = ScanGroundRange(
            selectedFiles, groundClassification,
            outputMinX, outputMinY, outputMaxX, outputMaxY,
            info.LinearUnitToMeters, progress);

        if (groundStats.Count == 0)
            throw new InvalidOperationException(
                $"No classification {groundClassification} points were found in the selected area. " +
                "Use a classified point cloud with ASPRS class 2 ground points, or correct the crop/CRS.");

        var range = groundStats.MaxElevationMeters - groundStats.MinElevationMeters;
        if (range <= 0.000001)
            range = 1.0;

        var cellArea = metersPerPixel * (double)metersPerPixel;
        var selectedArea = targetSize * (double)targetSize * cellArea;
        var groundDensity = groundStats.Count / selectedArea;
        var averageSpacing = groundDensity > 0 ? Math.Sqrt(1.0 / groundDensity) : double.PositiveInfinity;
        progress?.Invoke(
            $"Ground scan: {groundStats.Count:N0} points, {groundDensity:F2} pts/m², " +
            $"average spacing ~{averageSpacing:F2}m, elevation {groundStats.MinElevationMeters:F2}-{groundStats.MaxElevationMeters:F2}m");

        if (metersPerPixel < averageSpacing * 0.75)
            progress?.Invoke(
                $"Warning: {metersPerPixel:F2}m cells are finer than the ~{averageSpacing:F2}m average ground-point spacing; " +
                "empty cells will be interpolated and do not add measured detail.");

        var length = checked(targetSize * targetSize);
        var samples = GC.AllocateUninitializedArray<ushort>(length);
        Array.Fill(samples, ushort.MaxValue);
        var populated = new BitArray(length);

        try
        {
            RasterizeGroundPoints(
                selectedFiles, groundClassification,
                outputMinX, outputMaxY, outputExtentNative / targetSize,
                targetSize, groundStats.MinElevationMeters, range,
                info.LinearUnitToMeters, samples, populated, progress);

            progress?.Invoke("Interpolating cells between measured ground points...");
            FillMissingCells(samples, populated, targetSize);

            progress?.Invoke("Building 16-bit ground DTM heightmap...");
            var image = new Image<L16>(targetSize, targetSize);
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < targetSize; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var offset = y * targetSize;
                    for (var x = 0; x < targetSize; x++)
                        row[x] = new L16(samples[offset + x]);
                }
            });

            var selectedNativeBounds = new GeoBoundingBox(outputMinX, outputMinY, outputMaxX, outputMaxY);
            return new GeoTiffImportResult
            {
                HeightmapImage = image,
                BoundingBox = selectedNativeBounds,
                Wgs84BoundingBox = info.ProjectionWkt == null
                    ? null
                    : GeoBoundingBox.TransformToWgs84(selectedNativeBounds, info.ProjectionWkt),
                MinElevation = groundStats.MinElevationMeters,
                MaxElevation = groundStats.MaxElevationMeters,
                PixelSizeX = metersPerPixel,
                PixelSizeY = metersPerPixel,
                Projection = info.ProjectionWkt,
                SourcePath = selectedFiles.Length == 1 ? selectedFiles[0] : Path.GetDirectoryName(selectedFiles[0])
            };
        }
        finally
        {
            samples = null!;
            populated = null!;
            // Large 8K/16K working grids should not stay alive into terrain assembly.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
    }

    internal static void FillMissingCells(ushort[] samples, BitArray populated, int size)
    {
        var rowHasData = new bool[size];

        // Interpolate gaps horizontally. Dense LiDAR gaps are usually only a few cells;
        // the same method also bridges water/no-return strips without creating zero trenches.
        for (var y = 0; y < size; y++)
        {
            var rowStart = y * size;
            var first = -1;
            for (var x = 0; x < size; x++)
            {
                if (!populated[rowStart + x])
                    continue;
                first = x;
                break;
            }

            if (first < 0)
                continue;

            rowHasData[y] = true;
            var firstValue = samples[rowStart + first];
            for (var x = 0; x < first; x++)
                samples[rowStart + x] = firstValue;

            var previous = first;
            for (var x = first + 1; x < size; x++)
            {
                if (!populated[rowStart + x])
                    continue;

                var leftValue = samples[rowStart + previous];
                var rightValue = samples[rowStart + x];
                var gap = x - previous;
                for (var fillX = previous + 1; fillX < x; fillX++)
                {
                    var fraction = (fillX - previous) / (double)gap;
                    samples[rowStart + fillX] = (ushort)Math.Clamp(
                        Math.Round(leftValue + (rightValue - leftValue) * fraction), 0, ushort.MaxValue);
                }

                previous = x;
            }

            var lastValue = samples[rowStart + previous];
            for (var x = previous + 1; x < size; x++)
                samples[rowStart + x] = lastValue;
        }

        var firstDataRow = Array.FindIndex(rowHasData, hasData => hasData);
        if (firstDataRow < 0)
            throw new InvalidOperationException("The point cloud did not populate any terrain cells.");

        // Extend the first/last measured row to the output edge.
        for (var y = 0; y < firstDataRow; y++)
            Array.Copy(samples, firstDataRow * size, samples, y * size, size);

        var previousRow = firstDataRow;
        for (var y = firstDataRow + 1; y < size; y++)
        {
            if (!rowHasData[y])
                continue;

            var gap = y - previousRow;
            for (var fillY = previousRow + 1; fillY < y; fillY++)
            {
                var fraction = (fillY - previousRow) / (double)gap;
                var targetOffset = fillY * size;
                var topOffset = previousRow * size;
                var bottomOffset = y * size;
                for (var x = 0; x < size; x++)
                {
                    var top = samples[topOffset + x];
                    var bottom = samples[bottomOffset + x];
                    samples[targetOffset + x] = (ushort)Math.Clamp(
                        Math.Round(top + (bottom - top) * fraction), 0, ushort.MaxValue);
                }
            }

            previousRow = y;
        }

        for (var y = previousRow + 1; y < size; y++)
            Array.Copy(samples, previousRow * size, samples, y * size, size);
    }

    private static GroundStats ScanGroundRange(
        string[] paths,
        byte groundClass,
        double minX,
        double minY,
        double maxX,
        double maxY,
        double unitToMeters,
        Action<string>? progress)
    {
        long count = 0;
        var minElevation = double.PositiveInfinity;
        var maxElevation = double.NegativeInfinity;

        for (var fileIndex = 0; fileIndex < paths.Length; fileIndex++)
        {
            var path = paths[fileIndex];
            progress?.Invoke($"Ground scan {fileIndex + 1}/{paths.Length}: {Path.GetFileName(path)}");
            using var reader = new LasZipNativeReader();
            reader.Open(path);

            var pointCount = reader.PointCount;
            for (ulong i = 0; i < pointCount; i++)
            {
                if (!reader.ReadPoint(out var x, out var y, out var z, out var classification))
                    break;
                if (classification != groundClass || x < minX || x >= maxX || y < minY || y >= maxY)
                    continue;

                var elevationMeters = z * unitToMeters;
                minElevation = Math.Min(minElevation, elevationMeters);
                maxElevation = Math.Max(maxElevation, elevationMeters);
                count++;
            }
        }

        return new GroundStats(count, minElevation, maxElevation);
    }

    private static void RasterizeGroundPoints(
        string[] paths,
        byte groundClass,
        double minX,
        double maxY,
        double nativeCellSize,
        int size,
        double minElevationMeters,
        double elevationRangeMeters,
        double unitToMeters,
        ushort[] samples,
        BitArray populated,
        Action<string>? progress)
    {
        for (var fileIndex = 0; fileIndex < paths.Length; fileIndex++)
        {
            var path = paths[fileIndex];
            progress?.Invoke($"Rasterizing {fileIndex + 1}/{paths.Length}: {Path.GetFileName(path)}");
            using var reader = new LasZipNativeReader();
            reader.Open(path);

            var pointCount = reader.PointCount;
            for (ulong i = 0; i < pointCount; i++)
            {
                if (!reader.ReadPoint(out var x, out var y, out var z, out var classification))
                    break;
                if (classification != groundClass)
                    continue;

                var column = (int)Math.Floor((x - minX) / nativeCellSize);
                var row = (int)Math.Floor((maxY - y) / nativeCellSize);
                if ((uint)column >= (uint)size || (uint)row >= (uint)size)
                    continue;

                var normalized = ((z * unitToMeters) - minElevationMeters) / elevationRangeMeters;
                var packed = (ushort)Math.Clamp(Math.Round(normalized * ushort.MaxValue), 0, ushort.MaxValue);
                var index = row * size + column;

                // Lowest ground return per cell preserves small drainage cuts and avoids averaging them away.
                if (!populated[index] || packed < samples[index])
                {
                    samples[index] = packed;
                    populated[index] = true;
                }
            }
        }
    }

    private static int? DetectCommonEpsg(IEnumerable<string> paths)
    {
        int? detected = null;
        foreach (var path in paths)
        {
            var current = LasProjectionReader.TryReadEpsg(path);
            if (!current.HasValue)
                continue;
            if (detected.HasValue && detected.Value != current.Value)
                return null;
            detected = current;
        }

        return detected;
    }

    private static (string? Wkt, string? Name, double LinearUnitToMeters) BuildProjection(int? epsgCode)
    {
        if (!epsgCode.HasValue || epsgCode.Value <= 0)
            return (null, null, 1.0);

        GeoTiffReader.InitializeGdal();
        var srs = new SpatialReference(null);
        if (srs.ImportFromEPSG(epsgCode.Value) != 0)
            throw new ArgumentException($"Invalid or unsupported EPSG code: {epsgCode.Value}");
        if (srs.IsProjected() == 0)
            throw new ArgumentException(
                $"EPSG:{epsgCode.Value} is geographic (latitude/longitude). LAS/LAZ DTM generation requires a projected CRS in linear units.");

        srs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        var unitToMeters = srs.GetLinearUnits();
        if (!double.IsFinite(unitToMeters) || unitToMeters <= 0)
            unitToMeters = 1.0;

        srs.ExportToWkt(out var wkt, null);
        return (wkt, srs.GetName(), unitToMeters);
    }

    private static bool Intersects(
        TileBoundsInfo tile, double minX, double minY, double maxX, double maxY) =>
        tile.MaxX > minX && tile.MinX < maxX && tile.MaxY > minY && tile.MinY < maxY;

    private static int ClampDimension(double value) =>
        (int)Math.Clamp(value, 1, int.MaxValue);

    private readonly record struct GroundStats(long Count, double MinElevationMeters, double MaxElevationMeters);
}
