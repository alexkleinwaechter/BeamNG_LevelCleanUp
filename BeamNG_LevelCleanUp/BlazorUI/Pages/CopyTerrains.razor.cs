using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Color = MudBlazor.Color;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class CopyTerrains
{
    private readonly string _fileSelectTitle = "Select the target level to copy terrain materials to";
    private readonly string _fileSelectTitleCopyFrom = "Select the zipped source level with terrain materials";

    private readonly Func<FileInfo, string> converter = p => p?.Name;
    private Anchor _anchor;
    private bool _fixed_Header = true;
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
    private string _beamLogFilePath { get; set; } = string.Empty;
    private List<string> _missingFiles { get; set; } = new();
    private List<string> _errors { get; set; } = new();
    private List<string> _messages { get; set; } = new();
    private List<string> _warnings { get; set; } = new();
    private BeamFileReader Reader { get; set; }
    private bool _fileSelectDisabled { get; set; }
    private bool _isLoadingMap { get; set; }
    private bool _fileSelectExpanded { get; set; }
    private List<GridFileListItem> BindingListCopy { get; set; } = new();
    [AllowNull] private MudExpansionPanels FileSelect { get; set; }
    private string _labelFileSummaryShrink { get; set; } = string.Empty;
    private bool _showDeployButton { get; set; }
    private CompressionLevel _compressionLevel { get; set; } = CompressionLevel.Optimal;
    private bool _showErrorLog { get; set; }
    private bool _showWarningLog { get; set; }
    private List<FileInfo> _vanillaLevels { get; set; } = new();
    private FileInfo _vanillaLevelSourceSelected { get; set; }
    private FileInfo _vanillaLevelTargetSelected { get; set; }
    private string _beamInstallDir { get; set; }
    private List<string> _targetTerrainMaterials { get; set; } = new();
    private bool _copyCompleted { get; set; }
    private int _copiedMaterialsCount { get; set; }
    private int _totalCopiedMaterialsCount { get; set; }
    private string _lastCopiedSourceName { get; set; }
    private bool _showWizardSourceSelection { get; set; }
    private bool _isSelectingAnotherSource { get; set; }
    private string _initialWorkingDirectory { get; set; }

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
        _showDeployButton = false;
        _vanillaLevelSourceSelected = null;
        _vanillaLevelTargetSelected = null;
        ZipFileHandler.WorkingDirectory = null;
        _targetTerrainMaterials = new List<string>();
        _selectedItems = new HashSet<GridFileListItem>();
        Reader = null;
        _copyCompleted = false;
        _copiedMaterialsCount = 0;
        _totalCopiedMaterialsCount = 0;
        _lastCopiedSourceName = null;
    }

    /// <summary>
    ///     Resets the entire page state and allows selecting different maps
    /// </summary>
    protected async Task ResetPage()
    {
        // Call Reader.Reset() if it exists
        if (Reader != null) Reader.Reset();

        // Reset all page variables
        InitializeVariables();

        // Collapse all panels first
        await FileSelect.CollapseAllAsync();

        // Wait a moment for the collapse to complete
        await Task.Delay(100);

        // Expand only the source panel (index 1) using ExpandAsync
        if (FileSelect.Panels.Count > 1) await FileSelect.Panels[1].ExpandAsync();

        // Force UI refresh
        StateHasChanged();

        // Show success message
        Snackbar.Add("Page reset successfully. You can now select different maps.", Severity.Success);
    }

    /// <summary>
    ///     Resets only the source map state, keeping the target map intact for copying from another source
    /// </summary>
    protected async Task ResetSourceMap()
    {
        var savedLevelName = _levelName;
        var savedLevelPath = _levelPath;
        var savedTargetMaterials = _targetTerrainMaterials;
        var savedVanillaTarget = _vanillaLevelTargetSelected;
        var savedShowDeployButton = _showDeployButton;

        // Reset source-related state only
        _levelNameCopyFrom = null;
        _levelPathCopyFrom = null;
        _vanillaLevelSourceSelected = null;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _copyCompleted = false;
        _searchString = string.Empty;

        // Clear only source-related errors/warnings, keep general messages
        _errors = new List<string>();
        _warnings = new List<string>();

        _levelName = savedLevelName;
        _levelPath = savedLevelPath;
        _targetTerrainMaterials = savedTargetMaterials;
        _vanillaLevelTargetSelected = savedVanillaTarget;
        ZipFileHandler.WorkingDirectory = _initialWorkingDirectory;
        _showDeployButton = savedShowDeployButton;

        // Set flag to indicate we're selecting another source (preserve target on next file selection)
        _isSelectingAnotherSource = true;

        // Reset file select disabled state
        _fileSelectDisabled = false;
        _isLoadingMap = false;

        // Collapse all panels first
        await FileSelect.CollapseAllAsync();
        await Task.Delay(100);

        // Expand the source panel (index 1)
        if (FileSelect.Panels.Count > 1) await FileSelect.Panels[1].ExpandAsync();

        // Force UI refresh
        StateHasChanged();

        // Show info message
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
    ///     Loads a new source map in standalone mode while preserving target state
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
            if (Directory.Exists(copyFromPath)) Directory.Delete(copyFromPath, true);

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

            if (FileSelect?.Panels?.Count > 1) await FileSelect.Panels[1].CollapseAsync();
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

        // Check if we're selecting another source while preserving target
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

        // Check for PBR terrain materials upgrade
        await CheckPbrUpgrade();

        await ScanAssets();

        // Collapse the target panel (index 2) after successful selection
        if (FileSelect.Panels.Count > 2) await FileSelect.Panels[2].CollapseAsync();
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
                Reader.ReadAllForCopy();

                // Load target terrain materials for the dropdown
                var namePath = ZipFileHandler.GetNamePath(_levelPath);
                _targetTerrainMaterials = TerrainCopyScanner.GetTargetTerrainMaterials(namePath);
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
            "Done! Scanning Terrain Materials finished. Please select materials to copy.");

        // Force UI update after scanning completes
        await InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        _fileSelectExpanded = true;
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
        // In wizard mode, get the wizard state from CreateLevel's static field
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

            // Initialize vanilla levels list for the dropdown
            _beamInstallDir = Steam.GetBeamInstallDir();
            GetVanillaLevels();

            // 1. Set source level (from wizard state) - already extracted in _copyFrom
            // WizardState.SourceLevelPath should be the _levelPath from CreateLevel (pointing to the _copyFrom directory)
            // We need to find the actual level name path within it
            var sourceLevelPath = WizardState.SourceLevelPath;
            _levelNameCopyFrom = WizardState.SourceLevelName;

            // Find the actual level directory with info.json (e.g., WorkingDirectory/_copyFrom/levels/driver_training)
            _levelPathCopyFrom = ZipFileHandler.GetLevelPath(sourceLevelPath);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Source level path: {_levelPathCopyFrom}");

            // 2. Set target level (newly created level in _unpacked)
            // WizardState.TargetLevelRootPath is: WorkingDirectory/_unpacked/levels/targetLevelPath
            // This is already the full path to the level directory, we just need to get the parent (levels folder)
            var targetLevelRootPath = WizardState.TargetLevelRootPath;
            _levelName = WizardState.LevelName; // This is the display name

            // The target path should be the parent directory (the "levels" folder)
            var targetLevelNamePath = targetLevelRootPath;
            _levelPath = Directory.GetParent(targetLevelRootPath)?.FullName;

            if (_levelPath == null || !Directory.Exists(_levelPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Target level path not found: {_levelPath}");
                return;
            }

            // Set working directory from the wizard state path
            // Extract the working directory from the target path (remove /_unpacked/levels/targetLevelPath)
            if (string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory))
            {
                var unpackedIndex = _levelPath.IndexOf("_unpacked", StringComparison.OrdinalIgnoreCase);
                if (unpackedIndex > 0)
                {
                    ZipFileHandler.WorkingDirectory = _levelPath.Substring(0, unpackedIndex - 1);
                }
                else
                {
                    // Fallback: go up from levels folder
                    var levelsParent = Directory.GetParent(_levelPath);
                    if (levelsParent != null)
                        ZipFileHandler.WorkingDirectory = Directory.GetParent(levelsParent.FullName)?.FullName;
                }
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Target level path: {_levelPath}");
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Working directory: {ZipFileHandler.WorkingDirectory}");

            // 3. Initialize BeamFileReader with both paths
            // Both paths should point to the "levels" directories
            Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);

            // 4. Scan assets (call existing method)
            await ScanAssets();

            // 5. Force UI update after loading completes
            await InvokeAsync(StateHasChanged);

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                "Wizard mode: Levels loaded successfully");
        }
        catch (Exception ex)
        {
            ShowException(ex);
            PubSubChannel.SendMessage(PubSubMessageType.Error,
                $"Failed to load levels in wizard mode: {ex.Message}");

            // Force UI update even on error
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CopyDialogWizardMode()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var parameters = new DialogParameters();

        var replaceCount = _selectedItems.Count(x => x.CopyAsset.IsReplaceMode);
        var addCount = _selectedItems.Count - replaceCount;

        var messageText = $"Copy {addCount} new material(s) and replace {replaceCount} existing material(s)?";

        parameters.Add("ContentText", messageText);
        parameters.Add("ButtonText", "Copy Materials");
        parameters.Add("Color", Color.Primary);

        var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Terrain Materials", parameters, options);
        var result = await dialog.Result;

        if (!result.Canceled)
        {
            _staticSnackbar = Snackbar.Add("Copying terrain materials...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            // Store copy info before executing
            var copyCount = _selectedItems.Count;
            var sourceName = _levelNameCopyFrom;

            // Set wizard terrain size in PathResolver before copying
            if (WizardState != null) PathResolver.WizardTerrainSize = WizardState.TerrainSize;

            await Task.Run(() =>
            {
                var selected = _selectedItems.Select(y => y.Identifier).ToList();
                Reader.DoCopyAssets(selected);
            });

            // Clear wizard terrain size after copying
            PathResolver.WizardTerrainSize = null;

            Snackbar.Remove(_staticSnackbar);

            // Update wizard state (accumulate materials)
            UpdateWizardStateAfterCopy();

            // Set copy completed state (don't navigate away)
            _copyCompleted = true;
            _copiedMaterialsCount = copyCount;
            _totalCopiedMaterialsCount += copyCount;
            _lastCopiedSourceName = sourceName;

            // Refresh target terrain materials list (they may have changed)
            var namePath = ZipFileHandler.GetNamePath(_levelPath);
            _targetTerrainMaterials = TerrainCopyScanner.GetTargetTerrainMaterials(namePath);

            StateHasChanged();
        }
    }

    private void UpdateWizardStateAfterCopy()
    {
        if (WizardState == null) return;

        // Collect copied terrain materials
        var copiedMaterials = _selectedItems
            .Where(x => x.CopyAsset.CopyAssetType == CopyAssetType.Terrain)
            .SelectMany(x => x.CopyAsset.Materials)
            .ToList();

        WizardState.CopiedTerrainMaterials = copiedMaterials;
        WizardState.Step3_TerrainMaterialsSelected = true;
        WizardState.CurrentStep = 2; // Keep on step 2 to show completion

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Copied {copiedMaterials.Count} terrain material(s) to {WizardState.LevelName}");
    }

    private void CancelWizard()
    {
        // Clear wizard terrain size when canceling
        PathResolver.WizardTerrainSize = null;

        // Navigate back to CreateLevel without making changes
        Navigation.NavigateTo("/CreateLevel");
    }

    /// <summary>
    ///     Resets source map state in wizard mode to allow selecting another source
    /// </summary>
    private void ResetSourceMapWizardMode()
    {
        // Reset source-related state only
        _levelNameCopyFrom = null;
        _levelPathCopyFrom = null;
        _vanillaLevelSourceSelected = null;
        BindingListCopy = new List<GridFileListItem>();
        _selectedItems = new HashSet<GridFileListItem>();
        _copyCompleted = false;
        _searchString = string.Empty;

        // Clear errors/warnings from previous copy
        _errors = new List<string>();
        _warnings = new List<string>();

        // Reset file select state
        _fileSelectDisabled = false;
        _isLoadingMap = false;

        // Show the source selection UI
        _showWizardSourceSelection = true;

        // Force UI refresh
        StateHasChanged();

        Snackbar.Add($"Select a new source map. Target: {_levelName}", Severity.Info);
    }

    /// <summary>
    ///     Handles file selection in wizard mode source selection
    /// </summary>
    private async Task OnWizardSourceFileSelected(string filePath)
    {
        await LoadNewSourceMapWizardMode(filePath);
    }

    /// <summary>
    ///     Handles vanilla level selection in wizard mode source selection
    /// </summary>
    private async Task OnWizardVanillaSourceSelected(FileInfo file)
    {
        if (file == null)
        {
            _vanillaLevelSourceSelected = null;
            return;
        }

        _vanillaLevelSourceSelected = file;

        // Copy vanilla level to working directory and load it
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
    ///     Loads a new source map in wizard mode
    /// </summary>
    private async Task LoadNewSourceMapWizardMode(string filePath)
    {
        _isLoadingMap = true;
        StateHasChanged();

        try
        {
            _staticSnackbar = Snackbar.Add("Extracting new source level...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            // IMPORTANT: ZipFileHandler.ExtractToDirectory uses the file's directory as the extraction base
            // So we MUST copy the file to the working directory first if it's not already there
            var fileInWorkingDir = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(filePath));

            // Always ensure we have the file in the working directory
            // Use Path.GetFullPath for proper comparison (handles trailing slashes, casing, etc.)
            var sourceDir = Path.GetFullPath(Path.GetDirectoryName(filePath) ?? "");
            var workingDir = Path.GetFullPath(ZipFileHandler.WorkingDirectory ?? "");

            if (!sourceDir.Equals(workingDir, StringComparison.OrdinalIgnoreCase))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info,
                    $"Copying source level to working directory: {ZipFileHandler.WorkingDirectory}");
                File.Copy(filePath, fileInWorkingDir, true);
            }

            // Clean up existing _copyFrom folder before extracting new source
            var copyFromPath = Path.Join(ZipFileHandler.WorkingDirectory, "_copyFrom");
            if (Directory.Exists(copyFromPath))
            {
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Cleaning up previous source level...");
                Directory.Delete(copyFromPath, true);
            }

            await Task.Run(() =>
            {
                // Extract to _copyFrom directory - MUST use the file path in working directory
                _levelPathCopyFrom = ZipFileHandler.ExtractToDirectory(
                    fileInWorkingDir,
                    "_copyFrom",
                    true);

                // Find actual level path
                _levelPathCopyFrom = ZipFileHandler.GetLevelPath(_levelPathCopyFrom);

                // Get level name
                var tempReader = new BeamFileReader(_levelPathCopyFrom, null);
                _levelNameCopyFrom = tempReader.GetLevelName();
            });

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add($"Source level loaded: {_levelNameCopyFrom}", Severity.Success);

            // Hide the source selection UI
            _showWizardSourceSelection = false;

            // Scan assets from new source
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
    ///     Scans assets in wizard mode (source changed, target stays same)
    /// </summary>
    private async Task ScanAssetsWizardMode()
    {
        _fileSelectDisabled = true;
        await Task.Run(() =>
        {
            try
            {
                Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);
                Reader.ReadAllForCopy();
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

        // Clear and refill the copy list
        BindingListCopy = new List<GridFileListItem>();
        FillCopyList();

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Found {BindingListCopy.Count} terrain materials in {_levelNameCopyFrom}. Select materials to copy.");

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    ///     Finishes the wizard and navigates back to CreateLevel
    /// </summary>
    private void FinishWizard()
    {
        // Clear wizard terrain size
        PathResolver.WizardTerrainSize = null;

        PubSubChannel.SendMessage(PubSubMessageType.Info,
            $"Wizard completed! Copied {_totalCopiedMaterialsCount} terrain material(s) to {WizardState?.LevelName}.");

        // Navigate back to CreateLevel
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
        foreach (var asset in Reader.GetCopyList().Where(x => x.CopyAssetType == CopyAssetType.Terrain))
        {
            // Auto-detect roughness preset based on material name
            asset.RoughnessPreset = CopyAsset.DetectRoughnessPresetFromName(asset.Name);

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
        if (!string.IsNullOrEmpty(_levelName)) return $"{_fileSelectTitle} > {_levelName}";

        return $"{_fileSelectTitle}";
    }

    private string GetFileSelectTitleCopyFrom()
    {
        if (!string.IsNullOrEmpty(_levelNameCopyFrom)) return $"{_fileSelectTitleCopyFrom} > {_levelNameCopyFrom}";

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
        _searchString = text;
        table.ReloadServerData();
    }

    private async Task CopyDialog()
    {
        var options = new DialogOptions { CloseOnEscapeKey = true };
        var parameters = new DialogParameters();

        // Count how many materials are in replace mode
        var replaceCount = _selectedItems.Count(x => x.CopyAsset.IsReplaceMode);
        var addCount = _selectedItems.Count - replaceCount;
        var totalReplacements = _selectedItems
            .Where(x => x.CopyAsset.IsReplaceMode)
            .Sum(x => x.CopyAsset.ReplaceTargetMaterialNames.Count);

        var messageText = "Do you really want to copy these terrain materials? This process cannot be undone. ";

        if (addCount > 0) messageText += $"{addCount} material(s) will be added with the source level name suffix. ";

        if (replaceCount > 0)
            messageText +=
                $"{replaceCount} material(s) will replace {totalReplacements} target material(s) while preserving their names and IDs. ";

        messageText += "Please always use a copy of your project with this tool!";

        parameters.Add("ContentText", messageText);
        parameters.Add("ButtonText", "Copy");
        parameters.Add("Color", Color.Error);
        var dialog = await DialogService.ShowAsync<SimpleDialog>("Copy Terrain Materials", parameters, options);
        var result = await dialog.Result;
        if (!result.Canceled)
        {
            _staticSnackbar = Snackbar.Add("Terrain materials copy in process. Please be patient.", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            // Store copy info before executing
            var copyCount = _selectedItems.Count;
            var sourceName = _levelNameCopyFrom;

            await Task.Run(() =>
            {
                var selected = _selectedItems.Select(y => y.Identifier).ToList();
                Reader.DoCopyAssets(selected);
                _showDeployButton = true;
            });
            Snackbar.Remove(_staticSnackbar);

            Reader.WriteLogFile(_warnings, "Log_TerrainCopy_Warnings");
            Reader.WriteLogFile(_errors, "Log_TerrainCopy_Errors");

            // Set copy completed state
            _copyCompleted = true;
            _copiedMaterialsCount = copyCount;
            _lastCopiedSourceName = sourceName;

            // Refresh target terrain materials list (they may have changed)
            var namePath = ZipFileHandler.GetNamePath(_levelPath);
            _targetTerrainMaterials = TerrainCopyScanner.GetTargetTerrainMaterials(namePath);

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

    public static DirectoryInfo GetExecutingDirectory()
    {
        var location = new Uri(Assembly.GetEntryAssembly().GetName().CodeBase);
        return new FileInfo(location.AbsolutePath).Directory;
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

    /// <summary>
    ///     Handles replace target selection changes with mutual exclusivity logic
    /// </summary>
    private void OnReplaceTargetChanged(CopyAsset asset, IEnumerable<string> selectedValues)
    {
        var valuesList = selectedValues?.ToList() ?? new List<string>();

        // Check if "Add New Material" (null) was selected
        var hasAddNew = valuesList.Contains(null);
        var hasReplacements = valuesList.Any(v => v != null);

        if (hasAddNew && hasReplacements)
        {
            // If both "Add New" and replacements are selected, determine which was added last
            // by comparing with the current state
            var currentHasAddNew =
                asset.ReplaceTargetMaterialNames?.Count == 0 || asset.ReplaceTargetMaterialNames == null;

            if (currentHasAddNew)
                // A replacement was just selected -> clear "Add New"
                asset.ReplaceTargetMaterialNames = valuesList.Where(v => v != null).ToList();
            else
                // "Add New" was just selected -> clear all replacements
                asset.ReplaceTargetMaterialNames = new List<string>();
        }
        else if (hasAddNew)
        {
            // Only "Add New" selected -> clear list (Add mode)
            asset.ReplaceTargetMaterialNames = new List<string>();
        }
        else
        {
            // Only replacements selected -> use them
            asset.ReplaceTargetMaterialNames = valuesList.Where(v => v != null).ToList();
        }

        // Force UI refresh
        StateHasChanged();
    }

    /// <summary>
    ///     Checks if target level has PBR terrain materials, and prompts user for upgrade if not
    /// </summary>
    private async Task CheckPbrUpgrade()
    {
        if (string.IsNullOrEmpty(_levelPath))
            return;

        await Task.Run(async () =>
        {
            try
            {
                // Get the namePath from _levelPath
                var namePath = ZipFileHandler.GetNamePath(_levelPath);

                // Check if base material size can be determined
                var baseMaterialSize = TerrainTextureHelper.GetBaseMaterialSize(namePath);

                if (baseMaterialSize == null)
                {
                    // Prompt user about PBR upgrade on the UI thread
                    await InvokeAsync(async () =>
                    {
                        var options = new DialogOptions { CloseOnEscapeKey = true };
                        var parameters = new DialogParameters();
                        parameters.Add("ContentText",
                            "The target level does not appear to have PBR (Physically Based Rendering) terrain materials configured. " +
                            "Would you like to upgrade the terrain materials to PBR format? " +
                            "If yes please use the replace function in target material dropdown to change the old materials.");
                        parameters.Add("ButtonText", "Yes, Upgrade to PBR");
                        parameters.Add("Color", Color.Info);

                        var dialog = await DialogService.ShowAsync<SimpleDialog>("Upgrade Terrain Materials to PBR?",
                            parameters, options);
                        var result = await dialog.Result;

                        BeamFileReader.UpgradeTerrainMaterialsToPbr = !result.Canceled;

                        if (!result.Canceled)
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                "Terrain materials will be upgraded to PBR format during copy.");
                        else
                            PubSubChannel.SendMessage(PubSubMessageType.Info,
                                "Terrain materials will be copied in legacy format.");
                    });
                }
                else
                {
                    // Target already has PBR materials
                    BeamFileReader.UpgradeTerrainMaterialsToPbr = false;
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Target level already has PBR terrain materials (base size: {baseMaterialSize}x{baseMaterialSize}).");
                }
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Warning,
                        $"Could not determine PBR status: {ex.Message}")
                    ;
                BeamFileReader.UpgradeTerrainMaterialsToPbr = false;
            }
        });
    }
}