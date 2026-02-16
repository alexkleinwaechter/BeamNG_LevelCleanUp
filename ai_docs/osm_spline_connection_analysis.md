# OSM Spline Connection Analysis & Implementation Plan

## Document Purpose

This document analyzes how OSM way features are currently connected into longer splines during terrain generation road smoothing, and proposes an implementation plan to adopt OSM's own connectivity rules (shared nodes, name tags, route relations) for more accurate road assembly.

> **Scope**: This document covers OSM-sourced splines only. PNG-sourced splines use skeleton extraction and a different merging strategy (`MergeBrokenCurves` in `UnifiedRoadNetworkBuilder`) that should remain unchanged.

> **Bridge/Tunnel Constraint**: The existing bridge/tunnel detection, separation, elevation profiling, and terrain-exclusion pipeline MUST remain intact or be improved. A future feature will build on this infrastructure to generate bridge/tunnel 3D meshes (DAE). All changes in this plan must preserve the structural metadata flow described in Section 1.10.

---

## 1. As-Is Situation: Current OSM Spline Connection Pipeline

### 1.1 High-Level Data Flow

```
Overpass API Query
    └─> OsmGeoJsonParser.Parse()             [Parse JSON → OsmFeature objects]
        └─> OsmFeatureSelection                [User selects features per material]
            └─> OsmGeometryProcessor
                ├─> ConvertLinesToSplines()                     [Simple path]
                └─> ConvertLinesToSplinesWithRoundabouts()      [Roundabout-aware path]
                    ├─> RoundaboutDetector.DetectRoundabouts()
                    ├─> ConnectingRoadTrimmer.TrimConnectingRoads()
                    ├─> RoundaboutMerger.ProcessRoundabouts()
                    └─> ConvertLinesToSplines()  [for regular roads]
                        └─> ConnectAdjacentPaths()             [*** THE KEY METHOD ***]
                            └─> RoadSpline objects (pre-built splines)
                                └─> RoadSmoothingParameters.PreBuiltSplines
                                    └─> UnifiedRoadNetworkBuilder.BuildNetwork()
                                        └─> ParameterizedRoadSpline objects in UnifiedRoadNetwork
```

### 1.2 Overpass API Query

**File**: `OverpassApiService.BuildAllFeaturesQuery()` (line ~296)

The query fetches:
- `way["highway"]` — all road ways
- `relation["type"="route"]["route"="road"]` — route relations (e.g., named highways like "A1")

Output format: `out geom;` — returns geometry inline (lat/lon per point) instead of requiring node resolution.

