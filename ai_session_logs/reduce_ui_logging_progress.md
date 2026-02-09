# Reduce UI Logging Progress Tracker

## Goal
Reduce UI logging in GenerateTerrain.razor.cs and related files to only show important main steps to users.
- Loop-based logging should go to file only
- Debug messages can be neglected
- Important user-facing steps should remain in UI

## Logging Infrastructure Analysis

### 1. PubSubChannel (UI Logging)
- `PubSubChannel.SendMessage(PubSubMessageType, message)` - Sends to UI snackbar AND adds to state lists
- Has a `modulo` parameter to only send every 10th message (unused in most places)
- Messages are shown as Snackbar notifications in Blazor UI

### 2. TerrainLogger (Library Logging)
- `TerrainLogger.Info/Warning/Error(message)` - Forwards to PubSubChannel via handler in OnInitialized
- `TerrainLogger.Detail(message)` - Can be suppressed with `SuppressDetailedLogging` flag
- `TerrainLogger.DetailWarning(message)` - Same suppression logic
- Always writes to Console for file logging

### 3. TerrainCreationLogger (File-Only Methods)
- `TerrainCreationLogger.Current?.InfoFileOnly(message)` - Write to file only, not UI
- `TerrainCreationLogger.Current?.Detail(message)` - Write to file only
- `TerrainCreationLogger.Current?.Timing(message)` - Write to file only

### 4. Log Files
- `Log_TerrainGen.txt` - Main log
- `Log_TerrainGen_Warnings.txt`
- `Log_TerrainGen_Errors.txt`
- Written via `TerrainGenerationOrchestrator.WriteGenerationLogs()`

## Files Modified

### Session 1 - 2025-01-14

#### 1. GenerateTerrain.razor.cs
- **DONE**: Enabled `TerrainLogger.SuppressDetailedLogging = true` in `OnInitialized()`
- This suppresses all `TerrainLogger.Detail()` calls from reaching UI

#### 2. TerrainGenerationOrchestrator.cs
Changed these `PubSubChannel.SendMessage()` calls to `Console.WriteLine()` (file-only):
- `ClearDebugFolder()` - All messages (clearing files/folders count)
- `GetEffectiveBoundingBox()` - Bounding box technical details
- `CreateCoordinateTransformer()` - GDAL transformer details, crop coordinates
- `ProcessOsmMaterialAsync()` - Per-material processing
- `ProcessOsmRoadMaterialAsync()` - Spline creation details
- `ProcessOsmPolygonMaterialAsync()` - Polygon rasterization details
- `ApplyCropSettings()` - Crop offset/size details
- `UpdateStateFromParameters()` - Auto-calculated max height/base height
- `HandleSpawnPoints()` - Spawn point creation/update coordinates
- `SaveLayerMapToPngAsync()` - Per-file save confirmations
- `RunPostGenerationTasksAsync()` - All [Perf] timing messages
- `ExportAllOsmLayersAsync()` - OSM layer export count

#### 3. TerrainAnalysisOrchestrator.cs
- **DONE**: Added `Console.WriteLine` for debug image save confirmation

#### 4. NetworkJunctionDetector.cs
Changed these `TerrainLogger.Info()` calls to `TerrainCreationLogger.Current?.InfoFileOnly()`:
- Junction breakdown (T, Y, X, Complex counts)
- Cross-material junction count
- T-junction detection count
- Mid-spline crossing count
- OSM junction hints summary
- OSM junction matching summary
- OSM type breakdown
- New junctions from OSM count
- Junction summary totals
- Included OSM types

## Messages KEPT in UI (Important)
1. "Starting terrain generation..." - Main start message
2. "Terrain generated successfully" - Completion
3. "Post-processing complete" - Completion
4. "Analysis complete in X ms" - Summary
5. Errors and warnings
6. "TerrainBlock updated" - Important action
7. "Fetching OSM data from Overpass API" - Long operation

## Messages MOVED to file-only
1. All `[Perf]` timing messages
2. Per-material processing details
3. Per-file operations (saving PNGs, clearing folders)
4. Coordinate transformer technical details
5. Bounding box details
6. Auto-calculated values
7. Spawn point coordinates
8. Junction breakdown statistics
9. OSM matching/filtering details

## Remaining Work for Future Sessions

