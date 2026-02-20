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
    /// Roof direction angle in degrees (compass bearing, 0=north, 90=east).
    /// From the OSM roof:direction tag. Indicates the direction the roof slope faces (downhill).
    /// Null means auto-detect from building shape (longest edge = ridge direction).
    /// </summary>
    public float? RoofDirection { get; set; }

    /// <summary>
    /// Roof slope angle in degrees (from horizontal).
    /// From the OSM roof:angle tag. Used to compute roof height if roof:height is not set.
    /// </summary>
    public float? RoofAngle { get; set; }

    /// <summary>
    /// Roof orientation relative to building shape: "along" (default) or "across".
    /// "along" = ridge runs parallel to longest side, "across" = perpendicular.
    /// </summary>
    public string RoofOrientation { get; set; } = "along";

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
    /// Building passages (tunnel=building_passage) that cross through this building.
    /// Each entry represents one wall opening where a road/path enters or exits.
    /// Port of OSM2World BuildingPart.java lines 152-267.
    /// </summary>
    public List<PassageInfo> Passages { get; set; } = new();

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
    /// Effective base height of the roof above ground (= Height - RoofHeight).
    /// This is the eave elevation measured from ground level, NOT from MinHeight.
    /// Port of Java: building.getGroundLevelEle() + levelStructure.heightWithoutRoof()
    /// where groundLevelEle = 0 in local coordinates.
    /// </summary>
    public float RoofBaseHeight => WallHeight;
}

/// <summary>
/// Information about a building passage opening on one wall.
/// Each passage road crossing creates two PassageInfo entries (one per wall intersection).
/// Port of OSM2World BuildingPart.java passage detection (lines 152-267).
/// </summary>
/// <param name="Position">Position of the passage center on the building footprint, in local coordinates
/// (meters, origin at building centroid). This point lies on a wall segment.</param>
/// <param name="Width">Width of the passage opening in meters (from road width or default).</param>
/// <param name="Height">Clearance height of the passage in meters (default 2.5m).</param>
public record PassageInfo(Vector2 Position, float Width, float Height = 2.5f);
