# Spline-Based Corridor Footprint for Overlap Detection

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-DecalRoad-layer surface footprint with a single per-spline corridor using the full road surface width, fixing false negatives (missing edge lines/markings) caused by narrow layer widths.

**Architecture:** Collect spline centerline + road width during the generation loop, pass to the post-processor, and build `SurfaceFootprintIndex` from these synthetic full-width corridors instead of from individual generated DecalRoad layers. This eliminates the dependency on which layers happen to be `InterruptAtJunctions=false` and ensures the footprint always covers the full road surface.

**Tech Stack:** .NET 9 / C#, existing `SurfaceFootprintIndex` spatial hash grid, existing `UnifiedCrossSection` centerline data.

---

## Background: Why the Current Approach Fails

The post-processor currently builds `SurfaceFootprintIndex` from generated DecalRoad objects where `InterruptAtJunctions=false`. These are layers like TreadMarks and Wear patterns — all using `IsLaneWidth=true`, which gives them a width of `roadWidth/laneCount` (e.g., 3.5m for a 7m 2-lane road). The AIRoad layer has the full road width but is classified as `IsAIRoad` and excluded from the footprint.

**Result:** The footprint only covers ±2.25m from center (TreadMarks 3.5m + 0.5m margin), but the actual road surface is ±3.5m. Edge lines of crossing roads at 3.5m from center fall outside the footprint and are never interrupted.

## Key Insight

Every `GeneratedDecalRoad` already has a `SplineId` linking it to its parent spline. The generator already knows the full road width (`EffectiveMasterSplineWidthMeters`). The existing `RoadCorridorBuilder` already builds per-spline corridors from cross-sections — but the post-processor ignores them and builds its own narrow footprint from layers.

## Design Decisions

1. **Width for the footprint = `EffectiveMasterSplineWidthMeters / 2 + margin`** — this is the road's physical surface half-width. NOT the `CorridorHalfWidth` from `RoadCorridorBuilder` (which includes edge blend extents and would be too wide).

2. **Reuse `SurfaceFootprintIndex` spatial hash** — it already has the right performance characteristics (O(1) lookup via grid cells). Just need a new `AddSplineSurface()` method that accepts centerline points + road width instead of DecalRoad nodes.

3. **Collect centerlines in the generator loop** — the generator already iterates splines and sub-samples cross-sections. Collecting `(splineId, roadWidth, centerlinePoints)` is trivial and avoids duplicating the spline iteration.

4. **New data model: `SplineSurfaceData`** — lightweight record holding `SplineId`, `RoadHalfWidth`, and sampled 2D centerline points. Passed from generator to post-processor.

5. **No changes to `RoadCorridor`/`RoadCorridorBuilder`/`RoadCorridorOverlapChecker`** — these are the old corridor system. They compute a different (wider) width for a different purpose. Leave them untouched; they may be useful for other features or can be cleaned up separately.

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `BeamNgTerrainPoc/Terrain/Models/DecalRoad/SplineSurfaceData.cs` | **Create** | Lightweight record: SplineId, RoadHalfWidth, CenterlinePoints |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/SurfaceFootprintIndex.cs` | **Modify** | Add `AddSplineSurface(SplineSurfaceData)` method |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadOverlapPostProcessor.cs` | **Modify** | Accept `IReadOnlyList<SplineSurfaceData>` instead of classifying surface roads; build index from spline surfaces |
| `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs` | **Modify** | Collect `SplineSurfaceData` per spline during generation loop; pass to post-processor |

---

## Tasks

### Task 1: Create `SplineSurfaceData` Model

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Models/DecalRoad/SplineSurfaceData.cs`

- [ ] **Step 1: Create the model file**

```csharp
using System.Numerics;

namespace BeamNgTerrainPoc.Terrain.Models.DecalRoad;

/// <summary>
/// Represents a road spline's physical surface footprint for overlap detection.
/// Built from the spline's sampled centerline and full road surface width.
/// </summary>
public sealed class SplineSurfaceData
{
    public required int SplineId { get; init; }

    /// <summary>
    /// Half-width of the road surface in meters (EffectiveMasterSplineWidthMeters / 2 + margin).
    /// </summary>
    public required float SurfaceHalfWidth { get; init; }

