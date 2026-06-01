using System.Globalization;

namespace BeamNgTerrainPoc.Terrain.GeoTiff;

/// <summary>
/// Fast line-by-line XYZ ASCII scanner that extracts boundary metadata without GDAL.
/// Streams through files with StreamReader, parsing only coordinates per line,
/// avoiding the full sequential parse that GDAL performs on XYZ files.
/// </summary>
public class TileBoundsInfo
{
    public required string FilePath { get; init; }
    public double MinX { get; init; }
    public double MaxX { get; init; }
    public double MinY { get; init; }
    public double MaxY { get; init; }
}

public static class XyzFastScanner
{
    public class XyzScanResult
    {
        public double MinX, MaxX, MinY, MaxY;
        public double? MinZ, MaxZ;
        public double PixelSizeX, PixelSizeY;
        public int Width, Height;
        public long LineCount;
        public List<TileBoundsInfo>? TileBounds;
    }

    /// <summary>
    /// Filters tiles to only those whose bounds intersect the given crop bounding box.
    /// </summary>
    public static string[] FilterTilesByBbox(
        List<TileBoundsInfo> tiles,
        double cropMinX, double cropMinY, double cropMaxX, double cropMaxY)
    {
        return tiles
            .Where(t => t.MaxX > cropMinX && t.MinX < cropMaxX &&
                        t.MaxY > cropMinY && t.MinY < cropMaxY)
            .Select(t => t.FilePath)
            .ToArray();
    }

    /// <summary>
    /// Scans a single XYZ file line-by-line to extract bounds, pixel size, and grid dimensions.
    /// No GDAL involved — pure text streaming.
    /// </summary>
    public static XyzScanResult ScanFile(string xyzPath, bool includeElevation = true)
    {
        if (!File.Exists(xyzPath))
            throw new FileNotFoundException($"XYZ file not found: {xyzPath}");

        using var stream = new FileStream(xyzPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024);
        using var reader = new StreamReader(stream);

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;

        double pixelSizeX = 0;
        double pixelSizeY = 0;
        double firstX = double.NaN;
        double firstY = double.NaN;
        bool pixelSizeXFound = false;
        bool pixelSizeYFound = false;

        // For detecting pixel size when consecutive X values are identical (column-major)
        var initialXValues = new List<double>(100);
        bool collectingInitialX = true;

        long lineCount = 0;
        char[]? detectedSeparators = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Skip comment/header lines
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith('#'))
                continue;

            // Detect separator from the first data line
            detectedSeparators ??= DetectSeparator(trimmed);

            var parts = trimmed.Split(detectedSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                continue; // Non-numeric first token — skip header line
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                continue;

            lineCount++;

            // Track bounds
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;

            if (includeElevation && parts.Length >= 3 &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            // Determine pixel sizes from the first few data lines
            if (double.IsNaN(firstX))
            {
                firstX = x;
                firstY = y;
            }
            else
            {
                // Detect pixelSizeX: difference between consecutive X values
                if (!pixelSizeXFound)
                {
                    var diffX = Math.Abs(x - firstX);
                    if (diffX > 1e-10)
                    {
                        pixelSizeX = diffX;
                        pixelSizeXFound = true;
                        collectingInitialX = false;
                    }
                    else if (collectingInitialX)
                    {
                        // Same X — might be column-major order. Collect initial X values.
                        initialXValues.Add(x);
                    }
                }

                // Detect pixelSizeY: when Y value changes from first Y
                if (!pixelSizeYFound)
                {
                    var diffY = Math.Abs(y - firstY);
                    if (diffY > 1e-10)
                    {
                        pixelSizeY = diffY;
                        pixelSizeYFound = true;

                        // If pixelSizeX wasn't found from consecutive lines (column-major),
                        // compute from initial X values
                        if (!pixelSizeXFound && initialXValues.Count > 0)
                        {
                            pixelSizeX = FindMinSpacing(initialXValues, firstX);
                            pixelSizeXFound = pixelSizeX > 1e-10;
                            collectingInitialX = false;
                        }
                    }
                }
            }

            // Stop collecting initial X values after 1000 lines to avoid memory bloat
            if (collectingInitialX && initialXValues.Count >= 1000)
                collectingInitialX = false;
        }

        if (lineCount == 0)
            throw new InvalidOperationException(
                $"XYZ file contains no valid data lines: {Path.GetFileName(xyzPath)}");

        // Fallback: if pixelSizeX wasn't found from consecutive diffs, try initial X values
        if (!pixelSizeXFound && initialXValues.Count > 0)
        {
            pixelSizeX = FindMinSpacing(initialXValues, firstX);
            pixelSizeXFound = pixelSizeX > 1e-10;
        }

        // If pixel sizes still not determined, fall back to 1.0
        if (pixelSizeX < 1e-10) pixelSizeX = 1.0;
        if (pixelSizeY < 1e-10) pixelSizeY = pixelSizeX; // Assume square pixels

        var width = (int)Math.Round((maxX - minX) / pixelSizeX) + 1;
        var height = (int)Math.Round((maxY - minY) / pixelSizeY) + 1;

        return new XyzScanResult
        {
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY,
            MinZ = minZ < double.MaxValue && includeElevation ? minZ : null,
            MaxZ = maxZ > double.MinValue && includeElevation ? maxZ : null,
            PixelSizeX = pixelSizeX,
            PixelSizeY = pixelSizeY,
            Width = width,
            Height = height,
            LineCount = lineCount
        };
    }

