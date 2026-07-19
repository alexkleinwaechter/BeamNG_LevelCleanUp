# TerrainHoleCutter — Standalone Class Design

> Date: 2026-07-18 · Part of `01-tunnel-implementation-plan.md` Phase 1 (consumed by Phase 4).
> Verified facts in `00-current-state-and-reuse-map.md` §5.

## Purpose & scope

One reusable, source-agnostic class that stamps terrain holes (material index **255**) into the
generated `.ter`'s material layer grid. Consumers:

1. **Now**: tunnel portal holes (`TunnelPortalHoleProvider`, plan Phase 4).
2. **Later**: user-imported hole maps — the preset system already round-trips a
   `{TerrainName}_holemap.png` (`TerrainPresetResult.HoleMapPath`) that nothing consumes today.
3. **Later**: possible manual hole shapes (JSON rectangles/circles/polygons, as in the old editor-
   plugin plan) — same core API.

The cutter itself knows nothing about tunnels, PNGs, or presets — providers do; the cutter only
stamps cells. That separation is the whole point (user requirement: own class, reusable for hole-map
import).

## How holes work (recap of verified facts)

- BeamNG terrain hole = per-cell material byte `255` in the `.ter` layer map (V9 format).
- `Grille.BeamNG.Lib` already models this: `TerrainData.IsHole` (first-class field), serializer
  writes `IsHole → 255` and reads `255 → IsHole=true, Material=0`
  (`TerrainV9Serializer.cs:87-100, 124`); round-trip is lib-tested.
- Our pipeline's mutable grid: `byte[] materialIndices`, flat, `index = y*size + x`,
  **y=0 = bottom/south** (BeamNG space), created at `TerrainCreator.cs:579`, consumed by the fill
  loop at `:711-716` which currently hardcodes `IsHole = false`.
- Read-side code is already hole-aware (`LayerMaskReader`, `TerrainPbrMapBuilder`,
  `TerrainSpikeValidator` preserves bytes). Nothing writes holes today.

## Class design

Location: `BeamNgTerrainPoc/Terrain/Processing/TerrainHoleCutter.cs` (next to
`MaterialLayerProcessor`, which owns the same grid convention).

```csharp
namespace BeamNgTerrainPoc.Terrain.Processing;

/// <summary>
///     Stamps terrain holes (material index 255) into the flat material-index grid that
///     TerrainCreator serializes into the .ter layer map. Source-agnostic: callers (tunnel portal
///     provider, hole-map import) compute the cells; this class only validates and stamps.
///     Grid convention: row-major, index = y * size + x, y = 0 at the BOTTOM/south edge
///     (BeamNG terrain space, matching MaterialLayerProcessor / HeightmapProcessor).
/// </summary>
public static class TerrainHoleCutter
{
    /// <summary>BeamNG hole sentinel in the .ter layer map. Terrain materials must use 0..254.</summary>
    public const byte HoleMaterialIndex = byte.MaxValue; // 255

    /// <summary>Stamps the given cells as holes. Out-of-bounds cells are skipped (counted).
    /// Returns the number of cells newly converted to holes (already-hole cells don't count).</summary>
    public static HoleCutResult Apply(byte[] materialIndices, int size,
        IEnumerable<(int X, int Y)> holeCells);

    /// <summary>Mask overload: mask[y, x] == true ⇒ hole. Mask dimensions must equal size×size.</summary>
    public static HoleCutResult Apply(byte[] materialIndices, int size, bool[,] holeMask);

    /// <summary>Loads a hole mask from a hole-map PNG (image space, y-down) into terrain space
    /// (y-up, same flip as MaterialLayerProcessor.ProcessRow). Pixel < 128 luminance = hole when
    /// blackMeansHole (see polarity warning in doc). Throws if dimensions ≠ size×size.</summary>
    public static bool[,] LoadHoleMask(string pngPath, int size, bool blackMeansHole = true);
}

public sealed record HoleCutResult(int CellsStamped, int CellsAlreadyHole, int CellsOutOfBounds);
```

Design points:

- **Pure grid mutation, no I/O in `Apply`** — trivially unit-testable, no pipeline coupling.
- **Idempotent** — re-stamping a hole cell is a counted no-op (`CellsAlreadyHole`), so multiple
  providers (tunnel portals + imported map) can run in sequence.
- **`HoleCutResult`** feeds the `[TUNNEL-HOLE]` / import logging; callers decide severity.
- **No unstamping in v1** (no restore/undo — regeneration always starts from a fresh grid).
- SkiaSharp for the PNG load (`LoadHoleMask`), consistent with `MaterialLayerProcessor`'s L8
  handling (>127 = claimed there; here <128 = hole under `blackMeansHole`).

