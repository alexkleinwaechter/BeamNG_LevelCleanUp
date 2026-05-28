using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     A node in the elevation graph, representing a junction or a synthetic endpoint.
/// </summary>
public class ElevationNode
{
    public int NodeId { get; init; }
    public Vector2 Position { get; init; }
    public List<ElevationEdge> ConnectedEdges { get; } = [];

    /// <summary>
    ///     True if this node was synthesized for a dead-end endpoint (not a real junction).
    /// </summary>
    public bool IsSynthetic { get; init; }

    /// <summary>
    ///     Reference to the original junction, if this node came from one.
    /// </summary>
    public NetworkJunction? SourceJunction { get; init; }

    /// <summary>
    ///     Number of edges connected to this node.
    /// </summary>
    public int Degree => ConnectedEdges.Count;
}

/// <summary>
///     An edge in the elevation graph, representing a single spline connecting two nodes.
/// </summary>
public class ElevationEdge
{
    public int SplineId { get; init; }
    public ElevationNode StartNode { get; init; } = null!;
    public ElevationNode EndNode { get; init; } = null!;
    public int Priority { get; init; }
    public float Length { get; init; }
    public int CrossSectionCount { get; init; }
    public bool IsBridge { get; init; }
    public bool IsTunnel { get; init; }
    public string? OsmRoadType { get; init; }
    public float RoadWidth { get; init; }

    /// <summary>
    ///     True if the spline's geometric direction is EndNode→StartNode
    ///     (i.e., the spline's start point is closer to EndNode).
    /// </summary>
    public bool IsReversed { get; set; }
}

/// <summary>
///     An ordered sequence of edges forming a continuous path through the elevation graph.
///     The elevation smoother filters each chain as one long profile.
/// </summary>
public class ElevationChain
{
    public int ChainId { get; init; }
    public List<(ElevationEdge Edge, bool TraverseReversed)> Segments { get; } = [];

    /// <summary>
    ///     Cross-sections that were deduped during concatenation.
    ///     Each pair maps a skipped CS to the kept CS it should copy elevation from.
    ///     Set by ConcatenateChainCrossSections, consumed by PropagateToDeduped.
    /// </summary>
    public List<(UnifiedCrossSection skipped, UnifiedCrossSection kept)>? DedupPairs { get; set; }

    /// <summary>
    ///     Total cross-section count across all segments (after dedup).
    /// </summary>
    public int TotalCrossSectionCount => Segments.Sum(s => s.Edge.CrossSectionCount);

    /// <summary>
    ///     Total length in meters across all segments.
    /// </summary>
    public float TotalLength => Segments.Sum(s => s.Edge.Length);
}

/// <summary>
///     Builds an elevation graph from the road network's junction topology and chains splines
///     into long elevation runs for network-aware smoothing.
///
///     Lifecycle: Ephemeral — built at the start of Phase 2, consumed during elevation filtering,
///     and not retained for later phases. Phase 3's DetectJunctions() calls network.Junctions.Clear()
///     which would invalidate graph references.
/// </summary>
public class NetworkElevationGraph
{
    private readonly List<ElevationNode> _nodes = [];
    private readonly List<ElevationEdge> _edges = [];
    private readonly Dictionary<int, ElevationEdge> _edgeBySplineId = new();
    private int _nextNodeId;

    /// <summary>All nodes in the graph.</summary>
    public IReadOnlyList<ElevationNode> Nodes => _nodes;

    /// <summary>All edges in the graph.</summary>
    public IReadOnlyList<ElevationEdge> Edges => _edges;

