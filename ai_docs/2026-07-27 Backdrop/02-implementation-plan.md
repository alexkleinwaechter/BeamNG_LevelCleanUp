# Backdrop Generation (Variant 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate a satellite-textured, adaptively-meshed, drivable 3D backdrop ring around the playable terrain from the extended GeoTIFF selection, exported as chunked world-baked DAEs (`MT_backdrop`), with texture baking interlocked with the BaseColorManager.

**Architecture:** Core mesher/planner/scene-writer live in a new `BeamNgTerrainPoc/Terrain/Backdrop/` namespace (no app-layer references); the app layer (`BeamNG_LevelCleanUp`) owns texture baking (`MapTileOverlayService` refactored to an `OverlayRequest` contract), orchestration (`BackdropOrchestrator` gated stage after `CreateTerrainFileAsync`), settings persistence (`MtBackdropSettings` in `MT_settings.json`) and UI (`BackdropSettingsPanel`, second resizable box in `CropAnchorSelector(+Dialog)`, shared math extracted to `SelectionGeometry`). The seam between core and app is the **chunk plan**. Approved spec: `ai_docs/2026-07-27 Backdrop/01-design.md` (referenced below as "spec §N").

**Tech Stack:** .NET 10 (`net10.0` core lib / `net10.0-windows10.0.17763.0` app), xUnit 2.9.2 (`BeamNgTerrainPoc.Tests`), GDAL (OSGeo.GDAL, already referenced by `BeamNgTerrainPoc`), SixLabors.ImageSharp 3.1.12, `BeamNG.Procedural3D` (`Mesh`/`ColladaExporter`/`BeamNgDaeScene`), MudBlazor 8.14.0.

## Global Constraints

- **Backdrop is default-off everywhere** (spec D8): `BackdropSettings.Enabled = false` default; no core code path runs unless the app explicitly invokes `BackdropGenerator`. All existing tests and generation outputs stay **byte-identical**. (Same discipline as `BridgeRuleSystemOptions`/`TunnelRuleSystemOptions`, `BeamNgTerrainPoc/Terrain/Models/BridgeRuleSystemOptions.cs:5-13`.)
- **Dependency direction:** `BeamNgTerrainPoc` must NOT reference `BeamNG_LevelCleanUp`. Texture baking stays app-side (spec §4). `PubSubChannel` is `internal` to the app — core code logs via return-value warnings lists, never PubSub.
- **Horizontal datum:** all backdrop world-XY math derives from the terrain crop rect mapping (Task 2 `BackdropCoordinateMapper`); never re-derive an independent geotransform (spec §14.2). Effective geotransform math must match `GenerateTerrain.razor.cs:2883 GetEffectiveSourceGeoTransform` (`gt[0] += offsetX*gt[1] + offsetY*gt[2]; gt[3] += offsetX*gt[4] + offsetY*gt[5]`).
- **Heightmap conventions:** working form `float[y,x]` row-major, y = 0 at SOUTH edge; world origin at terrain center, X=East, Y=North, Z=Up; heights pre-base-height; `worldZ = height + TerrainBaseHeight` (`BeamNgTerrainPoc/Terrain/Utils/BeamNgCoordinateTransformer.cs`).
- **Terrain material byte 255 is reserved** (tunnel holes) — the backdrop never touches `.ter` data, but keep this in mind if debug artifacts render material maps.
- **Tests:** core-only automated suite in `BeamNgTerrainPoc.Tests/Backdrop/` (spec §13); app layer has no test project — app tasks verify via `dotnet build` + manual checklist. Test style: xUnit, global `using Xunit`, temp dirs under `Path.GetTempPath()` with `IDisposable` cleanup (mirror `BeamNgTerrainPoc.Tests/Export/BridgeSceneWriterTests.cs`).
- **Run commands:** `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj [--filter "FullyQualifiedName~Backdrop"]`, `dotnet build BeamNG_LevelCleanUp.sln`. Current suite: ~1069 tests green — keep it green after every task.
- **Commits:** one per task minimum, conventional-commit style (`feat(backdrop): …`, `refactor(ui): …`). **No AI attribution footers, no Co-Authored-By trailers** (repo policy — overrides any harness default).
- **All documents and code comments in English.**
- **Defaults (spec §15):** band 200 m; vertical error near/far 0.5/8 m; chunk target 2000 m; texel density near 1 m/px; max chunk texture 2048; far raster cap 8192; skirt on, 2 m; warnings 2 M/8 M triangles, 256 MB/1 GB texture.

## File Structure

```
BeamNgTerrainPoc/Terrain/Backdrop/                     (NEW — core, Tasks 1–10)
├── PixelRect.cs                     source-pixel int rectangle (record struct)
├── BackdropGenerationParameters.cs  explicit input contract + Validate()
├── BackdropValidationResult.cs
├── BackdropCoordinateMapper.cs      source px ↔ world meters (THE horizontal datum)
├── BackdropRaster.cs                elevation window + bilinear + nodata fill
├── BackdropRasterLoader.cs          GDAL window reads (native + downsampled)
├── BackdropHeightField.cs           band/far sampling + seam snap + delta blend (§7)
├── BackdropChunkPlanner.cs          lattice-aligned chunk grid + texture sizes
├── BackdropChunkDefinition.cs       + BackdropChunkPlan
├── BackdropQuadtreeMesher.cs        restricted quadtree + fan triangulation
├── BackdropMesherOptions.cs         + IBackdropImportanceSource + EdgeBandImportanceSource
├── BackdropEdgeSubdivider.cs        deterministic shared chunk-border subdivision
├── BackdropSceneWriter.cs           DAE + materials.json + items.level.json
├── BackdropChunkExportItem.cs
└── BackdropGenerator.cs             entry point + Estimate() + debug artifacts

BeamNgTerrainPoc.Tests/Backdrop/                       (NEW)
├── PixelRectTests.cs, BackdropParametersValidationTests.cs
├── BackdropCoordinateMapperTests.cs
├── BackdropRasterTests.cs, BackdropRasterLoaderTests.cs
├── BackdropHeightFieldSeamTests.cs
├── BackdropChunkPlannerTests.cs
├── BackdropQuadtreeMesherTests.cs   (refinement invariants + error bounds)
├── BackdropTriangulationTests.cs    (watertight, cutout, determinism, border identity)
├── BackdropSceneWriterTests.cs
└── BackdropGeneratorTests.cs

BeamNG_LevelCleanUp/                                    (Tasks 11–19)
├── BlazorUI/State/BackdropSettings.cs                 NEW POCO (+ TerrainGenerationState mods)
├── Objects/MtSettings/MtSettings.cs                   MOD: MtBackdropSettings + MtBackdropChunk
├── LogicBasecolorManager/MapTileOverlayService.cs     MOD: OverlayRequest overload
├── LogicBasecolorManager/BackdropTextureBaker.cs      NEW
├── LogicBasecolorManager/TerrainPbrMapBuilder.cs      MOD: internal adjustments overload
├── LogicBasecolorManager/BasecolorManagerService.cs   MOD: Reset&Rebake extraction + backdrop rebake
├── BlazorUI/Services/BackdropOrchestrator.cs          NEW app orchestration
├── BlazorUI/Services/TerrainGenerationOrchestrator.cs MOD: gated stage
├── BlazorUI/Components/SelectionGeometry.cs           NEW shared selection math
├── BlazorUI/Components/CropAnchorSelector.razor(.cs)  MOD: backdrop box
├── BlazorUI/Components/CropAnchorSelectorDialog.razor(.cs) MOD: backdrop box + S/W/N/E fields
├── BlazorUI/Components/CropDialogResult.cs            MOD: backdrop fields
├── BlazorUI/Components/BackdropSettingsPanel.razor(.cs) NEW
├── BlazorUI/Components/TerrainPresetExporter.razor    MOD: backdropSettings block
├── BlazorUI/Components/TerrainPresetImporter.razor    MOD
├── BlazorUI/Components/TerrainPresetResult.cs         MOD
├── BlazorUI/Pages/GenerateTerrain.razor(.cs)          MOD: thin wiring only
└── BlazorUI/Pages/BasecolorManager.razor.cs           MOD: use extracted service + staleness
```

## Task Order Rationale

Spec §14.1 names the seam logic the single highest-risk area, and the user mandates: seam logic (§7) and mesher **before** UI. Order: contracts (1) → datum (2) → rasters (3) → **seam (4)** → planner (5) → **mesher (6–8)** → scene writer (9) → generator (10) → app plumbing (11–15) → UI (16–19) → verification (20).

Key coordinate decision used throughout (locked here, referenced by many tasks):

- **Global lattice:** unit `u = TerrainMetersPerPixel`, origin at the terrain rect's SW world corner `(−half, −half)` where `half = TerrainSizePixels · u / 2`. Lattice coords `(ix, iy)`, ix→East, iy→North. The terrain rect occupies lattice `[0, size]²`. All mesh vertices (except leaf-fan centers) sit on integer lattice points → vertex welding and cross-chunk border identity are exact by construction. The chunk planner snaps all chunk grid lines to this lattice.
- **Terrain seam line** is the world square `±half` (matches `BeamNgCoordinateTransformer.GetWorldBounds`). Seam vertices are spaced `u` apart = terrain pixel corners (spec §7.1). The outermost terrain height sample (index `size−1`) sits at `half − u`; the seam at `±half` therefore reuses the clamped edge-row height (flat last half-cell). **Watch item for in-game validation:** if a visible step appears on the last half-cell, move the seam line to `(size−1)·u − half` in `BackdropHeightField` — one constant, no structural change.

---

