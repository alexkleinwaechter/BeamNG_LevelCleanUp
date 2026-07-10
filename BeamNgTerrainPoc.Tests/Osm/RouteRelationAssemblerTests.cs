using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
///     Tests for the Tier-0 oneway U-turn guard in <see cref="RouteRelationAssembler"/>.
///     A route relation contains BOTH carriageways of a dual carriageway, so blind stitching of
///     consecutive members can close a hairpin loop through a carriageway tip node (the B416
///     "Winninger Straße" ring: bidirectional single-carriageway ways at both ends, oneway
///     carriageway pairs between them). The old guard read the merged chain's BASE tags, so a
///     chain seeded from a bidirectional way bypassed it — oneway-ness must come from the lane
///     segment covering the connecting endpoint.
/// </summary>
public class RouteRelationAssemblerTests
{
    /// <summary>Creates a path with a single lane segment carrying the oneway flag, as production seeding does.</summary>
    private static PathWithMetadata MakePath(
        long wayId, long startNodeId, long endNodeId, List<Vector2> points, bool oneway)
    {
        var tags = new Dictionary<string, string> { ["highway"] = "primary" };
        if (oneway) tags["oneway"] = "yes";
        var path = new PathWithMetadata(
            points, startNodeId, endNodeId, wayId, tags,
            false, false, StructureType.None, 0, null);
        path.LaneSegments =
        [
            new LaneSegment
            {
                StartPointIndex = 0,
                LaneInfo = new OsmLaneInfo { TotalLanes = oneway ? 1 : 2, IsOneWay = oneway }
            }
        ];
        return path;
    }

    private static RouteRelation MakeRelation(long relationId, params long[] wayIds)
    {
        var relation = new RouteRelation
        {
            RelationId = relationId,
            Tags = new Dictionary<string, string>
            {
                ["type"] = "route",
                ["route"] = "road",
                ["ref"] = "B 416"
            }
        };
        foreach (var wayId in wayIds)
            relation.Members.Add(new RouteRelationMember { WayId = wayId, Role = "" });
        return relation;
    }

    /// <summary>
    /// The B416 ring, simplified: south tip node 200 at (0,0), north tip node 300 at (0,100).
    /// A bidirectional way feeds the south tip, the two oneway carriageways run between the tips
    /// (east side northbound, west side southbound), a bidirectional way leaves the north tip.
    /// </summary>
    private static (PathWithMetadata bidirSouth, PathWithMetadata eastNorthbound,
        PathWithMetadata westSouthbound, PathWithMetadata bidirNorth) MakeDualCarriagewayRing()
    {
        var bidirSouth = MakePath(1, 100, 200,
            [new(0, -40), new(0, -20), new(0, 0)], oneway: false);

        var eastPoints = new List<Vector2> { new(0, 0) };
        for (var k = 1; k <= 9; k++) eastPoints.Add(new(2, k * 10));
        eastPoints.Add(new(0, 100));
        var eastNorthbound = MakePath(2, 200, 300, eastPoints, oneway: true);

        var westPoints = new List<Vector2> { new(0, 100) };
        for (var k = 9; k >= 1; k--) westPoints.Add(new(-2, k * 10));
        westPoints.Add(new(0, 0));
        var westSouthbound = MakePath(3, 300, 200, westPoints, oneway: true);

        var bidirNorth = MakePath(4, 300, 400,
            [new(0, 100), new(0, 120), new(0, 140)], oneway: false);

        return (bidirSouth, eastNorthbound, westSouthbound, bidirNorth);
    }

    [Fact]
    public void DualCarriagewayRing_ChainSeededFromBidirectionalWay_DoesNotCloseHairpin()
    {
        // Relation order walks the chain INTO the ring via the bidirectional south way, so the
        // chain's base tags are bidirectional — the old whole-path oneway check was blind here
        // and stitched east + west carriageways through the north tip (deflection ≈ 178°).
        var (bidirSouth, eastNb, westSb, bidirNorth) = MakeDualCarriagewayRing();
        var relations = new List<RouteRelation> { MakeRelation(179405, 1, 2, 3, 4) };

        var result = RouteRelationAssembler.PreAssembleByRouteRelation(
            [bidirSouth, eastNb, westSb, bidirNorth], relations);

        // The two carriageways must never end up in the same chain.
        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(2L) && p.AllWayIds.Contains(3L));
    }

    [Fact]
    public void DualCarriagewayRing_SouthTip_AlsoBlocked()
    {
        // Same ring, relation order arriving at the SOUTH tip with an oneway chain end:
        // west southbound (ends at node 200) followed by east northbound (starts at node 200).
        var (_, eastNb, westSb, _) = MakeDualCarriagewayRing();
        var relations = new List<RouteRelation> { MakeRelation(179405, 3, 2) };

        var result = RouteRelationAssembler.PreAssembleByRouteRelation(
            [eastNb, westSb], relations);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(2L) && p.AllWayIds.Contains(3L));
    }

    [Fact]
    public void StraightChain_MixedBidirAndOneway_StillAssembles()
    {
        // A collinear bidir → oneway → bidir chain is a normal single-carriageway road whose
        // middle piece happens to be oneway (e.g. a short bridge) — must still assemble fully.
        var w1 = MakePath(1, 100, 200, [new(0, 0), new(0, 15), new(0, 30)], oneway: false);
        var w2 = MakePath(2, 200, 300, [new(0, 30), new(0, 45), new(0, 60)], oneway: true);
        var w3 = MakePath(3, 300, 400, [new(0, 60), new(0, 75), new(0, 90)], oneway: false);
        var relations = new List<RouteRelation> { MakeRelation(7, 1, 2, 3) };

        var result = RouteRelationAssembler.PreAssembleByRouteRelation([w1, w2, w3], relations);

        var chain = Assert.Single(result);
        Assert.Contains(1L, chain.AllWayIds);
        Assert.Contains(2L, chain.AllWayIds);
        Assert.Contains(3L, chain.AllWayIds);
    }
}