    /// <summary>
    /// Sampled 2D centerline points along the spline, in BeamNG world coordinates.
    /// Same spacing as DecalRoad node generation (NodeSpacingMeters).
    /// </summary>
    public required IReadOnlyList<Vector2> CenterlinePoints { get; init; }
}
```

- [ ] **Step 2: Build and verify no compilation errors**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Models/DecalRoad/SplineSurfaceData.cs
git commit -m "feat: add SplineSurfaceData model for spline-based overlap footprint"
```

---

### Task 2: Add `AddSplineSurface()` to `SurfaceFootprintIndex`

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/SurfaceFootprintIndex.cs`

The existing `AddRoad(GeneratedDecalRoad)` creates segments from DecalRoad nodes where each node carries its own width. We need `AddSplineSurface(SplineSurfaceData)` that creates segments from centerline points with a uniform road surface width.

- [ ] **Step 1: Add the `AddSplineSurface` method**

Add this method to `SurfaceFootprintIndex`, after the existing `AddRoad` method:

```csharp
/// <summary>
/// Adds a spline's full road surface to the spatial index.
/// Uses the spline centerline and uniform surface half-width instead of
/// individual DecalRoad layer widths.
/// </summary>
public void AddSplineSurface(SplineSurfaceData surface)
{
    var points = surface.CenterlinePoints;
    // Each segment uses the full surface width (surfaceHalfWidth * 2)
    // so that IsPointInSegment's halfWidth calculation (width/2 + Margin) yields
    // surfaceHalfWidth + Margin — exactly the detection radius we want.
    var segmentWidth = surface.SurfaceHalfWidth * 2f;

    for (int i = 0; i < points.Count - 1; i++)
    {
        var seg = new FootprintSegment(
            points[i],
            points[i + 1],
            segmentWidth,
            segmentWidth,
            surface.SplineId);

        var halfW = surface.SurfaceHalfWidth + Margin;
        var minX = MathF.Min(seg.A.X, seg.B.X) - halfW;
        var minY = MathF.Min(seg.A.Y, seg.B.Y) - halfW;
        var maxX = MathF.Max(seg.A.X, seg.B.X) + halfW;
        var maxY = MathF.Max(seg.A.Y, seg.B.Y) + halfW;

        var cellMinX = (int)MathF.Floor(minX / CellSize);
        var cellMinY = (int)MathF.Floor(minY / CellSize);
        var cellMaxX = (int)MathF.Floor(maxX / CellSize);
        var cellMaxY = (int)MathF.Floor(maxY / CellSize);

        for (int cx = cellMinX; cx <= cellMaxX; cx++)
        for (int cy = cellMinY; cy <= cellMaxY; cy++)
        {
            var key = (cx, cy);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = [];
                _grid[key] = list;
            }
            list.Add(seg);
        }
    }
}
```

**Note on width encoding:** `IsPointInSegment` computes `halfWidth = Max(WidthA, WidthB) / 2 + Margin`. By setting `WidthA = WidthB = SurfaceHalfWidth * 2`, the check becomes `SurfaceHalfWidth + Margin`, which is the correct detection radius.

- [ ] **Step 2: Add the using directive**

Add at the top of the file:
```csharp
using System.Numerics;  // already present
// Ensure this is also present:
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;  // already present — SplineSurfaceData lives here
```

No new using needed — `SplineSurfaceData` and `Vector2` are already in scope.

- [ ] **Step 3: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/SurfaceFootprintIndex.cs
git commit -m "feat: add AddSplineSurface() to SurfaceFootprintIndex for full-width corridor detection"
```

---

