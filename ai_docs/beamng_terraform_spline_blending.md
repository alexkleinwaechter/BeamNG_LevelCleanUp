# BeamNG Terraform Spline-to-Terrain Blending — Reference Document

## Purpose

This document reverse-engineers the two terraform implementations found in BeamNG's Lua editor codebase and explains the algorithms in depth. The goal is to provide enough detail to port the relevant parts into our C# terrain generation pipeline (`UnifiedTerrainBlender` and friends).

There are **two independent terraform systems** in BeamNG:

| System | File | Used By | Algorithm Family |
|--------|------|---------|-----------------|
| **Master Spline Terraform** | `editor/terraform/terraform.lua` | Master Spline editor (spline-based roads, rivers, etc.) | SDF + exponential falloff + optional FBM noise + box blur |
| **Road Architect Terraform** | `editor/tech/roadArchitect/terraform.lua` | Road Architect (lane-level road profiles) | Mask dilation + iterative shrinking-kernel box blur |

Both share the same core idea: **pin the terrain to a known surface (the "ribbon") under the spline, then smoothly transition back to the original terrain over a configurable Domain of Influence (DOI).**

---

## 1. Master Spline Terraform (`terraform.lua`)

This is the simpler and more elegant of the two. It is the one called from `masterSpline.lua` line 1612:

```lua
terra.terraformToSources(DOI, margin, falloffExp, roughness, scale, sources)
```

### 1.1 Input Data Structure — "Sources"

A `sources` array is an array of polylines. Each polyline is an array of sample points:

```
sources = [
  [  -- polyline 1 (one road/spline)
    { pos = vec3(x,y,z), width = float, binormal = vec3(bx,by,bz) },
    { pos = vec3(x,y,z), width = float, binormal = vec3(bx,by,bz) },
    ...
  ],
  [  -- polyline 2
    ...
  ]
]
```

- `pos`: 3D world position of the spline centerline sample
- `width`: total road/spline width at this sample point (meters)
- `binormal`: unit vector pointing laterally (perpendicular to the spline tangent, in the road plane)

### 1.2 Parameters

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| `DOI` | 70.0 | 0–500m | Domain of Influence — max distance from road edge at which terrain is modified |
| `margin` | 5.0 | 1–20m | Extra padding beyond road half-width when building the road ribbon quads |
| `falloffExp` | 1.5 | 1–5 | Exponent for the blend weight curve. 1 = linear, >1 = S-like (terrain stays original longer, then curves in) |
| `roughness` | 0.1 | 0–1 | Amplitude of FBM noise added to the blended terrain |
| `scale` | 0.5 | 0–1 | Frequency scale of FBM noise (0 = large bumps ~50m, 1 = small bumps ~5m) |

### 1.3 Algorithm — Step by Step

#### Step 1: Compute AABB and Grid Bounds

```
AABB = union of all source point positions
Expand AABB by DOI × 2.5 on each side
Clamp to terrain block world extents
Convert world AABB → grid cell bounds (bXMin, bXMax, bYMin, bYMax)
xSize = bXMax - bXMin + 1
ySize = bYMax - bYMin + 1
```

The `2.5×` multiplier (`doiMultFactor`) ensures the working area is large enough for the falloff to reach zero well before the grid edge.

#### Step 2: Build Quadrilaterals from Sources (the "Ribbon")

For each consecutive pair of sample points in each polyline:

```
For segment (s1, s2):
  halfWidth1 = s1.width / 2 + margin
  halfWidth2 = s2.width / 2 + margin
  lateral1 = s1.binormal × halfWidth1
  lateral2 = s2.binormal × halfWidth2

  quad = [
    s1.pos - lateral1,  // left  front
    s1.pos + lateral1,  // right front
    s2.pos - lateral2,  // left  back
    s2.pos + lateral2   // right back
  ]
```

The first and last segments are extended longitudinally by `margin` meters to avoid open ends.

All quads are inserted into a **2D KD-tree** (box queries) for fast spatial lookup.

#### Step 3: Initialize Height/Mask/SDF Arrays

For each grid cell `(x, y)`:

```
worldPos = gridToWorld(x, y)
z = null

For each quad in KD-tree near worldPos:
  hitZ = bilinearInterpolateQuadHeight(worldPos, quad)  // inverse bilinear on 2D, then lerp Z
  if hitZ exists:
    z = min(hitZ - terrainZOffset, z)   // take lowest if overlapping

if z exists:    // Grid cell is UNDER the road ribbon
  height[idx] = z           // Pinned elevation
  mask[idx]   = 1           // Part of the road surface
  sdf[idx]    = 0           // Distance = 0
  mod[idx]    = 1           // Will be modified
  closestX/Y  = (x, y)     // Closest road pixel is itself
else:           // Grid cell is NOT under the road
  height[idx] = originalTerrainHeight
  mask[idx]   = 0
  sdf[idx]    = INFINITY
  mod[idx]    = 0
  closestX/Y  = (-1, -1)
```

**Key insight**: The `height[]` array stores the *target elevation* — road surface elevation for masked pixels, original terrain for unmasked pixels. The `sdf[]` will be used to blend between these.

#### Step 4: Propagate the Signed Distance Field (SDF)

Uses a **Jump Flooding-style propagation** (2 forward+backward passes over the grid):

```
Repeat 2 times:
  Forward pass (x: 0→xSize, y: 0→ySize):
    For each of 4 rectilinear neighbors (±x, ±y):
      If neighbor has a known closest road pixel:
        dist = euclidean distance from (x,y) to that road pixel
        If dist < current sdf[x,y]:
          Update sdf, closestX, closestY

  Backward pass (x: xSize→0, y: ySize→0):
    Same neighbor checks
```

After this, `sdf[idx]` contains the approximate Euclidean distance from each grid cell to the nearest road pixel, in **grid units** (not meters — but BeamNG's grid is typically 1m per cell so they're approximately equal).

**C# equivalent**: This is essentially a brute-force approximation of an EDT. Our `DistanceFieldCalculator` (Felzenszwalb & Huttenlocher) is strictly superior — it computes exact EDT in O(n) per dimension. The Lua code's 2-pass raster scan is O(n × passes) and produces approximate results.

#### Step 5: Diffuse — Blend Heights Using SDF Falloff

This is the **core blending step**:

```
DOIInv = 1.0 / DOI

For each grid cell (x, y):
  original = originalTerrainHeight(x, y)
  dist = sdf[idx]

  if mask[idx] == 1:
    // Under the road → pin to road elevation
    final[idx] = height[idx]
    mod[idx] = 1

  else if dist <= DOI:
    // Within domain of influence → blend
    closestRoadPixel = (closestX[idx], closestY[idx])
    ribbonZ = height[closestRoadPixel]    // elevation of nearest road surface point

    w = clamp((1.0 - dist / DOI) ^ falloffExp, 0, 1)

    final[idx] = lerp(original, ribbonZ, w)
    //         = original × (1 - w) + ribbonZ × w
    mod[idx] = 1

  else:
    // Outside DOI → untouched
    final[idx] = original
```

**The weight function**: `w = (1 - d/DOI)^falloffExp`

- At `d = 0` (road edge): `w = 1.0` → terrain = road elevation
- At `d = DOI`: `w = 0.0` → terrain = original
- `falloffExp = 1.0`: linear transition
- `falloffExp = 1.5` (default): slightly concave, terrain stays closer to original further out
- `falloffExp = 3.0+`: sharp road ledge with gentle far approach

**Critical detail**: The blend target (`ribbonZ`) is the elevation of the **closest road surface pixel**, not a global average. This means the terrain slopes toward the specific nearest point on the road, producing natural-looking embankments/cuts.

#### Step 6: Apply FBM Noise (Optional)

If `roughness > 0`:

```
noiseFreq = lerp(0.02, 0.2, scale)          // wavelength: 50m → 5m
noiseStrength = lerp(0.0, 1.0, roughness)   // amplitude
octaves = round(lerp(3, 6, scale))          // detail layers
lacunarity = 2.0                            // frequency doubling per octave
gain = 0.5                                  // amplitude halving per octave

For each modified grid cell:
  n = FBM_simplex(worldX × noiseFreq, worldY × noiseFreq, octaves, lacunarity, gain)
  w = clamp((1.0 - dist/DOI) ^ falloffExp, 0, 1)
  final[idx] += n × noiseStrength × w    // noise fades with same falloff as blend
```

The noise is modulated by the same falloff weight, so it's strongest near the road and fades to zero at the DOI boundary. This prevents a visible noise edge.

#### Step 7: Gaussian-like Box Blur (Smoothing Pass)

A **separable box blur** with radius `globalSmoothRadius = 4` grid cells:

```
X pass:
  For each (x, y):
    Average final[] values in [x - 4, x + 4] → globalTemp[idx]

Y pass:
  For each (x, y):
    If mask[idx] == 1 (road pixel):
      final[idx] = average of globalTemp in [y - 4, y + 4]
    Else if any neighbor in kernel was modified (influence > 0):
      blurredValue = average of globalTemp in [y - 4, y + 4]
      w = influence / kernelSize    // fraction of kernel that was modified
      final[idx] = lerp(final[idx], blurredValue, w)
```

**Key subtlety**: The Y-pass has a **fade-in for outer non-modified points**. If a grid cell is *outside* the `mod` zone but has neighbors that *were* modified, it gets partially blurred. The blending weight is proportional to how many of its kernel neighbors were modified. This eliminates the hard boundary at the edge of the `mod` zone.

#### Step 8: Write to Terrain

All cells where `mod[idx] > 0.5` are written to the heightmap via `tb:setHeightGrid()`.

### 1.4 Visual Summary of the Pipeline

```
   Road Ribbon (quads)
        ↓
   [Mask: road=1, else=0]  +  [Height: roadZ or originalZ]
        ↓
   [SDF propagation: distance to nearest road pixel]
        ↓
   [Exponential falloff blend: w = (1 - d/DOI)^exp]
   [final = lerp(original, nearestRoadZ, w)]
        ↓
   [Optional: FBM noise, modulated by same weight]
        ↓
   [Box blur with soft boundary fade]
        ↓
   [Write to terrain heightmap]
```

---

## 2. Road Architect Terraform (`tech/roadArchitect/terraform.lua`)

Used by the Road Architect system for lane-level road profiles. More complex, uses a different blending strategy.

### 2.1 Parameters

| Parameter | Description |
|-----------|-------------|
| `DOI` | Domain of Influence (min 5m) — controls how far the blending extends |
| `margin` | Dilation radius for the road mask (meters) — creates a wider "fixed" zone around the road |

No falloff exponent or noise — this system uses iterative kernel averaging instead.

### 2.2 Algorithm — Step by Step

#### Step 1: Build Road Quads

Same as Master Spline: consecutive road render data points form quads from the leftmost to rightmost lane edges. Bridges and tunnel sections are excluded.

#### Step 2: Rasterize Fixed Mask

For each grid cell, test against quads via KD-tree + inverse bilinear interpolation:

```
For each grid cell (x, y):
  if cell is under any road quad:
    fixedMask[x][y] = 1
    fixedHeights[x][y] = quadZ - terrainZMin
  else:
    fixedMask[x][y] = 0
    fixedHeights[x][y] = originalTerrainHeight
```

#### Step 3: Mask Dilation (Two-Stage)

**Stage 1**: Dilate `fixedMask` by 1 pixel to eliminate edge artifacts:

```
(fixedMask, fixedHeights) = dilateMaskWithHeights(fixedMask, fixedHeights, xSize, ySize, radius=1)
```

**Stage 2**: Dilate again by `margin` pixels to create the wider road influence zone:

```
(mask, height) = dilateMaskWithHeights(fixedMask, fixedHeights, xSize, ySize, radius=margin)
```

The dilation is **circular** (distance check: `dx² + dy² ≤ radius²`) and uses **running average blending** for heights:

```
For each road pixel (x, y) in the source mask:
  For each pixel (nx, ny) within radius:
    newMask[nx][ny] = 1
    newHeights[nx][ny] = runningAverage(existingHeight, roadHeight)
```

This means overlapping dilations produce averaged heights, not last-writer-wins.

#### Step 4: Iterative Shrinking-Kernel Box Blur

This is the most distinctive part of the Road Architect approach. Instead of a single SDF + falloff, it uses **multiple passes of box blur with decreasing kernel size**:

```
numIter = ceil(0.5 × sqrt(8 × DOI + 1) - 1)
// For DOI=70m → numIter ≈ 11
// For DOI=100m → numIter ≈ 13

For i = numIter down to 1:
  kernelHalf = i
  kernelSize = 2i + 1

  X-pass (horizontal sliding window):
    For each row y:
      Maintain running sum/denom over kernel
      For each x:
        if any road pixel in kernel (denomS > 0):
          changes[x][y] = runningSum / kernelSize
          mark as modified
        else:
          changes[x][y] = height[x][y]   // unchanged

  Y-pass (vertical sliding window):
    For each column x:
      Maintain running sum/denom over kernel
      For each y:
        if any road pixel in kernel:
          changes[x][y] = average of (X-pass result + Y-running-sum) × 0.5

  Copy-back pass:
    For each (x, y):
      height[x][y] = mask[x][y] × height[x][y]        // road pixels: keep original road elevation
                   + (1 - mask[x][y]) × changes[x][y]  // non-road pixels: use blurred value
```