    /// <summary>
    ///     Builds the elevation graph from an existing road network with detected junctions.
    /// </summary>
    /// <param name="network">Network with junctions already detected (Phase 1.8).</param>
    public void BuildFromNetwork(UnifiedRoadNetwork network)
    {
        _nodes.Clear();
        _edges.Clear();
        _edgeBySplineId.Clear();
        _nextNodeId = 0;

        // Step 1: Create ElevationNodes from junctions
        var junctionToNode = new Dictionary<NetworkJunction, ElevationNode>();
        foreach (var junction in network.Junctions)
        {
            var node = new ElevationNode
            {
                NodeId = _nextNodeId++,
                Position = junction.Position,
                IsSynthetic = false,
                SourceJunction = junction
            };
            _nodes.Add(node);
            junctionToNode[junction] = node;
        }

        // Step 2: Build spatial index of junction positions for endpoint matching
        // Map each spline endpoint to the junction it belongs to via contributor data
        var splineStartJunction = new Dictionary<int, NetworkJunction>();
        var splineEndJunction = new Dictionary<int, NetworkJunction>();

        foreach (var junction in network.Junctions)
        {
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.IsSplineStart)
                    splineStartJunction[contributor.Spline.SplineId] = junction;
                if (contributor.IsSplineEnd)
                    splineEndJunction[contributor.Spline.SplineId] = junction;
                // For continuous contributors (T-junctions), map both start and end
                // based on which endpoint is closer to the junction
                if (contributor.IsContinuous)
                {
                    // The cross-section in the contributor is somewhere mid-spline;
                    // this is a MidSplineCrossing/T-junction pass-through.
                    // Don't map start/end from this — it's handled by other contributors.
                }
            }
        }

        // Step 3: Create edges from splines
        // When junctions are not available (e.g., shouldHarmonize=false), co-located
        // spline endpoints must be clustered into shared nodes to enable chaining.
        // We maintain a spatial index of all nodes and reuse existing ones when a new
        // endpoint is within the clustering tolerance.
        const float endpointClusteringToleranceMeters = 2.0f;

        foreach (var spline in network.Splines)
        {
            // Diagnostic: log why bridge/tunnel splines are skipped
            if (spline.IsBridge || spline.IsTunnel)
            {
                var cs = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
                TerrainCreationLogger.Current?.Detail(
                    $"  ElevGraph: spline {spline.SplineId} {(spline.IsBridge ? "[BRIDGE]" : "[TUNNEL]")} " +
                    $"IsRoundabout={spline.IsRoundabout} IsPaintOnly={spline.IsPaintOnly} " +
                    $"crossSections={cs.Count} nodes={spline.StartOsmNodeId}→{spline.EndOsmNodeId}");
            }

            // Skip roundabout ring splines — they have their own Phase 2.6 harmonization
            if (spline.IsRoundabout) continue;

            // Skip paint-only splines — they don't participate in elevation
            if (spline.IsPaintOnly) continue;

            var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
            if (crossSections.Count == 0) continue;

            // Find start/end nodes
            ElevationNode startNode;
            ElevationNode endNode;

            if (splineStartJunction.TryGetValue(spline.SplineId, out var startJunction))
            {
                startNode = junctionToNode[startJunction];
            }
            else
            {
                // No junction mapped — try to find an existing node at this position
                // (clusters co-located endpoints from different splines at the same layer)
                startNode = FindOrCreateNode(spline.StartPoint, endpointClusteringToleranceMeters);
            }

            if (splineEndJunction.TryGetValue(spline.SplineId, out var endJunction))
            {
                endNode = junctionToNode[endJunction];
            }
            else
            {
                endNode = FindOrCreateNode(spline.EndPoint, endpointClusteringToleranceMeters);
            }

            var edge = new ElevationEdge
            {
                SplineId = spline.SplineId,
                StartNode = startNode,
                EndNode = endNode,
                Priority = spline.Priority,
                Length = spline.TotalLengthMeters,
                CrossSectionCount = crossSections.Count,
                IsBridge = spline.IsBridge,
                IsTunnel = spline.IsTunnel,
                OsmRoadType = spline.OsmRoadType,
                RoadWidth = spline.Parameters.RoadWidthMeters
            };

            _edges.Add(edge);
            _edgeBySplineId[spline.SplineId] = edge;
            startNode.ConnectedEdges.Add(edge);
            endNode.ConnectedEdges.Add(edge);
        }

        TerrainCreationLogger.Current?.Detail(
            $"Elevation graph: {_nodes.Count} nodes ({_nodes.Count(n => n.IsSynthetic)} synthetic), " +
            $"{_edges.Count} edges");
    }

    /// <summary>
    ///     Finds an existing node within tolerance of the given position, or creates a new synthetic node.
    ///     This enables co-located spline endpoints to share nodes even without junction detection.
    /// </summary>
    private ElevationNode FindOrCreateNode(Vector2 position, float toleranceMeters)
    {
        var toleranceSq = toleranceMeters * toleranceMeters;
        foreach (var existing in _nodes)
        {
            var dx = existing.Position.X - position.X;
            var dy = existing.Position.Y - position.Y;
            if (dx * dx + dy * dy <= toleranceSq)
                return existing;
        }

        var node = new ElevationNode
        {
            NodeId = _nextNodeId++,
            Position = position,
            IsSynthetic = true
        };
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    ///     Looks up the edge for a given spline ID.
    /// </summary>
    public ElevationEdge? GetEdgeForSpline(int splineId) =>
        _edgeBySplineId.GetValueOrDefault(splineId);

    /// <summary>
    ///     Chains splines into long elevation runs using greedy longest-path extension.
    ///     Each chain is a continuous path through the graph suitable for chain-based filtering.
    /// </summary>
    public List<ElevationChain> BuildElevationChains()
    {
        var chains = new List<ElevationChain>();
        var visited = new HashSet<int>(); // SplineIds that have been assigned to a chain
        var chainId = 0;

        // Sort edges by priority descending, then length descending
        var sortedEdges = _edges
            .OrderByDescending(e => e.Priority)
            .ThenByDescending(e => e.Length)
            .ToList();

        foreach (var seedEdge in sortedEdges)
        {
            if (visited.Contains(seedEdge.SplineId)) continue;

            var chain = new ElevationChain { ChainId = chainId++ };
            chain.Segments.Add((seedEdge, false));
            visited.Add(seedEdge.SplineId);

            // Extend forward from end node
            ExtendChain(chain, seedEdge, seedEdge.EndNode, forward: true, visited);

            // Extend backward from start node
            ExtendChain(chain, seedEdge, seedEdge.StartNode, forward: false, visited);

            chains.Add(chain);
        }

        // Diagnostic: log chain membership for bridge/tunnel splines
        foreach (var chain in chains)
        {
            var hasBridge = chain.Segments.Any(s => s.Edge.IsBridge || s.Edge.IsTunnel);
            if (hasBridge || chain.Segments.Count >= 3)
            {
                var splineIds = string.Join("→", chain.Segments.Select(s =>
                    $"{s.Edge.SplineId}{(s.Edge.IsBridge ? "[B]" : s.Edge.IsTunnel ? "[T]" : "")}"));
                TerrainCreationLogger.Current?.Detail(
                    $"  Chain {chain.ChainId}: {chain.Segments.Count} segments, {splineIds}");
            }
        }

        // Diagnostic: log unchained bridge/tunnel edges and why they didn't chain
        foreach (var edge in _edges.Where(e => e.IsBridge || e.IsTunnel))
        {
            var startDegree = edge.StartNode.ConnectedEdges.Count;
            var endDegree = edge.EndNode.ConnectedEdges.Count;
            TerrainCreationLogger.Current?.Detail(
                $"  Bridge/Tunnel edge spline={edge.SplineId}: " +
                $"startNode={edge.StartNode.NodeId} (degree={startDegree}), " +
                $"endNode={edge.EndNode.NodeId} (degree={endDegree}), " +
                $"startIsSynthetic={edge.StartNode.IsSynthetic}, endIsSynthetic={edge.EndNode.IsSynthetic}");
        }

        LogChainStatistics(chains);
        return chains;
    }

    /// <summary>
    ///     Extends a chain in one direction (forward or backward) through the graph.
    /// </summary>
    private void ExtendChain(ElevationChain chain, ElevationEdge currentEdge, ElevationNode atNode,
        bool forward, HashSet<int> visited)
    {
        var node = atNode;
        var prevEdge = currentEdge;

        while (true)
        {
            var candidate = FindBestContinuation(prevEdge, node, visited);
            if (candidate == null) break;

            visited.Add(candidate.SplineId);

            // Determine how this edge connects at the current node.
            var edgeEndIsAtNode = candidate.EndNode == node;

            if (forward)
            {
                // Forward extension: we enter the edge at 'node' and traverse to the far end.
                // If we enter at StartNode, traverse forward (CS in natural order).
                // If we enter at EndNode, traverse reversed (CS reversed).
                chain.Segments.Add((candidate, edgeEndIsAtNode));
            }
            else
            {
                // Backward extension: edge is prepended to the chain. Its CS must END at 'node'
                // (the connection point to the next segment). If the edge's EndNode is at 'node',
                // CS in natural order (Start→End) will end at EndNode = connection point.
                // If StartNode is at 'node', CS must be reversed to end at StartNode.
                chain.Segments.Insert(0, (candidate, !edgeEndIsAtNode));
            }

            // Move to the other end of the candidate edge
            node = edgeEndIsAtNode ? candidate.StartNode : candidate.EndNode;
            prevEdge = candidate;
        }
    }

    /// <summary>
    ///     At a given node, finds the best unvisited edge to continue the chain.
    ///     Returns null if no suitable continuation exists.
    /// </summary>
    private ElevationEdge? FindBestContinuation(ElevationEdge arrivedFrom, ElevationNode atNode,
        HashSet<int> visited)
    {
        // Dead-end or synthetic terminal: can't extend
        if (atNode.Degree <= 1) return null;

        // Collect unvisited candidates at this node
        var candidates = atNode.ConnectedEdges
            .Where(e => e.SplineId != arrivedFrom.SplineId && !visited.Contains(e.SplineId))
            .ToList();

        if (candidates.Count == 0) return null;

        // Degree-2 node (simple continuation): always chain through
        if (atNode.Degree == 2 && candidates.Count == 1)
            return candidates[0];

        // Degree-3+ node (true junction): only chain through if there's a clear through-road
        var compatibleCandidates = candidates
            .Where(c => IsCompatibleForChaining(arrivedFrom, c))
            .ToList();

        if (compatibleCandidates.Count != 1)
            // Ambiguous or no compatible candidate — don't chain through
            return null;

        var best = compatibleCandidates[0];

        // Final check: deflection angle must be < 30°
        var deflection = CalculateDeflectionAngle(arrivedFrom, best, atNode);
        if (deflection > 30f)
            return null;

        return best;
    }

    /// <summary>
    ///     Checks if two edges are compatible for chaining at a junction.
    ///     Requirements: same OsmRoadType, width ratio within 2:1.
    /// </summary>
    private static bool IsCompatibleForChaining(ElevationEdge a, ElevationEdge b)
    {
        // Same OSM road type required
        if (a.OsmRoadType != b.OsmRoadType) return false;
        if (a.OsmRoadType == null) return false; // Don't chain unknown types through junctions

        // Width ratio within 2:1
        if (a.RoadWidth > 0 && b.RoadWidth > 0)
        {
            var ratio = a.RoadWidth > b.RoadWidth
                ? a.RoadWidth / b.RoadWidth
                : b.RoadWidth / a.RoadWidth;
            if (ratio > 2.0f) return false;
        }

        return true;
    }

    /// <summary>
    ///     Calculates the deflection angle (in degrees) between two edges meeting at a node.
    ///     0° = perfectly straight through, 180° = U-turn.
    /// </summary>
    private static float CalculateDeflectionAngle(ElevationEdge arrivedFrom, ElevationEdge departing,
        ElevationNode atNode)
    {
        // Get approach direction (toward the node)
        var approachDir = GetApproachDirection(arrivedFrom, atNode);

        // Get departure direction (away from the node)
        var departDir = GetDepartureDirection(departing, atNode);

        // Deflection = angle between approach and departure
        // 0° if continuing straight, 180° if U-turn
        var dot = Vector2.Dot(approachDir, departDir);
        dot = Math.Clamp(dot, -1f, 1f);
        var angleRad = MathF.Acos(dot);
        return angleRad * 180f / MathF.PI;
    }

    /// <summary>
    ///     Gets the direction vector of an edge as it approaches a node.
    /// </summary>
    private static Vector2 GetApproachDirection(ElevationEdge edge, ElevationNode node)
    {
        // If the edge's end node is 'node', the approach direction is from start toward end
        // If the edge's start node is 'node', the approach direction is from end toward start
        Vector2 from, to;
        if (edge.EndNode == node)
        {
            from = edge.StartNode.Position;
            to = edge.EndNode.Position;
        }
        else
        {
            from = edge.EndNode.Position;
            to = edge.StartNode.Position;
        }

        var dir = to - from;
        var len = dir.Length();
        return len > 0.001f ? dir / len : Vector2.UnitX;
    }

    /// <summary>
    ///     Gets the direction vector of an edge as it departs from a node.
    /// </summary>
    private static Vector2 GetDepartureDirection(ElevationEdge edge, ElevationNode node)
    {
        // Departure = away from node. If start node is 'node', depart toward end.
        Vector2 from, to;
        if (edge.StartNode == node)
        {
            from = edge.StartNode.Position;
            to = edge.EndNode.Position;
        }
        else
        {
            from = edge.EndNode.Position;
            to = edge.StartNode.Position;
        }

        var dir = to - from;
        var len = dir.Length();
        return len > 0.001f ? dir / len : Vector2.UnitX;
    }

    private void LogChainStatistics(List<ElevationChain> chains)
    {
        if (chains.Count == 0) return;

        var avgChainLength = chains.Average(c => c.TotalLength);
        var avgChainCS = chains.Average(c => c.TotalCrossSectionCount);
        var avgSplineLength = _edges.Count > 0 ? _edges.Average(e => e.Length) : 0;
        var avgSplineCS = _edges.Count > 0 ? _edges.Average(e => e.CrossSectionCount) : 0;
        var multiSegmentChains = chains.Count(c => c.Segments.Count > 1);

        TerrainCreationLogger.Current?.Detail(
            $"Elevation chains: {chains.Count} chains from {_edges.Count} splines " +
            $"({multiSegmentChains} multi-segment)");
        TerrainCreationLogger.Current?.Detail(
            $"  Avg chain: {avgChainLength:F0}m / {avgChainCS:F0} CS " +
            $"vs avg spline: {avgSplineLength:F0}m / {avgSplineCS:F0} CS " +
            $"(context gain: {(avgSplineLength > 0 ? avgChainLength / avgSplineLength : 0):F1}x)");
    }
}
