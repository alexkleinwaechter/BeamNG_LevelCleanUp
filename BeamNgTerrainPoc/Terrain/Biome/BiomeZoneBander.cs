namespace BeamNgTerrainPoc.Terrain.Biome;

/// <summary>
/// One zone band of a biome layer: a distance range measured from the region border inward.
/// Bands are consumed in order; <see cref="DepthMeters"/> is the band thickness.
/// An interior band takes all remaining depth and is only valid as the last band.
/// </summary>
public sealed record BiomeZoneBandDefinition(double DepthMeters, bool IsInterior);

/// <summary>
/// Splits a region (e.g. "all pixels painted with terrain material X") into
/// distance-to-border zone bands using the exact Euclidean distance transform.
///
/// The preferred entry points take the raw .ter material bytes plus a material index —
/// no per-material bool[] mask is ever materialized (on a 4096² terrain with 20
/// materials those masks would cost ~335 MB). The depth field invariant used below:
/// after running the EDT with foreground = "outside the region", a pixel is in-region
/// iff its distance is &gt; 0 (outside pixels are foreground and get distance 0).
/// </summary>
public static class BiomeZoneBander
{
    /// <summary>
    /// Per-band flat row-major pixel index arrays (exact-size allocations), computed
    /// directly from the .ter material bytes. Hole pixels (byte 255) are never in-region.
    /// </summary>
    public static List<int[]> ComputeZonePixels(
        byte[] materialData,
        byte materialIndex,
        int size,
        float metersPerPixel,
        IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        ValidateBands(bands);
        var depth = ComputeDepthField(materialData, materialIndex, size, metersPerPixel);
        return BandPixels(depth, size, bands);
    }

    /// <summary>
    /// Per-band pixel counts only — for UI estimates; skips the pixel array allocations.
    /// </summary>
    public static long[] ComputeZoneCounts(
        byte[] materialData,
        byte materialIndex,
        int size,
        float metersPerPixel,
        IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        ValidateBands(bands);
        var depth = ComputeDepthField(materialData, materialIndex, size, metersPerPixel);
        return CountBandPixels(depth, size, bands);
    }

    /// <summary>
    /// bool[]-mask convenience overload (used by tests and mask-image layers).
    /// </summary>
    public static List<int[]> ComputeZonePixels(
        bool[] regionMask,
        int size,
        float metersPerPixel,
        IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        ValidateBands(bands);
        if (regionMask.Length != size * size)
        {
            throw new ArgumentException($"Mask length {regionMask.Length} does not match size {size}^2.", nameof(regionMask));
        }
        var depth = ComputeDepthField(regionMask, size, metersPerPixel);
        return BandPixels(depth, size, bands);
    }

    /// <summary>
    /// bool[]-mask variant of <see cref="ComputeZoneCounts(byte[],byte,int,float,IReadOnlyList{BiomeZoneBandDefinition})"/>
    /// (used for OSM-layer UI estimates).
    /// </summary>
    public static long[] ComputeZoneCounts(
        bool[] regionMask,
        int size,
        float metersPerPixel,
        IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        ValidateBands(bands);
        if (regionMask.Length != size * size)
        {
            throw new ArgumentException($"Mask length {regionMask.Length} does not match size {size}^2.", nameof(regionMask));
        }
        var depth = ComputeDepthField(regionMask, size, metersPerPixel);
        return CountBandPixels(depth, size, bands);
    }

