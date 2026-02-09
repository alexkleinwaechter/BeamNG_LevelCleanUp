# Roundabout Detection and Handling Implementation Plan

## Implementation Status

| Phase | Description | Status | Notes |
|-------|-------------|--------|-------|
| 1 | Roundabout Detection from OSM Data | ? **COMPLETE** | `OsmRoundabout.cs`, `RoundaboutDetector.cs` created |
| 2 | Trim Connecting Roads | ? **COMPLETE** | `ConnectingRoadTrimmer.cs` created, integrated with OsmGeometryProcessor |
| 3 | Roundabout Spline Creation | ? **COMPLETE** | `RoundaboutMerger.cs` created with `ProcessedRoundaboutInfo` for junction detection |
| 4 | Enhanced Junction Detection | ? **COMPLETE** | `RoundaboutJunction.cs` created, `NetworkJunctionDetector.cs` updated with `DetectRoundaboutJunctions()` method, `JunctionType.Roundabout` added |
| 5 | Integration with OsmGeometryProcessor | ? **COMPLETE** | `ConvertLinesToSplinesWithRoundabouts()` fully integrated; `TerrainGenerationOrchestrator.ProcessOsmRoadMaterialAsync()` now uses roundabout-aware processing when `EnableRoundaboutDetection` is true; `RoadSmoothingParameters.RoundaboutProcessingResult` added for passing roundabout info to junction detection |
| 6 | Elevation Harmonization | ? **COMPLETE** | `RoundaboutElevationHarmonizer.cs` created; supports both uniform ring elevation (`ForceUniformRoundaboutElevation=true`) and terrain-following mode (`false`) where each connecting road blends to its local ring elevation; integrated into `UnifiedRoadSmoother` pipeline; `JunctionHarmonizationParameters.RoundaboutBlendDistanceMeters` added |
| 7 | Visual Debugging | ? **COMPLETE** | `RoundaboutDebugImageExporter.cs` created; exports debug images showing roundabout rings (yellow), trimmed portions (red), connecting roads (cyan), original paths (gray), connection points (green circles), and roundabout centers (magenta crosshairs) |

### Files Created/Modified

**New Files:**
- `BeamNgTerrainPoc/Terrain/Osm/Models/OsmRoundabout.cs` - Roundabout data model with connections
- `BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutDetector.cs` - Detection and ring assembly logic
- `BeamNgTerrainPoc/Terrain/Osm/Processing/ConnectingRoadTrimmer.cs` - Trims roads that overlap with roundabouts
- `BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutMerger.cs` - Converts roundabouts to splines with tracking info for junction detection
- `BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutDebugImageExporter.cs` - **NEW** - Exports visual debug images showing roundabout processing results
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoundaboutJunction.cs` - Extended junction model for roundabouts with `RoundaboutJunctionInfo` for harmonization
- `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs` - Harmonizes roundabout ring elevations uniformly and blends connecting roads

**Modified Files:**
- `BeamNgTerrainPoc/Terrain/Osm/Models/OsmFeature.cs` - Added `IsRoundabout` property
- `BeamNgTerrainPoc/Terrain/Osm/Models/OsmQueryResult.cs` - Added `RoundaboutFeatures` and `NonRoundaboutHighways` properties
- `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs` - Added `DetectRoundabouts()` and `ConvertLinesToSplinesWithRoundabouts()` methods with road trimming integration; updated to use `RoundaboutMerger` for spline creation; added `debugOutputPath` parameter for roundabout debug image export
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` - Added roundabout-specific settings including `EnableRoundaboutRoadTrimming`, `RoundaboutOverlapToleranceMeters`, `RoundaboutBlendDistanceMeters`, and `ExportRoundaboutDebugImage`
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs` - Added `JunctionType.Roundabout` enum value
- `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs` - Added `DetectRoundaboutJunctions()` method and supporting helper methods for roundabout junction detection
- `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` - Added `RoundaboutProcessingResult` property to store roundabout processing info for downstream junction detection
- `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` - Updated `ProcessOsmRoadMaterialAsync()` to use `ConvertLinesToSplinesWithRoundabouts()` when roundabout detection is enabled, respecting `JunctionHarmonizationParameters` settings; passes debug output path for roundabout visualization
- `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` - Added Phase 2.6 for roundabout elevation harmonization, integrated `RoundaboutElevationHarmonizer`

---

## Executive Summary

This document outlines an implementation plan for detecting and properly handling roundabouts in OSM road network data. Currently, roundabouts from OSM appear as fragmented, quirky splines because they are tagged with `junction=roundabout` but split into multiple ways at each connecting road. The goal is to:

1. **Detect roundabouts** during OSM data processing
2. **Merge roundabout segments** into single circular (donut) splines
3. **Create proper junctions** where connecting roads meet the roundabout ring
4. **Handle elevation harmonization** so connecting roads blend smoothly into the roundabout

## Problem Statement

### Current Behavior

When OSM data contains a roundabout, the Overpass API returns it as:
- Multiple way elements, each tagged with `junction=roundabout`
- These ways are split at each point where another road connects
- Each segment becomes a separate spline in our processing pipeline
- Junction detection creates poor connections because:
  - Endpoints of roundabout segments don't align well with connecting road endpoints
  - The circular geometry is lost when treated as linear segments
  - Elevation harmonization treats each segment independently

### Desired Behavior

1. Roundabout segments should be **merged into a single closed-loop spline** (donut shape)
2. **Connecting roads must be TRIMMED** - portions that overlap with the roundabout ring must be cut off and deleted
3. Connecting roads should form **T-junctions** with the roundabout ring at the trim point
4. The roundabout should have **consistent elevation** around its circumference
5. Connecting roads should **blend smoothly** into the roundabout at their junction points

### Critical Issue: Overlapping Road Segments

**The most important fix**: Connecting roads often have segments that overlap with the roundabout ring. In OSM data, roads don't just touch the roundabout at a single point - they often share several nodes WITH the roundabout, creating:

- **High-angle turns** where the road follows the circular path
- **Weird elevation changes** as the road tries to follow the ring curvature
- **Quirky spline geometry** that creates bumps and jumps

**Solution**: When a connecting road intersects a roundabout, we must:
1. Find the **first point** where the road touches the roundabout ring
2. **Cut the road** at that point
3. **Delete the portion** that overlaps with/follows the roundabout
4. Keep only the portion that approaches the roundabout from outside

## Architecture Overview

### New Code Files

All new roundabout-related code will be placed in dedicated files to keep existing code lean:

```
BeamNgTerrainPoc/
??? Terrain/
?   ??? Osm/
?   ?   ??? Models/
?   ?   ?   ??? OsmRoundabout.cs           # Roundabout data model
?   ?   ??? Processing/
?   ?   ?   ??? RoundaboutDetector.cs      # Detection logic
?   ?   ?   ??? RoundaboutMerger.cs        # Merging segments into rings
?   ?   ?   ??? ConnectingRoadTrimmer.cs   # Trim overlapping road segments (NEW - CRITICAL)
?   ?   ??? Services/
?   ?       ??? RoundaboutQueryBuilder.cs  # Enhanced Overpass queries (optional)
?   ??? Models/
?       ??? RoadGeometry/
?           ??? RoundaboutJunction.cs      # Extended junction type for roundabouts
```

### Integration Points

1. **OsmGeometryProcessor.cs** - Call roundabout detection before `ConvertLinesToSplines()`
2. **UnifiedRoadNetworkBuilder.cs** - Handle roundabout splines with special ring geometry
3. **NetworkJunctionDetector.cs** - Detect T-junctions with roundabout rings
4. **JunctionHarmonizationParameters** - Add roundabout-specific harmonization settings

## Implementation Phases

### Phase 1: Roundabout Detection from OSM Data

#### 1.1 Create `OsmRoundabout.cs` Model

```csharp
// BeamNgTerrainPoc/Terrain/Osm/Models/OsmRoundabout.cs

