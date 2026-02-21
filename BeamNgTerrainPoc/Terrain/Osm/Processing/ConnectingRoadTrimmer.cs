using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Trims connecting roads that overlap with roundabout rings.
/// 
/// Problem: OSM roads often share multiple nodes with roundabouts, creating:
/// - High-angle turns where the road follows the circular path
/// - Weird elevation changes
/// - Quirky spline geometry with bumps and jumps
/// 
/// Solution: Cut roads at the FIRST point where they touch the roundabout
/// and delete the portion that overlaps with/follows the ring.
/// </summary>
public class ConnectingRoadTrimmer
{
    /// <summary>
    /// Tolerance for coordinate matching (approximately 0.1 meters at equator).
    /// </summary>
    private const double CoordinateToleranceDegrees = 0.000001;
    
    /// <summary>
    /// Default tolerance for determining if a road point is "on" the roundabout ring (in meters).
    /// Points within this distance of the ring radius are considered overlapping.
    /// </summary>
    private const double DefaultOverlapToleranceMeters = 2.0;
    
    /// <summary>
    /// Result of trimming a road.
    /// </summary>
    public class TrimResult
    {
        /// <summary>
        /// The trimmed road coordinates (portion OUTSIDE the roundabout).
        /// Null if the entire road was inside the roundabout.
        /// </summary>
        public List<GeoCoordinate>? TrimmedCoordinates { get; set; }
        
        /// <summary>
        /// The point where the road was cut (on the roundabout ring).
        /// </summary>
        public GeoCoordinate? CutPoint { get; set; }
        
        /// <summary>
        /// Number of coordinates removed from the road.
        /// </summary>
        public int CoordinatesRemoved { get; set; }
        
        /// <summary>
        /// Whether any trimming was performed.
        /// </summary>
        public bool WasTrimmed => CoordinatesRemoved > 0;
        
        /// <summary>
        /// Whether the road was entirely inside the roundabout and should be deleted.
        /// </summary>
        public bool ShouldDeleteEntireRoad => TrimmedCoordinates == null || TrimmedCoordinates.Count < 2;

        /// <summary>
        /// Start index in the original Coordinates array where the kept segment begins.
        /// Used to synchronize NodeIds trimming with Coordinates trimming.
        /// </summary>
        public int KeptStartIndex { get; set; }

        /// <summary>
        /// Number of coordinates kept from the original array starting at KeptStartIndex.
        /// Used to synchronize NodeIds trimming with Coordinates trimming.
        /// </summary>
        public int KeptCount { get; set; }
    }
    
    /// <summary>
    /// Statistics from the trimming operation.
    /// </summary>
    public class TrimStatistics
    {
        /// <summary>
        /// Number of roads that were trimmed (but not deleted).
        /// </summary>
        public int RoadsTrimmed { get; set; }
        
        /// <summary>
        /// Number of roads entirely deleted (fully inside roundabout).
        /// </summary>
        public int RoadsDeleted { get; set; }
        
        /// <summary>
        /// Total number of coordinates removed across all roads.
        /// </summary>
        public int TotalCoordinatesRemoved { get; set; }
        
        /// <summary>
        /// Number of roads that were not affected (no overlap with any roundabout).
        /// </summary>
        public int RoadsUnaffected { get; set; }
    }
    
    /// <summary>
    /// Tolerance for determining if a road point is "on" the roundabout ring (in meters).
    /// </summary>
    public double OverlapToleranceMeters { get; set; } = DefaultOverlapToleranceMeters;
    
    /// <summary>
    /// Trims all connecting roads for all detected roundabouts.
    /// Modifies the OsmFeature.Coordinates in place for roads that need trimming.
    /// Returns features that should be completely removed (entirely inside roundabout).
    /// </summary>
    /// <param name="roundabouts">Detected roundabouts with their ring coordinates.</param>
    /// <param name="allFeatures">All OSM line features (will be modified in place).</param>
    /// <returns>Set of feature IDs that should be completely removed.</returns>
    public HashSet<long> TrimConnectingRoads(
        List<OsmRoundabout> roundabouts,
        List<OsmFeature> allFeatures)
    {
        return TrimConnectingRoads(roundabouts, allFeatures, out _);
    }
    
