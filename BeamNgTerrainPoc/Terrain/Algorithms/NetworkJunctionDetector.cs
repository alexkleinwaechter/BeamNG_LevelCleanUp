using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
///     Detects junctions across the entire unified road network.
///     Supports detection of:
///     - Endpoint clusters (Y, X intersections)
///     - T-junctions (endpoint touching middle of another road)
///     - Complex intersections (roundabouts, 4+ roads meeting)
///     - OSM-sourced junctions (motorway exits, traffic signals, etc.)
///     This detector operates on the unified network, meaning it can detect
///     junctions between roads from different materials (cross-material junctions).
/// </summary>
public class NetworkJunctionDetector
{
    /// <summary>
    ///     Spatial index cell size in meters for faster proximity queries.
    /// </summary>
    private const float SpatialIndexCellSize = 50f;

    /// <summary>
    ///     Detects all junctions in the unified road network.
    ///     Algorithm:
    ///     1. Build spatial index of all cross-section endpoints
    ///     2. Cluster endpoints within detection radius
    ///     3. Classify junction types (T, Y, X, Complex)
    ///     4. For T-junctions: identify continuous vs. terminating roads
    ///     5. Detect mid-spline crossings (where two roads cross without either terminating)
    /// </summary>
    /// <param name="network">The unified road network containing all splines and cross-sections.</param>
    /// <returns>List of detected network junctions.</returns>
    public List<NetworkJunction> DetectJunctions(
        UnifiedRoadNetwork network,
        float? detectionRadiusOverride = null)
    {
        TerrainLogger.SuppressDetailedLogging = true;
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("NetworkJunctionDetector");

        if (network.CrossSections.Count == 0)
        {
            TerrainLogger.Info("NetworkJunctionDetector: No cross-sections to process");
            return [];
        }

        // Step 1: Find all spline endpoints
        var endpoints = FindSplineEndpoints(network);
        TerrainLogger.Info($"  Found {endpoints.Count} spline endpoints from {network.Splines.Count} splines");

        // Step 2: Build spatial index for all cross-sections (for T-junction and crossing detection)
        var spatialIndex = BuildSpatialIndex(network.CrossSections);
        perfLog?.Timing("Built spatial index for cross-sections");

        // Step 3: Cluster endpoints into junctions
        // Detection radius is per-material (from JunctionHarmonizationParameters), default 5m
        var defaultDetectionRadius = detectionRadiusOverride ?? 5.0f;
        var junctions = ClusterEndpointsIntoJunctions(endpoints, network, defaultDetectionRadius);
        perfLog?.Timing($"Clustered into {junctions.Count} potential junctions");

        // Step 4: Detect T-junctions (endpoint meeting middle of another road)
        var tJunctionCount = DetectTJunctions(junctions, network, spatialIndex, defaultDetectionRadius);
        if (tJunctionCount > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"Detected {tJunctionCount} T-junction(s) (endpoint meeting middle of road)");

        // Step 5: Detect mid-spline crossings (two roads crossing without either terminating)
        var midSplineCrossings = DetectMidSplineCrossings(network, spatialIndex, defaultDetectionRadius, junctions);
        if (midSplineCrossings.Count > 0)
        {
            junctions.AddRange(midSplineCrossings);
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"Detected {midSplineCrossings.Count} mid-spline crossing(s) (roads crossing without endpoints)");
        }

        perfLog?.Timing("Mid-spline crossing detection complete");

        // Step 6: Classify junction types
        ClassifyJunctions(junctions, network);

        // Step 7: Assign junction IDs and calculate centroids
        for (var i = 0; i < junctions.Count; i++)
        {
            junctions[i].JunctionId = i;
            junctions[i].CalculateCentroid();
        }

