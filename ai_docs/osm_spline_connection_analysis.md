# OSM Spline Connection Analysis & Implementation Plan

## Document Purpose

This document analyzes how OSM way features are currently connected into longer splines during terrain generation road smoothing, and proposes an implementation plan to adopt OSM's own connectivity rules (shared nodes, name tags, route relations) for more accurate road assembly.

> **Scope**: This document covers OSM-sourced splines only. PNG-sourced splines use skeleton extraction and a different merging strategy (`MergeBrokenCurves` in `UnifiedRoadNetworkBuilder`) that should remain unchanged.

---

## 1. As-Is Situation: Current OSM Spline Connection Pipeline

### 1.1 High-Level Data Flow

```
Overpass API Query
    ??> OsmGeoJsonParser.Parse()             [Parse JSON ? OsmFeature objects]
        ??> OsmFeatureSelection                [User selects features per material]
            ??> OsmGeometryProcessor
                ??> ConvertLinesToSplines()                     [Simple path]
                ??> ConvertLinesToSplinesWithRoundabouts()      [Roundabout-aware path]
                    ??> RoundaboutDetector.DetectRoundabouts()
                    ??> ConnectingRoadTrimmer.TrimConnectingRoads()
                    ??> RoundaboutMerger.ProcessRoundabouts()
                    ??> ConvertLinesToSplines()  [for regular roads]
                        ??> ConnectAdjacentPaths()             [*** THE KEY METHOD ***]
                            ??> RoadSpline objects (pre-built splines)
                                ??> RoadSmoothingParameters.PreBuiltSplines
                                    ??> UnifiedRoadNetworkBuilder.BuildNetwork()
                                        ??> ParameterizedRoadSpline objects in UnifiedRoadNetwork
```

### 1.2 Overpass API Query

**File**: `OverpassApiService.BuildAllFeaturesQuery()` (line ~296)

The query fetches:
- `way["highway"]` ó all road ways
- `relation["type"="route"]["route"="road"]` ó route relations (e.g., named highways like "A1")

Output format: `out geom;` ó returns geometry inline (lat/lon per point) instead of requiring node resolution.

**What the response contains per way**:
```json
{
  "type": "way",
  "id": 123456789,
  "nodes": [111, 222, 333, 444],       // ? Node IDs (CURRENTLY IGNORED)
  "geometry": [                          // ? Only this is parsed
    {"lat": 51.123, "lon": 7.456},
    {"lat": 51.124, "lon": 7.457},
    ...
  ],
  "tags": {"highway": "primary", "name": "Main Street", "ref": "B51"}
}
```

### 1.3 Parsing (OsmGeoJsonParser)

**File**: `OsmGeoJsonParser.ParseElement()` (line ~69)

**What is parsed:**
- Way ID (`id`) ? stored as `OsmFeature.Id`
- Tags ? stored as `OsmFeature.Tags` dictionary (includes `name`, `ref`, `highway`, etc.)
- Geometry (`geometry` array) ? stored as `OsmFeature.Coordinates` (list of `GeoCoordinate`)

**What is NOT parsed:**
- **Node IDs** (`nodes` array) ó completely ignored
- Node-level metadata ó not available via `out geom` anyway

**For relations** (`ParseRelationMembers`, line ~158):
- Extracts member way geometries
- Groups by role (`outer`/`inner`)
- Assembles into rings using endpoint coordinate matching
- **Route relations** (`type=route, route=road`) are processed but the member roles (`forward`, `backward`, empty) are all treated as `outer` ó semantically incorrect for linear route assembly

### 1.4 Feature Selection & Material Assignment

**File**: `TerrainGenerationOrchestrator.ProcessOsmRoadMaterialAsync()` (line ~513)

Users select OSM features per material via `OsmFeatureSelection`. The orchestrator:
1. Filters selected features by geometry type (LineString for roads)
2. Builds `RoadSmoothingParameters` from material settings
3. Calls `ConvertLinesToSplines()` or `ConvertLinesToSplinesWithRoundabouts()`

### 1.5 Coordinate Transformation & Path Preparation

**File**: `OsmGeometryProcessor.ConvertLinesToSplines()` (line ~711)

