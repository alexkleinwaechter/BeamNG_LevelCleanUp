@echo off
REM ============================================
REM BeamNG Tools for Mapbuilders - Build Script
REM ============================================
REM This script builds the application and creates the MSI installer
REM 
REM Prerequisites:
REM   - .NET 9 SDK
REM   - WiX Toolset v4 (install with: dotnet tool install --global wix)
REM
REM Usage: build-installer.cmd
REM ============================================

echo.
echo ============================================
echo Building BeamNG Tools for Mapbuilders
echo ============================================
echo.

REM Navigate to solution root
cd /d "%~dp0"

REM Step 1: Restore NuGet packages
echo [1/4] Restoring NuGet packages...
dotnet restore BeamNG_LevelCleanUp.sln
if errorlevel 1 (
    echo ERROR: Package restore failed
    pause
    exit /b 1
)

REM Step 2: Build the solution
echo.
echo [2/4] Building solution in Release mode...
dotnet build BeamNG_LevelCleanUp.sln -c Release
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

REM Step 3: Publish the main application
echo.
echo [3/4] Publishing application...
echo        Target: win-x64, Self-contained, NO SingleFile (required for GDAL)
dotnet publish BeamNG_LevelCleanUp\BeamNG_LevelCleanUp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
if errorlevel 1 (
    echo ERROR: Publish failed
    pause
    exit /b 1
)

REM Step 4: Build the MSI installer
echo.
echo [4/4] Building MSI installer...

REM Check if WiX is installed
where wix >nul 2>&1
if errorlevel 1 (
    echo.
    echo WiX Toolset not found! Installing...
    dotnet tool install --global wix
    if errorlevel 1 (
        echo ERROR: Failed to install WiX Toolset
        echo Please install manually with: dotnet tool install --global wix
        pause
        exit /b 1
    )
)

cd Installer
echo Building the installer project...
dotnet build BeamNG_LevelCleanUp.Installer.wixproj -c Release
if errorlevel 1 (
    echo ERROR: Installer build failed
    cd ..
    pause
    exit /b 1
)
cd ..

echo.
echo ============================================
echo Build completed successfully!
echo ============================================
echo.
echo Output files:
echo   Application: BeamNG_LevelCleanUp\bin\Release\net9.0-windows10.0.17763.0\win-x64\publish\
echo   Installer:   Installer\bin\Release\net9.0\win-x64\en-us\BeamNG_LevelCleanUp_Setup.msi
echo.
echo The MSI installer includes:
echo   - BeamNG Tools for Mapbuilders application
echo   - All GDAL native libraries and proj.db
echo   - Start Menu and Desktop shortcuts
echo.

pause
