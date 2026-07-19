using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
///     Tunnel plan Phase 2b (ai_docs/2026-07-18_tunnel_generation/01): the post-smoothing tunnel floor
///     profile — the direct analogue of <see cref="BridgeProfileSolver.RefineSpans" />, deliberately
///     simpler. The chain solve leaves tunnel spans following smoothed terrain OVER the mountain; this
///     overrides ONLY the span sections with a portal-anchored G0+G1 Hermite fitted to the solved
///     in-spline approaches just outside the span (no junction walk — portals sit exactly where the
///     approach roads already are, so no pre-smoothing pins are needed).
///     <para>Rules: G0 portal anchors, G1 grade continuity, max-grade WARNING (never clamped — grade
///     violations mean bad OSM/DEM input, surfaced not hidden), banking honored when
///     <c>EnableTunnelBanking</c> is on (edges = z ± halfWidth·sin(bank), the bridge formula) or
///     zeroed when off (the v1 flat baseline). Each span is captured into <c>network.TunnelSpans</c>
///     — the single frozen source for mesh / holes / decals, exactly the bridge contract.</para>
/// </summary>
public static class TunnelProfileSolver
{
    private const float DefaultGradeSampleLengthMeters = 12f;

    /// <summary>
    ///     Max deviation (m) of the tunnel floor curve from the portal-to-portal chord before the
    ///     bridge overshoot guard steps down the curve family (cubic → parabola → chord). The
    ///     sampled portal grades are POLLUTED on long tunnels — the chain low-pass drags the
    ///     approach's last meters up the mountain flank (its filter window blends into the
    ///     over-the-mountain span interior), so honouring them exactly reproduced the terrain climb
    ///     (tunneljena 2026-07-18: 3.1 km span, entry grade 21.8% ⇒ ~100 m hump over the chord —
    ///     the tube rode over the mountain instead of through it). A tunnel's natural profile IS the
    ///     chord; the guard changes the curve family instead of clamping any grade (the accepted
    ///     bridge-solver discipline).
    /// </summary>
    private const float MaxBulgeAboveChordMeters = 4f;

    /// <summary>Sag cap below the chord (m) — symmetric hygiene for downward-polluted grades.
    ///     Wider than the bridge's 1 m (a deeper tunnel just gains cover, it doesn't hang in air).</summary>
    private const float MaxSagBelowChordMeters = 4f;

    /// <summary>
    ///     Solves every tunnel span of the network (cross-sections tagged
    ///     <see cref="UnifiedCrossSection.StructureSpanType" /> == Tunnel, grouped by (spline, span)),
    ///     gated per spline on <c>TunnelRules.EnableTunnelProfile</c>. Writes the floor profile into the
    ///     span cross-sections' TargetElevation (banked edges when <c>EnableTunnelBanking</c>, else
    ///     edges = center, banking zeroed) and captures <c>network.TunnelSpans</c>. Returns the
    ///     per-span applications for logging/tests.
    /// </summary>
    public static List<TunnelProfileApplication> RefineSpans(
        UnifiedRoadNetwork network,
        float gradeSampleLengthMeters = DefaultGradeSampleLengthMeters,
        bool log = true)
    {
        ArgumentNullException.ThrowIfNull(network);

        var applications = new List<TunnelProfileApplication>();
        var spanKeys = network.CrossSections
            .Where(c => c.StructureSpanId >= 0 && c.StructureSpanType == StructureType.Tunnel)
            .Select(c => (SplineId: c.OwnerSplineId, SpanId: c.StructureSpanId))
            .Distinct()
            .OrderBy(k => k.SplineId).ThenBy(k => k.SpanId)
            .ToList();
        if (spanKeys.Count == 0)
            return applications;

        network.TunnelSpans.Clear();
        foreach (var (splineId, spanId) in spanKeys)
        {
            var spline = network.GetSplineById(splineId);
            var rules = spline?.Parameters.TunnelRules;
            if (rules?.EnableTunnelProfile != true)
                continue;

            var app = ApplyToSpan(network, splineId, spanId, gradeSampleLengthMeters,
                rules.TunnelMaxGradePercent, rules.EnableTunnelBanking);
            if (app == null)
                continue;

            applications.Add(app);
            if (log)
                TerrainCreationLogger.Current?.InfoFileOnly(Format(app));
        }

        if (log && applications.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"[TUNNEL] profile summary: spans={applications.Count} " +
                $"applied={applications.Count(a => a.Applied)} " +
                $"gradeWarnings={applications.Count(a => a.MaxGradeExceeded)}");

        return applications;
    }

