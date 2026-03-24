# Terrain Blending Refactor — BeamNG-Style Single-Pass Blender

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the complex per-spline ownership/priority/protection blending system with BeamNG's proven single-pass approach: one combined mask, one EDT with nearest-source tracking, one simple blend pass using `w = (1 - d/DOI)^falloffExp`.

**Architecture:** The current system fails at junctions because it assigns per-pixel ownership to individual splines, then blends based on that ownership — creating discontinuities where ownership changes. BeamNG's approach sidesteps this entirely: ALL road surfaces are pinned in a single combined mask, and blend-zone pixels always blend toward the **nearest road surface pixel's elevation** (regardless of which road it belongs to). No ownership tracking, no priority rules, no protection buffers needed for the core blend.

**Tech Stack:** C# / .NET 9, existing `BeamNgTerrainPoc` library, Felzenszwalb & Huttenlocher EDT (extended with source tracking)

**Reference:** `d:\Source\beamng_mapping_pro\ai_docs\beamng_terraform_spline_blending.md` — reverse-engineered BeamNG algorithms

---

## Why the Current System Fails

The current `ProtectedBlendingProcessor` uses a 5-step pipeline:
1. Build combined road mask (for EDT)
2. Build protection mask with per-pixel ownership (which spline "owns" each pixel)
3. Compute EDT (distance only, no source tracking)
4. Build elevation map with IDW interpolation per ownership
5. Apply protected blending with per-spline parameters

**Root cause:** Steps 2 and 4 assign each pixel to a single owning spline. At junctions where roads overlap, pixels on Road A's surface can be owned by Road B (first-processed wins for same priority). The blending processor then uses Road B's parameters and elevation profile for those pixels, creating discontinuities, bumps, and terrain damage on road surfaces.

**BeamNG's solution (proven working):** Skip per-pixel ownership entirely. Build one combined mask from ALL roads. For each blend pixel, look up the elevation of the **nearest road surface pixel** and blend toward it. The mask itself is the protection — road surface pixels are pinned, never blended.

## What Changes

| Component | Current | New |
|-----------|---------|-----|
| **DistanceFieldCalculator** | Returns `float[,]` distances only | Also returns `int[,]` nearestX, `int[,]` nearestY (source tracking) |
| **RoadMaskBuilder** | Two masks (core + protection with ownership) | One combined mask with elevation map (no ownership tracking needed) |
| **ElevationMapBuilder** | Complex IDW interpolation with per-spline strategies | **Removed** — elevation comes from nearest-source lookup via EDT |
| **ProtectedBlendingProcessor** | Per-spline ownership, priority, protection buffers, slope constraints | **Replaced** by `SinglePassBlender`: mask check → nearest-source elevation → exponential falloff |
| **PriorityProtectionIndex** | Spatial index for higher-priority lookups | **Removed** — not needed without ownership |
| **BlendFunctions** | 4 curve types | **Extended** with exponential falloff `(1 - d/DOI)^exp` |
| **PostProcessingSmoother** | Gaussian/Box/Bilateral with per-material masks | **Kept** but add soft-boundary fade at DOI edge (BeamNG pattern) |
| **Parameters** | BlendRange, ProtectionBuffer, BlendFunctionType, SideMaxSlope | **Simplified**: DOI (maps from BlendRange), FalloffExponent (new), SideMaxSlope (kept). ProtectionBuffer and BlendFunctionType become vestigial. |

## What Stays Unchanged

- `UnifiedTerrainBlender` orchestrator (rewired to new components)
- `RasterizationUtils` (line drawing)
- `CrossSectionSpatialIndex` (still used by post-processing)
- `PostProcessingSmoother` (enhanced, not replaced)
- `BankedTerrainHelper` (still used for road surface elevation)
- All upstream phases (elevation solving, junction harmonization)
- UI components (parameter bindings — some parameters become unused)

---

## File Structure

### New Files
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/SinglePassBlender.cs` — The new core blender (replaces `ProtectedBlendingProcessor` + `ElevationMapBuilder`)

### Modified Files
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/DistanceFieldCalculator.cs` — Add nearest-source coordinate tracking
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs` — Simplify to build combined mask + elevation map (no ownership)
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/BlendFunctions.cs` — Add exponential falloff type
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/PostProcessingSmoother.cs` — Add soft-boundary fade
- `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedTerrainBlender.cs` — Rewire to new pipeline
- `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` — Add `FalloffExponent` parameter
- `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` — Add falloff exponent UI control

### Deleted (Task 0)
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs` — replaced by nearest-source EDT lookup
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs` — replaced by SinglePassBlender
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/PriorityProtectionIndex.cs` — not needed without ownership

---

## Tasks

### Task 0: Remove Obsolete Blending Code

Delete the three files that are being fully replaced, and remove all references to them from the orchestrator. This gives a clean slate before building the new pipeline.

