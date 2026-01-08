# Working Directory Consolidation Plan

## Executive Summary

This document analyzes the current working directory handling in BeamNG Tools and proposes a migration to a single, consistent working directory at:

```
C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp\
```

With the following structure:
```
C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\
??? temp\
?   ??? _unpacked\        # Target level extraction
?   ??? _copyFrom\        # Source level extraction
??? window-settings.json  # Already exists
??? logs\                 # Proposed: Centralized logs (optional)
```

---

## Key Requirements

### Mandatory Behavior Changes

1. **Every page (except GenerateTerrain) MUST reset the working directory** to the centralized `AppData\Local\BeamNG_LevelCleanUp\temp` folder on initialization
2. **CreateLevel wizard** especially needs this - it's a multi-step process that can break with stale state
3. **Previously loaded level detection** must continue to work (see analysis below)

---

## Current State Analysis

### 1. Current Working Directory Locations

The application currently uses **multiple working directory strategies** depending on the context:

| Scenario | Current Location | Set By |
|----------|-----------------|--------|
| User selects a ZIP file | Directory of the ZIP file | `ZipFileHandler.WorkingDirectory = Path.GetDirectoryName(file)` |
| User selects a vanilla level | `%USERPROFILE%\BeamNgMT` | `SetDefaultWorkingDirectory()` |
| Window settings | `%LOCALAPPDATA%\BeamNG_LevelCleanUp` | `WindowSettings.cs` |
| GenerateTerrain (folder mode) | User-selected folder | Direct folder selection |
| CopyAssets direct to folder | User-selected target folder | `FileSelected(file, isFolder: true)` |

### 2. Files Using Working Directory

#### **Static Property: `ZipFileHandler.WorkingDirectory`**
- Located in: `Logic/ZipFileHandler.cs`
- Type: `public static string WorkingDirectory { get; set; }`
- Used by almost all pages for extraction/deployment

#### **Pages That Set Working Directory:**

| Page | Method | Behavior |
|------|--------|----------|
| `MapShrink.razor` | `FileSelected()` | Sets to ZIP file's directory |
| `MapShrink.razor` | `SetDefaultWorkingDirectory()` | Sets to `%USERPROFILE%\BeamNgMT` for vanilla levels |
| `RenameMap.razor` | Same as MapShrink | Same behavior |
| `Utilities.razor` | Same as MapShrink | Same behavior |
| `CopyTerrains.razor.cs` | Multiple methods | Sets based on file selection + vanilla handling |
| `CopyAssets.razor.cs` | Multiple methods | Same as CopyTerrains |
| `CopyForestBrushes.razor.cs` | Multiple methods | Same pattern |
| `CreateLevel.razor.cs` | `SetDefaultWorkingDirectory()` | Uses `%USERPROFILE%\BeamNgMT` |
| `GenerateTerrain.razor.cs` | `OnWorkingDirectorySelected()` | User-selected folder (different pattern) |

### 3. Methods That Set Default Working Directory

All pages have nearly identical `SetDefaultWorkingDirectory()` methods:

```csharp
public void SetDefaultWorkingDirectory()
{
    if (string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory) || /* condition */)
    {
        ZipFileHandler.WorkingDirectory = 
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BeamNgMT");
        Directory.CreateDirectory(ZipFileHandler.WorkingDirectory);
    }
}
```

**Current default path:** `C:\Users\{username}\BeamNgMT`

---

## Analysis: Previously Loaded Level Detection

### How It Works

Several pages (RenameMap, Utilities) have a `CheckPreviousLevel()` method that detects if a level was previously loaded:

```csharp
// In RenameMap.razor and Utilities.razor
void CheckPreviousLevel()
{
    var bfr = new BeamFileReader();  // Parameterless constructor!
    _levelName = bfr.GetLevelName();
    _levelPath = bfr.GetLevelPath();
    if (!string.IsNullOrEmpty(_levelName) && !string.IsNullOrEmpty(_levelPath))
    {
        _renameCurrentName = _levelName;
        _staticSnackbar = Snackbar.Add(
            $"The level {_levelName} is still loaded. You can either rename it now or load another level.",
            Severity.Info);
    }
}
```

### Why It Works (Static State)

