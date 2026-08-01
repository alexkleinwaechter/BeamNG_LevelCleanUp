namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Elevation raster covering a window of the source mosaic, possibly downsampled
///     (far raster, spec §6). Row 0 = north-most row of the window. Values are absolute
///     DEM elevations in meters, nodata already filled by edge-extension.
/// </summary>
public sealed class BackdropRaster
{
    private readonly float[] _elevations; // row-major [y * Width + x]

    public int Width { get; }
    public int Height { get; }
    public PixelRect SourceWindow { get; }
    /// <summary>Mosaic pixels covered by one raster pixel (≥ 1 when downsampled).</summary>
    public double SourcePixelsPerCellX { get; }
    public double SourcePixelsPerCellY { get; }

    public BackdropRaster(float[] elevations, int width, int height, PixelRect sourceWindow)
    {
        if (elevations.Length != width * height)
            throw new ArgumentException($"Expected {width * height} samples, got {elevations.Length}.");
        _elevations = elevations;
        Width = width;
        Height = height;
        SourceWindow = sourceWindow;
        SourcePixelsPerCellX = (double)sourceWindow.Width / width;
        SourcePixelsPerCellY = (double)sourceWindow.Height / height;
    }

    public bool ContainsSourcePoint(double srcX, double srcY) =>
        srcX >= SourceWindow.X && srcX <= SourceWindow.Right &&
        srcY >= SourceWindow.Y && srcY <= SourceWindow.Bottom;

    /// <summary>Bilinear sample addressed in MOSAIC pixel coordinates; clamps outside the window.</summary>
    public double SampleBilinearAtSource(double srcX, double srcY)
    {
        // Convert to local raster grid coordinates, pixel centers at +0.5.
        var gx = (srcX - SourceWindow.X) / SourcePixelsPerCellX - 0.5;
        var gy = (srcY - SourceWindow.Y) / SourcePixelsPerCellY - 0.5;

        gx = Math.Clamp(gx, 0, Width - 1);
        gy = Math.Clamp(gy, 0, Height - 1);

        var x0 = (int)Math.Floor(gx);
        var y0 = (int)Math.Floor(gy);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = gx - x0;
        var fy = gy - y0;

        double v00 = _elevations[y0 * Width + x0];
        double v10 = _elevations[y0 * Width + x1];
        double v01 = _elevations[y1 * Width + x0];
        double v11 = _elevations[y1 * Width + x1];

        var top = v00 + (v10 - v00) * fx;
        var bottom = v01 + (v11 - v01) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>
    ///     Fills nodata cells with the value of the nearest valid cell (multi-source BFS,
    ///     4-neighborhood, O(n)). Returns the number of nodata cells (spec §6 warning %).
    /// </summary>
    public static int FillNodataByEdgeExtension(float[] elevations, bool[] nodata, int width, int height)
    {
        var total = 0;
        var queue = new Queue<int>();
        var pending = new bool[elevations.Length];

        for (var i = 0; i < elevations.Length; i++)
        {
            if (nodata[i]) { total++; pending[i] = true; }
        }
        if (total == 0 || total == elevations.Length)
            return total; // nothing to do, or nothing to extend from (values stay as-is)

        // Seed with valid cells adjacent to nodata.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            if (pending[i]) continue;
            if ((x > 0 && pending[i - 1]) || (x < width - 1 && pending[i + 1]) ||
                (y > 0 && pending[i - width]) || (y < height - 1 && pending[i + width]))
                queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var x = i % width;
            var y = i / width;
            Span<int> neighbors = [x > 0 ? i - 1 : -1, x < width - 1 ? i + 1 : -1,
                                   y > 0 ? i - width : -1, y < height - 1 ? i + width : -1];
            foreach (var n in neighbors)
            {
                if (n < 0 || !pending[n]) continue;
                elevations[n] = elevations[i];
                pending[n] = false;
                queue.Enqueue(n);
            }
        }

        return total;
    }
}
