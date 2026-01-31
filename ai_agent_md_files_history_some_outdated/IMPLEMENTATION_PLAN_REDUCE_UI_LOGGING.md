# Implementation Plan: Reduce UI Log Messages for Terrain Analysis/Generation

## Problem Summary
The terrain generation and analysis processes send too many detailed messages to the UI via `PubSubChannel.SendMessage()` and `TerrainLogger.Info()`, resulting in a cluttered user experience. Messages like "Note: Spline 241 has endpoint at junction but also passes through nearby" should only go to log files, not the UI.

## Current Architecture
1. **`TerrainLogger`** - Static logger that forwards messages to a handler (PubSubChannel by default)
2. **`TerrainCreationLogger`** - File-based logger with separate files for Info, Warnings, Errors, and Timing
3. **`PubSubChannel.SendMessage()`** - Sends messages to UI via Blazor Snackbar

## Key Issues Identified
1. `TerrainLogger.Info()` sends ALL messages to both UI and file logs
2. Per-item/per-junction detail messages clutter the UI
3. No clear separation between "user-important" and "diagnostic" messages

---

## Implementation Plan (7 Steps)

### **Step 1: Modify NetworkJunctionDetector to Use File-Only Logging for Details**
**File:** `BeamNgTerrainPoc\Terrain\Algorithms\NetworkJunctionDetector.cs`

**Changes:**
Replace verbose `TerrainLogger.Info()` call with `TerrainCreationLogger.Current?.Detail()`:

| Line | Current Message | Change To |
|------|-----------------|-----------|
| ~278 | `TerrainLogger.Info($"  Note: Spline {spline.SplineId} has endpoint at junction but also passes through nearby");` | `TerrainCreationLogger.Current?.Detail($"Spline {spline.SplineId} has endpoint at junction but also passes through nearby");` |

**Keep these as `TerrainLogger.Info()` (UI-visible):**
- `Found {endpoints.Count} spline endpoints from {network.Splines.Count} splines`
- `Detected {tJunctionCount} T-junction(s)`
- `Detected {midSplineCrossings.Count} mid-spline crossing(s)`
- Junction breakdown summary
- Cross-material count

---

### **Step 2: Modify NetworkJunctionHarmonizer to Use File-Only Logging for Per-Junction Details**
**File:** `BeamNgTerrainPoc\Terrain\Algorithms\NetworkJunctionHarmonizer.cs`

**Change these `TerrainLogger.Info()` calls to `TerrainCreationLogger.Current?.Detail()`:**

| Line | Current Message Pattern | Reason |
|------|-------------------------|--------|
| ~215 | `MidSplineCrossing #{junction.JunctionId}: harmonized elevation...` | Per-junction detail |
| ~391 | `Junction #{junction.JunctionId} ({junction.Type}): {contributorCount} contributors...` | Per-junction detail |
| ~422 | `Junction #{junction.JunctionId}: No valid reference elevations found, skipping` | Per-junction detail |
| ~432 | `Junction #{junction.JunctionId}: ORIGINAL reference elevations range...` | Per-junction detail |
| ~448 | `Junction #{junction.JunctionId}: Large elev range detected...` | Per-junction detail |
| ~475 | `Junction #{junction.JunctionId}: Smoothed {junctionSmoothedCount} cross-sections` | Per-junction detail |

**Keep these as `TerrainLogger.Info()` (UI-visible):**
- `=== UNIFIED NETWORK JUNCTION HARMONIZATION ===`
- Global settings summary
- `RESULT: Modified {result.ModifiedCrossSections} cross-sections`
- `=== NETWORK HARMONIZATION COMPLETE ===`

---

### **Step 3: Modify UnifiedRoadSmoother to Use File-Only Logging for Debug Export Messages**
**File:** `BeamNgTerrainPoc\Terrain\Services\UnifiedRoadSmoother.cs`

**Change these `TerrainLogger.Info()` calls to `TerrainCreationLogger.Current?.Detail()`:**

| Method | Message Pattern | Reason |
|--------|-----------------|--------|
| `ExportMaterialSplineDebugImage()` | `Exported spline debug image: {outputPath}` | Per-file export |
| `ExportMaterialElevationDebugImage()` | `Exported smoothed elevation debug image: {outputPath}` | Per-file export |
| `ExportMaterialElevationDebugImage()` | `Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)` | Detail |
| `ExportJunctionDebugImageIfRequested()` | `Exported unified junction debug image: {outputPath}` | Per-file export |
| `ExportSmoothedHeightmapWithOutlines()` | `Exported smoothed heightmap with outlines: {outputPath}` | Per-file export |

