# Development Setup Guide

This guide walks you through setting up a complete development environment for BeamNG Level CleanUp.

## System Requirements

### Operating System
- **Windows 10** (version 1903 or later) or **Windows 11**
- **64-bit architecture** required

### Development Tools

#### Required
- **.NET 7.0 SDK** or higher
  - Download: https://dotnet.microsoft.com/download/dotnet/7.0
  - Verify: `dotnet --version` should show 7.0.x or higher

- **Microsoft WebView2 Runtime**
  - Usually pre-installed on Windows 11
  - Download: https://developer.microsoft.com/en-us/microsoft-edge/webview2/
  - Required for Blazor WebView functionality

#### Recommended IDEs
- **Visual Studio 2022** (Community/Professional/Enterprise)
  - Workloads: ".NET desktop development"
  - Extensions: "Blazor WASM Debugging", "GitHub Extension"
  
- **Visual Studio Code** (Alternative)
  - Extensions: "C#", "Blazor", "GitLens"

- **JetBrains Rider** (Alternative)
  - Built-in support for .NET and Blazor

### Additional Tools

#### Version Control
- **Git for Windows**
  - Download: https://git-scm.com/download/win
  - Configure with your GitHub credentials

#### Optional but Helpful
- **Windows Terminal** - Modern terminal experience
- **PowerToys** - Windows utilities for developers
- **Postman** - API testing (if extending web features)

## Step-by-Step Setup

### 1. Install Prerequisites

#### Install .NET 7.0 SDK
```powershell
# Download and run the installer from Microsoft
# Or use winget (if available)
winget install Microsoft.DotNet.SDK.7
```

#### Verify Installation
```powershell
dotnet --version
# Should output: 7.0.xxx
```

#### Install Visual Studio 2022
1. Download from https://visualstudio.microsoft.com/
2. Run installer
3. Select ".NET desktop development" workload
4. Include these components:
   - .NET 7.0 Runtime
   - .NET Framework 4.8 targeting pack
   - Git for Windows (if not already installed)

### 2. Clone the Repository

#### Using Git Command Line
```bash
# Clone your fork
git clone https://github.com/YOUR_USERNAME/BeamNG_LevelCleanUp.git
cd BeamNG_LevelCleanUp

# Add upstream remote
git remote add upstream https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp.git

# Verify remotes
git remote -v
```

#### Using Visual Studio
1. Open Visual Studio 2022
2. Choose "Clone a repository"
3. Enter your fork URL
4. Choose local path
5. Click "Clone"

### 3. Build the Project

#### Command Line Build
```bash
# Navigate to project directory
cd BeamNG_LevelCleanUp

# Restore NuGet packages
dotnet restore

# Build in Debug mode
dotnet build --configuration Debug

# Build in Release mode
dotnet build --configuration Release
```

#### Visual Studio Build
1. Open `BeamNG_LevelCleanUp.sln`
2. Right-click solution in Solution Explorer
3. Select "Restore NuGet Packages"
4. Press F6 to build or Build → Build Solution

### 4. Run the Application

#### From Command Line
```bash
# Run main application
dotnet run --project BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj

# Run ETS material generator
dotnet run --project etsmaterialgen/etsmaterialgen.csproj
```

#### From Visual Studio
1. Set `BeamNG_LevelCleanUp` as startup project
2. Press F5 (Debug) or Ctrl+F5 (Run without debugging)

### 5. Development Environment Configuration

#### Visual Studio Settings

##### Recommended Extensions
- **GitHub Extension for Visual Studio**
- **Blazor WASM Debugging**
- **CodeMaid** (code cleanup)
- **Roslynator** (code analysis)

##### Editor Settings
```json
// .editorconfig (already in project)
root = true

[*.cs]
indent_style = space
indent_size = 4
end_of_line = crlf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true
```

#### Visual Studio Code Setup

##### Required Extensions
```bash
# Install extensions
code --install-extension ms-dotnettools.csharp
code --install-extension ms-dotnettools.blazorwasm-companion
code --install-extension eamodio.gitlens
```

##### Settings (settings.json)
```json
{
    "dotnet.completion.showCompletionItemsFromUnimportedNamespaces": true,
    "omnisharp.enableEditorConfigSupport": true,
    "files.exclude": {
        "**/bin": true,
        "**/obj": true
    }
}
```

### 6. Debug Configuration

#### Visual Studio Debugging
1. Set breakpoints in C# code
2. Press F5 to start debugging
3. Use Debug → Windows for various debug views

#### Blazor Debugging
1. Enable "Enable Blazor WebAssembly debugging" in project properties
2. Set breakpoints in .razor files
3. Use browser developer tools for JavaScript debugging