`BeamFileReader` uses **static properties** to store level information:

```csharp
// In BeamFileReader.cs
private static string _levelPath { get; set; }
private static string _levelName { get; set; }
private static string _levelNamePath { get; set; }

internal BeamFileReader()
{
    // Parameterless constructor - does NOT reset static fields
    var beamInstallDir = Steam.GetBeamInstallDir();
}

internal string GetLevelName() => _levelName;
internal string GetLevelPath() => _levelPath;
```

The flow:
1. User loads a level on Page A ? `BeamFileReader(levelpath, ...)` populates static fields
2. User navigates to Page B ? `CheckPreviousLevel()` creates `new BeamFileReader()` (parameterless)
3. `GetLevelName()` returns the static `_levelName` from step 1
4. The UI shows "level X is still loaded"

### Impact of Centralized Working Directory

**? This feature will work BETTER with centralized working directory!**

| Aspect | Current (scattered) | Proposed (centralized) |
|--------|---------------------|------------------------|
| `_levelPath` value | Points to extracted folder (varies per source ZIP location) | Always points to `AppData\...\temp\_unpacked\levels\{name}` |
| Files still exist? | Maybe - depends on where user extracted | Yes - centralized folder is persistent |
| Cross-page detection | Works if user didn't close app | Works reliably |
| After app restart | Files may be scattered in random folders | Files in known location |

**Key Point:** The static `_levelPath` points to the **extracted folder**, not the original ZIP. As long as:
1. The extracted files still exist at `_levelPath`
2. The static variables haven't been reset

...the detection will work. With centralized temp folders, the files are MORE likely to still exist.

### Considerations for Implementation

1. **Do NOT call `Reset()` on page init** - This clears the static fields
2. **Do NOT clear `ZipFileHandler.WorkingDirectory` to null** - Use `AppPaths.TempFolder` instead
3. **The `_unpacked` folder should persist between page navigations** - Only cleanup on explicit user action or new extraction

---

## Analysis of Potential Conflicts

### Feature: GenerateTerrain.razor (Opening Level Folder)

**Current Behavior:**
- User selects an **existing level folder** (not a ZIP)
- The folder IS the working directory (no extraction needed)
- Materials, heightmap, and output are all relative to this folder
- No use of `_unpacked` or `_copyFrom`

**Impact of Proposed Change:**
- ?? **CONFLICT DETECTED** - This feature should **NOT** use the centralized temp folder
- The selected folder is the actual level being edited
- The `_state.WorkingDirectory` in GenerateTerrain is conceptually different from `ZipFileHandler.WorkingDirectory`

**Recommendation:**
- **DO NOT CHANGE** GenerateTerrain behavior
- Keep GenerateTerrain using its own working directory concept (the level folder itself)
- Only change `ZipFileHandler.WorkingDirectory` for extraction/deployment scenarios

### Feature: CopyAssets Direct to Folder

**Current Behavior:**
- `CopyTerrains.razor` and `CopyAssets.razor` allow selecting a **folder as target**:
  ```csharp
  protected async Task FileSelected(string file, bool isFolder)
  {
      if (_vanillaLevelTargetSelected == null)
      {
          ZipFileHandler.WorkingDirectory = isFolder ? file : Path.GetDirectoryName(file);
      }
      // ...
      if (!isFolder)
          _levelPath = ZipFileHandler.ExtractToDirectory(..., "_unpacked");
      else
          _levelPath = ZipFileHandler.GetNamePath(file); // No extraction!
  }
  ```

**Impact of Proposed Change:**
- ?? **PARTIAL CONFLICT** - When a folder is selected, extraction is skipped
- The folder-as-target feature should continue to work directly on the selected folder
- Only ZIP extraction should use the centralized temp directory

**Recommendation:**
- When `isFolder == true`: Work directly in the selected folder (no change needed)
- When `isFolder == false` (ZIP): Use centralized temp directory for extraction

---

## Proposed Architecture

### New AppPaths Static Class

Create a new centralized class for path management:

