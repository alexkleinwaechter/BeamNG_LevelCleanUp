# Logfile Implementation Plan

## Overview

This document describes the implementation plan for adding consistent logfile support across all major feature pages in the BeamNG Mapping Tools application. The goal is to ensure users can easily find and review operation logs when they open the working directory.

## Current State Analysis

### MapShrink.razor - Reference Implementation ?

MapShrink currently writes logfiles after completing operations using the `BeamFileReader.WriteLogFile()` method:

```csharp
// After analysis is complete:
Reader.WriteLogFile(_warnings, "Log_Shrinker_Warnings");
Reader.WriteLogFile(_errors, "Log_Shrinker_Errors");
```

**Logfiles produced:**
- `Log_Shrinker_Warnings.txt` - Contains warning messages from the shrink operation
- `Log_Shrinker_Errors.txt` - Contains error messages from the shrink operation
- Additional files: `DeletedAssetFiles.txt`, `MissingFilesFromBeamNgLog.txt`, `DuplicateMaterials.txt`

**Location:** Files are written to `_levelPath` (the extracted level folder)

### GenerateTerrain.razor - Already Implemented ?

GenerateTerrain already has comprehensive logging via `TerrainGenerationOrchestrator.WriteGenerationLogs()`:

```csharp
// In TerrainGenerationOrchestrator.cs:
public void WriteGenerationLogs(TerrainGenerationState state)
{
    if (state.Messages.Any())
    {
        var messagesPath = Path.Combine(state.WorkingDirectory, "Log_TerrainGen.txt");
        File.WriteAllLines(messagesPath, state.Messages);
    }

    if (state.Warnings.Any())
    {
        var warningsPath = Path.Combine(state.WorkingDirectory, "Log_TerrainGen_Warnings.txt");
        File.WriteAllLines(warningsPath, state.Warnings);
    }

    if (state.Errors.Any())
    {
        var errorsPath = Path.Combine(state.WorkingDirectory, "Log_TerrainGen_Errors.txt");
        File.WriteAllLines(errorsPath, state.Errors);
    }
}
```

**Logfiles produced:**
- `Log_TerrainGen.txt` - Main generation log with all info messages
- `Log_TerrainGen_Warnings.txt` - Warnings during generation
- `Log_TerrainGen_Errors.txt` - Errors during generation
- Plus detailed logs in `MT_TerrainGeneration/` subfolder from `TerrainCreationLogger`

**Location:** Files are written to `state.WorkingDirectory` (level folder)

### CopyAssets.razor - Partial Implementation ??

CopyAssets writes logfiles after copy operation completes (in standalone mode):

```csharp
// After copy completes in CopyDialog():
Reader.WriteLogFile(_warnings, "Log_AssetCopy_Warnings");
Reader.WriteLogFile(_errors, "Log_AssetCopy_Errors");
```

**Issues:**
1. ? No info/messages log written
2. ? Wizard mode (`CopyDialogWizardMode()`) does NOT write logfiles
3. ? No success summary log

### CopyForestBrushes.razor - Partial Implementation ??

CopyForestBrushes writes logfiles after copy operation:

```csharp
// After copy completes in CopyDialog():
Reader.WriteLogFile(_warnings, "Log_ForestBrushCopy_Warnings");
Reader.WriteLogFile(_errors, "Log_ForestBrushCopy_Errors");
```

**Issues:**
1. ? No info/messages log written
2. ? Wizard mode (`CopyDialogWizardMode()`) does NOT write logfiles
3. ? No success summary log

### CreateLevel.razor - No Logfiles ?

CreateLevel currently does NOT write any logfiles.

**Required logs:**
- Level creation summary
- MissionGroup copy log
- Errors and warnings

### CopyTerrains.razor - Updated ?

CopyTerrains now uses `WriteOperationLogs()` for consistent logging in both standalone and wizard modes:

```csharp
// After copy completes in CopyDialog() and CopyDialogWizardMode():
Reader.WriteOperationLogs(_messages, _warnings, _errors, "TerrainCopy");
```

**Logfiles produced:**
- `Log_TerrainCopy.txt` - Main operation log with info messages
- `Log_TerrainCopy_Warnings.txt` - Warning messages
- `Log_TerrainCopy_Errors.txt` - Error messages

**Location:** Files are written to `_levelPath` (the extracted level folder)

### ConvertToForest.razor - Partial Implementation ??

