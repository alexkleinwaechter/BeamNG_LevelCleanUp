# Unified Elevation Data Import — Implementation Plan

## Context

The Generate Terrain page currently has three separate buttons for elevation sources: "PNG Heightmap", "GeoTIFF File", and "GeoTIFF Tiles". Each shows different pickers and requires the user to know their file format upfront.

**Goals:**
1. Add XYZ ASCII elevation data support (common German geodata format like DGM1)
2. Replace the 3-button approach with a unified drop-zone-style import area
3. Support: single file, multiple files, ZIP archives — for GeoTIFF, XYZ, and PNG formats
4. Auto-detect format from file content/extension

**Key discovery:** GDAL 3.10 (our version, `MaxRev.Gdal.Core 3.10.0.300`) natively reads XYZ ASCII files via its XYZ raster driver. No custom parser needed — we can reuse the existing `GeoTiffReader.ReadFromDataset()` pipeline. The only gap is that XYZ files lack embedded CRS, so we need the user to provide an EPSG code.

---

## UI Design: Drop Zone Import

Replace the current `MudButtonGroup` (PNG / GeoTIFF File / GeoTIFF Tiles) with a single drop-zone-style component.

### Before Import (empty state)

```
┌─────────────────────────────────────────────┐
│  Elevation Data (Required)              [?] │
│                                             │
│  ┌ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┐│
│  │                                         ││
│  │        📂 Import elevation data         ││
│  │     GeoTIFF · XYZ · ZIP · PNG           ││
│  │                                         ││
│  │    [Browse Files]  [Browse Folder]      ││
│  │                                         ││
│  └ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┘│
└─────────────────────────────────────────────┘
```

- "Browse Files" opens `OpenFileDialog` with multi-select and combined filter:
  `"Elevation Data (*.tif;*.tiff;*.xyz;*.zip;*.png)|*.tif;*.tiff;*.xyz;*.zip;*.png|GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|XYZ ASCII (*.xyz;*.txt)|*.xyz;*.txt|ZIP Archives (*.zip)|*.zip|PNG Heightmap (*.png)|*.png"`
- "Browse Folder" opens `FolderBrowserDialog` for tile directories
- Dashed border using MudBlazor `MudPaper` with `Outlined` + custom dashed border CSS

### After Import (loaded state)

The drop zone transforms into a summary card:

```
┌─────────────────────────────────────────────┐
│  Elevation Data                             │
│                                             │
│  ┌─────────────────────────────────────────┐│
│  │ ✓ 3 GeoTIFF tiles loaded    [Change]   ││
│  │   dgm1_tile_a.tif, dgm1_tile_b.tif,    ││
│  │   dgm1_tile_c.tif                       ││
│  │─────────────────────────────────────────││
│  │   2048×2048 px · EPSG:25832             ││
│  │   Elevation: 120.0m – 890.0m            ││
│  │   12.1km × 12.1km                       ││
│  └─────────────────────────────────────────┘│
│                                             │
│  For XYZ files, show EPSG input:            │
│  ┌─────────────────────────────────────────┐│
│  │ EPSG Code: [25832    ]                  ││
│  │ Auto-detected ✓  (ETRS89 / UTM 32N)    ││
│  │ Common: 25832, 25833, 32632             ││
│  └─────────────────────────────────────────┘│
└─────────────────────────────────────────────┘
```

- "Change" button resets to empty state
- File names shown in a condensed list (first 3 files + "and N more" if many)
- EPSG input only shown when XYZ files are detected (with auto-detection chip)
- Clicking a different EPSG re-reads metadata automatically

### Bounding Box Display

The existing bounding box `MudAlert` (lines 245-313 in GenerateTerrain.razor) and crop settings (lines 367-385) remain as-is below the import zone. They continue to be conditionally shown when geo metadata is available. The only change: remove the `HeightmapSourceType` condition from the crop visibility check — show it whenever `_geoTiffOriginalWidth > 0`.

---

## Architecture

### Detected Source Types

Rename `HeightmapSourceType` to `ElevationSourceType` (or keep old name as alias for backwards compat in presets):

```csharp
public enum ElevationSourceType
{
    Png,              // Single PNG heightmap (no geo metadata)
    GeoTiffSingle,    // Single GeoTIFF file
    GeoTiffMultiple,  // Multiple GeoTIFF tiles (need combining)
    XyzFile           // XYZ ASCII file(s) (need EPSG code)
}
```

### Import Detection Flow

```
User selects file(s) or folder
        │
        ▼
  ┌─────────────┐
  │ Detect input │
  └──────┬──────┘
         │
    ┌────┴────┬──────────┬───────────┐
    ▼         ▼          ▼           ▼
  .zip     .tif/.tiff   .xyz/.txt   .png
    │         │          │           │
    ▼         │          │           │
  Extract     │          │           │
  to temp     │          │           │
  dir, scan ──┤          │           │
    │         ▼          ▼           ▼
    │      Count files   │        PNG mode
    │      1 → Single    │     (no geo metadata)
    │      N → Multiple  │
    │                    │
    │              Auto-detect EPSG
    │              Show EPSG input
    │                    │
    └────────┬───────────┘
             ▼
       Read metadata via
       GeoTiffMetadataService
       (GDAL opens both formats)
             │
             ▼
       Populate state &
       show summary card
```

