# Junction Slope Adaptation - Implementation Plan

## Overview

This document provides a comprehensive implementation plan for fixing junction elevation smoothing issues related to the `FlipMaterialProcessingOrder` parameter. The goal is to achieve smooth junction connections **for ALL junction types** regardless of material processing order while preserving road surface integrity.

**Affected Junction Types:**
- **T-Junctions**: Endpoint meets middle of another road (primary focus)
- **Y-Junctions**: Two roads merge/split at endpoints  
- **X-Junctions/CrossRoads**: 3-4 roads meet at endpoints
- **Complex Junctions**: 5+ roads meeting
- **Mid-Spline Crossings**: Two roads cross without terminating

**Related Files:**
- `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`
- `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ElevationMapBuilder.cs`
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs`
- `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/UnifiedCrossSection.cs`

---

## Problem Analysis

### Current Behavior

#### When `FlipMaterialProcessingOrder = false`
- Lower-index materials get **lower priority**
- The main road (higher index) correctly wins in protection zones
- **Problem**: The secondary road's blend zone doesn't adapt to the main road's **longitudinal slope**
- The propagation uses a flat `junction.HarmonizedElevation` instead of a slope-following profile
- Result: "Step" artifacts at T-junctions when either road has steep slopes

#### When `FlipMaterialProcessingOrder = true`
- The secondary road gets **higher priority**
- Its protection zone overwrites the main road's protection pixels at the junction
- **Problem**: This effectively disables protection at junctions
- Result: Smoother junctions but **compromised main road surface integrity**

### Root Cause

The junction propagation algorithm in `PropagateJunctionConstraints()` blends from a **single elevation value** (`junction.HarmonizedElevation`) back to the original elevation:

```csharp
// Current implementation (line ~940)
newElevation = weightedJunctionElevation * totalInfluence + originalElevation * (1.0f - totalInfluence);
```

This should instead follow the main road's slope gradient as the secondary road moves away from the junction point.

---

## Part A: Fix for `FlipMaterialProcessingOrder = false`

### Goal
Make the secondary (terminating) road adapt to the main (continuous) road's slope within the junction blend zone, while respecting protection zones.

### Step A1: Extend NetworkJunction to Store Slope Information

**File:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs`

**Changes:**
1. Add new properties to `NetworkJunction` class:

```csharp
/// <summary>
/// For T-junctions: The longitudinal slope (rise/run) of the primary continuous road.
/// Positive = uphill in the direction of increasing LocalIndex.
/// </summary>
public float PrimaryRoadSlope { get; set; } = 0f;

/// <summary>
/// For T-junctions: The tangent direction of the primary road at the junction point.
/// Used to calculate slope-adjusted elevations along the secondary road.
/// </summary>
public Vector2 PrimaryRoadTangent { get; set; } = Vector2.Zero;

/// <summary>
/// For T-junctions: The spline ID of the primary (continuous) road.
/// </summary>
public int? PrimarySplineId { get; set; }
```

**Validation:**
- Verify `NetworkJunction` compiles without errors
- Run existing unit tests (if any)

---

### Step A2: Store Slope Data During T-Junction Elevation Computation

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Method:** `ComputeTJunctionElevation()`

**Changes:**
1. After calculating `primarySlope` (around line 352), store it in the junction:

```csharp
// After: primarySlope = CalculatePrimaryRoadSlope(primaryContinuous);
// Add:
junction.PrimaryRoadSlope = float.IsNaN(primarySlope) ? 0f : primarySlope;
junction.PrimaryRoadTangent = primaryCS.TangentDirection;
junction.PrimarySplineId = primaryContinuous.Spline.SplineId;
```

**Validation:**
- Add logging to confirm slope values are stored
- Test with a known sloped T-junction scenario
- Expected: `PrimaryRoadSlope` should be non-zero for sloped roads

---

### Step A3: Create Slope-Aware Propagation Method

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**New Method:** `CalculateSlopeAdjustedJunctionElevation()`

```csharp
/// <summary>
/// Calculates the slope-adjusted junction elevation for a cross-section on the secondary road.
/// As the secondary road moves away from the junction, the target elevation follows
/// the primary road's slope gradient to create a smooth transition.
/// </summary>
/// <param name="junction">The T-junction with stored slope information.</param>
/// <param name="secondaryCsPosition">Position of the cross-section on the secondary road.</param>
/// <param name="junctionPosition">The junction center position.</param>
/// <returns>Slope-adjusted target elevation.</returns>
private float CalculateSlopeAdjustedJunctionElevation(
    NetworkJunction junction,
    Vector2 secondaryCsPosition,
    Vector2 junctionPosition)
{
    if (junction.Type != JunctionType.TJunction || junction.PrimarySplineId == null)
    {
        // Not a T-junction or no slope data - use flat elevation
        return junction.HarmonizedElevation;
    }
    
    // Calculate how far the secondary cross-section is from the junction
    // projected onto the primary road's tangent direction
    var toSecondaryCs = secondaryCsPosition - junctionPosition;
    var longitudinalOffset = Vector2.Dot(toSecondaryCs, junction.PrimaryRoadTangent);
    
    // Apply slope adjustment: elevation changes along the primary road's gradient
    var slopeAdjustment = longitudinalOffset * junction.PrimaryRoadSlope;
    
    return junction.HarmonizedElevation + slopeAdjustment;
}
```

