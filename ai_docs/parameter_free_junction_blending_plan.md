# Parameter-Free Junction Blending Plan

## What We Did (Current Session)

### 1. Removed Global Junction Settings
Eliminated the dual global/per-material system. Junction parameters now exist ONLY per-material with defaults (5m detection, 30m blend). Removed `UseGlobalSettings` flag, global UI section, and all plumbing through 17 files.

### 2. Implemented Correction-Based Rubberband for CubicHermiteC1
Replaced the weight-based blend (which creates hard zone boundaries) with a parameter-free correction approach in `ApplyRubberbandProfiles`:

```
startDelta = junctionElev_start - terrainElev_start
endDelta   = junctionElev_end   - terrainElev_end
t = distAlongRoad / roadLength

h00(t) = 2t^3 - 3t^2 + 1   // smooth 1->0, zero slope at both ends
h01(t) = -2t^3 + 3t^2       // smooth 0->1, zero slope at both ends

newElev = terrainElev + startDelta * h00 + endDelta * h01
```

This computes the elevation for every cross-section on the road with NO blend distance parameter. The road follows terrain everywhere, smoothly shifted to hit junction elevations at endpoints.

### 3. Added Road-Length Cap for Legacy Blend Functions
For non-CubicHermiteC1 functions (Linear, Cosine, Cubic, Quintic), blend distance is capped to 40% of road length per endpoint.

## The Remaining Problem

**The bump still exists and still moves when blend distance changes.** The correction-based rubberband in `ApplyRubberbandProfiles` is correctly parameter-free, but THREE other methods in the same Phase 3 pipeline still use blend distance for CubicHermiteC1:

### Leak 1: `ApplyMidSplineCrossingInfluences` (Lines ~1083-1126)
For roads that pass through another road without terminating (crossings). Uses `blendDistance` to define the influence zone and computes Hermite boundaries at that distance. The Hermite boundary elevation/slope changes when blend distance changes, moving the bump.

### Leak 2: `PropagateEdgeConstraintsForTJunctions` (Lines ~1170-1287)
Propagates edge elevation constraints from T-junctions along terminating roads. Uses `blendDistance` as the zone boundary. Affects banking-related edge elevations.

### Leak 3: `ComputeJunctionIdwWeightModifiers` (Lines ~1715-1814)
Computes IDW weight modifiers for Phase 4 terrain blending. Uses blend distance to define the taper zone where terminating road weights are suppressed near junctions. Changes here affect the baseline terrain elevation.

### Additionally: The `splineEndJunctions` Lookup Table
The lookup table built at the start of `ApplyRubberbandProfiles` (lines 884-919) still computes `blendDist` for every junction contributor. CubicHermiteC1 doesn't use these values, but they're computed and stored wastefully.

## The Goal

**Compute perfect road elevation profiles between junctions with ZERO external parameters for CubicHermiteC1.** The algorithm should:
1. Take the terrain-following elevation profile (from Phase 2)
2. Take the junction harmonized elevations (from Phase 3 Step 1)
3. Compute a smooth elevation profile that hits junctions at endpoints and follows terrain in between
4. No blend distance, no blend function type, no detection radius needed
5. C1 continuous everywhere (no slope discontinuities, no bumps)

## Implementation Plan

### Step 1: Make MidSplineCrossings Parameter-Free for CubicHermiteC1

**File**: `NetworkJunctionHarmonizer.cs`, method `ApplyMidSplineCrossingInfluences`

For CubicHermiteC1, MidSplineCrossing junctions should use the same correction-based approach as the endpoint rubberband. A crossing is just another "anchor point" on the road that needs to hit a specific elevation.

**Approach**: When processing a MidSplineCrossing for a CubicHermiteC1 spline:
- Compute the crossing point's `delta = harmonizedElev - terrainFollowingElev`
- Apply a symmetric correction that decays in both directions from the crossing
- Use the same Hermite basis function: correction decays to 0 at the road endpoints (or at the next junction, whichever is closer)
- No blend distance needed

### Step 2: Make Edge Constraint Propagation Parameter-Free for CubicHermiteC1

**File**: `NetworkJunctionHarmonizer.cs`, method `PropagateEdgeConstraintsForTJunctions`

Edge constraints handle banking (lateral tilt) at T-junctions. For CubicHermiteC1:
- Instead of propagating within a fixed blend distance zone, propagate along the entire terminating road
- Use the same Hermite decay (h00 based on normalized distance along the terminating road)
- The constraint strength naturally decays from 1.0 at the junction to 0.0 at the far end

### Step 3: Make IDW Weight Modifiers Parameter-Free for CubicHermiteC1

**File**: `NetworkJunctionHarmonizer.cs`, method `ComputeJunctionIdwWeightModifiers`

