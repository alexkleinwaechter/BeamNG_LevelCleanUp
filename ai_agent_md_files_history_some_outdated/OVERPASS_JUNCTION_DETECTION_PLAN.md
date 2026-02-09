# Overpass API Junction Detection Implementation Plan

## Problem Statement

The current junction detection in `NetworkJunctionDetector.cs` relies on **geometric analysis** of road splines to find where roads meet. This approach has limitations:

1. **Missed junctions (~15-25%)**: Based on visual analysis, a significant portion of road intersections are not detected, particularly:
   - Low-angle crossings where roads meet at shallow angles
   - Mid-spline crossings with distance just beyond detection radius
   - Complex interchange areas with multiple merge/diverge points

2. **Detection relies on local geometry**: The algorithm can only see cross-section proximity, not semantic road network topology.

3. **No knowledge of junction types**: OSM explicitly tags junction types (motorway exits, traffic signals, stop signs) which the current detector cannot distinguish.

## Proposed Solution: Overpass API Junction Queries

OpenStreetMap (OSM) has **explicitly tagged junctions** that we can query via Overpass API. This approach is already used successfully for **roundabout detection** in `RoundaboutDetector.cs`.

### Benefits

1. **Higher accuracy**: OSM junction tags are curated by mappers who understand the road network topology
2. **Junction type information**: We get semantic labels (motorway_junction, traffic_signals, stop, etc.)
3. **Consistent with existing roundabout flow**: Reuses the proven `OverpassApiService` infrastructure
4. **Handles edge cases**: OSM mappers have already solved complex interchange geometry

## Overpass Query Patterns

### 1. Explicitly Tagged Junctions

These are nodes with specific junction tags:

```overpass
[out:json][timeout:25];
(
  // Motorway exits (named/numbered interchanges)
  node["highway"="motorway_junction"]({{bbox}});
  
  // Traffic signals at intersections
  node["highway"="traffic_signals"]({{bbox}});
  
  // Stop signs
  node["highway"="stop"]({{bbox}});
  
  // Give way / yield signs
  node["highway"="give_way"]({{bbox}});
  
  // Mini roundabouts (single node, not full ring)
  node["highway"="mini_roundabout"]({{bbox}});
  
  // Turning circles at road ends
  node["highway"="turning_circle"]({{bbox}});
);
out body;
```

### 2. Geometric Intersections (Shared Nodes)

Where two or more highway ways share a node:

```overpass
[out:json][timeout:25];
// Get all highway ways in the area
way["highway"~"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|service|track)$"]({{bbox}})->.all_highways;

// Find nodes that belong to at least 2 of those ways (T-junctions and crossroads)
node(way_cnt.all_highways:2-);

out body;
```

### 3. Junction Type Filtering

For specific junction types (e.g., only "real" crossroads, not name changes):

```overpass
[out:json][timeout:25];
// Find nodes shared by 3 or more highway segments (T-junctions, crossroads, complex)
way["highway"~"^(primary|secondary|tertiary|residential)$"]({{bbox}})->.roads;
node(way_cnt.roads:3-);
out body;
```

## Implementation Architecture

### Phase 1: New Service - `OsmJunctionQueryService`

Create a new service that queries Overpass for junction data:

```
BeamNgTerrainPoc/Terrain/Osm/Services/OsmJunctionQueryService.cs
```

**Responsibilities:**
- Query explicit junction nodes (motorway_junction, traffic_signals, etc.)
- Query geometric intersections (way_cnt filter)
- Transform geo-coordinates to world coordinates (meters)
- Cache results alongside existing OSM data

**Interface:**
```csharp
public interface IOsmJunctionQueryService
{
    /// <summary>
    /// Queries all junction nodes in the bounding box from OSM.
    /// </summary>
    Task<OsmJunctionQueryResult> QueryJunctionsAsync(
        GeoBoundingBox bbox,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Queries junctions with specific type filtering.
    /// </summary>
    Task<OsmJunctionQueryResult> QueryJunctionsByTypeAsync(
        GeoBoundingBox bbox,
        OsmJunctionType[] types,
        CancellationToken cancellationToken = default);
}
```

### Phase 2: New Model - `OsmJunction`