**Validation:**
- Unit test with mock junction data
- Verify positive slope gives higher elevation when moving in tangent direction
- Verify negative slope gives lower elevation

---

### Step A4: Modify Propagation to Use Slope-Adjusted Elevations

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Method:** `PropagateJunctionConstraints()` (around lines 829-900)

**Changes:**
1. In the loop that collects junction influences, modify to use slope-adjusted elevation:

```csharp
// Replace (around line 897):
// influences.Add((junction.HarmonizedElevation, weight, junction.JunctionId));

// With:
var slopeAdjustedElevation = CalculateSlopeAdjustedJunctionElevation(
    junction,
    cs.CenterPoint,
    junction.Position);
influences.Add((slopeAdjustedElevation, weight, junction.JunctionId));
```

2. Also update the bidirectional collection for mid-spline crossings (`CollectBidirectionalInfluences`) if applicable.

**Validation:**
- Test with a steep T-junction (main road at 5-10% grade)
- Visually inspect the terrain: secondary road should smoothly follow main road's slope
- Check debug images for elevation continuity

---

### Step A5: Add Slope Gradient Limit

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Purpose:** Prevent the slope adjustment from creating unrealistic elevation changes when the secondary road extends far from the junction.

**Changes:**
1. Add a parameter to `JunctionHarmonizationParameters`:

```csharp
// In JunctionHarmonizationParameters.cs
/// <summary>
/// Maximum distance (meters) over which the primary road's slope affects the secondary road.
/// Beyond this distance, the slope adjustment is clamped.
/// Default: Same as JunctionBlendDistanceMeters.
/// </summary>
public float SlopeAdaptationMaxDistanceMeters { get; set; } = 30.0f;
```

2. Modify `CalculateSlopeAdjustedJunctionElevation()`:

```csharp
// Clamp the longitudinal offset to prevent extreme adjustments
var maxOffset = slopeAdaptationMaxDistance;
var clampedOffset = Math.Clamp(longitudinalOffset, -maxOffset, maxOffset);
var slopeAdjustment = clampedOffset * junction.PrimaryRoadSlope;
```

**Validation:**
- Test with very long secondary roads
- Verify elevation doesn't become unrealistic far from junction

---

### Step A6: Handle Protection Zone Interactions

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs`

**Method:** `ApplyProtectedBlending()` (around lines 126-161)

**Problem:** When a pixel is in the secondary road's blend zone AND inside the main road's protection zone, the current code uses `targetElevation` which comes from the nearest cross-section - not accounting for the slope-adjusted junction elevation.

**Changes:**
1. Add slope-aware elevation lookup for protected pixels at junctions:

```csharp
// Around line 132-136, after checking protectionMask[y, x]:
if (protectionMask[y, x])
{
    // Check if this pixel is near a T-junction where we need slope adaptation
    var slopeAdjustedElevation = GetSlopeAdjustedElevationForProtectedPixel(
        worldPos, ownerId, network, metersPerPixel);
    
    if (slopeAdjustedElevation.HasValue)
    {
        newH = slopeAdjustedElevation.Value;
    }
    else
    {
        newH = targetElevation;
    }
    localProtected++;
}
```

2. Implement helper method:

```csharp
/// <summary>
/// For pixels in protection zones near T-junctions, calculates the slope-adjusted
/// elevation that accounts for the primary road's gradient.
/// </summary>
private float? GetSlopeAdjustedElevationForProtectedPixel(
    Vector2 worldPos,
    int ownerSplineId,
    UnifiedRoadNetwork network,
    float metersPerPixel)
{
    // Find if this position is near any T-junction involving this spline
    foreach (var junction in network.Junctions.Where(j => 
        j.Type == JunctionType.TJunction && 
        j.PrimarySplineId.HasValue))
    {
        var distToJunction = Vector2.Distance(worldPos, junction.Position);
        
        // Only apply within blend distance
        if (distToJunction > junction.BlendDistance)
            continue;
        
        // Check if this pixel belongs to a terminating road at this junction
        var isTerminatingRoad = junction.GetTerminatingRoads()
            .Any(t => t.Spline.SplineId == ownerSplineId);
        
        if (isTerminatingRoad)
        {
            // Calculate slope-adjusted elevation
            var toPixel = worldPos - junction.Position;
            var longitudinalOffset = Vector2.Dot(toPixel, junction.PrimaryRoadTangent);
            var slopeAdjustment = longitudinalOffset * junction.PrimaryRoadSlope;
            
            return junction.HarmonizedElevation + slopeAdjustment;
        }
    }
    
    return null; // No T-junction slope adjustment needed
}
```

**Validation:**
- Test protection zone behavior at sloped T-junctions
- Verify no "cliff" artifacts at protection zone boundaries

---

## Part B: Fix for `FlipMaterialProcessingOrder = true`

### Goal
When using flipped order (secondary road gets higher priority), ensure protection zones are still respected for the main road while allowing smooth junction connections.

### Step B1: Add Junction-Aware Protection Zone Override

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/Blending/RoadMaskBuilder.cs`