### Task 3: Update `DecalRoadOverlapPostProcessor` to Use Spline Surfaces

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadOverlapPostProcessor.cs`

The key change: the post-processor receives `IReadOnlyList<SplineSurfaceData>` instead of classifying roads into surface/interruptable and building the index from surface roads. All non-AI roads become candidates for overlap splitting.

- [ ] **Step 1: Change `Process()` signature and classification logic**

Replace the entire `Process` method with:

```csharp
/// <summary>
/// Processes all generated DecalRoads: builds a surface footprint from per-spline
/// corridor data (full road width), then splits interruptable roads where they
/// overlap another spline's surface.
/// </summary>
public static List<GeneratedDecalRoad> Process(
    List<GeneratedDecalRoad> allRoads,
    IReadOnlyList<SplineSurfaceData> splineSurfaces,
    IReadOnlyDictionary<int, HashSet<int>>? continuityLookup)
{
    // 1. Build footprint index from per-spline full road surface corridors
    var index = new SurfaceFootprintIndex();
    foreach (var surface in splineSurfaces)
        index.AddSplineSurface(surface);

    // 2. Classify roads — only AI vs interruptable matters now
    var results = new List<GeneratedDecalRoad>(allRoads.Count);
    var interruptableNonRoundabout = new List<GeneratedDecalRoad>();
    var interruptableRoundabout = new List<GeneratedDecalRoad>();

    foreach (var road in allRoads)
    {
        if (road.IsAIRoad || !road.InterruptAtJunctions)
        {
            // AI roads and non-interruptable roads pass through unchanged
            results.Add(road);
        }
        else if (road.IsRoundaboutRoad)
        {
            interruptableRoundabout.Add(road);
        }
        else
        {
            interruptableNonRoundabout.Add(road);
        }
    }

    // 3. Process non-roundabout interruptable roads
    foreach (var road in interruptableNonRoundabout)
        results.AddRange(SplitOpenRoad(road, index, continuityLookup));

    // 4. Process roundabout interruptable roads last
    foreach (var road in interruptableRoundabout)
        results.AddRange(SplitClosedLoopRoad(road, index, continuityLookup));

    return results;
}
```

**Key behavioral changes:**
- Non-interruptable roads (TreadMarks, Wear, etc.) now pass through unchanged alongside AI roads — they're no longer used to build the footprint
- The footprint is built from `splineSurfaces` which uses the full road width
- Classification is simpler: AI/non-interruptable → pass through, interruptable → split

**No changes needed** to `SplitOpenRoad`, `SplitClosedLoopRoad`, or `ComputeOverlapMask` — they already work with `SurfaceFootprintIndex` and `SplineId` exclusion.

- [ ] **Step 2: Add using directive**

Ensure this using is present at the top of the file:
```csharp
using BeamNgTerrainPoc.Terrain.Models.DecalRoad;  // already present
```

No new using needed.

- [ ] **Step 3: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build errors in `DecalRoadGenerator.cs` because `Process()` signature changed (this is expected — fixed in Task 4).

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadOverlapPostProcessor.cs
git commit -m "refactor: use spline surface corridors instead of DecalRoad layers for overlap footprint"
```

---

### Task 4: Collect Spline Surfaces in `DecalRoadGenerator` and Wire Up

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs`

The generator already iterates splines, sub-samples cross-sections, and knows the road width. We just need to collect `SplineSurfaceData` per spline and pass it to the post-processor.

- [ ] **Step 1: Collect spline surfaces during the generation loop**

In the `Generate()` method, add a `splineSurfaces` list before the `foreach` loop over splines, and populate it inside the loop.

Find this block (around line 42-81):
```csharp
var results = new List<GeneratedDecalRoad>();

// Build continuity lookup for post-processor overlap exemptions
var continuityLookup = BuildContinuityLookup(network.Junctions);

