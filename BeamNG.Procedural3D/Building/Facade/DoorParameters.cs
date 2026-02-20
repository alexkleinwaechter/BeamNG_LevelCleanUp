namespace BeamNG.Procedural3D.Building.Facade;

/// <summary>
/// Parametric description of a door. Immutable after construction.
/// Strict port of OSM2World's DoorParameters.java.
///
/// Door types:
/// - "hinged" (default): standard door, 1.0m × 2.0m (double: 2.0m wide)
/// - "overhead": garage door, 2.5m × 2.125m
/// - "no": invisible opening (renders as void)
/// </summary>
public class DoorParameters
{
    /// <summary>
    /// Door type: "hinged", "overhead", "no".
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Door width in meters.
    /// </summary>
    public float Width { get; }

    /// <summary>
    /// Door height in meters.
    /// </summary>
    public float Height { get; }

    /// <summary>
    /// How far the door is recessed into the wall (in meters).
    /// </summary>
    public float Inset { get; }

    /// <summary>
    /// Number of door wings (1 = single, 2 = double hinged).
    /// </summary>
    public int NumberOfWings { get; }

    /// <summary>
    /// Material identifier for the door surface.
    /// </summary>
    public string Material { get; init; }

    private DoorParameters(string type, float width, float height, float inset, int numberOfWings, string material)
    {
        Type = type;
        Width = width;
        Height = height;
        Inset = inset;
        NumberOfWings = numberOfWings;
        Material = material;
    }

    /// <summary>
    /// Creates door parameters based on building type.
    /// Port of DoorParameters.fromTags(TagSet tags, TagSet parentTags).
    /// </summary>
    public static DoorParameters FromBuildingType(string? buildingType, int numberOfWings = 1)
    {
        // Garage buildings get overhead doors by default
        if (buildingType is "garage" or "garages")
        {
            return new DoorParameters("overhead", 2.5f, 2.125f, 0.1f, 1, "DOOR_GARAGE");
        }

        float defaultWidth = 1.0f;
        if (numberOfWings == 2) defaultWidth = 2.0f;

        return new DoorParameters("hinged", defaultWidth, 2.0f, 0.1f, numberOfWings, "DOOR_DEFAULT");
    }

    /// <summary>
    /// Creates default hinged door parameters.
    /// </summary>
    public static DoorParameters DefaultHinged()
    {
        return new DoorParameters("hinged", 1.0f, 2.0f, 0.1f, 1, "DOOR_DEFAULT");
    }

    /// <summary>
    /// Creates garage (overhead) door parameters.
    /// </summary>
    public static DoorParameters Overhead()
    {
        return new DoorParameters("overhead", 2.5f, 2.125f, 0.1f, 1, "DOOR_GARAGE");
    }

    /// <summary>
    /// Returns a copy with a different inset distance.
    /// Port of DoorParameters.withInset(double inset) in Java.
    /// </summary>
    public DoorParameters WithInset(float inset)
    {
        return new DoorParameters(Type, Width, Height, inset, NumberOfWings, Material);
    }
}
