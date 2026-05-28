# Topology-First Junction Detection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Propagate OSM node IDs through the spline pipeline and use them for topology-first junction detection, with spatial clustering as fallback for non-OSM data.

**Architecture:** Add `StartOsmNodeId`/`EndOsmNodeId` to `RoadSpline` and `ParameterizedRoadSpline`, propagate from `PathWithMetadata` during spline creation, then pre-union endpoints sharing OSM node IDs before spatial clustering in `NetworkJunctionDetector`.

**Tech Stack:** .NET 9, xUnit, C#

**Spec:** `docs/superpowers/specs/2026-03-26-topology-first-junction-detection-design.md`

---

### Task 1: Add OSM Node ID Properties to RoadSpline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs:110` (after `OsmRoadType`)

- [ ] **Step 1: Add properties**

In `RoadSpline.cs`, after the `OsmRoadType` property (line ~110), add:

```csharp
/// <summary>
///     OSM node ID of the spline's start point, or null if not from OSM / cropped at boundary.
///     Set during spline creation from PathWithMetadata.StartNodeId.
/// </summary>
public long? StartOsmNodeId { get; set; }

/// <summary>
///     OSM node ID of the spline's end point, or null if not from OSM / cropped at boundary.
///     Set during spline creation from PathWithMetadata.EndNodeId.
/// </summary>
public long? EndOsmNodeId { get; set; }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/RoadSpline.cs
git commit -m "feat: add StartOsmNodeId/EndOsmNodeId properties to RoadSpline"
```

---

### Task 2: Add OSM Node ID Properties to ParameterizedRoadSpline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs:123` (after `Layer`)

- [ ] **Step 1: Add properties**

In `ParameterizedRoadSpline.cs`, after the `Layer` property (line ~123), add:

```csharp
/// <summary>
///     OSM node ID of the spline's start point, or null if not from OSM / cropped at boundary.
///     Propagated from RoadSpline during network building.
/// </summary>
public long? StartOsmNodeId { get; set; }

/// <summary>
///     OSM node ID of the spline's end point, or null if not from OSM / cropped at boundary.
///     Propagated from RoadSpline during network building.
/// </summary>
public long? EndOsmNodeId { get; set; }
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/RoadGeometry/ParameterizedRoadSpline.cs
git commit -m "feat: add StartOsmNodeId/EndOsmNodeId properties to ParameterizedRoadSpline"
```

---

### Task 3: Propagate Node IDs from PathWithMetadata to RoadSpline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs:890` (Step 5 — structure paths)
- Modify: `BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs:939` (Step 6 — regular paths)

- [ ] **Step 1: Propagate in Step 5 (structure paths)**

In `OsmGeometryProcessor.cs`, in the Step 5 `RoadSpline` initializer (line ~890), add after `OsmRoadType = pm.Tags.GetValueOrDefault("highway")`:

```csharp
StartOsmNodeId = pm.StartNodeId,
EndOsmNodeId = pm.EndNodeId,
```

The full initializer block becomes:
```csharp
var spline = new RoadSpline(cleanPath, interpolationType)
{
    // Copy structure metadata from PathWithMetadata
    IsBridge = pm.IsBridge,
    IsTunnel = pm.IsTunnel,
    StructureType = pm.StructureType,
    Layer = pm.Layer,
    BridgeStructureType = pm.BridgeStructureType,
    OsmRoadType = pm.Tags.GetValueOrDefault("highway"),
    StartOsmNodeId = pm.StartNodeId,
    EndOsmNodeId = pm.EndNodeId,
};
```

- [ ] **Step 2: Propagate in Step 6 (regular paths)**

In `OsmGeometryProcessor.cs`, in the Step 6 `RoadSpline` initializer (line ~939), add after `OsmRoadType = pm.Tags.GetValueOrDefault("highway")`:

```csharp
StartOsmNodeId = pm.StartNodeId,
EndOsmNodeId = pm.EndNodeId,
```

