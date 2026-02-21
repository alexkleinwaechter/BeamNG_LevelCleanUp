# Connected Road Network Elevation — Implementation Plan

## Context

The current elevation pipeline smooths each road spline independently (Phase 2), then uses ~3700 lines of junction harmonization heuristics (Phase 3) to reconcile elevation mismatches where roads meet. This "smooth then reconcile" approach fails for complex topologies: long chains, overlapping blend zones, tight loops, closely-spaced urban junctions. See `ai_docs/CONNECTED_MESH_ELEVATION_IDEA.md` for full problem analysis.

**Solution**: Build a road graph where junctions are shared nodes, solve elevation globally via iterative boundary-value smoothing. Junction continuity is guaranteed by construction — nothing to reconcile.

**Approach**: Hybrid / Graph-Aware Iterative Smoothing (Approach D from design doc). Reuses existing per-spline smoothing as initial estimate, adds a road graph, replaces Phase 3 with iterative boundary-value solving. No backward compatibility needed — the app is new.

---

## Phase 1: Preserve All OSM Node IDs Through Pipeline

**Goal**: Carry every OSM node ID through the entire spline pipeline (not just start/end). Foundation for graph construction, mid-path junction detection, and chunk tracking.

### Why needed
The `NodeBasedPathConnector` preserves `StartNodeId`/`EndNodeId` on `PathWithMetadata`, but discards mid-path node IDs. At-grade crossings where both ways pass THROUGH a shared node (not at endpoints) can't be detected without the full node ID list. The graph builder needs these to split splines at mid-path junctions.

### Changes

**`PathWithMetadata.cs`** — add field:
```csharp
public List<long?> AllNodeIds;  // Parallel to Points, null for cropped/synthetic points
```

**`OsmGeometryProcessor.cs`** (`ConvertLinesToSplines` Step 1) — populate AllNodeIds from `OsmFeature.NodeIds` during coordinate transformation. When `CropLineToTerrain` removes start/end points, set those node IDs to null.

**`NodeBasedPathConnector.cs`** — update all four merge methods (`MergeEndToStart`, `MergeEndToEnd`, `MergeStartToEnd`, `MergeStartToStart`) to concatenate `AllNodeIds` lists, matching point concatenation order. Remove duplicate at shared endpoint.

**`RoadSpline.cs`** — add property:
```csharp
public List<long?>? NodeIds { get; set; }  // Parallel to Points, null for PNG splines
```
Populated from `PathWithMetadata.AllNodeIds` during spline creation (Step 5 of ConvertLinesToSplines).

### Validation
- `AllNodeIds.Count == Points.Count` at every stage
- After merge, no duplicate consecutive node IDs
- PNG splines: `NodeIds` is null (no OSM data)

### Files to modify
- `BeamNgTerrainPoc/Terrain/Osm/Processing/PathWithMetadata.cs`
- `BeamNgTerrainPoc/Terrain/Osm/Processing/NodeBasedPathConnector.cs`
- `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs`
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs`

---

## Phase 2: OSM Chunk Tracking

**Goal**: Track original OSM way segments ("chunks") within merged splines. Enables per-chunk parameter overrides and master spline reconstruction.

### New type: `OsmWayChunk`
```
File: Terrain/Models/RoadGeometry/OsmWayChunk.cs

- OsmWayId: long
- Tags: Dictionary<string, string>  (highway, name, ref, lanes, surface, etc.)
- StartPointIndex: int              (index into RoadSpline.Points)
- EndPointIndex: int
- StartDistanceAlongSpline: float   (meters, computed after spline creation)
- EndDistanceAlongSpline: float
```
Future: add `ChunkParameterOverrides?` for per-chunk smoothing parameter overrides.

### Changes

**`PathWithMetadata.cs`** — add:
```csharp
public List<OsmWayChunkInfo> ChunkInfos;  // Lightweight chunk descriptors for merge tracking
```
Each PathWithMetadata starts with one chunk (its own OsmWayId + Tags + index range 0..N).

**`NodeBasedPathConnector.cs`** — when merging two paths, combine ChunkInfos lists. Adjust point indices in chunks from the appended path.

**`RoadSpline.cs`** — add:
```csharp
public List<OsmWayChunk>? Chunks { get; set; }  // null for PNG splines
```

**`UnifiedCrossSection.cs`** — add:
```csharp
public long? OsmWayId { get; set; }  // Which chunk this CS belongs to
```
Populated during cross-section generation based on which chunk's distance range contains this CS.

### Files
- New: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/OsmWayChunk.cs`
- Modify: `PathWithMetadata.cs`, `NodeBasedPathConnector.cs`, `RoadSpline.cs`, `UnifiedCrossSection.cs`

