using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     Structure-state guard on the CLASSIC-radius T-junction admission
///     (bugfix/roads_connect_to-bridges_wrong, Manhattan 2026-07-18): plan-view proximity welded the
///     ground junction of streets 154∩162 to the Manhattan Bridge deck passing overhead (spline 72,
///     span 46179737, junction station 1209.6 of [0, 1812.9]) — the junction-connected skip then
///     suppressed the pair's grade-separated crossing, and PinOnDeckJunctions raised the street
///     junction to deck Z (+11.8 m terrain dam). A mid-spline candidate INTERIOR to a bridge/tunnel
///     span may only join a junction when at least one endpoint contributor shares that structure
///     state; span edges (abutment tees) and structure-to-structure landings stay admissible.
/// </summary>
public class StructureStateTJunctionGuardTests
{
    private const int DeckId = 1;
    private const int StreetId = 2;
    private const int ThroughStreetId = 3;

    private static List<StructureSegment> BridgeSpan(float start = 50f, float end = 150f)
    {
        return [new StructureSegment { StartDistance = start, EndDistance = end, Type = StructureType.Bridge, Layer = 1 }];
    }

    /// <summary>Deck spline along the X axis, (0,0)→(200,0).</summary>
    private static ParameterizedRoadSpline CreateDeck(
        List<StructureSegment>? structureSegments = null, bool isBridge = false,
        bool mergeStructuresIntoCorridor = false)
    {
        return RoadNetworkTestHelpers.CreateParameterizedSpline(
            DeckId, new Vector2(0, 0), new Vector2(200, 0),
            osmRoadType: "secondary", priority: 75, roadWidth: 8f,
            isBridge: isBridge, structureSegments: structureSegments,
            mergeStructuresIntoCorridor: mergeStructuresIntoCorridor);
    }

    /// <summary>Street approaching the deck perpendicularly; its END sits 4 m off the deck
    /// centerline at <paramref name="x"/> — inside the classic 5 m detection radius.</summary>
    private static ParameterizedRoadSpline CreateStreet(
        float x = 100f, bool isBridge = false, List<StructureSegment>? structureSegments = null)
    {
        return RoadNetworkTestHelpers.CreateParameterizedSpline(
            StreetId, new Vector2(x, 44), new Vector2(x, 4),
            osmRoadType: "primary", priority: 80, roadWidth: 8f,
            isBridge: isBridge, structureSegments: structureSegments);
    }

