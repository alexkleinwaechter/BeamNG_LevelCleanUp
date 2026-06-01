namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase A.5 propagation-overlap taper. Pure helper used by
///     <see cref="UnifiedJunctionProfileBlender" /> Step 5b to attenuate
///     propagated-mid-spline-influence weights inside a directly-anchored
///     junction's blend zone. Returns 0 at the junction anchor node and 1 at
///     the blend-zone boundary, with C¹ smoothstep transition in between.
///     Outside the zone the taper is 1 (no contest). Geometric only — never
///     consults terrain elevation or grade.
/// </summary>
public static class OverlapTaper
{
    /// <summary>
    ///     Computes smoothstep(clamp(distFromAnchor / blendLength, 0, 1)).
    /// </summary>
    /// <param name="distFromAnchor">Distance from the contested junction's anchor node along the spline (m).</param>
    /// <param name="blendLength">The contested junction's blend distance (m).</param>
    /// <returns>0 at anchor, 1 at boundary, monotone smoothstep in between; 1 outside the zone or for non-positive blendLength.</returns>
    public static float Compute(float distFromAnchor, float blendLength)
    {
        if (blendLength <= 0.0001f)
            return 1f;

        var x = MathF.Max(0f, MathF.Min(distFromAnchor / blendLength, 1f));
        return x * x * (3f - 2f * x);
    }
}
