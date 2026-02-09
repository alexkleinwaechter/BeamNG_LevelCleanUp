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
    /// Fills a convex polygon (quad) using scanline rasterization.
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

        if (corners.Length < 3)
            return 0;

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

        var cornerCount = corners.Length;

        // Scanline fill using edge intersection
        for (var y = minY; y <= maxY; y++)
        {
            var scanY = y + 0.5f;

            // Find intersection points with polygon edges
            Span<float> intersections = stackalloc float[cornerCount];
            var intersectionCount = 0;

            for (var i = 0; i < cornerCount; i++)
            {
                var v1 = corners[i];
                var v2 = corners[(i + 1) % cornerCount];

                if ((v1.Y <= scanY && v2.Y > scanY) || (v2.Y <= scanY && v1.Y > scanY))
                {
                    var t = (scanY - v1.Y) / (v2.Y - v1.Y);
                    intersections[intersectionCount++] = v1.X + t * (v2.X - v1.X);
                }
            }

            // Sort intersections (simple insertion sort for small count)
            for (var i = 1; i < intersectionCount; i++)
            {
                var key = intersections[i];
                var j = i - 1;
                while (j >= 0 && intersections[j] > key)
                {
                    intersections[j + 1] = intersections[j];
                    j--;
                }
                intersections[j + 1] = key;
            }

            // Fill between pairs of intersections
            for (var i = 0; i + 1 < intersectionCount; i += 2)
            {
                var xStart = Math.Max(minX, (int)MathF.Floor(intersections[i]));
                var xEnd = Math.Min(maxX, (int)MathF.Ceiling(intersections[i + 1]));

                for (var x = xStart; x <= xEnd; x++)
                    if (!mask[y, x])
                    {
                        mask[y, x] = true;
                        filledCount++;
                    }
            }
        }

        return filledCount;
    }
}
