# Contributing Guidelines

Thank you for your interest in contributing to BeamNG Level CleanUp! This document provides guidelines and information for contributors.

## Table of Contents

1. [Code of Conduct](#code-of-conduct)
2. [Getting Started](#getting-started)
3. [Development Workflow](#development-workflow)
4. [Code Standards](#code-standards)
5. [Testing Guidelines](#testing-guidelines)
6. [Documentation](#documentation)
7. [Pull Request Process](#pull-request-process)
8. [Issue Reporting](#issue-reporting)

## Code of Conduct

We are committed to providing a welcoming and inclusive environment for all contributors. Please be respectful in all interactions and follow these principles:

- Be constructive and respectful in feedback
- Welcome newcomers and help them learn
- Focus on technical merit in discussions
- Respect different viewpoints and experiences

## Getting Started

### Prerequisites

Before contributing, ensure you have:

1. **Windows Development Environment**
   - Windows 10/11 (required for WinForms and WebView2)
   - Visual Studio 2022 or Visual Studio Code with C# extension
   - .NET 7.0 SDK or higher

2. **Development Tools**
   - Git for version control
   - Microsoft WebView2 Runtime

### Setting Up Development Environment

1. **Fork the Repository**
   ```bash
   # Click "Fork" on GitHub, then clone your fork
   git clone https://github.com/YOUR_USERNAME/BeamNG_LevelCleanUp.git
   cd BeamNG_LevelCleanUp
   ```

2. **Add Upstream Remote**
   ```bash
   git remote add upstream https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp.git
   ```

3. **Install Dependencies**
   ```bash
   dotnet restore
   ```

4. **Verify Build**
   ```bash
   dotnet build --configuration Debug
   ```

## Development Workflow

### Branching Strategy

- `master` - Main branch, always stable
- `develop` - Integration branch for new features
- `feature/feature-name` - Feature development branches
- `bugfix/bug-description` - Bug fix branches
- `hotfix/critical-fix` - Critical fixes for production

### Creating a Feature Branch

```bash
# Update your local master
git checkout master
git pull upstream master

# Create and switch to feature branch
git checkout -b feature/your-feature-name

# Make your changes...

# Commit changes
git add .
git commit -m "Add feature: brief description"

# Push to your fork
git push origin feature/your-feature-name
```

### Keeping Your Branch Updated

```bash
# Fetch latest changes from upstream
git fetch upstream

# Rebase your feature branch
git checkout feature/your-feature-name
git rebase upstream/master

# Force push if already pushed (be careful!)
git push --force-with-lease origin feature/your-feature-name
```

## Code Standards

### C# Coding Standards

Follow Microsoft's C# coding conventions:

#### Naming Conventions
```csharp
// Classes, methods, properties - PascalCase
public class MaterialScanner
{
    public string FilePath { get; set; }
    public void ScanMaterials() { }
}

// Fields, local variables - camelCase
private string materialPath;
var fileList = new List<string>();

// Constants - PascalCase
public const string DefaultExtension = ".zip";

// Interfaces - PascalCase with 'I' prefix
public interface IAssetScanner { }
```

#### Code Structure
```csharp
// File header (optional but recommended)
using System;
using System.Collections.Generic;
using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Logic
{
    /// <summary>
    /// Handles scanning of material files in BeamNG maps.
    /// </summary>
    public class MaterialScanner
    {
        // Fields first
        private readonly string _levelPath;
        
        // Constructors
        public MaterialScanner(string levelPath)
        {
            _levelPath = levelPath ?? throw new ArgumentNullException(nameof(levelPath));
        }
        
        // Properties
        public List<MaterialJson> Materials { get; private set; } = new();
        
        // Methods
        public async Task ScanAsync()
        {
            // Implementation
        }
    }
}
```

### Documentation Standards

#### XML Documentation Comments
```csharp
/// <summary>
/// Scans a BeamNG map for material dependencies.
/// </summary>
/// <param name="mapPath">Path to the extracted map directory.</param>
/// <param name="includeUnused">Whether to include unused materials in results.</param>
/// <returns>List of material objects found in the map.</returns>
/// <exception cref="DirectoryNotFoundException">Thrown when map directory doesn't exist.</exception>
public async Task<List<MaterialJson>> ScanMaterialsAsync(string mapPath, bool includeUnused = false)
```

### Error Handling Standards

#### Use Appropriate Exception Types
```csharp
// Good - specific exception type
if (!File.Exists(filePath))
    throw new FileNotFoundException($"Material file not found: {filePath}");

// Good - validation
if (string.IsNullOrWhiteSpace(mapName))
    throw new ArgumentException("Map name cannot be empty", nameof(mapName));
```

#### Log Important Operations
```csharp
// Use PubSubChannel for user-visible messages
PubSubChannel.SendMessage(PubSubMessageType.Info, "Starting material scan...");

try
{
    // Operation
    PubSubChannel.SendMessage(PubSubMessageType.Info, "Material scan completed successfully");
}
catch (Exception ex)
{
    PubSubChannel.SendMessage(PubSubMessageType.Error, $"Material scan failed: {ex.Message}");
    throw;
}
```

### UI Component Standards

#### Blazor Component Structure
```razor
@* Component documentation *@
@* Component: MaterialList - Displays list of materials with selection *@

@page "/materials"
@using BeamNG_LevelCleanUp.Objects
@inject IJSRuntime JSRuntime

<MudContainer MaxWidth="MaxWidth.Large">
    <MudText Typo="Typo.h4">Materials</MudText>
    
    @if (materials != null)
    {
        <MudDataGrid Items="materials" Filterable="true">
            <Columns>
                <PropertyColumn Property="x => x.Name" Title="Material Name" />
                <PropertyColumn Property="x => x.DiffuseMap" Title="Diffuse Map" />
            </Columns>
        </MudDataGrid>
    }
</MudContainer>

@code {
    private List<MaterialJson>? materials;
    
    protected override async Task OnInitializedAsync()
    {
        // Load materials
    }
}
```

## Testing Guidelines

### Manual Testing Requirements

Since this is a specialized tool for BeamNG maps, automated testing is challenging. Follow these manual testing practices:

#### Before Submitting PR
1. **Test with Real Maps**
   - Use various BeamNG map formats
   - Test with both small and large maps
   - Verify with official and community maps

2. **Test All Features**
   - Map Shrinking: Verify correct file identification and deletion
   - Map Renaming: Ensure all references are updated
   - Asset Copying: Confirm proper asset transfer
   - Forest Conversion: Validate scene tree conversion

3. **Edge Case Testing**
   - Empty directories
   - Corrupted ZIP files
   - Invalid map structures
   - Large files (>1GB)
   - Maps with special characters in names

#### Test Data Management
```bash
# Create a test data directory (not committed to repo)
mkdir TestData
cd TestData

# Download sample maps for testing
# Always test with COPIES, never original files
```

### Performance Testing

- Test with large maps (>500MB)
- Monitor memory usage during operations
- Verify UI responsiveness during long operations
- Test cancellation functionality

## Documentation

### Code Documentation

#### Required Documentation
- XML comments for all public APIs
- Inline comments for complex logic
- README updates for new features
- API documentation updates

#### Documentation Standards
```csharp
// Good - explains the why, not just what
// Calculate material dependencies recursively because BeamNG materials
// can reference other materials through inheritance chains
var dependencies = await CalculateDependenciesRecursive(material);

// Avoid - just restates the code
// Loop through materials
foreach (var material in materials) { }
```

### User Documentation

When adding user-facing features:
- Update main README.md
- Add screenshots if UI changes
- Update feature documentation
- Consider creating tutorial content

## Pull Request Process

### Before Creating a PR

1. **Code Review Checklist**
   - [ ] Code follows established patterns
   - [ ] No hardcoded paths or values
   - [ ] Error handling is appropriate
   - [ ] Performance considerations addressed
   - [ ] Documentation updated

2. **Testing Checklist**
   - [ ] Manually tested with real BeamNG maps
   - [ ] Edge cases considered
   - [ ] No regressions in existing features
   - [ ] Performance acceptable with large files

### PR Description Template

```markdown
## Description
Brief description of changes and motivation.

## Type of Change
- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Documentation update

## Testing
- [ ] Tested with [specific map names or types]
- [ ] Performance tested with large files
- [ ] Edge cases verified
- [ ] No regressions found

## Screenshots (if applicable)
Include screenshots of UI changes.

## Related Issues
Fixes #issue_number
```

### Review Process

1. **Automated Checks**
   - Build verification
   - Code format checking

2. **Manual Review**
   - Code quality and standards
   - Architecture and design
   - Security considerations
   - Performance implications

3. **Testing Review**
   - Test coverage adequacy
   - Edge case handling
   - User experience impact

## Issue Reporting

### Bug Reports

Use the following template for bug reports:

```markdown
**Describe the Bug**
Clear description of what the bug is.

**To Reproduce**
Steps to reproduce the behavior:
1. Go to '...'
2. Click on '....'
3. See error

**Expected Behavior**
What you expected to happen.

**Screenshots**
If applicable, add screenshots.

**Environment:**
- OS: [e.g., Windows 10]
- .NET Version: [e.g., 7.0]
- Application Version: [e.g., 1.2.1]

**Map Information:**
- Map name and source
- Map file size
- Any special characteristics

**Additional Context**
Any other context about the problem.
```

### Feature Requests

```markdown
**Feature Description**
Clear description of the requested feature.

**Use Case**
Explain why this feature would be useful.

**Proposed Solution**
If you have ideas for implementation.

**Alternative Solutions**
Other approaches you've considered.

**Additional Context**
Screenshots, mockups, or examples.
```

## Getting Help

- **Questions**: Use GitHub Discussions for general questions
- **Bug Reports**: Create GitHub Issues with the bug report template
- **Feature Requests**: Create GitHub Issues with the feature request template
- **Development Help**: Tag maintainers in discussions

## Recognition

Contributors will be acknowledged in:
- Release notes for significant contributions
- Contributors section in README
- Special recognition for major features or fixes

Thank you for contributing to BeamNG Level CleanUp!