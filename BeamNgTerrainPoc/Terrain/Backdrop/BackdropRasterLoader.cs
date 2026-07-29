using BeamNgTerrainPoc.Terrain.GeoTiff;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Reads a window of a GeoTIFF (or combined mosaic) into a <see cref="BackdropRaster"/> via GDAL,
///     optionally downsampled through GDAL's buf_size resampling (spec §6). Nodata cells are filled by
///     edge-extension (<see cref="BackdropRaster.FillNodataByEdgeExtension"/>) before the raster is
///     handed out — every <see cref="BackdropRaster"/> produced by this loader is already fully populated.
/// </summary>
public static class BackdropRasterLoader
{
    /// <summary>
    ///     Reads a window of the GeoTIFF as float elevations. <paramref name="maxDimension"/> caps the
    ///     LARGER output side (GDAL resamples via buf_size); null = native resolution. Nodata → edge-extension
    ///     fill; <paramref name="nodataPercentage"/> is in [0, 100].
    /// </summary>
    public static BackdropRaster LoadWindow(string geoTiffPath, PixelRect window, int? maxDimension,
        out double nodataPercentage)
    {
        return LoadWindow(geoTiffPath, window, maxDimension, out nodataPercentage, out _);
    }

    /// <summary>
    ///     Same as the public overload, but also returns the RAW (pre-fill) nodata mask at the loaded
    ///     raster's resolution — row-major, same layout as the returned <see cref="BackdropRaster"/>.
    ///     Used only by <see cref="BackdropGenerator"/> to detect chunks whose entire source region has
    ///     no elevation data at all (spec §6 "100% nodata chunk is skipped").
    /// </summary>
    internal static BackdropRaster LoadWindow(string geoTiffPath, PixelRect window, int? maxDimension,
        out double nodataPercentage, out bool[] nodataMask)
    {
        if (window.IsEmpty)
            throw new ArgumentException("Window must be non-empty.", nameof(window));

        GeoTiffReader.InitializeGdal();

        using var dataset = Gdal.Open(geoTiffPath, Access.GA_ReadOnly);
        if (dataset == null)
            throw new InvalidOperationException($"Failed to open GeoTIFF: {geoTiffPath}");

        var band = dataset.GetRasterBand(1);

        // maxDimension caps the LARGER side; GDAL resamples through buf_size != win_size (nearest
        // neighbor by default for RasterIO without an explicit resampling algorithm), which is exactly
        // what we want here: cheap decimation for the far raster, exact reads for band strips (null cap).
        var bufWidth = window.Width;
        var bufHeight = window.Height;
        if (maxDimension is { } cap)
        {
            var larger = Math.Max(window.Width, window.Height);
            if (larger > cap)
            {
                var scale = (double)cap / larger;
                bufWidth = Math.Max(1, (int)Math.Round(window.Width * scale));
                bufHeight = Math.Max(1, (int)Math.Round(window.Height * scale));
            }
        }

        var buffer = new float[bufWidth * bufHeight];
        band.ReadRaster(window.X, window.Y, window.Width, window.Height, buffer, bufWidth, bufHeight, 0, 0);

        band.GetNoDataValue(out var nodataValue, out var hasNodata);
        var hasDeclaredNodata = hasNodata != 0;

        // Void predicate mirrors GeoTiffReader.FillNodataVoids's IsVoidValue (GeoTiffReader.cs:536-539):
        // a declared nodata tag alone is NOT enough — a tag declared as NaN never matches via
        // `Math.Abs(v - nodataValue) < tolerance` (NaN arithmetic is never < anything), and plenty of
        // real mosaics have UNDECLARED sentinel fills (no tag at all, or a tag that doesn't cover every
        // gap: 0-filled seams, -9999, -32767) that would otherwise silently become "valid" elevations —
        // e.g. a -400 m plateau after the vertical-datum formula, with nodataPercentage reported as 0 and
        // no warning. Catch NaN/Infinity and out-of-any-plausible-elevation values unconditionally, in
        // addition to an exact match against the declared tag when one exists.
        var mask = new bool[buffer.Length];
        var nodataCount = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            var v = buffer[i];
            var isVoid = float.IsNaN(v) || float.IsInfinity(v) || v < -1000f || v > 1_000_000f ||
                         (hasDeclaredNodata && Math.Abs(v - nodataValue) < 1e-3);
            if (!isVoid) continue;
            mask[i] = true;
            nodataCount++;
        }

        nodataPercentage = buffer.Length > 0 ? 100.0 * nodataCount / buffer.Length : 0.0;

        if (nodataCount > 0)
            BackdropRaster.FillNodataByEdgeExtension(buffer, mask, bufWidth, bufHeight);

        nodataMask = mask;
        return new BackdropRaster(buffer, bufWidth, bufHeight, window);
    }
}