**What the response contains per way**:
```json
{
  "type": "way",
  "id": 123456789,
  "nodes": [111, 222, 333, 444],       // ← Node IDs (CURRENTLY IGNORED)
  "geometry": [                          // ← Only this is parsed
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
- Way ID (`id`) → stored as `OsmFeature.Id`
- Tags → stored as `OsmFeature.Tags` dictionary (includes `name`, `ref`, `highway`, etc.)
- Geometry (`geometry` array) → stored as `OsmFeature.Coordinates` (list of `GeoCoordinate`)

**What is NOT parsed:**
- **Node IDs** (`nodes` array) — completely ignored
- Node-level metadata — not available via `out geom` anyway

**For relations** (`ParseRelationMembers`, line ~158):
- Extracts member way geometries
- Groups by role (`outer`/`inner`)
- Assembles into rings using endpoint coordinate matching
- **Route relations** (`type=route, route=road`) are processed but the member roles (`forward`, `backward`, empty) are all treated as `outer` — semantically incorrect for linear route assembly

### 1.4 Feature Selection & Material Assignment

**File**: `TerrainGenerationOrchestrator.ProcessOsmRoadMaterialAsync()` (line ~513)

Users select OSM features per material via `OsmFeatureSelection`. The orchestrator:
1. Filters selected features by geometry type (LineString for roads)
2. Builds `RoadSmoothingParameters` from material settings
3. Calls `ConvertLinesToSplines()` or `ConvertLinesToSplinesWithRoundabouts()`

### 1.5 Coordinate Transformation & Path Preparation

**File**: `OsmGeometryProcessor.ConvertLinesToSplines()` (line ~711)

Each OSM way feature goes through:
1. **Transform** to terrain-space coordinates (WGS84 → pixel → meters)
2. **Crop** to terrain bounds via `CropLineToTerrain()`
3. **Convert** from pixel to meter coordinates
4. **Remove duplicate** consecutive points (tolerance: 0.01m = 1cm)
5. **Separate** into structure paths (bridges/tunnels) and regular paths

### 1.6 Path Connection — The Core Logic ⚠️

**File**: `OsmGeometryProcessor.ConnectAdjacentPaths()` (line ~913)

This is the **sole mechanism** for connecting OSM way segments into longer splines:

```csharp
private List<List<Vector2>> ConnectAdjacentPaths(List<List<Vector2>> paths, float tolerance)
```

**Current algorithm:**
1. Copy all paths into a working list
2. Iteratively scan all path pairs (O(n²) per iteration)
3. For each pair, check **4 endpoint combinations**:
   - path1.End ↔ path2.Start
   - path1.End ↔ path2.End
   - path1.Start ↔ path2.End
   - path1.Start ↔ path2.Start
4. If `Vector2.DistanceSquared(endpoint1, endpoint2) <= tolerance²`, merge the paths
5. Repeat until no more merges possible (safety limit: 1000 iterations)

**Default tolerance**: `endpointJoinToleranceMeters = 1.0f` (configurable, set in orchestrator)

### 1.7 Current Connection Rules Summary

| Rule | Implemented | Details |
|------|:-----------:|---------|
| Endpoint proximity (≤1m) | ✅ | The only rule. Distance-based in meter-space after coordinate transformation. |
| Shared OSM node IDs | ❌ | Node IDs not parsed from Overpass response. |
| Same road name (`name=*`) | ❌ | Tags are parsed but not used in connection logic. |
| Same road reference (`ref=*`) | ❌ | Available in tags but not considered. |
| Same highway type | ❌ | `highway=*` tag not used for connection decisions. |
| Route relation membership | ❌ | `route=road` relations queried but not used for way ordering/connection. |
| Direction awareness | ❌ | Paths can be reversed freely (all 4 endpoint combos tried). |
| Topology (shared nodes) | ❌ | Purely geometric, not topological. |

### 1.8 Where Merging Does NOT Apply

The following cases bypass `ConnectAdjacentPaths`:

- **Bridge/tunnel splines**: When `excludeBridges`/`excludeTunnels` is true, structure paths are kept separate and never merged with regular paths.
- **Roundabout ring splines**: Assembled separately by `RoundaboutMerger` using coordinate endpoint matching on the original geo-coordinates (similar principle but applied before coordinate transformation).
- **Cross-material connections**: Each material's OSM features are processed independently. Ways from different materials are never merged together.

### 1.10 Bridge/Tunnel Pipeline (MUST BE PRESERVED)

The bridge/tunnel handling is a multi-stage pipeline that currently works correctly and is designed as the foundation for future 3D mesh generation. Every change in this plan must preserve this flow.

#### 1.10.1 Detection (Tag-Based)

**File**: `OsmFeature.cs` (L189–302)

| Property | Logic |
|----------|-------|
| `IsBridge` | `Tags["bridge"]` exists and ≠ `"no"` |
| `IsTunnel` | `Tags["tunnel"]` exists and ≠ `"no"`, OR `Tags["covered"]` == `"yes"` |
| `IsStructure` | `IsBridge \|\| IsTunnel` |
| `GetStructureType()` | Returns enum: `Bridge`, `Tunnel`, `BuildingPassage`, `Culvert`, `None` |
| `Layer` | Parses `Tags["layer"]` as `int`, defaults to `0` |
| `BridgeStructureType` | Reads `Tags["bridge:structure"]`; falls back to special `bridge` tag values (`viaduct`, `cantilever`, `suspension`, `movable`, `aqueduct`) |

The `OverpassApiService` explicitly queries `way["bridge"]`, `way["tunnel"]`, `way["man_made"="bridge"]` to ensure these ways are fetched even if they don't match other category queries.

#### 1.10.2 Path Separation in `ConvertLinesToSplines()`

**File**: `OsmGeometryProcessor.cs` (L776–808)

After transforming all features to meter coordinates, the method partitions paths using a parallel `pathMetadata` list:

```csharp
bool isProtectedStructure = 
    (pathMetadata[i].IsBridge && excludeBridges) ||
    (pathMetadata[i].IsTunnel && excludeTunnels);
