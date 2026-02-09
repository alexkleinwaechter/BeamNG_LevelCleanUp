using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Osm.Models;

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Detects roundabouts from OSM query results by finding ways tagged with
/// junction=roundabout and merging connected segments into complete rings.
/// 
/// Algorithm based on Overpass API blog technique:
/// 1. Find all ways with junction=roundabout tag
/// 2. Group connected ways by shared endpoints (transitive closure)
/// 3. Merge each group into a single closed ring
/// 4. Identify connection points where other roads meet the ring
/// </summary>
public class RoundaboutDetector
{
    /// <summary>
    /// Tolerance for coordinate matching when connecting roundabout segments.
    /// Approximately 0.1 meters at the equator.
    /// </summary>
    private const double CoordinateToleranceDegrees = 0.000001;
    
    /// <summary>
    /// Maximum number of iterations when building transitive closure.
    /// Prevents infinite loops in malformed data.
    /// </summary>
    private const int MaxIterations = 1000;
    
    /// <summary>
    /// Detects all roundabouts in the OSM query result.
    /// </summary>
    /// <param name="queryResult">The OSM query result containing all features.</param>
    /// <returns>List of detected roundabouts with merged ring geometry.</returns>
    public List<OsmRoundabout> DetectRoundabouts(OsmQueryResult queryResult)
    {
        // Step 1: Find all features tagged as roundabouts
        var roundaboutWays = queryResult.Features
            .Where(f => f.Tags.TryGetValue("junction", out var junction) && 
                        junction.Equals("roundabout", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.GeometryType == OsmGeometryType.LineString)
            .ToList();
        
        if (roundaboutWays.Count == 0)
        {
            TerrainLogger.Info("RoundaboutDetector: No roundabout ways found in query result");
            return [];
        }
        
        TerrainLogger.Info($"RoundaboutDetector: Found {roundaboutWays.Count} roundabout way(s)");
        
        // Step 2: Group connected roundabout ways
        var roundaboutGroups = GroupConnectedRoundaboutWays(roundaboutWays);
        
        TerrainLogger.Info($"RoundaboutDetector: Merged into {roundaboutGroups.Count} distinct roundabout(s)");
        
        // Step 3: Build OsmRoundabout objects from groups
        var roundabouts = new List<OsmRoundabout>();
        foreach (var group in roundaboutGroups)
        {
            var roundabout = BuildRoundaboutFromWays(group);
            if (roundabout != null)
            {
                if (roundabout.IsClosed)
                {
                    roundabouts.Add(roundabout);
                }
                else
                {
                    TerrainLogger.Warning($"RoundaboutDetector: Roundabout {roundabout.Id} is not properly closed " +
                        $"(gap between first and last coordinate). It will still be processed.");
                    // Still add it - we'll try to work with it
                    roundabouts.Add(roundabout);
                }
            }
            else
            {
                TerrainLogger.Warning($"RoundaboutDetector: Could not build ring from {group.Count} ways " +
                    $"(IDs: {string.Join(", ", group.Select(f => f.Id))})");
            }
        }
        
        // Step 4: Detect connections to non-roundabout roads
        foreach (var roundabout in roundabouts)
        {
            DetectConnections(roundabout, queryResult);
        }
        
        TerrainLogger.Info($"RoundaboutDetector: Successfully built {roundabouts.Count} roundabout(s)");
        foreach (var r in roundabouts)
        {
            TerrainLogger.Detail($"  - {r}");
        }
        
        return roundabouts;
    }
    
    /// <summary>
    /// Groups roundabout ways that share endpoints (transitive closure).
    /// Each group represents a single physical roundabout.
    /// </summary>
    private List<List<OsmFeature>> GroupConnectedRoundaboutWays(List<OsmFeature> roundaboutWays)
    {
        var groups = new List<List<OsmFeature>>();
        var assigned = new HashSet<long>();
        
        foreach (var way in roundaboutWays)
        {
            if (assigned.Contains(way.Id))
                continue;
            
            // Start a new group with this way
            var group = new List<OsmFeature> { way };
            assigned.Add(way.Id);
            
            // Expand group using transitive closure
            bool expanded;
            int iterations = 0;
            do
            {
                expanded = false;
                iterations++;
                
                foreach (var otherWay in roundaboutWays)
                {
                    if (assigned.Contains(otherWay.Id))
                        continue;
                    
                    // Check if otherWay connects to any way in the current group
                    if (group.Any(g => WaysShareEndpoint(g, otherWay)))
                    {
                        group.Add(otherWay);
                        assigned.Add(otherWay.Id);
                        expanded = true;
                    }
                }
            } while (expanded && iterations < MaxIterations);
            
            if (iterations >= MaxIterations)
            {
                TerrainLogger.Warning($"RoundaboutDetector: Hit max iterations while grouping roundabout ways. " +
                    $"Group contains {group.Count} ways.");
            }
            
            groups.Add(group);
        }
        
        return groups;
    }
    
    /// <summary>
    /// Checks if two ways share an endpoint (within tolerance).
    /// </summary>
    private bool WaysShareEndpoint(OsmFeature a, OsmFeature b)
    {
        if (a.Coordinates.Count < 2 || b.Coordinates.Count < 2)
            return false;
        
        var aStart = a.Coordinates[0];
        var aEnd = a.Coordinates[^1];
        var bStart = b.Coordinates[0];
        var bEnd = b.Coordinates[^1];
        
        return CoordinatesMatch(aStart, bStart) || CoordinatesMatch(aStart, bEnd) ||
               CoordinatesMatch(aEnd, bStart) || CoordinatesMatch(aEnd, bEnd);
    }
    
    /// <summary>
    /// Checks if two coordinates are effectively the same point.
    /// </summary>
    private static bool CoordinatesMatch(GeoCoordinate a, GeoCoordinate b)
    {
        return Math.Abs(a.Longitude - b.Longitude) < CoordinateToleranceDegrees &&
               Math.Abs(a.Latitude - b.Latitude) < CoordinateToleranceDegrees;
    }
    
    /// <summary>
    /// Merges a group of connected roundabout ways into a single OsmRoundabout.
    /// </summary>
    private OsmRoundabout? BuildRoundaboutFromWays(List<OsmFeature> ways)
    {
        if (ways.Count == 0)
            return null;
        
        // Assemble ways into a closed ring
        var ringCoordinates = AssembleRing(ways);
        if (ringCoordinates.Count < 3)
        {
            TerrainLogger.Warning($"RoundaboutDetector: Ring has fewer than 3 coordinates after assembly");
            return null;
        }
        
        // Ensure ring is closed
        if (!CoordinatesMatch(ringCoordinates[0], ringCoordinates[^1]))
        {
            // Try to close it by adding the first coordinate again
            ringCoordinates.Add(ringCoordinates[0]);
        }
        
        // Calculate center and radius
        var center = CalculateCentroid(ringCoordinates);
        var radius = CalculateAverageRadius(ringCoordinates, center);
        
        // Use tags from the first way (could be enhanced to merge tags)
        var primaryWay = ways.OrderByDescending(w => w.Coordinates.Count).First();
        
        return new OsmRoundabout
        {
            Id = primaryWay.Id,
            WayIds = ways.Select(w => w.Id).ToList(),
            RingCoordinates = ringCoordinates,
            Center = center,
            RadiusMeters = radius,
            Tags = new Dictionary<string, string>(primaryWay.Tags),
            OriginalFeatures = ways
        };
    }
    
    /// <summary>
    /// Assembles roundabout ways into a single ordered ring.
    /// Similar to the ring assembly logic in OsmGeoJsonParser.
    /// </summary>
    private List<GeoCoordinate> AssembleRing(List<OsmFeature> ways)
    {
        if (ways.Count == 1)
            return new List<GeoCoordinate>(ways[0].Coordinates);
        
        // Make copies to avoid modifying originals
        var remaining = ways.Select(w => new List<GeoCoordinate>(w.Coordinates)).ToList();
        
        // Start with the first segment
        var ring = new List<GeoCoordinate>(remaining[0]);
        remaining.RemoveAt(0);
        
        // Keep trying to extend the ring
        bool didExtend;
        int iterations = 0;
        do
        {
            didExtend = false;
            iterations++;
            
            var ringStart = ring[0];
            var ringEnd = ring[^1];
            
            for (int i = 0; i < remaining.Count; i++)
            {
                var segment = remaining[i];
                if (segment.Count < 2)
                {
                    remaining.RemoveAt(i);
                    i--;
                    continue;
                }
                
                var segStart = segment[0];
                var segEnd = segment[^1];
                
                // Check if segment connects to ring end
                if (CoordinatesMatch(ringEnd, segStart))
                {
                    // Append segment (skip first point which is duplicate)
                    ring.AddRange(segment.Skip(1));
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
                else if (CoordinatesMatch(ringEnd, segEnd))
                {
                    // Append reversed segment (skip last point which is duplicate)
                    for (int j = segment.Count - 2; j >= 0; j--)
                        ring.Add(segment[j]);
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
                // Check if segment connects to ring start
                else if (CoordinatesMatch(ringStart, segEnd))
                {
                    // Prepend segment (skip last point which is duplicate)
                    var newRing = new List<GeoCoordinate>(segment.Take(segment.Count - 1));
                    newRing.AddRange(ring);
                    ring = newRing;
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
                else if (CoordinatesMatch(ringStart, segStart))
                {
                    // Prepend reversed segment (skip first point which is duplicate)
                    var newRing = new List<GeoCoordinate>();
                    for (int j = segment.Count - 1; j >= 1; j--)
                        newRing.Add(segment[j]);
                    newRing.AddRange(ring);
                    ring = newRing;
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
            }
        } while (didExtend && remaining.Count > 0 && iterations < MaxIterations);
        
        if (remaining.Count > 0)
        {
            TerrainLogger.Warning($"RoundaboutDetector: Could not connect all segments. " +
                $"{remaining.Count} segment(s) remaining after {iterations} iterations.");
        }
        
        return ring;
    }
    
    /// <summary>
    /// Calculates the centroid of a ring of coordinates.
    /// </summary>
    private static GeoCoordinate CalculateCentroid(List<GeoCoordinate> coords)
    {
        // For a closed ring, exclude the duplicate last point
        var uniqueCoords = coords;
        if (coords.Count > 1 && CoordinatesMatch(coords[0], coords[^1]))
        {
            uniqueCoords = coords.Take(coords.Count - 1).ToList();
        }
        
        var sumLon = uniqueCoords.Sum(c => c.Longitude);
        var sumLat = uniqueCoords.Sum(c => c.Latitude);
        return new GeoCoordinate(sumLon / uniqueCoords.Count, sumLat / uniqueCoords.Count);
    }
    
    /// <summary>
    /// Calculates the average radius from center to ring points.
    /// </summary>
    private static double CalculateAverageRadius(List<GeoCoordinate> coords, GeoCoordinate center)
    {
        // For a closed ring, exclude the duplicate last point
        var uniqueCoords = coords;
        if (coords.Count > 1 && CoordinatesMatch(coords[0], coords[^1]))
        {
            uniqueCoords = coords.Take(coords.Count - 1).ToList();
        }
        
        // Approximate meters per degree at the center latitude
        double latRadians = center.Latitude * Math.PI / 180.0;
        double metersPerDegreeLon = 111320.0 * Math.Cos(latRadians);
        double metersPerDegreeLat = 110574.0;
        
        double sumRadius = 0;
        foreach (var coord in uniqueCoords)
        {
            double dx = (coord.Longitude - center.Longitude) * metersPerDegreeLon;
            double dy = (coord.Latitude - center.Latitude) * metersPerDegreeLat;
            sumRadius += Math.Sqrt(dx * dx + dy * dy);
        }
        
        return sumRadius / uniqueCoords.Count;
    }
    
    /// <summary>
    /// Detects roads that connect to the roundabout ring.
    /// </summary>
    private void DetectConnections(OsmRoundabout roundabout, OsmQueryResult queryResult)
    {
        // Get all non-roundabout road features
        var otherRoads = queryResult.Features
            .Where(f => f.GeometryType == OsmGeometryType.LineString)
            .Where(f => !f.Tags.TryGetValue("junction", out var j) || !j.Equals("roundabout", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Category == "highway")
            .ToList();
        
        // Build a set of roundabout ring coordinates for fast lookup
        var ringCoordSet = new HashSet<(double lon, double lat)>();
        foreach (var coord in roundabout.RingCoordinates)
        {
            // Round to tolerance for matching
            ringCoordSet.Add((
                Math.Round(coord.Longitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees,
                Math.Round(coord.Latitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees
            ));
        }
        
        // Check each road for connections to the roundabout
        foreach (var road in otherRoads)
        {
            for (int i = 0; i < road.Coordinates.Count; i++)
            {
                var roadCoord = road.Coordinates[i];
                var roundedCoord = (
                    Math.Round(roadCoord.Longitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees,
                    Math.Round(roadCoord.Latitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees
                );
                
                if (ringCoordSet.Contains(roundedCoord))
                {
                    // Find the matching ring coordinate index
                    int ringIndex = FindClosestRingIndex(roundabout, roadCoord);
                    var ringCoord = roundabout.RingCoordinates[ringIndex];
                    
                    // Found a connection
                    var connection = new RoundaboutConnection
                    {
                        ConnectingWayId = road.Id,
                        ConnectionPoint = ringCoord,
                        RingCoordinateIndex = ringIndex,
                        AngleDegrees = CalculateAngleFromCenter(roundabout.Center, ringCoord),
                        Direction = DetermineConnectionDirection(road, i),
                        ConnectingRoad = road,
                        ConnectingRoadCoordinateIndex = i
                    };
                    
                    // Avoid duplicate connections (same road at same ring position)
                    if (!roundabout.Connections.Any(c => 
                        c.ConnectingWayId == road.Id && 
                        c.RingCoordinateIndex == ringIndex))
                    {
                        roundabout.Connections.Add(connection);
                    }
                }
            }
        }
        
        // Sort connections by angle for consistent ordering
        roundabout.Connections = roundabout.Connections
            .OrderBy(c => c.AngleDegrees)
            .ToList();
        
        TerrainLogger.Detail($"  Roundabout {roundabout.Id}: detected {roundabout.Connections.Count} road connection(s)");
    }
    
    /// <summary>
    /// Finds the closest ring coordinate index to a given point.
    /// </summary>
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
    /// Determines the direction of a connecting road based on position and oneway tag.
    /// </summary>
    private static RoundaboutConnectionDirection DetermineConnectionDirection(OsmFeature road, int connectionIndex)
    {
        // Check for oneway tag
        if (road.Tags.TryGetValue("oneway", out var oneway))
        {
            bool isOneway = oneway.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                           oneway.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           oneway.Equals("1", StringComparison.OrdinalIgnoreCase);
            
            if (isOneway)
            {
                // If connection is at start of road, it's an exit from roundabout
                // If connection is at end of road, it's an entry to roundabout
                if (connectionIndex == 0)
                    return RoundaboutConnectionDirection.Exit;
                else if (connectionIndex == road.Coordinates.Count - 1)
                    return RoundaboutConnectionDirection.Entry;
            }
            
            if (oneway.Equals("-1", StringComparison.OrdinalIgnoreCase))
            {
                // Reverse oneway
                if (connectionIndex == 0)
                    return RoundaboutConnectionDirection.Entry;
                else if (connectionIndex == road.Coordinates.Count - 1)
                    return RoundaboutConnectionDirection.Exit;
            }
        }
        
        return RoundaboutConnectionDirection.Bidirectional;
    }
}
