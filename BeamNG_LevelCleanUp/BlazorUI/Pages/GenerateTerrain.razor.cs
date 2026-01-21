using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.BlazorUI.Services;
using BeamNG_LevelCleanUp.BlazorUI.State;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Utilities;
using DialogResult = System.Windows.Forms.DialogResult;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class GenerateTerrain
{
    // Analysis dialog options for fullscreen display
    private static readonly DialogOptions AnalysisDialogOptions = new()
    {
        FullScreen = true,
        CloseButton = true,
        BackdropClick = false,
        CloseOnEscapeKey = true
    };

    private readonly TerrainAnalysisOrchestrator _analysisOrchestrator = new();
    private readonly TerrainAnalysisState _analysisState = new();
    private readonly TerrainGenerationOrchestrator _generationOrchestrator = new();

    // ========================================
    // SERVICES
    // ========================================
    private readonly GeoTiffMetadataService _geoTiffService = new();
    private readonly TerrainMaterialService _materialService = new();

    // ========================================
    // STATE (delegates to TerrainGenerationState)
    // ========================================
    private readonly TerrainGenerationState _state = new();

    // ========================================
    // UI-ONLY STATE (not in TerrainGenerationState)
    // ========================================
    private Anchor _anchor;
    private CropAnchorSelector? _cropAnchorSelector;
    private string _drawerHeight = "200px";
    private string _drawerWidth = "100%";
    private MudDropContainer<TerrainMaterialSettings.TerrainMaterialItemExtended> _dropContainer = null!;

    // Analysis state
    private bool _isAnalyzing;
    private bool _openDrawer;

    // Pending crop settings from preset import (applied after GeoTIFF metadata is loaded)
    private (int offsetX, int offsetY)? _pendingCropOffsets;
    private TerrainPresetExporter? _presetExporter;
    private TerrainPresetImporter? _presetImporter;
    private bool _showErrorLog;

    private bool _showWarningLog;
    // ========================================
    // WIZARD MODE PROPERTIES
    // ========================================

    [Parameter]
    [SupplyParameterFromQuery(Name = "wizardMode")]
    public bool WizardMode { get; set; }

    /// <summary>
    ///     Wizard state reference when in wizard mode
    /// </summary>
    public CreateLevelWizardState? WizardState { get; private set; }

    /// <summary>
    ///     Indicates if terrain has been generated during this wizard session
    /// </summary>
    private bool _terrainGeneratedInWizard { get; set; }

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

    private bool _enableCrossroadToTJunctionConversion
    {
        get => _state.EnableCrossroadToTJunctionConversion;
        set => _state.EnableCrossroadToTJunctionConversion = value;
    }

    private bool _enableExtendedOsmJunctionDetection
    {
        get => _state.EnableExtendedOsmJunctionDetection;
        set => _state.EnableExtendedOsmJunctionDetection = value;
    }

    private float _globalJunctionDetectionRadiusMeters
    {
        get => _state.GlobalJunctionDetectionRadiusMeters;
        set => _state.GlobalJunctionDetectionRadiusMeters = value;
    }

    private float _globalJunctionBlendDistanceMeters
    {
        get => _state.GlobalJunctionBlendDistanceMeters;
        set => _state.GlobalJunctionBlendDistanceMeters = value;
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

    private bool _flipMaterialProcessingOrder
    {
        get => _state.FlipMaterialProcessingOrder;
        set => _state.FlipMaterialProcessingOrder = value;
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

    // ========================================
    // WIZARD MODE LIFECYCLE METHODS
    // ========================================

    protected override async Task OnParametersSetAsync()
    {
        if (WizardMode)
        {
            WizardState = CreateLevel.GetWizardState();
            if (WizardState == null || !WizardState.IsActive)
            {
                // Invalid wizard state - redirect to CreateLevel
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Wizard state not found. Please start the wizard from Create Level page.");
                Navigation.NavigateTo("/CreateLevel");
                return;
            }

            // Set the current wizard step
            WizardState.CurrentStep = 5;

            // Auto-load level from wizard state
            await LoadLevelFromWizardState();
        }
    }

    /// <summary>
    ///     Loads the target level from wizard state for terrain generation
    /// </summary>
    private async Task LoadLevelFromWizardState()
    {
        if (WizardState == null) return;

        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Wizard mode: Loading level for terrain generation...");

            var targetLevelRootPath = WizardState.TargetLevelRootPath;

            if (string.IsNullOrEmpty(targetLevelRootPath) || !Directory.Exists(targetLevelRootPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Target level path not found: {targetLevelRootPath}");
                return;
            }

            // Use the material service to load the level
            await Task.Run(() =>
            {
                var result = _materialService.LoadLevelFromFolder(targetLevelRootPath);

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
                else if (WizardState.TerrainSize > 0)
                {
                    // Use terrain size from wizard state
                    _terrainSize = WizardState.TerrainSize;
                }

                if (!string.IsNullOrEmpty(result.TerrainName))
                    _terrainName = result.TerrainName;
                if (result.MetersPerPixel.HasValue)
                    _metersPerPixel = result.MetersPerPixel.Value;
            });

            await InvokeAsync(StateHasChanged);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Wizard mode: Level loaded successfully - {_terrainMaterials.Count} terrain materials found");
        }
        catch (Exception ex)
        {
            ShowException(ex);
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to load level in wizard mode: {ex.Message}");
        }
    }

    // ========================================
    // WIZARD FOOTER HELPER METHODS
    // ========================================

    private string GetBackButtonText()
    {
        return WizardMode ? "Back to Assets" : "";
    }

    private string GetNextButtonText()
    {
        return "";
        // Not used - this is the last step
    }

    private bool GetCanProceed()
    {
        return WizardMode && _terrainGeneratedInWizard &&
               WizardState?.TerrainCompletionDialogShown == true;
    }

    private bool GetShowFinishButton()
    {
        return WizardMode && _terrainGeneratedInWizard &&
               WizardState?.TerrainCompletionDialogShown == true;
    }

    private bool GetShowSkipButton()
    {
        return WizardMode && !_terrainGeneratedInWizard;
    }

    private void OnBackClicked()
    {
        if (WizardMode)
        {
            // Set step back to 4 (assets) before navigating
            if (WizardState != null) WizardState.CurrentStep = 4;

            Navigation.NavigateTo("/CopyAssets?wizardMode=true");
        }
    }

    private void SkipStep()
    {
        if (WizardState != null)
            // Mark as skipped (not generated) and finish wizard
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Terrain generation skipped. You can generate terrain later from this page.");
        Navigation.NavigateTo("/CreateLevel");
    }

    private void FinishWizard()
    {
        if (WizardState != null) WizardState.Step6_TerrainGenerated = true;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Create Level Wizard completed! Your level '{WizardState?.LevelName}' is ready.");

        Navigation.NavigateTo("/CreateLevel");
    }

    /// <summary>
    ///     Updates wizard state after successful terrain generation
    /// </summary>
    private void UpdateWizardStateAfterGeneration()
    {
        if (WizardState == null) return;

        WizardState.GeneratedTerrainPath = GetOutputPath();
        WizardState.Step6_TerrainGenerated = true;
        _terrainGeneratedInWizard = true;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Terrain generated for {WizardState.LevelName}");
    }

    /// <summary>
    ///     Shows the wizard completion dialog with next-steps instructions
    /// </summary>
    private async Task ShowTerrainWizardCompletionDialog()
    {
        var options = new DialogOptions
        {
            CloseButton = false,
            CloseOnEscapeKey = false,
            BackdropClick = false,
            MaxWidth = MaxWidth.Medium
        };

        var parameters = new DialogParameters<TerrainWizardCompletionDialog>
        {
            { x => x.TerrainFilePath, GetOutputPath() }
        };

        var dialog = await DialogService.ShowAsync<TerrainWizardCompletionDialog>(
            "Terrain Generation Complete",
            parameters,
            options);

        var result = await dialog.Result;

        // When dialog is closed, mark it as shown and trigger UI update
        if (WizardState != null) WizardState.TerrainCompletionDialogShown = true;

        await InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        // Configure snackbar to prevent duplicate key issues when messages arrive rapidly
        Snackbar.Configuration.PreventDuplicates = true;
        Snackbar.Configuration.MaxDisplayedSnackbars = 10;

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
                            break;
                        case PubSubMessageType.Warning:
                            _warnings.Add(msg.Message);
                            break;
                        case PubSubMessageType.Error:
                            _errors.Add(msg.Message);
                            break;
                    }

                    // Show snackbar and update UI on the main thread
                    await InvokeAsync(() =>
                    {
                        switch (msg.MessageType)
                        {
                            case PubSubMessageType.Info:
                                Snackbar.Add(msg.Message, Severity.Info);
                                break;
                            case PubSubMessageType.Warning:
                                Snackbar.Add(msg.Message, Severity.Warning);
                                break;
                            case PubSubMessageType.Error:
                                Snackbar.Add(msg.Message, Severity.Error);
                                break;
                        }
                    });
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

        // ========== Apply enhanced preset settings ==========

        // Apply heightmap source type
        if (result.HeightmapSourceType.HasValue)
            _heightmapSourceType = result.HeightmapSourceType.Value;

        // Apply terrain size BEFORE reading GeoTIFF (needed for crop calculations)
        if (result.TerrainSize.HasValue)
            _terrainSize = result.TerrainSize.Value;

        // Store pending crop offsets (will be applied after GeoTIFF metadata is loaded)
        // CRITICAL: We need to store these BEFORE calling ReadGeoTiffMetadata because
        // the CropAnchorSelector component will recenter when it receives new GeoTIFF dimensions
        if (result.CropOffsetX.HasValue && result.CropOffsetY.HasValue &&
            result.CropWidth.HasValue && result.CropHeight.HasValue &&
            result.CropWidth.Value > 0 && result.CropHeight.Value > 0)
        {
            _pendingCropOffsets = (result.CropOffsetX.Value, result.CropOffsetY.Value);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Preset contains crop settings: offset ({result.CropOffsetX}, {result.CropOffsetY}), " +
                $"size {result.CropWidth}x{result.CropHeight} - will apply after GeoTIFF loads");
        }
        else
        {
            _pendingCropOffsets = null;
        }

        // Apply GeoTIFF paths and trigger metadata read
        // The GeoTIFF must be re-loaded to populate dimensions and bounding box
        var geoTiffLoaded = false;

        if (result.HeightmapSourceType == HeightmapSourceType.GeoTiffFile &&
            !string.IsNullOrEmpty(result.GeoTiffPath))
        {
            _geoTiffPath = result.GeoTiffPath;
            _geoTiffDirectory = null; // Clear the other source

            if (File.Exists(result.GeoTiffPath))
            {
                // Read GeoTIFF metadata to restore bounding box and geo info
                await ReadGeoTiffMetadata();
                geoTiffLoaded = true;
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"GeoTIFF file not found: {result.GeoTiffPath}. Please browse to select the file.");
            }
        }
        else if (result.HeightmapSourceType == HeightmapSourceType.GeoTiffDirectory &&
                 !string.IsNullOrEmpty(result.GeoTiffDirectory))
        {
            _geoTiffDirectory = result.GeoTiffDirectory;
            _geoTiffPath = null; // Clear the other source

            if (Directory.Exists(result.GeoTiffDirectory))
            {
                await ReadGeoTiffMetadata();
                geoTiffLoaded = true;
            }
            else
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"GeoTIFF directory not found: {result.GeoTiffDirectory}. Please browse to select the folder.");
            }
        }

        // Apply terrain generation options
        if (result.UpdateTerrainBlock.HasValue)
            _updateTerrainBlock = result.UpdateTerrainBlock.Value;

        if (result.EnableCrossMaterialHarmonization.HasValue)
            _enableCrossMaterialHarmonization = result.EnableCrossMaterialHarmonization.Value;

        if (result.EnableCrossroadToTJunctionConversion.HasValue)
            _enableCrossroadToTJunctionConversion = result.EnableCrossroadToTJunctionConversion.Value;

        if (result.EnableExtendedOsmJunctionDetection.HasValue)
            _enableExtendedOsmJunctionDetection = result.EnableExtendedOsmJunctionDetection.Value;

        if (result.GlobalJunctionDetectionRadiusMeters.HasValue)
            _globalJunctionDetectionRadiusMeters = result.GlobalJunctionDetectionRadiusMeters.Value;

        if (result.GlobalJunctionBlendDistanceMeters.HasValue)
            _globalJunctionBlendDistanceMeters = result.GlobalJunctionBlendDistanceMeters.Value;

        // Apply GeoTIFF metadata from preset (as fallback if GeoTIFF couldn't be loaded)
        if (!geoTiffLoaded)
        {
            if (result.GeoTiffOriginalWidth.HasValue)
                _geoTiffOriginalWidth = result.GeoTiffOriginalWidth.Value;
            if (result.GeoTiffOriginalHeight.HasValue)
                _geoTiffOriginalHeight = result.GeoTiffOriginalHeight.Value;
            if (!string.IsNullOrEmpty(result.GeoTiffProjectionName))
                _geoTiffProjectionName = result.GeoTiffProjectionName;
        }

        // CRITICAL: Renormalize order values to be contiguous (0, 1, 2, 3...)
        // The preset import may have set non-contiguous order values
        RenormalizeMaterialOrder();

        // Refresh the drop container to reflect the new order in the UI
        _dropContainer?.Refresh();

        // Trigger UI refresh
        await InvokeAsync(StateHasChanged);

        // If GeoTIFF was loaded and we have pending crop offsets, apply them now
        // We need to wait for the UI to render the CropAnchorSelector first
        if (geoTiffLoaded && _pendingCropOffsets.HasValue)
        {
            // Use a small delay to ensure the CropAnchorSelector component is rendered
            await Task.Delay(100);
            await ApplyPendingCropOffsets();
        }
    }

    /// <summary>
    ///     Applies pending crop offsets that were stored during preset import.
    ///     This should be called after the CropAnchorSelector component is rendered.
    /// </summary>
    private async Task ApplyPendingCropOffsets()
    {
        if (!_pendingCropOffsets.HasValue)
            return;

        var (offsetX, offsetY) = _pendingCropOffsets.Value;
        _pendingCropOffsets = null;

        if (_cropAnchorSelector != null)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Applying restored crop offsets: ({offsetX}, {offsetY})");

            await _cropAnchorSelector.SetCropOffsetsAsync(offsetX, offsetY);
        }
        else
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                "Could not apply crop offsets - CropAnchorSelector component not available yet. " +
                "You may need to manually adjust the crop position.");
        }
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
        }

        _isGenerating = true;
        await InvokeAsync(StateHasChanged);

        // Yield to allow UI to render the loading state before starting heavy work
        await Task.Yield();

        try
        {
            // Execute terrain generation via orchestrator (runs on background thread)
            var result = await _generationOrchestrator.ExecuteAsync(_state).ConfigureAwait(false);

            if (result.Success)
            {
                await InvokeAsync(() =>
                {
                    Snackbar.Add($"Terrain generated successfully: {GetOutputPath()}", Severity.Success);
                });
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain file saved to: {GetOutputPath()}");

                // Run post-generation tasks
                await _generationOrchestrator.RunPostGenerationTasksAsync(_state, result.Parameters)
                    .ConfigureAwait(false);
                await InvokeAsync(() => { Snackbar.Add("Post-processing complete!", Severity.Success); });

                // Write log files
                _generationOrchestrator.WriteGenerationLogs(_state);

                // WIZARD MODE: Update state and show completion dialog
                if (WizardMode && WizardState != null)
                {
                    UpdateWizardStateAfterGeneration();
                    await InvokeAsync(async () => await ShowTerrainWizardCompletionDialog());
                }
            }
            else
            {
                await InvokeAsync(() =>
                {
                    Snackbar.Add("Terrain generation failed. Check errors for details.", Severity.Error);
                });
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    ShowException(new Exception(result.ErrorMessage));
            }
        }
        catch (Exception ex)
        {
            ShowException(ex);
            await InvokeAsync(() => { Snackbar.Add($"Error generating terrain: {ex.Message}", Severity.Error); });
        }
        finally
        {
            _isGenerating = false;
            await InvokeAsync(StateHasChanged);
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

    // ========================================
    // HELP DIALOGS
    // ========================================

    /// <summary>
    ///     Opens the Material Order Help dialog explaining texture painting and elevation priority.
    /// </summary>
    private async Task OpenMaterialOrderHelpDialog()
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        await DialogService.ShowAsync<TerrainMaterialOrderHelpDialog>(
            "Material Order & Priority Guide",
            options);
    }

    /// <summary>
    ///     Opens the Heightmap Source Help dialog explaining GeoTIFF sources and where to get elevation data.
    /// </summary>
    private async Task OpenHeightmapSourceHelpDialog()
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        await DialogService.ShowAsync<HeightmapSourceHelpDialog>(
            "Heightmap Sources Guide",
            options);
    }

    // ========================================
    // ANALYSIS METHODS
    // ========================================

    /// <summary>
    ///     Checks if terrain analysis can proceed.
    ///     Analysis requires at least one road material with road smoothing enabled.
    /// </summary>
    private bool CanAnalyze()
    {
        if (!CanGenerate())
            return false;

        // Must have at least one road material
        return _terrainMaterials.Any(m => m.IsRoadMaterial);
    }

    /// <summary>
    ///     Executes terrain analysis to preview splines and junctions before generation.
    /// </summary>
    private async Task ExecuteAnalysis()
    {
        if (!CanAnalyze()) return;

        _isAnalyzing = true;
        await InvokeAsync(StateHasChanged);

        // Yield to allow UI to render the loading state before starting heavy work
        await Task.Yield();

        try
        {
            // Clear previous analysis
            _analysisState.Reset();

            // Reorder materials if needed (same logic as generation)
            if (ReorderMaterialsWithoutLayerMapsToEnd()) _dropContainer?.Refresh();

            // Execute analysis via orchestrator (runs on background thread)
            var result = await _analysisOrchestrator.AnalyzeAsync(_state, _analysisState).ConfigureAwait(false);

            if (result.Success && result.AnalyzerResult != null)
            {
                await InvokeAsync(() =>
                {
                    Snackbar.Add(
                        $"Analysis complete: {_analysisState.SplineCount} splines, {_analysisState.TotalJunctionCount} junctions",
                        Severity.Success);
                });

                // Save debug image to disk
                if (_analysisState.DebugImageData != null)
                {
                    var debugImagePath = Path.Combine(_state.GetDebugPath(), "analysis_preview.png");
                    await _analysisOrchestrator.SaveDebugImageAsync(_analysisState, debugImagePath)
                        .ConfigureAwait(false);
                }

                // Show the fullscreen analysis dialog (must be on UI thread)
                await InvokeAsync(async () => await ShowAnalysisDialog());
            }
            else
            {
                await InvokeAsync(() => { Snackbar.Add(result.ErrorMessage ?? "Analysis failed", Severity.Error); });
            }
        }
        catch (Exception ex)
        {
            ShowException(ex);
            await InvokeAsync(() => { Snackbar.Add($"Error during analysis: {ex.Message}", Severity.Error); });
        }
        finally
        {
            _isAnalyzing = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    ///     Shows the fullscreen analysis dialog using IDialogService.
    /// </summary>
    private async Task ShowAnalysisDialog()
    {
        var parameters = new DialogParameters<TerrainAnalysisDialog>
        {
            { x => x.AnalysisState, _analysisState },
            { x => x.MetersPerPixel, _metersPerPixel }
        };

        var dialog = await DialogService.ShowAsync<TerrainAnalysisDialog>(
            "Terrain Analysis Results",
            parameters,
            AnalysisDialogOptions);

        var dialogResult = await dialog.Result;

        if (dialogResult == null || dialogResult.Canceled)
            // User cancelled - check if they wanted to clear analysis
            // The dialog returns Cancel for both Cancel and Clear buttons
            // We distinguish by checking if the dialog was explicitly closed vs cancelled
            return;

        // User clicked "Apply & Generate"
        await ApplyAnalysisAndGenerate();
    }

    /// <summary>
    ///     Applies the analysis results (including exclusions) and starts terrain generation.
    /// </summary>
    private async Task ApplyAnalysisAndGenerate()
    {
        // Apply all junction exclusions to the network
        _analysisState.ApplyExclusions();

        if (_analysisState.ExcludedCount > 0)
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Applied {_analysisState.ExcludedCount} junction exclusion(s) for terrain generation");

        // Execute terrain generation with the pre-analyzed network
        await ExecuteTerrainGenerationWithAnalysis();
    }

    /// <summary>
    ///     Executes terrain generation using the pre-analyzed road network.
    /// </summary>
    private async Task ExecuteTerrainGenerationWithAnalysis()
    {
        if (!CanGenerate()) return;

        _isGenerating = true;
        await InvokeAsync(StateHasChanged);

        // Yield to allow UI to render the loading state before starting heavy work
        await Task.Yield();

        try
        {
            // Execute terrain generation with pre-analyzed network (runs on background thread)
            var result = await _generationOrchestrator.ExecuteWithPreAnalyzedNetworkAsync(_state, _analysisState)
                .ConfigureAwait(false);

            if (result.Success)
            {
                await InvokeAsync(() =>
                {
                    Snackbar.Add($"Terrain generated successfully: {GetOutputPath()}", Severity.Success);
                });
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain file saved to: {GetOutputPath()}");

                // Run post-generation tasks
                await _generationOrchestrator.RunPostGenerationTasksAsync(_state, result.Parameters)
                    .ConfigureAwait(false);
                await InvokeAsync(() => { Snackbar.Add("Post-processing complete!", Severity.Success); });

                // Write log files
                _generationOrchestrator.WriteGenerationLogs(_state);

                // Clear analysis state after successful generation
                _analysisState.Reset();
            }
            else
            {
                await InvokeAsync(() =>
                {
                    Snackbar.Add("Terrain generation failed. Check errors for details.", Severity.Error);
                });
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    ShowException(new Exception(result.ErrorMessage));
            }
        }
        catch (Exception ex)
        {
            ShowException(ex);
            await InvokeAsync(() => { Snackbar.Add($"Error generating terrain: {ex.Message}", Severity.Error); });
        }
        finally
        {
            _isGenerating = false;
            await InvokeAsync(StateHasChanged);
        }
    }
}