```
BeamNgTerrainPoc/Terrain/Osm/Models/OsmJunction.cs
```

```csharp
public class OsmJunction
{
    /// <summary>OSM node ID for reference.</summary>
    public long OsmNodeId { get; set; }
    
    /// <summary>Geographic coordinates (lat/lon).</summary>
    public GeoCoordinate Location { get; set; }
    
    /// <summary>World position in meters (after coordinate transformation).</summary>
    public Vector2 PositionMeters { get; set; }
    
    /// <summary>Classified junction type from OSM tags.</summary>
    public OsmJunctionType Type { get; set; }
    
    /// <summary>Name of the junction (if tagged, e.g., motorway exit name).</summary>
    public string? Name { get; set; }
    
    /// <summary>Reference number (e.g., exit number for motorway_junction).</summary>
    public string? Reference { get; set; }
    
    /// <summary>Number of roads meeting at this junction (from way_cnt query).</summary>
    public int ConnectedRoadCount { get; set; }
    
    /// <summary>Raw OSM tags for additional information.</summary>
    public Dictionary<string, string> Tags { get; set; } = new();
}

public enum OsmJunctionType
{
    Unknown,
    MotorwayJunction,    // highway=motorway_junction
    TrafficSignals,      // highway=traffic_signals
    Stop,                // highway=stop
    GiveWay,             // highway=give_way
    MiniRoundabout,      // highway=mini_roundabout
    TurningCircle,       // highway=turning_circle
    Crossing,            // highway=crossing (pedestrian)
    
    // Geometric detection (from way_cnt)
    TJunction,           // 3 ways meeting
    CrossRoads,          // 4 ways meeting
    ComplexJunction      // 5+ ways meeting
}
```

### Phase 3: Query Result Model

```
BeamNgTerrainPoc/Terrain/Osm/Models/OsmJunctionQueryResult.cs
```

```csharp
public class OsmJunctionQueryResult
{
    public GeoBoundingBox BoundingBox { get; set; }
    public List<OsmJunction> Junctions { get; set; } = new();
    public DateTime QueryTime { get; set; }
    public bool IsFromCache { get; set; }
    
    // Statistics
    public int ExplicitJunctionCount => Junctions.Count(j => 
        j.Type != OsmJunctionType.TJunction && 
        j.Type != OsmJunctionType.CrossRoads && 
        j.Type != OsmJunctionType.ComplexJunction);
    
    public int GeometricJunctionCount => Junctions.Count - ExplicitJunctionCount;
}
```

### Phase 4: Integration with `NetworkJunctionDetector`

Modified `NetworkJunctionDetector.cs` to optionally use OSM junction data:

```csharp
public class NetworkJunctionDetector
{
    /// <summary>
    /// Detects junctions using both geometric analysis AND OSM data.
    /// OSM junctions serve as "hints" that boost detection confidence.
    /// </summary>
    public List<NetworkJunction> DetectJunctionsWithOsm(
        UnifiedRoadNetwork network,
        OsmJunctionQueryResult osmJunctions,
        float globalDetectionRadius,
        float osmMatchRadius = 20.0f)
    {
        // Step 1: Standard geometric detection (existing code)
        var geometricJunctions = DetectJunctions(network, globalDetectionRadius);
        
        // Step 2: Match OSM junctions to geometric junctions
        var unmatchedOsm = MatchOsmJunctionsToGeometric(
            geometricJunctions, osmJunctions.Junctions, osmMatchRadius);
        
        // Step 3: Create NEW junctions from unmatched OSM data
        var newJunctions = CreateJunctionsFromUnmatchedOsm(
            network, unmatchedOsm, geometricJunctions, globalDetectionRadius);
        
        // Step 4: Update junction types from OSM semantic info
        UpdateJunctionTypesFromOsm(geometricJunctions);
        
        // Merge and return
        geometricJunctions.AddRange(newJunctions);
        return geometricJunctions;
    }
}
```

The `NetworkJunction` model now includes OSM hint properties:

```csharp
public class NetworkJunction
{
    // ... existing properties ...
    
    /// <summary>
    /// OSM junction hint that was matched to this junction.
    /// Provides additional semantic information from OpenStreetMap.
    /// </summary>
    public OsmJunction? OsmHint { get; set; }
    
    /// <summary>
    /// Whether this junction was created from unmatched OSM data.
    /// </summary>
    public bool IsOsmSourced { get; set; }
    
    /// <summary>
    /// Distance from the OSM junction hint to this junction's position.
    /// </summary>
    public float? OsmMatchDistance { get; set; }
}
```

### Phase 5: Caching Strategy

OSM junction data should be cached alongside existing OSM road data:

```
BeamNgTerrainPoc/Terrain/Osm/Services/OsmJunctionCacheService.cs
```

**Cache structure:**
- Cache file: `{cacheDir}/osm_junctions_{bbox_hash}.json`
- Same invalidation rules as road data
- Combine queries when possible to reduce API calls

### Phase 6: Automatic Integration (No UI Toggle)

OSM junction detection is automatically enabled when using OSM roads. The system:

1. Checks if `GeoBoundingBox` is available (indicates OSM/GeoTIFF source)
2. If available, automatically queries OSM junctions via `OsmJunctionQueryService`
3. Uses `DetectJunctionsWithOsm()` to enhance junction detection
4. Falls back to geometric-only detection if OSM query fails

**No UI toggle is provided** because:
- OSM junction hints improve accuracy with no downside
- The feature is designed to be seamless and automatic
- Users don't need to understand the technical details

The integration happens in `UnifiedRoadSmoother.SmoothAllRoads()` which receives the 
`GeoBoundingBox` parameter from `TerrainCreationParameters`.

## Query Optimization

### Combined Query (Recommended)

To minimize API calls, combine all junction queries into one:

```overpass
[out:json][timeout:60];
(
  // Explicit junction tags
  node["highway"="motorway_junction"]({{bbox}});
  node["highway"="traffic_signals"]({{bbox}});
  node["highway"="stop"]({{bbox}});
  node["highway"="give_way"]({{bbox}});
  node["highway"="mini_roundabout"]({{bbox}});
  node["highway"="turning_circle"]({{bbox}});
  
  // Geometric intersections (shared nodes)
  way["highway"~"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|service)$"]({{bbox}})->.highways;
  node(way_cnt.highways:3-);  // T-junctions and above
);
out body;
```

### Performance Considerations

1. **Query timeout**: Set appropriate timeout (60-90 seconds for large areas)
2. **Bounding box size**: Split very large areas into sub-queries
3. **Rate limiting**: Respect Overpass API rate limits (already implemented in `OverpassApiService`)
4. **Caching**: Cache aggressively - junction positions rarely change

## Algorithm: Matching OSM Junctions to Road Network

```
ALGORITHM: MatchOsmJunctionsToGeometric

INPUT:
  - osmJunctions: List<OsmJunction>
  - network: UnifiedRoadNetwork
  - geometricJunctions: List<NetworkJunction>
  - matchRadius: float (default: 20 meters)

OUTPUT:
  - Updated geometricJunctions with OsmJunctionHint property
  - List of unmatched OSM junctions for new junction creation

1. Build spatial index of geometric junctions by position
2. For each osmJunction:
   a. Query geometric junctions within matchRadius
   b. If found:
      - Link osmJunction to closest geometric junction
      - Update geometric junction's OsmHint property
      - Mark as matched
   c. If not found:
      - Add to unmatchedOsmJunctions list
3. Return unmatchedOsmJunctions
```

## Algorithm: Create Junctions from Unmatched OSM Data

```
ALGORITHM: CreateJunctionsFromUnmatchedOsm

INPUT:
  - unmatchedOsmJunctions: List<OsmJunction>
  - network: UnifiedRoadNetwork
  - searchRadius: float

OUTPUT:
  - New NetworkJunction objects

1. For each unmatched OSM junction:
   a. Find cross-sections from ANY spline within searchRadius
   b. If found cross-sections from 2+ different splines:
      - Create new NetworkJunction at OSM position
      - Add contributors from each spline (closest cross-section)
      - Set junction type based on OSM type mapping
      - Mark as OsmSourced = true
   c. If found cross-sections from only 1 spline:
      - This might be a mid-spline feature (speed bump, crossing)
      - Create junction if near spline endpoints
   d. If no nearby cross-sections:
      - Log warning: OSM junction not matched to any road
```

