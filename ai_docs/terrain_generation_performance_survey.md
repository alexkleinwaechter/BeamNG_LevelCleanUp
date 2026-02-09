# Terrain Generation Performance Survey

## Date: 2025-01-XX
## Branch: `perf/improve_terrain_generation_performance`

---

## 1. Pipeline Overview

The terrain generation pipeline flows through these major stages:

```
GenerateTerrain.razor.cs (UI)
  ?? TerrainGenerationOrchestrator.ExecuteInternalAsync()
       ?? ProcessMaterialAsync() × N materials          [OSM fetch, spline conversion, rasterization]
       ?? ExportAllOsmLayersAsync()                      [optional, parallel]
       ?? TerrainCreator.CreateTerrainFileAsync()
            ?? LoadFromGeoTiffAsync()                    [GDAL read + crop + resample]
            ?? HeightmapProcessor.ProcessHeightmap()     [L16 ? float[]]
            ?? ApplyRoadSmoothing()
            ?    ?? UnifiedRoadSmoother.SmoothAllRoads()
            ?         ?? Phase 1:   NetworkBuilder.BuildNetwork()
            ?         ?? Phase 1.5: IdentifyRoundaboutSplines()
            ?         ?? Phase 2:   CalculateNetworkElevations()
            ?         ?? Phase 2.3: StructureElevationProfiles()
            ?         ?? Phase 2.5: BankingPreCalculation()
            ?         ?? Phase 2.6: RoundaboutElevationHarmonization()
            ?         ?? Phase 3:   JunctionDetection + Harmonization
            ?         ?? Phase 3.5: BankingFinalization()
            ?         ?? Phase 4:   UnifiedTerrainBlender.BlendNetworkWithTerrain()
            ?         ?    ?? BuildCombinedRoadCoreMask()
            ?         ?    ?? BuildRoadCoreProtectionMaskWithOwnership()
            ?         ?    ?? DistanceFieldCalculator.ComputeDistanceField()     [EDT]
            ?         ?    ?? ElevationMapBuilder.BuildElevationMapWithOwnership()
            ?         ?    ?? ProtectedBlendingProcessor.ApplyProtectedBlending()
            ?         ?? Phase 4+:  PostProcessingSmoother (optional)
            ?         ?? Phase 5:   MaterialPainter.PaintMaterials()
            ?? MaterialLayerProcessor.ProcessMaterialLayers()
            ?? Terrain Data Assembly (spike prevention)
            ?? terrain.Save() (Grille.BeamNG.Lib)
            ?? WriteTerrainJsonAsync()
```

---

## 2. Identified Performance Opportunities

### ?? HIGH IMPACT

#### 2.1 `TerrainCreator` — Spike Prevention: O(N) LINQ `.Where().OrderBy().ElementAt()` for Median
**File:** `TerrainCreator.cs`, lines in terrain data assembly section  
**Issue:** Computing the median height for GeoTIFF spike prevention uses:
```csharp
var validHeights = heights.Where(h => ...).ToList();
medianHeight = validHeights.OrderBy(h => h).ElementAt(validHeights.Count / 2);
```
For a 4096×4096 terrain (16.7M pixels), this:
1. Allocates a `List<float>` of up to 16.7M elements (~67 MB)
2. Sorts the entire list O(N log N) just to pick the middle element

**Fix:** Use a single-pass approximate median via reservoir sampling, or use the O(N) Quickselect algorithm. For spike replacement, even a simple average or percentile estimate from a small random sample (e.g., 10,000 points) would suffice.

**Estimated Savings:** ~2–5 seconds on 4096² terrains; eliminates ~130 MB transient allocation.

---

#### 2.2 `RoadMaskBuilder.BuildRoadCoreProtectionMaskWithOwnership` — Point-in-Polygon per Pixel
**File:** `RoadMaskBuilder.cs`, method `FillConvexPolygonWithOwnershipAndBanking`  
**Issue:** For every pixel in the bounding box of each road quad, `PolygonUtils.IsPointInConvexPolygon()` is called. This method does 4 cross-product tests per pixel. For a road network with thousands of cross-sections, each producing a quad covering ~20-100 pixels, this is called millions of times.

**Fix:** Replace point-in-convex-polygon with scanline rasterization (edge-crossing), which is already used in `MaterialPainter.FillConvexPolygon`. The polygon is a **quad** (4 vertices), so a proper scanline fill would iterate only over interior pixels without any per-pixel test. The `MaterialPainter` already has a working scanline implementation that should be reused here.

