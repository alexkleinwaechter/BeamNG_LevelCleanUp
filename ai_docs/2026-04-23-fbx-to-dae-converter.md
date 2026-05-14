# FBX → DAE Converter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a standalone console tool `fbxtodae` in this solution that batch-converts FBX assets to BeamNG-compatible Collada (DAE) files plus a `main.materials.json`, using a diffuse-and-normal texture-naming convention.

**Architecture:** Reuse `AssimpNet` (already in `BeamNG.Procedural3D`) to import FBX and `BeamNG.Procedural3D.ColladaExporter.ExportZUp` to write the DAE. Serialize `main.materials.json` with `System.Text.Json` directly (same shape as `BuildingSceneWriter.CreateMaterialEntry`). Tool is three phases: (1) discover FBX files + matching textures, (2) convert geometry/UVs, (3) write DAE + copy textures + append to materials.json.

**Tech Stack:** .NET 9 console app, `AssimpNet 5.0.0-beta1` (FBX import), `BeamNG.Procedural3D` project reference (mesh + DAE export), `System.Text.Json` (materials.json).

---

## Background: FBX UV Mapping — Key Facts

Investigation results you need before touching UVs:

1. **FBX stores UVs per "layer element"** (not per-vertex). Each vertex in a polygon can index into a separate UV array. Common mapping modes: `ByPolygonVertex` + `IndexToDirect` (most common for DCC exports), `ByVertice` + `Direct`.
2. **Assimp normalizes this for us** — `aiProcess_Triangulate | aiProcess_JoinIdenticalVertices` yields a flat indexed mesh with 1:1 vertex/UV correspondence. `scene.Meshes[i].TextureCoordinateChannels[0]` gives UVs on channel 0 (always the first texture set in FBX).
3. **V-axis convention**: FBX (like DirectX/Maya) uses V=0 at the *top* of the texture. Collada/OpenGL use V=0 at the *bottom*. BeamNG uses the DirectX convention. Empirically, FBX → BeamNG via Collada works with **no V flip** when we use `ColladaExporter.ExportZUp` (positions are written Z-up, UVs unchanged). If textures appear upside-down in-game, enable `FlipUVVertical` on `ColladaExportOptions`.
4. **Coordinate system**: FBX default is Y-up, right-handed; BeamNG is Z-up, right-handed. Conversion: `(x, y, z)_fbx → (x, -z, y)_zup`. We apply this manually before handing the mesh to `ExportZUp` (which writes positions as-is).
5. **Scale**: FBX stores unit scale in `GlobalSettings.UnitScaleFactor`. Assimp exposes this via `scene.RootNode.Transform` and optionally `aiProcess_GlobalScale`. We use `aiProcess_GlobalScale` so 1 FBX unit → 1 meter. Test assets in `UK_houses_3dassets` are already in meters, so this is a no-op there.
6. **One texture per channel**: the input FBX files each have exactly one diffuse + one normal map. We do not need to read embedded material data from FBX — we derive texture filenames from a simple convention: `{fbxBaseName}_d.png` (diffuse), `{fbxBaseName}_n.png` (normal). This matches the `UK_houses_3dassets` dataset.

---

## File Structure

```
fbxtodae/
├── fbxtodae.csproj        # new console project
├── Program.cs             # arg parsing, orchestration, exit codes
├── FbxLoader.cs           # AssimpNet → BeamNG.Procedural3D.Mesh
├── TextureMatcher.cs      # {basename}_d.png / _n.png discovery
├── MaterialsJsonWriter.cs # BeamNG main.materials.json emitter
└── Converter.cs           # end-to-end: one FBX → one DAE + material entry
```

Added to `BeamNG_LevelCleanUp.sln` as a new project (reuses `BeamNG.Procedural3D` via ProjectReference, plus its transitive AssimpNet package).

---

## Task 1: Scaffold the console project

**Files:**
- Create: `fbxtodae/fbxtodae.csproj`
- Create: `fbxtodae/Program.cs` (stub)
- Modify: `BeamNG_LevelCleanUp.sln`

- [ ] **Step 1: Create the csproj**

Create `fbxtodae/fbxtodae.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>FbxToDae</RootNamespace>
    <AssemblyName>fbxtodae</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\BeamNG.Procedural3D\BeamNG.Procedural3D.csproj" />
  </ItemGroup>

</Project>
```

`AssimpNet` is pulled in transitively via `BeamNG.Procedural3D`. No Windows-only dependency is needed, so target plain `net9.0` (keeps the tool runnable on CI/containers if ever useful).