**Method:** `FillConvexPolygonWithOwnershipAndBanking()` (around lines 182-259)

**Problem:** Currently, higher priority always wins. We need an exception for junction areas where the main road should retain some protection.

**Changes:**
1. Add junction proximity check before priority override:

```csharp
// Around line 247-254, modify the priority check:
else if (priority > priorityMap[y, x])
{
    // Check if this is a junction area where we should preserve lower-priority road
    // Only override if NOT in a junction blend zone of a higher-priority continuous road
    var shouldOverride = ShouldOverrideProtectionAtJunction(
        new Vector2(x * metersPerPixel, y * metersPerPixel),
        splineId,
        priority,
        network);
    
    if (shouldOverride)
    {
        ownershipMap[y, x] = splineId;
        elevationMap[y, x] = pixelElevation;
        priorityMap[y, x] = priority;
        overwrittenCount++;
    }
}
```

2. Implement helper method (may need to pass network reference):

```csharp
/// <summary>
/// Determines if a higher-priority road should override a lower-priority road's
/// protection at this location. Returns false if this is a T-junction where the
/// continuous (lower-priority with FlipMaterialProcessingOrder=true) road should
/// retain protection.
/// </summary>
private bool ShouldOverrideProtectionAtJunction(
    Vector2 worldPos,
    int candidateSplineId,
    int candidatePriority,
    UnifiedRoadNetwork network)
{
    // Find T-junctions where candidateSpline is the terminating road
    foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.TJunction))
    {
        // Check if candidateSpline is terminating at this junction
        var isTerminating = junction.GetTerminatingRoads()
            .Any(t => t.Spline.SplineId == candidateSplineId);
        
        if (!isTerminating)
            continue;
        
        // Check if we're within the junction's protection radius
        var distToJunction = Vector2.Distance(worldPos, junction.Position);
        var junctionProtectionRadius = junction.GetMaxRoadWidth() * 0.75f; // 75% of max road width
        
        if (distToJunction <= junctionProtectionRadius)
        {
            // Don't override - preserve the continuous road's protection
            return false;
        }
    }
    
    return true; // Safe to override
}
```

**Validation:**
- Test with `FlipMaterialProcessingOrder = true`
- Verify main road surface isn't corrupted at T-junctions
- Verify secondary road still connects smoothly

---

### Step B2: Add Blend Zone Merging at Junctions

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/Blending/ProtectedBlendingProcessor.cs`

**Purpose:** When `FlipMaterialProcessingOrder = true`, blend zones from both roads should merge smoothly at junctions instead of one dominating the other.

**Changes:**
1. Add junction blend zone merging logic:

```csharp
// In ApplyProtectedBlending(), around line 139-160
// After checking for higher priority protected elevation:

// NEW: Check for junction blend zone merging
if (IsInJunctionMergeZone(worldPos, network, out var mergeInfo))
{
    // Blend between both roads' target elevations based on junction merge rules
    newH = CalculateMergedJunctionElevation(
        worldPos,
        targetElevation,      // Secondary road's elevation (higher priority)
        mergeInfo.PrimaryElevation,  // Primary road's elevation
        mergeInfo.MergeWeight);
}
```

2. Implement merge zone detection:

```csharp
private record JunctionMergeInfo(float PrimaryElevation, float MergeWeight);

