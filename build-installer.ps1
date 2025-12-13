# ============================================
# BeamNG Tools for Mapbuilders - Build Script
# ============================================
# This script builds the application and creates the MSI installer
# 
# Prerequisites:
#   - .NET 9 SDK
#   - WiX Toolset v4 (will be installed automatically if missing)
#
# Usage: .\build-installer.ps1
# ============================================

param(
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [switch]$PublishOnly
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Building BeamNG Tools for Mapbuilders" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Navigate to solution root
Set-Location $PSScriptRoot

# Step 1: Restore NuGet packages
if (-not $SkipRestore -and -not $PublishOnly) {
    Write-Host "[1/4] Restoring NuGet packages..." -ForegroundColor Yellow
    dotnet restore BeamNG_LevelCleanUp.sln
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Package restore failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "Package restore completed." -ForegroundColor Green
} else {
    Write-Host "[1/4] Skipping package restore..." -ForegroundColor Gray
}

# Step 2: Build the solution
if (-not $SkipBuild -and -not $PublishOnly) {
    Write-Host ""
    Write-Host "[2/4] Building solution in Release mode..." -ForegroundColor Yellow
    dotnet build BeamNG_LevelCleanUp.sln -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build completed." -ForegroundColor Green
} else {
    Write-Host "[2/4] Skipping build..." -ForegroundColor Gray
}

# Step 3: Publish the main application
Write-Host ""
Write-Host "[3/4] Publishing application..." -ForegroundColor Yellow
Write-Host "       Target: win-x64, Self-contained, NO SingleFile (required for GDAL)" -ForegroundColor Gray

dotnet publish BeamNG_LevelCleanUp\BeamNG_LevelCleanUp.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Publish failed" -ForegroundColor Red
    exit 1
}
Write-Host "Publish completed." -ForegroundColor Green

# Verify the publish output contains GDAL files
$publishDir = "BeamNG_LevelCleanUp\bin\Release\net9.0-windows10.0.17763.0\win-x64\publish"
$runtimesDir = Join-Path $publishDir "runtimes"
if (Test-Path $runtimesDir) {
    $gdalDlls = Get-ChildItem -Path $runtimesDir -Recurse -Filter "gdal*.dll" -ErrorAction SilentlyContinue
    if ($gdalDlls.Count -gt 0) {
        Write-Host "Found $($gdalDlls.Count) GDAL DLLs in runtimes folder" -ForegroundColor Green
    } else {
        Write-Host "WARNING: No GDAL DLLs found in runtimes folder!" -ForegroundColor Yellow
    }
} else {
    Write-Host "WARNING: runtimes folder not found in publish output" -ForegroundColor Yellow
}

# Step 4: Build the MSI installer
Write-Host ""
Write-Host "[4/4] Building MSI installer..." -ForegroundColor Yellow

# Check if WiX is installed
$wixPath = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixPath) {
    Write-Host ""
    Write-Host "WiX Toolset not found. Installing..." -ForegroundColor Yellow
    dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to install WiX Toolset" -ForegroundColor Red
        Write-Host "Please install manually with: dotnet tool install --global wix" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "WiX Toolset installed." -ForegroundColor Green
    
    # Refresh PATH
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
}

Push-Location Installer
try {
    # Build the project file with dotnet build to correctly resolve NuGet extensions
    Write-Host "Building the installer project..." -ForegroundColor Yellow
    dotnet build BeamNG_LevelCleanUp.Installer.wixproj -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Installer build failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "Installer build completed." -ForegroundColor Green
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Build completed successfully!" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output files:" -ForegroundColor White
Write-Host "  Application: $publishDir\" -ForegroundColor Gray
Write-Host "  Installer:   Installer\bin\Release\net9.0\win-x64\en-us\BeamNG_LevelCleanUp_Setup.msi" -ForegroundColor Gray
Write-Host ""
Write-Host "The MSI installer includes:" -ForegroundColor White
Write-Host "  - BeamNG Tools for Mapbuilders application" -ForegroundColor Gray
Write-Host "  - All GDAL native libraries and proj.db" -ForegroundColor Gray
Write-Host "  - Start Menu and Desktop shortcuts" -ForegroundColor Gray
Write-Host ""

# Show file size
$msiPath = "Installer\bin\Release\net9.0\win-x64\en-us\BeamNG_LevelCleanUp_Setup.msi"
if (Test-Path $msiPath) {
    $msiSize = (Get-Item $msiPath).Length / 1MB
    Write-Host "Installer size: $([math]::Round($msiSize, 2)) MB" -ForegroundColor White
}