    private static TunnelProfileApplication? ApplyToSpan(
        UnifiedRoadNetwork network,
        int splineId,
        int spanId,
        float gradeSampleLengthMeters,
        float maxGradePercent,
        bool enableBanking)
    {
        var allSections = network.GetCrossSectionsForSpline(splineId)
            .OrderBy(c => c.LocalIndex).ToList();
        if (allSections.Count < 2)
            return null;

        var spanSections = allSections.Where(c => c.StructureSpanId == spanId).ToList();
        if (spanSections.Count < 1)
            return null;

        var firstLocal = spanSections[0].LocalIndex;
        var lastLocal = spanSections[^1].LocalIndex;

        // Portal anchors: the in-spline road neighbours just outside the (contiguous) span — the same
        // outside-the-span sampling trick as BridgeProfileSolver.RefineSpans, no fragile junction walk.
        var roadBefore = allSections.Where(c => c.LocalIndex < firstLocal).ToList();
        var roadAfter = allSections.Where(c => c.LocalIndex > lastLocal).ToList();
        var startIsolated = roadBefore.Count == 0;
        var endIsolated = roadAfter.Count == 0;

        // A span covering the whole spline has no approach truth on either side — leave it
        // terrain-following (mirrors the bridge §4.4 isolation rule).
        if (startIsolated && endIsolated)
            return new TunnelProfileApplication
            {
                SplineId = splineId, SpanId = spanId, Applied = false,
                Note = "span covers the whole spline — both portals isolated, left untouched"
            };

        float z0, g0, s0;
        if (!startIsolated)
        {
            var anchor = roadBefore[^1];
            s0 = anchor.DistanceAlongSpline;
            z0 = anchor.TargetElevation;
            g0 = BridgeProfileSolver.EstimateForwardGrade(roadBefore, atStart: false, gradeSampleLengthMeters);
        }
        else
        {
            var anchor = spanSections[0];
            s0 = anchor.DistanceAlongSpline;
            z0 = anchor.TargetElevation;
            g0 = BridgeProfileSolver.EstimateForwardGrade(spanSections, atStart: true, gradeSampleLengthMeters);
        }

        float z1, g1, s1;
        if (!endIsolated)
        {
            var anchor = roadAfter[0];
            s1 = anchor.DistanceAlongSpline;
            z1 = anchor.TargetElevation;
            g1 = BridgeProfileSolver.EstimateForwardGrade(roadAfter, atStart: true, gradeSampleLengthMeters);
        }
        else
        {
            var anchor = spanSections[^1];
            s1 = anchor.DistanceAlongSpline;
            z1 = anchor.TargetElevation;
            g1 = BridgeProfileSolver.EstimateForwardGrade(spanSections, atStart: false, gradeSampleLengthMeters);
        }

        var length = s1 - s0;
        if (length <= 0.01f)
            return new TunnelProfileApplication
            {
                SplineId = splineId, SpanId = spanId, Applied = false,
                Note = "degenerate span length — left untouched"
            };

        // Unchained rescue: extend a straight grade line from the connected portal if one end has no Z.
        var v0 = float.IsFinite(z0);
        var v1 = float.IsFinite(z1);
        var rescued = spanSections.Any(c => !float.IsFinite(c.TargetElevation));
        if (!v0 && v1) { g0 = g1; z0 = z1 - g1 * length; v0 = true; rescued = true; }
        if (!v1 && v0) { g1 = g0; z1 = z0 + g0 * length; v1 = true; rescued = true; }
        if (!v0 || !v1)
            return new TunnelProfileApplication
            {
                SplineId = splineId, SpanId = spanId, Applied = false,
                Note = "no finite portal elevation available — left untouched"
            };

        // Portal-anchored curve with the bridge overshoot guard: cubic Hermite (exact G0+G1) while the
        // deviation from the chord stays under the cap, else parabola (one-sided grade), else the
        // straight chord. The guard is essential for tunnels: long spans inherit POLLUTED approach
        // grades (the chain filter blends the approach into the over-the-mountain span interior) and
        // an unguarded Hermite reproduces the terrain climb (see MaxBulgeAboveChordMeters).
        var sLocals = spanSections.Select(c => c.DistanceAlongSpline - s0).ToArray();
        var (profileFn, curve, maxBulge, _) = BridgeProfileSolver.SelectCurve(
            sLocals, length, z0, z1, g0, g1,
            bulgeCapMeters: MaxBulgeAboveChordMeters,
            maxSagBelowChordMeters: MaxSagBelowChordMeters);
        float Profile(float s) => profileFn(s);

        // Grade rule: report the steepest grade of the FINAL profile. NEVER clamp (user feedback:
        // no grade-clamp mitigations) — a violation is surfaced as a warning.
        var maxAbsGrade = 0f;
        var prevS = 0f;
        var prevZ = Profile(0f);
        foreach (var cs in spanSections)
        {
            var s = cs.DistanceAlongSpline - s0;
            if (s - prevS > 0.01f)
            {
                var z = Profile(s);
                maxAbsGrade = MathF.Max(maxAbsGrade, MathF.Abs((z - prevZ) / (s - prevS)));
                prevS = s;
                prevZ = z;
            }
        }
        // Include the seam grades the curve actually meets the portals with (cubic honours g0/g1;
        // parabola/chord deliberately do not — measure the curve, not the polluted anchors).
        var eps = MathF.Min(0.5f, length * 0.05f);
        if (eps > 0.01f)
        {
            maxAbsGrade = MathF.Max(maxAbsGrade, MathF.Abs((Profile(eps) - Profile(0f)) / eps));
            maxAbsGrade = MathF.Max(maxAbsGrade, MathF.Abs((Profile(length) - Profile(length - eps)) / eps));
        }

        var maxGradeExceeded = maxAbsGrade * 100f > maxGradePercent;
        if (maxGradeExceeded)
            TerrainCreationLogger.Current?.Warning(
                $"[TUNNEL] grade: span {spanId} on spline {splineId} reaches " +
                $"{maxAbsGrade * 100f:F1}% (max {maxGradePercent:F1}%) — NOT clamped; " +
                "check OSM/DEM input around the portals");

        // Apply the floor profile. Banking (doc 03): flag on ⇒ keep the chain's bank and recompute
        // the edges against the new floor Z (the BridgeProfileSolver.ApplyToSpan formula); flag off
        // ⇒ the v1 zeroing — edges = center, flat baseline.
        foreach (var cs in spanSections)
        {
            var z = Profile(cs.DistanceAlongSpline - s0);
            cs.TargetElevation = z;
            if (enableBanking)
            {
                var edgeDelta = cs.EffectiveRoadWidth / 2f * MathF.Sin(cs.BankAngleRadians);
                cs.LeftEdgeElevation = z - edgeDelta;
                cs.RightEdgeElevation = z + edgeDelta;
            }
            else
            {
                cs.BankAngleRadians = 0f;
                cs.BankedNormal3D = new System.Numerics.Vector3(0, 0, 1);
                cs.LeftEdgeElevation = z;
                cs.RightEdgeElevation = z;
            }
        }

        CaptureSpanSnapshot(network, splineId, spanId, spanSections);

        return new TunnelProfileApplication
        {
            SplineId = splineId,
            SpanId = spanId,
            Applied = true,
            StartConnected = !startIsolated,
            EndConnected = !endIsolated,
            RescuedUnchained = rescued,
            StartElevation = z0,
            EndElevation = z1,
            StartGrade = g0,
            EndGrade = g1,
            LengthMeters = length,
            Curve = curve,
            MaxBulgeMeters = maxBulge,
            MaxAbsGrade = maxAbsGrade,
            MaxGradeExceeded = maxGradeExceeded,
            Note = "ok"
        };
    }

