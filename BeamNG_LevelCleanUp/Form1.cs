using BeamNG_LevelCleanUp.BlazorUI;
using BeamNG_LevelCleanUp.BlazorUI.Services;
using BeamNG_LevelCleanUp.Logic;
using BeamNG_LevelCleanUp.Objects;
using BeamNG_LevelCleanUp.Utils;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BeamNG_LevelCleanUp;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();
        RestoreWindowSettings();
        
        // Initialize centralized application paths and clean up stale temp folders from previous sessions
        AppPaths.Initialize(cleanupOnStartup: true);
        
        // Initialize game directory settings synchronously
        // This tries: 1) saved settings from game-settings.json, 2) Steam auto-detection
        var gameDirectoryFound = GameDirectoryService.Initialize();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddWindowsFormsBlazorWebView();

        // Code for MudBlazor
        serviceCollection.AddMudServices();
        
        // Add 3D viewer service
        serviceCollection.AddSingleton<Viewer3DService>();

        blazorWebView1.HostPage = @"wwwroot\index.html";
#if DEBUG
        serviceCollection.AddBlazorWebViewDeveloperTools();
        serviceCollection.AddLogging();
#endif
        blazorWebView1.Services = serviceCollection.BuildServiceProvider();
        blazorWebView1.RootComponents.Add<App>("#app");
        
        // If game directory not found, show dialog once after form is loaded
        if (!gameDirectoryFound)
        {
            this.Load += async (s, e) => await ShowGameDirectoryDialogAsync();
        }
    }

    /// <summary>
    /// Shows a Windows Forms folder browser dialog to select the BeamNG.drive directory.
    /// Called once at startup if the directory could not be auto-detected.
    /// </summary>
    private async Task ShowGameDirectoryDialogAsync()
    {
        // Small delay to ensure the form and Blazor WebView are fully loaded
        await Task.Delay(500);
        
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select BeamNG.drive Installation Directory",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        
        // Try to start in a reasonable location
        var commonPaths = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common",
            @"C:\Steam\steamapps\common",
            @"D:\Steam\steamapps\common",
            @"D:\SteamLibrary\steamapps\common"
        };
        
        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path))
            {
                dialog.InitialDirectory = path;
                break;
            }
        }
        
        var result = dialog.ShowDialog(this);
        
        if (result == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
        {
            if (GameDirectoryService.IsValidBeamNGDirectory(dialog.SelectedPath))
            {
                GameDirectoryService.SetInstallDirectory(dialog.SelectedPath);
                MessageBox.Show(
                    $"BeamNG.drive directory set to:\n{dialog.SelectedPath}",
                    "Configuration Saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "The selected folder does not appear to be a valid BeamNG.drive installation.\n\n" +
                    "Expected structure: [folder]/content/levels\n\n" +
                    "Vanilla level features will be unavailable. You can manually set the path later on the relevant pages.",
                    "Invalid Directory",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        else
        {
            MessageBox.Show(
                "BeamNG.drive installation directory was not configured.\n\n" +
                "Features requiring vanilla levels (like Copy Terrains, Copy Assets) will have limited functionality.\n\n" +
                "You can manually set the path later using the folder browser on those pages.",
                "Configuration Skipped",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
    }

    public Task InitializeAsync(CancellationToken token)
    {
        //Nur für splashscreen
        return Task.Delay(TimeSpan.FromSeconds(0));
    }

    public void Initialize()
    {
        //Nur für splashscreen
        //Thread.Sleep(TimeSpan.FromSeconds(5));
    }

    private void Form1_FormClosed(object sender, FormClosedEventArgs e)
    {
    }

    private void Form1_FormClosing(object sender, FormClosingEventArgs e)
    {
        // Save window settings before closing
        SaveWindowSettings();

        if (!string.IsNullOrEmpty(ZipFileHandler.GetLastUnpackedPath()) ||
            !string.IsNullOrEmpty(ZipFileHandler.GetLastUnpackedCopyFromPath()))
        {
            if (MessageBox.Show("Should the Working Directory be cleaned from unpacked data?", "Cleanup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                try
                {
                    ZipFileHandler.CleanUpWorkingDirectory();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    if (MessageBox.Show(
                            $"Error while cleaning up working directory: {ex.Message}. Maybe you have an editor or terminal open in one of the directories. Do you want to close the tool anyway?",
                            "Error", MessageBoxButtons.YesNo, MessageBoxIcon.Error) ==
                        DialogResult.Yes) Environment.Exit(0);
                    e.Cancel = true;
                }
        }
        else
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    ///     Restores window size, position, and state from saved settings.
    /// </summary>
    private void RestoreWindowSettings()
    {
        var settings = WindowSettings.Load();
        settings?.ApplyTo(this);
    }

    /// <summary>
    ///     Saves current window size, position, and state to settings file.
    /// </summary>
    private void SaveWindowSettings()
    {
        var settings = WindowSettings.FromForm(this);
        settings.Save();
    }
}