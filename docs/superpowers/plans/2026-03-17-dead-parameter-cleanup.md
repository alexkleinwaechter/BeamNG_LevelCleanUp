# Dead Parameter Cleanup — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove 6 dead UI parameters, delete 2 dead processing properties, and align 10 processing-layer defaults to match UI defaults.

**Architecture:** Pure deletion and constant changes across 12 files. No behavioral changes — the dead parameters never affected output, and the default alignment only matters for code paths that fall back to processing defaults (which the UI always overrides anyway). The Razor UI, model classes, presets, tooltips, import/export, preset exporter/importer, and test harness all need coordinated cleanup.

**Tech Stack:** .NET 9 / Blazor / MudBlazor v8. No test suite — verification is `dotnet build`.

**Reference:** See `ai_docs/2026-03-17_road_smoothing_parameter_audit.md` for the full audit.

---

## Files Overview

| File | Changes |
|------|---------|
| `BeamNgTerrainPoc/Terrain/Models/SplineRoadParameters.cs` | Remove 5 properties + validation; update 2 defaults |
| `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs` | Remove 2 properties + validation; update 2 defaults |
| `BeamNgTerrainPoc/Examples/RoadSmoothingPresets.cs` | Remove dead param assignments from all 10 presets |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor` | Remove 8 UI controls (Curve Fitting card, Graph Ordering section, EnableTerrainBlending switch) |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs` | Remove 7 properties, preset loading lines, build method lines, import/export lines |
| `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs` | Remove 6 tooltip constants |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs` | Remove 5 properties (Tension, Continuity, Bias, UseGraphOrdering, OrderingNeighborRadiusPixels, EnableTerrainBlending) |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor` | Remove dead param export lines |
| `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor` | Remove dead param import lines (keep graceful for old JSON) |
| `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs` | Update 5 fallback defaults |
| `BeamNgTerrainPoc/Terrain/Algorithms/SkeletonizationRoadExtractor.cs` | Update 1 fallback default |
| `BeamNgTerrainPoc/Program.cs` | Remove dead param assignments from test configs |

---

## Task 1: Remove Dead Properties from SplineRoadParameters.cs

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/SplineRoadParameters.cs`

- [ ] **Step 1: Remove the 5 dead properties**

Remove these properties and their XML doc comments:
- `SplineTension` (~line 124, float, default 0.3f)
- `SplineContinuity` (~line 133, float, default 0.5f)
- `SplineBias` (~line 142, float, default 0.0f)
- `UseGraphOrdering` (~line 41, bool, default true)
- `OrderingNeighborRadiusPixels` (~line 55, float, default 2.5f)

- [ ] **Step 2: Remove validation for deleted properties**

In the `Validate()` method, remove:
- Tension validation (~lines 243-244)
- Continuity validation (~lines 246-247)
- Bias validation (~lines 249-250)
- OrderingNeighborRadiusPixels validation (~lines 228-229)

- [ ] **Step 3: Update defaults for active properties**

Change these defaults to match UI:
- `SmoothingWindowSize`: change `101` → `301` (~line 154)
- `ButterworthFilterOrder`: change `3` → `4` (~line 172)
- `BridgeEndpointMaxDistancePixels`: change `30.0f` → `40.0f` (~line 62)
- `JunctionAngleThreshold`: change `45.0f` → `90.0f` (~line 89)

- [ ] **Step 4: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds (may fail due to references in other projects — that's expected, we fix those in later tasks)

---

## Task 2: Remove Dead Properties from RoadSmoothingParameters.cs

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Models/RoadSmoothingParameters.cs`

- [ ] **Step 1: Remove the 2 dead properties**

Remove these properties and their XML doc comments:
- `LongitudinalSmoothingWindowMeters` (~line 125, float, default 20.0f)
- `EnableTerrainBlending` (~line 175, bool, default true)

- [ ] **Step 2: Remove validation for deleted properties**

In the `Validate()` method, remove:
- LongitudinalSmoothingWindowMeters validation (~lines 394-395)

- [ ] **Step 3: Update defaults for active properties**