**Files:**
- Delete: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs`
- Delete: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs`
- Delete: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/PriorityProtectionIndex.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedTerrainBlender.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`

- [ ] **Step 1: Delete the three obsolete files**

```bash
git rm BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs
git rm BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs
git rm BeamNgTerrainPoc/Terrain/Algorithms/Blending/PriorityProtectionIndex.cs
```

- [ ] **Step 2: Gut UnifiedTerrainBlender.BlendNetworkWithTerrain — temporary stub**

Remove the fields, constructor lines, and method body that reference the deleted classes. Replace `BlendNetworkWithTerrain` with a temporary passthrough that returns the original heightmap unchanged (so the project compiles while we build the new pipeline):

In `UnifiedTerrainBlender.cs`:
- Remove `using BeamNgTerrainPoc.Terrain.Algorithms.Blending;` references to deleted types (if any standalone)
- Remove fields: `_elevationMapBuilder`, `_blendingProcessor`
- Remove constructor lines initializing them
- Remove Steps 2, 4, 5 from `BlendNetworkWithTerrain` body
- Keep Step 1 (combined core mask) and Step 3 (EDT) — these are reused
- Temporarily return `(float[,])originalHeightMap.Clone()` at the end so the method compiles
- Update the XML doc comments to remove references to deleted classes

- [ ] **Step 3: Remove obsolete methods from RoadMaskBuilder**

Delete `BuildRoadCoreProtectionMaskWithOwnership`, `FillConvexPolygonWithOwnershipAndBanking`, `FillConvexPolygonWithOwnership`, and the `ProtectionMaskResult` record. Keep `BuildCombinedRoadCoreMask` (still used by EDT in Step 1) and utility methods like `IsValidTargetElevation`.

- [ ] **Step 4: Build and verify 0 errors**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`
Expected: 0 errors. The blending now does nothing (passthrough), but the project compiles cleanly with no dead code.

- [ ] **Step 5: Commit**

```
refactor: remove obsolete blending code (ElevationMapBuilder, ProtectedBlendingProcessor, PriorityProtectionIndex)
```

---

### Task 1: Extend DistanceFieldCalculator with Nearest-Source Tracking

The EDT currently returns only distances. BeamNG's approach requires knowing WHICH road pixel is nearest to each blend pixel so we can look up that pixel's elevation. This is the foundation for the entire refactor.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/DistanceFieldCalculator.cs`

- [ ] **Step 1: Add DistanceFieldResult record**

Add a result type that bundles distance field with source coordinates:

```csharp
/// <summary>
/// Result of EDT computation with nearest-source tracking.
/// </summary>
public record DistanceFieldResult(
    float[,] Distances,      // Distance in meters to nearest road pixel
    int[,] NearestSourceX,   // X coordinate of nearest road pixel (pixel coords)
    int[,] NearestSourceY);  // Y coordinate of nearest road pixel (pixel coords)
