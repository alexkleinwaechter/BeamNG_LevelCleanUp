using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Contains the geometric representation of a road extracted from a layer map
/// </summary>
public class RoadGeometry
{
    /// <summary>
    /// Ordered list of points defining the road centerline in world coordinates
    /// </summary>
    public List<Vector2> Centerline { get; set; } = new();
    
    /// <summary>
    /// Smooth spline through centerline points (for spline-based approach)
    /// </summary>
    public RoadSpline? Spline { get; set; }
    
    /// <summary>
    /// Cross-sections generated along the road at regular intervals
    /// </summary>
    public List<CrossSection> CrossSections { get; set; } = new();
    
    /// <summary>
    /// Binary mask of road pixels (255 = road, 0 = not road)
    /// </summary>
    public byte[,] RoadMask { get; set; }
    
    /// <summary>
    /// Road smoothing parameters associated with this geometry
    /// </summary>
    public RoadSmoothingParameters Parameters { get; set; }
    
    /// <summary>
    /// Width of the road mask in pixels
    /// </summary>
    public int Width => RoadMask.GetLength(1);
    
    /// <summary>
    /// Height of the road mask in pixels
    /// </summary>
    public int Height => RoadMask.GetLength(0);
    
    public RoadGeometry(byte[,] roadMask, RoadSmoothingParameters parameters)
    {
        RoadMask = roadMask ?? throw new ArgumentNullException(nameof(roadMask));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }
}
