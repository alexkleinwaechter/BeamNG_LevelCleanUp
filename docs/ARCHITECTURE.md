# Architecture Overview

## High-Level Architecture

BeamNG Level CleanUp follows a layered architecture pattern with clear separation of concerns:

```
┌─────────────────────────────────────┐
│           Presentation Layer        │
│     (Blazor Components + WinForms)  │
├─────────────────────────────────────┤
│         Communication Layer         │
│         (PubSub Messaging)          │
├─────────────────────────────────────┤
│           Business Logic            │
│    (Scanners, Processors, Handlers) │
├─────────────────────────────────────┤
│            Data Layer               │
│      (Models, DTOs, Objects)        │
├─────────────────────────────────────┤
│           Infrastructure            │
│    (File I/O, JSON, ZIP, Utilities) │
└─────────────────────────────────────┘
```

## Component Interaction Flow

### 1. Application Startup
```
Program.cs → SplashScreen → Form1 → Blazor WebView → BlazorUI/App.razor
```

### 2. User Interaction Flow
```
User Action (UI) → Page Component → Logic Class → Data Processing → PubSub Message → UI Update
```

### 3. File Processing Flow
```
ZIP File → ZipFileHandler → Extract → Scanner Classes → Analysis → Results → UI Display
```

## Core Components

### Presentation Layer

#### Windows Forms Host
- **Form1.cs**: Main application window hosting Blazor WebView
- **SplashScreen.cs**: Initial loading screen
- **Program.cs**: Application entry point and initialization

#### Blazor UI Components
- **Pages/**: Main feature pages (MapShrink, RenameMap, etc.)
- **Components/**: Reusable UI components
- **Layout**: Navigation and page structure

### Communication Layer

#### PubSubChannel
Central messaging system for loose coupling between UI and business logic:
- **Publishers**: Logic classes send progress updates and results
- **Subscribers**: UI components listen for specific message types
- **Message Types**: Info, Warning, Error, Progress, JobStarted, JobFinished

```csharp
// Example usage
PubSubChannel.SendMessage(PubSubMessageType.Progress, "Scanning materials: 50%");
```

### Business Logic Layer

#### Core Processors
- **ZipFileHandler**: ZIP file extraction and creation
- **MaterialScanner**: BeamNG material analysis
- **DaeScanner**: COLLADA 3D model processing
- **ForestScanner**: Forest item analysis
- **FileDeleter**: Safe file removal with backup

#### Specialized Logic
- **LevelRenamer**: Map renaming operations
- **ObsoleteFileResolver**: Identifies unused assets
- **Asset Copiers**: Transfer assets between maps

### Data Layer

#### Core Models
- **Asset**: Represents map asset files
- **MaterialJson**: BeamNG material definitions
- **Forest**: Forest item placements
- **ManagedForestData**: Forest processing data

#### Communication Objects
- **PubSubMessage**: Messaging system data transfer
- **GridFileList**: UI data binding objects

### Infrastructure Layer

#### File Operations
- **JsonUtils**: JSON parsing with error handling
- **PathResolver**: File path resolution utilities
- **BeamLogReader**: BeamNG log file analysis

#### Data Processing
- **BeamJsonOptions**: JSON serialization configuration
- **Constants**: Application-wide constants

## Key Design Patterns

### 1. Publisher-Subscriber Pattern
Used for communication between UI and business logic layers to maintain loose coupling.

### 2. Strategy Pattern
Different scanner classes implement similar interfaces for processing various file types.

### 3. Factory Pattern
Object creation is centralized for consistency and maintainability.

### 4. Repository Pattern
File operations are abstracted through handler classes.

## Data Flow Diagrams

### Map Shrinking Process
```
User Selects ZIP → Extract Files → Scan Materials → Scan DAE Files → 
Scan Forest Items → Identify Used Assets → Mark Orphans → 
Display Results → User Confirms → Delete Files → Create New ZIP
```

### Map Renaming Process
```
User Enters Names → Validate Names → Extract Files → Update info.json → 
Update Level Files → Update References → Rename Directories → Create New ZIP
```

### Asset Copying Process
```
Load Source Map → Load Target Map → Scan Available Assets → User Selects Assets → 
Copy Files → Update Target References → Validate Integrity → Create New ZIP
```

## Technology Stack Integration

### .NET Framework Integration
- **Windows Forms**: Native Windows UI host
- **ASP.NET Core**: Blazor Server hosting
- **System.IO.Compression**: ZIP file handling
- **System.Text.Json**: JSON processing

### Third-Party Libraries
- **MudBlazor**: Material Design UI components
- **Blazor3D**: 3D visualization (if used)
- **AutoUpdater.NET**: Application update system
- **Pfim**: Image processing
- **SixLabors.ImageSharp**: Advanced image operations

## Security Considerations

### File System Access
- All operations work within user-specified directories
- No system file modifications
- Backup creation before destructive operations

### Input Validation
- ZIP file integrity checks
- JSON parsing with error handling
- Path traversal prevention

### Error Handling
- Comprehensive exception catching
- User-friendly error messages
- Graceful degradation

## Performance Architecture

### Asynchronous Operations
- Long-running tasks use async/await
- UI remains responsive during processing
- Progress reporting through PubSub

### Memory Management
- Large files processed in chunks
- Proper disposal of resources
- Garbage collection optimization

### I/O Optimization
- Minimal file system operations
- Efficient ZIP handling
- Cached parsing results

## Extensibility Points

### Adding New File Types
1. Create scanner class implementing standard pattern
2. Add data models for file format
3. Integrate with existing processing pipeline
4. Add UI components if needed

### Adding New Features
1. Create logic classes in appropriate namespace
2. Add UI pages/components
3. Wire up through PubSub messaging
4. Update navigation

### Customizing Processing
1. Extend existing scanner classes
2. Override processing methods
3. Maintain compatibility with existing data models

## Testing Architecture

### Unit Testing Strategy
- Logic classes are testable in isolation
- Mock file system operations
- Use dependency injection where needed

### Integration Testing
- Full workflow testing with sample data
- Performance testing with large files
- Error condition testing

### UI Testing
- Manual testing with real BeamNG maps
- Automated testing challenging due to file dependencies

This architecture provides a solid foundation for maintaining and extending the BeamNG Level CleanUp application while keeping concerns properly separated and components loosely coupled.