```

- [ ] **Step 2: Add ComputeDistanceFieldWithSources method**

The key insight: during the 1D EDT passes, when we find that pixel B's nearest source is closer than pixel A's current nearest source, we propagate B's source coordinates to A. This is a standard extension of Felzenszwalb & Huttenlocher.

```csharp
/// <summary>
/// Computes EDT with nearest-source coordinate tracking.
/// For each pixel, returns both the distance to the nearest road pixel
/// AND which road pixel that is (so we can look up its elevation).
/// </summary>
public static DistanceFieldResult ComputeDistanceFieldWithSources(
    byte[,] mask, float metersPerPixel)
{
    var h = mask.GetLength(0);
    var w = mask.GetLength(1);
    var dist = new float[h, w];
    var srcX = new int[h, w];
    var srcY = new int[h, w];

    // Initialize: road pixels (255) get distance 0 and point to themselves.
    // Background pixels get infinity and (-1, -1).
    for (var y = 0; y < h; y++)
    for (var x = 0; x < w; x++)
    {
        if (mask[y, x] == 255)
        {
            dist[y, x] = 0;
            srcX[y, x] = x;
            srcY[y, x] = y;
        }
        else
        {
            dist[y, x] = float.MaxValue;
            srcX[y, x] = -1;
            srcY[y, x] = -1;
        }
    }

    // Horizontal pass: for each row, compute 1D EDT and propagate source X/Y
    ProcessRowsWithSources(dist, srcX, srcY, w, h);

    // Vertical pass: for each column, compute 1D EDT and propagate source X/Y
    ProcessColumnsWithSources(dist, srcX, srcY, w, h);

    // Convert squared pixel distances to meters
    for (var y = 0; y < h; y++)
    for (var x = 0; x < w; x++)
        dist[y, x] = MathF.Sqrt(dist[y, x]) * metersPerPixel;

    return new DistanceFieldResult(dist, srcX, srcY);
}
```

The `ProcessRowsWithSources` and `ProcessColumnsWithSources` methods follow the same Felzenszwalb parabolic envelope algorithm as the existing code, but additionally propagate `srcX`/`srcY` whenever a nearer source is found. The implementation mirrors the existing `ProcessRows`/`ProcessColumns` methods — for each 1D segment, when the parabolic envelope selects a different source pixel as the nearest, copy that pixel's source coordinates.

**Implementation approach — two options (choose during implementation):**

**Option A: Extend Felzenszwalb with source tracking (optimal but tricky)**
- Row pass: track `rowSrcX[y, x]` alongside distances. When generator `v[k]` wins for position `q`, set `srcX[y, q] = v[k]` (the winning generator's X), `srcY[y, q] = y` (current row).
- Column pass: track source coordinates from the row-pass result. When generator row `p` wins for row `q` in column `x`, set `srcX[q, x] = rowSrcX[p, x]` and `srcY[q, x] = p`. The key subtlety: the row pass generator `v[k]` in the row pass is a column index, and the column pass generator is a row index. Keep the parabola stack index arrays (`v[]`) in sync with source coordinate arrays.
- After both passes, `srcX[y, x]` and `srcY[y, x]` point to the nearest road mask pixel.

**Option B: Separate JFA pass (simpler, slightly slower)**
- Keep the existing `ComputeDistanceField` for distances (unchanged).
- After EDT, run a Jump Flood Algorithm (JFA) pass to compute nearest-source coordinates:
  1. Initialize seeds: for each road pixel (mask=255), set `srcX[y,x]=x, srcY[y,x]=y`
  2. For step sizes `k = nextPow2(max(w,h))/2` down to 1:
     - For each pixel, check 8 neighbors at offset `k`
     - If neighbor has a valid source and `dist(pixel, neighbor.source) < dist(pixel, current.source)`, adopt neighbor's source
  3. O(n × log(n)) total, trivially parallelizable, no modification to EDT inner loop
- JFA is slightly less accurate than exact EDT for source assignment but the error is sub-pixel and irrelevant for elevation lookup.

**Recommendation:** Start with Option B (JFA) for simplicity. It avoids touching the sensitive EDT inner loop and is well-understood. If performance profiling later shows it's a bottleneck, switch to Option A.

- [ ] **Step 3: Verify the existing `ComputeDistanceField` still works unchanged**

The existing method must remain functional — it's used by the post-processing smoother. Only add the new method alongside it.

- [ ] **Step 4: Build and verify no compilation errors**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`
Expected: 0 errors

- [ ] **Step 5: Commit**

```
feat: extend DistanceFieldCalculator with nearest-source tracking
```

---

### Task 2: Simplify RoadMaskBuilder — Combined Mask with Elevation Only

Remove ownership tracking from the mask builder. The new mask only needs to know: is this pixel a road surface? If yes, what's its elevation?

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`

- [ ] **Step 1: Add new CombinedMaskResult record**

```csharp
/// <summary>
/// Result of building a combined road mask with elevation data.
/// No ownership tracking — the mask itself is the protection mechanism.
/// </summary>
public record CombinedMaskResult(
    byte[,] Mask,              // 255 = road surface, 0 = terrain
    float[,] ElevationMap,     // Road surface elevation where Mask=255, NaN elsewhere
    int MaskedPixels);
```

- [ ] **Step 2: Add BuildCombinedMaskWithElevation method**

This replaces both `BuildCombinedRoadCoreMask` and `BuildRoadCoreProtectionMaskWithOwnership`. It builds filled polygons (not just cross-section lines) for the EDT mask, and stores banking-aware elevation at each road pixel.

Key design decisions:
- Uses `EffectiveRoadWidth / 2 + margin` as the half-width, where `margin` reuses the existing `RoadEdgeProtectionBufferMeters` (default 2m). This matches BeamNG's `halfWidth = width/2 + margin` pattern (Section 1.3 Step 2 of the reference). The margin ensures the "pinned" zone extends slightly past the visible road edge, preventing rasterization edge artifacts. The mask IS the protection — no separate protection mask needed.
- For overlapping road polygons at junctions: use **minimum elevation** (matching BeamNG's `min(hitZ)` pattern from Section 1.3 Step 3). This handles cases where junction harmonization isn't perfect by conservatively choosing the lower surface. If the elevation difference between existing and new values exceeds 2m, log a warning (indicates a junction harmonization issue upstream).

```csharp
public CombinedMaskResult BuildCombinedMaskWithElevation(
    UnifiedRoadNetwork network,
    int width,
    int height,
    float metersPerPixel)