## OSM Junction Type Mapping

### Overview

OSM junction types (`OsmJunctionType`) must be converted to network junction types (`JunctionType`) 
so that the harmonization logic in `NetworkJunctionHarmonizer` can process them correctly.

The mapping is implemented in `NetworkJunctionDetector.MapOsmTypeToJunctionType()`.

### Mapping Table

| OSM Tag | `OsmJunctionType` | `JunctionType` | Harmonization Method | Notes |
|---------|-------------------|----------------|---------------------|-------|
| `highway=motorway_junction` | `MotorwayJunction` | `YJunction` (2 roads) or `TJunction` (3+ roads) | `ComputeMultiWayJunctionElevation()` or `ComputeTJunctionElevation()` | Highway ramp connections - typically T or Y shaped |
| `highway=traffic_signals` | `TrafficSignals` | Based on road count | Varies by geometry | Traffic control, not junction shape - uses road count |
| `highway=stop` | `Stop` | Based on road count | Varies by geometry | Traffic control, not junction shape - uses road count |
| `highway=give_way` | `GiveWay` | Based on road count | Varies by geometry | Traffic control, not junction shape - uses road count |
| `highway=mini_roundabout` | `MiniRoundabout` | `Roundabout` | `RoundaboutElevationHarmonizer` | Single-node roundabout, handled by roundabout system |
| `highway=turning_circle` | `TurningCircle` | `Endpoint` | `ComputeEndpointElevation()` | Road termination point |
| `highway=crossing` | `Crossing` | `Endpoint` | `ComputeEndpointElevation()` | Pedestrian crossing - mid-road feature, not road junction |
| `way_cnt=2` | `CrossRoads` | `CrossRoads` or `MidSplineCrossing` | `ComputeMidSplineCrossingElevation()` ? `CrossroadToTJunctionConverter` | Two roads sharing a node |
| `way_cnt=3` | `TJunction` | `TJunction` | `ComputeTJunctionElevation()` | Classic T-intersection |
| `way_cnt=4` | `CrossRoads` | `CrossRoads` | `ComputeMultiWayJunctionElevation()` | Four-way intersection |
| `way_cnt>=5` | `ComplexJunction` | `Complex` | `ComputeMultiWayJunctionElevation()` | Complex interchange |
| (unknown) | `Unknown` | Based on road count | Varies by geometry | Fallback to geometric heuristics |

### Road Count to Junction Type Mapping

When an OSM junction type doesn't have a direct mapping (e.g., traffic control features),
the system uses the number of connected roads to determine the junction geometry:

| Road Count | `JunctionType` | Description |
|------------|---------------|-------------|
| 1 | `Endpoint` | Dead end or road termination |
| 2 | `YJunction` | Two roads meeting (merge/diverge) |
| 3 | `TJunction` | Classic T-intersection |
| 4 | `CrossRoads` | Four-way intersection |
| 5+ | `Complex` | Complex interchange or roundabout entry |

### Special Cases

#### MidSplineCrossing Detection

OSM `CrossRoads` type (from `way_cnt:2-`) can represent two scenarios:

1. **Roads meet at endpoints** ? Standard junction (Y, T, or CrossRoads)
2. **Roads cross mid-spline** ? Both roads continue through ? `MidSplineCrossing`

The `UpdateJunctionTypesFromOsm()` method checks if 2+ contributors are "continuous" 
(neither `IsSplineStart` nor `IsSplineEnd`). If so, it converts to `MidSplineCrossing`,
which is then processed by `CrossroadToTJunctionConverter` to split into T-junctions.

#### Pedestrian Crossings

`highway=crossing` (pedestrian crossing) is mapped to `Endpoint` because:
- It's a mid-road feature, not an intersection between roads
- No elevation harmonization is needed with other roads
- Treating it as an endpoint prevents incorrect junction processing

#### Motorway Junctions

`highway=motorway_junction` uses road count to determine shape:
- **2 roads**: `YJunction` - Typical on/off ramp diverge/merge
- **3+ roads**: `TJunction` - Ramp connecting to main road

### Implementation Code