### NetworkJunctionHarmonizer.cs
Needs similar treatment - per-junction messages like:
- `Junction #{id}: harmonized elevation...`
- `Junction #{id}: Smoothed X cross-sections`

### UnifiedRoadSmoother.cs
Needs similar treatment - debug export messages:
- `Exported spline debug image: {path}`
- `Elevation range: X to Y`

### UnifiedTerrainBlender.cs
Needs similar treatment - pixel statistics:
- `Protection mask: X road core pixels protected`
- `Modified X pixels total`

### TerrainAnalyzer.cs
Needs similar treatment - per-item details

### GenerateTerrain.razor.cs (remaining)
Check for more verbose messages in:
- `ReadGeoTiffMetadata()`
- `RecalculateCroppedElevation()`
- `OnPresetImported()`

### Session 2 - 2025-01-14 (Continued)

#### 5. UnifiedTerrainBlender.cs
- Changed 5 step messages from `TerrainLogger.Info` (UI) to `TerrainCreationLogger.Current?.InfoFileOnly` (File only).

#### 6. UnifiedRoadSmoother.cs
- Moved "Network built..." message to file-only.
- Moved Phase 1.5 (Roundabout Identification) start and methods to file-only.
- Moved Phase 2 (Elevation Calculation) start to file-only.
- Moved Phase 2.3 (Structure Elevation Profiles) to file-only.
- Moved Phase 2.5 (Banking Pre-calculation) to file-only.
- Moved Phase 2.6 (Roundabout Elevation Harmonization) to file-only.
- Reduced verbosity of Phase 3 (Junction Harmonization) - kept main message but moved details about OSM, cross-material, banking, etc., to file-only.
- Moved Phase 3 skipped message to file-only.
- Moved Phase 3.5 (Banking Finalization) to file-only.

#### 7. NetworkJunctionHarmonizer.cs
- Moved "Crossroad to T-junction conversion disabled" message to file-only.

#### 8. TerrainAnalyzer.cs
- Moved analysis count details to file-only.
- Moved Phase 1, Phase 2, Phase 3 details to file-only.
- Moved image generation message to file-only.
- Moved stats summary to file-only (kept completion message).

#### 11. OsmGeometryProcessor.cs
- Changed "Using GDAL..." message to `Console.WriteLine`.
- Changed "Cropped features..." message to `Console.WriteLine`.
- Changed "Rasterized multipolygons..." message to `Console.WriteLine`.
- Changed "Prepared paths..." message to `Console.WriteLine`.
- Changed "Separated paths..." message to `Console.WriteLine`.
- Changed "Connected paths..." message to `Console.WriteLine`.
- Changed "Created splines..." message to `Console.WriteLine`.
- Changed "Path joining..." message to `Console.WriteLine`.
- Changed "Detected roundabouts..." message to `Console.WriteLine`.
- Changed "Regular roads after excluding..." message to `Console.WriteLine`.
- Changed "Total splines" message to `Console.WriteLine`.
- Changed debug image export messages to `Console.WriteLine`.

## Messages KEPT in UI (Important)
1. "Starting terrain generation..." - Main start message
2. "Terrain generated successfully" - Completion
3. "Post-processing complete" - Completion
4. "Analysis complete in X ms" - Summary
5. Errors and warnings
6. "TerrainBlock updated" - Important action
7. "Fetching OSM data from Overpass API" - Long operation
8. "=== UNIFIED ROAD SMOOTHING ===" - Major phase start
9. "=== UNIFIED TERRAIN BLENDING ===" - Major phase start
10. "Starting terrain generation with pre-analyzed network..." - Alternative start message

## Messages MOVED to file-only
1. All `[Perf]` timing messages
2. Per-material processing details (OSM fetching, spline creation, rasterization)
3. Per-file operations (saving PNGs, clearing folders)
4. Coordinate transformer technical details
5. Bounding box details
6. Auto-calculated values
7. Spawn point coordinates
8. Junction breakdown statistics
9. OSM matching/filtering (feature counts, spline counts, etc.)
10. Sub-phase start messages (Phase 1.5, 2.3, 2.5, 2.6, 3.5)
11. Intermediate step messages in terrain blending
12. Analysis phase details (only high-level progress remains)
13. GeoTIFF metadata read details (auto-calculated height, crop details)
14. Skipped junction harmonization message
15. Crossroad conversion config message
16. Spline conversion and path joining details

## Build Status
- [x] Build successful after all changes