namespace BeamNgTerrainPoc.Terrain.Osm.Models;

/// <summary>
/// Represents a detected roundabout composed of one or more OSM ways.
/// A roundabout is a closed circular road junction where traffic flows
/// continuously around a central island.
/// </summary>
public class OsmRoundabout
{
    /// <summary>
    /// Unique identifier for this roundabout (derived from first way ID).
    /// </summary>
    public long Id { get; set; }
    
    /// <summary>
    /// All OSM way IDs that make up this roundabout.
    /// A single roundabout may be composed of multiple ways (split at connecting roads).
    /// </summary>
    public List<long> WayIds { get; set; } = [];
    
    /// <summary>
    /// The merged, ordered coordinates forming the complete roundabout ring.
    /// First point equals last point (closed ring).
    /// </summary>
    public List<GeoCoordinate> RingCoordinates { get; set; } = [];
    
    /// <summary>
    /// Approximate center point of the roundabout.
    /// Calculated as the centroid of all ring coordinates.
    /// </summary>
    public GeoCoordinate Center { get; set; }
    
    /// <summary>
    /// Approximate radius of the roundabout in meters.
    /// Calculated as average distance from center to ring points.
    /// </summary>
    public double RadiusMeters { get; set; }
    
    /// <summary>
    /// Connection points where other roads join the roundabout.
    /// These are detected by finding where non-roundabout ways share
    /// nodes with the roundabout ring.
    /// </summary>
    public List<RoundaboutConnection> Connections { get; set; } = [];
    
    /// <summary>
    /// Tags from the primary roundabout way (e.g., highway type, name).
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
    
    /// <summary>
    /// Whether this roundabout ring is properly closed (first == last coordinate).
    /// </summary>
    public bool IsClosed => RingCoordinates.Count >= 3 && 
        Math.Abs(RingCoordinates[0].Longitude - RingCoordinates[^1].Longitude) < 0.0000001 &&
        Math.Abs(RingCoordinates[0].Latitude - RingCoordinates[^1].Latitude) < 0.0000001;
    
    /// <summary>
    /// Whether this roundabout has at least 3 connection points (typical minimum).
    /// </summary>
    public bool HasMinimumConnections => Connections.Count >= 3;
}

/// <summary>
/// Represents a connection point where a road joins a roundabout.
/// </summary>
public class RoundaboutConnection
{
    /// <summary>
    /// The OSM way ID of the connecting road.
    /// </summary>
    public long ConnectingWayId { get; set; }
    
    /// <summary>
    /// The coordinate where the connection occurs on the roundabout ring.
    /// </summary>
    public GeoCoordinate ConnectionPoint { get; set; }
    
    /// <summary>
    /// The index into the roundabout's RingCoordinates where this connection occurs.
    /// Used for efficient lookup during junction creation.
    /// </summary>
    public int RingCoordinateIndex { get; set; }
    
    /// <summary>
    /// Angle (in degrees, 0-360) around the roundabout center where this connection is.
    /// 0 = East, 90 = North, 180 = West, 270 = South.
    /// </summary>
    public double AngleDegrees { get; set; }
    
    /// <summary>
    /// Whether the connecting road is entering or exiting (based on OSM oneway if present).
    /// </summary>
    public RoundaboutConnectionDirection Direction { get; set; }
    
    /// <summary>
    /// The OsmFeature of the connecting road.
    /// </summary>
    public OsmFeature? ConnectingRoad { get; set; }
}

/// <summary>
/// Direction of traffic at a roundabout connection.
/// </summary>
public enum RoundaboutConnectionDirection
{
    /// <summary>
    /// Cannot determine direction (no oneway tag or bidirectional).
    /// </summary>
    Unknown,
    
    /// <summary>
    /// Road leads into the roundabout (entry arm).
    /// </summary>
    Entry,
    
    /// <summary>
    /// Road leads out of the roundabout (exit arm).
    /// </summary>
    Exit,
    
