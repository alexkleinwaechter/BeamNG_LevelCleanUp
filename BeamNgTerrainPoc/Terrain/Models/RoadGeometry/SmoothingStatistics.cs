namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Statistics about the road smoothing operation
/// </summary>
public class SmoothingStatistics
{
    /// <summary>
    /// Maximum road slope found in degrees (should be <= RoadMaxSlopeDegrees)
    /// </summary>
    public float MaxRoadSlope { get; set; }
    
    /// <summary>
    /// Maximum side slope found in degrees (should be <= SideMaxSlopeDegrees)
    /// </summary>
    public float MaxSideSlope { get; set; }
    
    /// <summary>
    /// Maximum transverse slope (side-to-side) in degrees (should be ~0°)
    /// </summary>
    public float MaxTransverseSlope { get; set; }
    
    /// <summary>
    /// Maximum height discontinuity between adjacent pixels in world units
    /// </summary>
    public float MaxDiscontinuity { get; set; }
    
    /// <summary>
    /// Total volume of terrain removed (cut) in cubic meters
    /// </summary>
    public float TotalCutVolume { get; set; }
    
    /// <summary>
    /// Total volume of terrain added (fill) in cubic meters
    /// </summary>
    public float TotalFillVolume { get; set; }
    
    /// <summary>
    /// Number of pixels modified in the heightmap
    /// </summary>
    public int PixelsModified { get; set; }
    
    /// <summary>
    /// Whether all constraints were met (slopes within limits, etc.)
    /// </summary>
    public bool MeetsAllConstraints { get; set; }
    
    /// <summary>
    /// List of constraint violations (if any)
    /// </summary>
    public List<string> ConstraintViolations { get; set; } = new();
}
