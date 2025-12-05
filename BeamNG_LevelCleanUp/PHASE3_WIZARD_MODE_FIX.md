# Phase 3 Wizard Mode Fix - Path Resolution Issue

## Problem Description

When navigating to `CopyTerrains.razor` in wizard mode (`?wizardMode=true`), the page appeared empty and no terrain materials were displayed. The issue was related to incorrect path handling when auto-loading levels from the wizard state.

## Root Cause

The `LoadLevelsFromWizardState()` method was not properly handling the directory structure created by `CreateLevel.razor`. The paths stored in `WizardState` needed additional processing:

### Expected Directory Structure

```
WorkingDirectory/
??? _copyFrom/                          # Extracted source level
?   ??? levels/
?       ??? driver_training/            # Actual level directory
?           ??? info.json
?           ??? art/
?           ??? main/
??? _unpacked/                          # Created target level
    ??? levels/
        ??? my_custom_map/              # Actual level directory
            ??? info.json
            ??? art/
            ??? main/
```

### Wizard State Values

From `CreateLevel.razor`:
```csharp
// Source level (extracted from ZIP)
_wizardState.SourceLevelPath = _sourceLevelPath;  
// This is: WorkingDirectory/_copyFrom (after ZipFileHandler.ExtractToDirectory)

// Target level (newly created)
_wizardState.TargetLevelRootPath = targetRoot;  
// This is: WorkingDirectory/_unpacked/levels/targetLevelPath
```

### What BeamFileReader Expects

`BeamFileReader` constructor expects:
- `levelpath` - The path to the **levels** directory (not the individual level folder)
- `levelPathCopyFrom` - The path to the source **levels** directory

## The Fix

The corrected `LoadLevelsFromWizardState()` method now:

### 1. **Source Level Path Resolution**
```csharp
// WizardState.SourceLevelPath = WorkingDirectory/_copyFrom
var sourceLevelPath = WizardState.SourceLevelPath;

// Use GetLevelPath to find the "levels" directory
_levelPathCopyFrom = ZipFileHandler.GetLevelPath(sourceLevelPath);
// Result: WorkingDirectory/_copyFrom/levels
```

### 2. **Target Level Path Resolution**
```csharp
// WizardState.TargetLevelRootPath = WorkingDirectory/_unpacked/levels/targetLevelPath
var targetLevelRootPath = WizardState.TargetLevelRootPath;

// Get the parent directory (the "levels" folder)
_levelPath = Directory.GetParent(targetLevelRootPath)?.FullName;
// Result: WorkingDirectory/_unpacked/levels
```

### 3. **Working Directory Extraction**
```csharp
// Extract WorkingDirectory from the path
var unpackedIndex = _levelPath.IndexOf("_unpacked", StringComparison.OrdinalIgnoreCase);
if (unpackedIndex > 0)
{
    ZipFileHandler.WorkingDirectory = _levelPath.Substring(0, unpackedIndex - 1);
}
// Result: WorkingDirectory
```

### 4. **Detailed Logging**
Added comprehensive logging to help diagnose path issues:
```csharp
PubSubChannel.SendMessage(PubSubMessageType.Info, $"Source level path: {_levelPathCopyFrom}");
PubSubChannel.SendMessage(PubSubMessageType.Info, $"Target level path: {_levelPath}");
PubSubChannel.SendMessage(PubSubMessageType.Info, $"Working directory: {ZipFileHandler.WorkingDirectory}");
```

## How BeamFileReader Processes These Paths

When `BeamFileReader` is constructed with these paths:

```csharp
Reader = new BeamFileReader(_levelPath, null, _levelPathCopyFrom);
```

It calls `SanitizePath()` which:

1. Finds the actual level directories using `GetNamePath()`:
   ```csharp
   _levelNamePath = ZipFileHandler.GetNamePath(_levelPath);
   // Finds: WorkingDirectory/_unpacked/levels/my_custom_map
   
   _levelNamePathCopyFrom = ZipFileHandler.GetNamePath(_levelPathCopyFrom);
   // Finds: WorkingDirectory/_copyFrom/levels/driver_training
   ```

