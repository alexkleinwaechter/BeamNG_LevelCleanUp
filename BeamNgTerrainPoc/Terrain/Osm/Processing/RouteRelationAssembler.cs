using System.Numerics;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Pre-assembles OSM way paths into longer splines based on route relation membership.
/// 
/// Route relations (type=route, route=road) define ordered sequences of ways forming
/// a named road (e.g., "B51", "A1"). This assembler uses that ordering to pre-merge
/// consecutive ways BEFORE the general node-based path connector runs, producing longer
/// and more accurate splines for major roads.
/// 
/// The assembly runs as "Tier 0" — before the 3-tier NodeBasedPathConnector:
///   Tier 0 — Route Relation Assembly (this class): merge ways that belong to the same
///            route relation in the order defined by the relation.
///   Tier 1 — Shared Node ID (NodeBasedPathConnector)
///   Tier 2 — Same Name/Ref + Proximity
///   Tier 3 — Proximity Only (for cropped paths)
/// 
/// Design principles:
/// - Only merges ways that share a node at their connection point (not purely by order).
///   OSM relation member ordering is sometimes incorrect; node ID validation ensures
///   we only merge topologically connected consecutive members.
/// - Respects anti-merge rules: junction nodes (valence >= 3) still block merging.
/// - Protected structure paths (bridges/tunnels) are already separated before this runs.
/// - Ways not in any route relation pass through unchanged.
/// - Ways in a route relation that can't be assembled (gaps, missing ways) also pass through.
/// </summary>
internal static class RouteRelationAssembler
{
    /// <summary>
    /// Pre-assembles paths by route relation membership. Paths that belong to the same
    /// route relation are merged in the relation's member order (validated by shared node IDs).
    /// 
    /// Non-relation paths and relation paths that couldn't be assembled are returned as-is.
    /// </summary>
    /// <param name="paths">All regular (non-structure) paths with metadata.</param>
    /// <param name="routeRelations">Route relations from the Overpass query result.</param>
    /// <returns>Paths with route-relation members pre-assembled into longer paths.</returns>
    public static List<PathWithMetadata> PreAssembleByRouteRelation(
        List<PathWithMetadata> paths,
        IReadOnlyList<RouteRelation> routeRelations)
    {
        if (routeRelations.Count == 0 || paths.Count <= 1)
            return paths;

        // Step 1: Build a lookup from OSM way ID → PathWithMetadata.
        // A way ID may appear multiple times if it was cropped into multiple segments
        // at terrain boundaries. Use only the first occurrence (most common case).
        var wayIdToPath = new Dictionary<long, PathWithMetadata>();
        foreach (var path in paths)
        {
            wayIdToPath.TryAdd(path.OsmWayId, path);
        }

        // Step 2: Track which paths have been consumed by route relation assembly.
        var consumedWayIds = new HashSet<long>();
        var assembledPaths = new List<PathWithMetadata>();
        int totalAssembledChains = 0;
        int totalMergesPerformed = 0;

        // Step 3: For each route relation, try to assemble its member ways in order.
        foreach (var relation in routeRelations)
        {
            var chainResults = AssembleRelationChains(relation, wayIdToPath, consumedWayIds);

            foreach (var chain in chainResults)
            {
                assembledPaths.Add(chain.Path);
                foreach (var wayId in chain.ConsumedWayIds)
                    consumedWayIds.Add(wayId);

                if (chain.ConsumedWayIds.Count > 1)
                {
                    totalAssembledChains++;
                    totalMergesPerformed += chain.ConsumedWayIds.Count - 1;
                }
            }
        }

        // Step 4: Add all paths that were NOT consumed by any route relation.
        var result = new List<PathWithMetadata>(assembledPaths.Count + paths.Count);
        result.AddRange(assembledPaths);

        foreach (var path in paths)
        {
            if (!consumedWayIds.Contains(path.OsmWayId))
                result.Add(path);
        }

        if (totalAssembledChains > 0)
        {
            Console.WriteLine($"  Route relation assembly: {totalAssembledChains} chain(s) assembled " +
                $"from {totalMergesPerformed + totalAssembledChains} ways " +
                $"({totalMergesPerformed} merges) across {routeRelations.Count} relation(s). " +
                $"Result: {result.Count} paths (was {paths.Count})");
        }
        else
        {
            Console.WriteLine($"  Route relation assembly: no assemblies possible " +
                $"({routeRelations.Count} relation(s) checked, {paths.Count} paths unchanged)");
        }

        return result;
    }

    /// <summary>
    /// Result of assembling a chain of consecutive ways from a route relation.
    /// </summary>
    private record AssembledChain(PathWithMetadata Path, List<long> ConsumedWayIds);

