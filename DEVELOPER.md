# BeamNG Level CleanUp - Developer Documentation

## Table of Contents
1. [Project Overview](#project-overview)
2. [Architecture](#architecture)
3. [Development Environment Setup](#development-environment-setup)
4. [Project Structure](#project-structure)
5. [Key Components](#key-components)
6. [Core Logic Classes](#core-logic-classes)
7. [UI Components](#ui-components)
8. [Data Models](#data-models)
9. [Build and Deployment](#build-and-deployment)
10. [Contributing Guidelines](#contributing-guidelines)
11. [Troubleshooting](#troubleshooting)

## Project Overview

BeamNG Level CleanUp is a Windows desktop application designed for BeamNG Drive map builders. It provides essential tools for managing, optimizing, and working with BeamNG map files.

### Key Features

1. **Map Shrinker**: Analyzes BeamNG map files to identify and remove orphaned/unused assets, significantly reducing map file sizes
2. **Map Renamer**: Safely renames map files both in the filesystem and display names
3. **Asset Copier**: Selectively copies assets (decalroads, decals, DAE files) between different maps
4. **Forest Converter**: Converts static scene tree assets to forest items for better performance

### Technology Stack

- **.NET 7.0**: Core framework with Windows-specific features
- **Windows Forms**: Main application framework
- **Blazor Server**: Modern web UI hosted in WebView2
- **MudBlazor**: Material Design UI components
- **WebView2**: Microsoft Edge WebView2 control for hosting Blazor UI

## Architecture

The application follows a layered architecture pattern:

```
┌─────────────────────────────────────┐
│           Blazor UI Layer           │  (User Interface)
├─────────────────────────────────────┤
│      Communication Layer           │  (Pub/Sub Messaging)
├─────────────────────────────────────┤
│         Logic Layer                │  (Business Logic & File Processing)
├─────────────────────────────────────┤
│         Objects Layer              │  (Data Models & DTOs)
├─────────────────────────────────────┤
│         Utilities Layer            │  (Helper Functions & Extensions)
└─────────────────────────────────────┘
```

### Communication Pattern

The application uses a Publisher-Subscriber pattern for communication between UI and business logic layers:
- `PubSubChannel` handles message routing
- UI components subscribe to specific message types
- Logic classes publish progress updates and results

## Development Environment Setup

### Prerequisites

1. **Windows 10/11** (required for Windows Forms and WebView2)
2. **Visual Studio 2022** or **Visual Studio Code** with C# extension
3. **.NET 7.0 SDK** or higher
4. **Microsoft WebView2 Runtime** (usually pre-installed on Windows 11)

### Setup Steps

1. Clone the repository:
   ```bash
   git clone https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp.git
   cd BeamNG_LevelCleanUp
   ```

2. Restore NuGet packages:
   ```bash
   dotnet restore
   ```

3. Build the solution:
   ```bash
   dotnet build --configuration Debug
   ```

4. Run the application:
   ```bash
   dotnet run --project BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
   ```

### Development Tools

- **Visual Studio 2022**: Full IDE with excellent debugging support
- **Visual Studio Code**: Lightweight editor with C# and Blazor extensions
- **JetBrains Rider**: Alternative IDE with great .NET support

## Project Structure

```
BeamNG_LevelCleanUp/
├── BeamNG_LevelCleanUp/          # Main application project
│   ├── BlazorUI/                 # Blazor web UI components
│   │   ├── Components/           # Reusable UI components
│   │   ├── Pages/               # Main application pages
│   │   └── State/               # Application state management
│   ├── Communication/           # Pub/Sub messaging system
│   ├── Logic/                   # Core business logic
│   ├── LogicConvertForest/      # Forest conversion logic
│   ├── LogicCopyAssets/         # Asset copying logic
│   ├── Objects/                 # Data models and DTOs
│   ├── Utils/                   # Utility functions
│   └── wwwroot/                 # Web assets for Blazor UI
├── etsmaterialgen/              # ETS material conversion utility
└── BeamNG_LevelCleanUp.sln     # Visual Studio solution file
```

## Key Components

### Entry Point
- **Program.cs**: Application entry point with async initialization and splash screen
- **Form1.cs**: Main Windows Form hosting the Blazor WebView

### Core Features
- **Map Shrinker** (`Logic/` classes): Analyzes and removes unused map assets
- **Map Renamer** (`LevelRenamer.cs`): Handles safe map renaming operations
- **Asset Copier** (`LogicCopyAssets/`): Manages selective asset copying between maps
- **Forest Converter** (`LogicConvertForest/`): Converts static assets to forest items

## Core Logic Classes

### File Analysis Classes

#### `ZipFileHandler`
```csharp
public static class ZipFileHandler
```
- **Purpose**: Manages ZIP file extraction and level path detection
- **Key Methods**:
  - `ExtractToDirectory()`: Extracts ZIP files to working directory
  - `GetLevelPath()`: Locates the main level directory within extracted files
  - `CreateZipFromDirectory()`: Creates new ZIP files from directories

#### `MaterialScanner`
```csharp
internal class MaterialScanner
```
- **Purpose**: Scans and analyzes material JSON files in BeamNG maps
- **Key Methods**:
  - `ScanMaterialsJsonFile()`: Parses material definitions
  - `GetUsedMaterials()`: Returns list of materials actually used in the map

#### `DaeScanner`
```csharp
internal class DaeScanner
```
- **Purpose**: Analyzes COLLADA (.dae) 3D model files for material dependencies
- **Key Methods**:
  - `ScanDaeFile()`: Extracts material references from DAE files
  - `GetMaterialDependencies()`: Returns materials used by 3D models

#### `ForestScanner`
```csharp
internal class ForestScanner
```
- **Purpose**: Processes BeamNG forest item definitions
- **Key Methods**:
  - `ScanForestFiles()`: Analyzes forest.json files
  - `GetForestAssets()`: Returns assets used in forest definitions

### File Processing Classes

#### `FileDeleter`
```csharp
internal class FileDeleter
```
- **Purpose**: Safely removes orphaned files from map directories
- **Key Methods**:
  - `DeleteOrphanedFiles()`: Removes files not referenced by the map
  - `CreateBackup()`: Creates safety backups before deletion

#### `ObsoleteFileResolver`
```csharp
internal class ObsoleteFileResolver
```
- **Purpose**: Identifies unused/orphaned files in map directories
- **Key Methods**:
  - `FindObsoleteFiles()`: Returns list of files safe to delete
  - `AnalyzeDependencies()`: Builds dependency graph of map assets

### Specialized Logic

#### `LevelRenamer`
```csharp
internal class LevelRenamer
```
- **Purpose**: Handles comprehensive map renaming operations
- **Key Methods**:
  - `RenameLevel()`: Renames map files and updates references
  - `UpdateLevelInfo()`: Modifies info.json with new map details

## UI Components

### Main Pages

#### `MapShrink.razor`
- **Purpose**: Main interface for the map shrinking functionality
- **Features**: 
  - File selection dialog
  - Progress tracking
  - Results display with file deletion preview
  - Safety warnings and confirmations

#### `RenameMap.razor`
- **Purpose**: Interface for map renaming operations
- **Features**:
  - Original and new name inputs
  - Preview of changes
  - Validation of naming constraints

#### `CopyAssets.razor`
- **Purpose**: Asset copying interface between maps
- **Features**:
  - Source and destination map selection
  - Asset type filtering
  - Selective asset copying with preview

#### `ConvertToForest.razor`
- **Purpose**: UI for converting static assets to forest items
- **Features**:
  - Scene tree visualization
  - Asset selection with filtering
  - Conversion progress tracking

### Shared Components

#### `MyNavMenu.razor`
```razor
@* Navigation component for main application sections *@
```

#### `MainLayout.razor`
```razor
@* Main layout wrapper for all pages *@
```

## Data Models

### Core Objects

#### `Asset`
```csharp
public class Asset
{
    public string Name { get; set; }
    public string Path { get; set; }
    public AssetType Type { get; set; }
    public long Size { get; set; }
}
```

#### `MaterialJson`
```csharp
public class MaterialJson
{
    public string Name { get; set; }
    public string DiffuseMap { get; set; }
    public string NormalMap { get; set; }
    public List<MaterialStage> Stages { get; set; }
}
```

#### `Forest`
```csharp
public class Forest
{
    public string MeshName { get; set; }
    public float[] Position { get; set; }
    public float Scale { get; set; }
    public float[] Rotation { get; set; }
}
```

### Communication Objects

#### `PubSubMessage`
```csharp
public class PubSubMessage
{
    public PubSubMessageType Type { get; set; }
    public string Content { get; set; }
    public DateTime Timestamp { get; set; }
}
```

## Build and Deployment

### Debug Build
```bash
dotnet build --configuration Debug
```

### Release Build
```bash
dotnet build --configuration Release
```

### Publishing Self-Contained
```bash
dotnet publish --configuration Release --self-contained true --runtime win-x64
```

### Creating Installer
The project uses a custom installer or ZIP distribution. The release process involves:
1. Building in Release configuration
2. Gathering all dependencies
3. Creating distribution package
4. Updating auto-updater XML configuration

### Auto-Update System
The application includes an auto-update mechanism:
- **AutoUpdater.xml**: Contains version and download information
- **AutoUpdater.NET**: Handles update checking and installation
- Updates are checked on application startup

## Contributing Guidelines

### Code Style
- Follow standard C# naming conventions (PascalCase for public members, camelCase for private fields)
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and under 50 lines when possible

### Pull Request Process
1. Fork the repository
2. Create a feature branch (`feature/your-feature-name`)
3. Make your changes with appropriate tests
4. Update documentation if needed
5. Submit a pull request with clear description

### Testing
- Test with various BeamNG map formats
- Verify functionality on different Windows versions
- Test with large map files to ensure performance
- Always test with backup copies of maps

### Documentation Updates
- Update this developer documentation for architectural changes
- Add inline code comments for complex logic
- Update README.md for user-facing changes
- Document any new dependencies or requirements

## Troubleshooting

### Common Issues

#### "WebView2 Runtime not found"
**Solution**: Install Microsoft WebView2 Runtime from Microsoft's website

#### "Cannot locate the file" errors
**Cause**: Missing dependencies or incorrect file paths
**Solution**: Ensure all NuGet packages are restored and file paths are correct

#### Application crashes on startup
**Causes**: 
- Missing .NET runtime
- Corrupted configuration files
- Insufficient permissions

**Solutions**:
- Install .NET 7.0 runtime
- Reset application settings
- Run as administrator

#### Map processing failures
**Causes**:
- Corrupted ZIP files
- Unsupported map formats
- Insufficient disk space

**Solutions**:
- Verify ZIP file integrity
- Check supported formats in documentation
- Ensure adequate free disk space

### Debug Tips

1. **Enable Debug Mode**: Use `#DEBUG` conditional compilation for detailed logging
2. **Blazor DevTools**: Enable Blazor developer tools in debug builds
3. **File System Monitoring**: Use Process Monitor to track file system operations
4. **Memory Profiling**: Use dotMemory or PerfView for memory leak detection

### Performance Considerations

- **Large Maps**: Processing time scales with map complexity and size
- **Memory Usage**: Monitor memory usage with large asset collections
- **Disk I/O**: Operations are disk-intensive; SSD recommended for development
- **Threading**: UI operations are single-threaded; long operations use background tasks

---

## Getting Help

- **Issues**: Report bugs and feature requests on GitHub Issues
- **Discussions**: Use GitHub Discussions for questions and community support
- **Documentation**: Check the main README.md for user documentation
- **Code Examples**: Refer to existing logic classes for implementation patterns

This documentation is maintained alongside the codebase. Please keep it updated when making significant changes to the application architecture or adding new features.