**Why shrinking kernels?** Starting with the largest kernel (≈ `numIter` cells) smooths out the coarsest transitions first. Each subsequent smaller kernel refines finer details. The road mask pixels are always preserved (the `mask * height + (1-mask) * changes` copy-back), so each iteration only modifies the *surrounding* terrain while keeping the road surface locked.

This produces a similar result to the SDF exponential falloff but through progressive diffusion rather than direct distance-based weighting.

#### Step 5: Post-Averaging

A final averaging pass (`averageMask`) smooths modified non-fixed cells using a 5×5 kernel (radius=2):

```
For each modified but non-fixed cell:
  height[x][y] = average of height in 5×5 neighborhood
```

`fixedMask` cells (the original rasterized road surface) are never modified.

---

## 3. Multi-Spline Safety: Does BeamNG Protect Other Roads' Surfaces?

This is a critical architectural question — when terraforming one road, does the algorithm destroy the surface of nearby already-processed roads? **BeamNG sidesteps this by processing all roads in a single pass, but has a known gap in its per-road API.**

### 3.1 Which Functions Are Safe vs Destructive

| Function | Scope | Multi-Spline Safe? | Why |
|----------|-------|-------------------|-----|
| `terraformToSources()` | All sources at once | **YES** | All splines contribute to one shared mask + SDF. Every road surface is pinned simultaneously. |
| `terraformMultiRoads()` | All roads in group | **YES** | Collects quads from all roads into one mask/KD-tree before blending. |
| `conformTerrainToRoad(rIdx)` | Single road | **NO** | Reads current terrain as "original" — if road A was already terraformed, road B's blend zone will modify road A's surface. |

### 3.2 How the Safe Functions Avoid Destruction

In `terraformToSources` (Master Spline) and `terraformMultiRoads` (Road Architect), the safety comes from a simple design choice: **build one combined mask from ALL roads before any blending happens.**

```
Step 1: Collect quads from ALL splines → single KD-tree
Step 2: For each grid cell, test against ALL quads
         → if under ANY road: mask=1, height=roadSurfaceZ
         → if under NO road: mask=0, height=originalTerrain
Step 3: Compute SDF/blur from the COMBINED mask
Step 4: Blend — road surface pixels are ALL locked before blending begins
```

Because every road surface is marked in the mask before the blend step, no road surface can be overwritten by another road's blend zone. The mask acts as a global "do not touch" layer for all road surfaces simultaneously.

### 3.3 How `conformTerrainToRoad` Destroys Other Roads

When processing a single road in isolation:

```
1. Build mask from ONLY road B's quads
2. For each grid cell NOT under road B:
     height = tb:getHeightGrid(x, y)   ← reads CURRENT terrain, including road A's surface!
3. Blend: cells within DOI of road B get blended toward road B's surface
   → Road A's surface pixels (which are NOT in road B's mask) get treated as "original terrain"
   → They get pulled toward road B's elevation via the falloff weight
```

**Concrete example**: Road A at elevation 50m, Road B at elevation 45m, 30m apart. When `conformTerrainToRoad` runs for road B with DOI=70m:
- Road A's surface is 30m from road B → well within DOI
- Blend weight: `w = (1 - 30/70)^1.5 ≈ 0.43`
- Road A's pixels get blended: `lerp(50, 45, 0.43) ≈ 47.8m` — **road A's surface is now corrupted**

### 3.4 Relevance to Our C# Pipeline

**We face exactly this problem.** Our pipeline processes roads sequentially (per-material, per-spline), and each pass reads the current heightmap state. This means:

1. **Spline A** is blended → terrain around A is smoothed, A's surface is correct
2. **Spline B** is blended → B's blend zone overlaps A's surface → A gets corrupted
3. **Spline C** is blended → C's blend zone corrupts both A and B

BeamNG's solution is architecturally simple: **never blend per-road, always blend the entire network at once.** This is what `terraformToSources` and `terraformMultiRoads` do.

Our `UnifiedTerrainBlender` was designed with this in mind (protection mask + ownership tracking), but the key insight from BeamNG is:

> **The combined mask must be built from ALL splines BEFORE any blending occurs. The mask is the protection mechanism — not per-spline priority rules or sequential ordering.**

