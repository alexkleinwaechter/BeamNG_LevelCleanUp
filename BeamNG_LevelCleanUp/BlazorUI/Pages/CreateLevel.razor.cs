using System.IO.Compression;
using BeamNG_LevelCleanUp.BlazorUI.Components;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.LogicCopyAssets;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using MudBlazor;
using Color = MudBlazor.Color;

namespace BeamNG_LevelCleanUp.BlazorUI.Pages;

public partial class CreateLevel
{
    private static CreateLevelWizardState _wizardState = new();

    /// <summary>
    ///     Exposes the wizard state for other pages (e.g., CopyTerrains in wizard mode)
    /// </summary>
    public static CreateLevelWizardState GetWizardState() => _wizardState;

    private string _sourceLevelName;
    private string _sourceLevelPath;
    private string _targetLevelPath;
    private string _targetLevelName;
    private string _levelDescription;
    private string _levelCountry;
    private string _levelRegion;
    private string _levelBiome;
    private string _levelRoads;
    private string _levelSuitableFor;
    private string _levelFeatures;
    private string _levelAuthors;
    private BeamFileReader _reader;
    private List<string> _errors = new();
    private List<string> _messages = new();
    private List<string> _warnings = new();
    private Snackbar _staticSnackbar;
    private bool _openDrawer;
    private Anchor _anchor;
    private string width;
    private string height;
    private bool _showErrorLog;
    private bool _showWarningLog;
    private bool _isInitializing;
    private List<FileInfo> _vanillaLevels = new();
    private FileInfo _vanillaLevelSourceSelected;
    private string _beamInstallDir;
    private MudExpansionPanels FileSelect;
    private CompressionLevel _compressionLevel = CompressionLevel.Optimal;