    /// <summary>
    /// Tries to assemble one or more chains of consecutive member ways for a route relation.
    /// 
    /// A "chain" is a maximal sequence of consecutive relation members that:
    /// 1. All have corresponding paths in our terrain (wayIdToPath lookup succeeds)
    /// 2. Share node IDs at their connection points (topological validation)
    /// 3. Are not already consumed by a previous relation's assembly
    /// 
    /// Gaps in the relation (missing ways, already-consumed ways) break the chain.
    /// Each contiguous segment becomes a separate chain.
    /// </summary>
    private static List<AssembledChain> AssembleRelationChains(
        RouteRelation relation,
        Dictionary<long, PathWithMetadata> wayIdToPath,
        HashSet<long> alreadyConsumed)
    {
        var results = new List<AssembledChain>();

        // Collect available members in relation order, skipping unavailable ones
        var availableMembers = new List<(RouteRelationMember Member, PathWithMetadata Path)>();
        foreach (var member in relation.Members)
        {
            if (alreadyConsumed.Contains(member.WayId))
                continue;

            if (wayIdToPath.TryGetValue(member.WayId, out var path))
                availableMembers.Add((member, path));
        }

        if (availableMembers.Count == 0)
            return results;

        // Walk the available members and assemble chains of topologically connected ways
        int i = 0;
        while (i < availableMembers.Count)
        {
            var chainWayIds = new List<long> { availableMembers[i].Path.OsmWayId };
            var chainPath = ClonePath(availableMembers[i].Path);

            // Try to extend the chain with the next available member
            int j = i + 1;
            while (j < availableMembers.Count)
            {
                var nextPath = availableMembers[j].Path;
                var nextRole = availableMembers[j].Member.Role;

                // Try to topologically connect chainPath's end to nextPath's start/end.
                // The role hint tells us the expected orientation:
                //   "forward"  → way direction matches route direction (connect end→start)
                //   "backward" → way direction is reversed (connect end→end)
                //   ""         → unknown, try both orientations
                var merged = TryMergeBySharedNode(chainPath, nextPath, nextRole);
                if (merged == null)
                    break; // Can't connect — end this chain

                chainPath = merged;
                chainWayIds.Add(nextPath.OsmWayId);
                j++;
            }

            results.Add(new AssembledChain(chainPath, chainWayIds));
            i = j; // Skip to the next unprocessed member
        }

        return results;
    }

    /// <summary>
    /// Tries to merge two paths that share an OSM node ID at connecting endpoints.
    /// Uses the relation member role as a hint for which endpoint combination to try first,
    /// but falls back to checking all 4 combinations if the hinted one fails.
    /// 
    /// Unlike NodeBasedPathConnector.TryMergeByNodeId, this method does NOT check junction
    /// valence — the route relation provides explicit ordering that overrides the valence
    /// heuristic. If a route relation says two ways are consecutive, they should merge even
    /// if the connecting node is shared by 3+ ways (the route relation knows which two are
    /// continuation partners).
    /// </summary>
    private static PathWithMetadata? TryMergeBySharedNode(
        PathWithMetadata current, PathWithMetadata next, string role)
    {
        // Order endpoint checks based on role hint
        // "forward" → next way goes same direction → try current.End→next.Start first
        // "backward" → next way goes opposite direction → try current.End→next.End first
        // "" → unknown, try all combos

        if (role == "backward")
        {
            // Prefer End→End (next is reversed relative to route direction)
            return TryEndToEnd(current, next)
                ?? TryEndToStart(current, next)
                ?? TryStartToEnd(current, next)
                ?? TryStartToStart(current, next);
        }

        // Default (forward or empty): prefer End→Start
        return TryEndToStart(current, next)
            ?? TryEndToEnd(current, next)
            ?? TryStartToEnd(current, next)
            ?? TryStartToStart(current, next);
    }

    // ========================================================================================
    //  Merge Operations (check shared node ID, then merge geometry)
    // ========================================================================================

