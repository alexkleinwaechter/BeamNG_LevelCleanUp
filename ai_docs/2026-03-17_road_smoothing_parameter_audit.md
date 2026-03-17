# Road Smoothing Parameter Audit — 2026-03-17

Deep code analysis of all road smoothing parameters in `TerrainMaterialSettings.razor`, tracing each parameter from UI binding through `BuildRoadSmoothingParameters()` to actual consumption in the processing pipeline.

**Scope**: Algorithm tab, Post-Processing tab, and related primary road parameters visible in the screenshots.

---

## Executive Summary

| Category | Count |
|----------|-------|
| Total UI-exposed road parameters | ~60 |
| Fully wired and active | 48 |
| **Dead parameters (UI control does nothing)** | **6** |
| **Default value mismatches (UI vs processing)** | **10** |
| Parameters not transferred to pipeline | 1 |

**Verdict**: Most parameters are correctly wired. However, **6 parameters have UI controls that do absolutely nothing** (the values are set but never read by processing code), and **10 parameters have different default values in the UI vs the processing model**, which means the defaults shown to users don't match what the processing layer would use if it fell back to its own defaults.

---

## Part 1: Dead Parameters (UI Controls That Do Nothing)

These parameters have full UI controls (numeric fields, switches, tooltips) but are **never consumed by any processing algorithm**. Users can change them freely with zero effect on output.

### 1.1 SplineTension, SplineContinuity, SplineBias

**UI Location**: Advanced Settings > PNG-Spline tab > Curve Fitting card
**UI Controls**: Three `MudNumericField` controls (lines 704-740 in .razor)
**Model Properties**: `Material.SplineTension`, `Material.SplineContinuity`, `Material.SplineBias`

**Why dead**: The road spline system uses **MathNet.Numerics Akima and Natural cubic spline interpolation** (`RoadSpline.cs:68-75`), not Catmull-Rom splines. Tension/Continuity/Bias (TCB) parameters are a Kochanek-Bartels concept that doesn't apply to the current spline implementation. These parameters are defined in `SplineRoadParameters.cs` and transferred via `BuildRoadSmoothingParameters()`, but no algorithm ever reads them.

**Recommendation**: Remove from UI or implement TCB spline alternative.

### 1.2 UseGraphOrdering

**UI Location**: Advanced Settings > PNG-Spline tab > Path Extraction card > "Use Graph Ordering" switch
**UI Control**: `MudSwitch` (line 832-835 in .razor)

**Why dead**: Defined in `SplineRoadParameters.cs:41` but never conditionally checked in any processing code. This was a legacy ordering algorithm that was replaced but the UI control was never removed.

### 1.3 OrderingNeighborRadiusPixels

**UI Location**: Advanced Settings > PNG-Spline tab > Path Extraction card > "Neighbor Radius" numeric field
**UI Control**: `MudNumericField` (line 839-845 in .razor), disabled when `UseGraphOrdering` is false

**Why dead**: Companion to `UseGraphOrdering`. Both are remnants of an abandoned graph-based point ordering system. Never read by any processing code.

### 1.4 EnableTerrainBlending

**UI Location**: Advanced Settings > Algorithm tab > Terrain Transition card > "Enable Terrain Blending" switch
**UI Control**: `MudSwitch` (line 646-649 in .razor)

**Why dead**: Defined in `RoadSmoothingParameters.cs:175` but **never conditionally checked**. The terrain blending phase (Phase 4, `DistanceFieldTerrainBlender`) always runs regardless of this flag. The UI shows "Debug mode: geometry only, no terrain modification" when disabled, but this is misleading — terrain IS still modified.

**Impact**: Users who disable this expecting a "dry run" will get full terrain modification anyway.

---

## Part 2: Active Parameters (Confirmed Wired)

### 2.1 Sampling & Geometry (Algorithm Tab)

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| CrossSectionIntervalMeters | MudNumericField, Min=0.1, Max=5.0, Step=0.1 | `UnifiedRoadNetworkBuilder.BuildNetwork()`, `OptimizedElevationSmoother`, `RoadDebugExporter` | **ACTIVE** |
| SplineInterpolationType | MudSelect (SmoothInterpolated / LinearControlPoints) | `UnifiedRoadNetworkBuilder` (2 places), `OsmGeometryProcessor` | **ACTIVE** |

### 2.2 Elevation Smoothing (Algorithm Tab)

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| SplineSmoothingWindowSize | MudNumericField, Min=11, Max=1001, Step=10 | `OptimizedElevationSmoother.cs:72` (Step 1 smoothing) | **ACTIVE** |
| SplineUseButterworthFilter | MudSwitch | `OptimizedElevationSmoother.cs:76` (filter selection) | **ACTIVE** |
| SplineButterworthFilterOrder | MudNumericField, Min=1, Max=8, Step=1 | `OptimizedElevationSmoother.cs:77` (passed to Butterworth lib) | **ACTIVE** |

