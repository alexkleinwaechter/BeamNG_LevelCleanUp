namespace BeamNgTerrainPoc.Terrain.Models;

/// <summary>
/// Type of blend function to use for terrain transitions around roads
/// </summary>
public enum BlendFunctionType
{
    /// <summary>
    /// Linear interpolation (simple, less smooth)
    /// </summary>
    Linear,
    
    /// <summary>
    /// Cosine interpolation (recommended - smoothest)
    /// </summary>
    Cosine,
    
    /// <summary>
    /// Cubic Hermite interpolation (smooth)
    /// </summary>
    Cubic,
    
    /// <summary>
    /// Quintic interpolation (extra smooth)
    /// </summary>
    Quintic
}