Change these defaults to match UI:
- `TerrainAffectedRangeMeters`: change `12.0f` → `6.0f` (~line 89)
- `RoadMaxSlopeDegrees`: change `4.0f` → `6.0f` (~line 148)
- `SideMaxSlopeDegrees`: change `30.0f` → `45.0f` (~line 159)
- `EnablePostProcessingSmoothing`: change `false` → `true` (~line 187)

- [ ] **Step 4: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`

---

## Task 3: Remove Dead Params from All 10 Presets

**Files:**
- Modify: `BeamNgTerrainPoc/Examples/RoadSmoothingPresets.cs`

- [ ] **Step 1: Remove from all SplineParameters blocks**

In every preset (PngHighway, PngRuralRoad, PngMountainRoad, PngDirtRoad, PngRacingCircuit, OsmHighway, OsmRuralRoad, OsmMountainRoad, OsmDirtRoad, OsmRacingCircuit), remove these lines from the `SplineParameters = new SplineRoadParameters { ... }` blocks:
- `SplineTension = ...`
- `SplineContinuity = ...`
- `SplineBias = ...`
- `UseGraphOrdering = ...`
- `OrderingNeighborRadiusPixels = ...`

That's 5 lines × 10 presets = 50 lines to remove.

- [ ] **Step 2: Remove EnableTerrainBlending from all presets**

In every preset's top-level initializer, remove:
- `EnableTerrainBlending = true,`

That's 1 line × 10 presets = 10 lines to remove.

- [ ] **Step 3: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds

---

## Task 4: Remove Dead Params from Program.cs Test Configs

**Files:**
- Modify: `BeamNgTerrainPoc/Program.cs`

- [ ] **Step 1: Remove dead param assignments from test configurations**

Remove from all test config blocks (~3 configs around lines 414-420, 495-501, 576-582):
- `SplineTension = ...`
- `SplineContinuity = ...`
- `SplineBias = ...`
- `UseGraphOrdering = ...`
- `OrderingNeighborRadiusPixels = ...`

Remove from test configs (~lines 360, 446, 527):
- `EnableTerrainBlending = true,`

- [ ] **Step 2: Build to verify**

Run: `dotnet build BeamNgTerrainPoc/BeamNgTerrainPoc.csproj`
Expected: Build succeeds with zero errors

- [ ] **Step 3: Commit Task 1-4 (processing layer cleanup)**

```bash
git add BeamNgTerrainPoc/
git commit -m "Remove dead road parameters from processing layer

Remove SplineTension, SplineContinuity, SplineBias, UseGraphOrdering,
OrderingNeighborRadiusPixels, EnableTerrainBlending, and
LongitudinalSmoothingWindowMeters — none were consumed by any algorithm.

Align processing defaults to match UI defaults for 8 parameters:
TerrainAffectedRangeMeters, RoadMaxSlopeDegrees, SideMaxSlopeDegrees,
EnablePostProcessingSmoothing, SmoothingWindowSize, ButterworthFilterOrder,
BridgeEndpointMaxDistancePixels, JunctionAngleThreshold.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 5: Remove Dead UI Controls from Razor Markup

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor`

- [ ] **Step 1: Remove the entire Curve Fitting card**

Remove the entire `<MudPaper>` block for "Curve Fitting" in the PNG-Spline tab (~lines 681-742). This contains the SplineTension, SplineContinuity, and SplineBias controls.

- [ ] **Step 2: Remove the Graph Ordering sub-section**

In the Path Extraction card, remove the `<MudDivider>` and the `<div>` block containing "Use Graph Ordering" switch and "Neighbor Radius" numeric field (~lines 828-848).

- [ ] **Step 3: Remove the EnableTerrainBlending switch**

In the Terrain Transition card (Algorithm tab), remove the `<MudItem>` containing the "Enable Terrain Blending" switch and its helper text (~lines 644-656).

- [ ] **Step 4: Build to verify**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`
Expected: May have CS warnings about unused properties — fixed in next task

---