The full initializer block becomes:
```csharp
var spline = new RoadSpline(cleanPath, interpolationType)
{
    IsBridge = pm.IsBridge,
    IsTunnel = pm.IsTunnel,
    StructureType = pm.StructureType,
    Layer = pm.Layer,
    BridgeStructureType = pm.BridgeStructureType,
    OsmRoadType = pm.Tags.GetValueOrDefault("highway"),
    StartOsmNodeId = pm.StartNodeId,
    EndOsmNodeId = pm.EndNodeId,
};
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Osm/Processing/OsmGeometryProcessor.cs
git commit -m "feat: propagate OSM node IDs from PathWithMetadata to RoadSpline"
```

---

### Task 4: Propagate Node IDs from RoadSpline to ParameterizedRoadSpline

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs:116` (after `BridgeStructureType`)

- [ ] **Step 1: Propagate in UnifiedRoadNetworkBuilder**

In `UnifiedRoadNetworkBuilder.cs`, in the `ParameterizedRoadSpline` initializer (line ~116), add after `BridgeStructureType = spline.BridgeStructureType`:

```csharp
StartOsmNodeId = spline.StartOsmNodeId,
EndOsmNodeId = spline.EndOsmNodeId,
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/UnifiedRoadNetworkBuilder.cs
git commit -m "feat: propagate OSM node IDs from RoadSpline to ParameterizedRoadSpline"
```

---

### Task 5: Update Test Helpers to Support OSM Node IDs

**Files:**
- Modify: `BeamNgTerrainPoc.Tests/Elevation/RoadNetworkTestHelpers.cs:31`

- [ ] **Step 1: Add optional node ID parameters to CreateParameterizedSpline**

In `RoadNetworkTestHelpers.cs`, add two optional parameters to `CreateParameterizedSpline` (line ~42, after `excludeTunnels`):

```csharp
public static ParameterizedRoadSpline CreateParameterizedSpline(
    int splineId,
    Vector2 start,
    Vector2 end,
    string osmRoadType = "primary",
    int priority = 50,
    float roadWidth = 8f,
    bool isRoundabout = false,
    bool isBridge = false,
    bool isTunnel = false,
    bool excludeBridges = true,
    bool excludeTunnels = true,
    long? startOsmNodeId = null,
    long? endOsmNodeId = null)
{
    var spline = CreateStraightSpline(start, end);
    spline.StartOsmNodeId = startOsmNodeId;
    spline.EndOsmNodeId = endOsmNodeId;
    return new ParameterizedRoadSpline
    {
        Spline = spline,
        Parameters = new RoadSmoothingParameters
        {
            RoadWidthMeters = roadWidth,
            TerrainAffectedRangeMeters = 6f,
            CrossSectionIntervalMeters = 0.5f,
            ExcludeBridgesFromTerrain = excludeBridges,
            ExcludeTunnelsFromTerrain = excludeTunnels
        },
        MaterialName = "asphalt",
        SplineId = splineId,
        OsmRoadType = osmRoadType,
        Priority = priority,
        IsRoundabout = isRoundabout,
        IsBridge = isBridge,
        IsTunnel = isTunnel,
        StartOsmNodeId = startOsmNodeId,
        EndOsmNodeId = endOsmNodeId,
    };
}
```

- [ ] **Step 2: Build tests to verify compilation**

Run: `dotnet build BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: Build succeeded