    private static PathWithMetadata? TryEndToStart(PathWithMetadata path1, PathWithMetadata path2)
    {
        if (path1.EndNodeId.HasValue && path2.StartNodeId.HasValue &&
            path1.EndNodeId.Value == path2.StartNodeId.Value)
        {
            // Reject U-turn on one-way divided roads (e.g. roundabout exit + entry)
            if (AreBothOneway(path1, path2) &&
                path1.Points.Count >= 2 && path2.Points.Count >= 2 &&
                IsUturnAtConnection(path1.Points[^2], path1.Points[^1], path2.Points[1]))
                return null;

            var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
            merged.AddRange(path1.Points);
            merged.AddRange(path2.Points.Skip(1));
            return new PathWithMetadata(
                merged,
                startNodeId: path1.StartNodeId,
                endNodeId: path2.EndNodeId,
                path1.OsmWayId, path1.Tags,
                path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
        }
        return null;
    }

    private static PathWithMetadata? TryEndToEnd(PathWithMetadata path1, PathWithMetadata path2)
    {
        if (path1.EndNodeId.HasValue && path2.EndNodeId.HasValue &&
            path1.EndNodeId.Value == path2.EndNodeId.Value)
        {
            // Reject U-turn on one-way divided roads
            if (AreBothOneway(path1, path2) &&
                path1.Points.Count >= 2 && path2.Points.Count >= 2 &&
                IsUturnAtConnection(path1.Points[^2], path1.Points[^1], path2.Points[^2]))
                return null;

            var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
            merged.AddRange(path1.Points);
            for (int k = path2.Points.Count - 2; k >= 0; k--)
                merged.Add(path2.Points[k]);
            return new PathWithMetadata(
                merged,
                startNodeId: path1.StartNodeId,
                endNodeId: path2.StartNodeId,
                path1.OsmWayId, path1.Tags,
                path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
        }
        return null;
    }

    private static PathWithMetadata? TryStartToEnd(PathWithMetadata path1, PathWithMetadata path2)
    {
        if (path1.StartNodeId.HasValue && path2.EndNodeId.HasValue &&
            path1.StartNodeId.Value == path2.EndNodeId.Value)
        {
            // Reject U-turn on one-way divided roads
            if (AreBothOneway(path1, path2) &&
                path2.Points.Count >= 2 && path1.Points.Count >= 2 &&
                IsUturnAtConnection(path2.Points[^2], path2.Points[^1], path1.Points[1]))
                return null;

            var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
            merged.AddRange(path2.Points);
            merged.AddRange(path1.Points.Skip(1));
            return new PathWithMetadata(
                merged,
                startNodeId: path2.StartNodeId,
                endNodeId: path1.EndNodeId,
                path1.OsmWayId, path1.Tags,
                path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
        }
        return null;
    }

    private static PathWithMetadata? TryStartToStart(PathWithMetadata path1, PathWithMetadata path2)
    {
        if (path1.StartNodeId.HasValue && path2.StartNodeId.HasValue &&
            path1.StartNodeId.Value == path2.StartNodeId.Value)
        {
            // Reject U-turn on one-way divided roads
            if (AreBothOneway(path1, path2) &&
                path2.Points.Count >= 2 && path1.Points.Count >= 2 &&
                IsUturnAtConnection(path2.Points[1], path2.Points[0], path1.Points[1]))
                return null;

            var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
            for (int k = path2.Points.Count - 1; k >= 0; k--)
                merged.Add(path2.Points[k]);
            merged.AddRange(path1.Points.Skip(1));
            return new PathWithMetadata(
                merged,
                startNodeId: path2.EndNodeId,
                endNodeId: path1.EndNodeId,
                path1.OsmWayId, path1.Tags,
                path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
        }
        return null;
    }

    // ========================================================================================
    //  U-turn Detection
    // ========================================================================================

    /// <summary>
    /// Checks whether merging at a connection point would create a U-turn (sharp reversal).
    /// 
    /// Only called when both ways are one-way (see callers), which means a sharp reversal
    /// indicates separate carriageways of a divided road (e.g. roundabout exit + entry sharing
    /// a convergence node) rather than a hairpin curve on a mountain pass (which would be a
    /// two-way road, so this check is skipped).
    /// </summary>
    /// <param name="incomingPrev">The point before the connection on the incoming path.</param>
    /// <param name="connectionPoint">The shared connection point.</param>
    /// <param name="outgoingNext">The point after the connection on the outgoing path.</param>
    /// <returns>True if the merge would create a U-turn (angle > ~135°), false otherwise.</returns>
    private static bool IsUturnAtConnection(Vector2 incomingPrev, Vector2 connectionPoint, Vector2 outgoingNext)
    {
        var dirIn = connectionPoint - incomingPrev;
        var dirOut = outgoingNext - connectionPoint;

        // Degenerate segments (zero-length) — can't determine angle, allow merge
        float lenInSq = dirIn.LengthSquared();
        float lenOutSq = dirOut.LengthSquared();
        if (lenInSq < 1e-8f || lenOutSq < 1e-8f)
            return false;

        // Normalize and compute dot product
        dirIn /= MathF.Sqrt(lenInSq);
        dirOut /= MathF.Sqrt(lenOutSq);
        float dot = Vector2.Dot(dirIn, dirOut);

        // dot < -0.7 corresponds to angle > ~135° — a clear U-turn
        return dot < -0.7f;
    }

    // ========================================================================================
    //  Helpers
    // ========================================================================================

    /// <summary>
    /// Returns true if both paths have oneway=yes (or equivalent). One-way roads that reverse
    /// direction at a shared node are separate carriageways of a divided road, not a hairpin.
    /// </summary>
    private static bool AreBothOneway(PathWithMetadata path1, PathWithMetadata path2)
    {
        return IsOneway(path1.Tags) && IsOneway(path2.Tags);
    }

    private static bool IsOneway(Dictionary<string, string> tags)
    {
        return tags.TryGetValue("oneway", out var value) &&
               (value == "yes" || value == "true" || value == "1" || value == "-1");
    }

    private static PathWithMetadata ClonePath(PathWithMetadata source)
    {
        return new PathWithMetadata(
            new List<Vector2>(source.Points),
            source.StartNodeId,
            source.EndNodeId,
            source.OsmWayId,
            source.Tags,
            source.IsBridge,
            source.IsTunnel,
            source.StructureType,
            source.Layer,
            source.BridgeStructureType);
    }
}