2. Sets up static path resolvers for the entire scanning process:
   ```csharp
   PathResolver.LevelPath = _levelPath;
   PathResolver.LevelNamePath = _levelNamePath;
   PathResolver.LevelPathCopyFrom = _levelPathCopyFrom;
   PathResolver.LevelNamePathCopyFrom = _levelNamePathCopyFrom;
   ```

## Terrain Scanning Process

With the correct paths, `ReadAllForCopy()` can now:

1. **Scan Source Terrain Materials**:
   ```csharp
   // In BeamFileReader.CopyTerrainMaterials()
   var dirInfo = new DirectoryInfo(_levelPathCopyFrom);
   // Walks: WorkingDirectory/_copyFrom/levels/driver_training/art/terrains/
   ```

2. **Scan Target Terrain Materials** (for replace dropdown):
   ```csharp
   // In TerrainCopyScanner.GetTargetTerrainMaterials()
   var terrainPath = Path.Join(namePath, "art", "terrains");
   // Searches: WorkingDirectory/_unpacked/levels/my_custom_map/art/terrains/
   ```

3. **Resolve Texture Paths**:
   ```csharp
   // In PathResolver.ResolvePath()
   var fi = new FileInfo(PathResolver.ResolvePath(_levelPathCopyFrom, propValue, false));
   // Resolves to: WorkingDirectory/_copyFrom/levels/driver_training/art/terrains/texture.png
   ```

## Testing the Fix

### Test Scenarios

1. **Basic Wizard Flow**:
   - Initialize new level in CreateLevel
   - Click "Select Terrain Materials" button
   - Verify terrain materials table appears
   - Verify all terrain materials from source are listed

2. **Path Verification**:
   - Check console/messages for log output
   - Verify paths are correctly resolved:
     ```
     Source level path: D:\...\BeamNgMT\_copyFrom\levels
     Target level path: D:\...\BeamNgMT\_unpacked\levels
     Working directory: D:\...\BeamNgMT
     ```

3. **Material Scanning**:
   - Verify material count is correct
   - Verify texture sizes are calculated
   - Verify base color and roughness are detected

4. **Copy Operation**:
   - Select materials and click "Copy Selected Materials"
   - Verify materials are copied to target
   - Verify navigation returns to CreateLevel
   - Verify wizard state is updated

## Key Takeaways

### Path Handling Rules

1. **Source Level**: Always use `ZipFileHandler.GetLevelPath()` to find the "levels" directory
2. **Target Level**: Use `Directory.GetParent()` when you have the full level path
3. **Working Directory**: Extract from known path components (e.g., "_unpacked" location)

### BeamFileReader Expectations

- Always pass the **levels** directory path, not the individual level folder
- Let `SanitizePath()` find the actual level directories using `GetNamePath()`
- Trust the internal path resolution system

### Wizard State Design

The wizard state stores:
- **SourceLevelPath**: Path to extracted source (may need `GetLevelPath()`)
- **TargetLevelRootPath**: Full path to target level folder (need to get parent)
- **Display Names**: For UI purposes only, not for path resolution

## Related Files Modified

- `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor` - Fixed `LoadLevelsFromWizardState()` method

## Related Files Referenced

- `BeamNG_LevelCleanUp/Logic/ZipFileHandler.cs` - Provides `GetLevelPath()` and `GetNamePath()`
- `BeamNG_LevelCleanUp/Logic/BeamFileReader.cs` - Processes paths in `SanitizePath()`
- `BeamNG_LevelCleanUp/Logic/PathResolver.cs` - Resolves asset file paths
- `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor` - Sets wizard state paths

## Build Status

? **Build Successful** - No compilation errors  
? **Path Resolution** - Correctly handles wizard state paths  
? **Backwards Compatible** - Standard mode unchanged  

---

**Issue Fixed**: Wizard mode now correctly loads terrain materials  
**Status**: ? RESOLVED  
**Date**: December 2024