```

- **`isProtectedStructure = true`** → path goes into `structurePaths` (kept as isolated segments, never enters `ConnectAdjacentPaths`)
- **`isProtectedStructure = false`** → path goes into `regularPaths` (merged via `ConnectAdjacentPaths`)
- **Backward compatibility**: When both `excludeBridges` and `excludeTunnels` are `false`, ALL paths including bridges/tunnels flow into `regularPaths` — identical to legacy behavior.

#### 1.10.3 Structure Metadata on Spline Objects

Metadata flows through 3 layers:

| Class | Properties |
|-------|------------|
| `RoadSpline` | `IsBridge`, `IsTunnel`, `IsStructure`, `StructureType`, `Layer`, `BridgeStructureType` |
| `ParameterizedRoadSpline` | Same properties + `ElevationProfile` (for future DAE generation) |
| `RoadSmoothingParameters` | `ExcludeBridgesFromTerrain`, `ExcludeTunnelsFromTerrain` (per-material flags) |

**Key invariant**: Protected structure paths get their `IsBridge`/`IsTunnel`/`StructureType`/`Layer`/`BridgeStructureType` copied from `OsmFeature`. Merged regular paths are always set to `IsBridge=false, IsTunnel=false, StructureType=None, Layer=0`.

#### 1.10.4 Downstream Consumers (Must Not Break)

| Stage | File | What It Does With Structures |
|-------|------|-----------------------------|
| Network building | `UnifiedRoadNetworkBuilder.BuildNetwork()` | Copies all structure metadata to `ParameterizedRoadSpline`; logs bridge/tunnel counts |
| Elevation integration | `StructureElevationIntegrator.IntegrateStructureElevationsSelective()` | Finds connecting road elevations at bridge/tunnel endpoints via geometric proximity (15m tolerance); calculates bridge arcs / tunnel profiles; stores `ElevationProfile` on spline |
| Entry/exit elevation | `StructureElevationIntegrator.FindConnectingRoadElevation()` | Searches non-structure splines' start/end points within `ConnectionTolerance` (15m) to find target elevations at bridge/tunnel endpoints |
| Terrain smoothing | `UnifiedRoadSmoother` Phase 4 | Marks excluded structure cross-sections `IsExcluded = true`, skipping terrain modification |
| Material painting | `OsmGeometryProcessor.RasterizeSplinesToLayerMap()` | Excludes bridge/tunnel splines from terrain material painting when flags are set |

#### 1.10.5 What Our Changes MUST Preserve

1. **Protected structure paths must never enter the merge algorithm.** The partition logic (Step 2 in `ConvertLinesToSplines`) must remain. Structure paths bypass merging entirely.
2. **Structure metadata must propagate intact.** `IsBridge`, `IsTunnel`, `StructureType`, `Layer`, `BridgeStructureType` must be copied from `pathMetadata[index]` to `RoadSpline` for all structure paths.
3. **The parallel `pathMetadata` list must stay in sync with `allPaths`.** When we add node IDs to the pipeline, the metadata tracking must not break the index-based correspondence between `allPaths`, `pathMetadata`, and `structurePaths`.
4. **Backward compatibility mode** (both exclude flags = false) must continue to work — all paths including bridges/tunnels merge together, with `IsBridge=false` on merged output.
5. **`StructureElevationIntegrator.FindConnectingRoadElevation()`** must keep working. This method uses geometric proximity (15m) to find connecting regular roads at bridge/tunnel endpoints. Our changes to the merge algorithm only affect which regular paths get merged into longer splines — the topology of the network at bridge endpoints remains the same.

#### 1.10.6 How Our Changes Will IMPROVE Bridge/Tunnel Handling

Node IDs open up several improvements for the future bridge/tunnel generation feature:

1. **Precise bridge endpoint connection**: `FindConnectingRoadElevation()` currently searches within 15m geometric proximity. With node IDs, bridge endpoint connections to regular roads could be found topologically (exact shared node match) instead of geometrically, eliminating false matches at complex intersections.
2. **Grade separation detection**: OSM distinguishes grade-separated crossings by NOT sharing nodes. Node-based merging inherently respects this — two roads crossing without a shared node will never be merged, even if geometrically close. This is critical for bridge generation where the terrain underneath must remain unmodified.
3. **Bridge/tunnel continuation chains**: Some long bridges are split into multiple OSM ways (e.g., at admin boundaries). Node IDs would allow merging consecutive bridge segments into a single bridge spline, providing smoother elevation profiles.
4. **Node IDs on `PathWithMetadata`**: The `StartNodeId`/`EndNodeId` fields proposed in Phase 2 can later be propagated to `RoadSpline` and `ParameterizedRoadSpline`, enabling `StructureElevationIntegrator` to find connecting roads by node ID instead of geometric search.

### 1.9 Consequences of Current Approach

**Works well when:**
- OSM data is clean and ways share exact endpoint coordinates (common in well-mapped areas)
- The 1m tolerance accommodates minor floating-point drift from coordinate transformation
- All ways of the same road type are assigned to the same material

**Breaks or produces suboptimal results when:**
- Two ways of different road types share a node (e.g., `primary` and `residential`) — these get incorrectly merged into one long spline
- A T-junction has three ways meeting at a shared node — only two will merge, creating inconsistent results depending on iteration order
- Coordinate transformation introduces drift >1m (rare but possible with GDAL reprojection)
- Ways that are part of the same named road but are split at a feature boundary (bridge segment, admin boundary) may not reconnect if their transformed endpoints drift

---

## 2. How OSM Defines Road Connectivity

### 2.1 Shared Nodes (Topology)

OSM's primary connectivity mechanism. Two ways are connected if they share a common **node** (point with a unique ID). This is binary — either they share a node or they don't.

- **Intersection**: Two roads cross → they share a node at the crossing point
- **Continuation**: One road is split into segments → adjacent segments share their boundary node
- **No connection**: Roads cross but don't intersect (overpass/bridge) → no shared node

### 2.2 Tags for Logical Grouping

- `name=*` — Human-readable road name (e.g., "Berliner Straße")
- `ref=*` — Route reference number (e.g., "B51", "A1")
- `highway=*` — Road classification (motorway, primary, secondary, residential, etc.)

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
1. **Missing node IDs** → The biggest gap. Without node IDs, we cannot do true topological connectivity. The 1m proximity tolerance is a fuzzy approximation.
2. **No highway type filtering** → Can cause incorrect merges at junctions where different road types meet.

**Medium Impact Gaps:**
3. **Route relations unused** → Named highways that span many segments could benefit from explicit ordering from route relations, but this is an optimization rather than a correctness issue.
4. **No name-based grouping** → Could improve spline continuity for roads that share a name but are split at OSM editing boundaries.

**Low Impact Gaps:**
5. **Direction awareness** → Currently all 4 endpoint combos are tried and paths can be reversed. This works correctly for our use case (we only care about geometry, not traffic direction).

---

## 4. Implementation Plan

### Phase 1: Parse and Preserve OSM Node IDs (Foundation)

**Goal**: Make node IDs available for downstream connectivity logic.

#### 4.1.1 Extend `GeoCoordinate` or Create a New Type

The Overpass `out geom` format does NOT include node IDs in the `geometry` array. However, the `nodes` array is present alongside `geometry` in each way element. Both arrays are ordered and have the same length, so we can zip them together.

**Option A — Extend `GeoCoordinate`** (minimal change):
```csharp
// Add optional NodeId to GeoCoordinate
public long? NodeId { get; init; }
```

**Option B — Add `NodeIds` list to `OsmFeature`** (cleaner separation):
```csharp
// In OsmFeature:
public List<long> NodeIds { get; set; }  // Parallel to Coordinates list, always present for ways
```

**Recommended**: Option B — keeps `GeoCoordinate` simple (it's used in non-OSM contexts too) and provides node IDs as an explicit parallel list on `OsmFeature`. The list is non-nullable because every OSM way always has node IDs.

#### 4.1.2 Update `OsmGeoJsonParser.ParseElement()`

Parse the `nodes` array from way elements:
```csharp
// Parse node IDs — always present for OSM ways
var nodesEl = element.GetProperty("nodes");
var nodeIds = new List<long>();
foreach (var nodeEl in nodesEl.EnumerateArray())
{
    nodeIds.Add(nodeEl.GetInt64());
}
// Store on OsmFeature
feature.NodeIds = nodeIds;
```

#### 4.1.3 Validation

- Assert that `nodes` array length matches `geometry` array length (these are always equal for OSM ways)
- If a mismatch is ever encountered, throw an exception — this would indicate a bug in parsing, not a data issue

### Phase 2: Node-Based Connectivity in `ConnectAdjacentPaths`

**Goal**: Use shared node IDs as the primary connection criterion. Geometric proximity is only used as a secondary signal for cropped paths where node IDs were removed at terrain boundaries.

#### 4.2.1 Propagate Node IDs Through the Pipeline

Currently, `ConvertLinesToSplines()` transforms coordinates and discards feature-level metadata. We need to carry `NodeIds` alongside the transformed paths.

**Change**: Create a lightweight struct to pair paths with their metadata:
```csharp
private record PathWithMetadata(
    List<Vector2> Points,
    long? StartNodeId,    // First node ID (null only if path was cropped at terrain start boundary)
    long? EndNodeId,      // Last node ID (null only if path was cropped at terrain end boundary)
    long OsmWayId,        // For debugging/logging
    Dictionary<string, string> Tags,  // For name/type matching
    // Structure metadata — carried alongside but NOT used for merge decisions
    bool IsBridge,
    bool IsTunnel,
    StructureType StructureType,
    int Layer,
    string? BridgeStructureType
);
```

When cropping to terrain bounds modifies the start or end point, the corresponding node ID should be set to `null` (the original OSM node is no longer the endpoint).

**Bridge/tunnel constraint**: The `PathWithMetadata` record carries structure metadata for completeness, but **protected structure paths (where `excludeBridges`/`excludeTunnels` applies) must be separated BEFORE the merge algorithm runs**, exactly as they are today. `PathWithMetadata` is only used for paths that enter the merge algorithm (the `regularPaths` partition). The existing 5-step pipeline in `ConvertLinesToSplines()` stays intact — we only change what happens inside Step 3 (`ConnectAdjacentPaths`).

#### 4.2.2 New Connection Algorithm

Replace `ConnectAdjacentPaths()` with a three-tier connection strategy:

**Tier 1 — Shared Node ID** (highest confidence):
```
If path1.EndNodeId == path2.StartNodeId (or any endpoint combo), merge them.
No distance check needed — OSM topology guarantees connectivity.
```

**Tier 2 — Same Name/Ref + Proximity** (medium confidence):
```
If path1 and path2 share the same non-empty `name` or `ref` tag,
AND their endpoints are within the existing tolerance (1m),
merge them.
```

**Tier 3 — Proximity Only** (for cropped paths that lost their node IDs at terrain boundaries):
```
If path1 and path2 endpoints are within tolerance AND they share
the same `highway` type, merge them.
This only applies when one or both endpoints have null node IDs due to terrain cropping.
```

#### 4.2.3 Anti-Merge Rules

Prevent merging when:
- Paths have different `highway=*` values (e.g., don't merge `motorway` with `residential`)
- A node ID is shared by 3+ paths (it's a junction, not a continuation) **UNLESS** through-junction merging applies (see below)
- Paths have conflicting `name=*` tags (both non-empty but different)
- **A bridge/tunnel-tagged path and a non-structure path share a node** — even in backward-compatible mode (both exclude flags = false), this anti-merge rule should prevent merging a bridge way with a regular way if we can detect it. This preserves the logical boundary for future bridge mesh generation.

#### 4.2.3.1 Through-Junction Merging Exception

The blanket junction rule (3+ shared paths → block all merges) was too aggressive. It created many short splines at every T-junction and crossroad, preventing the elevation solver's through-road chain detection from working. Roads that logically belong together (e.g., "Berliner Straße" continuing through a T-junction) were broken into separate splines with different SplineIds.

**Through-junction merging** allows merging at junction nodes when two paths form a logical through-road:
1. Both paths share a non-empty `name` or `ref` tag (road identity continuity)
2. Both paths have compatible highway types (same type group)
3. Neither path is tagged `junction=roundabout` (roundabout rings stay separate)
4. The deflection angle at the junction is below 90° (prevents merging roads that take sharp turns)

**How it works with the iterative loop**: After merging A+B at junction J1, J1 becomes interior to the merged path. On the next iteration, merged A+B's endpoint at J2 can merge with path C. The stale valence map still shows J2 as a junction, so the through-road test runs and allows the merge. This naturally chains multiple segments into long through-road splines.

**Why this matters for elevation solving**: The merged through-road has one SplineId. The RoadGraphBuilder splits it at mid-path junctions (where side roads connect), creating multiple edges from the same spline. Both elevation solvers detect these as through-road chains:
- `GraphElevationSolver.GetContributingEdges()` groups edges by SplineId — through-road edges dominate junction elevation
- `GlobalElevationSolver.ComputeWeights()` applies SplineId-based β suppression — non-through-road edges at junctions are suppressed

**Roundabout protection**: Roundabout ring splines are assembled by `RoundaboutMerger` before `NodeBasedPathConnector` runs. The `junction=roundabout` tag check prevents through-junction merging of any remaining roundabout-tagged paths.

#### 4.2.4 Bridge/Tunnel Merge Exclusions (Detailed)

The merge algorithm must handle bridge/tunnel paths correctly in both modes:

**When `excludeBridges=true` / `excludeTunnels=true`** (normal mode):
- Protected structure paths are separated in Step 2 and never enter the merge algorithm. **No change needed** — current behavior is correct.
- Regular paths (non-protected) are merged as usual. A bridge-tagged way that is NOT excluded (e.g., `excludeBridges=false` but `excludeTunnels=true` — the bridge enters regular paths) follows normal merge rules.

**When both exclude flags are `false`** (backward-compatible mode):
- ALL paths enter the merge algorithm, including bridges and tunnels.
- Currently, this means bridge metadata is lost on merged paths (`IsBridge=false` on output). This is the intended backward-compatible behavior.
- With node IDs, we gain an option to improve this: we could detect structure-tagged ways within the merge algorithm and keep them as separate splines even in backward mode. However, **this is a future enhancement, not part of the current plan** — backward compatibility takes priority.

**Bridge/tunnel continuation merging** (future Phase 5 consideration):
- Multiple consecutive bridge segments (same road split at admin boundaries) currently remain as separate structure splines.
- With node IDs, we could merge consecutive bridge segments that share node IDs AND have compatible structure metadata (`IsBridge && IsBridge`, same `Layer`, same `BridgeStructureType`).
- This would produce longer, smoother bridge splines — beneficial for the future DAE generation feature.
- **Not implemented in this plan** but the `PathWithMetadata` record carries all needed fields.

### Phase 3: Route Relation-Based Assembly (Enhancement)

**Goal**: Use `type=route, route=road` relations for explicit way ordering.

#### 4.3.1 Identify Route Relation Members

When the Overpass response includes route relations, their `members` array lists the constituent ways with roles (`forward`, `backward`):
```json
{
  "type": "relation",
  "id": 987654,
  "tags": {"type": "route", "route": "road", "ref": "B51", "name": "Bundesstraße 51"},
  "members": [
    {"type": "way", "ref": 111, "role": "forward"},
    {"type": "way", "ref": 222, "role": "forward"},
    {"type": "way", "ref": 333, "role": "forward"}
  ]
}
```

#### 4.3.2 Pre-Group Ways by Route Relation

Before running the general connection algorithm:
1. Parse route relations and build a mapping: `wayId → routeRelationId`
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
Dictionary<long, int> nodeValence;  // nodeId → number of ways sharing this node
```

