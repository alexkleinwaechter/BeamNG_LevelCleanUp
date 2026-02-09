using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Provides blend functions for smooth terrain transitions
/// </summary>
public static class BlendFunctions
{
    /// <summary>
    /// Linear interpolation
    /// </summary>
    public static float Linear(float t)
    {
        return t;
    }
    
    /// <summary>
    /// Cosine interpolation (recommended - smoothest)
    /// </summary>
    public static float Cosine(float t)
    {
        return (1.0f - MathF.Cos(t * MathF.PI)) / 2.0f;
    }
    
    /// <summary>
    /// Cubic Hermite interpolation (smooth)
    /// </summary>
    public static float Cubic(float t)
    {
        return t * t * (3.0f - 2.0f * t);
    }
    
    /// <summary>
    /// Quintic interpolation (extra smooth)
    /// </summary>
    public static float Quintic(float t)
    {
        return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
    }
    
    /// <summary>
    /// Applies the specified blend function type
    /// </summary>
    public static float Apply(float t, BlendFunctionType type)
    {
        // Clamp t to [0, 1]
        t = Math.Clamp(t, 0.0f, 1.0f);
        
        return type switch
        {
            BlendFunctionType.Linear => Linear(t),
            BlendFunctionType.Cosine => Cosine(t),
            BlendFunctionType.Cubic => Cubic(t),
            BlendFunctionType.Quintic => Quintic(t),
            _ => Linear(t)
        };
    }
}
