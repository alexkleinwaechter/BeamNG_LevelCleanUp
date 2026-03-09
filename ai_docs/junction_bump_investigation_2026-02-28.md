# Junction Bump Investigation — 2026-02-28

## Problem
Visible bump/kink on terminating roads near T-junctions and multi-way junctions. The bump appears in both the 3D mesh (DAE export) and the in-game terrain surface. It occurs roughly at the edge of the continuous road, where the terminating road transitions from "inside" to "outside" the continuous road's body.

## What We Fixed (Working Improvements)

### 1. Phase 3.6: Edge constraints moved after banking finalization
**Files:** `NetworkJunctionHarmonizer.cs`, `UnifiedRoadSmoother.cs`
- **Problem:** Phase 3.3 ran inside the iteration loop BEFORE Phase 3.5 banking finalization. Constrained edges used stale `LeftEdgeElevation`/`RightEdgeElevation` values. At the blend boundary, the constrained path diverged from the natural path.
- **Fix:** Moved `PropagateEdgeConstraintsForTJunctions` to run AFTER `FinalizeBankingAfterHarmonization` as Phase 3.6. Now `GetUnconstrainedEdgeElevation()` reads final edge values. At boundary: constrained ≈ natural → no divergence.
- **Result:** Blend zone boundary bump eliminated ✅

### 2. Synced hardcoded 30m transition distance with configured blend distance
**File:** `BankingOrchestrator.cs`
- **Problem:** `CalculateJunctionBankingBehavior` and `AdaptElevationsToHigherPriorityBanking` both used hardcoded 30.0f. User set blend distance to 100m → banking adaptation covered only 30m while edge constraints covered 100m → bump at 30m.
- **Fix:** Replaced all 4 hardcoded values with `GetMaxEffectiveBlendDistance(network)`.
- **Result:** Banking and edge constraint zones synchronized ✅

### 3. Master spline export uses effective center elevation
**File:** `MasterSplineExporter.cs`
- **Problem:** Export used `TargetElevation` but mesh uses constrained edge centers `(constrainedLeft + constrainedRight) / 2`. Spline diverged from road surface in junction blend zones.
- **Fix:** For constrained CSs, export now uses average of constrained edges.
- **Result:** Master splines follow actual road surface ✅

### 4. Junction CS recomputed in Phase 3.6
**File:** `NetworkJunctionHarmonizer.cs`
- **Problem:** Junction CS constraints were set in Phase 3 (before Phase 3.5). For equal-priority junctions where `SmoothSuppressedBankingTransitions` modifies the continuous road's TargetElevation, the junction CS constraint became stale.
- **Fix:** In Phase 3.6, the junction CS skip is removed (`skipTargetElevationModification` flag reused). Junction CS is recomputed at weight=1.0, overwriting stale value with post-Phase-3.5 surface.
- **Result:** Junction CS consistent with propagated CSs ✅

### 5. Phase 3.6 extended to all junction types
**File:** `NetworkJunctionHarmonizer.cs`
- **Problem:** Only T-junctions got constrained edges and Phase 3.6 propagation. Y-junctions, CrossRoads, Complex junctions had NO edge ramps → bumps at those junction types.
- **Fix:** Added `ApplyEdgeConstraintsForMultiWayJunction`. Widened Phase 3.6 filter to include all junction types. Added fallback for junctions with no continuous road (uses longest contributor as primary).
- **Result:** All junction types now get smooth edge ramps ✅

## What We Tried That Didn't Fix the Remaining Bump

### 1. Removing Phase 3.5 Step 3 (AdaptElevationsToHigherPriorityBanking)
- **Theory:** Three overlapping correction systems (Hermite rubberband + quintic ramp + blend function) create interference bumps near junctions.
- **Result:** No change. Bump persists. Phase 3.5 Step 3 was not the cause. Restored it.

### 2. Protection buffer set to 0
- **Theory:** `RoadEdgeProtectionBufferMeters` (default 2.0m) creates an expanded core zone in terrain blending. At the boundary, conflicting elevations from overlapping roads create a step.
- **Result:** No change. Bump persists. Protection buffer is not the cause.

### 3. Various stale constraint fixes
- Cleared and recomputed junction CS constraints → still bump
- Different weight curves → still bump
- Skip vs include junction CS → still bump (or 90° walls)

## Remaining Bump: Analysis

### Location
- On the terminating road, near the junction point
- Roughly at the distance where the terminating road exits the continuous road's body
- Visible in both mesh (DAE) and terrain

