using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Color = MudBlazor.Color;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class CopyAssets
{
    // Wizard mode properties
    [Parameter]
    [SupplyParameterFromQuery(Name = "wizardMode")]
    public bool WizardMode { get; set; }

    /// <summary>
    ///     Wizard state reference when in wizard mode
    /// </summary>
    public CreateLevelWizardState WizardState { get; private set; }

    // Wizard tracking
    private int _totalCopiedAssetsCount { get; set; }
    private bool _showWizardSourceSelection { get; set; }

    private string _levelName { get; set; }
    private string _levelPath { get; set; }
    private string _levelNameCopyFrom { get; set; }
    private string _levelPathCopyFrom { get; set; }
    private string _beamLogFilePath { get; set; } = string.Empty;
    private List<string> _missingFiles { get; set; } = new();
    private List<string> _errors { get; set; } = new();
    private List<string> _messages { get; set; } = new();
    private List<string> _warnings { get; set; } = new();
    private Snackbar _staticSnackbar;
    private Snackbar _unzipSnackbarCopyFrom;
    private BeamFileReader Reader { get; set; }
    private readonly string _fileSelectTitleCopyFrom = "Select the zipped source level you want to copy from";
    private readonly string _fileSelectTitle = "Select the target level you want to copy to";
    private bool _fileSelectDisabled { get; set; }
    private bool _isLoadingMap { get; set; }
    private bool _fileSelectExpanded { get; set; }
    private bool _openDrawer;
    private Anchor _anchor;
    private string width;
    private string height;
    private List<GridFileListItem> BindingListCopy { get; set; } = new();
    private HashSet<GridFileListItem> _selectedItems = new();
    private bool _fixed_Header = true;
    [AllowNull] private MudExpansionPanels FileSelect { get; set; }
    private string _searchString = string.Empty;
    private string _labelFileSummaryShrink { get; set; } = string.Empty;
    private bool _showDeployButton { get; set; }
    private CompressionLevel _compressionLevel { get; set; } = CompressionLevel.Optimal;
    private bool _showErrorLog { get; set; }
    private bool _showWarningLog { get; set; }
    private List<FileInfo> _vanillaLevels { get; set; } = new();
    private FileInfo _vanillaLevelSourceSelected { get; set; }
    private FileInfo _vanillaLevelTargetSelected { get; set; }
    private string _beamInstallDir { get; set; }
    private MudTable<GridFileListItem> table;
    private bool _copyCompleted { get; set; }
    private int _copiedAssetsCount { get; set; }
    private string _lastCopiedSourceName { get; set; }
    private string _initialWorkingDirectory { get; set; }
    private bool _isSelectingAnotherSource { get; set; }

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
            WizardState.CurrentStep = 4;

            // Auto-load levels from wizard state
            await LoadLevelsFromWizardState();
        }
    }

    /// <summary>
    ///     Loads source and target levels from wizard state
    /// </summary>
    private async Task LoadLevelsFromWizardState()
    {
        if (WizardState == null) return;

        try
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Wizard mode: Loading levels automatically...");

            _beamInstallDir = GameDirectoryService.GetInstallDirectory();
            GetVanillaLevels();

            // Set source level from wizard state
            var sourceLevelPath = WizardState.SourceLevelPath;
            _levelNameCopyFrom = WizardState.SourceLevelName;
            _levelPathCopyFrom = ZipFileHandler.GetLevelPath(sourceLevelPath);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Source level path: {_levelPathCopyFrom}");

            // Set target level from wizard state
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

            // Set working directory
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

            _initialWorkingDirectory = ZipFileHandler.WorkingDirectory;

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Target level path: {_levelPath}");
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Working directory: {ZipFileHandler.WorkingDirectory}");

            // Initialize reader and scan assets
            Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);

            await ScanAssets();
            await InvokeAsync(StateHasChanged);

            _showDeployButton = true;

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

    /// <summary>
    ///     Updates wizard state after copying assets
    /// </summary>
    private void UpdateWizardStateAfterCopy()
    {
        if (WizardState == null) return;

        // Collect copied asset names
        var copiedAssetNames = _selectedItems
            .Select(x => $"{x.AssetType}: {x.FullName}")
            .ToList();

        // Accumulate assets (allow multiple copy operations)
        WizardState.CopiedAssets.AddRange(copiedAssetNames);
        WizardState.Step5_AssetsSelected = true;
        WizardState.CurrentStep = 4;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Copied {copiedAssetNames.Count} asset(s) to {WizardState.LevelName}");
    }

    /// <summary>
    ///     Copy dialog for wizard mode with state updates
    /// </summary>
    private async Task CopyDialogWizardMode()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var parameters = new DialogParameters();

        var messageText = $"Copy {_selectedItems.Count} asset(s) to {WizardState.LevelName}?";

        parameters.Add("ContentText", messageText);
        parameters.Add("ButtonText", "Copy Assets");
        parameters.Add("Color", Color.Primary);

        var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Assets", parameters, options);
        var result = await dialog.Result;

        if (!result.Canceled)
        {
            _staticSnackbar = Snackbar.Add("Copying assets...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            var copyCount = _selectedItems.Count;
            var sourceName = _levelNameCopyFrom;

            await Task.Run(() =>
            {
                var selected = _selectedItems.Select(y => y.Identifier).ToList();
                Reader.DoCopyAssets(selected);
            });

            Snackbar.Remove(_staticSnackbar);

            // Handle duplicate materials warning
            var duplicateMaterialsPath = Reader.GetDuplicateMaterialsLogFilePath();
            if (!string.IsNullOrEmpty(duplicateMaterialsPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Duplicate Materials found. See logfile {duplicateMaterialsPath}");
            }

            // UPDATE WIZARD STATE
            UpdateWizardStateAfterCopy();

            _copyCompleted = true;
            _copiedAssetsCount = copyCount;
            _totalCopiedAssetsCount += copyCount;
            _lastCopiedSourceName = sourceName;

            StateHasChanged();
        }
    }

    /// <summary>
    ///     Resets source map state in wizard mode to allow selecting another source
    /// </summary>
    private void ResetSourceMapWizardMode()
    {
        // Reset source-related state while keeping target
        _levelNameCopyFrom = null;
        _levelPathCopyFrom = null;
        _vanillaLevelSourceSelected = null;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _copyCompleted = false;
        _searchString = string.Empty;

        // Clear messages but keep target context
        _errors = new List<string>();
        _warnings = new List<string>();

        // Set flag and re-enable selection
        _fileSelectDisabled = false;
        _isLoadingMap = false;

        // Show wizard source selection UI
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
                Reader.ReadAssetsForCopy();
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

        // Clear previous list before filling
        BindingListCopy = new List<GridFileListItem>();
        FillCopyList();

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found {BindingListCopy.Count} assets in {_levelNameCopyFrom}. Select assets to copy.");

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    ///     Finishes the wizard and navigates back to CreateLevel
    /// </summary>
    private void FinishWizard()
    {
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Wizard completed! Copied {_totalCopiedAssetsCount} asset(s) to {WizardState?.LevelName}.");

        // Ensure state is marked complete
        if (WizardState != null)
        {
            WizardState.Step5_AssetsSelected = true;
        }

        Navigation.NavigateTo("/CreateLevel");
    }

    /// <summary>
    ///     Cancels current step and navigates back to forest brushes
    /// </summary>
    private void CancelWizard()
    {
        Navigation.NavigateTo("/CopyForestBrushes?wizardMode=true");
    }

    /// <summary>
    ///     Skips the assets step (marks as complete without copying)
    /// </summary>
    private void SkipStep()
    {
        if (WizardState != null)
        {
            WizardState.Step5_AssetsSelected = true;
            PubSubChannel.SendMessage(PubSubMessageType.Info, "Assets step skipped.");
        }

        Navigation.NavigateTo("/CreateLevel");
    }

    /// <summary>
    ///     Determines if file selection should be disabled
    /// </summary>
    private bool GetFileSelectDisabled()
    {
        // Disable if currently loading a map
        if (_isLoadingMap)
            return true;

        // Disable if both source and target are already selected
        if (!string.IsNullOrEmpty(_levelNameCopyFrom) && !string.IsNullOrEmpty(_levelName))
            return true;

        return _fileSelectDisabled;
    }

    protected void InitializeVariables()
    {
        FileSelect.CollapseAllAsync();
        _labelFileSummaryShrink = string.Empty;
        _fileSelectDisabled = false;
        _isLoadingMap = false;
        _levelName = null;
        _levelNameCopyFrom = null;
        _levelPath = null;
        _levelPathCopyFrom = null;
        _beamLogFilePath = null;
        _errors = new List<string>();
        _warnings = new List<string>();
        _messages = new List<string>();
        _openDrawer = false;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _showDeployButton = false;
        _vanillaLevelSourceSelected = null;
        _vanillaLevelTargetSelected = null;
        // Reset to centralized working directory instead of null
        AppPaths.EnsureWorkingDirectory();
        Reader = null;
        _copyCompleted = false;
        _copiedAssetsCount = 0;
        _lastCopiedSourceName = null;
        _isSelectingAnotherSource = false;

        // Reset wizard tracking (but don't reset WizardState itself)
        _totalCopiedAssetsCount = 0;
    }

    /// <summary>
    ///     Resets the entire page state and allows selecting different maps
    /// </summary>
    protected async Task ResetPage()
    {
        // Call Reader.Reset() if it exists
        if (Reader != null)
        {
            Reader.Reset();
        }

        // Reset all page variables
        InitializeVariables();

        // Collapse all panels first
        await FileSelect.CollapseAllAsync();

        // Wait a moment for the collapse to complete
        await Task.Delay(100);

        // Expand only the source panel (index 1) using ExpandAsync
        if (FileSelect.Panels.Count > 1)
        {
            await FileSelect.Panels[1].ExpandAsync();
        }

        // Force UI refresh
        StateHasChanged();

        // Show success message
        Snackbar.Add("Page reset successfully. You can now select different maps.", Severity.Success);
    }

    /// <summary>
    /// Resets only the source map state, keeping the target map intact for copying from another source
    /// </summary>
    protected async Task ResetSourceMap()
    {
        // Save target map state
        var savedLevelName = _levelName;
        var savedLevelPath = _levelPath;
        var savedVanillaTarget = _vanillaLevelTargetSelected;
        var savedShowDeployButton = _showDeployButton;

        // Reset source-related state
        _levelNameCopyFrom = null;
        _levelPathCopyFrom = null;
        _vanillaLevelSourceSelected = null;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _copyCompleted = false;
        _searchString = string.Empty;

        // Clear messages but keep target context
        _errors = new List<string>();
        _warnings = new List<string>();

        // Restore target map state
        _levelName = savedLevelName;
        _levelPath = savedLevelPath;
        _vanillaLevelTargetSelected = savedVanillaTarget;
        ZipFileHandler.WorkingDirectory = _initialWorkingDirectory;
        _showDeployButton = savedShowDeployButton;

        // Set flag to indicate we're selecting another source
        _isSelectingAnotherSource = true;
        _fileSelectDisabled = false;
        _isLoadingMap = false;

        // Collapse all panels and expand source panel
        await FileSelect.CollapseAllAsync();
        await Task.Delay(100);

        if (FileSelect.Panels.Count > 1)
            await FileSelect.Panels[1].ExpandAsync();

        StateHasChanged();
        Snackbar.Add($"Ready to select another source map. Target: {_levelName}", Severity.Info);
    }

    protected async Task FileSelectedCopyFrom(string file)
    {
        // If selecting another source, use the dedicated method
        if (_isSelectingAnotherSource)
        {
            await LoadNewSourceMapStandaloneMode(file);
            return;
        }

        InitializeVariables();
        _isLoadingMap = true;
        _initialWorkingDirectory = ZipFileHandler.WorkingDirectory;
        
        // Copy file to centralized working directory if not already there
        var targetFile = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file));
        if (!file.Equals(targetFile, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(file, targetFile, true);
        }
        
        await Task.Run(() =>
        {
            try
            {
                _unzipSnackbarCopyFrom = Snackbar.Add("Unzipping source level...", Severity.Normal,
                    config => { config.VisibleStateDuration = int.MaxValue; });
                _levelPathCopyFrom =
                    ZipFileHandler.ExtractToDirectory(targetFile, "_copyFrom", true);
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
            // Clear selection
            _vanillaLevelSourceSelected = null;
            return;
        }

        // If selecting another source, handle it specially
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
        _initialWorkingDirectory = ZipFileHandler.WorkingDirectory;
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
            // Clear selection
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

        if (isFolder)
        {
            // Direct folder mode - work in place, set working directory to the folder
            ZipFileHandler.WorkingDirectory = file;
        }
        else
        {
            // ZIP mode - copy to centralized temp folder if needed
            var targetFile = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file));
            if (!file.Equals(targetFile, StringComparison.OrdinalIgnoreCase))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copy target level to {ZipFileHandler.WorkingDirectory} ...");
                try
                {
                    File.Copy(file, targetFile, true);
                    PubSubChannel.SendMessage(PubSubMessageType.Info, "Copy target level finished");
                }
                catch (Exception ex)
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Error,
                        $"Error copy target level to working directory: {ex.Message}");
                }
            }
        }

        await Task.Run(() =>
        {
            try
            {
                _staticSnackbar = Snackbar.Add("Unzipping target level...", Severity.Normal,
                    config => { config.VisibleStateDuration = int.MaxValue; });
                if (!isFolder)
                {
                    _levelPath =
                        ZipFileHandler.ExtractToDirectory(
                            Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file)), "_unpacked");
                }
                else
                {
                    _levelPath = ZipFileHandler.GetNamePath(file);
                }

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

        // Save initial working directory for later source resets
        if (string.IsNullOrEmpty(_initialWorkingDirectory))
        {
            _initialWorkingDirectory = ZipFileHandler.WorkingDirectory;
        }

        await ScanAssets();

        // Collapse the target panel (index 2) after successful selection
        if (FileSelect.Panels.Count > 2)
        {
            await FileSelect.Panels[2].CollapseAsync();
        }
    }

    protected void SetBeamInstallDir(string file)
    {
        if (file != GameDirectoryService.GetInstallDirectory())
        {
            GameDirectoryService.SetInstallDirectory(file);
            GetVanillaLevels();
        }
    }

    protected string GetBeamInstallDir()
    {
        var currentDir = GameDirectoryService.GetInstallDirectory();
        if (currentDir != _beamInstallDir)
        {
            _beamInstallDir = currentDir;
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
                Reader.ReadAssetsForCopy();
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
        
        // Clear previous list before filling
        BindingListCopy = new List<GridFileListItem>();
        FillCopyList();
        PubSubChannel.SendMessage(PubSubMessageType.Info,
            "Done! Scanning Assets finished. Please select assets to copy.");
        
        await InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        // IMPORTANT: Always reset to default working directory on page init
        AppPaths.EnsureWorkingDirectory();
        
        _fileSelectExpanded = true;
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
                }
            }
        });
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
        foreach (var asset in Reader.GetCopyList())
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
        {
            return $"{_fileSelectTitle} > {_levelName}";
        }

        return $"{_fileSelectTitle}";
    }

    private string GetFileSelectTitleCopyFrom()
    {
        if (!string.IsNullOrEmpty(_levelNameCopyFrom))
        {
            return $"{_fileSelectTitleCopyFrom} > {_levelNameCopyFrom}";
        }

        return $"{_fileSelectTitleCopyFrom}";
    }

    private bool FilterFunc1(GridFileListItem element)
    {
        return FilterFunc(element, _searchString);
    }

    private bool FilterFunc(GridFileListItem element, string searchString)
    {
        if (string.IsNullOrWhiteSpace(searchString))
            return true;
        if (element.FullName.Contains(searchString, StringComparison.OrdinalIgnoreCase))
            return true;
        if (element.AssetType.Contains(searchString, StringComparison.OrdinalIgnoreCase))
            return true;
        if (element.SizeMb.ToString().Contains(searchString, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
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
        _searchString = text ?? string.Empty;
        table.ReloadServerData();
    }

    private async Task CopyDialog()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var parameters = new DialogParameters();
        parameters.Add("ContentText",
            "Do you really want to copy these assets? This process cannot be undone. Please always use a copy of your project with this tool!");
        parameters.Add("ButtonText", "Copy");
        parameters.Add("Color", Color.Error);
        var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Assets", parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {
            _staticSnackbar = Snackbar.Add("Assets copy in process. Please be patient.", Severity.Normal,
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

            var duplicateMaterialsPath = Reader.GetDuplicateMaterialsLogFilePath();
            if (!string.IsNullOrEmpty(duplicateMaterialsPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                    $"Duplicate Materials found. You should resolve this if there are broken textures after shrinking. See logfile {duplicateMaterialsPath}");
            }

            Reader.WriteLogFile(_warnings, "Log_AssetCopy_Warnings");
            Reader.WriteLogFile(_errors, "Log_AssetCopy_Errors");

            // Set copy completed state
            _copyCompleted = true;
            _copiedAssetsCount = copyCount;
            _lastCopiedSourceName = sourceName;

            StateHasChanged();
        }
    }

    private async Task ZipAndDeploy()
    {
        var path = string.Empty;
        if (!string.IsNullOrEmpty(_levelPath))
        {
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
    }

    private void OpenAssetViewer(GridFileListItem context)
    {
        var dialogOptions = new DialogOptions { FullScreen = true, CloseButton = true };
        var parameters = new DialogParameters();
        parameters.Add("CopyAsset", context.CopyAsset);
        var dialog = DialogService.Show<AssetViewer>($"Asset Preview: {context.FullName}", parameters, dialogOptions);
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

    private readonly Func<FileInfo, string> converter = p => p?.Name;

    public static DirectoryInfo GetExecutingDirectory()
    {
        var location = new Uri(Assembly.GetEntryAssembly().GetName().CodeBase);
        return new FileInfo(location.AbsolutePath).Directory;
    }

    public void SetDefaultWorkingDirectory()
    {
        AppPaths.EnsureWorkingDirectory();
    }
}