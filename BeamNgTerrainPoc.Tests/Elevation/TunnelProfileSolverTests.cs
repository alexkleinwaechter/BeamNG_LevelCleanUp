using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Tunnel plan Phase 2b (ai_docs/2026-07-18_tunnel_generation/01): the portal-anchored tunnel floor
///     profile. <see cref="TunnelProfileSolver.RefineSpans" /> must override ONLY the tunnel span
///     sections with a G0+G1 Hermite fitted to the solved approaches (portals sit where the roads are),
///     warn — never clamp — on max-grade violations, honor the chain bank (doc 03, flag on) or zero
///     it (flag off), and capture <c>network.TunnelSpans</c>. Flags off ⇒ byte-identical.
/// </summary>
public class TunnelProfileSolverTests
{
    private const float Grade = 0.04f;

    private static Dictionary<int, List<UnifiedCrossSection>> GroupBySpline(UnifiedRoadNetwork network) =>
        network.CrossSections.GroupBy(cs => cs.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

    /// <summary>
    ///     40 m corridor: road [0,15) – tunnel span [15,25] – road (25,40]. Roads on a constant 4% grade;
    ///     the span's chain solve climbs a 30 m-peak parabola (today's "over the mountain" profile).
    /// </summary>
    private static (UnifiedRoadNetwork network, StructureSegment seg, List<UnifiedCrossSection> cs)
        BuildMountainCorridor(TunnelRuleSystemOptions? rules = null)
    {
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel,
            StartDistance = 15f,
            EndDistance = 25f,
            OsmWayIds = { 555L },
            OsmTags = new Dictionary<string, string> { ["tunnel"] = "yes" }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.TunnelRules = rules;

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);

        var bySpline = GroupBySpline(network);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines, bySpline);
        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, bySpline);

        foreach (var c in cs)
        {
            var d = c.DistanceAlongSpline;
            var roadZ = 100f + Grade * d;
            if (d >= 15f && d <= 25f)
            {
                var t = (d - 15f) / 10f;
                c.TargetElevation = roadZ + 120f * t * (1f - t); // peak +30 m at mid-span
            }
            else
            {
                c.TargetElevation = roadZ;
            }
        }

        return (network, seg, cs);
    }

    [Fact]
    public void PortalG0_SpanEndpointsMatchApproachLine()
    {
        var (network, _, cs) = BuildMountainCorridor(TunnelRuleSystemOptions.CreateWithAllRulesEnabled());

        var apps = TunnelProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(apps);
        Assert.True(app.Applied);
        Assert.True(app.StartConnected);
        Assert.True(app.EndConnected);

        // Anchors are the road sections just outside the span (d=14 / d=26 on the 4% line).
        Assert.Equal(100f + Grade * 14f, app.StartElevation, 0.2f);
        Assert.Equal(100f + Grade * 26f, app.EndElevation, 0.2f);

        // Span sections sit on (≈) the constant-grade line — the mountain climb is gone.
        var spanCs = cs.Where(c => c.DistanceAlongSpline is >= 15f and <= 25f).ToList();
        foreach (var c in spanCs)
            Assert.Equal(100f + Grade * c.DistanceAlongSpline, c.TargetElevation, 0.25f);
        Assert.True(spanCs.Max(c => c.TargetElevation) < 102f, "interior no longer tracks the peak");
    }

    [Fact]
    public void PortalG1_NoGradeDiscontinuityAtPortals()
    {
        var (network, _, cs) = BuildMountainCorridor(TunnelRuleSystemOptions.CreateWithAllRulesEnabled());
        TunnelProfileSolver.RefineSpans(network, log: false);

        var ordered = cs.OrderBy(c => c.DistanceAlongSpline).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var ds = ordered[i].DistanceAlongSpline - ordered[i - 1].DistanceAlongSpline;
            var step = MathF.Abs(ordered[i].TargetElevation - ordered[i - 1].TargetElevation);
            Assert.True(step <= Grade * ds + 0.05f,
                $"discontinuity at d={ordered[i].DistanceAlongSpline:F1}: step={step:F3} over ds={ds:F2}");
        }
    }

    [Fact]
    public void BankingFlagOff_SpanBankingZeroed_EdgesEqualCenter()
    {
        // v1 baseline: EnableTunnelBanking off ⇒ the zeroing path runs exactly as before.
        var rules = TunnelRuleSystemOptions.CreateWithAllRulesEnabled();
        rules.EnableTunnelBanking = false;
        var (network, _, cs) = BuildMountainCorridor(rules);
        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
            c.BankAngleRadians = 0.1f; // pretend the chain banked the span

        TunnelProfileSolver.RefineSpans(network, log: false);

        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
        {
            Assert.Equal(0f, c.BankAngleRadians);
            Assert.Equal(c.TargetElevation, c.LeftEdgeElevation);
            Assert.Equal(c.TargetElevation, c.RightEdgeElevation);
        }
    }

    /// <summary>
    ///     Banking follow-up (doc 03): with EnableTunnelBanking on, the solver STOPS erasing the
    ///     Phase 2.5 chain bank — span sections keep their bank angle and the edges are recomputed
    ///     against the new floor Z with the bridge formula (z ± halfWidth·sin(bank)); the captured
    ///     snapshot transports the banked edge Zs.
    /// </summary>
    [Fact]
    public void BankingFlagOn_KeepsChainBank_EdgesFollowSinBank()
    {
        var (network, _, cs) = BuildMountainCorridor(TunnelRuleSystemOptions.CreateWithAllRulesEnabled());
        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
            c.BankAngleRadians = 0.1f; // the chain's superelevation

        TunnelProfileSolver.RefineSpans(network, log: false);

        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
        {
            Assert.Equal(0.1f, c.BankAngleRadians);
            var edgeDelta = c.EffectiveRoadWidth / 2f * MathF.Sin(0.1f);
            Assert.Equal(c.TargetElevation - edgeDelta, c.LeftEdgeElevation, 1e-4f);
            Assert.Equal(c.TargetElevation + edgeDelta, c.RightEdgeElevation, 1e-4f);
        }

        var snap = Assert.Single(network.TunnelSpans);
        Assert.All(snap.Stations, s =>
        {
            Assert.True(s.RightEdgeZ > s.CenterZ, "snapshot must carry the banked right edge");
            Assert.True(s.LeftEdgeZ < s.CenterZ, "snapshot must carry the banked left edge");
        });
    }

    [Fact]
    public void CapturesTunnelSpanSnapshot_NotBridgeSpans()
    {
        var (network, seg, _) = BuildMountainCorridor(TunnelRuleSystemOptions.CreateWithAllRulesEnabled());
        TunnelProfileSolver.RefineSpans(network, log: false);

        var snap = Assert.Single(network.TunnelSpans);
        Assert.Equal(1, snap.SplineId);
        Assert.Equal(seg.SpanId, snap.SpanId);
        Assert.Contains(555L, snap.OsmWayIds);
        Assert.True(snap.Stations.Count >= 10);
        Assert.All(snap.Stations, s => Assert.True(float.IsFinite(s.CenterZ)));
        Assert.Empty(network.BridgeSpans);
    }

    [Fact]
    public void GradeViolation_Warned_NotClamped()
    {
        // Steep corridor: approaches at 12% grade — far beyond TunnelMaxGradePercent 6%.
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 15f, EndDistance = 25f, OsmWayIds = { 9L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(40, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.TunnelRules = TunnelRuleSystemOptions.CreateWithAllRulesEnabled();

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);
        var bySpline = GroupBySpline(network);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines, bySpline);
        foreach (var c in cs)
            c.TargetElevation = 100f + 0.12f * c.DistanceAlongSpline;

        var apps = TunnelProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(apps);
        Assert.True(app.Applied);
        Assert.True(app.MaxGradeExceeded);
        // NOT clamped: the profile still follows the 12% line through the span.
        foreach (var c in cs.Where(c => c.StructureSpanId >= 0))
            Assert.Equal(100f + 0.12f * c.DistanceAlongSpline, c.TargetElevation, 0.3f);
    }

    /// <summary>
    ///     tunneljena regression (2026-07-18 render: tube rode OVER the mountain): on long spans the
    ///     chain filter drags the approach's last meters up the mountain flank, so the sampled portal
    ///     grades are polluted (21.8% on a 3.1 km span ⇒ ~100 m Hermite hump). The overshoot guard
    ///     must reject the polluted grades and settle on (≈) the portal-to-portal chord — the tunnel
    ///     goes straight through, not over.
    /// </summary>
    [Fact]
    public void PollutedPortalGrades_LongSpan_ChordFallback_NoMountainClimb()
    {
        var network = new UnifiedRoadNetwork();
        var seg = new StructureSegment
        {
            Type = StructureType.Tunnel, StartDistance = 50f, EndDistance = 350f, OsmWayIds = { 8L }
        };
        var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
            splineId: 1, start: new Vector2(0, 0), end: new Vector2(400, 0),
            mergeStructuresIntoCorridor: true, structureSegments: [seg]);
        spline.Parameters.TunnelRules = TunnelRuleSystemOptions.CreateWithAllRulesEnabled();

        var cs = RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline, crossSectionSpacing: 1f);
        var bySpline = GroupBySpline(network);
        UnifiedRoadSmoother.TagStructureSpans(network.Splines, bySpline);

        foreach (var c in cs)
        {
            var d = c.DistanceAlongSpline;
            c.TargetElevation = d switch
            {
                // Flat approaches, but the last 10 m before each portal climb at 25% (the chain-filter
                // pollution zone blending into the over-the-mountain span profile).
                < 40f => 100f,
                < 50f => 100f + 0.25f * (d - 40f),                    // → 102.5 at the portal
                <= 350f => 102.5f + 40f * MathF.Sin(MathF.PI * (d - 50f) / 300f), // mountain +40 mid-span
                <= 360f => 100f + 0.25f * (360f - d),
                _ => 100f
            };
        }

        var apps = TunnelProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(apps);
        Assert.True(app.Applied);
        // The polluted 25% anchors were rejected — not the exact-G1 cubic.
        Assert.NotEqual(BridgeProfileSolver.BridgeProfileCurve.Cubic, app.Curve);
        Assert.True(app.MaxBulgeMeters <= 4.5f, $"bulge {app.MaxBulgeMeters:F1}m");

        // The span sits near the ~102.5 chord — the +40 m mountain climb is gone.
        var spanCs = cs.Where(c => c.StructureSpanId >= 0).ToList();
        Assert.True(spanCs.Max(c => c.TargetElevation) < 107f,
            $"max span z {spanCs.Max(c => c.TargetElevation):F1} — tunnel must not climb the mountain");
        Assert.False(app.MaxGradeExceeded); // chord ≈ 0% ≪ 6%
    }

    [Fact]
    public void FlagOff_ByteIdentical_NoSnapshot()
    {
        // TunnelRules present but EnableTunnelProfile off (baseline discipline).
        var (network, _, cs) = BuildMountainCorridor(new TunnelRuleSystemOptions());
        var before = cs.Select(c => c.TargetElevation).ToList();

        var apps = TunnelProfileSolver.RefineSpans(network, log: false);

        Assert.Empty(apps);
        Assert.Empty(network.TunnelSpans);
        Assert.Equal(before, cs.Select(c => c.TargetElevation).ToList());
    }

    [Fact]
    public void NullRules_ByteIdentical()
    {
        var (network, _, cs) = BuildMountainCorridor(rules: null);
        var before = cs.Select(c => c.TargetElevation).ToList();

        var apps = TunnelProfileSolver.RefineSpans(network, log: false);

        Assert.Empty(apps);
        Assert.Equal(before, cs.Select(c => c.TargetElevation).ToList());
    }

    [Fact]
    public void PortalApronShrink_ApronSectionsStayStampable()
    {
        // With EnablePortalAprons on, the first/last PortalApronMeters (3 m) of the span stay
        // NON-excluded (ordinary stamped road into the portal); the interior is excluded.
        var (_, _, cs) = BuildMountainCorridor(TunnelRuleSystemOptions.CreateWithAllRulesEnabled());

        UnifiedCrossSection At(float d) => cs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - d)).First();

        Assert.False(At(16f).IsExcluded);  // start apron
        Assert.False(At(24f).IsExcluded);  // end apron
        Assert.True(At(20f).IsExcluded);   // interior
        // Span tagging still covers the aprons (the mesh/holes read the full span).
        Assert.True(At(16f).StructureSpanId >= 0);
        Assert.Equal(StructureType.Tunnel, At(16f).StructureSpanType);
    }

    [Fact]
    public void ApronShrink_FlagOff_FullExclusion()
    {
        var (_, _, cs) = BuildMountainCorridor(new TunnelRuleSystemOptions());

        UnifiedCrossSection At(float d) => cs.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - d)).First();

        Assert.True(At(16f).IsExcluded);
        Assert.True(At(24f).IsExcluded);
        Assert.True(At(20f).IsExcluded);
    }
}