Nodes with valence ≥ 3 are junctions. Ways should NOT be merged through junction nodes — they should terminate there, allowing the junction harmonizer to handle the intersection properly.

#### 4.4.2 Highway Type Compatibility

Define which highway types can be merged together:
```
motorway ↔ motorway_link       (on-ramp continuation)
trunk ↔ trunk_link
primary ↔ primary_link
secondary ↔ secondary_link
tertiary ↔ tertiary_link
residential ↔ residential
unclassified ↔ unclassified
```

All other cross-type combinations should NOT merge, even if they share an endpoint node.

---

## 5. Summary of Changes by File

| File | Change | Phase | Bridge/Tunnel Impact |
|------|--------|:-----:|---------------------|
| `OsmFeature.cs` | Add `List<long> NodeIds` property (non-nullable) | 1 | None — structure tag properties unchanged |
| `OsmGeoJsonParser.cs` | Parse `nodes` array from way elements | 1 | None — tag parsing unchanged |
| `OsmGeometryProcessor.cs` | Propagate node IDs through `ConvertLinesToSplines`; new `ConnectAdjacentPaths` with 3-tier strategy; add anti-merge rules (node valence, highway type) | 2, 4 | **Must preserve**: Step 2 partition (protected structures bypass merge); Step 4 metadata copy to `RoadSpline`; `pathMetadata` index sync |
| `OverpassApiService.cs` | Possibly adjust query to ensure node IDs and route relations are included | 3 | None — bridge/tunnel queries unchanged |
| `OsmGeoJsonParser.cs` | Parse route relation member ordering | 3 | None |
| `ConnectingRoadTrimmer.cs` | No changes needed (operates on geo-coordinates before this stage) | — | Not affected |
| `RoundaboutDetector.cs` | No changes needed (uses its own coordinate-based grouping) | — | Not affected |
| `UnifiedRoadNetworkBuilder.cs` | No changes to PNG path (MergeBrokenCurves stays as-is) | — | Not affected |
| `StructureElevationIntegrator.cs` | No changes in this plan (future: could use node IDs for precise bridge endpoint matching) | — | Not affected — continues to use 15m geometric search |
| `UnifiedRoadSmoother.cs` | No changes in this plan | — | Not affected — continues to mark excluded structure cross-sections |
| `RoadSpline.cs` | No changes in this plan (future: could add `StartNodeId`/`EndNodeId` for bridge endpoint detection) | — | Structure metadata properties unchanged |