```

Algorithm:
1. Allocate `mask[h,w]`, `elevation[h,w]` (init NaN)
2. For each spline's consecutive cross-section pairs:
   - Get margin from spline's `RoadEdgeProtectionBufferMeters`
   - Build polygon corners from `EffectiveRoadWidth / 2 + margin`
   - Scanline fill the polygon
   - For each pixel in polygon:
     - Compute `pixelElevation` via `BankedTerrainHelper.GetBankedElevationForPixel(cs1, cs2, worldPos)`
     - If `mask == 0` (first hit): set elevation, set mask=255
     - If `mask == 255` (overlap): take `min(existing, pixelElevation)` (matching BeamNG's approach). If `|existing - pixelElevation| > 2.0f`, log warning.
3. Return `CombinedMaskResult(mask, elevation, totalMaskedPixels)`

The min-elevation approach at junctions matches BeamNG's `min(hitZ)` pattern. Junction harmonization (Phase 3) should ensure overlapping roads have near-identical elevations, so min vs first-wins is a safety net, not a major behavioral change.

- [ ] **Step 3: Keep existing methods intact**

Don't remove `BuildCombinedRoadCoreMask` or `BuildRoadCoreProtectionMaskWithOwnership` — they may still be referenced. Mark with `[Obsolete]` comments for future cleanup.

- [ ] **Step 4: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`

- [ ] **Step 5: Commit**

```
feat: add BuildCombinedMaskWithElevation to RoadMaskBuilder
```

---

### Task 3: Add Exponential Falloff to BlendFunctions

Add the BeamNG-style `(1 - d/DOI)^falloffExp` weight function.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/BlendFunctions.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Models/BlendFunctionType.cs`

- [ ] **Step 1: Find and extend the BlendFunctionType enum**

Search for `enum BlendFunctionType` and add `Exponential` value.

- [ ] **Step 2: Add ApplyExponential method and update dispatcher**

```csharp
/// <summary>
/// BeamNG-style exponential falloff: w = (1 - t)^exponent.
/// At t=0 (road edge): w=1 (full road elevation).
/// At t=1 (DOI boundary): w=0 (full terrain).
/// exponent=1.0: linear. exponent=1.5: natural (BeamNG default). exponent=3+: sharp shelf.
/// </summary>
public static float ApplyExponential(float t, float exponent = 1.5f)
{
    return MathF.Pow(MathF.Max(0f, 1f - t), exponent);
}
```

Note: The `Apply(float t, BlendFunctionType)` dispatcher needs to handle the new type. Since the exponential function takes an extra parameter (`exponent`), add an overload:

```csharp
public static float Apply(float t, BlendFunctionType blendType, float falloffExponent = 1.5f)
{
    return blendType switch
    {
        BlendFunctionType.Exponential => ApplyExponential(t, falloffExponent),
        // ... existing cases unchanged
    };
}
```

- [ ] **Step 3: Build and verify**

- [ ] **Step 4: Commit**

```
feat: add Exponential blend function type (BeamNG-style falloff)
```

---

### Task 4: Add FalloffExponent Parameter to RoadSmoothingParameters

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`

- [ ] **Step 1: Add FalloffExponent property**

Add near the existing blend parameters (around line 162 where BlendFunctionType is):

```csharp
/// <summary>
/// Exponent for the exponential falloff blend function.
/// Controls the shape of the terrain-to-road transition curve.
/// 1.0 = linear, 1.5 = natural (BeamNG default), 3.0+ = sharp shelf near road with gentle far approach.
/// Only used when BlendFunctionType is Exponential.
/// </summary>
[Range(0.5, 5.0)]
public float FalloffExponent { get; set; } = 1.5f;
```

- [ ] **Step 2: Update any validation/preset methods if they exist**

Check if `RoadSmoothingParameters` has a `Validate()` method or preset initialization that needs updating.

- [ ] **Step 3: Build and verify**

- [ ] **Step 4: Commit**

```
feat: add FalloffExponent parameter for terrain blending
```

---

### Task 5: Implement SinglePassBlender — The Core Refactor

This is the main new component that replaces `ProtectedBlendingProcessor` + `ElevationMapBuilder`. It implements the BeamNG Master Spline algorithm adapted to our codebase.

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Algorithms/Blending/SinglePassBlender.cs`

- [ ] **Step 1: Create the SinglePassBlender class**

```csharp
using System.Numerics;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Algorithms.Blending;

/// <summary>
/// Single-pass terrain blender implementing BeamNG's Master Spline approach.
///
/// Algorithm (from ai_docs/beamng_terraform_spline_blending.md):
/// 1. Road mask pixels (mask=255): pin to road surface elevation
/// 2. Blend zone pixels (0 < dist <= DOI): blend toward nearest road pixel's elevation
///    using w = (1 - dist/DOI)^falloffExp
/// 3. Outside DOI: keep original terrain
///
/// Key insight: each blend-zone pixel blends toward the elevation of its NEAREST
/// road surface pixel (tracked by the EDT), not a per-spline ownership target.
/// This eliminates junction artifacts because there is no ownership boundary.
/// </summary>
public class SinglePassBlender
{
    public record BlendResult(
        float[,] HeightMap,
        int RoadPixels,
        int BlendedPixels,
        int UnmodifiedPixels);