/// <summary>
/// Checks if a position is in a junction merge zone where both roads'
/// elevations should be blended together.
/// </summary>
private bool IsInJunctionMergeZone(
    Vector2 worldPos,
    UnifiedRoadNetwork network,
    out JunctionMergeInfo? mergeInfo)
{
    mergeInfo = null;
    
    foreach (var junction in network.Junctions.Where(j => j.Type == JunctionType.TJunction))
    {
        var distToJunction = Vector2.Distance(worldPos, junction.Position);
        var mergeZoneRadius = junction.GetMaxRoadWidth() * 1.5f;
        
        if (distToJunction > mergeZoneRadius)
            continue;
        
        // Calculate merge weight: 1.0 at junction center, 0.0 at edge
        var t = distToJunction / mergeZoneRadius;
        var mergeWeight = 1.0f - t;
        
        // Get primary road's surface elevation at this point
        var primaryElevation = GetPrimaryRoadSurfaceElevation(junction, worldPos);
        
        mergeInfo = new JunctionMergeInfo(primaryElevation, mergeWeight);
        return true;
    }
    
    return false;
}
```

**Validation:**
- Test junction blending with both material orders
- Verify smooth transitions in both directions

---

### Step B3: Add Per-Junction Priority Override Parameter

**File:** `BeamNgTerrainPoc/Terrain/Models/JunctionHarmonizationParameters.cs`

**Purpose:** Allow users to control junction priority behavior independently of material order.

**Changes:**
1. Add new parameter:

```csharp
/// <summary>
/// Controls how road priorities are resolved at T-junctions.
/// 
/// - ContinuousRoadWins (default): The continuous (through) road always wins,
///   regardless of material priority. This produces the smoothest junctions.
/// 
/// - MaterialPriorityWins: Standard priority rules apply. Higher priority roads
///   override lower priority roads at junctions.
/// 
/// - Blend: Both roads' elevations are blended at the junction merge zone.
/// </summary>
public JunctionPriorityMode JunctionPriorityMode { get; set; } = JunctionPriorityMode.ContinuousRoadWins;
```

2. Add enum:

```csharp
/// <summary>
/// Modes for resolving priority conflicts at T-junctions.
/// </summary>
public enum JunctionPriorityMode
{
    /// <summary>
    /// The continuous (through) road always wins at T-junctions,
    /// regardless of material processing order.
    /// </summary>
    ContinuousRoadWins,
    
    /// <summary>
    /// Standard priority rules apply - higher priority roads override lower.
    /// </summary>
    MaterialPriorityWins,
    
    /// <summary>
    /// Both roads' elevations are blended in the junction merge zone.
    /// </summary>
    Blend
}
```

**Validation:**
- Test all three modes with both `FlipMaterialProcessingOrder` settings
- Document expected behavior for each combination

---

### Step B4: Update UI to Expose New Parameter

**File:** `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

**Changes:**
1. Add UI control for `JunctionPriorityMode`:

```razor
<MudSelect T="JunctionPriorityMode" 
           Label="Junction Priority Mode"
           @bind-Value="@JunctionParams.JunctionPriorityMode"
           HelperText="How to resolve priority conflicts at T-junctions">
    <MudSelectItem Value="JunctionPriorityMode.ContinuousRoadWins">
        Continuous Road Wins (Recommended)
    </MudSelectItem>
    <MudSelectItem Value="JunctionPriorityMode.MaterialPriorityWins">
        Material Priority Wins
    </MudSelectItem>
    <MudSelectItem Value="JunctionPriorityMode.Blend">
        Blend Both Roads
    </MudSelectItem>
</MudSelect>
```

**Validation:**
- Verify UI binding works correctly
- Test preset save/load includes new parameter

---

## Part C: Testing Plan

### Test Scenarios

#### Scenario 1: Flat T-Junction
- Main road: 0% grade
- Secondary road: 0% grade
- Expected: Smooth connection at junction elevation

#### Scenario 2: Sloped Main Road, Flat Secondary
- Main road: 8% grade (uphill)
- Secondary road: 0% grade
- Expected: Secondary road follows main road's slope at junction

#### Scenario 3: Flat Main Road, Sloped Secondary
- Main road: 0% grade
- Secondary road: 5% grade (coming from below)
- Expected: Secondary road elevation adjusted to meet main road

#### Scenario 4: Both Roads Sloped
- Main road: 6% grade (uphill)
- Secondary road: 4% grade (coming from side)
- Expected: Smooth merge with main road slope dominating at junction

#### Scenario 5: Steep Grades
- Main road: 12% grade
- Secondary road: 10% grade
- Expected: No "cliff" artifacts, smooth transition

### Test Matrix

| Scenario | FlipOrder=false | FlipOrder=true | ContinuousWins | MaterialWins | Blend |
|----------|-----------------|----------------|----------------|--------------|-------|
| 1        | ?               | ?              | ?              | ?            | ?     |
| 2        | ?               | ?              | ?              | ?            | ?     |
| 3        | ?               | ?              | ?              | ?            | ?     |
| 4        | ?               | ?              | ?              | ?            | ?     |
| 5        | ?               | ?              | ?              | ?            | ?     |

### Validation Criteria
1. No elevation discontinuities visible in heightmap
2. No "cliff" artifacts at junction boundaries
3. Main road surface integrity preserved
4. Secondary road smoothly follows terrain away from junction
5. Debug images show expected elevation transitions

