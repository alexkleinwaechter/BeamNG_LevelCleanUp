# BeamNG Tools for Mapbuilders - WiX Installer

This directory contains the WiX v4 installer project for BeamNG Tools for Mapbuilders.

## Why an MSI Installer?

The application uses GDAL (Geospatial Data Abstraction Library) for reading GeoTIFF elevation data. GDAL requires a specific folder structure with native libraries and data files (like `proj.db` for coordinate transformations) that **cannot** be bundled into a single-file executable.

Using a WiX MSI installer solves this problem by:
1. Installing files in their proper folder structure
2. Properly laying out the `runtimes` folder that GDAL needs
3. Setting up Start Menu and Desktop shortcuts
4. Enabling clean uninstall

## Prerequisites

1. **.NET 9 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
2. **WiX Toolset v4** - Install with:
   ```bash
   dotnet tool install --global wix
   ```

## Building the Installer

### Option 1: Use the Build Script (Recommended)

From the solution root directory:

**PowerShell:**
```powershell
.\build-installer.ps1
```

**Command Prompt:**
```cmd
build-installer.cmd
```

### Option 2: Manual Build

1. **Publish the application** (from solution root):
   ```bash
   dotnet publish BeamNG_LevelCleanUp\BeamNG_LevelCleanUp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
   ```

2. **Build the installer** (from this directory):
   ```bash
   wix build Package.wxs -o BeamNG_LevelCleanUp_Setup.msi
   ```

## Output

After successful build:
- **MSI Installer**: `Installer\BeamNG_LevelCleanUp_Setup.msi`
- **Published Application**: `BeamNG_LevelCleanUp\bin\Release\net9.0-windows10.0.17763.0\win-x64\publish\`

## Installation

The MSI installs to: `C:\Program Files\BeamNG Tools for Mapbuilders`

It creates:
- Start Menu shortcut
- Desktop shortcut
- Registry entries for proper uninstall

## GDAL and MaxRev Packages

This project uses the MaxRev GDAL packages instead of the raw GDAL NuGet packages:
- `MaxRev.Gdal.Core` - Core GDAL wrapper with auto-configuration
- `MaxRev.Gdal.WindowsRuntime.Minimal` - Minimal Windows native binaries

MaxRev packages are specifically designed to work with .NET applications and automatically:
- Set up `GDAL_DATA` environment variable
- Set up `PROJ_LIB` environment variable for coordinate transformations
- Find the native library folder relative to the executable

## Troubleshooting

### "GDAL initialization failed"
Ensure the `runtimes` folder was properly installed alongside the executable. Check that `runtimes\win-x64\native\proj.db` exists.

### Missing drivers
Verify the `runtimes\win-x64\native` folder contains the GDAL DLLs.

### WiX build fails
1. Ensure WiX v4+ is installed: `dotnet tool list --global | findstr wix`
2. Check that the publish output exists in the expected location
3. Verify the path in `Package.wxs` matches your publish output

## File Structure

```
Installer/
??? BeamNG_LevelCleanUp.Installer.wixproj  # WiX project file
??? Package.wxs                              # Main WiX source file
??? README.md                                # This file
```