## Task 6: Remove Dead Properties from Code-Behind

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainMaterialSettings.razor.cs`

- [ ] **Step 1: Remove 7 properties from TerrainMaterialItemExtended**

Remove these property declarations:
- `SplineTension` (~line 813)
- `SplineContinuity` (~line 814)
- `SplineBias` (~line 815)
- `UseGraphOrdering` (~line 818)
- `OrderingNeighborRadiusPixels` (~line 825)
- `EnableTerrainBlending` (~line 803)

- [ ] **Step 2: Remove from ApplyPreset() method**

In `ApplyPreset()` (~lines 1015-1090), remove the lines that copy:
- `SplineTension`, `SplineContinuity`, `SplineBias`
- `UseGraphOrdering`, `OrderingNeighborRadiusPixels`
- `EnableTerrainBlending`

- [ ] **Step 3: Remove from BuildRoadSmoothingParameters()**

In `BuildRoadSmoothingParameters()` (~lines 1100-1208), remove lines that set:
- `SplineTension`, `SplineContinuity`, `SplineBias` in SplineRoadParameters
- `UseGraphOrdering`, `OrderingNeighborRadiusPixels` in SplineRoadParameters
- `EnableTerrainBlending` in RoadSmoothingParameters

**Note on paint-only mode**: Line ~1134 has `EnableTerrainBlending = isPaintOnlyMode ? false : EnableTerrainBlending`. Since `EnableTerrainBlending` is dead (never checked by any algorithm), this paint-only override had no effect. Safe to remove entirely.

- [ ] **Step 4: Remove from ExportRoadSettingsToFile()**

In export method (~lines 422-501), remove JSON keys:
- `"tension"`, `"continuity"`, `"bias"`
- `"useGraphOrdering"`, `"orderingNeighborRadiusPixels"`
- `"enableTerrainBlending"`

- [ ] **Step 5: Remove from ImportRoadSettingsFromFile()**

In import method (~lines 511-689), remove parsing of:
- `"tension"`, `"continuity"`, `"bias"`
- `"useGraphOrdering"`, `"orderingNeighborRadiusPixels"`
- `"enableTerrainBlending"`

Note: Leave import graceful — unknown keys in old exported files are already ignored.

- [ ] **Step 6: Build to verify**

Run: `dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj`

---

## Task 7: Remove Dead Tooltips, TerrainPresetResult, and Preset Exporter/Importer

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/RoadParameterTooltips.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor`

- [ ] **Step 1: Remove tooltip constants**

In `RoadParameterTooltips.cs`, remove these tooltip string constants:
- `SplineTension` tooltip (~lines 279-293)
- `SplineContinuity` tooltip (~lines 295-309)
- `SplineBias` tooltip (~lines 311-322)
- `UseGraphOrdering` tooltip (~lines 342-349)
- `OrderingNeighborRadiusPixels` tooltip (~lines 396-405)
- `EnableTerrainBlending` tooltip (~lines 268-273)

- [ ] **Step 2: Remove properties from TerrainPresetResult**

In `TerrainPresetResult.cs`, remove from the `SplineParametersSettings` class:
- `Tension` property (~line 301)
- `Continuity` property (~line 302)
- `Bias` property (~line 303)
- `UseGraphOrdering` property (~line 304)
- `OrderingNeighborRadiusPixels` property (~line 311)

And from the main class:
- `EnableTerrainBlending` property (~line 283)

Also remove any references to these in the class's methods (preset builders, etc.).

- [ ] **Step 3: Remove dead params from TerrainPresetExporter.razor**

Remove lines that export:
- `EnableTerrainBlending` (~line 615)
- `SplineTension` / `SplineContinuity` / `SplineBias` (~lines 619-621)
- `UseGraphOrdering` (~line 622)
- `OrderingNeighborRadiusPixels` (~line 629)

- [ ] **Step 4: Remove dead params from TerrainPresetImporter.razor**

Remove lines that import into material properties:
- `EnableTerrainBlending` (~lines 998, 1171)
- `Tension` / `Continuity` / `Bias` (~lines 1008-1012, 1178-1180)
- `UseGraphOrdering` (~lines 1014, 1181)
- `OrderingNeighborRadiusPixels` (~lines 1028, 1188)