```csharp
// Location: BeamNG_LevelCleanUp/Utils/AppPaths.cs
namespace BeamNG_LevelCleanUp.Utils;

using BeamNG_LevelCleanUp.Communication;

/// <summary>
/// Centralized application path management.
/// All temporary extraction folders are under AppData\Local\BeamNG_LevelCleanUp\temp
/// </summary>
public static class AppPaths
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BeamNG_LevelCleanUp");

    /// <summary>
    /// Base folder for temporary extraction operations.
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp
    /// </summary>
    public static string TempFolder => Path.Combine(AppDataFolder, "temp");

    /// <summary>
    /// Folder for extracted target level (unpacking ZIPs).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp\_unpacked
    /// </summary>
    public static string UnpackedFolder => Path.Combine(TempFolder, "_unpacked");

    /// <summary>
    /// Folder for extracted source level (copy operations).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp\_copyFrom
    /// </summary>
    public static string CopyFromFolder => Path.Combine(TempFolder, "_copyFrom");

    /// <summary>
    /// Folder for application logs.
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\logs
    /// </summary>
    public static string LogsFolder => Path.Combine(AppDataFolder, "logs");

    /// <summary>
    /// Settings folder (already used by WindowSettings).
    /// C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp
    /// </summary>
    public static string SettingsFolder => AppDataFolder;

    /// <summary>
    /// Ensures all required directories exist.
    /// Call this at application startup.
    /// </summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(TempFolder);
        Directory.CreateDirectory(LogsFolder);
    }

    /// <summary>
    /// Sets the working directory to the centralized temp folder.
    /// Call this on page initialization for all pages EXCEPT GenerateTerrain.
    /// </summary>
    public static void EnsureWorkingDirectory()
    {
        ZipFileHandler.WorkingDirectory = TempFolder;
        Initialize(); // Creates all directories
    }

    /// <summary>
    /// Cleans up all temporary extraction folders.
    /// Safe to call - creates fresh empty directories.
    /// </summary>
    public static void CleanupTempFolders()
    {
        CleanupFolder(UnpackedFolder);
        CleanupFolder(CopyFromFolder);
    }

    /// <summary>
    /// Cleans up only the _unpacked folder.
    /// </summary>
    public static void CleanupUnpackedFolder()
    {
        CleanupFolder(UnpackedFolder);
    }

    /// <summary>
    /// Cleans up only the _copyFrom folder.
    /// </summary>
    public static void CleanupCopyFromFolder()
    {
        CleanupFolder(CopyFromFolder);
    }

    private static void CleanupFolder(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
                Directory.Delete(folderPath, true);
            Directory.CreateDirectory(folderPath);
        }
        catch (Exception ex)
        {
            // Log but don't throw - cleanup failures shouldn't block operations
            PubSubChannel.SendMessage(PubSubMessageType.Warning,
                $"Could not clean up {Path.GetFileName(folderPath)}: {ex.Message}");
        }
    }
}
```

### Modified ZipFileHandler

Update `ZipFileHandler.cs` to use the new paths:

```csharp
public static class ZipFileHandler
{
    // Use AppPaths.TempFolder as default when not set
    public static string WorkingDirectory 
    { 
        get => _workingDirectory ?? AppPaths.TempFolder;
        set => _workingDirectory = value;
    }
    private static string _workingDirectory;

    /// <summary>
    /// Resets working directory to the default centralized temp folder.
    /// Call this instead of setting WorkingDirectory = null.
    /// </summary>
    public static void ResetToDefaultWorkingDirectory()
    {
        _workingDirectory = AppPaths.TempFolder;
    }

    // Existing methods remain largely unchanged...
}
```

---

## Migration Steps

### Phase 1: Add New Infrastructure (Non-Breaking)

1. **Create `AppPaths.cs`** - New centralized path management
2. **Update `Form1.cs`** - Call `AppPaths.Initialize()` at startup
3. **Update `WindowSettings.cs`** - Use `AppPaths.SettingsFolder` constant

### Phase 2: Update Page Initialization

**Every page (except GenerateTerrain) must call `AppPaths.EnsureWorkingDirectory()` on init:**

```csharp
protected override void OnInitialized()
{
    // IMPORTANT: Always reset to default working directory
    AppPaths.EnsureWorkingDirectory();
    
    // Then check for previously loaded level (this uses BeamFileReader static state, not ZipFileHandler)
    CheckPreviousLevel();
    
    // ... rest of initialization
}
```

