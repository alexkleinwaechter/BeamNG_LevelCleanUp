namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Parameters specific to DirectMask road smoothing approach.
/// This approach directly uses the road mask for grid-aligned sampling.
/// Best for: Complex road networks, intersections, robust general-purpose smoothing.
/// </summary>
public class DirectMaskRoadParameters
{
    /// <summary>
    /// Window size for elevation smoothing (number of samples in moving average).
    /// Larger values create smoother elevation transitions but may lose detail.
    /// Recommend: 10-30 for balance between smoothness and performance.
    /// Default: 10
    /// </summary>
    public int SmoothingWindowSize { get; set; } = 10;
    
    /// <summary>
    /// Search radius (in pixels) for finding nearby road pixels when sampling elevation.
    /// Larger radius = more robust to gaps in road mask but slower processing.
    /// Default: 3
    /// </summary>
    public int RoadPixelSearchRadius { get; set; } = 3;
    
    /// <summary>
    /// Use Butterworth low-pass filter instead of simple moving average for elevation smoothing.
    /// Butterworth provides smoother results with maximally flat passband.
    /// Default: true
    /// </summary>
    public bool UseButterworthFilter { get; set; } = true;
    
    /// <summary>
    /// Butterworth filter order (higher = sharper cutoff, flatter passband).
    /// Range: 1-8
    /// 1-2 = gentle smoothing
    /// 3-4 = aggressive smoothing (recommended)
    /// 5-6 = maximum flatness
    /// Default: 3
    /// </summary>
    public int ButterworthFilterOrder { get; set; } = 3;
    
    /// <summary>
    /// Validates the DirectMask-specific parameters.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        
        if (SmoothingWindowSize < 1)
            errors.Add("SmoothingWindowSize must be at least 1");
        
        if (RoadPixelSearchRadius < 1)
            errors.Add("RoadPixelSearchRadius must be at least 1");
        
        if (ButterworthFilterOrder < 1 || ButterworthFilterOrder > 8)
            errors.Add("ButterworthFilterOrder must be between 1 and 8");
            
        return errors;
    }
}
