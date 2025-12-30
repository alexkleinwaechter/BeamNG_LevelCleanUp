using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Utility methods for polygon operations used in terrain blending.
/// </summary>
public static class PolygonUtils
{
    /// <summary>
    /// Tests if a point is inside a convex polygon using cross product method.
    /// All cross products should have the same sign if the point is inside.
    /// </summary>
    /// <param name="point">The point to test</param>
    /// <param name="polygon">Array of polygon vertices in order</param>
    /// <returns>True if point is inside the convex polygon</returns>
    public static bool IsPointInConvexPolygon(Vector2 point, Vector2[] polygon)
    {
        var n = polygon.Length;
        var sign = 0;

        for (var i = 0; i < n; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % n];

            // Cross product of (b - a) and (point - a)
            var cross = (b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X);

            if (cross != 0)
            {
                var currentSign = cross > 0 ? 1 : -1;
                if (sign == 0)
                    sign = currentSign;
                else if (sign != currentSign)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Fills a convex polygon (quad) using scanline algorithm.
    /// Returns the number of pixels filled.
    /// </summary>
    /// <param name="mask">The mask to fill</param>
    /// <param name="corners">Polygon corners</param>
    /// <param name="width">Image width</param>
    /// <param name="height">Image height</param>
    /// <returns>Number of pixels filled</returns>
    public static int FillConvexPolygon(bool[,] mask, Vector2[] corners, int width, int height)
    {
        var filledCount = 0;

        // Find bounding box
        var minY = (int)MathF.Floor(corners.Min(c => c.Y));
        var maxY = (int)MathF.Ceiling(corners.Max(c => c.Y));
        var minX = (int)MathF.Floor(corners.Min(c => c.X));
        var maxX = (int)MathF.Ceiling(corners.Max(c => c.X));

        // Clamp to image bounds
        minY = Math.Max(0, minY);
        maxY = Math.Min(height - 1, maxY);
        minX = Math.Max(0, minX);
        maxX = Math.Min(width - 1, maxX);

        // Scanline fill using point-in-polygon test
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
            if (!mask[y, x] && IsPointInConvexPolygon(new Vector2(x, y), corners))
            {
                mask[y, x] = true;
                filledCount++;
            }

        return filledCount;
    }
}