```csharp
private static JunctionType MapOsmTypeToJunctionType(OsmJunctionType osmType, int roadCount)
{
    return osmType switch
    {
        // Roundabout types
        OsmJunctionType.MiniRoundabout => JunctionType.Roundabout,
        
        // Endpoint types
        OsmJunctionType.TurningCircle => JunctionType.Endpoint,
        OsmJunctionType.Crossing => JunctionType.Endpoint,
        
        // Geometric junction types
        OsmJunctionType.ComplexJunction => JunctionType.Complex,
        OsmJunctionType.CrossRoads => JunctionType.CrossRoads,
        OsmJunctionType.TJunction => JunctionType.TJunction,
        
        // Motorway junctions - shape depends on road count
        OsmJunctionType.MotorwayJunction => roadCount <= 2 
            ? JunctionType.YJunction 
            : JunctionType.TJunction,
        
        // Traffic control - use road count geometry
        OsmJunctionType.TrafficSignals => MapRoadCountToJunctionType(roadCount),
        OsmJunctionType.Stop => MapRoadCountToJunctionType(roadCount),
        OsmJunctionType.GiveWay => MapRoadCountToJunctionType(roadCount),
        
        // Unknown - fallback
        OsmJunctionType.Unknown => MapRoadCountToJunctionType(roadCount),
        _ => MapRoadCountToJunctionType(roadCount)
    };
}
```

## File Structure

```
BeamNgTerrainPoc/
??? Terrain/
?   ??? Osm/
?       ??? Models/
?       ?   ??? OsmJunction.cs              ? COMPLETED
?       ?   ??? OsmJunctionQueryResult.cs   ? COMPLETED
?       ??? Services/
?       ?   ??? IOsmJunctionQueryService.cs ? COMPLETED
?       ?   ??? OsmJunctionQueryService.cs  ? COMPLETED
?       ?   ??? OsmJunctionCache.cs         ? COMPLETED
?       ?   ??? OsmCacheManager.cs          ? COMPLETED (unified cache management)
?       ??? Parsing/
?           ??? OsmJunctionParser.cs        (Integrated into OsmJunctionQueryService)
??? Docs/
?   ??? OVERPASS_JUNCTION_DETECTION_PLAN.md (THIS FILE)
```

## Implementation Priority

### Phase 1 (High Priority) ? COMPLETED
1. `OsmJunction.cs` - Model definitions
2. `OsmJunctionQueryResult.cs` - Query result container
3. `OsmJunctionQueryService.cs` - Overpass queries
4. `IOsmJunctionQueryService.cs` - Service interface
5. `OsmJunctionCache.cs` - Caching implementation

### Phase 2 (Medium Priority) ? COMPLETED
4. ~~`OsmJunctionParser.cs` - Parse Overpass JSON response~~ (Integrated into OsmJunctionQueryService)
5. Integration with `NetworkJunctionDetector.DetectJunctions()`
6. **Automatic integration in `UnifiedRoadSmoother`** - OSM junction hints are automatically
   used when `GeoBoundingBox` is available (no UI toggle needed)

### Phase 5: Caching Strategy ? COMPLETED
6. `OsmJunctionCache.cs` - Disk and memory caching with bbox containment optimization
7. `OsmCacheManager.cs` - Unified cache management for roads and junctions

### Phase 3 (Lower Priority) - PENDING
6. ~~UI toggle in `GenerateTerrain.razor`~~ ? Replaced with automatic integration
7. Debug visualization (junction type colors)
8. Performance optimization for large areas

### Phase 4: Bug Fixes ?? IN PROGRESS
Recent fixes applied to improve MidSplineCrossing detection:

| Fix | Status | Description |
|-----|--------|-------------|
| `way_link` ? `way_cnt` | ? Fixed | Corrected invalid Overpass QL syntax |
| `way_cnt:3-` ? `way_cnt:2-` | ? Fixed | Now detects simple 2-road crossings |
| Junction type classification | ? Fixed | CrossRoads ? MidSplineCrossing mapping |
| Cache invalidation needed | ?? User action | Old cached data uses wrong query |
| MidSplineCrossing still missed | ? Open | Some crossings not detected - see Known Issues |

## Expected Improvements

Based on the visual analysis showing ~15-25% missed junctions:

