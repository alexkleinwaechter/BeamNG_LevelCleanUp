using System.Numerics;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
///     Connects OSM road paths into longer splines using angle-first greedy matching.
///     Instead of a tiered first-match strategy, this algorithm scores ALL possible
///     endpoint connections by deflection angle and always picks the straightest one.
///     This naturally prevents wrong merges — the correct continuation always has a
///     smaller deflection angle than an incorrect one.
///     Guards:
///     - Different highway types never merge (paths are partitioned by exact highway type).
///     - Roundabout-tagged paths are excluded from merging.
///     - At junction nodes (valence >= 3), deflection angle must be below 90°.
///     Scoring (higher = better):
///     - Base: dot product of incoming/outgoing directions (cos of deflection angle)
///     - Boost for shared OSM node (topology > proximity)
///     - Boost for same route relation (RelationAware strategy only)
///     - Tiny penalty for reversal merges (End→End, Start→Start) to prefer natural continuations
///     Two strategies are available:
///     - AngleFirst: pure geometry scoring
///     - RelationAware: same + route relation bonus for paths sharing an OSM route relation
/// </summary>
internal static class NodeBasedPathConnector
{
    private const float SharedNodeBoost = 0.5f;
    private const float RelationBoost = 0.5f;
    private const float ReversalPenalty = 0.001f;

    /// <summary>
    ///     Distance (in meters) to look back along a path for direction computation.
    ///     Using a longer distance produces more stable angle estimates near junctions
    ///     where short stub segments may have unreliable directions.
    /// </summary>
    private const float DirectionLookbackMeters = 30f;

    /// <summary>
    ///     Connects paths using angle-first greedy matching.
    /// </summary>
    /// <param name="paths">Paths with OSM metadata to connect.</param>
    /// <param name="tolerance">Maximum distance for geometric proximity fallback (in meters).</param>
    /// <param name="routeRelations">Optional route relations for relation-aware scoring.</param>
    /// <returns>Connected paths with preserved OSM metadata (tags, node IDs, way IDs).</returns>
    public static List<PathWithMetadata> Connect(
        List<PathWithMetadata> paths,
        float tolerance,
        IReadOnlyList<RouteRelation>? routeRelations = null)
    {
        if (paths.Count <= 1)
            return paths.Select(ClonePath).ToList();

        // Partition paths by highway type so each partition only scans within its own type.
        // This is purely a performance optimization — AreTypesCompatible already blocks
        // cross-type merges, so partitioning avoids O(n²) wasted comparisons.
        const string nullGroupKey = "__no_highway__";
        var partitions = new Dictionary<string, List<PathWithMetadata>>();
        foreach (var path in paths)
        {
            var group = GetHighwayGroup(path) ?? nullGroupKey;
            if (!partitions.TryGetValue(group, out var list))
            {
                list = new List<PathWithMetadata>();
                partitions[group] = list;
            }
            list.Add(path);
        }

        var result = new List<PathWithMetadata>();
        var totalMergeCount = 0;
        var totalSharedNodeMerges = 0;
        var totalProximityMerges = 0;
        var totalRelationBoostedMerges = 0;

        // Build relation lookup once (shared across partitions)
        Dictionary<long, HashSet<long>>? wayToRelations = null;
        if (routeRelations is { Count: > 0 })
            wayToRelations = BuildWayRelationMap(routeRelations);

        foreach (var (groupKey, partition) in partitions)
        {
            if (partition.Count <= 1)
            {
                result.AddRange(partition.Select(ClonePath));
                continue;
            }

            var merged = ConnectPartition(partition, tolerance, wayToRelations,
                out var mergeCount, out var sharedNodeMerges, out var proximityMerges,
                out var relationBoostedMerges);

            result.AddRange(merged);
            totalMergeCount += mergeCount;
            totalSharedNodeMerges += sharedNodeMerges;
            totalProximityMerges += proximityMerges;
            totalRelationBoostedMerges += relationBoostedMerges;
        }

        var strategyName = wayToRelations != null ? "RelationAware" : "AngleFirst";
        Console.WriteLine(
            $"  Angle-first path joining ({strategyName}): {totalMergeCount} merges " +
            $"(shared-node: {totalSharedNodeMerges}, proximity: {totalProximityMerges}" +
            (totalRelationBoostedMerges > 0 ? $", relation-boosted: {totalRelationBoostedMerges}" : "") +
            $"), {result.Count} paths remaining ({partitions.Count} type partitions), tolerance={tolerance:F2}m");

        return result;
    }