### Phase 3: Update SetDefaultWorkingDirectory Methods

Replace all `SetDefaultWorkingDirectory()` methods:

**Before (in each page):**
```csharp
public void SetDefaultWorkingDirectory()
{
    if (string.IsNullOrEmpty(ZipFileHandler.WorkingDirectory) || _vanillaLevelSourceSelected != null)
    {
        ZipFileHandler.WorkingDirectory = 
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BeamNgMT");
        Directory.CreateDirectory(ZipFileHandler.WorkingDirectory);
    }
}
```

**After (simplified - same in all pages):**
```csharp
private void SetDefaultWorkingDirectory()
{
    AppPaths.EnsureWorkingDirectory();
}
```

Or simply remove the method entirely and call `AppPaths.EnsureWorkingDirectory()` directly.

### Phase 4: Update FileSelected Methods

For pages with ZIP file selection (MapShrink, RenameMap, Utilities):

**Before:**
```csharp
protected async Task FileSelected(string file)
{
    // ... reset state ...
    ZipFileHandler.WorkingDirectory = Path.GetDirectoryName(file);  // REMOVE THIS
    // ...
}
```

**After:**
```csharp
protected async Task FileSelected(string file)
{
    // ... reset state ...
    // WorkingDirectory is already set by OnInitialized -> AppPaths.EnsureWorkingDirectory()
    // No need to change it here - always use centralized temp folder
    // ...
}
```

### Phase 5: Handle Folder-Selection Mode

For pages that support direct folder editing (CopyTerrains, CopyAssets):

```csharp
protected async Task FileSelected(string file, bool isFolder)
{
    if (isFolder)
    {
        // Direct folder mode - NO extraction, work in place
        // DO NOT change working directory to temp folder
        _levelPath = ZipFileHandler.GetNamePath(file);
        // Working directory remains the folder itself for deployment output
        ZipFileHandler.WorkingDirectory = file;
    }
    else
    {
        // ZIP mode - working directory already set to centralized temp folder
        // Extract to _unpacked within temp folder
        _levelPath = ZipFileHandler.ExtractToDirectory(..., "_unpacked");
    }
}
```

### Phase 6: Update GenerateTerrain (NO CHANGES)

**NO CHANGES NEEDED** to GenerateTerrain. It uses a completely different concept:
- The `_state.WorkingDirectory` represents the actual level folder being edited
- NOT a temp extraction folder
- It should NOT call `AppPaths.EnsureWorkingDirectory()`

### Phase 7: Update CreateLevel Cleanup Logic

Update `CreateLevel.razor.cs` cleanup method:

```csharp
private void CleanupWorkingDirectories()
{
    AppPaths.CleanupTempFolders(); // Use centralized cleanup
}
```

---

## Files to Modify

### Must Change:
| File | Changes |
|------|---------|
| `Utils/AppPaths.cs` | **NEW FILE** - Centralized path management |
| `Form1.cs` | Add `AppPaths.Initialize()` at startup |
| `Logic/ZipFileHandler.cs` | Update default, add `ResetToDefaultWorkingDirectory()` |
| `Objects/WindowSettings.cs` | Use `AppPaths.SettingsFolder` |

### Pages to Update (add OnInitialized call + update SetDefaultWorkingDirectory):
| File | Notes |
|------|-------|
| `BlazorUI/Pages/MapShrink.razor` | Add `AppPaths.EnsureWorkingDirectory()` in OnInitialized, remove `WorkingDirectory = Path.GetDirectoryName(file)` |
| `BlazorUI/Pages/RenameMap.razor` | Same + keep `CheckPreviousLevel()` |
| `BlazorUI/Pages/Utilities.razor` | Same + keep `CheckPreviousLevel()` |
| `BlazorUI/Pages/CopyTerrains.razor.cs` | Add init call, preserve folder mode exception |
| `BlazorUI/Pages/CopyAssets.razor.cs` | Same |
| `BlazorUI/Pages/CopyForestBrushes.razor.cs` | Same |
| `BlazorUI/Pages/CreateLevel.razor.cs` | Add init call + update cleanup logic |
| `BlazorUI/Pages/ConvertForest.razor` | Add init call if missing |