Each OSM way feature goes through:
1. **Transform** to terrain-space coordinates (WGS84 ? pixel ? meters)
2. **Crop** to terrain bounds via `CropLineToTerrain()`
3. **Convert** from pixel to meter coordinates
4. **Remove duplicate** consecutive points (tolerance: 0.01m = 1cm)
5. **Separate** into structure paths (bridges/tunnels) and regular paths

### 1.6 Path Connection ó The Core Logic ??

**File**: `OsmGeometryProcessor.ConnectAdjacentPaths()` (line ~913)

This is the **sole mechanism** for connecting OSM way segments into longer splines:

```csharp
private List<List<Vector2>> ConnectAdjacentPaths(List<List<Vector2>> paths, float tolerance)
```

**Current algorithm:**
1. Copy all paths into a working list
2. Iteratively scan all path pairs (O(n≤) per iteration)
3. For each pair, check **4 endpoint combinations**:
   - path1.End ? path2.Start
   - path1.End ? path2.End
   - path1.Start ? path2.End
   - path1.Start ? path2.Start
4. If `Vector2.DistanceSquared(endpoint1, endpoint2) <= tolerance≤`, merge the paths
5. Repeat until no more merges possible (safety limit: 1000 iterations)

**Default tolerance**: `endpointJoinToleranceMeters = 1.0f` (configurable, set in orchestrator)

### 1.7 Current Connection Rules Summary

| Rule | Implemented | Details |
|------|:-----------:|---------|
| Endpoint proximity (?1m) | ? | The only rule. Distance-based in meter-space after coordinate transformation. |
| Shared OSM node IDs | ? | Node IDs not parsed from Overpass response. |
| Same road name (`name=*`) | ? | Tags are parsed but not used in connection logic. |
| Same road reference (`ref=*`) | ? | Available in tags but not considered. |
| Same highway type | ? | `highway=*` tag not used for connection decisions. |
| Route relation membership | ? | `route=road` relations queried but not used for way ordering/connection. |
| Direction awareness | ? | Paths can be reversed freely (all 4 endpoint combos tried). |
| Topology (shared nodes) | ? | Purely geometric, not topological. |

### 1.8 Where Merging Does NOT Apply

The following cases bypass `ConnectAdjacentPaths`:

- **Bridge/tunnel splines**: When `excludeBridges`/`excludeTunnels` is true, structure paths are kept separate and never merged with regular paths.
- **Roundabout ring splines**: Assembled separately by `RoundaboutMerger` using coordinate endpoint matching on the original geo-coordinates (similar principle but applied before coordinate transformation).
- **Cross-material connections**: Each material's OSM features are processed independently. Ways from different materials are never merged together.

### 1.9 Consequences of Current Approach

**Works well when:**
- OSM data is clean and ways share exact endpoint coordinates (common in well-mapped areas)
- The 1m tolerance accommodates minor floating-point drift from coordinate transformation
- All ways of the same road type are assigned to the same material

**Breaks or produces suboptimal results when:**
- Two ways of different road types share a node (e.g., `primary` and `residential`) ó these get incorrectly merged into one long spline
- A T-junction has three ways meeting at a shared node ó only two will merge, creating inconsistent results depending on iteration order
- Coordinate transformation introduces drift >1m (rare but possible with GDAL reprojection)
- Ways that are part of the same named road but are split at a feature boundary (bridge segment, admin boundary) may not reconnect if their transformed endpoints drift

---

## 2. How OSM Defines Road Connectivity

### 2.1 Shared Nodes (Topology)

OSM's primary connectivity mechanism. Two ways are connected if they share a common **node** (point with a unique ID). This is binary ó either they share a node or they don't.

- **Intersection**: Two roads cross ? they share a node at the crossing point
- **Continuation**: One road is split into segments ? adjacent segments share their boundary node
- **No connection**: Roads cross but don't intersect (overpass/bridge) ? no shared node

### 2.2 Tags for Logical Grouping

- `name=*` ó Human-readable road name (e.g., "Berliner Straﬂe")
- `ref=*` ó Route reference number (e.g., "B51", "A1")
- `highway=*` ó Road classification (motorway, primary, secondary, residential, etc.)

### 2.3 Route Relations