### ZIP Handling

1. User selects a `.zip` file via "Browse Files"
2. Extract to `AppPaths.TempFolder/_elevation_import/` (clean up previous)
3. Scan extracted contents for supported files (`.tif`, `.tiff`, `.xyz`, `.txt`, `.png`)
4. Treat extracted files as if user selected them directly
5. Store the temp extraction path for cleanup

### XYZ + GDAL Integration

Since GDAL 3.10 reads XYZ natively, the approach is:

1. `Gdal.Open(xyzPath, Access.GA_ReadOnly)` — GDAL auto-detects XYZ format
2. XYZ files have no embedded CRS → `dataset.GetProjection()` returns empty
3. We construct WKT from user-provided EPSG code using `SpatialReference.ImportFromEPSG()`
4. Pass this WKT as an "override projection" to the existing reading pipeline

This requires a small refactor to `GeoTiffReader.ReadFromDataset()`: add an optional `string? overrideProjection` parameter. When provided, use it instead of `dataset.GetProjection()`.

---

## Files to Create

### 1. `BeamNG_LevelCleanUp/BlazorUI/Services/ElevationImportService.cs` (NEW)

New service that orchestrates the unified import flow. Replaces the direct file selection + metadata reading pattern.

```csharp
public class ElevationImportService
{
    private readonly GeoTiffMetadataService _metadataService;

    // Import from file(s) selected via OpenFileDialog
    public async Task<ElevationImportResult> ImportFilesAsync(string[] filePaths);

    // Import from folder selected via FolderBrowserDialog
    public async Task<ElevationImportResult> ImportFolderAsync(string folderPath);

    // Re-read metadata after EPSG change (for XYZ)
    public async Task<ElevationImportResult> ReloadWithEpsgAsync(
        ElevationImportResult previous, int epsgCode);

    // EPSG auto-detection heuristic
    public static int? AutoDetectEpsg(string filePath);

    // Cleanup extracted temp files
    public void CleanupTempFiles();
}
```

**`ImportFilesAsync` logic:**
1. Classify files by extension
2. If any `.zip`: extract to temp dir, add extracted files to the list
3. If all `.png`: → `ElevationSourceType.Png`, single file only
4. If all `.tif`/`.tiff`: → 1 file = `GeoTiffSingle`, N files = `GeoTiffMultiple`
5. If all `.xyz`/`.txt`: → `XyzFile`, auto-detect EPSG
6. Mixed formats: error with descriptive message
7. Call appropriate `GeoTiffMetadataService` method to read metadata
8. Return `ElevationImportResult`