#### Common Debug Scenarios
```csharp
// Debug material scanning
if (material.Name == "problematic_material")
{
    System.Diagnostics.Debugger.Break(); // Breaks here in debugger
}

// Debug file operations
Console.WriteLine($"Processing file: {filePath}");
```

## Development Workflow

### 1. Daily Development Routine

```bash
# Start of day - sync with upstream
git checkout master
git pull upstream master

# Create feature branch
git checkout -b feature/your-feature

# Make changes, test, commit
git add .
git commit -m "Implement feature: description"

# Push to your fork
git push origin feature/your-feature

# Create PR when ready
```

### 2. Testing Workflow

#### Manual Testing Setup
```bash
# Create test data directory (outside repo)
mkdir C:\BeamNG_TestData
cd C:\BeamNG_TestData

# Download sample maps for testing
# Always use COPIES for testing!
```

#### Test Categories
1. **Smoke Tests** - Basic functionality works
2. **Integration Tests** - Full workflows work end-to-end
3. **Performance Tests** - Large file handling
4. **Edge Case Tests** - Error conditions and corner cases

### 3. Code Quality Checks

#### Before Committing
```bash
# Format code (if using command line tools)
dotnet format

# Build without warnings
dotnet build --configuration Release --verbosity normal

# Check for common issues
dotnet analyze
```

## Troubleshooting

### Common Setup Issues

#### "WebView2 Runtime not found"
**Solution:**
1. Download from https://go.microsoft.com/fwlink/p/?LinkId=2124703
2. Install the runtime
3. Restart Visual Studio

#### Build Errors: "SDK not found"
**Solution:**
```bash
# Check installed SDKs
dotnet --list-sdks

# Install .NET 7.0 if missing
winget install Microsoft.DotNet.SDK.7
```

#### NuGet Package Restore Issues
**Solution:**
```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore packages
dotnet restore --force
```

#### Blazor Hot Reload Not Working
**Solution:**
1. Enable Hot Reload in Visual Studio
2. Ensure project targets correct framework
3. Rebuild solution if issues persist

### Performance Issues

#### Slow Build Times
**Solutions:**
- Exclude `bin/` and `obj/` folders from antivirus scanning
- Use SSD for development if possible
- Close unnecessary applications during build

#### High Memory Usage During Development
**Solutions:**
- Increase Visual Studio memory allocation
- Close unused browser tabs
- Restart IDE periodically during heavy development

### Debugging Common Issues

#### Application Won't Start
**Check:**
1. WebView2 Runtime installed
2. .NET 7.0 Runtime available
3. No conflicting processes

#### Map Processing Fails
**Debug Steps:**
1. Check file permissions
2. Verify ZIP file integrity
3. Ensure adequate disk space
4. Review log messages in UI

#### UI Not Responding
**Troubleshoot:**
1. Check browser developer console
2. Verify Blazor SignalR connection
3. Look for JavaScript errors

## Advanced Development Topics

### Custom Build Configurations

#### Creating a Development Configuration
```xml
<!-- In .csproj file -->
<PropertyGroup Condition="'$(Configuration)' == 'Development'">
  <DefineConstants>DEBUG;TRACE;DEVELOPMENT</DefineConstants>
  <Optimize>false</Optimize>
  <OutputPath>bin\Development\</OutputPath>
</PropertyGroup>
```

### Profiling and Performance

#### Memory Profiling
- Use Visual Studio Diagnostic Tools
- Consider JetBrains dotMemory for detailed analysis
- Monitor large file processing carefully

#### Performance Profiling
- Use Visual Studio Performance Profiler
- Focus on file I/O operations
- Profile with realistic data sizes

### Extending the Application

#### Adding New File Scanners
1. Implement scanner class in `Logic/` folder
2. Follow existing patterns (MaterialScanner, DaeScanner)
3. Add appropriate data models in `Objects/`
4. Wire up to UI components

#### Adding New UI Features
1. Create Blazor component in `BlazorUI/Components/`
2. Add page in `BlazorUI/Pages/`
3. Wire up navigation in `MyNavMenu.razor`
4. Follow MudBlazor component patterns

## Getting Help

- **Documentation**: Check `/docs` folder for detailed guides
- **Issues**: Search existing GitHub issues first
- **Discussions**: Use GitHub Discussions for questions
- **Community**: Connect with BeamNG modding community

## Next Steps

After completing setup:
1. Read the [DEVELOPER.md](DEVELOPER.md) for architecture overview
2. Review [CONTRIBUTING.md](docs/CONTRIBUTING.md) for contribution guidelines
3. Explore the codebase starting with `Program.cs`
4. Try building and running with a sample BeamNG map

Happy coding!