**Estimated Savings:** 20–40% faster protection mask building (eliminates 4 cross products per pixel).

---

#### 2.3 `ElevationMapBuilder.BuildElevationMapWithOwnership` — Blend Zone Pixel-by-Pixel Lookup
**File:** `ElevationMapBuilder.cs`, the `Parallel.For` loop  
**Issue:** For every non-core pixel in the terrain, `spatialIndex.FindNearest()` is called, followed by either `InterpolateNearbyCrossSections()` or `InterpolateFromSingleSpline()`, each calling `FindWithinRadius()`. On a 4096² terrain, the vast majority of pixels are NOT near any road, yet we still check `if (protectionMask[y, x]) continue;` which is fast, but the `FindNearest` call for pixels that *are* near roads involves iterating 9 grid cells.

The `FindWithinRadius()` method uses `yield return` which causes allocation of the IEnumerable state machine per call. This is called millions of times in the inner loop.

**Fix:**
1. **Early rejection:** Before calling `FindNearest`, check if the global distance field at this pixel exceeds the maximum possible influence distance (`maxRoadHalfWidth + maxBlendRange`). This single array lookup eliminates work for 90%+ of pixels.
2. **Replace `yield return` with a pre-allocated list** or `Span<T>`-based approach in `FindWithinRadius()`. The current iterator allocation is significant at millions of calls.
3. **Consider caching `FindNearest` results** for the row-adjacent pixel reuse pattern (pixels in the same row often map to the same nearest cross-section).

**Estimated Savings:** 30–50% faster elevation map building.

---

#### 2.4 `ProtectedBlendingProcessor.ApplyProtectedBlending` — Per-Pixel Higher-Priority Search
**File:** `ProtectedBlendingProcessor.cs`, method `FindHigherPriorityProtectedElevation`  
**Issue:** For every blend-zone pixel, this method iterates over ALL splines in the network to find if any higher-priority road's protection zone covers this pixel. For each candidate, it calls `crossSectionsBySpline.FindNearestForSpline()`. With 50+ splines and millions of blend-zone pixels, this is O(pixels × splines).

**Fix:**
1. **Pre-build a priority-aware spatial index** that maps pixels to the highest-priority road that covers them. This can be built once during the protection mask phase.
2. **Filter splines by spatial proximity** before the priority check using the same grid-based approach as the existing spatial indices.
3. **Skip splines with lower or equal priority early** — the current code already does this with `if (params_.Priority <= currentPriority) continue;` but the outer loop over `splines` is still O(all splines).

**Estimated Savings:** 20–40% faster blending.

---

#### 2.5 `PostProcessingSmoother` — Non-Separable Kernel Application
**File:** `PostProcessingSmoother.cs`, methods `ApplyGaussianSmoothing`, `ApplyBilateralSmoothing`  
**Issue:** The Gaussian and bilateral smoothing apply the kernel as a 2D convolution (radius×radius neighborhood per pixel), which is O(N × K²) where K is kernel size. For Gaussian smoothing, this can be **separated** into two 1D passes (horizontal + vertical), reducing to O(N × 2K).

Also, `Array.Copy(tempMap, heightMap, height * width)` on 2D arrays may not work correctly — `Array.Copy` on multidimensional arrays copies by total element count but the syntax should use `Buffer.BlockCopy` for float arrays.

**Fix:**
1. **Separable Gaussian:** Apply as two 1D passes (horizontal then vertical). This is a well-known optimization that reduces kernel size 7?14 operations instead of 49.
2. **Use `Buffer.BlockCopy`** for the final copy, or better yet, swap array references instead of copying.

**Estimated Savings:** For kernel size 7, this is 3.5× fewer operations. For kernel size 15, it's 7.5× fewer.

---

#### 2.6 `HeightmapProcessor.ProcessHeightmap` — Sequential Pixel Access
**File:** `HeightmapProcessor.cs`  
**Issue:** The heightmap processing accesses ImageSharp pixels one at a time with `heightmapImage[x, y]`. ImageSharp provides row-level span access via `image.DangerousGetPixelRowMemory(y)` which is significantly faster by avoiding per-pixel bounds checks and enabling SIMD.

**Fix:** Use `DangerousGetPixelRowMemory(y)` to get a `Span<L16>` for each row, then iterate the span:
```csharp
for (int y = 0; y < size; y++)
{
    var row = heightmapImage.DangerousGetPixelRowMemory(y).Span;
    int flippedY = size - 1 - y;
    for (int x = 0; x < size; x++)
    {
        heights[flippedY * size + x] = row[x].PackedValue / 65535f * maxHeight;
    }
}
```

