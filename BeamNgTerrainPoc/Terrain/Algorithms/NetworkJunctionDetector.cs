using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Terrain.Osm.Processing;

namespace BeamNgTerrainPoc.Terrain.Algorithms;

/// <summary>
/// Detects junctions across the entire unified road network.
/// Supports detection of:
/// - Endpoint clusters (Y, X intersections)
/// - T-junctions (endpoint touching middle of another road)
/// - Complex intersections (roundabouts, 4+ roads meeting)
/// 
/// This detector operates on the unified network, meaning it can detect
/// junctions between roads from different materials (cross-material junctions).
/// </summary>
public class NetworkJunctionDetector
{
    /// <summary>
    /// Spatial index cell size in meters for faster proximity queries.
    /// </summary>
    private const float SpatialIndexCellSize = 50f;

    /// <summary>
    /// Detects all junctions in the unified road network.
    /// 
    /// Algorithm:
    /// 1. Build spatial index of all cross-section endpoints
    /// 2. Cluster endpoints within detection radius
    /// 3. Classify junction types (T, Y, X, Complex)
    /// 4. For T-junctions: identify continuous vs. terminating roads
    /// 5. Detect mid-spline crossings (where two roads cross without either terminating)
    /// </summary>
    /// <param name="network">The unified road network containing all splines and cross-sections.</param>
    /// <param name="globalDetectionRadius">Global detection radius in meters (can be overridden per-material).</param>
    /// <returns>List of detected network junctions.</returns>
    public List<NetworkJunction> DetectJunctions(
        UnifiedRoadNetwork network,
        float globalDetectionRadius)
    {
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
        var junctions = ClusterEndpointsIntoJunctions(endpoints, network, globalDetectionRadius);
        perfLog?.Timing($"Clustered into {junctions.Count} potential junctions");

        // Step 4: Detect T-junctions (endpoint meeting middle of another road)
        var tJunctionCount = DetectTJunctions(junctions, network, spatialIndex, globalDetectionRadius);
        if (tJunctionCount > 0)
        {
            TerrainLogger.Info($"  Detected {tJunctionCount} T-junction(s) (endpoint meeting middle of road)");
        }

        // Step 5: Detect mid-spline crossings (two roads crossing without either terminating)
        var midSplineCrossings = DetectMidSplineCrossings(network, spatialIndex, globalDetectionRadius, junctions);
        if (midSplineCrossings.Count > 0)
        {
            junctions.AddRange(midSplineCrossings);
            TerrainLogger.Info($"  Detected {midSplineCrossings.Count} mid-spline crossing(s) (roads crossing without endpoints)");
        }
        perfLog?.Timing($"Mid-spline crossing detection complete");

        // Step 6: Classify junction types
        ClassifyJunctions(junctions, network);

        // Step 7: Assign junction IDs and calculate centroids
        for (int i = 0; i < junctions.Count; i++)
        {
            junctions[i].JunctionId = i;
            junctions[i].CalculateCentroid();
        }

        // Log junction statistics
        var junctionsByType = junctions.GroupBy(j => j.Type).ToDictionary(g => g.Key, g => g.Count());
        TerrainLogger.Info($"  Junction breakdown: " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.TJunction)} T, " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.YJunction)} Y, " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.CrossRoads)} X, " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.Complex)} Complex, " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.Endpoint)} Isolated, " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.MidSplineCrossing)} MidCrossing, " +
                          $"{junctionsByType.GetValueOrDefault(JunctionType.Roundabout)} Roundabout");

        var crossMaterialCount = junctions.Count(j => j.IsCrossMaterial);
        if (crossMaterialCount > 0)
        {
            TerrainLogger.Info($"  {crossMaterialCount} junction(s) involve multiple materials");
        }

        // Store junctions in the network
        network.Junctions.Clear();
        network.Junctions.AddRange(junctions);

        perfLog?.Timing($"Detected {junctions.Count} total junctions");

        return junctions;
    }

    /// <summary>
    /// Finds all spline endpoints (first and last cross-sections of each spline).
    /// </summary>
    private List<UnifiedCrossSection> FindSplineEndpoints(UnifiedRoadNetwork network)
    {
        var endpoints = new List<UnifiedCrossSection>();

        foreach (var spline in network.Splines)
        {
            var splineSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();

            if (splineSections.Count >= 1)
            {
                // First endpoint
                endpoints.Add(splineSections[0]);

                // Last endpoint (if different from first)
                if (splineSections.Count > 1)
                {
                    endpoints.Add(splineSections[^1]);
                }
            }
        }

        return endpoints;
    }

    /// <summary>
    /// Builds a spatial index for fast proximity queries.
    /// Returns a dictionary mapping grid cell -> cross-sections in that cell.
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
    /// Queries the spatial index for cross-sections near a point.
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

        for (int cx = minCellX; cx <= maxCellX; cx++)
        {
            for (int cy = minCellY; cy <= maxCellY; cy++)
            {
                if (index.TryGetValue((cx, cy), out var cell))
                {
                    foreach (var cs in cell)
                    {
                        var distSq = Vector2.DistanceSquared(cs.CenterPoint, position);
                        if (distSq <= radiusSq)
                        {
                            yield return cs;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Clusters nearby endpoints into junctions using transitive closure.
    /// </summary>
    private List<NetworkJunction> ClusterEndpointsIntoJunctions(
        List<UnifiedCrossSection> endpoints,
        UnifiedRoadNetwork network,
        float globalDetectionRadius)
    {
        var junctions = new List<NetworkJunction>();
        var assigned = new HashSet<int>(); // Track which endpoints are assigned (by their Index)

        for (int i = 0; i < endpoints.Count; i++)
        {
            if (assigned.Contains(endpoints[i].Index))
                continue;

            var junction = new NetworkJunction();
            var cluster = new List<int> { i };
            assigned.Add(endpoints[i].Index);

            // Expand cluster using transitive closure
            bool expanded;
            do
            {
                expanded = false;
                for (int j = 0; j < endpoints.Count; j++)
                {
                    if (assigned.Contains(endpoints[j].Index))
                        continue;

                    // Get effective detection radius (use per-spline if available, else global)
                    float detectionRadius = globalDetectionRadius;
                    var splineParams = network.GetParametersForSpline(endpoints[j].OwnerSplineId);
                    if (splineParams?.JunctionHarmonizationParameters != null)
                    {
                        detectionRadius = splineParams.JunctionHarmonizationParameters.JunctionDetectionRadiusMeters;
                    }

                    // Check distance to any endpoint in current cluster
                    foreach (var idx in cluster)
                    {
                        float dist = Vector2.Distance(endpoints[idx].CenterPoint, endpoints[j].CenterPoint);
                        if (dist <= detectionRadius)
                        {
                            cluster.Add(j);
                            assigned.Add(endpoints[j].Index);
                            expanded = true;
                            break;
                        }
                    }
                }
            } while (expanded);

            // Build junction from cluster
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

            if (junction.Contributors.Count > 0)
            {
                junctions.Add(junction);
            }
        }

        return junctions;
    }

    /// <summary>
    /// Detects T-junctions where an endpoint meets the middle of another road.
    /// Updates junction classifications and adds continuous road cross-sections.
    /// 
    /// IMPORTANT: This handles two scenarios:
    /// 1. A single endpoint near the middle of another spline (classic T-junction)
    /// 2. Multiple endpoints clustered together, but one of them is near the MIDDLE 
    ///    of another spline (the passing-through spline should dominate elevation)
    /// 
    /// For WITHIN-MATERIAL junctions: If spline A's endpoint is near spline B's middle,
    /// spline B is the "continuous" road and A is the "terminating" road.
    /// </summary>
    /// <returns>Number of T-junctions detected.</returns>
    private int DetectTJunctions(
        List<NetworkJunction> junctions,
        UnifiedRoadNetwork network,
        Dictionary<(int, int), List<UnifiedCrossSection>> spatialIndex,
        float globalDetectionRadius)
    {
        int tJunctionCount = 0;

        // Process ALL junctions to find passing-through splines
        foreach (var junction in junctions.ToList())
        {
            // Calculate junction center from all endpoint contributors
            junction.CalculateCentroid();
            var junctionPosition = junction.Position;

            // Get effective detection radius (use maximum from all contributors)
            float detectionRadius = globalDetectionRadius;
            foreach (var contributor in junction.Contributors)
            {
                if (contributor.Spline.Parameters.JunctionHarmonizationParameters != null)
                {
                    detectionRadius = Math.Max(detectionRadius,
                        contributor.Spline.Parameters.JunctionHarmonizationParameters.JunctionDetectionRadiusMeters);
                }
            }

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
            var continuousContributors = new List<(UnifiedCrossSection cs, ParameterizedRoadSpline spline, float dist)>();

            foreach (var cs in QuerySpatialIndex(spatialIndex, junctionPosition, detectionRadius))
            {
                // Skip if this cross-section is itself an endpoint
                if (cs.IsSplineStart || cs.IsSplineEnd)
                    continue;

                // This is a mid-spline cross-section near the junction
                float dist = Vector2.Distance(junctionPosition, cs.CenterPoint);
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
                {
                    // The spline has an endpoint here but also passes through nearby
                    // This is unusual - log it but don't add duplicate
                    TerrainCreationLogger.Current?.Detail($"Spline {spline.SplineId} has endpoint at junction but also passes through nearby");
                    continue;
                }

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
    /// Classifies each junction based on the number and type of contributors.
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
    /// Gets the effective junction detection radius for a given location.
    /// Uses the maximum radius among nearby splines.
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
        float maxRadius = globalDefault;

        foreach (var spline in network.Splines)
        {
            // Check if this spline is close to the position
            var startDist = Vector2.Distance(spline.StartPoint, position);
            var endDist = Vector2.Distance(spline.EndPoint, position);

            if (startDist < globalDefault * 2 || endDist < globalDefault * 2)
            {
                var splineParams = spline.Parameters.JunctionHarmonizationParameters;
                if (splineParams != null && splineParams.JunctionDetectionRadiusMeters > maxRadius)
                {
                    maxRadius = splineParams.JunctionDetectionRadiusMeters;
                }
            }
        }

        return maxRadius;
    }

    /// <summary>
    /// Detects mid-spline crossings where two roads cross each other without either terminating.
    /// This handles the case where roads physically intersect but neither has an endpoint at the crossing.
    /// 
    /// Algorithm:
    /// 1. For each spline, sample cross-sections at regular intervals
    /// 2. For each cross-section, check if any OTHER spline's cross-sections are very close
    /// 3. If two mid-spline cross-sections from different splines are close, it's a crossing
    /// 4. Cluster nearby crossings to avoid duplicates
    /// 5. Skip crossings that are already covered by existing junctions
    /// </summary>
    private List<NetworkJunction> DetectMidSplineCrossings(
        UnifiedRoadNetwork network,
        Dictionary<(int, int), List<UnifiedCrossSection>> spatialIndex,
        float globalDetectionRadius,
        List<NetworkJunction> existingJunctions)
    {
        var crossings = new List<NetworkJunction>();
        var processedPairs = new HashSet<(int, int)>(); // Track spline pairs we've already found crossings for
        
        // Use a tighter radius for mid-spline crossing detection
        // Crossings should be where roads actually overlap, not just nearby
        float crossingDetectionRadius = globalDetectionRadius * 0.5f;
        
        // Track positions where we've already created crossings to avoid duplicates
        var existingJunctionPositions = existingJunctions
            .Select(j => j.Position)
            .ToList();

        foreach (var spline in network.Splines)
        {
            var splineSections = network.GetCrossSectionsForSpline(spline.SplineId)
                .Where(cs => !cs.IsSplineStart && !cs.IsSplineEnd) // Only mid-spline sections
                .ToList();

            // Sample every Nth cross-section to reduce computation (crossings span multiple sections)
            int sampleInterval = Math.Max(1, splineSections.Count / 50); // Sample ~50 points per spline
            
            for (int i = 0; i < splineSections.Count; i += sampleInterval)
            {
                var cs = splineSections[i];
                
                // Skip if too close to an existing junction
                if (existingJunctionPositions.Any(p => Vector2.Distance(p, cs.CenterPoint) < crossingDetectionRadius))
                    continue;

                // Find cross-sections from OTHER splines that are very close
                var nearbySections = QuerySpatialIndex(spatialIndex, cs.CenterPoint, crossingDetectionRadius)
                    .Where(other => 
                        other.OwnerSplineId != spline.SplineId && // Different spline
                        !other.IsSplineStart && !other.IsSplineEnd) // Also mid-spline
                    .ToList();

                if (nearbySections.Count == 0)
                    continue;

                // Group by spline to find unique crossings
                var crossingSplines = nearbySections
                    .GroupBy(s => s.OwnerSplineId)
                    .Select(g => (SplineId: g.Key, ClosestSection: g.OrderBy(s => 
                        Vector2.Distance(s.CenterPoint, cs.CenterPoint)).First()))
                    .ToList();

                foreach (var (otherSplineId, otherCs) in crossingSplines)
                {
                    // Create a canonical pair key to avoid duplicates
                    var pairKey = spline.SplineId < otherSplineId 
                        ? (spline.SplineId, otherSplineId) 
                        : (otherSplineId, spline.SplineId);

                    if (processedPairs.Contains(pairKey))
                        continue;

                    var otherSpline = network.GetSplineById(otherSplineId);
                    if (otherSpline == null)
                        continue;

                    // Calculate crossing point as midpoint between the two closest cross-sections
                    var crossingPoint = (cs.CenterPoint + otherCs.CenterPoint) / 2f;
                    
                    // Double-check this isn't near an existing junction
                    if (existingJunctionPositions.Any(p => Vector2.Distance(p, crossingPoint) < crossingDetectionRadius))
                        continue;

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
                    existingJunctionPositions.Add(crossingPoint); // Prevent nearby duplicates
                }
            }
        }

        return crossings;
    }

    /// <summary>
    /// Detects junctions where roads connect to roundabout rings.
    /// Called after roundabout ring splines are added to the network.
    /// 
    /// For each roundabout, this method:
    /// 1. Finds all road splines with endpoints near the roundabout ring
    /// 2. Creates Roundabout-type junctions for each connection
    /// 3. Updates the network's junction list with roundabout junctions
    /// 4. Returns RoundaboutJunctionInfo for each roundabout for harmonization
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
        int totalRoundaboutJunctions = 0;

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
                var distToStart = DistanceToRing(spline.StartPoint, roundaboutInfo.CenterMeters, roundaboutInfo.RadiusMeters);
                if (distToStart <= detectionRadius)
                {
                    var junction = CreateRoundaboutJunction(
                        network, roundaboutSpline, spline, 
                        isConnectingRoadStart: true, 
                        roundaboutInfo, junctionInfo);
                    
                    if (junction != null)
                    {
                        junctionInfo.Junctions.Add(junction);
                        network.Junctions.Add(junction.ParentJunction);
                        totalRoundaboutJunctions++;
                    }
                }

                // Check end endpoint
                var distToEnd = DistanceToRing(spline.EndPoint, roundaboutInfo.CenterMeters, roundaboutInfo.RadiusMeters);
                if (distToEnd <= detectionRadius)
                {
                    var junction = CreateRoundaboutJunction(
                        network, roundaboutSpline, spline, 
                        isConnectingRoadStart: false, 
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

        TerrainLogger.Info($"Detected {totalRoundaboutJunctions} roundabout junction(s) across {roundaboutJunctionInfos.Count} roundabout(s)");
        perfLog?.Timing($"Detected {totalRoundaboutJunctions} roundabout junctions");

        return roundaboutJunctionInfos;
    }

    /// <summary>
    /// Calculates the distance from a point to a circular ring.
    /// Returns the absolute distance to the ring (how far inside or outside).
    /// </summary>
    private static float DistanceToRing(Vector2 point, Vector2 center, float radius)
    {
        float distToCenter = Vector2.Distance(point, center);
        return Math.Abs(distToCenter - radius);
    }

    /// <summary>
    /// Finds the ParameterizedRoadSpline in the network that corresponds to a ProcessedRoundaboutInfo.
    /// Matches by checking if spline center is near the roundabout center.
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
            {
                return spline;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a RoundaboutJunction for a connecting road meeting a roundabout ring.
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
            TerrainLogger.Detail($"Could not find ring cross-section for roundabout spline {roundaboutSpline.SplineId}");
            return null;
        }

        // Calculate junction position (midpoint between endpoint and ring point)
        var junctionPosition = (endpoint + closestRingPoint) / 2;

        // Calculate angle around the roundabout
        var angleDegrees = CalculateAngleFromCenter(roundaboutInfo.CenterMeters, closestRingPoint);

        // Determine connection direction from ProcessedRoundaboutInfo if available
        var direction = Osm.Models.RoundaboutConnectionDirection.Bidirectional;
        // Check if we have processed connection info for this road
        if (roundaboutInfo.OriginalRoundabout != null)
        {
            var originalConnection = roundaboutInfo.OriginalRoundabout.Connections
                .FirstOrDefault(c => IsMatchingConnection(c, connectingSpline));
            if (originalConnection != null)
            {
                direction = originalConnection.Direction;
            }
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
    /// Gets the endpoint cross-section for a spline.
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
    /// Finds the closest cross-section on a spline to a given point.
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
    /// Finds the distance along a spline that is closest to a target point.
    /// </summary>
    private static float FindClosestDistanceOnRing(RoadSpline spline, Vector2 targetPoint)
    {
        const float sampleInterval = 0.5f; // 0.5 meter intervals
        float closestDistance = 0;
        float minDistSq = float.MaxValue;

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
        float searchStart = Math.Max(0, closestDistance - sampleInterval);
        float searchEnd = Math.Min(spline.TotalLength, closestDistance + sampleInterval);
        const float refineSampleInterval = 0.05f;

        for (float d = searchStart; d <= searchEnd; d += refineSampleInterval)
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
    /// Calculates the angle from center to a point (0 = East, 90 = North).
    /// </summary>
    private static float CalculateAngleFromCenter(Vector2 center, Vector2 point)
    {
        float dx = point.X - center.X;
        float dy = point.Y - center.Y;
        float angleRadians = MathF.Atan2(dy, dx);
        float angleDegrees = angleRadians * 180f / MathF.PI;
        if (angleDegrees < 0) angleDegrees += 360f;
        return angleDegrees;
    }

    /// <summary>
    /// Checks if a RoundaboutConnection matches a connecting spline.
    /// </summary>
    private static bool IsMatchingConnection(
        Osm.Models.RoundaboutConnection connection,
        ParameterizedRoadSpline spline)
    {
        // Match by display name if available
        if (!string.IsNullOrEmpty(spline.DisplayName) && 
            connection.ConnectingRoad != null &&
            !string.IsNullOrEmpty(connection.ConnectingRoad.DisplayName))
        {
            return spline.DisplayName.Equals(connection.ConnectingRoad.DisplayName, 
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