- [ ] **Step 3: Run existing tests to verify no regression**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: All existing tests pass (new optional parameters have defaults, no callers change)

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Elevation/RoadNetworkTestHelpers.cs
git commit -m "feat: add OSM node ID parameters to test helper CreateParameterizedSpline"
```

---

### Task 6: Write Failing Tests for Topology-First Junction Detection

**Files:**
- Create: `BeamNgTerrainPoc.Tests/Junction/TopologyJunctionDetectionTests.cs`

- [ ] **Step 1: Create test file with all test cases**

Create the directory and file `BeamNgTerrainPoc.Tests/Junction/TopologyJunctionDetectionTests.cs`:

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Algorithms;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;
using BeamNgTerrainPoc.Tests.Elevation;

namespace BeamNgTerrainPoc.Tests.Junction;

public class TopologyJunctionDetectionTests
{
    /// <summary>
    ///     Three splines sharing OSM nodes at endpoints form a single junction
    ///     even with a tiny detection radius that would fail spatial clustering.
    /// </summary>
    [Fact]
    public void SharedNodePreUnion_ThreeSplinesShareNode_SingleJunction()
    {
        // Shared OSM node ID 100 at (150, 150)
        // Spline 1: (10,150) → (150,150) with EndNodeId=100
        // Spline 2: (150,150) → (290,150) with StartNodeId=100
        // Spline 3: (150,290) → (150,150) with EndNodeId=100
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 100);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: 100);
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 150), endOsmNodeId: 100);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2, s3 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        // Use tiny radius (0.1m) — too small for spatial clustering but topology should still work
        var junctions = detector.DetectJunctions(network, detectionRadiusOverride: 0.1f);

        // All three spline endpoints at (150,150) should be in one junction
        var junctionsWithThreeContributors = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 3)
            .ToList();

        Assert.Single(junctionsWithThreeContributors);
    }

    /// <summary>
    ///     Mix of OSM-sourced (with node IDs) and non-OSM (null node IDs) splines.
    ///     OSM splines connect via topology, non-OSM via spatial clustering.
    /// </summary>
    [Fact]
    public void MixedTopologyAndSpatial_OsmAndPngSplines_BothClustered()
    {
        // OSM splines share node ID 200 at (150,150)
        var osmS1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 200);
        var osmS2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: 200);

        // PNG spline — no node IDs, relies on spatial clustering
        // Endpoint at (150, 152) is within 5m of (150, 150)
        var pngS3 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 152));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { osmS1, osmS2, pngS3 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        // All three should end up in the same junction (topology + spatial)
        var junctionsWithThreeEndpoints = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 3)
            .ToList();

        Assert.Single(junctionsWithThreeEndpoints);
    }

    /// <summary>
    ///     When all node IDs are null (PNG pipeline), behavior is identical to pre-change:
    ///     pure spatial clustering.
    /// </summary>
    [Fact]
    public void PngPipelineNoNodeIds_PureSpatialClustering_BehaviorUnchanged()
    {
        // Three splines meeting near (150,150) — all null node IDs (PNG pipeline)
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150));
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150));
        var s3 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            3, new(150, 290), new(150, 150));

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2, s3 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        // Should still cluster via spatial proximity (default 5m radius)
        var junctionsWithThreeEndpoints = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 3)
            .ToList();

        Assert.Single(junctionsWithThreeEndpoints);
    }

    /// <summary>
    ///     Bridge (layer=1) and road (layer=0) sharing an OSM node are in the same junction.
    /// </summary>
    [Fact]
    public void BridgeRoadSharedNode_DifferentLayers_SameJunction()
    {
        // Road ends at node 300, bridge starts at node 300
        var road = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 300);
        var bridge = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(250, 150), isBridge: true, startOsmNodeId: 300);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { road, bridge })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        // Tiny radius — spatial would fail, topology should succeed
        var junctions = detector.DetectJunctions(network, detectionRadiusOverride: 0.1f);

        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }

    /// <summary>
    ///     Node ID appearing on only one endpoint does not cause errors.
    /// </summary>
    [Fact]
    public void SingleEndpointNode_NoPreUnion_HandledNormally()
    {
        // Only one spline references node 400 — no pre-union partner
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 400);
        // s2 is far away — no spatial or topology match
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(500, 500), new(600, 500), startOsmNodeId: 500);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        // Each endpoint should be in its own isolated junction (Endpoint type)
        // No crash, no spurious merging
        Assert.True(junctions.Count >= 2);
        Assert.All(junctions, j => Assert.True(j.Contributors.Count <= 2));
    }

    /// <summary>
    ///     Cropped path (null node ID) near another endpoint still clusters spatially.
    /// </summary>
    [Fact]
    public void CroppedBoundaryFallback_NullNodeIdNearEndpoint_SpatialClusters()
    {
        // s1 has an OSM node ID at its end
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 600);
        // s2 was cropped at terrain boundary — null start node ID but co-located
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: null);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        // Should cluster via spatial proximity despite mismatched node IDs
        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }

    /// <summary>
    ///     Simulates merged paths: outer node IDs survive merges and still form correct junctions.
    ///     Path A (nodes 800→801) merged with Path B (nodes 801→802) produces merged spline
    ///     with StartNodeId=800 and EndNodeId=802. Node 801 is consumed (interior).
    ///     The merged spline's EndNodeId=802 should still junction with another spline's StartNodeId=802.
    /// </summary>
    [Fact]
    public void MergedPathNodeIds_OuterNodesFormJunctions()
    {
        // Simulates post-merge: merged spline kept outer node IDs 800 and 802
        var merged = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(200, 150), startOsmNodeId: 800, endOsmNodeId: 802);
        // Another spline starts at the same OSM node 802
        var next = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(200, 150), new(350, 150), startOsmNodeId: 802);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { merged, next })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        // Tiny radius — only topology should connect them
        var junctions = detector.DetectJunctions(network, detectionRadiusOverride: 0.1f);

        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }

    /// <summary>
    ///     Two distinct OSM node IDs that happen to be co-located still form one junction
    ///     (spatial fallback catches them even though topology sees them as separate).
    /// </summary>
    [Fact]
    public void DifferentNodeIdsSameLocation_SpatialFallbackMerges()
    {
        // s1 ends at node 700, s2 starts at node 701 — different IDs, same location
        var s1 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            1, new(10, 150), new(150, 150), endOsmNodeId: 700);
        var s2 = RoadNetworkTestHelpers.CreateParameterizedSpline(
            2, new(150, 150), new(290, 150), startOsmNodeId: 701);

        var network = new UnifiedRoadNetwork();
        foreach (var s in new[] { s1, s2 })
            RoadNetworkTestHelpers.AddSplineWithCrossSections(network, s);

        var detector = new NetworkJunctionDetector();
        var junctions = detector.DetectJunctions(network);

        // Spatial fallback should merge them even though node IDs differ
        var sharedJunction = junctions
            .Where(j => j.Contributors.Count(c => c.IsSplineStart || c.IsSplineEnd) >= 2)
            .ToList();

        Assert.Single(sharedJunction);
    }
}
```

