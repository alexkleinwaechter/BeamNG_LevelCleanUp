# Banking Smoothing & Junction Blending Improvements Plan (2026-03-09)

## Context

After integrating banking into the unified smoothing pipeline (removing the old `JunctionBankingAdapter` Phase 3.5 in commit `98e8b8e`), two regressions appeared:

1. **Bumpy banked curves**: Strongly banked curves (~10deg) feel bumpier than the old post-processing approach. Small curvature fluctuations from OSM node spacing create visible edge-elevation bumps.
2. **Abrupt junction terrain slope**: Where a connecting road meets a banked main road, there is no smooth terrain transition between the flat road edge and the banked road's elevated outer edge (~0.5m difference at 10deg banking on a 6m-wide road).

### Root Causes Found

**Bumpy curves**: Bank angles get only ONE smoothing pass — `ApplyFalloffBlending` with a linear triangular kernel (searchRadius=60m). The old `JunctionBankingAdapter` provided additional smoothing as a side effect. Additionally, `CurvatureCalculator.SmoothCurvature()` (Gaussian, window=5, sigma=1.0) exists but is **never called**.

**Junction terrain**: `PriorityAwareJunctionBankingCalculator` correctly computes `JunctionBankingBehavior` (SuppressBanking, AdaptToHigherPriority) and `JunctionBankingFactor` on cross-sections near junctions, but `BankingCalculator.ApplyJunctionAwareBankingAdjustments()` is **dead code — never called**. So banking at junction approach zones is never modified, and the terrain (Phase 4 IDW blending) sees abrupt edge-elevation changes between banked and non-banked roads.

---

## Plan

### Part 1: Fix Bumpy Banked Curves (Curvature + Bank Angle Smoothing)

#### Step 1.1: Enable existing curvature Gaussian smoothing

**File**: [BankingOrchestrator.cs](BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs)
**Method**: `ApplyBankingPreCalculation()` (line 58)

After `_curvatureCalc.CalculateCurvature(crossSections)` at line 88, add:
```csharp
// Smooth curvature to reduce noise from OSM node spacing
if (bankingParams.EnableAutoBanking)
{
    _curvatureCalc.SmoothCurvature(crossSections, bankingParams.CurvatureSmoothingWindow);
}
```