**Estimated Savings:** 2–5× faster heightmap loading for large terrains.

---

### ?? MEDIUM IMPACT

#### 2.7 `MaterialPainter.PaintSplineDirectly` — Excessive Spline Sampling
**File:** `MaterialPainter.cs`  
**Issue:** `MaxPaintingSampleIntervalMeters = 0.25f` means a 10km road generates 40,000 samples. Each sample creates a quad with 4 vector operations and a scanline fill. The `List<float> intersections` in `FillConvexPolygon` is allocated per scanline per quad.

**Fix:**
1. **Adaptive sampling interval** based on road curvature. Straight sections can use 2-5m intervals; only curves need 0.25m.
2. **Pre-allocate the intersections list** outside the scanline loop and reuse it with `.Clear()`.
3. Use `stackalloc` or `ArrayPool<float>` for the intersections array since it's always ?8 entries (4 edges × 2 max intersections).

**Estimated Savings:** 50–70% fewer samples for typical road networks (mostly straight sections).

---

#### 2.8 `NetworkJunctionDetector.ClusterEndpointsIntoJunctions` — O(N²) Transitive Closure
**File:** `NetworkJunctionDetector.cs`  
**Issue:** The clustering algorithm uses a nested loop with transitive expansion:
```csharp
do { expanded = false;
  for i in endpoints:
    for j in endpoints:
      for idx in cluster:
        check distance...
} while (expanded);
```
This is O(E² × C) per iteration where E = endpoints, C = cluster size. For networks with hundreds of splines (200+ endpoints), this becomes noticeable.

**Fix:** Use Union-Find (disjoint set) data structure for O(E × ?(E)) clustering, or build a spatial grid index for endpoints and only check nearby endpoints.

**Estimated Savings:** Negligible for small networks (<50 splines), significant for large networks (500+ splines).

---

#### 2.9 `UnifiedRoadNetworkBuilder.RamerDouglasPeucker` — Recursive with LINQ `.Take()` / `.Skip()`
**File:** `UnifiedRoadNetworkBuilder.cs`  
**Issue:** The RDP implementation uses recursive calls with `points.Take(maxIndex + 1).ToList()` and `points.Skip(maxIndex).ToList()`, which allocates new lists at every recursion level. For a path with 10,000 points and moderate curvature, this creates O(N log N) list allocations.

**Fix:** Use the standard in-place iterative RDP algorithm with a stack and index ranges instead of copying sublists. The input `List<Vector2>` can be processed by passing `(startIndex, endIndex)` tuples.

**Estimated Savings:** Eliminates O(N log N) list allocations; meaningful for PNG road extraction paths.

---

#### 2.10 `MaterialLayerProcessor.ProcessMaterialLayers` — Pre-extracting All Pixel Data
**File:** `MaterialLayerProcessor.cs`  
**Issue:** All layer images are pre-extracted to `byte[]` arrays for thread safety:
```csharp
var pixels = new byte[size * size];
for (var y = 0; y < size; y++)
  for (var x = 0; x < size; x++)
    pixels[y * size + x] = image[x, y].PackedValue;
```
For 5 materials at 4096², this allocates 5 × 16.7MB = 83.5MB of byte arrays. This can use ImageSharp's row spans directly (with extraction happening per-row instead of all-at-once).

**Fix:** Use `image.DangerousGetPixelRowMemory(y).Span` per row during the parallel processing, extracting one row at a time. The parallel loop already processes by row, so thread safety is maintained.

**Estimated Savings:** Eliminates ~80MB transient allocation.

---

#### 2.11 `DistanceFieldCalculator` — Row/Column Buffer Allocation
**File:** `DistanceFieldCalculator.cs`  
**Issue:** The EDT algorithm allocates 3 arrays (`f`, `v`, `z`) per row AND per column, totaling 6 × size allocations. For a 4096 terrain, this is 24,576 array allocations.

**Fix:** Allocate the buffers ONCE outside the loops and reuse them. The row pass can reuse `f`, `v`, `z` across all rows. The column pass similarly.

**Estimated Savings:** Eliminates ~24K array allocations; reduces GC pressure.

---

#### 2.12 `SaveLayerMapToPngAsync` — Pixel-by-Pixel PNG Writing
**File:** `TerrainGenerationOrchestrator.cs`  
**Issue:** Each OSM layer map is written pixel-by-pixel:
```csharp
for (y) for (x) image[x, y] = new L8(layerMap[y, x]);
```
Use `DangerousGetPixelRowMemory` for row-level writes.

