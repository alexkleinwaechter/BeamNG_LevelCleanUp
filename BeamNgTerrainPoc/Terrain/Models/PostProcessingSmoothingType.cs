namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Type of post-processing smoothing filter to apply to eliminate staircase artifacts.
/// </summary>
public enum PostProcessingSmoothingType
{
    /// <summary>
    /// Gaussian blur - Best quality, smooth transitions.
    /// Uses normal distribution kernel for natural-looking smoothing.
    /// Slower than box filter but produces better results.
    /// Recommended for racing games where road smoothness is critical.
    /// </summary>
    Gaussian,

    /// <summary>
    /// Box blur (averaging) - Fast, simple smoothing.
    /// Each pixel is averaged with neighbors in a square window.
    /// Faster than Gaussian but can produce slightly blocky results.
    /// Good for large terrains where performance is important.
    /// </summary>
    Box,

    /// <summary>
    /// Bilateral filter - Edge-preserving smoothing.
    /// Smooths while preserving sharp transitions at road edges.
    /// Slower than Gaussian but prevents edge bleeding.
    /// Best for roads with sharp elevation changes at edges.
    /// </summary>
    Bilateral
}
