# API Documentation

This document provides detailed API documentation for the key classes and interfaces in the BeamNG Level CleanUp application.

## Core Logic APIs

### ZipFileHandler

Static class for handling ZIP file operations related to BeamNG maps.

```csharp
public static class ZipFileHandler
```

#### Methods

##### ExtractToDirectory
```csharp
public static string ExtractToDirectory(string filePath, string relativeTarget, bool isCopyFrom = false)
```
Extracts a ZIP file to the specified directory and returns the level path.

**Parameters:**
- `filePath` - Full path to the ZIP file
- `relativeTarget` - Relative directory name for extraction
- `isCopyFrom` - If true, tracks as a copy-from operation

**Returns:** Path to the main level directory

**Throws:** `Exception` if the file doesn't exist

##### GetLevelPath
```csharp
public static string GetLevelPath(string extractedPath)
```
Locates the main level directory within an extracted map structure.

**Parameters:**
- `extractedPath` - Root directory of extracted content

**Returns:** Path to the level directory containing info.json

##### CreateZipFromDirectory
```csharp
public static void CreateZipFromDirectory(string sourceDirectory, string zipFilePath)
```
Creates a ZIP file from a directory structure.

**Parameters:**
- `sourceDirectory` - Directory to compress
- `zipFilePath` - Output ZIP file path

### MaterialScanner

Analyzes BeamNG material files and tracks material usage.

```csharp
internal class MaterialScanner
```

#### Constructor
```csharp
public MaterialScanner(List<MaterialJson> materials, string levelPath, string namePath)
```

#### Methods

##### ScanMaterialsJsonFile
```csharp
internal void ScanMaterialsJsonFile()
```
Scans a materials JSON file and extracts material definitions.

**Side Effects:** Populates internal materials list

##### GetUsedMaterials
```csharp
internal List<MaterialJson> GetUsedMaterials()
```
Returns list of materials that are actually referenced by map geometry.

**Returns:** List of MaterialJson objects in use

### DaeScanner

Processes COLLADA (.dae) files to extract material dependencies.

```csharp
internal class DaeScanner
```

#### Methods

##### ScanDaeFile
```csharp
internal void ScanDaeFile(string daeFilePath)
```
Analyzes a DAE file and extracts material references.

**Parameters:**
- `daeFilePath` - Path to the COLLADA file

##### GetMaterialDependencies
```csharp
internal List<string> GetMaterialDependencies()
```
Returns list of material names referenced by the DAE file.

**Returns:** List of material names

### FileDeleter

Safely removes orphaned files from map directories.

```csharp
internal class FileDeleter
```

#### Methods

##### DeleteOrphanedFiles
```csharp
internal async Task<int> DeleteOrphanedFiles(List<string> filePaths, bool createBackup = true)
```
Deletes specified files with optional backup creation.

**Parameters:**
- `filePaths` - List of file paths to delete
- `createBackup` - Whether to create backup before deletion

**Returns:** Number of files successfully deleted

### LevelRenamer

Handles comprehensive map renaming operations.

```csharp
internal class LevelRenamer
```

#### Methods

##### RenameLevel
```csharp
internal async Task<bool> RenameLevel(string oldName, string newName, string displayName = null)
```
Renames a map in the filesystem and updates all references.

**Parameters:**
- `oldName` - Current map name
- `newName` - New map name for filesystem
- `displayName` - Optional display name for UI

**Returns:** True if rename was successful

## Data Models

### Asset

Represents a map asset file.

```csharp
public class Asset
{
    public string Name { get; set; }
    public string Path { get; set; }
    public AssetType Type { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsReferenced { get; set; }
}
```

### MaterialJson

Represents a BeamNG material definition.

```csharp
public class MaterialJson
{
    public string Name { get; set; }
    public string InternalName { get; set; }
    public string DiffuseMap { get; set; }
    public string NormalMap { get; set; }
    public string SpecularMap { get; set; }
    public List<MaterialStage> Stages { get; set; }
    public string MatJsonFileLocation { get; set; }
}
```

### Forest

Represents a forest item placement.

```csharp
public class Forest
{
    public string MeshName { get; set; }
    public float[] Position { get; set; } // [x, y, z]
    public float Scale { get; set; }
    public float[] Rotation { get; set; } // [x, y, z, w] quaternion
    public int TreeType { get; set; }
}
```

### MaterialStage

Represents a rendering stage in a BeamNG material.

```csharp
public class MaterialStage
{
    public string Type { get; set; }
    public Dictionary<string, object> Properties { get; set; }
    public List<string> Textures { get; set; }
}
```

## Communication API

### PubSubChannel

Static class for application-wide messaging.

```csharp
public static class PubSubChannel
```

#### Methods

##### SendMessage
```csharp
public static void SendMessage(PubSubMessageType type, string content)
```
Sends a message to all subscribers.

**Parameters:**
- `type` - Type of message (Info, Warning, Error, Progress)
- `content` - Message content

##### Subscribe
```csharp
public static void Subscribe(Action<PubSubMessage> callback)
```
Subscribes to all messages.

**Parameters:**
- `callback` - Function to call when messages are received

### PubSubMessageType

Enumeration of message types.

```csharp
public enum PubSubMessageType
{
    Info,
    Warning,
    Error,
    Progress,
    JobStarted,
    JobFinished
}
```

## Utility APIs

### JsonUtils

Utilities for working with JSON files.

```csharp
public static class JsonUtils
```

#### Methods

##### GetValidJsonDocumentFromFilePath
```csharp
public static JsonDocument GetValidJsonDocumentFromFilePath(string filePath)
```
Reads and parses a JSON file with error handling.

**Parameters:**
- `filePath` - Path to JSON file

**Returns:** JsonDocument or empty document if parsing fails

### PathResolver

Utilities for resolving file paths in BeamNG maps.

```csharp
public static class PathResolver
```

#### Methods

##### ResolveAssetPath
```csharp
public static string ResolveAssetPath(string relativePath, string basePath)
```
Resolves a relative asset path to an absolute path.

**Parameters:**
- `relativePath` - Relative path from material or DAE file
- `basePath` - Base directory path

**Returns:** Absolute file path

## Error Handling

All API methods follow these error handling patterns:

1. **Exceptions**: Critical errors that prevent operation throw exceptions
2. **Return Values**: Success/failure indicated by return values (bool, null checks)
3. **Logging**: All operations log to the PubSubChannel for UI feedback
4. **Validation**: Input parameters are validated and meaningful errors provided

## Thread Safety

- **UI Components**: Must be accessed from UI thread only
- **Logic Classes**: Generally not thread-safe, create new instances per operation
- **Static Classes**: Thread-safe for read operations, use locking for modifications
- **File Operations**: Use proper file locking and error handling

## Performance Notes

- **Large Files**: Operations on large maps may take several minutes
- **Memory Usage**: DAE file parsing can use significant memory for large models
- **Disk I/O**: All operations are I/O bound, performance depends on storage speed
- **Progress Reporting**: Use PubSubChannel for long-running operations