---

## 6. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| PNG spline connecting logic may be destroyed during implementation | No changes to PNG path merging; only OSM paths affected |
| Route relations may reference ways outside our bbox | Handle partial coverage; skip missing members |
| Node valence counting requires pre-scanning all ways | Single O(n) pass; negligible performance cost |
| Changing merge behavior may break existing terrain generation for users | Accepted — no fallback to old behavior; new node-based logic replaces proximity-based merging entirely |
| **Bridge/tunnel structure path separation may break** | Step 2 partition in `ConvertLinesToSplines()` is NOT modified — only Step 3 (`ConnectAdjacentPaths`) changes. The protected-structure bypass is upstream of all merge logic. |
| **Bridge/tunnel metadata may be lost during merge** | `pathMetadata` index tracking is preserved. Structure splines get metadata from `pathMetadata[index]` in Step 4 (unchanged). Merged regular splines continue to get `IsBridge=false`. |
| **`StructureElevationIntegrator` may fail to find connecting roads** | The integrator searches by geometric proximity (15m), not by merge status. Regular roads that previously merged into long splines vs. shorter splines still have the same endpoints. No impact expected. |
| **Backward-compatible mode (both exclude flags = false) changes behavior** | In this mode, all paths including bridges/tunnels enter the merge algorithm. With node IDs, the same merges will happen (shared nodes = proximity), plus anti-merge rules prevent bad merges. Net effect is neutral-to-positive. |
| **Code files get too long** | We try to do new codefiles if the base code file has more than 1000 lines |

