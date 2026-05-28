using System.Numerics;
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
}