### 2.3 Network Leveling (Algorithm Tab)

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| GlobalLevelingStrength | MudNumericField, Min=0.0, Max=1.0, Step=0.05 | `OptimizedElevationSmoother.cs:73,157` (Step 3: blend toward network average) | **ACTIVE** |

### 2.4 Terrain Transition (Algorithm Tab)

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| BlendFunctionType | MudSelect (Linear/Cosine/Cubic/Quintic) | `DistanceFieldTerrainBlender`, `ProtectedBlendingProcessor`, `BlendFunctions` | **ACTIVE** |
| EnableTerrainBlending | MudSwitch | *NONE* | **DEAD** (see Part 1) |

### 2.5 Post-Processing (Post-Processing Tab)

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| EnablePostProcessingSmoothing | MudSwitch | `UnifiedRoadSmoother`, `DistanceFieldTerrainBlender`, `PostProcessingSmoother` | **ACTIVE** |
| SmoothingType | MudSelect (Gaussian/Box/Bilateral) | `PostProcessingSmoother`, `DistanceFieldTerrainBlender` | **ACTIVE** |
| SmoothingKernelSize | MudNumericField, Min=3, Max=21, Step=2 | `PostProcessingSmoother`, `DistanceFieldTerrainBlender` | **ACTIVE** |
| SmoothingSigma | MudNumericField, Min=0.1, Max=5.0, Step=0.1 | `PostProcessingSmoother`, `DistanceFieldTerrainBlender` | **ACTIVE** |
| SmoothingMaskExtensionMeters | MudNumericField, Min=0.0, Max=20.0, Step=1.0 | `DistanceFieldTerrainBlender` | **ACTIVE** |
| SmoothingIterations | MudNumericField, Min=1, Max=5, Step=1 | `PostProcessingSmoother`, `DistanceFieldTerrainBlender` | **ACTIVE** |

### 2.6 Primary Road Parameters

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| RoadWidthMeters | MudNumericField, Min=1.0, Max=50.0 | `DistanceFieldTerrainBlender`, `UnifiedRoadNetworkBuilder`, `BlendFunctions` | **ACTIVE** |
| RoadSurfaceWidthMeters | MudNumericField, Min=0.0, Max=50.0 | `MaterialPainter`, `MasterSplineExporter`, `DecalRoadGenerator` | **ACTIVE** |
| TerrainAffectedRangeMeters | MudNumericField, Min=0.0, Max=50.0 | `DistanceFieldTerrainBlender`, validation | **ACTIVE** |
| RoadEdgeProtectionBufferMeters | MudNumericField, Min=0.0, Max=20.0 | `ProtectedBlendingProcessor`, `RoadMaskBuilder` | **ACTIVE** |
| EnableMaxSlopeConstraint | MudSwitch | `OptimizedElevationSmoother.cs:74` (gates Step 4) | **ACTIVE** |
| RoadMaxSlopeDegrees | MudNumericField, Min=0.0, Max=45.0 | `OptimizedElevationSmoother.cs:75,288`, `UnifiedJunctionProfileBlender.cs:1452` | **ACTIVE** |
| SideMaxSlopeDegrees | MudNumericField, Min=0.0, Max=90.0 | `ProtectedBlendingProcessor.cs:61,183` | **ACTIVE** |

### 2.7 Master Spline Export

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| MasterSplineNodeDistanceMeters | MudNumericField, Min=5.0, Max=100.0 | `MasterSplineExporter`, `UnifiedRoadSmoother` | **ACTIVE** |
| MasterSplineWidthMeters | MudNumericField, Min=0.0, Max=50.0 | `DecalRoadGenerator`, `RoadCorridorBuilder`, `MasterSplineExporter` | **ACTIVE** |