ConvertToForest writes logfiles:

```csharp
Reader.WriteLogFile(_warnings, "Log_ForestConvert_Warnings");
Reader.WriteLogFile(_errors, "Log_ForestConvert_Errors");
```

---

## Implementation Plan

### Phase 1: Define Consistent Naming Convention ? COMPLETED

All logfiles should follow this naming pattern:
```
Log_{FeatureName}.txt           - Main operation log (info messages)
Log_{FeatureName}_Warnings.txt  - Warning messages
Log_{FeatureName}_Errors.txt    - Error messages
```

**Feature Name Mapping (Verified):**
| Page | Feature Name | Prefix | Current Status |
|------|--------------|--------|----------------|
| MapShrink.razor | Shrinker | `Log_Shrinker` | ? Warnings + Errors (needs Info) |
| GenerateTerrain.razor | TerrainGen | `Log_TerrainGen` | ? Complete (Info + Warnings + Errors) |
| CreateLevel.razor | CreateLevel | `Log_CreateLevel` | ? Not implemented |
| CopyAssets.razor | AssetCopy | `Log_AssetCopy` | ?? Warnings + Errors only |
| CopyForestBrushes.razor | ForestBrushCopy | `Log_ForestBrushCopy` | ?? Warnings + Errors only |
| CopyTerrains.razor | TerrainCopy | `Log_TerrainCopy` | ? Complete (Info + Warnings + Errors) |
| ConvertToForest.razor | ForestConvert | `Log_ForestConvert` | ?? Warnings + Errors only |

**Verified Existing Implementations:**

1. **MapShrink.razor** (Line ~115):
   ```csharp
   Reader.WriteLogFile(_warnings, "Log_Shrinker_Warnings");
   Reader.WriteLogFile(_errors, "Log_Shrinker_Errors");
   ```

2. **GenerateTerrain.razor** (via `TerrainGenerationOrchestrator.WriteGenerationLogs()`):
   - `Log_TerrainGen.txt` - Complete with header
   - `Log_TerrainGen_Warnings.txt`
   - `Log_TerrainGen_Errors.txt`
   - Plus detailed debug logs in `MT_TerrainGeneration/` subfolder

3. **CopyAssets.razor.cs** (Line ~630):
   ```csharp
   Reader.WriteLogFile(_warnings, "Log_AssetCopy_Warnings");
   Reader.WriteLogFile(_errors, "Log_AssetCopy_Errors");
   ```

4. **CopyForestBrushes.razor.cs** (Line ~620):
   ```csharp
   Reader.WriteLogFile(_warnings, "Log_ForestBrushCopy_Warnings");
   Reader.WriteLogFile(_errors, "Log_ForestBrushCopy_Errors");
   ```

5. **CopyTerrains.razor.cs**:
```csharp
Reader.WriteOperationLogs(_messages, _warnings, _errors, "TerrainCopy");
```
- Standalone mode: ? Logs written after copy operation
- Wizard mode: ? Logs written after copy operation

**Naming Convention Rules:**
1. All log filenames start with `Log_`
2. Feature name uses PascalCase without spaces
3. Suffixes are: (none), `_Warnings`, `_Errors`
4. File extension is always `.txt`
5. Files are written to the level path (working directory)

### Phase 2: Add Helper Method to BeamFileReader

Add a new overload to `BeamFileReader` that writes an info log in addition to warnings/errors:

```csharp
/// <summary>
/// Writes operation logs to files in the level path.
/// </summary>
/// <param name="messages">Info messages list</param>
/// <param name="warnings">Warning messages list</param>
/// <param name="errors">Error messages list</param>
/// <param name="featureName">Feature name for log file prefix (e.g., "AssetCopy")</param>
internal void WriteOperationLogs(List<string> messages, List<string> warnings, List<string> errors, string featureName)
{
    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    
    if (messages.Count > 0)
    {
        var messagesWithHeader = new List<string> { $"# {featureName} Log - {timestamp}", "" };
        messagesWithHeader.AddRange(messages);
        WriteLogFile(messagesWithHeader, $"Log_{featureName}");
    }
    
    if (warnings.Count > 0)
        WriteLogFile(warnings, $"Log_{featureName}_Warnings");
    
    if (errors.Count > 0)
        WriteLogFile(errors, $"Log_{featureName}_Errors");
}
```

### Phase 3: Update CopyAssets.razor.cs