    /// <summary>
    /// Trims all connecting roads for all detected roundabouts.
    /// Modifies the OsmFeature.Coordinates in place for roads that need trimming.
    /// Returns features that should be completely removed (entirely inside roundabout).
    /// </summary>
    /// <param name="roundabouts">Detected roundabouts with their ring coordinates.</param>
    /// <param name="allFeatures">All OSM line features (will be modified in place).</param>
    /// <param name="statistics">Output statistics about the trimming operation.</param>
    /// <returns>Set of feature IDs that should be completely removed.</returns>
    public HashSet<long> TrimConnectingRoads(
        List<OsmRoundabout> roundabouts,
        List<OsmFeature> allFeatures,
        out TrimStatistics statistics)
    {
        var featuresToRemove = new HashSet<long>();
        statistics = new TrimStatistics();
        
        if (roundabouts.Count == 0)
        {
            statistics.RoadsUnaffected = allFeatures.Count;
            return featuresToRemove;
        }
        
        TerrainLogger.Info($"ConnectingRoadTrimmer: Processing {allFeatures.Count} features against {roundabouts.Count} roundabout(s)");
        
        // Build set of all roundabout ring coordinates for fast lookup
        var roundaboutRingPoints = new Dictionary<long, HashSet<(double lon, double lat)>>();
        foreach (var roundabout in roundabouts)
        {
            var ringPointSet = new HashSet<(double lon, double lat)>();
            foreach (var coord in roundabout.RingCoordinates)
            {
                // Round to tolerance for matching
                ringPointSet.Add(RoundCoordinate(coord));
            }
            roundaboutRingPoints[roundabout.Id] = ringPointSet;
        }
        
        // Process each non-roundabout road
        foreach (var feature in allFeatures)
        {
            // Skip roundabout ways themselves
            if (feature.IsRoundabout)
                continue;
            
            // Skip non-highway features
            if (feature.Category != "highway")
                continue;
            
            if (feature.GeometryType != OsmGeometryType.LineString)
                continue;
            
            bool wasTrimmed = false;
            
            // Check against each roundabout
            foreach (var roundabout in roundabouts)
            {
                var result = TrimRoadAgainstRoundabout(feature, roundabout, roundaboutRingPoints[roundabout.Id]);
                
                if (result.ShouldDeleteEntireRoad)
                {
                    featuresToRemove.Add(feature.Id);
                    statistics.RoadsDeleted++;
                    statistics.TotalCoordinatesRemoved += feature.Coordinates.Count;
                    TerrainLogger.Detail($"  Road {feature.Id} ({feature.DisplayName}) entirely inside roundabout - marking for deletion");
                    wasTrimmed = true;
                    break; // No need to check other roundabouts
                }
                else if (result.WasTrimmed)
                {
                    // Update the feature's coordinates with the trimmed version
                    feature.Coordinates = result.TrimmedCoordinates!;

                    // Synchronize NodeIds to stay parallel with Coordinates
                    if (feature.NodeIds.Count > 0 && result.KeptCount > 0)
                    {
                        feature.NodeIds = feature.NodeIds
                            .Skip(result.KeptStartIndex)
                            .Take(result.KeptCount)
                            .ToList();
                    }

                    statistics.RoadsTrimmed++;
                    statistics.TotalCoordinatesRemoved += result.CoordinatesRemoved;
                    
                    TerrainLogger.Detail($"  Trimmed road {feature.Id} ({feature.DisplayName}): removed {result.CoordinatesRemoved} overlapping point(s)");
                    
                    // Update the connection info on the roundabout
                    if (result.CutPoint != null)
                    {
                        UpdateConnectionPoint(roundabout, feature, result.CutPoint);
                    }
                    
                    wasTrimmed = true;
                    // Continue checking other roundabouts in case road intersects multiple
                }
            }
            
            if (!wasTrimmed && !featuresToRemove.Contains(feature.Id))
            {
                statistics.RoadsUnaffected++;
            }
        }
        
        TerrainLogger.Info($"ConnectingRoadTrimmer: Trimmed {statistics.RoadsTrimmed} road(s), " +
            $"removed {statistics.TotalCoordinatesRemoved} overlapping coord(s), " +
            $"{statistics.RoadsDeleted} road(s) entirely deleted, " +
            $"{statistics.RoadsUnaffected} road(s) unaffected");
        
        return featuresToRemove;
    }
    
