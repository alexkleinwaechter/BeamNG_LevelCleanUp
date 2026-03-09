using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Interface for calculating target elevations for road cross-sections
/// </summary>
public interface IHeightCalculator
{
    /// <summary>
    /// Calculates target elevations for all cross-sections in the road geometry.
    /// Legacy overload that wraps RoadGeometry — prefer the UnifiedCrossSection overload
    /// to avoid unnecessary object allocations.
    /// </summary>
    void CalculateTargetElevations(
        RoadGeometry geometry,
        float[,] heightMap,
        float metersPerPixel);

    /// <summary>
    /// Calculates target elevations for unified cross-sections directly,
    /// avoiding the RoadGeometry/CrossSection conversion roundtrip.
    /// Sets <see cref="UnifiedCrossSection.TargetElevation"/> on each cross-section in place.
    /// </summary>
    void CalculateTargetElevations(
        List<UnifiedCrossSection> crossSections,
        RoadSmoothingParameters parameters,
        float[,] heightMap,
        float metersPerPixel);

    /// <summary>
    /// Re-smooths elevations using existing TargetElevation values as input instead of sampling
    /// from the heightmap. Used in iterative junction refinement (WI-4) where the smoother
    /// operates on already-harmonized profiles to produce smoother results.
    /// </summary>
    void ReSmoothFromExistingElevations(
        List<UnifiedCrossSection> crossSections,
        RoadSmoothingParameters parameters);
}
