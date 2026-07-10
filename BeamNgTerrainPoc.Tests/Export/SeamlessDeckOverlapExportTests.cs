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
