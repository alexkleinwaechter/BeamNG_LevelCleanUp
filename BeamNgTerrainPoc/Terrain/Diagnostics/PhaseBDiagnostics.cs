using System.Globalization;
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Diagnostics;

/// <summary>
///     Phase B diagnostic CSV emitter. Captures the empirical inputs needed to
///     validate B.2 (short-connector overlap distribution) and B.3 (slope mismatch
///     at the parabolic seam) on real franco_same_prio data. Strictly side-effect
///     free — only writes files, never mutates network state.
/// </summary>
public static class PhaseBDiagnostics
{
    public static void Emit(
        string outputDirectory,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints,
        Dictionary<int, float> originalElevations)
    {
        if (!Directory.Exists(outputDirectory))
            return;

        EmitShortConnectorCsv(
            Path.Combine(outputDirectory, "phase_b_short_connectors.csv"),
            crossSectionsBySpline, constraints);

        EmitSlopeMismatchCsv(
            Path.Combine(outputDirectory, "phase_b_slope_mismatch.csv"),
            crossSectionsBySpline, constraints, originalElevations);
    }

    private static void EmitShortConnectorCsv(
        string path,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("splineId,totalLength,startBlendDist,endBlendDist,overlap_m,is_short_connector");

        foreach (var (splineId, sections) in crossSectionsBySpline)
        {
            if (sections.Count < 2) continue;
            var length = ComputeLength(sections);

            constraints.TryGetValue((splineId, true), out var startC);
            constraints.TryGetValue((splineId, false), out var endC);
            if (startC == null || endC == null) continue;

            var s = startC.BlendDistanceMeters;
            var e = endC.BlendDistanceMeters;
            var overlap = MathF.Max(0f, s + e - length);
            var isShort = overlap > 0f;

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{splineId},{length:F2},{s:F2},{e:F2},{overlap:F2},{(isShort ? 1 : 0)}"));
        }
    }

    private static void EmitSlopeMismatchCsv(
        string path,
        Dictionary<int, List<UnifiedCrossSection>> crossSectionsBySpline,
        Dictionary<(int splineId, bool isStart), JunctionEndpointConstraint> constraints,
        Dictionary<int, float> originalElevations)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine(
            "junctionId,splineId,side,L_blend,zJunction,mJunction,zNaturalAtL,parabolicSlopeAtL,naturalSlopeAtLPlusEps,absDiffPct");

        foreach (var ((splineId, isStart), constraint) in constraints)
        {
            if (!crossSectionsBySpline.TryGetValue(splineId, out var sections) || sections.Count < 3)
                continue;

            var distFromStart = ComputeDistances(sections);
            var roadLength = distFromStart[^1];
            var L = constraint.BlendDistanceMeters;
            if (L <= 0.01f || L >= roadLength) continue;

            int sampleIdx;
            int afterIdx;
            float zJunction = constraint.Elevation;
            float mJunction = constraint.Slope;
            float zNaturalAtL;

            if (isStart)
            {
                sampleIdx = FindFirstAtOrAfter(distFromStart, L);
                afterIdx = FindFirstAtOrAfter(distFromStart, L + 5f);
                if (sampleIdx < 0 || afterIdx < 0 || afterIdx == sampleIdx) continue;
                zNaturalAtL = originalElevations.GetValueOrDefault(
                    sections[sampleIdx].Index, sections[sampleIdx].TargetElevation);
            }
            else
            {
                var thresh = roadLength - L;
                sampleIdx = FindLastAtOrBefore(distFromStart, thresh);
                afterIdx = FindLastAtOrBefore(distFromStart, thresh - 5f);
                if (sampleIdx < 0 || afterIdx < 0 || afterIdx == sampleIdx) continue;
                zNaturalAtL = originalElevations.GetValueOrDefault(
                    sections[sampleIdx].Index, sections[sampleIdx].TargetElevation);
            }

            var parabolicSlopeAtL = 2f * (zNaturalAtL - zJunction) / L - mJunction;

            var zAfter = originalElevations.GetValueOrDefault(
                sections[afterIdx].Index, sections[afterIdx].TargetElevation);
            var naturalSlope = isStart
                ? (zAfter - zNaturalAtL) / (distFromStart[afterIdx] - distFromStart[sampleIdx])
                : (zNaturalAtL - zAfter) / (distFromStart[sampleIdx] - distFromStart[afterIdx]);

            var absDiffPct = MathF.Abs(parabolicSlopeAtL - naturalSlope) * 100f;

            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{constraint.Junction?.JunctionId ?? 0},{splineId},{(isStart ? "start" : "end")}," +
                $"{L:F2},{zJunction:F3},{mJunction:F5},{zNaturalAtL:F3}," +
                $"{parabolicSlopeAtL:F5},{naturalSlope:F5},{absDiffPct:F3}"));
        }
    }

    private static float ComputeLength(List<UnifiedCrossSection> sections)
    {
        var total = 0f;
        for (var i = 1; i < sections.Count; i++)
            total += Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
        return total;
    }

    private static float[] ComputeDistances(List<UnifiedCrossSection> sections)
    {
        var d = new float[sections.Count];
        for (var i = 1; i < sections.Count; i++)
            d[i] = d[i - 1] + Vector2.Distance(sections[i].CenterPoint, sections[i - 1].CenterPoint);
        return d;
    }

    private static int FindFirstAtOrAfter(float[] distFromStart, float target)
    {
        for (var i = 0; i < distFromStart.Length; i++)
            if (distFromStart[i] >= target) return i;
        return -1;
    }

    private static int FindLastAtOrBefore(float[] distFromStart, float target)
    {
        for (var i = distFromStart.Length - 1; i >= 0; i--)
            if (distFromStart[i] <= target) return i;
        return -1;
    }
}
