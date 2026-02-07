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
    private static readonly CreateLevelWizardState _wizardState = new();
    private readonly CompressionLevel _compressionLevel = CompressionLevel.Optimal;
    private readonly List<string> _errors = new();
    private readonly List<string> _messages = new();
    private readonly List<string> _warnings = new();

    private readonly Func<FileInfo, string> converter = p => p?.Name;
    private Anchor _anchor;
    private string _beamInstallDir;
    private bool _isInitializing;
    private readonly LevelInfoModel _levelInfo = new();
    private bool _openDrawer;
    private BeamFileReader _reader;
    private bool _showErrorLog;
    private bool _showWarningLog;

    private string _sourceLevelName;
    private string _sourceLevelPath;
    private string _sourceZipPath;
    private Snackbar _staticSnackbar;
    private List<FileInfo> _vanillaLevels = new();
    private FileInfo _vanillaLevelSourceSelected;
    private MudExpansionPanels FileSelect;
    private string height;
    private string width;

    /// <summary>
    ///     Exposes the wizard state for other pages (e.g., CopyTerrains in wizard mode)
    /// </summary>
    public static CreateLevelWizardState GetWizardState()
    {
        return _wizardState;
    }

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

    protected void OnSourceMapSelected(string file)
    {
        _sourceZipPath = file;
        _sourceLevelName = Path.GetFileNameWithoutExtension(file);
        _vanillaLevelSourceSelected = null;
        StateHasChanged();
    }

    protected void OnVanillaSourceSelected(FileInfo file)
    {
        if (file == null)
        {
            _vanillaLevelSourceSelected = null;
            _sourceZipPath = null;
            _sourceLevelName = null;
            StateHasChanged();
            return;
        }

        _vanillaLevelSourceSelected = file;
        _sourceZipPath = file.FullName;
        _sourceLevelName = Path.GetFileNameWithoutExtension(file.Name);
        StateHasChanged();
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
                // 1. Set up working directory and clean up previous data
                SetDefaultWorkingDirectory();
                CleanupWorkingDirectories();

                // 2. Copy source file to working directory if needed
                var sourceFile = _sourceZipPath;
                if (ZipFileHandler.WorkingDirectory != Path.GetDirectoryName(sourceFile))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                        $"Copying source level to working directory...");
                    var target = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(sourceFile));
                    File.Copy(sourceFile, target, true);
                    sourceFile = target;
                    PubSubChannel.SendMessage(PubSubMessageType.Info, "Source level copied");
                }

                // 3. Extract source level
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Extracting source level...");
                _sourceLevelPath = ZipFileHandler.ExtractToDirectory(sourceFile, "_copyFrom", true);
                _reader = new BeamFileReader(_sourceLevelPath, null);
                var resolvedName = _reader.GetLevelName();
                if (!string.IsNullOrEmpty(resolvedName))
                    _sourceLevelName = resolvedName;

                _wizardState.SourceLevelPath = _sourceLevelPath;
                _wizardState.SourceLevelName = _sourceLevelName;
                PubSubChannel.SendMessage(PubSubMessageType.Info, "Source level extracted successfully");

                // 4. Create target directory structure
                // targetRoot will be: WorkingDirectory/_unpacked/levels/targetLevelPath
                var sanitizedPath = StringUtils.SanitizeFileName(_levelInfo.LevelPath);
                var targetRoot = Path.Join(
                    ZipFileHandler.WorkingDirectory,
                    "_unpacked",
                    "levels",
                    sanitizedPath);

                _wizardState.TargetLevelRootPath = targetRoot;
                // targetLevelNamePath is the same as targetRoot since we're already in levels/levelname
                var targetLevelNamePath = targetRoot;
                _wizardState.TargetLevelPath = sanitizedPath;
                _wizardState.LevelName = _levelInfo.DisplayName;

                Directory.CreateDirectory(targetRoot);
                Directory.CreateDirectory(Path.Join(targetLevelNamePath, "art", "terrains"));
                Directory.CreateDirectory(Path.Join(targetLevelNamePath, "main", "MissionGroup"));

                PubSubChannel.SendMessage(PubSubMessageType.Info, "Created directory structure");

                // 5. Create empty terrain material files
                File.WriteAllText(
                    Path.Join(targetLevelNamePath, "art", "terrains", "main.materials.json"),
                    "{}");

                PubSubChannel.SendMessage(PubSubMessageType.Info, "Created empty material files");

                // 6. Create info.json and mainLevel.lua
                InfoJsonGenerator.CreateInfoJson(
                    targetLevelNamePath,
                    _levelInfo.DisplayName,
                    _levelInfo.Description,
                    _levelInfo.Country,
                    _levelInfo.Region,
                    _levelInfo.Biome,
                    _levelInfo.Roads,
                    _levelInfo.SuitableFor,
                    _levelInfo.Features,
                    _levelInfo.Authors);
                InfoJsonGenerator.CreateMainLevelLua(targetLevelNamePath);

                // 7. Generate preview image
                LevelPreviewGenerator.GeneratePreviewImage(targetLevelNamePath, sanitizedPath, _levelInfo.DisplayName);

                // 8. Copy MissionGroup data
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
                    sanitizedPath,
                    BeamFileReader.MaterialsJson); // Pass source materials

                missionGroupCopier.CopyMissionGroupData();

                // 9. Update wizard state
                _wizardState.CopiedMissionGroupAssets = new List<Asset>(BeamFileReader.Assets);
                _wizardState.Step1_SetupComplete = true;
                _wizardState.Step2_MissionGroupsCopied = true;
                _wizardState.IsActive = true;
                _wizardState.CurrentStep = 1;
            });

            // Write operation logs
            WriteCreateLevelLogs();

            Snackbar.Remove(_staticSnackbar);
            Snackbar.Add("Level initialization complete!", Severity.Success);
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

                if (result.Canceled) return; // User canceled, don't proceed with copy

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
        _sourceZipPath = null;
        _levelInfo.Reset();
        _reader = null;
        _errors.Clear();
        _warnings.Clear();
        _messages.Clear();

        StateHasChanged();
        Snackbar.Add("Wizard reset. You can create a new level now.", Severity.Info);
    }

    private bool CanInitialize()
    {
        return !string.IsNullOrWhiteSpace(_levelInfo.LevelPath) &&
               !string.IsNullOrWhiteSpace(_levelInfo.DisplayName) &&
               !string.IsNullOrEmpty(_sourceZipPath);
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

    private void WriteCreateLevelLogs()
    {
        if (string.IsNullOrEmpty(_wizardState.TargetLevelRootPath))
            return;

        try
        {
            var logPath = _wizardState.TargetLevelRootPath;

            if (_messages.Any())
            {
                var messagesPath = Path.Combine(logPath, "Log_CreateLevel.txt");
                var messagesWithHeader = new List<string>
                {
                    $"# Create Level Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"# Source: {_sourceLevelName}",
                    $"# Target: {_levelInfo.DisplayName} ({_levelInfo.LevelPath})",
                    ""
                };
                messagesWithHeader.AddRange(_messages);
                File.WriteAllLines(messagesPath, messagesWithHeader);
            }

            if (_warnings.Any())
            {
                var warningsPath = Path.Combine(logPath, "Log_CreateLevel_Warnings.txt");
                File.WriteAllLines(warningsPath, _warnings);
            }

            if (_errors.Any())
            {
                var errorsPath = Path.Combine(logPath, "Log_CreateLevel_Errors.txt");
                File.WriteAllLines(errorsPath, _errors);
            }

            PubSubChannel.SendMessage(PubSubMessageType.Info,
                $"Level creation logs written to: {Path.GetFileName(logPath)}");
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not write log files: {ex.Message}");
        }
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
}