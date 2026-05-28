namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase B.3 — 4-constraint cubic Hermite vertical profile helper. Replaces
///     the 3-constraint <see cref="ParabolicJunctionProfile" /> in single-end blend
///     zones when EnableBlendZoneEndC1 is on. The cubic is z(d) = a·d³ + b·d² +
///     mJunction·d + zJunction with coefficients chosen so that z(0)=zJunction,
///     z'(0)=mJunction, z(L)=zNaturalAtL, z'(L)=mNaturalAtL. Eliminates the
///     slope kink at the parabolic-to-natural seam at d=L.
/// </summary>
public static class CubicJunctionProfile
{
    /// <summary>
    ///     Samples the 4-constraint cubic at distance <paramref name="d" /> from
    ///     the junction. Caller must supply both anchor elevations AND both anchor
    ///     slopes; see plan §B.3 background for derivation.
    /// </summary>
    /// <param name="d">Distance from junction (m); clamped to [0, blendLength].</param>
    /// <param name="blendLength">Blend zone length L (m).</param>
    /// <param name="zJunction">Anchor elevation at d=0.</param>
    /// <param name="mJunction">Anchor slope at d=0 (dz/dd, dimensionless).</param>
    /// <param name="zNaturalAtL">Natural profile elevation at d=L.</param>
    /// <param name="mNaturalAtL">Natural profile slope at d=L.</param>
    public static float Sample(
        float d, float blendLength,
        float zJunction, float mJunction,
        float zNaturalAtL, float mNaturalAtL)
    {
        if (blendLength <= 0.0001f)
            return zJunction;

        double dClamped = MathF.Max(0f, MathF.Min(d, blendLength));

        // Internal computation in double to avoid catastrophic cancellation when
        // elevations are large (e.g. 100 m) and slope changes over the blend zone
        // are small. Result is cast to float; callers consume float elevations.
        double L = blendLength;
        double P = ((double)zNaturalAtL - zJunction - (double)mJunction * L) / (L * L);
        double Q = ((double)mNaturalAtL - mJunction) / L;
        double a = (Q - 2.0 * P) / L;
        double b = 3.0 * P - Q;

        return (float)(a * dClamped * dClamped * dClamped
                     + b * dClamped * dClamped
                     + (double)mJunction * dClamped
                     + zJunction);
    }
}