| Metric | Before | Expected After |
|--------|--------|----------------|
| Junction detection rate | ~75-85% | ~95%+ |
| False positives | Low | Very Low (OSM validated) |
| Junction type accuracy | Generic only | Specific types available |
| Motorway exit detection | Poor | Excellent (explicit tags) |
| Traffic signal detection | None | Excellent (explicit tags) |

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| OSM data outdated | Cache invalidation, fallback to geometric |
| Overpass API rate limits | Already handled in OverpassApiService |
| Coordinate transformation errors | Reuse existing GeoCoordinate infrastructure |
| Performance impact | Async queries, aggressive caching |
| Missing OSM coverage | Geometric detection as fallback (always runs) |

## Testing Strategy

1. **Unit tests**: Query parsing, coordinate transformation
2. **Integration tests**: Full query flow with mock responses
3. **Visual validation**: Export junction debug images showing:
   - OSM-sourced junctions (different color)
   - Matched vs unmatched junctions
   - Junction type visualization

## Related Files

- `RoundaboutDetector.cs` - Reference for OSM tag-based detection pattern
- `OverpassApiService.cs` - Existing Overpass query infrastructure
- `NetworkJunctionDetector.cs` - Target for integration
- `NetworkJunctionHarmonizer.cs` - Consumes junction data for elevation

---

## Known Issues & Remaining Work

### ?? MidSplineCrossing Detection Still Incomplete

**Status: IN PROGRESS**

Even with OSM junction hints, some mid-spline crossings are still not being detected. The following issues have been identified and partially addressed:

#### Bug Fixes Applied (2024-01)

1. **Fixed Overpass Query Syntax**
   - **Bug**: Query used `way_link` instead of `way_cnt`
   - **Fix**: Changed to correct Overpass QL syntax `way_cnt`
   - **File**: `OsmJunctionQueryService.cs`

2. **Changed `way_cnt:3-` to `way_cnt:2-`**
   - **Bug**: Original query only found nodes shared by 3+ ways, missing simple 2-road crossings
   - **Fix**: Changed to `way_cnt:2-` to catch ALL road intersections including simple crossroads
   - **Impact**: Should significantly increase detected crossroads
   - **File**: `OsmJunctionQueryService.cs`

3. **Updated Junction Type Classification**
   - OSM junctions from `way_cnt:2-` are now classified as `OsmJunctionType.CrossRoads`
   - These map to `JunctionType.MidSplineCrossing` when 2+ roads pass through continuously
   - The `CrossroadToTJunctionConverter` can then properly split them

#### Still Unresolved

**Problem**: Some crossings are still not being found even with the above fixes.

**Potential Causes**:

1. **Grade-separated crossings (overpasses/underpasses)**
   - OSM correctly does NOT have a shared node for these
   - Roads cross vertically but don't intersect at grade level
   - **Solution needed**: These should NOT be treated as crossroads - they're correct in OSM

2. **OSM data quality issues**
   - Some road crossings may have separate nodes for each road (bad mapping)
   - The `way_cnt` filter only finds SHARED nodes
   - **Workaround**: Geometric detection in `DetectMidSplineCrossings()` should catch these

3. **Coordinate transformation mismatch**
   - OSM junction positions are transformed from WGS84 to terrain meters
   - If transformation is slightly off, the junction position may not match nearby cross-sections
   - **Potential fix**: Increase `osmMatchRadius` parameter (currently 20m)

4. **Search radius too small**
   - `CreateJunctionsFromUnmatchedOsm()` uses `globalDetectionRadius` to find nearby cross-sections
   - For wide roads or roads at angles, the OSM junction node may be further from cross-section centers
   - **Potential fix**: Increase search radius or use road-width-aware searching

5. **Cross-sections too sparse**
   - If the cross-section interval is large (e.g., 5m), the nearest cross-section might be too far
   - The OSM junction position won't match any cross-section within the search radius
   - **Potential fix**: Sample additional cross-sections near OSM junction positions

#### Debugging Steps

To investigate why a specific crossing isn't being detected:

1. **Check the junction debug image** (`unified_junction_harmonization_debug.png`)
   - Look for the crossing location
   - Is there an OSM-sourced junction marker (dotted circle)?
   - What color is the junction marker?

