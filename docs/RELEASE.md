# Release Process and Guidelines

This document outlines the release process and guidelines for BeamNG Level CleanUp.

## Release Process

### Version Numbering

The project follows Semantic Versioning (SemVer):
- **MAJOR.MINOR.PATCH** (e.g., 1.2.1)
- **MAJOR**: Incompatible API changes or major feature overhauls
- **MINOR**: New functionality in a backwards compatible manner
- **PATCH**: Backwards compatible bug fixes

### Release Types

#### Patch Release (1.2.1 → 1.2.2)
- Bug fixes
- Minor UI improvements
- Performance optimizations
- Documentation updates

#### Minor Release (1.2.x → 1.3.0)
- New features
- Significant UI enhancements
- New file format support
- Major performance improvements

#### Major Release (1.x.x → 2.0.0)
- Breaking changes to existing functionality
- Complete UI overhaul
- Architectural changes
- Remove deprecated features

### Release Checklist

#### Pre-Release
- [ ] All tests pass with sample BeamNG maps
- [ ] Performance tested with large maps (>500MB)
- [ ] UI tested on different screen resolutions
- [ ] Documentation updated
- [ ] Version numbers updated in:
  - [ ] `BeamNG_LevelCleanUp.csproj` (AssemblyVersion, FileVersion)
  - [ ] `AutoUpdater.xml`
  - [ ] Release notes prepared

#### Release Build
- [ ] Build in Release configuration
- [ ] Test the release build thoroughly
- [ ] Create installer/distribution package
- [ ] Test installer on clean system
- [ ] Upload release artifacts

#### Post-Release
- [ ] Update AutoUpdater.xml with new version info
- [ ] Create GitHub release with:
  - [ ] Release notes
  - [ ] Binary attachments
  - [ ] Known issues (if any)
- [ ] Update documentation if needed
- [ ] Announce release in appropriate channels

## Development Guidelines

### Code Quality Standards

#### Before Committing
```bash
# Ensure code builds without warnings
dotnet build --configuration Release --verbosity normal

# Run any available analyzers
dotnet format --verify-no-changes
```

#### Code Review Checklist
- [ ] Code follows established patterns
- [ ] Error handling is appropriate and user-friendly
- [ ] Performance implications considered
- [ ] Security implications reviewed
- [ ] Documentation updated for public APIs
- [ ] Manual testing completed

### Feature Development Process

#### 1. Planning Phase
- Create or reference GitHub issue
- Design UI mockups if needed
- Consider impact on existing functionality
- Plan testing approach

#### 2. Development Phase
- Create feature branch from master
- Implement feature following coding standards
- Add appropriate error handling
- Document public APIs

#### 3. Testing Phase
- Test with variety of BeamNG maps
- Test edge cases and error conditions
- Performance test with large files
- UI test on different screen sizes

#### 4. Review Phase
- Self-review code changes
- Update documentation
- Create pull request with detailed description
- Address review feedback

### Testing Standards

#### Manual Testing Requirements

**Basic Functionality Tests**
- [ ] Application starts without errors
- [ ] All main features accessible
- [ ] UI responsive and functional
- [ ] Auto-updater works (if applicable)

**Feature-Specific Tests**

*Map Shrinking*
- [ ] Correctly identifies used assets
- [ ] Safely removes orphaned files
- [ ] Creates valid output ZIP
- [ ] Preserves map functionality

*Map Renaming*
- [ ] Updates all file references
- [ ] Creates valid renamed map
- [ ] Preserves original if requested
- [ ] Handles special characters correctly

*Asset Copying*
- [ ] Correctly identifies available assets
- [ ] Copies selected assets properly
- [ ] Updates material references
- [ ] Creates valid target map

*Forest Conversion*
- [ ] Converts static assets correctly
- [ ] Preserves positioning and rotation
- [ ] Handles scale variations
- [ ] Creates valid forest files

#### Performance Testing
- Test with maps over 1GB in size
- Monitor memory usage during processing
- Verify UI remains responsive
- Test cancellation functionality

#### Edge Case Testing
- Empty or minimal maps
- Corrupted ZIP files
- Invalid map structures
- Very long file paths
- Special characters in names
- Network drive locations

### Security Considerations

#### File System Operations
- Always validate file paths
- Prevent directory traversal attacks
- Use safe temporary directories
- Proper file handle cleanup

#### Input Validation
- Validate ZIP file contents
- Check JSON structure before parsing
- Limit file sizes where appropriate
- Sanitize user input for file names

#### Error Handling
- Don't expose sensitive file paths in errors
- Provide user-friendly error messages
- Log detailed errors for debugging
- Fail securely when errors occur

## Deployment Guidelines

### Building Release Versions

#### Standard Release Build
```bash
# Clean previous builds
dotnet clean

# Restore packages
dotnet restore

# Build in Release mode
dotnet build --configuration Release

# Publish self-contained (if desired)
dotnet publish --configuration Release --self-contained true --runtime win-x64
```

#### Distribution Package
The application can be distributed as:
1. **Standalone Executable** - Single .exe with dependencies
2. **ZIP Package** - All files in compressed archive
3. **Installer** - Windows installer package (future consideration)

### Auto-Update System

#### Updating AutoUpdater.xml
```xml
<?xml version="1.0" encoding="UTF-8"?>
<item>
    <version>1.2.1.0</version>
    <url>https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases/download/v1.2.1/BeamNG_LevelCleanUp.exe</url>
    <changelog>https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases/tag/v1.2.1</changelog>
    <mandatory>false</mandatory>
</item>
```

#### Update Process
1. User starts application
2. AutoUpdater checks for new version
3. If available, prompts user to download
4. Downloads and replaces executable
5. Restarts with new version

## Troubleshooting Common Issues

### Build Issues
- Ensure .NET 7.0 SDK installed
- Clear NuGet cache if package issues
- Check for missing Windows-specific dependencies

### Runtime Issues
- Verify WebView2 Runtime installed
- Check file permissions for temp directories
- Ensure adequate disk space for operations

### Performance Issues
- Profile memory usage with large files
- Optimize I/O operations
- Consider async patterns for UI responsiveness

---

This document should be updated as the development and release processes evolve.