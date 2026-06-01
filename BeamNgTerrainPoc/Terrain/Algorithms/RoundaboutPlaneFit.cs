using System;
using System.Collections.Generic;
using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Least-squares fit of a plane z = a·x + b·y + c to a set of (x,y,z) points, with the plane's tilt
///     magnitude clamped to a maximum (the roundabout max Querneigung, civil limit 6%). Pure — no side
///     effects. Used to make a roundabout ring follow terrain as a single drivable tilted disk instead of
///     a forced-uniform horizontal disk, minimizing cut/fill.
/// </summary>
public static class RoundaboutPlaneFit
{
    public static float Evaluate(float a, float b, float c, Vector2 xy) => a * xy.X + b * xy.Y + c;

    /// <summary>
    ///     Fits z = a·x + b·y + c by least squares, then clamps tilt = sqrt(a²+b²) to <paramref name="maxTilt" />
    ///     (scaling a,b about the centroid so the plane still passes through (x̄,ȳ,z̄) → balanced cut/fill).
    ///     Returns the coefficients and the PRE-clamp tilt (for diagnostics). Degenerate/rank-deficient input
    ///     falls back to a flat plane at the mean z.
    /// </summary>
    public static (float A, float B, float C, float PreClampTilt) FitClamped(
        IReadOnlyList<(Vector2 Xy, float Z)> points, float maxTilt)
    {
        var n = points.Count;
        if (n == 0) return (0f, 0f, 0f, 0f);

        double sx = 0, sy = 0, sz = 0, sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0;
        foreach (var (xy, z) in points)
        {
            double x = xy.X, y = xy.Y;
            sx += x; sy += y; sz += z;
            sxx += x * x; sxy += x * y; syy += y * y;
            sxz += x * z; syz += y * z;
        }

        var meanZ = (float)(sz / n);
        var meanX = (float)(sx / n);
        var meanY = (float)(sy / n);

        // Solve the 3×3 normal equations via Cramer's rule (centered to improve conditioning).
        // Use the covariance form: subtract the means so the system is [Cxx Cxy; Cxy Cyy][a;b] = [Cxz; Cyz].
        double cxx = sxx - sx * sx / n;
        double cxy = sxy - sx * sy / n;
        double cyy = syy - sy * sy / n;
        double cxz = sxz - sx * sz / n;
        double cyz = syz - sy * sz / n;

        var det = cxx * cyy - cxy * cxy;
        float a, b;
        if (System.Math.Abs(det) < 1e-9)
        {
            a = 0f; b = 0f; // rank-deficient (collinear / coincident points) → flat
        }
        else
        {
            a = (float)((cxz * cyy - cyz * cxy) / det);
            b = (float)((cyz * cxx - cxz * cxy) / det);
        }

        var preTilt = MathF.Sqrt(a * a + b * b);
        if (preTilt > maxTilt && preTilt > 1e-9f)
        {
            var scale = maxTilt / preTilt;
            a *= scale;
            b *= scale;
        }

        // Plane passes through the centroid (x̄,ȳ,z̄): c = z̄ − a·x̄ − b·ȳ.
        var c = meanZ - a * meanX - b * meanY;
        return (a, b, c, preTilt);
    }
}
