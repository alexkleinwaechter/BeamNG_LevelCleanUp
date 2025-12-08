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
using MudBlazor;
using MudBlazor.Utilities;
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
    private bool _hasWorkingDirectory;
    private string? _heightmapPath;
    private bool _isGenerating;
    private bool _isLoading;
    private string _levelName = string.Empty;
    private float _maxHeight = 500.0f;
    private float _metersPerPixel = 1.0f;
    private bool _openDrawer;
    private bool _showErrorLog;
    private bool _showWarningLog;
    private float _terrainBaseHeight;
    private string _terrainName = "theTerrain";
    private int _terrainSize = 2048;
    private bool _updateTerrainBlock = true;
    private bool _enableCrossMaterialHarmonization = true;
    private string _workingDirectory = string.Empty;
    private TerrainPresetImporter? _presetImporter;
    private TerrainPresetExporter? _presetExporter;
    
    // GeoTIFF import fields
    private HeightmapSourceType _heightmapSourceType = HeightmapSourceType.Png;
    private string? _geoTiffPath;
    private string? _geoTiffDirectory;
    private GeoBoundingBox? _geoBoundingBox;
    private double? _geoTiffMinElevation;
    private double? _geoTiffMaxElevation;
    
    [AllowNull] private MudExpansionPanels FileSelect { get; set; }
    
    /// <summary>
    /// Enum to track which heightmap source type is selected
    /// </summary>
    private enum HeightmapSourceType
    {
        Png,
        GeoTiffFile,
        GeoTiffDirectory
    }

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
            HeightmapSourceType.GeoTiffDirectory => !string.IsNullOrEmpty(_geoTiffDirectory) && Directory.Exists(_geoTiffDirectory),
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
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ClearGeoMetadata()
    {
        _geoBoundingBox = null;
        _geoTiffMinElevation = null;
        _geoTiffMaxElevation = null;
    }

    private void OnHeightmapSourceTypeChanged(HeightmapSourceType newType)
    {
        _heightmapSourceType = newType;
        StateHasChanged();
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

    private void OnPresetImported(TerrainPresetResult result)
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
                    if (jsonNode["size"] != null) _terrainSize = jsonNode["size"]!.GetValue<int>();

                    // Extract terrain name from filename
                    _terrainName = Path.GetFileNameWithoutExtension(terrainJsonPath)
                        .Replace(".terrain", "");
                }
            }
        }
        catch
        {
            // Ignore errors, use defaults
        }
    }

    private void ScanTerrainMaterials(string levelPath)
    {
        var terrainMaterialsPath = Path.Join(levelPath, "art", "terrains", "main.materials.json");

        if (!File.Exists(terrainMaterialsPath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Terrain materials file not found at: {terrainMaterialsPath}");
            return;
        }

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
    /// Renormalizes material order values to be contiguous starting from 0.
    /// This is critical because the material index in the .ter file must match the order.
    /// </summary>
    private void RenormalizeMaterialOrder()
    {
        var sorted = _terrainMaterials.OrderBy(m => m.Order).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].Order = i;
        }
        
        _terrainMaterials.Clear();
        _terrainMaterials.AddRange(sorted);
    }

    /// <summary>
    /// Reorders materials so that those without layer maps (except the one at index 0) are moved to the end.
    /// Materials without layer maps at positions > 0 will never claim pixels in terrain generation,
    /// so they should be at the end to avoid confusion with material indices.
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
        {
            if (expectedOrder[i] != currentOrder[i])
            {
                needsReorder = true;
                break;
            }
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
    /// Handles the request from TerrainPresetExporter to reorder materials before export.
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

                foreach (var mat in orderedMaterials)
                {
                    RoadSmoothingParameters? roadParams = null;

                    if (mat.IsRoadMaterial)
                        // Build full parameters from preset with prominent parameter overrides
                        roadParams = mat.BuildRoadSmoothingParameters(debugPath);

                    materialDefinitions.Add(new MaterialDefinition(
                        mat.InternalName,
                        mat.LayerMapPath,
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

                // Set heightmap source based on selected type
                switch (_heightmapSourceType)
                {
                    case HeightmapSourceType.Png:
                        parameters.HeightmapPath = _heightmapPath;
                        break;
                    case HeightmapSourceType.GeoTiffFile:
                        parameters.GeoTiffPath = _geoTiffPath;
                        break;
                    case HeightmapSourceType.GeoTiffDirectory:
                        parameters.GeoTiffDirectory = _geoTiffDirectory;
                        break;
                }

                var outputPath = GetOutputPath();

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Starting terrain generation: {_terrainSize}x{_terrainSize}, {materialDefinitions.Count} materials...");

                success = await creator.CreateTerrainFileAsync(outputPath, parameters);
                
                // Capture geo-metadata from parameters (populated during GeoTIFF import)
                if (parameters.GeoBoundingBox != null)
                {
                    _geoBoundingBox = parameters.GeoBoundingBox;
                    _geoTiffMinElevation = parameters.GeoTiffMinElevation;
                    _geoTiffMaxElevation = parameters.GeoTiffMaxElevation;
                    
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"GeoTIFF Bounding Box for OSM Overpass: {_geoBoundingBox.ToOverpassBBox()}");
                }
                
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

                // Update TerrainBlock in items.level.json if requested
                if (_updateTerrainBlock)
                {
                    var terrainBlockUpdated = TerrainBlockUpdater.UpdateOrCreateTerrainBlock(
                        _workingDirectory,
                        _terrainName,
                        _terrainSize,
                        _maxHeight,
                        _terrainBaseHeight);

                    if (terrainBlockUpdated)
                        Snackbar.Add("TerrainBlock updated in items.level.json", Severity.Success);
                    else
                        Snackbar.Add("Could not update TerrainBlock - check warnings", Severity.Warning);
                }
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
        ClearGeoMetadata();

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