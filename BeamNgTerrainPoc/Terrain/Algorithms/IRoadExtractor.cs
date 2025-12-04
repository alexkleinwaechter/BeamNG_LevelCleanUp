using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Interface for extracting road geometry from layer images
/// </summary>
public interface IRoadExtractor
{
    /// <summary>
    /// Extracts road geometry (centerline and cross-sections) from a road layer image
    /// </summary>
    /// <param name="roadLayer">Binary road layer (255 = road, 0 = not road)</param>
    /// <param name="parameters">Road smoothing parameters</param>
    /// <param name="metersPerPixel">Scale factor for converting pixels to world coordinates</param>
    /// <returns>Road geometry with centerline and cross-sections</returns>
    RoadGeometry ExtractRoadGeometry(
        byte[,] roadLayer, 
        RoadSmoothingParameters parameters,
        float metersPerPixel);
}
