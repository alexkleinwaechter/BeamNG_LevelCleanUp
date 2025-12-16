# AutoUpdater.NET Configuration

This document explains how the auto-update functionality works in BeamNG Tools for Mapbuilders.

## Overview

The application uses [AutoUpdater.NET](https://github.com/ravibpatel/AutoUpdater.NET) to check for and install updates automatically. When the application starts, it checks the XML configuration file hosted on GitHub for a newer version.

## How It Works

1. On application startup, `AutoUpdater.Start()` is called with the URL to the XML configuration file
2. AutoUpdater.NET compares the version in the XML with the application's assembly version
3. If a newer version is available, the user is prompted to update
4. The update file (ZIP or installer) is downloaded and executed

## Configuration File

The update configuration is stored in:
```
BeamNG_LevelCleanUp/AutoUpdater.xml
```

And hosted at:
```
https://raw.githubusercontent.com/alexkleinwaechter/BeamNG_LevelCleanUp/master/BeamNG_LevelCleanUp/AutoUpdater.xml
```

### XML Structure

```xml
<?xml version="1.0" encoding="UTF-8"?>
<item>
  <version>2.0.0.0</version>
  <url>https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases/download/v2.0.0/Setup.exe</url>
  <changelog>https://github.com/alexkleinwaechter/BeamNG_LevelCleanUp/releases</changelog>
  <mandatory>false</mandatory>
</item>
```

### XML Elements

| Element | Required | Description |
|---------|----------|-------------|
| `version` | Yes | Latest version in X.X.X.X format. Must be higher than assembly version to trigger update. |
| `url` | Yes | URL to the update file (ZIP or installer EXE/MSI) |
| `changelog` | No | URL to the changelog/release notes page |
| `mandatory` | No | If `true`, user cannot skip the update |
| `checksum` | No | Checksum to verify download integrity |
| `args` | No | Command line arguments for the installer |

## Update File Types

### ZIP Files
- AutoUpdater.NET extracts contents directly to the application directory
- Good for portable/xcopy deployments
- Requires write access to application folder

### Installer Files (EXE/MSI)
- AutoUpdater.NET downloads and executes the installer
- Installer handles the update process
- Better for standard Windows installations
- Can run silently with arguments

#### Silent Installation Arguments

For Inno Setup installers:
```xml
<args>/SILENT</args>
```

For completely silent (no UI):
```xml
<args>/VERYSILENT</args>
```

For MSI installers:
```xml
<args>/quiet</args>
```

## Releasing a New Version

1. **Update Assembly Version**
   - Update the version in `BeamNG_LevelCleanUp.csproj` or project properties
   - The new version must be higher than the previous release

2. **Build the Release**
   - Build in Release configuration
   - Create installer or ZIP package

3. **Upload the Update File**
   - Upload to GitHub Releases or other hosting

4. **Update AutoUpdater.xml**
   ```xml
   <?xml version="1.0" encoding="UTF-8"?>
   <item>
     <version>X.X.X.X</version>
     <url>https://github.com/.../releases/download/vX.X.X/Setup.exe</url>
     <changelog>https://github.com/.../releases</changelog>
     <mandatory>false</mandatory>
   </item>
   ```

5. **Commit and Push**
   - Push the updated XML to the master branch
   - Users will see the update on next application launch

## Optional: Adding Checksum Verification

For added security, include a checksum:

```xml
<checksum algorithm="SHA256">YOUR_FILE_HASH_HERE</checksum>
```

Generate the hash with PowerShell:
```powershell
Get-FileHash -Algorithm SHA256 .\Setup.exe | Select-Object -ExpandProperty Hash
```

## Code Location

The AutoUpdater is initialized in `BeamNG_LevelCleanUp/Program.cs`:

```csharp
AutoUpdater.Start(
    "https://raw.githubusercontent.com/alexkleinwaechter/BeamNG_LevelCleanUp/master/BeamNG_LevelCleanUp/AutoUpdater.xml");
```

## Troubleshooting

### Update Not Detected
- Ensure XML version is higher than assembly version
- Check the XML URL is accessible
- Verify XML format is valid

### Download Fails
- Check the download URL is correct and accessible
- Verify file hosting allows direct downloads
- Check for firewall/proxy issues

### Installer Doesn't Run
- Ensure the user has permissions to run installers
- Check if antivirus is blocking the download
- Verify the installer is signed (if applicable)

## References

- [AutoUpdater.NET GitHub](https://github.com/ravibpatel/AutoUpdater.NET)
- [AutoUpdater.NET NuGet](https://www.nuget.org/packages/Autoupdater.NET.Official)
