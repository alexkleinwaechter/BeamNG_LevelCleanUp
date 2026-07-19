namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Engineered vertical profile for bridge approach ramps (2026-07-13 shape rework, experimental).
///     Replaces the full-length cubic smoothstep fade <c>(1−u)²(1+2u)</c> with the standard
///     road-design shape: a parabolic CREST vertical curve at the deck end, a constant-grade
///     tangent, and a parabolic SAG vertical curve where the ramp rejoins the natural profile.
///     Curvature is confined to the two vertical curves; the tangent holds one constant grade.
///     Like the old shape, the weight has value 1 / slope 0 at u=0 (the abutment) and value 0 /
///     slope 0 at u=1 (the ramp end), so both seams stay kink-free relative to the natural profile.
/// </summary>
/// <remarks>
///     With the old smoothstep the peak grade was 1.5× the average <c>delta/rampLen</c> and the
///     profile curved over its whole length — near the deck the road drooped through a long
///     asymptotic flattening. Here the peak (tangent) grade is <see cref="TangentGradeFactor"/> ×
///     the average, and <see cref="LengthFor"/> sizes the ramp so that tangent grade equals the
///     class grade exactly (the old <c>|delta|/slope</c> sizing silently exceeded it mid-ramp).
/// </remarks>
internal static class ApproachRampProfile
{
    /// <summary>
    ///     Fraction of the ramp length used by EACH parabolic vertical curve (crest at the deck end,
    ///     sag at the natural-profile end); the remaining 1 − 2·fraction runs at constant grade.
    /// </summary>
    internal const float VerticalCurveFraction = 0.25f;

    /// <summary>
    ///     Tangent grade as a multiple of the average grade <c>delta/rampLen</c>: 1/(1 − fraction).
    ///     The flat-ended vertical curves shift their share of the climb onto the tangent.
    /// </summary>
    internal const float TangentGradeFactor = 1f / (1f - VerticalCurveFraction);

    /// <summary>
    ///     Ramp length whose TANGENT grade equals <paramref name="maxSlope"/> (rise/run) for a climb of
    ///     <paramref name="delta"/> meters. Longer than the naive <c>|delta|/slope</c> by
    ///     <see cref="TangentGradeFactor"/> — the honest class-grade length for this profile shape.
    /// </summary>
    internal static float LengthFor(float delta, float maxSlope) =>
        MathF.Abs(delta) / maxSlope * TangentGradeFactor;

    /// <summary>
    ///     Profile weight at normalized station <paramref name="u"/> (0 = abutment, 1 = ramp end).
    ///     The pinned elevation is <c>naturalZ + delta · Weight(u)</c>.
    /// </summary>
    internal static float Weight(float u)
    {
        if (u <= 0f) return 1f;
        if (u >= 1f) return 0f;

        const float a = VerticalCurveFraction;
        const float g = TangentGradeFactor;

        if (u < a)
            return 1f - g * u * u / (2f * a); // crest VC: flat at the abutment, grade −g at u=a
        if (u <= 1f - a)
            return 1f - g * (u - a / 2f); // constant-grade tangent
        var r = 1f - u;
        return g * r * r / (2f * a); // sag VC: grade −g at u=1−a, flat at the ramp end
    }
}