- [ ] **Step 2: Build test project to verify compilation**

Run: `dotnet build BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: Build succeeded

**Note:** Tests require a `detectionRadiusOverride` parameter on `DetectJunctions` that doesn't exist yet. Two options:
- If the build fails because `detectionRadiusOverride` doesn't exist: that's expected. The tests that use it (`SharedNodePreUnion`, `BridgeRoadSharedNode`) will be uncompilable until Task 7 adds the parameter. You can temporarily comment out the `detectionRadiusOverride:` argument to verify the other tests compile, or proceed to Task 7 first.
- Alternatively, build all tasks together and run tests after Task 7.

- [ ] **Step 3: Run tests to verify they fail (for topology-dependent ones)**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~TopologyJunctionDetection"`
Expected: `SharedNodePreUnion` and `BridgeRoadSharedNode` FAIL (no topology pre-union yet). Others may pass (spatial clustering works for co-located endpoints).

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc.Tests/Junction/TopologyJunctionDetectionTests.cs
git commit -m "test: add failing tests for topology-first junction detection"
```

---

### Task 7: Implement Topology Pre-Union in NetworkJunctionDetector

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs:37` (DetectJunctions signature)
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs:204` (ClusterEndpointsIntoJunctions)

This is the core implementation task. Two changes:

1. Add optional `detectionRadiusOverride` parameter to `DetectJunctions` (for testability)
2. Add topology pre-union pass before spatial Union-Find in `ClusterEndpointsIntoJunctions`

- [ ] **Step 1: Add detectionRadiusOverride parameter to DetectJunctions**

In `NetworkJunctionDetector.cs`, change the `DetectJunctions` signature (line ~37) from:

```csharp
public List<NetworkJunction> DetectJunctions(
    UnifiedRoadNetwork network)
```

to:

```csharp
public List<NetworkJunction> DetectJunctions(
    UnifiedRoadNetwork network,
    float? detectionRadiusOverride = null)
