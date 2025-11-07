# AssetCopy Refactoring Summary

## Overview
The `AssetCopy.cs` file has been refactored from a single 500+ line file into multiple focused classes, improving maintainability, testability, and adherence to the Single Responsibility Principle.

## New Structure

### 1. **AssetCopy.cs** (Main Orchestrator)
- **Responsibility**: Coordinates the asset copying process
- **Size**: ~100 lines (reduced from ~500)
- **Key Methods**:
  - `Copy()`: Main entry point that iterates through assets and delegates to specialized copiers
  - `CopyRoad()`, `CopyDecal()`, `CopyDae()`, `CopyTerrain()`: Delegation methods

### 2. **PathConverter.cs** 
- **Responsibility**: Handles all path conversion and resolution logic
- **Key Methods**:
  - `GetTargetFileName()`: Converts source paths to target paths for general files
  - `GetTerrainTargetFileName()`: Handles terrain-specific path conversion
  - `GetBeamNgJsonFileName()`: Converts Windows paths to BeamNG JSON format

### 3. **FileCopyHandler.cs**
- **Responsibility**: Manages file copying with fallback to zip extraction
- **Key Methods**:
  - `CopyFile()`: Main copy method with error handling
  - `TryExtractFromZip()`: Attempts to extract files from BeamNG zip archives
  - `TryExtractImageWithExtensions()`: Tries multiple image extensions (.png, .dds, .jpg, .jpeg, .link)

### 4. **MaterialCopier.cs**
- **Responsibility**: Handles copying of general material files and their textures
- **Key Methods**:
  - `Copy()`: Orchestrates material copying for an asset
  - `CopyMaterial()`: Copies a single material with all its files
  - `CopyMaterialFilesAndUpdatePaths()`: Copies textures and updates JSON paths
  - `WriteMaterialJson()`: Writes or updates material JSON files

### 5. **ManagedDecalCopier.cs**
- **Responsibility**: Specializes in copying managed decal data
- **Key Methods**:
  - `Copy()`: Main entry point for decal copying
  - `CreateNewManagedDecalFile()`: Creates new managedDecalData.json
  - `UpdateExistingManagedDecalFile()`: Updates existing decal files

### 6. **DaeCopier.cs**
- **Responsibility**: Handles DAE (Collada) file copying with associated materials
- **Key Methods**:
  - `Copy()`: Copies DAE files and their materials
  - `CopyDaeFiles()`: Copies both .dae and .cdae files
- **Dependencies**: Uses `MaterialCopier` for material handling

### 7. **TerrainMaterialCopier.cs**
- **Responsibility**: Specialized terrain material copying with GUID generation and renaming
- **Key Methods**:
  - `Copy()`: Main terrain material copying orchestration
  - `CopyTerrainMaterial()`: Copies individual terrain material
  - `GenerateTerrainMaterialNames()`: Generates new names and GUIDs for terrain materials
  - `UpdateTerrainMaterialMetadata()`: Updates material metadata
  - `CopyTerrainTextures()`: Copies terrain textures
  - `UpdateTexturePathsInMaterial()`: Recursively updates texture paths in JSON
  - `WriteTerrainMaterialJson()`: Writes to main.materials.json

## Benefits

### 1. **Single Responsibility Principle**
Each class now has one clear responsibility, making code easier to understand and maintain.

### 2. **Better Testability**
Smaller, focused classes are much easier to unit test in isolation.

### 3. **Improved Readability**
- Reduced file size from 500+ lines to ~100 lines in the main orchestrator
- Clear separation of concerns
- Descriptive class names that indicate purpose

### 4. **Easier Maintenance**
- Changes to path handling only require editing `PathConverter`
- File copy logic is isolated in `FileCopyHandler`
- Material-specific logic is properly encapsulated

### 5. **Reusability**
Components like `PathConverter` and `FileCopyHandler` can be reused in other parts of the application.

### 6. **Dependency Injection Ready**
The classes are structured to easily support dependency injection if needed in the future.

## Migration Notes

- All existing functionality is preserved
- No breaking changes to the public API
- The `AssetCopy` constructor signatures remain unchanged
- Error handling behavior is maintained

## Future Improvements

Potential areas for further enhancement:
1. Extract interfaces for better testability
2. Add logging abstraction
3. Consider async/await for file operations
4. Add progress reporting for long-running operations
5. Implement retry logic for transient failures
