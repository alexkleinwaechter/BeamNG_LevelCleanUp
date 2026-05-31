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
    /// </summary>
    /// <param name="elevations">Smoothed elevations, ordered start→end. Mutated in place.</param>
    /// <param name="distances">Cumulative distance-along-spline per sample, same length, ascending.</param>
    /// <param name="targetStart">Target elevation at the start endpoint, or null if the start is free.</param>
    /// <param name="targetEnd">Target elevation at the end endpoint, or null if the end is free.</param>
    /// <returns>Number of samples whose elevation changed by more than <see cref="Epsilon" />.</returns>
    public static int Apply(
        float[] elevations,
        float[] distances,
        float? targetStart,
        float? targetEnd)
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

        var modified = 0;
        for (var i = 0; i < n; i++)
        {
            if (float.IsNaN(elevations[i])) continue;

            var t = distances[i] / totalLength; // 0 at start, 1 at end

            float correction;
            if (hasStart && hasEnd)
                correction = e0 + (e1 - e0) * t;       // affine interpolation between both errors
            else if (hasStart)
                correction = e0 * (1f - t);            // decays to 0 at the free end
            else
                correction = e1 * t;                   // grows from 0 at the free start

            if (MathF.Abs(correction) > Epsilon)
            {
                elevations[i] += correction;
                modified++;
            }
        }

        return modified;
    }
}