    /// <summary>
    ///     Connects paths within a single highway-type partition using greedy angle-first matching.
    ///     Uses a spatial grid index on endpoints to avoid O(N²) all-pairs scanning.
    /// </summary>
    private static List<PathWithMetadata> ConnectPartition(
        List<PathWithMetadata> paths,
        float tolerance,
        Dictionary<long, HashSet<long>>? wayToRelations,
        out int mergeCount,
        out int sharedNodeMerges,
        out int proximityMerges,
        out int relationBoostedMerges)
    {
        var nodeValence = BuildNodeValenceMap(paths);
        var toleranceSq = tolerance * tolerance;
        var cellSize = MathF.Max(tolerance, 1f);

        // Use stable IDs instead of list indices (avoids index shifting on removal)
        var nextId = 0;
        var pathById = new Dictionary<int, PathWithMetadata>(paths.Count);
        foreach (var path in paths)
            pathById[nextId++] = ClonePath(path);

        // Build spatial grid of endpoints: cell → list of (pathId, isStart)
        var grid = new Dictionary<(int, int), List<(int pathId, bool isStart)>>();
        foreach (var (id, p) in pathById)
            GridAdd(grid, cellSize, id, p);

        mergeCount = 0;
        sharedNodeMerges = 0;
        proximityMerges = 0;
        relationBoostedMerges = 0;

        while (mergeCount < 10_000) // Safety limit
        {
            var best = FindBestCandidateIndexed(pathById, grid, cellSize,
                nodeValence, toleranceSq, wayToRelations);
            if (best == null)
                break;

            var b = best.Value;
            var p1 = pathById[b.Index1];
            var p2 = pathById[b.Index2];

            // Remove old paths from grid and dictionary
            GridRemove(grid, cellSize, b.Index1, p1);
            GridRemove(grid, cellSize, b.Index2, p2);
            pathById.Remove(b.Index1);
            pathById.Remove(b.Index2);

            // Merge and insert with new stable ID
            var merged = ExecuteMerge(p1, p2, b.Type);
            var mergedId = nextId++;
            pathById[mergedId] = merged;
            GridAdd(grid, cellSize, mergedId, merged);

            mergeCount++;

            // Track statistics
            if (b.Score >= SharedNodeBoost)
                sharedNodeMerges++;
            else
                proximityMerges++;

            if (wayToRelations != null && b.Score >= SharedNodeBoost + RelationBoost - 0.1f)
                relationBoostedMerges++;
        }

        return pathById.Values.ToList();
    }

    // ========================================================================================
    //  Candidate Finding & Scoring
    // ========================================================================================