If our pipeline ever processes splines individually (even with "protection"), it's vulnerable to the same issue as `conformTerrainToRoad`. The only truly safe approach is the single-pass combined-mask approach that BeamNG uses in its working functions.

### 3.5 Design Implications

| Approach | Safe? | Trade-off |
|----------|-------|-----------|
| **Single-pass all-roads** (BeamNG's `terraformToSources`) | Yes | Must reprocess entire network for any change; higher memory for large networks |
| **Sequential per-road with protection mask** (our current attempt) | Partially | Protection mask can miss edge cases; ordering-dependent artifacts |
| **Single-pass with pre-built global mask** (recommended) | Yes | Build combined mask once, then blend once — same as BeamNG but with our better EDT |

**Recommendation**: Adopt BeamNG's pattern — build one combined road mask from ALL splines, compute one global EDT, then blend in a single pass. This is what `UnifiedTerrainBlender` aims to do, but any code paths that process splines individually should be eliminated or converted to the single-pass model.

---

## 4. Comparison of the Two Blend Algorithms

| Aspect | Master Spline | Road Architect |
|--------|---------------|----------------|
| **Blend method** | SDF distance + `(1 - d/DOI)^exp` weight | Iterative shrinking-kernel box blur |
| **Noise support** | FBM Simplex noise, distance-weighted | None |
| **Smoothing** | Single box blur pass (radius=4) | Multiple blur passes + post-averaging |
| **Computational complexity** | O(n × passes) for SDF + O(n) blur | O(n × numIter) for iterative blur |
| **Quality at transitions** | Very smooth with exp≥1.5, sharp control via exp | Smooth but less controllable shape |
| **Edge handling** | `mod` zone soft boundary in blur | Mask dilation + averaging smoothing |
| **Suitable for** | General splines (roads, rivers, paths) | Lane-level road profiles with precise width |

---

## 5. Relevance to Our C# Implementation

### 5.1 What We Already Have (UnifiedTerrainBlender)

Our current implementation uses:
- **Felzenszwalb & Huttenlocher EDT** (exact, O(n) per dimension) — strictly better than Lua's 2-pass SDF propagation
- **Road core mask** with ownership tracking
- **Protected blending** with per-spline blend ranges
- **Post-processing smoothing** (Gaussian, Box, Bilateral)

### 5.2 What We Could Adopt from BeamNG

#### A. The Exponential Falloff Weight Function (High Value)

The weight function `w = (1 - d/DOI)^falloffExp` is simple and effective. Our `ProtectedBlendingProcessor` could use this instead of or in addition to its current blending curve.

```csharp
float normalizedDist = distanceField[x, y] / doiPixels;
float weight = MathF.Pow(MathF.Max(0f, 1f - normalizedDist), falloffExponent);
float blended = original * (1f - weight) + ribbonElevation * weight;
```

Key: `falloffExponent` controls the shape:
- `1.0` = linear
- `1.5` = default, slight concavity (recommended)
- `3.0+` = sharp shelf near road, gentle far approach

#### B. Closest-Point Elevation Lookup (High Value)

BeamNG blends toward the elevation of the **nearest road surface pixel**, not an average or interpolated value. This is what makes the blending look natural — the terrain slopes toward the specific nearest point on the road.

Our EDT (`DistanceFieldCalculator`) already computes distances but doesn't track which road pixel is closest. Adding a **nearest-source-pixel map** (like BeamNG's `closestX`/`closestY` arrays) would let us look up the correct ribbon elevation for each blend pixel.

However, this may already be approximated by our `ElevationMapBuilder` which tracks per-pixel ownership.

#### C. FBM Noise for Natural Terrain Break-Up (Medium Value)

Adding distance-weighted FBM noise to the blended zone would break up the smooth "artificial" look of pure mathematical blending. The key is multiplying by the same falloff weight so noise fades to zero at the DOI boundary:

```csharp
float noise = FBM(worldX * freq, worldY * freq, octaves, lacunarity, gain);
blended += noise * strength * weight;
```

#### D. Soft-Boundary Blur (Medium Value)

BeamNG's Y-pass blur has a clever soft boundary: cells outside the modified zone that have modified neighbors get partially blurred based on how many neighbors were modified. This eliminates the visible edge at the `mod` zone boundary. Our `PostProcessingSmoother` could adopt this pattern.

#### E. The Shrinking-Kernel Iterative Approach (Low Value for Us)

The Road Architect's iterative approach is interesting but our EDT-based system is already more precise. The iterative kernel approach is essentially a workaround for not having a good distance field — it approximates a distance-weighted blend through progressive diffusion. Since we have exact EDT, we don't need this.

