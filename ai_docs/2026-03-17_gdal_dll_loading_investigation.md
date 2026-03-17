# GDAL DLL Loading Failure Investigation

**Date:** 2026-03-17
**Status:** Open — awaiting vcredist cleanup result from user
**Affected user:** Windows 11 24H2 build 26200, x64, .NET 9.0.11

## Problem

GDAL initialization fails with `DllNotFoundException`:
```
Unable to load DLL 'gdal_wrap' or one of its dependencies:
Die angegebene Prozedur wurde nicht gefunden. (0x8007007F)
```

Win32 error code `0x8007007F` = `ERROR_PROC_NOT_FOUND` — a DLL was found and loaded, but a specific **exported function** it tries to import from another DLL does not exist. This is a binary compatibility issue, not a missing file.

## What We Know

### Diagnostics collected

| Check | Result |
|-------|--------|
| Architecture | x64 process on x64 OS (not ARM) |
| All shipped DLLs present | Yes — `gdal.dll`, `gdal_wrap.dll`, `proj_9.dll`, etc. all FOUND on disk |
| Individual DLL loading | 83/88 native DLLs load fine via `LoadLibrary` |
| Failed DLLs | `gdal.dll`, `gdal_wrap.dll`, `ogr_wrap.dll`, `osr_wrap.dll`, `gdalconst_wrap.dll` |
| `gdal.dll` loads without imports | Yes (`LoadLibraryEx` with `DONT_RESOLVE_DLL_REFERENCES` succeeds) |
| VC++ runtime loads | `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` all OK |
| Dependencies tool (lucasg) | No red entries in direct or transitive dependency tree |
| GDAL 3.10 vs 3.12 | Both fail identically |

### Root cause narrowed to

`gdal.dll` is a valid x64 PE that loads without imports, but fails when Windows resolves its import table. One of its dependencies exports a function set that doesn't include a procedure `gdal.dll` expects.

Since the Dependencies tool shows no red entries, the issue is likely in a **Windows "Known DLL"** or **UCRT API set** — system DLLs that Windows forces to load from `System32` regardless of app-local copies.

### What was tried

1. `SetDllDirectory(appDir)` — no effect (system DLLs bypass this)
2. `NativeLibrary.SetDllImportResolver` — no effect (only controls .NET P/Invoke, not native PE loader)
3. Pre-loading all native DLLs with `LoadLibrary` — confirmed the 5 failing DLLs but didn't fix
4. Installing VC++ 2015-2022 Redistributable x64 — no effect
5. Upgrading MaxRev.Gdal from 3.10.0.306 to 3.12.2.472 — no effect
6. vcredist cleanup with abbodi1406 tool — awaiting result

## Next Diagnostic Step: Process Monitor

If the vcredist cleanup doesn't resolve the issue, **Process Monitor** (Sysinternals) can capture the exact DLL and function that fails.

### Setup instructions for the user

1. **Download Process Monitor** from https://learn.microsoft.com/en-us/sysinternals/downloads/procmon
   - Direct download: https://download.sysinternals.com/files/ProcessMonitor.zip
   - Extract to any folder and run `Procmon64.exe`

2. **Configure filters** (Edit > Filter, or Ctrl+L):
   - Add these filters (set each to "Include"):
     ```
     Process Name    is    BeamNG_LevelCleanUp.exe    Include
     Operation       is    Load Image                 Include
     Result          is    NAME NOT FOUND             Include
     ```
   - Also add a broader filter to catch the actual failure:
     ```
     Process Name    is    BeamNG_LevelCleanUp.exe    Include
     Path            contains    gdal                 Include
     ```
   - Remove or exclude all default filters that don't apply

3. **Capture the failure:**
   - Start Process Monitor (it begins capturing immediately)
   - Launch the BeamNG Mapping Tools application
   - Trigger the GDAL initialization (e.g., try to import a GeoTIFF or validate one)
   - Stop capture in Process Monitor (File > Capture Events or Ctrl+E)

4. **What to look for:**
   - Any `Load Image` operation with result `NAME NOT FOUND` — this shows a DLL that couldn't be found
   - Any `Load Image` for `gdal.dll` — check which path it actually loads from and the result
   - The sequence of DLL loads right before the failure
   - Look for system DLLs (`api-ms-win-crt-*.dll`, `ucrtbase.dll`, `odbc32.dll`) loaded from `C:\Windows\System32` — note their paths

5. **Export and share:**
   - File > Save (save as PML format for full data, or CSV for quick sharing)
   - Apply the `gdal` path filter before saving to reduce file size
   - Share the exported file

### What the Process Monitor output tells us

- **If a DLL path shows `NAME NOT FOUND`:** A dependency is missing entirely from the system
- **If `gdal.dll` loads from an unexpected path:** Another GDAL installation is interfering
- **If all loads succeed but init still fails:** The issue is in a specific function export, and we'd need `dumpbin /imports gdal.dll` cross-referenced with `dumpbin /exports <dependency>.dll` to find the mismatch

## Windows "Known DLLs" — Why app-local copies don't work

Windows maintains a registry key at `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs` listing DLLs that are **always** loaded from `System32`, ignoring the application directory. This is a security measure to prevent DLL hijacking.

Known DLLs typically include:
- `kernel32.dll`, `ntdll.dll`, `user32.dll`, `gdi32.dll`
- `crypt32.dll`, `odbc32.dll`, `ole32.dll`, `oleaut32.dll`
- `ws2_32.dll`, `advapi32.dll`, `shell32.dll`

Additionally, **UCRT (Universal C Runtime) API sets** like `api-ms-win-crt-runtime-l1-1-0.dll` are resolved through the Windows API set schema, not through normal DLL search. These forward to `ucrtbase.dll` in System32.

If `gdal.dll` imports a CRT function that was added in a newer UCRT version than the user's Windows provides, the load fails with `ERROR_PROC_NOT_FOUND`. This can happen when:
- The GDAL native binaries were compiled with a newer MSVC toolchain than the user's Windows UCRT supports
- A Windows update removed or changed a UCRT export (rare but possible on Insider builds)
- The user's Windows build (26200) has a UCRT version that differs from what the GDAL binaries expect

## Code changes made during investigation

### Kept (defensive, low-cost)
- `SetDllDirectory(AppContext.BaseDirectory)` before `GdalBase.ConfigureAll()` — ensures app dir is in native DLL search path
- Detailed error diagnostics in `catch` block — logs environment, file presence, and full exception chain to `TerrainCreationLogger` or fallback `gdal_error.log`

### Removed (diagnostic scaffolding)
- Full DLL enumeration and `LoadLibrary` pre-load loop
- `IsManaged()`, `GetPEMachineType()`, `LoadLibraryEx` helpers
- `NativeLibrary.SetDllImportResolver` registration
- `_nativeResolverRegistered` flag

### Package updates
- `MaxRev.Gdal.Core`: 3.10.0.300 -> 3.12.2.472
- `MaxRev.Gdal.WindowsRuntime.Minimal`: 3.10.0.300 -> 3.12.2.472
- `BeamNgTerrainPoc.csproj`: added `<PlatformTarget>x64</PlatformTarget>` to fix MSB3270 architecture mismatch warning