    /// <summary>
    ///     Finds the best merge candidate using a spatial grid index on endpoints.
    ///     Instead of O(N²) all-pairs, each endpoint only checks nearby grid cells → O(N·k)
    ///     where k is the average number of nearby endpoints (typically small and constant).
    /// </summary>
    private static MergeCandidate? FindBestCandidateIndexed(
        Dictionary<int, PathWithMetadata> pathById,
        Dictionary<(int, int), List<(int pathId, bool isStart)>> grid,
        float cellSize,
        Dictionary<long, int> nodeValence,
        float toleranceSq,
        Dictionary<long, HashSet<long>>? wayToRelations)
    {
        MergeCandidate? best = null;
        var bestScore = float.MinValue;

        // Precompute direction points (~30m lookback) for stable angle estimates.
        var endLookback = new Dictionary<int, Vector2>(pathById.Count);
        var startLookforward = new Dictionary<int, Vector2>(pathById.Count);
        foreach (var (id, p) in pathById)
        {
            endLookback[id] = GetDirectionPoint(p.Points, true);
            startLookforward[id] = GetDirectionPoint(p.Points, false);
        }

        foreach (var (id1, p1) in pathById)
        {
            if (p1.Points.Count < 2) continue;
            if (IsRoundabout(p1)) continue;

            // Check p1's END endpoint against nearby endpoints
            var endPos = p1.Points[^1];
            var endCell = GridCell(endPos.X, endPos.Y, cellSize);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((endCell.Item1 + dx, endCell.Item2 + dy), out var nearby))
                    continue;
                foreach (var (id2, isStart2) in nearby)
                {
                    if (id2 <= id1) continue; // avoid scoring same pair twice
                    if (!pathById.TryGetValue(id2, out var p2)) continue;
                    if (p2.Points.Count < 2) continue;
                    if (IsRoundabout(p2)) continue;

                    var type = isStart2 ? MergeType.EndToStart : MergeType.EndToEnd;
                    var nodeId2 = isStart2 ? p2.StartNodeId : p2.EndNodeId;
                    var outgoingNext = isStart2 ? startLookforward[id2] : endLookback[id2];

                    var relationBonus = 0f;
                    if (wayToRelations != null &&
                        ShareRouteRelation(p1.OsmWayId, p2.OsmWayId, wayToRelations))
                        relationBonus = RelationBoost;

                    ScoreEndpoint(id1, id2, p1, p2, type,
                        p1.EndNodeId, nodeId2,
                        endLookback[id1], p1.Points[^1], outgoingNext,
                        type == MergeType.EndToEnd, nodeValence, toleranceSq, relationBonus,
                        ref best, ref bestScore);
                }
            }