```

Then at line ~60, change:

```csharp
const float defaultDetectionRadius = 5.0f;
var junctions = ClusterEndpointsIntoJunctions(endpoints, network, defaultDetectionRadius);
```

to:

```csharp
var defaultDetectionRadius = detectionRadiusOverride ?? 5.0f;
var junctions = ClusterEndpointsIntoJunctions(endpoints, network, defaultDetectionRadius);
```

And at line ~65, change:

```csharp
var tJunctionCount = DetectTJunctions(junctions, network, spatialIndex, defaultDetectionRadius);
```

(this already uses `defaultDetectionRadius` as a variable, so no change needed if it was already a var — just ensure it's consistent).

Also at line ~71:

```csharp
var midSplineCrossings = DetectMidSplineCrossings(network, spatialIndex, defaultDetectionRadius, junctions);
```

(same — just ensure it uses the local variable, not a const).

- [ ] **Step 2: Add topology pre-union pass**

In `ClusterEndpointsIntoJunctions` (line ~204), after the Union-Find initialization (line ~250, after `parent[i] = i;`) and BEFORE the spatial neighbor loop (line ~253), insert the topology pre-union pass:

```csharp
// ── Topology pre-union: group endpoints sharing the same OSM node ID ──
// This ensures shared OSM nodes form junctions regardless of detection radius.
// Endpoints without node IDs (PNG pipeline, cropped boundaries) are skipped
// and handled by the spatial fallback below.
var osmNodeToEndpointIndices = new Dictionary<long, List<int>>();
for (var i = 0; i < endpoints.Count; i++)
{
    var ep = endpoints[i];
    var spline = network.GetSplineById(ep.OwnerSplineId);
    if (spline == null) continue;

    long? nodeId = ep.IsSplineStart ? spline.StartOsmNodeId
                 : ep.IsSplineEnd   ? spline.EndOsmNodeId
                 : null;

    if (nodeId == null) continue;

    if (!osmNodeToEndpointIndices.TryGetValue(nodeId.Value, out var list))
    {
        list = [];
        osmNodeToEndpointIndices[nodeId.Value] = list;
    }
    list.Add(i);
}

// Pre-union all endpoints sharing the same OSM node
var topologyUnionCount = 0;
foreach (var (_, indices) in osmNodeToEndpointIndices)
{
    if (indices.Count < 2) continue;
    var first = indices[0];
    for (var k = 1; k < indices.Count; k++)
    {
        Union(parent, rank, first, indices[k]);
        topologyUnionCount++;
    }
}

if (topologyUnionCount > 0)
    TerrainLogger.Info($"  Topology pre-union: {topologyUnionCount} endpoint pair(s) connected via shared OSM node IDs ({osmNodeToEndpointIndices.Count} unique nodes)");
```

The full `ClusterEndpointsIntoJunctions` method structure should now be:

```
1. Pre-compute detection radii           (existing, unchanged)
2. Build spatial grid index              (existing, unchanged)
3. Union-Find initialization             (existing, unchanged)
3b. ── NEW: Topology pre-union pass ──
4. Spatial neighbor loop                 (existing, unchanged)
5. Group by root representative          (existing, unchanged)
6. Build junctions from clusters         (existing, unchanged)
```

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 4: Run ALL tests**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: ALL tests pass including:
- All new `TopologyJunctionDetectionTests`
- All existing `NetworkElevationGraphTests` (regression check)
- All existing `ChainElevationFilteringTests` (regression check)
- All existing `BridgeElevationChainingTests` (regression check)

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionDetector.cs
git commit -m "feat: topology-first junction detection via OSM node ID pre-union"
```

---

### Task 8: Verify Existing Tests Still Pass (Full Regression Check)

**Files:** None modified — verification only.

- [ ] **Step 1: Run complete test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj -v normal`
Expected: ALL tests pass. Pay attention to:
- `NetworkElevationGraphTests` — chains still built correctly
- `ChainElevationFilteringTests` — elevation smoothing unchanged
- `BridgeElevationChainingTests` — bridge chaining unchanged
- `NodeBasedPathConnectorTests` — path merging unchanged
- All `DecalRoad*Tests` — road generation unchanged

- [ ] **Step 2: Build main application**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded (verifies no breaking changes in main app)

- [ ] **Step 3: Commit (if any fixups were needed)**

Only if fixes were applied during this task.