    private static (UnifiedRoadNetwork network, List<NetworkJunction> junctions) Detect(
        params ParameterizedRoadSpline[] splines)
    {
        var network = new UnifiedRoadNetwork();
        foreach (var spline in splines)
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);
        return (network, new NetworkJunctionDetector().DetectJunctions(network));
    }

    private static NetworkJunction StreetEndJunction(List<NetworkJunction> junctions)
    {
        return junctions.Single(j => j.Contributors.Any(
            c => c.Spline.SplineId == StreetId && c.IsSplineEnd));
    }

    [Fact]
    public void GroundStreetEnd_UnderDeckInterior_NotAdmitted()
    {
        // The Manhattan 154 shape: a ground street dead-ends 4 m from the deck centerline while
        // the deck span [50,150] passes 100 m-station overhead. Must NOT junction onto the deck.
        var (_, junctions) = Detect(CreateDeck(BridgeSpan()), CreateStreet());

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.Endpoint, junction.Type);
        Assert.DoesNotContain(junction.Contributors, c => c.Spline.SplineId == DeckId);
    }

    [Fact]
    public void GroundStreetEnd_UnderTunnelInterior_NotAdmitted()
    {
        var tunnel = new List<StructureSegment>
        {
            new() { StartDistance = 50f, EndDistance = 150f, Type = StructureType.Tunnel, Layer = -1 }
        };
        var (_, junctions) = Detect(CreateDeck(tunnel), CreateStreet());

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.Endpoint, junction.Type);
        Assert.DoesNotContain(junction.Contributors, c => c.Spline.SplineId == DeckId);
    }

    [Fact]
    public void GroundStreetEnd_TeeingIntoThroughStreet_KeepsStreetJunction_DropsDeck()
    {
        // Full Manhattan junction-254 shape: street A tees into through street B at grade, the
        // bridge deck C passes overhead within the classic radius. The street T-junction must
        // survive WITHOUT the deck, and the deck/street pair must be recorded as a
        // grade-separated crossing (clearance machinery) instead of a junction weld.
        var street = RoadNetworkTestHelpers.CreateParameterizedSpline(
            StreetId, new Vector2(60, 40), new Vector2(100, 4),
            osmRoadType: "primary", priority: 80);
        var throughStreet = RoadNetworkTestHelpers.CreateParameterizedSpline(
            ThroughStreetId, new Vector2(100, -40), new Vector2(100, 40),
            osmRoadType: "primary", priority: 80);

        // Production shape: the app always runs MergeStructuresIntoCorridor=true, so the crossing
        // classifier reads the span's layer/bridge state (EffectiveStructureAt is flag-gated).
        var (network, junctions) = Detect(
            CreateDeck(BridgeSpan(), mergeStructuresIntoCorridor: true), street, throughStreet);

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == ThroughStreetId && c.IsContinuous);
        Assert.DoesNotContain(junction.Contributors, c => c.Spline.SplineId == DeckId);
        Assert.Contains(network.GradeSeparatedCrossings, c => c.UpperSplineId == DeckId);
    }

    [Fact]
    public void BridgeRampEnd_LandingMidDeck_Admitted()
    {
        // A ramp that IS a bridge at its end lands mid-span on the deck — deck-deck merges
        // (doc 14 landings) must keep junctioning.
        var (_, junctions) = Detect(CreateDeck(BridgeSpan()), CreateStreet(isBridge: true));

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == DeckId && c.IsContinuous);
    }

    [Fact]
    public void BridgeRampEnd_WithOwnStructureSegment_LandingMidDeck_Admitted()
    {
        // Merged-corridor style: the ramp's bridge state lives in a StructureSegment that runs to
        // its endpoint (whole-spline flag false) — must still count as on-structure there.
        var ramp = CreateStreet(structureSegments:
        [
            new StructureSegment { StartDistance = 0f, EndDistance = 40f, Type = StructureType.Bridge, Layer = 1 }
        ]);
        var (_, junctions) = Detect(CreateDeck(BridgeSpan()), ramp);

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == DeckId && c.IsContinuous);
    }

    [Fact]
    public void GroundStreetEnd_AtDeckSpanEdge_Admitted()
    {
        // Abutment tee: the street meets the deck spline within DeckEndEpsilonMeters of the span
        // start (station ~52 of span [50,150]) — a genuine connection, must keep junctioning.
        var (_, junctions) = Detect(CreateDeck(BridgeSpan()), CreateStreet(x: 52f));

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == DeckId && c.IsContinuous);
    }

    [Fact]
    public void GroundStreetEnd_UnderLegacyWholeSplineBridge_NotAdmitted()
    {
        // Legacy separate-bridge-spline network (no StructureSegments): whole-spline IsBridge with
        // the spline's own extents as the span.
        var (_, junctions) = Detect(CreateDeck(isBridge: true), CreateStreet());

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.Endpoint, junction.Type);
        Assert.DoesNotContain(junction.Contributors, c => c.Spline.SplineId == DeckId);
    }

    [Fact]
    public void GroundStreetEnd_NearLegacyBridgeSplineEnd_Admitted()
    {
        // Within DeckEndEpsilonMeters of the legacy bridge spline's own end = abutment area.
        var (_, junctions) = Detect(CreateDeck(isBridge: true), CreateStreet(x: 195f));

        var junction = StreetEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == DeckId && c.IsContinuous);
    }
}