**`ImportFolderAsync` logic:**
1. Scan folder for supported file extensions
2. If GeoTIFF files found: → `GeoTiffMultiple` (existing tile flow)
3. If XYZ files found: → `XyzFile`, auto-detect EPSG
4. If both: error (don't mix formats)
5. Read metadata, return result

### 2. `BeamNG_LevelCleanUp/BlazorUI/Services/ElevationImportResult.cs` (NEW)

Result of the import detection + metadata reading step:

```csharp
public class ElevationImportResult
{
    public ElevationSourceType SourceType { get; init; }
    public string[] FilePaths { get; init; } = [];
    public string[] FileNames { get; init; } = [];  // Just the file names for display
    public int FileCount { get; init; }
    public string FormatLabel { get; init; } = "";   // "GeoTIFF", "XYZ ASCII", "PNG"

    // Metadata (from GeoTiffMetadataService)
    public GeoTiffMetadataService.GeoTiffMetadataResult? Metadata { get; init; }

    // XYZ-specific
    public bool NeedsEpsgCode { get; init; }
    public int? DetectedEpsgCode { get; init; }
    public int EpsgCode { get; set; }                // User-confirmed EPSG

    // ZIP extraction
    public string? TempExtractionPath { get; init; } // For cleanup
    public bool WasExtractedFromZip { get; init; }

    // For the pipeline (resolved paths)
    // Single GeoTIFF: ResolvedGeoTiffPath set
    // Multiple GeoTIFF: ResolvedGeoTiffDirectory set
    // XYZ: ResolvedXyzPath set
    // PNG: ResolvedHeightmapPath set
    public string? ResolvedGeoTiffPath { get; init; }
    public string? ResolvedGeoTiffDirectory { get; init; }
    public string? ResolvedXyzPath { get; init; }
    public string? ResolvedHeightmapPath { get; init; }
}
```

---

## Files to Modify

### 3. `BeamNgTerrainPoc/Terrain/GeoTiff/GeoTiffReader.cs`

**Add `overrideProjection` parameter to `ReadFromDataset()` and related methods:**

```csharp
// Line ~111: Add optional parameter
private GeoTiffImportResult ReadFromDataset(
    Dataset dataset, string? sourcePath = null,
    int? targetSize = null, string? overrideProjection = null)
{
    // ...existing code...

    // Line ~162: Replace dataset.GetProjection() with override when available
    var projection = overrideProjection ?? dataset.GetProjection();

    // ...rest unchanged...
}
```

Same for `ReadFromDatasetCropped()` and `GetGeoTiffInfoExtended()` — thread the override through.

**Add XYZ-specific public methods:**

```csharp
/// Opens an XYZ file via GDAL and reads it with the provided EPSG as CRS.
public GeoTiffImportResult ReadXyz(string xyzPath, int epsgCode, int? targetSize = null)
{
    InitializeGdal();

    using var dataset = Gdal.Open(xyzPath, Access.GA_ReadOnly);
    if (dataset == null)
        throw new InvalidOperationException($"Failed to open XYZ file: {xyzPath}");

    // Construct projection WKT from EPSG code
    var srs = new SpatialReference(null);
    srs.ImportFromEPSG(epsgCode);
    srs.ExportToWkt(out string projectionWkt, null);

    return ReadFromDataset(dataset, xyzPath, targetSize, overrideProjection: projectionWkt);
}

/// Gets metadata from an XYZ file without loading full elevation data.
public GeoTiffInfoResult GetXyzInfoExtended(string xyzPath, int epsgCode)
{
    InitializeGdal();

    using var dataset = Gdal.Open(xyzPath, Access.GA_ReadOnly);
    if (dataset == null)
        throw new InvalidOperationException($"Failed to open XYZ file: {xyzPath}");

    var srs = new SpatialReference(null);
    srs.ImportFromEPSG(epsgCode);
    srs.ExportToWkt(out string projectionWkt, null);

    // Reuse existing metadata extraction with projection override
    return GetInfoFromDataset(dataset, xyzPath, projectionWkt);
}
```

Extract common metadata logic from `GetGeoTiffInfoExtended()` into `GetInfoFromDataset(Dataset, string?, string?)` to avoid duplication.

### 4. `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs`

**Update enum:**
```csharp
public enum HeightmapSourceType
{
    Png,
    GeoTiffFile,
    GeoTiffDirectory,  // Keep for backwards compat with presets
    XyzFile
}
```

**Add fields to `TerrainPresetResult`:**
```csharp
public string? XyzPath { get; set; }
public int? XyzEpsgCode { get; set; }
```

### 5. `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs`

**Add XYZ fields:**
```csharp
public string? XyzPath { get; set; }
public int XyzEpsgCode { get; set; } = 25832;
```

### 6. `BeamNgTerrainPoc/Terrain/TerrainCreator.cs`

**Add XYZ to the priority chain (~line 140, after GeoTiffDirectory):**
```csharp
else if (!string.IsNullOrWhiteSpace(parameters.XyzPath))
{
    perfLog.Info($"Loading heightmap from XYZ: {parameters.XyzPath}");
    var xyzResult = await LoadFromXyzAsync(parameters.XyzPath, parameters, perfLog);
    heightmapImage = xyzResult;
    shouldDisposeHeightmap = true;
    isGeoTiffSource = true; // Use same spike prevention as GeoTIFF
    perfLog.Timing($"Loaded XYZ heightmap: {sw.Elapsed.TotalSeconds:F2}s");
}
```

**Add `LoadFromXyzAsync()` method** (follows `LoadFromGeoTiffAsync()` pattern):
```csharp
private async Task<Image<L16>> LoadFromXyzAsync(
    string xyzPath, TerrainCreationParameters parameters, TerrainCreationLogger log)
{
    var reader = new GeoTiffReader();
    var result = await Task.Run(() =>
        reader.ReadXyz(xyzPath, parameters.XyzEpsgCode, parameters.Size));

    // Populate geo-metadata (same as LoadFromGeoTiffAsync)
    parameters.GeoBoundingBox = result.Wgs84BoundingBox ??
                                (result.BoundingBox.IsValidWgs84 ? result.BoundingBox : null);
    parameters.GeoTiffMinElevation = result.MinElevation;
    parameters.GeoTiffMaxElevation = result.MaxElevation;

    if (parameters.MaxHeight <= 0)
    {
        parameters.MaxHeight = (float)result.ElevationRange;
        log.Info($"Using XYZ elevation range as MaxHeight: {parameters.MaxHeight:F1}m");
        if (parameters.AutoSetBaseHeightFromGeoTiff)
        {
            parameters.TerrainBaseHeight = (float)result.MinElevation;
            log.Info($"Using XYZ minimum elevation as TerrainBaseHeight: {parameters.TerrainBaseHeight:F1}m");
        }
    }

    if (parameters.GeoBoundingBox != null)
        log.Info($"XYZ bounding box for Overpass API: {parameters.GeoBoundingBox.ToOverpassBBox()}");
    else
        log.Warning("Could not determine WGS84 bounding box - OSM features unavailable.");

    return result.HeightmapImage;
}
```

### 7. `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs`

**Add XYZ state fields (in HEIGHTMAP SOURCE section):**
```csharp
public string? XyzPath { get; set; }
public int XyzEpsgCode { get; set; } = 25832;
public int? XyzDetectedEpsg { get; set; }
```

**Add import result tracking:**
```csharp
public ElevationImportResult? ElevationImportResult { get; set; }
```

**Update `CanGenerate()`:**
```csharp
HeightmapSourceType.XyzFile => !string.IsNullOrEmpty(XyzPath) && File.Exists(XyzPath) && XyzEpsgCode > 0,
```

**Update `GetHeightmapSourceDescription()`:**
```csharp
HeightmapSourceType.XyzFile => "XYZ ASCII elevation file (georeferenced grid data)",
```

**Update `Reset()` and `ClearGeoMetadata()`:**
```csharp
XyzPath = null;
XyzEpsgCode = 25832;
XyzDetectedEpsg = null;
ElevationImportResult = null;
```

### 8. `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor`

**Replace the entire heightmap source section** (lines 136-241) with the drop zone component:

```razor
<!-- Elevation Data Import -->
<MudItem xs="12">
    <div class="d-flex align-center gap-2 mb-2">
        <MudText Typo="Typo.subtitle2">Elevation Data (Required)</MudText>
        <MudTooltip Text="Learn about supported elevation data formats">
            <MudIconButton Icon="@Icons.Material.Filled.Help"
                           Size="Size.Small" Color="Color.Primary"
                           OnClick="OpenHeightmapSourceHelpDialog" />
        </MudTooltip>
    </div>

    @if (_elevationImportResult == null)
    {
        @* Empty state: drop zone *@
        <MudPaper Outlined="true" Class="pa-6 d-flex flex-column align-center gap-3"
                  Style="border-style: dashed; border-width: 2px; border-color: var(--mud-palette-primary);">
            <MudIcon Icon="@Icons.Material.Filled.Terrain" Size="Size.Large"
                     Color="Color.Primary" />
            <MudText Typo="Typo.subtitle1">Import elevation data</MudText>
            <MudText Typo="Typo.caption" Color="Color.Secondary">
                GeoTIFF · XYZ · ZIP · PNG
            </MudText>
            <div class="d-flex gap-2">
                <MudButton Variant="Variant.Filled" Color="Color.Primary"
                           StartIcon="@Icons.Material.Filled.InsertDriveFile"
                           OnClick="ImportElevationFiles">
                    Browse Files
                </MudButton>
                <MudButton Variant="Variant.Outlined" Color="Color.Primary"
                           StartIcon="@Icons.Material.Filled.Folder"
                           OnClick="ImportElevationFolder">
                    Browse Folder
                </MudButton>
            </div>
        </MudPaper>
    }
    else
    {
        @* Loaded state: summary card *@
        <MudPaper Outlined="true" Class="pa-4">
            <div class="d-flex align-center justify-space-between mb-2">
                <div class="d-flex align-center gap-2">
                    <MudIcon Icon="@Icons.Material.Filled.CheckCircle"
                             Color="Color.Success" Size="Size.Small" />
                    <MudText Typo="Typo.subtitle2">
                        @_elevationImportResult.FileCount @_elevationImportResult.FormatLabel
                        file@(_elevationImportResult.FileCount != 1 ? "s" : "") loaded
                    </MudText>
                    @if (_elevationImportResult.WasExtractedFromZip)
                    {
                        <MudChip T="string" Size="Size.Small" Color="Color.Info">
                            from ZIP
                        </MudChip>
                    }
                </div>
                <MudButton Variant="Variant.Text" Size="Size.Small"
                           StartIcon="@Icons.Material.Filled.SwapHoriz"
                           OnClick="ClearElevationImport">
                    Change
                </MudButton>
            </div>

            @* File list (condensed) *@
            <MudText Typo="Typo.caption" Color="Color.Secondary" Class="mb-2">
                @GetFileListSummary()
            </MudText>

            @* Metadata summary *@
            @if (_elevationImportResult.Metadata != null)
            {
                var m = _elevationImportResult.Metadata;
                <MudDivider Class="my-2" />
                <div class="d-flex flex-wrap gap-4">
                    @if (m.OriginalWidth > 0)
                    {
                        <MudText Typo="Typo.body2">
                            <strong>Size:</strong> @m.OriginalWidth×@m.OriginalHeight px
                        </MudText>
                    }
                    @if (m.MinElevation.HasValue && m.MaxElevation.HasValue)
                    {
                        <MudText Typo="Typo.body2">
                            <strong>Elevation:</strong>
                            @m.MinElevation.Value.ToString("F1")m – @m.MaxElevation.Value.ToString("F1")m
                        </MudText>
                    }
                    @if (!string.IsNullOrEmpty(m.ProjectionName))
                    {
                        <MudText Typo="Typo.body2">
                            <strong>CRS:</strong> @m.ProjectionName
                        </MudText>
                    }
                </div>
            }
        </MudPaper>

        @* EPSG input for XYZ files *@
        @if (_elevationImportResult.NeedsEpsgCode)
        {
            <MudPaper Outlined="true" Class="pa-3 mt-2">
                <div class="d-flex align-center gap-3">
                    <MudNumericField @bind-Value="_xyzEpsgCode"
                                     Label="EPSG Code" Variant="Variant.Outlined"
                                     Min="1000" Max="99999"
                                     Style="max-width: 160px;"
                                     DebounceInterval="500"
                                     OnDebounceIntervalElapsed="OnEpsgCodeChanged" />
                    @if (_xyzDetectedEpsg.HasValue)
                    {
                        <MudChip T="string" Color="Color.Success" Size="Size.Small">
                            Auto-detected
                        </MudChip>
                    }
                    <MudText Typo="Typo.caption" Color="Color.Secondary">
                        Common: 25832 (ETRS89/UTM 32N), 25833 (UTM 33N), 32632 (WGS84/UTM 32N)
                    </MudText>
                </div>
            </MudPaper>
        }
    }
</MudItem>
```

**Update crop settings visibility** (remove source type condition):
```razor
@if (_geoTiffOriginalWidth > 0 && _geoTiffOriginalHeight > 0 &&
    _heightmapSourceType != HeightmapSourceType.Png)
```

### 9. `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`

**Replace the individual select methods** with unified import methods:

```csharp
private ElevationImportResult? _elevationImportResult
{
    get => _state.ElevationImportResult;
    set => _state.ElevationImportResult = value;
}

private async Task ImportElevationFiles()
{
    string[]? selectedPaths = null;
    var staThread = new Thread(() =>
    {
        using var dialog = new OpenFileDialog();
        dialog.Filter = "Elevation Data (*.tif;*.tiff;*.xyz;*.zip;*.png)|*.tif;*.tiff;*.xyz;*.zip;*.png|" +
                        "GeoTIFF (*.tif;*.tiff)|*.tif;*.tiff|" +
                        "XYZ ASCII (*.xyz;*.txt)|*.xyz;*.txt|" +
                        "ZIP Archives (*.zip)|*.zip|" +
                        "PNG Heightmap (*.png)|*.png|" +
                        "All Files (*.*)|*.*";
        dialog.Title = "Import Elevation Data";
        dialog.Multiselect = true;
        if (dialog.ShowDialog() == DialogResult.OK)
            selectedPaths = dialog.FileNames;
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();

    if (selectedPaths?.Length > 0)
    {
        ClearGeoMetadata();
        _elevationImportResult = await _elevationImportService.ImportFilesAsync(selectedPaths);
        ApplyImportResult(_elevationImportResult);
        _dropContainer?.Refresh();
        await InvokeAsync(StateHasChanged);
    }
}

private async Task ImportElevationFolder()
{
    string? selectedPath = null;
    var staThread = new Thread(() =>
    {
        using var dialog = new FolderBrowserDialog();
        dialog.Description = "Select folder with elevation data tiles";
        dialog.UseDescriptionForTitle = true;
        if (dialog.ShowDialog() == DialogResult.OK)
            selectedPath = dialog.SelectedPath;
    });
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();

    if (!string.IsNullOrEmpty(selectedPath))
    {
        ClearGeoMetadata();
        _elevationImportResult = await _elevationImportService.ImportFolderAsync(selectedPath);
        ApplyImportResult(_elevationImportResult);
        _dropContainer?.Refresh();
        await InvokeAsync(StateHasChanged);
    }
}

private void ApplyImportResult(ElevationImportResult result)
{
    // Map to existing state fields
    _heightmapSourceType = result.SourceType switch
    {
        ElevationSourceType.Png => HeightmapSourceType.Png,
        ElevationSourceType.GeoTiffSingle => HeightmapSourceType.GeoTiffFile,
        ElevationSourceType.GeoTiffMultiple => HeightmapSourceType.GeoTiffDirectory,
        ElevationSourceType.XyzFile => HeightmapSourceType.XyzFile,
        _ => HeightmapSourceType.Png
    };

    // Set resolved paths
    _heightmapPath = result.ResolvedHeightmapPath;
    _geoTiffPath = result.ResolvedGeoTiffPath;
    _geoTiffDirectory = result.ResolvedGeoTiffDirectory;
    _xyzPath = result.ResolvedXyzPath;

    if (result.EpsgCode > 0)
        _xyzEpsgCode = result.EpsgCode;
    if (result.DetectedEpsgCode.HasValue)
        _xyzDetectedEpsg = result.DetectedEpsgCode;

    // Apply metadata to geo display fields
    if (result.Metadata != null)
    {
        _geoBoundingBox = result.Metadata.Wgs84BoundingBox;
        _geoTiffNativeBoundingBox = result.Metadata.NativeBoundingBox;
        _geoTiffProjectionName = result.Metadata.ProjectionName;
        _geoTiffProjectionWkt = result.Metadata.ProjectionWkt;
        _geoTiffGeoTransform = result.Metadata.GeoTransform;
        _geoTiffOriginalWidth = result.Metadata.OriginalWidth;
        _geoTiffOriginalHeight = result.Metadata.OriginalHeight;
        _geoTiffMinElevation = result.Metadata.MinElevation;
        _geoTiffMaxElevation = result.Metadata.MaxElevation;
        _canFetchOsmData = result.Metadata.CanFetchOsmData;
        _osmBlockedReason = result.Metadata.OsmBlockedReason;

        // Auto-populate terrain settings
        if (result.Metadata.SuggestedTerrainSize.HasValue && !_hasExistingTerrainSettings)
            _terrainSize = result.Metadata.SuggestedTerrainSize.Value;
        if (_geoTiffMinElevation.HasValue && _geoTiffMaxElevation.HasValue)
        {
            _maxHeight = (float)(_geoTiffMaxElevation.Value - _geoTiffMinElevation.Value);
            _terrainBaseHeight = (float)_geoTiffMinElevation.Value;
        }

        SyncMetersPerPixelFromGeoTiff(result.Metadata);
    }

    Snackbar.Add($"{result.FormatLabel}: {result.FileCount} file(s) loaded", Severity.Success);
}

private void ClearElevationImport()
{
    _elevationImportService.CleanupTempFiles();
    _elevationImportResult = null;
    _heightmapPath = null;
    _geoTiffPath = null;
    _geoTiffDirectory = null;
    _xyzPath = null;
    ClearGeoMetadata();
    StateHasChanged();
}

private async Task OnEpsgCodeChanged()
{
    if (_elevationImportResult?.NeedsEpsgCode == true && _xyzEpsgCode > 0)
    {
        _elevationImportResult = await _elevationImportService.ReloadWithEpsgAsync(
            _elevationImportResult, _xyzEpsgCode);
        ApplyImportResult(_elevationImportResult);
        await InvokeAsync(StateHasChanged);
    }
}

private string GetFileListSummary()
{
    if (_elevationImportResult == null) return "";
    var names = _elevationImportResult.FileNames;
    if (names.Length <= 3) return string.Join(", ", names);
    return $"{string.Join(", ", names.Take(3))} and {names.Length - 3} more";
}
```

### 10. `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs`

**Add XYZ case in `BuildTerrainParameters()`:**

```csharp
case HeightmapSourceType.XyzFile:
    parameters.XyzPath = state.XyzPath;
    parameters.XyzEpsgCode = state.XyzEpsgCode;
    break;
```

### 11. `BeamNG_LevelCleanUp/BlazorUI/Services/GeoTiffMetadataService.cs`

**Add XYZ metadata reading method:**

```csharp
public async Task<GeoTiffMetadataResult> ReadFromXyzFileAsync(string xyzPath, int epsgCode)
{
    return await Task.Run(() =>
    {
        var reader = new GeoTiffReader();
        var info = reader.GetXyzInfoExtended(xyzPath, epsgCode);
        var suggestedTerrainSize = GetNearestPowerOfTwo(Math.Max(info.Width, info.Height));

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"XYZ: {info.Width}x{info.Height}px, EPSG:{epsgCode}");

        return new GeoTiffMetadataResult
        {
            Wgs84BoundingBox = info.Wgs84BoundingBox,
            NativeBoundingBox = info.BoundingBox,
            ProjectionName = info.ProjectionName,
            ProjectionWkt = info.Projection,
            GeoTransform = info.GeoTransform,
            OriginalWidth = info.Width,
            OriginalHeight = info.Height,
            MinElevation = info.MinElevation,
            MaxElevation = info.MaxElevation,
            SuggestedTerrainSize = suggestedTerrainSize,
            CanFetchOsmData = info.Wgs84BoundingBox?.IsValidWgs84 == true,
            OsmBlockedReason = info.Wgs84BoundingBox?.IsValidWgs84 != true
                ? "XYZ file requires valid EPSG code for WGS84 transformation" : null
        };
    });
}
```

---

## EPSG Auto-Detection Heuristic

Implemented in `ElevationImportService.AutoDetectEpsg()`:

1. Open XYZ file with GDAL, read geoTransform to get coordinate extents
2. Check ranges:
   - X: 200,000–900,000 and Y: 5,000,000–6,200,000 → EPSG:25832 (ETRS89/UTM 32N, covers most of Germany)
   - Could extend with more zones later
3. Conservative: returns `null` if uncertain, forcing manual input

---

## DI Registration

Register the new service in the Blazor startup:

```csharp
builder.Services.AddSingleton<ElevationImportService>();
```

Or if it needs `GeoTiffMetadataService`:
```csharp
builder.Services.AddTransient<ElevationImportService>();
```

Check existing service registration pattern in `Form1.cs` or wherever Blazor services are configured.

---

## Implementation Order

1. **GeoTiffReader refactor**: Add `overrideProjection` parameter to `ReadFromDataset()` and extract `GetInfoFromDataset()`. Add `ReadXyz()` and `GetXyzInfoExtended()` methods.
2. **Model changes**: Add `XyzFile` to enum, `XyzPath`/`XyzEpsgCode` to `TerrainCreationParameters` and `TerrainPresetResult`.
3. **TerrainCreator**: Add `XyzPath` to priority chain and `LoadFromXyzAsync()` method.
4. **GeoTiffMetadataService**: Add `ReadFromXyzFileAsync()`.
5. **ElevationImportService**: Create the new service with format detection, ZIP handling, EPSG auto-detection.
6. **ElevationImportResult**: Create the result class.
7. **TerrainGenerationState**: Add XYZ fields and import result tracking.
8. **GenerateTerrain.razor + .razor.cs**: Replace source selection UI with drop zone, wire up import methods.
9. **TerrainGenerationOrchestrator**: Add `XyzFile` case in `BuildTerrainParameters()`.
10. **Preset export/import**: Update `TerrainPresetExporter`/`TerrainPresetImporter` for XYZ fields.

---

## File Summary

| # | File | Action | Description |
|---|------|--------|-------------|
| 1 | `BeamNG_LevelCleanUp/BlazorUI/Services/ElevationImportService.cs` | NEW | Unified import orchestrator (format detection, ZIP extraction, EPSG auto-detect) |
| 2 | `BeamNG_LevelCleanUp/BlazorUI/Services/ElevationImportResult.cs` | NEW | Import result DTO (source type, files, metadata, resolved paths) |
| 3 | `BeamNgTerrainPoc/Terrain/GeoTiff/GeoTiffReader.cs` | MODIFY | Add `overrideProjection` param, extract `GetInfoFromDataset()`, add `ReadXyz()` + `GetXyzInfoExtended()` |
| 4 | `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs` | MODIFY | Add `XyzFile` to enum, `XyzPath`/`XyzEpsgCode` to preset |
| 5 | `BeamNgTerrainPoc/Terrain/Models/TerrainCreationParameters.cs` | MODIFY | Add `XyzPath`, `XyzEpsgCode` |
| 6 | `BeamNgTerrainPoc/Terrain/TerrainCreator.cs` | MODIFY | Add XYZ to priority chain, add `LoadFromXyzAsync()` |
| 7 | `BeamNG_LevelCleanUp/BlazorUI/Services/GeoTiffMetadataService.cs` | MODIFY | Add `ReadFromXyzFileAsync()` |
| 8 | `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs` | MODIFY | Add XYZ fields, import result, update `CanGenerate()`/`Reset()` |
| 9 | `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` | MODIFY | Replace source buttons with drop zone UI |
| 10 | `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` | MODIFY | Add unified import methods, replace individual select methods |
| 11 | `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` | MODIFY | Add `XyzFile` case in `BuildTerrainParameters()` |

---

## Verification

1. **Single GeoTIFF**: Browse Files → select one .tif → verify metadata loads, summary shows "1 GeoTIFF file loaded"
2. **Multiple GeoTIFF tiles**: Browse Files → multi-select .tif files → verify "N GeoTIFF files loaded", tiles combined
3. **GeoTIFF folder**: Browse Folder → select tile directory → verify same as above
4. **Single XYZ**: Browse Files → select .xyz → verify EPSG auto-detected, metadata loads after EPSG confirmed
5. **Multiple XYZ tiles**: Browse Files → multi-select .xyz files → verify "N XYZ ASCII files loaded", EPSG input shown
6. **XYZ folder**: Browse Folder → select folder of .xyz files → verify tiles detected, EPSG input shown
7. **ZIP with GeoTIFF**: Browse Files → select .zip containing .tif → verify extraction + metadata
8. **ZIP with XYZ**: Browse Files → select .zip containing .xyz → verify extraction + EPSG prompt
9. **PNG**: Browse Files → select .png → verify "1 PNG file loaded", no geo metadata
10. **Change button**: Click Change → verify returns to empty drop zone, state cleared
11. **EPSG change**: Load XYZ → change EPSG code → verify metadata re-reads with new projection
12. **Full generation**: Load any format → configure materials → Generate → verify .ter output
13. **Preset round-trip**: Export preset with XYZ source → import → verify XYZ path + EPSG restored

---

## Addendum: Multi-XYZ Tile Support & GeoTiffCombiner Analysis

*Added 2026-02-20 after initial single-XYZ implementation was complete.*

### GeoTiffCombiner — Is It Needed?

**YES, the GeoTiffCombiner IS needed** for multi-tile workflows. Analysis:

1. **What it does**: GDAL can only open files one at a time — it can't natively treat a folder of tiles as a single dataset. The combiner:
   - First pass: opens each tile, reads bounds and pixel size to determine combined extent
   - Creates a single output GeoTIFF with the total extent
   - Second pass: copies pixel data from each tile at the correct offset
   - Writes the combined GeoTIFF to disk

2. **Where it's used in the pipeline**:
   - **Crop preview** (GenerateTerrain.razor.cs:1416): Combines tiles ONCE, caches result in `CachedCombinedGeoTiffPath`. Subsequent crop adjustments read from the cached file.
   - **Terrain generation** (TerrainCreator.cs:636-659): If no cached file exists, combines during generation. If cached, the orchestrator bypasses the combiner entirely (passes cached path as `GeoTiffPath`).

3. **Why it also makes sense for multi-XYZ**:
   - XYZ files opened by GDAL behave like regular raster datasets — same `ReadRaster()`/`GetGeoTransform()` API
   - The intermediate combined GeoTIFF **adds value** for XYZ specifically: embeds CRS, converts from slow text format to fast binary
   - The cached combined file enables fast crop preview adjustments

4. **Minor suboptimality**: During `TerrainCreator.LoadFromGeoTiffDirectoryAsync()`, the combiner writes a temp file then reads it back — unnecessary I/O. But the caching optimization in the UI layer means this code path is rarely hit in practice.

### Multi-XYZ Implementation Plan

XYZ ASCII files come as tiles (e.g., DGM1 data from German geodata portals), just like GeoTIFF tiles. We reuse the `GeoTiffCombiner` with the `overrideProjection` parameter already added in the `CombineFilesAsync()` method.

#### New Source Type

```csharp
public enum ElevationSourceType
{
    Png,
    GeoTiffSingle,
    GeoTiffMultiple,
    XyzFile,        // Single XYZ file (existing)
    XyzMultiple     // Multiple XYZ tiles (NEW)
}
```

#### Changes Required

| # | File | Change |
|---|------|--------|
| 1 | `ElevationImportResult.cs` | Add `XyzMultiple` to `ElevationSourceType` enum |
| 2 | `ElevationImportService.cs` | Remove single-file limitation for XYZ; detect multi-XYZ → `XyzMultiple`; read metadata from first file with projection override |
| 3 | `TerrainCreationParameters.cs` | Add `string[]? XyzFilePaths` for multi-XYZ (keep `XyzPath` for single) |
| 4 | `TerrainGenerationState.cs` | Add `XyzFilePaths` field, update `CanGenerate()` for multi-XYZ |
| 5 | `TerrainPresetResult.cs` | Add `XyzFilePaths` to preset |
| 6 | `TerrainCreator.cs` | Add `LoadFromMultipleXyzAsync()` method using `GeoTiffCombiner.CombineFilesAsync()` with projection override |
| 7 | `TerrainGenerationOrchestrator.cs` | Add `XyzMultiple` case in `BuildTerrainParameters()` |
| 8 | `GenerateTerrain.razor.cs` | Map `XyzMultiple` to `HeightmapSourceType.XyzFile` (same UI treatment, just multi-file) |
| 9 | `GeoTiffCombiner.cs` | Already extended with `CombineFilesAsync(string[], outputPath, overrideProjection?)` |

#### Pipeline Flow for Multi-XYZ

```
User selects multiple .xyz files (or folder with .xyz)
        │
        ▼
ElevationImportService.ImportFilesAsync()
  → Detects XYZ, count > 1 → XyzMultiple
  → Auto-detect EPSG from first file
  → Read metadata from first file via GeoTiffMetadataService
        │
        ▼
GenerateTerrain.razor.cs ApplyImportResult()
  → Sets _heightmapSourceType = XyzFile
  → Sets _state.XyzFilePaths = [...all paths...]
  → Sets _state.XyzEpsgCode from auto-detect or user input
        │
        ▼
TerrainGenerationOrchestrator.BuildTerrainParameters()
  → XyzMultiple: sets parameters.XyzFilePaths + XyzEpsgCode
        │
        ▼
TerrainCreator.CreateTerrainFileAsync()
  → XyzFilePaths set → LoadFromMultipleXyzAsync()
    → GeoTiffCombiner.CombineFilesAndImportAsync(filePaths, projectionWkt, targetSize, ...)
      → CombineFilesInternal(): stitches all XYZ tiles into combined GeoTIFF (with CRS embedded)
      → GeoTiffReader.ReadGeoTiff(combinedPath): reads combined result
      → Deletes temp combined file
    → Returns Image<L16> heightmap + geo metadata
```

#### Crop Preview for Multi-XYZ

For crop preview (`RecalculateCroppedElevation()`), multi-XYZ needs the same caching as multi-GeoTIFF:
1. First crop change: combine all XYZ tiles → cached GeoTIFF
2. Subsequent crop changes: read from cached file (fast)

This reuses the existing `CachedCombinedGeoTiffPath` mechanism.
