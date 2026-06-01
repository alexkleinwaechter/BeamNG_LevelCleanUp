using System.Diagnostics;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicBasecolorManager;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Objects.MtSettings;
using Grille.BeamNG.IO.Binary;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class BasecolorManager
{
    private const string OperationLoadLevel = "load-level";
    private const string OperationSaveSettings = "save-settings";
    private const string OperationPreview = "preview";
    private const string OperationPaintMode = "paint-mode";
    private const string OperationBaseColorMode = "basecolor-mode";
    private const string OperationFetchTile = "fetch-tile";

    private readonly BasecolorManagerService _service = new();
    private readonly PaintModeApplier _paintModeApplier = new();
    private readonly BaseColorModeApplier _baseColorModeApplier = new();
    private readonly MapTileOverlayService _tileOverlayService = new();

    private Anchor _anchor;
    private bool _isLoading;
    private bool _isApplying;
    private bool _openDrawer;
    private bool _showErrorLog;
    private bool _showWarningLog;
    private string _levelPath = string.Empty;
    private string _levelName = string.Empty;
    private string _materialsJsonPath = string.Empty;
    private string _previewDataUri = string.Empty;
    private string _busyOperation = string.Empty;
    private string _busyMessage = string.Empty;
    private string _searchString = string.Empty;
    private string _drawerWidth = "100%";
    private string _drawerHeight = "50%";
    private int _terrainSize;
    private TerrainV9Binary? _terrain;
    private MtSettings _settings = new();
    private List<CopyAsset> _paintMaterials = new();
    private List<CopyAsset> _basecolorMaterials = new();
    private List<string> _availableOsmLayerMaskPaths = new();
    private List<string> _errors = new();
    private List<string> _warnings = new();
    private List<string> _messages = new();

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    private bool HasLevel => !string.IsNullOrWhiteSpace(_levelPath);
    private bool IsBusy => _isLoading || _isApplying;
    private bool HasBusyMessage => IsBusy && !string.IsNullOrWhiteSpace(_busyMessage);
    private bool HasGeoReference => MapTileOverlayService.HasUsableGeoReference(_settings.GeoReferenceSettings);
    private bool CanFetchTileOverlay => HasLevel && HasGeoReference && !IsBusy &&
                                        !string.IsNullOrWhiteSpace(_settings.BasecolorModeSettings.OverlaySettings.SelectedTileProvider);
    private int GlobalOverlayBlendPercent => (int)Math.Round(Math.Clamp(_settings.BasecolorModeSettings.OverlaySettings.GlobalBlend, 0.0, 1.0) * 100.0);

    protected override void OnInitialized()
    {
        Task.Run(ReadPubSubMessages);
    }

    private async Task OnFolderSelected(string folder)
    {
        _isLoading = true;
        _busyOperation = OperationLoadLevel;
        _busyMessage = "Loading terrain materials...";
        ClearMessages();
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        try
        {
            var result = await Task.Run(() => _service.LoadLevel(folder));
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    PubSubChannel.SendMessage(PubSubMessageType.Error, result.ErrorMessage);
                return;
            }

            _levelPath = result.LevelPath;
            _levelName = result.LevelName;
            _materialsJsonPath = result.MaterialsJsonPath;
            _terrain = result.Terrain;
            _terrainSize = result.TerrainSize;
            _settings = result.Settings;
            EnsureOverlayDefaults();
            LoadAvailableOsmLayerMasks();
            _paintMaterials = result.PaintMaterials;
            _basecolorMaterials = result.BasecolorMaterials;
            _previewDataUri = result.PreviewDataUri;
        }
        finally
        {
            _isLoading = false;
            ClearBusyOperation();
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ActivatePaintMode()
    {
        if (!CanApply())
            return;

        await RunBusyOperation(OperationPaintMode, "Applying Paint Mode...", async () =>
        {
            await Task.Run(() =>
            {
                UpdateSettingsFromMaterialLists();
                _paintModeApplier.Apply(_levelPath, _levelName, _materialsJsonPath, _paintMaterials, _settings);
                if (PaintModeHasUsableMaterialSettings())
                    SyncBasecolorMaterialsFromPaintMode();
                if (_terrain != null)
                    _previewDataUri = _service.BuildPreview(_terrain, _basecolorMaterials, _settings);
            });
        });
    }

    private async Task ActivateBaseColorMode()
    {
        if (!CanApply() || _terrain == null)
            return;

        await RunBusyOperation(OperationBaseColorMode, "Generating BaseColor Mode maps...", async () =>
        {
            await Task.Run(() =>
            {
                if (_settings.CurrentMode == BasecolorMode.PaintMode && PaintModeHasUsableMaterialSettings())
                    SyncBasecolorMaterialsFromPaintMode();
                else
                    UpdateSettingsFromMaterialLists();

                _baseColorModeApplier.Apply(
                    _levelPath,
                    _levelName,
                    _materialsJsonPath,
                    _terrain,
                    _basecolorMaterials,
                    _settings,
                    _settings.BasecolorModeSettings.GenerateHeight,
                    _settings.BasecolorModeSettings.NormalStrength,
                    _settings.BasecolorModeSettings.AoRadius,
                    _settings.BasecolorModeSettings.AoIntensity,
                    BasecolorManagerService.CreateOverlayOptions(_settings));
                _previewDataUri = _service.BuildPreview(_terrain, _basecolorMaterials, _settings);
            });
        });
    }

    private bool PaintModeHasUsableMaterialSettings()
    {
        return BasecolorManagerService.HasUsableMaterialSettings(
            _paintMaterials.Select(MtTerrainMaterialSetting.FromCopyAsset));
    }

    private void SyncBasecolorMaterialsFromPaintMode()
    {
        var existingOverlayBlends = _basecolorMaterials.ToDictionary(GetMaterialKey, x => x.BaseColorOverlayBlend, StringComparer.OrdinalIgnoreCase);
        _basecolorMaterials = _paintMaterials
            .Select(MtTerrainMaterialSetting.FromCopyAsset)
            .Select(x => x.ToCopyAsset())
            .ToList();
        foreach (var material in _basecolorMaterials)
        {
            if (existingOverlayBlends.TryGetValue(GetMaterialKey(material), out var blend))
                material.BaseColorOverlayBlend = blend;
        }

        _settings.BasecolorModeSettings.Materials = _basecolorMaterials
            .Select(MtTerrainMaterialSetting.FromCopyAsset)
            .ToList();
    }

    private async Task SaveSettings()
    {
        if (!HasLevel)
            return;

        await RunBusyOperation(OperationSaveSettings, "Saving Basecolor Manager settings...", async () =>
        {
            UpdateSettingsFromMaterialLists();
            await Task.Run(() => _service.SaveSettings(_levelPath, _settings, _paintMaterials, _basecolorMaterials));
        });
    }

    private void OnMaterialColorChanged(CopyAsset asset, string value)
    {
        asset.BaseColorHex = NormalizeHexColor(value);
        SyncMatchingMaterial(asset);
        UpdateSettingsFromMaterialLists();
    }

    private void OnRoughnessPresetChanged(CopyAsset asset, TerrainRoughnessPreset value)
    {
        asset.RoughnessPreset = value;
        SyncMatchingMaterial(asset);
        UpdateSettingsFromMaterialLists();
    }

    private void OnRoughnessValueChanged(CopyAsset asset, int value)
    {
        asset.RoughnessValue = Math.Clamp(value, 0, 255);
        SyncMatchingMaterial(asset);
        UpdateSettingsFromMaterialLists();
    }

    private async Task OnOverlayImageSelected(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, "Selected overlay image could not be found.");
            return;
        }

        _settings.BasecolorModeSettings.OverlaySettings.SelectedImagePath = imagePath;
        _settings.BasecolorModeSettings.OverlaySettings.UseTileProvider = false;
        EnsureDefaultOverlayBlend();
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnTileProviderChanged(string providerName)
    {
        _settings.BasecolorModeSettings.OverlaySettings.SelectedTileProvider = providerName ?? string.Empty;
        var provider = MapTileOverlayService.Providers.FirstOrDefault(x => x.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        if (provider != null && HasLevel)
        {
            var cachedPath = Path.Join(_levelPath, "MT_Tiles", provider.FinalImageName);
            if (File.Exists(cachedPath))
            {
                _settings.BasecolorModeSettings.OverlaySettings.CachedTileImagePath = cachedPath;
                _settings.BasecolorModeSettings.OverlaySettings.UseTileProvider = true;
                EnsureDefaultOverlayBlend();
                await RefreshPreview();
            }
        }

        UpdateSettingsFromMaterialLists();
    }

    private async Task FetchSelectedTileOverlay()
    {
        if (!CanFetchTileOverlay)
            return;

        await RunBusyOperation(OperationFetchTile, "Fetching tile overlay...", async () =>
        {
            var providerName = _settings.BasecolorModeSettings.OverlaySettings.SelectedTileProvider;
            var imagePath = await _tileOverlayService.EnsureOverlayImageAsync(_levelPath, _settings.GeoReferenceSettings, providerName, _terrainSize);
            _settings.BasecolorModeSettings.OverlaySettings.CachedTileImagePath = imagePath;
            _settings.BasecolorModeSettings.OverlaySettings.UseTileProvider = true;
            EnsureDefaultOverlayBlend();
            UpdateSettingsFromMaterialLists();
            if (_terrain != null)
                _previewDataUri = await Task.Run(() => _service.BuildPreview(_terrain, _basecolorMaterials, _settings));
        });
    }

    private void OnGlobalOverlayBlendChanged(int value)
    {
        var blend = Math.Clamp(value, 0, 100) / 100.0;
        _settings.BasecolorModeSettings.OverlaySettings.GlobalBlend = blend;
        foreach (var material in _basecolorMaterials)
            material.BaseColorOverlayBlend = blend;
        UpdateSettingsFromMaterialLists();
    }

    private void OnMaterialOverlayBlendChanged(CopyAsset asset, int value)
    {
        asset.BaseColorOverlayBlend = Math.Clamp(value, 0, 100) / 100.0;
        UpdateSettingsFromMaterialLists();
    }

    private void SyncMatchingMaterial(CopyAsset source)
    {
        foreach (var target in _paintMaterials.Concat(_basecolorMaterials))
        {
            if (ReferenceEquals(source, target) || !IsSameMaterial(source, target))
                continue;

            target.BaseColorHex = source.BaseColorHex;
            target.RoughnessPreset = source.RoughnessPreset;
            target.RoughnessValue = source.RoughnessValue;
            target.CalculatedRoughnessValue = source.CalculatedRoughnessValue;
        }
    }

    private void UpdateSettingsFromMaterialLists()
    {
        _settings.BasecolorModeSettings.OsmLayerBlendExceptions ??= new List<MtOsmLayerBlendException>();
        _settings.PaintModeSettings.Materials = _paintMaterials
            .Select(MtTerrainMaterialSetting.FromCopyAsset)
            .ToList();
        _settings.BasecolorModeSettings.Materials = _basecolorMaterials
            .Select(MtTerrainMaterialSetting.FromCopyAsset)
            .ToList();
    }

    private void EnsureOverlayDefaults()
    {
        _settings.GeoReferenceSettings ??= new MtGeoReferenceSettings();
        _settings.BasecolorModeSettings ??= new MtBasecolorModeSettings();
        _settings.BasecolorModeSettings.OverlaySettings ??= new MtBasecolorOverlaySettings();
        _settings.BasecolorModeSettings.OsmLayerBlendExceptions ??= new List<MtOsmLayerBlendException>();
        if (string.IsNullOrWhiteSpace(_settings.BasecolorModeSettings.OverlaySettings.SelectedTileProvider))
            _settings.BasecolorModeSettings.OverlaySettings.SelectedTileProvider = "Google Satelite Only";
    }

    private void LoadAvailableOsmLayerMasks()
    {
        _availableOsmLayerMaskPaths = new List<string>();
        if (string.IsNullOrWhiteSpace(_levelPath))
            return;

        var osmLayerPath = Path.Join(_levelPath, "MT_TerrainGeneration", "osm_layer");
        if (!Directory.Exists(osmLayerPath))
            return;

        _availableOsmLayerMaskPaths = Directory.GetFiles(osmLayerPath, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task AddOsmLayerException()
    {
        EnsureOverlayDefaults();
        _settings.BasecolorModeSettings.OsmLayerBlendExceptions.Add(new MtOsmLayerBlendException
        {
            Enabled = true,
            AffectedBlendMultiplier = 0.0,
            BaseColorHex = "#808080",
            BaseColorStrength = 1.0,
            RoughnessValue = 128,
            RoughnessStrength = 1.0
        });

        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task RemoveOsmLayerException(MtOsmLayerBlendException exception)
    {
        EnsureOverlayDefaults();
        _settings.BasecolorModeSettings.OsmLayerBlendExceptions.Remove(exception);
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionPathChanged(MtOsmLayerBlendException exception, string path)
    {
        exception.ImagePath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exception.Name) && !string.IsNullOrWhiteSpace(exception.ImagePath))
            exception.Name = Path.GetFileNameWithoutExtension(exception.ImagePath);
        if (!string.IsNullOrWhiteSpace(exception.ImagePath))
            exception.Id = Path.GetFileNameWithoutExtension(exception.ImagePath);

        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionFileSelected(MtOsmLayerBlendException exception, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, "Selected OSM layer mask could not be found.");
            return;
        }

        if (!_availableOsmLayerMaskPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            _availableOsmLayerMaskPaths.Add(path);

        await OnOsmLayerExceptionPathChanged(exception, path);
    }

    private async Task OnOsmLayerExceptionNameChanged(MtOsmLayerBlendException exception, string name)
    {
        exception.Name = name ?? string.Empty;
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionEnabledChanged(MtOsmLayerBlendException exception, bool enabled)
    {
        exception.Enabled = enabled;
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionMultiplierChanged(MtOsmLayerBlendException exception, int percent)
    {
        exception.AffectedBlendMultiplier = Math.Clamp(percent, 0, 100) / 100.0;
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionOverrideBaseColorChanged(MtOsmLayerBlendException exception, bool enabled)
    {
        exception.OverrideBaseColor = enabled;
        if (string.IsNullOrWhiteSpace(exception.BaseColorHex))
            exception.BaseColorHex = "#808080";
        if (exception.BaseColorStrength <= 0)
            exception.BaseColorStrength = 1.0;

        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionBaseColorChanged(MtOsmLayerBlendException exception, string value)
    {
        exception.BaseColorHex = NormalizeHexColor(value);
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionBaseColorStrengthChanged(MtOsmLayerBlendException exception, int percent)
    {
        exception.BaseColorStrength = Math.Clamp(percent, 0, 100) / 100.0;
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionOverrideRoughnessChanged(MtOsmLayerBlendException exception, bool enabled)
    {
        exception.OverrideRoughness = enabled;
        exception.RoughnessValue = Math.Clamp(exception.RoughnessValue, 0, 255);
        if (exception.RoughnessStrength <= 0)
            exception.RoughnessStrength = 1.0;

        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionRoughnessValueChanged(MtOsmLayerBlendException exception, int value)
    {
        exception.RoughnessValue = Math.Clamp(value, 0, 255);
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private async Task OnOsmLayerExceptionRoughnessStrengthChanged(MtOsmLayerBlendException exception, int percent)
    {
        exception.RoughnessStrength = Math.Clamp(percent, 0, 100) / 100.0;
        UpdateSettingsFromMaterialLists();
        await RefreshPreview();
    }

    private IEnumerable<string> GetOsmLayerMaskChoices(MtOsmLayerBlendException exception)
    {
        var paths = _availableOsmLayerMaskPaths.ToList();
        if (!string.IsNullOrWhiteSpace(exception.ImagePath) && !paths.Contains(exception.ImagePath, StringComparer.OrdinalIgnoreCase))
            paths.Add(exception.ImagePath);

        return paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    private static int GetOsmLayerExceptionMultiplierPercent(MtOsmLayerBlendException exception)
    {
        return (int)Math.Round(Math.Clamp(exception.AffectedBlendMultiplier, 0.0, 1.0) * 100.0);
    }

    private static int GetOsmLayerExceptionBaseColorStrengthPercent(MtOsmLayerBlendException exception)
    {
        return (int)Math.Round(Math.Clamp(exception.BaseColorStrength, 0.0, 1.0) * 100.0);
    }

    private static int GetOsmLayerExceptionRoughnessStrengthPercent(MtOsmLayerBlendException exception)
    {
        return (int)Math.Round(Math.Clamp(exception.RoughnessStrength, 0.0, 1.0) * 100.0);
    }

    private static string GetOsmLayerMaskDisplayName(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileNameWithoutExtension(path);
    }

    private static string GetOsmLayerExceptionDisplayName(MtOsmLayerBlendException exception)
    {
        if (!string.IsNullOrWhiteSpace(exception.Name))
            return exception.Name;
        if (!string.IsNullOrWhiteSpace(exception.ImagePath))
            return Path.GetFileNameWithoutExtension(exception.ImagePath);

        return "New exception";
    }

    private void EnsureDefaultOverlayBlend()
    {
        EnsureOverlayDefaults();
        if (_settings.BasecolorModeSettings.OverlaySettings.GlobalBlend > 0 || _basecolorMaterials.Any(x => x.BaseColorOverlayBlend > 0))
            return;

        OnGlobalOverlayBlendChanged(50);
    }

    private static bool IsSameMaterial(CopyAsset source, CopyAsset target)
    {
        return Matches(source.TerrainMaterialInternalName, target.TerrainMaterialInternalName) ||
               Matches(source.TerrainMaterialName, target.TerrainMaterialName) ||
               Matches(source.Name, target.Name);
    }

    private static bool Matches(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMaterialKey(CopyAsset asset)
    {
        return asset.TerrainMaterialInternalName
               ?? asset.TerrainMaterialName
               ?? asset.Name
               ?? asset.Identifier.ToString();
    }

    private static int GetMaterialOverlayBlendPercent(CopyAsset asset)
    {
        return (int)Math.Round(Math.Clamp(asset.BaseColorOverlayBlend, 0.0, 1.0) * 100.0);
    }

    private static string NormalizeHexColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "#808080";

        value = value.Trim();
        if (!value.StartsWith("#"))
            value = "#" + value;

        return value.Length == 7 ? value : "#808080";
    }

    private async Task RefreshPreview()
    {
        if (_terrain == null)
            return;

        await RunBusyOperation(OperationPreview, "Refreshing BaseColor preview...", RefreshPreviewCore);
    }

    private async Task RefreshPreviewCore()
    {
        if (_terrain == null)
            return;

        _previewDataUri = await Task.Run(() => _service.BuildPreview(_terrain, _basecolorMaterials, _settings));
    }

    private bool CanApply()
    {
        return HasLevel && !string.IsNullOrWhiteSpace(_materialsJsonPath) && !IsBusy;
    }

    private IEnumerable<CopyAsset> FilterMaterials(IEnumerable<CopyAsset> materials)
    {
        if (string.IsNullOrWhiteSpace(_searchString))
            return materials;

        return materials.Where(x =>
            (x.TerrainMaterialName ?? string.Empty).Contains(_searchString, StringComparison.OrdinalIgnoreCase) ||
            (x.TerrainMaterialInternalName ?? string.Empty).Contains(_searchString, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSearch(string searchString)
    {
        _searchString = searchString ?? string.Empty;
    }

    private void OpenDrawer(Anchor anchor, PubSubMessageType msgType)
    {
        _anchor = anchor;
        _openDrawer = true;
        _showErrorLog = msgType == PubSubMessageType.Error;
        _showWarningLog = msgType == PubSubMessageType.Warning;
    }

    private void OpenLevelFolder()
    {
        if (!string.IsNullOrWhiteSpace(_levelPath))
            Process.Start("explorer.exe", _levelPath);
    }

    private async Task OpenBasecolorManagerHelpDialog()
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            CloseButton = true,
            CloseOnEscapeKey = true
        };

        await DialogService.ShowAsync<BasecolorManagerHelpDialog>(
            "Basecolor Manager Guide",
            options);
    }

    private void ResetPage()
    {
        _levelPath = string.Empty;
        _levelName = string.Empty;
        _materialsJsonPath = string.Empty;
        _previewDataUri = string.Empty;
        _terrainSize = 0;
        _terrain = null;
        _settings = new MtSettings();
        _paintMaterials = new List<CopyAsset>();
        _basecolorMaterials = new List<CopyAsset>();
        _availableOsmLayerMaskPaths = new List<string>();
        ClearBusyOperation();
        ClearMessages();
    }

    private async Task RunBusyOperation(string operation, string message, Func<Task> action)
    {
        if (_isApplying)
            return;

        _isApplying = true;
        _busyOperation = operation;
        _busyMessage = message;
        await InvokeAsync(StateHasChanged);
        await Task.Yield();

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Error, ex.InnerException != null ? $"{ex.Message} {ex.InnerException.Message}" : ex.Message);
        }
        finally
        {
            _isApplying = false;
            ClearBusyOperation();
            await InvokeAsync(StateHasChanged);
        }
    }

    private bool IsOperation(string operation)
    {
        return IsBusy && _busyOperation.Equals(operation, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearBusyOperation()
    {
        _busyOperation = string.Empty;
        _busyMessage = string.Empty;
    }

    private void ClearMessages()
    {
        _errors = new List<string>();
        _warnings = new List<string>();
        _messages = new List<string>();
        _openDrawer = false;
    }

    private async Task ReadPubSubMessages()
    {
        while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
        {
            var msg = await PubSubChannel.ch.Reader.ReadAsync();
            if (_messages.Contains(msg.Message) || _errors.Contains(msg.Message) || _warnings.Contains(msg.Message))
                continue;

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

            await InvokeAsync(StateHasChanged);
        }
    }
}
