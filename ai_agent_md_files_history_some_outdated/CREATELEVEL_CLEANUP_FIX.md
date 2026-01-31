# CreateLevel.razor Cleanup and File Handling Fix

## Issues
The `CreateLevel.razor` page had two issues:

1. **Missing Cleanup**: Not cleaning up temporary extracted folders (`_unpacked` and `_copyFrom`) before extracting new source levels, which could lead to:
   - Disk space issues from accumulated temporary files
   - Potential conflicts with old extracted data
   - Inconsistent state between different wizard runs

2. **File Selection from Different Folders**: When selecting a file from a different folder than the working directory, the `ExtractToDirectory` method failed because it tried to extract from the wrong directory.

## Solution
Added cleanup logic and file copy handling following the same pattern used in other pages (`CopyAssets.razor`, `MapShrink.razor`, `RenameMap.razor`).

## Changes Made

### 1. Added Cleanup to `OnSourceMapSelected()`
Added call to `ZipFileHandler.CleanUpWorkingDirectory()` before extracting a new source level:

```csharp
protected async Task OnSourceMapSelected(string file)
{
    SetDefaultWorkingDirectory();
    
    // Clean up any existing extracted files
    ZipFileHandler.CleanUpWorkingDirectory();
    
    // ... extraction logic
}
```

### 2. Added File Copy Logic for Different Folders
When a file is selected from a different folder than the working directory, it's now copied to the working directory first before extraction:

```csharp
// If file is in a different folder than working directory, copy it first
if (ZipFileHandler.WorkingDirectory != Path.GetDirectoryName(file))
{
    PubSubChannel.SendMessage(PubSubMessageType.Info, $"Copy source level to {ZipFileHandler.WorkingDirectory} ...");
    try
    {
        var target = Path.Join(ZipFileHandler.WorkingDirectory, Path.GetFileName(file));
        File.Copy(file, target, true);
        PubSubChannel.SendMessage(PubSubMessageType.Info, "Copy source level finished");
    }
    catch (Exception ex)
    {
        PubSubChannel.SendMessage(PubSubMessageType.Error, $"Error copying source level to working directory: {ex.Message}");
        ShowException(ex);
        return;
    }
}
```

This ensures that `ExtractToDirectory` always works with files in the working directory.

### 3. Added Cleanup to `OnVanillaSourceSelected()`
Also added cleanup when selecting a vanilla level from the dropdown:

```csharp
protected async Task OnVanillaSourceSelected(FileInfo file)
{
    if (file == null)
    {
        _vanillaLevelSourceSelected = null;
        return;
    }

    SetDefaultWorkingDirectory();
    
    // Clean up any existing extracted files
    ZipFileHandler.CleanUpWorkingDirectory();
    
    _vanillaLevelSourceSelected = file;
    // ... copy and extract logic
}
```

## How It Works

### The File Copy Pattern
1. **Check Working Directory**: Compare the file's directory with `ZipFileHandler.WorkingDirectory`
2. **Copy if Different**: If they don't match, copy the file to the working directory
3. **Extract**: Always extract from the working directory using `Path.GetFileName(file)`

### The Cleanup Method
The `ZipFileHandler.CleanUpWorkingDirectory()` method:
1. Deletes the `_unpacked` directory (target level extraction)
2. Deletes the `_copyFrom` directory (source level extraction)
3. Maintains references to the last extracted paths for logging purposes

This ensures that:
- Each new level selection starts with a clean slate
- No old data interferes with new extractions
- Disk space is properly managed
- The working directory stays clean
- Files from any location can be selected and extracted successfully

## Benefits

1. **Consistent Behavior**: CreateLevel now behaves like all other pages in the application
2. **Prevents File Conflicts**: Old extracted files don't interfere with new wizard runs
3. **Disk Space Management**: Temporary files are cleaned up automatically
4. **Better User Experience**: Fresh start for each level creation attempt
5. **Cross-Directory Support**: Users can select source levels from any folder (vanilla levels, custom folders, etc.)
6. **Robust Error Handling**: Provides clear error messages if file copy fails

## Testing Checklist

- [x] Build successful
- [ ] Test selecting a source level from the working directory
- [ ] Test selecting a source level from a different directory (e.g., vanilla levels)
- [ ] Test selecting a source level multiple times
- [ ] Test switching between different source levels
- [ ] Test selecting vanilla levels from dropdown
- [ ] Verify temporary folders are deleted between selections
- [ ] Verify file copying works for files in different locations
- [ ] Verify wizard still works end-to-end
- [ ] Check working directory stays clean after multiple operations

## Related Files

- `BeamNG_LevelCleanUp/BlazorUI/Pages/CreateLevel.razor` (modified)
- `BeamNG_LevelCleanUp/Logic/ZipFileHandler.cs` (uses existing methods)
- `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyAssets.razor` (reference pattern)
- `BeamNG_LevelCleanUp/BlazorUI/Pages/MapShrink.razor` (reference pattern)
- `BeamNG_LevelCleanUp/BlazorUI/Pages/RenameMap.razor` (reference pattern)
- `BeamNG_LevelCleanUp/BlazorUI/Pages/CopyTerrains.razor` (reference pattern)

## Build Status
? Build successful - no compilation errors