### No Changes Needed:
| File | Reason |
|------|--------|
| `BlazorUI/Pages/GenerateTerrain.razor.cs` | Uses separate working directory concept |
| `BlazorUI/Pages/Welcome.razor` | No file operations |

---

## Backward Compatibility Considerations

### Existing User Data
- Old `%USERPROFILE%\BeamNgMT` folder will be **ignored** (not deleted)
- Users can manually clean it up
- No migration of existing temp files needed (they're temporary by nature)

### Previously Loaded Level Detection
- **WILL CONTINUE TO WORK** - Uses `BeamFileReader` static fields, not `ZipFileHandler.WorkingDirectory`
- Actually works **better** because extracted files are in a predictable location

### Working Directory Display in UI
- Footer displays: `Working Directory: {path}`
- This will now show: `C:\Users\{username}\AppData\Local\BeamNG_LevelCleanUp\temp`
- Consider shortening display to `%LOCALAPPDATA%\BeamNG_LevelCleanUp\temp`

### Deployment ZIP Output Location
- Currently: ZIP is created in working directory (same as source ZIP location or `BeamNgMT`)
- **Decision needed:** Should deployment ZIPs go to:
  - A) Same temp folder (current behavior)
  - B) User's Documents folder
  - C) Same folder as source ZIP was selected from
  - D) Let user choose via save dialog

**Recommendation:** Keep current behavior (option A) initially, add save dialog option later.

---

## Testing Checklist

After implementation, test the following scenarios:

### Basic Operations:
- [ ] MapShrink: Select ZIP file, analyze, delete files, build ZIP
- [ ] RenameMap: Select ZIP file, rename, build ZIP
- [ ] Utilities: Select ZIP file, change position, build ZIP
- [ ] Selecting vanilla level from dropdown works

### Previously Loaded Level Detection:
- [ ] Load level on MapShrink, navigate to RenameMap ? should show "level X is still loaded"
- [ ] Load level on RenameMap, navigate to Utilities ? should show "level X is still loaded"
- [ ] This detection should work AFTER migration

### Copy Operations:
- [ ] CopyTerrains: Source ZIP + Target ZIP
- [ ] CopyTerrains: Source ZIP + Target FOLDER (direct edit mode)
- [ ] CopyAssets: Both modes
- [ ] CopyForestBrushes: Both modes

### CreateLevel Wizard:
- [ ] Full wizard flow from source to deployment
- [ ] "Copy to BeamNG Levels" works
- [ ] Wizard reset cleans up properly
- [ ] **Switching pages and returning to CreateLevel doesn't break state**

### GenerateTerrain:
- [ ] Select existing level folder - works as before
- [ ] Output files go to correct location
- [ ] Does NOT affect temp folder

### Edge Cases:
- [ ] Switch between pages without finishing operations
- [ ] Multiple back-to-back operations
- [ ] Application restart (temp folders should be empty or recreated)

---

## Summary

| Aspect | Current | Proposed |
|--------|---------|----------|
| Default temp location | `%USERPROFILE%\BeamNgMT` | `%LOCALAPPDATA%\BeamNG_LevelCleanUp\temp` |
| When ZIP selected | Uses ZIP's parent folder | Uses centralized temp folder |
| When folder selected | Uses selected folder | **No change** (works in place) |
| GenerateTerrain | Uses selected level folder | **No change** |
| Settings storage | `%LOCALAPPDATA%\BeamNG_LevelCleanUp` | **No change** |
| Cleanup | Per-page cleanup methods | Centralized `AppPaths.CleanupTempFolders()` |
| Previously loaded level | Works via static BeamFileReader fields | **No change** (still works) |
| Page initialization | Varies by page | All pages call `AppPaths.EnsureWorkingDirectory()` (except GenerateTerrain) |

The key insight is that there are **two different concepts of "working directory"**:
1. **Extraction temp folder** - For ZIP operations, should be centralized
2. **Level folder** - For direct folder editing (GenerateTerrain, folder mode), should remain as-is

And **two different state mechanisms**:
1. **`ZipFileHandler.WorkingDirectory`** - Where to extract ZIPs ? centralize to AppData
2. **`BeamFileReader` static fields** - Which level is currently loaded ? unchanged

This plan consolidates #1 while preserving #2.
