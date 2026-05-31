# GenerateTerrain GeoReference Settings Handoff

Date: 2026-05-31

## Goal

Implement a follow-up for the Basecolor Manager settings integration.

When `GenerateTerrain.razor` successfully generates terrain, save the georeferencing information from the selected elevation data into `MT_settings.json`, creating the settings file if it does not exist.

This metadata will be used later by BaseColor Mode to download a satellite image and blend it into the generated basecolor texture. The Basecolor Manager needs to know the real-world coordinates and projection of the generated terrain/elevation data.

## Context

Recent commits added Basecolor Manager and `MT_settings.json` support:

- `BeamNG_LevelCleanUp/Objects/MtSettings/MtSettings.cs`
- `BeamNG_LevelCleanUp/LogicBasecolorManager/*`
- `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor(.cs)`
- Wizard terrain-copy integration in `CopyTerrains.razor.cs`

`MT_settings.json` lives in the selected/unpacked level root. It currently stores:

- `CurrentMode`
- `PaintModeSettings`
- `BasecolorModeSettings`

Build command:

```powershell
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```

Ignore DLL-lock `MSB3027` / `MSB3021` if the app is running; only `error CS` matters.

## Required Behavior

1. Find the successful terrain-generation completion path in `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs`.

   Likely anchors:
   - code that sets `Step6_TerrainGenerated`
   - code that writes terrain outputs
   - code that shows the terrain completion dialog
   - code that handles wizard completion after terrain generation

2. Determine where GenerateTerrain already knows the selected elevation data bounds/projection.

   Look for:
   - GeoTIFF import / selected elevation data
   - crop bounds / world bounds
   - projection / CRS / EPSG / coordinate transform metadata
   - terrain size / meters-per-pixel if relevant

3. Extend `MtSettings` with a new georeferencing settings block.

Suggested shape:

```csharp
public MtGeoReferenceSettings GeoReferenceSettings { get; set; } = new();
```

Suggested fields, adjusted to match the actual data available in the pipeline:

```csharp
public class MtGeoReferenceSettings
{
    public bool HasGeoReference { get; set; }
    public string Projection { get; set; } = string.Empty;
    public string SourceElevationPath { get; set; } = string.Empty;
    public double MinLongitude { get; set; }
    public double MinLatitude { get; set; }
    public double MaxLongitude { get; set; }
    public double MaxLatitude { get; set; }
    public double CenterLongitude { get; set; }
    public double CenterLatitude { get; set; }
    public double MetersPerPixel { get; set; }
    public int TerrainSize { get; set; }
    public DateTime SavedAtUtc { get; set; }
}
```

If the existing elevation pipeline uses projected coordinates rather than longitude/latitude, include both projected bounds and WGS84 bounds if available. Prefer storing raw/source metadata exactly as available over inventing coordinate conversions. Only convert to WGS84 if existing utilities already do that in the pipeline.

4. On successful terrain generation:

   - Locate the level root/unpacked folder.
   - Load `MtSettings.Load(levelRoot)`.
   - If no file exists, create `new MtSettings()`.
   - Fill/update `GeoReferenceSettings`.
   - Preserve existing `CurrentMode`, `PaintModeSettings`, and `BasecolorModeSettings`.
   - Save via `settings.Save(levelRoot)`.

5. This should run for normal GenerateTerrain and wizard GenerateTerrain if both have a successful generation path and a valid level root.

6. Do not overwrite Basecolor/Paint mode materials.

7. Do not change `*.terrain.json` `"size"` or any terrain/basecolor texture behavior.

8. Add PubSub messages:

   - Info when georeference metadata is saved.
   - Warning if terrain generation succeeds but no usable elevation projection/bounds were available.

## Implementation Notes

Follow existing patterns in:

- `GenerateTerrain.razor.cs`
- `TerrainMaterialService.LoadLevelFromFolder`
- `MtSettings.Load(levelRoot)` / `settings.Save(levelRoot)`

Use the `MtSettings` Load/Save pattern, not ad hoc JSON writing.

Keep changes minimal and scoped.

If projection/bounds fields are not obvious, inspect:

- `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs`
- `BeamNG_LevelCleanUp/Services/TerrainGenerationOrchestrator*`
- `BeamNgTerrainPoc/Terrain/**`
- GeoTIFF/elevation import related classes

## Verification

1. Build:

```powershell
dotnet build BeamNG_LevelCleanUp/BeamNG_LevelCleanUp.csproj
```

2. Manual check:

- Generate terrain from elevation data.
- Confirm `MT_settings.json` is created if absent.
- Confirm existing Basecolor Manager settings are preserved if present.
- Confirm the new georeference block contains projection and coordinate/bounds metadata suitable for future satellite imagery download/blending.
