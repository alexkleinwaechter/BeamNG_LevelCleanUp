# Implementation Plan: BeamNG Game Directory Settings

## Overview

This document outlines the implementation plan for persisting the BeamNG.drive installation directory to a settings file, with fallback to Steam detection and user prompts when the path cannot be found.

## Current State

- `Steam.GetBeamInstallDir()` tries to auto-detect BeamNG.drive installation via Windows Registry and Steam library folders
- If detection fails, it returns an empty string
- Multiple pages call `Steam.GetBeamInstallDir()` in their `GetBeamInstallDir()` methods:
  - `CopyAssets.razor.cs`
  - `CopyTerrains.razor.cs`
  - `CopyForestBrushes.razor.cs`
  - `CreateLevel.razor.cs`
- The install path is stored in `Steam.BeamInstallDir` static field (volatile - lost on app restart)

## Goals

1. **On startup**: Check if stored path exists ? use it; if not, try Steam detection ? prompt user if that fails
2. **Persist path**: Save to JSON file in `[userFolder]\AppData\Local\BeamNG_LevelCleanUp\game-settings.json`
3. **Validate on startup**: Verify the stored path still exists before using it
4. **Centralized access**: All calls to `Steam.GetBeamInstallDir()` should be replaced with a centralized getter
5. **Single prompt**: Show the directory selection dialog **once at startup** if needed, not on every page

## Implementation Steps

### Step 1: Create Settings Model

**File**: `BeamNG_LevelCleanUp/Objects/GameSettings.cs`

```csharp
namespace BeamNG_LevelCleanUp.Objects;

/// <summary>
/// Persists game-related settings between application sessions.
/// Settings are stored in the user's AppData folder.
/// </summary>
public class GameSettings
{
    private static readonly string SettingsFile = Path.Combine(AppPaths.SettingsFolder, "game-settings.json");
    
    /// <summary>
    /// Path to the BeamNG.drive installation directory (e.g., "C:\Steam\steamapps\common\BeamNG.drive")
    /// </summary>
    public string BeamNGInstallDirectory { get; set; } = string.Empty;
    
    public static GameSettings? Load();
    public void Save();
}
```

### Step 2: Create GameDirectoryService

**File**: `BeamNG_LevelCleanUp/Utils/GameDirectoryService.cs`

This service will handle all game directory resolution logic:

```csharp
namespace BeamNG_LevelCleanUp.Utils;

/// <summary>
/// Centralized service for managing BeamNG.drive game directory.
/// Handles persistence, validation, and user prompts.
/// </summary>
public static class GameDirectoryService
{
    private static string _cachedInstallDir = string.Empty;
    private static bool _isInitialized = false;
    
    /// <summary>
    /// Gets the BeamNG install directory. Returns cached value if available.
    /// </summary>
    public static string GetInstallDirectory();
    
    /// <summary>
    /// Initializes the game directory on application startup.
    /// Returns true if a valid directory was found/configured.
    /// </summary>
    public static bool Initialize();
    
    /// <summary>
    /// Checks if the BeamNG directory needs to be configured.
    /// </summary>
    public static bool NeedsConfiguration();
    
    /// <summary>
    /// Sets the install directory and saves to settings.
    /// </summary>
    public static void SetInstallDirectory(string path);
    
    /// <summary>
    /// Validates that a given path is a valid BeamNG.drive installation.
    /// </summary>
    public static bool IsValidBeamNGDirectory(string path);
}
```

**Logic Flow in `Initialize()`:**

1. Load `GameSettings` from file
2. If `BeamNGInstallDirectory` exists and is valid ? use it, update `Steam.BeamInstallDir`, return true
3. If path doesn't exist or is invalid:
   a. Try `Steam.GetBeamInstallDir()` (force fresh detection by clearing `Steam.BeamInstallDir` first)
   b. If Steam detection succeeds ? save to settings, return true
   c. If Steam detection fails ? set flag for user prompt, return false

### Step 3: Modify Form1.cs for Startup Initialization and Dialog

**File**: `BeamNG_LevelCleanUp/Form1.cs`

The dialog is shown **once at startup** only if:
1. No valid path is stored in `game-settings.json`, AND
2. Steam auto-detection fails

This ensures users are only prompted when necessary, and the dialog is not shown on every page navigation.

**Note**: We use a Windows Forms `FolderBrowserDialog` since `Form1.cs` is a WinForms host. This is simpler and more reliable than trying to show a Blazor dialog before the WebView is fully loaded. A Blazor dialog component is NOT needed for this feature.

```csharp
public Form1()
{
    InitializeComponent();
    RestoreWindowSettings();
    
    // Initialize centralized application paths
    AppPaths.Initialize(cleanupOnStartup: true);
    
    // Initialize game directory settings synchronously
    // This tries: 1) saved settings, 2) Steam detection
    var gameDirectoryFound = GameDirectoryService.Initialize();
    
    // ... rest of constructor (Blazor setup) ...
    
    // If game directory not found, show dialog after form is loaded
    if (!gameDirectoryFound)
    {
        this.Load += async (s, e) => await ShowGameDirectoryDialogAsync();
    }
};

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
                "Vanilla level features will be unavailable. You can manually set the path later in the settings.",
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
```

### Step 4: Update All Pages to Use GameDirectoryService ? (Partial)

Replace all occurrences of `Steam.GetBeamInstallDir()` with `GameDirectoryService.GetInstallDirectory()`.

