using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     V2 plan A0 (review amendment P0-2). At Phase 1.85 every cross-section's <c>TargetElevation</c> is still
///     NaN — Phase 2 smoothing has not run — so the bridge planner used to fall back to RAW DEM samples for
///     approach and obstacle elevations. Raw pre-smooth DEM ≈ embankment banks: that misfire broke the parked
///     branch's raise/dip gate (§5a lesson). This estimator builds a cheap per-section "early road elevation":
///     the CENTERLINE DEM sampled per cross-section, then low-passed ALONG the spline (sliding arc-length
///     window) — centerline-at-station sampling avoids the bank contamination (the §5a failure read span
///     AVERAGES including banks), the longitudinal smooth removes single-cell DEM noise. It approximates where
///     the smoothed road will sit well enough for clearance decisions; A7's post-smooth verification is the
///     backstop and logs the estimate-vs-final delta.
/// </summary>
public static class EarlyRoadElevationEstimator
{
    /// <summary>
    ///     Builds the per-section estimate for every spline, keyed by <see cref="UnifiedCrossSection.Index"/>.
    ///     Sections whose DEM sample is invalid are absent from the result.
    /// </summary>
    public static Dictionary<int, float> Build(
        UnifiedRoadNetwork network, float[,] heightMap, float metersPerPixel, float windowMeters = 30f)
    {
        var result = new Dictionary<int, float>();
        var w = heightMap.GetLength(1);
        var h = heightMap.GetLength(0);
        var halfWindow = MathF.Max(1f, windowMeters * 0.5f);

        foreach (var spline in network.Splines)
        {
            var sections = network.GetCrossSectionsForSpline(spline.SplineId)
                .OrderBy(c => c.DistanceAlongSpline).ToList();
            if (sections.Count == 0) continue;

            // Raw centerline DEM per section (NaN where off-map / invalid).
            var raw = new float[sections.Count];
            for (var i = 0; i < sections.Count; i++)
            {
                var p = sections[i].CenterPoint;
                var px = Math.Clamp((int)(p.X / metersPerPixel), 0, w - 1);
                var py = Math.Clamp((int)(p.Y / metersPerPixel), 0, h - 1);
                var v = heightMap[py, px];
                raw[i] = float.IsNaN(v) || float.IsInfinity(v) ? float.NaN : v;
            }

            // Sliding arc-length window mean (two-pointer, NaN-skipping) — O(n) per spline.
            var lo = 0;
            var hi = 0;
            var sum = 0.0;
            var n = 0;
            for (var i = 0; i < sections.Count; i++)
            {
                var center = sections[i].DistanceAlongSpline;

                while (hi < sections.Count && sections[hi].DistanceAlongSpline <= center + halfWindow)
                {
                    if (!float.IsNaN(raw[hi])) { sum += raw[hi]; n++; }
                    hi++;
                }

                while (lo < sections.Count && sections[lo].DistanceAlongSpline < center - halfWindow)
                {
                    if (!float.IsNaN(raw[lo])) { sum -= raw[lo]; n--; }
                    lo++;
                }

                if (n > 0)
                    result[sections[i].Index] = (float)(sum / n);
                else if (!float.IsNaN(raw[i]))
                    result[sections[i].Index] = raw[i];
            }
        }

        return result;
    }
}
