using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.BlazorUI.Services;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using MudBlazor;
using MudBlazor.Utilities;
using DialogResult = System.Windows.Forms.DialogResult;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class GenerateTerrain
{
    // ========================================
    // SERVICES
    // ========================================
    private readonly GeoTiffMetadataService _geoTiffService = new();
    private readonly TerrainMaterialService _materialService = new();
    private readonly TerrainGenerationOrchestrator _generationOrchestrator = new();

    // ========================================
    // STATE (delegates to TerrainGenerationState)
    // ========================================
    private readonly TerrainGenerationState _state = new();

    // ========================================
    // UI-ONLY STATE (not in TerrainGenerationState)
    // ========================================
    private Anchor _anchor;
    private string _drawerHeight = "200px";
    private string _drawerWidth = "100%";
    private MudDropContainer<TerrainMaterialSettings.TerrainMaterialItemExtended> _dropContainer = null!;
    private bool _openDrawer;
    private TerrainPresetExporter? _presetExporter;
    private TerrainPresetImporter? _presetImporter;
    private bool _showErrorLog;
    private bool _showWarningLog;

    // Convenience accessors for state properties (to minimize razor changes)
    private List<string> _errors => _state.Errors;
    private List<string> _messages => _state.Messages;
    private List<string> _warnings => _state.Warnings;
    private List<TerrainMaterialSettings.TerrainMaterialItemExtended> _terrainMaterials => _state.TerrainMaterials;

    private bool _enableCrossMaterialHarmonization
    {
        get => _state.EnableCrossMaterialHarmonization;
        set => _state.EnableCrossMaterialHarmonization = value;
    }

    private GeoBoundingBox? _geoBoundingBox
    {
        get => _state.GeoBoundingBox;
        set => _state.GeoBoundingBox = value;
    }

    private string? _geoTiffDirectory
    {
        get => _state.GeoTiffDirectory;
        set => _state.GeoTiffDirectory = value;
    }

    private double[]? _geoTiffGeoTransform
    {
        get => _state.GeoTiffGeoTransform;
        set => _state.GeoTiffGeoTransform = value;
    }

    private double? _geoTiffMaxElevation
    {
        get => _state.GeoTiffMaxElevation;
        set => _state.GeoTiffMaxElevation = value;
    }

    private double? _geoTiffMinElevation
    {
        get => _state.GeoTiffMinElevation;
        set => _state.GeoTiffMinElevation = value;
    }

    private GeoBoundingBox? _geoTiffNativeBoundingBox
    {
        get => _state.GeoTiffNativeBoundingBox;
        set => _state.GeoTiffNativeBoundingBox = value;
    }

    private int _geoTiffOriginalHeight
    {
        get => _state.GeoTiffOriginalHeight;
        set => _state.GeoTiffOriginalHeight = value;
    }

    private int _geoTiffOriginalWidth
    {
        get => _state.GeoTiffOriginalWidth;
        set => _state.GeoTiffOriginalWidth = value;
    }

    private string? _geoTiffPath
    {
        get => _state.GeoTiffPath;
        set => _state.GeoTiffPath = value;
    }

    private string? _geoTiffProjectionName
    {
        get => _state.GeoTiffProjectionName;
        set => _state.GeoTiffProjectionName = value;
    }

    private string? _geoTiffProjectionWkt
    {
        get => _state.GeoTiffProjectionWkt;
        set => _state.GeoTiffProjectionWkt = value;
    }

    private bool _hasWorkingDirectory
    {
        get => _state.HasWorkingDirectory;
        set => _state.HasWorkingDirectory = value;
    }

    private bool _hasExistingTerrainSettings
    {
        get => _state.HasExistingTerrainSettings;
        set => _state.HasExistingTerrainSettings = value;
    }

    private string? _heightmapPath
    {
        get => _state.HeightmapPath;
        set => _state.HeightmapPath = value;
    }

    private HeightmapSourceType _heightmapSourceType
    {
        get => _state.HeightmapSourceType;
        set => _state.HeightmapSourceType = value;
    }

    private CropAnchor _cropAnchor
    {
        get => _state.CropAnchor;
        set => _state.CropAnchor = value;
    }

    private CropResult? _cropResult
    {
        get => _state.CropResult;
        set => _state.CropResult = value;
    }

    private string? _cachedCombinedGeoTiffPath
    {
        get => _state.CachedCombinedGeoTiffPath;
        set => _state.CachedCombinedGeoTiffPath = value;
    }

    private bool _isGenerating
    {
        get => _state.IsGenerating;
        set => _state.IsGenerating = value;
    }

    private bool _isLoading
    {
        get => _state.IsLoading;
        set => _state.IsLoading = value;
    }

    private string _levelName
    {
        get => _state.LevelName;
        set => _state.LevelName = value;
    }

    private float _maxHeight
    {
        get => _state.MaxHeight;
        set => _state.MaxHeight = value;
    }

    private float _metersPerPixel
    {
        get => _state.MetersPerPixel;
        set => _state.MetersPerPixel = value;
    }

    private float _terrainBaseHeight
    {
        get => _state.TerrainBaseHeight;
        set => _state.TerrainBaseHeight = value;
    }

    private string _terrainName
    {
        get => _state.TerrainName;
        set => _state.TerrainName = value;
    }

    private int _terrainSize
    {
        get => _state.TerrainSize;
        set => _state.TerrainSize = value;
    }

    private bool _updateTerrainBlock
    {
        get => _state.UpdateTerrainBlock;
        set => _state.UpdateTerrainBlock = value;
    }

    private string _workingDirectory
    {
        get => _state.WorkingDirectory;
        set => _state.WorkingDirectory = value;
    }

    private bool _canFetchOsmData
    {
        get => _state.CanFetchOsmData;
        set => _state.CanFetchOsmData = value;
    }

    private string? _osmBlockedReason
    {
        get => _state.OsmBlockedReason;
        set => _state.OsmBlockedReason = value;
    }

    private GeoTiffValidationResult? _geoTiffValidationResult
    {
        get => _state.GeoTiffValidationResult;
        set => _state.GeoTiffValidationResult = value;
    }

    /// <summary>
    ///     Gets the effective bounding box for OSM queries.
    /// </summary>
    private GeoBoundingBox? EffectiveBoundingBox => _state.EffectiveBoundingBox;

    [AllowNull] private MudExpansionPanels FileSelect { get; set; }

    protected override void OnInitialized()
    {
        // Configure TerrainLogger to forward messages to PubSub
        TerrainLogger.SetLogHandler((level, message) =>
        {
            var pubSubType = level switch
            {
                TerrainLogLevel.Warning => PubSubMessageType.Warning,
                TerrainLogLevel.Error => PubSubMessageType.Error,
                _ => PubSubMessageType.Info
            };
            PubSubChannel.SendMessage(pubSubType, message);
        });

        // Subscribe to PubSub messages
        var consumer = Task.Run(async () =>
        {
            while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
            {
                var msg = await PubSubChannel.ch.Reader.ReadAsync();
                if (!_messages.Contains(msg.Message) && !_errors.Contains(msg.Message))
                {
                    switch (msg.MessageType)
                    {
                        case PubSubMessageType.Info:
                            _messages.Add(msg.Message);
                            Snackbar.Add(msg.Message, Severity.Info);
                            break;
                        case PubSubMessageType.Warning:
                            _warnings.Add(msg.Message);
                            Snackbar.Add(msg.Message, Severity.Warning);
                            break;
                        case PubSubMessageType.Error:
                            _errors.Add(msg.Message);
                            Snackbar.Add(msg.Message, Severity.Error);
                            break;
                    }

                    await InvokeAsync(StateHasChanged);
                }
            }
        });
    }

    private string GetWorkingDirectoryTitle()
    {
        return _state.GetWorkingDirectoryTitle();
    }

    private string GetOutputPath()
    {
        return _state.GetOutputPath();
    }

    private string GetDebugPath()
    {
        return _state.GetDebugPath();
    }

    private bool CanGenerate()
    {
        return _state.CanGenerate();
    }

    private async Task SelectHeightmap()
    {
        string? selectedPath = null;
        var staThread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog();
            dialog.Filter = "PNG Images (*.png)|*.png|All Files (*.*)|*.*";
            dialog.Title = "Select Heightmap (16-bit grayscale PNG)";
            if (dialog.ShowDialog() == DialogResult.OK) selectedPath = dialog.FileName;
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (!string.IsNullOrEmpty(selectedPath))
        {
            _heightmapPath = selectedPath;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SelectGeoTiffFile()
    {
        string? selectedPath = null;
        var staThread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog();
            dialog.Filter = "GeoTIFF Files (*.tif;*.tiff;*.geotiff)|*.tif;*.tiff;*.geotiff|All Files (*.*)|*.*";
            dialog.Title = "Select GeoTIFF Elevation File";
            if (dialog.ShowDialog() == DialogResult.OK) selectedPath = dialog.FileName;
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (!string.IsNullOrEmpty(selectedPath))
        {
            _geoTiffPath = selectedPath;
            // Clear directory selection when file is selected
            _geoTiffDirectory = null;
            // Clear previous geo metadata
            ClearGeoMetadata();

            // Read GeoTIFF metadata immediately to get bounding box for OSM
            await ReadGeoTiffMetadata();

            // Force refresh of the drop container to pass updated GeoBoundingBox to child components
            _dropContainer?.Refresh();

            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SelectGeoTiffDirectory()
    {
        string? selectedPath = null;
        var staThread = new Thread(() =>
        {
            using var dialog = new FolderBrowserDialog();
            dialog.Description = "Select folder containing GeoTIFF tiles";
            dialog.UseDescriptionForTitle = true;
            if (dialog.ShowDialog() == DialogResult.OK) selectedPath = dialog.SelectedPath;
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (!string.IsNullOrEmpty(selectedPath))
        {
            _geoTiffDirectory = selectedPath;
            // Clear file selection when directory is selected
            _geoTiffPath = null;
            // Clear previous geo metadata
            ClearGeoMetadata();

            // Read GeoTIFF metadata immediately to get bounding box for OSM
            await ReadGeoTiffMetadata();

            // Force refresh of the drop container to pass updated GeoBoundingBox to child components
            _dropContainer?.Refresh();

            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    ///     Reads GeoTIFF metadata (bounding box, elevation range) without fully processing the file.
    ///     This enables OSM feature selection before terrain generation.
    ///     Also validates the GeoTIFF and sets OSM availability flags.
    /// </summary>
    private async Task ReadGeoTiffMetadata()
    {
        // Reset OSM availability
        _canFetchOsmData = false;
        _osmBlockedReason = null;
        _geoTiffValidationResult = null;

        try
        {
            GeoTiffMetadataService.GeoTiffMetadataResult? result = null;

            if (!string.IsNullOrEmpty(_geoTiffPath) && File.Exists(_geoTiffPath))
                result = await _geoTiffService.ReadFromFileAsync(_geoTiffPath);
            else if (!string.IsNullOrEmpty(_geoTiffDirectory) && Directory.Exists(_geoTiffDirectory))
                result = await _geoTiffService.ReadFromDirectoryAsync(_geoTiffDirectory);

            if (result == null) return;

            // Apply result to state
            _geoBoundingBox = result.Wgs84BoundingBox;
            _geoTiffNativeBoundingBox = result.NativeBoundingBox;
            _geoTiffProjectionName = result.ProjectionName;
            _geoTiffProjectionWkt = result.ProjectionWkt;
            _geoTiffGeoTransform = result.GeoTransform;
            _geoTiffOriginalWidth = result.OriginalWidth;
            _geoTiffOriginalHeight = result.OriginalHeight;
            _geoTiffMinElevation = result.MinElevation;
            _geoTiffMaxElevation = result.MaxElevation;
            _canFetchOsmData = result.CanFetchOsmData;
            _osmBlockedReason = result.OsmBlockedReason;
            _geoTiffValidationResult = result.ValidationResult;

            // Only update terrain size from GeoTIFF if no existing terrain.json was loaded
            if (result.SuggestedTerrainSize.HasValue && !_hasExistingTerrainSettings)
                _terrainSize = result.SuggestedTerrainSize.Value;
            else if (result.SuggestedTerrainSize.HasValue && _hasExistingTerrainSettings)
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Keeping terrain size {_terrainSize} from terrain.json (GeoTIFF suggests {result.SuggestedTerrainSize.Value})");

            // Auto-populate maxHeight and terrainBaseHeight from GeoTIFF elevation data
            if (_geoTiffMinElevation.HasValue && _geoTiffMaxElevation.HasValue)
            {
                var elevationRange = _geoTiffMaxElevation.Value - _geoTiffMinElevation.Value;
                _maxHeight = (float)elevationRange;
                _terrainBaseHeight = (float)_geoTiffMinElevation.Value;

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Auto-calculated: Max Height = {_maxHeight:F1}m, Base Height = {_terrainBaseHeight:F1}m");
            }

            // Log scale information
            LogScaleInformation(result);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not read GeoTIFF metadata: {ex.Message}. OSM features will not be available until terrain generation.");
        }
    }

    private void LogScaleInformation(GeoTiffMetadataService.GeoTiffMetadataResult result)
    {
        if (_geoBoundingBox == null || !result.SuggestedTerrainSize.HasValue || _geoTiffGeoTransform == null)
            return;

        var nativePixelSizeX = Math.Abs(_geoTiffGeoTransform[1]);
        var nativePixelSizeY = Math.Abs(_geoTiffGeoTransform[5]);
        var avgNativePixelSize = (nativePixelSizeX + nativePixelSizeY) / 2.0;

        var originalSize = Math.Max(_geoTiffOriginalWidth, _geoTiffOriginalHeight);
        var scaleFactor = (double)originalSize / result.SuggestedTerrainSize.Value;
        var suggestedMpp = (float)(avgNativePixelSize * scaleFactor);

        if (Math.Abs(_metersPerPixel - 1.0f) < 0.01f && suggestedMpp > 1.5f)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "⚠️ Geographic scale mismatch detected!");
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"   Source DEM covers ~{suggestedMpp * result.SuggestedTerrainSize.Value / 1000:F1}km but terrain is {result.SuggestedTerrainSize.Value}px");
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"   Suggested: Set 'Meters per Pixel' to {suggestedMpp:F1} for real-world scale");
        }
        else
        {
            var totalSizeKm = _metersPerPixel * result.SuggestedTerrainSize.Value / 1000f;
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Terrain scale: {_metersPerPixel:F1}m/px = {totalSizeKm:F1}km × {totalSizeKm:F1}km in-game");
        }
    }

    private void ClearGeoMetadata()
    {
        _state.ClearGeoMetadata();
    }

    private void CleanupCachedCombinedGeoTiff()
    {
        _state.CleanupCachedCombinedGeoTiff();
    }

    private void OnHeightmapSourceTypeChanged(HeightmapSourceType newType)
    {
        _heightmapSourceType = newType;
        StateHasChanged();
    }

    private async Task OnCropTargetSizeChanged(int newSize)
    {
        _terrainSize = newSize;
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnCropAnchorChanged(CropAnchor newAnchor)
    {
        _cropAnchor = newAnchor;
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnCropResultChanged(CropResult result)
    {
        _cropResult = result;

        // Update the effective bounding box if cropping is needed
        if (result.NeedsCropping && result.CroppedBoundingBox is not null)
        {
            // Store the cropped bounding box for OSM queries
            // The original _geoBoundingBox is preserved for reference
            // Note: Don't send UI messages for every drag movement - too noisy

            // CRITICAL: Recalculate elevation range for the cropped region
            // The maxHeight and baseHeight must reflect the cropped area, not the full image
            // This will call StateHasChanged internally when done
            await RecalculateCroppedElevation(result);
        }
        else if (!result.NeedsCropping && _geoTiffMinElevation.HasValue && _geoTiffMaxElevation.HasValue)
        {
            // No cropping needed - use the full image elevation values
            var elevationRange = _geoTiffMaxElevation.Value - _geoTiffMinElevation.Value;
            _maxHeight = (float)elevationRange;
            _terrainBaseHeight = (float)_geoTiffMinElevation.Value;

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Using full image elevation: Max Height = {_maxHeight:F1}m, Base Height = {_terrainBaseHeight:F1}m");

            await InvokeAsync(StateHasChanged);
        }
        else
        {
            // Just refresh UI for other changes
            await InvokeAsync(StateHasChanged);
        }
        
        // IMPORTANT: Refresh the drop container to ensure child TerrainMaterialSettings 
        // components receive the updated EffectiveBoundingBox for OSM queries
        _dropContainer?.Refresh();
    }

    /// <summary>
    ///     Recalculates the elevation range for a cropped region of the GeoTIFF.
    /// </summary>
    private async Task RecalculateCroppedElevation(CropResult cropResult)
    {
        if (_heightmapSourceType != HeightmapSourceType.GeoTiffFile &&
            _heightmapSourceType != HeightmapSourceType.GeoTiffDirectory)
            return;

        try
        {
            string? geoTiffPathToRead = null;

            if (_heightmapSourceType == HeightmapSourceType.GeoTiffFile)
            {
                if (string.IsNullOrEmpty(_geoTiffPath) || !File.Exists(_geoTiffPath))
                    return;
                geoTiffPathToRead = _geoTiffPath;
            }
            else if (_heightmapSourceType == HeightmapSourceType.GeoTiffDirectory)
            {
                if (string.IsNullOrEmpty(_geoTiffDirectory) || !Directory.Exists(_geoTiffDirectory))
                    return;

                // Use cached combined file to avoid re-combining on every crop change
                if (string.IsNullOrEmpty(_cachedCombinedGeoTiffPath) || !File.Exists(_cachedCombinedGeoTiffPath))
                    _cachedCombinedGeoTiffPath = await _geoTiffService.CombineGeoTiffTilesAsync(_geoTiffDirectory);
                geoTiffPathToRead = _cachedCombinedGeoTiffPath;
            }

            if (string.IsNullOrEmpty(geoTiffPathToRead) || !File.Exists(geoTiffPathToRead))
                return;

            var (croppedMin, croppedMax) = await _geoTiffService.GetCroppedElevationRangeAsync(
                geoTiffPathToRead,
                cropResult.OffsetX,
                cropResult.OffsetY,
                cropResult.CropWidth,
                cropResult.CropHeight);

            if (croppedMin.HasValue && croppedMax.HasValue)
            {
                cropResult.CroppedMinElevation = croppedMin.Value;
                cropResult.CroppedMaxElevation = croppedMax.Value;

                var elevationRange = croppedMax.Value - croppedMin.Value;
                _maxHeight = (float)elevationRange;
                _terrainBaseHeight = (float)croppedMin.Value;

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Cropped elevation: {croppedMin:F1}m to {croppedMax:F1}m (Max Height = {_maxHeight:F1}m, Base = {_terrainBaseHeight:F1}m)");
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Could not read cropped elevation. Keeping current values.");
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not recalculate cropped elevation: {ex.Message}. Using full image values.");
        }
    }

    private string GetHeightmapSourceDescription()
    {
        return _state.GetHeightmapSourceDescription();
    }

    private string GetMetersPerPixelHelperText()
    {
        return _state.GetMetersPerPixelHelperText();
    }

    private double GetNativePixelSizeX()
    {
        return _geoTiffGeoTransform != null ? Math.Abs(_geoTiffGeoTransform[1]) : 0;
    }

    private double GetNativePixelSizeY()
    {
        return _geoTiffGeoTransform != null ? Math.Abs(_geoTiffGeoTransform[5]) : 0;
    }

    private bool IsGeographicCrs()
    {
        return _geoTiffGeoTransform != null && Math.Abs(_geoTiffGeoTransform[1]) < 0.1;
    }

    private string GetNativePixelSizeDescription()
    {
        return _geoTiffService.GetNativePixelSizeDescription(_geoTiffGeoTransform, _geoBoundingBox);
    }

    private double GetRealWorldWidthKm()
    {
        return _geoTiffService.GetRealWorldWidthKm(_geoTiffGeoTransform, _geoTiffOriginalWidth, _geoBoundingBox);
    }

    private double GetRealWorldHeightKm()
    {
        return _geoTiffService.GetRealWorldHeightKm(_geoTiffGeoTransform, _geoTiffOriginalHeight, _geoBoundingBox);
    }

    private float GetNativePixelSizeAverage()
    {
        return _geoTiffService.GetNativePixelSizeAverage(_geoTiffGeoTransform, _geoBoundingBox);
    }

    private async void OnPresetImported(TerrainPresetResult result)
    {
        // Apply imported settings to the page
        if (!string.IsNullOrEmpty(result.TerrainName))
            _terrainName = result.TerrainName;

        if (result.MaxHeight.HasValue)
            _maxHeight = result.MaxHeight.Value;

        if (result.MetersPerPixel.HasValue)
            _metersPerPixel = result.MetersPerPixel.Value;

        if (result.TerrainBaseHeight.HasValue)
            _terrainBaseHeight = result.TerrainBaseHeight.Value;

        if (!string.IsNullOrEmpty(result.HeightmapPath))
            _heightmapPath = result.HeightmapPath;

        // ========== NEW: Apply enhanced preset settings ==========

        // Apply heightmap source type
        if (result.HeightmapSourceType.HasValue) _heightmapSourceType = result.HeightmapSourceType.Value;

        // Apply GeoTIFF paths and trigger metadata read
        if (!string.IsNullOrEmpty(result.GeoTiffPath))
        {
            _geoTiffPath = result.GeoTiffPath;
            if (File.Exists(result.GeoTiffPath))
                // Read GeoTIFF metadata to restore bounding box and geo info
                await ReadGeoTiffMetadata();
        }

        if (!string.IsNullOrEmpty(result.GeoTiffDirectory))
        {
            _geoTiffDirectory = result.GeoTiffDirectory;
            if (Directory.Exists(result.GeoTiffDirectory)) await ReadGeoTiffMetadata();
        }

        // Apply terrain size (from preset)
        if (result.TerrainSize.HasValue)
            _terrainSize = result.TerrainSize.Value;

        // Apply terrain generation options
        if (result.UpdateTerrainBlock.HasValue)
            _updateTerrainBlock = result.UpdateTerrainBlock.Value;

        if (result.EnableCrossMaterialHarmonization.HasValue)
            _enableCrossMaterialHarmonization = result.EnableCrossMaterialHarmonization.Value;

        // Apply crop settings (for GeoTIFF mode)
        // Note: The CropAnchorSelector component will need to be notified of these changes
        if (result.CropOffsetX.HasValue && result.CropOffsetY.HasValue &&
            result.CropWidth.HasValue && result.CropHeight.HasValue)
        {
            // Create a CropResult from the imported settings
            _cropResult = new CropResult
            {
                OffsetX = result.CropOffsetX.Value,
                OffsetY = result.CropOffsetY.Value,
                CropWidth = result.CropWidth.Value,
                CropHeight = result.CropHeight.Value,
                TargetSize = _terrainSize,
                NeedsCropping = result.CropWidth.Value > 0 && result.CropHeight.Value > 0,
                CroppedBoundingBox = null, // Will be recalculated when GeoTIFF is loaded
                Anchor = _cropAnchor
            };

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Restored crop settings: offset ({result.CropOffsetX}, {result.CropOffsetY}), " +
                $"size {result.CropWidth}x{result.CropHeight}");
        }

        // Apply GeoTIFF metadata (informational - actual values come from metadata read)
        if (result.GeoTiffOriginalWidth.HasValue)
            _geoTiffOriginalWidth = result.GeoTiffOriginalWidth.Value;
        if (result.GeoTiffOriginalHeight.HasValue)
            _geoTiffOriginalHeight = result.GeoTiffOriginalHeight.Value;
        if (!string.IsNullOrEmpty(result.GeoTiffProjectionName))
            _geoTiffProjectionName = result.GeoTiffProjectionName;

        // CRITICAL: Renormalize order values to be contiguous (0, 1, 2, 3...)
        // The preset import may have set non-contiguous order values
        RenormalizeMaterialOrder();

        // Refresh the drop container to reflect the new order in the UI
        _dropContainer?.Refresh();
        StateHasChanged();
    }

    private async Task OnWorkingDirectorySelected(string folder)
    {
        _isLoading = true;
        _state.ClearMessages();
        _terrainMaterials.Clear();

        StateHasChanged();

        await Task.Run(() =>
        {
            var result = _materialService.LoadLevelFromFolder(folder);

            if (!result.Success)
            {
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    PubSubChannel.SendMessage(PubSubMessageType.Error, result.ErrorMessage);
                return;
            }

            _workingDirectory = result.LevelPath;
            _hasWorkingDirectory = true;
            _levelName = result.LevelName;

            // Apply loaded materials
            _terrainMaterials.Clear();
            _terrainMaterials.AddRange(result.Materials);

            // Apply existing terrain settings if found
            if (result.ExistingTerrainSize.HasValue)
            {
                _terrainSize = result.ExistingTerrainSize.Value;
                _hasExistingTerrainSettings = true;
            }

            if (!string.IsNullOrEmpty(result.TerrainName))
                _terrainName = result.TerrainName;
            if (result.MetersPerPixel.HasValue)
                _metersPerPixel = result.MetersPerPixel.Value;
        });

        _isLoading = false;

        if (_hasWorkingDirectory && FileSelect?.Panels.Count > 0)
            await FileSelect.Panels[0].CollapseAsync();

        StateHasChanged();
    }

    private void ScanTerrainMaterials(string levelPath)
    {
        var materials = _materialService.ScanTerrainMaterials(levelPath);
        _terrainMaterials.Clear();
        _terrainMaterials.AddRange(materials);
    }

    private void ToggleRoadSmoothing(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        material.IsRoadMaterial = !material.IsRoadMaterial;
        _dropContainer?.Refresh();
        StateHasChanged();
    }

    private void OnMaterialDropped(MudItemDropInfo<TerrainMaterialSettings.TerrainMaterialItemExtended> dropItem)
    {
        if (dropItem.Item == null) return;

        dropItem.Item.Selector = dropItem.DropzoneIdentifier;
        _terrainMaterials.UpdateOrder(dropItem, item => item.Order);

        // CRITICAL: Renormalize order values to be contiguous (0, 1, 2, 3...)
        // This ensures material indices in the .ter file are correct
        RenormalizeMaterialOrder();
    }

    private void RenormalizeMaterialOrder()
    {
        _materialService.RenormalizeMaterialOrder(_terrainMaterials);
    }

    private bool ReorderMaterialsWithoutLayerMapsToEnd()
    {
        return _materialService.ReorderMaterialsWithoutLayerMapsToEnd(_terrainMaterials);
    }

    private bool HandleMaterialReorderRequest()
    {
        var wasReordered = ReorderMaterialsWithoutLayerMapsToEnd();
        if (wasReordered)
        {
            _dropContainer?.Refresh();
            StateHasChanged();
        }

        return wasReordered;
    }

    private void MoveToTop(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        _materialService.MoveToTop(_terrainMaterials, material);
        _dropContainer?.Refresh();
        StateHasChanged();
    }

    private void MoveToBottom(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        _materialService.MoveToBottom(_terrainMaterials, material);
        _dropContainer?.Refresh();
        StateHasChanged();
    }

    private void OnMaterialSettingsChanged(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        StateHasChanged();
    }

    private async Task ExecuteTerrainGeneration()
    {
        if (!CanGenerate()) return;

        // Reorder materials: move those without layer maps (except index 0) to end
        if (ReorderMaterialsWithoutLayerMapsToEnd())
        {
            var movedCount = _terrainMaterials.Skip(1).Count(m => !m.HasLayerMap);
            Snackbar.Add(
                $"Reordered {movedCount} material(s) without layer maps to end of list. " +
                "Materials without layers at positions > 0 cannot claim pixels.",
                Severity.Info);
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Moved {movedCount} material(s) without layer maps to end of list for correct terrain generation.");
            _dropContainer?.Refresh();
            StateHasChanged();
        }

        _isGenerating = true;
        StateHasChanged();

        try
        {
            // Execute terrain generation via orchestrator
            var result = await _generationOrchestrator.ExecuteAsync(_state);

            if (result.Success)
            {
                Snackbar.Add($"Terrain generated successfully: {GetOutputPath()}", Severity.Success);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain file saved to: {GetOutputPath()}");

                // Run post-generation tasks
                await _generationOrchestrator.RunPostGenerationTasksAsync(_state, result.Parameters);
                Snackbar.Add("Post-processing complete!", Severity.Success);

                // Write log files
                _generationOrchestrator.WriteGenerationLogs(_state);
            }
            else
            {
                Snackbar.Add("Terrain generation failed. Check errors for details.", Severity.Error);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    ShowException(new Exception(result.ErrorMessage));
            }
        }
        catch (Exception ex)
        {
            ShowException(ex);
            Snackbar.Add($"Error generating terrain: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isGenerating = false;
            StateHasChanged();
        }
    }

    private async Task ResetPage()
    {
        _state.Reset();
        _presetImporter?.Reset();
        _presetExporter?.Reset();

        if (FileSelect?.Panels.Count > 0) await FileSelect.Panels[0].ExpandAsync();

        StateHasChanged();
        Snackbar.Add("Page reset. You can now select a different folder.", Severity.Info);
    }

    private void OpenWorkingDirectory()
    {
        if (!string.IsNullOrEmpty(_workingDirectory)) Process.Start("explorer.exe", _workingDirectory);
    }

    private void ShowException(Exception ex)
    {
        var message = ex.InnerException != null ? ex.Message + $" {ex.InnerException}" : ex.Message;
        Snackbar.Add(message, Severity.Error);
        _errors.Add(message);
    }

    private void OpenDrawer(Anchor anchor, PubSubMessageType msgType)
        {
            _showErrorLog = msgType == PubSubMessageType.Error;
            _showWarningLog = msgType == PubSubMessageType.Warning;
            _openDrawer = true;
            _anchor = anchor;

            switch (anchor)
            {
                case Anchor.Bottom:
                    _drawerWidth = "100%";
                    _drawerHeight = "200px";
                    break;
                default:
                    _drawerWidth = "400px";
                    _drawerHeight = "100%";
                    break;
            }
        }
    }