### What we know
- Not caused by Phase 3.5 Step 3 (removing it didn't help)
- Not caused by protection buffer (setting to 0 didn't help)
- Not caused by stale junction CS constraints (recomputing didn't help)
- Not caused by junction type mismatch (extending to all types didn't help for T-junctions)
- Appears at equal-priority AND unequal-priority junctions

### Possible remaining causes (unverified)

1. **Nearest primary CS jump**: `FindNearestPrimaryCrossSection` does nearest-neighbor lookup. As we walk along the terminating road, the nearest primary CS may "jump" from one CS to another. Different primary CSs have different elevation/slope/banking → small discontinuity in projected surface.

2. **Primary road's banking suppression reflection**: The primary road's banking transitions near the junction (SuppressBanking for equal-priority). Phase 3.6 projects the terminating road's edges onto this changing surface, inheriting the banking transition profile as a visible crease.

3. **Multiple overlapping systems with different curves**: Even with Phase 3.5 Step 3, the rubberband (Hermite), banking adjustment (cosine), and edge constraints (blend function) operate on the same CSs with different transition curves. The interference creates subtle bumps that no single system removal eliminates.

4. **Fundamental geometry**: At the junction, the terminating road must match the primary road's cross-slope. Away from the junction, it has its own natural cross-slope. The transition between these two cross-slopes is inherently a surface shape change that may always appear as a subtle bump/crease, regardless of how smooth the transition function is.

## Proposed Solution: Post-Junction Edge Smoothing Pass

### Concept
After all junction processing (Phase 3, 3.5, 3.6) has set the constrained edge elevations, run a **1D smoothing pass** along each terminating road's constrained edge profiles. This eliminates any remaining bumps regardless of their source — banking transitions, curve interference, projection artifacts, or nearest-CS jumps.

### Why this should work
- **Treats the root cause**: The bump is a high-frequency artifact in the constrained edge profile. Smoothing removes high frequencies while preserving the overall ramp shape.
- **Existing infrastructure**: `OptimizedElevationSmoother.ReSmoothFromExistingElevations()` already smooths TargetElevation from existing values. A similar approach can smooth constrained edge profiles.
- **Existing post-processing**: `PostProcessingSmoother` already runs after Phase 4 for terrain. A similar concept applies to edge profiles.
- **Preserves junction match**: Anchor the junction CS value (weight=1.0, must match primary surface exactly). Smooth everything between junction and blend boundary.

### Implementation approach

**Option A: Re-run elevation smoother on constrained edges (new Phase 3.7)**

After Phase 3.6 sets all constrained edges, add a Phase 3.7 that:
1. For each spline with constrained edges:
   - Extract the `ConstrainedLeftEdgeElevation` and `ConstrainedRightEdgeElevation` profiles as 1D arrays
   - Apply Butterworth or Box filter with a moderate window (e.g., 11-21 samples)
   - Pin the junction CS value (index 0, must not change)
   - Pin the blend boundary value (must match unconstrained edge for continuity)
   - Write smoothed values back to cross-sections

This is simple, targeted, and uses existing filter code from `OptimizedElevationSmoother`.

**Option B: Re-run full Phase 2 smoothing after Phase 3.6**

After Phase 3.6, call `ReSmoothFromExistingElevations()` for each spline. This re-smooths TargetElevation (which was modified by the rubberband + Phase 3.5). Then Phase 3.5 Step 4 recalculates edges from the re-smoothed TargetElevation.

Problem: This only smooths TargetElevation, not constrained edges directly. The bump is in the constrained edges.

**Option C: Hybrid — smooth TargetElevation AND re-derive constrained edges**

1. Re-smooth TargetElevation (Option B)
2. Re-run Phase 3.5 Step 4 (recalculate natural edges)
3. Re-run Phase 3.6 (re-derive constrained edges from smoothed natural edges)

This is the most thorough but also the most expensive. Essentially another iteration.

### Recommended: Option A

Option A is the simplest and most targeted. It directly smooths the constrained edge profiles where the bump lives. It doesn't require re-running the full pipeline. The smoothing window can be tuned independently (small window = only removes sharp bumps, large window = more aggressive smoothing).

### Key files
- `OptimizedElevationSmoother.cs` — has `BoxFilterPrefixSum()` and `ButterworthLowPassFilter()`
- `NetworkJunctionHarmonizer.cs` — Phase 3.6 `PropagateEdgeConstraintsPostBanking()` would call the new smoothing
- `UnifiedRoadSmoother.cs` — orchestration, Phase 3.7 call site
- `RoadSmoothingParameters.cs` — could add parameters for edge smoothing window/iterations

### Constraints to preserve
1. Junction CS constrained edges must NOT be modified (must match primary surface exactly)
2. At blend boundary, constrained edges must still match unconstrained edges (Phase 3.6 continuity fix)
3. Smoothing must not create new discontinuities at the boundary
4. The overall ramp shape (junction → boundary) must be preserved — only high-frequency bumps should be removed

## File Reference

### Modified in this session
- `NetworkJunctionHarmonizer.cs` — Phase 3.6, edge constraints for all junction types
- `BankingOrchestrator.cs` — synced transition distances, restored Phase 3.5 Step 3
- `MasterSplineExporter.cs` — effective center elevation export
- `UnifiedRoadSmoother.cs` — Phase 3.6 call site

### Key reference files
- `JunctionSurfaceCalculator.cs` — surface-following projection
- `BankedTerrainHelper.cs` — HasJunctionConstraint code path split
- `BankedElevationCalculator.cs` — edge elevation from banking
- `JunctionBankingAdapter.cs` — Phase 3.5 Step 3 elevation adaptation
- `PriorityAwareJunctionBankingCalculator.cs` — banking behavior assignment
- `CrossSectionConverter.cs` — mesh export reads constrained edges
- `OptimizedElevationSmoother.cs` — existing smoothing filters (reusable for Option A)
- `PostProcessingSmoother.cs` — existing post-processing pattern (reference)