    /// <summary>
    /// Distance in meters from every pixel to the nearest in-region pixel, [y, x] —
    /// region pixels get exactly 0. This is the opposite direction of
    /// <see cref="ComputeDepthField(bool[],int,float)"/> and drives the negative-list
    /// cleanup buffer (membership = distance ≤ buffer). Uses the same double-precision
    /// envelope EDT — do not swap in the shared float DistanceFieldCalculator (corrupts
    /// at 8192², see the class remarks). An all-false mask yields huge distances everywhere.
    /// </summary>
    public static float[,] ComputeDistanceToRegionMeters(bool[] regionMask, int size, float metersPerPixel)
    {
        if (regionMask.Length != size * size)
        {
            throw new ArgumentException($"Mask length {regionMask.Length} does not match size {size}^2.", nameof(regionMask));
        }

        var foreground = new byte[size, size];
        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                if (regionMask[row + x])
                {
                    foreground[y, x] = 255;
                }
            }
        }

        return ComputeEdtMeters(foreground, metersPerPixel);
    }

    /// <summary>
    /// Depth-from-border field in meters, [y, x]. In-region pixels get their distance to
    /// the nearest non-region pixel (&gt; 0); non-region pixels get exactly 0. A region
    /// touching the map edge has no border there. A region covering the whole map has no
    /// border at all — every pixel gets float.MaxValue (only an interior band can claim them).
    ///
    /// Uses a double-precision envelope EDT (see <see cref="ComputeEdtMeters"/>) — the shared
    /// float EDT corrupts at 8192² (its 1e12 INF sentinel exceeds float precision, giving
    /// foreground pixels small nonzero distances, which put millions of foreign pixels into
    /// the border bands on a real map). Non-region pixels are additionally zeroed explicitly
    /// so no numeric noise can ever leak them into a band.
    /// </summary>
    public static float[,] ComputeDepthField(byte[] materialData, byte materialIndex, int size, float metersPerPixel)
    {
        if (materialData.Length != size * size)
        {
            throw new ArgumentException($"Material data length {materialData.Length} does not match size {size}^2.", nameof(materialData));
        }

        return ComputeDepthFieldCore(size, metersPerPixel, i => materialData[i] == materialIndex);
    }

    /// <summary>bool[]-mask variant of <see cref="ComputeDepthField(byte[],byte,int,float)"/>.</summary>
    public static float[,] ComputeDepthField(bool[] regionMask, int size, float metersPerPixel)
    {
        if (regionMask.Length != size * size)
        {
            throw new ArgumentException($"Mask length {regionMask.Length} does not match size {size}^2.", nameof(regionMask));
        }

        return ComputeDepthFieldCore(size, metersPerPixel, i => regionMask[i]);
    }

    private static float[,] ComputeDepthFieldCore(int size, float metersPerPixel, Func<int, bool> inRegion)
    {
        var outside = new byte[size, size];
        var hasOutside = false;
        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                if (!inRegion(row + x))
                {
                    outside[y, x] = 255;
                    hasOutside = true;
                }
            }
        }

        if (!hasOutside)
        {
            return CreateWholeMapDepthField(size);
        }

        var depth = ComputeEdtMeters(outside, metersPerPixel);

        // Invariant enforcement: a non-region pixel must never carry depth > 0.
        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                if (!inRegion(row + x))
                {
                    depth[y, x] = 0f;
                }
            }
        }

        return depth;
    }

    /// <summary>
    /// Exact Euclidean distance transform (Felzenszwalb &amp; Huttenlocher) with the per-scanline
    /// envelope math in DOUBLE precision. The grid stays float (squared distances fit float
    /// comfortably); only the f/z/s envelope buffers are double — that is where the shared
    /// float implementation breaks down at 8192² map sizes.
    /// Input: 255 = foreground (distance 0 there). Output: meters to nearest foreground.
    /// </summary>
    private static float[,] ComputeEdtMeters(byte[,] mask, float metersPerPixel)
    {
        var h = mask.GetLength(0);
        var w = mask.GetLength(1);
        var dist = new float[h, w];
        const double INF = 1e18;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                dist[y, x] = mask[y, x] > 0 ? 0f : float.MaxValue;
            }
        }

        // Row pass.
        {
            var f = new double[w];
            var v = new int[w];
            var z = new double[w + 1];
            var scratch = new double[w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    f[x] = dist[y, x] == float.MaxValue ? INF : dist[y, x];
                }
                EdtScanline(f, v, z, scratch, w);
                for (var x = 0; x < w; x++)
                {
                    dist[y, x] = (float)f[x];
                }
            }
        }

        // Column pass.
        {
            var f = new double[h];
            var v = new int[h];
            var z = new double[h + 1];
            var scratch = new double[h];
            for (var x = 0; x < w; x++)
            {
                for (var y = 0; y < h; y++)
                {
                    f[y] = dist[y, x];
                }
                EdtScanline(f, v, z, scratch, h);
                for (var y = 0; y < h; y++)
                {
                    dist[y, x] = MathF.Sqrt((float)f[y]) * metersPerPixel;
                }
            }
        }

        return dist;
    }

    /// <summary>
    /// One 1D squared-distance pass (lower envelope of parabolas), in place on
    /// <paramref name="f"/>. Standard Felzenszwalb &amp; Huttenlocher.
    /// </summary>
    private static void EdtScanline(double[] f, int[] v, double[] z, double[] scratch, int n)
    {
        var k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (var q = 1; q < n; q++)
        {
            double s;
            while (true)
            {
                var p = v[k];
                s = (f[q] + (double)q * q - (f[p] + (double)p * p)) / (2.0 * (q - p));
                if (s <= z[k])
                {
                    k--;
                    if (k < 0)
                    {
                        k = 0;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            var p = v[k];
            scratch[q] = (double)(q - p) * (q - p) + f[p];
        }
        Array.Copy(scratch, f, n);
    }

    private static float[,] CreateWholeMapDepthField(int size)
    {
        var depth = new float[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                depth[y, x] = float.MaxValue;
            }
        }
        return depth;
    }

    /// <summary>
    /// Two-pass banding: count per band, then fill exact-size arrays — no List growth waste.
    /// </summary>
    private static List<int[]> BandPixels(float[,] depth, int size, IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        var result = new List<int[]>(bands.Count);
        if (bands.Count == 0)
        {
            return result;
        }

        var starts = ComputeBandStarts(bands);
        var counts = CountBandPixels(depth, size, bands, starts);

        var arrays = new int[bands.Count][];
        var cursors = new int[bands.Count];
        for (var b = 0; b < bands.Count; b++)
        {
            arrays[b] = new int[counts[b]];
        }

        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            for (var x = 0; x < size; x++)
            {
                var d = depth[y, x];
                if (d <= 0f)
                {
                    continue;
                }
                var b = FindBand(bands, starts, d);
                if (b >= 0)
                {
                    arrays[b][cursors[b]++] = row + x;
                }
            }
        }

        result.AddRange(arrays);
        return result;
    }

    private static long[] CountBandPixels(float[,] depth, int size, IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        return CountBandPixels(depth, size, bands, ComputeBandStarts(bands));
    }

    private static long[] CountBandPixels(
        float[,] depth, int size, IReadOnlyList<BiomeZoneBandDefinition> bands, double[] starts)
    {
        var counts = new long[bands.Count];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = depth[y, x];
                if (d <= 0f)
                {
                    continue;
                }
                var b = FindBand(bands, starts, d);
                if (b >= 0)
                {
                    counts[b]++;
                }
            }
        }
        return counts;
    }

    private static double[] ComputeBandStarts(IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        var starts = new double[bands.Count];
        var acc = 0.0;
        for (var i = 0; i < bands.Count; i++)
        {
            starts[i] = acc;
            acc += bands[i].IsInterior ? 0.0 : bands[i].DepthMeters;
        }
        return starts;
    }

    private static int FindBand(IReadOnlyList<BiomeZoneBandDefinition> bands, double[] starts, float depth)
    {
        for (var b = 0; b < bands.Count; b++)
        {
            var matches = bands[b].IsInterior
                ? depth >= starts[b]
                : depth >= starts[b] && depth < starts[b] + bands[b].DepthMeters;
            if (matches)
            {
                return b;
            }
        }
        return -1;
    }

    private static void ValidateBands(IReadOnlyList<BiomeZoneBandDefinition> bands)
    {
        for (var i = 0; i < bands.Count; i++)
        {
            if (bands[i].IsInterior && i != bands.Count - 1)
            {
                throw new ArgumentException("An interior band is only valid as the last band.", nameof(bands));
            }
            if (!bands[i].IsInterior && bands[i].DepthMeters <= 0)
            {
                throw new ArgumentException($"Band {i} has non-positive depth {bands[i].DepthMeters}.", nameof(bands));
            }
        }
    }
}
