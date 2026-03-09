# Junction "Ditch" Artifact Fix Plan

## Problem

Connecting roads approaching junctions exhibit a shallow ditch (elevation dip) within the junction blending zone. The exported master spline shows smooth elevation, but the actual terrain/road surface has a visible depression ~5-15m from the junction center.

## Root Cause Analysis

### Root Cause 1 (Primary): WI-6 Endpoint Anchoring Creates a Depression That Phase 3 Ramp Cannot Fully Compensate

**Mechanism:**

1. **Phase 2 endpoint anchoring** (`UnifiedRoadSmoother.cs:784`, `OptimizedElevationSmoother.cs:215-271`) biases the terminating road's endpoint toward terrain elevation using exponential decay:
   ```
   weight = 0.5 * exp(-dist / blendDistance)
   ```
   At the endpoint (dist=0), weight=0.5 pulls elevation halfway toward terrain. This creates a depression in the first ~10 cross-sections of the terminating road.

2. **Phase 3 Pass 1** (`NetworkJunctionHarmonizer.cs:952-987`) ramps from `HarmonizedElevation` toward the **original (already-depressed)** elevation:
   ```
   newElev = HarmonizedElevation × weight + originalElev × (1 - weight)
   ```
   Near junction (weight≈1.0): correct. At 5-15m (weight≈0.7-0.9): blends with anchoring-depressed values, creating a valley.

**Example on 2% slope:**
```
Phase 2 (smooth):     [100.0, 99.8, 99.6, 99.4, 99.2, 99.0]  (far→junction)
After anchoring:      [100.0, 99.8, 99.6, 99.3, 98.9, 98.5]  (pulled down near junction)
HarmonizedElevation:  99.0  (primary surface at connection point)

Phase 3 Pass 1 result:
  CS@0m  (w=1.0):  99.0×1.0 + 98.5×0.0 = 99.0   ✓ correct
  CS@5m  (w=0.9):  99.0×0.9 + 98.9×0.1 = 98.99  ← DITCH (below junction!)
  CS@10m (w=0.75): 99.0×0.75+ 99.3×0.25= 99.08  ← recovering
  CS@15m (w=0.5):  99.0×0.5 + 99.6×0.5 = 99.3
  CS@30m (w=0.0):  100.0
```

The CS at 5m is BELOW the junction center (99.0) and below the next CS (99.08) — this is the ditch.

### Root Cause 2 (Secondary): Pass 2 Distance Guard Creates Transition Discontinuity

`NetworkJunctionHarmonizer.cs:1183-1186`:
```csharp
if (interpolatedCenter.HasValue && distToPrimaryRoad <= primaryRoadWidth)
    cs.TargetElevation = interpolatedCenter.Value;
```

Pass 2 overwrites TargetElevation with surface-following values (tracking actual sloped primary road surface) but ONLY within `primaryRoadWidth` distance (~7m). Beyond that, Pass 1's flat ramp remains. On sloped primary roads, the surface-following values and the Pass 1 ramp don't match at this hard boundary, creating a secondary kink.

### Why Iterative Refinement Doesn't Fully Fix It

- Iteration 2+ uses `HarmonizedElevation` as anchor instead of terrain, reducing anchoring pull
- But the smoother operates on the already-ditched profile and spreads the energy
- Each iteration reduces ditch by ~50-70%, leaving ~10-15% residual after 3 iterations

### Phase 3.5 JunctionBankingAdapter Preserves (Not Creates) the Ditch

`JunctionBankingAdapter.cs:374-381` applies a uniform offset (scaled by distance) to all cross-sections. This shifts everything up/down but preserves the relative ditch shape.

---

## Fix Plan

### Fix 1: Skip Endpoint Anchoring for Multi-Road Junctions (Primary Fix)

**File:** `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs`
**Method:** `BuildEndpointAnchorLookup` (line ~815)

**Change:** Skip anchoring for T-junctions, Y-junctions, crossroads, and other multi-road junctions. Anchoring should only apply to `JunctionType.Endpoint` (isolated dead-end roads that need to meet terrain).

```csharp
foreach (var junction in network.Junctions)
{
    if (junction.IsExcluded) continue;
    // NEW: Only anchor isolated endpoints — multi-road junctions are handled by Phase 3 harmonization.
    // Anchoring at multi-road junctions creates a depression that the Phase 3 ramp blends with,
    // producing the "ditch" artifact.
    if (junction.Type != JunctionType.Endpoint) continue;

    // ... rest of existing anchoring logic unchanged
}
```

