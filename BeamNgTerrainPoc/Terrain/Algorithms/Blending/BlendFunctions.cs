using BeamNgTerrainPoc.Terrain.Models;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Provides blend function implementations for terrain-to-road transitions.
/// </summary>
public static class BlendFunctions
{
    /// <summary>
    /// Applies the configured blend function to a transition parameter.
    /// </summary>
    /// <param name="t">Transition parameter in range [0, 1]. 0 = road edge, 1 = blend zone edge</param>
    /// <param name="blendType">Type of blend function to apply</param>
    /// <returns>Blended value in range [0, 1]</returns>
    public static float Apply(float t, BlendFunctionType blendType)
    {
        return blendType switch
        {
            BlendFunctionType.Cosine => ApplyCosine(t),
            BlendFunctionType.Cubic => ApplyCubic(t),
            BlendFunctionType.Quintic => ApplyQuintic(t),
            _ => t // Linear
        };
    }

    /// <summary>
    /// Linear blend function.
    /// f(t) = t
    /// Simple but may have visible transition points.
    /// </summary>
    public static float ApplyLinear(float t) => t;

    /// <summary>
    /// Cosine blend function.
    /// f(t) = 0.5 - 0.5 * cos(? * t)
    /// Smooth S-curve, good balance of smoothness and performance.
    /// </summary>
    public static float ApplyCosine(float t) => 0.5f - 0.5f * MathF.Cos(MathF.PI * t);

    /// <summary>
    /// Cubic Hermite blend function (smoothstep).
    /// f(t) = t² * (3 - 2t)
    /// Very smooth with zero first derivative at endpoints.
    /// </summary>
    public static float ApplyCubic(float t) => t * t * (3f - 2f * t);

    /// <summary>
    /// Quintic blend function (smootherstep).
    /// f(t) = t³ * (t * (6t - 15) + 10)
    /// Extremely smooth with zero first and second derivatives at endpoints.
    /// Best quality but slightly more computation.
    /// </summary>
    public static float ApplyQuintic(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
}