2. **Check the log output**
   - Search for "OSM junction matching" to see match statistics
   - Search for "Skipping OSM junction" to see which junctions were skipped and why
   - Search for "Created MidSplineCrossing" to see which crossings were created

3. **Verify OSM data**
   - Use [OpenStreetMap.org](https://www.openstreetmap.org) to check if the crossing has a shared node
   - If it's an overpass/underpass, it correctly won't have a shared node

4. **Increase detection radius**
   - Try increasing `JunctionDetectionRadiusMeters` from 10m to 15-20m
   - This helps catch crossings where roads meet at oblique angles

#### Next Steps

1. ~~**Clear the OSM cache** and re-run to test the `way_cnt:2-` fix~~ — DONE
2. ~~**Analyze which crossings are still missed** and categorize by cause~~ — DONE (see bug fix below)
3. ~~**Consider hybrid approach**: Use OSM hints to "guide" geometric detection to specific locations~~ — **IMPLEMENTED**
4. **Add diagnostic logging** to track exactly why specific OSM junctions fail to create network junctions

#### Critical Bug Fixed: OSM Junctions Were Being Overwritten

**Problem discovered**: The hybrid approach WAS implemented in `DetectJunctionsWithOsm()`, but the
`NetworkJunctionHarmonizer.HarmonizeNetwork()` method was **always re-running** junction detection
via `_detector.DetectJunctions()`, even when `DetectJunctionsWithOsm()` had already populated
`network.Junctions`. This caused all OSM-enhanced junctions to be **lost**.

**Fix applied**: Modified `HarmonizeNetwork()` to check if junctions have already been detected
(by looking for non-roundabout junctions in `network.Junctions`). If pre-detected junctions exist,
they are now preserved and used for harmonization. Detection only runs if no regular junctions
are present yet.

**The hybrid approach is now working**: OSM junction hints correctly flow through the pipeline:
1. `UnifiedRoadSmoother.SmoothAllRoads()` calls `DetectJunctionsWithOsm()` ? Creates OSM-enhanced junctions
2. `HarmonizeNetwork()` now **preserves** those junctions instead of overwriting them
3. `CrossroadToTJunctionConverter` processes `MidSplineCrossing` junctions (including OSM-sourced ones)
4. Elevation harmonization correctly uses OSM junction positions and types

#### Critical Bug Fixed: Duplicate Junction Creation from OSM

**Problem discovered**: When geometric detection found a junction (e.g., TJunction at position A) and 
OSM had a nearby junction (e.g., CrossRoads at position B), the code was creating BOTH junctions if 
the positions were slightly different, resulting in:
- **Overlapping harmonization zones**: Each junction calculates its own `HarmonizedElevation` and 
  propagates constraints within its blend distance
- **Elevation conflicts**: The two junctions pull nearby cross-sections in different directions
- **Bumps/artifacts**: At the boundary where the two blend zones meet, there's a discontinuity

**Root cause**: The proximity threshold in `CreateJunctionsFromUnmatchedOsm()` was too small.
OSM node positions can differ from geometric junction centroids by several meters because:
- OSM places nodes at exact road intersections in the map data
- Geometric detection calculates junction centroids from cross-section endpoint clusters
- These positions can be 5-15 meters apart for the same junction

**Fix applied**: Use 1.5x the search radius as the proximity threshold:
```csharp
// NEW (fixed): Use 1.5x search radius to account for position differences
var proximityThreshold = searchRadius * 1.5f;
var closestExistingDist = existingPositions.Select(p => Vector2.Distance(p, osmPos)).Min();
if (closestExistingDist < proximityThreshold)
{
    // Skip - too close to existing junction
}
```

This ensures OSM junctions are ONLY used to create new network junctions when there is 
**NO existing geometric junction within 1.5x the detection radius**, accounting for the 
positional offset between OSM data and geometric detection.

---

## See Also

- [Crossroad to T-Junction Converter](CROSSROAD_TO_TJUNCTION_CONVERTER.md)
- [Junction Surface Constraint Implementation Plan](JUNCTION_SURFACE_CONSTRAINT_IMPLEMENTATION_PLAN.md)
- [Road Elevation Smoothing Documentation](../ROAD_ELEVATION_SMOOTHING_DOCUMENTATION.md)