**Location:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyAssets.razor.cs`

**Changes:**

1. Update `CopyDialog()` method (standalone mode):
```csharp
private async Task CopyDialog()
{
    // ... existing code ...
    
    if (!result.Canceled)
    {
        // ... existing copy logic ...
        
        // Write all logs (not just warnings/errors)
        Reader.WriteOperationLogs(_messages, _warnings, _errors, "AssetCopy");
        
        // ... rest of method ...
    }
}
```

2. Update `CopyDialogWizardMode()` method to also write logs:
```csharp
private async Task CopyDialogWizardMode()
{
    // ... existing code ...
    
    if (!result.Canceled)
    {
        // ... existing copy logic ...
        
        // Write logs even in wizard mode
        Reader.WriteOperationLogs(_messages, _warnings, _errors, "AssetCopy");
        
        // ... rest of method ...
    }
}
```

### Phase 4: Update CopyForestBrushes.razor.cs

**Location:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyForestBrushes.razor.cs`

**Changes:**

1. Update `CopyDialog()` method:
```csharp
private async Task CopyDialog()
{
    // ... existing code ...
    
    if (!result.Canceled)
    {
        // ... existing copy logic ...
        
        // Write all logs (replace existing two WriteLogFile calls)
        Reader.WriteOperationLogs(_messages, _warnings, _errors, "ForestBrushCopy");
        
        // ... rest of method ...
    }
}
```

2. Update `CopyDialogWizardMode()` method:
```csharp
private async Task CopyDialogWizardMode()
{
    // ... existing code ...
    
    if (!result.Canceled)
    {
        // ... existing copy logic ...
        
        // Write logs even in wizard mode
        Reader.WriteOperationLogs(_messages, _warnings, _errors, "ForestBrushCopy");
        
        // ... rest of method ...
    }
}
```

### Phase 5: Update CreateLevel.razor.cs

