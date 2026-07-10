using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Services;

namespace BeamNgTerrainPoc.Tests.Elevation;

/// <summary>
///     Doc 14 — deck-to-deck continuity at bridge→bridge merges (<c>EnableDeckToDeckContinuity</c>).
///     The 135439 Manhattan evidence: ramp span 904452323 (spline 58) lands mid-span on trunk deck
///     1546435469 (spline 2) at junction 106 — the ramp's end solved in ISOLATION to 26.43 while the
///     trunk deck there is 24.90 (a 1.5 m step), and <c>PinOnDeckJunctions</c> let the ramp span
///     OVERWRITE the junction with its plan deckEnd cap 34.53. Covers: (a) the junction authority rule,
///     (b) the landing-anchored span profile + priority solve order, (c) the junction-driven landing
///     records, (d) the deck-seam diagnostics. All flag-gated; flag off = byte-identical.
/// </summary>
public class DeckToDeckContinuityTests
{
    private const int RampId = 1;   // lower spline id than the trunk ⇒ naive id-order would solve it FIRST
    private const int TrunkId = 2;

    private static BridgeRuleSystemOptions Rules(bool continuityOn) => new()
    {
        EnableBridgeToBridgeAbutmentSuppression = true,
        EnableDeckToDeckContinuity = continuityOn,
    };

