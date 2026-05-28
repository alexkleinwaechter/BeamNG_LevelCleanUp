using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Phase 1.9 — pins junction elevations before per-road smoothing runs.
///     Currently sets <see cref="NetworkJunction.HarmonizedElevation"/> for Endpoint
///     and TJunction; multi-way handling (Y/X/Complex) is not yet implemented and
///     those junctions are left at NaN, as are MidSplineCrossing, Roundabout, and
///     Continuation — their existing handlers in Phase 3 / Phase 2.6 still compute those.
///     Pure function: only side effect is setting HarmonizedElevation on junctions.
/// </summary>
public static class JunctionElevationPinner
{
    public static void PinNetwork(
        UnifiedRoadNetwork network,
        float[,] heightMap,
        float metersPerPixel,
        JunctionHarmonizationParameters parameters)
    {
        if (!parameters.EnablePhase19JunctionPinning) return;
        if (network.Junctions.Count == 0) return;

        var mapHeight = heightMap.GetLength(0);
        var mapWidth = heightMap.GetLength(1);
        var pinned = 0;

        foreach (var j in network.Junctions)
        {
            if (j.IsExcluded) continue;

            switch (j.Type)
            {
                case JunctionType.Endpoint:
                case JunctionType.TJunction:
                    j.HarmonizedElevation = SampleHeightmapBilinear(
                        heightMap, j.Position.X, j.Position.Y, metersPerPixel, mapWidth, mapHeight);
                    if (!float.IsNaN(j.HarmonizedElevation))
                    {
                        j.IsPinned = true;
                        pinned++;
                    }
                    break;

                // MidSplineCrossing, Roundabout, Continuation: deliberately skipped —
                // their existing Phase 3 / Phase 2.6 handlers still own those types.
                default:
                    break;
            }
        }

        TerrainCreationLogger.Current?.Detail(
            $"Phase 1.9: pinned {pinned} junction elevation(s) out of {network.Junctions.Count}");
    }

    /// <summary>
    ///     W3 — AASHTO §4.1.5 class-keyed max grade in percent. Motorway and trunk are
    ///     capped at 3 %, primary/secondary at 5 %, tertiary/residential/living_street/
    ///     unclassified at 7 %, anything else (service/track/null/unknown) at 9 %.
    /// </summary>
    public static float GetMaxGradePercent(string? osmRoadType)
    {
        if (string.IsNullOrEmpty(osmRoadType)) return 9.0f;
        return osmRoadType switch
        {
            "motorway" or "motorway_link" or "trunk" or "trunk_link" => 3.0f,
            "primary" or "primary_link" or "secondary" or "secondary_link" => 5.0f,
            "tertiary" or "tertiary_link" or "unclassified" or "residential" or "living_street" => 7.0f,
            _ => 9.0f
        };
    }

    /// <summary>
    ///     Clamps a signed grade percentage to a magnitude cap, preserving sign.
    ///     +5 % clamped to 3 % → +3 %; −5 % clamped to 3 % → −3 %.
    /// </summary>
    public static float ClampGradePercent(float input, float cap)
    {
        if (cap <= 0f) return input;
        return MathF.Sign(input) * MathF.Min(MathF.Abs(input), cap);
    }

    /// <summary>
    ///     W2 — AASHTO §4.1.5 grade-skip rule. When the natural Phase-2 grade and the pinned
    ///     grade at a junction leg differ by ≤ <paramref name="thresholdPct"/>, the Hermite
    ///     ramp on that leg adds noise without benefit (the seam is invisible). Returns true
    ///     when the ramp should be skipped.
    /// </summary>
    public static bool ShouldSkipHermiteRamp(float naturalGradePct, float pinnedGradePct, float thresholdPct)
    {
        return MathF.Abs(naturalGradePct - pinnedGradePct) <= thresholdPct;
    }

    private static float SampleHeightmapBilinear(
        float[,] heightMap, float worldX, float worldY, float metersPerPixel, int mapWidth, int mapHeight)
    {
        var fx = worldX / metersPerPixel;
        var fy = worldY / metersPerPixel;
        var x0 = Math.Clamp((int)MathF.Floor(fx), 0, mapWidth - 1);
        var y0 = Math.Clamp((int)MathF.Floor(fy), 0, mapHeight - 1);
        var x1 = Math.Clamp(x0 + 1, 0, mapWidth - 1);
        var y1 = Math.Clamp(y0 + 1, 0, mapHeight - 1);
        var tx = MathF.Max(0f, MathF.Min(1f, fx - x0));
        var ty = MathF.Max(0f, MathF.Min(1f, fy - y0));

        var h00 = heightMap[y0, x0];
        var h10 = heightMap[y0, x1];
        var h01 = heightMap[y1, x0];
        var h11 = heightMap[y1, x1];

        if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11))
            return float.NaN;

        var top = h00 + (h10 - h00) * tx;
        var bot = h01 + (h11 - h01) * tx;
        return top + (bot - top) * ty;
    }
}
