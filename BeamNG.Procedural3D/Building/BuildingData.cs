namespace BeamNG.Procedural3D.Building;

using System.Drawing;
using System.Numerics;

/// <summary>
/// Parsed representation of a building from OSM data.
/// Footprint coordinates are in local 2D space (meters, origin at building centroid).
/// Height values are in meters.
/// </summary>
public class BuildingData
{
    /// <summary>
    /// The OSM element ID for this building.
    /// </summary>
    public long OsmId { get; set; }

    /// <summary>
    /// The building type from the building=* tag (e.g., "residential", "commercial", "yes").
    /// </summary>
    public string BuildingType { get; set; } = "yes";

    /// <summary>
    /// Outer ring of the building footprint polygon in local coordinates (meters, origin at centroid).
    /// Counter-clockwise winding order.
    /// </summary>
    public List<Vector2> FootprintOuter { get; set; } = new();

    /// <summary>
    /// Optional inner rings (holes/courtyards) in clockwise winding order.
    /// </summary>
    public List<List<Vector2>>? FootprintHoles { get; set; }

    /// <summary>
    /// Total building height in meters (including roof).
    /// </summary>
    public float Height { get; set; } = 7.5f;

    /// <summary>
    /// Minimum height above ground (for floating building parts like building:part with min_height).
    /// </summary>
    public float MinHeight { get; set; }

    /// <summary>
    /// Number of above-ground levels (floors).
    /// </summary>
    public int Levels { get; set; } = 3;

    /// <summary>
    /// Height per level in meters.
    /// </summary>
    public float HeightPerLevel { get; set; } = 2.5f;

    /// <summary>
    /// Roof shape identifier (e.g., "flat", "gabled", "hipped", "pyramidal").
    /// </summary>
    public string RoofShape { get; set; } = "flat";

    /// <summary>
    /// Roof height in meters (the portion of total Height that is roof).
    /// </summary>
    public float RoofHeight { get; set; }

    /// <summary>
    /// Wall material identifier (maps to BuildingMaterialLibrary).
    /// </summary>
    public string WallMaterial { get; set; } = "BUILDING_DEFAULT";

    /// <summary>
    /// Roof material identifier (maps to BuildingMaterialLibrary).
    /// </summary>
    public string RoofMaterial { get; set; } = "ROOF_DEFAULT";

    /// <summary>
    /// Optional wall color override from building:colour tag.
    /// </summary>
    public Color? WallColor { get; set; }

    /// <summary>
    /// Optional roof color override from roof:colour tag.
    /// </summary>
    public Color? RoofColor { get; set; }

    /// <summary>
    /// Whether this building should have window textures on walls.
    /// </summary>
    public bool HasWindows { get; set; } = true;

    /// <summary>
    /// Whether this building has walls (false for carports, roof-only structures).
    /// </summary>
    public bool HasWalls { get; set; } = true;

    /// <summary>
    /// Ground elevation at the building position (Z coordinate in BeamNG world space).
    /// Set during coordinate transformation from terrain heightmap data.
    /// </summary>
    public float GroundElevation { get; set; }

    /// <summary>
    /// World position of the building centroid (BeamNG world coordinates).
    /// Used as TSStatic position. Building geometry is relative to this point.
    /// </summary>
    public Vector3 WorldPosition { get; set; }

    /// <summary>
    /// Height of walls only (total height minus roof height).
    /// </summary>
    public float WallHeight => Math.Max(0, Height - RoofHeight);

    /// <summary>
    /// Effective base height of the roof (MinHeight + WallHeight).
    /// </summary>
    public float RoofBaseHeight => MinHeight + WallHeight;
}