    protected override void OnInitialized()
    {
        // IMPORTANT: Always reset to default working directory on page init
        AppPaths.EnsureWorkingDirectory();
        
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

    protected async Task OnSourceMapSelected(string file)
    {
        SetDefaultWorkingDirectory();

        // CRITICAL: Clean up any existing _unpacked and _copyFrom folders BEFORE extracting
        // This ensures we start completely fresh and don't have stale data from previous runs
        CleanupWorkingDirectories();

        // If file is in a different folder than working directory, copy it first
        if (ZipFileHandler.WorkingDirectory != Path.GetDirectoryName(file))
        {
            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Copy source level to {ZipFileHandler.WorkingDirectory} ...");
            try
            {
                var target = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file));
                File.Copy(file, target, true);
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Copy source level finished");
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
                    $"Error copying source level to working directory: {ex.Message}");
                ShowException(ex);
                return;
            }
        }

        await Task.Run(() =>
        {
            try
            {
                _staticSnackbar = Snackbar.Add("Unzipping source level...", Severity.Normal,
                    config => { config.VisibleStateDuration = int.MaxValue; });

                _sourceLevelPath = ZipFileHandler.ExtractToDirectory(
                    Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file)),
                    "_copyFrom",
                    true);

                _reader = new BeamFileReader(_sourceLevelPath, null);
                _sourceLevelName = _reader.GetLevelName();

                _wizardState.SourceLevelPath = _sourceLevelPath;
                _wizardState.SourceLevelName = _sourceLevelName;

                Snackbar.Add("Source level loaded successfully", Severity.Success);
                Snackbar.Remove(_staticSnackbar);
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        });

        StateHasChanged();
    }

    protected async Task OnVanillaSourceSelected(FileInfo file)
    {
        if (file == null)
        {
            _vanillaLevelSourceSelected = null;
            return;
        }

        SetDefaultWorkingDirectory();

        // CRITICAL: Clean up any existing _unpacked and _copyFrom folders BEFORE extracting
        // This ensures we start completely fresh and don't have stale data from previous runs
        CleanupWorkingDirectories();

        _vanillaLevelSourceSelected = file;
        var target = Path.Join(ZipFileHandler.WorkingDirectory, _vanillaLevelSourceSelected.Name);

        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Copy {_vanillaLevelSourceSelected.Name} to {target}");
        File.Copy(_vanillaLevelSourceSelected.FullName, target, true);

        await OnSourceMapSelected(target);
    }

    protected async Task InitializeNewLevel()
    {
        if (!CanInitialize() || _isInitializing)
            return;

        _isInitializing = true;
        StateHasChanged();

        try
        {
            _staticSnackbar = Snackbar.Add("Initializing new level...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            await Task.Run(() =>
            {
                // 1. Create target directory structure
                // targetRoot will be: WorkingDirectory/_unpacked/levels/targetLevelPath
                _targetLevelPath = StringUtils.SanitizeFileName(_targetLevelPath);
                var targetRoot = Path.Join(
                    ZipFileHandler.WorkingDirectory,
                    "_unpacked",
                    "levels",
                    _targetLevelPath);

                _wizardState.TargetLevelRootPath = targetRoot;
                // targetLevelNamePath is the same as targetRoot since we're already in levels/levelname
                var targetLevelNamePath = targetRoot;
                _wizardState.TargetLevelPath = _targetLevelPath;
                _wizardState.LevelName = _targetLevelName;

                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(Path.Join(targetLevelNamePath, "art", "terrains"));
                Directory.CreateDirectory(Path.Join(targetLevelNamePath, "art", "shapes", "groundcover"));
                Directory.CreateDirectory(Path.Join(targetLevelNamePath, "main", "MissionGroup"));

                PubSubChannel.SendMessage(PubSubMessageType.Info, "Created directory structure");

                // 2. Create empty terrain material files
                File.WriteAllText(
                    Path.Join(targetLevelNamePath, "art", "terrains", "main.materials.json"),
                    "{}");
                File.WriteAllText(
                    Path.Join(targetLevelNamePath, "art", "shapes", "groundcover", "main.materials.json"),
                    "{}");

                PubSubChannel.SendMessage(PubSubMessageType.Info, "Created empty material files");

                // 3. Create info.json and mainLevel.lua
                InfoJsonGenerator.CreateInfoJson(
                    targetLevelNamePath,
                    _targetLevelName,
                    _levelDescription,
                    _levelCountry,
                    _levelRegion,
                    _levelBiome,
                    _levelRoads,
                    _levelSuitableFor,
                    _levelFeatures,
                    _levelAuthors);
                InfoJsonGenerator.CreateMainLevelLua(targetLevelNamePath);

                // 4. Generate preview image
                LevelPreviewGenerator.GeneratePreviewImage(targetLevelNamePath, _targetLevelPath, _targetLevelName);

                // 5. Copy MissionGroup data
                _reader.ReadMissionGroupsForCreateLevel();

                // Also read materials from source level for copying referenced materials
                var sourceReader = new BeamFileReader(_sourceLevelPath, null);
                sourceReader.ReadMaterialsJson();

                var sourceLevelNamePath = ZipFileHandler.GetNamePath(_sourceLevelPath);

                var missionGroupCopier = new MissionGroupCopier(
                    BeamFileReader.Assets,
                    _sourceLevelPath,
                    sourceLevelNamePath,
                    targetRoot,
                    targetLevelNamePath,
                    _targetLevelPath,
                    BeamFileReader.MaterialsJson); // Pass source materials

                missionGroupCopier.CopyMissionGroupData();

                // 6. Update wizard state
                _wizardState.CopiedMissionGroupAssets = new List<Asset>(BeamFileReader.Assets);
                _wizardState.Step1_SetupComplete = true;
                _wizardState.Step2_MissionGroupsCopied = true;
                _wizardState.IsActive = true;
                _wizardState.CurrentStep = 1;
            });

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add("Level initialization complete!", Severity.Success);

            // Collapse source selection panel
            if (FileSelect?.Panels.Count > 1)
            {
                await FileSelect.Panels[1].CollapseAsync();
            }
        }
        catch (Exception ex)
        {
            if (_staticSnackbar != null)
                Snackbar.Remove(_staticSnackbar);
            ShowException(ex);
        }
        finally
        {
            _isInitializing = false;
            StateHasChanged();
        }
    }

    private async Task ZipAndDeploy()
    {
        try
        {
            var path = Path.Join(ZipFileHandler.WorkingDirectory, "_unpacked");
            _staticSnackbar = Snackbar.Add("Building deployment file...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            await Task.Run(() =>
            {
                ZipFileHandler.BuildDeploymentFile(path, _wizardState.TargetLevelPath, _compressionLevel);
            });

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add($"Deployment file created for {_wizardState.LevelName}", Severity.Success);

            // Optionally reset wizard after successful deployment
            // (User can click "Create Another Level" button instead)
        }
        catch (Exception ex)
        {
            if (_staticSnackbar != null)
                Snackbar.Remove(_staticSnackbar);
            ShowException(ex);
        }
    }

    private async Task CopyToLevelsFolder()
    {
        try
        {
            var path = Path.Join(ZipFileHandler.WorkingDirectory, "_unpacked", "levels");
            var customChangesChecker = new CustomChangesChecker(_wizardState.TargetLevelPath, path);

            // Check if target directory exists and ask for confirmation
            if (customChangesChecker.TargetDirectoryExists())
            {
                var options = new DialogOptions { CloseOnEscapeKey = true };
                var parameters = new DialogParameters();
                parameters.Add("ContentText",
                    $"The level '{_wizardState.TargetLevelPath}' already exists in your BeamNG levels folder. Do you want to overwrite it?");
                parameters.Add("ButtonText", "Yes, Overwrite");
                parameters.Add("Color", Color.Warning);

                var dialog = await DialogService.ShowAsync<SimpleDialog>("Level Already Exists", parameters, options);
                var result = await dialog.Result;

                if (result.Canceled)
                {
                    return; // User canceled, don't proceed with copy
                }

                // Delete the existing directory before copying
                customChangesChecker.DeleteTargetDirectory();
            }

            _staticSnackbar = Snackbar.Add("Copying level to BeamNG levels folder...", Severity.Normal,
                config => { config.VisibleStateDuration = int.MaxValue; });

            await Task.Run(() =>
            {
                ZipFileHandler.RemoveModInfo(path);
                customChangesChecker.CopyUnpackedToUserFolder();
            });

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add($"Level '{_wizardState.LevelName}' successfully copied to BeamNG levels folder.",
                Severity.Success);
        }
        catch (Exception ex)
        {
            if (_staticSnackbar != null)
                Snackbar.Remove(_staticSnackbar);
            ShowException(ex);
        }
    }

    /// <summary>
    ///     Cleanup method to clear all wizard-related static state
    /// </summary>
    private void CleanupWizardState()
    {
        PathResolver.WizardTerrainSize = null;
        // Add other PathResolver cleanup if needed in the future
    }

    private void ResetWizard()
    {
        // Clear wizard state
        _wizardState.Reset();

        // Clear all wizard-related static state
        CleanupWizardState();

        // Clear local page variables
        _sourceLevelName = null;
        _sourceLevelPath = null;
        _targetLevelPath = null;
        _targetLevelName = null;
        _levelDescription = null;
        _levelCountry = null;
        _levelRegion = null;
        _levelBiome = null;
        _levelRoads = null;
        _levelSuitableFor = null;
        _levelFeatures = null;
        _levelAuthors = null;
        _reader = null;
        _errors.Clear();
        _warnings.Clear();
        _messages.Clear();

        StateHasChanged();
        Snackbar.Add("Wizard reset. You can create a new level now.", Severity.Info);
    }

    private bool CanInitialize()
    {
        return !string.IsNullOrWhiteSpace(_targetLevelPath) &&
               !string.IsNullOrWhiteSpace(_targetLevelName) &&
               !string.IsNullOrEmpty(_sourceLevelName);
    }

    private string GetSourceMapTitle()
    {
        if (!string.IsNullOrEmpty(_sourceLevelName))
            return $"Source Map > {_sourceLevelName}";
        return "Select Source Map";
    }

    private void SetBeamInstallDir(string file)
    {
        if (file != GameDirectoryService.GetInstallDirectory())
        {
            GameDirectoryService.SetInstallDirectory(file);
            GetVanillaLevels();
        }
    }

    private string GetBeamInstallDir()
    {
        var currentDir = GameDirectoryService.GetInstallDirectory();
        if (currentDir != _beamInstallDir)
        {
            _beamInstallDir = currentDir;
            GetVanillaLevels();
        }

        return "BeamNG install directory: " + _beamInstallDir;
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
                // Silently fail - vanilla levels are optional
            }
        }
    }

    private void SetDefaultWorkingDirectory()
    {
        AppPaths.EnsureWorkingDirectory();
    }

    /// <summary>
    ///     Cleans up _unpacked and _copyFrom folders to ensure a fresh start.
    ///     This is important to prevent stale data from previous runs.
    /// </summary>
    private void CleanupWorkingDirectories()
    {
        AppPaths.CleanupTempFolders();
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
                width = "100%";
                height = "200px";
                break;
            default:
                width = "400px";
                height = "100%";
                break;
        }
    }

    private readonly Func<FileInfo, string> converter = p => p?.Name;
}