**Backward compatibility**: The importer's JSON parsing itself (reading keys from the file) should remain graceful. Since the importer uses per-field null checks or try/catch, old exported JSON files containing these keys will simply have their values parsed but not assigned anywhere — which is fine. Just remove the lines that write into the now-deleted model properties.

- [ ] **Step 5: Build full solution**

Run: `dotnet build`
Expected: Build succeeds with zero errors

- [ ] **Step 6: Commit Task 5-7 (UI layer cleanup)**

```bash
git add BeamNG_LevelCleanUp/
git commit -m "Remove dead road parameter UI controls and properties

Remove Curve Fitting card (Tension/Continuity/Bias), Graph Ordering
section (UseGraphOrdering/OrderingNeighborRadiusPixels), and
EnableTerrainBlending switch from TerrainMaterialSettings.

Clean up code-behind properties, preset loading, build method,
import/export, tooltips, TerrainPresetResult, TerrainPresetExporter,
and TerrainPresetImporter.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Update Fallback Defaults in Processing Algorithms

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/OptimizedElevationSmoother.cs`
- Modify: `BeamNgTerrainPoc/Terrain/Algorithms/SkeletonizationRoadExtractor.cs`

- [ ] **Step 1: Update OptimizedElevationSmoother fallbacks**

Change null-coalescing fallback values:
- `RoadMaxSlopeDegrees ?? 4.0f` → `?? 6.0f` (~line 75)
- `RoadMaxSlopeDegrees ?? 4.0f` → `?? 6.0f` (~line 288)
- `SmoothingWindowSize ?? 101` → `?? 301` (~line 66)
- `ButterworthFilterOrder ?? 3` → `?? 4` (~line 68)
- `ButterworthFilterOrder ?? 3` → `?? 4` (~line 285)

- [ ] **Step 2: Update SkeletonizationRoadExtractor fallback**

Change:
- `JunctionAngleThreshold ?? 45.0f` → `?? 90.0f` (~line 277)

- [ ] **Step 3: Build and verify**

Run: `dotnet build`
Expected: Build succeeds

- [ ] **Step 4: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Algorithms/
git commit -m "Align algorithm fallback defaults with UI defaults

Update null-coalescing fallbacks in OptimizedElevationSmoother and
SkeletonizationRoadExtractor to match the values users see in the UI.

Co-Authored-By: Claude Opus 4.6 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Final Verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build BeamNG_LevelCleanUp.sln`
Expected: Build succeeds with zero errors

- [ ] **Step 2: Search for any remaining references to removed parameters**

```bash
grep -rn "SplineTension\|SplineContinuity\|SplineBias\|UseGraphOrdering\|OrderingNeighborRadius\|EnableTerrainBlending\|LongitudinalSmoothing" --include="*.cs" --include="*.razor"
```

Expected: Zero matches (or only in comments/docs that can be left as-is)

- [ ] **Step 3: Verify no compilation warnings about unused members**

Check build output for CS0169/CS0414 warnings related to the removed parameters.

---

## Summary of Changes

| Priority | What | Lines Removed (approx.) |
|----------|------|------------------------|
| P1 | Remove 6 dead UI controls | ~120 lines of Razor markup |
| P1 | Remove 7 dead properties + preset/build/import/export refs | ~80 lines of C# |
| P1 | Remove from 10 presets + 3 test configs | ~80 lines of C# |
| P1 | Remove 6 tooltip constants | ~50 lines of C# |
| P1 | Clean up TerrainPresetExporter + TerrainPresetImporter | ~20 lines of C# |
| P1 | Clean up TerrainPresetResult (Tension/Continuity/Bias + others) | ~10 lines of C# |
| P2 | Remove EnableTerrainBlending everywhere | included above |
| P3 | Align 10 processing defaults to UI | ~10 one-line changes |
| P4 | Remove LongitudinalSmoothingWindowMeters | ~4 lines |
| **Total** | | **~370 lines removed, ~10 lines changed** |

---

## Out of Scope (Deferred)

- **`ExclusionLayerPaths`** property on `RoadSmoothingParameters` — the audit flagged this as potentially unused, but it was not confirmed dead with the same rigor. Defer to a follow-up investigation.
