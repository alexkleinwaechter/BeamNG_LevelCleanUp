using System.Numerics;
using BeamNG.Procedural3D.Building.Roof;
using BeamNG.Procedural3D.Core;

namespace BeamNG.Procedural3D.Building;

/// <summary>
/// Thin facade for backwards compatibility. Delegates to modular roof classes:
///   - <see cref="GabledRoof"/> (port of OSM2World's GabledRoof.java)
///   - <see cref="HippedRoof"/> (port of OSM2World's HippedRoof.java)
///
/// New code should use the Roof/ classes directly via BuildingMeshGenerator.
/// </summary>
public static class RoofGenerator
{
    public static Mesh GenerateGabledRoof(BuildingData building, Vector2 textureScale)
    {
        var polygon = RoofWithRidge.PreparePolygon(building.FootprintOuter);
        if (polygon == null || polygon.Count < 3)
            return new Mesh();
        return new GabledRoof(building, polygon).GenerateMesh(textureScale);
    }

    public static Mesh GenerateHippedRoof(BuildingData building, Vector2 textureScale)
    {
        var polygon = RoofWithRidge.PreparePolygon(building.FootprintOuter);
        if (polygon == null || polygon.Count < 3)
            return new Mesh();
        return new HippedRoof(building, polygon).GenerateMesh(textureScale);
    }
}