**Rationale:** Phase 3 harmonization computes the correct target elevation for multi-road junctions (primary road surface elevation). The anchoring pre-bias toward terrain is redundant for these junctions and actively harmful — it creates a depression in the Phase 2 profile that Phase 3's weighted blend cannot fully compensate.

**Risk:** Low. The anchoring was designed to "reduce the gap that Phase 3 harmonization must bridge" but in practice creates a worse gap (depression). Phase 3 can handle the full gap from smooth profile to harmonized elevation directly — that's what its blend function is for.

### Fix 2: Replace Pass 2 Distance Guard with Smooth Falloff (Secondary Fix)

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`
**Method:** `PropagateEdgeConstraintsForTJunctions` (line ~1177)

**Change:** Replace the hard `distToPrimaryRoad <= primaryRoadWidth` cutoff with a smooth fade-out:

```csharp
// BEFORE (hard cutoff):
if (interpolatedCenter.HasValue && distToPrimaryRoad <= primaryRoadWidth)
    cs.TargetElevation = interpolatedCenter.Value;

// AFTER (smooth falloff over 2× road width):
if (interpolatedCenter.HasValue)
{
    var maxSurfaceFollowDistance = primaryRoadWidth * 2.0f;
    if (distToPrimaryRoad <= maxSurfaceFollowDistance)
    {
        var surfaceWeight = 1.0f - (distToPrimaryRoad / maxSurfaceFollowDistance);
        // Smooth the falloff with cubic ease-out
        surfaceWeight = surfaceWeight * surfaceWeight * (3.0f - 2.0f * surfaceWeight);
        cs.TargetElevation = interpolatedCenter.Value * surfaceWeight
                           + cs.TargetElevation * (1.0f - surfaceWeight);
    }
}
```

**Rationale:** Eliminates the hard transition between surface-following (Pass 2) and flat ramp (Pass 1) zones. The smooth falloff blends between the two over twice the road width, preventing the kink on sloped primary roads.

**Risk:** Low-medium. The clamped version was added to prevent extrapolation errors at distant cross-sections. The smooth falloff still limits the influence range (2× road width instead of 1×) and the weight drops to zero, preventing wild extrapolation. However, `GetPrimarySurfaceElevation` extrapolates banking/slope linearly — should use `GetPrimarySurfaceElevationClamped` (already defined but unused at line 323 of `JunctionSurfaceCalculator.cs`) or `FindProjectedPrimaryCrossSection` (already defined but unused at line 1230) to prevent extrapolation errors at the extended range.

### Fix 3 (Optional): Monotone Profile Enforcement After Phase 3

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`
**Location:** After `PropagateJunctionConstraints` returns (around line 1040)

**Change:** Add a post-processing pass that ensures no local minima exist in the elevation profile within the blend zone of terminating roads:

```csharp
// After Pass 1 + Pass 2, for each terminating road:
// Walk from junction outward along the blend zone.
// If any CS elevation is below the junction HarmonizedElevation (for ascending roads)
// or above it (for descending roads), clamp to eliminate the dip.
```

**Rationale:** Belt-and-suspenders defense against any residual ditch from other interactions. Simple and safe.

**Risk:** Very low. Only modifies cross-sections that have a provably incorrect local minimum.

---

## Implementation Order

1. **Fix 1 first** — this alone should eliminate most of the ditch
2. **Test** with the Italy map junction from the screenshot
3. **Fix 2** if a secondary kink remains on sloped primary roads
4. **Fix 3** only if edge cases still produce dips

## Files Involved

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs` | Fix 1: Add junction type filter in `BuildEndpointAnchorLookup` |
| `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs` | Fix 2: Smooth falloff in `PropagateEdgeConstraintsForTJunctions`; Fix 3: monotone enforcement |
| `BeamNgTerrainPoc/Terrain/Algorithms/JunctionSurfaceCalculator.cs` | Fix 2 support: use existing `GetPrimarySurfaceElevationClamped` and `FindProjectedPrimaryCrossSection` |

## Key Architectural Insight

The endpoint anchoring (WI-6) was designed to pre-bias Phase 2 profiles toward junction elevations, reducing the correction Phase 3 needs. In practice, for multi-road junctions:
- The "correct" elevation is the **primary road surface** (computed in Phase 3), NOT terrain
- Biasing toward terrain creates a depression that Phase 3's weighted blend CANNOT eliminate (it blends with the depressed original, inheriting part of the depression)
- The iterative loop reduces but never fully eliminates the residual

**Principle:** Don't pre-bias toward an approximate target (terrain) when a precise target (primary surface) will be computed later. Let Phase 3 handle the full gap.
