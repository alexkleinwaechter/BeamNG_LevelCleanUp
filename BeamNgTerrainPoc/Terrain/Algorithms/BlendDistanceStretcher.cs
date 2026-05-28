namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase C — stretches the parabolic blend distance L so the parabola's emergent
///     slope at d=L matches the natural Phase-2 slope just past the blend zone.
///     The parabola z(d) = a·d² + mJunction·d + zJunction with z(L) = zNaturalAtL has
///     emergent slope z'(L) = 2·(zNaturalAtL − zJunction)/L − mJunction. Setting this
///     equal to the natural-past-blend slope mNaturalAtL and solving for L gives:
///         L_target = 2·(zNaturalAtL − zJunction) / (mNaturalAtL + mJunction)
///     Replaces the rejected B.3 cubic — instead of curving harder within a fixed L,
///     we extend the existing parabola so it has more distance to ease into the
///     natural grade. Caller is responsible for additional clamping (K-cap ceiling,
///     opposite-end hard ceiling).
/// </summary>
public static class BlendDistanceStretcher
{
    /// <summary>
    ///     Computes the stretch target L. Returns <paramref name="currentL" /> unchanged
    ///     when no stretch is warranted: slope mismatch below threshold, divisor near
    ///     zero, sign mismatch yielding non-positive L, or algebraic L_target shorter
    ///     than current L (we only ever extend, never shorten).
    /// </summary>
    /// <param name="currentL">Existing blend distance from CalculateAdaptiveBlendDistance + B.1 K-cap.</param>
    /// <param name="zJunction">Anchor elevation at d=0 (constraint.Elevation).</param>
    /// <param name="mJunction">Anchor slope at d=0 (constraint.Slope).</param>
    /// <param name="zNaturalAtL">Natural Phase-2 elevation sampled at the current d=L.</param>
    /// <param name="mNaturalAtL">Natural Phase-2 slope sampled just past d=L.</param>
    /// <param name="thresholdGrade">If |m_emergent − mNaturalAtL| ≤ this, skip stretch. Default 0.01 = 1%.</param>
    public static float ComputeStretchTarget(
        float currentL,
        float zJunction, float mJunction,
        float zNaturalAtL, float mNaturalAtL,
        float thresholdGrade = 0.01f)
    {
        if (currentL <= 0.01f) return currentL;

        var emergentSlopeAtL = 2f * (zNaturalAtL - zJunction) / currentL - mJunction;
        if (MathF.Abs(emergentSlopeAtL - mNaturalAtL) <= thresholdGrade)
            return currentL;

        var denom = mNaturalAtL + mJunction;
        if (MathF.Abs(denom) < 0.001f) return currentL;

        var lTarget = 2f * (zNaturalAtL - zJunction) / denom;
        if (lTarget <= currentL) return currentL;

        return lTarget;
    }
}