IDW weight modifiers suppress terminating roads' influence near junctions in Phase 4 terrain blending. For CubicHermiteC1:
- Instead of tapering within a fixed distance, taper based on normalized position along the road
- Use Hermite basis: `modifier = minWeight + (1 - minWeight) * h01(t)` where t = distFromJunction / roadLength
- No taper distance parameter needed

### Step 4: Clean Up the Lookup Table

In `ApplyRubberbandProfiles`, the `splineEndJunctions` lookup table stores `(junction, blendDist)`. For CubicHermiteC1, `blendDist` is not needed. Either:
- Skip computing `blendDist` for CubicHermiteC1 splines
- Or change the lookup to store `(junction, blendDist?)` with null for CubicHermiteC1

### Step 5: Remove/Hide UI Parameters for CubicHermiteC1

Once all four code paths are parameter-free, the following UI elements should be hidden/disabled when CubicHermiteC1 is selected:

**Per-material junction settings to hide/disable:**
- "Min Blend Distance (m)" field - not used
- "Auto-calculate from road width" toggle - not used
- `BlendDistanceMultiplier`, `BlendDistanceOffset`, `MinAutoBlendDistanceMeters`, `MaxAutoBlendDistanceMeters` - not used

**Per-material settings to KEEP:**
- "Detection Radius (m)" - still needed for junction detection (topology)
- "Enable Junction Harmonization" toggle - master enable
- "Blend Function" dropdown - to choose CubicHermiteC1 vs legacy
- All roundabout settings - separate system

**Parameters in `JunctionHarmonizationParameters.cs` to mark as legacy:**
- `JunctionBlendDistanceMeters` - only for non-CubicHermiteC1
- `AutoCalculateBlendDistance` - only for non-CubicHermiteC1
- `BlendDistanceMultiplier`, `BlendDistanceOffset` - only for non-CubicHermiteC1
- `MinAutoBlendDistanceMeters`, `MaxAutoBlendDistanceMeters` - only for non-CubicHermiteC1
- `IdwFilterTaperDistanceMeters` - only for non-CubicHermiteC1

**Parameters to keep for all modes:**
- `EnableJunctionHarmonization`
- `JunctionDetectionRadiusMeters`
- `BlendFunctionType`
- `MinTerminatingIdwWeight` (the IDW minimum weight value, not the distance)
- `EnableJunctionIdwFiltering`
- All roundabout parameters
- All debug parameters

## Key Files to Modify

| File | What Changes |
|------|-------------|
| `NetworkJunctionHarmonizer.cs` | Steps 1-4: make MidSplineCrossing, edge constraints, IDW modifiers parameter-free |
| `TerrainMaterialSettings.razor` | Step 5: hide blend distance fields when CubicHermiteC1 selected |
| `TerrainMaterialSettings.razor.cs` | Step 5: conditional visibility logic |
| `JunctionHarmonizationParameters.cs` | Add comments marking blend-distance params as legacy |

## Verification

1. Set blend function to CubicHermiteC1
2. Generate terrain
3. Change "Min Blend Distance" to any value (20m, 50m, 100m) and regenerate
4. **Expected**: Identical result regardless of blend distance value
5. **Expected**: No bumps at any distance from junctions
6. **Expected**: Roads smoothly follow terrain, hitting junction elevations at intersections

## Mathematical Properties of the Correction-Based Approach

For a road between junction A (start) and junction B (end):

```
t ∈ [0, 1]  (normalized position along road)

correction(t) = deltaA × (2t³ - 3t² + 1) + deltaB × (-2t³ + 3t²)
```

**At t=0 (junction A):**
- correction = deltaA × 1 + deltaB × 0 = deltaA
- elevation = terrainElev + deltaA = junctionElev_A ✓

**At t=1 (junction B):**
- correction = deltaA × 0 + deltaB × 1 = deltaB
- elevation = terrainElev + deltaB = junctionElev_B ✓

**At t=0.5 (middle):**
- h00(0.5) = 0.5, h01(0.5) = 0.5
- correction = (deltaA + deltaB) / 2
- elevation = terrainElev + average of both corrections (terrain-following with half correction)

**Slope of correction:**
- d(correction)/dt at t=0: 6×0² - 6×0 = 0 → adds zero slope at start ✓
- d(correction)/dt at t=1: 6×1 - 6×1 = 0 → adds zero slope at end ✓
- Terrain slope is preserved at both endpoints → C1 continuous ✓

**For a road with only ONE junction (start only):**
- correction(t) = deltaA × h00(t)
- At t=0: correction = deltaA (full junction shift)
- At t=1: correction = 0 (pure terrain-following)
- Slope at t=1: 0 (smooth arrival at terrain)

**No overshoot possible** because:
- h00(t) is monotonically decreasing from 1 to 0
- h01(t) is monotonically increasing from 0 to 1
- The correction is a weighted sum of two monotone functions with bounded coefficients
