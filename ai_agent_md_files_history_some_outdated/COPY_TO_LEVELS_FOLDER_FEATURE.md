# Copy to BeamNG Levels Folder Feature - Implementation Summary

## Overview

Added "Copy to BeamNG Levels Folder" functionality to the Create Level wizard, allowing users to directly copy their newly created level to the BeamNG user levels folder without needing to manually extract and copy files.

## Problem Statement

After creating a level using the wizard, users had to:
1. Build a deployment ZIP file
2. Manually extract it
3. Copy it to `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\`

This was cumbersome and error-prone, especially for users unfamiliar with the BeamNG folder structure.

## Solution

Implemented a "Copy to BeamNG Levels Folder" button that:
- Automatically finds the BeamNG user levels folder
- Copies the newly created level directly
- Handles existing levels with overwrite confirmation
- Removes mod.info file before copying

## Implementation Details

### UI Changes

**File**: `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`

Added a new button in the completion section (Step 3):

```razor
<MudButton OnClick="@CopyToLevelsFolder" 
          Color="Color.Success" 
          Variant="Variant.Filled"
          StartIcon="@Icons.Material.Filled.Folder">
    Copy to BeamNG Levels Folder
</MudButton>
```

**Button Placement**:
- Shown only when wizard is complete (`Step3_TerrainMaterialsSelected`)
- Positioned between "Build Deployment ZIP" and "Create Another Level" buttons
- Uses green color (`Color.Success`) to differentiate from ZIP build (blue/primary)

### Backend Logic

Added `CopyToLevelsFolder()` method that:

1. **Determines paths**:
   ```csharp
   var path = Path.Join(ZipFileHandler.WorkingDirectory, "_unpacked");
   var customChangesChecker = new CustomChangesChecker(_targetLevelPath, path);
   ```

2. **Checks for existing level**:
   ```csharp
   if (customChangesChecker.TargetDirectoryExists())
   {
       // Show confirmation dialog
       // Delete existing if user confirms
   }
   ```

3. **Copies level**:
   ```csharp
   ZipFileHandler.RemoveModInfo(path);
   customChangesChecker.CopyUnpackedToUserFolder();
   ```

4. **Provides feedback**:
   ```csharp
   Snackbar.Add($"Level '{_targetLevelName}' successfully copied to BeamNG levels folder.", 
       Severity.Success);
   ```

## How It Works

### Target Directory Detection

Uses `CustomChangesChecker` which:
- Tries new path first: `%LOCALAPPDATA%\BeamNG\BeamNG.drive\current\levels\`
- Falls back to versioned folders: `%LOCALAPPDATA%\BeamNG.drive\{version}\levels\`
- Automatically selects the highest version number if multiple exist

### Overwrite Handling

If the level already exists:
1. Shows a warning dialog with level name
2. Asks user "Do you want to overwrite it?"
3. If confirmed: Deletes existing directory completely
4. If canceled: Aborts copy operation

### File Preparation

Before copying:
- Removes `mod.info` file using `ZipFileHandler.RemoveModInfo()`
- This ensures the level isn't treated as a mod by BeamNG

### Copy Process

- Recursively copies all files and subdirectories
- Overwrites existing files (if directory wasn't deleted)
- Preserves directory structure
- Shows progress notification during copy

## User Experience Flow

### Wizard Complete State

```
???????????????????????????????????????????????????
? ? Level Creation Complete!                     ?
?                                                 ?
? Your level is ready. You can now build a       ?
? deployment file or copy it to BeamNG.          ?
?                                                 ?
? [Build Deployment ZIP]  [Copy to Levels]       ?
?                         [Create Another Level] ?
???????????????????????????????????????????????????
```

### Copy Flow

1. **User clicks "Copy to BeamNG Levels Folder"**
2. **If level exists**:
   ```
   ???????????????????????????????????????????????
   ? Level Already Exists                        ?
   ?                                             ?
   ? The level 'my_custom_map' already exists   ?
   ? in your BeamNG levels folder.              ?
   ? Do you want to overwrite it?               ?
   ?                                             ?
   ?            [Cancel] [Yes, Overwrite]        ?
   ???????????????????????????????????????????????
   ```
3. **Progress notification**: "Copying level to BeamNG levels folder..."
4. **Success notification**: "Level 'My Custom Map' successfully copied to BeamNG levels folder."

### In-Game Result

After copying, the level appears in BeamNG:
- **Free Roam** ? Level list shows "My Custom Map"
- **Level Editor** ? Can be opened directly
- No need to restart BeamNG (levels loaded dynamically)

## Code Reuse

Leverages existing `CustomChangesChecker` class used in `RenameMap.razor`:
- `TargetDirectoryExists()` - Checks if level exists
- `DeleteTargetDirectory()` - Removes existing level
- `CopyUnpackedToUserFolder()` - Performs the copy

This ensures consistency across features and reduces code duplication.

## Benefits

### For Users

1. **Convenience**: One-click copy to BeamNG
2. **Safety**: Warns before overwriting existing levels
3. **Time-saving**: No manual file extraction/copying
4. **Error-prevention**: Automatically finds correct folder
5. **Immediate testing**: Level available in BeamNG right away

### For Workflow

1. **Create** ? Select source, configure level, initialize
2. **Customize** ? Copy terrain materials
3. **Deploy** ? Copy to levels folder
4. **Test** ? Launch BeamNG and load level

## Technical Considerations

### BeamNG Folder Versions

The implementation handles:
- **New structure**: `BeamNG\BeamNG.drive\current\levels\`
- **Old structure**: `BeamNG.drive\{version}\levels\` (e.g., `0.32\levels\`)
- Automatically selects highest version if multiple exist

### Mod Info Removal

Before copying, `mod.info` is removed because:
- Wizard creates levels, not mods
- `mod.info` presence changes how BeamNG loads the level
- Prevents confusion about mod vs. user level

### Overwrite Strategy

Uses **delete-then-copy** instead of **merge**:
- Ensures clean slate (no leftover files from previous version)
- Prevents issues with renamed/deleted files
- Matches user expectation of "replace"

## Error Handling

### Scenarios Covered

1. **BeamNG folder not found**:
   ```
   Exception: "Could not determine BeamNG user folder path."
   ```

2. **Source directory not found**:
   ```
   Exception: "The source directory was not found."
   ```

3. **Access denied**:
   ```
   Exception: "Access to the path ... is denied."
   ```

4. **Disk full**:
   ```
   Exception: "There is not enough space on the disk."
   ```

All exceptions are caught and displayed via `ShowException()` with Snackbar error notification.

## Comparison with RenameMap

| Feature | CreateLevel | RenameMap |
|---------|-------------|-----------|
| **Source path** | `_unpacked` folder | `_unpacked` folder |
| **Level identifier** | `_targetLevelPath` | `_levelName` |
| **mod.info removal** | ? Yes | ? Yes |
| **Overwrite dialog** | ? Yes | ? Yes |
| **Delete-then-copy** | ? Yes | ? Yes |
| **Success message** | Display name | Level name |

## Files Modified

1. **`BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor`**
   - Added "Copy to BeamNG Levels Folder" button
   - Added `CopyToLevelsFolder()` method
   - Uses existing `CustomChangesChecker` class

## Build Status

? **Build Successful** - No compilation errors  
? **Feature Complete** - Copy to levels folder implemented  
? **Code Reuse** - Leverages existing `CustomChangesChecker`  
? **Error Handling** - Comprehensive exception handling  
? **User Feedback** - Clear notifications and confirmations  

## Future Enhancements

Potential improvements:
1. **Open in Explorer** button after copy
2. **Launch BeamNG** button to immediately test
3. **Copy progress bar** for large levels
4. **Selective file copy** (exclude certain folders)
5. **Multiple level selection** for batch copy

## Testing Recommendations

1. **Fresh level creation** ? Copy ? Verify in BeamNG
2. **Existing level** ? Overwrite ? Confirm replacement
3. **Cancel overwrite** ? Verify no changes
4. **Non-existent BeamNG folder** ? Verify error handling
5. **Read-only target** ? Verify permission error
6. **Different BeamNG versions** ? Test folder detection

---

**Feature**: Copy to BeamNG Levels Folder  
**Status**: ? COMPLETE  
**Impact**: Simplified level deployment workflow  
**Date**: December 2024
