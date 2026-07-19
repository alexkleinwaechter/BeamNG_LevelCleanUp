using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Doc 08 §5 D2 — the post-solve "dam report". For every spline it compares the FINAL solved
///     <see cref="UnifiedCrossSection.TargetElevation"/> against the A0 early centerline estimate
///     (<see cref="UnifiedRoadNetwork.EarlyElevationEstimate"/>, the road's natural pre-bridge profile) and
///     logs, ranked by worst deviation: max/mean |final − estimate|, the road length deviating &gt; 1 m /
///     &gt; 3 m, and the nearest bridge span. Emitted for BOTH bridge and no-bridge runs (the estimate is
///     built unconditionally), so a regen pair yields a directly comparable ranked list — every containment
///     measure (doc 08 C1–C6) becomes measurable. Deck sections (excluded / span-tagged) are skipped: the
///     deck's own raise is legitimate; the report measures what leaks onto NORMAL roads.
/// </summary>
public static class RoadElevationDeviationReport
{
    /// <summary>Splines with a worst deviation below this are aggregated but not given their own line.</summary>
    private const float ReportLineThresholdMeters = 0.5f;

    /// <summary>Hard cap on per-spline lines; the header states how many were dropped (no silent caps).</summary>
    private const int MaxReportLines = 150;

    public static void Emit(UnifiedRoadNetwork network)
    {
        var log = TerrainCreationLogger.Current;
        if (log == null) return;

        var estimate = network.EarlyElevationEstimate;
        if (estimate == null || estimate.Count == 0)
        {
            log.InfoFileOnly("[DAM-REPORT] skipped - no early elevation estimate on the network");
            return;
        }

        var anchors = BuildSpanAnchors(network);
        var rows = new List<SplineDeviation>();
        long sectionsCompared = 0, deckSectionsSkipped = 0, tunnelSectionsSkipped = 0;

        foreach (var spline in network.Splines)
        {
            var sections = network.GetCrossSectionsForSpline(spline.SplineId)
                .OrderBy(c => c.DistanceAlongSpline).ToList();
            if (sections.Count == 0) continue;

            var maxAbs = 0f;
            var devAtMax = 0f;
            UnifiedCrossSection? maxSection = null;
            var sumAbs = 0.0;
            var count = 0;
            float lenOver1 = 0f, lenOver3 = 0f;
            var prevDistance = float.NaN;

            foreach (var cs in sections)
            {
                var segLen = float.IsNaN(prevDistance) ? 0f : cs.DistanceAlongSpline - prevDistance;
                prevDistance = cs.DistanceAlongSpline;

                if (cs.IsExcluded || cs.StructureSpanId >= 0)
                {
                    // ALL structure spans stay out of the dam metric (a span's profile is solver-owned,
                    // not terrain-following); tunnels are merely counted apart for diagnosis (Phase 0).
                    if (cs.StructureSpanType == Osm.Models.StructureType.Tunnel)
                        tunnelSectionsSkipped++;
                    else
                        deckSectionsSkipped++;
                    continue;
                }

                if (float.IsNaN(cs.TargetElevation) || !estimate.TryGetValue(cs.Index, out var est))
                    continue;

                var dev = cs.TargetElevation - est;
                var abs = MathF.Abs(dev);
                sumAbs += abs;
                count++;
                sectionsCompared++;
                if (abs > 1f) lenOver1 += segLen;
                if (abs > 3f) lenOver3 += segLen;
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                    devAtMax = dev;
                    maxSection = cs;
                }
            }

            if (count == 0 || maxSection == null) continue;

            rows.Add(new SplineDeviation(
                spline, maxAbs, devAtMax, maxSection, (float)(sumAbs / count), lenOver1, lenOver3));
        }

        rows.Sort((a, b) => b.MaxAbs.CompareTo(a.MaxAbs));

        var reportable = rows.Where(r => r.MaxAbs >= ReportLineThresholdMeters).ToList();
        var emitted = Math.Min(reportable.Count, MaxReportLines);
        log.InfoFileOnly(
            $"=== [DAM-REPORT] final TargetElevation vs A0 estimate: splines={rows.Count} " +
            $"sections={sectionsCompared} lines={emitted}" +
            (reportable.Count > emitted ? $" (dropped {reportable.Count - emitted} over {ReportLineThresholdMeters:F1}m cap)" : "") +
            $" threshold={ReportLineThresholdMeters:F1}m ===");

        for (var i = 0; i < emitted; i++)
        {
            var r = reportable[i];
            var s = r.Spline;
            var nearest = NearestSpan(anchors, r.MaxSection.CenterPoint);
            var ways = s.OsmWayIds.Count > 0 ? string.Join(",", s.OsmWayIds.OrderBy(x => x)) : "none";
            log.InfoFileOnly(
                $"[DAM-REPORT] spline={s.SplineId} type={s.OsmRoadType ?? "?"} prio={s.Priority} " +
                $"len={s.TotalLengthMeters:F0}m maxDev={r.DevAtMax:+0.00;-0.00} " +
                $"@s={r.MaxSection.DistanceAlongSpline:F0}m meanAbs={r.MeanAbs:F2} " +
                $"len>1m={r.LenOver1:F0}m len>3m={r.LenOver3:F0}m " +
                nearest +
                $" ways={ways}");
            EmitDamCauses(network, estimate, r, log);
        }

