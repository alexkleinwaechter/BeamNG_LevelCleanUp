using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Represents a cross-section perpendicular to the road centerline
/// </summary>
public class CrossSection
{
    /// <summary>
    /// World coordinates of the cross-section center point (on the road centerline)
    /// </summary>
    public Vector2 CenterPoint { get; set; }
    
    /// <summary>
    /// Unit vector perpendicular to the road direction (points to the right)
    /// </summary>
    public Vector2 NormalDirection { get; set; }
    
    /// <summary>
    /// Unit vector along the road direction (tangent)
    /// </summary>
    public Vector2 TangentDirection { get; set; }
    
    /// <summary>
    /// Calculated target elevation for this cross-section in world units
    /// </summary>
    public float TargetElevation { get; set; }
    
    /// <summary>
    /// Road width at this point in meters
    /// </summary>
    public float WidthMeters { get; set; }
    
    /// <summary>
    /// Whether this cross-section is in an exclusion zone (e.g., over water)
    /// If true, no smoothing is applied at this location
    /// </summary>
    public bool IsExcluded { get; set; }
    
    /// <summary>
    /// Global index of this cross-section across all paths
    /// </summary>
    public int Index { get; set; }
    
    /// <summary>
    /// Identifier for the path this cross-section belongs to
    /// </summary>
    public int PathId { get; set; }
    
    /// <summary>
    /// Index within its own path (monotonic along the path)
    /// </summary>
    public int LocalIndex { get; set; }
}
