using System.Numerics;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Type of elevated/underground structure.
/// </summary>
public enum StructureType
{
    /// <summary>Way passes over an obstacle (water, road, valley, etc.).</summary>
    Bridge,

    /// <summary>Way passes through terrain (underground).</summary>
    Tunnel,

    /// <summary>Covered passage through a building.</summary>
    BuildingPassage,

    /// <summary>Small tunnel for water drainage under road.</summary>
    Culvert
}

/// <summary>
/// Represents a bridge or tunnel segment from OSM.
/// </summary>
public class OsmBridgeTunnel
{
    /// <summary>OSM way ID.</summary>
    public long WayId { get; set; }

    /// <summary>Type of structure (bridge, tunnel, etc.).</summary>
    public StructureType StructureType { get; set; }

    /// <summary>
    /// The geometry of the structure as geographic coordinates.
    /// These are the actual OSM way coordinates.
    /// </summary>
    public List<GeoCoordinate> Coordinates { get; set; } = [];

    /// <summary>
    /// World positions in meters (after coordinate transformation).
    /// Set during processing when geo coordinates are converted to local terrain coordinates.
    /// </summary>
    public List<Vector2> PositionsMeters { get; set; } = [];

    /// <summary>
    /// Vertical layer (default 0, positive for elevated, negative for underground).
    /// </summary>
    public int Layer { get; set; } = 0;

    /// <summary>Highway type (e.g., "primary", "secondary", "motorway").</summary>
    public string? HighwayType { get; set; }

    /// <summary>Name of the bridge/tunnel (from name or bridge:name/tunnel:name tag).</summary>
    public string? Name { get; set; }

    /// <summary>Bridge structure type (beam, arch, suspension, etc.).</summary>
    public string? BridgeStructure { get; set; }

    /// <summary>Original OSM tags for additional processing.</summary>
    public Dictionary<string, string> Tags { get; set; } = new();

    /// <summary>
    /// Approximate length of the structure in meters.
    /// Calculated from coordinates after projection.
    /// </summary>
    public float LengthMeters { get; set; }

    /// <summary>
    /// Road width in meters - only used as fallback if no spline match.
    /// In normal flow, width comes from the matched spline's RoadSmoothingParameters.
    /// </summary>
    public float WidthMeters { get; set; }

    /// <summary>
    /// Gets the effective width for this structure.
    /// Uses the matched spline's user-defined width for seamless road continuity.
    /// </summary>
    /// <param name="matchedSpline">The road spline that this structure is part of (if matched).</param>
    /// <returns>The width in meters to use for this structure.</returns>
    public float GetEffectiveWidth(ParameterizedRoadSpline? matchedSpline)
    {
        // Primary: Use the user-defined road width from the material parameters
        if (matchedSpline != null)
        {
            return matchedSpline.Parameters.RoadWidthMeters;
        }

        // Fallback only if no spline match (shouldn't happen in normal flow)
        return WidthMeters > 0 ? WidthMeters : 6.0f;
    }

    /// <summary>
    /// Whether this is a bridge structure (Bridge type).
    /// </summary>
    public bool IsBridge => StructureType == StructureType.Bridge;

    /// <summary>
    /// Whether this is a tunnel structure (Tunnel or BuildingPassage type).
    /// </summary>
    public bool IsTunnel => StructureType == StructureType.Tunnel ||
                            StructureType == StructureType.BuildingPassage;

    /// <summary>
    /// Whether this is a culvert (small drainage tunnel).
    /// </summary>
    public bool IsCulvert => StructureType == StructureType.Culvert;

    /// <summary>
    /// Gets a human-readable display name for this structure.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Name))
                return Name;
            return $"{StructureType} (Way {WayId})";
        }
    }

    public override string ToString()
    {
        var lengthInfo = LengthMeters > 0 ? $", {LengthMeters:F1}m" : "";
        var layerInfo = Layer != 0 ? $", layer={Layer}" : "";
        return $"OsmBridgeTunnel[{StructureType}] Way {WayId}: {DisplayName}{lengthInfo}{layerInfo}";
    }
}
