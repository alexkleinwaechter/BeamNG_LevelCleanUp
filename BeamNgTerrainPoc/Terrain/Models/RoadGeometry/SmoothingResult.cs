namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Result of applying road smoothing to a heightmap
/// </summary>
public class SmoothingResult
{
    /// <summary>
    /// The modified heightmap after road smoothing (2D array: [y, x])
    /// </summary>
    public float[,] ModifiedHeightMap { get; set; }
    
    /// <summary>
    /// Delta map showing height changes (new height - original height)
    /// Useful for debugging and visualization
    /// </summary>
    public float[,] DeltaMap { get; set; }
    
    /// <summary>
    /// Statistics about the smoothing operation
    /// </summary>
    public SmoothingStatistics Statistics { get; set; }
    
    /// <summary>
    /// The road geometry that was used for smoothing
    /// </summary>
    public RoadGeometry Geometry { get; set; }
    
    public SmoothingResult(
        float[,] modifiedHeightMap, 
        float[,] deltaMap, 
        SmoothingStatistics statistics,
        RoadGeometry geometry)
    {
        ModifiedHeightMap = modifiedHeightMap ?? throw new ArgumentNullException(nameof(modifiedHeightMap));
        DeltaMap = deltaMap ?? throw new ArgumentNullException(nameof(deltaMap));
        Statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
    }
}