For complex multi-segment roads, OSM uses `type=route, route=road` relations:
- Group multiple way segments into a logical route
- Members have roles: `forward`, `backward`, or empty
- Provide ordering information for the segments
- Essential for named highways that span many way segments

### 2.4 Key Principle

OSM connects ways based on **shared node IDs**, NOT based on geometric proximity. Two ways at the same location without a shared node are explicitly **not** connected (this is how bridges/overpasses work).

---

## 3. Gap Analysis: Current vs. Desired

| OSM Connectivity Rule | Current Implementation | Gap |
|---|---|---|
| **Shared nodes** | Approximated by 1m endpoint proximity | Node IDs not available; proximity is a loose approximation that can over-merge or under-merge |
| **Same-name grouping** | Not implemented | Ways with `name=Main St` are merged with `name=Oak Ave` if endpoints are close |
| **Route relations** | Queried but not used for road assembly | `route=road` relation members could provide explicit way ordering |
| **Highway type matching** | Not implemented | A `motorway` segment can merge with a `residential` segment if endpoints touch |
| **No-connection at grade separation** | Partially handled via bridge/tunnel exclusion | Only works when exclusion flags are enabled; pure geometric proximity can't distinguish grade-separated crossings without structure tags |

### 3.1 Impact Assessment

**High Impact Gaps:**
1. **Missing node IDs** ? The biggest gap. Without node IDs, we cannot do true topological connectivity. The 1m proximity tolerance is a fuzzy approximation.
2. **No highway type filtering** ? Can cause incorrect merges at junctions where different road types meet.

**Medium Impact Gaps:**
3. **Route relations unused** ? Named highways that span many segments could benefit from explicit ordering from route relations, but this is an optimization rather than a correctness issue.
4. **No name-based grouping** ? Could improve spline continuity for roads that share a name but are split at OSM editing boundaries.

**Low Impact Gaps:**
5. **Direction awareness** ? Currently all 4 endpoint combos are tried and paths can be reversed. This works correctly for our use case (we only care about geometry, not traffic direction).

---

## 4. Implementation Plan

### Phase 1: Parse and Preserve OSM Node IDs (Foundation)

**Goal**: Make node IDs available for downstream connectivity logic.

#### 4.1.1 Extend `GeoCoordinate` or Create a New Type

The Overpass `out geom` format does NOT include node IDs in the `geometry` array. However, the `nodes` array is present alongside `geometry` in each way element. Both arrays are ordered and have the same length, so we can zip them together.

**Option A ó Extend `GeoCoordinate`** (minimal change):
```csharp
// Add optional NodeId to GeoCoordinate
public long? NodeId { get; init; }
```

**Option B ó Add `NodeIds` list to `OsmFeature`** (cleaner separation):
```csharp
// In OsmFeature:
public List<long>? NodeIds { get; set; }  // Parallel to Coordinates list
```

