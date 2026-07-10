using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 15 — seamless intersecting decks (<c>EnableSeamlessDeckOverlap</c>), the AREA follow-up to
///     doc 14's seam-POINT anchor: (a) every landing-span section still overlapping the landed-on deck
///     footprint is conformed EXACTLY onto the deck surface (center + both edges sampled independently,
///     so the intersecting part is coplanar — bank included), eased out past the overlap; the walk is
///     one-directional (the trunk is untouched) and refuses cap-skipped crossings. Also covers the §5
///     overlap AREA diagnostic and the doc-15 merge markers the mesh layer consumes. Same j106 fixture
///     as <see cref="DeckToDeckContinuityTests"/>: trunk (id 2) along X on a 2% grade, ramp (id 1)
///     diagonal, landing ON the trunk centerline at trunk station 200.
/// </summary>
public class SeamlessDeckOverlapTests
{
    private const int RampId = 1;
    private const int TrunkId = 2;

    private static BridgeRuleSystemOptions Rules(bool continuityOn, bool overlapOn) => new()
    {
        EnableBridgeToBridgeAbutmentSuppression = true,
        EnableDeckToDeckContinuity = continuityOn,
        EnableSeamlessDeckOverlap = overlapOn,
    };

    /// <summary>
    ///     Doc-14 BuildMerge shape: trunk (0,100)→(400,100), z = 20 + 0.02·d, span [100,300]; ramp
    ///     (0,0)→(200,100) ending ON the trunk centerline at trunk station 200, span [160,224] (to the
    ///     spline end), road part flat at 10, span pre-solved linearly to <paramref name="rampEndZ"/>.
    ///     Ramp direction (0.894,0.447) ⇒ the overlap zone (any of center/edges within halfWidth 4 +
    ///     margin of the trunk centerline) reaches ≈18 m back from the ramp end.
    /// </summary>
    private static (UnifiedRoadNetwork network, StructureSegment rampSpan)
        BuildMerge(bool continuityOn, bool overlapOn, float rampEndZ = 26.5f, float trunkBankRadians = 0f)
    {
        var trunkSpan = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 1546435469L }
        };
        var trunk = RoadNetworkTestHelpers.CreateParameterizedSpline(
            TrunkId, new Vector2(0, 100), new Vector2(400, 100), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [trunkSpan]);
        trunk.Parameters.BridgeRules = Rules(continuityOn, overlapOn);

        var rampSpan = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 160f, EndDistance = 224f, OsmWayIds = { 904452323L }
        };
        var ramp = RoadNetworkTestHelpers.CreateParameterizedSpline(
            RampId, new Vector2(0, 0), new Vector2(200, 100), priority: 9500,
            mergeStructuresIntoCorridor: true, structureSegments: [rampSpan]);
        ramp.Parameters.BridgeRules = Rules(continuityOn, overlapOn);

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, ramp);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, trunk);

        foreach (var cs in network.GetCrossSectionsForSpline(TrunkId))
        {
            var d = cs.DistanceAlongSpline;
            cs.TargetElevation = 20f + 0.02f * d;
            if (d >= trunkSpan.StartDistance && d <= trunkSpan.EndDistance)
            {
                cs.StructureSpanId = trunkSpan.SpanId;
                cs.IsExcluded = true;
                cs.BankAngleRadians = trunkBankRadians;
            }
        }

        var rampSections = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).ToList();
        var rampLength = rampSections[^1].DistanceAlongSpline;
        foreach (var cs in rampSections)
        {
            var d = cs.DistanceAlongSpline;
            if (d >= rampSpan.StartDistance && d <= rampSpan.EndDistance)
            {
                cs.StructureSpanId = rampSpan.SpanId;
                cs.IsExcluded = true;
                var t = (d - rampSpan.StartDistance) / (rampLength - rampSpan.StartDistance);
                cs.TargetElevation = 10f + (rampEndZ - 10f) * t;
            }
            else
            {
                cs.TargetElevation = 10f;
            }
        }

        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);
        return (network, rampSpan);
    }

    private static List<UnifiedCrossSection> RampSpanSections(UnifiedRoadNetwork network, StructureSegment rampSpan) =>
        network.GetCrossSectionsForSpline(RampId)
            .Where(c => c.StructureSpanId == rampSpan.SpanId)
            .OrderBy(c => c.LocalIndex).ToList();

    /// <summary>The ramp span section nearest <paramref name="metersFromEnd"/> back from the landed end.</summary>
    private static UnifiedCrossSection RampSectionFromEnd(
        UnifiedRoadNetwork network, StructureSegment rampSpan, float metersFromEnd)
    {
        var sections = RampSpanSections(network, rampSpan);
        var target = sections[^1].DistanceAlongSpline - metersFromEnd;
        return sections.OrderBy(c => MathF.Abs(c.DistanceAlongSpline - target)).First();
    }

    // ── (a) conformance zone ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OverlapZone_ConformsCenterAndEdges_ToTrunkSurfacePlane()
    {
        var (network, rampSpan) = BuildMerge(continuityOn: true, overlapOn: true);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications, a => a.BridgeSplineId == RampId);
        Assert.Contains("overlap conformed", app.Note);

        // ~5 m back from the landed end the section is deep inside the overlap footprint. The trunk is
        // unbanked on a 2% X-grade, so its surface at any plan point is exactly 20 + 0.02·x — sampled
        // independently at the section's center AND edge points (the trunk cross-slope along the ramp).
        var cs = RampSectionFromEnd(network, rampSpan, 5f);
        var normal = Vector2.Normalize(cs.NormalDirection);
        var half = cs.EffectiveRoadWidth / 2f;
        var leftPt = cs.CenterPoint - normal * half;
        var rightPt = cs.CenterPoint + normal * half;

        Assert.Equal(20f + 0.02f * cs.CenterPoint.X, cs.TargetElevation, 0.03f);
        Assert.Equal(20f + 0.02f * leftPt.X, cs.LeftEdgeElevation, 0.03f);
        Assert.Equal(20f + 0.02f * rightPt.X, cs.RightEdgeElevation, 0.03f);

        // The edges now carry the trunk plane's cross-slope (±0.02·1.79 ≈ ±0.036 m) instead of the
        // ramp's own bank (0 ⇒ doc-14 edges == center).
        Assert.Equal(0.02f * (leftPt.X - cs.CenterPoint.X), cs.LeftEdgeElevation - cs.TargetElevation, 0.012f);
        Assert.Equal(0.02f * (rightPt.X - cs.CenterPoint.X), cs.RightEdgeElevation - cs.TargetElevation, 0.012f);
        Assert.True(MathF.Abs(cs.LeftEdgeElevation - cs.RightEdgeElevation) > 0.02f,
            "conformed edges must differ (trunk cross-slope), doc-14 left them equal");
    }

    [Fact]
    public void OverlapZone_BankedTrunk_LateralOffsetTermApplied()
    {
        const float bank = 0.15f;
        var (network, rampSpan) = BuildMerge(continuityOn: true, overlapOn: true, trunkBankRadians: bank);

        BridgeProfileSolver.RefineSpans(network, log: false);

        // ~5 m back the ramp center sits ≈2.24 m laterally off the trunk centerline — on a banked trunk
        // the sampled surface includes offset·sin(bank), not just the interpolated center Z.
        var cs = RampSectionFromEnd(network, rampSpan, 5f);
        var trunkCs = network.GetCrossSectionsForSpline(TrunkId)
            .OrderBy(t => MathF.Abs(t.CenterPoint.X - cs.CenterPoint.X)).First();
        var offset = Vector2.Dot(cs.CenterPoint - trunkCs.CenterPoint, Vector2.Normalize(trunkCs.NormalDirection));
        var expected = 20f + 0.02f * cs.CenterPoint.X + Math.Clamp(offset, -4f, 4f) * MathF.Sin(bank);

        Assert.True(MathF.Abs(offset) > 1.5f, "fixture: the probe section must sit off the trunk centerline");
        Assert.Equal(expected, cs.TargetElevation, 0.03f);
    }

    [Fact]
    public void OverlapFlagOff_Doc14BehaviourUntouched()
    {
        var (network, rampSpan) = BuildMerge(continuityOn: true, overlapOn: false);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications, a => a.BridgeSplineId == RampId);
        Assert.Contains("anchored to deck", app.Note);
        Assert.DoesNotContain("overlap conformed", app.Note);

        // Doc-14 edges derive from the ramp's OWN bank (0) — exactly equal to the center, no trunk
        // cross-slope.
        var cs = RampSectionFromEnd(network, rampSpan, 5f);
        Assert.Equal(cs.TargetElevation, cs.LeftEdgeElevation, 0.001f);
        Assert.Equal(cs.TargetElevation, cs.RightEdgeElevation, 0.001f);
    }

    [Fact]
    public void CrossingBeyondAnchorCap_NeverConforms()
    {
        // Trunk lifted 21 m: the landing anchor is cap-skipped (a plan-view crossing, not a merge) —
        // the overlap of stacked decks is legitimate and must never be conformed.
        var (network, rampSpan) = BuildMerge(continuityOn: true, overlapOn: true);
        foreach (var cs in network.GetCrossSectionsForSpline(TrunkId))
            cs.TargetElevation += 21f;

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications, a => a.BridgeSplineId == RampId);
        Assert.Contains("crossing, not a merge", app.Note);
        Assert.DoesNotContain("overlap conformed", app.Note);

        var cs5 = RampSectionFromEnd(network, rampSpan, 5f);
        Assert.Equal(cs5.TargetElevation, cs5.LeftEdgeElevation, 0.001f);
        Assert.False(rampSpan.EndDeckLandingApplied);
    }

    [Fact]
    public void EaseOut_NoStepWhereConformanceEnds_AndFarSpanUntouched()
    {
        var (onNetwork, onSpan) = BuildMerge(continuityOn: true, overlapOn: true);
        var (offNetwork, offSpan) = BuildMerge(continuityOn: true, overlapOn: false);

        BridgeProfileSolver.RefineSpans(onNetwork, log: false);
        BridgeProfileSolver.RefineSpans(offNetwork, log: false);

        // No new step anywhere on the conformed span: relative to the doc-14 baseline profile, the
        // ease may add at most its bounded grade (smoothstep peak 1.5·Δ/run ≈ 0.15 m/m — the
        // delta-scaled run guarantees it), never a z jump. (This deliberately steep fixture ramp
        // climbs 14 m in 64 m, so absolute neighbour deltas are large in BOTH runs.)
        var onSections = RampSpanSections(onNetwork, onSpan);
        var offSections = RampSpanSections(offNetwork, offSpan);
        Assert.Equal(offSections.Count, onSections.Count);
        for (var i = 1; i < onSections.Count; i++)
        {
            var ds = onSections[i].DistanceAlongSpline - onSections[i - 1].DistanceAlongSpline;
            var dzOn = MathF.Abs(onSections[i].TargetElevation - onSections[i - 1].TargetElevation);
            var dzOff = MathF.Abs(offSections[i].TargetElevation - offSections[i - 1].TargetElevation);
            Assert.True(dzOn <= dzOff + 0.16f * ds + 0.03f,
                $"ease added a step: on={dzOn:F3} off={dzOff:F3} at station {onSections[i].DistanceAlongSpline:F1}");
        }

        // Beyond overlap zone (≈18 m) + delta-scaled ease run (≈24 m for this steep fixture) the span
        // is byte-identical to the doc-14 result.
        var endStation = onSections[^1].DistanceAlongSpline;
        for (var i = 0; i < onSections.Count; i++)
        {
            if (endStation - onSections[i].DistanceAlongSpline < 45f) continue;
            Assert.Equal(offSections[i].TargetElevation, onSections[i].TargetElevation, 0.001f);
            Assert.Equal(offSections[i].LeftEdgeElevation, onSections[i].LeftEdgeElevation, 0.001f);
        }
    }

    [Fact]
    public void Snapshot_InheritsConformedGeometry()
    {
        var (network, rampSpan) = BuildMerge(continuityOn: true, overlapOn: true);

        BridgeProfileSolver.RefineSpans(network, log: false);

        var snapshot = Assert.Single(network.BridgeSpans, s => s.SplineId == RampId);
        var sections = RampSpanSections(network, rampSpan);
        Assert.Equal(sections.Count, snapshot.Stations.Count);
        for (var i = 0; i < sections.Count; i++)
        {
            Assert.Equal(sections[i].TargetElevation, snapshot.Stations[i].CenterZ, 0.001f);
            Assert.Equal(sections[i].LeftEdgeElevation, snapshot.Stations[i].LeftEdgeZ, 0.001f);
            Assert.Equal(sections[i].RightEdgeElevation, snapshot.Stations[i].RightEdgeZ, 0.001f);
        }
    }

    [Fact]
    public void MergeMarker_RecordedOnSegment_ForTheMeshLayer()
    {
        var (network, rampSpan) = BuildMerge(continuityOn: true, overlapOn: true);
        Assert.False(rampSpan.EndDeckLandingApplied);

        BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.True(rampSpan.EndDeckLandingApplied);
        Assert.False(rampSpan.StartDeckLandingApplied);
    }

    // ── §5 overlap AREA diagnostic ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DeckSeamDiagnostics_OverlapAreaMetric_BaselineShowsTheStep()
    {
        // Flag-independent baseline (doc 14's point metric covers only the end center): the pre-solve
        // ramp runs 10→26.5 over the span while the trunk plane sits ≈24 under the gore — the AREA gap
        // over all overlapping stations × {center,L,R} is metres, not the ≈0 of the end point.
        var (network, _) = BuildMerge(continuityOn: false, overlapOn: false);

        var seams = BridgeProfileSolver.DiagnoseDeckToDeckSeams(network, log: false);

        var seam = Assert.Single(seams);
        Assert.True(seam.OverlapStations > 10,
            $"expected a wide overlap zone, got {seam.OverlapStations} station(s)");
        Assert.True(seam.OverlapMaxGapMeters > 1.5f,
            $"expected metre-scale baseline area gap, got {seam.OverlapMaxGapMeters:F2}");
    }

    [Fact]
    public void DeckSeamDiagnostics_OverlapAreaMetric_NearZeroAfterConformance()
    {
        var (network, _) = BuildMerge(continuityOn: true, overlapOn: true);

        BridgeProfileSolver.RefineSpans(network, log: false);
        var seams = BridgeProfileSolver.DiagnoseDeckToDeckSeams(network, log: false);

        var seam = Assert.Single(seams);
        Assert.True(seam.OverlapStations > 10);
        Assert.True(seam.OverlapMaxGapMeters < 0.02f,
            $"conformed overlap must be coplanar, got {seam.OverlapMaxGapMeters:F3}");
    }

    // ── (d) flag wiring ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnableSeamlessDeckOverlap_IsNotPartOfAnyEnabled()
    {
        Assert.False(new BridgeRuleSystemOptions { EnableSeamlessDeckOverlap = true }.AnyEnabled);
    }
}