### 2.8 PNG-Spline Tab Parameters (Skeleton Extraction)

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| SkeletonDilationRadius | MudNumericField, Min=0, Max=5 | `SkeletonizationRoadExtractor.cs` | **ACTIVE** |
| DensifyMaxSpacingPixels | MudNumericField, Min=0.5, Max=10.0 | `SkeletonizationRoadExtractor.cs:118` | **ACTIVE** |
| SimplifyTolerancePixels | MudNumericField, Min=0.0, Max=5.0 | `UnifiedRoadNetworkBuilder.cs:293` (RDP) | **ACTIVE** |
| BridgeEndpointMaxDistancePixels | MudNumericField, Min=0.0, Max=100.0 | `UnifiedRoadNetworkBuilder.cs:654`, `SkeletonizationRoadExtractor.cs:100` | **ACTIVE** |
| MinPathLengthPixels | MudNumericField, Min=0.0, Max=200.0 | `SkeletonizationRoadExtractor.cs:68,108` | **ACTIVE** |
| PreferStraightThroughJunctions | MudSwitch | `SkeletonizationRoadExtractor.cs:276` | **ACTIVE** |
| JunctionAngleThreshold | MudNumericField, Min=10.0, Max=90.0 | `UnifiedRoadNetworkBuilder.cs:655`, `SkeletonizationRoadExtractor.cs:277` | **ACTIVE** |
| SplineTension | MudNumericField | *NONE* | **DEAD** |
| SplineContinuity | MudNumericField | *NONE* | **DEAD** |
| SplineBias | MudNumericField | *NONE* | **DEAD** |
| UseGraphOrdering | MudSwitch | *NONE* | **DEAD** |
| OrderingNeighborRadiusPixels | MudNumericField | *NONE* | **DEAD** |

### 2.9 Junction Tab Parameters

| Parameter | UI Control | Pipeline Consumer | Status |
|-----------|-----------|-------------------|--------|
| JunctionDetectionRadiusMeters | MudNumericField | `NetworkJunctionDetector` (3 places) | **ACTIVE** |
| JunctionBlendDistanceMeters | MudNumericField | `UnifiedJunctionProfileBlender`, `RoundaboutElevationHarmonizer` | **ACTIVE** |
| AutoCalculateBlendDistance | MudSwitch | `GetEffectiveBlendDistance()` | **ACTIVE** |
| EnableJunctionIdwFiltering | MudSwitch | `UnifiedJunctionProfileBlender` | **ACTIVE** |
| MinTerminatingIdwWeight | MudNumericField | `UnifiedJunctionProfileBlender` | **ACTIVE** |
| IdwFilterTaperDistanceMeters | MudNumericField | `UnifiedJunctionProfileBlender` | **ACTIVE** |
| EnableRoundaboutDetection | MudSwitch | `UnifiedRoadSmoother` (Phase 1.5) | **ACTIVE** |
| EnableRoundaboutRoadTrimming | MudSwitch | OSM processing | **ACTIVE** |
| RoundaboutConnectionRadiusMeters | MudNumericField | `UnifiedRoadSmoother` | **ACTIVE** |
| RoundaboutOverlapToleranceMeters | MudNumericField | OSM processing | **ACTIVE** |
| ForceUniformRoundaboutElevation | MudSwitch | `RoundaboutElevationHarmonizer` (5 places) | **ACTIVE** |
| RoundaboutBlendDistanceMeters | MudNumericField | `RoundaboutElevationHarmonizer` | **ACTIVE** |

---

## Part 3: Default Value Mismatches

The UI model (`TerrainMaterialItemExtended` in `TerrainMaterialSettings.razor.cs`) and the processing model (`RoadSmoothingParameters` / `SplineRoadParameters`) have **different default values** for the following parameters. When a preset is applied, the preset values override both, but if a user creates a material without applying a preset, they get the UI defaults.

| Parameter | UI Default | Processing Default | Delta | Concern |
|-----------|------------|-------------------|-------|---------|
| **TerrainAffectedRangeMeters** | 6.0 | 12.0 | UI is 50% lower | Users get tighter blending than intended |
| **RoadMaxSlopeDegrees** | 6.0 | 4.0 | UI is 50% higher | Users allow steeper roads |
| **SideMaxSlopeDegrees** | 45.0 | 30.0 | UI is 50% higher | Users allow much steeper embankments |
| **SplineTension** | 0.2 | 0.3 | UI is 33% lower | Moot (parameter is dead) |
| **SplineContinuity** | 0.7 | 0.5 | UI is 40% higher | Moot (parameter is dead) |
| **SplineSmoothingWindowSize** | 301 | 101 | UI is 3x larger | UI applies much heavier smoothing |
| **SplineButterworthFilterOrder** | 4 | 3 | UI is 33% higher | Sharper filter cutoff |
| **BridgeEndpointMaxDistancePixels** | 40.0 | 30.0 | UI is 33% higher | More aggressive gap bridging |
| **JunctionAngleThreshold** | 90.0 | 45.0 | UI is 2x higher | Very different junction behavior |
| **EnablePostProcessingSmoothing** | true | false | Opposite | UI enables by default, processing doesn't |

**Note**: The SplineTension/SplineContinuity mismatches are moot since those parameters are dead anyway.

