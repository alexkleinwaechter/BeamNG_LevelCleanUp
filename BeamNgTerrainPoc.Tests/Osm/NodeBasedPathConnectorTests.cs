using System.Numerics;
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Tests.Osm;

/// <summary>
/// Tests for NodeBasedPathConnector with relation-protected junction blocking.
///
/// Junction blocking rules (at nodes with valence >= 3):
///   - Both paths have relations, shared    → ALLOW (relation-mandated)
///   - Both paths have relations, not shared → BLOCK
///   - One has relation, other orphan        → BLOCK
///   - Both orphan                           → ALLOW (angle-based)
///
/// At non-junction nodes (valence &lt; 3), all merges use angle scoring as before.
/// </summary>
public class NodeBasedPathConnectorTests
{
    // ========================================================================================
    //  Helpers
    // ========================================================================================

    /// <summary>
    /// Creates a straight horizontal path from (startX, y) to (endX, y).
    /// Points are spaced 10m apart for stable angle computation.
    /// </summary>
    private static PathWithMetadata MakePath(
        float startX, float endX, float y,
        long osmWayId,
        long startNodeId, long endNodeId,
        string highway = "secondary",
        bool oneway = false)
    {
        var points = new List<Vector2>();
        var step = startX < endX ? 10f : -10f;
        for (var x = startX; ; x += step)
        {
            points.Add(new Vector2(x, y));
            if (MathF.Abs(x - endX) < 0.1f) break;
            if ((step > 0 && x > endX) || (step < 0 && x < endX))
            {
                points.Add(new Vector2(endX, y));
                break;
            }
        }

        var tags = new Dictionary<string, string> { ["highway"] = highway };
        if (oneway) tags["oneway"] = "yes";

        return new PathWithMetadata(
            points, startNodeId, endNodeId,
            osmWayId, tags,
            false, false, StructureType.None, 0, null);
    }

    /// <summary>
    /// Creates a straight horizontal path with explicit (nullable) node IDs and an OSM layer.
    /// Used by the layer anti-merge guard tests, where a null endpoint node ID is what lets the
    /// proximity fallback consider a merge in the first place.
    /// </summary>
    private static PathWithMetadata MakeLayeredPath(
        float startX, float endX, float y,
        long osmWayId,
        long? startNodeId, long? endNodeId,
        int layer,
        string highway = "secondary")
    {
        var points = new List<Vector2>();
        var step = startX < endX ? 10f : -10f;
        for (var x = startX; ; x += step)
        {
            points.Add(new Vector2(x, y));
            if (MathF.Abs(x - endX) < 0.1f) break;
            if ((step > 0 && x > endX) || (step < 0 && x < endX))
            {
                points.Add(new Vector2(endX, y));
                break;
            }
        }

        var tags = new Dictionary<string, string> { ["highway"] = highway };
        return new PathWithMetadata(
            points, startNodeId, endNodeId,
            osmWayId, tags,
            isBridge: layer > 0, isTunnel: false, StructureType.None, layer, null);
    }

    /// <summary>
    /// Creates a route relation containing the specified way IDs.
    /// </summary>
    private static RouteRelation MakeRelation(long relationId, params long[] wayIds)
    {
        var relation = new RouteRelation
        {
            RelationId = relationId,
            Tags = new Dictionary<string, string>
            {
                ["type"] = "route",
                ["route"] = "road",
                ["ref"] = $"R{relationId}"
            }
        };
        foreach (var wayId in wayIds)
            relation.Members.Add(new RouteRelationMember { WayId = wayId, Role = "forward" });
        return relation;
    }

    // ========================================================================================
    //  AllWayIds propagation
    // ========================================================================================

    [Fact]
    public void AllWayIds_InitializedWithOsmWayId()
    {
        var path = MakePath(0, 100, 0, osmWayId: 42, startNodeId: 1, endNodeId: 2);

        Assert.Single(path.AllWayIds);
        Assert.Contains(42L, path.AllWayIds);
    }

