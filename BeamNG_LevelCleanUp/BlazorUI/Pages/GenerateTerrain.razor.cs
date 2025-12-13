using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using BeamNgTerrainPoc.Terrain;
using BeamNgTerrainPoc.Terrain.GeoTiff;
using BeamNgTerrainPoc.Terrain.Logging;
using BeamNgTerrainPoc.Terrain.Models;
using BeamNgTerrainPoc.Terrain.Osm.Models;
using BeamNgTerrainPoc.Terrain.Osm.Processing;
using BeamNgTerrainPoc.Terrain.Osm.Services;
using MudBlazor;
using MudBlazor.Utilities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using DialogResult = System.Windows.Forms.DialogResult;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class GenerateTerrain
{
    private readonly List<string> _errors = new();
    private readonly List<string> _messages = new();
    private readonly List<TerrainMaterialSettings.TerrainMaterialItemExtended> _terrainMaterials = new();
    private readonly List<string> _warnings = new();
    private Anchor _anchor;
    private string _drawerHeight = "200px";
    private string _drawerWidth = "100%";
    private MudDropContainer<TerrainMaterialSettings.TerrainMaterialItemExtended> _dropContainer = null!;
    private bool _enableCrossMaterialHarmonization = true;
    private GeoBoundingBox? _geoBoundingBox;
    private string? _geoTiffDirectory;
    private double[]? _geoTiffGeoTransform;
    private double? _geoTiffMaxElevation;
    private double? _geoTiffMinElevation;
    private GeoBoundingBox? _geoTiffNativeBoundingBox;
    private int _geoTiffOriginalHeight;
    private int _geoTiffOriginalWidth;
    private string? _geoTiffPath;
    private string? _geoTiffProjectionName;
    private string? _geoTiffProjectionWkt;
    private bool _hasWorkingDirectory;
    private bool _hasExistingTerrainSettings; // True if terrain.json was found and loaded
    private string? _heightmapPath;

    // GeoTIFF import fields
    private HeightmapSourceType _heightmapSourceType = HeightmapSourceType.Png;

    // Crop settings for oversized GeoTIFFs
    private CropAnchor _cropAnchor = CropAnchor.Center;
    private CropResult? _cropResult;
    
    // Cached combined GeoTIFF path for directory mode (avoids re-combining on every crop change)
    private string? _cachedCombinedGeoTiffPath;
    
    private bool _isGenerating;
    private bool _isLoading;
    private string _levelName = string.Empty;
    private float _maxHeight;
    private float _metersPerPixel = 1.0f;
    private bool _openDrawer;
    private TerrainPresetExporter? _presetExporter;
    private TerrainPresetImporter? _presetImporter;
    private bool _showErrorLog;
    private bool _showWarningLog;
    private float _terrainBaseHeight;
    private string _terrainName = "theTerrain";
    private int _terrainSize = 2048;
    private bool _updateTerrainBlock = true;
    private string _workingDirectory = string.Empty;

    // OSM data availability tracking
    private bool _canFetchOsmData;
    private string? _osmBlockedReason;
    private GeoTiffValidationResult? _geoTiffValidationResult;

    /// <summary>
    /// Gets the effective bounding box for OSM queries.
    /// Returns the cropped bounding box if cropping is enabled, otherwise returns the full bounding box.
    /// This MUST be used for all OSM-related operations to ensure correct geographic extent.
    /// </summary>
    private GeoBoundingBox? EffectiveBoundingBox =>
        _cropResult is { NeedsCropping: true, CroppedBoundingBox: not null }
            ? _cropResult.CroppedBoundingBox
            : _geoBoundingBox;

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
        if (!string.IsNullOrEmpty(_levelName))
            return $"Working Directory > {_levelName}";
        if (!string.IsNullOrEmpty(_workingDirectory))
            return $"Working Directory > {_workingDirectory}";
        return "Select Level Folder";
    }

    private string GetOutputPath()
    {
        if (string.IsNullOrEmpty(_workingDirectory))
            return "Not set";
        return Path.Combine(_workingDirectory, $"{_terrainName}.ter");
    }

    private string GetDebugPath()
    {
        if (string.IsNullOrEmpty(_workingDirectory))
            return "Not set";
        return Path.Combine(_workingDirectory, "MT_TerrainGeneration");
    }

    private bool CanGenerate()
    {
        // Check if we have a valid heightmap source based on selected type
        var hasValidHeightmapSource = _heightmapSourceType switch
        {
            HeightmapSourceType.Png => !string.IsNullOrEmpty(_heightmapPath) && File.Exists(_heightmapPath),
            HeightmapSourceType.GeoTiffFile => !string.IsNullOrEmpty(_geoTiffPath) && File.Exists(_geoTiffPath),
            HeightmapSourceType.GeoTiffDirectory => !string.IsNullOrEmpty(_geoTiffDirectory) &&
                                                    Directory.Exists(_geoTiffDirectory),
            _ => false
        };

        return hasValidHeightmapSource &&
               _terrainMaterials.Any() &&
               !string.IsNullOrEmpty(_terrainName);
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
        GeoBoundingBox? wgs84BoundingBox = null;
        GeoBoundingBox? nativeBoundingBox = null;
        string? projectionName = null;
        string? projectionWkt = null;
        double[]? geoTransform = null;
        var originalWidth = 0;
        var originalHeight = 0;
        int? newTerrainSize = null;

        // Reset OSM availability
        _canFetchOsmData = false;
        _osmBlockedReason = null;
        _geoTiffValidationResult = null;

        try
        {
            await Task.Run(() =>
            {
                var reader = new GeoTiffReader();

                if (!string.IsNullOrEmpty(_geoTiffPath) && File.Exists(_geoTiffPath))
                {
                    // FIRST: Validate the GeoTIFF and log diagnostic info
                    _geoTiffValidationResult = reader.ValidateGeoTiff(_geoTiffPath);
                    _canFetchOsmData = _geoTiffValidationResult.CanFetchOsmData;
                    _osmBlockedReason = _geoTiffValidationResult.OsmBlockedReason;
                    
                    // Log validation results to UI
                    if (!_geoTiffValidationResult.IsValid)
                    {
                        foreach (var error in _geoTiffValidationResult.Errors)
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Error, $"GeoTIFF Validation: {error}");
                        }
                    }
                    foreach (var warning in _geoTiffValidationResult.Warnings)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning, $"GeoTIFF: {warning}");
                    }
                    
                    if (!_canFetchOsmData && !string.IsNullOrEmpty(_osmBlockedReason))
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"⚠️ OSM road data will NOT be available: {_osmBlockedReason}");
                    }

                    // Single file - read extended info to get WGS84 bounding box
                    var info = reader.GetGeoTiffInfoExtended(_geoTiffPath);
                    wgs84BoundingBox = info.Wgs84BoundingBox;
                    nativeBoundingBox = info.BoundingBox;
                    projectionName = info.ProjectionName;
                    projectionWkt = info.Projection;
                    geoTransform = info.GeoTransform;
                    originalWidth = info.Width;
                    originalHeight = info.Height;
                    newTerrainSize = GetNearestPowerOfTwo(Math.Max(info.Width, info.Height));

                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"GeoTIFF: {info.Width}x{info.Height}px, terrain size will be {newTerrainSize}");
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Projection: {projectionName}" + (info.EpsgCode != null ? $" ({info.EpsgCode})" : ""));
                    
                    // Show native pixel resolution from GeoTransform
                    if (info.GeoTransform != null)
                    {
                        var nativePixelSizeX = Math.Abs(info.GeoTransform[1]);
                        var nativePixelSizeY = Math.Abs(info.GeoTransform[5]);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Native DEM resolution: {nativePixelSizeX:F2}m × {nativePixelSizeY:F2}m per pixel");
                        
                        // Calculate real-world extent
                        var realWorldWidth = nativePixelSizeX * info.Width;
                        var realWorldHeight = nativePixelSizeY * info.Height;
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Real-world extent: {realWorldWidth/1000:F2}km × {realWorldHeight/1000:F2}km");
                    }
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Native Bounding Box: {info.BoundingBox}");

                    if (wgs84BoundingBox != null && wgs84BoundingBox.IsValidWgs84)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"WGS84 Bounding Box: {wgs84BoundingBox}");
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Overpass bbox: {wgs84BoundingBox.ToOverpassBBox()}");
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            "Could not determine WGS84 coordinates. OSM features will not be available.");
                    }

                    // Capture elevation range for auto-calculation
                    if (info.MinElevation.HasValue && info.MaxElevation.HasValue)
                    {
                        _geoTiffMinElevation = info.MinElevation;
                        _geoTiffMaxElevation = info.MaxElevation;
                    }
                }
                else if (!string.IsNullOrEmpty(_geoTiffDirectory) && Directory.Exists(_geoTiffDirectory))
                {
                    // Directory with tiles - use the new comprehensive method
                    GeoTiffDirectoryInfoResult dirInfo;
                    try
                    {
                        dirInfo = reader.GetGeoTiffDirectoryInfoExtended(_geoTiffDirectory);
                    }
                    catch (InvalidOperationException ex)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning, ex.Message);
                        return;
                    }

                    // Store validation result and OSM availability from the combined analysis
                    _geoTiffValidationResult = dirInfo.ValidationResult;
                    _canFetchOsmData = dirInfo.CanFetchOsmData;
                    _osmBlockedReason = dirInfo.OsmBlockedReason;

                    // Log validation warnings
                    foreach (var warning in dirInfo.Warnings)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning, $"GeoTIFF Tiles: {warning}");
                    }
                    
                    if (_geoTiffValidationResult != null)
                    {
                        if (!_geoTiffValidationResult.IsValid)
                        {
                            foreach (var error in _geoTiffValidationResult.Errors)
                            {
                                PubSubChannel.SendMessage(PubSubMessageType.Error, $"GeoTIFF Validation: {error}");
                            }
                        }
                        foreach (var valWarning in _geoTiffValidationResult.Warnings)
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Warning, $"GeoTIFF: {valWarning}");
                        }
                    }
                    
                    if (!_canFetchOsmData && !string.IsNullOrEmpty(_osmBlockedReason))
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            $"⚠️ OSM road data will NOT be available: {_osmBlockedReason}");
                    }

                    // Use COMBINED values instead of first tile values
                    projectionName = dirInfo.ProjectionName;
                    projectionWkt = dirInfo.Projection;
                    geoTransform = dirInfo.CombinedGeoTransform;
                    originalWidth = dirInfo.CombinedWidth;
                    originalHeight = dirInfo.CombinedHeight;

                    nativeBoundingBox = dirInfo.NativeBoundingBox;
                    wgs84BoundingBox = dirInfo.Wgs84BoundingBox;

                    // Store elevation range
                    if (dirInfo.MinElevation.HasValue && dirInfo.MaxElevation.HasValue)
                    {
                        _geoTiffMinElevation = dirInfo.MinElevation;
                        _geoTiffMaxElevation = dirInfo.MaxElevation;
                    }

                    // Calculate terrain size based on combined dimensions
                    newTerrainSize = GetNearestPowerOfTwo(Math.Max(dirInfo.CombinedWidth, dirInfo.CombinedHeight));

                    // Log comprehensive info
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Found {dirInfo.TileCount} GeoTIFF tile(s), combined size {dirInfo.CombinedWidth}x{dirInfo.CombinedHeight}px");
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Terrain size will be {newTerrainSize}");
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Projection: {projectionName}");
                    
                    // Show native pixel resolution from combined GeoTransform
                    if (dirInfo.CombinedGeoTransform != null)
                    {
                        var nativePixelSizeX = Math.Abs(dirInfo.CombinedGeoTransform[1]);
                        var nativePixelSizeY = Math.Abs(dirInfo.CombinedGeoTransform[5]);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Native DEM resolution: {nativePixelSizeX:F2}m × {nativePixelSizeY:F2}m per pixel");
                        
                        // Calculate real-world extent
                        var realWorldWidth = nativePixelSizeX * dirInfo.CombinedWidth;
                        var realWorldHeight = nativePixelSizeY * dirInfo.CombinedHeight;
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Real-world extent: {realWorldWidth/1000:F2}km × {realWorldHeight/1000:F2}km");
                    }
                    
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Native Bounding Box: {dirInfo.NativeBoundingBox}");

                    if (wgs84BoundingBox != null && wgs84BoundingBox.IsValidWgs84)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Combined WGS84 Bounding Box: {wgs84BoundingBox}");
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Overpass bbox: {wgs84BoundingBox.ToOverpassBBox()}");
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            "Could not determine WGS84 coordinates for tiles. OSM features will not be available.");
                    }

                    if (_geoTiffMinElevation.HasValue && _geoTiffMaxElevation.HasValue)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Combined elevation range: {_geoTiffMinElevation:F1}m to {_geoTiffMaxElevation:F1}m");
                    }
                }
            });

            // Update fields on UI thread after Task.Run completes
            if (wgs84BoundingBox != null) _geoBoundingBox = wgs84BoundingBox;
            if (nativeBoundingBox != null) _geoTiffNativeBoundingBox = nativeBoundingBox;
            _geoTiffProjectionName = projectionName;
            _geoTiffProjectionWkt = projectionWkt;
            _geoTiffGeoTransform = geoTransform;
            _geoTiffOriginalWidth = originalWidth;
            _geoTiffOriginalHeight = originalHeight;

            // Only update terrain size from GeoTIFF if no existing terrain.json was loaded
            // This preserves the user's existing level settings
            if (newTerrainSize.HasValue && !_hasExistingTerrainSettings)
            {
                _terrainSize = newTerrainSize.Value;
            }
            else if (newTerrainSize.HasValue && _hasExistingTerrainSettings)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Keeping terrain size {_terrainSize} from terrain.json (GeoTIFF suggests {newTerrainSize.Value})");
            }

            // Auto-populate maxHeight and terrainBaseHeight from GeoTIFF elevation data
            if (_geoTiffMinElevation.HasValue && _geoTiffMaxElevation.HasValue)
            {
                // Calculate elevation range for maxHeight
                var elevationRange = _geoTiffMaxElevation.Value - _geoTiffMinElevation.Value;
                _maxHeight = (float)elevationRange;

                // Set base height to minimum elevation so terrain sits at correct world height
                _terrainBaseHeight = (float)_geoTiffMinElevation.Value;

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Auto-calculated: Max Height = {_maxHeight:F1}m, Base Height = {_terrainBaseHeight:F1}m");
            }

            // CRITICAL: Auto-calculate metersPerPixel from GeoTIFF extent
            // This is essential for correct road smoothing and terrain scaling
            // 
            // User's intent: If metersPerPixel = 1, then a 4096 terrain = 4096m (4.096km) in-game
            // The metersPerPixel value is the TARGET game scale, not the source DEM resolution.
            //
            // We SUGGEST a value based on the source data, but user can override.
            if (wgs84BoundingBox != null && newTerrainSize.HasValue && geoTransform != null)
            {
                // Calculate what metersPerPixel WOULD be if we used the full geographic extent
                // This is informational - the user sets the actual game scale
                var nativePixelSizeX = Math.Abs(geoTransform[1]);
                var nativePixelSizeY = Math.Abs(geoTransform[5]);
                var avgNativePixelSize = (nativePixelSizeX + nativePixelSizeY) / 2.0;
                
                // If resizing from original to target size, scale accordingly
                var originalSize = Math.Max(originalWidth, originalHeight);
                var scaleFactor = (double)originalSize / newTerrainSize.Value;
                var suggestedMpp = (float)(avgNativePixelSize * scaleFactor);
                
                // Only auto-set if user hasn't manually configured it (still at default 1.0)
                // AND the suggested value is significantly different
                if (Math.Abs(_metersPerPixel - 1.0f) < 0.01f && suggestedMpp > 1.5f)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"⚠️ Geographic scale mismatch detected!");
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"   Source DEM covers ~{suggestedMpp * newTerrainSize.Value / 1000:F1}km but terrain is {newTerrainSize.Value}px");
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"   Suggested: Set 'Meters per Pixel' to {suggestedMpp:F1} for real-world scale");
                    PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"   Current: 1.0 m/px = {newTerrainSize.Value / 1000f:F1}km terrain (compressed)");
                }
                else
                {
                    var totalSizeKm = _metersPerPixel * newTerrainSize.Value / 1000f;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Terrain scale: {_metersPerPixel:F1}m/px = {totalSizeKm:F1}km × {totalSizeKm:F1}km in-game");
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not read GeoTIFF metadata: {ex.Message}. OSM features will not be available until terrain generation.");
        }
    }

    /// <summary>
    ///     Gets the nearest power of 2 that is >= the input value.
    /// </summary>
    private static int GetNearestPowerOfTwo(int value)
    {
        if (value <= 256) return 256;
        if (value <= 512) return 512;
        if (value <= 1024) return 1024;
        if (value <= 2048) return 2048;
        if (value <= 4096) return 4096;
        if (value <= 8192) return 8192;
        return 16384;
    }

    private void ClearGeoMetadata()
    {
        _geoBoundingBox = null;
        _geoTiffNativeBoundingBox = null;
        _geoTiffProjectionName = null;
        _geoTiffProjectionWkt = null;
        _geoTiffGeoTransform = null;
        _geoTiffOriginalWidth = 0;
        _geoTiffOriginalHeight = 0;
        _geoTiffMinElevation = null;
        _geoTiffMaxElevation = null;
        
        // Clean up cached combined GeoTIFF file
        CleanupCachedCombinedGeoTiff();
    }

    /// <summary>
    /// Cleans up the cached combined GeoTIFF file if it exists.
    /// </summary>
    private void CleanupCachedCombinedGeoTiff()
    {
        if (!string.IsNullOrEmpty(_cachedCombinedGeoTiffPath))
        {
            try
            {
                if (File.Exists(_cachedCombinedGeoTiffPath))
                {
                    File.Delete(_cachedCombinedGeoTiffPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
            _cachedCombinedGeoTiffPath = null;
        }
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
    }

    /// <summary>
    /// Recalculates the elevation range for a cropped region of the GeoTIFF.
    /// Updates _maxHeight and _terrainBaseHeight with the cropped region's values.
    /// For GeoTIFF directory mode, uses a cached combined file to avoid re-combining on every crop change.
    /// </summary>
    private async Task RecalculateCroppedElevation(CropResult cropResult)
    {
        // Only recalculate for GeoTIFF sources
        if (_heightmapSourceType != HeightmapSourceType.GeoTiffFile &&
            _heightmapSourceType != HeightmapSourceType.GeoTiffDirectory)
        {
            return;
        }

        // Variables to capture results from background thread
        double? croppedMin = null;
        double? croppedMax = null;
        string? geoTiffPathToRead = null;

        try
        {
            if (_heightmapSourceType == HeightmapSourceType.GeoTiffFile)
            {
                // Single file - direct crop elevation calculation
                if (string.IsNullOrEmpty(_geoTiffPath) || !File.Exists(_geoTiffPath))
                {
                    return;
                }
                geoTiffPathToRead = _geoTiffPath;
            }
            else if (_heightmapSourceType == HeightmapSourceType.GeoTiffDirectory)
            {
                // Directory mode - use cached combined file to avoid re-combining on every crop change
                if (string.IsNullOrEmpty(_geoTiffDirectory) || !Directory.Exists(_geoTiffDirectory))
                {
                    return;
                }

                // Check if we need to create/update the cached combined file
                if (string.IsNullOrEmpty(_cachedCombinedGeoTiffPath) || !File.Exists(_cachedCombinedGeoTiffPath))
                {
                    await Task.Run(async () =>
                    {
                        var combiner = new GeoTiffCombiner();
                        _cachedCombinedGeoTiffPath = Path.Combine(Path.GetTempPath(), $"combined_geotiff_{Guid.NewGuid():N}.tif");

                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            "Combining GeoTIFF tiles (one-time operation)...");
                        
                        await combiner.CombineGeoTiffsAsync(_geoTiffDirectory!, _cachedCombinedGeoTiffPath);
                        
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            "GeoTIFF tiles combined. Subsequent crop changes will be fast.");
                    });
                }
                geoTiffPathToRead = _cachedCombinedGeoTiffPath;
            }

            // Read elevation from the appropriate file
            if (!string.IsNullOrEmpty(geoTiffPathToRead) && File.Exists(geoTiffPathToRead))
            {
                await Task.Run(() =>
                {
                    var reader = new GeoTiffReader();
                    (croppedMin, croppedMax) = reader.GetCroppedElevationRange(
                        geoTiffPathToRead,
                        cropResult.OffsetX,
                        cropResult.OffsetY,
                        cropResult.CropWidth,
                        cropResult.CropHeight);
                });
            }

            // Update UI-bound fields on the UI thread AFTER the background task completes
            if (croppedMin.HasValue && croppedMax.HasValue)
            {
                // Update the crop result with elevation data
                cropResult.CroppedMinElevation = croppedMin.Value;
                cropResult.CroppedMaxElevation = croppedMax.Value;

                // Update the terrain parameters
                var elevationRange = croppedMax.Value - croppedMin.Value;
                _maxHeight = (float)elevationRange;
                _terrainBaseHeight = (float)croppedMin.Value;

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Cropped elevation: {croppedMin:F1}m to {croppedMax:F1}m (Max Height = {_maxHeight:F1}m, Base = {_terrainBaseHeight:F1}m)");

                // Force UI refresh since we updated bound fields
                await InvokeAsync(StateHasChanged);
            }
            else
            {
                // Failed to get cropped elevation - keep existing values but log warning
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Could not read cropped elevation (min={croppedMin}, max={croppedMax}). Keeping current values.");
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not recalculate cropped elevation: {ex.Message}. Using full image values.");
        }
    }

    private string GetHeightmapSourceDescription()
    {
        return _heightmapSourceType switch
        {
            HeightmapSourceType.Png => "16-bit grayscale PNG heightmap",
            HeightmapSourceType.GeoTiffFile => "Single GeoTIFF elevation file with geographic coordinates",
            HeightmapSourceType.GeoTiffDirectory => "Directory with multiple GeoTIFF tiles to combine",
            _ => "Unknown"
        };
    }

    private string GetMetersPerPixelHelperText()
    {
        var terrainSizeKm = _metersPerPixel * _terrainSize / 1000f;
        return $"Terrain = {terrainSizeKm:F1}km × {terrainSizeKm:F1}km in-game";
    }

    private double GetNativePixelSizeX()
    {
        return _geoTiffGeoTransform != null ? Math.Abs(_geoTiffGeoTransform[1]) : 0;
    }

    private double GetNativePixelSizeY()
    {
        return _geoTiffGeoTransform != null ? Math.Abs(_geoTiffGeoTransform[5]) : 0;
    }

    /// <summary>
    /// Checks if the GeoTIFF uses a geographic (lat/lon) coordinate system.
    /// Geographic systems have pixel sizes in degrees (typically very small numbers like 0.0001).
    /// Projected systems (UTM, etc.) have pixel sizes in meters (typically 1-100).
    /// </summary>
    private bool IsGeographicCrs()
    {
        if (_geoTiffGeoTransform == null) return false;
        
        // If pixel size is less than 1, it's likely in degrees (geographic CRS)
        // Projected CRS typically have pixel sizes of 1m or more
        var pixelSizeX = Math.Abs(_geoTiffGeoTransform[1]);
        return pixelSizeX < 0.1; // Less than 0.1 = definitely degrees
    }

    /// <summary>
    /// Gets the native pixel size as a formatted string.
    /// For geographic CRS, shows in arc-seconds. For projected, shows in meters.
    /// </summary>
    private string GetNativePixelSizeDescription()
    {
        if (_geoTiffGeoTransform == null) return "Unknown";
        
        var pixelSizeX = Math.Abs(_geoTiffGeoTransform[1]);
        var pixelSizeY = Math.Abs(_geoTiffGeoTransform[5]);
        
        if (IsGeographicCrs())
        {
            // Convert degrees to arc-seconds for readability
            var arcSecX = pixelSizeX * 3600;
            var arcSecY = pixelSizeY * 3600;
            
            // Also calculate approximate meters at the center latitude
            var centerLat = _geoBoundingBox != null 
                ? (_geoBoundingBox.MinLatitude + _geoBoundingBox.MaxLatitude) / 2.0 
                : 35.0; // Default to mid-latitude
            var metersPerDegree = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
            var approxMetersX = pixelSizeX * metersPerDegree;
            var approxMetersY = pixelSizeY * 111320.0; // Latitude is constant
            
            return $"{arcSecX:F1}\" × {arcSecY:F1}\" (~{approxMetersX:F0}m × {approxMetersY:F0}m)";
        }
        else
        {
            // Projected CRS - values are in meters
            return $"{pixelSizeX:F2}m × {pixelSizeY:F2}m";
        }
    }

    private double GetRealWorldWidthKm()
    {
        if (_geoTiffGeoTransform == null || _geoTiffOriginalWidth == 0) return 0;
        
        if (IsGeographicCrs())
        {
            // For geographic CRS, calculate from degrees
            var degreesWidth = Math.Abs(_geoTiffGeoTransform[1]) * _geoTiffOriginalWidth;
            var centerLat = _geoBoundingBox != null 
                ? (_geoBoundingBox.MinLatitude + _geoBoundingBox.MaxLatitude) / 2.0 
                : 35.0;
            var metersPerDegree = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
            return degreesWidth * metersPerDegree / 1000.0;
        }
        else
        {
            // Projected CRS - GeoTransform is in meters
            return GetNativePixelSizeX() * _geoTiffOriginalWidth / 1000.0;
        }
    }

    private double GetRealWorldHeightKm()
    {
        if (_geoTiffGeoTransform == null || _geoTiffOriginalHeight == 0) return 0;
        
        if (IsGeographicCrs())
        {
            // For geographic CRS, calculate from degrees
            var degreesHeight = Math.Abs(_geoTiffGeoTransform[5]) * _geoTiffOriginalHeight;
            // Latitude degrees are roughly constant at 111.32 km/degree
            return degreesHeight * 111.32;
        }
        else
        {
            // Projected CRS - GeoTransform is in meters
            return GetNativePixelSizeY() * _geoTiffOriginalHeight / 1000.0;
        }
    }

    /// <summary>
    /// Gets the average native pixel size in meters for the GeoTIFF.
    /// Used by the crop selector to calculate proper selection size.
    /// </summary>
    private float GetNativePixelSizeAverage()
    {
        if (_geoTiffGeoTransform == null) return 1.0f;
        
        if (IsGeographicCrs())
        {
            // For geographic CRS, calculate approximate meters at center latitude
            var pixelSizeX = Math.Abs(_geoTiffGeoTransform[1]);
            var pixelSizeY = Math.Abs(_geoTiffGeoTransform[5]);
            
            var centerLat = _geoBoundingBox != null 
                ? (_geoBoundingBox.MinLatitude + _geoBoundingBox.MaxLatitude) / 2.0 
                : 35.0;
            
            var metersPerDegreeLon = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
            var metersPerDegreeLat = 111320.0;
            
            var metersX = pixelSizeX * metersPerDegreeLon;
            var metersY = pixelSizeY * metersPerDegreeLat;
            
            return (float)((metersX + metersY) / 2.0);
        }
        else
        {
            // Projected CRS - GeoTransform is in meters
            var pixelSizeX = Math.Abs(_geoTiffGeoTransform[1]);
            var pixelSizeY = Math.Abs(_geoTiffGeoTransform[5]);
            return (float)((pixelSizeX + pixelSizeY) / 2.0);
        }
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
        if (result.HeightmapSourceType.HasValue)
        {
            _heightmapSourceType = result.HeightmapSourceType.Value;
        }

        // Apply GeoTIFF paths and trigger metadata read
        if (!string.IsNullOrEmpty(result.GeoTiffPath))
        {
            _geoTiffPath = result.GeoTiffPath;
            if (File.Exists(result.GeoTiffPath))
            {
                // Read GeoTIFF metadata to restore bounding box and geo info
                await ReadGeoTiffMetadata();
            }
        }

        if (!string.IsNullOrEmpty(result.GeoTiffDirectory))
        {
            _geoTiffDirectory = result.GeoTiffDirectory;
            if (Directory.Exists(result.GeoTiffDirectory))
            {
                await ReadGeoTiffMetadata();
            }
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
        _errors.Clear();
        _warnings.Clear();
        _messages.Clear();
        _terrainMaterials.Clear();

        StateHasChanged();

        await Task.Run(() =>
        {
            try
            {
                // Validate the folder contains expected level structure
                var levelPath = ZipFileHandler.GetNamePath(folder);
                if (string.IsNullOrEmpty(levelPath))
                {
                    var infoJsonPath = Path.Join(folder, "info.json");
                    if (File.Exists(infoJsonPath))
                    {
                        levelPath = folder;
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Error,
                            "Selected folder does not appear to be a valid BeamNG level. " +
                            "Please select a folder containing info.json.");
                        return;
                    }
                }

                _workingDirectory = levelPath;
                _hasWorkingDirectory = true;

                // Get level name from info.json
                var reader = new BeamFileReader(levelPath, null);
                _levelName = reader.GetLevelName();

                // Scan for terrain materials
                ScanTerrainMaterials(levelPath);

                // Try to read existing terrain size from terrain.json
                LoadTerrainSettings(levelPath);

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Loaded {_terrainMaterials.Count} terrain materials from {_levelName}");
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
            finally
            {
                _isLoading = false;
            }
        });

        if (_hasWorkingDirectory && FileSelect?.Panels.Count > 0) await FileSelect.Panels[0].CollapseAsync();

        StateHasChanged();
    }

    private void LoadTerrainSettings(string levelPath)
    {
        try
        {
            // Try to find terrain.json to get existing settings
            var terrainFiles = Directory.GetFiles(levelPath, "*.terrain.json", SearchOption.TopDirectoryOnly);
            if (terrainFiles.Length > 0)
            {
                var terrainJsonPath = terrainFiles[0];
                var jsonContent = File.ReadAllText(terrainJsonPath);
                var jsonNode = JsonUtils.GetValidJsonNodeFromString(jsonContent, terrainJsonPath);

                if (jsonNode != null)
                {
                    if (jsonNode["size"] != null)
                    {
                        _terrainSize = jsonNode["size"]!.GetValue<int>();
                        _hasExistingTerrainSettings = true;
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Loaded terrain size {_terrainSize} from existing terrain.json");
                    }

                    // Extract terrain name from filename
                    _terrainName = Path.GetFileNameWithoutExtension(terrainJsonPath)
                        .Replace(".terrain", "");
                }
            }

            // CRITICAL: Load metersPerPixel (squareSize) from TerrainBlock if it exists
            // This is essential for correct coordinate system when working with existing levels
            var metersPerPixelFromBlock = LoadMetersPerPixelFromTerrainBlock(levelPath);
            if (metersPerPixelFromBlock.HasValue)
            {
                _metersPerPixel = metersPerPixelFromBlock.Value;
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Loaded metersPerPixel {_metersPerPixel} from existing TerrainBlock");
            }
        }
        catch
        {
            // Ignore errors, use defaults
        }
    }

    /// <summary>
    ///     Loads the squareSize (metersPerPixel) from the existing TerrainBlock in items.level.json.
    ///     This is critical for maintaining correct coordinate system in existing levels.
    /// </summary>
    private float? LoadMetersPerPixelFromTerrainBlock(string levelPath)
    {
        try
        {
            // Search in common locations
            var searchPaths = new[]
            {
                Path.Join(levelPath, "main", "MissionGroup", "Level_object"),
                Path.Join(levelPath, "main", "MissionGroup"),
                Path.Join(levelPath, "main")
            };

            foreach (var searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath))
                    continue;

                var itemsFiles = Directory.GetFiles(searchPath, "items.level.json", SearchOption.AllDirectories);

                foreach (var itemsFile in itemsFiles)
                {
                    var lines = File.ReadAllLines(itemsFile);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("class", out var classProperty) &&
                                classProperty.GetString() == "TerrainBlock")
                            {
                                // Found TerrainBlock - extract squareSize
                                if (doc.RootElement.TryGetProperty("squareSize", out var squareSize))
                                {
                                    return (float)squareSize.GetDouble();
                                }
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // Skip invalid JSON lines
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not load metersPerPixel from TerrainBlock: {ex.Message}");
        }

        return null;
    }

    private void ScanTerrainMaterials(string levelPath)
    {
        // Dynamically find the terrain materials.json file instead of hardcoding the path
        var terrainMaterialsPath = TerrainTextureHelper.FindTerrainMaterialsJsonPath(levelPath);

        if (string.IsNullOrEmpty(terrainMaterialsPath) || !File.Exists(terrainMaterialsPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Terrain materials file not found in: {levelPath}");
            return;
        }

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found terrain materials at: {Path.GetFileName(terrainMaterialsPath)}");

        try
        {
            var jsonContent = File.ReadAllText(terrainMaterialsPath);
            var jsonNode = JsonUtils.GetValidJsonNodeFromString(jsonContent, terrainMaterialsPath);

            if (jsonNode == null)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    "Failed to parse terrain materials JSON.");
                return;
            }

            var order = 0;
            foreach (var property in jsonNode.AsObject())
            {
                var materialClass = property.Value?["class"]?.ToString();

                if (materialClass != "TerrainMaterial")
                    continue;

                var materialName = property.Value?["name"]?.ToString() ?? property.Key;
                var internalName = property.Value?["internalName"]?.ToString() ?? materialName;

                // Auto-detect road materials - disabled, let user decide
                var isRoad = false;

                _terrainMaterials.Add(new TerrainMaterialSettings.TerrainMaterialItemExtended
                {
                    Order = order,
                    MaterialName = materialName,
                    InternalName = internalName,
                    JsonKey = property.Key,
                    IsRoadMaterial = isRoad
                });

                order++;
            }
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Error reading terrain materials: {ex.Message}");
        }
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

    /// <summary>
    ///     Renormalizes material order values to be contiguous starting from 0.
    ///     This is critical because the material index in the .ter file must match the order.
    /// </summary>
    private void RenormalizeMaterialOrder()
    {
        var sorted = _terrainMaterials.OrderBy(m => m.Order).ToList();
        for (var i = 0; i < sorted.Count; i++) sorted[i].Order = i;

        _terrainMaterials.Clear();
        _terrainMaterials.AddRange(sorted);
    }

    /// <summary>
    ///     Reorders materials so that those without layer maps (except the one at index 0) are moved to the end.
    ///     Materials without layer maps at positions > 0 will never claim pixels in terrain generation,
    ///     so they should be at the end to avoid confusion with material indices.
    /// </summary>
    /// <returns>True if any materials were reordered, false otherwise.</returns>
    private bool ReorderMaterialsWithoutLayerMapsToEnd()
    {
        // First, normalize the current order
        RenormalizeMaterialOrder();

        var sorted = _terrainMaterials.OrderBy(m => m.Order).ToList();

        // Separate materials:
        // - Material at index 0 stays (regardless of having layer map)
        // - Materials with layer maps (indices 1+)
        // - Materials without layer maps (indices 1+)
        var firstMaterial = sorted.FirstOrDefault();
        var remainingWithLayerMaps = sorted.Skip(1).Where(m => m.HasLayerMap).ToList();
        var remainingWithoutLayerMaps = sorted.Skip(1).Where(m => !m.HasLayerMap).ToList();

        // Check if any reordering is needed
        if (!remainingWithoutLayerMaps.Any())
            return false; // Nothing to reorder

        // Check if materials without layer maps are already at the end
        var expectedOrder = new List<TerrainMaterialSettings.TerrainMaterialItemExtended>();
        if (firstMaterial != null)
            expectedOrder.Add(firstMaterial);
        expectedOrder.AddRange(remainingWithLayerMaps);
        expectedOrder.AddRange(remainingWithoutLayerMaps);

        // Compare with current order
        var currentOrder = sorted.ToList();
        var needsReorder = false;
        for (var i = 0; i < expectedOrder.Count; i++)
            if (expectedOrder[i] != currentOrder[i])
            {
                needsReorder = true;
                break;
            }

        if (!needsReorder)
            return false;

        // Apply new order
        _terrainMaterials.Clear();
        for (var i = 0; i < expectedOrder.Count; i++)
        {
            expectedOrder[i].Order = i;
            _terrainMaterials.Add(expectedOrder[i]);
        }

        return true;
    }

    /// <summary>
    ///     Handles the request from TerrainPresetExporter to reorder materials before export.
    /// </summary>
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

    private void OnMaterialSettingsChanged(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        StateHasChanged();
    }

    private void MoveToTop(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        var currentOrder = material.Order;

        // If already at top, nothing to do
        if (currentOrder == 0) return;

        // Increment the order of all materials that were above this one
        foreach (var mat in _terrainMaterials.Where(m => m.Order < currentOrder)) mat.Order++;

        // Set the moved material to order 0
        material.Order = 0;

        // Renormalize and sort
        RenormalizeMaterialOrder();

        _dropContainer?.Refresh();
        StateHasChanged();
    }

    private void MoveToBottom(TerrainMaterialSettings.TerrainMaterialItemExtended material)
    {
        var currentOrder = material.Order;
        var maxOrder = _terrainMaterials.Max(m => m.Order);

        // If already at bottom, nothing to do
        if (currentOrder == maxOrder) return;

        // Decrement the order of all materials that were below this one
        foreach (var mat in _terrainMaterials.Where(m => m.Order > currentOrder)) mat.Order--;

        // Set the moved material to the maximum order
        material.Order = maxOrder;

        // Renormalize and sort
        RenormalizeMaterialOrder();

        _dropContainer?.Refresh();
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

        var success = false;
        TerrainCreationParameters? terrainParameters = null;

        try
        {
            // Create debug directory
            var debugPath = GetDebugPath();
            Directory.CreateDirectory(debugPath);

        await Task.Run(async () =>
            {
                var creator = new TerrainCreator();

                // Build material definitions in order
                var orderedMaterials = _terrainMaterials.OrderBy(m => m.Order).ToList();
                var materialDefinitions = new List<MaterialDefinition>();

                // Cache for OSM query results to avoid re-fetching
                OsmQueryResult? osmQueryResult = null;

                // CRITICAL: Determine which bounding box to use for OSM queries
                // If cropping is enabled, use the cropped bounding box!
                GeoBoundingBox? effectiveBoundingBox = _geoBoundingBox;
                if (_cropResult is { NeedsCropping: true, CroppedBoundingBox: not null })
                {
                    effectiveBoundingBox = _cropResult.CroppedBoundingBox;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Using CROPPED bounding box for OSM: {effectiveBoundingBox}");
                }
                else if (_geoBoundingBox != null)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Using FULL bounding box for OSM: {effectiveBoundingBox}");
                }

                // Create coordinate transformer for proper WGS84 -> pixel conversion
                // This is essential for projected CRS (UTM, etc.) to avoid rotation/skew
                // NOTE: When cropping, we need to adjust the transformer to map to the cropped region
                GeoCoordinateTransformer? coordinateTransformer = null;
                if (_geoTiffGeoTransform != null && _geoTiffProjectionWkt != null && effectiveBoundingBox != null)
                {
                    // For cropped regions, create a transformer that maps WGS84 coordinates
                    // directly to the OUTPUT terrain pixels (0 to _terrainSize)
                    // The transformer handles the reprojection and scaling internally
                    if (_cropResult is { NeedsCropping: true })
                    {
                        // Create adjusted GeoTransform for the cropped region
                        // The crop offset shifts the origin
                        var croppedGeoTransform = new double[6];
                        Array.Copy(_geoTiffGeoTransform, croppedGeoTransform, 6);
                        
                        // Adjust origin to the crop offset
                        // GeoTransform[0] = x-origin, GeoTransform[3] = y-origin
                        croppedGeoTransform[0] = _geoTiffGeoTransform[0] + _cropResult.OffsetX * _geoTiffGeoTransform[1];
                        croppedGeoTransform[3] = _geoTiffGeoTransform[3] + _cropResult.OffsetY * _geoTiffGeoTransform[5];
                        
                        coordinateTransformer = new GeoCoordinateTransformer(
                            _geoTiffProjectionWkt,
                            croppedGeoTransform,
                            _cropResult.CropWidth,
                            _cropResult.CropHeight,
                            _terrainSize);
                        
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Using CROPPED coordinate transformer: crop origin ({_cropResult.OffsetX}, {_cropResult.OffsetY}), " +
                            $"crop size {_cropResult.CropWidth}x{_cropResult.CropHeight} -> terrain {_terrainSize}x{_terrainSize}");
                    }
                    else
                    {
                        coordinateTransformer = new GeoCoordinateTransformer(
                            _geoTiffProjectionWkt,
                            _geoTiffGeoTransform,
                            _geoTiffOriginalWidth,
                            _geoTiffOriginalHeight,
                            _terrainSize);
                    }

                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Using GDAL coordinate transformer for OSM features (reprojection: {coordinateTransformer.UsesReprojection})");
                }

                foreach (var mat in orderedMaterials)
                {
                    RoadSmoothingParameters? roadParams = null;
                    string? layerImagePath = null;

                    // Process layer source based on type
                    if (mat.LayerSourceType == LayerSourceType.OsmFeatures &&
                        mat.SelectedOsmFeatures?.Any() == true &&
                        effectiveBoundingBox != null)
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"Processing OSM features for material: {mat.InternalName}");

                        // Fetch OSM data if not cached
                        // Use the EFFECTIVE bounding box (cropped if applicable)
                        if (osmQueryResult == null)
                        {
                            var cache = new OsmQueryCache();
                            osmQueryResult = await cache.GetAsync(effectiveBoundingBox);

                            if (osmQueryResult == null)
                            {
                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    "Fetching OSM data from Overpass API...");
                                var service = new OverpassApiService();
                                osmQueryResult = await service.QueryAllFeaturesAsync(effectiveBoundingBox);
                                await cache.SetAsync(effectiveBoundingBox, osmQueryResult);
                            }
                        }

                        var processor = new OsmGeometryProcessor();

                        // Set the coordinate transformer for proper reprojection
                        if (coordinateTransformer != null) processor.SetCoordinateTransformer(coordinateTransformer);

                        // Get full features from query result
                        var fullFeatures = processor.GetFeaturesFromSelections(
                            osmQueryResult,
                            mat.SelectedOsmFeatures);

                        if (mat.IsRoadMaterial)
                        {
                            // For road materials: filter to line features and convert to splines
                            var lineFeatures = fullFeatures
                                .Where(f => f.GeometryType == OsmGeometryType.LineString)
                                .ToList();

                            if (lineFeatures.Any())
                            {
                                // Convert MinPathLengthPixels to meters for OSM mode
                                // This parameter is reused from skeleton extraction settings
                                var minPathLengthMeters = mat.MinPathLengthPixels * _metersPerPixel;

                                // Convert lines to splines for direct use (bypassing skeleton extraction)
                                // Use the EFFECTIVE bounding box (cropped if applicable)
                                var splines = processor.ConvertLinesToSplines(
                                    lineFeatures,
                                    effectiveBoundingBox,
                                    _terrainSize,
                                    _metersPerPixel,
                                    minPathLengthMeters);

                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    $"Created {splines.Count} splines from {lineFeatures.Count} OSM line features");

                                // Export OSM spline debug image to verify coordinate transformation
                                var osmDebugPath = Path.Combine(debugPath, $"{mat.InternalName}_osm_splines_debug.png");
                                processor.ExportOsmSplineDebugImage(splines, _terrainSize, _metersPerPixel,
                                    osmDebugPath);

                                roadParams = mat.BuildRoadSmoothingParameters(debugPath);
                                roadParams.PreBuiltSplines = splines;
                            }

                            // Also rasterize for road mask (needed for blending)
                            // Use the EFFECTIVE bounding box (cropped if applicable)
                            // Use RoadSurfaceWidthMeters if set, otherwise fall back to RoadWidthMeters
                            var effectiveRoadSurfaceWidth = mat.RoadSurfaceWidthMeters.HasValue && mat.RoadSurfaceWidthMeters.Value > 0
                                ? mat.RoadSurfaceWidthMeters.Value
                                : mat.RoadWidthMeters;
                            
                            var roadMask = processor.RasterizeLinesToLayerMap(
                                lineFeatures,
                                effectiveBoundingBox,
                                _terrainSize,
                                effectiveRoadSurfaceWidth / _metersPerPixel); // Convert to pixels

                            layerImagePath = await SaveLayerMapToPng(roadMask, debugPath, mat.InternalName);
                        }
                        else
                        {
                            // For non-road materials: filter to polygon features and rasterize
                            var polygonFeatures = fullFeatures
                                .Where(f => f.GeometryType == OsmGeometryType.Polygon)
                                .ToList();

                            if (polygonFeatures.Any())
                            {
                                // Use the EFFECTIVE bounding box (cropped if applicable)
                                var layerMap = processor.RasterizePolygonsToLayerMap(
                                    polygonFeatures,
                                    effectiveBoundingBox,
                                    _terrainSize);

                                layerImagePath = await SaveLayerMapToPng(layerMap, debugPath, mat.InternalName);

                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    $"Rasterized {polygonFeatures.Count} OSM polygons for {mat.InternalName}");
                            }
                        }
                    }
                    else if (mat.LayerSourceType == LayerSourceType.PngFile)
                    {
                        layerImagePath = mat.LayerMapPath;

                        if (mat.IsRoadMaterial)
                            roadParams = mat.BuildRoadSmoothingParameters(debugPath);
                    }
                    else if (mat.IsRoadMaterial)
                    {
                        // No layer source but road smoothing enabled
                        roadParams = mat.BuildRoadSmoothingParameters(debugPath);
                    }

                    materialDefinitions.Add(new MaterialDefinition(
                        mat.InternalName,
                        layerImagePath,
                        roadParams));
                }

                var parameters = new TerrainCreationParameters
                {
                    Size = _terrainSize,
                    MaxHeight = _maxHeight,
                    MetersPerPixel = _metersPerPixel,
                    TerrainName = _terrainName,
                    TerrainBaseHeight = _terrainBaseHeight,
                    Materials = materialDefinitions,
                    EnableCrossMaterialHarmonization = _enableCrossMaterialHarmonization,
                    // Enable auto-setting base height from GeoTIFF when MaxHeight is 0
                    AutoSetBaseHeightFromGeoTiff = _maxHeight <= 0
                };
                
                // Store reference for use after Task.Run
                terrainParameters = parameters;

                // Set heightmap source based on selected type
                switch (_heightmapSourceType)
                {
                    case HeightmapSourceType.Png:
                        parameters.HeightmapPath = _heightmapPath;
                        break;
                    case HeightmapSourceType.GeoTiffFile:
                        parameters.GeoTiffPath = _geoTiffPath;
                        // Apply crop settings if the user has selected a crop region
                        if (_cropResult is { NeedsCropping: true })
                        {
                            parameters.CropGeoTiff = true;
                            parameters.CropOffsetX = _cropResult.OffsetX;
                            parameters.CropOffsetY = _cropResult.OffsetY;
                            parameters.CropWidth = _cropResult.CropWidth;
                            parameters.CropHeight = _cropResult.CropHeight;
                            
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                $"Applying GeoTIFF crop: offset ({_cropResult.OffsetX}, {_cropResult.OffsetY}), " +
                                $"size {_cropResult.CropWidth}x{_cropResult.CropHeight}");
                        }
                        break;
                    case HeightmapSourceType.GeoTiffDirectory:
                        // OPTIMIZATION: If we have a cached combined GeoTIFF from crop selection,
                        // use it directly instead of re-combining all tiles (which is very slow).
                        // This can save minutes of processing time for large tile sets.
                        if (!string.IsNullOrEmpty(_cachedCombinedGeoTiffPath) && File.Exists(_cachedCombinedGeoTiffPath))
                        {
                            parameters.GeoTiffPath = _cachedCombinedGeoTiffPath;
                            // Don't set GeoTiffDirectory - this forces single-file mode
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                "Using cached combined GeoTIFF (skipping tile re-combination)");
                        }
                        else
                        {
                            parameters.GeoTiffDirectory = _geoTiffDirectory;
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                "No cached combined GeoTIFF - will combine tiles during generation");
                        }
                        
                        // Apply crop settings if the user has selected a crop region (same as single file)
                        if (_cropResult is { NeedsCropping: true })
                        {
                            parameters.CropGeoTiff = true;
                            parameters.CropOffsetX = _cropResult.OffsetX;
                            parameters.CropOffsetY = _cropResult.OffsetY;
                            parameters.CropWidth = _cropResult.CropWidth;
                            parameters.CropHeight = _cropResult.CropHeight;
                            
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                $"Applying GeoTIFF crop: offset ({_cropResult.OffsetX}, {_cropResult.OffsetY}), " +
                                $"size {_cropResult.CropWidth}x{_cropResult.CropHeight}");
                        }
                        break;
                }

                var outputPath = GetOutputPath();

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Starting terrain generation: {_terrainSize}x{_terrainSize}, {materialDefinitions.Count} materials...");

                success = await creator.CreateTerrainFileAsync(outputPath, parameters);

                // Capture auto-calculated values from GeoTIFF import (but NOT the bounding box!)
                // The WGS84 bounding box is already set correctly in ReadGeoTiffMetadata()
                // TerrainCreator populates GeoBoundingBox with the raw/projected coordinates,
                // which are NOT suitable for Overpass API queries.
                if (parameters.GeoTiffMinElevation.HasValue) _geoTiffMinElevation = parameters.GeoTiffMinElevation;
                if (parameters.GeoTiffMaxElevation.HasValue) _geoTiffMaxElevation = parameters.GeoTiffMaxElevation;

                // Update _maxHeight with the auto-calculated value from GeoTIFF if it was 0
                // This ensures TerrainBlockUpdater uses the correct value
                if (_maxHeight <= 0 && parameters.MaxHeight > 0)
                {
                    _maxHeight = parameters.MaxHeight;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Max height auto-calculated from GeoTIFF: {_maxHeight:F1}m");
                }

                // Update _terrainBaseHeight with the auto-calculated value from GeoTIFF
                // The base height should be set to the minimum elevation so the terrain
                // sits at the correct world height
                if (parameters.TerrainBaseHeight != _terrainBaseHeight && parameters.GeoTiffMinElevation.HasValue)
                {
                    _terrainBaseHeight = parameters.TerrainBaseHeight;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Base height auto-calculated from GeoTIFF min elevation: {_terrainBaseHeight:F1}m");
                }
            });

            if (success)
            {
                Snackbar.Add($"Terrain generated successfully: {GetOutputPath()}", Severity.Success);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain file saved to: {GetOutputPath()}");

                // Run post-generation tasks on background thread to avoid blocking UI
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Running post-generation tasks...");
                var postGenStopwatch = Stopwatch.StartNew();

                await Task.Run(() =>
                {
                    // Update TerrainMaterialTextureSet baseTexSize to match terrain size
                    // This is CRITICAL: the baseTexSize must match the terrain size for proper rendering
                    // Dynamically find the terrain materials.json file instead of hardcoding the path
                    var taskStopwatch = Stopwatch.StartNew();
                    var terrainMaterialsPath = TerrainTextureHelper.FindTerrainMaterialsJsonPath(_workingDirectory);
                    if (!string.IsNullOrEmpty(terrainMaterialsPath))
                    {
                        var pbrHandler = new PbrUpgradeHandler(terrainMaterialsPath, _levelName, _workingDirectory);
                        pbrHandler.EnsureTerrainMaterialTextureSetSize(_terrainSize);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"[Perf] EnsureTerrainMaterialTextureSetSize: {taskStopwatch.ElapsedMilliseconds}ms");

                        // Resize any existing base textures to match the terrain size
                        taskStopwatch.Restart();
                        var resizedCount = TerrainTextureHelper.ResizeBaseTexturesToTerrainSize(_workingDirectory, _terrainSize);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"[Perf] ResizeBaseTexturesToTerrainSize: {taskStopwatch.ElapsedMilliseconds}ms ({resizedCount} textures)");
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            "Could not find terrain materials.json for PBR texture set update.");
                    }

                    // Update TerrainBlock in items.level.json if requested
                    if (_updateTerrainBlock)
                    {
                        taskStopwatch.Restart();
                        var terrainBlockUpdated = TerrainBlockUpdater.UpdateOrCreateTerrainBlock(
                            _workingDirectory,
                            _terrainName,
                            _terrainSize,
                            _maxHeight,
                            _terrainBaseHeight,
                            _metersPerPixel);
                        PubSubChannel.SendMessage(PubSubMessageType.Info,
                            $"[Perf] UpdateOrCreateTerrainBlock: {taskStopwatch.ElapsedMilliseconds}ms");

                        if (terrainBlockUpdated)
                            PubSubChannel.SendMessage(PubSubMessageType.Info, "TerrainBlock updated in items.level.json");
                        else
                            PubSubChannel.SendMessage(PubSubMessageType.Warning, "Could not update TerrainBlock - check warnings");
                    }

                    // Handle spawn point creation/update
                    taskStopwatch.Restart();
                    var spawnPointExists = SpawnPointUpdater.SpawnPointExists(_workingDirectory);
                    
                    // Create spawn point if it doesn't exist
                    if (!spawnPointExists)
                    {
                        if (terrainParameters?.ExtractedSpawnPoint != null)
                        {
                            var extractedSpawn = terrainParameters.ExtractedSpawnPoint;
                            var spawnPoint = new SpawnPointSuggestion
                            {
                                X = extractedSpawn.X,
                                Y = extractedSpawn.Y,
                                Z = extractedSpawn.Z,
                                RotationMatrix = extractedSpawn.RotationMatrix,
                                IsOnRoad = extractedSpawn.IsOnRoad,
                                SourceMaterialName = extractedSpawn.SourceMaterialName
                            };
                            
                            if (SpawnPointUpdater.CreateSpawnPoint(_workingDirectory, spawnPoint))
                            {
                                PubSubChannel.SendMessage(PubSubMessageType.Info,
                                    $"Created spawn point at ({spawnPoint.X:F1}, {spawnPoint.Y:F1}, {spawnPoint.Z:F1})");
                            }
                        }
                        else
                        {
                            // Create default spawn point at origin
                            if (SpawnPointUpdater.CreateSpawnPoint(_workingDirectory))
                            {
                                PubSubChannel.SendMessage(PubSubMessageType.Info, 
                                    "Created default spawn point 'spawn_default_MT'");
                            }
                        }
                    }
                    else if (terrainParameters?.ExtractedSpawnPoint != null)
                    {
                        // Spawn point exists, update it with extracted position
                        var extractedSpawn = terrainParameters.ExtractedSpawnPoint;
                        var spawnPoint = new SpawnPointSuggestion
                        {
                            X = extractedSpawn.X,
                            Y = extractedSpawn.Y,
                            Z = extractedSpawn.Z,
                            RotationMatrix = extractedSpawn.RotationMatrix,
                            IsOnRoad = extractedSpawn.IsOnRoad,
                            SourceMaterialName = extractedSpawn.SourceMaterialName
                        };

                        if (SpawnPointUpdater.UpdateSpawnPoint(_workingDirectory, spawnPoint))
                        {
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                $"Spawn point updated at ({spawnPoint.X:F1}, {spawnPoint.Y:F1}, {spawnPoint.Z:F1})");
                        }
                    }
                    else
                    {
                        PubSubChannel.SendMessage(PubSubMessageType.Warning,
                            "Could not extract spawn point from terrain generation");
                    }
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"[Perf] SpawnPoint handling: {taskStopwatch.ElapsedMilliseconds}ms");
                });

                postGenStopwatch.Stop();
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"[Perf] Total post-generation tasks: {postGenStopwatch.ElapsedMilliseconds}ms");
                Snackbar.Add("Post-processing complete!", Severity.Success);
                
                // Write log files for debugging
                WriteTerrainGenerationLogs();
            }
            else
            {
                Snackbar.Add("Terrain generation failed. Check errors for details.", Severity.Error);
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
        _workingDirectory = string.Empty;
        _levelName = string.Empty;
        _hasWorkingDirectory = false;
        _hasExistingTerrainSettings = false;
        _terrainMaterials.Clear();
        _errors.Clear();
        _warnings.Clear();
        _messages.Clear();
        _heightmapPath = null;
        _terrainSize = 2048;
        _maxHeight = 500.0f;
        _metersPerPixel = 1.0f;
        _terrainName = "theTerrain";
        _terrainBaseHeight = 0.0f;
        _updateTerrainBlock = true;
        _enableCrossMaterialHarmonization = true;
        _presetImporter?.Reset();
        _presetExporter?.Reset();

        // Reset GeoTIFF fields
        _heightmapSourceType = HeightmapSourceType.Png;
        _geoTiffPath = null;
        _geoTiffDirectory = null;
        _cropAnchor = CropAnchor.Center;
        _cropResult = null;
        _canFetchOsmData = false;
        _osmBlockedReason = null;
        _geoTiffValidationResult = null;
        ClearGeoMetadata(); // This also cleans up cached combined GeoTIFF

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

    /// <summary>
    ///     Saves a rasterized layer map to a PNG file.
    /// </summary>
    /// <param name="layerMap">The layer map data (byte[height, width]).</param>
    /// <param name="debugPath">The debug output directory.</param>
    /// <param name="materialName">The material name (used for filename).</param>
    /// <returns>The path to the saved PNG file.</returns>
    private async Task<string> SaveLayerMapToPng(byte[,] layerMap, string debugPath, string materialName)
    {
        var height = layerMap.GetLength(0);
        var width = layerMap.GetLength(1);

        // Sanitize material name for filename
        var safeName = string.Join("_", materialName.Split(Path.GetInvalidFileNameChars()));
        var filePath = Path.Combine(debugPath, $"{safeName}_osm_layer.png");

        using var image = new Image<L8>(width, height);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            image[x, y] = new L8(layerMap[y, x]);

        await image.SaveAsPngAsync(filePath);

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Saved OSM layer map: {Path.GetFileName(filePath)}");

        return filePath;
    }

    /// <summary>
    ///     Writes terrain generation log files for debugging.
    /// </summary>
    private void WriteTerrainGenerationLogs()
    {
        if (string.IsNullOrEmpty(_workingDirectory))
            return;

        try
        {
            // Write all messages to Log_TerrainGen.txt
            if (_messages.Any())
            {
                var messagesPath = Path.Combine(_workingDirectory, "Log_TerrainGen.txt");
                File.WriteAllLines(messagesPath, _messages);
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Terrain generation log written to: {Path.GetFileName(messagesPath)}");
            }

            // Write warnings to separate file
            if (_warnings.Any())
            {
                var warningsPath = Path.Combine(_workingDirectory, "Log_TerrainGen_Warnings.txt");
                File.WriteAllLines(warningsPath, _warnings);
            }

            // Write errors to separate file
            if (_errors.Any())
            {
                var errorsPath = Path.Combine(_workingDirectory, "Log_TerrainGen_Errors.txt");
                File.WriteAllLines(errorsPath, _errors);
            }
        }
        catch (Exception ex)
        {
            // Don't fail terrain generation just because logging failed
            Snackbar.Add($"Could not write log files: {ex.Message}", Severity.Warning);
        }
    }
}