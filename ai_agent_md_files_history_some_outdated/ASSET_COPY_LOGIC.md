# Asset Copy Logic

This document describes the asset and terrain material copy pipelines used by the **Copy Assets** and **Copy Terrains** pages.

---

## Entry Points

| Page | Razor code-behind | BeamFileReader scan method | Copy orchestrator |
|---|---|---|---|
| **Copy Assets** | `CopyAssets.razor.cs` | `ReadAssetsForCopy()` | `AssetCopy` (delegates to `MaterialCopier`, `DaeCopier`, etc.) |
| **Copy Terrains** | `CopyTerrains.razor.cs` | `ReadTerrainMaterialsForCopy()` | `AssetCopy` (delegates to `TerrainMaterialCopier` + `GroundCoverCopier`) |

Both pages share the same high-level flow:

1. Extract source level ZIP to `_copyFrom`, target level ZIP to `_unpacked`.
2. `BeamFileReader` scans the source, building `CopyAsset` items with `MaterialJson` + `MaterialFile` lists.
3. User selects items in the UI grid.
4. `Reader.DoCopyAssets(selectedIds)` triggers the copy via `AssetCopy`.

---

## Shared Infrastructure

### PathConverter

Converts between Windows filesystem paths and BeamNG JSON paths.

| Method | Purpose |
|---|---|
| `GetTargetFileName(source)` | Maps a source file path to the target level, inserting `MT_<sourceLevel>` after `art/` to isolate copied assets. E.g. `art/shapes/building/tex.png` becomes `art/MT_source/shapes/building/tex.png`. |
| `GetTerrainTargetFileName(source)` | Simpler variant for terrain textures: places files directly in `art/terrains/`. |
| `GetBeamNgJsonPathOrFileName(windowsPath)` | Converts a Windows path to a BeamNG JSON path (`levels/levelname/...`). **Strips `.link` extension** because materials.json should never reference `.link`. |

### FileCopyHandler

Copies a file from source to target with a multi-level fallback:

```
File.Copy(source, target)          -- direct filesystem copy
    |
    v  (on failure)
TryExtractFromContentZip()         -- BeamNG content ZIPs (assets.zip etc.)
    |
    v  (on failure)
TryExtractFromLevelZip()           -- level-specific ZIPs from BeamNG install
    |
    +--> Direct ZIP entry extract
    |
    +--> TryExtractImageWithExtensions()
             tries .png, .dds, .jpg, .jpeg
             AND their .link variants (.png.link, .dds.link, ...)
```

**Important .link handling**: When `TryExtractImageWithExtensions` finds a `.link` file in the ZIP, the `.link` extension is **appended** to the target path (not substituted). This preserves the full extension chain so BeamNG can resolve it:

```
target.png  +  extracted .png.link  -->  target.png.link   (correct)
target.png  +  Path.ChangeExtension  -->  target.link      (WRONG - old bug)
```

### FileUtils

Static helpers for `.link` file handling:

- `IsLinkFile(path)` -- checks if path ends with `.link`
- `StripLinkExtension(path)` -- removes trailing `.link`
- `ResolveImageFileName(path)` -- finds the actual file on disk by trying multiple image extensions and their `.link` variants. Fallback order: direct file, `file.link`, then loop through `.dds`, `.png`, `.jpg`, `.jpeg` (each with and without `.link`).

### LinkFileResolver

