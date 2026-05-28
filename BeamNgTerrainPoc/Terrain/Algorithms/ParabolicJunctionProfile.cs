namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase A parabolic vertical-curve helper. Replaces the legacy h00-weighted
///     additive delta in BlendSplineProfile for single-end blend-zone samples.
///     The parabola anchors at the junction (elevation + slope) and meets the
///     natural spline elevation at the far end of the blend zone. Mathematically
///     guaranteed not to overshoot beyond [min(zJunction, zNaturalAtL),
///     max(zJunction, zNaturalAtL)] when mJunction = 0; small overshoots are
///     possible for non-zero mJunction but bounded by mJunction·L/4.
/// </summary>
public static class ParabolicJunctionProfile
{
    /// <summary>
    ///     Samples the parabolic profile z(d) = a·d² + mJunction·d + zJunction,
    ///     where a is chosen so z(blendLength) = zNaturalAtL.
    /// </summary>
    /// <param name="d">Distance from junction (m), in [0, blendLength].</param>
    /// <param name="blendLength">Blend zone length L (m).</param>
    /// <param name="zJunction">Anchor elevation at d=0.</param>
    /// <param name="mJunction">Anchor slope at d=0 (dz/dd, dimensionless).</param>
    /// <param name="zNaturalAtL">Natural profile elevation at d=blendLength.</param>
    public static float Sample(
        float d, float blendLength,
        float zJunction, float mJunction, float zNaturalAtL)
    {
        if (blendLength <= 0.0001f)
            return zJunction;

        // Clamp d to [0, L] to avoid quadratic extrapolation blowups.
        var dClamped = MathF.Max(0f, MathF.Min(d, blendLength));

        var a = (zNaturalAtL - zJunction - mJunction * blendLength)
                / (blendLength * blendLength);

        return a * dClamped * dClamped + mJunction * dClamped + zJunction;
    }
}