    /// <summary>
    /// Road is bidirectional (both entry and exit).
    /// </summary>
    Bidirectional
}
```

#### 1.2 Create `RoundaboutDetector.cs`

```csharp
// BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutDetector.cs

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
            return [];
        
        TerrainLogger.Info($"RoundaboutDetector: Found {roundaboutWays.Count} roundabout way(s)");
        
        // Step 2: Group connected roundabout ways
        var roundaboutGroups = GroupConnectedRoundaboutWays(roundaboutWays);
        
        TerrainLogger.Info($"RoundaboutDetector: Merged into {roundaboutGroups.Count} distinct roundabout(s)");
        
        // Step 3: Build OsmRoundabout objects from groups
        var roundabouts = new List<OsmRoundabout>();
        foreach (var group in roundaboutGroups)
        {
            var roundabout = BuildRoundaboutFromWays(group, queryResult);
            if (roundabout != null && roundabout.IsClosed)
            {
                roundabouts.Add(roundabout);
            }
            else
            {
                TerrainLogger.Warning($"RoundaboutDetector: Could not build closed ring from {group.Count} ways (IDs: {string.Join(", ", group.Select(f => f.Id))})");
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
            TerrainLogger.Info($"  - Roundabout {r.Id}: {r.WayIds.Count} way(s), {r.RingCoordinates.Count} coords, " +
                              $"radius ~{r.RadiusMeters:F1}m, {r.Connections.Count} connections");
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
    private bool CoordinatesMatch(GeoCoordinate a, GeoCoordinate b)
    {
        return Math.Abs(a.Longitude - b.Longitude) < CoordinateToleranceDegrees &&
               Math.Abs(a.Latitude - b.Latitude) < CoordinateToleranceDegrees;
    }
    
    /// <summary>
    /// Merges a group of connected roundabout ways into a single OsmRoundabout.
    /// </summary>
    private OsmRoundabout? BuildRoundaboutFromWays(List<OsmFeature> ways, OsmQueryResult queryResult)
    {
        if (ways.Count == 0)
            return null;
        
        // Assemble ways into a closed ring
        var ringCoordinates = AssembleRing(ways);
        if (ringCoordinates.Count < 3)
            return null;
        
        // Ensure ring is closed
        if (!CoordinatesMatch(ringCoordinates[0], ringCoordinates[^1]))
        {
            ringCoordinates.Add(ringCoordinates[0]);
        }
        
        // Calculate center and radius
        var center = CalculateCentroid(ringCoordinates);
        var radius = CalculateAverageRadius(ringCoordinates, center);
        
        return new OsmRoundabout
        {
            Id = ways[0].Id,
            WayIds = ways.Select(w => w.Id).ToList(),
            RingCoordinates = ringCoordinates,
            Center = center,
            RadiusMeters = radius,
            Tags = ways[0].Tags
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
                var segStart = segment[0];
                var segEnd = segment[^1];
                
                // Check if segment connects to ring end
                if (CoordinatesMatch(ringEnd, segStart))
                {
                    ring.AddRange(segment.Skip(1));
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
                else if (CoordinatesMatch(ringEnd, segEnd))
                {
                    for (int j = segment.Count - 2; j >= 0; j--)
                        ring.Add(segment[j]);
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
                // Check if segment connects to ring start
                else if (CoordinatesMatch(ringStart, segEnd))
                {
                    var newRing = new List<GeoCoordinate>(segment.Take(segment.Count - 1));
                    newRing.AddRange(ring);
                    ring = newRing;
                    remaining.RemoveAt(i);
                    didExtend = true;
                    break;
                }
                else if (CoordinatesMatch(ringStart, segStart))
                {
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
        
        return ring;
    }
    
    /// <summary>
    /// Calculates the centroid of a ring of coordinates.
    /// </summary>
    private GeoCoordinate CalculateCentroid(List<GeoCoordinate> coords)
    {
        var sumLon = coords.Sum(c => c.Longitude);
        var sumLat = coords.Sum(c => c.Latitude);
        return new GeoCoordinate(sumLon / coords.Count, sumLat / coords.Count);
    }
    
    /// <summary>
    /// Calculates the average radius from center to ring points.
    /// </summary>
    private double CalculateAverageRadius(List<GeoCoordinate> coords, GeoCoordinate center)
    {
        // Approximate meters per degree at the center latitude
        double latRadians = center.Latitude * Math.PI / 180.0;
        double metersPerDegreeLon = 111320.0 * Math.Cos(latRadians);
        double metersPerDegreeLat = 110574.0;
        
        double sumRadius = 0;
        foreach (var coord in coords)
        {
            double dx = (coord.Longitude - center.Longitude) * metersPerDegreeLon;
            double dy = (coord.Latitude - center.Latitude) * metersPerDegreeLat;
            sumRadius += Math.Sqrt(dx * dx + dy * dy);
        }
        
        return sumRadius / coords.Count;
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
        
        // Check each road for connections to the roundabout
        foreach (var road in otherRoads)
        {
            for (int i = 0; i < road.Coordinates.Count; i++)
            {
                var roadCoord = road.Coordinates[i];
                
                // Check if this coordinate matches any point on the roundabout ring
                for (int j = 0; j < roundabout.RingCoordinates.Count; j++)
                {
                    var ringCoord = roundabout.RingCoordinates[j];
                    
                    if (CoordinatesMatch(roadCoord, ringCoord))
                    {
                        // Found a connection
                        var connection = new RoundaboutConnection
                        {
                            ConnectingWayId = road.Id,
                            ConnectionPoint = ringCoord,
                            RingCoordinateIndex = j,
                            AngleDegrees = CalculateAngleFromCenter(roundabout.Center, ringCoord),
                            Direction = DetermineConnectionDirection(road, i),
                            ConnectingRoad = road
                        };
                        
                        // Avoid duplicate connections
                        if (!roundabout.Connections.Any(c => c.ConnectingWayId == road.Id && c.RingCoordinateIndex == j))
                        {
                            roundabout.Connections.Add(connection);
                        }
                        
                        break; // Only one connection per coordinate
                    }
                }
            }
        }
        
        // Sort connections by angle for consistent ordering
        roundabout.Connections = roundabout.Connections
            .OrderBy(c => c.AngleDegrees)
            .ToList();
    }
    
    /// <summary>
    /// Calculates the angle from center to a point (0 = East, 90 = North).
    /// </summary>
    private double CalculateAngleFromCenter(GeoCoordinate center, GeoCoordinate point)
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
    private RoundaboutConnectionDirection DetermineConnectionDirection(OsmFeature road, int connectionIndex)
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
```

### Phase 2: Trim Connecting Roads (CRITICAL)

This is the most important phase for fixing quirky splines. Roads that connect to roundabouts often share multiple nodes with the ring, creating high-angle turns and elevation spikes. We must **cut these roads at the first intersection point** and **delete the overlapping portion**.

#### 2.1 Create `ConnectingRoadTrimmer.cs`

```csharp
// BeamNgTerrainPoc/Terrain/Osm/Processing/ConnectingRoadTrimmer.cs

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
    }
    
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
        var featuresToRemove = new HashSet<long>();
        int totalTrimmed = 0;
        int totalCoordsRemoved = 0;
        
        // Build set of all roundabout ring coordinates for fast lookup
        var roundaboutRingPoints = new Dictionary<long, HashSet<(double lon, double lat)>>();
        foreach (var roundabout in roundabouts)
        {
            var ringPointSet = new HashSet<(double lon, double lat)>();
            foreach (var coord in roundabout.RingCoordinates)
            {
                // Round to tolerance for matching
                ringPointSet.Add((
                    Math.Round(coord.Longitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees,
                    Math.Round(coord.Latitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees
                ));
            }
            roundaboutRingPoints[roundabout.Id] = ringPointSet;
        }
        
        // Process each non-roundabout road
        foreach (var feature in allFeatures)
        {
            // Skip roundabout ways themselves
            if (feature.Tags.TryGetValue("junction", out var junction) &&
                junction.Equals("roundabout", StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Skip non-highway features
            if (feature.Category != "highway")
                continue;
            
            if (feature.GeometryType != OsmGeometryType.LineString)
                continue;
            
            // Check against each roundabout
            foreach (var roundabout in roundabouts)
            {
                var result = TrimRoadAgainstRoundabout(feature, roundabout, roundaboutRingPoints[roundabout.Id]);
                
                if (result.ShouldDeleteEntireRoad)
                {
                    featuresToRemove.Add(feature.Id);
                    TerrainLogger.Info($"  Road {feature.Id} ({feature.DisplayName}) entirely inside roundabout - marking for deletion");
                    break; // No need to check other roundabouts
                }
                else if (result.WasTrimmed)
                {
                    // Update the feature's coordinates with the trimmed version
                    feature.Coordinates = result.TrimmedCoordinates!;
                    totalTrimmed++;
                    totalCoordsRemoved += result.CoordinatesRemoved;
                    
                    TerrainLogger.Info($"  Trimmed road {feature.Id} ({feature.DisplayName}): removed {result.CoordinatesRemoved} overlapping point(s)");
                    
                    // Update the connection info on the roundabout
                    UpdateConnectionPoint(roundabout, feature, result.CutPoint!);
                }
            }
        }
        
        TerrainLogger.Info($"ConnectingRoadTrimmer: Trimmed {totalTrimmed} road(s), removed {totalCoordsRemoved} overlapping coord(s), {featuresToRemove.Count} road(s) entirely deleted");
        
        return featuresToRemove;
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
            var rounded = (
                Math.Round(coords[i].Longitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees,
                Math.Round(coords[i].Latitude / CoordinateToleranceDegrees) * CoordinateToleranceDegrees
            );
            
            if (ringPointSet.Contains(rounded) || IsWithinRadiusTolerance(coords[i], roundabout))
            {
                onRingMask[i] = true;
                onRingCount++;
            }
        }
        
        // No overlap with this roundabout
        if (onRingCount == 0)
            return new TrimResult { TrimmedCoordinates = coords.ToList() };
        
        // Entire road is on the roundabout (should be deleted)
        if (onRingCount == coords.Count)
            return new TrimResult { TrimmedCoordinates = null, CoordinatesRemoved = coords.Count };
        
        // Find the segments to keep:
        // We want to keep the portion(s) that are OUTSIDE the roundabout
        // and cut at the FIRST point where the road enters the roundabout
        
        // Case 1: Road starts outside, enters roundabout -> keep start to first ring point
        // Case 2: Road starts inside, exits roundabout -> keep from last ring point to end
        // Case 3: Road passes through (enters and exits) -> this is complex, may need to split
        
        var result = FindBestTrimStrategy(coords, onRingMask, roundabout);
        return result;
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
                CoordinatesRemoved = coords.Count - entryIndex - 1
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
                CoordinatesRemoved = exitIndex
            };
        }
        
        // Road passes through (enters and exits)
        // This creates TWO separate road segments - we'll keep the longer one
        if (transitions.Count >= 2)
        {
            var firstEntry = transitions.FirstOrDefault(t => t.enteringRing);
            var lastExit = transitions.LastOrDefault(t => !t.enteringRing);
            
            // Calculate which segment is longer
            int preRingLength = firstEntry.index > 0 ? firstEntry.index + 1 : 0;
            int postRingLength = lastExit.index < coords.Count - 1 ? coords.Count - lastExit.index : 0;
            
            if (preRingLength >= postRingLength && preRingLength >= 2)
            {
                var trimmed = coords.Take(firstEntry.index + 1).ToList();
                return new TrimResult
                {
                    TrimmedCoordinates = trimmed,
                    CutPoint = coords[firstEntry.index],
                    CoordinatesRemoved = coords.Count - firstEntry.index - 1
                };
            }
            else if (postRingLength >= 2)
            {
                var trimmed = coords.Skip(lastExit.index).ToList();
                return new TrimResult
                {
                    TrimmedCoordinates = trimmed,
                    CutPoint = coords[lastExit.index],
                    CoordinatesRemoved = lastExit.index
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
            cutPoint = coords[bestStart - 1];
        else if (bestStart + bestLength < coords.Count && onRingMask[bestStart + bestLength])
            cutPoint = coords[bestStart + bestLength];
        
        return new TrimResult
        {
            TrimmedCoordinates = trimmed,
            CutPoint = cutPoint,
            CoordinatesRemoved = coords.Count - bestLength
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
        
        // Point is "on" the roundabout if within 2 meters of the ring radius
        return Math.Abs(distance - roundabout.RadiusMeters) < 2.0;
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
            roundabout.Connections.Add(new RoundaboutConnection
            {
                ConnectingWayId = road.Id,
                ConnectionPoint = cutPoint,
                RingCoordinateIndex = FindClosestRingIndex(roundabout, cutPoint),
                AngleDegrees = CalculateAngleFromCenter(roundabout.Center, cutPoint),
                Direction = RoundaboutConnectionDirection.Bidirectional,
                ConnectingRoad = road
            });
        }
    }
    
    private int FindClosestRingIndex(OsmRoundabout roundabout, GeoCoordinate point)
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
    
    private double CalculateAngleFromCenter(GeoCoordinate center, GeoCoordinate point)
    {
        double dx = point.Longitude - center.Longitude;
        double dy = point.Latitude - center.Latitude;
        double angleRadians = Math.Atan2(dy, dx);
        double angleDegrees = angleRadians * 180.0 / Math.PI;
        if (angleDegrees < 0) angleDegrees += 360.0;
        return angleDegrees;
    }
}
```

#### 2.2 Visual Example of Road Trimming

```
BEFORE TRIMMING:
                    
     Roundabout Ring
        ?????????
       ?         ?
      ?           ?
Road: ?????A??B??C??D??E???  (A-E are coordinates)
      ?           ?          A,B,C are OUTSIDE roundabout
       ?         ?           D,E are ON the roundabout ring
        ?????????

AFTER TRIMMING:

        ?????????
       ?         ?
      ?           ?
Road: ?????A??B??C?           Cut at C (first point on ring)
      ?           ?           D,E DELETED (overlapping portion)
       ?         ?
        ?????????

Result: Road now forms a clean T-junction at point C
```

### Phase 3: Roundabout Spline Creation

#### 2.1 Create `RoundaboutMerger.cs`

This class converts detected roundabouts into proper RoadSpline objects:

```csharp
// BeamNgTerrainPoc/Terrain/Osm/Processing/RoundaboutMerger.cs

namespace BeamNgTerrainPoc.Terrain.Osm.Processing;

/// <summary>
/// Converts detected OsmRoundabout objects into road splines suitable for
/// terrain generation. Creates a single closed-loop spline for the roundabout
/// ring and updates connection points for junction detection.
/// </summary>
public class RoundaboutMerger
{
    private readonly OsmGeometryProcessor _geometryProcessor;
    
    public RoundaboutMerger(OsmGeometryProcessor geometryProcessor)
    {
        _geometryProcessor = geometryProcessor;
    }
    
    /// <summary>
    /// Result of processing roundabouts for a material.
    /// </summary>
    public class RoundaboutProcessingResult
    {
        /// <summary>
        /// Road splines representing roundabout rings.
        /// </summary>
        public List<RoadSpline> RoundaboutSplines { get; set; } = [];
        
        /// <summary>
        /// Road splines representing connecting roads (with adjusted endpoints).
        /// </summary>
        public List<RoadSpline> ConnectingRoadSplines { get; set; } = [];
        
        /// <summary>
        /// OSM feature IDs that have been processed as roundabouts and should
        /// be excluded from normal road processing.
        /// </summary>
        public HashSet<long> ProcessedFeatureIds { get; set; } = [];
        
        /// <summary>
        /// Information about each roundabout for junction detection.
        /// </summary>
        public List<ProcessedRoundaboutInfo> RoundaboutInfos { get; set; } = [];
    }
    
    /// <summary>
    /// Information about a processed roundabout for junction detection.
    /// </summary>
    public class ProcessedRoundaboutInfo
    {
        public long OriginalId { get; set; }
        public int SplineId { get; set; }
        public Vector2 CenterMeters { get; set; }
        public float RadiusMeters { get; set; }
        public List<(int ConnectingSplineId, float AngleRadians, Vector2 ConnectionPointMeters)> Connections { get; set; } = [];
    }
    
    /// <summary>
    /// Processes roundabouts from OSM data and creates appropriate splines.
    /// </summary>
    public RoundaboutProcessingResult ProcessRoundabouts(
        List<OsmRoundabout> roundabouts,
        List<OsmFeature> allLineFeatures,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType)
    {
        var result = new RoundaboutProcessingResult();
        
        if (roundabouts.Count == 0)
            return result;
        
        TerrainLogger.Info($"RoundaboutMerger: Processing {roundabouts.Count} roundabout(s)");
        
        foreach (var roundabout in roundabouts)
        {
            // Mark all roundabout ways as processed
            foreach (var wayId in roundabout.WayIds)
            {
                result.ProcessedFeatureIds.Add(wayId);
            }
            
            // Convert roundabout ring to spline
            var ringSpline = CreateRoundaboutRingSpline(roundabout, bbox, terrainSize, metersPerPixel, interpolationType);
            if (ringSpline != null)
            {
                result.RoundaboutSplines.Add(ringSpline);
            }
        }
        
        TerrainLogger.Info($"RoundaboutMerger: Created {result.RoundaboutSplines.Count} roundabout ring spline(s)");
        TerrainLogger.Info($"RoundaboutMerger: Excluded {result.ProcessedFeatureIds.Count} way(s) from normal processing");
        
        return result;
    }
    
    /// <summary>
    /// Creates a closed-loop spline for a roundabout ring.
    /// Uses special handling to ensure smooth circular geometry.
    /// </summary>
    private RoadSpline? CreateRoundaboutRingSpline(
        OsmRoundabout roundabout,
        GeoBoundingBox bbox,
        int terrainSize,
        float metersPerPixel,
        SplineInterpolationType interpolationType)
    {
        if (roundabout.RingCoordinates.Count < 4)
            return null;
        
        // Transform to terrain coordinates
        var terrainCoords = roundabout.RingCoordinates
            .Select(c => _geometryProcessor.TransformToTerrainCoordinate(c, bbox, terrainSize))
            .ToList();
        
        // Crop to terrain bounds
        var croppedCoords = _geometryProcessor.CropLineToTerrain(terrainCoords, terrainSize);
        
        if (croppedCoords.Count < 4)
            return null;
        
        // Convert to meter coordinates
        var meterCoords = croppedCoords
            .Select(p => new Vector2(p.X * metersPerPixel, p.Y * metersPerPixel))
            .ToList();
        
        // Ensure ring is closed
        if (Vector2.Distance(meterCoords[0], meterCoords[^1]) > 0.1f)
        {
            meterCoords.Add(meterCoords[0]);
        }
        
        // Remove duplicate consecutive points
        var uniqueCoords = RemoveDuplicateConsecutivePoints(meterCoords, 0.01f);
        
        if (uniqueCoords.Count < 4)
            return null;
        
        try
        {
            // For roundabouts, we always use SmoothInterpolated to get nice circular curves
            return new RoadSpline(uniqueCoords, SplineInterpolationType.SmoothInterpolated);
        }
        catch (Exception ex)
        {
            TerrainLogger.Warning($"Failed to create roundabout ring spline: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Removes consecutive duplicate points from a path.
    /// </summary>
    private List<Vector2> RemoveDuplicateConsecutivePoints(List<Vector2> points, float tolerance)
    {
        if (points.Count < 2)
            return points;
        
        var result = new List<Vector2> { points[0] };
        var toleranceSquared = tolerance * tolerance;
        
        for (int i = 1; i < points.Count; i++)
        {
            if (Vector2.DistanceSquared(result[^1], points[i]) > toleranceSquared)
            {
                result.Add(points[i]);
            }
        }
        
        return result;
    }
}
```

### Phase 4: Enhanced Junction Detection

#### 4.1 Create `RoundaboutJunction.cs`

```csharp
// BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoundaboutJunction.cs

namespace BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

/// <summary>
/// Represents a junction where a road connects to a roundabout ring.
/// Extends standard junction logic with roundabout-specific handling.
/// </summary>
public class RoundaboutJunction
{
    /// <summary>
    /// Reference to the parent NetworkJunction.
    /// </summary>
    public NetworkJunction ParentJunction { get; set; }
    
    /// <summary>
    /// The roundabout ring spline ID.
    /// </summary>
    public int RoundaboutSplineId { get; set; }
    
    /// <summary>
    /// The connecting road spline ID.
    /// </summary>
    public int ConnectingRoadSplineId { get; set; }
    
    /// <summary>
    /// Position where connection occurs on the roundabout ring (in meters).
    /// </summary>
    public Vector2 ConnectionPointMeters { get; set; }
    
    /// <summary>
    /// Distance along the roundabout ring spline where connection occurs.
    /// </summary>
    public float DistanceAlongRoundabout { get; set; }
    
    /// <summary>
    /// Angle around the roundabout center (0 = East, 90 = North).
    /// </summary>
    public float AngleDegrees { get; set; }
    
    /// <summary>
    /// Whether the connecting road is an entry, exit, or bidirectional.
    /// </summary>
    public RoundaboutConnectionDirection Direction { get; set; }
    
    /// <summary>
    /// Target elevation at this connection point (from roundabout ring).
    /// </summary>
    public float TargetElevation { get; set; }
}
```

#### 4.2 Update `NetworkJunctionDetector.cs` Integration

Add a new junction type to `JunctionType` enum:

```csharp
/// <summary>
/// Road connects to a roundabout ring.
/// The roundabout is continuous; the connecting road terminates.
/// </summary>
Roundabout
```

Add detection method for roundabout junctions (to be called from existing detection flow):

```csharp
/// <summary>
/// Detects junctions where roads connect to roundabout rings.
/// Called after roundabout ring splines are added to the network.
/// </summary>
public void DetectRoundaboutJunctions(
    UnifiedRoadNetwork network,
    List<RoundaboutMerger.ProcessedRoundaboutInfo> roundaboutInfos,
    float detectionRadius)
{
    foreach (var roundaboutInfo in roundaboutInfos)
    {
        var roundaboutSpline = network.GetSplineById(roundaboutInfo.SplineId);
        if (roundaboutSpline == null)
            continue;
        
        // Find roads that have endpoints near the roundabout ring
        foreach (var spline in network.Splines)
        {
            if (spline.SplineId == roundaboutInfo.SplineId)
                continue; // Skip the roundabout itself
            
            // Check start endpoint
            var distToStart = DistanceToRing(spline.StartPoint, roundaboutInfo.CenterMeters, roundaboutInfo.RadiusMeters);
            if (distToStart < detectionRadius)
            {
                CreateRoundaboutJunction(network, roundaboutSpline, spline, isSplineStart: true, roundaboutInfo);
            }
            
            // Check end endpoint
            var distToEnd = DistanceToRing(spline.EndPoint, roundaboutInfo.CenterMeters, roundaboutInfo.RadiusMeters);
            if (distToEnd < detectionRadius)
            {
                CreateRoundaboutJunction(network, roundaboutSpline, spline, isSplineStart: false, roundaboutInfo);
            }
        }
    }
}

private float DistanceToRing(Vector2 point, Vector2 center, float radius)
{
    float distToCenter = Vector2.Distance(point, center);
    return Math.Abs(distToCenter - radius);
}

private void CreateRoundaboutJunction(
    UnifiedRoadNetwork network,
    ParameterizedRoadSpline roundaboutSpline,
    ParameterizedRoadSpline connectingSpline,
    bool isSplineStart,
    RoundaboutMerger.ProcessedRoundaboutInfo roundaboutInfo)
{
    var endpoint = isSplineStart ? connectingSpline.StartPoint : connectingSpline.EndPoint;
    var endpointCs = GetEndpointCrossSection(network, connectingSpline.SplineId, isSplineStart);
    
    if (endpointCs == null)
        return;
    
    // Find closest point on roundabout ring
    var ringCs = FindClosestCrossSectionOnRing(network, roundaboutSpline.SplineId, endpoint);
    if (ringCs == null)
        return;
    
    // Create junction
    var junction = new NetworkJunction
    {
        Position = (endpoint + ringCs.CenterPoint) / 2,
        Type = JunctionType.Roundabout
    };
    
    // Add continuous contributor (roundabout ring)
    junction.Contributors.Add(new JunctionContributor
    {
        CrossSection = ringCs,
        Spline = roundaboutSpline,
        IsSplineStart = false,
        IsSplineEnd = false // Ring is continuous
    });
    
    // Add terminating contributor (connecting road)
    junction.Contributors.Add(new JunctionContributor
    {
        CrossSection = endpointCs,
        Spline = connectingSpline,
        IsSplineStart = isSplineStart,
        IsSplineEnd = !isSplineStart
    });
    
    network.Junctions.Add(junction);
}
```

### Phase 5: Integration with OsmGeometryProcessor

#### 5.1 Update `OsmGeometryProcessor.ConvertLinesToSplines()`

Add roundabout detection AND road trimming before creating regular splines:

```csharp
/// <summary>
/// Converts line features to splines with roundabout handling.
/// This includes:
/// 1. Detecting roundabouts from junction=roundabout tags
/// 2. TRIMMING connecting roads that overlap with roundabout rings (CRITICAL!)
/// 3. Creating closed-loop splines for roundabout rings
/// 4. Creating normal splines for trimmed connecting roads
/// </summary>
public List<RoadSpline> ConvertLinesToSplinesWithRoundabouts(
    List<OsmFeature> lineFeatures,
    OsmQueryResult fullQueryResult, // Need full result for roundabout detection
    GeoBoundingBox bbox,
    int terrainSize,
    float metersPerPixel,
    SplineInterpolationType interpolationType,
    out List<OsmRoundabout> detectedRoundabouts,
    out HashSet<long> roundaboutFeatureIds,
    out HashSet<long> deletedFeatureIds)
{
    // Step 1: Detect roundabouts
    var detector = new RoundaboutDetector();
    detectedRoundabouts = detector.DetectRoundabouts(fullQueryResult);
    
    // Step 2: CRITICAL - Trim connecting roads that overlap with roundabouts
    // This removes the quirky high-angle segments that follow the roundabout ring
    var trimmer = new ConnectingRoadTrimmer();
    deletedFeatureIds = trimmer.TrimConnectingRoads(detectedRoundabouts, lineFeatures);
    
    // Step 3: Process roundabout rings into closed-loop splines
    var merger = new RoundaboutMerger(this);
    var roundaboutResult = merger.ProcessRoundabouts(
        detectedRoundabouts,
        lineFeatures,
        bbox,
        terrainSize,
        metersPerPixel,
        interpolationType);
    
    roundaboutFeatureIds = roundaboutResult.ProcessedFeatureIds;
    
    // Step 4: Filter out roundabout features AND deleted features from regular processing
    var regularFeatures = lineFeatures
        .Where(f => !roundaboutFeatureIds.Contains(f.Id))
        .Where(f => !deletedFeatureIds.Contains(f.Id))
        .Where(f => f.Coordinates.Count >= 2) // Ensure still valid after trimming
        .ToList();
    
    // Step 5: Process regular (now trimmed) roads
    var regularSplines = ConvertLinesToSplines(
        regularFeatures,
        bbox,
        terrainSize,
        metersPerPixel,
        interpolationType);
    
    // Step 6: Combine results
    var allSplines = new List<RoadSpline>();
    allSplines.AddRange(roundaboutResult.RoundaboutSplines);
    allSplines.AddRange(regularSplines);
    
    TerrainLogger.Info($"ConvertLinesToSplinesWithRoundabouts: " +
        $"{detectedRoundabouts.Count} roundabout(s), " +
        $"{roundaboutResult.RoundaboutSplines.Count} ring spline(s), " +
        $"{regularSplines.Count} road spline(s), " +
        $"{deletedFeatureIds.Count} road(s) deleted");
    
    return allSplines;
}
```

### Phase 6: Elevation Harmonization for Roundabouts

#### 6.1 Add Roundabout-Specific Harmonization

The roundabout ring should have consistent elevation around its circumference. Update the elevation harmonization to:

1. Calculate average elevation around the roundabout ring
2. Apply uniform elevation to all ring cross-sections
3. Blend connecting roads toward this elevation at their junction points

```csharp
/// <summary>
/// Harmonizes elevation for roundabout junctions.
/// The roundabout ring elevation is calculated as the weighted average of:
/// - Terrain elevation at the ring position
/// - Connecting road elevations (weighted by road priority)
/// </summary>
public void HarmonizeRoundaboutElevations(
    UnifiedRoadNetwork network,
    float[,] heightMap,
    int terrainSize,
    float metersPerPixel,
    float terrainBaseHeight)
{
    var roundaboutJunctions = network.Junctions
        .Where(j => j.Type == JunctionType.Roundabout)
        .GroupBy(j => j.Contributors.First(c => c.IsContinuous).Spline.SplineId)
        .ToList();
    
    foreach (var group in roundaboutJunctions)
    {
        var roundaboutSplineId = group.Key;
        var junctions = group.ToList();
        
        // Get all cross-sections of the roundabout ring
        var ringCrossSections = network.GetCrossSectionsForSpline(roundaboutSplineId).ToList();
        
        // Calculate average terrain elevation around the ring
        float sumElevation = 0;
        int count = 0;
        foreach (var cs in ringCrossSections)
        {
            var pixelX = (int)(cs.CenterPoint.X / metersPerPixel);
            var pixelY = (int)(cs.CenterPoint.Y / metersPerPixel);
            
            if (pixelX >= 0 && pixelX < terrainSize && pixelY >= 0 && pixelY < terrainSize)
            {
                sumElevation += heightMap[pixelY, pixelX] + terrainBaseHeight;
                count++;
            }
        }
        
        float ringElevation = count > 0 ? sumElevation / count : 0;
        
        // Apply uniform elevation to all ring cross-sections
        foreach (var cs in ringCrossSections)
        {
            cs.TargetElevation = ringElevation;
        }
        
        // Harmonize connecting roads
        foreach (var junction in junctions)
        {
            junction.HarmonizedElevation = ringElevation;
            
            // Apply elevation blending to terminating roads
            var terminatingRoads = junction.GetTerminatingRoads();
            foreach (var road in terminatingRoads)
            {
                ApplyJunctionBlend(network, road, ringElevation, blendDistance: 30f);
            }
        }
    }
}
```

## Testing Strategy

### Unit Tests

1. **RoundaboutDetector Tests**
   - Detect single-way roundabout
   - Detect multi-way split roundabout
   - Handle unclosed roundabout (should warn)
   - Detect connections from multiple roads

2. **ConnectingRoadTrimmer Tests (CRITICAL)**
   - Trim road that enters roundabout from outside (keep pre-entry portion)
   - Trim road that starts on roundabout and exits (keep post-exit portion)
   - Handle road that passes through roundabout (keep longer portion)
   - Delete road entirely inside roundabout
   - Handle road with multiple nodes on ring (remove all overlapping nodes)
   - Preserve roads that don't touch roundabout
   - Edge case: road tangent to roundabout (should not trim)

3. **RoundaboutMerger Tests**
   - Create closed ring spline
   - Handle roundabouts at terrain edge (cropping)
   - Convert connections to junction hints

4. **Junction Detection Tests**
   - Detect roundabout junctions at trim points
   - Handle multiple roads connecting to same roundabout
   - Elevation harmonization across ring

### Integration Tests

1. Load real OSM data with known roundabouts
2. Verify roundabout rings are properly merged
3. Verify connecting roads form proper junctions
4. Verify elevation is consistent around roundabout

### Visual Debugging

The `RoundaboutDebugImageExporter` class provides comprehensive visual debugging for roundabout processing:

#### Debug Image Contents

The debug image (`{materialName}_roundabout_debug.png`) shows:

| Layer | Color | Description |
|-------|-------|-------------|
| Original road paths | Gray (semi-transparent) | Shows road geometry BEFORE trimming for comparison |
| Trimmed/deleted portions | Red (bright) | Highlights segments that were cut from roads |
| Connecting roads (after trimming) | Cyan | Shows the final road splines that connect to roundabouts |
| Roundabout rings | Yellow (thick) | The merged roundabout ring splines |
| Connection points | Green circles with white outline | Where roads meet the roundabout ring |
| Roundabout centers | Magenta crosshairs | Center point of each roundabout |

#### Configuration

The debug image export is controlled by the `ExportRoundaboutDebugImage` setting in `JunctionHarmonizationParameters`:

```csharp
/// <summary>
/// Export debug image showing roundabout detection and road trimming.
/// Default: true (always export debug images to MT_TerrainGeneration folder)
/// </summary>
public bool ExportRoundaboutDebugImage { get; set; } = true;
```

#### Output Location

Debug images are exported to: `{WorkingDirectory}/MT_TerrainGeneration/{MaterialName}_roundabout_debug.png`

#### Usage

The debug image is automatically generated during terrain generation when:
1. `EnableRoundaboutDetection` is `true`
2. `ExportRoundaboutDebugImage` is `true`
3. At least one roundabout is detected in the OSM data

The `RoundaboutDebugImageExporter.PreTrimSnapshot` class captures road geometry before trimming, enabling the before/after comparison visualization.

## Implementation Order

1. **Week 1: Models and Detection**
   - Create `OsmRoundabout.cs`
   - Create `RoundaboutDetector.cs`
   - Add unit tests for detection

2. **Week 2: Road Trimming (CRITICAL)**
   - Create `ConnectingRoadTrimmer.cs`
   - Implement trimming logic for roads that overlap roundabouts
   - Test with various roundabout configurations
   - Visual debug output showing trimmed portions

3. **Week 3: Spline Creation**
   - Create `RoundaboutMerger.cs`
   - Integration with `OsmGeometryProcessor`
   - Debug visualization for roundabout rings

4. **Week 4: Junction Detection**
   - Create `RoundaboutJunction.cs`
   - Update `NetworkJunctionDetector.cs`
   - Add `JunctionType.Roundabout`

5. **Week 5: Elevation Harmonization**
   - Uniform ring elevation
   - Connecting road blending at trim points
   - Testing and refinement

## Configuration Options

Add to `JunctionHarmonizationParameters.cs`:

```csharp
/// <summary>
/// When true, automatically detect and handle roundabouts from OSM data.
/// Roundabout segments are merged into single ring splines, and connecting
/// roads are treated as T-junctions.
/// Default: true
/// </summary>
public bool EnableRoundaboutDetection { get; set; } = true;

/// <summary>
/// When true, automatically trim connecting roads that overlap with roundabout rings.
/// This removes the high-angle segments that create quirky splines and elevation spikes.
/// STRONGLY RECOMMENDED to keep enabled.
/// Default: true
/// </summary>
public bool EnableRoundaboutRoadTrimming { get; set; } = true;

/// <summary>
/// Detection radius for roundabout connections (in meters).
/// Roads within this distance of a roundabout ring are considered connected.
/// Default: 10.0
/// </summary>
public float RoundaboutConnectionRadiusMeters { get; set; } = 10.0f;

/// <summary>
/// Tolerance for determining if a road point is "on" the roundabout ring (in meters).
/// Points within this distance of the ring radius are considered overlapping.
/// Default: 2.0
/// </summary>
public float RoundaboutOverlapToleranceMeters { get; set; } = 2.0f;

/// <summary>
/// When true, force uniform elevation around roundabout rings.
/// The elevation is calculated as the weighted average of terrain elevation
/// at the ring position and connecting road elevations. All connecting roads
/// are blended toward this single elevation, which may cause artificial
/// bumps or dips for roads that naturally approach at different elevations.
///
/// When false, allow gradual elevation changes around the ring following
/// the natural terrain. Each connecting road blends toward the local ring
/// elevation at its specific connection point, avoiding artificial elevation
/// changes. This is more appropriate for roundabouts on sloped terrain.
///
/// Default: false (natural terrain-following behavior)
/// </summary>
public bool ForceUniformRoundaboutElevation { get; set; } = false;
```

## Future Enhancements

1. **Mini-roundabouts** - Handle smaller roundabouts with different geometry
2. **Traffic islands** - Detect and handle central islands
3. **Multi-lane roundabouts** - Support variable lane widths
4. **Spiral roundabouts** - Handle non-circular designs
5. **Turbo roundabouts** - Specialized detection for turbo designs

## References

- [OSM Wiki: junction=roundabout](https://wiki.openstreetmap.org/wiki/Tag:junction%3Droundabout)
- [Overpass API: Counting roundabouts](https://dev.overpass-api.de/blog/counting_roundabouts.html)
- [OSM Multipolygon Relations](https://wiki.openstreetmap.org/wiki/Relation:multipolygon)