---

## Phase 3: Road Graph Data Structure

**Goal**: Define the graph types. Nodes = junctions, Edges = road segments between junctions.

### New file: `Terrain/Models/RoadGeometry/RoadGraph.cs` (~200 lines)

**`GraphNode`**:
- `NodeId: int` (auto-generated sequential ID)
- `Position: Vector2` (world position in meters)
- `OsmNodeId: long?` (from OSM, null for PNG/synthetic)
- `Elevation: float` (solved by GraphElevationSolver)
- `IncidentEdgeIds: List<int>`
- `IsFixed: bool` (true for structure endpoints — elevation from StructureElevationCalculator)
- `IsRoundabout: bool`
- `IsDeadEnd: bool` (degree 1)

**`GraphEdge`**:
- `EdgeId: int`
- `StartNodeId: int`, `EndNodeId: int`
- `SplineId: int` (reference to ParameterizedRoadSpline)
- `CrossSectionRange: (int Start, int End)` — indices into the spline's cross-sections for this edge
- `LengthMeters: float`
- `Priority: int`
- `MaterialName: string`
- `IsSelfLoop: bool` (start == end node, e.g., closed loop roads)
- `IsStructure: bool` (bridge/tunnel — fixed elevation, not free in solver)

**`RoadGraph`**:
- `Nodes: Dictionary<int, GraphNode>`
- `Edges: Dictionary<int, GraphEdge>`
- `GetIncidentEdges(nodeId) → List<GraphEdge>`
- `GetAdjacentNodes(nodeId) → List<GraphNode>`
- `GetOppositeNode(edgeId, nodeId) → GraphNode`
- `GetDeadEndNodes() → List<GraphNode>`
- `GetRoundaboutCycles() → List<List<int>>` (node ID lists forming cycles)
- `NodeCount`, `EdgeCount` properties

### Design note: Spline ↔ Edge relationship
One spline may map to **multiple** graph edges (when it passes through mid-path junction nodes and gets split). We do NOT split the actual `ParameterizedRoadSpline` object — instead, each `GraphEdge` references the spline ID + a cross-section index range. This avoids duplicating spline data and keeps the existing pipeline intact.

### Files
- New: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadGraph.cs`

---

## Phase 4: Road Graph Builder

**Goal**: Construct the RoadGraph from the UnifiedRoadNetwork.

### New file: `Terrain/Services/RoadGraphBuilder.cs` (~400 lines)

### Algorithm for OSM splines (have NodeIds):

1. **Collect all node IDs** from all splines' `NodeIds` lists
2. **Build node valence map**: `Dictionary<long, int>` — count how many DIFFERENT splines reference each node ID. Nodes with count ≥ 2 (shared across splines) or with valence ≥ 3 from the original NodeBasedPathConnector analysis are junction candidates.
3. **Create GraphNodes** for each junction OSM node
4. **For each spline**: walk its `NodeIds` list. When encountering a junction node that is NOT at an endpoint (mid-path junction):
   - Create a GraphNode at that position if not already created
   - Split the spline's cross-section range at that point → two GraphEdges
5. **For spline endpoints**: match to existing GraphNodes by OSM node ID, or create new ones
6. **Dead-end splines**: endpoint nodes with degree 1 → `IsDeadEnd = true`

### Algorithm for PNG splines (no NodeIds):

1. **Collect all spline endpoints** (start/end positions)
2. **Cluster by spatial proximity** — same approach as current `NetworkJunctionDetector.ClusterEndpointsIntoJunctions` (Union-Find with distance threshold)
3. **Each cluster → one GraphNode**
4. **Mid-spline crossings**: spatial proximity scan for splines passing near each other → if detected, split at crossing point (use simplified version of current `DetectMidSplineCrossings` logic)
5. **Each segment → one GraphEdge**

### Special handling:
- **Bridge/tunnel splines**: edges marked `IsStructure = true`, endpoint nodes marked `IsFixed = true` if elevation is set by StructureElevationCalculator
- **Roundabout splines**: identified by `IsRoundabout` flag → mark all their nodes `IsRoundabout = true`
- **Self-loops**: spline where start endpoint == end endpoint → single edge with `IsSelfLoop = true`, one shared node

### Integration:
- `UnifiedRoadNetwork.cs` — add `RoadGraph? Graph { get; set; }` property
- `UnifiedRoadNetworkBuilder.cs` — call `RoadGraphBuilder.Build(network)` after spline creation

### Validation:
- Every cross-section belongs to exactly one GraphEdge
- Every GraphEdge references a valid spline
- Every GraphNode has ≥ 1 incident edge
- Sum of all edge CrossSectionRange lengths = total cross-section count

### Files
- New: `BeamNgTerrainPoc/Terrain/Services/RoadGraphBuilder.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedRoadNetwork.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs`

---

## Phase 5: Boundary-Value Elevation Solver

**Goal**: The core new algorithm. Replace Phase 3 (junction harmonization) with graph-based iterative boundary-value elevation solving.

### New file: `Terrain/Algorithms/GraphElevationSolver.cs` (~400 lines)

### Algorithm (Gauss-Seidel with longest-path ordering):

```
Input: RoadGraph with initial elevations from Phase 2 per-spline smoothing

