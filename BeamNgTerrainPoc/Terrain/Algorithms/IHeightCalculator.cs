using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Interface for calculating target elevations for road cross-sections
/// </summary>
public interface IHeightCalculator
{
    /// <summary>
    /// Calculates target elevations for all cross-sections in the road geometry
    /// </summary>
    void CalculateTargetElevations(
        RoadGeometry geometry, 
        float[,] heightMap,
        float metersPerPixel);
}
