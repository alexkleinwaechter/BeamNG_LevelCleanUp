namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Computes exact Euclidean Distance Transform (EDT) using the
/// Felzenszwalb &amp; Huttenlocher algorithm.
/// 
/// This is a linear-time O(W*H) algorithm that computes the exact
/// Euclidean distance to the nearest foreground pixel for each pixel
/// in a binary mask.
/// </summary>
public static class DistanceFieldCalculator
{
    /// <summary>
    /// Computes exact Euclidean distance field using Felzenszwalb &amp; Huttenlocher algorithm.
    /// </summary>
    /// <param name="mask">Binary mask where 255 = foreground (road), 0 = background</param>
    /// <param name="metersPerPixel">Scale factor for converting pixels to meters</param>
    /// <returns>Distance field in meters (0 for road pixels, increasing outward)</returns>
    public static float[,] ComputeDistanceField(byte[,] mask, float metersPerPixel)
    {
        var h = mask.GetLength(0);
        var w = mask.GetLength(1);
        var dist = new float[h, w];
        const float INF = 1e12f;

        // Initialize: 0 for road pixels, INF for non-road
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            dist[y, x] = mask[y, x] > 0 ? 0f : INF;

        // 1D EDT per row
        ProcessRows(dist, w, h);

        // 1D EDT per column
        ProcessColumns(dist, w, h);

        // Convert squared pixel distance to meters
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            dist[y, x] = MathF.Sqrt(dist[y, x]) * metersPerPixel;

        return dist;
    }

    /// <summary>
    /// Process rows for 1D EDT (horizontal pass).
    /// </summary>
    private static void ProcessRows(float[,] dist, int w, int h)
    {
        var f = new float[w];
        var v = new int[w];
        var z = new float[w + 1];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) 
                f[x] = dist[y, x];

            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (var q = 1; q < w; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
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
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < w; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                dist[y, q] = (q - p) * (q - p) + f[p];
            }
        }
    }

    /// <summary>
    /// Process columns for 1D EDT (vertical pass).
    /// </summary>
    private static void ProcessColumns(float[,] dist, int w, int h)
    {
        var f = new float[h];
        var v = new int[h];
        var z = new float[h + 1];

        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++) 
                f[y] = dist[y, x];

            var k = 0;
            v[0] = 0;
            z[0] = float.NegativeInfinity;
            z[1] = float.PositiveInfinity;

            for (var q = 1; q < h; q++)
            {
                float s;
                while (true)
                {
                    var p = v[k];
                    s = (f[q] + q * q - (f[p] + p * p)) / (2f * (q - p));
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
                z[k + 1] = float.PositiveInfinity;
            }

            k = 0;
            for (var q = 0; q < h; q++)
            {
                while (z[k + 1] < q) k++;
                var p = v[k];
                dist[q, x] = (q - p) * (q - p) + f[p];
            }
        }
    }
}
