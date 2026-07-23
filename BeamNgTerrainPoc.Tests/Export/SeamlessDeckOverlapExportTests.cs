using System.Numerics;
using BeamNgTerrainPoc.Terrain.Export;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Doc 15 (b)/(c) exporter wiring: <see cref="BridgeDeckDaeExporter"/> computes a
///     <c>BridgeDeckTrim</c> per span from the FINAL snapshots — parapet stations whose edge point lies
///     inside a conforming partner span's footprint are suppressed (only pairs where a landing anchor
///     APPLIED count; stacked crossings keep full parapets), and end stamps are skipped at
///     ContinuesOntoDeck ends. Flag off ⇒ byte-identical decks.
/// </summary>
public class SeamlessDeckOverlapExportTests
{
    private const int RampId = 1;
    private const int TrunkId = 2;

    /// <summary>
    ///     Snapshot analogue of the solver fixture: trunk deck along X (y=100, stations 100..300,
    ///     flat 24), ramp deck along (0.894,0.447) ending ON the trunk centerline at (200,100), at
    ///     <paramref name="rampZ"/> (24 = coplanar merge; higher = stacked crossing). Both 8 m wide.
    ///     The ramp's segment records an end landing on the trunk at station 200.
    /// </summary>
    private static UnifiedRoadNetwork BuildNetwork(
        bool overlapOn, bool landingApplied = true, float rampZ = 24f)
    {
        var rules = new BridgeRuleSystemOptions
        {
            EnableBridgeToBridgeAbutmentSuppression = true,
            EnableDeckToDeckContinuity = true,
            EnableSeamlessDeckOverlap = overlapOn,
        };

        var trunkSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { 1546435469L }
        };
        var trunk = RoadNetworkTestHelpers.CreateParameterizedSpline(
            TrunkId, new Vector2(0, 100), new Vector2(400, 100), priority: 10000,
            mergeStructuresIntoCorridor: true, structureSegments: [trunkSeg]);
        trunk.Parameters.BridgeRules = rules;

        var rampSeg = new StructureSegment
        {
            Type = StructureType.Bridge, StartDistance = 160f, EndDistance = 224f, OsmWayIds = { 904452323L },
            EndContinuesOntoDeck = true,
            EndDeckLanding = new DeckLandingRecord(TrunkId, 200f, JunctionId: 106),
            EndDeckLandingApplied = landingApplied,
        };
        var ramp = RoadNetworkTestHelpers.CreateParameterizedSpline(
            RampId, new Vector2(0, 0), new Vector2(200, 100), priority: 9500,
            mergeStructuresIntoCorridor: true, structureSegments: [rampSeg]);
        ramp.Parameters.BridgeRules = rules;