        // Log junction statistics (to file only - summary info)
        var junctionsByType = junctions.GroupBy(j => j.Type).ToDictionary(g => g.Key, g => g.Count());
        TerrainCreationLogger.Current?.InfoFileOnly($"Junction breakdown: " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.TJunction)} T, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.YJunction)} Y, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.CrossRoads)} X, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.Complex)} Complex, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.Endpoint)} Isolated, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.MidSplineCrossing)} MidCrossing, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.Roundabout)} Roundabout, " +
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.Continuation)} Continuation");

        var crossMaterialCount = junctions.Count(j => j.IsCrossMaterial);
        if (crossMaterialCount > 0)
            TerrainCreationLogger.Current?.InfoFileOnly($"{crossMaterialCount} junction(s) involve multiple materials");

        // Store junctions in the network
        network.Junctions.Clear();
        network.Junctions.AddRange(junctions);

        perfLog?.Timing($"Detected {junctions.Count} total junctions");
        TerrainLogger.SuppressDetailedLogging = false;
        return junctions;
    }

    /// <summary>
    ///     Finds all spline endpoints (first and last cross-sections of each spline).
    /// </summary>
    private List<UnifiedCrossSection> FindSplineEndpoints(UnifiedRoadNetwork network)
    {
        var endpoints = new List<UnifiedCrossSection>();
        var sectionsBySpline = network.GetCrossSectionsBySpline();

        foreach (var spline in network.Splines)
        {
            if (!sectionsBySpline.TryGetValue(spline.SplineId, out var splineSections)
                || splineSections.Count == 0)
                continue;

            // First endpoint
            endpoints.Add(splineSections[0]);

            // Last endpoint (if different from first)
            if (splineSections.Count > 1) endpoints.Add(splineSections[^1]);
        }

        return endpoints;
    }

    /// <summary>
    ///     Builds a spatial index for fast proximity queries.
    ///     Returns a dictionary mapping grid cell -> cross-sections in that cell.
    /// </summary>
    private Dictionary<(int, int), List<UnifiedCrossSection>> BuildSpatialIndex(
        List<UnifiedCrossSection> crossSections)
    {
        var index = new Dictionary<(int, int), List<UnifiedCrossSection>>();

        foreach (var cs in crossSections)
        {
            var cellX = (int)(cs.CenterPoint.X / SpatialIndexCellSize);
            var cellY = (int)(cs.CenterPoint.Y / SpatialIndexCellSize);
            var key = (cellX, cellY);

            if (!index.TryGetValue(key, out var list))
            {
                list = [];
                index[key] = list;
            }

            list.Add(cs);
        }

        return index;
    }

    /// <summary>
    ///     Queries the spatial index for cross-sections near a point.
    /// </summary>
    private IEnumerable<UnifiedCrossSection> QuerySpatialIndex(
        Dictionary<(int, int), List<UnifiedCrossSection>> index,
        Vector2 position,
        float radius)
    {
        var minCellX = (int)((position.X - radius) / SpatialIndexCellSize);
        var maxCellX = (int)((position.X + radius) / SpatialIndexCellSize);
        var minCellY = (int)((position.Y - radius) / SpatialIndexCellSize);
        var maxCellY = (int)((position.Y + radius) / SpatialIndexCellSize);

        var radiusSq = radius * radius;

        for (var cx = minCellX; cx <= maxCellX; cx++)
        for (var cy = minCellY; cy <= maxCellY; cy++)
            if (index.TryGetValue((cx, cy), out var cell))
                foreach (var cs in cell)
                {
                    var distSq = Vector2.DistanceSquared(cs.CenterPoint, position);
                    if (distSq <= radiusSq) yield return cs;
                }
    }

    /// <summary>
    ///     Clusters nearby endpoints into junctions using Union-Find (disjoint set) with
    ///     a spatial grid index for efficient neighbor lookups.
    ///     Complexity: O(E � ?(E)) where E = number of endpoints and ? is the inverse
    ///     Ackermann function (effectively constant). This replaces the previous O(E� � C)
    ///     transitive closure algorithm.
    ///     Algorithm:
    ///     1. Pre-compute per-endpoint detection radii
    ///     2. Build a spatial grid index of endpoints for fast neighbor queries
    ///     3. For each endpoint, find neighbors within detection radius and union them
    ///     4. Group endpoints by their root representative to form clusters
    ///     5. Build NetworkJunction from each cluster
    /// </summary>
    private List<NetworkJunction> ClusterEndpointsIntoJunctions(
        List<UnifiedCrossSection> endpoints,
        UnifiedRoadNetwork network,
        float globalDetectionRadius)
    {
        if (endpoints.Count == 0)
            return [];

        // Step 1: Pre-compute effective detection radius for each endpoint
        var detectionRadii = new float[endpoints.Count];
        var maxDetectionRadius = globalDetectionRadius;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var radius = globalDetectionRadius;
            var splineParams = network.GetParametersForSpline(endpoints[i].OwnerSplineId);
            if (splineParams?.JunctionHarmonizationParameters != null)
                radius = splineParams.JunctionHarmonizationParameters.JunctionDetectionRadiusMeters;
            detectionRadii[i] = radius;
            if (radius > maxDetectionRadius)
                maxDetectionRadius = radius;
        }

        // Step 2: Build spatial grid index of endpoints
        // Cell size should be at least maxDetectionRadius so that neighbors are in adjacent cells
        var cellSize = MathF.Max(maxDetectionRadius, SpatialIndexCellSize);
        var endpointGrid = new Dictionary<(int, int), List<int>>();

        for (var i = 0; i < endpoints.Count; i++)
        {
            var cellX = (int)(endpoints[i].CenterPoint.X / cellSize);
            var cellY = (int)(endpoints[i].CenterPoint.Y / cellSize);
            var key = (cellX, cellY);

            if (!endpointGrid.TryGetValue(key, out var list))
            {
                list = [];
                endpointGrid[key] = list;
            }

            list.Add(i);
        }

        // Step 3: Union-Find with path compression and union by rank
        var parent = new int[endpoints.Count];
        var rank = new int[endpoints.Count];
        for (var i = 0; i < endpoints.Count; i++)
            parent[i] = i;

        // ── Topology pre-union: group endpoints sharing the same OSM node ID ──
        // This ensures shared OSM nodes form junctions regardless of detection radius.
        // Endpoints without node IDs (PNG pipeline, cropped boundaries) are skipped
        // and handled by the spatial fallback below.
        var osmNodeToEndpointIndices = new Dictionary<long, List<int>>();
        for (var i = 0; i < endpoints.Count; i++)
        {
            var ep = endpoints[i];
            var spline = network.GetSplineById(ep.OwnerSplineId);
            if (spline == null) continue;

            long? nodeId = ep.IsSplineStart ? spline.StartOsmNodeId
                         : ep.IsSplineEnd   ? spline.EndOsmNodeId
                         : null;

            if (nodeId == null) continue;

            if (!osmNodeToEndpointIndices.TryGetValue(nodeId.Value, out var list))
            {
                list = [];
                osmNodeToEndpointIndices[nodeId.Value] = list;
            }
            list.Add(i);
        }

        // Pre-union all endpoints sharing the same OSM node
        var topologyUnionCount = 0;
        foreach (var (_, indices) in osmNodeToEndpointIndices)
        {
            if (indices.Count < 2) continue;
            var first = indices[0];
            for (var k = 1; k < indices.Count; k++)
            {
                Union(parent, rank, first, indices[k]);
                topologyUnionCount++;
            }
        }

        if (topologyUnionCount > 0)
            TerrainLogger.Info($"  Topology pre-union: {topologyUnionCount} endpoint pair(s) connected via shared OSM node IDs ({osmNodeToEndpointIndices.Count} unique nodes)");

        // For each endpoint, query nearby cells and union with neighbors within detection radius
        for (var i = 0; i < endpoints.Count; i++)
        {
            var pos = endpoints[i].CenterPoint;
            var radius = detectionRadii[i];
            var minCellX = (int)((pos.X - maxDetectionRadius) / cellSize);
            var maxCellX = (int)((pos.X + maxDetectionRadius) / cellSize);
            var minCellY = (int)((pos.Y - maxDetectionRadius) / cellSize);
            var maxCellY = (int)((pos.Y + maxDetectionRadius) / cellSize);

            for (var cx = minCellX; cx <= maxCellX; cx++)
            for (var cy = minCellY; cy <= maxCellY; cy++)
            {
                if (!endpointGrid.TryGetValue((cx, cy), out var cell))
                    continue;

                foreach (var j in cell)
                {
                    if (j <= i) // Avoid duplicate checks (symmetric)
                        continue;

                    // Use the maximum of both endpoints' detection radii for the merge check
                    // This preserves the transitive closure semantics: if either endpoint considers
                    // the other "close enough", they should be in the same cluster
                    var effectiveRadius = MathF.Max(radius, detectionRadii[j]);
                    var dist = Vector2.Distance(pos, endpoints[j].CenterPoint);

                    if (dist <= effectiveRadius)
                        Union(parent, rank, i, j);
                }
            }
        }

        // Step 4: Group endpoints by their root representative
        var clusters = new Dictionary<int, List<int>>();
        for (var i = 0; i < endpoints.Count; i++)
        {
            var root = Find(parent, i);
            if (!clusters.TryGetValue(root, out var cluster))
            {
                cluster = [];
                clusters[root] = cluster;
            }

            cluster.Add(i);
        }

        // Step 5: Build junctions from clusters
        var junctions = new List<NetworkJunction>(clusters.Count);
        foreach (var cluster in clusters.Values)
        {
            var junction = new NetworkJunction();

            foreach (var idx in cluster)
            {
                var ep = endpoints[idx];
                var spline = network.GetSplineById(ep.OwnerSplineId);
                if (spline == null) continue;

                junction.Contributors.Add(new JunctionContributor
                {
                    CrossSection = ep,
                    Spline = spline,
                    IsSplineStart = ep.IsSplineStart,
                    IsSplineEnd = ep.IsSplineEnd
                });
            }

            if (junction.Contributors.Count > 0) junctions.Add(junction);
        }

        return junctions;
    }

    /// <summary>
    ///     Union-Find: Finds the root representative of element x with path compression.
    /// </summary>
    private static int Find(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]]; // Path halving (simpler than full compression, same amortized cost)
            x = parent[x];
        }

        return x;
    }

    /// <summary>
    ///     Union-Find: Merges the sets containing elements a and b using union by rank.
    /// </summary>
    private static void Union(int[] parent, int[] rank, int a, int b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA == rootB)
            return;

        // Union by rank: attach smaller tree under larger tree
        if (rank[rootA] < rank[rootB])
            parent[rootA] = rootB;
        else if (rank[rootA] > rank[rootB])
            parent[rootB] = rootA;
        else
        {
            parent[rootB] = rootA;
            rank[rootA]++;
        }
    }

    /// <summary>
    ///     Detects T-junctions where an endpoint meets the middle of another road.
    ///     Updates junction classifications and adds continuous road cross-sections.
    ///     IMPORTANT: This handles two scenarios:
    ///     1. A single endpoint near the middle of another spline (classic T-junction)
    ///     2. Multiple endpoints clustered together, but one of them is near the MIDDLE
    ///     of another spline (the passing-through spline should dominate elevation)
    ///     For WITHIN-MATERIAL junctions: If spline A's endpoint is near spline B's middle,
    ///     spline B is the "continuous" road and A is the "terminating" road.
    /// </summary>
    /// <returns>Number of T-junctions detected.</returns>
    private int DetectTJunctions(
        List<NetworkJunction> junctions,
        UnifiedRoadNetwork network,
        Dictionary<(int, int), List<UnifiedCrossSection>> spatialIndex,
        float globalDetectionRadius)
    {
        var tJunctionCount = 0;

        // Process ALL junctions to find passing-through splines
        foreach (var junction in junctions.ToList())
        {
            // Calculate junction center from all endpoint contributors
            junction.CalculateCentroid();
            var junctionPosition = junction.Position;

            // Get effective detection radius (use maximum from all contributors)
            var detectionRadius = globalDetectionRadius;
            foreach (var contributor in junction.Contributors)
                if (contributor.Spline.Parameters.JunctionHarmonizationParameters != null)
                    detectionRadius = Math.Max(detectionRadius,
                        contributor.Spline.Parameters.JunctionHarmonizationParameters.JunctionDetectionRadiusMeters);

            // Get all spline IDs that have ENDPOINTS in this junction
            var splineIdsWithEndpoints = junction.Contributors
                .Where(c => c.IsEndpoint)
                .Select(c => c.Spline.SplineId)
                .ToHashSet();

            // Find mid-spline (non-endpoint) cross-sections near the junction center
            // These could be from:
            // - Splines NOT in the junction at all (cross-material or just nearby)
            // - Splines that ARE in the junction with an endpoint, but ALSO pass through
            //   (this happens when a spline loops back or when the road is continuous)
            var continuousContributors =
                new List<(UnifiedCrossSection cs, ParameterizedRoadSpline spline, float dist)>();

            foreach (var cs in QuerySpatialIndex(spatialIndex, junctionPosition, detectionRadius))
            {
                // Skip if this cross-section is itself an endpoint
                if (cs.IsSplineStart || cs.IsSplineEnd)
                    continue;

                // This is a mid-spline cross-section near the junction
                var dist = Vector2.Distance(junctionPosition, cs.CenterPoint);
                var spline = network.GetSplineById(cs.OwnerSplineId);
                if (spline == null)
                    continue;

                // Check if this spline already has a CONTINUOUS contributor in the junction
                if (junction.Contributors.Any(c => c.Spline.SplineId == spline.SplineId && c.IsContinuous))
                    continue;

                // Add this as a continuous contributor
                continuousContributors.Add((cs, spline, dist));
            }

            // Add the closest continuous contributor for each unique spline
            var addedSplines = new HashSet<int>();
            foreach (var (cs, spline, _) in continuousContributors.OrderBy(c => c.dist))
            {
                if (addedSplines.Contains(spline.SplineId))
                    continue;

                // Check if this spline already has an ENDPOINT contributor
                // If so, we have a special case: the spline both terminates AND passes through
                // (this shouldn't normally happen, but handle gracefully)
                var existingEndpointContributor = junction.Contributors
                    .FirstOrDefault(c => c.Spline.SplineId == spline.SplineId && c.IsEndpoint);

                if (existingEndpointContributor != null)
                    // The spline has an endpoint here but also passes through nearby.
                    // This commonly happens with short splines or large detection radii.
                    // It's not a problem - we simply skip adding a duplicate contributor.
                    // Only log at Trace level since this is expected behavior.
                    continue;

                // Add as new continuous contributor
                junction.Contributors.Add(new JunctionContributor
                {
                    CrossSection = cs,
                    Spline = spline,
                    IsSplineStart = false,
                    IsSplineEnd = false
                    // IsContinuous will be true because neither IsSplineStart nor IsSplineEnd
                });

                addedSplines.Add(spline.SplineId);
                tJunctionCount++;
            }
        }

        return tJunctionCount;
    }

    /// <summary>
    ///     Classifies each junction based on the number and type of contributors.
    /// </summary>
    private void ClassifyJunctions(List<NetworkJunction> junctions, UnifiedRoadNetwork network)
    {
        foreach (var junction in junctions)
        {
            // Skip junctions that already have a specific type assigned (e.g., MidSplineCrossing, Roundabout)
            if (junction.Type == JunctionType.MidSplineCrossing || junction.Type == JunctionType.Roundabout)
                continue;

            var uniqueSplineIds = junction.Contributors
                .Select(c => c.Spline.SplineId)
                .Distinct()
                .Count();

            if (uniqueSplineIds == 1 && junction.Contributors.Count == 1)
            {
                // Single endpoint, no connection to other roads
                junction.Type = JunctionType.Endpoint;
            }
            else if (junction.Contributors.Any(c => c.IsContinuous))
            {
                // At least one contributor passes through (not an endpoint) = T-junction
                junction.Type = JunctionType.TJunction;
            }
            else if (uniqueSplineIds == 2 && IsDegree2Continuation(junction))
            {
                // Two splines meet at near-straight angle with similar width = OSM way boundary
                junction.Type = JunctionType.Continuation;
            }
            else
            {
                // All contributors are endpoints
                junction.Type = uniqueSplineIds switch
                {
                    2 => JunctionType.YJunction,
                    3 or 4 => JunctionType.CrossRoads,
                    _ => JunctionType.Complex
                };
            }
        }
    }

    /// <summary>
    ///     Checks if a degree-2 junction is a simple continuation (OSM way boundary)
    ///     rather than a real Y-junction. Uses the same heuristics as
    ///     NetworkElevationGraph.FindBestContinuation: deflection angle &lt; 30°
    ///     and width ratio within 2:1.
    /// </summary>
    private static bool IsDegree2Continuation(NetworkJunction junction)
    {
        var endpoints = junction.Contributors.Where(c => c.IsEndpoint).ToList();
        if (endpoints.Count != 2) return false;

        var a = endpoints[0];
        var b = endpoints[1];

        // Width ratio check (same as NetworkElevationGraph.IsCompatibleForChaining)
        var widthA = a.Spline.WidthProfile
                ?.GetWidthsAtDistance(a.CrossSection.DistanceAlongSpline).corridor
            ?? a.Spline.Parameters.RoadWidthMeters;
        var widthB = b.Spline.WidthProfile
                ?.GetWidthsAtDistance(b.CrossSection.DistanceAlongSpline).corridor
            ?? b.Spline.Parameters.RoadWidthMeters;

        if (widthA > 0 && widthB > 0)
        {
            var ratio = widthA > widthB ? widthA / widthB : widthB / widthA;
            if (ratio > 2.0f) return false;
        }

        // Deflection angle check: the two splines should point in roughly the same direction.
        // Get tangent directions pointing AWAY from the junction for each spline.
        var tangentA = a.IsSplineStart
            ? -a.CrossSection.TangentDirection   // start endpoint: tangent points into spline, negate for "away"
            : a.CrossSection.TangentDirection;    // end endpoint: tangent points away from spline
        var tangentB = b.IsSplineStart
            ? -b.CrossSection.TangentDirection
            : b.CrossSection.TangentDirection;

        // For a continuation, the two "away" tangents should point in OPPOSITE directions
        // (one road goes left, the other goes right from the junction).
        // So we check the angle between tangentA and -tangentB (should be < 30°).
        var dot = Vector2.Dot(tangentA, -tangentB);
        dot = Math.Clamp(dot, -1f, 1f);
        var deflectionDegrees = MathF.Acos(dot) * 180f / MathF.PI;

        return deflectionDegrees < 30f;
    }

    /// <summary>
    ///     Gets the effective junction detection radius for a given location.
    ///     Uses the maximum radius among nearby splines.
    /// </summary>
    /// <param name="network">The unified road network.</param>
    /// <param name="position">The position to query.</param>
    /// <param name="globalDefault">The global default detection radius.</param>
    /// <returns>The effective detection radius in meters.</returns>
    public float GetEffectiveDetectionRadius(
        UnifiedRoadNetwork network,
        Vector2 position,
        float globalDefault)
    {
        // Find nearby splines and use the maximum configured radius
        var maxRadius = globalDefault;

        foreach (var spline in network.Splines)
        {
            // Check if this spline is close to the position
            var startDist = Vector2.Distance(spline.StartPoint, position);
            var endDist = Vector2.Distance(spline.EndPoint, position);

            if (startDist < globalDefault * 2 || endDist < globalDefault * 2)
            {
                var splineParams = spline.Parameters.JunctionHarmonizationParameters;
                if (splineParams != null && splineParams.JunctionDetectionRadiusMeters > maxRadius)
                    maxRadius = splineParams.JunctionDetectionRadiusMeters;
            }
        }

        return maxRadius;
    }

    /// <summary>
    ///     Detects mid-spline crossings where two roads cross each other without either terminating.
    ///     This handles the case where roads physically intersect but neither has an endpoint at the crossing.
    ///     Algorithm:
    ///     1. For each spline, sample cross-sections at regular intervals
    ///     2. For each cross-section, check if any OTHER spline's cross-sections are very close
    ///     3. If two mid-spline cross-sections from different splines are close, it's a crossing
    ///     4. Cluster nearby crossings to avoid duplicates
    ///     5. Skip crossings only if THOSE SAME TWO SPLINES are already connected at an existing junction
    /// </summary>
    private List<NetworkJunction> DetectMidSplineCrossings(
        UnifiedRoadNetwork network,
        Dictionary<(int, int), List<UnifiedCrossSection>> spatialIndex,
        float globalDetectionRadius,
        List<NetworkJunction> existingJunctions)
    {
        var crossings = new List<NetworkJunction>();
        var processedPairs = new HashSet<(int, int)>(); // Track spline pairs we've already found crossings for

        // E-A: grade-separated crossings are recorded on the network instead of becoming at-grade junctions.
        // This is the sole populator of the list, so reset it for this detection run.
        network.GradeSeparatedCrossings.Clear();
        var gradeSeparatedCount = 0;

        // Use the full detection radius for mid-spline crossings
        // The roads need to be within this distance to be considered "crossing"
        // This accounts for road width - two 8m wide roads crossing need ~8-10m detection
        var crossingDetectionRadius = globalDetectionRadius;

        TerrainCreationLogger.Current?.Detail(
            $"DetectMidSplineCrossings: Using detection radius = {crossingDetectionRadius:F1}m, " +
            $"processing {network.Splines.Count} splines, {existingJunctions.Count} existing junctions");

        // Build a set of spline pairs that are ALREADY connected at existing junctions
        // Only skip mid-spline crossings for pairs that are already handled
        var alreadyConnectedPairs = new HashSet<(int, int)>();
        foreach (var junction in existingJunctions)
        {
            var splineIds = junction.Contributors.Select(c => c.Spline.SplineId).Distinct().ToList();
            // Add all pairs of splines in this junction
            for (var i = 0; i < splineIds.Count; i++)
            for (var j = i + 1; j < splineIds.Count; j++)
            {
                var pairKey = splineIds[i] < splineIds[j]
                    ? (splineIds[i], splineIds[j])
                    : (splineIds[j], splineIds[i]);
                alreadyConnectedPairs.Add(pairKey);
            }
        }

        TerrainCreationLogger.Current?.Detail(
            $"DetectMidSplineCrossings: {alreadyConnectedPairs.Count} spline pairs already connected at existing junctions");

        // Track positions where we've already created crossings to avoid duplicates
        var newCrossingPositions = new List<Vector2>();

        var totalMidSplineSectionsChecked = 0;
        var skippedAlreadyConnected = 0;
        var candidateCrossingsFound = 0;

        foreach (var spline in network.Splines)
        {
            var splineSections = network.GetCrossSectionsForSpline(spline.SplineId)
                .Where(cs => !cs.IsSplineStart && !cs.IsSplineEnd) // Only mid-spline sections
                .ToList();

            // Sample more frequently to catch crossings - at least every 5 meters
            // But limit to ~100 samples per spline to avoid excessive computation
            var maxSamples = 100;
            var sampleInterval = Math.Max(1, splineSections.Count / maxSamples);

            for (var i = 0; i < splineSections.Count; i += sampleInterval)
            {
                var cs = splineSections[i];
                totalMidSplineSectionsChecked++;

                // Find the closest mid-spline cross-section per OTHER spline near this sample.
                // Manual loop instead of Where/ToList/GroupBy/OrderBy — this runs for every sampled
                // section (200k+ on big maps) and the LINQ chains dominated allocation/GC cost.
                // Semantics preserved EXACTLY: keys in first-appearance order (GroupBy), float sqrt
                // distance metric with strict < (ties keep the first-seen section, like stable OrderBy
                // over Vector2.Distance — squared distance would resolve sqrt-rounding ties differently).
                Dictionary<int, (UnifiedCrossSection cs, float dist)>? closestBySpline = null;
                List<int>? splineOrder = null;

                foreach (var other in QuerySpatialIndex(spatialIndex, cs.CenterPoint, crossingDetectionRadius))
                {
                    if (other.OwnerSplineId == spline.SplineId || // Different spline
                        other.IsSplineStart || other.IsSplineEnd) // Also mid-spline
                        continue;

                    var dist = Vector2.Distance(other.CenterPoint, cs.CenterPoint);
                    closestBySpline ??= new Dictionary<int, (UnifiedCrossSection, float)>();
                    splineOrder ??= [];

                    if (!closestBySpline.TryGetValue(other.OwnerSplineId, out var current))
                    {
                        closestBySpline[other.OwnerSplineId] = (other, dist);
                        splineOrder.Add(other.OwnerSplineId);
                    }
                    else if (dist < current.dist)
                    {
                        closestBySpline[other.OwnerSplineId] = (other, dist);
                    }
                }

                if (closestBySpline == null || splineOrder == null)
                    continue;

                candidateCrossingsFound++;

                foreach (var otherSplineId in splineOrder)
                {
                    var otherCs = closestBySpline[otherSplineId].cs;
                    // Create a canonical pair key to avoid duplicates
                    var pairKey = spline.SplineId < otherSplineId
                        ? (spline.SplineId, otherSplineId)
                        : (otherSplineId, spline.SplineId);

                    // Skip if we've already processed this pair in this detection run
                    if (processedPairs.Contains(pairKey))
                        continue;

                    // Skip if these two splines are ALREADY connected at an existing junction
                    // (e.g., one has an endpoint meeting the other - that's a T-junction, not a crossing)
                    if (alreadyConnectedPairs.Contains(pairKey))
                    {
                        skippedAlreadyConnected++;
                        processedPairs.Add(pairKey); // Don't check this pair again
                        continue;
                    }

                    var otherSpline = network.GetSplineById(otherSplineId);
                    if (otherSpline == null)
                        continue;

                    // Calculate crossing point as midpoint between the two closest cross-sections
                    var crossingPoint = (cs.CenterPoint + otherCs.CenterPoint) / 2f;

                    // Check this isn't too close to another crossing we just created
                    if (newCrossingPositions.Any(p =>
                            Vector2.Distance(p, crossingPoint) < crossingDetectionRadius * 0.5f))
                        continue;

                    // E-A: is this a grade-separated crossing (one road rides over the other) rather than a
                    // real at-grade intersection? If so, record it on the network and SKIP the false
                    // at-grade junction — this both fixes the latent grade-separation bug and yields the data
                    // GradeSeparationResolver needs (doc 07 §6).
                    if (TryClassifyGradeSeparation(spline, cs, otherSpline, otherCs, crossingPoint, out var gsc))
                    {
                        network.GradeSeparatedCrossings.Add(gsc);
                        processedPairs.Add(pairKey);
                        newCrossingPositions.Add(crossingPoint);
                        gradeSeparatedCount++;
                        TerrainCreationLogger.Current?.Detail(
                            $"GradeSeparatedCrossing: upper spline {gsc.UpperSplineId} " +
                            $"(layer {gsc.UpperLayer}, bridge={gsc.UpperIsBridge}, prio {gsc.UpperPriority}) " +
                            $"over lower spline {gsc.LowerSplineId} " +
                            $"(layer {gsc.LowerLayer}, prio {gsc.LowerPriority}) " +
                            $"at ({crossingPoint.X:F1}, {crossingPoint.Y:F1})");
                        if (gsc.IsBridgeOverBridge && spline.Parameters.BridgeRules?.EnableBridgeBridge == true)
                            TerrainCreationLogger.Current?.Detail(
                                $"[BRIDGE-BRIDGE] spline {gsc.UpperSplineId} over bridge {gsc.LowerSplineId} " +
                                $"(layers {gsc.UpperLayer}/{gsc.LowerLayer}) — detection only, R6 multi-level deferred");
                        continue;
                    }

                    // Create a new junction for this mid-spline crossing
                    var junction = new NetworkJunction
                    {
                        Position = crossingPoint,
                        Type = JunctionType.MidSplineCrossing
                    };

                    // Add both splines as continuous contributors (neither terminates here)
                    junction.Contributors.Add(new JunctionContributor
                    {
                        CrossSection = cs,
                        Spline = spline,
                        IsSplineStart = false,
                        IsSplineEnd = false
                    });

                    junction.Contributors.Add(new JunctionContributor
                    {
                        CrossSection = otherCs,
                        Spline = otherSpline,
                        IsSplineStart = false,
                        IsSplineEnd = false
                    });

                    crossings.Add(junction);
                    processedPairs.Add(pairKey);
                    newCrossingPositions.Add(crossingPoint); // Prevent nearby duplicates

                    TerrainCreationLogger.Current?.Detail(
                        $"MidSplineCrossing detected: Spline {spline.SplineId} x Spline {otherSplineId} " +
                        $"at ({crossingPoint.X:F1}, {crossingPoint.Y:F1}), " +
                        $"distance between CS = {Vector2.Distance(cs.CenterPoint, otherCs.CenterPoint):F2}m");
                }
            }
        }

        // Footprint pass (plan doc 14 §5): the ~100-sample proximity loop above can MISS an under-deck road
        // (sparse sampling, lateral offset under a wide deck). Sweep each bridge span's XY footprint for any
        // other spline's section it contains and record the grade separation directly — so "no road under the
        // bridge is harmonized as a crossing with it" is structurally true, not sampling-dependent. Add-only:
        // pairs the loop already handled are skipped via processedPairs.
        RecordFootprintGradeSeparations(network, processedPairs, ref gradeSeparatedCount);

        TerrainCreationLogger.Current?.Detail(
            $"DetectMidSplineCrossings summary: Checked {totalMidSplineSectionsChecked} mid-spline sections, " +
            $"skipped {skippedAlreadyConnected} pairs already connected at junctions, " +
            $"found {candidateCrossingsFound} candidate locations, " +
            $"created {crossings.Count} crossing junctions, " +
            $"recorded {gradeSeparatedCount} grade-separated crossing(s)");

        return crossings;
    }

    /// <summary>
    ///     E-A classification: decides whether two crossing splines are vertically separated (one rides
    ///     above the other) rather than meeting at grade. Grade separation is established by the
    ///     <b>effective</b> OSM <c>Layer</c> at the crossing first, then by one being a bridge while the
    ///     other is not. Priority is NOT used here (it only breaks the under/over tie in
    ///     <c>GradeSeparationResolver</c>); two same-layer, equally-bridged roads are treated as a genuine
    ///     at-grade crossing.
    ///
    ///     <para><b>Merged corridors (plan doc 14 §5/§1a).</b> When a bridge way has been merged INTO its
    ///     through-road corridor (<see cref="RoadSmoothingParameters.MergeStructuresIntoCorridor" />), the
    ///     whole-spline <c>Layer</c>/<c>IsBridge</c> describe the merge-base approach (layer 0, not a bridge),
    ///     not the interior bridge span. The span carries the real <c>layer=1</c> on its
    ///     <see cref="StructureSegment" />, so the effective layer at the crossing is the containing span's
    ///     layer if the crossing cross-section falls inside a span, else the whole-spline layer. With the flag
    ///     off there are no <c>StructureSegments</c> on a merged corridor, so this falls back to the
    ///     whole-spline values — byte-identical to the legacy separate-bridge-spline behaviour.</para>
    /// </summary>
    /// <returns>True and a populated <paramref name="crossing"/> if grade-separated; false otherwise.</returns>
    private static bool TryClassifyGradeSeparation(
        ParameterizedRoadSpline splineA,
        UnifiedCrossSection csA,
        ParameterizedRoadSpline splineB,
        UnifiedCrossSection csB,
        Vector2 crossingPoint,
        out GradeSeparatedCrossing crossing)
    {
        crossing = null!;

        var (layerA, bridgeA) = EffectiveStructureAt(splineA, csA);
        var (layerB, bridgeB) = EffectiveStructureAt(splineB, csB);

        int upperLayer, lowerLayer;
        bool upperBridge, lowerBridge;
        ParameterizedRoadSpline upper, lower;
        if (layerA != layerB)
        {
            var aIsUpper = layerA > layerB;
            (upper, upperLayer, upperBridge) = aIsUpper ? (splineA, layerA, bridgeA) : (splineB, layerB, bridgeB);
            (lower, lowerLayer, lowerBridge) = aIsUpper ? (splineB, layerB, bridgeB) : (splineA, layerA, bridgeA);
        }
        else if (bridgeA != bridgeB)
        {
            (upper, upperLayer, upperBridge) = bridgeA ? (splineA, layerA, bridgeA) : (splineB, layerB, bridgeB);
            (lower, lowerLayer, lowerBridge) = bridgeA ? (splineB, layerB, bridgeB) : (splineA, layerA, bridgeA);
        }
        else
        {
            return false; // same effective layer, neither uniquely a bridge → genuine at-grade crossing
        }

        crossing = new GradeSeparatedCrossing
        {
            UpperSplineId = upper.SplineId,
            LowerSplineId = lower.SplineId,
            CrossingXY = crossingPoint,
            UpperLayer = upperLayer,
            LowerLayer = lowerLayer,
            UpperPriority = upper.Priority,
            LowerPriority = lower.Priority,
            UpperIsBridge = upperBridge,
            LowerIsBridge = lowerBridge,
            LowerKind = BridgeObstacleKind.Road, // both members are road splines (rail/water are synthetic, A1)
            UpperOsmClass = upper.OsmRoadType,
            LowerOsmClass = lower.OsmRoadType
        };
        return true;
    }

    /// <summary>
    ///     The effective vertical layer and bridge state of <paramref name="spline"/> at the crossing
    ///     cross-section <paramref name="cs"/>. On a merged corridor (flag on, with structure spans) the
    ///     containing span's <see cref="StructureSegment.Layer"/>/<see cref="StructureSegment.IsBridge"/>
    ///     win; otherwise the whole-spline values are used. See <see cref="TryClassifyGradeSeparation"/>.
    /// </summary>
    private static (int layer, bool isBridge) EffectiveStructureAt(
        ParameterizedRoadSpline spline, UnifiedCrossSection cs)
    {
        if (spline.Parameters.MergeStructuresIntoCorridor &&
            spline.StructureSegments is { Count: > 0 } segments)
        {
            foreach (var seg in segments)
            {
                // LayerAt: a consolidated span (doc 10) carries the original per-way layers as station
                // sub-ranges — the classification must see the LOCAL layer at the crossing, not the
                // joined span's governing max.
                if (cs.DistanceAlongSpline >= seg.StartDistance &&
                    cs.DistanceAlongSpline <= seg.EndDistance)
                    return (seg.LayerAt(cs.DistanceAlongSpline), seg.IsBridge);
            }
        }

        return (spline.Layer, spline.IsBridge);
    }

    /// <summary>
    ///     Footprint-based grade-separation pass (plan doc 14 §5). For every merged-corridor bridge span,
    ///     records a <see cref="GradeSeparatedCrossing"/> for any other spline that has a mid-section inside
    ///     the span's XY footprint and sits on a lower effective layer — catching under-deck roads the
    ///     proximity sampler missed. Add-only and pair-deduped: pairs already handled by the mid-spline loop
    ///     (whether grade-separated or genuine at-grade) are left untouched. No-op when there are no spans
    ///     (legacy / flag off), so it never affects non-merged networks.
    /// </summary>
    private static void RecordFootprintGradeSeparations(
        UnifiedRoadNetwork network,
        HashSet<(int, int)> processedPairs,
        ref int gradeSeparatedCount)
    {
        var footprints = BridgeSpanFootprint.BuildAll(network);
        if (footprints.Count == 0) return;

        // Per-spline cross-section lists (cached on the network) + per-spline XY bounds, computed ONCE.
        // The previous per-pair GetCrossSectionsForSpline call re-scanned and re-sorted ALL cross-sections
        // (~1M) for every footprint × spline pair — O(footprints × splines × totalCS), the dominant cost of
        // the whole detection phase on large maps. The AABB prefilter rejects almost every pair outright.
        var sectionsBySpline = network.GetCrossSectionsBySpline();
        var boundsBySpline = new Dictionary<int, (Vector2 min, Vector2 max)>(sectionsBySpline.Count);
        foreach (var (splineId, sections) in sectionsBySpline)
        {
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            foreach (var cs in sections)
            {
                // Skip non-finite centers — NaN would propagate through Min/Max and disable the prefilter.
                if (!float.IsFinite(cs.CenterPoint.X) || !float.IsFinite(cs.CenterPoint.Y)) continue;
                min = Vector2.Min(min, cs.CenterPoint);
                max = Vector2.Max(max, cs.CenterPoint);
            }

            boundsBySpline[splineId] = (min, max);
        }

        foreach (var fp in footprints)
        {
            var ownerSpline = network.GetSplineById(fp.OwnerSplineId);
            if (ownerSpline == null) continue;

            foreach (var other in network.Splines)
            {
                if (other.SplineId == fp.OwnerSplineId) continue;

                var pairKey = fp.OwnerSplineId < other.SplineId
                    ? (fp.OwnerSplineId, other.SplineId)
                    : (other.SplineId, fp.OwnerSplineId);
                if (processedPairs.Contains(pairKey))
                    continue; // mid-spline loop already classified this pair

                // AABB prefilter: a spline whose bounds don't overlap the footprint can't have a
                // section inside it (Contains starts with the same bbox test per point).
                if (!boundsBySpline.TryGetValue(other.SplineId, out var bounds)
                    || bounds.min.X > fp.Max.X || bounds.max.X < fp.Min.X
                    || bounds.min.Y > fp.Max.Y || bounds.max.Y < fp.Min.Y)
                    continue;

                if (!sectionsBySpline.TryGetValue(other.SplineId, out var otherSections))
                    continue;

                // First mid-section of `other` whose center lies inside the deck footprint.
                // Non-finite centers (degenerate spline geometry) must be skipped explicitly:
                // with NaN coordinates every point-in-triangle sign test is false, which the
                // all-same-sign check misreads as "inside EVERY footprint" — producing bogus
                // (NaN, NaN) grade-separated crossings that poison the bridge planner.
                UnifiedCrossSection? underSection = null;
                foreach (var cs in otherSections)
                {
                    if (cs.IsSplineStart || cs.IsSplineEnd) continue;
                    if (!float.IsFinite(cs.CenterPoint.X) || !float.IsFinite(cs.CenterPoint.Y)) continue;
                    if (fp.Contains(cs.CenterPoint))
                    {
                        underSection = cs;
                        break;
                    }
                }

                if (underSection == null)
                    continue;

                var (lowerLayer, lowerBridge) = EffectiveStructureAt(other, underSection);
                if (fp.Layer <= lowerLayer)
                    continue; // owner span is not genuinely above → leave to the planner / the other span's footprint

                network.GradeSeparatedCrossings.Add(new GradeSeparatedCrossing
                {
                    UpperSplineId = fp.OwnerSplineId,
                    LowerSplineId = other.SplineId,
                    CrossingXY = underSection.CenterPoint,
                    UpperLayer = fp.Layer,
                    LowerLayer = lowerLayer,
                    UpperPriority = ownerSpline.Priority,
                    LowerPriority = other.Priority,
                    UpperIsBridge = true,
                    LowerIsBridge = lowerBridge,
                    LowerKind = BridgeObstacleKind.Road, // both members are road splines (rail/water are synthetic, A1)
                    UpperOsmClass = ownerSpline.OsmRoadType,
                    LowerOsmClass = other.OsmRoadType
                });
                processedPairs.Add(pairKey);
                gradeSeparatedCount++;

                TerrainCreationLogger.Current?.Detail(
                    $"GradeSeparatedCrossing (footprint): upper spline {fp.OwnerSplineId} span {fp.SpanId} " +
                    $"(layer {fp.Layer}) over lower spline {other.SplineId} (layer {lowerLayer}) at " +
                    $"({underSection.CenterPoint.X:F1}, {underSection.CenterPoint.Y:F1}) — sampler missed it");
                if (lowerBridge && ownerSpline.Parameters.BridgeRules?.EnableBridgeBridge == true)
                    TerrainCreationLogger.Current?.Detail(
                        $"[BRIDGE-BRIDGE] span {fp.SpanId} over bridge {other.SplineId} " +
                        $"(layers {fp.Layer}/{lowerLayer}) — detection only, R6 multi-level deferred");
            }
        }
    }

    /// <summary>
    ///     Detects junctions where roads connect to roundabout rings.
    ///     Called after roundabout ring splines are added to the network.
    ///     For each roundabout, this method:
    ///     1. Finds all road splines with endpoints near the roundabout ring
    ///     2. Creates Roundabout-type junctions for each connection
    ///     3. Updates the network's junction list with roundabout junctions
    ///     4. Returns RoundaboutJunctionInfo for each roundabout for harmonization
    /// </summary>
    /// <param name="network">The unified road network containing roundabout ring splines.</param>
    /// <param name="roundaboutInfos">Information about processed roundabouts from RoundaboutMerger.</param>
    /// <param name="detectionRadius">Detection radius for connections (typically RoundaboutConnectionRadiusMeters).</param>
    /// <returns>List of roundabout junction info for elevation harmonization.</returns>
    public List<RoundaboutJunctionInfo> DetectRoundaboutJunctions(
        UnifiedRoadNetwork network,
        List<RoundaboutMerger.ProcessedRoundaboutInfo> roundaboutInfos,
        float detectionRadius)
    {
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("DetectRoundaboutJunctions");

        var roundaboutJunctionInfos = new List<RoundaboutJunctionInfo>();
        var totalRoundaboutJunctions = 0;

        if (roundaboutInfos.Count == 0)
        {
            TerrainLogger.Detail("No roundabout infos provided for junction detection");
            return roundaboutJunctionInfos;
        }

        // Build set of roundabout spline IDs for quick lookup
        var roundaboutSplineIds = new HashSet<int>();
        var roundaboutInfoBySplineIndex = new Dictionary<int, RoundaboutMerger.ProcessedRoundaboutInfo>();

        foreach (var info in roundaboutInfos)
        {
            if (!info.IsValid) continue;

            // Find the corresponding ParameterizedRoadSpline in the network
            var matchingSpline = FindRoundaboutSplineInNetwork(network, info);
            if (matchingSpline != null)
            {
                roundaboutSplineIds.Add(matchingSpline.SplineId);
                roundaboutInfoBySplineIndex[matchingSpline.SplineId] = info;
            }
        }

        if (roundaboutSplineIds.Count == 0)
        {
            TerrainLogger.Warning("No roundabout splines found in network - roundabout junction detection skipped");
            return roundaboutJunctionInfos;
        }

        TerrainLogger.Info($"Detecting roundabout junctions for {roundaboutSplineIds.Count} roundabout(s)");

        // For each roundabout, find connecting roads
        TerrainLogger.SuppressDetailedLogging = true;
        foreach (var roundaboutSplineId in roundaboutSplineIds)
        {
            var roundaboutInfo = roundaboutInfoBySplineIndex[roundaboutSplineId];
            var roundaboutSpline = network.GetSplineById(roundaboutSplineId);
            if (roundaboutSpline == null) continue;

            var junctionInfo = new RoundaboutJunctionInfo
            {
                RoundaboutSplineId = roundaboutSplineId,
                CenterMeters = roundaboutInfo.CenterMeters,
                RadiusMeters = roundaboutInfo.RadiusMeters
            };

            // Find all non-roundabout splines with endpoints near this roundabout
            foreach (var spline in network.Splines)
            {
                // Skip roundabout ring splines
                if (roundaboutSplineIds.Contains(spline.SplineId))
                    continue;

                // Check start endpoint
                var distToStart = DistanceToRing(spline.StartPoint, roundaboutInfo.CenterMeters,
                    roundaboutInfo.RadiusMeters);
                if (distToStart <= detectionRadius)
                {
                    var junction = CreateRoundaboutJunction(
                        network, roundaboutSpline, spline,
                        true,
                        roundaboutInfo, junctionInfo);

                    if (junction != null)
                    {
                        junctionInfo.Junctions.Add(junction);
                        network.Junctions.Add(junction.ParentJunction);
                        totalRoundaboutJunctions++;
                    }
                }

                // Check end endpoint
                var distToEnd = DistanceToRing(spline.EndPoint, roundaboutInfo.CenterMeters,
                    roundaboutInfo.RadiusMeters);
                if (distToEnd <= detectionRadius)
                {
                    var junction = CreateRoundaboutJunction(
                        network, roundaboutSpline, spline,
                        false,
                        roundaboutInfo, junctionInfo);

                    if (junction != null)
                    {
                        junctionInfo.Junctions.Add(junction);
                        network.Junctions.Add(junction.ParentJunction);
                        totalRoundaboutJunctions++;
                    }
                }
            }

            if (junctionInfo.Junctions.Count > 0)
            {
                roundaboutJunctionInfos.Add(junctionInfo);
                TerrainLogger.Detail($"  Roundabout {roundaboutSplineId}: " +
                                     $"{junctionInfo.Junctions.Count} connection(s), " +
                                     $"radius={roundaboutInfo.RadiusMeters:F1}m");
            }
        }

        TerrainLogger.SuppressDetailedLogging = false;

        TerrainLogger.Info(
            $"Detected {totalRoundaboutJunctions} roundabout junction(s) across {roundaboutJunctionInfos.Count} roundabout(s)");
        perfLog?.Timing($"Detected {totalRoundaboutJunctions} roundabout junctions");

        return roundaboutJunctionInfos;
    }

    /// <summary>
    ///     Calculates the distance from a point to a circular ring.
    ///     Returns the absolute distance to the ring (how far inside or outside).
    /// </summary>
    private static float DistanceToRing(Vector2 point, Vector2 center, float radius)
    {
        var distToCenter = Vector2.Distance(point, center);
        return Math.Abs(distToCenter - radius);
    }

    /// <summary>
    ///     Finds the ParameterizedRoadSpline in the network that corresponds to a ProcessedRoundaboutInfo.
    ///     Matches by checking if spline center is near the roundabout center.
    /// </summary>
    private static ParameterizedRoadSpline? FindRoundaboutSplineInNetwork(
        UnifiedRoadNetwork network,
        RoundaboutMerger.ProcessedRoundaboutInfo roundaboutInfo)
    {
        // The roundabout spline should have been added to the network
        // Look for a closed-loop spline near the roundabout center
        const float matchTolerance = 5.0f; // 5 meters tolerance

        foreach (var spline in network.Splines)
        {
            // Check if start and end are close (closed loop)
            if (Vector2.Distance(spline.StartPoint, spline.EndPoint) > matchTolerance)
                continue;

            // Check if the center of the spline is near the roundabout center
            var splineCenter = (spline.StartPoint + spline.EndPoint) / 2;

            // Better: calculate actual center from a point on the spline
            var midPoint = spline.Spline.GetPointAtDistance(spline.TotalLengthMeters / 2);
            var estimatedCenter = (spline.StartPoint + midPoint + spline.EndPoint) / 3;

            if (Vector2.Distance(estimatedCenter, roundaboutInfo.CenterMeters) < roundaboutInfo.RadiusMeters * 2)
                return spline;
        }

        return null;
    }

    /// <summary>
    ///     Creates a RoundaboutJunction for a connecting road meeting a roundabout ring.
    /// </summary>
    private RoundaboutJunction? CreateRoundaboutJunction(
        UnifiedRoadNetwork network,
        ParameterizedRoadSpline roundaboutSpline,
        ParameterizedRoadSpline connectingSpline,
        bool isConnectingRoadStart,
        RoundaboutMerger.ProcessedRoundaboutInfo roundaboutInfo,
        RoundaboutJunctionInfo junctionInfo)
    {
        var endpoint = isConnectingRoadStart ? connectingSpline.StartPoint : connectingSpline.EndPoint;

        // Find the closest cross-section on the connecting road's endpoint
        var endpointCs = GetEndpointCrossSection(network, connectingSpline.SplineId, isConnectingRoadStart);
        if (endpointCs == null)
        {
            TerrainLogger.Detail($"Could not find endpoint cross-section for spline {connectingSpline.SplineId}");
            return null;
        }

        // Find the closest point on the roundabout ring
        var closestRingDistance = FindClosestDistanceOnRing(roundaboutSpline.Spline, endpoint);
        var closestRingPoint = roundaboutSpline.Spline.GetPointAtDistance(closestRingDistance);

        // Find the closest cross-section on the roundabout ring
        var ringCs = GetClosestCrossSectionOnSpline(network, roundaboutSpline.SplineId, closestRingPoint);
        if (ringCs == null)
        {
            TerrainLogger.Detail(
                $"Could not find ring cross-section for roundabout spline {roundaboutSpline.SplineId}");
            return null;
        }

        // Calculate junction position (midpoint between endpoint and ring point)
        var junctionPosition = (endpoint + closestRingPoint) / 2;

        // Calculate angle around the roundabout
        var angleDegrees = CalculateAngleFromCenter(roundaboutInfo.CenterMeters, closestRingPoint);

        // Determine connection direction from ProcessedRoundaboutInfo if available
        var direction = RoundaboutConnectionDirection.Bidirectional;
        // Check if we have processed connection info for this road
        if (roundaboutInfo.OriginalRoundabout != null)
        {
            var originalConnection = roundaboutInfo.OriginalRoundabout.Connections
                .FirstOrDefault(c => IsMatchingConnection(c, connectingSpline));
            if (originalConnection != null) direction = originalConnection.Direction;
        }

        // Create the parent NetworkJunction
        var networkJunction = new NetworkJunction
        {
            Position = junctionPosition,
            Type = JunctionType.Roundabout
        };

        // Add continuous contributor (roundabout ring)
        networkJunction.Contributors.Add(new JunctionContributor
        {
            CrossSection = ringCs,
            Spline = roundaboutSpline,
            IsSplineStart = false,
            IsSplineEnd = false // Ring is continuous
        });

        // Add terminating contributor (connecting road)
        networkJunction.Contributors.Add(new JunctionContributor
        {
            CrossSection = endpointCs,
            Spline = connectingSpline,
            IsSplineStart = isConnectingRoadStart,
            IsSplineEnd = !isConnectingRoadStart
        });

        // Assign junction ID
        networkJunction.JunctionId = network.Junctions.Count;

        // Create the RoundaboutJunction
        var roundaboutJunction = new RoundaboutJunction
        {
            ParentJunction = networkJunction,
            RoundaboutSplineId = roundaboutSpline.SplineId,
            ConnectingRoadSplineId = connectingSpline.SplineId,
            ConnectionPointMeters = closestRingPoint,
            DistanceAlongRoundabout = closestRingDistance,
            AngleDegrees = angleDegrees,
            Direction = direction,
            RoundaboutCenterMeters = roundaboutInfo.CenterMeters,
            RoundaboutRadiusMeters = roundaboutInfo.RadiusMeters,
            IsConnectingRoadStart = isConnectingRoadStart
        };

        return roundaboutJunction;
    }

    /// <summary>
    ///     Gets the endpoint cross-section for a spline.
    /// </summary>
    private static UnifiedCrossSection? GetEndpointCrossSection(
        UnifiedRoadNetwork network,
        int splineId,
        bool isStart)
    {
        var crossSections = network.GetCrossSectionsForSpline(splineId).ToList();
        if (crossSections.Count == 0)
            return null;

        return isStart ? crossSections[0] : crossSections[^1];
    }

    /// <summary>
    ///     Finds the closest cross-section on a spline to a given point.
    /// </summary>
    private static UnifiedCrossSection? GetClosestCrossSectionOnSpline(
        UnifiedRoadNetwork network,
        int splineId,
        Vector2 targetPoint)
    {
        var crossSections = network.GetCrossSectionsForSpline(splineId).ToList();
        if (crossSections.Count == 0)
            return null;

        return crossSections
            .OrderBy(cs => Vector2.DistanceSquared(cs.CenterPoint, targetPoint))
            .First();
    }

    /// <summary>
    ///     Finds the distance along a spline that is closest to a target point.
    /// </summary>
    private static float FindClosestDistanceOnRing(RoadSpline spline, Vector2 targetPoint)
    {
        const float sampleInterval = 0.5f; // 0.5 meter intervals
        float closestDistance = 0;
        var minDistSq = float.MaxValue;

        for (float d = 0; d <= spline.TotalLength; d += sampleInterval)
        {
            var point = spline.GetPointAtDistance(d);
            var distSq = Vector2.DistanceSquared(point, targetPoint);
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closestDistance = d;
            }
        }

        // Refine search around the found point
        var searchStart = Math.Max(0, closestDistance - sampleInterval);
        var searchEnd = Math.Min(spline.TotalLength, closestDistance + sampleInterval);
        const float refineSampleInterval = 0.05f;

        for (var d = searchStart; d <= searchEnd; d += refineSampleInterval)
        {
            var point = spline.GetPointAtDistance(d);
            var distSq = Vector2.DistanceSquared(point, targetPoint);
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closestDistance = d;
            }
        }

        return closestDistance;
    }

    /// <summary>
    ///     Calculates the angle from center to a point (0 = East, 90 = North).
    /// </summary>
    private static float CalculateAngleFromCenter(Vector2 center, Vector2 point)
    {
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var angleRadians = MathF.Atan2(dy, dx);
        var angleDegrees = angleRadians * 180f / MathF.PI;
        if (angleDegrees < 0) angleDegrees += 360f;
        return angleDegrees;
    }

    /// <summary>
    ///     Checks if a RoundaboutConnection matches a connecting spline.
    /// </summary>
    private static bool IsMatchingConnection(
        RoundaboutConnection connection,
        ParameterizedRoadSpline spline)
    {
        // Match by display name if available
        if (!string.IsNullOrEmpty(spline.DisplayName) &&
            connection.ConnectingRoad != null &&
            !string.IsNullOrEmpty(connection.ConnectingRoad.DisplayName))
            return spline.DisplayName.Equals(connection.ConnectingRoad.DisplayName,
                StringComparison.OrdinalIgnoreCase);

        return false;
    }
}