**Location:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CreateLevel.razor.cs`

**Changes:**

Add log writing at the end of `InitializeNewLevel()` method:

```csharp
protected async Task InitializeNewLevel()
{
    // ... existing code ...
    
    try
    {
        // ... existing initialization logic ...
        
        await Task.Run(() =>
        {
            // ... existing code ...
        });
        
        // Write operation logs
        WriteCreateLevelLogs();
        
        Snackbar.Remove(_staticSnackbar);
        Snackbar.Add("Level initialization complete!", Severity.Success);
        
        // ... rest of method ...
    }
    // ... catch/finally ...
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
                $"# Target: {_targetLevelName} ({_targetLevelPath})",
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
```

### Phase 6: Update CopyTerrains.razor.cs ? COMPLETED

**Location:** `BeamNG_LevelCleanUp\BlazorUI\Pages\CopyTerrains.razor.cs`

**Changes Made:**

1. Updated `CopyDialog()` method (standalone mode) to use `WriteOperationLogs()`:
```csharp
// Write operation logs (info, warnings, errors)
Reader.WriteOperationLogs(_messages, _warnings, _errors, "TerrainCopy");
```

2. Updated `CopyDialogWizardMode()` method to also write logs:
```csharp
// Write operation logs (info, warnings, errors)
Reader.WriteOperationLogs(_messages, _warnings, _errors, "TerrainCopy");
```

**Benefits:**
- Both standalone and wizard modes now write consistent logs
- Info messages are captured in addition to warnings/errors
- Log file has timestamp header for traceability

### Phase 7: Update ConvertToForest.razor

**Location:** `BeamNG_LevelCleanUp\BlazorUI\Pages\ConvertToForest.razor`

**Changes:**

Update `ConvertDialog()` to include info messages:
```csharp
// Replace existing:
Reader.WriteLogFile(_warnings, "Log_ForestConvert_Warnings");
Reader.WriteLogFile(_errors, "Log_ForestConvert_Errors");

// With:
Reader.WriteOperationLogs(_messages, _warnings, _errors, "ForestConvert");
```

---

## Summary of Files to Modify

1. **BeamNG_LevelCleanUp\Logic\BeamFileReader.cs**
   - Add new `WriteOperationLogs()` method

2. **BeamNG_LevelCleanUp\BlazorUI\Pages\CopyAssets.razor.cs**
   - Update `CopyDialog()` to use `WriteOperationLogs()`
   - Update `CopyDialogWizardMode()` to write logs

3. **BeamNG_LevelCleanUp\BlazorUI\Pages\CopyForestBrushes.razor.cs**
   - Update `CopyDialog()` to use `WriteOperationLogs()`
   - Update `CopyDialogWizardMode()` to write logs

4. **BeamNG_LevelCleanUp\BlazorUI\Pages\CreateLevel.razor.cs**
   - Add `WriteCreateLevelLogs()` method
   - Call it from `InitializeNewLevel()`

5. **BeamNG_LevelCleanUp\BlazorUI\Pages\ConvertToForest.razor**
   - Update `ConvertDialog()` to use `WriteOperationLogs()`

6. **BeamNG_LevelCleanUp\BlazorUI\Pages\CopyTerrains.razor.cs** (if needed)
   - Add logging if not already present

---

## Expected Logfile Output

After implementation, users opening the working folder should see logfiles like:

```
levels/
??? my_new_level/
    ??? Log_CreateLevel.txt            # Level creation summary
    ??? Log_TerrainCopy.txt            # Terrain material copy log
    ??? Log_TerrainCopy_Warnings.txt   # Warnings (if any)
    ??? Log_ForestBrushCopy.txt        # Forest brush copy log
    ??? Log_AssetCopy.txt              # Asset copy log
    ??? Log_TerrainGen.txt             # Terrain generation log
    ??? Log_TerrainGen_Warnings.txt    # Generation warnings
    ??? MT_TerrainGeneration/          # Detailed terrain gen debug folder
        ??? Log_TerrainGen_*.txt       # Detailed performance logs
        ??? ...debug images...
```

---

## Testing Checklist

- [ ] MapShrink.razor - Verify existing logging still works
- [ ] GenerateTerrain.razor - Verify existing logging still works  
- [ ] CreateLevel.razor - Test new logging (standalone + wizard)
- [ ] CopyAssets.razor - Test logging in standalone and wizard modes
- [ ] CopyForestBrushes.razor - Test logging in standalone and wizard modes
- [ ] CopyTerrains.razor - Test logging
- [ ] ConvertToForest.razor - Test updated logging
- [ ] Verify all logfiles appear in working directory with correct naming
- [ ] Verify "Open Working Directory" and "Logfiles" buttons work correctly

---

## Implementation Progress

### Phase 1: Define Consistent Naming Convention ? COMPLETED
- [x] Document naming convention pattern
- [x] Verify existing implementations
- [x] Create feature name mapping table
- [x] Document current status of each page

### Phase 6: Update CopyTerrains.razor.cs ? COMPLETED
- [x] Updated `CopyDialog()` to use `WriteOperationLogs()`
- [x] Updated `CopyDialogWizardMode()` to write logs

---

## Notes

1. **Wizard Mode Consideration**: In wizard mode, multiple operations run on the same target level. Each operation should append to or create its own logfile, not overwrite others.

2. **Log Location**: ? **FIXED** - All log files now write to `_levelNamePath` (the actual level folder like `levels/_logtest`) instead of `_levelPath` (the parent `levels` folder). This ensures all logs appear in the target level's folder regardless of whether operating in standalone or wizard mode.

3. **Performance**: `WriteOperationLogs()` should be called AFTER the main operation completes, not during, to avoid I/O delays during processing.

4. **Error Handling**: Log writing should never throw exceptions that interrupt the main operation flow - wrap in try/catch and send warning via PubSub if writing fails.

---

## Bug Fix: Log Path Inconsistency (2026-01-13)

### Issue
Log files from wizard mode operations (CopyAssets, CopyForestBrushes, CopyTerrains) were being written to the parent `levels` folder instead of the target level folder (e.g., `levels/_logtest`).

**Root Cause**: `BeamFileReader.WriteLogFile()` and `WriteOperationLogs()` methods were using `_levelPath` (which points to `levels`) instead of `_levelNamePath` (which points to the actual level folder like `levels/_logtest`).

### Fix Applied
Changed all log file writing to use `_levelNamePath` instead of `_levelPath`:

1. **`WriteLogFile()`** - Now writes to `_levelNamePath`
2. **`WriteOperationLogs()`** - Uses `WriteLogFile()`, so automatically fixed
3. **`GetDuplicateMaterialsLogFilePath()`** - Now writes to `_levelNamePath`
4. **`GetMissingFilesFromBeamLog()`** - Now writes to `_levelNamePath`

### Verification
All log files now appear in the correct location (`levels/_logtest/`) for both standalone and wizard modes.