    /// <summary>
    ///     Trunk (id 2, p10000) along X: (0,100)→(400,100) on a 2% grade (z = 20 + 0.02·d), bridge span
    ///     [100,300]. Ramp (id 1, p9500) diagonal (0,0)→(200,100): its END is exactly ON the trunk
    ///     centerline at trunk station 200; bridge span [160,224] reaches the ramp's spline end (the
    ///     doc-14 isolated-endpoint shape). Ramp road part solved flat at 10, span pre-solved to end at
    ///     <paramref name="rampEndZ"/> (the "isolated" wrong height).
    /// </summary>
    private static (UnifiedRoadNetwork network,
        ParameterizedRoadSpline trunk, StructureSegment trunkSpan,
        ParameterizedRoadSpline ramp, StructureSegment rampSpan)
        BuildMerge(bool continuityOn, float rampEndZ = 26.5f, bool trunkSag = false, int trunkPriority = 10000)
    {
        var trunkSpan = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 1546435469L }
        };
        var trunk = RoadNetworkTestHelpers.CreateParameterizedSpline(
            TrunkId, new Vector2(0, 100), new Vector2(400, 100), priority: trunkPriority,
            mergeStructuresIntoCorridor: true, structureSegments: [trunkSpan]);
        trunk.Parameters.BridgeRules = Rules(continuityOn);

        var rampSpan = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 160f, EndDistance = 224f, OsmWayIds = { 904452323L }
        };
        var ramp = RoadNetworkTestHelpers.CreateParameterizedSpline(
            RampId, new Vector2(0, 0), new Vector2(200, 100), priority: 9500,
            mergeStructuresIntoCorridor: true, structureSegments: [rampSpan]);
        ramp.Parameters.BridgeRules = Rules(continuityOn);

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
                if (trunkSag)
                {
                    // Terrain-following sag the trunk's own refine must remove BEFORE the ramp anchors.
                    var t = (d - trunkSpan.StartDistance) / (trunkSpan.EndDistance - trunkSpan.StartDistance);
                    cs.TargetElevation -= 15f * 4f * t * (1f - t);
                }
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

        return (network, trunk, trunkSpan, ramp, rampSpan);
    }

    private static Dictionary<int, List<UnifiedCrossSection>> GroupBySpline(UnifiedRoadNetwork network) =>
        network.CrossSections.GroupBy(c => c.OwnerSplineId)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.LocalIndex).ToList());

    private static UnifiedCrossSection SectionAt(UnifiedRoadNetwork network, int splineId, float station) =>
        network.GetCrossSectionsForSpline(splineId)
            .OrderBy(c => MathF.Abs(c.DistanceAlongSpline - station)).First();

    // ── (b) landing anchor in the span profile solver ──────────────────────────────────────────────

    [Fact]
    public void LandingSpanEnd_AnchorsToTrunkDeckSurface_ZAndProjectedGrade()
    {
        var (network, _, _, ramp, rampSpan) = BuildMerge(continuityOn: true);
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications, a => a.BridgeSplineId == RampId);
        Assert.True(app.Applied);
        Assert.True(app.EndConnected);
        Assert.Contains("anchored to deck", app.Note);

        // End Z = the trunk deck SURFACE at station 200 (20 + 0.02·200 = 24.0), not the isolated 26.5.
        var rampEnd = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).Last();
        Assert.Equal(24.0f, rampEnd.TargetElevation, 0.05f);

        // Anchor grade = trunk dZ/ds projected onto the ramp's +s: 0.02·dot((1,0),(0.894,0.447)) ≈ 0.0179.
        Assert.Equal(0.0179f, app.EndGrade, 0.002f);
    }

    [Fact]
    public void LandingRecordPresent_FlagOff_KeepsIsolatedEndByteIdentical()
    {
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: false);
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        // The record is inert without the flag: the end stays the isolated fallback (its own 26.5).
        var app = Assert.Single(result.Applications, a => a.BridgeSplineId == RampId);
        Assert.False(app.EndConnected);
        Assert.Contains("end isolated", app.Note);
        var rampEnd = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).Last();
        Assert.Equal(26.5f, rampEnd.TargetElevation, 0.05f);
    }

    [Fact]
    public void SolveOrder_TrunkRefinedBeforeRampAnchorsToIt()
    {
        // The trunk span pre-solve SAGS 15 m at mid-span (station 200 ≈ 9 instead of 24). The ramp has
        // the LOWER spline id, so naive id-order would anchor to the sagged value; the landing
        // dependency (ramp lands on trunk) must make the ramp read the trunk's REFINED chord (24.0).
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: true, trunkSag: true);
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        BridgeProfileSolver.RefineSpans(network, log: false);

        Assert.Equal(24.0f, SectionAt(network, TrunkId, 200f).TargetElevation, 0.25f);
        var rampEnd = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).Last();
        Assert.Equal(24.0f, rampEnd.TargetElevation, 0.3f);
    }

    [Fact]
    public void SolveOrder_LandingDependencyBeatsPriority()
    {
        // Manhattan run 214227: the Brooklyn Bridge trunk (p9000) is OUTRANKED by its own ramps
        // (p9500) — priority order would refine the ramp FIRST and anchor it to the trunk's still-
        // sagging pre-refine surface. The landing dependency must order the trunk first regardless.
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: true, trunkSag: true, trunkPriority: 9000);
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        BridgeProfileSolver.RefineSpans(network, log: false);

        var rampEnd = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).Last();
        Assert.Equal(24.0f, rampEnd.TargetElevation, 0.3f);
    }

    [Fact]
    public void CycleReAnchor_FirstSolvedSpanReanchorsToFinalTarget()
    {
        // A landing CYCLE (ramp lands on trunk, trunk records a landing back on the ramp) cannot be
        // ordered. With the trunk also OUTRANKED (p9000 < ramp p9500) the stall picks the ramp first,
        // whose anchor sees the trunk still sagging 15 m (capped away as a crossing). The re-pass must
        // re-anchor the ramp to the trunk's FINAL chord — and replace, not duplicate, its snapshot.
        var (network, _, trunkSpan, _, rampSpan) =
            BuildMerge(continuityOn: true, trunkSag: true, trunkPriority: 9000);
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);
        trunkSpan.StartDeckLanding = new DeckLandingRecord(RampId, 20f, JunctionId: null); // closes the cycle; capped (ramp road 12 m below)

        BridgeProfileSolver.RefineSpans(network, log: false);

        var rampEnd = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).Last();
        Assert.Equal(24.0f, rampEnd.TargetElevation, 0.3f);
        Assert.Equal(22.0f, SectionAt(network, TrunkId, 100f).TargetElevation, 0.3f); // cap held both passes
        Assert.Equal(2, network.BridgeSpans.Count); // snapshot replaced, not duplicated
    }

    [Fact]
    public void LandingAnchor_SkippedWhenDeckFarFromEnd_CrossingNotMerge()
    {
        // The doc-13 radius test is plan-view only: a ramp passing UNDER a deck records a "landing"
        // too (214227: spline 51 vs the Brooklyn deck 19 m above). The anchor must refuse to drag the
        // span to a surface farther than MaxLandingAnchorZGapMeters from its own end.
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: true);
        foreach (var cs in network.GetCrossSectionsForSpline(TrunkId))
            cs.TargetElevation += 21f; // trunk deck now ~45 at the landing — 18.5 m above the ramp end
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        var result = BridgeProfileSolver.RefineSpans(network, log: false);

        var app = Assert.Single(result.Applications, a => a.BridgeSplineId == RampId);
        Assert.False(app.EndConnected);
        Assert.Contains("crossing, not a merge", app.Note);
        var rampEnd = network.GetCrossSectionsForSpline(RampId).OrderBy(c => c.LocalIndex).Last();
        Assert.Equal(26.5f, rampEnd.TargetElevation, 0.05f); // kept its own isolated profile
    }

    // ── (a) junction authority rule in PinOnDeckJunctions ──────────────────────────────────────────

    private static SpanDeckPlan PlanSpan(
        UnifiedRoadNetwork network, ParameterizedRoadSpline spline, StructureSegment seg,
        float deckZ, bool isRaised = true)
    {
        var pins = isRaised
            ? network.GetCrossSectionsForSpline(spline.SplineId)
                .Where(c => c.StructureSpanId == seg.SpanId)
                .OrderBy(c => c.DistanceAlongSpline)
                .Select(c => new DeckPin(c, c.Index, c.DistanceAlongSpline, deckZ))
                .ToList()
            : [];
        return new SpanDeckPlan
        {
            OwnerSplineId = spline.SplineId,
            SpanId = seg.SpanId,
            StartDistance = seg.StartDistance,
            EndDistance = seg.EndDistance,
            Layer = 1,
            RequiredDeckZ = deckZ,
            IsRaised = isRaised,
            ApproachZLeft = 10f,
            ApproachZRight = 10f,
            ClearanceUsed = 5f,
            Pins = pins,
        };
    }

    /// <summary>j106 analogue: trunk contributor mid-span (station 200), ramp contributor at its span END.</summary>
    private static NetworkJunction AddDeckDeckJunction(
        UnifiedRoadNetwork network, ParameterizedRoadSpline trunk, ParameterizedRoadSpline ramp)
    {
        var trunkCs = SectionAt(network, trunk.SplineId, 200f);
        var rampCs = network.GetCrossSectionsForSpline(ramp.SplineId).OrderBy(c => c.LocalIndex).Last();
        var junction = new NetworkJunction { JunctionId = 106, Type = JunctionType.TJunction, Position = trunkCs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = trunkCs, Spline = trunk });
        junction.Contributors.Add(new JunctionContributor { CrossSection = rampCs, Spline = ramp, IsSplineEnd = true });
        network.Junctions.Add(junction);
        return junction;
    }

    private static float[,] FlatMap => RoadNetworkTestHelpers.CreateFlatHeightmap(512, 10f);

    [Fact]
    public void DeckDeckJunction_ThroughDeckOwnsZ_LandingPlanCapSuppressed()
    {
        var (network, trunk, trunkSpan, ramp, rampSpan) = BuildMerge(continuityOn: true);
        var junction = AddDeckDeckJunction(network, trunk, ramp);
        var plan = new BridgeElevationPlan
        {
            Spans = [PlanSpan(network, trunk, trunkSpan, 24.9f), PlanSpan(network, ramp, rampSpan, 34.53f)],
        };

        var raised = new HashSet<NetworkJunction>();
        var onDeck = UnifiedRoadSmoother.PinOnDeckJunctions(network, plan, null, FlatMap, 1f, raised);

        // The trunk (junction interior to its span) owns j106; the ramp's 34.53 plan cap never lands.
        Assert.Equal(1, onDeck);
        Assert.True(junction.IsPinned);
        Assert.Equal(24.9f, junction.HarmonizedElevation, 0.05f);
        Assert.Contains(junction, raised);
    }

    [Fact]
    public void DeckDeckJunction_FlagOff_LegacyLetsLandingCapWin()
    {
        var (network, trunk, trunkSpan, ramp, rampSpan) = BuildMerge(continuityOn: false);
        var junction = AddDeckDeckJunction(network, trunk, ramp);
        var plan = new BridgeElevationPlan
        {
            Spans = [PlanSpan(network, trunk, trunkSpan, 24.9f), PlanSpan(network, ramp, rampSpan, 34.53f)],
        };

        UnifiedRoadSmoother.PinOnDeckJunctions(network, plan, null, FlatMap, 1f, []);

        // The 135439 defect, preserved byte-identically with the flag off: raise-only lets the higher
        // plan deckEnd cap win regardless of write order.
        Assert.Equal(34.53f, junction.HarmonizedElevation, 0.05f);
    }

    [Fact]
    public void DeckDeckJunction_AuthorityLowersEarlierInflatedValue()
    {
        var (network, trunk, trunkSpan, ramp, rampSpan) = BuildMerge(continuityOn: true);
        var junction = AddDeckDeckJunction(network, trunk, ramp);
        junction.HarmonizedElevation = 34.53f; // inflated by an earlier pass
        var plan = new BridgeElevationPlan
        {
            Spans = [PlanSpan(network, trunk, trunkSpan, 24.9f), PlanSpan(network, ramp, rampSpan, 34.53f)],
        };

        UnifiedRoadSmoother.PinOnDeckJunctions(network, plan, null, FlatMap, 1f, []);

        Assert.Equal(24.9f, junction.HarmonizedElevation, 0.05f);
    }

    [Fact]
    public void DeckDeckJunction_UnraisedThroughDeck_SuppressesAllWrites()
    {
        // The trunk deck follows the natural profile (not raised) — the landing ramp must adapt DOWN to
        // it in the profile solver; its plan cap must not lift the junction.
        var (network, trunk, trunkSpan, ramp, rampSpan) = BuildMerge(continuityOn: true);
        var junction = AddDeckDeckJunction(network, trunk, ramp);
        var plan = new BridgeElevationPlan
        {
            Spans =
            [
                PlanSpan(network, trunk, trunkSpan, 24.9f, isRaised: false),
                PlanSpan(network, ramp, rampSpan, 34.53f),
            ],
        };

        var onDeck = UnifiedRoadSmoother.PinOnDeckJunctions(network, plan, null, FlatMap, 1f, []);

        Assert.Equal(0, onDeck);
        Assert.False(junction.IsPinned);
        Assert.True(float.IsNaN(junction.HarmonizedElevation));
    }

    [Fact]
    public void SingleSpanJunction_AuthorityMode_KeepsLegacyRaiseOnly()
    {
        // A junction on ONE deck (ordinary side-road tee on the span) keeps raise-only semantics even
        // with the flag on: an already-higher junction value is not lowered.
        var (network, trunk, trunkSpan, _, _) = BuildMerge(continuityOn: true);
        var trunkCs = SectionAt(network, TrunkId, 150f);
        var junction = new NetworkJunction { JunctionId = 7, Type = JunctionType.TJunction, Position = trunkCs.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = trunkCs, Spline = trunk });
        network.Junctions.Add(junction);
        junction.HarmonizedElevation = 30f;

        var onDeck = UnifiedRoadSmoother.PinOnDeckJunctions(
            network, new BridgeElevationPlan { Spans = [PlanSpan(network, trunk, trunkSpan, 24.9f)] },
            null, FlatMap, 1f, []);

        Assert.Equal(0, onDeck);
        Assert.Equal(30f, junction.HarmonizedElevation, 0.01f);
    }

    // ── (c) landing detection + records ────────────────────────────────────────────────────────────

    [Fact]
    public void RadiusLanding_RecordsDeckSplineAndStation_WithoutContinuityFlag()
    {
        // The doc-13 radius test still drives the flag; the landing RECORD is new but inert.
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: false);

        UnifiedRoadSmoother.MarkStructureExclusions(network.Splines, GroupBySpline(network));

        Assert.True(rampSpan.EndContinuesOntoDeck);
        var landing = Assert.IsType<DeckLandingRecord>(rampSpan.EndDeckLanding);
        Assert.Equal(TrunkId, landing.DeckSplineId);
        Assert.Equal(200f, landing.DeckStation, 2f);
        Assert.Null(landing.JunctionId);
    }

    [Fact]
    public void JunctionLanding_OutsideRadius_RecordedButFlagOnlyWithContinuity()
    {
        // 904452323's start at j103: the end sits just OUTSIDE halfWidth+1 m of the foreign deck, but a
        // junction connects it to a contributor INSIDE the trunk span. Record always; suppression flag
        // flips only with EnableDeckToDeckContinuity.
        foreach (var continuityOn in new[] { false, true })
        {
            var (network, trunk, _, ramp, rampSpan) = BuildMerge(continuityOn);

            // Move the ramp's end sections 10 m off the trunk centerline (outside halfWidth 4 + 1).
            foreach (var cs in network.GetCrossSectionsForSpline(RampId))
                cs.CenterPoint += new Vector2(0f, 10f);

            AddDeckDeckJunction(network, trunk, ramp);
            UnifiedRoadSmoother.MarkStructureExclusions(
                network.Splines, GroupBySpline(network), network.Junctions);

            Assert.Equal(continuityOn, rampSpan.EndContinuesOntoDeck);
            var landing = Assert.IsType<DeckLandingRecord>(rampSpan.EndDeckLanding);
            Assert.Equal(TrunkId, landing.DeckSplineId);
            Assert.Equal(200f, landing.DeckStation, 2f);
            Assert.Equal(106, landing.JunctionId);
        }
    }

    // ── (d) deck-seam diagnostics ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DeckSeamDiagnostics_MeasureTheMergeStep()
    {
        // The 135439 baseline metric: ramp end 26.5 vs trunk surface 24.0 at station 200 ⇒ zGap ≈ +2.5.
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: false);
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        var seams = BridgeProfileSolver.DiagnoseDeckToDeckSeams(network, log: false);

        var seam = Assert.Single(seams);
        Assert.Equal(RampId, seam.SplineId);
        Assert.False(seam.IsStart);
        Assert.Equal(TrunkId, seam.DeckSplineId);
        Assert.Equal(106, seam.JunctionId);
        Assert.Equal(26.5f, seam.EndElevation, 0.1f);
        Assert.Equal(24.0f, seam.DeckSurfaceElevation, 0.1f);
        Assert.Equal(2.5f, seam.ZGapMeters, 0.15f);
    }

    [Fact]
    public void DeckSeamDiagnostics_NearZeroAfterAnchoredRefine()
    {
        var (network, _, _, _, rampSpan) = BuildMerge(continuityOn: true);
        rampSpan.EndContinuesOntoDeck = true;
        rampSpan.EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106);

        BridgeProfileSolver.RefineSpans(network, log: false);
        var seams = BridgeProfileSolver.DiagnoseDeckToDeckSeams(network, log: false);

        var seam = Assert.Single(seams);
        Assert.Equal(0f, seam.ZGapMeters, 0.05f);
    }

    // ── (b) widened seam-anchor filter for the legacy junction walk ─────────────────────────────────

    [Fact]
    public void FindConnectedRoadContributor_AcceptsDeckNeighbour_OnlyWithFlag()
    {
        // Two separated whole-spline decks meeting end-to-end: the legacy filter drops the deck
        // neighbour (the 70↔14 luck dependency); with the flag it is a designed anchor.
        var network = new UnifiedRoadNetwork();
        var bridgeA = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new Vector2(0, 0), new Vector2(50, 0), isBridge: true);
        var bridgeB = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new Vector2(50, 0), new Vector2(100, 0), isBridge: true);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridgeA);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, bridgeB);
        foreach (var cs in network.CrossSections)
            cs.TargetElevation = 30f;

        var aEnd = network.GetCrossSectionsForSpline(1).OrderBy(c => c.LocalIndex).Last();
        var bStart = network.GetCrossSectionsForSpline(2).OrderBy(c => c.LocalIndex).First();
        var junction = new NetworkJunction { JunctionId = 1, Type = JunctionType.Endpoint, Position = aEnd.CenterPoint };
        junction.Contributors.Add(new JunctionContributor { CrossSection = aEnd, Spline = bridgeA, IsSplineEnd = true });
        junction.Contributors.Add(new JunctionContributor { CrossSection = bStart, Spline = bridgeB, IsSplineStart = true });
        network.Junctions.Add(junction);

        Assert.Null(BridgeProfileSolver.FindConnectedRoadContributor(network, 1, isStart: false));

        bridgeA.Parameters.BridgeRules = Rules(continuityOn: true);
        var contributor = BridgeProfileSolver.FindConnectedRoadContributor(network, 1, isStart: false);
        Assert.NotNull(contributor);
        Assert.Equal(2, contributor!.RoadSplineId);
        Assert.Equal(30f, contributor.Elevation, 0.01f);
    }
}