---

## Part D: Documentation Updates

### Step D1: Update ROAD_ELEVATION_SMOOTHING_DOCUMENTATION.md

Add new section after 5.6 (Junction Surface Constraints):

```markdown
### 5.7 Slope-Aware T-Junction Propagation

For T-junctions on sloped terrain, the secondary road must adapt not only to the
junction elevation but also to the primary road's longitudinal slope.

#### Slope-Adjusted Elevation Calculation
```csharp
// As secondary road moves away from junction, target elevation follows main road slope
slopeAdjustedElevation = junctionElevation + (longitudinalOffset * primarySlope)
```

#### Parameters
- `SlopeAdaptationMaxDistanceMeters`: Maximum distance for slope influence (default: 30m)
- `JunctionPriorityMode`: Controls priority resolution at T-junctions
```

### Step D2: Update Parameter Reference Table

Add new parameters to the Junction/Endpoint Parameters table.

---

## Implementation Order

1. **Phase 1 (Core Fix):** Steps A1-A4
   - Estimated time: 2-3 hours
   - Risk: Low (additive changes)

2. **Phase 2 (Slope Limits):** Step A5
   - Estimated time: 30 minutes
   - Risk: Low

3. **Phase 3 (Protection Integration):** Step A6
   - Estimated time: 1-2 hours
   - Risk: Medium (touches blending logic)

4. **Phase 4 (Flipped Order Fix):** Steps B1-B2
   - Estimated time: 2-3 hours
   - Risk: Medium

5. **Phase 5 (User Control):** Steps B3-B4
   - Estimated time: 1 hour
   - Risk: Low

6. **Phase 6 (Testing):** Part C
   - Estimated time: 2-3 hours
   - Dependency: All previous phases

7. **Phase 7 (Documentation):** Part D
   - Estimated time: 1 hour
   - Dependency: All previous phases

**Total Estimated Time:** 10-14 hours

---

## Rollback Plan

If issues are discovered:
1. The `JunctionPriorityMode` can be set to `MaterialPriorityWins` to disable new behavior
2. `SlopeAdaptationMaxDistanceMeters` can be set to 0 to disable slope propagation
3. All changes are additive - existing functionality remains available

---

## Success Criteria

1. T-junctions connect smoothly with `FlipMaterialProcessingOrder = false`
2. T-junctions connect smoothly with `FlipMaterialProcessingOrder = true`
3. Main road surface integrity is preserved in both cases
4. Performance impact is negligible (< 5% increase in processing time)
5. All existing tests pass
6. New test scenarios pass

---

## Part E: Other Junction Types - Analysis and Implementation

### Why Other Junction Types Need Attention

The core problem identified for T-junctions also affects other junction types:

**Current Issue:** All junction types use `PropagateJunctionConstraints()` which blends from a **single flat elevation** (`junction.HarmonizedElevation`) back to original elevations. When roads at a junction are sloped, this creates elevation discontinuities.

### E1: Y-Junction Analysis

**Geometry:** Two roads meet at their endpoints (both terminate).

**Current Behavior:**
```csharp
// In ComputeMultiWayJunctionElevation() / ComputeEqualPriorityJunctionElevation()
// Uses weighted average or longer-road-wins heuristic
junction.HarmonizedElevation = weighted average of both endpoints
```

**Problem:** 
- Both roads propagate from a single flat elevation
- If Road A approaches uphill and Road B approaches downhill, the junction elevation is averaged
- Both roads then blend from this flat point, creating "kinks" where slope changes abruptly

**Solution Strategy:**
1. Store approach slopes for BOTH contributing roads
2. Each road's propagation follows its OWN slope direction away from junction
3. The junction point itself uses the harmonized elevation

---

### Step E1.1: Extend NetworkJunction for Multi-Road Slopes

**File:** `BeamNgTerrainPoc/Terrain/Models/RoadGeometry/NetworkJunction.cs`

**Add:**
```csharp
/// <summary>
/// For Y/X/Complex junctions: Maps each contributor's SplineId to its local slope
/// at the junction point. Used for slope-aware propagation.
/// </summary>
public Dictionary<int, ContributorSlopeInfo> ContributorSlopes { get; } = new();

/// <summary>
/// Slope information for a junction contributor.
/// </summary>
public record ContributorSlopeInfo(
    float Slope,              // Rise/run at junction point
    Vector2 TangentDirection, // Direction the road approaches from
    bool IsIncoming           // True if road approaches junction (slope toward junction)
);
```

---

### Step E1.2: Compute Slopes for All Contributors

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**New Method:** `ComputeContributorSlopes()`