**Keep these as `TerrainLogger.Info()` (UI-visible):**
- `=== UNIFIED ROAD SMOOTHING ===`
- Phase announcements (Phase 1-5)
- Final timing summary

---

### **Step 4: Modify UnifiedTerrainBlender to Use File-Only Logging for Pixel Statistics**
**File:** `BeamNgTerrainPoc\Terrain\Algorithms\UnifiedTerrainBlender.cs`

**Change these `TerrainLogger.Info()` calls to `TerrainCreationLogger.Current?.Detail()`:**

| Line | Message Pattern | Reason |
|------|-----------------|--------|
| ~116 | `Rasterized {sectionsProcessed} cross-sections into combined road mask` | Internal processing |
| ~164 | `Protection mask: {protectedPixels:N0} road core pixels protected` | Pixel statistics |
| ~166 | `Priority resolution: {overwrittenByPriority:N0} pixels assigned...` | Pixel statistics |
| ~332 | `Pre-filled {corePixelsUsed:N0} road core pixels from protection mask` | Pixel statistics |
| ~363 | `Set {blendPixelsSet:N0} blend zone elevation values` | Pixel statistics |
| ~364 | `Total: {corePixelsUsed + blendPixelsSet:N0} pixels with elevation data` | Pixel statistics |
| ~386 | `WARNING: Skipped {skippedInvalid} cross-sections with invalid target elevations` | Keep as Warning |
| ~516-519 | `Modified {modifiedPixels:N0} pixels total`, `Road core:`, `Shoulder:`, `Protected from blend overlap:` | Pixel statistics |
| ~552 | `Smoothing mask: {maskedPixels:N0} pixels` | Detail |
| ~584 | `Gaussian smoothed {smoothedPixels:N0} pixels` | Detail |
| ~614 | `Box smoothed {smoothedPixels:N0} pixels` | Detail |
| ~655 | `Bilateral smoothed {smoothedPixels:N0} pixels` | Detail |

**Keep these as `TerrainLogger.Info()` (UI-visible):**
- `=== UNIFIED TERRAIN BLENDING ===`
- Step announcements (Step 1-5)
- `=== UNIFIED TERRAIN BLENDING COMPLETE ===`
- `=== POST-PROCESSING SMOOTHING ===` and summary

---

### **Step 5: Modify TerrainAnalyzer to Use File-Only Logging for Details**
**File:** `BeamNgTerrainPoc\Terrain\Services\TerrainAnalyzer.cs`

**Change these `TerrainLogger.Info()` calls to `TerrainCreationLogger.Current?.Detail()`:**

| Line | Message Pattern | Reason |
|------|-----------------|--------|
| ~99 | `Built network: {network.Splines.Count} splines, {network.CrossSections.Count} cross-sections` | Already in summary |
| ~127 | `Analysis complete: Splines: {stats.TotalSplines}` | Already in summary |
| ~128-130 | Individual stats lines | Already in summary |
| ~166 | `Junction #{junction.JunctionId} ({junction.Type}) marked as excluded: {junction.ExclusionReason}` | Per-junction action |
| ~182 | `Junction #{junction.JunctionId} ({junction.Type}) exclusion cleared` | Per-junction action |
| ~200 | `Junction #{junction.JunctionId} ({junction.Type}) marked as excluded` | Per-junction action |
| ~205 | `Junction #{junction.JunctionId} ({junction.Type}) exclusion cleared` | Per-junction action |
| ~246 | `Calculated elevations for {totalCalculated} cross-sections` | Already in summary |

**Keep these as `TerrainLogger.Info()` (UI-visible):**
- `=== TERRAIN ANALYSIS (Preview Mode) ===`
- `Analyzing {roadMaterials.Count} road material(s)...`
- `Phase X: ...` announcements
- `=== TERRAIN ANALYSIS COMPLETE ===`

---

### **Step 6: Modify RoadDebugExporter and MasterSplineExporter to Use File-Only Logging**
**Files:** 
- `BeamNgTerrainPoc\Terrain\Services\RoadDebugExporter.cs`
- `BeamNgTerrainPoc\Terrain\Services\MasterSplineExporter.cs`