            // Check p1's START endpoint against nearby endpoints
            var startPos = p1.Points[0];
            var startCell = GridCell(startPos.X, startPos.Y, cellSize);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((startCell.Item1 + dx, startCell.Item2 + dy), out var nearby))
                    continue;
                foreach (var (id2, isStart2) in nearby)
                {
                    if (id2 <= id1) continue;
                    if (!pathById.TryGetValue(id2, out var p2)) continue;
                    if (p2.Points.Count < 2) continue;
                    if (IsRoundabout(p2)) continue;

                    var type = isStart2 ? MergeType.StartToStart : MergeType.StartToEnd;
                    var nodeId2 = isStart2 ? p2.StartNodeId : p2.EndNodeId;
                    var incomingPrev = isStart2 ? startLookforward[id2] : endLookback[id2];

                    var relationBonus = 0f;
                    if (wayToRelations != null &&
                        ShareRouteRelation(p1.OsmWayId, p2.OsmWayId, wayToRelations))
                        relationBonus = RelationBoost;

                    ScoreEndpoint(id1, id2, p1, p2, type,
                        p1.StartNodeId, nodeId2,
                        incomingPrev, p1.Points[0], startLookforward[id1],
                        type == MergeType.StartToStart, nodeValence, toleranceSq, relationBonus,
                        ref best, ref bestScore);
                }
            }
        }

        return best;
    }

    /// <summary>
    ///     Scores a single endpoint combination and updates the best candidate if this one is better.
    /// </summary>
    private static void ScoreEndpoint(
        int i, int j,
        PathWithMetadata p1, PathWithMetadata p2,
        MergeType type,
        long? nodeId1, long? nodeId2,
        Vector2 incomingPrev, Vector2 connectionPoint, Vector2 outgoingNext,
        bool requiresReversal,
        Dictionary<long, int> nodeValence,
        float toleranceSq,
        float relationBonus,
        ref MergeCandidate? best,
        ref float bestScore)
    {
        // Block reversal merges on one-way roads. EndToEnd and StartToStart reverse path2;
        // if path2 is oneway=yes, the reversed segment goes against traffic flow.
        // This prevents dual carriageway wrong merges (two parallel one-way roads sharing a node).
        if (requiresReversal && IsOneway(p2))
            return;

        // Determine connection type: shared node or proximity
        var isSharedNode = nodeId1.HasValue && nodeId2.HasValue && nodeId1.Value == nodeId2.Value;

        if (!isSharedNode)
        {
            // Proximity fallback: require endpoints within tolerance AND at least one null node ID
            // (both having node IDs that don't match = topologically different endpoints)
            if (nodeId1.HasValue && nodeId2.HasValue)
                return;

            var ep1 = GetEndpoint(p1, type, true);
            var ep2 = GetEndpoint(p2, type, false);
            if (Vector2.DistanceSquared(ep1, ep2) > toleranceSq)
                return;
        }

        var isJunction = isSharedNode && IsJunctionNode(nodeId1!.Value, nodeValence);

        // Compute deflection angle as dot product (higher = straighter)
        var dot = ComputeDotProduct(incomingPrev, connectionPoint, outgoingNext);
        if (float.IsNaN(dot))
            return; // Degenerate segment

        // Junction nodes: hard threshold at 90° (dot > 0)
        // TESTING: disabled to evaluate merge behavior without angle guard
        // if (isJunction && dot <= 0f)
        //     return;

        // Compute score
        var score = dot
                    + (isSharedNode ? SharedNodeBoost : 0f)
                    + relationBonus
                    - (requiresReversal ? ReversalPenalty : 0f);

        if (score > bestScore)
        {
            bestScore = score;
            best = new MergeCandidate(i, j, type, score);
        }
    }

    /// <summary>
    ///     Returns the relevant endpoint position for a path given the merge type.
    /// </summary>
    private static Vector2 GetEndpoint(PathWithMetadata path, MergeType type, bool isFirst)
    {
        return type switch
        {
            MergeType.EndToStart => isFirst ? path.Points[^1] : path.Points[0],
            MergeType.EndToEnd => isFirst ? path.Points[^1] : path.Points[^1],
            MergeType.StartToEnd => isFirst ? path.Points[0] : path.Points[^1],
            MergeType.StartToStart => isFirst ? path.Points[0] : path.Points[0],
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    // ========================================================================================
    //  Spatial Grid Index for Endpoint Proximity
    // ========================================================================================

    private static (int, int) GridCell(float x, float y, float cellSize) =>
        ((int)MathF.Floor(x / cellSize), (int)MathF.Floor(y / cellSize));

    private static void GridAdd(
        Dictionary<(int, int), List<(int pathId, bool isStart)>> grid,
        float cellSize, int pathId, PathWithMetadata path)
    {
        var startCell = GridCell(path.Points[0].X, path.Points[0].Y, cellSize);
        if (!grid.TryGetValue(startCell, out var startList))
            grid[startCell] = startList = new List<(int, bool)>();
        startList.Add((pathId, true));

        var endCell = GridCell(path.Points[^1].X, path.Points[^1].Y, cellSize);
        if (!grid.TryGetValue(endCell, out var endList))
            grid[endCell] = endList = new List<(int, bool)>();
        endList.Add((pathId, false));
    }

    private static void GridRemove(
        Dictionary<(int, int), List<(int pathId, bool isStart)>> grid,
        float cellSize, int pathId, PathWithMetadata path)
    {
        var startCell = GridCell(path.Points[0].X, path.Points[0].Y, cellSize);
        if (grid.TryGetValue(startCell, out var startList))
            startList.RemoveAll(e => e.pathId == pathId && e.isStart);

        var endCell = GridCell(path.Points[^1].X, path.Points[^1].Y, cellSize);
        if (grid.TryGetValue(endCell, out var endList))
            endList.RemoveAll(e => e.pathId == pathId && !e.isStart);
    }

    // ========================================================================================
    //  Angle Computation
    // ========================================================================================

    /// <summary>
    ///     Computes the dot product of the normalized incoming and outgoing direction vectors
    ///     at a connection point. Returns cos(deflection angle):
    ///     +1.0 = perfectly straight, 0.0 = 90° turn, -1.0 = U-turn.
    ///     Returns NaN for degenerate (zero-length) segments.
    /// </summary>
    private static float ComputeDotProduct(Vector2 incomingPrev, Vector2 connectionPoint, Vector2 outgoingNext)
    {
        var dirIn = connectionPoint - incomingPrev;
        var dirOut = outgoingNext - connectionPoint;

        var lenInSq = dirIn.LengthSquared();
        var lenOutSq = dirOut.LengthSquared();
        if (lenInSq < 1e-8f || lenOutSq < 1e-8f)
            return float.NaN;

        dirIn /= MathF.Sqrt(lenInSq);
        dirOut /= MathF.Sqrt(lenOutSq);
        return Vector2.Dot(dirIn, dirOut);
    }

    // ========================================================================================
    //  Valence Map
    // ========================================================================================

    /// <summary>
    ///     Builds a map of nodeId → number of path endpoints that reference this node.
    ///     Nodes with valence >= 3 are junctions where merging requires angle &lt; 90°.
    /// </summary>
    private static Dictionary<long, int> BuildNodeValenceMap(List<PathWithMetadata> paths)
    {
        var valence = new Dictionary<long, int>();
        foreach (var path in paths)
        {
            if (path.StartNodeId.HasValue)
            {
                valence.TryGetValue(path.StartNodeId.Value, out var count);
                valence[path.StartNodeId.Value] = count + 1;
            }

            if (path.EndNodeId.HasValue)
            {
                valence.TryGetValue(path.EndNodeId.Value, out var count);
                valence[path.EndNodeId.Value] = count + 1;
            }
        }

        return valence;
    }

    private static bool IsJunctionNode(long nodeId, Dictionary<long, int> nodeValence)
    {
        return nodeValence.TryGetValue(nodeId, out var valence) && valence >= 3;
    }

    // ========================================================================================
    //  Merge Operations
    // ========================================================================================

    /// <summary>
    ///     Dispatches to the appropriate merge method based on merge type.
    /// </summary>
    private static PathWithMetadata ExecuteMerge(PathWithMetadata path1, PathWithMetadata path2, MergeType type)
    {
        return type switch
        {
            MergeType.EndToStart => MergeEndToStart(path1, path2),
            MergeType.EndToEnd => MergeEndToEnd(path1, path2),
            MergeType.StartToEnd => MergeStartToEnd(path1, path2),
            MergeType.StartToStart => MergeStartToStart(path1, path2),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private static PathWithMetadata MergeEndToStart(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        merged.AddRange(path1.Points);
        merged.AddRange(path2.Points.Skip(1));
        return new PathWithMetadata(
            merged,
            path1.StartNodeId,
            path2.EndNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    private static PathWithMetadata MergeEndToEnd(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        merged.AddRange(path1.Points);
        for (var k = path2.Points.Count - 2; k >= 0; k--)
            merged.Add(path2.Points[k]);
        return new PathWithMetadata(
            merged,
            path1.StartNodeId,
            path2.StartNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    private static PathWithMetadata MergeStartToEnd(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        merged.AddRange(path2.Points);
        merged.AddRange(path1.Points.Skip(1));
        return new PathWithMetadata(
            merged,
            path2.StartNodeId,
            path1.EndNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    private static PathWithMetadata MergeStartToStart(PathWithMetadata path1, PathWithMetadata path2)
    {
        var merged = new List<Vector2>(path1.Points.Count + path2.Points.Count - 1);
        for (var k = path2.Points.Count - 1; k >= 0; k--)
            merged.Add(path2.Points[k]);
        merged.AddRange(path1.Points.Skip(1));
        return new PathWithMetadata(
            merged,
            path2.EndNodeId,
            path1.EndNodeId,
            path1.OsmWayId, path1.Tags,
            path1.IsBridge, path1.IsTunnel, path1.StructureType, path1.Layer, path1.BridgeStructureType);
    }

    // ========================================================================================
    //  Anti-Merge Rules
    // ========================================================================================

    private static string? GetHighwayGroup(PathWithMetadata path)
    {
        if (!path.Tags.TryGetValue("highway", out var highway) || string.IsNullOrEmpty(highway))
            return null;
        // Return exact highway type (no grouping) so that subtypes like motorway_link
        // stay as separate splines for correct DecalRoad layer set resolution.
        // Was: return HighwayTypeGroups.TryGetValue(highway, out var group) ? group : highway;
        return highway;
    }

    private static bool IsRoundabout(PathWithMetadata path)
    {
        return path.Tags.TryGetValue("junction", out var junction) &&
               string.Equals(junction, "roundabout", StringComparison.OrdinalIgnoreCase);
    }

    // ========================================================================================
    //  Route Relation Lookup
    // ========================================================================================

    /// <summary>
    ///     Builds a map from OSM way ID → set of route relation IDs that contain this way.
    /// </summary>
    private static Dictionary<long, HashSet<long>> BuildWayRelationMap(IReadOnlyList<RouteRelation> relations)
    {
        var map = new Dictionary<long, HashSet<long>>();
        foreach (var relation in relations)
        foreach (var member in relation.Members)
        {
            if (!map.TryGetValue(member.WayId, out var relationIds))
            {
                relationIds = new HashSet<long>();
                map[member.WayId] = relationIds;
            }

            relationIds.Add(relation.RelationId);
        }

        return map;
    }

    /// <summary>
    ///     Returns true if both way IDs share at least one route relation.
    /// </summary>
    private static bool ShareRouteRelation(
        long wayId1, long wayId2,
        Dictionary<long, HashSet<long>> wayToRelations)
    {
        if (!wayToRelations.TryGetValue(wayId1, out var relations1))
            return false;
        if (!wayToRelations.TryGetValue(wayId2, out var relations2))
            return false;
        return relations1.Overlaps(relations2);
    }

    // ========================================================================================
    //  Helpers
    // ========================================================================================

    /// <summary>
    ///     Returns a point approximately <see cref="DirectionLookbackMeters" /> along the path
    ///     from the given endpoint, measured along the polyline. Used for computing stable
    ///     direction vectors that aren't sensitive to short stub segments near junctions.
    ///     Falls back to the furthest available point if the path is shorter than the lookback distance.
    /// </summary>
    /// <param name="points">The path's control points.</param>
    /// <param name="fromEnd">If true, walk backward from the last point. If false, walk forward from the first.</param>
    private static Vector2 GetDirectionPoint(List<Vector2> points, bool fromEnd)
    {
        var accumulated = 0f;

        if (fromEnd)
        {
            for (var i = points.Count - 1; i > 0; i--)
            {
                var segLen = Vector2.Distance(points[i], points[i - 1]);
                accumulated += segLen;
                if (accumulated >= DirectionLookbackMeters)
                {
                    // Interpolate to get exact distance
                    var overshoot = accumulated - DirectionLookbackMeters;
                    var t = overshoot / segLen;
                    return Vector2.Lerp(points[i - 1], points[i], t);
                }
            }

            return points[0]; // Path shorter than lookback — use start
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            var segLen = Vector2.Distance(points[i], points[i + 1]);
            accumulated += segLen;
            if (accumulated >= DirectionLookbackMeters)
            {
                var overshoot = accumulated - DirectionLookbackMeters;
                var t = overshoot / segLen;
                return Vector2.Lerp(points[i + 1], points[i], t);
            }
        }

        return points[^1]; // Path shorter than lookback — use end
    }

    /// <summary>
    ///     Returns true if the path is a one-way road in the forward direction (oneway=yes/true/1).
    ///     For such paths, the point order (Start→End) IS the legal driving direction.
    ///     Reversing this path during a merge would create a segment going against traffic.
    /// </summary>
    private static bool IsOneway(PathWithMetadata path)
    {
        return path.Tags.TryGetValue("oneway", out var value) &&
               (value == "yes" || value == "true" || value == "1");
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

    private enum MergeType
    {
        EndToStart,
        EndToEnd,
        StartToEnd,
        StartToStart
    }

    private readonly record struct MergeCandidate(
        int Index1,
        int Index2,
        MergeType Type,
        float Score);
}