// Generate all DecalRoads uninterrupted (no corridor checking during generation)
foreach (var spline in network.Splines)
{
```

Replace with:
```csharp
var results = new List<GeneratedDecalRoad>();
var splineSurfaces = new List<SplineSurfaceData>();

// Build continuity lookup for post-processor overlap exemptions
var continuityLookup = BuildContinuityLookup(network.Junctions);

// Generate all DecalRoads uninterrupted (no corridor checking during generation)
foreach (var spline in network.Splines)
{
```

Then, inside the loop, after the `sampledSections` computation (which happens inside `GenerateForSpline`), we need to collect the centerline. The problem is `sampledSections` is computed inside `GenerateForSpline`, not in `Generate`. Two options:

**Option A (minimal change):** Sub-sample cross-sections again in `Generate()` just for the surface data. This duplicates the sub-sampling but is architecturally clean — the generator loop already has the cross-sections and road width.

**Option B:** Have `GenerateForSpline` return the sampled centerline alongside the roads.

**Choose Option A** because it keeps `GenerateForSpline` unchanged and the sub-sampling is cheap (just filtering by distance).

After this block in the loop (around line 72-80):
```csharp
var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
if (crossSections.Count < 2)
    continue;

var splineResults = GenerateForSpline(
    spline, layerSet, crossSections,
    heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
    settings.NodeSpacingMeters, settings);
results.AddRange(splineResults);
```

Replace with:
```csharp
var crossSections = network.GetCrossSectionsForSpline(spline.SplineId).ToList();
if (crossSections.Count < 2)
    continue;

// Collect spline surface data for overlap detection footprint
var surfaceSections = SubSampleCrossSections(crossSections, settings.NodeSpacingMeters);
if (surfaceSections.Count >= 2)
{
    var roadWidth = spline.Parameters.EffectiveMasterSplineWidthMeters;
    splineSurfaces.Add(new SplineSurfaceData
    {
        SplineId = spline.SplineId,
        SurfaceHalfWidth = roadWidth / 2f,
        CenterlinePoints = surfaceSections.Select(cs => cs.CenterPoint).ToList()
    });
}

var splineResults = GenerateForSpline(
    spline, layerSet, crossSections,
    heightMap, metersPerPixel, terrainSizePixels, terrainBaseHeight,
    settings.NodeSpacingMeters, settings);
results.AddRange(splineResults);
```

- [ ] **Step 2: Update the post-processor call**

Find (around line 84):
```csharp
results = DecalRoadOverlapPostProcessor.Process(results, continuityLookup);
```

Replace with:
```csharp
results = DecalRoadOverlapPostProcessor.Process(results, splineSurfaces, continuityLookup);
```

- [ ] **Step 3: Add using directive**

Add at the top of the file if not already present:
```csharp
using System.Numerics;  // already present
```

`SplineSurfaceData` is in `BeamNgTerrainPoc.Terrain.Models.DecalRoad` which is already imported.

- [ ] **Step 4: Build and verify the full solution compiles**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeded — all signature changes are now wired up.

Also build the main app to catch any downstream callers:
Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadGenerator.cs
git commit -m "feat: collect spline surfaces and pass to overlap post-processor for full-width detection"
```

---

### Task 5: Manual Verification and Cleanup

**Files:**
- No code changes — verification and documentation only

- [ ] **Step 1: Generate terrain and visually verify**

1. Run the application
2. Load an OSM area with intersecting roads (T-junctions and crossroads)
3. Generate terrain with default layer sets
4. In BeamNG, check at junctions:
   - Edge lines should be interrupted where they cross another road's surface
   - Lane markings should be interrupted at junctions
   - TreadMarks/Wear layers should NOT be interrupted (they are `InterruptAtJunctions=false`)
   - Roundabout edge markings should be interrupted at connecting road entries
   - DirectionDivider center lines should flow through continuous junctions (PreserveContinuity)

**What was broken before and should now work:**
- Edge lines that previously disappeared entirely on some roads
- Lane markings that stopped too early or were completely absent
- Roads between two close junctions losing all markings

- [ ] **Step 2: Check for regressions**

Verify these still work correctly (they were working before):
- Roundabout edge blends interrupted at connecting roads
- T-junction edge blends interrupted correctly
- DirectionDivider continuity through T-junctions
- Roads without nearby junctions have uninterrupted markings

- [ ] **Step 3: Update the debugging document**

Update `ai_docs/decalroad_overlap_debugging_2026-03-20.md`:
- Mark the bug as resolved
- Document the root cause (footprint built from narrow layers instead of full road width)
- Document the fix (spline-based corridor footprint)
- Close Leads 1 and 3 as confirmed root causes

- [ ] **Step 4: Final commit with updated docs**

```bash
git add ai_docs/decalroad_overlap_debugging_2026-03-20.md
git commit -m "docs: document spline corridor footprint fix for overlap detection"
```

---

## Risk Assessment

**Low risk:**
- `SplitOpenRoad`, `SplitClosedLoopRoad`, `ComputeOverlapMask` are unchanged
- `SurfaceFootprintIndex.CheckPoint()` is unchanged — same spatial hash, same point-in-segment test
- Continuity exemption logic is unchanged

**Medium risk:**
- The margin value (0.5m in `SurfaceFootprintIndex.Margin`) now applies on top of the full road half-width. If roads are very close together (parallel roads < 1m apart), this could cause false positives. Monitor for this during verification.
- Sub-sampling cross-sections twice (once for surface data, once inside `GenerateForSpline`) is a minor perf cost but negligible compared to the spatial hash lookups.

**Rollback:** If the fix introduces regressions, revert to the old behavior by passing an empty `splineSurfaces` list and adding back the surface-road classification in the post-processor. The old `AddRoad` method is still present.