- [ ] **Step 2: Add stub Program.cs**

Create `fbxtodae/Program.cs`:

```csharp
using FbxToDae;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: fbxtodae <fbxSourceDir> <textureSourceDir> <targetDir>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  fbxSourceDir     Folder containing *.fbx files (non-recursive).");
    Console.Error.WriteLine("  textureSourceDir Folder containing {fbxname}_d.png and {fbxname}_n.png.");
    Console.Error.WriteLine("  targetDir        Output folder for *.dae files and main.materials.json.");
    return 2;
}

return Program.Run(args[0], args[1], args[2]);
```

And a minimal `Program` class the stub calls into (we'll fill it out in Task 6):

```csharp
namespace FbxToDae;

public static class Program
{
    public static int Run(string fbxDir, string textureDir, string targetDir)
    {
        Console.WriteLine($"FBX dir:     {fbxDir}");
        Console.WriteLine($"Texture dir: {textureDir}");
        Console.WriteLine($"Target dir:  {targetDir}");
        return 0;
    }
}
```

- [ ] **Step 3: Register project in the solution**

Edit `BeamNG_LevelCleanUp.sln`. After the last `Project(...)` entry (before `Global`), add:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "fbxtodae", "fbxtodae\fbxtodae.csproj", "{F8B7C2D1-1234-5678-9ABC-DEF012345678}"
EndProject
```

Then in `GlobalSection(ProjectConfigurationPlatforms)` add the six standard config lines (mirror the etsmaterialgen block — all six `Debug|*` and `Release|*` entries must map to `Any CPU`):

```
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Debug|Any CPU.Build.0 = Debug|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Debug|x64.ActiveCfg = Debug|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Debug|x64.Build.0 = Debug|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Debug|x86.ActiveCfg = Debug|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Debug|x86.Build.0 = Debug|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Release|Any CPU.ActiveCfg = Release|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Release|Any CPU.Build.0 = Release|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Release|x64.ActiveCfg = Release|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Release|x64.Build.0 = Release|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Release|x86.ActiveCfg = Release|Any CPU
{F8B7C2D1-1234-5678-9ABC-DEF012345678}.Release|x86.Build.0 = Release|Any CPU
```

Use a fresh GUID if you prefer (`[guid]::NewGuid()` in PowerShell); the value shown above is fine.

- [ ] **Step 4: Verify the project builds**

Run: `dotnet build fbxtodae/fbxtodae.csproj`
Expected: `Build succeeded` with 0 errors/warnings.

- [ ] **Step 5: Smoke-test the stub**

Run: `dotnet run --project fbxtodae -- a b c`
Expected output:
```
FBX dir:     a
Texture dir: b
Target dir:  c
```

And with wrong args: `dotnet run --project fbxtodae`
Expected: usage message on stderr, exit code 2.

- [ ] **Step 6: Commit**

```bash
git add fbxtodae/ BeamNG_LevelCleanUp.sln
git commit -m "feat(fbxtodae): scaffold console project with argument parsing"
```

---

## Task 2: Texture matcher

**Files:**
- Create: `fbxtodae/TextureMatcher.cs`

- [ ] **Step 1: Implement the matcher**

Create `fbxtodae/TextureMatcher.cs`:

```csharp
namespace FbxToDae;

/// <summary>
/// Resolves {fbxBaseName}_d.* (diffuse) and {fbxBaseName}_n.* (normal) textures
/// from the texture source folder. Case-insensitive on filename (Windows FS is
/// case-insensitive anyway, but we compare invariant-lower to be safe).
/// </summary>
public sealed class TextureMatcher
{
    private readonly Dictionary<string, string> _byLowerName;

    public TextureMatcher(string textureDir)
    {
        if (!Directory.Exists(textureDir))
            throw new DirectoryNotFoundException($"Texture directory not found: {textureDir}");

        _byLowerName = Directory.EnumerateFiles(textureDir)
            .ToDictionary(
                p => Path.GetFileName(p).ToLowerInvariant(),
                p => p,
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves the diffuse and normal textures for the given FBX base name.
    /// Returns (diffusePath, normalPath). Either may be null if not found.
    /// Tries .png first, then .jpg/.jpeg/.tga/.dds as fallbacks.
    /// </summary>
    public (string? diffuse, string? normal) Resolve(string fbxBaseName)
    {
        return (Find(fbxBaseName, "_d"), Find(fbxBaseName, "_n"));
    }

    private string? Find(string baseName, string suffix)
    {
        string[] exts = [".png", ".jpg", ".jpeg", ".tga", ".dds"];
        foreach (var ext in exts)
        {
            var candidate = (baseName + suffix + ext).ToLowerInvariant();
            if (_byLowerName.TryGetValue(candidate, out var path))
                return path;
        }
        return null;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add fbxtodae/TextureMatcher.cs
git commit -m "feat(fbxtodae): texture matcher by {name}_d/_n convention"
```

---

## Task 3: FBX loader (Assimp → Mesh)

**Files:**
- Create: `fbxtodae/FbxLoader.cs`

- [ ] **Step 1: Implement the loader**

Create `fbxtodae/FbxLoader.cs`:

```csharp
using System.Numerics;
using Assimp;
using BeamNG.Procedural3D.Core;
using Mesh = BeamNG.Procedural3D.Core.Mesh;

namespace FbxToDae;

/// <summary>
/// Loads an FBX file and converts all sub-meshes into a flat list of
/// BeamNG.Procedural3D Mesh instances. All meshes are assigned the same
/// material name (one material per FBX). Coordinate system is converted
/// from FBX (Y-up right-handed) to BeamNG (Z-up right-handed) here, so the
/// caller can hand the meshes to ColladaExporter.ExportZUp directly.
///
/// Conversion: (x, y, z)_fbx -> (x, -z, y)_zup
/// Normals:    (x, y, z)_fbx -> (x, -z, y)_zup
/// UVs:        unchanged (V flip handled by ColladaExporter if needed)
/// </summary>
public static class FbxLoader
{
    public static List<Mesh> Load(string fbxPath, string materialName)
    {
        if (!File.Exists(fbxPath))
            throw new FileNotFoundException("FBX file not found.", fbxPath);

        // PostProcessSteps:
        //   Triangulate              -> ensure every face is a triangle
        //   GenerateSmoothNormals    -> fill Normals if the file lacks them
        //   JoinIdenticalVertices    -> indexed mesh with 1:1 vertex/UV/normal
        //   FlipWindingOrder is NOT set - FBX is right-handed same as Collada
        //   GlobalScale              -> applies GlobalSettings.UnitScaleFactor
        //   PreTransformVertices     -> bake node transforms into vertex data
        //                               so we get world-space meshes (FBX node
        //                               hierarchies often rotate/scale parts)
        var steps = PostProcessSteps.Triangulate
                  | PostProcessSteps.GenerateSmoothNormals
                  | PostProcessSteps.JoinIdenticalVertices
                  | PostProcessSteps.GlobalScale
                  | PostProcessSteps.PreTransformVertices;

        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(fbxPath, steps)
            ?? throw new InvalidOperationException($"Assimp failed to load: {fbxPath}");

        var result = new List<Mesh>();
        for (int i = 0; i < scene.MeshCount; i++)
        {
            var a = scene.Meshes[i];
            if (a.PrimitiveType != PrimitiveType.Triangle || a.FaceCount == 0)
                continue;

            var mesh = new Mesh
            {
                Name = string.IsNullOrWhiteSpace(a.Name) ? $"part_{i}" : a.Name,
                MaterialName = materialName,
            };

            bool hasUv = a.HasTextureCoords(0);
            bool hasNormals = a.HasNormals;

            for (int v = 0; v < a.VertexCount; v++)
            {
                var p = a.Vertices[v];
                var n = hasNormals ? a.Normals[v] : new Vector3D(0, 1, 0);
                var uv = hasUv ? a.TextureCoordinateChannels[0][v] : new Vector3D(0, 0, 0);

                var position = YupToZup(new Vector3(p.X, p.Y, p.Z));
                var normal   = YupToZup(new Vector3(n.X, n.Y, n.Z));

                mesh.Vertices.Add(new Vertex(position, normal, new Vector2(uv.X, uv.Y)));
            }

            foreach (var face in a.Faces)
            {
                if (face.IndexCount != 3) continue; // should never happen after Triangulate
                mesh.Triangles.Add(new Triangle(
                    face.Indices[0],
                    face.Indices[1],
                    face.Indices[2]));
            }

            if (mesh.HasGeometry)
                result.Add(mesh);
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"No triangulated meshes found in {fbxPath}");

        return result;
    }

    /// <summary>
    /// Converts a right-handed Y-up vector (FBX) to a right-handed Z-up vector
    /// (BeamNG). The rotation is +90° around the X axis:
    ///     new_x = x
    ///     new_y = -z
    ///     new_z =  y
    /// </summary>
    private static Vector3 YupToZup(Vector3 v) => new(v.X, -v.Z, v.Y);
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build fbxtodae/fbxtodae.csproj`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add fbxtodae/FbxLoader.cs
git commit -m "feat(fbxtodae): FBX loader via AssimpNet with Y-up -> Z-up conversion"
```

---

## Task 4: BeamNG main.materials.json writer

**Files:**
- Create: `fbxtodae/MaterialsJsonWriter.cs`

Shape emitted (mirrors `BuildingSceneWriter.CreateMaterialEntry` with only the fields we need for a simple diffuse+normal PBR material):

```json
{
  "bungalow10": {
    "class": "Material",
    "name": "bungalow10",
    "mapTo": "bungalow10",
    "internalName": "bungalow10",
    "persistentId": "<guid>",
    "version": 1.5,
    "Stages": [
      {
        "baseColorMap": "bungalow10_d.png",
        "normalMap":    "bungalow10_n.png"
      },
      {}, {}, {}
    ]
  }
}
```

Notes:
- `Stages` is a 4-element array; BeamNG treats entries 1–3 as extra detail layers. Empty `{}` objects are required (BeamNG's parser expects the array to have length 4).
- Texture paths are relative filenames because DAE + textures + materials.json all live in the same folder. BeamNG resolves them relative to the materials.json location.
- `version: 1.5` enables PBR shader (sRGB for baseColorMap, linear for normalMap — handled automatically by BeamNG).

- [ ] **Step 1: Implement the writer**

Create `fbxtodae/MaterialsJsonWriter.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FbxToDae;

/// <summary>
/// Writes BeamNG main.materials.json. Each call to Add() registers one material
/// by name. Call Save() once at the end to flush to disk. If the file already
/// exists, it is loaded first so we merge-without-overwriting-new entries.
/// Duplicate names are replaced (latest wins), since re-running the tool on
/// the same input should be idempotent.
/// </summary>
public sealed class MaterialsJsonWriter
{
    private readonly JsonObject _root;

    public MaterialsJsonWriter(string? existingFile = null)
    {
        if (!string.IsNullOrEmpty(existingFile) && File.Exists(existingFile))
        {
            var text = File.ReadAllText(existingFile);
            _root = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        else
        {
            _root = new JsonObject();
        }
    }

    public void Add(string materialName, string diffuseFileName, string? normalFileName)
    {
        var stage0 = new JsonObject
        {
            ["baseColorMap"] = diffuseFileName,
        };
        if (!string.IsNullOrEmpty(normalFileName))
            stage0["normalMap"] = normalFileName;

        var material = new JsonObject
        {
            ["class"] = "Material",
            ["name"] = materialName,
            ["mapTo"] = materialName,
            ["internalName"] = materialName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            ["version"] = 1.5,
            ["Stages"] = new JsonArray(stage0, new JsonObject(), new JsonObject(), new JsonObject()),
        };

        _root[materialName] = material;
    }

    public void Save(string outputFile)
    {
        var dir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(outputFile, _root.ToJsonString(opts));
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add fbxtodae/MaterialsJsonWriter.cs
git commit -m "feat(fbxtodae): BeamNG materials.json writer with merge-on-rerun"
```

---

## Task 5: Per-FBX converter

**Files:**
- Create: `fbxtodae/Converter.cs`

- [ ] **Step 1: Implement the converter**

Create `fbxtodae/Converter.cs`:

```csharp
using BeamNG.Procedural3D.Exporters;

namespace FbxToDae;

/// <summary>
/// Converts one FBX file into one DAE file in the target directory,
/// copies its textures there, and registers a material entry with the
/// given MaterialsJsonWriter.
/// </summary>
public sealed class Converter
{
    private readonly TextureMatcher _textures;
    private readonly MaterialsJsonWriter _materials;
    private readonly string _targetDir;
    private readonly ColladaExporter _exporter;

    public Converter(TextureMatcher textures, MaterialsJsonWriter materials, string targetDir)
    {
        _textures = textures;
        _materials = materials;
        _targetDir = targetDir;

        // ExportZUp writes positions verbatim and we already rotate Y-up->Z-up
        // inside FbxLoader, so ConvertToZUp is irrelevant here (that flag is
        // only used by the Assimp-based Export path). FlipUVVertical is kept
        // off: FBX and BeamNG share the DirectX V-convention. If a specific
        // asset shows upside-down textures in-game, flip this flag.
        _exporter = new ColladaExporter(new ColladaExportOptions
        {
            IncludeUVs = true,
            IncludeNormals = true,
            FlipUVVertical = false,
        });
    }

    public void Convert(string fbxPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(fbxPath);
        var materialName = baseName;

        // 1) Load FBX -> flat mesh list tagged with materialName
        var meshes = FbxLoader.Load(fbxPath, materialName);

        // 2) Resolve textures
        var (diffuseSrc, normalSrc) = _textures.Resolve(baseName);
        if (diffuseSrc is null)
            Console.Error.WriteLine($"  WARN: no diffuse texture for {baseName} (looked for {baseName}_d.*)");
        if (normalSrc is null)
            Console.Error.WriteLine($"  WARN: no normal texture for {baseName} (looked for {baseName}_n.*)");

        // 3) Copy textures into target dir (alongside DAE and materials.json)
        string? diffuseName = CopyTexture(diffuseSrc);
        string? normalName  = CopyTexture(normalSrc);

        // 4) Write DAE (Z-up, positions already converted)
        var daePath = Path.Combine(_targetDir, baseName + ".dae");
        _exporter.ExportZUp(meshes, daePath);

        // 5) Register material (only if we found at least a diffuse map —
        //    without a baseColorMap BeamNG will render the mesh magenta/checkered)
        if (diffuseName is not null)
            _materials.Add(materialName, diffuseName, normalName);

        Console.WriteLine($"  -> {Path.GetFileName(daePath)} ({meshes.Count} mesh(es), "
                        + $"diffuse={diffuseName ?? "<missing>"}, normal={normalName ?? "<missing>"})");
    }

    private string? CopyTexture(string? sourcePath)
    {
        if (sourcePath is null) return null;
        var fileName = Path.GetFileName(sourcePath);
        var dest = Path.Combine(_targetDir, fileName);
        if (!File.Exists(dest) || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(dest))
            File.Copy(sourcePath, dest, overwrite: true);
        return fileName;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add fbxtodae/Converter.cs
git commit -m "feat(fbxtodae): per-FBX converter producing DAE + texture copy + material entry"
```

---

## Task 6: Batch orchestration in Program.Run

**Files:**
- Modify: `fbxtodae/Program.cs`

- [ ] **Step 1: Replace the stub Program.Run with the real orchestration**

Replace the body of the `Program` class in `fbxtodae/Program.cs` (keep the top-level `args`-handling block unchanged):

```csharp
namespace FbxToDae;

public static class Program
{
    public static int Run(string fbxDir, string textureDir, string targetDir)
    {
        if (!Directory.Exists(fbxDir))
        {
            Console.Error.WriteLine($"FBX source directory does not exist: {fbxDir}");
            return 1;
        }
        if (!Directory.Exists(textureDir))
        {
            Console.Error.WriteLine($"Texture source directory does not exist: {textureDir}");
            return 1;
        }
        Directory.CreateDirectory(targetDir);

        var fbxFiles = Directory.EnumerateFiles(fbxDir, "*.fbx", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (fbxFiles.Count == 0)
        {
            Console.Error.WriteLine($"No *.fbx files found in {fbxDir}");
            return 1;
        }

        Console.WriteLine($"Converting {fbxFiles.Count} FBX file(s) to {targetDir}");

        var textures = new TextureMatcher(textureDir);
        var materialsPath = Path.Combine(targetDir, "main.materials.json");
        var materials = new MaterialsJsonWriter(existingFile: materialsPath);
        var converter = new Converter(textures, materials, targetDir);

        int ok = 0, failed = 0;
        foreach (var fbx in fbxFiles)
        {
            Console.WriteLine(Path.GetFileName(fbx));
            try
            {
                converter.Convert(fbx);
                ok++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ERROR: {ex.Message}");
                failed++;
            }
        }

        materials.Save(materialsPath);

        Console.WriteLine();
        Console.WriteLine($"Done. {ok} converted, {failed} failed. Materials -> {materialsPath}");
        return failed == 0 ? 0 : 1;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build fbxtodae/fbxtodae.csproj`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add fbxtodae/Program.cs
git commit -m "feat(fbxtodae): batch orchestration over source folder"
```

---

## Task 7: End-to-end manual validation against UK_houses bungalows

This is the only "test" in the plan — the user confirmed the tool is personal-use, so we validate against the real dataset rather than building a unit-test harness.

**Files:** none — this is a runtime check.

- [ ] **Step 1: Run the converter**

```bash
dotnet run --project fbxtodae -- \
  "D:/Source/beamng_mapping_pro/examples_for_ai/UK_houses_3dassets/bungalow/Models" \
  "D:/Source/beamng_mapping_pro/examples_for_ai/UK_houses_3dassets/bungalow/Textures" \
  "D:/tmp/fbxtodae_out/bungalow"
```

Expected:
- Console prints 30 `bungalowN[bm].FBX → bungalowN[bm].dae (...)` lines.
- No `WARN: no diffuse/normal texture` lines (every FBX has both).
- Final line: `Done. 30 converted, 0 failed.`

- [ ] **Step 2: Inspect output directory**

Run: `ls "D:/tmp/fbxtodae_out/bungalow" | head -20`
Expected: 30 `*.dae`, 60 `*_d.png`/`*_n.png`, and 1 `main.materials.json`.

- [ ] **Step 3: Spot-check one DAE in a viewer**

Open `D:/tmp/fbxtodae_out/bungalow/bungalow1.dae` in any Collada viewer (Blender, online Collada viewer, or the app's own 3D viewer). Confirm:
- The building stands upright (Z-up worked).
- UVs appear on walls (not a featureless blob).
- Reasonable polygon count (~500–900 tris per Readme.txt).

If geometry is on its side: revisit the `YupToZup` transform in `FbxLoader.cs:69` — some FBX authoring tools export with Z-up already baked; in that case comment out the rotation or add a `--no-axis-convert` flag. For this dataset (Unity-style exports) the rotation is correct.

- [ ] **Step 4: Spot-check main.materials.json**

Run: `head -30 "D:/tmp/fbxtodae_out/bungalow/main.materials.json"`
Expected: valid JSON with a `bungalow1` key whose `Stages[0]` has `baseColorMap: "bungalow1_d.png"` and `normalMap: "bungalow1_n.png"`.

- [ ] **Step 5: Drop into a BeamNG level and verify in-game**

- Copy `D:/tmp/fbxtodae_out/bungalow/*` into `levels/<yourTestLevel>/art/shapes/bungalow/` inside your BeamNG user folder.
- In the BeamNG World Editor, place one `TSStatic` with `shapeName` set to `levels/<yourTestLevel>/art/shapes/bungalow/bungalow1.dae`.
- Confirm the building appears with correct textures (not pink/white/magenta) and is not upside down.

If textures are upside-down: re-run with `FlipUVVertical = true` in `Converter.cs:20` and reconvert. Record the outcome in the commit message so future-you remembers which way this dataset flips.

- [ ] **Step 6: Commit any tweaks + a README note (optional)**

If you change `FlipUVVertical`, add a one-line note at the top of `fbxtodae/Program.cs` explaining the convention the tool assumes. Then:

```bash
git add fbxtodae/
git commit -m "chore(fbxtodae): validated against UK_houses/bungalow dataset"
```

---

## Open questions / deferred decisions (only tackle if the above doesn't work)

1. **Multiple UV sets.** If some asset needs a second UV channel (lightmaps, detail maps), extend `FbxLoader` to read `TextureCoordinateChannels[1]` into a second vertex attribute — but `BeamNG.Procedural3D.Vertex` currently carries only one UV. Needs a core change there; skip unless encountered.
2. **Embedded FBX textures.** Binary FBX can embed texture binaries inside the file. Assimp exposes these via `scene.Textures`. We don't support that path (the dataset uses external PNGs). If encountered, extract them with `scene.Textures[i].CompressedData` and write next to the DAE.
3. **Non-PNG texture formats.** `TextureMatcher` already tries `.jpg/.jpeg/.tga/.dds` as fallbacks. For BeamNG in-game, `.dds` (BC7/BC5) is preferred for perf; that's a separate texture-conversion task and out of scope here.
4. **LODs.** Real BeamNG DAEs use the `base00 / start01 / {name}_a{px}` LOD hierarchy (see `BeamNgDaeScene`). `ExportZUp` writes a flat mesh list without that hierarchy, which BeamNG still loads — it just won't LOD-switch. For personal-use background buildings this is acceptable; if detail drops become a perf issue, switch to `ColladaExporter.Export(BeamNgDaeScene, ...)` with one LOD level.