**RoadDebugExporter - Change to `TerrainCreationLogger.Current?.Detail()`:**

| Line | Message Pattern | Reason |
|------|-----------------|--------|
| ~113 | `Exporting {pathGroups.Count} spline mask(s) to: {splinesDir}` | Detail |
| ~159 | `Exported {pathIndex} individual spline mask(s) + combined mask (16-bit grayscale)` | Per-file export |
| ~160-161 | `Road width:`, `Combined mask:` | Detail |
| ~222 | `Exported spline debug image: {filePath}` | Per-file export |
| ~223 | `Splines drawn: {splinesDrawn}...` | Detail |
| ~260 | `Exported smoothed elevation debug image: {filePath}` | Per-file export |
| ~261 | `Elevation range: {minElev:F2}m (blue) to {maxElev:F2}m (red)` | Detail |
| ~282 | `Exporting smoothed heightmap with road outlines ({width}x{height})...` | Detail |
| ~328-331 | `Exported smoothed heightmap with outlines:`, `Height range:`, `Road edge outline:`, `Blend zone edge outline:` | Detail |

**MasterSplineExporter - Change to `TerrainCreationLogger.Current?.Detail()`:**

| Line | Message Pattern | Reason |
|------|-----------------|--------|
| ~54-55 | `Exporting {network.Splines.Count} master spline(s)...`, `TerrainBaseHeight=...` | Detail |
| ~63 | `Material '{materialName}': {materialSplines.Count} spline(s)` | Per-material detail |
| ~108 | `Exported {masterSplines.Count} master spline(s) to: {outputPath}` | Per-file export |
| ~154 | `Exported {totalSplineCount} individual spline mask(s) + combined mask (16-bit grayscale)` | Per-file export |
| ~155 | `Combined mask: {combinedFilePath}` | Detail |
| ~299 | `Exporting {pathGroups.Count} master spline(s) to JSON...` | Detail |
| ~332 | `Exported {masterSplines.Count} master spline(s) to: {outputPath}` | Per-file export |
| ~360 | `Exporting {splines.Count} pre-built spline(s) to JSON...` | Detail |
| ~404 | `Exported {masterSplines.Count} OSM master spline(s) to: {outputPath}` | Per-file export |

---

### **Step 7: Review TerrainAnalysisOrchestrator for Verbose PubSub Messages**
**File:** `BeamNG_LevelCleanUp\BlazorUI\Services\TerrainAnalysisOrchestrator.cs`

**Change these `PubSubChannel.SendMessage()` calls to file-only or remove:**

| Line | Message | Action |
|------|---------|--------|
| ~153-155 | Junction type breakdown loop: `$"  {type}: {count}"` | REMOVE (detail already logged) |
| ~228 | `Analysis debug image saved: {Path.GetFileName(outputPath)}` | REMOVE (minor status) |
| ~233 | `Failed to save debug image: {ex.Message}` | KEEP as Warning |

**Keep these as `PubSubChannel.SendMessage()` (UI-visible):**
- `Starting terrain analysis (preview mode)...`
- `Loading heightmap for analysis...`
- `Analyzing {roadMaterials.Count} road material(s)...`
- `Fetching OSM data from Overpass API...`
- `Analysis complete in {time}ms: {splines} splines, {junctions} junctions`
- `Analysis failed: {error}`

---

## Summary of Message Categories

### Messages that SHOULD appear in UI
- Phase start/end announcements (`=== PHASE NAME ===`)
- Total counts and summaries (e.g., "Analysis complete: 5 splines, 12 junctions")
- Final results and timing
- Errors and warnings

### Messages that should go to FILE ONLY
- Per-junction details (elevation, position, contributors)
- Per-spline notes and actions
- Debug/export image file paths
- Pixel statistics (road core, shoulder, blend zone counts)
- Coordinate values and elevation ranges
- Per-material processing details

---

## Expected Outcome
After implementation:
- **UI will show:** ~10-15 messages per terrain generation (phases, summaries, results)
- **Log files will contain:** Full details for debugging (hundreds of messages)
- **Users see:** Clean progress without clutter
- **Developers can:** Check logs in `MT_TerrainGeneration\logs\` for detailed diagnostics

---

## Build and Verify
After making all changes:
1. Run `dotnet build` to verify no compilation errors
2. Test terrain generation to verify:
   - UI shows only main processing steps
   - Log files contain all details
3. Test terrain analysis to verify same behavior