**Note**: These mismatches are **not bugs per se** — the UI defaults may have been intentionally tuned for better user experience. However, the processing-layer defaults become misleading since they're never actually seen by users. Consider aligning them or documenting the divergence.

---

## Part 4: Missing Parameter Transfer

### LongitudinalSmoothingWindowMeters

- **Defined in**: `RoadSmoothingParameters.cs:125` with default 20.0
- **UI property**: Does not exist on `TerrainMaterialItemExtended`
- **Transfer**: Not in `BuildRoadSmoothingParameters()`
- **Pipeline usage**: **COMPLETELY UNUSED** — no algorithm reads this property
- **Assessment**: Dead parameter at the processing model level too. Can be safely removed from `RoadSmoothingParameters`.

---

## Part 5: Cross-Parameter Validation Analysis

The `GetValidationWarnings()` method (lines 144-262 of code-behind) implements 9 validation rules. All are correctly wired:

| Rule | Parameters Checked | Severity | Correct? |
|------|-------------------|----------|----------|
| Disconnected road risk | GlobalLevelingStrength > 0.5 + TerrainAffectedRange < 15 | Error | Yes |
| Blend zone too narrow | GlobalLevelingStrength > 0.3 + TerrainAffectedRange < 12 | Warning | Yes |
| Cross-section gaps | CrossSectionInterval vs (RoadWidth/2 + TerrainAffectedRange)/3 | Warning | Yes |
| Window size odd check | SplineSmoothingWindowSize % 2 == 0 | Info | Yes |
| Kernel size odd check | SmoothingKernelSize % 2 == 0 (when enabled) | Warning | Yes |
| Mask extension insufficient | SmoothingMaskExtension < CrossSectionInterval * 2 | Info | Yes |
| Butterworth recommendation | !UseButterworthFilter + WindowSize > 150 | Info | Yes |
| High Butterworth order | ButterworthFilterOrder > 6 | Info | Yes |
| Narrow road | RoadWidthMeters < 3.0 | Info | Yes |
| Steep road | RoadMaxSlopeDegrees > 12.0 | Info | Yes |

All validation rules reference **active parameters** and produce correct warnings.

---

## Part 6: Preset System Analysis

The preset application via `ApplyPreset()` (line 1015-1090) correctly transfers ALL parameter categories:
- Primary parameters (road widths, slopes, blend range)
- Algorithm settings (blend function, cross-section interval, terrain blending)
- Post-processing parameters
- Spline parameters (including banking sub-object)
- Junction harmonization (including roundabout settings)

**No gaps found** in preset application — all active parameters are covered.

---

## Part 7: Import/Export Analysis

The `ExportRoadSettingsToFile()` and `ImportRoadSettingsFromFile()` methods handle all parameters with:
- Graceful null checks on import (missing fields use current values)
- Enum parsing with fallback
- Backward compatibility (ignores unknown/legacy fields like "approach")
- Sets `SelectedPreset = Custom` after import

**No gaps found** — export/import covers all active parameters including banking.

---

## Recommendations

### Priority 1: Remove Dead UI Controls
Remove or hide these controls to avoid user confusion:
1. **SplineTension / SplineContinuity / SplineBias** — Curve Fitting card in PNG-Spline tab
2. **UseGraphOrdering / OrderingNeighborRadiusPixels** — Path Extraction card in PNG-Spline tab
3. **EnableTerrainBlending** — Terrain Transition card in Algorithm tab

### Priority 2: Fix Misleading EnableTerrainBlending
If the intent is to have a debug mode that skips terrain blending, implement the conditional check in `DistanceFieldTerrainBlender` or `UnifiedRoadSmoother`. If not needed, remove the switch.

### Priority 3: Align or Document Default Mismatches
Decide whether the UI defaults or processing defaults are authoritative. The significant ones:
- `SplineSmoothingWindowSize`: 301 (UI) vs 101 (processing) — 3x difference
- `JunctionAngleThreshold`: 90 (UI) vs 45 (processing) — 2x difference
- `TerrainAffectedRangeMeters`: 6 (UI) vs 12 (processing) — 50% difference

### Priority 4: Clean Up Dead Processing Properties
- Remove `LongitudinalSmoothingWindowMeters` from `RoadSmoothingParameters`
- Remove `ExclusionLayerPaths` if unused

---

*Analysis performed by tracing every parameter from `TerrainMaterialSettings.razor` UI bindings → `TerrainMaterialItemExtended` model → `BuildRoadSmoothingParameters()` transfer → `RoadSmoothingParameters`/`SplineRoadParameters`/`JunctionHarmonizationParameters` processing models → actual algorithm consumption in `BeamNgTerrainPoc/Terrain/` classes.*