    /// <summary>
    ///     Re-captures one tunnel span's snapshot from the network's CURRENT cross-section elevations
    ///     (the <see cref="BridgeProfileSolver.RecaptureSpanSnapshot" /> mirror, for any post-solve pass
    ///     that moves tunnel sections).
    /// </summary>
    public static void RecaptureSpanSnapshot(UnifiedRoadNetwork network, int splineId, int spanId)
    {
        ArgumentNullException.ThrowIfNull(network);
        var spanSections = network.GetCrossSectionsForSpline(splineId)
            .Where(c => c.StructureSpanId == spanId)
            .OrderBy(c => c.LocalIndex)
            .ToList();
        if (spanSections.Count == 0)
            return;

        network.TunnelSpans.RemoveAll(s => s.SplineId == splineId && s.SpanId == spanId);
        CaptureSpanSnapshot(network, splineId, spanId, spanSections);
    }

    private static void CaptureSpanSnapshot(
        UnifiedRoadNetwork network, int splineId, int spanId, List<UnifiedCrossSection> spanSections)
    {
        var seg = network.GetSplineById(splineId)?.StructureSegments?.FirstOrDefault(s => s.SpanId == spanId);
        network.TunnelSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = splineId,
            SpanId = spanId,
            OsmWayIds = seg != null ? new HashSet<long>(seg.OsmWayIds) : [],
            OsmTags = seg?.OsmTags,
            Stations = spanSections.Select(cs => new BridgeStation
            {
                Center = cs.CenterPoint,
                Normal = cs.NormalDirection,
                Tangent = cs.TangentDirection,
                Width = cs.EffectiveRoadWidth,
                CenterZ = cs.TargetElevation,
                LeftEdgeZ = cs.LeftEdgeElevation,
                RightEdgeZ = cs.RightEdgeElevation,
                DistanceAlongSpline = cs.DistanceAlongSpline
            }).ToList()
        });
    }

    private static string Format(TunnelProfileApplication a)
    {
        if (!a.Applied)
            return $"[TUNNEL] span {a.SpanId} spline {a.SplineId}: NOT applied — {a.Note}";
        return $"[TUNNEL] span {a.SpanId} spline {a.SplineId}: len={a.LengthMeters:F1}m " +
               $"z {a.StartElevation:F2}->{a.EndElevation:F2} " +
               $"anchorGrades {a.StartGrade * 100f:F1}%->{a.EndGrade * 100f:F1}% " +
               $"curve={a.Curve} bulge={a.MaxBulgeMeters:F1}m max|grade|={a.MaxAbsGrade * 100f:F1}%" +
               (a.MaxGradeExceeded ? " GRADE-WARN" : "") +
               (a.RescuedUnchained ? " rescued" : "") +
               $" portals[{(a.StartConnected ? "road" : "isolated")},{(a.EndConnected ? "road" : "isolated")}]";
    }
}

/// <summary>Per-span result of <see cref="TunnelProfileSolver.RefineSpans" /> (logging/tests).</summary>
public sealed class TunnelProfileApplication
{
    public int SplineId { get; init; }
    public int SpanId { get; init; }
    public bool Applied { get; init; }
    public bool StartConnected { get; init; }
    public bool EndConnected { get; init; }
    public bool RescuedUnchained { get; init; }
    public float StartElevation { get; init; } = float.NaN;
    public float EndElevation { get; init; } = float.NaN;
    public float StartGrade { get; init; }
    public float EndGrade { get; init; }
    public float LengthMeters { get; init; }

    /// <summary>Curve family the overshoot guard settled on (Cubic = exact G0+G1; Parabola/Chord =
    /// polluted anchor grades rejected, see <c>TunnelProfileSolver.MaxBulgeAboveChordMeters</c>).</summary>
    public BridgeProfileSolver.BridgeProfileCurve Curve { get; init; }

    /// <summary>Max deviation (m) of the chosen curve from the portal-to-portal chord.</summary>
    public float MaxBulgeMeters { get; init; }

    public float MaxAbsGrade { get; init; }
    public bool MaxGradeExceeded { get; init; }
    public string Note { get; init; } = "";
}
