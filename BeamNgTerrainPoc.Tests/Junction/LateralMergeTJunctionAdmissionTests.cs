using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

/// <summary>
///     Lateral-merge T-junction admission (ai_docs/2026-07-10, Manhattan spline 57 floating ramp):
///     a laterally-merged corridor's centerline is the MIDLINE of the two original carriageways, so
///     a ramp endpoint that shared an OSM node with one carriageway sits ~half the carriageway
///     separation away — beyond the 5 m default detection radius. The detector must admit such
///     endpoints by the corridor's painted surface half-width, guarded by bridge/tunnel-state
///     agreement so streets dead-ending UNDER an elevated corridor do not junction onto the deck.
/// </summary>
public class LateralMergeTJunctionAdmissionTests
{
    private const int CorridorId = 1;
    private const int RampId = 2;

    /// <summary>Merged corridor along the X axis, 24 m wide (surface half-width 12 m).</summary>
    private static ParameterizedRoadSpline CreateCorridor(
        bool laterallyMerged = true,
        List<StructureSegment>? structureSegments = null)
    {
        var corridor = RoadNetworkTestHelpers.CreateParameterizedSpline(
            CorridorId, new Vector2(0, 0), new Vector2(200, 0),
            osmRoadType: "motorway", priority: 100, roadWidth: 24f,
            structureSegments: structureSegments);
        corridor.IsLaterallyMerged = laterallyMerged;
        return corridor;
    }

    /// <summary>Ramp approaching the corridor; its END sits <paramref name="lateralOffset"/> m
    /// from the corridor centerline — on the old carriageway's alignment.</summary>
    private static ParameterizedRoadSpline CreateRamp(float lateralOffset, bool isBridge = false)
    {
        return RoadNetworkTestHelpers.CreateParameterizedSpline(
            RampId, new Vector2(110, 40 + lateralOffset), new Vector2(105, lateralOffset),
            osmRoadType: "motorway_link", priority: 95, roadWidth: 8f, isBridge: isBridge);
    }

    private static List<NetworkJunction> Detect(params ParameterizedRoadSpline[] splines)
    {
        var network = new UnifiedRoadNetwork();
        foreach (var spline in splines)
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, spline);
        return new NetworkJunctionDetector().DetectJunctions(network);
    }

    private static NetworkJunction RampEndJunction(List<NetworkJunction> junctions)
    {
        return junctions.Single(j => j.Contributors.Any(
            c => c.Spline.SplineId == RampId && c.IsSplineEnd));
    }

    [Fact]
    public void EndpointBeyondRadius_OnMergedCorridorSurface_BecomesTJunction()
    {
        // 6 m off the merged centerline: outside the 5 m radius, inside the 12+1 m surface.
        var junctions = Detect(CreateCorridor(), CreateRamp(lateralOffset: 6f));

        var junction = RampEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == CorridorId && c.IsContinuous);
    }

    [Fact]
    public void EndpointBeyondRadius_NonMergedSpline_StaysIsolated()
    {
        var junctions = Detect(
            CreateCorridor(laterallyMerged: false), CreateRamp(lateralOffset: 6f));

        var junction = RampEndJunction(junctions);
        Assert.Equal(JunctionType.Endpoint, junction.Type);
        Assert.DoesNotContain(junction.Contributors, c => c.Spline.SplineId == CorridorId);
    }

    [Fact]
    public void EndpointBeyondSurfaceHalfWidth_NotAdmitted()
    {
        // 14 m > 12 m half-width + 1 m margin — off the roadway, stays isolated.
        var junctions = Detect(CreateCorridor(), CreateRamp(lateralOffset: 14f));

        var junction = RampEndJunction(junctions);
        Assert.Equal(JunctionType.Endpoint, junction.Type);
    }

    [Fact]
    public void GroundEndpointUnderMergedDeck_NotAdmitted()
    {
        // Corridor is a bridge deck over [50, 150] m; a ground-level street dead-ends beneath it.
        // Bridge/tunnel-state mismatch must block the widened admission.
        var corridor = CreateCorridor(structureSegments:
        [
            new StructureSegment { StartDistance = 50f, EndDistance = 150f, Type = StructureType.Bridge, Layer = 1 }
        ]);
        var junctions = Detect(corridor, CreateRamp(lateralOffset: 6f));

        var junction = RampEndJunction(junctions);
        Assert.Equal(JunctionType.Endpoint, junction.Type);
        Assert.DoesNotContain(junction.Contributors, c => c.Spline.SplineId == CorridorId);
    }

    [Fact]
    public void BridgeRampEnd_OntoMergedDeck_Admitted()
    {
        // The Manhattan spline-57 shape: a bridge ramp lands on the merged corridor's deck span.
        var corridor = CreateCorridor(structureSegments:
        [
            new StructureSegment { StartDistance = 50f, EndDistance = 150f, Type = StructureType.Bridge, Layer = 1 }
        ]);
        var junctions = Detect(corridor, CreateRamp(lateralOffset: 6f, isBridge: true));

        var junction = RampEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == CorridorId && c.IsContinuous);
    }

    [Fact]
    public void WithinClassicRadius_BehavesAsBefore_EvenWithoutMergeFlag()
    {
        // 4 m offset is inside the classic radius — admission must not depend on the new path.
        var junctions = Detect(
            CreateCorridor(laterallyMerged: false), CreateRamp(lateralOffset: 4f));

        var junction = RampEndJunction(junctions);
        Assert.Equal(JunctionType.TJunction, junction.Type);
        Assert.Contains(junction.Contributors,
            c => c.Spline.SplineId == CorridorId && c.IsContinuous);
    }
}
