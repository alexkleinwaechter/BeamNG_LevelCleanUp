# fbxtodae

Batch-converts FBX 3D assets to BeamNG-compatible Collada (DAE) files and emits a matching `main.materials.json`.

Part of the `BeamNG_LevelCleanUp` solution. Personal-use tool — not shipped to end users.

## What it does

For every `*.fbx` in the source folder, the tool:

1. Loads the FBX via [AssimpNet](https://www.nuget.org/packages/AssimpNet) — triangulates, generates smooth normals if missing, pre-transforms the node hierarchy, and bakes the GlobalScale.
2. Rotates the geometry from FBX's Y-up right-handed system into BeamNG's Z-up right-handed system (`(x, y, z) → (x, -z, y)`). UVs are left unchanged — FBX and BeamNG share the DirectX V convention.
3. Writes a BeamNG-compatible `Z_UP` DAE with the required `base00 / start01` scene tree (see [DAE structure](#dae-structure) below), including a `Colmesh-1` collision node.
4. Finds the matching textures in the texture source folder, renames them to BeamNG's texture-cooker convention, and copies them into the target folder.
5. Registers one material entry (`class: "Material"`, `version: 1.5` — PBR) in `main.materials.json` next to the DAEs.

## DAE structure

The emitted DAE follows the BeamNG scene hierarchy required for collision and LOD loading:

```
base00
└── start01
    ├── Colmesh-1            -> collision geometry (same mesh, materials stripped)
    └── {name}_a100          -> single LOD node (pixel threshold 100, see note)
```

- **`Colmesh-1`** — a direct merge of the FBX geometry without materials. The user requirement was "no simplification; just take the main mesh" — so visual and collision share the same triangle set. BeamNG recognises the node name `Colmesh-1` as a collision primitive.
- **`{name}_a100`** — single LOD level at `SingleBuilding.Lod0PixelSize = 100` px. Below 100 px on screen the object is hidden. The `{name}` used inside the DAE is the FBX base name with digits mapped to letters (`bungalow1` → `bungalowb`) to keep BeamNG's LOD-name parser happy — the filename and material bindings stay as the original name.
- **Multi-LOD, `nulldetail{N}`, LOD1/2 with progressively simpler meshes** — deliberately not implemented. Add later if performance demands it.

## Usage

```
fbxtodae <fbxSourceDir> <textureSourceDir> <targetDir>
```

| Arg | Meaning |
|---|---|
| `fbxSourceDir` | Folder containing `*.fbx` files (non-recursive). |
| `textureSourceDir` | Folder containing textures named `{fbxName}_d.<ext>` (diffuse) and `{fbxName}_n.<ext>` (normal). |
| `targetDir` | Output folder. Ideally inside a BeamNG level at `…/levels/<levelName>/art/shapes/<folderName>/` so the tool can emit BeamNG-absolute texture paths. |

### Example

```powershell
dotnet run --project fbxtodae -- `
  "D:/Source/beamng_mapping_pro/examples_for_ai/UK_houses_3dassets/bungalow/Models" `
  "D:/Source/beamng_mapping_pro/examples_for_ai/UK_houses_3dassets/bungalow/Textures" `
  "C:/Users/$env:USERNAME/AppData/Local/BeamNG/BeamNG.drive/current/levels/rochester/art/shapes/bungalow"
```

Drop `TSStatic` scene objects with `shapeName = "/levels/rochester/art/shapes/bungalow/bungalow1.dae"` in the BeamNG World Editor and they'll render with textures.

## Input texture naming convention

For an FBX named `bungalow1.FBX` the tool looks for (extensions tried in order, case-insensitive):

- Diffuse: `bungalow1_d.png`, `bungalow1_d.jpg`, `bungalow1_d.jpeg`, `bungalow1_d.tga`, `bungalow1_d.dds`
- Normal: `bungalow1_n.png`, …

If nothing is found **and** the FBX base name ends with a lowercase `m`, the tool retries once with the trailing `m` stripped. This handles the common "mirrored variant reuses base textures" convention (e.g., `terraced7am.FBX` reuses `terraced7a_d.png` / `terraced7a_n.png`). Packs where the `*m` variant ships its own textures (e.g., `bungalow1bm_d.png`) are unaffected because the primary lookup hits first.

## Output texture naming (BeamNG cooker convention)

PNG textures are renamed on copy per [BeamNG's texture cooker](../ai_agent_md_files_history_some_outdated/BeamNG_Materials_Documentation.md#texture-cooker-suffixes):

| Source                | Copied as                   | Engine compiles to |
|-----------------------|-----------------------------|--------------------|
| `bungalow1_d.png`     | `bungalow1.color.png`       | `BC7 sRGB` DDS     |
| `bungalow1_n.png`     | `bungalow1.normal.png`      | `BC5` DDS          |
| *(future)* grayscale  | `bungalow1.data.png`        | `BC4/BC7` linear   |

Non-PNG textures (DDS, JPG, TGA) are copied as-is — they either bypass the cooker (DDS is already compressed) or aren't cooker input formats.

## Output layout

```
<targetDir>/
├── <name1>.dae
├── <name1>.color.png
├── <name1>.normal.png
├── <name2>.dae
├── …
└── main.materials.json
```

`main.materials.json` (abridged):

```json
{
  "bungalow1": {
    "class": "Material",
    "name": "bungalow1",
    "mapTo": "bungalow1",
    "internalName": "bungalow1",
    "persistentId": "4ebaef43-…",
    "version": 1.5,
    "Stages": [
      {
        "baseColorMap": "/levels/rochester/art/shapes/bungalow/bungalow1.color.png",
        "normalMap":    "/levels/rochester/art/shapes/bungalow/bungalow1.normal.png"
      },
      {}, {}, {}
    ]
  }
}
```

## BeamNG-absolute path derivation

The tool detects a `levels` segment in the target path (case-insensitive, `\` and `/` tolerant) and emits texture paths starting from it:

```
C:\…\BeamNG.drive\current\levels\rochester\art\shapes\bungalow
                               └──────────────────────────────┘
                               /levels/rochester/art/shapes/bungalow/
```

If the target isn't under a `levels/` folder, the tool prints a `WARN` and falls back to bare filenames — the DAEs are still valid but BeamNG won't resolve the textures in-game.

## Idempotency

Re-running into the same target folder is safe:
- DAEs are rewritten from scratch.
- Textures are only recopied when the source's `LastWriteTimeUtc` is newer than the destination (incremental).
- `main.materials.json` is loaded first; existing entries keep their `persistentId` so BeamNG's material tracking stays stable. Unknown/extra top-level keys in an existing file are preserved.
- Malformed existing `main.materials.json` is reported with a `WARN` and discarded rather than crashing.

## Missing textures

When a texture can't be found (and the mirrored fallback also misses), the tool prints a `WARN`, still writes the DAE, but **does not** register the material in `main.materials.json`. BeamNG will render those meshes magenta/checkered. Fix by adding the missing texture to the source folder and re-running.

## Known limitations / future work

Everything below is deferred — add if you hit them:

- **Second UV channel** (lightmaps / detail maps) — currently only UV channel 0 is read.
- **Embedded FBX textures** — binary FBX can inline texture binaries. The tool doesn't extract them; all textures must exist as separate files in the texture source folder.
- **Multi-LOD** — we emit only one LOD at pixel threshold 100. Distant buildings just disappear below that threshold instead of stepping down to a cheaper mesh. For dense scenes, feed `BeamNgDaeScene.LodLevels` with three levels instead of one in [Converter.cs](Converter.cs).
- **`nulldetail{N}` cull threshold** — not emitted. Objects are drawn whenever the LOD's pixel threshold is met.
- **Upside-down textures in-game** — if you ever see them, flip `FlipUVVertical = true` in [Converter.cs](Converter.cs) and re-run. The current default works for the UK_houses dataset.

## Build

```powershell
dotnet build fbxtodae/fbxtodae.csproj
```

Windows-only at runtime (AssimpNet's native `assimp.dll` is shipped for `win-x64`). The csproj itself targets plain `net10.0` so code analysis works on non-Windows CI.

## Implementation plan

See [../ai_docs/2026-04-23-fbx-to-dae-converter.md](../ai_docs/2026-04-23-fbx-to-dae-converter.md) for the original task-by-task implementation plan.