```csharp
/// <summary>
/// Computes and stores the local slope for each contributor at a junction.
/// This enables slope-aware propagation for Y, X, and Complex junctions.
/// </summary>
private void ComputeContributorSlopes(NetworkJunction junction)
{
    junction.ContributorSlopes.Clear();
    
    foreach (var contributor in junction.Contributors)
    {
        if (_crossSectionsBySpline == null ||
            !_crossSectionsBySpline.TryGetValue(contributor.Spline.SplineId, out var splineSections))
            continue;
        
        var cs = contributor.CrossSection;
        var index = splineSections.FindIndex(s => s.Index == cs.Index);
        
        if (index < 0)
            continue;
        
        // Calculate local slope using neighbors
        var prevIdx = Math.Max(0, index - 3);
        var nextIdx = Math.Min(splineSections.Count - 1, index + 3);
        
        if (prevIdx == nextIdx)
        {
            junction.ContributorSlopes[contributor.Spline.SplineId] = 
                new ContributorSlopeInfo(0f, cs.TangentDirection, contributor.IsSplineEnd);
            continue;
        }
        
        var cs1 = splineSections[prevIdx];
        var cs2 = splineSections[nextIdx];
        var distance = Vector2.Distance(cs1.CenterPoint, cs2.CenterPoint);
        
        if (distance < 0.1f)
        {
            junction.ContributorSlopes[contributor.Spline.SplineId] = 
                new ContributorSlopeInfo(0f, cs.TangentDirection, contributor.IsSplineEnd);
            continue;
        }
        
        var elevDiff = cs2.TargetElevation - cs1.TargetElevation;
        var slope = elevDiff / distance;
        
        // Determine if this is an "incoming" road (slope direction matters)
        var isIncoming = contributor.IsSplineEnd; // End of spline = approaching junction
        
        junction.ContributorSlopes[contributor.Spline.SplineId] = 
            new ContributorSlopeInfo(slope, cs.TangentDirection, isIncoming);
    }
}
```

**Call Location:** Add to `ComputeJunctionElevations()` for Y, X, and Complex junction types.

---

### Step E1.3: Slope-Aware Propagation for Y/X/Complex Junctions

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Modify:** `PropagateJunctionConstraints()` around lines 870-900

**Current Code:**
```csharp
influences.Add((junction.HarmonizedElevation, weight, junction.JunctionId));
```

**New Code:**
```csharp
// Calculate slope-adjusted elevation based on junction type
var adjustedElevation = CalculateJunctionTypeAwareElevation(
    junction,
    contributor.Spline.SplineId,
    cs.CenterPoint,
    distances[i]);

influences.Add((adjustedElevation, weight, junction.JunctionId));
```

**New Method:**
```csharp
/// <summary>
/// Calculates slope-adjusted elevation for any junction type.
/// For T-junctions: Uses primary road slope
/// For Y/X/Complex: Uses the contributor's own slope
/// </summary>
private float CalculateJunctionTypeAwareElevation(
    NetworkJunction junction,
    int splineId,
    Vector2 csPosition,
    float distanceFromEndpoint)
{
    // T-junctions use primary road slope (existing logic)
    if (junction.Type == JunctionType.TJunction && junction.PrimarySplineId.HasValue)
    {
        return CalculateSlopeAdjustedJunctionElevation(junction, csPosition, junction.Position);
    }
    
    // Y/X/Complex junctions: each road follows its own slope
    if (junction.ContributorSlopes.TryGetValue(splineId, out var slopeInfo))
    {
        // Calculate how the road's elevation changes as we move away from junction
        // Use the road's own slope direction
        var toCs = csPosition - junction.Position;
        var alongRoadOffset = Vector2.Dot(toCs, slopeInfo.TangentDirection);
        
        // If road is incoming (approaching junction), reverse slope direction
        // because we're propagating AWAY from junction
        var effectiveSlope = slopeInfo.IsIncoming ? -slopeInfo.Slope : slopeInfo.Slope;
        
        // Clamp offset to prevent extreme values
        var maxOffset = 30.0f; // Could be parameterized
        var clampedOffset = Math.Clamp(alongRoadOffset, -maxOffset, maxOffset);
        
        return junction.HarmonizedElevation + clampedOffset * effectiveSlope;
    }
    
    // Fallback to flat elevation
    return junction.HarmonizedElevation;
}
```

---

### E2: Mid-Spline Crossing Analysis

**Geometry:** Two roads cross without either terminating (both pass through).

**Current Behavior:**
```csharp
// In ComputeMidSplineCrossingElevation()
// Uses priority-weighted average, then propagates bidirectionally
```

**Problem:**
- Both roads must blend toward a single crossing elevation
- If roads cross at different heights (one on embankment, one in cut), the averaged elevation creates bumps
- The bidirectional propagation uses flat elevation in both directions

