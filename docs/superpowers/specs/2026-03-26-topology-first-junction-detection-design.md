# Topology-First Junction Detection

**Date:** 2026-03-26
**Branch:** `feature/relation-protected-junction-blocking`
**Status:** Design approved, pending implementation

## Problem

`NetworkJunctionDetector.ClusterEndpointsIntoJunctions()` discovers road junctions by spatially clustering spline endpoints within a configurable detection radius (default 5m). This is a lossy re-discovery of topology that OSM data already defines explicitly via shared node IDs.

With `disableSplineMerging=true` (Part 1 of the segment-based architecture), every OSM way becomes a separate spline. Connections that were previously internal to merged splines are now spline boundaries. More boundaries means more sensitivity to the detection radius parameter. In practice, the radius had to be increased from 5m to 10m to avoid missed junctions — a fragile workaround.

### Root Cause

OSM node IDs (`StartNodeId`, `EndNodeId`) are stored in `PathWithMetadata` and used during path merging, but **discarded** when converting to `RoadSpline`. The junction detector then has to re-discover connections geometrically.

### Example

Three OSM ways share node references:
- Way 38103278 ends at node `5119450792`
- Way 1133006487 (bridge) connects `5119450792` to `10563722464`
- Way 1133006486 starts at `10563722464`

The shared node IDs **are** the junction definitions. No radius needed.

## Solution: Topology-First with Spatial Fallback

Propagate OSM node IDs through the full pipeline and use them as the primary junction detection mechanism, with spatial clustering as a fallback for non-OSM data and cropped boundaries.

## Design

### 1. Data Propagation

Add two nullable properties to carry OSM node IDs through the spline pipeline:

**`RoadSpline`** (after existing `OsmRoadType` property):
```csharp
/// <summary>
///     OSM node ID of the spline's start point, or null if not from OSM / cropped at boundary.
/// </summary>
public long? StartOsmNodeId { get; set; }

/// <summary>
///     OSM node ID of the spline's end point, or null if not from OSM / cropped at boundary.
/// </summary>
public long? EndOsmNodeId { get; set; }
```

**`ParameterizedRoadSpline`** (matching properties):
```csharp
public long? StartOsmNodeId { get; set; }
public long? EndOsmNodeId { get; set; }
```

**`OsmGeometryProcessor`** — propagate in both Step 5 (structure paths, ~line 890) and Step 6 (regular paths, ~line 939):
```csharp
StartOsmNodeId = pm.StartNodeId,
EndOsmNodeId = pm.EndNodeId,
```

**`UnifiedRoadNetworkBuilder.cs`** (line ~116, where `ParameterizedRoadSpline` is created from `RoadSpline`) — propagate forward:
```csharp
StartOsmNodeId = spline.StartOsmNodeId,
EndOsmNodeId = spline.EndOsmNodeId,
```

### 2. Topology-First Pre-Union in Junction Detector

In `NetworkJunctionDetector.ClusterEndpointsIntoJunctions()`, **before** the spatial Union-Find loop, insert a topology pass:

```
Algorithm:
1. Build Dictionary<long, List<int>> mapping OSM node ID -> endpoint indices
2. For each endpoint:
   - If IsSplineStart, look up owning spline's StartOsmNodeId
   - If IsSplineEnd, look up owning spline's EndOsmNodeId
   - If node ID is non-null, add endpoint index to the node's list
3. For each node ID with 2+ endpoints, union all indices together
4. Proceed with existing spatial Union-Find (which handles null-node-ID endpoints)
```

The spatial loop still runs for all endpoints but finds topology-connected ones already unioned. No behavior change for endpoints without node IDs.

### 3. Downstream Benefits (No Changes Needed)

These systems benefit automatically from better junction detection:

- **`NetworkElevationGraph`** — uses junction contributor data to map spline endpoints to elevation nodes. The 2m endpoint clustering tolerance (line 158) becomes a fallback rather than the primary mechanism.
- **`DecalRoadOverlapPostProcessor`** — overlap detection uses junction geometry. Better junctions = better overlap masking.
- **`StructureElevationIntegrator`** — bridge entry/exit elevation matching uses connecting road proximity. Better junctions improve the search.

### 4. Both Code Paths (Merged and Unmerged)

**Unmerged (`disableSplineMerging=true`):** Each OSM way is one spline. `StartNodeId`/`EndNodeId` map directly to the way's first/last OSM node. Every shared node creates a pre-union.

**Merged (`disableSplineMerging=false`):** The merger combines paths at shared nodes. The inner node is consumed; outer nodes survive as the merged spline's `StartNodeId`/`EndNodeId`. These outer nodes are the true topology endpoints and correctly identify junctions at the merged spline's boundaries.

Both cases are safe because:
- Merge operations (`MergeEndToStart`, etc.) correctly assign the outer node IDs
- Null node IDs (from cropped boundaries) are ignored by topology pass, handled by spatial fallback
- No existing behavior is removed — topology is additive

## Testing

New test class: `TopologyJunctionDetectionTests`

| Test | Description |
|------|-------------|
| SharedNodePreUnion | 3 splines sharing an OSM node at endpoints -> single junction regardless of detection radius |
| MixedTopologyAndSpatial | Some endpoints with node IDs, some null -> topology for former, spatial for latter |
| MergedPathNodeIds | After merge operations, outer node IDs correctly identify junctions |
| NoRegressionSpatialOnly | All null node IDs -> behavior identical to before |
| BridgeRoadSharedNode | Bridge (layer=1) and road (layer=0) sharing a node -> same junction |
| SingleEndpointNode | Node ID appearing on only one endpoint -> no pre-union, handled normally |
| CroppedBoundaryFallback | Cropped path with null node ID near another endpoint -> spatial clustering still works |
| PngPipelineNoNodeIds | Non-OSM pipeline (PNG road masks) where all splines have null node IDs -> pure spatial clustering, identical behavior to pre-change |

## Files Changed

| File | Change |
|------|--------|
| `RoadSpline.cs` | Add `StartOsmNodeId`, `EndOsmNodeId` properties |
| `ParameterizedRoadSpline.cs` | Add `StartOsmNodeId`, `EndOsmNodeId` properties |
| `OsmGeometryProcessor.cs` | Propagate node IDs in Step 5 and Step 6 |
| `UnifiedRoadNetworkBuilder.cs` (line ~116) | Propagate node IDs to `ParameterizedRoadSpline` |
| `NetworkJunctionDetector.cs` | Add topology pre-union before spatial clustering |
| New: test class | `TopologyJunctionDetectionTests` |
