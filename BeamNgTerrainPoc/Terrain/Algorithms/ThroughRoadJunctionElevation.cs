using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     "Junction follows the main road" elevation policy for the no-blend-zones affine path.
///     <para>
///         Root cause of the junction ramp (franco_same_prio, node 282534762): at a T-junction the
///         THROUGH road is set by the wide low-pass to its natural smoothed Z, while the terminating
///         road follows terrain — and the old <c>Consensus</c>/<c>RawTerrain</c> targets pulled both
///         toward a blended/raw-terrain value, dragging the through road off its profile and stepping
///         it against the side road. The fix: the junction's elevation IS the through road's elevation
///         there. The terminating road affine-tilts to meet it; the through road is never moved.
///     </para>
///     Pure policy — no side effects. Reads only contributor (elevation, isThrough) pairs.
/// </summary>
public static class ThroughRoadJunctionElevation
{
    /// <summary>
    ///     Computes the elevation a junction should sit at:
    ///     <list type="bullet">
    ///         <item>If any THROUGH (continuous) road is present — the average of the through roads'
    ///             elevations (one through road = exactly its elevation, no step).</item>
    ///         <item>Else (Y-junction / fork, all roads terminate) — the average of the terminating
    ///             roads' elevations, so they meet at a shared Z.</item>
    ///         <item>Else (lone dead-end, &lt; 2 finite contributors) — NaN, meaning "no target":
    ///             the caller leaves the road on its natural smoothed profile.</item>
    ///     </list>
    ///     Non-finite (NaN/Infinity) contributor elevations are ignored.
    /// </summary>
    public static float Compute(IReadOnlyList<(float elevation, bool isThrough)> contributors)
    {
        if (contributors == null || contributors.Count == 0)
            return float.NaN;

        var throughSum = 0f;
        var throughCount = 0;
        var termSum = 0f;
        var termCount = 0;

        foreach (var (elevation, isThrough) in contributors)
        {
            if (float.IsNaN(elevation) || float.IsInfinity(elevation))
                continue;

            if (isThrough)
            {
                throughSum += elevation;
                throughCount++;
            }
            else
            {
                termSum += elevation;
                termCount++;
            }
        }

        if (throughCount > 0)
            return throughSum / throughCount;

        // No through road: a fork/Y needs at least two ends to define a shared meeting Z.
        if (termCount >= 2)
            return termSum / termCount;

        return float.NaN;
    }

    /// <summary>
    ///     Convenience overload reading a live junction's contributors. A contributor is "through"
    ///     when its spline passes through the junction (<see cref="JunctionContributor.IsContinuous" />);
    ///     its elevation is the cross-section's current <see cref="UnifiedCrossSection.TargetElevation" />.
    /// </summary>
    public static float Compute(NetworkJunction junction)
    {
        var contributors = new List<(float, bool)>(junction.Contributors.Count);
        foreach (var c in junction.Contributors)
            contributors.Add((c.CrossSection.TargetElevation, c.IsContinuous));
        return Compute(contributors);
    }
}