### Task 1: Core contracts — `PixelRect`, `BackdropGenerationParameters`, validation

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/PixelRect.cs`
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropGenerationParameters.cs`
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropValidationResult.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/PixelRectTests.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropParametersValidationTests.cs`

**Interfaces:**
- Consumes: `BeamNgTerrainPoc.Terrain.Models.RoadGeometry.UnifiedRoadNetwork` (type reference only, V2 hook).
- Produces: `PixelRect`, `BackdropGenerationParameters` (all tunables with spec-§15 defaults), `BackdropValidationResult` — consumed by every later core task.

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/PixelRectTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class PixelRectTests
{
    [Fact]
    public void ContainsRect_TrueForEqualAndInner_FalseForOverhang()
    {
        var outer = new PixelRect(10, 10, 100, 80);
        Assert.True(outer.ContainsRect(outer));
        Assert.True(outer.ContainsRect(new PixelRect(20, 20, 50, 40)));
        Assert.False(outer.ContainsRect(new PixelRect(5, 20, 50, 40)));    // west overhang
        Assert.False(outer.ContainsRect(new PixelRect(20, 20, 100, 40))); // east overhang
    }

    [Fact]
    public void RightBottom_AreExclusive()
    {
        var r = new PixelRect(3, 4, 10, 20);
        Assert.Equal(13, r.Right);
        Assert.Equal(24, r.Bottom);
    }
}
```

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropParametersValidationTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropParametersValidationTests
{
    private static BackdropGenerationParameters Valid() => new()
    {
        TerrainHeightMap = new float[64, 64],
        TerrainSizePixels = 64,
        TerrainMetersPerPixel = 2.0f,
        TerrainBaseHeight = 100f,
        TerrainCropMinElevation = 100.0,
        SourceGeoTiffPath = "unused-in-validation.tif",
        SourceRasterWidth = 400,
        SourceRasterHeight = 300,
        SourceGeoTransform = [500000, 2, 0, 5400000, 0, -2],
        ProjectionWkt = null,
        TerrainRect = new PixelRect(150, 100, 64, 64),
        BackdropRect = new PixelRect(100, 50, 200, 180),
        LevelPath = "unused",
        LevelName = "test_level",
        EdgeBandMeters = 20,
    };

    [Fact]
    public void ValidParameters_Pass()
    {
        var r = Valid().Validate();
        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
    }

    [Fact]
    public void BackdropMustContainTerrainRect()
    {
        var p = Valid() with { BackdropRect = new PixelRect(160, 50, 200, 180) };
        var r = p.Validate();
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("contain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackdropMustLieInsideMosaic()
    {
        var p = Valid() with { BackdropRect = new PixelRect(-5, 50, 200, 180) };
        Assert.False(p.Validate().IsValid);
    }

    [Fact]
    public void AllZeroMargins_IsError()
    {
        var p = Valid() with { BackdropRect = new PixelRect(150, 100, 64, 64) };
        var r = p.Validate();
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Contains("margin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarginSmallerThanBand_ProducesWarningNotError()
    {
        // West margin = 5 px. Meters per source px = 64*2/64 = 2 → 10 m < EdgeBandMeters(20) → warning.
        var p = Valid() with { BackdropRect = new PixelRect(145, 50, 150, 180) };
        var r = p.Validate();
        Assert.True(r.IsValid);
        Assert.Contains(r.Warnings, w => w.Contains("band", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HeightmapSizeMismatch_IsError()
    {
        var p = Valid() with { TerrainHeightMap = new float[32, 64] };
        Assert.False(p.Validate().IsValid);
    }

    [Fact]
    public void GeoTransformMustHaveSixElements()
    {
        var p = Valid() with { SourceGeoTransform = [1.0, 2.0] };
        Assert.False(p.Validate().IsValid);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~Backdrop"`
Expected: compile errors (types don't exist) — that is the failing state for a greenfield namespace.

- [ ] **Step 3: Implement the types**

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/PixelRect.cs
namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Integer rectangle in combined-GeoTIFF source pixel space (x → east/right, y = 0 at the TOP/north row).
///     Same space as <c>CropResult.OffsetX/OffsetY</c> in the app layer.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;    // exclusive
    public int Bottom => Y + Height;  // exclusive
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool ContainsRect(PixelRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
}
```

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropValidationResult.cs
namespace BeamNgTerrainPoc.Terrain.Backdrop;

public sealed class BackdropValidationResult
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool IsValid => Errors.Count == 0;
}
```

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropGenerationParameters.cs
using BeamNgTerrainPoc.Terrain.Models.RoadGeometry;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Explicit input contract for backdrop generation (spec §4/D7). The core consumes ONLY this —
///     no TerrainGenerationState, no MT_settings. A record class so tests can use `with`.
/// </summary>
public sealed record BackdropGenerationParameters
{
    // ---- Final terrain output (post quantization/erosion/smoothing) ----
    /// <summary>[y,x] row-major, y=0 = SOUTH edge, heights pre-base-height in meters.</summary>
    public required float[,] TerrainHeightMap { get; init; }
    public required int TerrainSizePixels { get; init; }
    public required float TerrainMetersPerPixel { get; init; }
    public required float TerrainBaseHeight { get; init; }
    /// <summary>Min elevation used for the terrain's own normalization (spec §7.3 vertical datum).</summary>
    public required double TerrainCropMinElevation { get; init; }

    // ---- Source raster space (combined GeoTIFF mosaic) ----
    /// <summary>Single GeoTIFF (or cached combined mosaic) covering the FULL uncropped source raster.</summary>
    public required string SourceGeoTiffPath { get; init; }
    public int? EpsgOverride { get; init; }
    public required int SourceRasterWidth { get; init; }
    public required int SourceRasterHeight { get; init; }
    /// <summary>6-parameter affine geotransform of the UNCROPPED mosaic (GDAL convention).</summary>
    public required double[] SourceGeoTransform { get; init; }
    public string? ProjectionWkt { get; init; }
    /// <summary>WGS84 bounds of the full mosaic — linear fallback for chunk bboxes when WKT is unusable.</summary>
    public GeoTiff.GeoBoundingBox? SourceWgs84Bounds { get; init; }

    /// <summary>Terrain crop rect in source pixels (the terrain selection).</summary>
    public required PixelRect TerrainRect { get; init; }
    /// <summary>Backdrop rect in source pixels; must contain <see cref="TerrainRect"/>.</summary>
    public required PixelRect BackdropRect { get; init; }

    // ---- Output ----
    public required string LevelPath { get; init; }
    public required string LevelName { get; init; }

    // ---- Tunables (defaults = spec §15) ----
    public double EdgeBandMeters { get; init; } = 200;
    public double MaxVerticalErrorNearMeters { get; init; } = 0.5;
    public double MaxVerticalErrorFarMeters { get; init; } = 8.0;
    public double ChunkTargetMeters { get; init; } = 2000;
    public double TexelDensityNearMPerPx { get; init; } = 1.0;
    public int MaxChunkTextureSize { get; init; } = 2048;
    public int MaxFarRasterDimension { get; init; } = 8192;
    public bool SeamSkirt { get; init; } = true;
    public double SeamSkirtDepthMeters { get; init; } = 2.0;

    /// <summary>V2 hook (spec §12) — unused in V1, reserved so the signature never changes.</summary>
    public UnifiedRoadNetwork? RoadNetwork { get; init; }

    /// <summary>Meters covered by one source pixel in X (derived from the terrain mapping, spec §7.4).</summary>
    public double MetersPerSourcePixelX => TerrainSizePixels * (double)TerrainMetersPerPixel / TerrainRect.Width;
    public double MetersPerSourcePixelY => TerrainSizePixels * (double)TerrainMetersPerPixel / TerrainRect.Height;

    public BackdropValidationResult Validate()
    {
        var result = new BackdropValidationResult();

        if (TerrainSizePixels <= 0 || TerrainMetersPerPixel <= 0)
            result.Errors.Add("Terrain size and meters-per-pixel must be positive.");
        if (TerrainHeightMap.GetLength(0) != TerrainSizePixels ||
            TerrainHeightMap.GetLength(1) != TerrainSizePixels)
            result.Errors.Add(
                $"TerrainHeightMap is {TerrainHeightMap.GetLength(1)}x{TerrainHeightMap.GetLength(0)}, expected {TerrainSizePixels}x{TerrainSizePixels}.");
        if (SourceGeoTransform is not { Length: 6 })
            result.Errors.Add("SourceGeoTransform must have exactly 6 elements.");
        if (TerrainRect.IsEmpty || BackdropRect.IsEmpty)
            result.Errors.Add("Terrain and backdrop rects must be non-empty.");
        if (EdgeBandMeters < 0 || ChunkTargetMeters <= 0 || TexelDensityNearMPerPx <= 0 ||
            MaxVerticalErrorNearMeters <= 0 || MaxVerticalErrorFarMeters <= 0 ||
            MaxChunkTextureSize < 256 || MaxFarRasterDimension < 256)
            result.Errors.Add("One or more tunables are out of range.");
        if (result.Errors.Count > 0)
            return result; // margin math below needs sane inputs

        var mosaic = new PixelRect(0, 0, SourceRasterWidth, SourceRasterHeight);
        if (!mosaic.ContainsRect(BackdropRect))
            result.Errors.Add("The backdrop rect must lie inside the loaded tile mosaic.");
        if (!BackdropRect.ContainsRect(TerrainRect))
            result.Errors.Add("The backdrop rect must fully contain the terrain rect.");
        if (result.Errors.Count > 0)
            return result;

        // Per-side margins in meters (spec §5: 0 allowed per side, but not on ALL sides;
        // 0 < margin < EdgeBandMeters → warning, band is clipped there).
        double west = (TerrainRect.X - BackdropRect.X) * MetersPerSourcePixelX;
        double east = (BackdropRect.Right - TerrainRect.Right) * MetersPerSourcePixelX;
        double north = (TerrainRect.Y - BackdropRect.Y) * MetersPerSourcePixelY;
        double south = (BackdropRect.Bottom - TerrainRect.Bottom) * MetersPerSourcePixelY;

        if (west <= 0 && east <= 0 && north <= 0 && south <= 0)
            result.Errors.Add("At least one side must have a margin > 0 — the backdrop ring is empty.");

        foreach (var (name, margin) in new[] { ("west", west), ("east", east), ("north", north), ("south", south) })
            if (margin > 0 && margin < EdgeBandMeters)
                result.Warnings.Add(
                    $"The {name} margin ({margin:F0} m) is narrower than the full-resolution edge band ({EdgeBandMeters:F0} m); the band is clipped there.");

        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~Backdrop"`
Expected: all Task-1 tests PASS. Also run the full suite once (`dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`) — count unchanged + new tests, zero failures.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/ BeamNgTerrainPoc.Tests/Backdrop/
git commit -m "feat(backdrop): core input contract, PixelRect and parameter validation"
```

---

### Task 2: `BackdropCoordinateMapper` — the horizontal datum

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropCoordinateMapper.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropCoordinateMapperTests.cs`

**Interfaces:**
- Consumes: `PixelRect` (Task 1).
- Produces: `BackdropCoordinateMapper` with `SourcePixelToWorld(double, double) → (double WorldX, double WorldY)`, `WorldToSourcePixel(double, double) → (double SrcX, double SrcY)`, `HalfSizeMeters`, `MetersPerSourcePixelX/Y` — consumed by height field (4), planner (5), generator (10).

**Why this exact math (spec §7.4, §14.2):** the terrain warp maps the crop rect *linearly* onto the terrain grid (`MapTileOverlayService.CreateWarpedOverlay`: `sourcePixelX = (x+0.5)·sourceWidth/outputSize`). The terrain's world extent is `TerrainSizePixels · MetersPerPixel` regardless of the crop's exact native meter size (selection uses `ceil` + clamp). So the ONLY consistent mapping is: source pixel → fraction of terrain rect → world meters. Any use of the native geotransform for world XY would diverge from the terrain by up to a pixel — exactly the cliff spec §14.2 forbids.

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropCoordinateMapperTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropCoordinateMapperTests
{
    // Terrain: 64 px @ 2 m/px = 128 m world span, half = 64. Crop rect 100x100 source px at (150, 100).
    private static BackdropCoordinateMapper Mapper() =>
        new(new PixelRect(150, 100, 100, 100), terrainSizePixels: 64, terrainMetersPerPixel: 2.0f);

    [Fact]
    public void TerrainRectCorners_MapToWorldBounds()
    {
        var m = Mapper();
        // NW source corner (150,100) → world (−half, +half); SE source corner (250,200) → (+half, −half).
        var nw = m.SourcePixelToWorld(150, 100);
        var se = m.SourcePixelToWorld(250, 200);
        Assert.Equal(-64.0, nw.WorldX, 10);
        Assert.Equal(64.0, nw.WorldY, 10);
        Assert.Equal(64.0, se.WorldX, 10);
        Assert.Equal(-64.0, se.WorldY, 10);
    }

    [Fact]
    public void TerrainRectCenter_MapsToOrigin()
    {
        var (wx, wy) = Mapper().SourcePixelToWorld(200, 150);
        Assert.Equal(0.0, wx, 10);
        Assert.Equal(0.0, wy, 10);
    }

    [Fact]
    public void RoundTrip_IsExact()
    {
        var m = Mapper();
        var (wx, wy) = m.SourcePixelToWorld(123.25, 77.5);
        var (sx, sy) = m.WorldToSourcePixel(wx, wy);
        Assert.Equal(123.25, sx, 9);
        Assert.Equal(77.5, sy, 9);
    }

    [Fact]
    public void MetersPerSourcePixel_ComesFromTerrainMapping_NotNativeSize()
    {
        // 64 px * 2 m = 128 m spread over 100 source px → 1.28 m per source px.
        var m = Mapper();
        Assert.Equal(1.28, m.MetersPerSourcePixelX, 10);
        Assert.Equal(1.28, m.MetersPerSourcePixelY, 10);
    }

    [Fact]
    public void SourceYIncreasesSouthward_WorldYDecreases()
    {
        var m = Mapper();
        var a = m.SourcePixelToWorld(200, 120);
        var b = m.SourcePixelToWorld(200, 130);
        Assert.True(b.WorldY < a.WorldY);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropCoordinateMapper"`
Expected: compile failure (type missing).

- [ ] **Step 3: Implement**

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropCoordinateMapper.cs
namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Source-pixel ↔ world mapping derived from the terrain crop rect (spec §7.4).
///     World origin = terrain center, X=East, Y=North (matches BeamNgCoordinateTransformer);
///     source pixel y grows southward (raster top = north).
///     This is the ONLY sanctioned horizontal datum for backdrop geometry (spec §14.2).
/// </summary>
public sealed class BackdropCoordinateMapper
{
    private readonly PixelRect _terrainRect;

    public double HalfSizeMeters { get; }
    public double MetersPerSourcePixelX { get; }
    public double MetersPerSourcePixelY { get; }

    public BackdropCoordinateMapper(PixelRect terrainRect, int terrainSizePixels, float terrainMetersPerPixel)
    {
        if (terrainRect.IsEmpty) throw new ArgumentException("Terrain rect must be non-empty.", nameof(terrainRect));
        if (terrainSizePixels <= 0) throw new ArgumentOutOfRangeException(nameof(terrainSizePixels));
        if (terrainMetersPerPixel <= 0) throw new ArgumentOutOfRangeException(nameof(terrainMetersPerPixel));

        _terrainRect = terrainRect;
        HalfSizeMeters = terrainSizePixels * (double)terrainMetersPerPixel / 2.0;
        MetersPerSourcePixelX = terrainSizePixels * (double)terrainMetersPerPixel / terrainRect.Width;
        MetersPerSourcePixelY = terrainSizePixels * (double)terrainMetersPerPixel / terrainRect.Height;
    }

    public (double WorldX, double WorldY) SourcePixelToWorld(double srcX, double srcY) =>
        ((srcX - _terrainRect.X) * MetersPerSourcePixelX - HalfSizeMeters,
         HalfSizeMeters - (srcY - _terrainRect.Y) * MetersPerSourcePixelY);

    public (double SrcX, double SrcY) WorldToSourcePixel(double worldX, double worldY) =>
        (_terrainRect.X + (worldX + HalfSizeMeters) / MetersPerSourcePixelX,
         _terrainRect.Y + (HalfSizeMeters - worldY) / MetersPerSourcePixelY);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropCoordinateMapper"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropCoordinateMapper.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropCoordinateMapperTests.cs
git commit -m "feat(backdrop): coordinate mapper - single source of the horizontal datum"
```

---

### Task 3: `BackdropRaster` — elevation window, bilinear sampling, nodata edge-extension

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropRaster.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropRasterTests.cs`

**Interfaces:**
- Consumes: `PixelRect` (Task 1).
- Produces:
  - `BackdropRaster(float[] elevations, int width, int height, PixelRect sourceWindow)` — `elevations` row-major, row 0 = `sourceWindow.Y` (north-most row), already nodata-filled.
  - `double SampleBilinearAtSource(double srcX, double srcY)` — sample by MOSAIC pixel coordinates (handles internal downsampling), clamped at window borders.
  - `bool ContainsSourcePoint(double srcX, double srcY)`
  - `static int FillNodataByEdgeExtension(float[] elevations, bool[] nodata, int width, int height)` — returns filled count; pure, used by the loader (Task 9, spec §6 nodata rule).

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropRasterTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropRasterTests
{
    [Fact]
    public void SampleBilinear_ReproducesGridValuesAtPixelCenters()
    {
        // 3x2 window at mosaic (10, 20); value = 100 + x + 10*y (local indices)
        var data = new float[] { 100, 101, 102, 110, 111, 112 };
        var raster = new BackdropRaster(data, 3, 2, new PixelRect(10, 20, 3, 2));
        // Mosaic pixel-center (10.5, 20.5) = local pixel (0,0) center
        Assert.Equal(100.0, raster.SampleBilinearAtSource(10.5, 20.5), 6);
        Assert.Equal(112.0, raster.SampleBilinearAtSource(12.5, 21.5), 6);
    }

    [Fact]
    public void SampleBilinear_InterpolatesBetweenCenters()
    {
        var data = new float[] { 0, 10, 0, 10 };
        var raster = new BackdropRaster(data, 2, 2, new PixelRect(0, 0, 2, 2));
        Assert.Equal(5.0, raster.SampleBilinearAtSource(1.0, 0.5), 6);  // halfway between (0,0) and (1,0) centers
    }

    [Fact]
    public void SampleBilinear_ClampsAtWindowBorder()
    {
        var data = new float[] { 1, 2, 3, 4 };
        var raster = new BackdropRaster(data, 2, 2, new PixelRect(5, 5, 2, 2));
        Assert.Equal(1.0, raster.SampleBilinearAtSource(4.0, 4.0), 6);   // outside NW → clamped to first pixel
        Assert.Equal(4.0, raster.SampleBilinearAtSource(99.0, 99.0), 6); // outside SE → clamped to last pixel
    }

    [Fact]
    public void DownsampledWindow_SamplesInMosaicCoordinates()
    {
        // 2x1 raster covering a 4x2 mosaic window: each raster pixel spans 2x2 mosaic pixels.
        var data = new float[] { 10, 20 };
        var raster = new BackdropRaster(data, 2, 1, new PixelRect(0, 0, 4, 2));
        Assert.Equal(10.0, raster.SampleBilinearAtSource(1.0, 1.0), 6);  // center of first coarse pixel
        Assert.Equal(20.0, raster.SampleBilinearAtSource(3.0, 1.0), 6);
        Assert.Equal(15.0, raster.SampleBilinearAtSource(2.0, 1.0), 6);  // midpoint
    }

    [Fact]
    public void FillNodata_UsesNearestValidSample()
    {
        // 4x1: [5, X, X, 9] → nearest fill: [5, 5, 9, 9]
        var data = new float[] { 5, 0, 0, 9 };
        var nodata = new[] { false, true, true, false };
        var filled = BackdropRaster.FillNodataByEdgeExtension(data, nodata, 4, 1);
        Assert.Equal(2, filled);
        Assert.Equal(new float[] { 5, 5, 9, 9 }, data);
    }

    [Fact]
    public void FillNodata_AllNodata_FillsZeroAndReportsAll()
    {
        var data = new float[] { 0, 0 };
        var nodata = new[] { true, true };
        var filled = BackdropRaster.FillNodataByEdgeExtension(data, nodata, 2, 1);
        Assert.Equal(2, filled); // nothing to extend from → values stay 0, all counted
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropRaster"`
Expected: compile failure.

- [ ] **Step 3: Implement**

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropRaster.cs
namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Elevation raster covering a window of the source mosaic, possibly downsampled
///     (far raster, spec §6). Row 0 = north-most row of the window. Values are absolute
///     DEM elevations in meters, nodata already filled by edge-extension.
/// </summary>
public sealed class BackdropRaster
{
    private readonly float[] _elevations; // row-major [y * Width + x]

    public int Width { get; }
    public int Height { get; }
    public PixelRect SourceWindow { get; }
    /// <summary>Mosaic pixels covered by one raster pixel (≥ 1 when downsampled).</summary>
    public double SourcePixelsPerCellX { get; }
    public double SourcePixelsPerCellY { get; }

    public BackdropRaster(float[] elevations, int width, int height, PixelRect sourceWindow)
    {
        if (elevations.Length != width * height)
            throw new ArgumentException($"Expected {width * height} samples, got {elevations.Length}.");
        _elevations = elevations;
        Width = width;
        Height = height;
        SourceWindow = sourceWindow;
        SourcePixelsPerCellX = (double)sourceWindow.Width / width;
        SourcePixelsPerCellY = (double)sourceWindow.Height / height;
    }

    public bool ContainsSourcePoint(double srcX, double srcY) =>
        srcX >= SourceWindow.X && srcX <= SourceWindow.Right &&
        srcY >= SourceWindow.Y && srcY <= SourceWindow.Bottom;

    /// <summary>Bilinear sample addressed in MOSAIC pixel coordinates; clamps outside the window.</summary>
    public double SampleBilinearAtSource(double srcX, double srcY)
    {
        // Convert to local raster grid coordinates, pixel centers at +0.5.
        var gx = (srcX - SourceWindow.X) / SourcePixelsPerCellX - 0.5;
        var gy = (srcY - SourceWindow.Y) / SourcePixelsPerCellY - 0.5;

        gx = Math.Clamp(gx, 0, Width - 1);
        gy = Math.Clamp(gy, 0, Height - 1);

        var x0 = (int)Math.Floor(gx);
        var y0 = (int)Math.Floor(gy);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = gx - x0;
        var fy = gy - y0;

        double v00 = _elevations[y0 * Width + x0];
        double v10 = _elevations[y0 * Width + x1];
        double v01 = _elevations[y1 * Width + x0];
        double v11 = _elevations[y1 * Width + x1];

        var top = v00 + (v10 - v00) * fx;
        var bottom = v01 + (v11 - v01) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>
    ///     Fills nodata cells with the value of the nearest valid cell (multi-source BFS,
    ///     4-neighborhood, O(n)). Returns the number of nodata cells (spec §6 warning %).
    /// </summary>
    public static int FillNodataByEdgeExtension(float[] elevations, bool[] nodata, int width, int height)
    {
        var total = 0;
        var queue = new Queue<int>();
        var pending = new bool[elevations.Length];

        for (var i = 0; i < elevations.Length; i++)
        {
            if (nodata[i]) { total++; pending[i] = true; }
        }
        if (total == 0 || total == elevations.Length)
            return total; // nothing to do, or nothing to extend from (values stay as-is)

        // Seed with valid cells adjacent to nodata.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            if (pending[i]) continue;
            if ((x > 0 && pending[i - 1]) || (x < width - 1 && pending[i + 1]) ||
                (y > 0 && pending[i - width]) || (y < height - 1 && pending[i + width]))
                queue.Enqueue(i);
        }

        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var x = i % width;
            var y = i / width;
            Span<int> neighbors = [x > 0 ? i - 1 : -1, x < width - 1 ? i + 1 : -1,
                                   y > 0 ? i - width : -1, y < height - 1 ? i + width : -1];
            foreach (var n in neighbors)
            {
                if (n < 0 || !pending[n]) continue;
                elevations[n] = elevations[i];
                pending[n] = false;
                queue.Enqueue(n);
            }
        }

        return total;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropRaster"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropRaster.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropRasterTests.cs
git commit -m "feat(backdrop): elevation raster window with bilinear sampling and nodata edge-extension"
```

---

### Task 4: `BackdropHeightField` — seam snap, delta band blend, vertical datum (spec §7)

This is the highest-risk logic in the feature (spec §14.1). It is built and fully tested before any mesh code exists.

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropHeightField.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropHeightFieldSeamTests.cs`

**Interfaces:**
- Consumes: `BackdropRaster` (3), `BackdropCoordinateMapper` (2).
- Produces:

```csharp
public sealed class BackdropHeightField
{
    public BackdropHeightField(
        BackdropRaster farRaster,
        IReadOnlyList<BackdropRaster> bandRasters,   // native-res strips around the terrain; may be empty
        float[,] terrainHeightMap,                    // [y,x], y=0 south, pre-base-height
        BackdropCoordinateMapper mapper,
        int terrainSizePixels, float terrainMetersPerPixel,
        float terrainBaseHeight, double terrainCropMinElevation,
        double edgeBandMeters);

    public double SampleDemElevation(double worldX, double worldY);   // absolute DEM meters (band raster preferred)
    public double SampleWorldZ(double worldX, double worldY);          // §7 final backdrop Z
    public double SignedDistanceToTerrainRect(double worldX, double worldY); // >0 outside, ≤0 on/inside seam
    internal double TerrainEdgeWorldZ(double worldX, double worldY);   // terrain edge height at clamped boundary point
}
```

Consumed by mesher (6–8) and generator (10).

**The §7 algorithm, exactly:**

1. `d = SignedDistanceToTerrainRect(p)` — Euclidean distance to the world square `±half` (`dx = max(|x|−half, 0)`, `dy = max(|y|−half, 0)`, `d = √(dx²+dy²)`; inside: `max(|x|,|y|) − half ≤ 0`).
2. `demZ(p) = SampleDemElevation(p) − TerrainCropMinElevation + TerrainBaseHeight` — **unclamped** (§7.3; distant peaks may exceed MaxHeight, that is a feature).
3. `d ≤ 0` → return `TerrainEdgeWorldZ(p)` — exact snap (§7.1).
4. `d ≥ EdgeBandMeters` (or band = 0) → return `demZ(p)`.
5. Else: `q = (clamp(x, ±half), clamp(y, ±half))` (nearest boundary point); `delta = TerrainEdgeWorldZ(q) − demZ(q)`; `w = 1 − smoothstep(d / EdgeBandMeters)` with `smoothstep(t) = t²(3−2t)`; return `demZ(p) + delta·w` (§7.2 — the *difference field* fades, not just the boundary line).

`TerrainEdgeWorldZ(q)`: terrain pixel coords `px = (qx + half)/u`, `py = (qy + half)/u`, both clamped to `[0, size−1]`, bilinear over `terrainHeightMap[y, x]` (y index = py because y=0 is south), `+ TerrainBaseHeight`. `SampleDemElevation`: world → source pixel via mapper; first band raster whose window contains the point wins, else far raster.

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropHeightFieldSeamTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropHeightFieldSeamTests
{
    private const int Size = 16;          // terrain 16 px
    private const float U = 2.0f;         // 2 m/px → span 32 m, half = 16
    private const float BaseHeight = 50f;
    private const double CropMin = 400.0;
    private const double Band = 8.0;

    /// <summary>Terrain rect at source (100,100,16,16); backdrop raster covers (84,84,48,48).</summary>
    private static BackdropHeightField Build(
        Func<int, int, float> terrainHeight,      // (x, y[south-up]) → pre-base-height meters
        Func<double, double, double> demElevation) // (srcX, srcY) → absolute DEM meters
    {
        var terrain = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            terrain[y, x] = terrainHeight(x, y);

        var window = new PixelRect(84, 84, 48, 48);
        var far = new float[48 * 48];
        for (var y = 0; y < 48; y++)
        for (var x = 0; x < 48; x++)
            far[y * 48 + x] = (float)demElevation(window.X + x + 0.5, window.Y + y + 0.5);

        var mapper = new BackdropCoordinateMapper(new PixelRect(100, 100, Size, Size), Size, U);
        return new BackdropHeightField(
            new BackdropRaster(far, 48, 48, window),
            bandRasters: [],
            terrain, mapper, Size, U, BaseHeight, CropMin, Band);
    }

    [Fact]
    public void SeamVertices_TakeExactTerrainEdgeHeights()
    {
        // Terrain = tilted plane h(x,y) = 3 + 0.5x; DEM constant 420 (deliberately mismatched).
        var field = Build((x, y) => 3f + 0.5f * x, (_, _) => 420.0);

        // East seam at worldX = +16: terrain edge column x = Size−1 → h = 3 + 0.5*15 = 10.5.
        for (var iy = 0; iy <= Size; iy++)
        {
            var worldY = iy * U - 16.0;
            var z = field.SampleWorldZ(16.0, worldY);
            Assert.Equal(10.5 + BaseHeight, z, 9);   // EXACT snap, §7.1
        }
        // West seam at worldX = −16: column x = 0 → h = 3.
        Assert.Equal(3.0 + BaseHeight, field.SampleWorldZ(-16.0, 0.0), 9);
    }

    [Fact]
    public void BeyondBand_IsPureDemWithDatumFormula()
    {
        var field = Build((_, _) => 0f, (_, _) => 470.0);
        // d = 10 > band 8 east of the seam.
        var z = field.SampleWorldZ(16.0 + 10.0, 0.0);
        Assert.Equal(470.0 - CropMin + BaseHeight, z, 9);   // §7.3: dem − cropMin + base
    }

    [Fact]
    public void BandBlend_FadesDeltaMonotonically()
    {
        // Terrain edge z = 12 + base; DEM z = 470 − 400 + 50 = 120 → delta = (12+50) − 120 = −58.
        var field = Build((_, _) => 12f, (_, _) => 470.0);
        var demZ = 470.0 - CropMin + BaseHeight;
        double? previousError = null;
        for (var i = 0; i <= 8; i++)
        {
            var d = Band * i / 8.0;
            var z = field.SampleWorldZ(16.0 + d, 0.0);
            var error = Math.Abs(z - demZ);           // remaining influence of the terrain delta
            if (previousError.HasValue)
                Assert.True(error <= previousError.Value + 1e-9,
                    $"|z−dem| must not increase across the band (d={d}: {error} > {previousError})");
            previousError = error;
        }
        // Ends: at d=0 exact terrain edge; at d=band exact dem.
        Assert.Equal(12.0 + BaseHeight, field.SampleWorldZ(16.0, 0.0), 9);
        Assert.Equal(demZ, field.SampleWorldZ(16.0 + Band, 0.0), 9);
    }

    [Fact]
    public void MatchingDemAndTerrain_ProducesNoBandDistortion()
    {
        // DEM plane in absolute meters whose normalized form equals the terrain heights:
        // dem(src) = CropMin + 0.5 * (srcX − 100) * (terrain has h = 0.5 * x, u == 1 src px per terrain px … careful:
        // terrain px x ↔ srcX = 100 + x, so dem − cropMin at terrain sample = 0.5x = h. Datum formula ⇒ zero delta.
        var field = Build((x, _) => 0.5f * x,
            (srcX, _) => CropMin + 0.5 * (srcX - 100.0) * 1.0);
        // Inside the band the blend must be a no-op within bilinear tolerance.
        // NOTE the terrain edge column sits at srcX=115 (sample x=15) while the seam is at srcX=116 —
        // the DEM keeps rising on that last half-cell, terrain edge is clamped flat, so compare with
        // the DEM value, allowing the documented last-half-cell tolerance of 0.5*u*slope.
        var z = field.SampleWorldZ(16.0 + Band / 2.0, 0.0);
        var demZ = field.SampleDemElevation(16.0 + Band / 2.0, 0.0) - CropMin + BaseHeight;
        Assert.True(Math.Abs(z - demZ) <= 0.5 * U * 0.5 + 1e-6,
            $"delta blend should be ≈ no-op when DEM matches terrain (|{z} − {demZ}|)");
    }

    [Fact]
    public void BandRaster_PreferredOverFarRaster()
    {
        var terrain = new float[Size, Size];
        var mapper = new BackdropCoordinateMapper(new PixelRect(100, 100, Size, Size), Size, U);
        var farWindow = new PixelRect(84, 84, 48, 48);
        var far = Enumerable.Repeat(500f, 48 * 48).ToArray();
        // Band strip east of the terrain: src (116, 96, 8, 24) with different value.
        var stripWindow = new PixelRect(116, 96, 8, 24);
        var strip = Enumerable.Repeat(600f, 8 * 24).ToArray();

        var field = new BackdropHeightField(
            new BackdropRaster(far, 48, 48, farWindow),
            [new BackdropRaster(strip, 8, 24, stripWindow)],
            terrain, mapper, Size, U, BaseHeight, CropMin, Band);

        Assert.Equal(600.0, field.SampleDemElevation(16.0 + 4.0, 0.0), 6);   // inside strip
        Assert.Equal(500.0, field.SampleDemElevation(-16.0 - 4.0, 0.0), 6);  // west side → far raster
    }

    [Fact]
    public void SignedDistance_EuclideanOutside_NegativeInside()
    {
        var field = Build((_, _) => 0f, (_, _) => CropMin);
        Assert.Equal(5.0, field.SignedDistanceToTerrainRect(21.0, 0.0), 9);
        Assert.Equal(Math.Sqrt(50), field.SignedDistanceToTerrainRect(21.0, 21.0), 9); // corner: √(5²+5²)
        Assert.True(field.SignedDistanceToTerrainRect(0.0, 0.0) < 0);
        Assert.Equal(0.0, field.SignedDistanceToTerrainRect(16.0, 8.0), 9);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropHeightFieldSeam"`
Expected: compile failure.

- [ ] **Step 3: Implement**

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropHeightField.cs
namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Height source for the backdrop mesh implementing the seam rules of spec §7:
///     exact terrain-edge snap at distance 0, delta-field blend across the edge band,
///     unclamped DEM with the −cropMin+baseHeight datum beyond the band.
/// </summary>
public sealed class BackdropHeightField
{
    private readonly BackdropRaster _farRaster;
    private readonly IReadOnlyList<BackdropRaster> _bandRasters;
    private readonly float[,] _terrainHeightMap;
    private readonly BackdropCoordinateMapper _mapper;
    private readonly int _terrainSizePixels;
    private readonly double _u;              // terrain meters per pixel
    private readonly double _half;
    private readonly float _terrainBaseHeight;
    private readonly double _cropMinElevation;
    private readonly double _edgeBandMeters;

    public BackdropHeightField(
        BackdropRaster farRaster,
        IReadOnlyList<BackdropRaster> bandRasters,
        float[,] terrainHeightMap,
        BackdropCoordinateMapper mapper,
        int terrainSizePixels, float terrainMetersPerPixel,
        float terrainBaseHeight, double terrainCropMinElevation,
        double edgeBandMeters)
    {
        _farRaster = farRaster;
        _bandRasters = bandRasters;
        _terrainHeightMap = terrainHeightMap;
        _mapper = mapper;
        _terrainSizePixels = terrainSizePixels;
        _u = terrainMetersPerPixel;
        _half = terrainSizePixels * (double)terrainMetersPerPixel / 2.0;
        _terrainBaseHeight = terrainBaseHeight;
        _cropMinElevation = terrainCropMinElevation;
        _edgeBandMeters = edgeBandMeters;
    }

    public double SignedDistanceToTerrainRect(double worldX, double worldY)
    {
        var dx = Math.Abs(worldX) - _half;
        var dy = Math.Abs(worldY) - _half;
        if (dx <= 0 && dy <= 0)
            return Math.Max(dx, dy);                       // inside/on boundary: ≤ 0
        var ox = Math.Max(dx, 0);
        var oy = Math.Max(dy, 0);
        return Math.Sqrt(ox * ox + oy * oy);               // Euclidean outside (correct at corners)
    }

    public double SampleDemElevation(double worldX, double worldY)
    {
        var (srcX, srcY) = _mapper.WorldToSourcePixel(worldX, worldY);
        foreach (var strip in _bandRasters)
            if (strip.ContainsSourcePoint(srcX, srcY))
                return strip.SampleBilinearAtSource(srcX, srcY);
        return _farRaster.SampleBilinearAtSource(srcX, srcY);
    }

    public double SampleWorldZ(double worldX, double worldY)
    {
        var d = SignedDistanceToTerrainRect(worldX, worldY);
        if (d <= 0)
            return TerrainEdgeWorldZ(worldX, worldY);       // §7.1 exact snap

        var demZ = SampleDemElevation(worldX, worldY) - _cropMinElevation + _terrainBaseHeight;
        if (_edgeBandMeters <= 0 || d >= _edgeBandMeters)
            return demZ;                                    // §7.3 pure DEM, unclamped

        // §7.2: fade the (terrainEdge − demAtSeam) delta across the band.
        var qx = Math.Clamp(worldX, -_half, _half);
        var qy = Math.Clamp(worldY, -_half, _half);
        var demZAtSeam = SampleDemElevation(qx, qy) - _cropMinElevation + _terrainBaseHeight;
        var delta = TerrainEdgeWorldZ(qx, qy) - demZAtSeam;

        var t = d / _edgeBandMeters;
        var w = 1.0 - (t * t * (3.0 - 2.0 * t));            // 1 − smoothstep(t)
        return demZ + delta * w;
    }

    /// <summary>
    ///     Terrain height at the boundary point nearest to (worldX, worldY), bilinear along the
    ///     final terrain output heightmap. The outermost sample row/column (index size−1) covers
    ///     the seam line at ±half — see the "last half-cell" watch item in the plan header.
    /// </summary>
    internal double TerrainEdgeWorldZ(double worldX, double worldY)
    {
        var qx = Math.Clamp(worldX, -_half, _half);
        var qy = Math.Clamp(worldY, -_half, _half);

        var px = Math.Clamp((qx + _half) / _u, 0, _terrainSizePixels - 1);
        var py = Math.Clamp((qy + _half) / _u, 0, _terrainSizePixels - 1);

        var x0 = (int)Math.Floor(px);
        var y0 = (int)Math.Floor(py);
        var x1 = Math.Min(x0 + 1, _terrainSizePixels - 1);
        var y1 = Math.Min(y0 + 1, _terrainSizePixels - 1);
        var fx = px - x0;
        var fy = py - y0;

        double v00 = _terrainHeightMap[y0, x0];
        double v10 = _terrainHeightMap[y0, x1];
        double v01 = _terrainHeightMap[y1, x0];
        double v11 = _terrainHeightMap[y1, x1];

        var south = v00 + (v10 - v00) * fx;
        var north = v01 + (v11 - v01) * fx;
        return south + (north - south) * fy + _terrainBaseHeight;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~Backdrop"`
Expected: PASS (all Backdrop tests so far).

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropHeightField.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropHeightFieldSeamTests.cs
git commit -m "feat(backdrop): height field with seam snap, delta band blend and vertical datum (spec section 7)"
```

---

### Task 5: `BackdropChunkPlanner` — lattice-aligned chunk grid, texture sizes, chunk bboxes

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkDefinition.cs`
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkPlanner.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropChunkPlannerTests.cs`

**Interfaces:**
- Consumes: `BackdropGenerationParameters` (1), `BackdropCoordinateMapper` (2), `GeoBoundingBox` (`BeamNgTerrainPoc.Terrain.GeoTiff`, `[JsonConstructor] GeoBoundingBox(minLon, minLat, maxLon, maxLat)`, static `TransformToWgs84(GeoBoundingBox, string wkt, bool quiet)`).
- Produces:

```csharp
public sealed class BackdropChunkDefinition
{
    public required int Cx { get; init; }                 // column index, 0 = west-most
    public required int Cy { get; init; }                 // row index, 0 = south-most
    // Lattice rect (units of u, origin at terrain SW corner; iy grows north):
    public required int LatticeX { get; init; }
    public required int LatticeY { get; init; }
    public required int LatticeWidth { get; init; }
    public required int LatticeHeight { get; init; }
    // Derived world rect in meters:
    public double WorldMinX { get; init; }
    public double WorldMinY { get; init; }
    public double WorldMaxX { get; init; }
    public double WorldMaxY { get; init; }
    // Source-pixel rect (double precision; for the texture warp + MtSettings):
    public required double SourceRectX { get; init; }
    public required double SourceRectY { get; init; }
    public required double SourceRectWidth { get; init; }
    public required double SourceRectHeight { get; init; }
    public GeoTiff.GeoBoundingBox? Wgs84Bounds { get; init; }   // null when neither WKT nor mosaic bbox usable
    public required string DaeFileName { get; init; }           // $"backdrop_{Cx}_{Cy}.dae"
    public required string TextureFileName { get; init; }       // $"backdrop_{Cx}_{Cy}.png"
    public required string MaterialName { get; init; }          // $"mt_backdrop_{Cx}_{Cy}"
    public required int TextureSize { get; init; }              // pow2, clamped [256, MaxChunkTextureSize]
    public required double DistanceToTerrainMeters { get; init; } // chunk-center distance to terrain rect
}

public sealed class BackdropChunkPlan
{
    public required IReadOnlyList<BackdropChunkDefinition> Chunks { get; init; }
    public required double MaxMarginMeters { get; init; }          // for tolerance/texel lerps
    // Backdrop rect snapped inward to the lattice (whole-ring bounds used by the mesher):
    public required int LatticeMinX { get; init; }
    public required int LatticeMinY { get; init; }
    public required int LatticeMaxX { get; init; }
    public required int LatticeMaxY { get; init; }
}

public static class BackdropChunkPlanner
{
    public static BackdropChunkPlan Plan(BackdropGenerationParameters parameters);
}
```

**Algorithm:**
1. Convert the backdrop rect corners to world via the mapper, then to **lattice** (`ix = (worldX + half)/u`), snapping **inward** (`ceil` on min, `floor` on max) so the ring never exceeds the mosaic. Terrain rect = lattice `[0, size]²`.
2. Grid lines per axis = backdrop min/max + terrain rect edges (`0`, `size`) + interval subdivisions: each of the three intervals per axis (west margin, terrain span, east margin — terrain span only contributes its own edges as ring cells never overlap it; but the N/S margin cells above/below the terrain DO span the terrain columns, so the terrain-span interval is also subdivided) is split into `ceil(intervalMeters / ChunkTargetMeters)` integer-lattice cells: `baseWidth = intervalLattice / count`, first `intervalLattice % count` cells get `+1` (deterministic integer partition).
3. Cells fully inside the terrain rect (`lattice [0,size]²`) are dropped. Because terrain edges are grid lines, every cell is either fully inside or fully outside.
4. Per chunk: world rect from lattice; source rect via `mapper.WorldToSourcePixel` of the NW/SE world corners (note Y flip: world max Y → source min Y); WGS84 bounds: `PixelToNative(gt, …)` on the 4 source-rect corners → native bbox → `GeoBoundingBox.TransformToWgs84(nativeBbox, wkt, quiet: true)`; when WKT missing/unusable, fall back to linear interpolation of `SourceWgs84Bounds` by pixel fractions (same math as `CropAnchorSelector.RecalculateSelectionBoundingBox`, `CropAnchorSelector.razor.cs:311`); if that is null too → `Wgs84Bounds = null`.
5. Texture size (spec §10): `dNorm = clamp(DistanceToTerrainMeters / MaxMarginMeters, 0, 1)`; `density = TexelDensityNearMPerPx · (1 + 3·dNorm)`; `TextureSize = clamp(NextPow2(chunkMaxExtentMeters / density), 256, MaxChunkTextureSize)`. `DistanceToTerrainMeters` = Euclidean distance of the chunk center to the terrain rect (0 for band-adjacent chunks). `MaxMarginMeters` = max of the four margins (≥ 1 to avoid /0).

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropChunkPlannerTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropChunkPlannerTests
{
    /// <summary>Terrain 64 px @ 2 m (span 128 m, lattice [0,64]); backdrop margins 32 px = 64 m each side.</summary>
    private static BackdropGenerationParameters Params(double chunkTargetMeters = 40) => new()
    {
        TerrainHeightMap = new float[64, 64],
        TerrainSizePixels = 64,
        TerrainMetersPerPixel = 2.0f,
        TerrainBaseHeight = 0f,
        TerrainCropMinElevation = 0.0,
        SourceGeoTiffPath = "unused.tif",
        SourceRasterWidth = 200,
        SourceRasterHeight = 200,
        SourceGeoTransform = [500000, 2, 0, 5400000, 0, -2],
        ProjectionWkt = null,
        SourceWgs84Bounds = new BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox(7.0, 50.0, 7.4, 50.4),
        TerrainRect = new PixelRect(68, 68, 64, 64),
        BackdropRect = new PixelRect(36, 36, 128, 128),
        LevelPath = "unused",
        LevelName = "test_level",
        ChunkTargetMeters = chunkTargetMeters,
    };

    [Fact]
    public void GridLines_IncludeTerrainBoundary()
    {
        var plan = BackdropChunkPlanner.Plan(Params());
        // No chunk crosses the terrain rect edges: each chunk is fully inside or outside lattice [0,64]².
        foreach (var c in plan.Chunks)
        {
            var crossesX = c.LatticeX < 0 && c.LatticeX + c.LatticeWidth > 0
                        || c.LatticeX < 64 && c.LatticeX + c.LatticeWidth > 64;
            var crossesY = c.LatticeY < 0 && c.LatticeY + c.LatticeHeight > 0
                        || c.LatticeY < 64 && c.LatticeY + c.LatticeHeight > 64;
            // Crossing an edge is allowed only OUTSIDE the perpendicular terrain span (corner strips
            // never overlap the terrain interior) — the real invariant is: no overlap with (0,64)².
            var overlapsTerrain = c.LatticeX < 64 && c.LatticeX + c.LatticeWidth > 0 &&
                                  c.LatticeY < 64 && c.LatticeY + c.LatticeHeight > 0;
            Assert.False(overlapsTerrain, $"chunk {c.Cx},{c.Cy} overlaps the terrain rect");
            _ = crossesX; _ = crossesY;
        }
    }

    [Fact]
    public void Chunks_TileTheRingExactly()
    {
        var plan = BackdropChunkPlanner.Plan(Params());
        var ringArea = (double)(plan.LatticeMaxX - plan.LatticeMinX) * (plan.LatticeMaxY - plan.LatticeMinY)
                       - 64.0 * 64.0;
        var chunkArea = plan.Chunks.Sum(c => (double)c.LatticeWidth * c.LatticeHeight);
        Assert.Equal(ringArea, chunkArea, 6);
    }

    [Fact]
    public void ChunkTarget_BoundsCellSize()
    {
        var plan = BackdropChunkPlanner.Plan(Params(chunkTargetMeters: 40)); // 64 m margins → 2 cells of 32 px? no: 40 m target → ceil(64/40)=2 cells per margin
        Assert.All(plan.Chunks, c =>
        {
            Assert.True(c.LatticeWidth * 2.0 <= 40 + 2.0, $"chunk width {c.LatticeWidth * 2.0} m exceeds target+1cell");
            Assert.True(c.LatticeHeight * 2.0 <= 40 + 2.0);
        });
    }

    [Fact]
    public void NamesAndIndices_AreStable()
    {
        var plan = BackdropChunkPlanner.Plan(Params());
        var first = plan.Chunks[0];
        Assert.Equal($"backdrop_{first.Cx}_{first.Cy}.dae", first.DaeFileName);
        Assert.Equal($"backdrop_{first.Cx}_{first.Cy}.png", first.TextureFileName);
        Assert.Equal($"mt_backdrop_{first.Cx}_{first.Cy}", first.MaterialName);
        // Deterministic ordering: sorted by (Cy, Cx).
        var sorted = plan.Chunks.OrderBy(c => c.Cy).ThenBy(c => c.Cx).ToList();
        Assert.Equal(sorted.Select(c => (c.Cx, c.Cy)), plan.Chunks.Select(c => (c.Cx, c.Cy)));
    }

    [Fact]
    public void TextureSize_IsPow2Clamped_AndCoarsensWithDistance()
    {
        var p = Params() with { TexelDensityNearMPerPx = 1.0, MaxChunkTextureSize = 2048 };
        var plan = BackdropChunkPlanner.Plan(p);
        foreach (var c in plan.Chunks)
        {
            Assert.True(c.TextureSize is >= 256 and <= 2048);
            Assert.Equal(0, c.TextureSize & (c.TextureSize - 1));   // power of two
        }
        // A touching chunk (d=0) must not have a smaller texture than the farthest chunk of equal size.
        var near = plan.Chunks.Where(c => c.DistanceToTerrainMeters == 0).Max(c => c.TextureSize);
        var far = plan.Chunks.Max(c => c.DistanceToTerrainMeters);
        var farthest = plan.Chunks.First(c => c.DistanceToTerrainMeters == far);
        Assert.True(near >= farthest.TextureSize);
    }

    [Fact]
    public void SourceRect_RoundTripsThroughMapper()
    {
        var p = Params();
        var plan = BackdropChunkPlanner.Plan(p);
        var mapper = new BackdropCoordinateMapper(p.TerrainRect, p.TerrainSizePixels, p.TerrainMetersPerPixel);
        var c = plan.Chunks[0];
        var (srcX, srcY) = mapper.WorldToSourcePixel(c.WorldMinX, c.WorldMaxY);  // NW world corner → NW source corner
        Assert.Equal(c.SourceRectX, srcX, 6);
        Assert.Equal(c.SourceRectY, srcY, 6);
    }

    [Fact]
    public void Wgs84Fallback_UsesLinearMosaicInterpolation_WhenNoWkt()
    {
        var plan = BackdropChunkPlanner.Plan(Params());   // ProjectionWkt = null, SourceWgs84Bounds set
        Assert.All(plan.Chunks, c => Assert.NotNull(c.Wgs84Bounds));
        var c0 = plan.Chunks[0];
        Assert.True(c0.Wgs84Bounds!.MinLongitude >= 7.0 && c0.Wgs84Bounds.MaxLongitude <= 7.4);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropChunkPlanner"`
Expected: compile failure.

- [ ] **Step 3: Implement `BackdropChunkPlanner`**

Core of the implementation (the DTOs follow the interface block above verbatim):

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkPlanner.cs
using BeamNgTerrainPoc.Terrain.GeoTiff;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

public static class BackdropChunkPlanner
{
    public static BackdropChunkPlan Plan(BackdropGenerationParameters p)
    {
        var mapper = new BackdropCoordinateMapper(p.TerrainRect, p.TerrainSizePixels, p.TerrainMetersPerPixel);
        var u = (double)p.TerrainMetersPerPixel;
        var half = mapper.HalfSizeMeters;
        var size = p.TerrainSizePixels;

        // 1. Backdrop rect → lattice, snapped inward.
        var (wMinX, wMaxY) = mapper.SourcePixelToWorld(p.BackdropRect.X, p.BackdropRect.Y);
        var (wMaxX, wMinY) = mapper.SourcePixelToWorld(p.BackdropRect.Right, p.BackdropRect.Bottom);
        var latMinX = (int)Math.Ceiling((wMinX + half) / u - 1e-9);
        var latMinY = (int)Math.Ceiling((wMinY + half) / u - 1e-9);
        var latMaxX = (int)Math.Floor((wMaxX + half) / u + 1e-9);
        var latMaxY = (int)Math.Floor((wMaxY + half) / u + 1e-9);

        // 2. Grid lines per axis: margins + terrain span, each partitioned to ~ChunkTargetMeters.
        var xLines = BuildAxisLines(latMinX, latMaxX, terrainMin: 0, terrainMax: size, u, p.ChunkTargetMeters);
        var yLines = BuildAxisLines(latMinY, latMaxY, terrainMin: 0, terrainMax: size, u, p.ChunkTargetMeters);

        var margins = new[]
        {
            Math.Max(0, 0 - latMinX) * u, Math.Max(0, latMaxX - size) * u,
            Math.Max(0, 0 - latMinY) * u, Math.Max(0, latMaxY - size) * u
        };
        var maxMargin = Math.Max(1.0, margins.Max());

        var chunks = new List<BackdropChunkDefinition>();
        for (var cy = 0; cy < yLines.Count - 1; cy++)
        for (var cx = 0; cx < xLines.Count - 1; cx++)
        {
            int lx = xLines[cx], ly = yLines[cy];
            int lw = xLines[cx + 1] - lx, lh = yLines[cy + 1] - ly;
            if (lw <= 0 || lh <= 0) continue;
            var insideTerrain = lx >= 0 && ly >= 0 && lx + lw <= size && ly + lh <= size;
            if (insideTerrain) continue;                            // 3. drop terrain cells

            chunks.Add(CreateDefinition(p, mapper, u, half, maxMargin, cx, cy, lx, ly, lw, lh));
        }

        return new BackdropChunkPlan
        {
            Chunks = chunks,                                       // built (cy, cx)-ordered → stable
            MaxMarginMeters = maxMargin,
            LatticeMinX = latMinX, LatticeMinY = latMinY,
            LatticeMaxX = latMaxX, LatticeMaxY = latMaxY
        };
    }

    /// <summary>Sorted grid lines: interval [min,max] split at terrain edges, then each piece partitioned.</summary>
    private static List<int> BuildAxisLines(int min, int max, int terrainMin, int terrainMax,
        double u, double chunkTargetMeters)
    {
        var lines = new List<int> { min };
        void Partition(int from, int to)
        {
            var lattice = to - from;
            if (lattice <= 0) return;
            var count = Math.Max(1, (int)Math.Ceiling(lattice * u / chunkTargetMeters));
            var baseWidth = lattice / count;
            var remainder = lattice % count;
            var pos = from;
            for (var i = 0; i < count; i++)
            {
                pos += baseWidth + (i < remainder ? 1 : 0);
                lines.Add(pos);
            }
        }
        Partition(min, Math.Min(max, Math.Max(min, terrainMin)));                 // west/south margin
        if (terrainMin > min && terrainMin < max && !lines.Contains(terrainMin)) lines.Add(terrainMin);
        Partition(Math.Max(min, terrainMin), Math.Min(max, terrainMax));          // terrain span
        if (terrainMax > min && terrainMax < max && !lines.Contains(terrainMax)) lines.Add(terrainMax);
        Partition(Math.Max(min, Math.Min(max, terrainMax)), max);                 // east/north margin
        return lines.Distinct().OrderBy(v => v).ToList();
    }

    private static BackdropChunkDefinition CreateDefinition(BackdropGenerationParameters p,
        BackdropCoordinateMapper mapper, double u, double half, double maxMargin,
        int cx, int cy, int lx, int ly, int lw, int lh)
    {
        double wMinX = lx * u - half, wMinY = ly * u - half;
        double wMaxX = (lx + lw) * u - half, wMaxY = (ly + lh) * u - half;

        // Chunk-center distance to the terrain rect (Euclidean, 0 if touching).
        double centerX = (wMinX + wMaxX) / 2, centerY = (wMinY + wMaxY) / 2;
        double dx = Math.Max(Math.Abs(centerX) - half, 0), dy = Math.Max(Math.Abs(centerY) - half, 0);
        // Touching chunks: center may sit outside but the chunk borders the rect → use min corner distance 0
        var touches = wMinX <= half && wMaxX >= -half && wMinY <= half && wMaxY >= -half
                      && (wMinX <= -half || wMaxX >= half || wMinY <= -half || wMaxY >= half)
                      && (Math.Abs(wMinX) <= half || Math.Abs(wMaxX) <= half ||
                          Math.Abs(wMinY) <= half || Math.Abs(wMaxY) <= half);
        var distance = touches && (lx <= p.TerrainSizePixels && lx + lw >= 0 && ly <= p.TerrainSizePixels && ly + lh >= 0)
            ? DistanceRectToRect(wMinX, wMinY, wMaxX, wMaxY, half)
            : Math.Sqrt(dx * dx + dy * dy);

        var (srcNwX, srcNwY) = mapper.WorldToSourcePixel(wMinX, wMaxY);
        var (srcSeX, srcSeY) = mapper.WorldToSourcePixel(wMaxX, wMinY);

        var extent = Math.Max(wMaxX - wMinX, wMaxY - wMinY);
        var dNorm = Math.Clamp(distance / maxMargin, 0.0, 1.0);
        var density = p.TexelDensityNearMPerPx * (1.0 + 3.0 * dNorm);
        var texture = Math.Clamp(NextPow2((int)Math.Ceiling(extent / density)), 256, p.MaxChunkTextureSize);

        return new BackdropChunkDefinition
        {
            Cx = cx, Cy = cy,
            LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
            WorldMinX = wMinX, WorldMinY = wMinY, WorldMaxX = wMaxX, WorldMaxY = wMaxY,
            SourceRectX = srcNwX, SourceRectY = srcNwY,
            SourceRectWidth = srcSeX - srcNwX, SourceRectHeight = srcSeY - srcNwY,
            Wgs84Bounds = ComputeWgs84Bounds(p, srcNwX, srcNwY, srcSeX, srcSeY),
            DaeFileName = $"backdrop_{cx}_{cy}.dae",
            TextureFileName = $"backdrop_{cx}_{cy}.png",
            MaterialName = $"mt_backdrop_{cx}_{cy}",
            TextureSize = texture,
            DistanceToTerrainMeters = distance
        };
    }

    /// <summary>Euclidean distance between an axis-aligned rect and the centered terrain square (0 when touching).</summary>
    private static double DistanceRectToRect(double minX, double minY, double maxX, double maxY, double half)
    {
        var dx = Math.Max(Math.Max(-half - maxX, minX - half), 0);
        var dy = Math.Max(Math.Max(-half - maxY, minY - half), 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static GeoBoundingBox? ComputeWgs84Bounds(BackdropGenerationParameters p,
        double srcMinX, double srcMinY, double srcMaxX, double srcMaxY)
    {
        var gt = p.SourceGeoTransform;
        if (!string.IsNullOrWhiteSpace(p.ProjectionWkt))
        {
            (double X, double Y) Native(double px, double py) =>
                (gt[0] + px * gt[1] + py * gt[2], gt[3] + px * gt[4] + py * gt[5]);
            var corners = new[] { Native(srcMinX, srcMinY), Native(srcMaxX, srcMinY),
                                  Native(srcMinX, srcMaxY), Native(srcMaxX, srcMaxY) };
            var native = new GeoBoundingBox(
                corners.Min(c => c.X), corners.Min(c => c.Y),
                corners.Max(c => c.X), corners.Max(c => c.Y));
            var wgs84 = GeoBoundingBox.TransformToWgs84(native, p.ProjectionWkt, quiet: true);
            if (wgs84 != null) return wgs84;
        }
        // Linear fallback over the mosaic bbox (same math as CropAnchorSelector.RecalculateSelectionBoundingBox).
        if (p.SourceWgs84Bounds is { } bbox && p.SourceRasterWidth > 0 && p.SourceRasterHeight > 0)
        {
            var lonRange = bbox.MaxLongitude - bbox.MinLongitude;
            var latRange = bbox.MaxLatitude - bbox.MinLatitude;
            return new GeoBoundingBox(
                bbox.MinLongitude + lonRange * (srcMinX / p.SourceRasterWidth),
                bbox.MaxLatitude - latRange * (srcMaxY / p.SourceRasterHeight),
                bbox.MinLongitude + lonRange * (srcMaxX / p.SourceRasterWidth),
                bbox.MaxLatitude - latRange * (srcMinY / p.SourceRasterHeight));
        }
        return null;
    }

    private static int NextPow2(int v)
    {
        var result = 256;
        while (result < v && result < 1 << 24) result <<= 1;
        return result;
    }
}
```

Note: `GeoBoundingBox.TransformToWgs84` requires GDAL — the planner test passes `ProjectionWkt = null` to stay GDAL-free; the WKT path is covered by the generator integration test (Task 10) which creates a real GeoTIFF.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropChunkPlanner"`
Expected: PASS. Adjust the `touches` special-case only if the distance test fails for band-adjacent chunks — the invariant that matters: chunks bordering the terrain rect report `DistanceToTerrainMeters == 0`.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkDefinition.cs BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkPlanner.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropChunkPlannerTests.cs
git commit -m "feat(backdrop): lattice-aligned chunk planner with texture-size formula"
```

---

### Task 6: Quadtree refinement — restricted quadtree, tolerance lerp, importance sources, shared edge subdivision

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropMesherOptions.cs` (+ `IBackdropImportanceSource`, `EdgeBandImportanceSource`)
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropEdgeSubdivider.cs`
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs` (refinement part; triangulation added in Task 7)
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropQuadtreeMesherTests.cs`

**Interfaces:**
- Consumes: `BackdropHeightField` (4), `BackdropChunkDefinition` (5).
- Produces:

```csharp
public sealed class BackdropMesherOptions
{
    public double MaxVerticalErrorNearMeters { get; init; } = 0.5;
    public double MaxVerticalErrorFarMeters { get; init; } = 8.0;
    public double EdgeBandMeters { get; init; } = 200;
    public required double MaxMarginMeters { get; init; }
    public int ErrorProbeGridSize { get; init; } = 4;        // (n+1)² samples per cell
    public bool SeamSkirt { get; init; } = true;
    public double SeamSkirtDepthMeters { get; init; } = 2.0;
    public required double LatticeUnitMeters { get; init; }  // u
    public required double HalfSizeMeters { get; init; }     // lattice origin offset
}

public interface IBackdropImportanceSource
{
    /// <summary>Max allowed cell size (meters) for a cell intersecting this source, or null = no constraint.</summary>
    double? RequiredMaxCellSizeMeters(double worldMinX, double worldMinY, double worldMaxX, double worldMaxY);
}

/// <summary>V1 contributor: forces subdivision to the lattice unit inside the edge band (spec §8).</summary>
public sealed class EdgeBandImportanceSource(double halfSizeMeters, double edgeBandMeters, double latticeUnitMeters)
    : IBackdropImportanceSource
{
    public double? RequiredMaxCellSizeMeters(double minX, double minY, double maxX, double maxY)
    {
        // Distance of the cell rect to the terrain square; inside band → full resolution.
        var dx = Math.Max(Math.Max(-halfSizeMeters - maxX, minX - halfSizeMeters), 0);
        var dy = Math.Max(Math.Max(-halfSizeMeters - maxY, minY - halfSizeMeters), 0);
        var d = Math.Sqrt(dx * dx + dy * dy);
        return d < edgeBandMeters ? latticeUnitMeters : null;
    }
}

public static class BackdropEdgeSubdivider
{
    /// <summary>
    ///     Deterministic 1D subdivision of a chunk-border segment given in lattice coords.
    ///     Returns sorted lattice positions INCLUDING both endpoints. Bisection at floor((a+b)/2)
    ///     while the predicate demands refinement — identical for both adjacent chunks by construction.
    /// </summary>
    public static IReadOnlyList<int> Subdivide(
        int fixedCoord, bool verticalEdge, int from, int to,
        BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importance);
}

public sealed partial class BackdropQuadtreeMesher   // partial: Task 7 adds triangulation
{
    public BackdropQuadtreeMesher(BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importanceSources);
    internal IReadOnlyList<LeafCell> RefineChunk(BackdropChunkDefinition chunk);   // exposed to tests via InternalsVisibleTo
}
internal readonly record struct LeafCell(int X, int Y, int Width, int Height);     // lattice units
```

**Refinement rules (spec §8):**
- `tolerance(d) = lerp(near, far, clamp(d / MaxMarginMeters, 0, 1))`, `d` = cell-rect distance to terrain square.
- `verticalError(cell)` = max |`field.SampleWorldZ` − bilinear interpolation of the 4 corner Z| over an `(n+1)×(n+1)` probe grid (n = `ErrorProbeGridSize`).
- Split while `cellSizeMeters > importanceLimit` **or** (`cell lattice size > 1` **and** `verticalError > tolerance`).
- Split position: `floor((a+b)/2)` per axis; cells of lattice size 1 never split. Degenerate axes (width or height 1) split only on the other axis (binary split).
- **Restriction for quality:** after refinement, balance so adjacent leaves differ by ≤ 1 level (level = `ceil(log2(maxLatticeExtent))`); the Task-7 fan triangulation is crack-free even without it, but balancing keeps fan sizes and normal quality bounded — assert it in tests as the spec §13 invariant.
- **Chunk borders:** when a node's edge lies on the chunk border, the split coordinate on that axis MUST be a member of the shared `BackdropEdgeSubdivider` set for that border (choose the member closest to the midpoint; if none lies strictly inside, do not split along that axis). This keeps every border vertex of both neighbor chunks inside the shared set → bitwise identical borders (spec §8 "computed once per edge").

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropQuadtreeMesherTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropQuadtreeMesherTests
{
    private const int Size = 32;
    private const float U = 1.0f;
    private const double Half = 16.0;

    private static (BackdropHeightField Field, BackdropMesherOptions Options, List<IBackdropImportanceSource> Importance)
        Setup(Func<double, double, double> demElevation, double band = 4.0, double maxMargin = 64.0)
    {
        var terrain = new float[Size, Size];
        var window = new PixelRect(0, 0, 160, 160);   // terrain rect at (64,64,32,32) inside a 160² mosaic
        var far = new float[160 * 160];
        var mapper = new BackdropCoordinateMapper(new PixelRect(64, 64, Size, Size), Size, U);
        for (var y = 0; y < 160; y++)
        for (var x = 0; x < 160; x++)
        {
            var (wx, wy) = mapper.SourcePixelToWorld(x + 0.5, y + 0.5);
            far[y * 160 + x] = (float)demElevation(wx, wy);
        }
        var field = new BackdropHeightField(new BackdropRaster(far, 160, 160, window), [],
            terrain, mapper, Size, U, terrainBaseHeight: 0f, terrainCropMinElevation: 0.0, band);
        var options = new BackdropMesherOptions
        {
            EdgeBandMeters = band, MaxMarginMeters = maxMargin,
            LatticeUnitMeters = U, HalfSizeMeters = Half
        };
        var importance = new List<IBackdropImportanceSource> { new EdgeBandImportanceSource(Half, band, U) };
        return (field, options, importance);
    }

    private static BackdropChunkDefinition Chunk(int lx, int ly, int lw, int lh, double distance = 0) => new()
    {
        Cx = 0, Cy = 0, LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
        WorldMinX = lx * U - Half, WorldMinY = ly * U - Half,
        WorldMaxX = (lx + lw) * U - Half, WorldMaxY = (ly + lh) * U - Half,
        SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 0, SourceRectHeight = 0,
        DaeFileName = "backdrop_0_0.dae", TextureFileName = "backdrop_0_0.png",
        MaterialName = "mt_backdrop_0_0", TextureSize = 256, DistanceToTerrainMeters = distance
    };

    [Fact]
    public void PlanarDem_CollapsesToCoarseLeaves_OutsideBand()
    {
        var (field, options, importance) = Setup((x, y) => 100.0 + 0.01 * x);   // near-plane → tiny error
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        // Chunk far east of the band: lattice (48..64, 0..16) → world x in [32, 48], d ≥ 16 > band 4.
        var leaves = mesher.RefineChunk(Chunk(48, 0, 16, 16, distance: 16));
        Assert.True(leaves.Count <= 4, $"plane should not refine (got {leaves.Count} leaves)");
    }

    [Fact]
    public void EdgeBand_ForcesUnitCells()
    {
        var (field, options, importance) = Setup((_, _) => 100.0);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        // Chunk touching the east seam: lattice (32..48, 0..16); band = 4 m → cells with worldX in [16,20] must be 1×1.
        var leaves = mesher.RefineChunk(Chunk(32, 0, 16, 16));
        foreach (var leaf in leaves)
        {
            var minX = leaf.X * U - Half;
            if (minX < 16.0 + 4.0 - 1e-9 && leaf.X < 32 + 4)
                Assert.True(leaf.Width == 1 && leaf.Height == 1,
                    $"band leaf at lattice ({leaf.X},{leaf.Y}) is {leaf.Width}x{leaf.Height}");
        }
    }

    [Fact]
    public void SineDem_RefinesUntilErrorBound_Holds()
    {
        var (field, options, importance) = Setup((x, y) => 100.0 + 6.0 * Math.Sin(x / 3.0) * Math.Cos(y / 3.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var chunk = Chunk(48, 0, 16, 16, distance: 16);
        var leaves = mesher.RefineChunk(chunk);
        // Verify the vertical error bound per leaf against the tolerance at its distance (spec §13).
        foreach (var leaf in leaves)
        {
            double minX = leaf.X * U - Half, minY = leaf.Y * U - Half;
            double maxX = minX + leaf.Width * U, maxY = minY + leaf.Height * U;
            if (leaf.Width == 1 && leaf.Height == 1) continue;      // cannot refine further
            var tol = ToleranceAt(field, options, minX, minY, maxX, maxY);
            var err = ProbeError(field, minX, minY, maxX, maxY, 4);
            Assert.True(err <= tol + 1e-6, $"leaf error {err:F3} > tolerance {tol:F3}");
        }
    }

    [Fact]
    public void RestrictedQuadtree_AdjacentLeafLevelsDifferByAtMostOne()
    {
        var (field, options, importance) = Setup((x, y) => 100.0 + 6.0 * Math.Sin(x / 2.5));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var leaves = mesher.RefineChunk(Chunk(32, 0, 32, 32));
        foreach (var a in leaves)
        foreach (var b in leaves)
        {
            if (!SharesEdge(a, b)) continue;
            var la = Level(a); var lb = Level(b);
            Assert.True(Math.Abs(la - lb) <= 1, $"leaves {a} and {b} differ by {Math.Abs(la - lb)} levels");
        }
    }

    [Fact]
    public void EdgeSubdivider_IsDeterministic_AndFullResOnSeam()
    {
        var (field, options, importance) = Setup((_, _) => 100.0);
        // Terrain seam border (fixed x = lattice 32, i.e. worldX = +16): full res → every lattice point.
        var s1 = BackdropEdgeSubdivider.Subdivide(32, verticalEdge: true, 0, 32, field, options, importance);
        var s2 = BackdropEdgeSubdivider.Subdivide(32, verticalEdge: true, 0, 32, field, options, importance);
        Assert.Equal(s1, s2);
        Assert.Equal(33, s1.Count);                                  // 0..32 inclusive
        Assert.Equal(Enumerable.Range(0, 33), s1);
    }

    private static bool SharesEdge(LeafCell a, LeafCell b) =>
        (a.X + a.Width == b.X || b.X + b.Width == a.X) && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height
        || (a.Y + a.Height == b.Y || b.Y + b.Height == a.Y) && a.X < b.X + b.Width && b.X < a.X + a.Width;

    private static int Level(LeafCell c) => (int)Math.Ceiling(Math.Log2(Math.Max(c.Width, c.Height)));

    private static double ToleranceAt(BackdropHeightField field, BackdropMesherOptions o,
        double minX, double minY, double maxX, double maxY)
    {
        var d = Math.Max(0, Math.Min(Math.Min(field.SignedDistanceToTerrainRect(minX, minY),
            field.SignedDistanceToTerrainRect(maxX, minY)), Math.Min(
            field.SignedDistanceToTerrainRect(minX, maxY), field.SignedDistanceToTerrainRect(maxX, maxY))));
        var t = Math.Clamp(d / o.MaxMarginMeters, 0, 1);
        return o.MaxVerticalErrorNearMeters + (o.MaxVerticalErrorFarMeters - o.MaxVerticalErrorNearMeters) * t;
    }

    private static double ProbeError(BackdropHeightField field,
        double minX, double minY, double maxX, double maxY, int n)
    {
        double z00 = field.SampleWorldZ(minX, minY), z10 = field.SampleWorldZ(maxX, minY);
        double z01 = field.SampleWorldZ(minX, maxY), z11 = field.SampleWorldZ(maxX, maxY);
        var worst = 0.0;
        for (var j = 0; j <= n; j++)
        for (var i = 0; i <= n; i++)
        {
            double fx = (double)i / n, fy = (double)j / n;
            var plane = (z00 * (1 - fx) + z10 * fx) * (1 - fy) + (z01 * (1 - fx) + z11 * fx) * fy;
            var actual = field.SampleWorldZ(minX + fx * (maxX - minX), minY + fy * (maxY - minY));
            worst = Math.Max(worst, Math.Abs(actual - plane));
        }
        return worst;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropQuadtreeMesher"`
Expected: compile failure. Note `BeamNgTerrainPoc` already has `InternalsVisibleTo("BeamNgTerrainPoc.Tests")` (`BeamNgTerrainPoc.csproj`), so `internal RefineChunk`/`LeafCell` are test-visible.

- [ ] **Step 3: Implement refinement**

`BackdropMesherOptions.cs`, `IBackdropImportanceSource`, `EdgeBandImportanceSource`, `BackdropEdgeSubdivider` exactly as in the interface block. Mesher refinement core:

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs  (refinement half)
namespace BeamNgTerrainPoc.Terrain.Backdrop;

internal readonly record struct LeafCell(int X, int Y, int Width, int Height);

public sealed partial class BackdropQuadtreeMesher
{
    private readonly BackdropHeightField _field;
    private readonly BackdropMesherOptions _options;
    private readonly IReadOnlyList<IBackdropImportanceSource> _importance;

    public BackdropQuadtreeMesher(BackdropHeightField field, BackdropMesherOptions options,
        IReadOnlyList<IBackdropImportanceSource> importanceSources)
    {
        _field = field;
        _options = options;
        _importance = importanceSources;
    }

    internal IReadOnlyList<LeafCell> RefineChunk(BackdropChunkDefinition chunk)
    {
        // Shared border subdivisions of this chunk's four borders (Task 6 border rule).
        var west = BackdropEdgeSubdivider.Subdivide(chunk.LatticeX, true,
            chunk.LatticeY, chunk.LatticeY + chunk.LatticeHeight, _field, _options, _importance);
        var east = BackdropEdgeSubdivider.Subdivide(chunk.LatticeX + chunk.LatticeWidth, true,
            chunk.LatticeY, chunk.LatticeY + chunk.LatticeHeight, _field, _options, _importance);
        var south = BackdropEdgeSubdivider.Subdivide(chunk.LatticeY, false,
            chunk.LatticeX, chunk.LatticeX + chunk.LatticeWidth, _field, _options, _importance);
        var north = BackdropEdgeSubdivider.Subdivide(chunk.LatticeY + chunk.LatticeHeight, false,
            chunk.LatticeX, chunk.LatticeX + chunk.LatticeWidth, _field, _options, _importance);

        var leaves = new List<LeafCell>();
        Refine(new LeafCell(chunk.LatticeX, chunk.LatticeY, chunk.LatticeWidth, chunk.LatticeHeight),
            chunk, west, east, south, north, leaves);
        Balance(leaves);
        return leaves;
    }

    private void Refine(LeafCell cell, BackdropChunkDefinition chunk,
        IReadOnlyList<int> west, IReadOnlyList<int> east,
        IReadOnlyList<int> south, IReadOnlyList<int> north, List<LeafCell> leaves)
    {
        var u = _options.LatticeUnitMeters;
        var half = _options.HalfSizeMeters;
        double minX = cell.X * u - half, minY = cell.Y * u - half;
        double maxX = minX + cell.Width * u, maxY = minY + cell.Height * u;
        var cellSize = Math.Max(cell.Width, cell.Height) * u;

        var needSplit = false;
        foreach (var source in _importance)
            if (source.RequiredMaxCellSizeMeters(minX, minY, maxX, maxY) is { } limit && cellSize > limit + 1e-9)
                needSplit = true;
        if (!needSplit && (cell.Width > 1 || cell.Height > 1))
            needSplit = ProbeVerticalError(minX, minY, maxX, maxY) > ToleranceAt(minX, minY, maxX, maxY);
        if (!needSplit || (cell.Width <= 1 && cell.Height <= 1))
        {
            leaves.Add(cell);
            return;
        }

        // X-split creates new vertices on the cell's south/north edges; if such an edge lies on a
        // chunk border, the split coordinate must belong to that border's shared subdivision.
        var splitX = ChooseSplit(cell.X, cell.X + cell.Width,
            mustMatch: CollectBorderSets(
                onSouthBorder: cell.Y == chunk.LatticeY ? south : null,
                onNorthBorder: cell.Y + cell.Height == chunk.LatticeY + chunk.LatticeHeight ? north : null));
        var splitY = ChooseSplit(cell.Y, cell.Y + cell.Height,
            mustMatch: CollectBorderSets(
                onSouthBorder: cell.X == chunk.LatticeX ? west : null,
                onNorthBorder: cell.X + cell.Width == chunk.LatticeX + chunk.LatticeWidth ? east : null));

        // splitX/splitY are null when that axis cannot split (size 1, or border set has no interior point).
        if (splitX == null && splitY == null) { leaves.Add(cell); return; }

        foreach (var child in Split(cell, splitX, splitY))
            Refine(child, chunk, west, east, south, north, leaves);
    }
}
```

Implementation notes for the engineer (all inside the same file):
- `ChooseSplit(from, to, mustMatch)`: default candidate `floor((from+to)/2)`; `mustMatch` (built by `CollectBorderSets`, a trivial null-filtering combiner returning the intersection of the passed subdivision sets restricted to `(from, to)`) is null/empty when no chunk border is involved → use the default candidate; otherwise snap the candidate to the member of `mustMatch` closest to the midpoint that lies strictly inside `(from, to)`. Return `null` when `to − from < 2` or a required match set has no interior member.
- `Split(cell, splitX, splitY)`: 4 children when both non-null, 2 when one, deterministic child order (SW, SE, NW, NE).
- `ProbeVerticalError` / `ToleranceAt`: same math as the test helpers above (probe grid `_options.ErrorProbeGridSize`, distance = min corner distance to the terrain square, clamped ≥ 0).
- `Balance(leaves)`: iterate until fixpoint — any leaf whose neighbor (shared edge) is more than 1 level finer gets split (re-running the border-aware `ChooseSplit`); levels per the test's `Level()` definition. A simple worklist over a `Dictionary<(int, int), List<LeafCell>>` spatial hash is fine at these counts.
- `BackdropEdgeSubdivider.Subdivide`: recursive bisection — for segment `[a, b]` on the fixed line, evaluate the same predicate (importance limit using a zero-thickness rect inflated by `u/2`, vertical error via 1D chord: max |`SampleWorldZ(mid…)` − lerp(endpoints)| probed at `ErrorProbeGridSize` points); if refinement demanded and `b − a ≥ 2`, split at `floor((a+b)/2)` and recurse both halves; collect endpoints into a `SortedSet<int>`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropQuadtreeMesher"`
Expected: PASS. Also run the full Backdrop filter — everything green.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/ BeamNgTerrainPoc.Tests/Backdrop/BackdropQuadtreeMesherTests.cs
git commit -m "feat(backdrop): restricted quadtree refinement with importance sources and shared border subdivision"
```

---

### Task 7: Crack-free triangulation — center-fan with neighbor-vertex inclusion

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs` (triangulation half of the partial class)
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropTriangulationTests.cs`

**Interfaces:**
- Consumes: `RefineChunk` leaves (6), `BackdropHeightField` (4), `BeamNG.Procedural3D` `Mesh`/`Vertex`/`Triangle` (`Mesh { Name, Vertices: List<Vertex>, Triangles: List<Triangle>, MaterialName }`, `Vertex(Vector3 position, Vector3 normal, Vector2 uv)`, `Triangle(int v0, int v1, int v2)`).
- Produces:

```csharp
public sealed class BackdropChunkMeshResult
{
    public required Mesh VisualMesh { get; init; }     // triangulated surface (+ skirt after Task 8)
    public required Mesh CollisionMesh { get; init; }  // surface only, never the skirt
    public required int LeafCount { get; init; }
    public required int SurfaceTriangleCount { get; init; }  // triangles excluding skirt
}
public sealed partial class BackdropQuadtreeMesher
{
    public BackdropChunkMeshResult MeshChunk(BackdropChunkDefinition chunk);
}
```

**Triangulation scheme (why fans):** every leaf emits a triangle fan from its center to its ordered boundary-vertex loop. The loop contains the 4 corners **plus every lattice vertex on its edges that any neighboring leaf (same chunk) or the shared chunk-border subdivision contributes**. A finer neighbor's corner is therefore always part of the coarser leaf's loop → no T-vertices, watertight by construction, no transition-pattern case analysis. Unit cells (1×1 with no extra edge vertices) skip the center and emit 2 triangles.

**Determinism:** leaves are iterated in the deterministic refinement order; vertices are appended to an ordered list with a `Dictionary<(int ix, int iy), int>` for lattice-vertex dedup; fan centers are appended per leaf. No hash-order iteration anywhere (spec §13 "deterministic output").

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropTriangulationTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;
using BeamNG.Procedural3D.Core;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropTriangulationTests
{
    // Reuse the Setup/Chunk helpers of BackdropQuadtreeMesherTests (copy them in; keep tests self-contained).
    private const int Size = 32;
    private const float U = 1.0f;
    private const double Half = 16.0;

    private static (BackdropHeightField Field, BackdropMesherOptions Options, List<IBackdropImportanceSource> Importance)
        Setup(Func<double, double, double> demElevation, double band = 4.0)
    {
        var terrain = new float[Size, Size];
        var window = new PixelRect(0, 0, 160, 160);
        var far = new float[160 * 160];
        var mapper = new BackdropCoordinateMapper(new PixelRect(64, 64, Size, Size), Size, U);
        for (var y = 0; y < 160; y++)
        for (var x = 0; x < 160; x++)
        {
            var (wx, wy) = mapper.SourcePixelToWorld(x + 0.5, y + 0.5);
            far[y * 160 + x] = (float)demElevation(wx, wy);
        }
        var field = new BackdropHeightField(new BackdropRaster(far, 160, 160, window), [],
            terrain, mapper, Size, U, 0f, 0.0, band);
        var options = new BackdropMesherOptions
            { EdgeBandMeters = band, MaxMarginMeters = 64.0, LatticeUnitMeters = U, HalfSizeMeters = Half };
        return (field, options, [new EdgeBandImportanceSource(Half, band, U)]);
    }

    private static BackdropChunkDefinition Chunk(int lx, int ly, int lw, int lh, int cx = 0, int cy = 0) => new()
    {
        Cx = cx, Cy = cy, LatticeX = lx, LatticeY = ly, LatticeWidth = lw, LatticeHeight = lh,
        WorldMinX = lx * U - Half, WorldMinY = ly * U - Half,
        WorldMaxX = (lx + lw) * U - Half, WorldMaxY = (ly + lh) * U - Half,
        SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 0, SourceRectHeight = 0,
        DaeFileName = $"backdrop_{cx}_{cy}.dae", TextureFileName = $"backdrop_{cx}_{cy}.png",
        MaterialName = $"mt_backdrop_{cx}_{cy}", TextureSize = 256, DistanceToTerrainMeters = 0
    };

    /// <summary>Every interior edge must be used exactly twice with opposite direction (watertight).</summary>
    private static void AssertWatertight(Mesh mesh, int surfaceTriangles)
    {
        var edgeUse = new Dictionary<(int A, int B), int>();
        for (var t = 0; t < surfaceTriangles; t++)
        {
            var tri = mesh.Triangles[t];
            foreach (var (a, b) in new[] { (tri.V0, tri.V1), (tri.V1, tri.V2), (tri.V2, tri.V0) })
            {
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        }
        Assert.DoesNotContain(edgeUse, kv => kv.Value > 2);   // >2 = non-manifold; 1 = boundary edge (allowed)
    }

    [Fact]
    public void MeshChunk_IsWatertight_OnBumpyTerrain()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 5 * Math.Sin(x / 2.0) * Math.Cos(y / 3.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 32, 32));
        Assert.True(result.SurfaceTriangleCount > 0);
        AssertWatertight(result.VisualMesh, result.SurfaceTriangleCount);
    }

    [Fact]
    public void MeshChunk_NoTriangleInsideTerrainRect()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 32, 32));   // chunk east of terrain
        for (var t = 0; t < result.SurfaceTriangleCount; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var c = (result.VisualMesh.Vertices[tri.V0].Position +
                     result.VisualMesh.Vertices[tri.V1].Position +
                     result.VisualMesh.Vertices[tri.V2].Position) / 3f;
            Assert.False(Math.Abs(c.X) < Half - 1e-3 && Math.Abs(c.Y) < Half - 1e-3,
                $"triangle centroid {c} lies inside the terrain rect");
        }
    }

    [Fact]
    public void MeshChunk_CoversChunkAreaExactly()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 4 * Math.Sin(x / 2.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 16, 16));
        double area = 0;
        for (var t = 0; t < result.SurfaceTriangleCount; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var a = result.VisualMesh.Vertices[tri.V0].Position;
            var b = result.VisualMesh.Vertices[tri.V1].Position;
            var c = result.VisualMesh.Vertices[tri.V2].Position;
            area += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) / 2.0;
        }
        Assert.Equal(16.0 * 16.0, area, 3);   // XY-projected area = lattice area (ring cutout exact, spec §13)
    }

    [Fact]
    public void MeshChunk_TrianglesWoundCounterClockwise_SeenFromAbove()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(40, 8, 8, 8));
        for (var t = 0; t < result.SurfaceTriangleCount; t++)
        {
            var tri = result.VisualMesh.Triangles[t];
            var a = result.VisualMesh.Vertices[tri.V0].Position;
            var b = result.VisualMesh.Vertices[tri.V1].Position;
            var c = result.VisualMesh.Vertices[tri.V2].Position;
            var cross = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            Assert.True(cross > 0, $"triangle {t} wound clockwise");
        }
    }

    [Fact]
    public void MeshChunk_IsDeterministic()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 5 * Math.Sin(x / 2.0) * Math.Cos(y / 3.0));
        var m1 = new BackdropQuadtreeMesher(field, options, importance).MeshChunk(Chunk(32, 0, 32, 32));
        var m2 = new BackdropQuadtreeMesher(field, options, importance).MeshChunk(Chunk(32, 0, 32, 32));
        Assert.Equal(m1.VisualMesh.Vertices.Count, m2.VisualMesh.Vertices.Count);
        for (var i = 0; i < m1.VisualMesh.Vertices.Count; i++)
            Assert.Equal(m1.VisualMesh.Vertices[i].Position, m2.VisualMesh.Vertices[i].Position); // bitwise
        Assert.Equal(m1.VisualMesh.Triangles.Select(t => (t.V0, t.V1, t.V2)),
                     m2.VisualMesh.Triangles.Select(t => (t.V0, t.V1, t.V2)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropTriangulation"`
Expected: compile failure (`MeshChunk` missing).

- [ ] **Step 3: Implement triangulation**

```csharp
// BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs  (triangulation half, same partial class)
using System.Numerics;
using BeamNG.Procedural3D.Core;

public sealed partial class BackdropQuadtreeMesher
{
    public BackdropChunkMeshResult MeshChunk(BackdropChunkDefinition chunk)
    {
        var leaves = RefineChunk(chunk);

        // Index all leaf-corner lattice vertices by column and row for edge-vertex lookup.
        var byColumn = new Dictionary<int, SortedSet<int>>();
        var byRow = new Dictionary<int, SortedSet<int>>();
        void Register(int ix, int iy)
        {
            if (!byColumn.TryGetValue(ix, out var col)) byColumn[ix] = col = [];
            col.Add(iy);
            if (!byRow.TryGetValue(iy, out var row)) byRow[iy] = row = [];
            row.Add(ix);
        }
        foreach (var leaf in leaves)
        {
            Register(leaf.X, leaf.Y); Register(leaf.X + leaf.Width, leaf.Y);
            Register(leaf.X, leaf.Y + leaf.Height); Register(leaf.X + leaf.Width, leaf.Y + leaf.Height);
        }
        // Chunk-border vertices from the shared subdivision (both neighbors see the same set):
        foreach (var iy in BackdropEdgeSubdivider.Subdivide(chunk.LatticeX, true,
                     chunk.LatticeY, chunk.LatticeY + chunk.LatticeHeight, _field, _options, _importance))
            Register(chunk.LatticeX, iy);
        // … same three calls for east / south / north borders …

        var mesh = new Mesh { Name = Path.GetFileNameWithoutExtension(chunk.DaeFileName), MaterialName = chunk.MaterialName };
        var vertexLookup = new Dictionary<(int, int), int>();
        int GetLatticeVertex(int ix, int iy)
        {
            if (vertexLookup.TryGetValue((ix, iy), out var idx)) return idx;
            var wx = ix * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var wy = iy * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var z = _field.SampleWorldZ(wx, wy);
            idx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vertex(new Vector3((float)wx, (float)wy, (float)z)));  // normal/uv in Task 8
            vertexLookup[(ix, iy)] = idx;
            return idx;
        }

        foreach (var leaf in leaves)
        {
            // Boundary loop counter-clockwise starting at SW corner:
            var loop = new List<int>();
            void EdgePoints(bool vertical, int fixedCoord, int from, int to, bool ascending)
            {
                var set = vertical ? byColumn[fixedCoord] : byRow[fixedCoord];
                var range = set.GetViewBetween(Math.Min(from, to), Math.Max(from, to));
                var points = ascending ? range.ToList() : range.Reverse().ToList();
                points.RemoveAt(points.Count - 1);          // end corner belongs to the next edge
                foreach (var v in points)
                    loop.Add(vertical ? GetLatticeVertex(fixedCoord, v) : GetLatticeVertex(v, fixedCoord));
            }
            EdgePoints(false, leaf.Y, leaf.X, leaf.X + leaf.Width, ascending: true);              // south edge W→E
            EdgePoints(true, leaf.X + leaf.Width, leaf.Y, leaf.Y + leaf.Height, ascending: true); // east edge S→N
            EdgePoints(false, leaf.Y + leaf.Height, leaf.X + leaf.Width, leaf.X, ascending: false); // north E→W
            EdgePoints(true, leaf.X, leaf.Y + leaf.Height, leaf.Y, ascending: false);             // west N→S

            if (loop.Count == 4)
            {
                mesh.Triangles.Add(new Triangle(loop[0], loop[1], loop[2]));
                mesh.Triangles.Add(new Triangle(loop[0], loop[2], loop[3]));
                continue;
            }
            // Fan from the leaf center (unique vertex, not on the lattice dictionary).
            var cx = (leaf.X + leaf.X + leaf.Width) * 0.5 * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var cy = (leaf.Y + leaf.Y + leaf.Height) * 0.5 * _options.LatticeUnitMeters - _options.HalfSizeMeters;
            var centerIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vertex(new Vector3((float)cx, (float)cy, (float)_field.SampleWorldZ(cx, cy))));
            for (var i = 0; i < loop.Count; i++)
                mesh.Triangles.Add(new Triangle(centerIdx, loop[i], loop[(i + 1) % loop.Count]));
        }

        var surfaceTriangles = mesh.Triangles.Count;
        var collision = new Mesh { Name = "Colmesh-1" };
        collision.Vertices.AddRange(mesh.Vertices);
        collision.Triangles.AddRange(mesh.Triangles);      // snapshot BEFORE the skirt is appended (Task 8)

        // Task 8 extends this method: normals, UVs, seam skirt.
        return new BackdropChunkMeshResult
        {
            VisualMesh = mesh, CollisionMesh = collision,
            LeafCount = leaves.Count, SurfaceTriangleCount = surfaceTriangles
        };
    }
}
```

Winding check: with the loop ordered CCW (south W→E, east S→N, north E→W, west N→S) and fan `(center, loop[i], loop[i+1])`, cross products are positive; the two-triangle case `(0,1,2)/(0,2,3)` likewise. If the winding test fails, flip the loop direction — do not flip triangles individually.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropTriangulation"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropTriangulationTests.cs
git commit -m "feat(backdrop): crack-free center-fan triangulation with exact ring cutout"
```

---

### Task 8: Mesh finishing — seam vertices, chunk-border identity, skirt, normals, UVs

**Files:**
- Modify: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs`
- Test: extend `BeamNgTerrainPoc.Tests/Backdrop/BackdropTriangulationTests.cs`

**Interfaces:**
- Consumes/produces: unchanged (`MeshChunk` fills `Vertex.Normal`/`Vertex.UV`, appends the skirt to `VisualMesh` only).

- [ ] **Step 1: Write the failing tests** (append to `BackdropTriangulationTests`)

```csharp
    [Fact]
    public void SeamVertices_MatchTerrainEdgeExactly_AtPixelCorners()
    {
        // Terrain with a distinctive edge profile; DEM deliberately offset.
        var terrain = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
            terrain[y, x] = 2f * y;                       // ramp northward
        var mapper = new BackdropCoordinateMapper(new PixelRect(64, 64, Size, Size), Size, U);
        var far = Enumerable.Repeat(500f, 160 * 160).ToArray();
        var field = new BackdropHeightField(new BackdropRaster(far, 160, 160, new PixelRect(0, 0, 160, 160)),
            [], terrain, mapper, Size, U, 10f, 400.0, 4.0);
        var options = new BackdropMesherOptions
            { EdgeBandMeters = 4.0, MaxMarginMeters = 64.0, LatticeUnitMeters = U, HalfSizeMeters = Half };
        var mesher = new BackdropQuadtreeMesher(field, options, [new EdgeBandImportanceSource(Half, 4.0, U)]);

        var result = mesher.MeshChunk(Chunk(32, 0, 16, 32));   // chunk hugging the east seam, full terrain height
        // Every vertex with X == +half must sit exactly at TerrainEdgeWorldZ (spec §7.1) and at integer lattice Y.
        var seamVertices = result.VisualMesh.Vertices.Take(result.CollisionMesh.Vertices.Count)
            .Where(v => Math.Abs(v.Position.X - Half) < 1e-6).ToList();
        Assert.True(seamVertices.Count >= Size + 1, "expected a seam vertex per terrain pixel corner");
        foreach (var v in seamVertices)
        {
            var expected = field.TerrainEdgeWorldZ(Half, v.Position.Y);
            Assert.Equal(expected, v.Position.Z, 4);
            var lattice = (v.Position.Y + Half) / U;
            Assert.Equal(Math.Round(lattice), lattice, 6);
        }
    }

    [Fact]
    public void AdjacentChunks_ShareBitwiseIdenticalBorderVertices()
    {
        var (field, options, importance) = Setup((x, y) => 100 + 5 * Math.Sin(x / 2.0) * Math.Cos(y / 3.0));
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var left = mesher.MeshChunk(Chunk(32, 0, 16, 32, cx: 0));
        var right = mesher.MeshChunk(Chunk(48, 0, 16, 32, cx: 1));
        // Border x = lattice 48 → world 32.
        static List<(float Y, float Z)> Border(BackdropChunkMeshResult r, float x) =>
            r.VisualMesh.Vertices.Take(r.CollisionMesh.Vertices.Count)
                .Where(v => v.Position.X == x)
                .Select(v => (v.Position.Y, v.Position.Z))
                .OrderBy(v => v.Item1).ToList();
        var borderLeft = Border(left, 32f);
        var borderRight = Border(right, 32f);
        Assert.NotEmpty(borderLeft);
        Assert.Equal(borderLeft, borderRight);   // bitwise float equality (spec §13)
    }

    [Fact]
    public void SeamSkirt_AppendedToVisualOnly_AndExcludedFromCollision()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(32, 0, 16, 32));   // touches the east seam → skirt exists
        Assert.True(result.VisualMesh.Triangles.Count > result.SurfaceTriangleCount, "skirt missing");
        Assert.Equal(result.SurfaceTriangleCount, result.CollisionMesh.Triangles.Count);
        // Skirt quads: bottom vertices exactly SeamSkirtDepthMeters below their seam vertex.
        var skirtVerts = result.VisualMesh.Vertices.Skip(result.CollisionMesh.Vertices.Count)
            .Where(v => Math.Abs(v.Position.X - Half) < 1e-6).ToList();
        Assert.NotEmpty(skirtVerts);
        foreach (var v in skirtVerts)
            Assert.Equal(field.TerrainEdgeWorldZ(Half, v.Position.Y) - 2.0, v.Position.Z, 4);
    }

    [Fact]
    public void NoSkirt_WhenChunkDoesNotTouchTheSeam()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(48, 0, 16, 16));   // 16 m away from the seam
        Assert.Equal(result.SurfaceTriangleCount, result.VisualMesh.Triangles.Count);
    }

    [Fact]
    public void Normals_AreSmoothAndUpwardFacing()
    {
        var (field, options, importance) = Setup((x, _) => 100 + 0.5 * x);   // constant eastward slope
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var result = mesher.MeshChunk(Chunk(48, 0, 16, 16));
        foreach (var v in result.VisualMesh.Vertices.Take(result.CollisionMesh.Vertices.Count))
        {
            Assert.True(v.Normal.Z > 0.5f, "normal not upward");
            Assert.Equal(1.0f, v.Normal.Length(), 3);
            // Constant slope 0.5 in x → normal ≈ normalize(−0.5, 0, 1) inside the far field.
            if (v.Position.X > Half + 8)
                Assert.Equal(-0.5f / MathF.Sqrt(1.25f), v.Normal.X, 2);
        }
    }

    [Fact]
    public void UVs_ArePlanarOverTheChunkRect()
    {
        var (field, options, importance) = Setup((_, _) => 100);
        var mesher = new BackdropQuadtreeMesher(field, options, importance);
        var chunk = Chunk(48, 8, 8, 8);
        var result = mesher.MeshChunk(chunk);
        foreach (var v in result.VisualMesh.Vertices.Take(result.CollisionMesh.Vertices.Count))
        {
            var expectedU = (v.Position.X - chunk.WorldMinX) / (chunk.WorldMaxX - chunk.WorldMinX);
            var expectedV = (v.Position.Y - chunk.WorldMinY) / (chunk.WorldMaxY - chunk.WorldMinY);
            Assert.Equal((float)expectedU, v.UV.X, 5);
            Assert.Equal((float)expectedV, v.UV.Y, 5);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropTriangulation"`
Expected: new tests FAIL (normals/UVs zero, no skirt).

- [ ] **Step 3: Implement** — extend `MeshChunk` between the collision snapshot and the return:

```csharp
        // Normals from the height-field gradient (central differences, step = lattice unit — spec §8:
        // gradient normals, not per-face, so lighting doesn't reveal the triangulation).
        var h = _options.LatticeUnitMeters;
        for (var i = 0; i < mesh.Vertices.Count; i++)
        {
            var pos = mesh.Vertices[i].Position;
            var dzdx = (_field.SampleWorldZ(pos.X + h, pos.Y) - _field.SampleWorldZ(pos.X - h, pos.Y)) / (2 * h);
            var dzdy = (_field.SampleWorldZ(pos.X, pos.Y + h) - _field.SampleWorldZ(pos.X, pos.Y - h)) / (2 * h);
            var normal = Vector3.Normalize(new Vector3((float)-dzdx, (float)-dzdy, 1f));
            var uv = new Vector2(
                (float)((pos.X - chunk.WorldMinX) / (chunk.WorldMaxX - chunk.WorldMinX)),
                (float)((pos.Y - chunk.WorldMinY) / (chunk.WorldMaxY - chunk.WorldMinY)));
            mesh.Vertices[i] = mesh.Vertices[i].WithNormal(normal).WithUV(uv);
        }
        collision.Vertices.Clear();
        collision.Vertices.AddRange(mesh.Vertices);        // re-snapshot with normals/uvs (same order/count)

        // Seam skirt (spec §7.5): vertical flange along chunk borders that lie ON the terrain seam.
        if (_options.SeamSkirt)
            AppendSeamSkirt(mesh, chunk);
```

`AppendSeamSkirt`: for each of the 4 chunk borders, if it coincides with a terrain rect edge (`chunk.LatticeX == 0 && …` — i.e. the border's fixed lattice coordinate equals 0 or `size` AND the border segment overlaps `[0, size]` on the other axis), take the shared subdivision positions of that border (they are full-res there: consecutive lattice points), and for each consecutive pair `(a, b)` emit a quad: top vertices = the existing seam vertices (`vertexLookup[(ix, iy)]`), bottom vertices = new vertices at `z − SeamSkirtDepthMeters` with normal pointing away from the terrain (horizontal) and the top vertex's UV. Two triangles per quad, wound to face away from the terrain. The terrain size in lattice units is `(int)Math.Round(2 * _options.HalfSizeMeters / _options.LatticeUnitMeters)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~Backdrop"`
Expected: all PASS, including the earlier watertight test (skirt triangles sit beyond `SurfaceTriangleCount`, so it is unaffected).

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropQuadtreeMesher.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropTriangulationTests.cs
git commit -m "feat(backdrop): seam-exact vertices, chunk-border identity, skirt, gradient normals, planar UVs"
```

---

### Task 9: `BackdropSceneWriter` — DAE, textured materials, scene items, clean-and-rewrite

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkExportItem.cs`
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropSceneWriter.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropSceneWriterTests.cs`

**Interfaces:**
- Consumes: `BackdropChunkMeshResult` (7/8), `BeamNG.Procedural3D` exporters: `ColladaExporter(new ColladaExportOptions { ConvertToZUp = true, FlipWindingOrder = false })`, `Export(BeamNgDaeScene, string)`, `BeamNgDaeScene { BaseName, LodLevels, ColmeshMeshes, NullDetailPixelSize }`, `LodLevel(int pixelSize, List<Mesh>)`, `Material.CreateWithTexture(name, path)`; `Grille.BeamNG.IO.Text.SimItemsJsonSerializer.Save` / `ArtItemsJsonSerializer.Load/Save` (`JsonDict`).
- Produces:

```csharp
public sealed class BackdropChunkExportItem
{
    public required int Cx { get; init; }
    public required int Cy { get; init; }
    public required string DaeFileName { get; init; }
    public required string MaterialName { get; init; }
    public required string TextureFileName { get; init; }
    public int Vertices { get; init; }
    public int Triangles { get; init; }
}

public class BackdropSceneWriter        // mirror of BridgeSceneWriter/TunnelSceneWriter
{
    public string GroupName { get; set; } = "MT_backdrop";
    public void EnsureSimGroupInParent(string parentItemsPath, string parentGroupName = "MissionGroup");
    public int WriteSceneItems(IReadOnlyList<BackdropChunkExportItem> chunks, string outputPath, string shapePath);
    public int WriteMaterials(IReadOnlyList<BackdropChunkExportItem> chunks, string outputPath, string texturePath);
    public BackdropChunkExportItem ExportChunkDae(BackdropChunkDefinition chunk,
        BackdropChunkMeshResult meshResult, string shapesDirectory);
    public static void CleanPreviousOutputs(string levelPath);
}
```

**Conventions (spec §9), copied from the codebase precedents:**
- `EnsureSimGroupInParent`: byte-for-byte the `BridgeSceneWriter.EnsureSimGroupInParent` algorithm (`BeamNgTerrainPoc/Terrain/Export/BridgeSceneWriter.cs:34-70`) with `GroupName = "MT_backdrop"` — parse NDJSON lines, return if a `SimGroup` named `MT_backdrop` exists, else append `{name, class: SimGroup, persistentId: Guid, __parent}`.
- TSStatic entry per chunk (BridgeSceneWriter `:171-188` shape): `class=TSStatic`, `name=$"backdrop_{Cx}_{Cy}"`, `__parent=GroupName`, `persistentId=Guid.NewGuid()`, `position=[0,0,0]`, `rotationMatrix=[1,0,0,0,1,0,0,0,1]`, `shapeName=shapePath + DaeFileName`, `isRenderEnabled=true`, `useInstanceRenderData=true`. Write with `SimItemsJsonSerializer.Save(outputPath, items)`; append trailing `/` to `shapePath` if missing; return 0 and write nothing on an empty list.
- `WriteMaterials`: **idempotent-by-name against the existing file** (spec §9 "never clobbers a user-edited material" — the `BridgeSceneWriter.WritePlaceholderMaterial` merge pattern, NOT the overwrite-all of `BuildingSceneWriter.WriteMaterials`): `ArtItemsJsonSerializer.Load` existing, skip names already present, append new entries, `Save`. Entry per chunk follows `BuildingSceneWriter.CreateMaterialEntry` (`Terrain/Building/BuildingSceneWriter.cs:346-414`):

```csharp
    private static JsonDict CreateMaterialEntry(string materialName, string texturePath, string textureFile)
    {
        var stage0 = new JsonDict
        {
            ["baseColorMap"] = texturePath + textureFile,          // "/levels/{level}/art/shapes/MT_backdrop/textures/backdrop_0_1.png"
            ["roughnessFactor"] = 1.0f,                            // spec §9
            ["baseColorFactor"] = new float[] { 1f, 1f, 1f, 1f }   // untinted — the satellite texture IS the color
        };
        return new JsonDict
        {
            ["class"] = "Material",
            ["name"] = materialName,
            ["mapTo"] = materialName,
            ["internalName"] = materialName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            ["version"] = 1.5f,
            ["Stages"] = new JsonDict[] { stage0, new JsonDict(), new JsonDict(), new JsonDict() }
        };
    }
```

- `ExportChunkDae` (BridgeDeckDaeExporter `WriteBeamNgBridgeDae` pattern, `:457-484`):

```csharp
    public BackdropChunkExportItem ExportChunkDae(BackdropChunkDefinition chunk,
        BackdropChunkMeshResult meshResult, string shapesDirectory)
    {
        Directory.CreateDirectory(shapesDirectory);
        meshResult.VisualMesh.MaterialName = chunk.MaterialName;
        meshResult.CollisionMesh.MaterialName = null;
        meshResult.CollisionMesh.Name = "Colmesh-1";

        var scene = new BeamNgDaeScene
        {
            BaseName = $"backdrop_{chunk.Cx}_{chunk.Cy}",          // digits mangled to letters by the exporter
            LodLevels = [new LodLevel(2, [meshResult.VisualMesh])], // small pixel size → visible almost forever
            ColmeshMeshes = meshResult.CollisionMesh.HasGeometry ? [meshResult.CollisionMesh] : null,
            NullDetailPixelSize = 0                                 // no nulldetail → chunks never vanish (spec §9;
                                                                    // in-game validation item — see manual checklist)
        };
        // Conscious deviation from spec §9's "BeamNgLodDefaults.ComputeForBounds per chunk" wording:
        // ComputeForBounds scales pixel sizes UP with bounds (LOD0 ≈ 6600 px for a 2 km chunk), which
        // would hide far chunks — the opposite of the stated goal "distant chunks stay visible". A
        // single LOD at a tiny fixed pixel size (2) + no nulldetail keeps every chunk rendered at any
        // distance. If in-game validation shows popping/perf issues, revisit with ComputeForBounds'
        // bias parameter — the knob is confined to this method.
        var exporter = new ColladaExporter(new ColladaExportOptions { ConvertToZUp = true, FlipWindingOrder = false });
        exporter.RegisterMaterial(Material.CreateWithTexture(chunk.MaterialName, "textures/" + chunk.TextureFileName));
        exporter.Export(scene, Path.Combine(shapesDirectory, chunk.DaeFileName));

        return new BackdropChunkExportItem
        {
            Cx = chunk.Cx, Cy = chunk.Cy,
            DaeFileName = chunk.DaeFileName, MaterialName = chunk.MaterialName,
            TextureFileName = chunk.TextureFileName,
            Vertices = meshResult.VisualMesh.VertexCount, Triangles = meshResult.VisualMesh.TriangleCount
        };
    }
```

- `CleanPreviousOutputs(levelPath)` (the `TerrainCreator.CleanBridgeOutputDirectories` pattern, `TerrainCreator.cs:1470-1484`): delete `art/shapes/MT_backdrop/` **except** `textures/` is deleted too (regen recreates the plan; textures are rebaked afterwards) and `main/MissionGroup/MT_backdrop/`; leave the parent `items.level.json` untouched (the SimGroup upsert keeps it correct).

- [ ] **Step 1: Write the failing tests** (mirror `BeamNgTerrainPoc.Tests/Export/BridgeSceneWriterTests.cs` — temp root, `ReadNdjson` helper, `IDisposable`)

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropSceneWriterTests.cs
using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Backdrop;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropSceneWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "beamng_backdrop_scene_tests", Guid.NewGuid().ToString("N"));

    private string ParentItemsPath => Path.Combine(_root, "main", "MissionGroup", "items.level.json");
    private string GroupItemsPath => Path.Combine(_root, "main", "MissionGroup", "MT_backdrop", "items.level.json");
    private string MaterialsPath => Path.Combine(_root, "art", "shapes", "MT_backdrop", "main.materials.json");
    private const string ShapePath = "/levels/test_level/art/shapes/MT_backdrop/";
    private const string TexturePath = "/levels/test_level/art/shapes/MT_backdrop/textures/";

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static List<BackdropChunkExportItem> SampleChunks() =>
    [
        new() { Cx = 0, Cy = 1, DaeFileName = "backdrop_0_1.dae", MaterialName = "mt_backdrop_0_1",
                TextureFileName = "backdrop_0_1.png", Vertices = 10, Triangles = 8 },
        new() { Cx = 2, Cy = 0, DaeFileName = "backdrop_2_0.dae", MaterialName = "mt_backdrop_2_0",
                TextureFileName = "backdrop_2_0.png", Vertices = 12, Triangles = 10 }
    ];

    private static List<JsonDocument> ReadNdjson(string path) =>
        File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l)).ToList();

    [Fact]
    public void EnsureSimGroupInParent_AddsGroup_AndIsIdempotent()
    {
        var writer = new BackdropSceneWriter();
        writer.EnsureSimGroupInParent(ParentItemsPath);
        writer.EnsureSimGroupInParent(ParentItemsPath);
        var entries = ReadNdjson(ParentItemsPath);
        Assert.Single(entries);
        Assert.Equal("SimGroup", entries[0].RootElement.GetProperty("class").GetString());
        Assert.Equal("MT_backdrop", entries[0].RootElement.GetProperty("name").GetString());
        Assert.Equal("MissionGroup", entries[0].RootElement.GetProperty("__parent").GetString());
    }

    [Fact]
    public void WriteSceneItems_WritesOneTSStaticPerChunk_AtOrigin()
    {
        var writer = new BackdropSceneWriter();
        var count = writer.WriteSceneItems(SampleChunks(), GroupItemsPath, ShapePath);
        Assert.Equal(2, count);
        var entries = ReadNdjson(GroupItemsPath);
        foreach (var (doc, chunk) in entries.Zip(SampleChunks()))
        {
            var root = doc.RootElement;
            Assert.Equal("TSStatic", root.GetProperty("class").GetString());
            Assert.Equal($"backdrop_{chunk.Cx}_{chunk.Cy}", root.GetProperty("name").GetString());
            Assert.Equal("MT_backdrop", root.GetProperty("__parent").GetString());
            Assert.Equal(ShapePath + chunk.DaeFileName, root.GetProperty("shapeName").GetString());
            var pos = root.GetProperty("position").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            Assert.Equal(new[] { 0f, 0f, 0f }, pos);
        }
    }

    [Fact]
    public void WriteMaterials_WritesTexturedEntries()
    {
        var writer = new BackdropSceneWriter();
        var count = writer.WriteMaterials(SampleChunks(), MaterialsPath, TexturePath);
        Assert.Equal(2, count);
        var materials = ArtItemsJsonSerializer.Load(MaterialsPath).ToList();
        var m = materials.First(x => (string)x["name"]! == "mt_backdrop_0_1");
        var stages = (JsonDict[])m["Stages"]!;
        Assert.Equal(TexturePath + "backdrop_0_1.png", (string)stages[0]["baseColorMap"]!);
        Assert.Equal(1.0f, (float)stages[0]["roughnessFactor"]!);
    }

    [Fact]
    public void WriteMaterials_IsIdempotentByName_AndPreservesForeignMaterials()
    {
        var writer = new BackdropSceneWriter();
        Directory.CreateDirectory(Path.GetDirectoryName(MaterialsPath)!);
        ArtItemsJsonSerializer.Save(MaterialsPath,
            new List<JsonDict> { new() { ["name"] = "user_material", ["class"] = "Material" } });
        writer.WriteMaterials(SampleChunks(), MaterialsPath, TexturePath);
        var second = writer.WriteMaterials(SampleChunks(), MaterialsPath, TexturePath);
        Assert.Equal(0, second);                                   // nothing new on the second run
        var materials = ArtItemsJsonSerializer.Load(MaterialsPath).ToList();
        Assert.Equal(3, materials.Count);                          // user material survived
        Assert.Contains(materials, m => (string)m["name"]! == "user_material");
    }

    [Fact]
    public void CleanPreviousOutputs_RemovesShapesAndSceneFolder_KeepsParentItems()
    {
        var writer = new BackdropSceneWriter();
        writer.EnsureSimGroupInParent(ParentItemsPath);
        writer.WriteSceneItems(SampleChunks(), GroupItemsPath, ShapePath);
        writer.WriteMaterials(SampleChunks(), MaterialsPath, TexturePath);
        BackdropSceneWriter.CleanPreviousOutputs(_root);
        Assert.False(Directory.Exists(Path.Combine(_root, "art", "shapes", "MT_backdrop")));
        Assert.False(Directory.Exists(Path.Combine(_root, "main", "MissionGroup", "MT_backdrop")));
        Assert.True(File.Exists(ParentItemsPath));                 // SimGroup line kept (spec §9)
    }

    [Fact]
    public void ExportChunkDae_WritesDaeWithColmesh()
    {
        // Minimal 2-triangle mesh via the mesher on a flat field (reuse Task 7 Setup) or hand-built:
        var visual = new BeamNG.Procedural3D.Core.Mesh { Name = "backdrop_0_0" };
        visual.Vertices.Add(new(new(0, 0, 0))); visual.Vertices.Add(new(new(1, 0, 0)));
        visual.Vertices.Add(new(new(1, 1, 0))); visual.Vertices.Add(new(new(0, 1, 0)));
        visual.Triangles.Add(new(0, 1, 2)); visual.Triangles.Add(new(0, 2, 3));
        var collision = new BeamNG.Procedural3D.Core.Mesh { Name = "Colmesh-1" };
        collision.Vertices.AddRange(visual.Vertices); collision.Triangles.AddRange(visual.Triangles);

        var chunk = new BackdropChunkDefinition
        {
            Cx = 0, Cy = 0, LatticeX = 0, LatticeY = 0, LatticeWidth = 1, LatticeHeight = 1,
            SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 1, SourceRectHeight = 1,
            DaeFileName = "backdrop_0_0.dae", TextureFileName = "backdrop_0_0.png",
            MaterialName = "mt_backdrop_0_0", TextureSize = 256, DistanceToTerrainMeters = 0
        };
        var result = new BackdropChunkMeshResult
            { VisualMesh = visual, CollisionMesh = collision, LeafCount = 1, SurfaceTriangleCount = 2 };

        var shapesDir = Path.Combine(_root, "art", "shapes", "MT_backdrop");
        var item = new BackdropSceneWriter().ExportChunkDae(chunk, result, shapesDir);

        var daePath = Path.Combine(shapesDir, "backdrop_0_0.dae");
        Assert.True(File.Exists(daePath));
        var dae = File.ReadAllText(daePath);
        Assert.Contains("Colmesh-1", dae);
        Assert.DoesNotContain("backdrop_0_0_a", dae);   // digits must be letter-mangled inside the DAE
        Assert.Contains("backdrop_a_a", dae);           // 0→a per DigitsToLetters
        Assert.Equal(4, item.Vertices);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropSceneWriter"`
Expected: compile failure.

- [ ] **Step 3: Implement `BackdropSceneWriter`** per the conventions block above (copy the algorithms from `BridgeSceneWriter`, adjust names). Namespace `BeamNgTerrainPoc.Terrain.Backdrop`, usings `System.Text.Json`, `Grille.BeamNG.IO.Text`, `BeamNG.Procedural3D.Core`, `BeamNG.Procedural3D.Exporters`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropSceneWriter"`
Expected: PASS. (If `ArtItemsJsonSerializer` stores `Stages` differently than `JsonDict[]` on load, assert via `JsonDocument` on the raw file instead — check how `BridgeSceneWriterTests` asserts materials and mirror it.)

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/BackdropSceneWriter.cs BeamNgTerrainPoc/Terrain/Backdrop/BackdropChunkExportItem.cs BeamNgTerrainPoc.Tests/Backdrop/BackdropSceneWriterTests.cs
git commit -m "feat(backdrop): scene writer - chunk DAEs, textured materials, TSStatics, clean-and-rewrite"
```

---

### Task 10: `BackdropRasterLoader` + `BackdropGenerator` — end-to-end core pipeline + cost estimate

**Files:**
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropRasterLoader.cs`
- Create: `BeamNgTerrainPoc/Terrain/Backdrop/BackdropGenerator.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropRasterLoaderTests.cs`
- Test: `BeamNgTerrainPoc.Tests/Backdrop/BackdropGeneratorTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–9; GDAL (`OSGeo.GDAL.Gdal.Open`, `Band.ReadRaster` with `buf_size != win_size` for downsampling, `Band.GetNoDataValue`); `GeoTiffReader.InitializeGdal()` (public static, must be called before any GDAL work).
- Produces:

```csharp
public static class BackdropRasterLoader
{
    /// <summary>
    ///     Reads a window of the GeoTIFF as float elevations. maxDimension caps the LARGER output side
    ///     (GDAL resamples via buf_size); null = native resolution. Nodata → edge-extension fill;
    ///     nodataPercentage ∈ [0, 100].
    /// </summary>
    public static BackdropRaster LoadWindow(string geoTiffPath, PixelRect window,
        int? maxDimension, out double nodataPercentage);
}

public sealed class BackdropGenerationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; } = [];
    public BackdropChunkPlan? ChunkPlan { get; set; }
    public List<BackdropChunkExportItem> ExportedChunks { get; } = [];
    public int ChunksSkipped { get; set; }         // fully-nodata chunks (spec §11 table)
    public int TotalVertices { get; set; }
    public int TotalTriangles { get; set; }
    public double NodataPercentage { get; set; }
}

public sealed class BackdropEstimateResult
{
    public long EstimatedTriangles { get; set; }
    public long TextureMemoryBytes { get; set; }   // Σ chunkTexSize² × 4 (uncompressed upper bound, spec §5)
    public int ChunkCount { get; set; }
}

public sealed class BackdropGenerator
{
    /// <summary>Full pipeline: validate → rasters → plan → mesh → DAEs → scene. Never throws for data errors.</summary>
    public BackdropGenerationResult Generate(BackdropGenerationParameters parameters, string? debugOutputPath = null);
    /// <summary>Cheap cost probe for the UI (spec §5): coarse far raster (≤512) + per-cell error scan; no writes.</summary>
    public BackdropEstimateResult Estimate(BackdropGenerationParameters parameters);
}
```

**`Generate` sequence:**
1. `parameters.Validate()` — errors → `Success=false` + message; warnings → `result.Warnings`.
2. `GeoTiffReader.InitializeGdal()`; load far raster (`LoadWindow(path, BackdropRect, MaxFarRasterDimension)`) and up to 4 band strips at native resolution: per side with margin > 0, window = terrain rect inflated by `bandPx = ceil(EdgeBandMeters / MetersPerSourcePixel) + 2` on that side, minus the terrain rect, **plus a 2 px overlap into the terrain rect and neighbors** so bilinear sampling near strip borders never reads clamped edges. Aggregate nodata % → warning `"{p:F1} % of the backdrop area has no elevation data (filled by edge extension)"` when > 0 (spec §6).
3. Build mapper (2), height field (4), plan (5). Chunks whose source rect is fully nodata (loader exposes a `WasFullyNodata` flag on the far raster region — simplest: track nodata cell rects during load and test chunk source rects) → skip + warning (spec §6). V1 simplification: only skip when the WHOLE far raster was nodata-free is false AND the chunk region nodata fraction is 100 % — compute from the nodata mask before filling (loader also returns the mask via an overload used only by the generator).
4. Mesher (6–8) per chunk; accumulate totals.
5. `BackdropSceneWriter.CleanPreviousOutputs(LevelPath)`; export DAEs to `{LevelPath}/art/shapes/MT_backdrop/`; `WriteMaterials(..., "/levels/{LevelName}/art/shapes/MT_backdrop/textures/")`; `EnsureSimGroupInParent({LevelPath}/main/MissionGroup/items.level.json)`; `WriteSceneItems(..., {LevelPath}/main/MissionGroup/MT_backdrop/items.level.json, "/levels/{LevelName}/art/shapes/MT_backdrop/")`.
6. Debug artifacts when `debugOutputPath != null` (spec §11): `band_raster.png` / `far_raster.png` (ImageSharp `L16`, min–max normalized), `chunk_stats.txt` (per chunk: name, leaves, verts, tris, texture size, distance), `quadtree_levels.png` (leaf size → gray level, far raster resolution).
7. Any unexpected exception → caught, `Success=false`, `ErrorMessage=ex.Message` (the app layer decides how to surface it; core never throws out of `Generate`).

**`Estimate`:** validate → far raster at ≤512 → plan → for each chunk, walk a virtual quadtree WITHOUT vertex emission (refinement only, `ErrorProbeGridSize=2`) → leaves×2 triangles; band contribution analytic: `bandArea/u²×2` (`bandArea` = ring-band overlap area). Texture memory from the plan. Runs in O(chunks × coarse cells).

- [ ] **Step 1: Write the failing tests**

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropRasterLoaderTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropRasterLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backdrop_loader_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Creates a 100x80 float GeoTIFF, value = x + 100*y, nodata (−9999) in a 10x10 block at (20,20).</summary>
    private string CreateTestTiff()
    {
        BeamNgTerrainPoc.Terrain.GeoTiff.GeoTiffReader.InitializeGdal();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "test.tif");
        using var driver = Gdal.GetDriverByName("GTiff");
        using var ds = driver.Create(path, 100, 80, 1, DataType.GDT_Float32, null);
        ds.SetGeoTransform([500000.0, 2.0, 0.0, 5400000.0, 0.0, -2.0]);
        var band = ds.GetRasterBand(1);
        band.SetNoDataValue(-9999.0);
        var data = new float[100 * 80];
        for (var y = 0; y < 80; y++)
        for (var x = 0; x < 100; x++)
            data[y * 100 + x] = x >= 20 && x < 30 && y >= 20 && y < 30 ? -9999f : x + 100f * y;
        band.WriteRaster(0, 0, 100, 80, data, 100, 80, 0, 0);
        ds.FlushCache();
        return path;
    }

    [Fact]
    public void LoadWindow_NativeResolution_ReadsValues()
    {
        var path = CreateTestTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(50, 40, 20, 10), null, out var nodata);
        Assert.Equal(0.0, nodata, 3);
        Assert.Equal(50 + 100 * 40, raster.SampleBilinearAtSource(50.5, 40.5), 3);
    }

    [Fact]
    public void LoadWindow_Downsampled_CapsLargerSide()
    {
        var path = CreateTestTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(0, 0, 100, 80), 50, out _);
        Assert.Equal(50, raster.Width);
        Assert.Equal(40, raster.Height);
        Assert.Equal(new PixelRect(0, 0, 100, 80), raster.SourceWindow);
    }

    [Fact]
    public void LoadWindow_FillsNodata_AndReportsPercentage()
    {
        var path = CreateTestTiff();
        var raster = BackdropRasterLoader.LoadWindow(path, new PixelRect(15, 15, 20, 20), null, out var nodata);
        Assert.Equal(100.0 * 100 / 400, nodata, 1);                 // 10x10 of 20x20
        var filled = raster.SampleBilinearAtSource(25.5, 25.5);     // inside the hole → edge-extended
        Assert.True(filled >= 0, "nodata not filled");
        Assert.NotEqual(-9999.0, filled, 1);
    }
}
```

```csharp
// BeamNgTerrainPoc.Tests/Backdrop/BackdropGeneratorTests.cs
using BeamNgTerrainPoc.Terrain.Backdrop;
using OSGeo.GDAL;

namespace BeamNgTerrainPoc.Tests.Backdrop;

public class BackdropGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "backdrop_gen_" + Guid.NewGuid().ToString("N"));
    private string LevelPath => Path.Combine(_dir, "levels", "test_level");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private BackdropGenerationParameters CreateParameters()
    {
        BeamNgTerrainPoc.Terrain.GeoTiff.GeoTiffReader.InitializeGdal();
        Directory.CreateDirectory(LevelPath);
        var tiffPath = Path.Combine(_dir, "dem.tif");
        using (var driver = Gdal.GetDriverByName("GTiff"))
        using (var ds = driver.Create(tiffPath, 128, 128, 1, DataType.GDT_Float32, null))
        {
            ds.SetGeoTransform([500000.0, 2.0, 0.0, 5400000.0, 0.0, -2.0]);
            var data = new float[128 * 128];
            for (var y = 0; y < 128; y++)
            for (var x = 0; x < 128; x++)
                data[y * 128 + x] = 400f + 3f * MathF.Sin(x / 5f) * MathF.Cos(y / 7f);
            ds.GetRasterBand(1).WriteRaster(0, 0, 128, 128, data, 128, 128, 0, 0);
            ds.FlushCache();
        }
        return new BackdropGenerationParameters
        {
            TerrainHeightMap = new float[32, 32],
            TerrainSizePixels = 32, TerrainMetersPerPixel = 2.0f,
            TerrainBaseHeight = 0f, TerrainCropMinElevation = 400.0,
            SourceGeoTiffPath = tiffPath,
            SourceRasterWidth = 128, SourceRasterHeight = 128,
            SourceGeoTransform = [500000, 2, 0, 5400000, 0, -2],
            ProjectionWkt = null,
            SourceWgs84Bounds = new BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox(7.0, 50.0, 7.2, 50.2),
            TerrainRect = new PixelRect(48, 48, 32, 32),
            BackdropRect = new PixelRect(16, 16, 96, 96),
            LevelPath = LevelPath, LevelName = "test_level",
            EdgeBandMeters = 8, ChunkTargetMeters = 40
        };
    }

    [Fact]
    public void Generate_EndToEnd_WritesAllArtifacts()
    {
        var result = new BackdropGenerator().Generate(CreateParameters());
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.ExportedChunks);
        Assert.True(Directory.EnumerateFiles(
            Path.Combine(LevelPath, "art", "shapes", "MT_backdrop"), "*.dae").Any());
        Assert.True(File.Exists(Path.Combine(LevelPath, "art", "shapes", "MT_backdrop", "main.materials.json")));
        Assert.True(File.Exists(Path.Combine(LevelPath, "main", "MissionGroup", "MT_backdrop", "items.level.json")));
        Assert.True(File.Exists(Path.Combine(LevelPath, "main", "MissionGroup", "items.level.json")));
        Assert.True(result.TotalTriangles > 0);
    }

    [Fact]
    public void Generate_IsDeterministic_ByteIdenticalDaes()
    {
        var generator = new BackdropGenerator();
        var p = CreateParameters();
        Assert.True(generator.Generate(p).Success);
        var dae = Directory.EnumerateFiles(Path.Combine(LevelPath, "art", "shapes", "MT_backdrop"), "*.dae").First();
        var first = File.ReadAllBytes(dae);
        Assert.True(generator.Generate(p).Success);   // clean-and-rewrite, then regenerate
        var second = File.ReadAllBytes(dae);
        // persistentIds live in JSON files, not DAEs — DAEs must be byte-identical (the bridge
        // pipeline already relies on ColladaExporter determinism for its byte-identical baselines).
        // If this fails ONLY on <created>/<modified> asset timestamps, compare with those lines
        // stripped and note the exporter nondeterminism — do not weaken the geometry comparison.
        Assert.Equal(first, second);
    }

    [Fact]
    public void Generate_InvalidParameters_FailsWithMessage_WritesNothing()
    {
        var p = CreateParameters() with { BackdropRect = new PixelRect(60, 60, 10, 10) };
        var result = new BackdropGenerator().Generate(p);
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(Directory.Exists(Path.Combine(LevelPath, "art", "shapes", "MT_backdrop")));
    }

    [Fact]
    public void Generate_WritesDebugArtifacts_WhenPathGiven()
    {
        var debug = Path.Combine(_dir, "MT_TerrainGeneration", "backdrop");
        var result = new BackdropGenerator().Generate(CreateParameters(), debug);
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(debug, "far_raster.png")));
        Assert.True(File.Exists(Path.Combine(debug, "chunk_stats.txt")));
    }

    [Fact]
    public void Estimate_ReturnsPlausibleNumbers_WithoutWriting()
    {
        var estimate = new BackdropGenerator().Estimate(CreateParameters());
        Assert.True(estimate.EstimatedTriangles > 0);
        Assert.True(estimate.TextureMemoryBytes > 0);
        Assert.True(estimate.ChunkCount > 0);
        Assert.False(Directory.Exists(Path.Combine(LevelPath, "art")));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj --filter "FullyQualifiedName~BackdropGenerator|FullyQualifiedName~BackdropRasterLoader"`
Expected: compile failure. If GDAL native init fails in the test host, check how existing GeoTIFF-touching tests handle it first (search the test project for `InitializeGdal`) and mirror that.

- [ ] **Step 3: Implement** `BackdropRasterLoader.LoadWindow` (GDAL `Band.ReadRaster(xOff, yOff, winW, winH, buffer, bufW, bufH, 0, 0)`, nodata mask from `GetNoDataValue` + float comparison with `1e-3` tolerance, fill via `BackdropRaster.FillNodataByEdgeExtension`) and `BackdropGenerator` per the sequence above.

- [ ] **Step 4: Run tests to verify they pass**

Run: full Backdrop filter, then the FULL suite: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: all green — the existing ~1069 tests must be untouched (nothing in Tasks 1–10 modified existing files except none; verify `git status` shows only additions).

- [ ] **Step 5: Commit**

```bash
git add BeamNgTerrainPoc/Terrain/Backdrop/ BeamNgTerrainPoc.Tests/Backdrop/
git commit -m "feat(backdrop): GDAL raster loader and end-to-end generator with estimate and debug artifacts"
```

---

## Phase B — App layer (no automated tests; verify with `dotnet build BeamNG_LevelCleanUp.sln` per task + manual checklist in Task 20)

### Task 11: App state + persistence — `BackdropSettings`, `TerrainGenerationState.Backdrop`, `MtBackdropSettings`

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/State/BackdropSettings.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/State/TerrainGenerationState.cs` (property + `Reset()` at `:445-506`)
- Modify: `BeamNG_LevelCleanUp/Objects/MtSettings/MtSettings.cs`

**Interfaces:**
- Produces (app-wide):

```csharp
// BeamNG_LevelCleanUp/BlazorUI/State/BackdropSettings.cs
namespace BeamNG_LevelCleanUp.BlazorUI.State;

/// <summary>UI/state POCO for backdrop generation (spec §5). Selection in combined-GeoTIFF source pixels.</summary>
public class BackdropSettings
{
    public bool Enabled { get; set; }                       // default FALSE (spec D8) — never change this default
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox? BoundingBox { get; set; }  // derived WGS84
    public double EdgeBandMeters { get; set; } = 200;
    public double MaxVerticalErrorNearMeters { get; set; } = 0.5;
    public double MaxVerticalErrorFarMeters { get; set; } = 8.0;
    public double ChunkTargetMeters { get; set; } = 2000;
    public double TexelDensityNearMPerPx { get; set; } = 1.0;
    public int MaxChunkTextureSize { get; set; } = 2048;
    public int MaxFarRasterDimension { get; set; } = 8192;
    public bool SeamSkirt { get; set; } = true;
    public bool HasSelection => Width > 0 && Height > 0;
}
```

- `TerrainGenerationState`: add `public BackdropSettings Backdrop { get; set; } = new();` next to the other nested POCOs (near `HydraulicErosion` at `:37`); in `Reset()` add `Backdrop = new BackdropSettings();` — **spec §14.11 explicitly calls this out; forgetting it leaks stale state between sessions.**
- `MtSettings.cs`: add to the root class `[JsonPropertyName("BackdropSettings")] public MtBackdropSettings? BackdropSettings { get; set; }` (**nullable, no `= new()`** — the BaseColorManager gates on presence, spec §10) plus the new POCOs in the same file (all existing sub-POCOs live there):

```csharp
public class MtBackdropSettings
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("MinLongitude")] public double MinLongitude { get; set; }
    [JsonPropertyName("MinLatitude")] public double MinLatitude { get; set; }
    [JsonPropertyName("MaxLongitude")] public double MaxLongitude { get; set; }
    [JsonPropertyName("MaxLatitude")] public double MaxLatitude { get; set; }
    [JsonPropertyName("SourceGeoTransform")] public double[] SourceGeoTransform { get; set; } = [];
    [JsonPropertyName("ProjectionWkt")] public string ProjectionWkt { get; set; } = string.Empty;
    [JsonPropertyName("TerrainMetersPerPixel")] public double TerrainMetersPerPixel { get; set; }
    [JsonPropertyName("EdgeBandMeters")] public double EdgeBandMeters { get; set; }
    [JsonPropertyName("Chunks")] public List<MtBackdropChunk> Chunks { get; set; } = new();
    [JsonPropertyName("LastBakeUtc")] public DateTime? LastBakeUtc { get; set; }                 // mesh generation
    [JsonPropertyName("LastTextureBakeUtc")] public DateTime? LastTextureBakeUtc { get; set; }   // texture bake
    [JsonPropertyName("LastBakeProvider")] public string LastBakeProvider { get; set; } = string.Empty;
    [JsonPropertyName("LastBakeImageryDate")] public string LastBakeImageryDate { get; set; } = string.Empty;
}

public class MtBackdropChunk
{
    [JsonPropertyName("Cx")] public int Cx { get; set; }
    [JsonPropertyName("Cy")] public int Cy { get; set; }
    [JsonPropertyName("MinLongitude")] public double MinLongitude { get; set; }
    [JsonPropertyName("MinLatitude")] public double MinLatitude { get; set; }
    [JsonPropertyName("MaxLongitude")] public double MaxLongitude { get; set; }
    [JsonPropertyName("MaxLatitude")] public double MaxLatitude { get; set; }
    [JsonPropertyName("SourceRectX")] public double SourceRectX { get; set; }
    [JsonPropertyName("SourceRectY")] public double SourceRectY { get; set; }
    [JsonPropertyName("SourceRectWidth")] public double SourceRectWidth { get; set; }
    [JsonPropertyName("SourceRectHeight")] public double SourceRectHeight { get; set; }
    [JsonPropertyName("TextureFile")] public string TextureFile { get; set; } = string.Empty;
    [JsonPropertyName("TextureSize")] public int TextureSize { get; set; }
}
```

- [ ] **Step 1: Implement the three file changes** exactly as above.
- [ ] **Step 2: Build**: `dotnet build BeamNG_LevelCleanUp.sln` — zero new warnings/errors (DLL-lock `MSB` errors are normal on this machine; only `error CS` counts).
- [ ] **Step 3: Grep-verify Reset coverage**: `Backdrop = new BackdropSettings();` present inside `Reset()`.
- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/State/ BeamNG_LevelCleanUp/Objects/MtSettings/MtSettings.cs
git commit -m "feat(backdrop): app state POCO, state reset coverage and MT_settings backdrop block"
```

---

### Task 12: `MapTileOverlayService` — `OverlayRequest` refactor (spec §10)

**Files:**
- Modify: `BeamNG_LevelCleanUp/LogicBasecolorManager/MapTileOverlayService.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record OverlayRequest(
    BeamNgTerrainPoc.Terrain.GeoTiff.GeoBoundingBox Wgs84Bounds,
    double MetersPerPixel,                 // drives ChooseZoom (center latitude comes from Wgs84Bounds.Center)
    double[]? NativeGeoTransform,          // null ⇒ bbox-only linear warp (spec §10)
    int NativeRasterWidth,
    int NativeRasterHeight,
    string? ProjectionWkt,
    int OutputSize,                        // square, pow2
    string OutputPath,                     // full path of the final PNG
    string TileCacheRoot,                  // e.g. {level}\MT_Tiles\cache — SHARED with the terrain overlay
    string ProviderName,
    string? ImageryDate,
    string? ExtraFingerprint = null);      // e.g. adjustment hash — forces rebuild-from-tile-cache on change

public async Task<MapTileOverlayResult> EnsureOverlayImageAsync(OverlayRequest request);
public static int CountTilesForBounds(GeoBoundingBox bounds, double metersPerPixel);  // for the cost estimator
```

**Refactor steps (behavior-preserving for the terrain overlay):**
1. Move the body of the existing `EnsureOverlayImageAsync(levelPath, MtGeoReferenceSettings, provider, outputSize, date)` (`:94-214`) into the new `EnsureOverlayImageAsync(OverlayRequest)`; the legacy signature becomes a thin adapter that builds an `OverlayRequest` from `MtGeoReferenceSettings` (`Wgs84Bounds = new GeoBoundingBox(TerrainMinLongitude, TerrainMinLatitude, TerrainMaxLongitude, TerrainMaxLatitude)`, `MetersPerPixel = TerrainMetersPerPixel`, `NativeGeoTransform = CanWarpFromNativeGeoReference(settings) ? settings.SourceGeoTransform : null`, `OutputPath = Path.Join(levelPath, "MT_Tiles", GetFinalImageName(provider, normalizedDate))`, `TileCacheRoot = Path.Join(levelPath, "MT_Tiles", "cache")`).
2. `GetCachePath` changes from `(tileRoot, provider, date)` to `(cacheRoot, provider, date)` — `Path.Join(cacheRoot, provider.Slug[, date])`. `HasOverlayCache`/`ClearOverlayCache`/`GetFinalOverlayPath` keep their public signatures (they derive `cacheRoot` from `levelPath` internally).
3. `CreateWarpedOverlay(mosaic, settings, zoom, …)` (`:250`) → `CreateWarpedOverlay(mosaic, OverlayRequest, zoom, …)` reading `NativeGeoTransform`/`NativeRasterWidth/Height`/`ProjectionWkt`; warp path chosen by `request.NativeGeoTransform is { Length: 6 } && NativeRasterWidth > 0 && NativeRasterHeight > 0 && !string.IsNullOrWhiteSpace(ProjectionWkt)`.
4. Fingerprint: `BuildWarpFingerprintJson` populated from the request. **CRITICAL:** keep the `WarpFingerprint` record's fields, order and values EXACTLY as today (`Version=1`, min/max lon/lat, center latitude = `Wgs84Bounds.Center.Latitude`, metersPerPixel, geotransform (empty array when null), raster sizes, WKT (empty when null), outputSize) so existing `{final}.png.meta.json` sidecars still match and terrain overlays are not needlessly re-warped. Append `ExtraFingerprint` as a NEW final record field with default `""` — an added field changes the JSON for ALL overlays, so instead: only append `"|" + ExtraFingerprint` to the serialized string when `ExtraFingerprint` is non-empty. Terrain overlays pass null → byte-identical sidecar JSON.
5. `ChooseZoom(request.Wgs84Bounds.Center.Latitude, request.MetersPerPixel)`; tile math unchanged.
6. `CountTilesForBounds`: expose the existing `LonLatToTile` span computation (`(maxTileX−minTileX+1)×(maxTileY−minTileY+1)` at `ChooseZoom`) as a public static.

- [ ] **Step 1: Implement.**
- [ ] **Step 2: Build** (`dotnet build BeamNG_LevelCleanUp.sln`) — both existing call sites (`BasecolorManager.razor.cs:227`, plus any orchestrator use) compile untouched.
- [ ] **Step 3: Manual parity check** (behavior-preserving refactor): open BasecolorManager on a baked level with an existing overlay → "Using cached map tile overlay …" message must appear (fingerprint still matches — proves step 4).
- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/LogicBasecolorManager/MapTileOverlayService.cs
git commit -m "refactor(basecolor): OverlayRequest contract for arbitrary-bbox tile warps, terrain path unchanged"
```

---

### Task 13: `BackdropTextureBaker` (app layer, spec §10)

**Files:**
- Create: `BeamNG_LevelCleanUp/LogicBasecolorManager/BackdropTextureBaker.cs`
- Modify: `BeamNG_LevelCleanUp/LogicBasecolorManager/TerrainPbrMapBuilder.cs` (adjustments overload)

**Interfaces:**
- Consumes: `OverlayRequest` (12), `MtBackdropSettings`/`MtBackdropChunk` (11), `MtBasecolorOverlaySettings` (provider, date, brightness/contrast/saturation), `PubSubChannel` (internal, allowed here).
- Produces:

```csharp
public class BackdropTextureBaker
{
    private readonly MapTileOverlayService _overlayService = new();

    /// <summary>
    ///     Warps + adjusts one satellite texture per chunk into art/shapes/MT_backdrop/textures/.
    ///     Returns the number of successfully baked chunks. One bad chunk never fails the run
    ///     (retry once, then flat-gray + warning — spec §10).
    /// </summary>
    public async Task<int> BakeAllChunksAsync(string levelPath, MtSettings settings);
}
```

- In `TerrainPbrMapBuilder`, add next to the existing private method (`:351`):

```csharp
    /// <summary>4-arg entry so the backdrop baker reuses the exact terrain adjustment math (spec §10).</summary>
    internal static void ApplyOverlayAdjustments(Image<Rgba32> image,
        double brightness, double contrast, double saturation)
    {
        ApplyOverlayAdjustments(image, new BasecolorOverlayOptions(
            string.Empty, 1.0, Array.Empty<BasecolorMaskBlendExceptionOptions>(), brightness, contrast, saturation));
    }
```

- `BakeAllChunksAsync` core:

```csharp
    var backdrop = settings.BackdropSettings;
    if (backdrop is not { Enabled: true } || backdrop.Chunks.Count == 0)
        return 0;

    var overlay = settings.BasecolorModeSettings.OverlaySettings;
    var provider = string.IsNullOrWhiteSpace(overlay.SelectedTileProvider)
        ? "Google Satelite Only" : overlay.SelectedTileProvider;
    var texturesDir = Path.Join(levelPath, "art", "shapes", "MT_backdrop", "textures");
    Directory.CreateDirectory(texturesDir);
    var cacheRoot = Path.Join(levelPath, "MT_Tiles", "cache");           // SHARED tile cache (spec §10)
    var adjustmentFingerprint = FormattableString.Invariant(
        $"adj:{overlay.Brightness:F4}|{overlay.Contrast:F4}|{overlay.Saturation:F4}");

    var baked = 0;
    foreach (var chunk in backdrop.Chunks)
    {
        var outputPath = Path.Join(texturesDir, chunk.TextureFile);
        // Per-chunk translated geotransform — same affine math as GetEffectiveSourceGeoTransform:
        double[]? gt = null;
        if (backdrop.SourceGeoTransform is { Length: 6 } baseGt && !string.IsNullOrWhiteSpace(backdrop.ProjectionWkt))
        {
            gt = baseGt.ToArray();
            gt[0] = baseGt[0] + chunk.SourceRectX * baseGt[1] + chunk.SourceRectY * baseGt[2];
            gt[3] = baseGt[3] + chunk.SourceRectX * baseGt[4] + chunk.SourceRectY * baseGt[5];
        }
        var request = new OverlayRequest(
            new GeoBoundingBox(chunk.MinLongitude, chunk.MinLatitude, chunk.MaxLongitude, chunk.MaxLatitude),
            backdrop.TerrainMetersPerPixel,
            gt,
            (int)Math.Round(chunk.SourceRectWidth), (int)Math.Round(chunk.SourceRectHeight),
            string.IsNullOrWhiteSpace(backdrop.ProjectionWkt) ? null : backdrop.ProjectionWkt,
            chunk.TextureSize, outputPath, cacheRoot, provider,
            string.IsNullOrWhiteSpace(overlay.TileImageryDate) ? null : overlay.TileImageryDate,
            adjustmentFingerprint);

        var success = await TryBakeChunkAsync(request, overlay, chunk);   // retry once inside
        if (success) baked++;
    }
    settings.BackdropSettings!.LastTextureBakeUtc = DateTime.UtcNow;
    settings.BackdropSettings.LastBakeProvider = provider;
    settings.BackdropSettings.LastBakeImageryDate = overlay.TileImageryDate ?? string.Empty;
    return baked;
```

`TryBakeChunkAsync`: 2 attempts of `_overlayService.EnsureOverlayImageAsync(request)`; on success and `!result.ReusedFinalImage`, load the PNG, `TerrainPbrMapBuilder.ApplyOverlayAdjustments(image, overlay.Brightness, overlay.Contrast, overlay.Saturation)`, save over `OutputPath` (adjustments are baked into the final texture; `ExtraFingerprint` guarantees a fresh warp whenever they change, and `ReusedFinalImage` prevents double-application). On final failure: write a flat `#808080` PNG of `chunk.TextureSize`² + `PubSubChannel.SendMessage(Warning, $"Backdrop chunk {chunk.Cx}_{chunk.Cy}: tile download failed, flat texture used")`. Progress: one `Info` per chunk via `SendMessage(..., modulo: true)`.

- [ ] **Step 1: Implement.**
- [ ] **Step 2: Build.**
- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/LogicBasecolorManager/BackdropTextureBaker.cs BeamNG_LevelCleanUp/LogicBasecolorManager/TerrainPbrMapBuilder.cs
git commit -m "feat(backdrop): per-chunk satellite texture baker with shared tile cache and adjustment fingerprint"
```

---

### Task 14: `BackdropOrchestrator` + gated pipeline stage + standalone regen + remove

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Services/BackdropOrchestrator.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Services/TerrainGenerationOrchestrator.cs` (insertion seam `:206-224`)

**Interfaces:**
- Consumes: core `BackdropGenerator`/`BackdropGenerationParameters` (10), `BackdropTextureBaker` (13), `MtSettings` (11), `TerrainGenerationState` (incl. `CachedHeightMap`, `CachedCombinedGeoTiffPath` `:262`, `CropResult`, GeoTIFF metadata block `:242-250`), `DecalRoadNetworkSnapshotLoader.LoadHeightmap(terPath, maxHeight)` (`BeamNgTerrainPoc/Terrain/Services/DecalRoad/DecalRoadNetworkSnapshotLoader.cs:34` — the established `.ter` → `float[y,x]` reconstruction).
- Produces:

```csharp
public class BackdropOrchestrator
{
    /// <summary>In-run stage AND in-session standalone regen. Never throws; reports via PubSub.</summary>
    public async Task<bool> GenerateAsync(TerrainGenerationState state, float[,] outputHeightMap);
    /// <summary>Standalone button: cached heightmap in-session, .ter reconstruction cross-session (spec §11).</summary>
    public async Task<bool> RegenerateStandaloneAsync(TerrainGenerationState state);
    public static bool CanRegenerate(TerrainGenerationState state);
    /// <summary>Deletes art/shapes/MT_backdrop, the scene folder, the SimGroup entry and the settings block.</summary>
    public static void RemoveBackdrop(string levelPath);
}
```

**`GenerateAsync` (all inside `Task.Run`, wrapped `try/catch` → `Warning` + `return false`):**
1. Resolve the source GeoTIFF covering the FULL mosaic (backdrop offsets are mosaic-pixel coordinates):
   - `HeightmapSourceType.GeoTiffFile` → `state.GeoTiffPath`;
   - `GeoTiffDirectory`/XYZ-derived → `state.CachedCombinedGeoTiffPath` when it exists AND covers the full mosaic (`GeoTiffOriginalWidth/Height`); otherwise combine now via `GeoTiffCombiner.CombineFilesAsync` into a temp file and store it in `state.CachedCombinedGeoTiffPath` (reuse the page's existing combine path — see `GenerateTerrain.razor.cs:1939-1952` for the established pattern). **Note the direct-crop optimization** (`TerrainGenerationOrchestrator.cs:1139-1158`) produces a terrain-crop-only file — that file is NOT valid backdrop input; detect via raster dimensions ≠ `GeoTiffOriginalWidth/Height` and fall back to combining.
2. Build parameters:

```csharp
    var crop = state.CropResult;
    var terrainRect = crop is { NeedsCropping: true }
        ? new PixelRect(crop.OffsetX, crop.OffsetY, crop.CropWidth, crop.CropHeight)
        : new PixelRect(0, 0, state.GeoTiffOriginalWidth, state.GeoTiffOriginalHeight);
    var parameters = new BackdropGenerationParameters
    {
        TerrainHeightMap = outputHeightMap,
        TerrainSizePixels = state.TerrainSize,
        TerrainMetersPerPixel = state.MetersPerPixel,
        TerrainBaseHeight = state.TerrainBaseHeight,
        TerrainCropMinElevation = crop?.CroppedMinElevation ?? state.GeoTiffMinElevation ?? 0.0,
        SourceGeoTiffPath = sourcePath,
        EpsgOverride = state.GeoTiffEpsgOverride,
        SourceRasterWidth = state.GeoTiffOriginalWidth,
        SourceRasterHeight = state.GeoTiffOriginalHeight,
        SourceGeoTransform = state.GeoTiffGeoTransform ?? [],
        ProjectionWkt = state.GeoTiffProjectionWkt,
        SourceWgs84Bounds = state.GeoBoundingBox,
        TerrainRect = terrainRect,
        BackdropRect = new PixelRect(state.Backdrop.OffsetX, state.Backdrop.OffsetY,
                                     state.Backdrop.Width, state.Backdrop.Height),
        LevelPath = state.WorkingDirectory,
        LevelName = state.LevelName,
        EdgeBandMeters = state.Backdrop.EdgeBandMeters,
        MaxVerticalErrorNearMeters = state.Backdrop.MaxVerticalErrorNearMeters,
        MaxVerticalErrorFarMeters = state.Backdrop.MaxVerticalErrorFarMeters,
        ChunkTargetMeters = state.Backdrop.ChunkTargetMeters,
        TexelDensityNearMPerPx = state.Backdrop.TexelDensityNearMPerPx,
        MaxChunkTextureSize = state.Backdrop.MaxChunkTextureSize,
        MaxFarRasterDimension = state.Backdrop.MaxFarRasterDimension,
        SeamSkirt = state.Backdrop.SeamSkirt,
    };
```

   (When `AutoSetBaseHeightFromGeoTiff` applied, `TerrainBaseHeight == cropMin` — log both via `Console.WriteLine("[BACKDROP] datum: cropMin={0}, baseHeight={1}")` for datum debugging.)
3. `var result = new BackdropGenerator().Generate(parameters, Path.Combine(state.GetDebugPath(), "backdrop"));` — forward `result.Warnings` as PubSub `Warning`, failure as `Warning` + return false (**the terrain run still succeeds** — spec §11, same contract as `ExportAllOsmLayersAsync`, `TerrainGenerationOrchestrator.cs:1387-1445`).
4. Write `MtBackdropSettings` (from the plan: bounds of the union, per-chunk `MtBackdropChunk` rows, geotransform = **effective source geotransform of the mosaic** (uncropped `state.GeoTiffGeoTransform`), `TerrainMetersPerPixel = state.MetersPerPixel`, `LastBakeUtc = DateTime.UtcNow`): `var settings = MtSettings.Load(levelPath) ?? new MtSettings(); settings.BackdropSettings = built; settings.Save(levelPath);`
5. `await new BackdropTextureBaker().BakeAllChunksAsync(levelPath, settings); settings.Save(levelPath);`
6. PubSub `Info` summary: chunks, verts, tris, textures baked.

**`RegenerateStandaloneAsync`:** heightmap = `state.CachedHeightMap` ?? `DecalRoadNetworkSnapshotLoader.LoadHeightmap(state.GetOutputPath(), state.MaxHeight)` (cache it back on state); null → PubSub `Error` "Terrain file not found … Generate terrain first." and return false (spec §11). Then `GenerateAsync(state, heightMap)`. Wipe discipline: `BackdropSceneWriter.CleanPreviousOutputs` already runs inside `Generate`; the standalone path must additionally clear ONLY `MT_TerrainGeneration/backdrop/` (`Directory.Delete(path, true)` if exists) — never the whole debug folder (spec §14.8).

**`CanRegenerate`:** `state.CachedHeightMap != null || (state.HasWorkingDirectory && File.Exists(state.GetOutputPath()))` — plus GeoTIFF metadata loaded (`state.GeoTiffOriginalWidth > 0`) and `state.Backdrop.HasSelection`.

**`RemoveBackdrop(levelPath)`:** delete `art/shapes/MT_backdrop/` + `main/MissionGroup/MT_backdrop/`; remove the `MT_backdrop` SimGroup line from `main/MissionGroup/items.level.json` (parse NDJSON lines, filter `class==SimGroup && name==MT_backdrop`, rewrite); `settings.BackdropSettings = null; settings.Save(levelPath);` (spec §11 Remove button). A terrain run with backdrop disabled must NOT call this — non-destructive default.

**Orchestrator insertion** — `TerrainGenerationOrchestrator.ExecuteInternalAsync`, directly after the heightmap cache assignment (`:219-224`, after `state.CachedHeightMap = terrainParameters.OutputHeightMap;`):

```csharp
            // Backdrop generation (optional stage — failure never fails the terrain run, spec §11)
            if (success && state.Backdrop.Enabled &&
                terrainParameters?.OutputHeightMap is { } backdropHeightMap)
            {
                await new BackdropOrchestrator().GenerateAsync(state, backdropHeightMap).ConfigureAwait(false);
            }
```

- [ ] **Step 1: Implement `BackdropOrchestrator`.**
- [ ] **Step 2: Insert the gated stage.**
- [ ] **Step 3: Build**; then run the existing manual smoke: generate a small terrain with `Backdrop.Enabled = false` (default) → verify NO `MT_backdrop` folder appears and generation output is unchanged (default-off guarantee).
- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Services/
git commit -m "feat(backdrop): app orchestrator - in-run gated stage, standalone regen, remove"
```

---

### Task 15: BaseColorManager interlock — service extraction, backdrop rebake, staleness

**Files:**
- Modify: `BeamNG_LevelCleanUp/LogicBasecolorManager/BasecolorManagerService.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor.cs` (`:204-253` Reset&Rebake, `:311-342` staleness, `:182-202` ApplyBaseColorModeCore)

**Interfaces:**
- Produces on `BasecolorManagerService`:

```csharp
/// <summary>Rebakes every backdrop chunk texture from the shared tile cache (spec §10). Returns count, 0 when no backdrop.</summary>
public async Task<int> RebakeBackdropTexturesAsync(string levelPath, MtSettings settings)
{
    if (settings.BackdropSettings is not { Enabled: true })
        return 0;
    var count = await new BackdropTextureBaker().BakeAllChunksAsync(levelPath, settings);
    settings.Save(levelPath);
    return count;
}
```

**Changes:**
1. **Targeted extraction (spec §10):** move the non-UI pipeline of `ResetAndRebakeBaseColorMode` into the service so the backdrop step has a non-UI home:

```csharp
public sealed record ResetRebakeInputs(
    string LevelPath, string LevelName, string MaterialsJsonPath, string TerrainFilePath,
    List<CopyAsset> PaintMaterials, List<CopyAsset> BasecolorMaterials, MtSettings Settings);

public sealed record ResetRebakeResult(TerrainV9Binary Terrain, int TerrainSize);

public async Task<ResetRebakeResult> ResetAndRebakeAsync(ResetRebakeInputs inputs,
    Func<Task>? refreshOverlayAsync = null);
```

   Body = steps 1–4 of the current page method (`BasecolorManager.razor.cs:209-248`): reload terrain from disk (`LayerMaskReader.ReadTerrainBinary`), sync settings from material lists (move `UpdateSettingsFromMaterialLists`/`SyncBasecolorMaterialsFromPaintMode`/`PaintModeHasUsableMaterialSettings` logic into private service helpers operating on `(settings, lists)` — they only copy between `MtTerrainMaterialSetting` and `CopyAsset`, no UI state), `_paintModeApplier.Apply(...)`, invoke `refreshOverlayAsync` (the page supplies its existing tile-overlay refresh step 3, which needs page-computed provider/date properties), `_baseColorModeApplier.Apply(...)` with `CreateOverlayOptions`/`CreateMaterialBorderBlendOptions`, then **NEW step: `await RebakeBackdropTexturesAsync(...)`**. The page method shrinks to: `RunBusyOperation` wrapper → build inputs → call service → assign `_terrain`/`_terrainSize` → rebuild preview (`_service.BuildPreview`) → `UpdateBakeStaleness()` → snackbar.
2. **Apply BaseColor Mode** (`ApplyBaseColorModeCore`, page `:182-202`): after `_baseColorModeApplier.Apply(...)`, add `await _service.RebakeBackdropTexturesAsync(_levelPath, _settings);` (make the callers async — `ApplyBaseColorMode` already awaits).
3. **Staleness** (`ComputeBakeStaleReason`, page `:311-342`): append a third reason —

```csharp
    var backdrop = _settings.BackdropSettings;
    if (backdrop is { Enabled: true })
    {
        var overlay = _settings.BasecolorModeSettings.OverlaySettings;
        var providerChanged = !string.Equals(backdrop.LastBakeProvider, overlay.SelectedTileProvider, StringComparison.Ordinal)
                           || !string.Equals(backdrop.LastBakeImageryDate, overlay.TileImageryDate ?? string.Empty, StringComparison.Ordinal);
        var geoRefNewer = backdrop.LastTextureBakeUtc.HasValue &&
                          _settings.GeoReferenceSettings != null &&
                          _settings.GeoReferenceSettings.SavedAtUtc > backdrop.LastTextureBakeUtc.Value;
        if (providerChanged || geoRefNewer)
            reasons.Add("the backdrop textures no longer match the provider or georeference");
    }
```

- [ ] **Step 1: Implement extraction + rebake + staleness.**
- [ ] **Step 2: Build.**
- [ ] **Step 3: Manual parity check:** on a level WITHOUT a backdrop, Apply BaseColor Mode and Reset & Rebake behave exactly as before (no new messages, same outputs).
- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/LogicBasecolorManager/ BeamNG_LevelCleanUp/BlazorUI/Pages/BasecolorManager.razor.cs
git commit -m "feat(backdrop): basecolor manager interlock - service-side reset-and-rebake with backdrop textures and staleness"
```

---

## Phase C — UI

### Task 16: `SelectionGeometry` extraction (targeted de-duplication, behavior-identical)

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Components/SelectionGeometry.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelector.razor.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelectorDialog.razor.cs`

**Interfaces:**
- Produces (pure static class + one record, namespace `BeamNG_LevelCleanUp.BlazorUI.Components`):

```csharp
/// <summary>Axis-aligned selection rect in source pixels — the backdrop box DTO used across UI/state.</summary>
public sealed record SelectionRect(int OffsetX, int OffsetY, int Width, int Height)
{
    public int Right => OffsetX + Width;
    public int Bottom => OffsetY + Height;
}

/// <summary>
///     Pure selection math shared by CropAnchorSelector and CropAnchorSelectorDialog (spec §5 de-duplication).
///     No Blazor dependencies — every method is a pure function.
/// </summary>
public static class SelectionGeometry
{
    // From the duplicated methods (identical copies verified at CropAnchorSelector.razor.cs:266/299/311
    // and CropAnchorSelectorDialog.razor.cs:185/225/233):
    public static int CalculateSelectionSizePixels(int targetSize, float metersPerPixel,
        float nativePixelSizeMeters, int originalWidth, int originalHeight);
    public static (int X, int Y) ClampOffsets(int offsetX, int offsetY, int selW, int selH,
        int originalWidth, int originalHeight);
    public static GeoBoundingBox? PixelRectToBoundingBox(GeoBoundingBox? original,
        int originalWidth, int originalHeight, int offsetX, int offsetY, int selW, int selH);
    // From GetSelectionStyleWithZoom (selector :563) / GetSelectionStyle (dialog :260) — returns null when off-screen:
    public static (double Left, double Top, double Width, double Height)? ComputeBoxRect(
        int offsetX, int offsetY, int selW, int selH, double baseScale, float zoomLevel,
        (float X, float Y) viewCenter, int originalWidth, int originalHeight, int displayWidth, int displayHeight);
    public static string ToCssStyle((double Left, double Top, double Width, double Height)? rect);  // "display: none;" for null
    public static (int X, int Y) ScreenDeltaToSourceDelta(double deltaX, double deltaY, double baseScale, float zoomLevel);

    // NEW for the backdrop box (Task 17):
    /// <summary>Clamp a backdrop rect: must contain the terrain rect, stay inside the mosaic, honor min size.</summary>
    public static SelectionRect ClampBackdropRect(SelectionRect rect, SelectionRect terrainRect,
        int originalWidth, int originalHeight);
    /// <summary>Apply a drag on a handle (or body move) to the rect, then clamp.</summary>
    public static SelectionRect ResizeBackdropRect(SelectionRect start, BackdropHandle handle,
        int sourceDeltaX, int sourceDeltaY, SelectionRect terrainRect, int originalWidth, int originalHeight);
    /// <summary>Default rect when the feature is first enabled: terrain rect inflated 25 % per side, clamped.</summary>
    public static SelectionRect DefaultBackdropRect(SelectionRect terrainRect, int originalWidth, int originalHeight);
}

public enum BackdropHandle { Body, N, S, E, W, NE, NW, SE, SW }
```

`ClampBackdropRect` rules: `OffsetX = clamp(OffsetX, 0, terrain.OffsetX)`, `Right ≥ terrain.Right` (i.e. `Width ≥ terrain.Right − OffsetX`), `Right ≤ originalWidth`; symmetric for Y. Zero margin on a side is legal (spec §5). `ResizeBackdropRect`: `Body` moves both offsets (size fixed) — clamping then squeezes the move so containment holds; edge handles move one border; corner handles two.

- [ ] **Step 1: Implement `SelectionGeometry`** by MOVING the duplicated method bodies verbatim (they are identical between the two components — see the duplication map in the exploration: `CalculateSelectionSizePixels`, `ClampOffsets`, `RecalculateSelectionBoundingBox`, style-with-zoom, screen-delta math).
- [ ] **Step 2: Refactor both components** to delegate: e.g. selector `CalculateSelectionSizePixels() => SelectionGeometry.CalculateSelectionSizePixels(TargetSize, MetersPerPixel, NativePixelSizeMeters, OriginalWidth, OriginalHeight);` — keep the public component API and all `_previous*` change-detection logic untouched. This is a targeted cleanup, NOT a component rewrite (spec §5).
- [ ] **Step 3: Build + manual parity:** run the app, load a multi-tile GeoTIFF, drag the terrain box in the inline selector AND the fullscreen dialog, type S/W/N/E values — behavior identical to before.
- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/SelectionGeometry.cs BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelector.razor.cs BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelectorDialog.razor.cs
git commit -m "refactor(ui): extract shared SelectionGeometry from crop selector components"
```

---

### Task 17: Backdrop box in `CropAnchorSelector` + `CropAnchorSelectorDialog`

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelector.razor` + `.razor.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/CropAnchorSelectorDialog.razor` + `.razor.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/CropDialogResult.cs`

**Interfaces:**
- New `[Parameter]`s on **both** components (dialog gets them via the `DialogParameters` initializer in `CropAnchorSelector.OpenFullScreenDialog`, `CropAnchorSelector.razor.cs:622-635`):

```csharp
[Parameter] public bool BackdropEnabled { get; set; }                      // false → nothing renders (default-off)
[Parameter] public SelectionRect? BackdropSelection { get; set; }
[Parameter] public EventCallback<SelectionRect> BackdropSelectionChanged { get; set; }   // selector only
```

- `CropDialogResult` (`Components/CropDialogResult.cs`) gains: `public SelectionRect? BackdropSelection { get; init; }`.

**Implementation notes:**

1. **Markup (both components):** inside the map surface div, after the terrain selection rect, render the backdrop box + 8 handles:

```razor
@if (BackdropEnabled && _backdropRect is { } bd)
{
    <div class="backdrop-selection" style="@SelectionGeometry.ToCssStyle(GetBackdropBoxRect())"
         @onmousedown="e => OnBackdropMouseDown(e, BackdropHandle.Body)"
         @onmousedown:stopPropagation>
    </div>
    @foreach (var handle in BackdropHandles)   // static array N,S,E,W,NE,NW,SE,SW
    {
        <div class="backdrop-handle backdrop-handle-@handle.ToString().ToLowerInvariant()"
             style="@GetBackdropHandleStyle(handle)"
             @onmousedown="e => OnBackdropMouseDown(e, handle)"
             @onmousedown:stopPropagation></div>
    }
}
```

CSS (append to each component's style block): `.backdrop-selection { position: absolute; border: 2px dashed var(--mud-palette-secondary); background: transparent; cursor: move; z-index: 9; pointer-events: auto; }`, `.backdrop-handle { position: absolute; width: 10px; height: 10px; background: var(--mud-palette-secondary); border: 1px solid white; z-index: 11; }` with per-handle `cursor` (`n-resize`, `ne-resize`, …). Handle positions from `GetBackdropBoxRect()` corners/edge midpoints, offset −5 px.

2. **Drag logic (code-behind, both):** mirror the existing terrain drag pattern (`_isDragging`, `_dragStart*` — `CropAnchorSelector.razor.cs:343-393`): `OnBackdropMouseDown(e, handle)` records `_backdropDragHandle = handle`, `_backdropDragStartRect = bd`, client start; the EXISTING `OnMinimapMouseMove`/`OnMouseMoveWithHitTest` gets a branch at the top: when `_backdropDragHandle != null`, compute `SelectionGeometry.ScreenDeltaToSourceDelta(...)` and set `_backdropRect = SelectionGeometry.ResizeBackdropRect(_backdropDragStartRect, handle, dx, dy, TerrainRect(), OriginalWidth, OriginalHeight)` (`TerrainRect()` = `new SelectionRect(CropOffsetX, CropOffsetY, SelectionWidthPixels, SelectionHeightPixels)`); mouse-up in the selector fires `BackdropSelectionChanged.InvokeAsync(_backdropRect)`, in the dialog just clears the drag state (result returned on Confirm, as with the terrain box).
3. **Terrain box moves re-clamp the backdrop box:** wherever the terrain offsets change (`SetCropOffsetsAsync`, drag mouse-up, `OnParametersSet` recenter), call `_backdropRect = SelectionGeometry.ClampBackdropRect(_backdropRect, TerrainRect(), OriginalWidth, OriginalHeight)` so containment is live (spec §5).
4. **Initialization:** when `BackdropEnabled` flips true and `BackdropSelection` is null/empty → `SelectionGeometry.DefaultBackdropRect(...)` + notify.
5. **Dialog S/W/N/E fields for the backdrop box** (spec §5): duplicate the four terrain `MudTextField T="string"` fields (`CropAnchorSelectorDialog.razor:28-67`) as a second labeled group ("Backdrop"), bound to `_backdropSouthStr` etc., `Immediate="false"`, parse with `double.TryParse(..., NumberStyles.Float, CultureInfo.InvariantCulture, ...)` (NEVER `MudNumericField` — the established invariant-culture convention). Apply: convert the entered bbox to a pixel rect by linear interpolation over `OriginalBoundingBox` (inverse of `SelectionGeometry.PixelRectToBoundingBox` — add `BoundingBoxToPixelRect` to `SelectionGeometry`), then `ClampBackdropRect`. Unlike the terrain fields this is a plain rectangular mapping — no MetersPerPixel/TargetSize re-derivation. Sync back after every drag via `UpdateBackdropBboxInputs()`.
6. **Dialog round-trip:** `OpenFullScreenDialog` passes `BackdropEnabled`/`InitialBackdropRect`; `Confirm()` puts `_backdropRect` into `CropDialogResult.BackdropSelection`; the selector applies it and fires `BackdropSelectionChanged`.
7. Add `public async Task SetBackdropSelectionAsync(SelectionRect rect, bool notifyChange = true)` on `CropAnchorSelector` (mirror of `SetCropOffsetsAsync` `:700`) — needed for preset restore (Task 19).

- [ ] **Step 1: Implement selector changes.**
- [ ] **Step 2: Implement dialog changes + `CropDialogResult`.**
- [ ] **Step 3: Build + manual check:** enable backdrop (temporarily via Task 18's panel, or set `BackdropEnabled="true"` in markup during development): box renders around the terrain square, all 8 handles resize with live clamping, body drag moves, terrain box drag pushes the backdrop box when they collide, dialog fields accept typed coordinates, `BackdropEnabled=false` renders nothing.
- [ ] **Step 4: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/
git commit -m "feat(backdrop): resizable backdrop selection box in crop selector and fullscreen dialog"
```

---

### Task 18: `BackdropSettingsPanel` + `GenerateTerrain` wiring + cost estimator

**Files:**
- Create: `BeamNG_LevelCleanUp/BlazorUI/Components/BackdropSettingsPanel.razor` + `.razor.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` (panel tag + selector params only — the page stays thin, spec §5)
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs` (thin handlers)

**Interfaces:**
- Panel parameters (follows the `BankingSettingsPanel` contract pattern — object + change callback + action callbacks):

```csharp
public partial class BackdropSettingsPanel : ComponentBase
{
    [Parameter] public BackdropSettings Settings { get; set; } = null!;      // _state.Backdrop
    [Parameter] public EventCallback SettingsChanged { get; set; }
    [Parameter] public bool Disabled { get; set; }                            // _isGenerating || _isAnalyzing
    [Parameter] public bool CanRegenerate { get; set; }
    [Parameter] public bool HasGeoTiffSource { get; set; }                    // hide panel content for PNG sources
    [Parameter] public EventCallback OnRegenerateBackdrop { get; set; }
    [Parameter] public EventCallback OnRemoveBackdrop { get; set; }
    [Parameter] public EventCallback OnUpdateEstimate { get; set; }
    [Parameter] public BackdropEstimateDisplay? Estimate { get; set; }
}

/// <summary>UI DTO for the cost estimate (filled by the page from BackdropGenerator.Estimate + tile count).</summary>
public sealed record BackdropEstimateDisplay(long Triangles, long TextureBytes, int TileCount, int ChunkCount)
{
    // Spec §5/§15 thresholds: yellow > 2 M tris or > 256 MB, red > 8 M tris or > 1 GB. Never blocks (D6).
    public Severity Severity =>
        Triangles > 8_000_000 || TextureBytes > 1L << 30 ? Severity.Error :
        Triangles > 2_000_000 || TextureBytes > 256L << 20 ? Severity.Warning : Severity.Info;
}
```

**Panel markup** — section shell exactly like the existing sections (`GenerateTerrain.razor:688-700` pattern: `MudPaper` + clickable header + `MudCollapse`), title "Backdrop (Experimental)", icon `Icons.Material.Filled.Landscape`. Content:
- `MudSwitch T="bool"` "Generate Backdrop" bound to `Settings.Enabled` with `@bind-Value:after="NotifyChanged"`.
- Numeric fields (all `MudNumericField`, `Variant.Text`, disabled via `Disabled`, **always rendered and only CSS-hidden while disabled** — the MudNumericField visibility rule documented at `GenerateTerrain.razor:738-744` applies: fields created after first render show empty until blur): Edge band (m, 0–1000, step 10), Max vertical error near/far (m, 0.1–50), Chunk size (m, 250–8000, step 250), Texel density (m/px, 0.25–16), Max chunk texture (`MudSelect`: 512/1024/2048/4096), Far raster cap (`MudSelect`: 2048/4096/8192/16384), `MudSwitch` Seam skirt.
- Help note (spec §10 caveat): `MudText Typo.caption`: "For a seamless look at the terrain edge, use a high satellite overlay blend in the BaseColor Manager — the backdrop is pure satellite imagery."
- Estimate row: "Update estimate" `MudButton` → `OnUpdateEstimate`; when `Estimate != null` render a `MudAlert` with `Estimate.Severity`: "≈ {Triangles:N0} triangles, {TextureBytes/1MB:N0} MB textures (uncompressed upper bound), {TileCount:N0} tile downloads, {ChunkCount} chunks".
- Buttons: "Regenerate Backdrop" (`Disabled="@(!CanRegenerate || Disabled)"`) → `OnRegenerateBackdrop`; "Remove Backdrop" (`Color.Error`, confirmation via `IDialogService.ShowMessageBox`) → `OnRemoveBackdrop`.

**Page wiring (`GenerateTerrain.razor`)** — the ONLY page changes (spec §4 "thin wiring"):
1. Selector tag (`:401-444`): add `BackdropEnabled="@_state.Backdrop.Enabled" BackdropSelection="@BackdropSelectionFromState()" BackdropSelectionChanged="OnBackdropSelectionChanged"`.
2. New section tag after Bridges & Tunnels:

```razor
<BackdropSettingsPanel Settings="@_state.Backdrop"
                       SettingsChanged="OnBackdropSettingsChanged"
                       Disabled="@(_isGenerating || _isAnalyzing)"
                       CanRegenerate="@BackdropOrchestrator.CanRegenerate(_state)"
                       HasGeoTiffSource="@(_heightmapSourceType != HeightmapSourceType.Png)"
                       OnRegenerateBackdrop="RegenerateBackdrop"
                       OnRemoveBackdrop="RemoveBackdrop"
                       OnUpdateEstimate="UpdateBackdropEstimate"
                       Estimate="@_backdropEstimate" />
```

3. Code-behind handlers (each ≤ 15 lines):
   - `private SelectionRect? BackdropSelectionFromState() => _state.Backdrop.HasSelection ? new SelectionRect(_state.Backdrop.OffsetX, _state.Backdrop.OffsetY, _state.Backdrop.Width, _state.Backdrop.Height) : null;`
   - `OnBackdropSelectionChanged(SelectionRect r)` → copy into `_state.Backdrop.OffsetX/…/Height`, recompute `_state.Backdrop.BoundingBox` via `SelectionGeometry.PixelRectToBoundingBox`;
   - `RegenerateBackdrop()` → `_isGenerating` guard + `await new BackdropOrchestrator().RegenerateStandaloneAsync(_state)` + snackbar (mirror `RegenerateDecalRoads`, `GenerateTerrain.razor.cs:3069-3164`);
   - `RemoveBackdrop()` → `BackdropOrchestrator.RemoveBackdrop(_state.WorkingDirectory)` + snackbar;
   - `UpdateBackdropEstimate()` → build parameters via the orchestrator's builder on `Task.Run` (expose `BackdropOrchestrator.BuildParameters(state, heightMapOrNull)` as `internal static` for this reuse; heightmap may be a zero `float[size,size]` for estimation — the estimate never touches the seam) → `new BackdropGenerator().Estimate(...)` + tile count = `MapTileOverlayService.CountTilesForBounds(backdropBounds, mpp)` minus the already-cached tiles (count `*.img` files under `{level}\MT_Tiles\cache\{providerSlug}\{zoom}\` for the same zoom — spec §5 "minus already-cached count") → `_backdropEstimate = new(...)`.

- [ ] **Step 1: Implement panel + page wiring.**
- [ ] **Step 2: Build + manual check:** panel renders collapsed by default; enable switch shows the backdrop box in the selector; estimate button produces numbers; generate with backdrop enabled on a small map end-to-end → `MT_backdrop` DAEs + textures + scene entries exist; disable → next terrain run leaves the existing backdrop untouched.
- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/BackdropSettingsPanel.razor BeamNG_LevelCleanUp/BlazorUI/Components/BackdropSettingsPanel.razor.cs BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor.cs
git commit -m "feat(backdrop): settings panel with cost estimator and thin GenerateTerrain wiring"
```

---

### Task 19: Preset round-trip (spec §11)

**Files:**
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetExporter.razor` (`BuildAppSettings`, `:477-546`)
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetImporter.razor` (parse blocks near `:679-800`)
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Components/TerrainPresetResult.cs`
- Modify: `BeamNG_LevelCleanUp/BlazorUI/Pages/GenerateTerrain.razor` (exporter tag `:92-141`) + `.razor.cs` (`OnPresetImported` `:2104`, `ApplyPendingCropOffsets` `:2399-2424`)

**Changes:**
1. Exporter: new `[Parameter] public BackdropSettings? Backdrop { get; set; }`; page passes `Backdrop="@_state.Backdrop"`. In `BuildAppSettings` add (camelCase like `cropSettings`):

```csharp
            ["backdropSettings"] = Backdrop == null ? null : new JsonObject
            {
                ["enabled"] = Backdrop.Enabled,
                ["offsetX"] = Backdrop.OffsetX,
                ["offsetY"] = Backdrop.OffsetY,
                ["width"] = Backdrop.Width,
                ["height"] = Backdrop.Height,
                ["edgeBandMeters"] = Backdrop.EdgeBandMeters,
                ["maxVerticalErrorNearMeters"] = Backdrop.MaxVerticalErrorNearMeters,
                ["maxVerticalErrorFarMeters"] = Backdrop.MaxVerticalErrorFarMeters,
                ["chunkTargetMeters"] = Backdrop.ChunkTargetMeters,
                ["texelDensityNearMPerPx"] = Backdrop.TexelDensityNearMPerPx,
                ["maxChunkTextureSize"] = Backdrop.MaxChunkTextureSize,
                ["maxFarRasterDimension"] = Backdrop.MaxFarRasterDimension,
                ["seamSkirt"] = Backdrop.SeamSkirt
            },
```

2. `TerrainPresetResult`: nullable fields (absent-in-preset ⇒ don't touch state, the file's convention): `public bool? BackdropEnabled { get; set; }`, `public int? BackdropOffsetX { get; set; }`, `…OffsetY/Width/Height`, `public double? BackdropEdgeBandMeters { get; set; }`, `…` (one per exported scalar), `public bool? BackdropSeamSkirt { get; set; }`.
3. Importer: parse block mirroring `cropSettings` (`TerrainPresetImporter.razor:789-800` `if (node["x"] != null) result.X = node["x"]!.GetValue<T>();` per field).
4. `OnPresetImported`: apply scalars directly to `_state.Backdrop`; the RECT uses the deferred pattern because the selector recenters when GeoTIFF metadata loads (`GenerateTerrain.razor.cs:2133-2158` comment): store `private SelectionRect? _pendingBackdropRect;` alongside `_pendingCropOffsets`; in `ApplyPendingCropOffsets()` (which already runs after the selector is rendered, `:2399-2424`), after `SetCropOffsetsAsync` add:

```csharp
        if (_pendingBackdropRect is { } backdropRect && _cropAnchorSelector != null)
        {
            await _cropAnchorSelector.SetBackdropSelectionAsync(backdropRect);
            _pendingBackdropRect = null;
        }
```

   (Same source-pixel fragility as crop offsets — mitigated by the existing copy-tiles-into-preset-folder behavior, spec §11.)

- [ ] **Step 1: Implement all four files.**
- [ ] **Step 2: Build + manual check:** export a preset with backdrop enabled → JSON contains the block; fresh app session → import → backdrop switch, tunables and box position all restored; import a PRE-BACKDROP preset → `_state.Backdrop` stays at defaults (enabled=false).
- [ ] **Step 3: Commit**

```bash
git add BeamNG_LevelCleanUp/BlazorUI/Components/ BeamNG_LevelCleanUp/BlazorUI/Pages/
git commit -m "feat(backdrop): preset export/import round-trip with deferred rect apply"
```

---

## Phase D — Verification

### Task 20: Full verification, docs, handoff

**Files:**
- Modify: `ai_docs/2026-07-27 Backdrop/00-status-and-handoff.md` (session log + doc index)

- [ ] **Step 1: Full test suite**

Run: `dotnet test BeamNgTerrainPoc.Tests/BeamNgTerrainPoc.Tests.csproj`
Expected: previous count (~1069) + all new Backdrop tests, zero failures. Existing tests untouched proves the byte-identical-baseline constraint for the core.

- [ ] **Step 2: Full solution build**

Run: `dotnet build BeamNG_LevelCleanUp.sln`
Expected: no `error CS`. (DLL-lock/MSB errors while the app is running are environmental noise.)

- [ ] **Step 3: Default-off regression check**

Generate a terrain on a test map with the backdrop switch OFF (the default): no `MT_backdrop` folder, no `BackdropSettings` block in `MT_settings.json`, no new PubSub messages — the run is indistinguishable from pre-feature behavior.

- [ ] **Step 4: Manual validation checklist** (user, spec §13 — record outcomes in the handoff doc)

In-app: enable backdrop → select box → generate → chunk DAEs/textures/scene entries present; Regenerate Backdrop (same session + after app restart); Remove Backdrop cleans everything incl. the SimGroup line and settings block; BaseColorManager provider switch + Reset & Rebake rebake the backdrop textures; staleness banner appears after a georeference change; preset round-trip.
In-game (cannot be unit-tested — spec §13 + open watch items): drive across the seam on all four sides (no step, no gap — watch the "last half-cell" item from the plan header); collision everywhere on the backdrop incl. coarse far-field triangles; distant chunks stay visible (LOD pixel size 2 / no nulldetail decision); no hairline cracks at distance (skirt sufficiency); texture look vs. terrain basecolor at the boundary (blend < 100 % caveat); UV orientation (if textures are mirrored north-south, set `FlipUVVertical = true` in the export options — single-line fix flagged in Task 8).

- [ ] **Step 5: Update the handoff doc**

Add a session entry to `00-status-and-handoff.md`: tasks completed, test count, open validation items; set `02-implementation-plan.md` status to "in execution"/"done" in the doc index.

- [ ] **Step 6: Commit**

```bash
git add "ai_docs/2026-07-27 Backdrop/00-status-and-handoff.md"
git commit -m "docs(backdrop): record implementation session results in handoff"
```

---

## Spec coverage self-review (§ → task)

| Spec section | Covered by |
|---|---|
| §2 D1 V1 scope + V2 prep (importance list, RoadNetwork param, compositor-ready baker) | 1 (`RoadNetwork` field), 6 (`IBackdropImportanceSource`), 13 (baker isolated per chunk) |
| §2 D2 pure C# restricted quadtree | 6–8 (no native deps beyond already-present GDAL for IO) |
| §2 D3 free rectangle containing terrain, clamped to mosaic | 1 (validation), 16–17 (UI clamping) |
| §2 D4 full collision, chunked | 7–9 (colmesh clone per chunk, skirt excluded) |
| §2 D5 auto rebake together | 13, 15 |
| §2 D6 no size limit, estimator + warnings | 10 (`Estimate`), 18 (panel; never blocks) |
| §2 D7 hybrid architecture, standalone re-run | 10 (core entry), 14 (in-run + standalone) |
| §2 D8 fully optional, default off | 11 (`Enabled=false`), 14 (gate), 20 (regression check) |
| §5 data model + validation + UI + estimator | 11, 1, 16–18 |
| §6 two rasters + nodata | 3, 9 (loader in Task 10), 10 |
| §7 seam correctness 1–5 | 4 (snap/blend/datum), 2 (horizontal datum), 8 (skirt) |
| §8 adaptive mesh, crack-free, chunk borders, output per chunk | 5–8 |
| §9 DAE/scene output conventions | 9 |
| §10 OverlayRequest, baker, MtSettings contract, BaseColorManager behavior, caveat note | 12, 13, 11, 15, 18 (help note) |
| §11 orchestration, presets, error table | 14, 19, (error table: 1/3/9/10/13/14) |
| §12 V2 prep | 1, 6, 13 (noted, not implemented) |
| §13 test suite | Tasks 1–10 test files; manual checklist Task 20 |
| §14 named risks 1–11 | 4/2/8/18-note/18-estimator/3/9-loddecision/14-debug-subfolder/19/plan-structure/11-Reset |
| §15 defaults | 1 (parameter defaults), 11 (state defaults) |

## Execution notes

- Tasks 1–10 are strictly sequential (each consumes the previous interfaces). Tasks 11–15 sequential. Task 16 is independent of 11–15 and may run in parallel with them; 17 needs 16; 18 needs 14+17; 19 needs 18.
- The mesher tasks (6–8) are the highest-effort items; if Task 6's border-constrained splitting proves gnarly, the fallback that preserves ALL invariants is: snap chunk sizes to powers of two in the planner (Task 5) and use pow2 midpoint splits — the tests stay identical.
- When a step's exact line numbers have drifted (this plan pins them to the `feature/backdrop` branch point), search for the quoted identifiers instead — every referenced symbol name is verbatim from the codebase.