    [Fact]
    public void AllWayIds_PreservedThroughMerge()
    {
        // Two paths sharing node 2, same highway type
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2 },
            tolerance: 1f);

        // Should merge into single path
        Assert.Single(result);
        // Merged path must contain both way IDs
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
    }

    // ========================================================================================
    //  Junction blocking: both paths have relations, shared → ALLOW
    // ========================================================================================

    [Fact]
    public void Junction_BothShareRelation_MergesAllowed()
    {
        // Junction node 2 has valence 3 (three paths meet here)
        // p1 and p2 share relation R1, p3 is a side road (different highway type to avoid interference)
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(100, 100, 50, osmWayId: 30, startNodeId: 2, endNodeId: 4, highway: "tertiary");

        var relations = new List<RouteRelation> { MakeRelation(1, 10, 20) };

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f,
            routeRelations: relations);

        // p1 and p2 should merge (shared relation at junction), p3 stays separate
        Assert.Equal(2, result.Count);

        var mergedPath = result.FirstOrDefault(p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
        Assert.NotNull(mergedPath);
    }

    // ========================================================================================
    //  Junction blocking: both have relations, NOT shared → BLOCK
    // ========================================================================================

    [Fact]
    public void Junction_DifferentRelations_MergeBlocked()
    {
        // Three paths meet at junction node 2
        // p1 belongs to R1, p2 belongs to R2 (different relation)
        // Even though they're collinear (same y, continuation geometry), merge should be blocked
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(100, 150, 50, osmWayId: 30, startNodeId: 2, endNodeId: 4);

        var relations = new List<RouteRelation>
        {
            MakeRelation(1, 10),
            MakeRelation(2, 20)
        };

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f,
            routeRelations: relations);

        // All three should remain separate — p1-p2 merge blocked (different relations at junction)
        // p1-p3 and p2-p3 also blocked (one has relation, other has different relation)
        Assert.Equal(3, result.Count);
    }

    // ========================================================================================
    //  Junction blocking: one has relation, other orphan → BLOCK
    // ========================================================================================

    [Fact]
    public void Junction_RelationVsOrphan_MergeBlocked()
    {
        // Three paths at junction node 2
        // p1 has relation R1, p2 is orphan (no relation), p3 provides valence >= 3
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(100, 150, 50, osmWayId: 30, startNodeId: 2, endNodeId: 4);

        // Only p1 is in a relation
        var relations = new List<RouteRelation> { MakeRelation(1, 10) };

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f,
            routeRelations: relations);

        // p1 should NOT merge with p2 (relation vs orphan at junction)
        var p1Merged = result.FirstOrDefault(p => p.AllWayIds.Contains(10L));
        Assert.NotNull(p1Merged);
        Assert.DoesNotContain(20L, p1Merged.AllWayIds);
    }

    // ========================================================================================
    //  Junction blocking: both orphan → ALLOW
    // ========================================================================================

    [Fact]
    public void Junction_BothOrphan_MergeAllowed()
    {
        // Three paths at junction node 2 — none have relations
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(100, 150, 50, osmWayId: 30, startNodeId: 2, endNodeId: 4);

        // No relations — all orphans
        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f,
            routeRelations: new List<RouteRelation>());

        // p1 and p2 should merge (collinear, best angle) — orphan angle-based merge allowed
        var merged = result.FirstOrDefault(p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
        Assert.NotNull(merged);
    }

    // ========================================================================================
    //  Non-junction: relation has no blocking effect
    // ========================================================================================

    [Fact]
    public void NonJunction_RelationVsOrphan_MergeAllowed()
    {
        // Only two paths share node 2 → valence = 2 (not a junction)
        // p1 has relation, p2 orphan — should still merge at non-junction
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);

        var relations = new List<RouteRelation> { MakeRelation(1, 10) };

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2 },
            tolerance: 1f,
            routeRelations: relations);

        // Should merge — node 2 is not a junction (valence 2)
        Assert.Single(result);
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
    }

    // ========================================================================================
    //  No relations provided — pure angle-based merging (backwards compatibility)
    // ========================================================================================

    [Fact]
    public void NoRelations_AngleBasedMerging_Unchanged()
    {
        // Three paths at junction — no relations means pure angle-based merging
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(100, 150, 50, osmWayId: 30, startNodeId: 2, endNodeId: 4);

        // null relations = AngleFirst strategy
        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f,
            routeRelations: null);

        // p1-p2 are collinear, should merge via angle scoring
        var merged = result.FirstOrDefault(p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
        Assert.NotNull(merged);
    }

    // ========================================================================================
    //  Complex scenario: relation protects correct continuation at T-junction
    // ========================================================================================

    [Fact]
    public void TJunction_RelationProtectsCorrectContinuation()
    {
        // Simulate a T-junction where a ramp (p_ramp) has a geometrically plausible
        // but topologically wrong connection to the main road's continuation (p_main2).
        // The route relation should protect the correct merge: p_main1 → p_main2.

        // Main road: straight east along y=0
        var pMain1 = MakePath(0, 100, 0, osmWayId: 100, startNodeId: 1, endNodeId: 2);
        var pMain2 = MakePath(100, 200, 0, osmWayId: 200, startNodeId: 2, endNodeId: 3);

        // Ramp: approaches from slight angle (nearly collinear — this is the tricky case)
        var rampPoints = new List<Vector2>();
        for (float x = 50; x <= 100; x += 10)
        {
            var y = (x - 50f) * -0.1f; // slight angle, nearly straight
            rampPoints.Add(new Vector2(x, y));
        }
        var pRamp = new PathWithMetadata(
            rampPoints, startNodeId: 5, endNodeId: 2,
            osmWayId: 300,
            new Dictionary<string, string> { ["highway"] = "secondary" },
            false, false, StructureType.None, 0, null);

        // Route relation groups main road ways
        var relations = new List<RouteRelation> { MakeRelation(1, 100, 200) };

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { pMain1, pMain2, pRamp },
            tolerance: 1f,
            routeRelations: relations);

        // Main road should merge correctly, ramp stays separate
        var mainMerged = result.FirstOrDefault(p => p.AllWayIds.Contains(100L) && p.AllWayIds.Contains(200L));
        Assert.NotNull(mainMerged);

        // Ramp should NOT be merged with any main road path
        var rampResult = result.FirstOrDefault(p => p.AllWayIds.Contains(300L));
        Assert.NotNull(rampResult);
        Assert.DoesNotContain(100L, rampResult.AllWayIds);
        Assert.DoesNotContain(200L, rampResult.AllWayIds);
    }

    // ========================================================================================
    //  Junction: multi-relation overlap — share one of many relations → ALLOW
    // ========================================================================================

    [Fact]
    public void Junction_MultiRelationOverlap_MergeAllowed()
    {
        // Way 10 belongs to R1 and R2, way 20 belongs to R2 only.
        // They share R2, so merge should be allowed at junction.
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(100, 150, 50, osmWayId: 30, startNodeId: 2, endNodeId: 4, highway: "tertiary");

        var relations = new List<RouteRelation>
        {
            MakeRelation(1, 10),       // R1 has way 10 only
            MakeRelation(2, 10, 20)    // R2 has way 10 and way 20
        };

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f,
            routeRelations: relations);

        // p1 and p2 share relation R2 → merge allowed at junction
        var merged = result.FirstOrDefault(p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
        Assert.NotNull(merged);
    }

    // ========================================================================================
    //  AllWayIds survives multi-hop merges
    // ========================================================================================

    // ========================================================================================
    //  Layer anti-merge guard (merged-corridor bridges, plan doc 11 §4.2.3)
    // ========================================================================================

    [Fact]
    public void LayerGuard_GradeSeparatedFlyover_DoesNotMerge()
    {
        // A bridge (layer 1) whose endpoint lands near a road (layer 0) endpoint but shares NO OSM node
        // (null node IDs at the join) — a grade-separated fly-over that would only merge via the proximity
        // fallback. With the guard ON it must NOT merge.
        var bridge = MakeLayeredPath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: null, layer: 1);
        var road = MakeLayeredPath(100, 200, 0, osmWayId: 20, startNodeId: null, endNodeId: 3, layer: 0);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { bridge, road },
            tolerance: 1f,
            routeRelations: null,
            enforceLayerAntiMerge: true);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
    }

    [Fact]
    public void LayerGuard_Off_GradeSeparatedFlyover_MergesViaProximity()
    {
        // Same geometry, guard OFF (legacy default): the proximity fallback DOES merge them. This pins the
        // legacy behaviour and proves the guard — not some other rule — is what blocks the fly-over.
        var bridge = MakeLayeredPath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: null, layer: 1);
        var road = MakeLayeredPath(100, 200, 0, osmWayId: 20, startNodeId: null, endNodeId: 3, layer: 0);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { bridge, road },
            tolerance: 1f,
            routeRelations: null,
            enforceLayerAntiMerge: false);

        Assert.Single(result);
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
    }

    [Fact]
    public void LayerGuard_SharedAbutmentNode_MergesAcrossLayerChange()
    {
        // A bridge (layer 1) meeting its approach road (layer 0) at a SHARED OSM node — a real abutment.
        // The guard must allow this even though the layers differ (shared node ⇒ allow).
        var approach = MakeLayeredPath(0, 100, 0, osmWayId: 20, startNodeId: 1, endNodeId: 2, layer: 0);
        var bridge = MakeLayeredPath(100, 200, 0, osmWayId: 10, startNodeId: 2, endNodeId: 3, layer: 1);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { approach, bridge },
            tolerance: 1f,
            routeRelations: null,
            enforceLayerAntiMerge: true);

        Assert.Single(result);
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
    }

    [Fact]
    public void AllWayIds_SurvivesMultiHopMerge()
    {
        // Chain: p1 → p2 → p3 at non-junction nodes
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);
        var p3 = MakePath(200, 300, 0, osmWayId: 30, startNodeId: 3, endNodeId: 4);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2, p3 },
            tolerance: 1f);

        Assert.Single(result);
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
        Assert.Contains(30L, result[0].AllWayIds);
    }

    // ========================================================================================
    //  Global junction set + >90° deflection guard (ramp hairpin fix)
    // ========================================================================================

    /// <summary>
    /// Two ramps meeting nearly head-to-tail at shared node 2 — the off-ramp/on-ramp pair at the
    /// node where both touch a through road of a DIFFERENT highway type. Deflection ≈ 177°.
    /// </summary>
    private static (PathWithMetadata rampIn, PathWithMetadata rampOut) MakeHairpinRamps(bool oneway = true)
    {
        var rampIn = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2,
            highway: "primary_link", oneway: oneway);

        var points = new List<Vector2>();
        for (float x = 100; x >= 0; x -= 10)
            points.Add(new Vector2(x, (100 - x) * 0.06f));
        var tags = new Dictionary<string, string> { ["highway"] = "primary_link" };
        if (oneway) tags["oneway"] = "yes";
        var rampOut = new PathWithMetadata(
            points, startNodeId: 2, endNodeId: 3,
            osmWayId: 20, tags,
            false, false, StructureType.None, 0, null);

        return (rampIn, rampOut);
    }

    [Fact]
    public void GlobalJunction_HairpinRampPair_MergeBlocked()
    {
        // Node 2 is a junction with a through road of a different highway type — invisible to the
        // per-partition valence map, visible only via the global junction set.
        var (rampIn, rampOut) = MakeHairpinRamps();

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { rampIn, rampOut },
            tolerance: 1f,
            globalJunctionNodes: new HashSet<long> { 2 });

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
    }

    [Fact]
    public void BidirectionalHairpin_NoJunction_StillMerges()
    {
        // A hairpin between two BIDIRECTIONAL ways at a valence-2 node is a legitimate mountain
        // switchback — neither the junction guard (no junction) nor the oneway U-turn guard
        // (not oneway) may block it.
        var (rampIn, rampOut) = MakeHairpinRamps(oneway: false);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { rampIn, rampOut },
            tolerance: 1f);

        Assert.Single(result);
    }

    [Fact]
    public void OnewayUTurn_DualCarriagewayThroat_MergeBlocked_WithoutJunction()
    {
        // The B416 Winninger Straße case: two oneway carriageways rejoin head-to-tail at a node
        // whose only other ways (highway=path) are outside the downloaded network — no junction
        // is detectable. The oneway U-turn guard must still block the hairpin.
        var (rampIn, rampOut) = MakeHairpinRamps(oneway: true);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { rampIn, rampOut },
            tolerance: 1f);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
    }

    [Fact]
    public void OnewayUTurn_DilutedChainTags_EndpointLaneSegmentStillBlocks()
    {
        // A Tier-0 chain keeps only its BASE way's tags. A chain seeded from a bidirectional way
        // but ENDING in a oneway carriageway piece must still trigger the U-turn guard — the
        // endpoint's lane segment, not the diluted path tags, carries the truth.
        var (rampIn, rampOut) = MakeHairpinRamps(oneway: true);
        rampIn.Tags.Remove("oneway");
        rampIn.LaneSegments =
        [
            new LaneSegment { StartPointIndex = 0, LaneInfo = new OsmLaneInfo { TotalLanes = 2 } },
            new LaneSegment { StartPointIndex = 5, LaneInfo = new OsmLaneInfo { TotalLanes = 1, IsOneWay = true } }
        ];

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { rampIn, rampOut },
            tolerance: 1f);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
    }

    [Fact]
    public void OnewayModerateAngle_StillMerges()
    {
        // Two oneway ways bending ~100° at a valence-2 node — sharper than the 90° junction
        // threshold but flatter than the 120° oneway U-turn threshold. Must still merge.
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2,
            highway: "primary_link", oneway: true);

        var points = new List<Vector2>();
        for (var k = 0; k <= 10; k++)
            points.Add(new Vector2(100f - 1.74f * k, 9.85f * k));
        var p2 = new PathWithMetadata(
            points, startNodeId: 2, endNodeId: 3,
            osmWayId: 20,
            new Dictionary<string, string> { ["highway"] = "primary_link", ["oneway"] = "yes" },
            false, false, StructureType.None, 0, null);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2 },
            tolerance: 1f);

        Assert.Single(result);
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
    }

    [Fact]
    public void GlobalJunction_StraightContinuation_StillMerges()
    {
        // A road split at a junction node continues straight through it — deflection ≈ 0°,
        // the angle guard must not block that.
        var p1 = MakePath(0, 100, 0, osmWayId: 10, startNodeId: 1, endNodeId: 2);
        var p2 = MakePath(100, 200, 0, osmWayId: 20, startNodeId: 2, endNodeId: 3);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { p1, p2 },
            tolerance: 1f,
            globalJunctionNodes: new HashSet<long> { 2 });

        Assert.Single(result);
        Assert.Contains(10L, result[0].AllWayIds);
        Assert.Contains(20L, result[0].AllWayIds);
    }

    [Fact]
    public void PartitionValence_HairpinAtSameTypeJunction_MergeBlocked()
    {
        // Even WITHOUT the global set, a valence-3 node within one partition is a junction —
        // the re-enabled deflection guard must block the hairpin there too.
        var (rampIn, rampOut) = MakeHairpinRamps();
        var third = MakePath(100, 200, 40, osmWayId: 30, startNodeId: 2, endNodeId: 4,
            highway: "primary_link", oneway: true);

        var result = NodeBasedPathConnector.Connect(
            new List<PathWithMetadata> { rampIn, rampOut, third },
            tolerance: 1f);

        Assert.DoesNotContain(result, p => p.AllWayIds.Contains(10L) && p.AllWayIds.Contains(20L));
    }
}
