namespace BeamNG.Procedural3D.Building;

using System.Drawing;
using System.Numerics;

/// <summary>
/// A building composed of one or more parts.
/// Port of OSM2World's Building.java — container for BuildingPart objects.
///
/// When the building has explicit building:part features in OSM that cover its footprint,
/// each part gets its own polygon, roof shape, height, and materials.
/// Otherwise, the building itself is treated as a single part (fallback behavior).
///
/// All part footprints share the same local coordinate system (origin at the building centroid).
/// The building's <see cref="WorldPosition"/> is the TSStatic placement position in BeamNG.
/// </summary>
public class Building
{
    /// <summary>
    /// The OSM element ID for the main building outline.
    /// </summary>
    public long OsmId { get; set; }

    /// <summary>
    /// The building type from the building=* tag (e.g., "residential", "commercial", "yes").
    /// </summary>
    public string BuildingType { get; set; } = "yes";

    /// <summary>
    /// World position of the building centroid (BeamNG world coordinates).
    /// Used as TSStatic position. All part geometry is relative to this point.
    /// </summary>
    public Vector3 WorldPosition { get; set; }

    /// <summary>
    /// Ground elevation at the building position (Z coordinate in BeamNG world space).
    /// Set from terrain heightmap sampling.
    /// </summary>
    public float GroundElevation { get; set; }

    /// <summary>
    /// The parts of this building. Each part has its own polygon, roof, and materials.
    /// For simple buildings without building:part features, this contains a single part
    /// representing the entire building outline.
    /// </summary>
    public List<BuildingData> Parts { get; set; } = new();

    /// <summary>
    /// The building's outline polygon in local coordinates (meters, origin at centroid).
    /// Used during parsing for point-in-polygon tests when discovering building:part features.
    /// May be null after parsing is complete.
    /// </summary>
    public List<Vector2>? OutlinePolygon { get; set; }

    /// <summary>
    /// DAE filename for this building (without directory path).
    /// </summary>
    public string DaeFileName => $"building_{OsmId}.dae";

    /// <summary>
    /// Scene object name for the TSStatic entry.
    /// </summary>
    public string SceneName => $"building_{BuildingType}_{OsmId}";

    /// <summary>
    /// The primary wall color from the first part that has a wall color set.
    /// Used by BuildingSceneWriter for instanceColor on non-clustered TSStatic entries.
    /// </summary>
    public Color? PrimaryWallColor => Parts.FirstOrDefault(p => p.WallColor.HasValue)?.WallColor;
}