    /// <summary>
    /// Scans multiple XYZ files in parallel and merges bounds.
    /// </summary>
    public static XyzScanResult ScanFiles(string[] xyzPaths,
        bool includeElevation = true, IProgress<string>? progress = null)
    {
        if (xyzPaths.Length == 0)
            throw new ArgumentException("No XYZ files provided.");

        if (xyzPaths.Length == 1)
        {
            var single = ScanFile(xyzPaths[0], includeElevation);
            single.TileBounds =
            [
                new TileBoundsInfo
                {
                    FilePath = xyzPaths[0],
                    MinX = single.MinX, MaxX = single.MaxX,
                    MinY = single.MinY, MaxY = single.MaxY
                }
            ];
            return single;
        }

        var results = new XyzScanResult[xyzPaths.Length];
        var completed = 0;

        Parallel.For(0, xyzPaths.Length, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                results[i] = ScanFile(xyzPaths[i], includeElevation);
                var count = Interlocked.Increment(ref completed);
                if (count % 20 == 0 || count == xyzPaths.Length)
                    progress?.Report($"Scanned {count}/{xyzPaths.Length} XYZ tiles...");
            });

        // Build per-tile bounds
        var tileBounds = new List<TileBoundsInfo>(xyzPaths.Length);
        for (int i = 0; i < xyzPaths.Length; i++)
        {
            var r = results[i];
            tileBounds.Add(new TileBoundsInfo
            {
                FilePath = xyzPaths[i],
                MinX = r.MinX, MaxX = r.MaxX,
                MinY = r.MinY, MaxY = r.MaxY
            });
        }

        // Merge results
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        long totalLines = 0;
        double pixelSizeX = 0, pixelSizeY = 0;
        bool firstPixelSize = true;

        foreach (var r in results)
        {
            if (r.MinX < minX) minX = r.MinX;
            if (r.MaxX > maxX) maxX = r.MaxX;
            if (r.MinY < minY) minY = r.MinY;
            if (r.MaxY > maxY) maxY = r.MaxY;

            if (includeElevation)
            {
                if (r.MinZ.HasValue && r.MinZ.Value < minZ) minZ = r.MinZ.Value;
                if (r.MaxZ.HasValue && r.MaxZ.Value > maxZ) maxZ = r.MaxZ.Value;
            }

            totalLines += r.LineCount;

            if (firstPixelSize)
            {
                pixelSizeX = r.PixelSizeX;
                pixelSizeY = r.PixelSizeY;
                firstPixelSize = false;
            }
        }

        var width = (int)Math.Round((maxX - minX) / pixelSizeX) + 1;
        var height = (int)Math.Round((maxY - minY) / pixelSizeY) + 1;

        return new XyzScanResult
        {
            MinX = minX,
            MaxX = maxX,
            MinY = minY,
            MaxY = maxY,
            MinZ = minZ < double.MaxValue && includeElevation ? minZ : null,
            MaxZ = maxZ > double.MinValue && includeElevation ? maxZ : null,
            PixelSizeX = pixelSizeX,
            PixelSizeY = pixelSizeY,
            Width = width,
            Height = height,
            LineCount = totalLines,
            TileBounds = tileBounds
        };
    }

    /// <summary>
    /// Auto-detects EPSG code from the first data line's coordinate ranges.
    /// Reads only the first data line — no GDAL involved.
    /// </summary>
    public static int? AutoDetectEpsg(string xyzPath)
    {
        try
        {
            using var reader = new StreamReader(xyzPath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith('#'))
                    continue;

                var separators = DetectSeparator(trimmed);
                var parts = trimmed.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                    continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                    continue;

                // German ETRS89/UTM Zone 32N (EPSG:25832)
                // Easting (X): typically 280,000 - 840,000
                // Northing (Y): typically 5,230,000 - 6,090,000
                if (x >= 200_000 && x <= 900_000 && y >= 5_000_000 && y <= 6_200_000)
                    return 25832;

                // Could extend with Zone 33N (EPSG:25833) and others in the future

                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detects the separator used in an XYZ data line.
    /// Tries space/tab first (most common), then semicolon.
    /// </summary>
    private static char[] DetectSeparator(string dataLine)
    {
        // Try whitespace (space/tab) — most common for XYZ
        var whitespaceParts = dataLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (whitespaceParts.Length >= 3 &&
            double.TryParse(whitespaceParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return [' ', '\t'];

        // Try semicolon
        var semicolonParts = dataLine.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (semicolonParts.Length >= 3 &&
            double.TryParse(semicolonParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return [';'];

        // Try comma
        var commaParts = dataLine.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (commaParts.Length >= 3 &&
            double.TryParse(commaParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return [','];

        // Default to whitespace
        return [' ', '\t'];
    }

    /// <summary>
    /// Finds the minimum non-zero spacing among collected values and a reference value.
    /// Used to determine pixel size when consecutive X values are identical (column-major order).
    /// </summary>
    private static double FindMinSpacing(List<double> values, double referenceValue)
    {
        var allValues = new HashSet<double> { referenceValue };
        foreach (var v in values) allValues.Add(v);

        var sorted = allValues.OrderBy(v => v).ToList();
        var minSpacing = double.MaxValue;

        for (int i = 1; i < sorted.Count; i++)
        {
            var diff = sorted[i] - sorted[i - 1];
            if (diff > 1e-10 && diff < minSpacing)
                minSpacing = diff;
        }

        return minSpacing < double.MaxValue ? minSpacing : 0;
    }
}