**Estimated Savings:** 2–3× faster layer map saving.

---

### ?? LOW IMPACT (but easy wins)

#### 2.13 `CrossSectionSpatialIndex` — Dictionary with Tuple Keys
**File:** `CrossSectionSpatialIndex.cs`  
**Issue:** Uses `Dictionary<(int, int), List<UnifiedCrossSection>>` which incurs tuple boxing/hashing overhead. The `ContainsKey` + index pattern should use `TryGetValue` (it partially does already, but some places use `ContainsKey`).

**Fix:** Some paths already use `TryGetValue`, ensure ALL paths do. The spatial index constructor has:
```csharp
if (!_index.ContainsKey(key))
    _index[key] = [];
_index[key].Add(cs);
```
Replace with:
```csharp
if (!_index.TryGetValue(key, out var list))
{
    list = [];
    _index[key] = list;
}
list.Add(cs);
```
This is already partially done but not consistently.

**Estimated Savings:** Minor per-call, but significant at millions of lookups.

---

#### 2.14 `TerrainCreator.ConvertTo2DArray` / `ConvertTo1DArray` — Unnecessary Copies
**File:** `TerrainCreator.cs`  
**Issue:** After road smoothing, the smoothed 2D heightmap is converted to 1D (`ConvertTo1DArray`), only to be used in the terrain data assembly loop which indexes as `heights[y * size + x]`. The 2D?1D?data assembly chain involves an extra full-terrain copy.

**Fix:** Keep the 2D array and index directly as `smoothedMap[y, x]` in the terrain data assembly loop, eliminating one full-terrain copy.

**Estimated Savings:** Eliminates one 64MB copy (4096² × 4 bytes) and 64MB allocation.

---

#### 2.15 `NetworkJunctionDetector.DetectMidSplineCrossings` — O(N²) LINQ `.Any()` for Dedup
**File:** `NetworkJunctionDetector.cs`  
**Issue:** 
```csharp
if (newCrossingPositions.Any(p => Vector2.Distance(p, crossingPoint) < ...))
    continue;
```
This is O(C) per candidate where C = number of already-created crossings. Also:
```csharp
var nearestExistingDist = existingJunctionPositions
    .Select(p => Vector2.Distance(p, crossingPoint))
    .DefaultIfEmpty(float.MaxValue).Min();
```
This is O(J) per candidate where J = existing junctions.

**Fix:** Use a spatial grid index for both `newCrossingPositions` and `existingJunctionPositions` for O(1) proximity checks.

**Estimated Savings:** Significant for networks with many crossings (100+), negligible for small networks.

---

#### 2.16 `UnifiedRoadSmoother.CalculateNetworkElevations` — Unnecessary Object Creation
**File:** `UnifiedRoadSmoother.cs`  
**Issue:** For each spline, a new `RoadGeometry` and list of `CrossSection` objects are created just to pass to `_elevationCalculator.CalculateTargetElevations()`. The geometry's `CrossSections` list is populated, smoothed, and then values are copied back.

**Fix:** Refactor `IHeightCalculator` to accept `UnifiedCrossSection` lists directly, avoiding the conversion roundtrip. This is a design improvement that eliminates allocations.

**Estimated Savings:** Eliminates N × CrossSectionCount object allocations.

---

#### 2.17 `PostProcessingSmoother.FindCrossGroupJunctions` — O(E²) Endpoint Comparison
**File:** `PostProcessingSmoother.cs`  
**Issue:** Nested loop comparing all endpoints pairwise:
```csharp
for i in endpoints:
  for j in i+1..endpoints:
    check distance
```
Plus T-junction detection iterates all cross-sections for each endpoint.

**Fix:** Use spatial grid index for endpoints. The T-junction detection should use the existing `CrossSectionSpatialIndex` pattern.

**Estimated Savings:** Minor for typical use (few materials), could matter with 10+ road materials.

---

#### 2.18 Redundant `Array.Copy` on 2D Arrays in Smoothers
**File:** `PostProcessingSmoother.cs`, methods `ApplyGaussianSmoothing`, `ApplyBoxSmoothing`, `ApplyBilateralSmoothing`  
**Issue:** `Array.Copy(tempMap, heightMap, height * width)` works on multidimensional arrays but is not optimal. Better to swap references.

**Fix:** Return the new array instead of copying, or pass by `ref` and swap. This avoids copying 64MB per smoothing iteration.

**Estimated Savings:** Eliminates 64MB copy per smoothing iteration.

---

## 3. Memory Allocation Hotspots