**Non-risks (clarifications):**
- ~~Overpass `out geom` may not include `nodes` array~~ — Not a risk. Every OSM way always has nodes and node IDs. The `nodes` array is guaranteed in `out geom` responses for ways.
- ~~Poor OSM data quality with missing nodes~~ — Not a risk. As long as a way exists in OSM, it has nodes. Node IDs are a fundamental part of the OSM data model.
- ~~Bridge/tunnel elevation profiles may break~~ — Not a risk. `StructureElevationIntegrator`, `StructureElevationCalculator`, and `StructureElevationProfile` are entirely downstream of the merge algorithm. They operate on already-built `ParameterizedRoadSpline` objects whose structure metadata is unaffected by merge logic changes.
- ~~Bridge/tunnel terrain exclusion may break~~ — Not a risk. `RasterizeSplinesToLayerMap()` checks `spline.IsBridge && excludeBridges` on the final `RoadSpline` objects. Since structure splines bypass merging and retain their metadata, this check remains valid.

---

## 7. Testing Strategy

### 7.1 General Regression
1. **Regression test**: Generate terrain for existing test areas and compare output quality with the previous proximity-based approach
2. **Junction accuracy**: Compare junction detection results with and without node-based connectivity
3. **Named highways**: Verify that B-roads and named streets produce longer, more continuous splines
4. **Cross-type junctions**: Verify that motorway/residential junctions are NOT merged
5. **Edge cases**: Ways cropped at terrain boundary (node IDs nulled at crop points), ways in route relations with gaps