**Solution Strategy:**
1. Store both roads' slopes at the crossing point
2. The harmonized elevation is the weighted average (existing)
3. Each road's propagation follows ITS OWN slope in BOTH directions from crossing
4. This creates a smooth "saddle" at the crossing rather than a flat spot

---

### Step E2.1: Bidirectional Slope-Aware Collection

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Method:** `CollectBidirectionalInfluences()`

**Modify to use slope information:**

```csharp
private void CollectBidirectionalInfluences(
    List<UnifiedCrossSection> splineSections,
    UnifiedCrossSection crossingCs,
    NetworkJunction junction,
    float blendDistance,
    JunctionBlendFunctionType blendFunctionType,
    Dictionary<int, List<(float elevation, float weight, int junctionId)>> crossSectionInfluences)
{
    var splineId = crossingCs.OwnerSplineId;
    
    // Get this road's slope at the crossing
    junction.ContributorSlopes.TryGetValue(splineId, out var slopeInfo);
    var slope = slopeInfo?.Slope ?? 0f;
    var tangent = slopeInfo?.TangentDirection ?? crossingCs.TangentDirection;
    
    // Find the crossing index
    var crossingIndex = splineSections.FindIndex(cs => cs.Index == crossingCs.Index);
    if (crossingIndex < 0) return;
    
    // Process BEFORE crossing (negative direction along road)
    ProcessDirectionalInfluences(
        splineSections, crossingIndex, junction, blendDistance, blendFunctionType,
        crossSectionInfluences, slope, tangent, isForward: false);
    
    // Process AFTER crossing (positive direction along road)
    ProcessDirectionalInfluences(
        splineSections, crossingIndex, junction, blendDistance, blendFunctionType,
        crossSectionInfluences, slope, tangent, isForward: true);
}

private void ProcessDirectionalInfluences(
    List<UnifiedCrossSection> splineSections,
    int startIndex,
    NetworkJunction junction,
    float blendDistance,
    JunctionBlendFunctionType blendFunctionType,
    Dictionary<int, List<(float elevation, float weight, int junctionId)>> crossSectionInfluences,
    float slope,
    Vector2 tangent,
    bool isForward)
{
    var step = isForward ? 1 : -1;
    var cumulativeDistance = 0f;
    var prevCs = splineSections[startIndex];
    
    for (var i = startIndex + step; i >= 0 && i < splineSections.Count; i += step)
    {
        var cs = splineSections[i];
        cumulativeDistance += Vector2.Distance(prevCs.CenterPoint, cs.CenterPoint);
        
        if (cumulativeDistance > blendDistance)
            break;
        
        var t = cumulativeDistance / blendDistance;
        var blend = ApplyBlendFunction(t, blendFunctionType);
        var weight = 1.0f - blend;
        
        if (weight > 0.001f)
        {
            // Calculate slope-adjusted elevation
            // Direction from junction determines sign
            var directionMultiplier = isForward ? 1f : -1f;
            var slopeAdjustment = cumulativeDistance * slope * directionMultiplier;
            var adjustedElevation = junction.HarmonizedElevation + slopeAdjustment;
            
            if (!crossSectionInfluences.TryGetValue(cs.Index, out var influences))
            {
                influences = new List<(float, float, int)>();
                crossSectionInfluences[cs.Index] = influences;
            }
            influences.Add((adjustedElevation, weight, junction.JunctionId));
        }
        
        prevCs = cs;
    }
}
```

---

### E3: Complex Junction Handling

**Geometry:** 5+ roads meeting (rare, but possible at major intersections or roundabout-like structures).

**Current Behavior:** Same as Y/X junctions - weighted average.

**Solution:** Same as Y/X junctions - each road follows its own slope. The `ComputeContributorSlopes()` method handles any number of contributors.

**No additional implementation needed** if E1.1-E1.3 are implemented correctly.

---

### E4: Isolated Endpoints (Dead Ends)

**Geometry:** Single road ending with no connection.

**Current Behavior:** 
```csharp
// In ApplyEndpointTapering()
// Blends from road elevation toward terrain elevation
```

**Problem:** If the road is sloped, the tapering creates an abrupt slope change at the taper start.

**Solution Strategy:**
1. Calculate the road's slope at the endpoint
2. Project the slope continuation into the taper zone
3. Blend from this slope-projected elevation toward terrain

---

### Step E4.1: Slope-Aware Endpoint Tapering

**File:** `BeamNgTerrainPoc/Terrain/Algorithms/NetworkJunctionHarmonizer.cs`

**Method:** `ApplyEndpointTapering()` (around lines 542-620)

**Modify the elevation calculation:**