### 5.3 Key Parameters to Expose

Based on BeamNG's defaults, recommended parameter ranges for our system:

| Parameter | Recommended Default | Range | Notes |
|-----------|-------------------|-------|-------|
| DOI (meters) | 70 | 5–500 | Per-spline or global; 70m is good for typical roads |
| Margin (meters) | 5 | 1–20 | Extra zone beyond road edge where terrain is pinned flat |
| Falloff Exponent | 1.5 | 1.0–5.0 | 1=linear, 1.5=natural, 3+=sharp ledge |
| Noise Roughness | 0.1 | 0–1 | Low default to keep terrain clean near roads |
| Noise Scale | 0.5 | 0–1 | 0=large bumps (50m wavelength), 1=small (5m) |
| Blur Radius (cells) | 4 | 1–8 | Post-blend smoothing kernel |

---

## 6. Pseudo-Code for C# Port of the Master Spline Approach

This is the most portable algorithm. Pseudo-code for the core blend step:

```
Input:
  heightMap[w, h]          — original terrain
  roadMask[w, h]           — 1 where road surface, 0 elsewhere
  roadElevation[w, h]      — road surface Z where roadMask=1
  distanceField[w, h]      — EDT distance to nearest road pixel (in pixels)
  nearestRoadX[w, h]       — X coord of nearest road pixel (from EDT)
  nearestRoadY[w, h]       — Y coord of nearest road pixel (from EDT)
  doiPixels                — DOI converted to pixel units
  falloffExp               — falloff exponent (1.5 default)
  metersPerPixel           — grid resolution

Output:
  blendedMap[w, h]

Algorithm:
  doiInv = 1.0 / doiPixels

  for each (x, y):
    if roadMask[x, y] == 1:
      blendedMap[x, y] = roadElevation[x, y]

    else if distanceField[x, y] <= doiPixels:
      // Find elevation of nearest road pixel
      nx = nearestRoadX[x, y]
      ny = nearestRoadY[x, y]
      ribbonZ = roadElevation[nx, ny]

      // Compute blend weight
      d = distanceField[x, y]
      w = clamp((1.0 - d * doiInv) ^ falloffExp, 0, 1)

      // Blend
      blendedMap[x, y] = lerp(heightMap[x, y], ribbonZ, w)

    else:
      blendedMap[x, y] = heightMap[x, y]

  // Optional: add FBM noise
  if noiseEnabled:
    for each modified (x, y) where distanceField[x,y] <= doiPixels:
      worldX = x * metersPerPixel + offsetX
      worldY = y * metersPerPixel + offsetY
      n = FBM(worldX * noiseFreq, worldY * noiseFreq, octaves, lacunarity, gain)
      d = distanceField[x, y]
      w = clamp((1.0 - d * doiInv) ^ falloffExp, 0, 1)
      blendedMap[x, y] += n * noiseStrength * w

  // Smoothing pass with soft boundary
  SoftBoundaryBoxBlur(blendedMap, modifiedMask, radius=4)
```

### Soft Boundary Box Blur:

```
For each (x, y):
  if modified[x, y]:
    blendedMap[x, y] = boxAverage(x, y, radius)
  else:
    influence = count of modified neighbors in kernel
    if influence > 0:
      w = influence / kernelSize
      blendedMap[x, y] = lerp(blendedMap[x, y], boxAverage(x, y, radius), w)
```

---

## 7. Source File Reference

| File | Key Functions |
|------|--------------|
| `editor/terraform/terraform.lua` | `terraformToSources()` — main SDF-based blend algorithm |
| `editor/tech/roadArchitect/terraform.lua` | `conformTerrainToRoad()`, `terraformMultiRoads()` — iterative kernel blend |
| `editor/toolUtilities/geom.lua` | `getAllQuadrilaterals()`, `populateTreeQuads()`, `intersectsUpQuadBarycentric()`, `computeSourcesAABB()` |
| `editor/toolUtilities/util.lua` | `getSourcesSingle()`, `getAllSources()` — build source data from splines |
| `editor/toolUtilities/simplex.lua` | Simplex noise used in FBM |
| `editor/masterSpline/layerMgr.lua` | Default parameter values (DOI=70, margin=5, falloff=1.5, roughness=0.1, scale=0.5) |
| `editor/masterSpline.lua` | UI integration, calls `terraformToSources()` at line 1612 |