### 7.2 Bridge/Tunnel Preservation Tests
6. **Structure path count**: With `excludeBridges=true`, verify that the number of bridge `RoadSpline` objects with `IsBridge=true` is identical before and after the merge algorithm change. Same for tunnels.
7. **Structure metadata integrity**: For each structure spline, verify `StructureType`, `Layer`, and `BridgeStructureType` match the original `OsmFeature` values.
8. **Elevation profile integration**: Run `StructureElevationIntegrator.IntegrateStructureElevationsSelective()` and verify it still finds connecting road elevations (entry/exit) for bridge/tunnel splines. Compare entry/exit elevation values before and after the change.
9. **Terrain exclusion**: Verify that bridge/tunnel splines are still excluded from `RasterizeSplinesToLayerMap()` when their exclusion flag is set.
10. **Backward-compatible mode**: With both exclude flags = false, verify that bridges/tunnels still merge with regular roads and output splines have `IsBridge=false` (legacy behavior).
11. **Grade separation**: Find a test area with an overpass (bridge crossing over a road). Verify that the bridge road and the road below are NOT merged — node-based logic should inherently prevent this since OSM doesn't share nodes at grade separations.

### 7.3 Test Areas
- Areas with bridges: Any city with a river crossing
- Areas with tunnels: Mountain roads, urban underpasses
- Complex interchanges: Highway on/off ramps with bridges over local roads
- Long bridges split into segments: Major river crossings (e.g., Rhine bridges) where the bridge is split at municipality boundaries

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
    "name": "Berliner Straße",
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
| PNG path merging | `UnifiedRoadNetworkBuilder.cs` | `MergeBrokenCurves()` (line ~768) — NOT affected |