## Coordinate contract for providers

- Grid cell ⇄ terrain-local meters: `pixel = meters / MetersPerPixel` (bottom-left origin).
  Spline / cross-section / `StructureSegment` positions are **already terrain-local meters** — only
  the division is needed (the `MaterialPainter` pattern, :228-257).
- World-space inputs (future JSON shapes): `BeamNgCoordinateTransformer.WorldToTerrain(...)`
  (`Terrain/Utils/BeamNgCoordinateTransformer.cs:82`) first, then divide.
- Image-space inputs (PNG): y-flip via `flippedY = size - 1 - y` — done inside `LoadHoleMask`.

## Pipeline integration (Phase 1 deliverable)

In `TerrainCreator.CreateTerrainFileAsync`:

1. **Insertion window**: after `BridgeUnderDeckMaterialPainter.Paint` (`:593-603`), before
   `new Grille.BeamNG.Terrain(...)` (`:608`). Holes must be stamped **last** among material-grid
   mutations so no painter overwrites them.
   ```csharp
   // (Phase 4 wires the provider; Phase 1 only creates the hook)
   var holeResult = TunnelPortalHoleProvider.CutPortalHoles(
       unifiedResult.Network, heightMap2D, materialIndices,
       parameters.Size, parameters.MetersPerPixel, parameters.TunnelRules, logger);
   ```
2. **Fill-loop hardening** (`:711-716`) — make the abstract model truthful instead of relying on
   the serializer's cast of a fake material 255:
   ```csharp
   var isHole = materialIndices[i] == TerrainHoleCutter.HoleMaterialIndex;
   terrain.Data[i] = new TerrainData
   {
       Height = height,               // heights under holes stay meaningful (portal floor)
       Material = isHole ? 0 : materialIndices[i],
       IsHole = isHole
   };
   ```
3. **Constant unification**: replace the private `HoleMaterialIndex = 255` constants in
   `LayerMaskReader.cs:13` and `TerrainPbrMapBuilder.cs:12` with references to
   `TerrainHoleCutter.HoleMaterialIndex` (TerrainPbrMapBuilder lives in BeamNG_LevelCleanUp, which
   already references BeamNgTerrainPoc — verify; otherwise leave a doc comment cross-link).
4. **Validator guard**: `TerrainValidator` warns when `Materials.Count > 255 - 1` (index 255
   reserved for holes). Today nothing stops material index 255 from silently becoming a hole.

## What must keep working (verified non-issues, keep tested)

- MapShrink / CopyAssets / CopyTerrains never re-serialize `.ter` binaries — unaffected.
- `TerrainSpikeValidator.ValidateAndFix` preserves `MaterialData` byte-for-byte — holes survive.
- Basecolor Manager (`TerrainPbrMapBuilder`) already renders holes transparent.
- `Grille.BeamNG.Terrain.Draw` **drops `IsHole`** (`Terrain.cs:167-171`) — not on the generation
  path; add a doc comment there rather than changing lib behavior.

## Hole-map PNG polarity — open question (blocks import feature only)

Two contradictory statements exist in-repo:
- Editor-plugin tutorial (`terrain_holes_editor_plugin_plan.md:46-53`): **black = hole**, white =
  solid (`*_holeMap.png` from `tb:exportHoleMaps`).
- `TerrainPresetExporter.razor:238-245`: always writes an **all-black** PNG, commented
  "black = no holes".

If the tutorial is right, our exported preset hole maps have been declaring all-hole terrain (inert
today because nothing consumes them — but the import feature would make it live).
**Action before shipping import**: export a hole map from a vanilla map with known holes in the
BeamNG editor and inspect. `LoadHoleMask(blackMeansHole)` keeps polarity explicit either way;
fix the preset exporter's comment/output in the same change. Portal cutting (Phase 4) is unaffected
— it writes grid bytes, never PNGs.

## Tests (`BeamNgTerrainPoc.Tests/Processing/TerrainHoleCutterTests.cs`)

- `Apply` cells: stamps 255, counts new vs already-hole vs out-of-bounds, leaves other cells
  untouched (full-grid assert).
- Mask overload: dimension mismatch throws; y-orientation fixed by round-trip with a marked corner.
- `LoadHoleMask`: y-flip correctness (asymmetric fixture PNG), polarity flag honored.
- Integration: stamp → `Terrain` fill (hardened loop) → `TerrainSerializer.Serialize` →
  `Deserialize` → `IsHole == true`, `Material == 0`, height preserved (extends the lib's own
  round-trip test at the pipeline level).
- Baseline: no provider active ⇒ grid and `.ter` bytes identical to pre-change output.