**Recommended**: Option B ó keeps `GeoCoordinate` simple (it's used in non-OSM contexts too) and provides node IDs as an explicit parallel list on `OsmFeature`.

#### 4.1.2 Update `OsmGeoJsonParser.ParseElement()`

Parse the `nodes` array from way elements:
```csharp
// After parsing geometry, parse node IDs if available
List<long>? nodeIds = null;
if (element.TryGetProperty("nodes", out var nodesEl))
{
    nodeIds = new List<long>();
    foreach (var nodeEl in nodesEl.EnumerateArray())
    {
        nodeIds.Add(nodeEl.GetInt64());
    }
}
// Store on OsmFeature
feature.NodeIds = nodeIds;
```

#### 4.1.3 Validation

- Verify `nodes` array length matches `geometry` array length
- Log warning if mismatch (gracefully degrade to current behavior)

### Phase 2: Node-Based Connectivity in `ConnectAdjacentPaths`

**Goal**: Use shared node IDs as the primary connection criterion, falling back to geometric proximity.

#### 4.2.1 Propagate Node IDs Through the Pipeline

Currently, `ConvertLinesToSplines()` transforms coordinates and discards feature-level metadata. We need to carry `NodeIds` alongside the transformed paths.

**Change**: Create a lightweight struct to pair paths with their metadata:
```csharp
private record PathWithMetadata(
    List<Vector2> Points,
    long? StartNodeId,    // First node ID (null if unavailable or cropped)
    long? EndNodeId,      // Last node ID (null if unavailable or cropped)
    long OsmWayId,        // For debugging/logging
    Dictionary<string, string> Tags  // For name/type matching
);
```

When cropping to terrain bounds modifies the start or end point, the corresponding node ID should be set to `null` (the original OSM node is no longer the endpoint).

#### 4.2.2 New Connection Algorithm

Replace `ConnectAdjacentPaths()` with a three-tier connection strategy:

**Tier 1 ó Shared Node ID** (highest confidence):
```
If path1.EndNodeId == path2.StartNodeId (or any endpoint combo), merge them.
No distance check needed ó OSM topology guarantees connectivity.
```

**Tier 2 ó Same Name/Ref + Proximity** (medium confidence):
```
If path1 and path2 share the same non-empty `name` or `ref` tag,
AND their endpoints are within the existing tolerance (1m),
merge them.
```

**Tier 3 ó Proximity Only** (fallback, current behavior):
```
If path1 and path2 endpoints are within tolerance AND they share
the same `highway` type, merge them.
```

#### 4.2.3 Anti-Merge Rules

Prevent merging when:
- Paths have different `highway=*` values (e.g., don't merge `motorway` with `residential`)
- A node ID is shared by 3+ paths (it's a junction, not a continuation) ó this requires counting node ID occurrences across all paths first
- Paths have conflicting `name=*` tags (both non-empty but different)

The 3-way shared node rule is critical: at a T-junction, three ways share one node. Currently, two of them get arbitrarily merged. With node ID counting, we can detect this is a junction (3+ ways sharing a node) and avoid merging any of them at that point.

### Phase 3: Route Relation-Based Assembly (Enhancement)

**Goal**: Use `type=route, route=road` relations for explicit way ordering.

#### 4.3.1 Identify Route Relation Members

When the Overpass response includes route relations, their `members` array lists the constituent ways with roles (`forward`, `backward`):
```json
{
  "type": "relation",
  "id": 987654,
  "tags": {"type": "route", "route": "road", "ref": "B51", "name": "Bundesstraﬂe 51"},
  "members": [
    {"type": "way", "ref": 111, "role": "forward"},
    {"type": "way", "ref": 222, "role": "forward"},
    {"type": "way", "ref": 333, "role": "forward"}
  ]
}
```

#### 4.3.2 Pre-Group Ways by Route Relation

Before running the general connection algorithm:
1. Parse route relations and build a mapping: `wayId ? routeRelationId`
2. Group ways that belong to the same route relation
3. Order them according to the relation's member sequence
4. Assemble each group into a pre-connected path
5. Feed the pre-assembled paths into the normal pipeline

**Note**: This requires changing the Overpass query output from `out geom` to `out geom meta` or separately fetching relations with `out body` and ways with `out geom`. The current `out geom` output for relations provides member geometries inline, which may be sufficient.

#### 4.3.3 Handle Partial Coverage

Route relations may include ways outside the terrain bounding box. The assembly should:
- Only include ways that exist in the current query result
- Handle gaps where some member ways are outside the bbox
- Fall back to node-based connectivity for ways not in any route relation

### Phase 4: Highway Type-Aware Merging (Refinement)

**Goal**: Prevent inappropriate cross-type merges at junctions.

#### 4.4.1 Node Valence Counting

Before merging, build a node valence map:
```csharp
Dictionary<long, int> nodeValence;  // nodeId ? number of ways sharing this node
```

Nodes with valence ? 3 are junctions. Ways should NOT be merged through junction nodes ó they should terminate there, allowing the junction harmonizer to handle the intersection properly.

#### 4.4.2 Highway Type Compatibility

Define which highway types can be merged together:
```
motorway ? motorway_link       (on-ramp continuation)
trunk ? trunk_link
primary ? primary_link
secondary ? secondary_link
tertiary ? tertiary_link
residential ? residential
unclassified ? unclassified
```

All other cross-type combinations should NOT merge, even if they share an endpoint node.

---

## 5. Summary of Changes by File

| File | Change | Phase |
|------|--------|:-----:|
| `OsmFeature.cs` | Add `List<long>? NodeIds` property | 1 |
| `OsmGeoJsonParser.cs` | Parse `nodes` array from way elements | 1 |
| `OsmGeometryProcessor.cs` | Propagate node IDs through `ConvertLinesToSplines`; new `ConnectAdjacentPaths` with 3-tier strategy; add anti-merge rules (node valence, highway type) | 2, 4 |
| `OverpassApiService.cs` | Possibly adjust query to ensure node IDs and route relations are included | 3 |
| `OsmGeoJsonParser.cs` | Parse route relation member ordering | 3 |
| `ConnectingRoadTrimmer.cs` | No changes needed (operates on geo-coordinates before this stage) | ó |
| `RoundaboutDetector.cs` | No changes needed (uses its own coordinate-based grouping) | ó |
| `UnifiedRoadNetworkBuilder.cs` | No changes to PNG path (MergeBrokenCurves stays as-is) | ó |

---

## 6. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Overpass `out geom` may not include `nodes` array in all responses | Check for its presence; gracefully fall back to current proximity-based logic |
| Route relations may reference ways outside our bbox | Handle partial coverage; skip missing members |
| Node valence counting requires pre-scanning all ways | Single O(n) pass; negligible performance cost |
| Changing merge behavior may break existing terrain generation for users | Feature-flag the new logic; allow fallback to legacy proximity-only merging |
| Some OSM areas have poor data quality (missing nodes, misaligned ways) | Keep the proximity fallback as Tier 3; log when it's used |

---

## 7. Testing Strategy

1. **Regression test**: Generate terrain for existing test areas and verify identical output when new logic is disabled
2. **Junction accuracy**: Compare junction detection results with and without node-based connectivity
3. **Named highways**: Verify that B-roads and named streets produce longer, more continuous splines
4. **Cross-type junctions**: Verify that motorway/residential junctions are NOT merged
5. **Edge cases**: Ways cropped at terrain boundary, ways with missing node IDs, ways in route relations with gaps

---

## Appendix A: Overpass API `out geom` Response Format

Example way element with both `nodes` and `geometry`:
```json
{
  "type": "way",
  "id": 4579143,
  "nodes": [26945854, 26945855, 26945856, 26945857, 26945858],
  "geometry": [
    {"lat": 51.5134, "lon": 7.4652},
    {"lat": 51.5136, "lon": 7.4655},
    {"lat": 51.5138, "lon": 7.4658},
    {"lat": 51.5140, "lon": 7.4661},
    {"lat": 51.5142, "lon": 7.4664}
  ],
  "tags": {
    "highway": "primary",
    "name": "Berliner Straﬂe",
    "ref": "B51",
    "lanes": "2",
    "surface": "asphalt"
  }
}
```

Key: `nodes[i]` corresponds to `geometry[i]`. If way 4579143 ends with node 26945858, and another way starts with node 26945858, OSM considers them topologically connected.

## Appendix B: Current Code References

| Component | File | Key Method |
|-----------|------|-----------|
| Overpass query | `OverpassApiService.cs` | `BuildAllFeaturesQuery()` (line ~296) |
| JSON parsing | `OsmGeoJsonParser.cs` | `ParseElement()` (line ~69) |
| Relation assembly | `OsmGeoJsonParser.cs` | `ParseRelationMembers()` (line ~158) |
| Ring assembly | `OsmGeoJsonParser.cs` | `AssembleRingsFromSegments()` (line ~240) |
| Feature selection | `TerrainGenerationOrchestrator.cs` | `ProcessOsmRoadMaterialAsync()` (line ~513) |
| Spline conversion | `OsmGeometryProcessor.cs` | `ConvertLinesToSplines()` (line ~711) |
| **Path connection** | `OsmGeometryProcessor.cs` | `ConnectAdjacentPaths()` (line ~913) |
| Roundabout detection | `RoundaboutDetector.cs` | `DetectRoundabouts()` (line ~36) |
| Road trimming | `ConnectingRoadTrimmer.cs` | `TrimConnectingRoads()` |
| Network building | `UnifiedRoadNetworkBuilder.cs` | `BuildNetwork()` (line ~46) |
| PNG path merging | `UnifiedRoadNetworkBuilder.cs` | `MergeBrokenCurves()` (line ~768) ó NOT affected |
