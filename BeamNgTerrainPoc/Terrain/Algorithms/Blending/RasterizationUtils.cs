namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Utility methods for rasterization operations used in terrain blending.
/// </summary>
public static class RasterizationUtils
{
    /// <summary>
    /// Bresenham line rasterization with bounds checking.
    /// Draws a line from (x0, y0) to (x1, y1) on the mask, setting pixels to 255.
    /// </summary>
    /// <param name="mask">The mask to draw on</param>
    /// <param name="x0">Start X coordinate</param>
    /// <param name="y0">Start Y coordinate</param>
    /// <param name="x1">End X coordinate</param>
    /// <param name="y1">End Y coordinate</param>
    /// <param name="width">Image width for bounds checking</param>
    /// <param name="height">Image height for bounds checking</param>
    public static void DrawLine(byte[,] mask, int x0, int y0, int x1, int y1, int width, int height)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                mask[y0, x0] = 255;

            if (x0 == x1 && y0 == y1) 
                break;
            
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }
}