**Files already updated:**
- `CopyAssets.razor.cs` ?
- `CopyTerrains.razor.cs` ?
- `CopyForestBrushes.razor.cs` ?
- `CreateLevel.razor.cs` ?

**Files still using old pattern (need update):**
- `Utilities.razor` ?
- `RenameMap.razor` ?
- `ConvertToForest.razor` ?
- `MapShrink.razor` ? (if applicable)

**Pattern replacement:**

Before:
```csharp
protected string GetBeamInstallDir()
{
    if (Steam.BeamInstallDir != _beamInstallDir)
    {
        _beamInstallDir = Steam.GetBeamInstallDir();
        GetVanillaLevels();
    }
    return "BeamNG install directory: " + _beamInstallDir;
}
```

After:
```csharp
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
```

Also update `SetBeamInstallDir()` methods:

Before:
```csharp
protected void SetBeamInstallDir(string file)
{
    if (file != Steam.BeamInstallDir)
    {
        Steam.BeamInstallDir = file;
        GetVanillaLevels();
    }
}
```

After:
```csharp
protected void SetBeamInstallDir(string file)
{
    if (file != GameDirectoryService.GetInstallDirectory())
    {
        GameDirectoryService.SetInstallDirectory(file);
        GetVanillaLevels();
    }
}
```

**Important**: Pages do NOT need to check `NeedsConfiguration()` or show dialogs themselves. They simply call `GetInstallDirectory()` and gracefully handle an empty result (vanilla levels dropdown will be empty, but the page remains functional).

## File Summary

### New Files
1. `BeamNG_LevelCleanUp/Objects/GameSettings.cs` ? - Settings model with Load/Save
2. `BeamNG_LevelCleanUp/Utils/GameDirectoryService.cs` ? - Centralized directory management

### Modified Files
1. `BeamNG_LevelCleanUp/Form1.cs` ? - Add startup initialization and dialog
2. `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyAssets.razor.cs` ? - Use `GameDirectoryService`
3. `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor.cs` ? - Use `GameDirectoryService`
4. `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyForestBrushes.razor.cs` ? - Use `GameDirectoryService`
5. `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor.cs` ? - Use `GameDirectoryService`
6. `BeamNG_LevelCleanUp/BlazorUI/Pages/Utilities.razor` ? - Needs update
7. `BeamNG_LevelCleanUp/BlazorUI/Pages/RenameMap.razor` ? - Needs update
8. `BeamNG_LevelCleanUp/BlazorUI/Pages/ConvertToForest.razor` ? - Needs update

### Removed Files
1. `BeamNG_LevelCleanUp/BlazorUI/Components/GameDirectoryDialog.razor` ? - Removed (not needed, using WinForms dialog)

## Settings File Format

**Path**: `%LOCALAPPDATA%\BeamNG_LevelCleanUp\game-settings.json`

```json
{
  "BeamNGInstallDirectory": "C:\\Steam\\steamapps\\common\\BeamNG.drive"
}
```

## Validation Logic

A directory is a valid BeamNG.drive installation if:
- The directory exists
- Contains a `content` subdirectory
- Contains `content/levels` or similar BeamNG structure

```csharp
public static bool IsValidBeamNGDirectory(string path)
{
    if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        return false;
    
    // Check for BeamNG.drive content structure
    var contentPath = Path.Join(path, "content");
    var levelsPath = Path.Join(contentPath, "levels");
    
    return Directory.Exists(contentPath) && Directory.Exists(levelsPath);
}
```

## Startup Flow Diagram

```
Application Start (Form1 constructor)
         ?
         ?
   AppPaths.Initialize()
         ?
         ?
 GameDirectoryService.Initialize()
         ?
         ??? Load game-settings.json
         ?         ?
         ?         ?
         ?   Path stored and valid? ??Yes??? Use it, return true
         ?         ?
         ?        No
         ?         ?
         ?   Try Steam detection
         ?         ?
         ?         ?
         ?   Steam found valid path? ??Yes??? Save to settings, return true
         ?         ?
         ?        No
         ?         ?
         ?   return false (needs configuration)
         ?
         ?
   Initialize() returned false?
         ?
        Yes
         ?
         ?
   Form.Load event: ShowGameDirectoryDialogAsync()
         ?
         ?
   User selects folder (or cancels)
         ?
         ?
   Valid folder? ??Yes??? Save via SetInstallDirectory()
         ?
        No/Cancel
         ?
         ?
   Show info message, continue with limited functionality
```

## Error Handling

1. **Settings file doesn't exist**: Create new settings with empty directory
2. **Settings file is corrupted**: Log warning, create new settings
3. **Stored path no longer exists**: Try Steam detection, then prompt user
4. **Steam detection fails**: Prompt user for manual selection (once at startup)
5. **User cancels dialog**: Show info message, vanilla levels features will be unavailable but app continues working
6. **User selects invalid folder in dialog**: Show validation error, don't save

## Testing Scenarios

1. **Fresh install, Steam version**: Should auto-detect via Steam, save to settings, no dialog shown
2. **Fresh install, non-Steam version**: Should prompt user once at startup, then save
3. **Existing settings, valid path**: Should load immediately, no dialog shown
4. **Existing settings, path moved/deleted**: Should try Steam detection, then prompt if that fails
5. **User manually changes path in settings file to invalid**: Should try Steam detection, then prompt
6. **User selects invalid folder in dialog**: Should show validation error, not save
7. **User cancels dialog**: App should continue functioning, vanilla level dropdowns will be empty