Resolves `.link` files to actual content by reading the JSON inside the `.link` file (which contains a `path` property pointing to an asset in the game's content ZIPs) and extracting the asset via `ZipAssetExtractor`.

---

## Copy Assets Pipeline (CopyAssets.razor.cs)

### Scanning

`BeamFileReader.ReadAssetsForCopy()` scans the source level:
- Walks the scene hierarchy (`items.level.json` NDJSON files)
- For each `TSStatic` / `Prefab` / `DecalRoad`, builds a `CopyAsset` with associated `MaterialJson` entries
- `MaterialScanner` resolves texture files for each material via `FileUtils.ResolveImageFileName`

### Copying

`AssetCopy.Copy()` orchestrates per-asset type:

**For regular materials** (`MaterialCopier`):
1. Reads source `materials.json` (cached per batch).
2. For each `MaterialFile`, copies the texture via `PathConverter.GetTargetFileName()` + `FileCopyHandler.CopyFile()`.
3. Updates texture paths in the material JSON using string replacement (`GetBeamNgJsonPathOrFileName` on both old and new paths).
4. Writes the material to the target's `main.materials.json` (under the `art/MT_<source>/...` directory tree).

**For DAE files** (`DaeCopier`):
- Copies `.dae`/`.cdae` files to the target with the `MT_<source>` path prefix.

**Path isolation**: The `MT_<sourceLevel>` prefix is inserted right after `art/` in the target path. This prevents material name collisions between different source levels.

---

## Copy Terrains Pipeline (CopyTerrains.razor.cs)

### Scanning

`BeamFileReader.ReadTerrainMaterialsForCopy()`:
1. Finds `*.materials.json` in `art/terrains/` of the source level.
2. `TerrainCopyScanner.ScanTerrainMaterials()` parses each `TerrainMaterial` entry.
3. `ScanTextureFilesFromProperties()` dynamically discovers texture references by checking any property ending with `Tex` or `Map`.
4. Each texture is resolved to a `MaterialFile` with `OriginalJsonPath` (the path as it appears in JSON) and `File` (the resolved `FileInfo`).
5. Color and roughness extraction from the source `.ter` binary file (via `TerrainColorExtractor` / `TerrainRoughnessExtractor`) populates default color/roughness values for each material.

### Copying

`TerrainMaterialCopier.Copy()`:
1. Loads the target's `art/terrains/main.materials.json` (or creates it).
2. For each terrain material:
   - Generates a new suffixed name: `MaterialName_<sourceLevel>`, new GUID.
   - Delegates texture copying to `TerrainTextureHelper.CopyTerrainTextures()`.
   - Writes the updated material JSON to the target.

**TerrainTextureHelper.CopyTerrainTextures()**:

For each `MaterialFile`:
- **Replaceable textures** (base color, roughness, normal, AO, height base textures): generates solid-color placeholder PNGs at terrain size using `TerrainTextureGenerator`. User-selected color and roughness values are applied.
- **Detail textures** (all other `*Tex`/`*Map` properties): copied from source with a level-name suffix appended to the filename.

The suffix logic handles `.link` files:
```
source: texture.png.link
  strip .link  -->  texture.png
  split        -->  name="texture", ext=".png"
  suffix       -->  texture_source.png
  re-add .link -->  texture_source.png.link
```

Path updates in the material JSON are collected as a batch dictionary (`originalPath -> newPath`) and applied in a single pass via `UpdateTexturePathsInMaterialBatch()`.

### GroundCover Copying

After terrain materials are copied, `GroundCoverCopier` handles associated vegetation:

1. **Phase 1 - Collect**: `CollectGroundCoversForTerrainMaterials()` scans all groundcover definitions (from `.forestbrushes4.json`). If any groundcover `Type` references a copied terrain material's `layer` name, it's marked for copying.

2. **Phase 2 - Write**: `WriteAllGroundCovers()` processes all collected groundcovers:
   - Copies the groundcover's material via `MaterialCopier` (textures go to `art/MT_<source>/shapes/groundcover/`).
   - Copies referenced DAE files via `DaeCopier`.
   - Suffixes all `layer` names and the groundcover `name` (prefixed with `gc_`).
   - Updates `shapeFilename` paths to point to the copied DAE files.
   - Writes all groundcovers to the target's vegetation NDJSON file.

`GroundCoverDependencyHelper` provides the same dependency-copy logic for the replace flow (`GroundCoverReplacer`).

---

## .link File Handling Summary

`.link` files are BeamNG's virtual filesystem redirects. A `.link` file contains JSON pointing to an asset inside the game's content ZIP archives.

### Naming Convention

BeamNG expects `.link` files to have the **full original extension** preserved:
```
texture.png       -- referenced in materials.json
texture.png.link  -- the .link redirect file on disk (correct)
texture.link      -- WRONG: BeamNG won't resolve this for a .png reference
```

### Where .link is handled in the copy pipeline

| Location | What it does |
|---|---|
| `FileUtils.ResolveImageFileName()` | Discovers `.link` files during scanning by trying `path + ".link"` and extension variants |
| `FileUtils.StripLinkExtension()` | Removes `.link` suffix for path manipulation |
| `FileUtils.IsLinkFile()` | Checks if a path ends with `.link` |
| `PathConverter.GetBeamNgJsonPathOrFileName()` | Strips `.link` before building JSON paths (materials.json must not reference `.link`) |
| `TerrainTextureHelper.CopyTerrainTextures()` | Strips `.link` for suffix insertion, then re-appends `.link` |
| `FileCopyHandler.TryExtractFromLevelZip()` | Appends `.link` to target (not replace) when extracting `.link` files from ZIPs |
| `LinkFileResolver.GetFileStream()` | Resolves `.link` content from game ZIPs for image processing (color/roughness extraction) |

### Key invariant

**The materials.json always references paths WITHOUT `.link`**. The `.link` extension only exists on the filesystem as a redirect mechanism. All `GetBeamNgJsonPathOrFileName()` calls strip `.link` before producing JSON paths.

---

## Wizard Mode

Both Copy Assets and Copy Terrains support a wizard flow (from `CreateLevel` page):

1. Levels are pre-loaded from `CreateLevelWizardState` (no manual ZIP selection needed).
2. After copying, wizard state is updated (`CopiedTerrainMaterials`, `CopiedAssets`).
3. User can select additional sources without re-selecting the target ("Select Another Source" button).
4. Navigation proceeds to the next wizard step when finished.

The wizard also sets `PathResolver.WizardTerrainSize` during terrain copy so that `TerrainTextureHelper` can use the wizard's terrain size for placeholder texture generation.
