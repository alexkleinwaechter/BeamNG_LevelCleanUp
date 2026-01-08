using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.LogicCopyForest;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Color = MudBlazor.Color;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class CopyForestBrushes
{
    private readonly string _fileSelectTitle = "Select the target level to copy forest brushes to";
    private readonly string _fileSelectTitleCopyFrom = "Select the zipped source level with forest brushes";

    private readonly Func<FileInfo, string> converter = p => p?.Name;
    private Anchor _anchor;
    private bool _openDrawer;
    private string _searchString = string.Empty;
    private HashSet<GridFileListItem> _selectedItems = new();
    private Snackbar _staticSnackbar;
    private Snackbar _unzipSnackbarCopyFrom;
    private string height;
    private MudTable<GridFileListItem> table;
    private string width;

    [Parameter]
    [SupplyParameterFromQuery(Name = "wizardMode")]
    public bool WizardMode { get; set; }

    private CreateLevelWizardState WizardState { get; set; }
    private string _levelName { get; set; }
    private string _levelPath { get; set; }
    private string _levelNameCopyFrom { get; set; }
    private string _levelPathCopyFrom { get; set; }
    private List<string> _errors { get; set; } = new();
    private List<string> _messages { get; set; } = new();
    private List<string> _warnings { get; set; } = new();
    private BeamFileReader Reader { get; set; }
    private bool _fileSelectDisabled { get; set; }
    private bool _isLoadingMap { get; set; }
    private List<GridFileListItem> BindingListCopy { get; set; } = new();
    [AllowNull] private MudExpansionPanels FileSelect { get; set; }
    private bool _showDeployButton { get; set; }
    private CompressionLevel _compressionLevel { get; set; } = CompressionLevel.Optimal;
    private bool _showErrorLog { get; set; }
    private bool _showWarningLog { get; set; }
    private List<FileInfo> _vanillaLevels { get; set; } = new();
    private FileInfo _vanillaLevelSourceSelected { get; set; }
    private FileInfo _vanillaLevelTargetSelected { get; set; }
    private string _beamInstallDir { get; set; }
    private bool _copyCompleted { get; set; }
    private int _copiedBrushesCount { get; set; }
    private int _totalCopiedBrushesCount { get; set; }
    private string _lastCopiedSourceName { get; set; }
    private bool _showWizardSourceSelection { get; set; }
    private bool _isSelectingAnotherSource { get; set; }
    private string _initialWorkingDirectory { get; set; }

    /// <summary>
    /// Determines if file selection should be disabled
    /// </summary>
    private bool GetFileSelectDisabled()
    {
        if (_isLoadingMap)
            return true;

        if (!string.IsNullOrEmpty(_levelNameCopyFrom) && !string.IsNullOrEmpty(_levelName))
            return true;

        return _fileSelectDisabled;
    }

    /// <summary>
    /// Gets the element count for a forest brush
    /// </summary>
    private int GetElementCount(GridFileListItem item)
    {
        return item.CopyAsset?.ForestBrushInfo?.ReferencedItemDataNames?.Count ?? 0;
    }

    /// <summary>
    /// Gets the ForestItemData names as a tooltip string
    /// </summary>
    private string GetItemDataNames(GridFileListItem item)
    {
        var names = item.CopyAsset?.ForestBrushInfo?.ReferencedItemDataNames;
        if (names == null || !names.Any())
            return "No items";

        return string.Join("\n", names.Take(10)) + (names.Count > 10 ? $"\n...and {names.Count - 10} more" : "");
    }

    protected void InitializeVariables()
    {
        FileSelect?.CollapseAllAsync();
        _fileSelectDisabled = false;
        _isLoadingMap = false;
        _levelName = null;
        _levelNameCopyFrom = null;
        _levelPath = null;
        _levelPathCopyFrom = null;
        _errors = new List<string>();
        _warnings = new List<string>();
        _messages = new List<string>();
        _openDrawer = false;
        BindingListCopy = new List<GridFileListItem>();
        _showDeployButton = false;
        _vanillaLevelSourceSelected = null;
        _vanillaLevelTargetSelected = null;
        ZipFileHandler.WorkingDirectory = null;
        _selectedItems = new HashSet<GridFileListItem>();
        Reader = null;
        _copyCompleted = false;
        _copiedBrushesCount = 0;
        _totalCopiedBrushesCount = 0;
        _lastCopiedSourceName = null;
    }

    /// <summary>
    /// Resets the entire page state and allows selecting different maps
    /// </summary>
    protected async Task ResetPage()
    {
        Reader?.Reset();
        InitializeVariables();

        await FileSelect.CollapseAllAsync();
        await Task.Delay(100);

        if (FileSelect.Panels.Count > 1)
            await FileSelect.Panels[1].ExpandAsync();

        StateHasChanged();
        Snackbar.Add("Page reset successfully. You can now select different maps.", Severity.Success);
    }

    /// <summary>
    /// Resets only the source map state, keeping the target map intact for copying from another source
    /// </summary>
    protected async Task ResetSourceMap()
    {
        var savedLevelName = _levelName;
        var savedLevelPath = _levelPath;
        var savedVanillaTarget = _vanillaLevelTargetSelected;
        var savedShowDeployButton = _showDeployButton;

        _levelNameCopyFrom = null;
        _levelPathCopyFrom = null;
        _vanillaLevelSourceSelected = null;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _copyCompleted = false;
        _searchString = string.Empty;

        _errors = new List<string>();
        _warnings = new List<string>();

        _levelName = savedLevelName;
        _levelPath = savedLevelPath;
        _vanillaLevelTargetSelected = savedVanillaTarget;
        ZipFileHandler.WorkingDirectory = _initialWorkingDirectory;
        _showDeployButton = savedShowDeployButton;

        _isSelectingAnotherSource = true;
        _fileSelectDisabled = false;
        _isLoadingMap = false;

        await FileSelect.CollapseAllAsync();
        await Task.Delay(100);

        if (FileSelect.Panels.Count > 1)
            await FileSelect.Panels[1].ExpandAsync();

        StateHasChanged();
        Snackbar.Add($"Ready to select another source map. Target: {_levelName}", Severity.Info);
    }

    protected async Task FileSelectedCopyFrom(string file)
    {
        if (_isSelectingAnotherSource)
        {
            await LoadNewSourceMapStandaloneMode(file);
            return;
        }

        InitializeVariables();
        _isLoadingMap = true;
        ZipFileHandler.WorkingDirectory = Path.GetDirectoryName(file);
        _initialWorkingDirectory = ZipFileHandler.WorkingDirectory;
        await Task.Run(() =>
        {
            try
            {
                _unzipSnackbarCopyFrom = Snackbar.Add("Unzipping source level...", Severity.Normal,
                    config => { config.VisibleStateDuration = int.MaxValue; });
                _levelPathCopyFrom =
                    ZipFileHandler.ExtractToDirectory(
                        Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file)), "_copyFrom", true);
                Reader = new BeamFileReader(_levelPathCopyFrom, null);
                _levelNameCopyFrom = Reader.GetLevelName();
                Snackbar.Add("Unzipping source level finished", Severity.Success);
                Snackbar.Remove(_unzipSnackbarCopyFrom);
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
            finally
            {
                _isLoadingMap = false;
                _fileSelectDisabled = false;
            }
        });
    }

    /// <summary>
    /// Loads a new source map in standalone mode while preserving target state
    /// </summary>
    private async Task LoadNewSourceMapStandaloneMode(string filePath)
    {
        _isLoadingMap = true;
        StateHasChanged();

        try
        {
            _staticSnackbar = Snackbar.Add("Extracting new source level...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            var fileInWorkingDir = Path.Join(_initialWorkingDirectory, Path.GetFileName(filePath));
            if (!filePath.Equals(fileInWorkingDir, StringComparison.OrdinalIgnoreCase))
                File.Copy(filePath, fileInWorkingDir, true);

            var copyFromPath = Path.Join(_initialWorkingDirectory, "_copyFrom");
            if (Directory.Exists(copyFromPath))
                Directory.Delete(copyFromPath, true);

            await Task.Run(() =>
            {
                _levelPathCopyFrom = ZipFileHandler.ExtractToDirectory(fileInWorkingDir, "_copyFrom", true);
                var tempReader = new BeamFileReader(_levelPathCopyFrom, null);
                _levelNameCopyFrom = tempReader.GetLevelName();
            });

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add($"Source level loaded: {_levelNameCopyFrom}", Severity.Success);

            _isSelectingAnotherSource = false;
            await ScanAssets();

            if (FileSelect?.Panels?.Count > 1)
                await FileSelect.Panels[1].CollapseAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Remove(_staticSnackbar);
            ShowException(ex);
        }
        finally
        {
            _isLoadingMap = false;
            StateHasChanged();
        }
    }

    protected async Task OnVanillaSourceSelected(FileInfo file)
    {
        if (file == null)
        {
            _vanillaLevelSourceSelected = null;
            return;
        }

        if (_isSelectingAnotherSource)
        {
            _vanillaLevelSourceSelected = file;
            var target = Path.Join(ZipFileHandler.WorkingDirectory, file.Name);
            PubSubChannel.SendMessage(PubSubMessageType.Info, $"Copying {file.Name} to working directory...");

            try
            {
                File.Copy(file.FullName, target, true);
                await LoadNewSourceMapStandaloneMode(target);
            }
            catch (Exception ex)
            {
                ShowException(ex);
                PubSubChannel.SendMessage(PubSubMessageType.Error, $"Failed to copy vanilla level: {ex.Message}");
            }

            return;
        }

        InitializeVariables();
        _isLoadingMap = true;
        SetDefaultWorkingDirectory();
        _vanillaLevelSourceSelected = file;
        var targetPath = Path.Join(ZipFileHandler.WorkingDirectory, _vanillaLevelSourceSelected.Name);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Copy {_vanillaLevelSourceSelected.Name} to {targetPath}");
        File.Copy(_vanillaLevelSourceSelected.FullName, targetPath, true);
        await FileSelectedCopyFrom(targetPath);
    }

    protected async Task OnVanillaTargetSelected(FileInfo file)
    {
        if (file == null)
        {
            _vanillaLevelTargetSelected = null;
            return;
        }

        _isLoadingMap = true;
        SetDefaultWorkingDirectory();
        _vanillaLevelTargetSelected = file;
        var target = Path.Join(ZipFileHandler.WorkingDirectory, _vanillaLevelTargetSelected.Name);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Copy {_vanillaLevelTargetSelected.Name} to {target}");
        File.Copy(_vanillaLevelTargetSelected.FullName, target, true);
        await FileSelected(target, false);
    }

    protected async Task FileSelected(string file, bool isFolder)
    {
        _isLoadingMap = true;

        if (_vanillaLevelTargetSelected == null)
        {
            ZipFileHandler.WorkingDirectory = isFolder ? file : Path.GetDirectoryName(file);
        }
        else if (!isFolder && ZipFileHandler.WorkingDirectory != Path.GetDirectoryName(file))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Copy target level to {ZipFileHandler.WorkingDirectory} ...");
            try
            {
                File.Copy(file, Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file)), true);
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Copy target level finished");
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Error copy target level to working directory: {ex.Message}");
            }
        }

        await Task.Run(() =>
        {
            try
            {
                _staticSnackbar = Snackbar.Add("Unzipping target level...", Severity.Normal,
                    config => { config.VisibleStateDuration = int.MaxValue; });
                if (!isFolder)
                    _levelPath =
                        ZipFileHandler.ExtractToDirectory(
                            Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file)), "_unpacked");
                else
                    _levelPath = ZipFileHandler.GetNamePath(file);

                Reader = new BeamFileReader(_levelPath, null);
                _levelName = Reader.GetLevelName();
                Snackbar.Add("Unzipping target level finished", Severity.Success);
                Snackbar.Remove(_staticSnackbar);
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
            finally
            {
                _isLoadingMap = false;
                _fileSelectDisabled = false;
            }
        });

        await ScanAssets();

        if (FileSelect.Panels.Count > 2)
            await FileSelect.Panels[2].CollapseAsync();
    }

    protected void SetBeamInstallDir(string file)
    {
        if (file != Steam.BeamInstallDir)
        {
            Steam.BeamInstallDir = file;
            GetVanillaLevels();
        }
    }

    protected string GetBeamInstallDir()
    {
        if (Steam.BeamInstallDir != _beamInstallDir)
        {
            _beamInstallDir = Steam.GetBeamInstallDir();
            GetVanillaLevels();
        }

        return "BeamNG install directory: " + _beamInstallDir;
    }

    protected async Task ScanAssets()
    {
        _fileSelectDisabled = true;
        await Task.Run(() =>
        {
            try
            {
                Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);
                Reader.ReadForestBrushesForCopy();
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
            finally
            {
                _fileSelectDisabled = false;
            }
        });
        FillCopyList();
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Done! Scanning Forest Brushes finished. Please select brushes to copy.");

        await InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        var consumer = Task.Run(async () =>
        {
            while (!StaticVariables.ApplicationExitRequest && await PubSubChannel.ch.Reader.WaitToReadAsync())
            {
                var msg = await PubSubChannel.ch.Reader.ReadAsync();
                if (!_messages.Contains(msg.Message) && !_errors.Contains(msg.Message))
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
            }
        });
    }

    protected override async Task OnParametersSetAsync()
    {
        if (WizardMode)
        {
            WizardState = CreateLevel.GetWizardState();

            if (WizardState == null || !WizardState.IsActive)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    "Wizard state not available. Please start from the Create Level page.");
                return;
            }

            await LoadLevelsFromWizardState();
        }

        await base.OnParametersSetAsync();
    }

    private async Task LoadLevelsFromWizardState()
    {
        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Wizard mode: Loading levels automatically...");

            _beamInstallDir = Steam.GetBeamInstallDir();
            GetVanillaLevels();

            var sourceLevelPath = WizardState.SourceLevelPath;
            _levelNameCopyFrom = WizardState.SourceLevelName;
            _levelPathCopyFrom = ZipFileHandler.GetLevelPath(sourceLevelPath);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Source level path: {_levelPathCopyFrom}");

            var targetLevelRootPath = WizardState.TargetLevelRootPath;
            _levelName = WizardState.LevelName;

            var targetLevelNamePath = targetLevelRootPath;
            _levelPath = Directory.GetParent(targetLevelRootPath)?.FullName;

            if (_levelPath == null || !Directory.Exists(_levelPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Target level path not found: {_levelPath}");
                return;
            }

            if (string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory))
            {
                var unpackedIndex = _levelPath.IndexOf("_unpacked", StringComparison.OrdinalIgnoreCase);
                if (unpackedIndex > 0)
                {
                    ZipFileHandler.WorkingDirectory = _levelPath.Substring(0, unpackedIndex - 1);
                }
                else
                {
                    var levelsParent = Directory.GetParent(_levelPath);
                    if (levelsParent != null)
                        ZipFileHandler.WorkingDirectory = Directory.GetParent(levelsParent.FullName)?.FullName;
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Target level path: {_levelPath}");
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Working directory: {ZipFileHandler.WorkingDirectory}");

            Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);

            await ScanAssets();
            await InvokeAsync(StateHasChanged);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Wizard mode: Levels loaded successfully");
        }
        catch (Exception ex)
        {
            ShowException(ex);
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to load levels in wizard mode: {ex.Message}");
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CopyDialogWizardMode()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var parameters = new DialogParameters();

        var messageText = $"Copy {_selectedItems.Count} forest brush(es) to {WizardState.LevelName}?";

        parameters.Add("ContentText", messageText);
        parameters.Add("ButtonText", "Copy Brushes");
        parameters.Add("Color", Color.Primary);

        var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Forest Brushes", parameters, options);
        var result = await dialog.Result;

        if (!result.Canceled)
        {
            _staticSnackbar = Snackbar.Add("Copying forest brushes...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            var copyCount = _selectedItems.Count;
            var sourceName = _levelNameCopyFrom;

            await Task.Run(() =>
            {
                var selected = _selectedItems.Select(y => y.Identifier).ToList();
                Reader.DoCopyAssets(selected);
            });

            Snackbar.Remove(_staticSnackbar);

            _copyCompleted = true;
            _copiedBrushesCount = copyCount;
            _totalCopiedBrushesCount += copyCount;
            _lastCopiedSourceName = sourceName;

            StateHasChanged();
        }
    }

    private void CancelWizard()
    {
        Navigation.NavigateTo("/CreateLevel");
    }

    /// <summary>
    /// Resets source map state in wizard mode to allow selecting another source
    /// </summary>
    private void ResetSourceMapWizardMode()
    {
        _levelNameCopyFrom = null;
        _levelPathCopyFrom = null;
        _vanillaLevelSourceSelected = null;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _copyCompleted = false;
        _searchString = string.Empty;

        _errors = new List<string>();
        _warnings = new List<string>();

        _fileSelectDisabled = false;
        _isLoadingMap = false;

        _showWizardSourceSelection = true;

        StateHasChanged();

        Snackbar.Add($"Select a new source map. Target: {_levelName}", Severity.Info);
    }

    /// <summary>
    /// Handles file selection in wizard mode source selection
    /// </summary>
    private async Task OnWizardSourceFileSelected(string filePath)
    {
        await LoadNewSourceMapWizardMode(filePath);
    }

    /// <summary>
    /// Handles vanilla level selection in wizard mode source selection
    /// </summary>
    private async Task OnWizardVanillaSourceSelected(FileInfo file)
    {
        if (file == null)
        {
            _vanillaLevelSourceSelected = null;
            return;
        }

        _vanillaLevelSourceSelected = file;

        var target = Path.Join(ZipFileHandler.WorkingDirectory, file.Name);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Copying {file.Name} to working directory...");

        try
        {
            File.Copy(file.FullName, target, true);
            await LoadNewSourceMapWizardMode(target);
        }
        catch (Exception ex)
        {
            ShowException(ex);
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Failed to copy vanilla level: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads a new source map in wizard mode
    /// </summary>
    private async Task LoadNewSourceMapWizardMode(string filePath)
    {
        _isLoadingMap = true;
        StateHasChanged();

        try
        {
            _staticSnackbar = Snackbar.Add("Extracting new source level...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            var fileInWorkingDir = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(filePath));

            var sourceDir = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
            var workingDir = Path.GetFullPath(ZipFileHandler.WorkingDirectory ?? "");

            if (!sourceDir.Equals(workingDir, StringComparison.OrdinalIgnoreCase))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copying source level to working directory: {ZipFileHandler.WorkingDirectory}");
                File.Copy(filePath, fileInWorkingDir, true);
            }

            var copyFromPath = Path.Join(ZipFileHandler.WorkingDirectory, "_copyFrom");
            if (Directory.Exists(copyFromPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Cleaning up previous source level...");
                Directory.Delete(copyFromPath, true);
            }

            await Task.Run(() =>
            {
                _levelPathCopyFrom = ZipFileHandler.ExtractToDirectory(
                    fileInWorkingDir,
                    "_copyFrom",
                    true);

                _levelPathCopyFrom = ZipFileHandler.GetLevelPath(_levelPathCopyFrom);

                var tempReader = new BeamFileReader(_levelPathCopyFrom, null);
                _levelNameCopyFrom = tempReader.GetLevelName();
            });

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add($"Source level loaded: {_levelNameCopyFrom}", Severity.Success);

            _showWizardSourceSelection = false;

            await ScanAssetsWizardMode();
        }
        catch (Exception ex)
        {
            Snackbar.Remove(_staticSnackbar);
            ShowException(ex);
            PubSubChannel.SendMessage(PubSubMessageType.Error, $"Failed to load source level: {ex.Message}");
        }
        finally
        {
            _isLoadingMap = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// Scans assets in wizard mode (source changed, target stays same)
    /// </summary>
    private async Task ScanAssetsWizardMode()
    {
        _fileSelectDisabled = true;
        await Task.Run(() =>
        {
            try
            {
                Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);
                Reader.ReadForestBrushesForCopy();
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
            finally
            {
                _fileSelectDisabled = false;
            }
        });

        BindingListCopy = new List<GridFileListItem>();
        FillCopyList();

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found {BindingListCopy.Count} forest brushes in {_levelNameCopyFrom}. Select brushes to copy.");

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Finishes the wizard and navigates back to CreateLevel
    /// </summary>
    private void FinishWizard()
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Completed! Copied {_totalCopiedBrushesCount} forest brush(es) to {WizardState?.LevelName}.");

        Navigation.NavigateTo("/CreateLevel");
    }

    private void OpenDrawer(Anchor anchor, PubSubMessageType msgType)
    {
        _showErrorLog = msgType == PubSubMessageType.Error;
        _showWarningLog = msgType == PubSubMessageType.Warning;
        _openDrawer = true;
        _anchor = anchor;

        switch (anchor)
        {
            case Anchor.Start:
                width = "300px";
                height = "100%";
                break;
            case Anchor.End:
                width = "400px";
                height = "100%";
                break;
            case Anchor.Bottom:
                width = "100%";
                height = "200px";
                break;
            case Anchor.Top:
                width = "100%";
                height = "350px";
                break;
        }
    }

    private void ShowException(Exception ex)
    {
        var message = ex.InnerException != null ? ex.Message + $" {ex.InnerException}" : ex.Message;
        Snackbar.Add(message, Severity.Error);
        _errors.Add(message);
    }

    private void FillCopyList()
    {
        foreach (var asset in Reader.GetCopyList().Where(x => x.CopyAssetType == CopyAssetType.ForestBrush))
        {
            var item = new GridFileListItem
            {
                Identifier = asset.Identifier,
                AssetType = asset.CopyAssetType.ToString(),
                FullName = asset.Name,
                SizeMb = asset.SizeMb,
                Duplicate = asset.Duplicate,
                DuplicateFrom = asset.DuplicateFrom,
                CopyAsset = asset
            };
            BindingListCopy.Add(item);
        }
    }

    private string GetFileSelectTitle()
    {
        if (!string.IsNullOrEmpty(_levelName))
            return $"{_fileSelectTitle} > {_levelName}";

        return $"{_fileSelectTitle}";
    }

    private string GetFileSelectTitleCopyFrom()
    {
        if (!string.IsNullOrEmpty(_levelNameCopyFrom))
            return $"{_fileSelectTitleCopyFrom} > {_levelNameCopyFrom}";

        return $"{_fileSelectTitleCopyFrom}";
    }

    private async Task<TableData<GridFileListItem>> LoadServerData(TableState state, CancellationToken token)
    {
        var items = BindingListCopy.Where(element =>
        {
            if (string.IsNullOrWhiteSpace(_searchString))
                return true;
            if (element.FullName.Contains(_searchString, StringComparison.OrdinalIgnoreCase))
                return true;
            if (element.AssetType.Contains(_searchString, StringComparison.OrdinalIgnoreCase))
                return true;
            if (element.SizeMb.ToString().Contains(_searchString, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        });

        return new TableData<GridFileListItem>
        {
            Items = items,
            TotalItems = items.Count()
        };
    }

    private void OnSearch(string text)
    {
        _searchString = text;
        table.ReloadServerData();
    }

    private async Task CopyDialog()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var parameters = new DialogParameters();

        var messageText = $"Do you really want to copy {_selectedItems.Count} forest brush(es)? " +
                          "This process will copy brush definitions and all associated shape files, materials, and textures. " +
                          "Please always use a copy of your project with this tool!";

        parameters.Add("ContentText", messageText);
        parameters.Add("ButtonText", "Copy");
        parameters.Add("Color", Color.Error);
        var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Forest Brushes", parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {
            _staticSnackbar = Snackbar.Add("Forest brushes copy in process. Please be patient.", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            var copyCount = _selectedItems.Count;
            var sourceName = _levelNameCopyFrom;

            await Task.Run(() =>
            {
                var selected = _selectedItems.Select(y => y.Identifier).ToList();
                Reader.DoCopyAssets(selected);
                _showDeployButton = true;
            });
            Snackbar.Remove(_staticSnackbar);

            Reader.WriteLogFile(_warnings, "Log_ForestBrushCopy_Warnings");
            Reader.WriteLogFile(_errors, "Log_ForestBrushCopy_Errors");

            _copyCompleted = true;
            _copiedBrushesCount = copyCount;
            _lastCopiedSourceName = sourceName;

            StateHasChanged();
        }
    }

    private async Task ZipAndDeploy()
    {
        var path = string.Empty;
        if (!string.IsNullOrEmpty(_levelPath))
            try
            {
                path = ZipFileHandler.GetLastUnpackedPath();
                _staticSnackbar = Snackbar.Add("Zipping the deployment file. Please be patient.", Severity.Normal,
                    config => { config.VisibleStateDuration = int.MaxValue; });
                await Task.Run(() => { ZipFileHandler.BuildDeploymentFile(path, _levelName, _compressionLevel); });
                Snackbar.Remove(_staticSnackbar);
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
    }

    private void GetVanillaLevels()
    {
        if (!string.IsNullOrEmpty(_beamInstallDir))
        {
            var dir = Path.Join(Steam.BeamInstallDir, Constants.BeamMapPath);
            try
            {
                _vanillaLevels = Directory.GetFiles(dir)
                    .Where(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .Select(x => new FileInfo(x)).ToList();
                StateHasChanged();
            }
            catch (Exception ex)
            {
            }
        }
    }

    public void SetDefaultWorkingDirectory()
    {
        if (string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory) ||
            (_vanillaLevelSourceSelected != null && _vanillaLevelTargetSelected != null))
        {
            ZipFileHandler.WorkingDirectory =
                Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BeamNgMT");
            Directory.CreateDirectory(ZipFileHandler.WorkingDirectory);
        }
    }
}