1. INITIALIZE junction elevations:
   For each GraphNode (non-fixed, non-dead-end):
     Collect TargetElevation from each incident edge at this node
     node.Elevation = weighted average (weight = priority × sqrt(roadWidth))

   For dead-end nodes:
     node.Elevation = terrain elevation at that point

   For fixed nodes (bridge/tunnel endpoints):
     node.Elevation = value from StructureElevationCalculator (don't modify)

2. ORDER nodes by distance from network periphery (longest-path ordering):
   Start from dead-end nodes, BFS inward → process periphery first, core last
   This gives Gauss-Seidel faster convergence than arbitrary ordering

3. ITERATE until convergence (max 20 iterations):
   For each GraphNode in order (non-fixed):
     a. For each incident edge:
        - Get the "desired" elevation at this node from the edge's
          terrain-following profile (what Phase 2 computed)
        - Weight by edge priority and road width
     b. node.Elevation = weighted average of desired elevations

     c. For each incident edge (re-smooth immediately — Gauss-Seidel):
        - Re-smooth as boundary-value problem:
          h(startNode) = fixed, h(endNode) = fixed
          Smooth terrain-following profile between boundaries
        - Update cross-section TargetElevations

   Convergence check: max |ΔElevation| across all non-fixed nodes < 0.01m

4. APPLY roundabout constraint (after main iteration converges):
   For each roundabout cycle:
     uniform_elevation = average of cycle node elevations
     Set all cycle nodes to uniform_elevation
     Re-smooth all cycle edges with new boundary values
```

### Boundary-value smoothing method (residual approach):

Add to `OptimizedElevationSmoother.cs`:
```csharp
public float[] SmoothWithBoundaryConditions(
    float[] terrainElevations,
    float startElevation,
    float endElevation,
    RoadSmoothingParameters parameters)
```

**Residual smoothing algorithm**:
1. Compute linear interpolation `L(i)` from `startElevation` to `endElevation`
2. Compute residual: `R(i) = terrainElevation(i) - L(i)`
3. Apply existing Butterworth/box filter to residual R → smoothed residual R'
4. Final elevation: `h(i) = L(i) + R'(i)`
5. Pin boundaries: `h(0) = startElevation`, `h(N-1) = endElevation`
6. Apply slope constraint if enabled

This naturally satisfies boundary conditions because the residual is approximately zero at boundaries (terrain ≈ linear interpolation near junction points after a few iterations).

### Special constraints:
- **Dead-end nodes**: `Elevation = lerp(terrainHeight, solvedElevation, deadEndBlendStrength)`
- **Structure edges**: skip re-smoothing, elevation profile is fixed
- **Self-loops**: `startElevation == endElevation` (periodic boundary)
- **Roundabout cycles**: post-iteration uniform constraint

### New parameters (replace JunctionHarmonizationParameters):
```csharp
public class GraphSolverParameters
{
    public bool Enabled { get; set; } = true;
    public float ConvergenceThresholdMeters { get; set; } = 0.01f;
    public int MaxIterations { get; set; } = 20;
    public float DeadEndTerrainBlendStrength { get; set; } = 1.0f;  // 0=keep, 1=terrain
    public bool EnableRoundaboutUniformConstraint { get; set; } = true;
}
```

### Files
- New: `BeamNgTerrainPoc/Terrain/Algorithms/GraphElevationSolver.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` — add `SmoothWithBoundaryConditions`

---

## Phase 6: Pipeline Integration

**Goal**: Wire the graph builder and elevation solver into UnifiedRoadSmoother, replacing old Phase 3.

### New pipeline in `UnifiedRoadSmoother.SmoothAllRoads()`:

```
Phase 1:   Build unified network             (EXISTING — unchanged)
Phase 1.5: Identify roundabouts              (EXISTING — unchanged)
Phase 1.7: BUILD ROAD GRAPH                  (NEW)
Phase 2:   Per-spline elevation calculation   (EXISTING — unchanged, provides initial estimate)
Phase 2.3: Structure elevation profiles       (EXISTING — unchanged)
Phase 2.5: Banking pre-calculation            (EXISTING — unchanged)
Phase 2.7: GRAPH-BASED ELEVATION SOLVE       (NEW — replaces Phase 2.6 + Phase 3)
Phase 3.5: Banking finalization               (EXISTING — adapted to use graph topology)
Phase 4:   Terrain blending                   (EXISTING — unchanged)
Phase 5:   Material painting                  (EXISTING — unchanged)
```

### Removed from orchestrator:
- Phase 2.6: `RoundaboutElevationHarmonizer.HarmonizeRoundaboutElevations()` — handled by solver's cycle constraint
- Phase 3: `NetworkJunctionDetector.DetectJunctions()` — graph topology replaces detection
- Phase 3: `NetworkJunctionHarmonizer.HarmonizeNetwork()` — eliminated entirely
- OSM junction query (`QueryOsmJunctions` via Overpass API) — topology is structural, no need for external hints

### Method signature simplification:
Remove parameters from `SmoothAllRoads()`:
- `enableCrossroadToTJunctionConversion` — no crossroad/T-junction distinction needed
- `enableExtendedOsmJunctionDetection` — no external junction hints needed
- `globalJunctionDetectionRadius` — topology replaces spatial detection

Add parameter:
- `GraphSolverParameters? graphSolverParameters` — new solver configuration

### Files to modify
- `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` — major restructure of SmoothAllRoads()
- `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` — update call to SmoothAllRoads()
- `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` — update call chain

---

## Phase 7: Banking Adaptation

**Goal**: Adapt banking to use graph topology instead of junction harmonizer's contributor model.

### Changes:

**`BankingOrchestrator.FinalizeBankingAfterHarmonization()`**:
- Currently iterates over `network.Junctions` (from NetworkJunctionDetector) to determine banking behavior
- Change to iterate over `network.Graph.Nodes` where degree ≥ 2
- At each junction node: determine banking behavior from incident GraphEdge priorities
- Graph makes adjacency explicit — no spatial search needed

**`PriorityAwareJunctionBankingCalculator`**:
- Currently receives `JunctionContributor` objects from NetworkJunction
- Change to receive `GraphEdge` objects from `graph.GetIncidentEdges(nodeId)`
- Same priority logic, cleaner data source

**`JunctionBankingAdapter`**:
- Currently uses junction blend distances to adapt banking
- Change to use graph edge lengths and neighbor node elevations
- Simpler: graph node elevation is authoritative

### Files to modify
- `BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Banking/PriorityAwareJunctionBankingCalculator.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Banking/JunctionBankingAdapter.cs`

---

## Phase 8: Parameter Cleanup

**Goal**: Remove obsolete parameters, reorganize by scope, clean up UI bindings.

### Delete/gut `JunctionHarmonizationParameters.cs`:
Remove:
- `JunctionDetectionRadiusMeters` — topology replaces detection
- `JunctionBlendDistanceMeters` — no blend zones
- `BlendFunctionType` — no blend curves
- `EnableEndpointTaper` / `EndpointTaperDistanceMeters` — dead-end handled by solver
- `EndpointTerrainBlendStrength` — moved to GraphSolverParameters
- `EnableCrossroadToTJunctionConversion` — no distinction needed
- `IncludedOsmJunctionTypes` — not needed
- `ExportJunctionDebugImage` — replaced by graph debug output

Keep (move to appropriate location):
- Roundabout settings (`EnableRoundaboutDetection`, `EnableRoundaboutRoadTrimming`, `RoundaboutConnectionRadiusMeters`, etc.) — move to a `RoundaboutParameters` section or keep in JunctionHarmonizationParameters renamed to `RoadNetworkParameters`

### Remove from `RoadSmoothingParameters.cs`:
- `RoadEdgeProtectionBufferMeters` — the protected blending system handles priority-based protection without this heuristic buffer

### Remove from `UnifiedRoadSmoother` signature:
- `enableCrossroadToTJunctionConversion`
- `enableExtendedOsmJunctionDetection`
- `globalJunctionDetectionRadius`

### Remove from `TerrainGenerationState.cs`:
- `EnableCrossroadToTJunctionConversion`
- `EnableExtendedOsmJunctionDetection`
- Related UI bindings in `TerrainMaterialSettings.razor.cs`

### Add `GraphSolverParameters` to UI:
- Convergence threshold, max iterations, dead-end blend strength
- Add to `TerrainGenerationState` and material settings UI

### Parameter scope summary:
| Scope | Parameters |
|-------|-----------|
| Global | ConvergenceThreshold, MaxIterations, TerrainSize, MetersPerPixel |
| Per-material | SmoothingWindow, MaxSlope, Banking, RoadWidth, BlendRange, CrossSectionInterval |
| Per-spline | Priority (from OSM highway type), IsRoundabout, IsStructure |
| Per-chunk (future) | Override any per-material parameter |

### Files to modify
- `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs` — gut and rename
- `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` — remove EdgeProtectionBuffer
- `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs` — remove obsolete, add new
- `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` — update UI

---

## Phase 9: Code Deletion

**Goal**: Remove dead code from the eliminated junction harmonization subsystem.

### Delete files (~3700+ lines):
- `Terrain/Algorithms/NetworkJunctionHarmonizer.cs` (~1789 lines)
- `Terrain/Algorithms/NetworkJunctionDetector.cs` (~1597 lines)
- `Terrain/Algorithms/JunctionSurfaceCalculator.cs` (~331 lines)
- `Terrain/Algorithms/CrossroadToTJunctionConverter.cs`
- `Terrain/Algorithms/JunctionElevationHarmonizer.cs` (single-material variant)

### Evaluate for deletion/simplification:
- `Terrain/Algorithms/RoundaboutElevationHarmonizer.cs` — if fully replaced by solver's cycle constraint, delete. If roundabout-specific logic is needed beyond uniform elevation, simplify.
- `Terrain/Osm/Services/OsmJunctionQueryService.cs` — if no longer querying OSM for junction hints, delete
- `Terrain/Osm/Models/OsmJunction.cs`, `OsmJunctionQueryResult.cs` — delete if service removed

### Clean up references:
- Remove unused `using` statements across all modified files
- Remove dead parameter validation in UI components
- Remove junction debug image export code paths
- Remove `NetworkJunction` and `JunctionContributor` types from `Models/RoadGeometry/` if no longer referenced

### Files to delete
- `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/JunctionSurfaceCalculator.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/CrossroadToTJunctionConverter.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/JunctionElevationHarmonizer.cs`
- (Conditionally) `BeamNgTerrainPoc/Terrain/Algorithms/RoundaboutElevationHarmonizer.cs`
- (Conditionally) `BeamNgTerrainPoc/Terrain/Osm/Services/OsmJunctionQueryService.cs`
- (Conditionally) `BeamNgTerrainPoc/Terrain/Osm/Models/OsmJunction.cs`
- (Conditionally) `BeamNgTerrainPoc/Terrain/Osm/Models/OsmJunctionQueryResult.cs`

---

## Phase 10: Master Spline Export Enhancement

**Goal**: Export long, logically connected master splines per road to BeamNG. This minimizes the number of splines the user must apply decal roads to — one spline per logical road, not one per graph edge.

**Key principle**: The export logic is completely decoupled from the internal graph used for elevation smoothing. The graph splits roads at every junction for solving purposes, but the export re-joins them into long splines for usability in BeamNG.

### Constraint: Format unchanged
The BeamNG spline JSON format is fixed. Metadata goes only in the **spline name** field, kept short.

### Export algorithm: Graph-walk to build long splines

1. **Group graph edges** by logical road identity: `(highway_type, name, ref)` tuple
   - Edges with matching tags belong to the same logical road
2. **Walk the graph** to chain edges of the same group into long continuous paths:
   - At each junction node, follow the outgoing edge that belongs to the same road group
   - At junctions where the road branches (e.g., splits into two carriageways), start a new export spline
   - Stop at dead-ends, material boundaries, or when no same-group edge continues
3. **Export each chain** as one BeamNG master spline with concatenated control points
4. **Unnamed/unref'd roads**: group by `highway_type` alone, but only chain through degree-2 junction nodes (pass-through junctions). At degree-3+ junctions, start a new spline to avoid ambiguous routing.

### Spline naming convention:
```
"primary_RueDesCerises"
"residential_ImpDuPort"
"motorway_A9"
"tertiary_001"          (unnamed road, sequential numbering)
```
Pattern: `{highway_type}_{name|ref|seqNr}` — short and human-readable.

### Result:
- A named road like "Rue des Cerises" that passes through 5 junctions → **1 export spline** (not 6 short segments)
- A highway that splits into on/off ramps → main spline + separate ramp splines
- Unnamed residential streets → one spline per continuous segment between real junctions

### Files to modify
- `BeamNgTerrainPoc/Terrain/Services/MasterSplineExporter.cs`

---

## Phase 11: Verification & Testing

### Functional verification:
1. Generate terrain for Cerbère area (roundabouts, split carriageways, bridges)
2. Generate terrain for dense urban grid (many closely-spaced junctions)
3. Generate terrain for mountain roads (steep terrain, tight hairpins)
4. Verify all junction elevations are continuous (ΔZ = 0 at graph nodes by construction)
5. Verify bridge/tunnel metadata preserved through pipeline
6. Verify master spline export produces valid BeamNG splines
7. Test the "Avenue du Professeur Henri Mary" self-loop case

### Performance verification:
- Solver convergence: should converge in 5-15 iterations for typical maps
- Total solve time: comparable to current Phase 3 (~1s for typical, ~6s for large)
- Memory: graph adds ~10-50KB overhead for typical networks

### Regression checks:
- Compare overall elevation profile quality vs. current pipeline on multiple test maps
- Check for artifacts: elevation oscillation from solver, boundary discontinuities
- Verify protected blending still works correctly (priority-aware, no corruption)

---

## Recommended Session Grouping

| Session | Phases | Focus |
|---------|--------|-------|
| 1 | 1 + 2 | Node IDs + chunk tracking (same files modified) |
| 2 | 3 + 4 | Graph data structure + builder |
| 3 | 5 | Elevation solver (the core algorithm) |
| 4 | 6 | Pipeline integration |
| 5 | 7 + 8 + 9 | Banking + parameter cleanup + code deletion |
| 6 | 10 | Master spline export |
| 7 | 11 | Verification & testing |

---

## Summary: What Gets Created vs. Deleted

### New files (~1200 lines):
| File | Lines (est.) | Phase |
|------|-------------|-------|
| `Terrain/Models/RoadGeometry/RoadGraph.cs` | ~200 | 3 |
| `Terrain/Models/RoadGeometry/OsmWayChunk.cs` | ~50 | 2 |
| `Terrain/Services/RoadGraphBuilder.cs` | ~400 | 4 |
| `Terrain/Algorithms/GraphElevationSolver.cs` | ~400 | 5 |

### Deleted files (~3700+ lines):
| File | Lines | Phase |
|------|-------|-------|
| `NetworkJunctionHarmonizer.cs` | ~1789 | 9 |
| `NetworkJunctionDetector.cs` | ~1597 | 9 |
| `JunctionSurfaceCalculator.cs` | ~331 | 9 |
| `CrossroadToTJunctionConverter.cs` | ~300+ | 9 |
| `JunctionElevationHarmonizer.cs` | ~200+ | 9 |

**Net reduction: ~2500+ lines of heuristic code replaced by ~1200 lines of principled solver logic.**