    /// <summary>
    /// Blends terrain using the single-pass nearest-source approach.
    /// </summary>
    public BlendResult Blend(
        float[,] originalHeightMap,
        byte[,] roadMask,
        float[,] roadElevationMap,
        DistanceFieldCalculator.DistanceFieldResult edt,
        UnifiedRoadNetwork network,
        float metersPerPixel)
    {
        var height = originalHeightMap.GetLength(0);
        var width = originalHeightMap.GetLength(1);
        var result = (float[,])originalHeightMap.Clone();

        // Determine per-pixel DOI and falloff from the nearest road's spline parameters.
        // Since we no longer track ownership, we use the nearest cross-section to determine
        // which spline's parameters apply at each blend pixel.
        // For simplicity and performance, use the MAXIMUM DOI across all splines as the
        // global cutoff, and per-pixel DOI from the nearest spline's parameters.
        var maxDoi = network.Splines.Max(s => s.Parameters.TerrainAffectedRangeMeters);
        var maxHalfWidth = network.Splines.Max(s => s.Parameters.RoadWidthMeters / 2.0f);
        var maxInfluence = maxHalfWidth + maxDoi;

        // Build spatial index for per-pixel parameter lookup
        var spatialIndex = new CrossSectionSpatialIndex(network.CrossSections, metersPerPixel);

        // Build spline parameter lookup
        var splineParams = network.Splines.ToDictionary(
            s => s.SplineId,
            s => (
                HalfWidth: s.Parameters.RoadWidthMeters / 2.0f,
                DOI: s.Parameters.TerrainAffectedRangeMeters,
                FalloffExp: s.Parameters.FalloffExponent,
                BlendFunction: s.Parameters.BlendFunctionType,
                MaxSlopeDeg: s.Parameters.SideMaxSlopeDegrees
            ));

        var roadPixels = 0;
        var blendedPixels = 0;
        var unmodifiedPixels = 0;

        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };

        Parallel.For(0, height, options, y =>
        {
            var localRoad = 0;
            var localBlend = 0;
            var localUnmod = 0;

            for (var x = 0; x < width; x++)
            {
                var dist = edt.Distances[y, x];

                // Case 1: Road surface pixel — pin to road elevation
                if (roadMask[y, x] == 255)
                {
                    var roadElev = roadElevationMap[y, x];
                    if (!float.IsNaN(roadElev))
                        result[y, x] = roadElev;
                    localRoad++;
                    continue;
                }

                // Early rejection: outside maximum influence zone
                if (dist > maxInfluence || dist <= 0)
                {
                    localUnmod++;
                    continue;
                }

                // Find the nearest road pixel's elevation via EDT source tracking
                var nearX = edt.NearestSourceX[y, x];
                var nearY = edt.NearestSourceY[y, x];
                if (nearX < 0 || nearY < 0)
                {
                    localUnmod++;
                    continue;
                }

                var ribbonZ = roadElevationMap[nearY, nearX];
                if (float.IsNaN(ribbonZ))
                {
                    localUnmod++;
                    continue;
                }

                // Determine which spline's parameters to use for this pixel.
                // Use the nearest cross-section to find the spline.
                var worldPos = new Vector2(x * metersPerPixel, y * metersPerPixel);
                var (nearestCs, _) = spatialIndex.FindNearest(worldPos);
                if (nearestCs == null)
                {
                    localUnmod++;
                    continue;
                }

                var splineId = nearestCs.OwnerSplineId;
                if (!splineParams.TryGetValue(splineId, out var sp))
                {
                    localUnmod++;
                    continue;
                }

                // Calculate distance from road EDGE (not center)
                // dist is from nearest road mask pixel, which is already the road edge
                var doi = sp.DOI;

                // Case 2: Within DOI — blend
                if (dist <= doi)
                {
                    // Normalized distance: 0 at road edge, 1 at DOI boundary
                    var t = dist / doi;

                    // Compute blend weight using configured function
                    var w = BlendFunctions.Apply(t, sp.BlendFunction, sp.FalloffExp);
                    // w is 1 at road edge, 0 at DOI boundary (for exponential)
                    // For existing functions, w goes 0→1, so we invert
                    if (sp.BlendFunction != BlendFunctionType.Exponential)
                        w = 1f - w; // Existing functions: 0 at road, 1 at terrain

                    // Blend: road elevation × w + original terrain × (1 - w)
                    var blendedH = ribbonZ * w + originalHeightMap[y, x] * (1f - w);

                    // Enforce max side slope constraint
                    blendedH = EnforceSideMaxSlope(
                        ribbonZ, originalHeightMap[y, x], blendedH,
                        dist, doi, sp.MaxSlopeDeg);

                    if (MathF.Abs(result[y, x] - blendedH) > 0.001f)
                        result[y, x] = blendedH;

                    localBlend++;
                }
                else
                {
                    // Case 3: Outside DOI — keep original
                    localUnmod++;
                }
            }

            Interlocked.Add(ref roadPixels, localRoad);
            Interlocked.Add(ref blendedPixels, localBlend);
            Interlocked.Add(ref unmodifiedPixels, localUnmod);
        });

        TerrainLogger.Info($"SinglePassBlender: {roadPixels:N0} road, {blendedPixels:N0} blended, {unmodifiedPixels:N0} unmodified");