        var network = new UnifiedRoadNetwork();
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, ramp);
        RoadNetworkTestHelpers.AddSplineWithCrossSections(network, trunk);

        var trunkStations = new List<BridgeStation>();
        for (var d = 100f; d <= 300f; d += 5f)
            trunkStations.Add(new BridgeStation
            {
                Center = new Vector2(d, 100f),
                Normal = new Vector2(0f, -1f),
                Tangent = new Vector2(1f, 0f),
                Width = 8f,
                CenterZ = 24f, LeftEdgeZ = 24f, RightEdgeZ = 24f,
                DistanceAlongSpline = d
            });
        network.BridgeSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = TrunkId, SpanId = trunkSeg.SpanId, OsmWayIds = { 1546435469L }, Stations = trunkStations
        });

        var dir = Vector2.Normalize(new Vector2(200f, 100f));
        var normal = new Vector2(dir.Y, -dir.X);
        var rampStations = new List<BridgeStation>();
        for (var back = 60f; back >= 0f; back -= 5f)
            rampStations.Add(new BridgeStation
            {
                Center = new Vector2(200f, 100f) - dir * back,
                Normal = normal,
                Tangent = dir,
                Width = 8f,
                CenterZ = rampZ, LeftEdgeZ = rampZ, RightEdgeZ = rampZ,
                DistanceAlongSpline = 223.6f - back
            });
        network.BridgeSpans.Add(new BridgeSpanSnapshot
        {
            SplineId = RampId, SpanId = rampSeg.SpanId, OsmWayIds = { 904452323L }, Stations = rampStations
        });

        return network;
    }

    private static (BridgeDeckExportItem ramp, BridgeDeckExportItem trunk) ExportDecks(UnifiedRoadNetwork network)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deckoverlap_" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = new BridgeDeckDaeExporter().Export(
                network, dir, terrainSizePixels: 512, metersPerPixel: 1f, terrainBaseHeight: 0f);
            Assert.True(result.Success);
            Assert.Equal(2, result.Decks.Count);
            var rampSpanId = network.GetSplineById(RampId)!.StructureSegments![0].SpanId;
            return (result.Decks.Single(d => d.SplineId == rampSpanId),
                    result.Decks.Single(d => d.SplineId != rampSpanId));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FlagOn_OpensParapetsAndSkipsMergeEndStamp_OnBothDecks()
    {
        var (rampOff, trunkOff) = ExportDecks(BuildNetwork(overlapOn: false));
        var (rampOn, trunkOn) = ExportDecks(BuildNetwork(overlapOn: true));

        // Ramp: end stamp gone (−24 verts: one clipped soffit-following segment = 6 faces at the 5 m
        // fixture spacing) AND parapet stations inside the trunk footprint opened — strictly more than
        // the stamp alone.
        Assert.True(rampOn.Vertices < rampOff.Vertices - 24,
            $"ramp deck must lose stamp + parapet segments, {rampOff.Vertices} → {rampOn.Vertices}");

        // Trunk: the gore-mouth parapet segment the ramp drives through is opened.
        Assert.True(trunkOn.Vertices < trunkOff.Vertices,
            $"trunk deck must open the gore mouth, {trunkOff.Vertices} → {trunkOn.Vertices}");
    }

    [Fact]
    public void FlagOff_DecksByteIdentical()
    {
        var (ramp1, trunk1) = ExportDecks(BuildNetwork(overlapOn: false));
        var (ramp2, trunk2) = ExportDecks(BuildNetwork(overlapOn: false));

        Assert.Equal(ramp1.Vertices, ramp2.Vertices);
        Assert.Equal(ramp1.Triangles, ramp2.Triangles);
        Assert.Equal(trunk1.Vertices, trunk2.Vertices);
        Assert.Equal(trunk1.Triangles, trunk2.Triangles);
    }

    [Fact]
    public void StackedCrossing_KeepsFullParapets_ButStillNoStampAtContinuationEnd()
    {
        // Manhattan render 2026-07-07 (second screenshot): decks stacked ABOVE other decks got
        // parapet openings — the footprint test was plan-view only. The union-roadway rule applies
        // only where the two surfaces are actually ONE surface: a deck 12 m above the trunk keeps
        // its full parapets even when its plan footprint overlaps (and even with a landing record —
        // stacking, not merging, is what the elevation says). The end stamp still follows
        // ContinuesOntoDeck — the exact mirror of the doc-13 terrain suppression at that end.
        var (rampOff, trunkOff) = ExportDecks(BuildNetwork(overlapOn: false, rampZ: 36f));
        var (rampOn, trunkOn) = ExportDecks(BuildNetwork(overlapOn: true, rampZ: 36f));

        Assert.Equal(rampOff.Vertices - 24, rampOn.Vertices);      // stamp only, no parapet openings
        Assert.Equal(rampOff.Triangles - 12, rampOn.Triangles);
        Assert.Equal(trunkOff.Vertices, trunkOn.Vertices);         // trunk untouched
        Assert.Equal(trunkOff.Triangles, trunkOn.Triangles);
    }

    [Fact]
    public void ButtSeamKink_ExtendsLandedEndOntoPartnerDeck()
    {
        // Manhattan render 2026-07-11 (span 1192907311 → 39479211, junction 24): two z-conformed
        // decks meeting END-TO-END with a heading kink leave a pie-slice hole on the outside of the
        // bend — each end cap is perpendicular to its OWN tangent. The landed end must be extended
        // along its tangent (halfWidth·tanΔ), stations riding the partner surface SUB-FLUSH (3 cm —
        // exactly-coincident surfaces z-fight, run 130041). Fixture kink: atan(100/200) ≈ 26.6°,
        // half-width 4 ⇒ +2 m. The trunk is truncated to end AT the meet point (butt joint).
        var network = BuildNetwork(overlapOn: true);
        var trunkSpan = network.BridgeSpans.Single(s => s.SplineId == TrunkId);
        trunkSpan.Stations.RemoveAll(st => st.DistanceAlongSpline > 200f);
        var rampSpan = network.BridgeSpans.Single(s => s.SplineId == RampId);
        var stationsBefore = rampSpan.Stations.Count;
        var endBefore = rampSpan.Stations[^1].Center;

        ExportDecks(network);

        Assert.True(rampSpan.Stations.Count > stationsBefore,
            $"landed end must gain extension stations, {stationsBefore} → {rampSpan.Stations.Count}");
        var tail = rampSpan.Stations[^1];
        Assert.True(Vector2.Distance(tail.Center, endBefore) >= 1.9f,
            "extension must reach past the old end toward the wedge");
        Assert.Equal(23.97f, tail.CenterZ, 0.05f); // roadway plane minus the 3 cm sub-flush
    }

    [Fact]
    public void MidDeckGoreLanding_DoesNotExtend()
    {
        // A mid-deck landing has the partner deck continuing underneath — no hole to fill, and a
        // filler would only shadow (z-fight) the partner's surface. The shared fixture lands at
        // trunk station 200, mid-span [100,300] ⇒ no extension. Stacked decks likewise.
        var network = BuildNetwork(overlapOn: true);
        var rampSpan = network.BridgeSpans.Single(s => s.SplineId == RampId);
        var stationsBefore = rampSpan.Stations.Count;
        ExportDecks(network);
        Assert.Equal(stationsBefore, rampSpan.Stations.Count);

        var stacked = BuildNetwork(overlapOn: true, rampZ: 36f);
        var stackedSpan = stacked.BridgeSpans.Single(s => s.SplineId == RampId);
        var stackedBefore = stackedSpan.Stations.Count;
        ExportDecks(stacked);
        Assert.Equal(stackedBefore, stackedSpan.Stations.Count);
    }

    [Fact]
    public void MergeTransitionZone_LowerDeckParapetOpens_UpperDeckKeepsWalls()
    {
        // Manhattan render 2026-07-11: at the 904452323→621793096 gore the LANDING deck rides up to
        // ~1 parapet-height above the trunk edge before the surfaces meet — inside the trunk
        // footprint but no longer coplanar (Δz 1 m > 0.5 m), so the trunk wall was KEPT and pierced
        // the ramp's roadway from below. The Z window must be asymmetric: partner surface up to
        // parapet height + tolerance ABOVE the edge still suppresses (the wall would poke through
        // that roadway); the UPPER deck's own parapet sits above everything and stays.
        var (rampOff, trunkOff) = ExportDecks(BuildNetwork(overlapOn: false, rampZ: 25f));
        var (rampOn, trunkOn) = ExportDecks(BuildNetwork(overlapOn: true, rampZ: 25f));

        // Trunk (lower): gore-mouth wall under the ramp surface is opened.
        Assert.True(trunkOn.Vertices < trunkOff.Vertices,
            $"trunk parapet under the ramp roadway must open, {trunkOff.Vertices} → {trunkOn.Vertices}");

        // Ramp (upper, 1 m above the trunk surface): keeps its parapets — only the merge-end stamp
        // goes (its edge is ABOVE the trunk surface; nothing to pierce).
        Assert.Equal(rampOff.Vertices - 24, rampOn.Vertices);
        Assert.Equal(rampOff.Triangles - 12, rampOn.Triangles);
    }

    [Fact]
    public void WallOverCoplanarGroundRoad_Opens_GradeSeparatedRoadKeepsWall()
    {
        // loerrach render 2026-07-23 (bridge_28570394 at bridge_914252310's abutment): the ramp's
        // first stations stand over the trunk's AT-GRADE motorway past the abutment — no span
        // projection can see it (past the partner span's end), so the deck-footprint test kept a
        // wall standing ON the road. A parapet must never stand on ANY drivable surface: edges over
        // a coplanar ground road (solved cross-section band) open, roads a full clearance below the
        // deck (grade-separated) keep the wall.
        var (coplanar, coplanarBase) = ExportDeckOverGroundRoad(groundRoadZ: 24f);
        Assert.True(coplanar.Vertices < coplanarBase.Vertices,
            $"wall over the coplanar ground road must open, {coplanarBase.Vertices} → {coplanar.Vertices}");

        var (separated, separatedBase) = ExportDeckOverGroundRoad(groundRoadZ: 18f);
        Assert.Equal(separatedBase.Vertices, separated.Vertices);
        Assert.Equal(separatedBase.Triangles, separated.Triangles);
    }

    /// <summary>One 8 m deck span (stations 100..300 along y=100, z=24) beside an at-grade road at
    /// y=106 (band y∈[102,110] ⇒ the deck's left edge y=104 is 2 m inside it) at
    /// <paramref name="groundRoadZ"/>. Returns the deck exported with the flag on and the flag-off
    /// baseline.</summary>
    private static (BridgeDeckExportItem On, BridgeDeckExportItem Off) ExportDeckOverGroundRoad(
        float groundRoadZ)
    {
        BridgeDeckExportItem Export(bool overlapOn)
        {
            var rules = new BridgeRuleSystemOptions
            {
                EnableBridgeToBridgeAbutmentSuppression = true,
                EnableDeckToDeckContinuity = true,
                EnableSeamlessDeckOverlap = overlapOn,
            };

            var seg = new StructureSegment
            {
                Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 300f,
                OsmWayIds = { 800001L }
            };
            var deckSpline = RoadNetworkTestHelpers.CreateParameterizedSpline(
                RampId, new Vector2(0, 100), new Vector2(400, 100), priority: 10000,
                mergeStructuresIntoCorridor: true, structureSegments: [seg]);
            deckSpline.Parameters.BridgeRules = rules;

            var groundSpline = RoadNetworkTestHelpers.CreateParameterizedSpline(
                TrunkId, new Vector2(0, 106), new Vector2(400, 106), priority: 6000);

            var network = new UnifiedRoadNetwork();
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, deckSpline);
            foreach (var cs in RoadNetworkTestHelpers.AddSplineWithCrossSections(network, groundSpline))
                cs.TargetElevation = groundRoadZ;

            var stations = new List<BridgeStation>();
            for (var d = 100f; d <= 300f; d += 5f)
                stations.Add(new BridgeStation
                {
                    Center = new Vector2(d, 100f),
                    Normal = new Vector2(0f, -1f),
                    Tangent = new Vector2(1f, 0f),
                    Width = 8f,
                    CenterZ = 24f, LeftEdgeZ = 24f, RightEdgeZ = 24f,
                    DistanceAlongSpline = d
                });
            network.BridgeSpans.Add(new BridgeSpanSnapshot
            {
                SplineId = RampId, SpanId = seg.SpanId, OsmWayIds = { 800001L }, Stations = stations
            });

            var dir = Path.Combine(Path.GetTempPath(), "deckground_" + Guid.NewGuid().ToString("N"));
            try
            {
                var result = new BridgeDeckDaeExporter().Export(
                    network, dir, terrainSizePixels: 512, metersPerPixel: 1f, terrainBaseHeight: 0f);
                Assert.True(result.Success);
                return Assert.Single(result.Decks);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        return (Export(overlapOn: true), Export(overlapOn: false));
    }

    [Fact]
    public void ParallelSeparateDecks_KeepFacingWalls_DespiteTouchingCorridors()
    {
        // Two coplanar decks 12 m apart (edge gap 4 m) with 8 m smoothing margins: each deck's facing
        // edge stands inside the OTHER's smoothing corridor, but the plan footprints never overlap —
        // no shared roadway, so the corridor widening must not apply. Opening the facing walls would
        // strip both decks toward the gap between two separate structures.
        var (a1, b1) = ExportDecks(BuildParallelNetwork(overlapOn: false));
        var (a2, b2) = ExportDecks(BuildParallelNetwork(overlapOn: true));

        Assert.Equal(a1.Vertices, a2.Vertices);
        Assert.Equal(a1.Triangles, a2.Triangles);
        Assert.Equal(b1.Vertices, b2.Vertices);
        Assert.Equal(b1.Triangles, b2.Triangles);
    }

    /// <summary>Two parallel, coplanar 8 m decks along X with centerlines 12 m apart, both with an
    /// 8 m smoothing margin — corridors overlap the facing edges, footprints never touch.</summary>
    private static UnifiedRoadNetwork BuildParallelNetwork(bool overlapOn)
    {
        var rules = new BridgeRuleSystemOptions
        {
            EnableBridgeToBridgeAbutmentSuppression = true,
            EnableDeckToDeckContinuity = true,
            EnableSeamlessDeckOverlap = overlapOn,
        };

        var network = new UnifiedRoadNetwork();
        foreach (var (splineId, y, wayId) in new[] { (RampId, 100f, 700001L), (TrunkId, 112f, 700002L) })
        {
            var seg = new StructureSegment
            {
                Type = StructureType.Bridge, StartDistance = 100f, EndDistance = 300f, OsmWayIds = { wayId }
            };
            var spline = RoadNetworkTestHelpers.CreateParameterizedSpline(
                splineId, new Vector2(0, y), new Vector2(400, y), priority: 10000,
                mergeStructuresIntoCorridor: true, structureSegments: [seg]);
            spline.Parameters.BridgeRules = rules;
            spline.Parameters.TerrainAffectedRangeMeters = 8f;
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);

            var stations = new List<BridgeStation>();
            for (var d = 100f; d <= 300f; d += 5f)
                stations.Add(new BridgeStation
                {
                    Center = new Vector2(d, y),
                    Normal = new Vector2(0f, -1f),
                    Tangent = new Vector2(1f, 0f),
                    Width = 8f,
                    CenterZ = 24f, LeftEdgeZ = 24f, RightEdgeZ = 24f,
                    DistanceAlongSpline = d
                });
            network.BridgeSpans.Add(new BridgeSpanSnapshot
            {
                SplineId = splineId, SpanId = seg.SpanId, OsmWayIds = { wayId }, Stations = stations
            });
        }

        return network;
    }

    [Fact]
    public void CoplanarOverlap_OpensParapets_WithoutRequiringALandingPair()
    {
        // Manhattan render 2026-07-07 (first screenshot): walls still crossed same-level roadways
        // where the overlapping decks were not a landing PAIR (two ramps conformed onto the same
        // trunk, handoff neighbours). The criterion is geometric — coplanar footprint overlap IS the
        // union roadway — so openings must not depend on who landed on whom.
        var (rampOff, trunkOff) = ExportDecks(BuildNetwork(overlapOn: false, landingApplied: false));
        var (rampOn, trunkOn) = ExportDecks(BuildNetwork(overlapOn: true, landingApplied: false));

        Assert.True(rampOn.Vertices < rampOff.Vertices - 24,
            $"coplanar overlap must open the ramp parapets, {rampOff.Vertices} → {rampOn.Vertices}");
        Assert.True(trunkOn.Vertices < trunkOff.Vertices,
            $"coplanar overlap must open the trunk gore mouth, {trunkOff.Vertices} → {trunkOn.Vertices}");
    }
}
