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
    /// <param name="globalDetectionRadius">Global detection radius in meters (can be overridden per-material).</param>
    /// <returns>List of detected network junctions.</returns>
    public List<NetworkJunction> DetectJunctions(
        UnifiedRoadNetwork network,
        float globalDetectionRadius)
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
        var junctions = ClusterEndpointsIntoJunctions(endpoints, network, globalDetectionRadius);
        perfLog?.Timing($"Clustered into {junctions.Count} potential junctions");

        // Step 4: Detect T-junctions (endpoint meeting middle of another road)
        var tJunctionCount = DetectTJunctions(junctions, network, spatialIndex, globalDetectionRadius);
        if (tJunctionCount > 0)
            TerrainCreationLogger.Current?.InfoFileOnly(
                $"Detected {tJunctionCount} T-junction(s) (endpoint meeting middle of road)");

        // Step 5: Detect mid-spline crossings (two roads crossing without either terminating)
        var midSplineCrossings = DetectMidSplineCrossings(network, spatialIndex, globalDetectionRadius, junctions);
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
                                                    $"{junctionsByType.GetValueOrDefault(JunctionType.Roundabout)} Roundabout");

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
    ///     Detects junctions using both geometric analysis AND OSM junction data.
    ///     OSM junctions serve as "hints" that boost detection confidence and can
    ///     create new junctions at locations that geometric analysis missed.
    ///     This method:
    ///     1. Filters OSM junctions to only include specified types
    ///     2. Runs standard geometric detection
    ///     3. Matches OSM junctions to geometric junctions within matchRadius
    ///     4. Creates NEW junctions from unmatched OSM data
    ///     5. Updates junction types based on OSM semantic information
    /// </summary>
    /// <param name="network">The unified road network containing all splines and cross-sections.</param>
    /// <param name="osmJunctions">OSM junction query result containing explicitly tagged and geometric junctions.</param>
    /// <param name="globalDetectionRadius">Global detection radius in meters (also used for OSM junction matching).</param>
    /// <param name="includedOsmTypes">
    ///     Optional list of OSM junction types to include in processing. If null or empty, all
    ///     types are processed.
    /// </param>
    /// <returns>List of detected network junctions enhanced with OSM data.</returns>
    public List<NetworkJunction> DetectJunctionsWithOsm(
        UnifiedRoadNetwork network,
        OsmJunctionQueryResult osmJunctions,
        float globalDetectionRadius,
        List<OsmJunctionType>? includedOsmTypes = null)
    {
        TerrainLogger.SuppressDetailedLogging = true;
        var perfLog = TerrainCreationLogger.Current;
        perfLog?.LogSection("NetworkJunctionDetector (OSM-enhanced)");

        // Step 1: Run standard geometric detection
        var geometricJunctions = DetectJunctions(network, globalDetectionRadius);

        if (osmJunctions.Junctions.Count == 0)
        {
            TerrainLogger.Info("  No OSM junctions provided, using geometric detection only");
            return geometricJunctions;
        }

        // Step 1.5: Filter OSM junctions to only include specified types
        var filteredOsmJunctions = osmJunctions.Junctions;
        var filteredOutCount = 0;

        if (includedOsmTypes != null && includedOsmTypes.Count > 0)
        {
            var includedSet = includedOsmTypes.ToHashSet();
            filteredOsmJunctions = osmJunctions.Junctions
                .Where(j => includedSet.Contains(j.Type))
                .ToList();

            filteredOutCount = osmJunctions.Junctions.Count - filteredOsmJunctions.Count;

            if (filteredOutCount > 0)
            {
                // Log which types were included
                var includedByType = filteredOsmJunctions
                    .GroupBy(j => j.Type)
                    .ToDictionary(g => g.Key, g => g.Count());

                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"Included {filteredOsmJunctions.Count} OSM junction(s) by type: " +
                    string.Join(", ", includedByType.Select(kvp => $"{kvp.Key}={kvp.Value}")) +
                    $" (filtered out {filteredOutCount})");
            }
        }

        TerrainCreationLogger.Current?.InfoFileOnly($"OSM junction hints: {filteredOsmJunctions.Count} " +
                                                    $"({osmJunctions.ExplicitJunctionCount} explicit, {osmJunctions.GeometricJunctionCount} geometric" +
                                                    (filteredOutCount > 0 ? $", {filteredOutCount} filtered out" : "") +
                                                    ")");

        if (filteredOsmJunctions.Count == 0)
        {
            TerrainLogger.Info("  All OSM junctions were filtered out, using geometric detection only");
            return geometricJunctions;
        }

        // Step 2: Match OSM junctions to geometric junctions
        var unmatchedOsmJunctions = MatchOsmJunctionsToGeometric(
            geometricJunctions,
            filteredOsmJunctions,
            globalDetectionRadius);

        perfLog?.Timing(
            $"Matched {osmJunctions.Junctions.Count - unmatchedOsmJunctions.Count} OSM junctions to geometric");

        // Step 3: Create new junctions from unmatched OSM data
        if (unmatchedOsmJunctions.Count > 0)
        {
            var newJunctions = CreateJunctionsFromUnmatchedOsm(
                network,
                unmatchedOsmJunctions,
                geometricJunctions,
                globalDetectionRadius);

            if (newJunctions.Count > 0)
            {
                // Assign junction IDs to new junctions
                var nextId = geometricJunctions.Count > 0
                    ? geometricJunctions.Max(j => j.JunctionId) + 1
                    : 0;

                foreach (var junction in newJunctions) junction.JunctionId = nextId++;

                geometricJunctions.AddRange(newJunctions);
                TerrainCreationLogger.Current?.InfoFileOnly(
                    $"Created {newJunctions.Count} new junction(s) from unmatched OSM data");
            }
        }

        // Step 4: Update junction types based on OSM semantic information
        UpdateJunctionTypesFromOsm(geometricJunctions);

        // Update network's junction list
        network.Junctions.Clear();
        network.Junctions.AddRange(geometricJunctions);

        // Log enhanced statistics
        var osmSourcedCount = geometricJunctions.Count(j => j.IsOsmSourced);
        var osmHintedCount = geometricJunctions.Count(j => j.OsmHint != null && !j.IsOsmSourced);

        TerrainCreationLogger.Current?.InfoFileOnly($"Junction summary: {geometricJunctions.Count} total, " +
                                                    $"{osmHintedCount} OSM-matched, {osmSourcedCount} OSM-sourced");

        // Log by OSM type
        var osmTypeBreakdown = geometricJunctions
            .Where(j => j.OsmHint != null)
            .GroupBy(j => j.OsmHint!.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        if (osmTypeBreakdown.Count > 0)
            TerrainCreationLogger.Current?.InfoFileOnly("OSM junction types: " +
                                                        string.Join(", ",
                                                            osmTypeBreakdown.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        perfLog?.Timing($"OSM-enhanced junction detection complete: {geometricJunctions.Count} junctions");

        TerrainLogger.SuppressDetailedLogging = false;
        return geometricJunctions;
    }

    /// <summary>
    ///     Matches OSM junctions to geometric junctions within a search radius.
    ///     Updates geometric junctions with OsmHint when matched.
    /// </summary>
    /// <param name="geometricJunctions">Junctions detected by geometric analysis.</param>
    /// <param name="osmJunctions">OSM junction data to match.</param>
    /// <param name="matchRadius">Maximum distance for matching (meters).</param>
    /// <returns>List of unmatched OSM junctions that could not be paired with geometric detections.</returns>
    private List<OsmJunction> MatchOsmJunctionsToGeometric(
        List<NetworkJunction> geometricJunctions,
        List<OsmJunction> osmJunctions,
        float matchRadius)
    {
        var unmatchedOsm = new List<OsmJunction>();
        var matchRadiusSq = matchRadius * matchRadius;

        // Build spatial index of geometric junctions for faster lookup
        var geoJunctionIndex = new Dictionary<(int, int), List<NetworkJunction>>();
        const float cellSize = 50f;

        foreach (var geoJunction in geometricJunctions)
        {
            var cellX = (int)(geoJunction.Position.X / cellSize);
            var cellY = (int)(geoJunction.Position.Y / cellSize);
            var key = (cellX, cellY);

            if (!geoJunctionIndex.TryGetValue(key, out var list))
            {
                list = [];
                geoJunctionIndex[key] = list;
            }

            list.Add(geoJunction);
        }

        TerrainLogger.SuppressDetailedLogging = true;
        foreach (var osmJunction in osmJunctions)
        {
            var osmPos = osmJunction.PositionMeters;

            // Skip OSM junctions that haven't been transformed to terrain coordinates
            if (osmPos == Vector2.Zero && osmJunction.Location != null)
            {
                TerrainLogger.Detail(
                    $"Skipping OSM junction {osmJunction.OsmNodeId}: not transformed to terrain coordinates");
                continue;
            }

            // Query nearby cells
            var cellX = (int)(osmPos.X / cellSize);
            var cellY = (int)(osmPos.Y / cellSize);

            NetworkJunction? closestMatch = null;
            var closestDistSq = float.MaxValue;

            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                var key = (cellX + dx, cellY + dy);
                if (!geoJunctionIndex.TryGetValue(key, out var candidates))
                    continue;

                foreach (var candidate in candidates)
                {
                    // Skip if already has an OSM hint
                    if (candidate.OsmHint != null)
                        continue;

                    var distSq = Vector2.DistanceSquared(osmPos, candidate.Position);
                    if (distSq <= matchRadiusSq && distSq < closestDistSq)
                    {
                        closestDistSq = distSq;
                        closestMatch = candidate;
                    }
                }
            }

            if (closestMatch != null)
            {
                // Match found - update the geometric junction with OSM hint
                closestMatch.OsmHint = osmJunction;
                closestMatch.OsmMatchDistance = MathF.Sqrt(closestDistSq);

                TerrainCreationLogger.Current?.Detail(
                    $"Matched OSM junction {osmJunction.DisplayName} ({osmJunction.Type}) " +
                    $"to geometric junction #{closestMatch.JunctionId} at {closestMatch.OsmMatchDistance:F1}m");
            }
            else
            {
                // No match found - add to unmatched list
                unmatchedOsm.Add(osmJunction);
            }
        }

        TerrainLogger.SuppressDetailedLogging = false;
        TerrainCreationLogger.Current?.InfoFileOnly(
            $"OSM junction matching: {osmJunctions.Count - unmatchedOsm.Count} matched, " +
            $"{unmatchedOsm.Count} unmatched");

        return unmatchedOsm;
    }

    /// <summary>
    ///     Creates new NetworkJunction objects from OSM junctions that couldn't be matched
    ///     to existing geometric detections.
    ///     Algorithm:
    ///     1. For each unmatched OSM junction, find nearby cross-sections
    ///     2. If cross-sections from 2+ different splines are found, create junction
    ///     3. Mark as OSM-sourced for tracking
    ///     IMPORTANT: For OSM CrossRoads type (way_cnt:2-), we specifically look for
    ///     mid-spline crossings since these represent 2 roads sharing a node (crossing point).
    ///     These become MidSplineCrossing junctions that the CrossroadToTJunctionConverter processes.
    ///     CRITICAL: OSM junctions should ONLY create new network junctions if there is NO
    ///     existing geometric junction nearby. The proximity threshold must be LARGER than
    ///     the match radius to account for positional differences between:
    ///     - OSM node positions (from map data)
    ///     - Geometric junction centroids (calculated from cross-section clusters)
    ///     Using 1.5x the search radius ensures we don't create duplicates when positions differ.
    /// </summary>
    private List<NetworkJunction> CreateJunctionsFromUnmatchedOsm(
        UnifiedRoadNetwork network,
        List<OsmJunction> unmatchedOsm,
        List<NetworkJunction> existingJunctions,
        float searchRadius)
    {
        var newJunctions = new List<NetworkJunction>();

        // Build spatial index of all cross-sections
        var crossSectionIndex = BuildSpatialIndex(network.CrossSections);

        // Track positions where we already have junctions to avoid duplicates
        // CRITICAL: Use 1.5x the search radius to account for positional differences between
        // OSM node positions and geometric junction centroids. This prevents creating duplicate
        // junctions that cause overlapping harmonization zones (which creates bumps/artifacts).
        var existingPositions = existingJunctions
            .Select(j => j.Position)
            .ToList();

        // Use a larger proximity threshold than the search radius
        // OSM junction positions can differ from geometric centroids by several meters
        var proximityThreshold = searchRadius * 1.5f;

        TerrainLogger.SuppressDetailedLogging = true;
        foreach (var osmJunction in unmatchedOsm)
        {
            var osmPos = osmJunction.PositionMeters;

            // Skip OSM junctions that haven't been transformed to terrain coordinates
            if (osmPos == Vector2.Zero && osmJunction.Location != null)
            {
                TerrainLogger.Detail(
                    $"Skipping OSM junction {osmJunction.OsmNodeId}: not transformed to terrain coordinates");
                continue;
            }

            // Skip if too close to existing junction (use 1.5x search radius to prevent duplicates)
            // OSM node positions can differ from geometric junction centroids by several meters,
            // so we need a larger threshold than just the search radius.
            var closestExistingDist = existingPositions
                .Select(p => Vector2.Distance(p, osmPos))
                .DefaultIfEmpty(float.MaxValue)
                .Min();

            if (closestExistingDist < proximityThreshold)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"Skipping OSM junction {osmJunction.DisplayName} ({osmJunction.Type}): " +
                    $"too close to existing junction ({closestExistingDist:F1}m < {proximityThreshold:F1}m threshold)");
                continue;
            }

            // Find cross-sections from any spline within search radius
            var nearbySections = QuerySpatialIndex(crossSectionIndex, osmPos, searchRadius).ToList();

            if (nearbySections.Count == 0)
            {
                TerrainCreationLogger.Current?.Detail(
                    $"Skipping OSM junction {osmJunction.DisplayName}: no nearby cross-sections");
                continue;
            }

            // Group by spline ID to find how many different roads are nearby
            var sectionsBySpline = nearbySections
                .GroupBy(cs => cs.OwnerSplineId)
                .ToDictionary(g => g.Key, g => g.OrderBy(cs =>
                    Vector2.DistanceSquared(cs.CenterPoint, osmPos)).First());

            if (sectionsBySpline.Count < 2)
            {
                // Only one road nearby - might be a mid-road feature (crossing, traffic light)
                // Still useful to record but mark differently
                var singleSpline = network.GetSplineById(sectionsBySpline.Keys.First());
                if (singleSpline != null && osmJunction.IsExplicitlyTagged)
                {
                    // Create a "feature point" junction for explicitly tagged OSM features
                    // even if they're mid-road (e.g., traffic signals, crossings)
                    var closestCs = sectionsBySpline.Values.First();

                    var junction = new NetworkJunction
                    {
                        Position = osmPos,
                        Type = MapOsmTypeToJunctionType(osmJunction.Type, 1),
                        OsmHint = osmJunction,
                        IsOsmSourced = true
                    };

                    junction.Contributors.Add(new JunctionContributor
                    {
                        CrossSection = closestCs,
                        Spline = singleSpline,
                        IsSplineStart = closestCs.IsSplineStart,
                        IsSplineEnd = closestCs.IsSplineEnd
                    });

                    newJunctions.Add(junction);
                    existingPositions.Add(osmPos);

                    TerrainCreationLogger.Current?.Detail(
                        $"Created feature-point junction from OSM {osmJunction.DisplayName} ({osmJunction.Type})");
                }

                continue;
            }

            // Multiple roads nearby - create a proper junction
            // For OSM CrossRoads, check if this is a mid-spline crossing (2 continuous roads)
            var isMidSplineCrossing = false;
            var continuousCount = 0;

            foreach (var (splineId, closestCs) in sectionsBySpline)
                // Check if this is a mid-spline cross-section (not an endpoint)
                if (!closestCs.IsSplineStart && !closestCs.IsSplineEnd)
                    continuousCount++;

            // If 2+ roads pass through (neither terminates), this is a mid-spline crossing
            if (continuousCount >= 2 &&
                (osmJunction.Type == OsmJunctionType.CrossRoads || osmJunction.Type == OsmJunctionType.Unknown))
                isMidSplineCrossing = true;

            var junction2 = new NetworkJunction
            {
                Position = osmPos,
                Type = isMidSplineCrossing
                    ? JunctionType.MidSplineCrossing
                    : MapOsmTypeToJunctionType(osmJunction.Type, sectionsBySpline.Count),
                OsmHint = osmJunction,
                IsOsmSourced = true
            };

            // Add contributors from each nearby spline
            foreach (var (splineId, closestCs) in sectionsBySpline)
            {
                var spline = network.GetSplineById(splineId);
                if (spline == null) continue;

                junction2.Contributors.Add(new JunctionContributor
                {
                    CrossSection = closestCs,
                    Spline = spline,
                    IsSplineStart = closestCs.IsSplineStart,
                    IsSplineEnd = closestCs.IsSplineEnd
                });
            }

            newJunctions.Add(junction2);
            existingPositions.Add(osmPos);

            var typeDesc = isMidSplineCrossing ? "MidSplineCrossing" : junction2.Type.ToString();
            TerrainCreationLogger.Current?.Detail(
                $"Created {typeDesc} from OSM {osmJunction.DisplayName} ({osmJunction.Type}) " +
                $"with {sectionsBySpline.Count} contributing roads ({continuousCount} continuous)");
        }

        TerrainLogger.SuppressDetailedLogging = false;
        return newJunctions;
    }

    /// <summary>
    ///     Updates junction types based on OSM semantic information.
    ///     This can refine generic junction types (T, Y, X) with more specific
    ///     information from OSM tags (motorway_junction, traffic_signals, etc.).
    ///     OSM CrossRoads type (from way_cnt:2-) indicates where 2+ roads share a node.
    ///     These are converted to MidSplineCrossing type so the CrossroadToTJunctionConverter
    ///     can properly split them into T-junctions.
    /// </summary>
    private void UpdateJunctionTypesFromOsm(List<NetworkJunction> junctions)
    {
        foreach (var junction in junctions.Where(j => j.OsmHint != null))
        {
            var osmType = junction.OsmHint!.Type;

            // Mini-roundabouts should be marked as roundabout type
            if (osmType == OsmJunctionType.MiniRoundabout)
            {
                junction.Type = JunctionType.Roundabout;
            }
            // Complex OSM junctions (5+ ways) should be marked as complex
            else if (osmType == OsmJunctionType.ComplexJunction)
            {
                junction.Type = JunctionType.Complex;
            }
            // OSM CrossRoads (2 ways sharing a node) should be MidSplineCrossing
            // This allows the CrossroadToTJunctionConverter to process them
            else if (osmType == OsmJunctionType.CrossRoads &&
                     junction.Type != JunctionType.TJunction && // Don't override if already T-junction
                     junction.Type != JunctionType.Roundabout) // Don't override roundabouts
            {
                // Only mark as MidSplineCrossing if this junction has 2+ continuous contributors
                // (roads that pass through without terminating)
                var continuousCount = junction.Contributors.Count(c => c.IsContinuous);
                if (continuousCount >= 2)
                {
                    junction.Type = JunctionType.MidSplineCrossing;
                    TerrainCreationLogger.Current?.Detail(
                        $"Junction #{junction.JunctionId}: OSM CrossRoads hint converted to MidSplineCrossing " +
                        $"({continuousCount} continuous roads)");
                }
            }
            // Motorway junctions are typically T or Y junctions (ramp connections)
            // Keep the geometric type but the OSM hint provides naming info
        }
    }

    /// <summary>
    ///     Maps OSM junction type to the appropriate JunctionType enum value.
    ///     Mapping rules:
    ///     - Explicit geometric types (TJunction, CrossRoads, ComplexJunction) map directly
    ///     - Roundabout types (MiniRoundabout) map to Roundabout
    ///     - Motorway junctions (ramp connections) map to TJunction (most common) or YJunction
    ///     - Traffic control features (TrafficSignals, Stop, GiveWay) use road count geometry
    ///     - Pedestrian crossings are treated as Endpoint (mid-road feature, not a junction)
    ///     - TurningCircle is an Endpoint (road termination)
    ///     - Unknown types fall back to road count heuristics
    /// </summary>
    /// <param name="osmType">The OSM junction type.</param>
    /// <param name="roadCount">Number of roads meeting at the junction.</param>
    /// <returns>The corresponding JunctionType.</returns>
    private static JunctionType MapOsmTypeToJunctionType(OsmJunctionType osmType, int roadCount)
    {
        // First check explicit OSM types that have direct mappings
        return osmType switch
        {
            // Roundabout types
            OsmJunctionType.MiniRoundabout => JunctionType.Roundabout,

            // Endpoint types (road terminations or mid-road features)
            OsmJunctionType.TurningCircle => JunctionType.Endpoint,
            OsmJunctionType.Crossing => JunctionType.Endpoint, // Pedestrian crossing - mid-road feature

            // Geometric junction types from OSM way_cnt analysis
            OsmJunctionType.ComplexJunction => JunctionType.Complex,
            OsmJunctionType.CrossRoads => JunctionType.CrossRoads,
            OsmJunctionType.TJunction => JunctionType.TJunction,

            // Motorway junctions are typically T or Y shaped (ramp connections)
            // Use road count to determine: 2 roads = Y, 3+ roads = T
            OsmJunctionType.MotorwayJunction => roadCount <= 2 ? JunctionType.YJunction : JunctionType.TJunction,

            // Traffic control features - use road count for geometry classification
            // These tell us about traffic rules, not junction shape
            OsmJunctionType.TrafficSignals => MapRoadCountToJunctionType(roadCount),
            OsmJunctionType.Stop => MapRoadCountToJunctionType(roadCount),
            OsmJunctionType.GiveWay => MapRoadCountToJunctionType(roadCount),

            // Unknown - fall back to road count heuristics
            OsmJunctionType.Unknown => MapRoadCountToJunctionType(roadCount),

            // Default fallback for any new types added later
            _ => MapRoadCountToJunctionType(roadCount)
        };
    }

    /// <summary>
    ///     Maps road count to junction type using geometric heuristics.
    /// </summary>
    private static JunctionType MapRoadCountToJunctionType(int roadCount)
    {
        return roadCount switch
        {
            1 => JunctionType.Endpoint,
            2 => JunctionType.YJunction,
            3 => JunctionType.TJunction,
            4 => JunctionType.CrossRoads,
            _ => JunctionType.Complex
        };
    }

    /// <summary>
    ///     Finds all spline endpoints (first and last cross-sections of each spline).
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
                if (splineSections.Count > 1) endpoints.Add(splineSections[^1]);
            }
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
    ///     Complexity: O(E × ?(E)) where E = number of endpoints and ? is the inverse
    ///     Ackermann function (effectively constant). This replaces the previous O(E² × C)
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
                // Single endpoint, no connection to other roads
                junction.Type = JunctionType.Endpoint;
            else if (junction.Contributors.Any(c => c.IsContinuous))
                // At least one contributor passes through (not an endpoint) = T-junction
                junction.Type = JunctionType.TJunction;
            else
                // All contributors are endpoints
                junction.Type = uniqueSplineIds switch
                {
                    2 => JunctionType.YJunction,
                    3 or 4 => JunctionType.CrossRoads,
                    _ => JunctionType.Complex
                };
        }
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

                // Find cross-sections from OTHER splines that are very close
                var nearbySections = QuerySpatialIndex(spatialIndex, cs.CenterPoint, crossingDetectionRadius)
                    .Where(other =>
                        other.OwnerSplineId != spline.SplineId && // Different spline
                        !other.IsSplineStart && !other.IsSplineEnd) // Also mid-spline
                    .ToList();

                if (nearbySections.Count == 0)
                    continue;

                candidateCrossingsFound++;

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

        TerrainCreationLogger.Current?.Detail(
            $"DetectMidSplineCrossings summary: Checked {totalMidSplineSectionsChecked} mid-spline sections, " +
            $"skipped {skippedAlreadyConnected} pairs already connected at junctions, " +
            $"found {candidateCrossingsFound} candidate locations, " +
            $"created {crossings.Count} crossing junctions");

        return crossings;
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