        return new BlendResult(result, roadPixels, blendedPixels, unmodifiedPixels);
    }

    /// <summary>
    /// Enforces maximum side slope constraint (carried over from ProtectedBlendingProcessor).
    /// </summary>
    private static float EnforceSideMaxSlope(
        float roadElevation, float terrainElevation, float blendedElevation,
        float distFromRoadEdge, float doi, float maxSlopeDegrees)
    {
        if (distFromRoadEdge < 0.01f) return blendedElevation;
        var elevDiff = MathF.Abs(roadElevation - terrainElevation);
        if (elevDiff < 0.1f) return blendedElevation;

        var tanMaxSlope = MathF.Tan(maxSlopeDegrees * MathF.PI / 180f);
        var maxElevChange = distFromRoadEdge * tanMaxSlope;
        bool isCut = roadElevation > terrainElevation;

        var slopeConstrained = isCut
            ? MathF.Max(roadElevation - maxElevChange, terrainElevation)
            : MathF.Min(roadElevation + maxElevChange, terrainElevation);

        var exceedsSlope = isCut
            ? blendedElevation < slopeConstrained
            : blendedElevation > slopeConstrained;

        return exceedsSlope ? slopeConstrained : blendedElevation;
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`

- [ ] **Step 3: Commit**

```
feat: add SinglePassBlender implementing BeamNG-style terrain blending
```

---

### Task 6: Rewire UnifiedTerrainBlender to New Pipeline

Replace the 5-step pipeline with the new 3-step pipeline.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/UnifiedTerrainBlender.cs`

- [ ] **Step 1: Add the SinglePassBlender field and update constructor**

```csharp
private readonly SinglePassBlender _singlePassBlender;

public UnifiedTerrainBlender()
{
    _maskBuilder = new RoadMaskBuilder();
    _singlePassBlender = new SinglePassBlender();
    _postProcessingSmoother = new PostProcessingSmoother();
    // Keep old fields for backward compat, but they won't be used in the main path
}
```

- [ ] **Step 2: Rewrite BlendNetworkWithTerrain**

Replace the 5-step pipeline with:

```csharp
// Step 1: Build combined road mask with elevations (ALL roads, single pass)
var maskResult = _maskBuilder.BuildCombinedMaskWithElevation(network, width, height, metersPerPixel);

// Step 2: Compute EDT with nearest-source tracking
var edtResult = DistanceFieldCalculator.ComputeDistanceFieldWithSources(maskResult.Mask, metersPerPixel);
_lastDistanceField = edtResult.Distances;

// Step 3: Single-pass blend
var blendResult = _singlePassBlender.Blend(
    originalHeightMap, maskResult.Mask, maskResult.ElevationMap,
    edtResult, network, metersPerPixel);

return blendResult.HeightMap;
```

This eliminates Steps 2 (protection mask with ownership), 4 (elevation map builder with IDW), and replaces Step 5 (protected blending) with the simple single-pass blend.

- [ ] **Step 3: Verify _lastDistanceField is still set for post-processing**

The post-processing smoother uses `_lastDistanceField`. Ensure `edtResult.Distances` is assigned to it.

- [ ] **Step 4: Check if protection mask is needed elsewhere**

Search for usages of `BuildRoadCoreProtectionMaskWithOwnership` outside of `UnifiedTerrainBlender`. If DecalRoad suppression or other systems depend on the protection mask, keep generating it as a separate optional step (not part of the blend pipeline). If nothing else uses it, skip generation entirely.

- [ ] **Step 5: Build the full solution to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj --no-restore -v q`

- [ ] **Step 6: Commit**

```
feat: rewire UnifiedTerrainBlender to use SinglePassBlender pipeline
```

---

### Task 7: Add FalloffExponent UI Control

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs`

- [ ] **Step 1: Add Exponential option to BlendFunctionType MudSelect dropdown**

In `TerrainMaterialSettings.razor` around line 630-640, find the `<MudSelect T="BlendFunctionType">` and add:

```razor
<MudSelectItem Value="BlendFunctionType.Exponential">Exponential (BeamNG-style)</MudSelectItem>
```

- [ ] **Step 2: Add FalloffExponent numeric field to the UI**

Add near the BlendFunctionType selector. Only show when BlendFunctionType is Exponential:

```razor
@if (Material.BlendFunctionType == BlendFunctionType.Exponential)
{
    <MudNumericField T="float" @bind-Value="Material.FalloffExponent"
        Label="Falloff Exponent" Min="0.5" Max="5.0" Step="0.1"
        HelperText="1.0=linear, 1.5=natural (default), 3.0+=sharp shelf" />
}
```

- [ ] **Step 3: Add tooltip text**

In `RoadParameterTooltips.cs`, add:
```csharp
public const string FalloffExponent = "Controls the shape of the terrain-to-road transition.\n" +
    "1.0 = linear falloff\n" +
    "1.5 = natural concave curve (BeamNG default)\n" +
    "3.0+ = sharp road shelf with gentle far approach";
```

- [ ] **Step 4: Build full solution**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj --no-restore -v q`

- [ ] **Step 5: Commit**

```
feat: add FalloffExponent UI control for terrain blending
```

---

### Task 8: Verify and Set Default BlendFunctionType to Exponential

Update presets to use the new Exponential blend function by default.

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`

- [ ] **Step 1: Change default BlendFunctionType**

```csharp
public BlendFunctionType BlendFunctionType { get; set; } = BlendFunctionType.Exponential;
```

- [ ] **Step 2: Update any preset methods that set BlendFunctionType**

Search for preset initialization code and update to use `Exponential` as default.

- [ ] **Step 3: Build and verify**

- [ ] **Step 4: Commit**

```
feat: set Exponential as default blend function type
```

---

### Task 9: Full Build Verification and Integration Test

**Files:**
- All modified files

- [ ] **Step 1: Full solution build**

Run: `dotnet build BeamNG_LevelCleanUp.sln --no-restore -v q`
Expected: 0 errors

- [ ] **Step 2: Check for unused variable warnings in modified files**

Review warnings related to old fields/variables that are no longer used in the main path. Suppress or remove as appropriate.

- [ ] **Step 3: Verify the old pipeline code still compiles**

The deprecated `ProtectedBlendingProcessor`, `ElevationMapBuilder`, and `PriorityProtectionIndex` should still compile even though they're no longer called from the main path.

- [ ] **Step 4: Commit any cleanup**

```
chore: clean up warnings from terrain blending refactor
```

---

## Parameter Mapping (Old → New)

| Old Parameter | New Usage | Notes |
|---------------|-----------|-------|
| `TerrainAffectedRangeMeters` | Maps to DOI (Domain of Influence) | Same semantics — distance from road edge |
| `RoadWidthMeters` | Used for road mask polygon width | Same as before |
| `RoadEdgeProtectionBufferMeters` | **No longer used in blending** | The mask itself is the protection. Keep parameter for other uses (DecalRoad suppression). |
| `BlendFunctionType` | Still used — now includes Exponential | New default: Exponential |
| `FalloffExponent` | **New** — exponent for exponential falloff | Default 1.5 (BeamNG default) |
| `SideMaxSlopeDegrees` | Still used — slope constraint | Carried over unchanged |
| Post-processing params | Unchanged | Smoothing still applied after blending |

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| EDT source tracking doubles memory (~3 arrays vs 1) | For a 2048x2048 terrain: 3×16MB = 48MB. Acceptable. |
| Averaging elevations at overlapping mask polygons creates smooth but potentially incorrect junction surfaces | Junction harmonization (Phase 3) ensures roads at junctions have matching elevations. Averaging only creates visible artifacts if harmonization fails — which is a separate bug. |
| Removing per-spline ownership means blend zones can't have per-spline DOI | SinglePassBlender uses nearest cross-section to determine per-pixel DOI. This is an approximation but works because roads typically have consistent DOI within their material group. |
| PostProcessingSmoother still uses old mask approach | The smoother operates on the RESULT of blending, using distance field only. It doesn't depend on ownership. Should work unchanged. |

---

## Implementation Changelog (2026-03-23)

### Planned Tasks (Tasks 0–9) — All Completed

All 10 tasks from the original plan were implemented as specified. Key implementation decisions:

| Task | Commit | Notes |
|------|--------|-------|
| Task 0: Remove obsolete code | `4a9a243` | Deleted ElevationMapBuilder, ProtectedBlendingProcessor, PriorityProtectionIndex. Stubbed orchestrator. |
| Task 1: EDT source tracking | `de5f705` | Used **Option B (JFA)** as recommended. Parallelized with `Parallel.For`. |
| Task 2: Combined mask with elevation | `dc8aa43` | Scanline fill with banking-aware elevation via `BankedTerrainHelper`. |
| Task 3: Exponential blend function | `498cdec` | Added `Exponential` to enum + `ApplyExponential` method. Extended `Apply()` with `falloffExponent` parameter. |
| Task 4: FalloffExponent parameter | `ad21823` | Added to `RoadSmoothingParameters`. |
| Task 5: SinglePassBlender | `8cd28f8` | Core blender as specified. `EnforceSideMaxSlope` carried over. |
| Task 6: Rewire orchestrator | `75041fa` | 3-step pipeline: mask → EDT w/ sources → blend. |
| Task 7: UI control | `5658323` | Conditional FalloffExponent field + serialization round-trip. |
| Task 8: Default to Exponential | `e307039` | Updated all presets + Program.cs + UI default. |
| Task 9: Verification | — | Full solution builds clean (0 code errors). |

### Code Review Fix

| Commit | Change |
|--------|--------|
| `d035632` | **JFA double-buffer elimination**: Removed per-pass `Clone()` of srcX/srcY arrays. In-place JFA updates are safe (races only propagate closer sources). **maxInfluence simplification**: Changed early-rejection cutoff from `maxHalfWidth + maxDoi` to just `maxDoi` (EDT distance is already from road edge). **stackalloc for scanline**: Replaced `List<float>` with `Span<float>` stackalloc in scanline fill to avoid GC pressure. |

### Post-Implementation Bug Fixes (Junction Artifacts)

Testing revealed that the original plan's design had three blind spots causing terrain cliffs at road junctions. These were diagnosed and fixed iteratively:

#### Fix 1: Spline Owner Map for DOI Lookup (`5a1db5b`)

**Problem:** The `SinglePassBlender` used `CrossSectionSpatialIndex.FindNearest()` to determine per-pixel DOI/falloff parameters. At junctions, this returned a cross-section from Road B (closer center point) while the EDT nearest-source pixel was from Road A (closer mask edge). The mismatch caused Road B's short DOI to partially blend Road A's pixels with raw terrain.

**Fix:** Added `int[,] SplineOwnerMap` to `CombinedMaskResult`. Each mask pixel records which spline filled it. The `SinglePassBlender` now looks up parameters from `splineOwnerMap[nearY, nearX]` — the spline that owns the nearest road mask pixel — instead of using the spatial index. Removed `CrossSectionSpatialIndex` dependency from the blender entirely.

**Files changed:** `RoadMaskBuilder.cs` (added SplineOwnerMap to record + fill), `SinglePassBlender.cs` (rewrote parameter lookup), `UnifiedTerrainBlender.cs` (pass-through).

#### Fix 2: Junction Gap Fill (`0ceb763`)

**Problem:** Per-spline quad polygons leave triangle-shaped gaps at junctions where roads meet at angles. Pixels in those gaps were not in the mask, so the blender treated them as terrain and blended with the raw heightmap, creating hard cliffs.

**Fix:** After building per-spline quads, iterate over `network.Junctions` (detected in Phase 1.8/3). For each junction with 2+ contributors, fill a circular area using `max(roadWidth/2 + margin)` across all contributing roads, pinned to the junction's harmonized elevation. Existing mask pixels are NOT overwritten (they have accurate per-pixel banking elevation).

**Files changed:** `RoadMaskBuilder.cs` (added junction gap fill loop after quad fill).

#### Fix 3: Corridor Overlap Protection (`a931521`)

**Problem:** The mask builder used `min(existing, new)` for pixel overlaps between different splines. When Road B's wide smoothing corridor (`RoadWidthMeters/2 + margin`) overlapped Road A's surface, the lower elevation was kept — pinning Road A's pixels to Road B's elevation, creating a cliff.

**Fix:** Changed overlap strategy: only same-spline overlaps can update elevation (adjacent polygon segments). **Different splines cannot overwrite each other's mask pixels** (first writer wins). This prevents corridor-on-surface conflicts.

**Files changed:** `RoadMaskBuilder.cs` (replaced `min()` with same-spline-only update).

#### Fix 4: Width-Ordered Processing (`55775e2`)

**Problem:** Dictionary iteration order for splines was non-deterministic. A narrow side road could be processed before the wide main road, claiming pixels in the main road's surface area. With "first writer wins", processing order determines who wins.

**Fix:** Sort splines **widest-first** (then highest priority descending) before mask filling. The wide main road claims its surface pixels first; the narrow side road's overlapping cross-sections are blocked by the "different spline can't overwrite" rule.

**Files changed:** `RoadMaskBuilder.cs` (added `OrderByDescending(RoadWidthMeters).ThenByDescending(Priority)` sort).

### Updated Architecture (Post-Fixes)

The mask builder now has a three-layer protection model:

```
Layer 1: Width-ordered processing — widest roads fill first
Layer 2: Same-spline-only overwrites — different splines can't touch each other's pixels
Layer 3: Junction gap fill — circular fill at junction centers catches remaining gaps
```

The `SinglePassBlender` uses `SplineOwnerMap` from the nearest EDT source pixel for parameter lookup, ensuring DOI/falloff always comes from the correct road.

### Plan Deviations

| Original Plan | Actual Implementation | Reason |
|--------------|----------------------|--------|
| `CombinedMaskResult` has 3 fields (Mask, ElevationMap, MaskedPixels) | Added 4th field: `SplineOwnerMap` | Needed for correct per-pixel DOI lookup at junctions |
| Mask overlap uses `min(hitZ)` matching BeamNG | Uses "first writer wins" with width-ordered processing | BeamNG's `min()` assumes same-road overlaps; our corridors cross different roads |
| `SinglePassBlender` uses `CrossSectionSpatialIndex` for per-pixel parameters | Uses `SplineOwnerMap` from EDT nearest source | Spatial index returned wrong road's parameters at junctions |
| `PostProcessingSmoother` enhanced with soft-boundary fade | Not implemented (deferred) | Not needed for core junction fix; can be added later |
| `BuildCombinedRoadCoreMask` marked `[Obsolete]` | Left as-is (no annotation) | Minor cleanup, not urgent |