This activates the existing `CurvatureCalculator.SmoothCurvature()` at [CurvatureCalculator.cs:155](BeamNgTerrainPoc/Terrain/Algorithms/Banking/CurvatureCalculator.cs#L155) — no changes needed to that method.

**Guard**: Only when `EnableAutoBanking=true`, so non-banked roads are unaffected.

#### Step 1.2: Add Gaussian smoothing pass for bank angles

**File**: [BankingCalculator.cs](BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankingCalculator.cs)

Add new method `SmoothBankAngles(crossSections, windowSize=7, sigma=1.5)` — structurally identical to `CurvatureCalculator.SmoothCurvature()` but operating on `BankAngleRadians`. Wider window (7 vs 5) and sigma (1.5 vs 1.0) because bank angles are a derived quantity where noise is amplified. After smoothing, call `CalculateBankedNormals(crossSections)` to update 3D normals.

**File**: [BankingOrchestrator.cs](BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs)

In `ApplyBankingPreCalculation()`, after `_bankingCalc.CalculateBankingBasic(crossSections, bankingParams)` at line 91, add:
```csharp
if (bankingParams.EnableAutoBanking)
{
    _bankingCalc.SmoothBankAngles(crossSections, bankingParams.BankAngleSmoothingWindow);
}
```

#### Step 1.3: Add smoothing parameters to BankingParameters

**File**: [BankingParameters.cs](BeamNgTerrainPoc/Terrain/Models/BankingParameters.cs)

Add two properties:
```csharp
/// <summary>Window size for curvature Gaussian smoothing (odd, default 5).</summary>
public int CurvatureSmoothingWindow { get; set; } = 5;

/// <summary>Window size for bank angle Gaussian smoothing (odd, default 7).</summary>
public int BankAngleSmoothingWindow { get; set; } = 7;
```

Update `Clone()` and preset profiles (Highway/RaceTrack/RuralRoad) if they exist.

---

### Part 2: Fix Junction Terrain Slope (Activate Dead Banking Code)

#### Step 2.1: Wire junction-aware banking into the pipeline

**File**: [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs)

**Insertion point**: After the unified profile blender result at line 372 and before convergence checking at line 384. This is inside the iteration loop, after the blender has set elevation+banking at junction endpoints via Hermite corrections.

Add a call to a new orchestrator method:
```csharp
// Apply junction-aware banking adjustments (suppress/adapt banking in approach zones)
if (network.Splines.Any(s => s.Parameters.GetSplineParameters()?.Banking?.EnableAutoBanking == true))
{
    _bankingOrchestrator.ApplyJunctionAwareBanking(network);
}
```

**Why here**: The unified blender handles the junction endpoint itself (flat zone + Hermite decay). The junction-aware banking handles the **approach zone** — the cross-sections leading up to the junction where banking should gradually suppress/adapt. These are complementary, not conflicting.

#### Step 2.2: Create `ApplyJunctionAwareBanking` orchestrator method

**File**: [BankingOrchestrator.cs](BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs)

New method that ties together the existing dead code:

```csharp
public void ApplyJunctionAwareBanking(UnifiedRoadNetwork network)
{
    // Step 1: Calculate junction banking behavior for all cross-sections
    // (PriorityAwareJunctionBankingCalculator sets JunctionBankingBehavior,
    //  JunctionBankingFactor, HigherPrioritySplineId on each CS)
    _junctionBankingCalc.CalculateJunctionBankingBehavior(network);

    // Step 2: Apply adjustments per spline using the existing dead code
    var crossSectionsBySpline = network.CrossSections
        .GroupBy(cs => cs.OwnerSplineId)
        .ToDictionary(g => g.Key, g => g.OrderBy(cs => cs.LocalIndex).ToList());

    foreach (var spline in network.Splines.Where(s => !ShouldExcludeFromBanking(s)))
    {
        var bankingParams = GetBankingParameters(spline) ?? new BankingParameters();
        if (!bankingParams.EnableAutoBanking) continue;
        if (!crossSectionsBySpline.TryGetValue(spline.SplineId, out var crossSections))
            continue;

        // This calls the existing ApplyJunctionAwareBankingAdjustments in BankingCalculator
        _bankingCalc.ApplyJunctionAwareBankingAdjustments(
            crossSections,
            bankingParams,
            cs => CalculateAdaptiveBankAngle(cs, network, crossSectionsBySpline));

        // Recalculate edge elevations with updated bank angles
        var halfWidth = spline.Parameters.RoadWidthMeters / 2.0f;
        foreach (var cs in crossSections)
            _edgeCalc.CalculateEdgeElevationsForCS(cs, halfWidth);
    }
}
```

**Existing code activated**:
- `BankingCalculator.ApplyJunctionAwareBankingAdjustments()` at [BankingCalculator.cs:117-163](BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankingCalculator.cs#L117-L163) — handles SuppressBanking (multiply by factor) and AdaptToHigherPriority (lerp to ramp angle)
- `BankingOrchestrator.CalculateAdaptiveBankAngle()` at [BankingOrchestrator.cs:166-237](BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs#L166-L237) — calculates ramp angle based on primary road surface elevation
- `PriorityAwareJunctionBankingCalculator.CalculateJunctionBankingBehavior()` — sets behaviors based on road priority rules

#### Step 2.3: Handle interaction with unified blender's Hermite corrections

The unified blender at [UnifiedJunctionProfileBlender.cs:794-830](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L794-L830) applies Hermite h00 delta corrections to both elevation and `BankAngleRadians` at T-junction endpoints. The junction-aware banking from Step 2.2 then modifies bank angles in the **approach zone** (further from junction, where `JunctionBankingFactor` transitions from 0 to 1).

**Potential conflict**: The blender sets `BankAngleRadians` at the endpoint to match the primary surface. Then `ApplyJunctionAwareBankingAdjustments` may overwrite it if the endpoint CS has `AdaptToHigherPriority` behavior.

**Mitigation**: In `ApplyJunctionAwareBanking`, skip cross-sections that the unified blender already marked with `MaintainBanking` behavior (set at [UnifiedJunctionProfileBlender.cs:154](BeamNgTerrainPoc/Terrain/Algorithms/UnifiedJunctionProfileBlender.cs#L154) for CSes within the blend zone extent). This is already handled — the blender sets `JunctionBankingBehavior = MaintainBanking` for CSes within the blend extent at lines 130-158, and `ApplyJunctionAwareBankingAdjustments` keeps existing angles for `MaintainBanking` (line 135-138).

---

## Files Modified

| File | Changes |
|------|---------|
| [BankingOrchestrator.cs](BeamNgTerrainPoc/Terrain/Services/BankingOrchestrator.cs) | Add curvature smoothing call (Step 1.1), add bank angle smoothing call (Step 1.2), add `ApplyJunctionAwareBanking()` method (Step 2.2) |
| [BankingCalculator.cs](BeamNgTerrainPoc/Terrain/Algorithms/Banking/BankingCalculator.cs) | Add `SmoothBankAngles()` method (Step 1.2) |
| [BankingParameters.cs](BeamNgTerrainPoc/Terrain/Models/BankingParameters.cs) | Add `CurvatureSmoothingWindow`, `BankAngleSmoothingWindow` properties (Step 1.3) |
| [UnifiedRoadSmoother.cs](BeamNgTerrainPoc/Terrain/Services/UnifiedRoadSmoother.cs) | Wire `ApplyJunctionAwareBanking()` after unified blender (Step 2.1) |

**No changes needed** to: `CurvatureCalculator.cs` (existing method used as-is), `UnifiedJunctionProfileBlender.cs` (already sets MaintainBanking in blend zones), `ElevationMapBuilder.cs` (benefits automatically from smoother edge elevations), `PriorityAwareJunctionBankingCalculator.cs` (already works, just never wired in).

---

## Verification

1. **Bumpy curves**: Generate terrain with a map containing tight banked curves (MaxBankAngleDegrees >= 8). Compare edge elevation profiles before/after — should see smoother transitions with fewer high-frequency bumps. Drive test in BeamNG.
2. **Junction blending**: Generate terrain with T-junction where primary road has banking. Check terrain slope alongside junction — should transition smoothly from flat connecting road edge to banked road's outer edge.
3. **Regression check**: Generate terrain with banking disabled — all changes guarded by `EnableAutoBanking`, should produce identical output.
4. **Convergence**: Verify iteration loop still converges in 1-2 iterations (junction-aware banking only modifies bank angles, not TargetElevation, so convergence metric should be unaffected).

## Risks

- **Over-smoothing**: Gaussian windows (5 for curvature, 7 for bank angle) are conservative. At 2m cross-section spacing, this smooths over ~10m/14m respectively — well below typical curve lengths. Can tune via the new `BankingParameters` if needed.
- **Banking fights blender**: Mitigated by `MaintainBanking` guard — the blender marks its blend zone CSes so junction-aware banking doesn't overwrite them.
- **Performance**: Three additional O(n) passes per spline — negligible vs Phase 4 terrain blending.