    /// <summary>
    /// Rounds a coordinate to the tolerance for matching.
    /// </summary>
    private static (double lon, double lat) RoundCoordinate(GeoCoordinate coord)
    {
        return (
            Math.Round(coord.Longitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees,
            Math.Round(coord.Latitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees
        );
    }
    
    /// <summary>
    /// Trims a single road against a single roundabout.
    /// </summary>
    private TrimResult TrimRoadAgainstRoundabout(
        OsmFeature road,
        OsmRoundabout roundabout,
        HashSet<(double lon, double lat)> ringPointSet)
    {
        var coords = road.Coordinates;
        if (coords.Count < 2)
            return new TrimResult { TrimmedCoordinates = null };
        
        // Find which coordinates are ON the roundabout ring
        var onRingMask = new bool[coords.Count];
        int onRingCount = 0;
        
        for (int i = 0; i < coords.Count; i++)
        {
            var rounded = RoundCoordinate(coords[i]);
            
            if (ringPointSet.Contains(rounded) || IsWithinRadiusTolerance(coords[i], roundabout))
            {
                onRingMask[i] = true;
                onRingCount++;
            }
        }
        
        // No overlap with this roundabout
        if (onRingCount == 0)
            return new TrimResult { TrimmedCoordinates = coords.ToList(), KeptStartIndex = 0, KeptCount = coords.Count };
        
        // Entire road is on the roundabout (should be deleted)
        if (onRingCount == coords.Count)
            return new TrimResult { TrimmedCoordinates = null, CoordinatesRemoved = coords.Count };
        
        // Find the segments to keep:
        // We want to keep the portion(s) that are OUTSIDE the roundabout
        // and cut at the FIRST point where the road enters the roundabout
        return FindBestTrimStrategy(coords, onRingMask, roundabout);
    }
    
    /// <summary>
    /// Determines the best trimming strategy for a road that intersects a roundabout.
    /// </summary>
    private TrimResult FindBestTrimStrategy(
        List<GeoCoordinate> coords,
        bool[] onRingMask,
        OsmRoundabout roundabout)
    {
        // Find transitions between on-ring and off-ring
        var transitions = new List<(int index, bool enteringRing)>();
        
        for (int i = 1; i < coords.Count; i++)
        {
            if (!onRingMask[i - 1] && onRingMask[i])
            {
                // Entering the ring
                transitions.Add((i, true));
            }
            else if (onRingMask[i - 1] && !onRingMask[i])
            {
                // Exiting the ring
                transitions.Add((i - 1, false)); // Last point on ring
            }
        }
        
        // Check edge cases: road starts or ends on ring
        bool startsOnRing = onRingMask[0];
        bool endsOnRing = onRingMask[^1];
        
        // Simple case: road enters ring and doesn't exit (common case)
        // Keep from start to entry point
        if (transitions.Count == 1 && transitions[0].enteringRing)
        {
            var entryIndex = transitions[0].index;
            var trimmed = coords.Take(entryIndex + 1).ToList(); // Include the entry point
            var cutPoint = coords[entryIndex];
            
            return new TrimResult
            {
                TrimmedCoordinates = trimmed.Count >= 2 ? trimmed : null,
                CutPoint = cutPoint,
                CoordinatesRemoved = coords.Count - entryIndex - 1,
                KeptStartIndex = 0,
                KeptCount = entryIndex + 1
            };
        }

        // Road starts on ring and exits (keep from exit to end)
        if (transitions.Count == 1 && !transitions[0].enteringRing)
        {
            var exitIndex = transitions[0].index;
            var trimmed = coords.Skip(exitIndex).ToList();
            var cutPoint = coords[exitIndex];
            
            return new TrimResult
            {
                TrimmedCoordinates = trimmed.Count >= 2 ? trimmed : null,
                CutPoint = cutPoint,
                CoordinatesRemoved = exitIndex,
                KeptStartIndex = exitIndex,
                KeptCount = coords.Count - exitIndex
            };
        }

        // Road starts on ring with no transitions (all on ring but not all marked)
        if (startsOnRing && transitions.Count == 0)
        {
            // Find the last on-ring point
            int lastOnRingIndex = 0;
            for (int i = 0; i < coords.Count; i++)
            {
                if (onRingMask[i]) lastOnRingIndex = i;
            }
            
            if (lastOnRingIndex < coords.Count - 1)
            {
                // Some portion at end is off-ring
                var trimmed = coords.Skip(lastOnRingIndex).ToList();
                return new TrimResult
                {
                    TrimmedCoordinates = trimmed.Count >= 2 ? trimmed : null,
                    CutPoint = coords[lastOnRingIndex],
                    CoordinatesRemoved = lastOnRingIndex,
                    KeptStartIndex = lastOnRingIndex,
                    KeptCount = coords.Count - lastOnRingIndex
                };
            }
        }

        // Road ends on ring with no transitions
        if (endsOnRing && transitions.Count == 0)
        {
            // Find the first on-ring point
            int firstOnRingIndex = coords.Count - 1;
            for (int i = coords.Count - 1; i >= 0; i--)
            {
                if (onRingMask[i]) firstOnRingIndex = i;
            }
            
            if (firstOnRingIndex > 0)
            {
                // Some portion at start is off-ring
                var trimmed = coords.Take(firstOnRingIndex + 1).ToList();
                return new TrimResult
                {
                    TrimmedCoordinates = trimmed.Count >= 2 ? trimmed : null,
                    CutPoint = coords[firstOnRingIndex],
                    CoordinatesRemoved = coords.Count - firstOnRingIndex - 1,
                    KeptStartIndex = 0,
                    KeptCount = firstOnRingIndex + 1
                };
            }
        }

        // Road passes through (enters and exits)
        // This creates TWO separate road segments - we'll keep the longer one
        if (transitions.Count >= 2)
        {
            var firstEntry = transitions.FirstOrDefault(t => t.enteringRing);
            var lastExit = transitions.LastOrDefault(t => !t.enteringRing);
            
            // Calculate which segment is longer
            int preRingLength = 0;
            int postRingLength = 0;
            
            if (firstEntry.enteringRing) // Check it's actually an entry
            {
                preRingLength = firstEntry.index + 1;
            }
            
            if (lastExit != default && !lastExit.enteringRing) // Check it's actually an exit
            {
                postRingLength = coords.Count - lastExit.index;
            }
            
            if (preRingLength >= postRingLength && preRingLength >= 2)
            {
                var trimmed = coords.Take(firstEntry.index + 1).ToList();
                return new TrimResult
                {
                    TrimmedCoordinates = trimmed,
                    CutPoint = coords[firstEntry.index],
                    CoordinatesRemoved = coords.Count - firstEntry.index - 1,
                    KeptStartIndex = 0,
                    KeptCount = firstEntry.index + 1
                };
            }
            else if (postRingLength >= 2)
            {
                var trimmed = coords.Skip(lastExit.index).ToList();
                return new TrimResult
                {
                    TrimmedCoordinates = trimmed,
                    CutPoint = coords[lastExit.index],
                    CoordinatesRemoved = lastExit.index,
                    KeptStartIndex = lastExit.index,
                    KeptCount = coords.Count - lastExit.index
                };
            }
        }
        
        // Fallback: Find longest contiguous off-ring segment
        return FindLongestOffRingSegment(coords, onRingMask, roundabout);
    }
    
    /// <summary>
    /// Finds the longest contiguous segment that is NOT on the roundabout ring.
    /// </summary>
    private TrimResult FindLongestOffRingSegment(
        List<GeoCoordinate> coords,
        bool[] onRingMask,
        OsmRoundabout roundabout)
    {
        int bestStart = -1;
        int bestLength = 0;
        int currentStart = -1;
        int currentLength = 0;
        
        for (int i = 0; i < coords.Count; i++)
        {
            if (!onRingMask[i])
            {
                if (currentStart == -1)
                    currentStart = i;
                currentLength++;
            }
            else
            {
                if (currentLength > bestLength)
                {
                    bestStart = currentStart;
                    bestLength = currentLength;
                }
                currentStart = -1;
                currentLength = 0;
            }
        }
        
        // Check final segment
        if (currentLength > bestLength)
        {
            bestStart = currentStart;
            bestLength = currentLength;
        }
        
        if (bestLength < 2)
            return new TrimResult { TrimmedCoordinates = null, CoordinatesRemoved = coords.Count };
        
        var trimmed = coords.Skip(bestStart).Take(bestLength).ToList();
        
        // Find the cut point (adjacent on-ring point)
        GeoCoordinate? cutPoint = null;
        if (bestStart > 0 && onRingMask[bestStart - 1])
        {
            cutPoint = coords[bestStart - 1];
        }
        else if (bestStart + bestLength < coords.Count && onRingMask[bestStart + bestLength])
        {
            cutPoint = coords[bestStart + bestLength];
        }
        else
        {
            // Use the first point of the kept segment as the cut point
            cutPoint = trimmed[0];
        }
        
        return new TrimResult
        {
            TrimmedCoordinates = trimmed,
            CutPoint = cutPoint,
            CoordinatesRemoved = coords.Count - bestLength,
            KeptStartIndex = bestStart,
            KeptCount = bestLength
        };
    }
    
    /// <summary>
    /// Checks if a coordinate is within the roundabout radius tolerance.
    /// This catches points that are ON the roundabout but not exactly matching ring nodes.
    /// </summary>
    private bool IsWithinRadiusTolerance(GeoCoordinate coord, OsmRoundabout roundabout)
    {
        // Calculate distance from roundabout center
        double latRadians = roundabout.Center.Latitude * Math.PI / 180.0;
        double metersPerDegreeLon = 111320.0 * Math.Cos(latRadians);
        double metersPerDegreeLat = 110574.0;
        
        double dx = (coord.Longitude - roundabout.Center.Longitude) * metersPerDegreeLon;
        double dy = (coord.Latitude - roundabout.Center.Latitude) * metersPerDegreeLat;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        
        // Point is "on" the roundabout if within tolerance of the ring radius
        return Math.Abs(distance - roundabout.RadiusMeters) < OverlapToleranceMeters;
    }
    
    /// <summary>
    /// Updates the roundabout's connection info after a road has been trimmed.
    /// </summary>
    private void UpdateConnectionPoint(OsmRoundabout roundabout, OsmFeature road, GeoCoordinate cutPoint)
    {
        // Find or update the connection for this road
        var existingConnection = roundabout.Connections.FirstOrDefault(c => c.ConnectingWayId == road.Id);
        
        if (existingConnection != null)
        {
            existingConnection.ConnectionPoint = cutPoint;
            existingConnection.RingCoordinateIndex = FindClosestRingIndex(roundabout, cutPoint);
            existingConnection.AngleDegrees = CalculateAngleFromCenter(roundabout.Center, cutPoint);
        }
        else
        {
            // Add a new connection if one doesn't exist
            // (This can happen if the road was not previously detected as connecting)
            roundabout.Connections.Add(new RoundaboutConnection
            {
                ConnectingWayId = road.Id,
                ConnectionPoint = cutPoint,
                RingCoordinateIndex = FindClosestRingIndex(roundabout, cutPoint),
                AngleDegrees = CalculateAngleFromCenter(roundabout.Center, cutPoint),
                Direction = DetermineConnectionDirection(road),
                ConnectingRoad = road,
                ConnectingRoadCoordinateIndex = road.Coordinates.Count > 0 ? road.Coordinates.Count - 1 : 0
            });
        }
    }
    
    /// <summary>
    /// Finds the closest ring coordinate index to a given point.
    /// </summary>
    private static int FindClosestRingIndex(OsmRoundabout roundabout, GeoCoordinate point)
    {
        int closestIndex = 0;
        double closestDistance = double.MaxValue;
        
        for (int i = 0; i < roundabout.RingCoordinates.Count; i++)
        {
            var ringCoord = roundabout.RingCoordinates[i];
            double dx = point.Longitude - ringCoord.Longitude;
            double dy = point.Latitude - ringCoord.Latitude;
            double dist = dx * dx + dy * dy;
            
            if (dist < closestDistance)
            {
                closestDistance = dist;
                closestIndex = i;
            }
        }
        
        return closestIndex;
    }
    
    /// <summary>
    /// Calculates the angle from center to a point (0 = East, 90 = North).
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
    /// Determines the connection direction for a trimmed road.
    /// After trimming, the road's last point is the connection point.
    /// </summary>
    private static RoundaboutConnectionDirection DetermineConnectionDirection(OsmFeature road)
    {
        // Check for oneway tag
        if (road.Tags.TryGetValue("oneway", out var oneway))
        {
            if (oneway.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                oneway.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                oneway.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                // After trimming, the road ends at the roundabout -> Entry
                return RoundaboutConnectionDirection.Entry;
            }
            
            if (oneway.Equals("-1", StringComparison.OrdinalIgnoreCase))
            {
                // Reverse oneway -> Exit
                return RoundaboutConnectionDirection.Exit;
            }
        }
        
        return RoundaboutConnectionDirection.Bidirectional;
    }
}