```csharp
// Current code (around line 610):
// newElevation = targetAtEndpoint * (1 - blend) + originalElevation * blend

// Replace with slope-aware version:
// First, calculate the road's slope at the endpoint
var roadSlope = CalculateEndpointSlope(pathSections, isStartOfPath);

// Calculate what the road elevation WOULD be if it continued at the same slope
var projectedRoadElevation = targetAtEndpoint + (dist * roadSlope);

// Now blend between this projected elevation and terrain
// This creates a gradual slope change instead of an abrupt one
newElevation = projectedRoadElevation * (1 - blend) + originalElevation * blend;
```

**New Helper Method:**
```csharp
private float CalculateEndpointSlope(List<UnifiedCrossSection> pathSections, bool isStartOfPath)
{
    if (pathSections.Count < 3)
        return 0f;
    
    // Get the endpoint and its neighbors
    int endpointIdx = isStartOfPath ? 0 : pathSections.Count - 1;
    int neighborIdx = isStartOfPath ? Math.Min(3, pathSections.Count - 1) : Math.Max(0, pathSections.Count - 4);
    
    var endpointCs = pathSections[endpointIdx];
    var neighborCs = pathSections[neighborIdx];
    
    var distance = Vector2.Distance(endpointCs.CenterPoint, neighborCs.CenterPoint);
    if (distance < 0.1f)
        return 0f;
    
    var elevDiff = neighborCs.TargetElevation - endpointCs.TargetElevation;
    
    // Slope direction: positive = going uphill away from endpoint
    return isStartOfPath ? -elevDiff / distance : elevDiff / distance;
}
```

---

## Part F: Updated Testing Plan for All Junction Types

### Additional Test Scenarios

#### Scenario 6: Y-Junction with Opposite Slopes
- Road A: 6% grade downhill to junction
- Road B: 4% grade downhill to junction  
- Expected: Both roads smoothly merge, no kink at junction

#### Scenario 7: Y-Junction with Same-Direction Slopes
- Road A: 5% grade uphill to junction
- Road B: 3% grade uphill to junction
- Expected: Average slope at junction, smooth blend

#### Scenario 8: X-Junction (4-Way) with Mixed Slopes
- Road A-B (through): 4% grade
- Road C-D (crossing): 0% grade (flat)
- Expected: Each road follows its own slope through junction

#### Scenario 9: Mid-Spline Crossing with Elevation Difference
- Road A: At elevation 100m, 3% grade
- Road B: At elevation 102m, 0% grade
- Junction elevation: ~101m (weighted average)
- Expected: Both roads smoothly transition to/from junction

#### Scenario 10: Dead End on Slope
- Road: 8% grade ending in dead end
- Expected: Taper follows slope continuation, then blends to terrain

### Updated Test Matrix

| Scenario | Junction Type | FlipOrder=false | FlipOrder=true | Slope-Aware |
|----------|---------------|-----------------|----------------|-------------|
| 1-5      | T-Junction    | ?               | ?              | ?           |
| 6        | Y-Junction    | ?               | ?              | ?           |
| 7        | Y-Junction    | ?               | ?              | ?           |
| 8        | X-Junction    | ?               | ?              | ?           |
| 9        | MidSpline     | ?               | ?              | ?           |
| 10       | Endpoint      | ?               | ?              | ?           |

---

## Updated Implementation Order

1. **Phase 1 (T-Junction Core):** Steps A1-A4 (T-junctions only)
   - Estimated time: 2-3 hours

2. **Phase 2 (T-Junction Limits):** Step A5
   - Estimated time: 30 minutes

3. **Phase 3 (T-Junction Protection):** Step A6
   - Estimated time: 1-2 hours

4. **Phase 4 (Flipped Order):** Steps B1-B4
   - Estimated time: 3-4 hours

5. **Phase 5 (Y/X/Complex Junctions):** Steps E1.1-E1.3
   - Estimated time: 2-3 hours
   - Risk: Medium

6. **Phase 6 (Mid-Spline Crossings):** Step E2.1
   - Estimated time: 1-2 hours
   - Risk: Medium

7. **Phase 7 (Endpoints):** Step E4.1
   - Estimated time: 1 hour
   - Risk: Low

8. **Phase 8 (Testing):** Part F
   - Estimated time: 3-4 hours

9. **Phase 9 (Documentation):** Part D + updates
   - Estimated time: 1-2 hours

**Updated Total Estimated Time:** 16-22 hours

---

## Updated Success Criteria

1. T-junctions connect smoothly with both `FlipMaterialProcessingOrder` settings
2. Y-junctions merge smoothly with no kinks at junction point
3. X-junctions allow each road to follow its own slope
4. Mid-spline crossings create smooth saddle points
5. Endpoint tapers follow slope continuation before blending to terrain
6. Main road surface integrity preserved in all cases
7. Performance impact negligible (< 5% increase)
8. All existing tests pass
9. All new test scenarios pass
