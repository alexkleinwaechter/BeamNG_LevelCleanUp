namespace BeamNG.Procedural3D.Building;

/// <summary>
/// Default properties for particular building types.
/// Ported from OSM2World BuildingDefaults.java.
/// Maps building=* tag values to default level count, materials, roof shape, etc.
/// </summary>
public static class BuildingDefaults
{
    /// <summary>
    /// Gets default building properties based on the building type tag value.
    /// </summary>
    /// <param name="buildingType">The value of the building=* or building:part=* tag.</param>
    /// <param name="roofShapeTag">Optional explicit roof:shape tag value (overrides default).</param>
    /// <returns>Default values for this building type.</returns>
    public static BuildingDefaultValues GetDefaultsFor(string buildingType, string? roofShapeTag = null)
    {
        int levels = 3;
        float heightPerLevel = 2.5f;
        string wallMaterial = "BUILDING_DEFAULT";
        string roofMaterial = "ROOF_DEFAULT";
        bool hasWindows = true;
        bool hasWalls = true;
        string roofShape = "flat";

        switch (buildingType.ToLowerInvariant())
        {
            case "greenhouse":
                levels = 1;
                wallMaterial = "GLASS_WALL";
                roofMaterial = "GLASS_ROOF";
                hasWindows = false;
                break;

            case "garage":
            case "garages":
                levels = 1;
                wallMaterial = "CONCRETE";
                roofMaterial = "CONCRETE";
                hasWindows = false;
                break;

            case "carport":
                levels = 1;
                wallMaterial = "CONCRETE";
                roofMaterial = "CONCRETE";
                hasWindows = false;
                hasWalls = false;
                break;

            case "hut":
            case "shed":
                levels = 1;
                roofShape = "gabled";
                break;

            case "cabin":
                levels = 1;
                wallMaterial = "WOOD_WALL";
                roofMaterial = "WOOD_ROOF";
                roofShape = "gabled";
                break;

            case "roof":
                levels = 1;
                hasWindows = false;
                hasWalls = false;
                break;

            case "church":
                hasWindows = false;
                roofShape = "gabled";
                break;

            case "hangar":
            case "industrial":
                hasWindows = false;
                break;

            case "residential":
            case "house":
            case "detached":
            case "semidetached_house":
            case "terrace":
                levels = 2;
                roofShape = "gabled";
                break;

            case "apartments":
            case "dormitory":
                levels = 4;
                break;

            case "commercial":
            case "office":
            case "retail":
                levels = 3;
                break;

            case "warehouse":
                levels = 1;
                wallMaterial = "CONCRETE";
                hasWindows = false;
                break;

            case "farm_auxiliary":
            case "barn":
                levels = 1;
                wallMaterial = "WOOD_WALL";
                hasWindows = false;
                roofShape = "gabled";
                break;

            case "school":
            case "university":
            case "hospital":
                levels = 3;
                roofShape = "hipped";
                break;

            case "parking":
                levels = 5;
                hasWindows = false;
                break;

            case "chimney":
                levels = 1;
                heightPerLevel = 10.0f;
                wallMaterial = "BRICK";
                roofMaterial = "BRICK";
                hasWindows = false;
                roofShape = "flat";
                break;
        }

        // Use explicit roof shape tag if provided
        if (!string.IsNullOrEmpty(roofShapeTag))
        {
            roofShape = roofShapeTag;
        }

        return new BuildingDefaultValues(
            levels, heightPerLevel, roofShape,
            wallMaterial, roofMaterial,
            hasWindows, hasWalls);
    }
}

/// <summary>
/// Default properties for a building type. Immutable.
/// </summary>
public record BuildingDefaultValues(
    int Levels,
    float HeightPerLevel,
    string RoofShape,
    string WallMaterial,
    string RoofMaterial,
    bool HasWindows,
    bool HasWalls);
