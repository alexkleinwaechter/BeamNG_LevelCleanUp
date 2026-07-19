namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     "No blend zones" affine junction leveling.
///     Applies an affine (offset + tilt) correction to a smoothed road elevation profile so that
///     its endpoints land exactly on supplied junction targets, spreading the endpoint error over
///     the ENTIRE spline length rather than a local blend/decay distance.
///     <para>
///         Because the correction is a first-degree polynomial in distance, it preserves the
///         smoothed profile's curvature (second differences) exactly — it only shifts and tilts
///         the road to meet the junctions. The added grade is ~error/length, so on long roads the
///         join is imperceptible and no embankment ramp forms. This is the non-ramping replacement
///         for endpoint anchoring (#2) on the blend-off path.
///     </para>
///     Pure function: only side effect is mutating the supplied <c>elevations</c> array.
/// </summary>
public static class AffineJunctionLeveler
{
    private const float Epsilon = 1e-4f;

    /// <summary>
    ///     Corrects <paramref name="elevations" /> in place so endpoints hit the given targets.
    ///     <para>
    ///         Doc 08 §7 C3 (bridge-raised junctions): the full-length spread is exactly right for the
    ///         centimetre-scale errors of ordinary junctions, but it is the "Damm" machine for a junction
    ///         held metres above the road's natural profile — a +14 m bridgehead error tilts a 2 km side
    ///         road into a 1 km embankment. When <paramref name="decayLengthStart" /> /
    ///         <paramref name="decayLengthEnd" /> are given, that end's correction instead decays along the
    ///         eased weight <c>(1−u)²(1+2u)</c> (1 at the endpoint, 0 in value AND slope at the decay
    ///         length), so the road climbs to the junction over a bounded, class-slope-sized run and keeps
    ///         its own solved profile beyond it. Both-null is byte-identical to the legacy full-length
    ///         formula.
    ///     </para>
    /// </summary>
    /// <param name="elevations">Smoothed elevations, ordered start→end. Mutated in place.</param>
    /// <param name="distances">Cumulative distance-along-spline per sample, same length, ascending.</param>
    /// <param name="targetStart">Target elevation at the start endpoint, or null if the start is free.</param>
    /// <param name="targetEnd">Target elevation at the end endpoint, or null if the end is free.</param>
    /// <param name="decayLengthStart">Bounded decay run (m) for the start correction; null = full length.</param>
    /// <param name="decayLengthEnd">Bounded decay run (m) for the end correction; null = full length.</param>
    /// <returns>Number of samples whose elevation changed by more than <see cref="Epsilon" />.</returns>
    public static int Apply(
        float[] elevations,
        float[] distances,
        float? targetStart,
        float? targetEnd,
        float? decayLengthStart = null,
        float? decayLengthEnd = null)
    {
        var n = elevations.Length;
        if (n == 0 || distances.Length != n) return 0;
        if (!targetStart.HasValue && !targetEnd.HasValue) return 0;

        var totalLength = distances[n - 1];
        if (totalLength < Epsilon) return 0;

        // Endpoint errors. A side is "active" only when it has a target AND a finite anchor sample.
        var hasStart = targetStart.HasValue && !float.IsNaN(targetStart.Value) && !float.IsNaN(elevations[0]);
        var hasEnd = targetEnd.HasValue && !float.IsNaN(targetEnd.Value) && !float.IsNaN(elevations[n - 1]);
        if (!hasStart && !hasEnd) return 0;

        var e0 = hasStart ? targetStart!.Value - elevations[0] : 0f;
        var e1 = hasEnd ? targetEnd!.Value - elevations[n - 1] : 0f;

        // Effective decay runs, clamped to the spline (a road shorter than the run degenerates to the
        // legacy full-length spread, which is correct — there is no room to decay).
        var decayStart = hasStart && decayLengthStart.HasValue
            ? MathF.Min(MathF.Max(decayLengthStart.Value, Epsilon), totalLength)
            : (float?)null;
        var decayEnd = hasEnd && decayLengthEnd.HasValue
            ? MathF.Min(MathF.Max(decayLengthEnd.Value, Epsilon), totalLength)
            : (float?)null;

        var modified = 0;
        for (var i = 0; i < n; i++)
        {
            if (float.IsNaN(elevations[i])) continue;

            var t = distances[i] / totalLength; // 0 at start, 1 at end

            float correction;
            if (decayStart == null && decayEnd == null)
            {
                // Legacy full-length affine — byte-identical to the pre-decay implementation.
                if (hasStart && hasEnd)
                    correction = e0 + (e1 - e0) * t;   // affine interpolation between both errors
                else if (hasStart)
                    correction = e0 * (1f - t);        // decays to 0 at the free end
                else
                    correction = e1 * t;               // grows from 0 at the free start
            }
            else
            {
                // Per-end weight composition. A decayed end uses the eased run; an undecayed end keeps
                // its legacy linear weight ((1−t)/t), so mixed configurations compose naturally.
                var wStart = !hasStart ? 0f
                    : decayStart.HasValue ? EasedWeight(distances[i] / decayStart.Value)
                    : 1f - t;
                var wEnd = !hasEnd ? 0f
                    : decayEnd.HasValue ? EasedWeight((totalLength - distances[i]) / decayEnd.Value)
                    : t;
                correction = e0 * wStart + e1 * wEnd;
            }

            if (MathF.Abs(correction) > Epsilon)
            {
                elevations[i] += correction;
                modified++;
            }
        }

        return modified;
    }

    /// <summary>Eased decay weight: 1 (slope 0) at u=0, 0 (slope 0) at u≥1 — the well/ramp shape.</summary>
    private static float EasedWeight(float u) =>
        u >= 1f ? 0f : u <= 0f ? 1f : (1f - u) * (1f - u) * (1f + 2f * u);
}
