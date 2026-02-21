using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Resolves short cycleway/footway stubs at roundabout entries and exits.
///
/// Problem: OSM mappers often model separate entry/exit lanes at roundabouts as short
/// highway=cycleway or highway=footway stubs. These stubs:
///   - Bridge the gap between the main road endpoint ("divergence node") and the roundabout ring
///   - Are typically 20-50m long, one-way, in pairs (entry + exit)
///   - Are NOT connected to the road network for smoothing (different highway type group)
///   - Without them, the main road doesn't reach the roundabout — leaving a gap
///
/// Solution: Extend the parent road to connect directly to the roundabout ring at the
/// midpoint of the stub connection points, then remove the stubs.
/// </summary>
public class RoundaboutCyclewayStubResolver
{
    /// <summary>
    /// Maximum length in meters for a feature to be considered a stub.
    /// </summary>
    public double MaxStubLengthMeters { get; set; } = 100.0;

    /// <summary>
    /// Highway types that are considered potential stubs at roundabout entries/exits.
    /// </summary>
    private static readonly HashSet<string> StubHighwayTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cycleway", "footway", "path"
    };

    /// <summary>
    /// Highway types that can NOT be parent roads (they are stubs themselves).
    /// </summary>
    private static readonly HashSet<string> NonParentHighwayTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "cycleway", "footway", "path", "steps", "bridleway", "pedestrian"
    };

    /// <summary>
    /// Tolerance for coordinate matching (approximately 0.1 meters at equator).
    /// </summary>
    private const double CoordinateToleranceDegrees = 0.000001;

    public class ResolveResult
    {
        /// <summary>
        /// Feature IDs of cycleway stubs that should be removed from processing.
        /// </summary>
        public HashSet<long> StubFeatureIdsToRemove { get; set; } = new();

        /// <summary>
        /// Number of parent roads that were extended to the roundabout ring.
        /// </summary>
        public int RoadsExtended { get; set; }

        /// <summary>
        /// Number of stub groups processed (each group = stubs sharing a divergence node).
        /// </summary>
        public int StubGroupsResolved { get; set; }
    }

    /// <summary>
    /// Resolves cycleway stubs at roundabout entries/exits by extending parent roads
    /// to the roundabout ring and marking stubs for removal.
    /// </summary>
    /// <param name="roundabouts">Detected roundabouts.</param>
    /// <param name="allFeatures">All OSM highway features (will be modified in place for extended roads).</param>
    /// <returns>Result containing feature IDs to remove and statistics.</returns>
    public ResolveResult ResolveStubs(
        List<OsmRoundabout> roundabouts,
        List<OsmFeature> allFeatures)
    {
        var result = new ResolveResult();

        if (roundabouts.Count == 0)
            return result;

        // Build endpoint node map: NodeId → list of features that have this node at start or end
        var endpointNodeMap = BuildEndpointNodeMap(allFeatures);

        foreach (var roundabout in roundabouts)
        {
            ResolveStubsForRoundabout(roundabout, allFeatures, endpointNodeMap, result);
        }

        if (result.StubGroupsResolved > 0)
        {
            TerrainLogger.Info($"RoundaboutCyclewayStubResolver: Resolved {result.StubGroupsResolved} stub group(s), " +
                $"extended {result.RoadsExtended} parent road(s), " +
                $"removed {result.StubFeatureIdsToRemove.Count} stub(s)");
        }

        return result;
    }

    /// <summary>
    /// Builds a map from OSM node ID to features that have that node at their start or end.
    /// </summary>
    private static Dictionary<long, List<(OsmFeature Feature, bool IsStart)>> BuildEndpointNodeMap(
        List<OsmFeature> features)
    {
        var map = new Dictionary<long, List<(OsmFeature, bool)>>();

        foreach (var feature in features)
        {
            if (feature.GeometryType != OsmGeometryType.LineString)
                continue;
            if (feature.Category != "highway")
                continue;
            if (feature.NodeIds.Count < 2)
                continue;

            var startNodeId = feature.NodeIds[0];
            var endNodeId = feature.NodeIds[^1];

            if (!map.TryGetValue(startNodeId, out var startList))
            {
                startList = new List<(OsmFeature, bool)>();
                map[startNodeId] = startList;
            }
            startList.Add((feature, true));

            if (!map.TryGetValue(endNodeId, out var endList))
            {
                endList = new List<(OsmFeature, bool)>();
                map[endNodeId] = endList;
            }
            endList.Add((feature, false));
        }

        return map;
    }

    /// <summary>
    /// Resolves cycleway stubs for a single roundabout.
    /// </summary>
    private void ResolveStubsForRoundabout(
        OsmRoundabout roundabout,
        List<OsmFeature> allFeatures,
        Dictionary<long, List<(OsmFeature Feature, bool IsStart)>> endpointNodeMap,
        ResolveResult result)
    {
        // Build set of ring node IDs for fast lookup
        var ringNodeIds = new HashSet<long>();
        foreach (var origFeature in roundabout.OriginalFeatures)
        {
            foreach (var nodeId in origFeature.NodeIds)
            {
                ringNodeIds.Add(nodeId);
            }
        }

        if (ringNodeIds.Count == 0)
            return;

        // Find stub features: highway=cycleway/footway/path that connect to this roundabout ring
        var stubs = FindStubFeatures(roundabout, allFeatures, ringNodeIds);

        if (stubs.Count == 0)
            return;

        TerrainLogger.Detail($"  Roundabout {roundabout.Id}: Found {stubs.Count} cycleway/footway stub(s)");

        // Group stubs by their divergence node (the non-ring endpoint)
        var stubGroups = GroupStubsByDivergenceNode(stubs, ringNodeIds);

        foreach (var (divergenceNodeId, groupStubs) in stubGroups)
        {
            // Find the parent road (two-way road that ends/starts at the divergence node)
            var parentRoad = FindParentRoad(divergenceNodeId, groupStubs, endpointNodeMap);

            if (parentRoad == null)
            {
                TerrainLogger.Detail($"  Roundabout {roundabout.Id}: No parent road found at divergence node {divergenceNodeId} " +
                    $"for {groupStubs.Count} stub(s) — skipping");
                continue;
            }

            // Calculate the ring connection point (midpoint of stub connections on the arc)
            var ringConnectionPoint = CalculateRingConnectionPoint(roundabout, groupStubs, ringNodeIds);

            if (ringConnectionPoint == null)
            {
                TerrainLogger.Detail($"  Roundabout {roundabout.Id}: Could not calculate ring connection point — skipping");
                continue;
            }

            // Extend the parent road to the ring connection point
            ExtendParentRoad(parentRoad.Value.Feature, parentRoad.Value.IsStart,
                ringConnectionPoint.Value.Coordinate, ringConnectionPoint.Value.NodeId);

            result.RoadsExtended++;

            // Mark all stubs in this group for removal
            foreach (var stub in groupStubs)
            {
                result.StubFeatureIdsToRemove.Add(stub.Feature.Id);
            }

            // Update roundabout connections
            UpdateRoundaboutConnections(roundabout, groupStubs, parentRoad.Value.Feature,
                ringConnectionPoint.Value.Coordinate);

            result.StubGroupsResolved++;

            TerrainLogger.Detail($"  Roundabout {roundabout.Id}: Extended road {parentRoad.Value.Feature.Id} " +
                $"({parentRoad.Value.Feature.DisplayName}) to ring, removed {groupStubs.Count} stub(s) " +
                $"at divergence node {divergenceNodeId}");
        }
    }

    /// <summary>
    /// Finds stub features that connect to a roundabout ring.
    /// A stub is: highway=cycleway/footway/path, short length, has an endpoint on the ring.
    /// </summary>
    private List<StubInfo> FindStubFeatures(
        OsmRoundabout roundabout,
        List<OsmFeature> allFeatures,
        HashSet<long> ringNodeIds)
    {
        var stubs = new List<StubInfo>();

        foreach (var feature in allFeatures)
        {
            if (feature.GeometryType != OsmGeometryType.LineString)
                continue;
            if (feature.IsRoundabout)
                continue;
            if (feature.NodeIds.Count < 2)
                continue;

            // Check if this is a stub highway type
            if (!feature.Tags.TryGetValue("highway", out var highway))
                continue;
            if (!StubHighwayTypes.Contains(highway))
                continue;

            // Check length
            double lengthMeters = CalculateFeatureLengthMeters(feature);
            if (lengthMeters > MaxStubLengthMeters)
                continue;

            // Check if start or end is on the ring
            var startNodeId = feature.NodeIds[0];
            var endNodeId = feature.NodeIds[^1];
            bool startOnRing = ringNodeIds.Contains(startNodeId);
            bool endOnRing = ringNodeIds.Contains(endNodeId);

            if (startOnRing && !endOnRing)
            {
                stubs.Add(new StubInfo
                {
                    Feature = feature,
                    RingNodeId = startNodeId,
                    DivergenceNodeId = endNodeId,
                    RingCoordinate = feature.Coordinates[0],
                    DivergenceCoordinate = feature.Coordinates[^1]
                });
            }
            else if (endOnRing && !startOnRing)
            {
                stubs.Add(new StubInfo
                {
                    Feature = feature,
                    RingNodeId = endNodeId,
                    DivergenceNodeId = startNodeId,
                    RingCoordinate = feature.Coordinates[^1],
                    DivergenceCoordinate = feature.Coordinates[0]
                });
            }
            // If both on ring, it's a segment that runs along the ring — handled by trimmer, not us
        }

        return stubs;
    }

    /// <summary>
    /// Groups stubs by their divergence node (the non-ring endpoint shared with a parent road).
    /// </summary>
    private static Dictionary<long, List<StubInfo>> GroupStubsByDivergenceNode(
        List<StubInfo> stubs, HashSet<long> ringNodeIds)
    {
        var groups = new Dictionary<long, List<StubInfo>>();

        foreach (var stub in stubs)
        {
            if (!groups.TryGetValue(stub.DivergenceNodeId, out var list))
            {
                list = new List<StubInfo>();
                groups[stub.DivergenceNodeId] = list;
            }
            list.Add(stub);
        }

        return groups;
    }

    /// <summary>
    /// Finds the parent road at a divergence node.
    /// The parent road must: terminate at the divergence node, not be a stub-type highway,
    /// not be a roundabout.
    /// If multiple candidates exist, picks the highest highway priority.
    /// </summary>
    private static (OsmFeature Feature, bool IsStart)? FindParentRoad(
        long divergenceNodeId,
        List<StubInfo> stubs,
        Dictionary<long, List<(OsmFeature Feature, bool IsStart)>> endpointNodeMap)
    {
        if (!endpointNodeMap.TryGetValue(divergenceNodeId, out var candidates))
            return null;

        var stubIds = stubs.Select(s => s.Feature.Id).ToHashSet();

        (OsmFeature Feature, bool IsStart)? bestCandidate = null;
        int bestPriority = -1;

        foreach (var (feature, isStart) in candidates)
        {
            // Skip stubs themselves
            if (stubIds.Contains(feature.Id))
                continue;

            // Skip roundabout ways
            if (feature.IsRoundabout)
                continue;

            // Skip non-highway or stub-type highways
            if (!feature.Tags.TryGetValue("highway", out var highway))
                continue;
            if (NonParentHighwayTypes.Contains(highway))
                continue;

            int priority = GetHighwayPriority(highway);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestCandidate = (feature, isStart);
            }
        }

        return bestCandidate;
    }

    /// <summary>
    /// Calculates the ring connection point — the midpoint on the ring arc between
    /// the stub connection points. For a single stub, uses its ring connection point directly.
    /// </summary>
    private (GeoCoordinate Coordinate, long NodeId)? CalculateRingConnectionPoint(
        OsmRoundabout roundabout,
        List<StubInfo> stubs,
        HashSet<long> ringNodeIds)
    {
        if (stubs.Count == 0)
            return null;

        if (stubs.Count == 1)
        {
            // Single stub: use its ring connection point directly
            var stub = stubs[0];
            return (stub.RingCoordinate, stub.RingNodeId);
        }

        // Multiple stubs: find the midpoint angle on the ring
        var angles = new List<double>();
        foreach (var stub in stubs)
        {
            double angle = CalculateAngleFromCenter(roundabout.Center, stub.RingCoordinate);
            angles.Add(angle);
        }

        // Calculate the angular midpoint (handles wraparound at 0°/360°)
        double midAngle = CalculateAngularMidpoint(angles);

        // Find the closest ring coordinate to that angle
        return FindClosestRingCoordinateByAngle(roundabout, midAngle);
    }

    /// <summary>
    /// Extends the parent road to reach the roundabout ring by adding the ring connection
    /// point to the road's coordinate list.
    /// </summary>
    private static bool ExtendParentRoad(
        OsmFeature parentRoad,
        bool divergenceIsStart,
        GeoCoordinate ringPoint,
        long ringNodeId)
    {
        if (divergenceIsStart)
        {
            // Divergence node is at the start — prepend ring point
            parentRoad.Coordinates.Insert(0, ringPoint);
            if (parentRoad.NodeIds.Count > 0)
            {
                parentRoad.NodeIds.Insert(0, ringNodeId);
            }
        }
        else
        {
            // Divergence node is at the end — append ring point
            parentRoad.Coordinates.Add(ringPoint);
            if (parentRoad.NodeIds.Count > 0)
            {
                parentRoad.NodeIds.Add(ringNodeId);
            }
        }

        return true;
    }

    /// <summary>
    /// Updates the roundabout's connection list: removes cycleway stub connections and
    /// adds the parent road as a connection at the ring midpoint.
    /// </summary>
    private static void UpdateRoundaboutConnections(
        OsmRoundabout roundabout,
        List<StubInfo> stubs,
        OsmFeature parentRoad,
        GeoCoordinate ringConnectionPoint)
    {
        // Remove stub connections
        var stubIds = stubs.Select(s => s.Feature.Id).ToHashSet();
        roundabout.Connections.RemoveAll(c => stubIds.Contains(c.ConnectingWayId));

        // Add parent road connection if not already present
        if (!roundabout.Connections.Any(c => c.ConnectingWayId == parentRoad.Id))
        {
            int ringIndex = FindClosestRingIndex(roundabout, ringConnectionPoint);
            roundabout.Connections.Add(new RoundaboutConnection
            {
                ConnectingWayId = parentRoad.Id,
                ConnectionPoint = ringConnectionPoint,
                RingCoordinateIndex = ringIndex,
                AngleDegrees = CalculateAngleFromCenter(roundabout.Center, ringConnectionPoint),
                Direction = RoundaboutConnectionDirection.Bidirectional,
                ConnectingRoad = parentRoad,
                ConnectingRoadCoordinateIndex = 0 // Will be recalculated by trimmer
            });
        }
    }

    // ========================================================================================
    //  Geometry Helpers
    // ========================================================================================

    /// <summary>
    /// Calculates the approximate length of a feature in meters.
    /// </summary>
    private static double CalculateFeatureLengthMeters(OsmFeature feature)
    {
        double totalLength = 0;
        for (int i = 1; i < feature.Coordinates.Count; i++)
        {
            totalLength += DistanceMeters(feature.Coordinates[i - 1], feature.Coordinates[i]);
        }
        return totalLength;
    }

    /// <summary>
    /// Approximate distance between two geo coordinates in meters.
    /// </summary>
    private static double DistanceMeters(GeoCoordinate a, GeoCoordinate b)
    {
        double latRadians = (a.Latitude + b.Latitude) / 2.0 * Math.PI / 180.0;
        double metersPerDegreeLon = 111320.0 * Math.Cos(latRadians);
        double metersPerDegreeLat = 110574.0;

        double dx = (b.Longitude - a.Longitude) * metersPerDegreeLon;
        double dy = (b.Latitude - a.Latitude) * metersPerDegreeLat;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Calculates angle from center to a point (0 = East, 90 = North, CCW).
    /// </summary>
    private static double CalculateAngleFromCenter(GeoCoordinate center, GeoCoordinate point)
    {
        double dx = point.Longitude - center.Longitude;
        double dy = point.Latitude - center.Latitude;
        double angleRadians = Math.Atan2(dy, dx);
        double angleDegrees = angleRadians * 180.0 / Math.PI;
        if (angleDegrees < 0) angleDegrees += 360.0;
        return angleDegrees;
    }

    /// <summary>
    /// Calculates the angular midpoint of a set of angles (handles wraparound at 0°/360°).
    /// Uses vector averaging to handle the circular mean correctly.
    /// </summary>
    private static double CalculateAngularMidpoint(List<double> anglesDegrees)
    {
        double sumX = 0, sumY = 0;
        foreach (var angle in anglesDegrees)
        {
            double rad = angle * Math.PI / 180.0;
            sumX += Math.Cos(rad);
            sumY += Math.Sin(rad);
        }
        double midRad = Math.Atan2(sumY / anglesDegrees.Count, sumX / anglesDegrees.Count);
        double midDeg = midRad * 180.0 / Math.PI;
        if (midDeg < 0) midDeg += 360.0;
        return midDeg;
    }

    /// <summary>
    /// Finds the closest ring coordinate by angle from center, returning both the
    /// coordinate and its OSM node ID.
    /// </summary>
    private (GeoCoordinate Coordinate, long NodeId)? FindClosestRingCoordinateByAngle(
        OsmRoundabout roundabout,
        double targetAngleDegrees)
    {
        if (roundabout.RingCoordinates.Count == 0)
            return null;

        // We need node IDs from the original features. Build a coordinate-to-nodeId map.
        var coordToNodeId = BuildRingCoordinateNodeIdMap(roundabout);

        int bestIndex = -1;
        double bestAngleDiff = double.MaxValue;

        for (int i = 0; i < roundabout.RingCoordinates.Count; i++)
        {
            var coord = roundabout.RingCoordinates[i];
            double angle = CalculateAngleFromCenter(roundabout.Center, coord);
            double diff = Math.Abs(AngleDifference(angle, targetAngleDegrees));

            if (diff < bestAngleDiff)
            {
                bestAngleDiff = diff;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
            return null;

        var bestCoord = roundabout.RingCoordinates[bestIndex];

        // Look up the node ID for this coordinate
        var roundedKey = RoundCoordinate(bestCoord);
        if (coordToNodeId.TryGetValue(roundedKey, out var nodeId))
        {
            return (bestCoord, nodeId);
        }

        // Fallback: use -1 as synthetic node ID (shouldn't happen for valid roundabouts)
        TerrainLogger.Warning($"RoundaboutCyclewayStubResolver: Could not find node ID for ring coordinate " +
            $"at index {bestIndex} of roundabout {roundabout.Id}");
        return (bestCoord, -1);
    }

    /// <summary>
    /// Builds a map from rounded ring coordinates to OSM node IDs using the original features.
    /// </summary>
    private static Dictionary<(double, double), long> BuildRingCoordinateNodeIdMap(OsmRoundabout roundabout)
    {
        var map = new Dictionary<(double, double), long>();

        foreach (var feature in roundabout.OriginalFeatures)
        {
            for (int i = 0; i < feature.Coordinates.Count && i < feature.NodeIds.Count; i++)
            {
                var key = RoundCoordinate(feature.Coordinates[i]);
                map.TryAdd(key, feature.NodeIds[i]);
            }
        }

        return map;
    }

    /// <summary>
    /// Calculates the smallest signed angle difference between two angles in degrees.
    /// Result is in range [-180, 180].
    /// </summary>
    private static double AngleDifference(double angle1, double angle2)
    {
        double diff = angle1 - angle2;
        while (diff > 180) diff -= 360;
        while (diff < -180) diff += 360;
        return diff;
    }

    private static (double lon, double lat) RoundCoordinate(GeoCoordinate coord)
    {
        return (
            Math.Round(coord.Longitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees,
            Math.Round(coord.Latitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees
        );
    }

    private static int FindClosestRingIndex(OsmRoundabout roundabout, GeoCoordinate point)
    {
        int closestIndex = 0;
        double closestDistSq = double.MaxValue;

        for (int i = 0; i < roundabout.RingCoordinates.Count; i++)
        {
            var ringCoord = roundabout.RingCoordinates[i];
            double dx = point.Longitude - ringCoord.Longitude;
            double dy = point.Latitude - ringCoord.Latitude;
            double distSq = dx * dx + dy * dy;

            if (distSq < closestDistSq)
            {
                closestDistSq = distSq;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    /// <summary>
    /// Highway priority for choosing the best parent road when multiple candidates exist.
    /// Higher = more important road.
    /// </summary>
    private static int GetHighwayPriority(string highway) => highway.ToLowerInvariant() switch
    {
        "motorway" => 100,
        "motorway_link" => 95,
        "trunk" => 90,
        "trunk_link" => 85,
        "primary" => 80,
        "primary_link" => 78,
        "secondary" => 75,
        "secondary_link" => 73,
        "tertiary" => 60,
        "tertiary_link" => 58,
        "residential" => 55,
        "unclassified" => 50,
        "service" => 45,
        "living_street" => 40,
        "track" => 30,
        _ => 35
    };

    // ========================================================================================
    //  Internal Types
    // ========================================================================================

    private class StubInfo
    {
        /// <summary>The cycleway/footway stub feature.</summary>
        public OsmFeature Feature { get; set; } = null!;

        /// <summary>OSM node ID of the endpoint ON the roundabout ring.</summary>
        public long RingNodeId { get; set; }

        /// <summary>OSM node ID of the endpoint NOT on the ring (shared with parent road).</summary>
        public long DivergenceNodeId { get; set; }

        /// <summary>The coordinate on the roundabout ring.</summary>
        public GeoCoordinate RingCoordinate { get; set; } = null!;

        /// <summary>The coordinate at the divergence point (shared with parent road).</summary>
        public GeoCoordinate DivergenceCoordinate { get; set; } = null!;
    }
}