        log.InfoFileOnly(
            $"[DAM-REPORT] totals: splines>1m={rows.Count(r => r.MaxAbs > 1f)} " +
            $"splines>3m={rows.Count(r => r.MaxAbs > 3f)} " +
            $"len>1m={rows.Sum(r => r.LenOver1):F0}m len>3m={rows.Sum(r => r.LenOver3):F0}m " +
            $"deckSectionsSkipped={deckSectionsSkipped} tunnelSectionsSkipped={tunnelSectionsSkipped}");
        log.InfoFileOnly("=== [DAM-REPORT] end ===");
    }

    /// <summary>How far above the local A0 estimate a junction on the spline must sit to be named a cause.</summary>
    private const float CauseExcessMeters = 1.5f;

    /// <summary>Per-spline cap on [DAM-CAUSE] lines (worst first; a summary line reports the overflow).</summary>
    private const int CauseLineCap = 3;

    /// <summary>
    ///     Doc 10 follow-up (Park Row mesa): name WHO pinned a dammed spline up. The junction blender
    ///     transplants a junction's <c>HarmonizedElevation</c> onto EVERY contributor road, so a junction
    ///     sitting well above the A0 estimate at its cross-section on this spline is the dam's injection
    ///     point — cross-reference its id against the <c>[BRIDGE-PLAN] junction-raise /
    ///     junction-on-deck / dip junction-lower</c> lines to find the pass (and flag) that raised it.
    ///     File-only, read-only.
    /// </summary>
    private static void EmitDamCauses(
        UnifiedRoadNetwork network,
        Dictionary<int, float> estimate,
        SplineDeviation r,
        TerrainCreationLogger log)
    {
        var causes = new List<(float Excess, string Line)>();
        foreach (var junction in network.Junctions)
        {
            if (float.IsNaN(junction.HarmonizedElevation)) continue;

            foreach (var contributor in junction.Contributors)
            {
                if (contributor.Spline.SplineId != r.Spline.SplineId) continue;
                if (!estimate.TryGetValue(contributor.CrossSection.Index, out var a0)) break;

                var excess = junction.HarmonizedElevation - a0;
                if (excess > CauseExcessMeters)
                {
                    var contribs = junction.Contributors
                        .Select(c => c.Spline)
                        .DistinctBy(sp => sp.SplineId)
                        .OrderByDescending(sp => sp.Priority)
                        .Select(sp => $"{sp.SplineId}:p{sp.Priority}");
                    causes.Add((excess,
                        $"[DAM-CAUSE] spline={r.Spline.SplineId} junction={junction.JunctionId} " +
                        $"({junction.Type}) station={contributor.CrossSection.DistanceAlongSpline:F0}m " +
                        $"junctionZ={junction.HarmonizedElevation:F2} a0={a0:F2} excess={excess:+0.0;-0.0} " +
                        $"pinned={junction.IsPinned} contributors=[{string.Join(",", contribs)}]"));
                }

                break; // one contributor locates the junction on this spline
            }
        }

        foreach (var (_, line) in causes.OrderByDescending(c => c.Excess).Take(CauseLineCap))
            log.InfoFileOnly(line);
        if (causes.Count > CauseLineCap)
            log.InfoFileOnly(
                $"[DAM-CAUSE] spline={r.Spline.SplineId} +{causes.Count - CauseLineCap} more junction(s) " +
                $"over {CauseExcessMeters:F1}m (capped)");
    }

    private sealed record SplineDeviation(
        ParameterizedRoadSpline Spline,
        float MaxAbs,
        float DevAtMax,
        UnifiedCrossSection MaxSection,
        float MeanAbs,
        float LenOver1,
        float LenOver3);

    /// <summary>
    ///     One XY anchor per span end (the abutments — the raise's epicentres), located via the owner
    ///     spline's cross-section nearest to each span station. Works for raised and un-raised spans
    ///     (un-raised spans carry no pins, so pins can't be the anchor source).
    /// </summary>
    private static List<(int SpanId, bool IsRaised, Vector2 Pos)> BuildSpanAnchors(UnifiedRoadNetwork network)
    {
        var anchors = new List<(int, bool, Vector2)>();
        var plan = network.BridgeElevationPlan;
        if (plan == null) return anchors;

        foreach (var span in plan.Spans)
        {
            UnifiedCrossSection? atStart = null, atEnd = null;
            float dStart = float.MaxValue, dEnd = float.MaxValue;
            foreach (var cs in network.GetCrossSectionsForSpline(span.OwnerSplineId))
            {
                var a = MathF.Abs(cs.DistanceAlongSpline - span.StartDistance);
                var b = MathF.Abs(cs.DistanceAlongSpline - span.EndDistance);
                if (a < dStart) { dStart = a; atStart = cs; }
                if (b < dEnd) { dEnd = b; atEnd = cs; }
            }

            if (atStart != null) anchors.Add((span.SpanId, span.IsRaised, atStart.CenterPoint));
            if (atEnd != null) anchors.Add((span.SpanId, span.IsRaised, atEnd.CenterPoint));
        }

        return anchors;
    }

    private static string NearestSpan(List<(int SpanId, bool IsRaised, Vector2 Pos)> anchors, Vector2 point)
    {
        if (anchors.Count == 0) return "nearestSpan=none";

        var best = anchors[0];
        var bestDist = float.MaxValue;
        foreach (var a in anchors)
        {
            var d = Vector2.Distance(a.Pos, point);
            if (d < bestDist)
            {
                bestDist = d;
                best = a;
            }
        }

        return $"nearestSpan={best.SpanId}{(best.IsRaised ? "(raised)" : "")} d={bestDist:F0}m";
    }
}