| Location | Allocation | Size (4096²) | Frequency |
|---|---|---|---|
| Spike prevention median | `List<float>` + sorted copy | ~130 MB | 1× |
| `ConvertTo2DArray` + `ConvertTo1DArray` | `float[]` + `float[,]` | 64 MB each | 2× |
| `MaterialLayerProcessor` pixel extraction | `byte[]` per material | 16.7 MB each | N materials |
| `PostProcessingSmoother` tempMap | `float[,]` | 64 MB | per iteration |
| `ElevationMapBuilder` arrays | 4× `float[,]` + `int[,]` | 64 MB each | 1× |
| EDT calculator buffer arrays | `float[]`, `int[]` | ~48 KB each | 24K allocations |
| `FindWithinRadius` iterator | IEnumerable state machine | ~100 bytes | millions |

---

## 4. Algorithmic Complexity Summary

| Component | Current | Optimal | Notes |
|---|---|---|---|
| Spike prevention median | O(N log N) | O(N) | Quickselect or approximate |
| EDT (Felzenszwalb) | O(N) ? | O(N) | Already optimal algorithm |
| Endpoint clustering | O(E² × C) | O(E ?(E)) | Union-Find |
| Protection mask fill | O(P × 4) per pixel | O(P) scanline | Replace point-in-polygon with scanline |
| Elevation map blend zone | O(P × grid_cells) | O(P × grid_cells) | Already has spatial index, needs early rejection |
| Higher-priority search | O(P × S) | O(P × log S) | Spatial index for priority query |
| Gaussian smoothing | O(N × K²) | O(N × 2K) | Separable filter |
| RDP simplification | O(N log N) allocs | O(N) in-place | Stack-based iterative |
| Material painting | O(samples × pixels) | O(samples × pixels) | Reduce samples via adaptive interval |

Where: N = terrain pixels, P = road-adjacent pixels, S = spline count, K = kernel size, E = endpoints, C = cluster size

---

## 5. Recommended Implementation Order

### Phase 1: Quick Wins (1-2 days, no API changes)
1. **§2.1** Replace LINQ median with Quickselect
2. **§2.6** Use ImageSharp row spans in HeightmapProcessor
3. **§2.11** Reuse EDT buffers across rows/columns
4. **§2.13** Consistent `TryGetValue` in spatial indices
5. **§2.14** Eliminate redundant 2D?1D array conversions

### Phase 2: Medium Effort (3-5 days, internal refactoring)
6. **§2.2** Replace point-in-polygon with scanline fill in RoadMaskBuilder
7. **§2.3** Add early distance-field rejection in ElevationMapBuilder + eliminate `yield return`
8. **§2.5** Separate Gaussian into 2 × 1D passes
9. **§2.7** Adaptive sampling interval in MaterialPainter
10. **§2.18** Swap array references instead of copying in smoothers

### Phase 3: Deeper Refactoring (5+ days)
11. **§2.4** Pre-build priority-aware spatial index for blending
12. **§2.8** Union-Find for endpoint clustering
13. **§2.9** In-place RDP algorithm
14. **§2.16** Refactor `IHeightCalculator` to accept UnifiedCrossSections directly
15. **§2.10** Row-level extraction in MaterialLayerProcessor

---

## 6. Measurement Notes

The existing `TerrainCreationLogger` and `PerformanceLogger` already instrument the major phases with `Stopwatch` timings. Key timing points exist at:
- Each phase in `UnifiedRoadSmoother.SmoothAllRoads()`
- Each step in `UnifiedTerrainBlender.BlendNetworkWithTerrain()`
- `TerrainCreator.CreateTerrainFileAsync()` total and per-section

Before implementing optimizations, capture baseline timings from the `logs/TerrainGen_*.log` files with a representative terrain (e.g., 4096² with 3+ road materials and OSM data).

---

## 7. Non-Performance Observations (Out of Scope)

These are NOT performance issues but were observed during the survey:
- `PostProcessingSmoother.ApplyGaussianSmoothing` uses `Array.Copy` on 2D arrays which may silently produce incorrect results if the array layout doesn't match expectations. This should be validated for correctness.
- The `volatile bool _suppressSnackbars` in `GenerateTerrain.razor.cs` may have race conditions with `await InvokeAsync()` patterns, but this is a UI concern not a performance issue.
- The `FindHigherPriorityProtectedElevation` iterates `network.Splines` (a `List<>`) directly in a hot parallel loop. While the list is read-only during blending, it should be captured as a local variable or array before the `Parallel.For` to avoid potential cache line contention.
