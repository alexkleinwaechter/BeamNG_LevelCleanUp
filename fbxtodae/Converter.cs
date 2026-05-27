using BeamNG.Procedural3D.Building;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.Exporters;
using Mesh = BeamNG.Procedural3D.Core.Mesh;

namespace FbxToDae;

/// <summary>
/// Converts one FBX file into one DAE file in the target directory,
/// copies its textures there (renamed to BeamNG's texture-cooker convention
/// for .png — see <c>ai_agent_md_files_history_some_outdated/BeamNG_Materials_Documentation.md</c>),
/// and registers a material entry with the given MaterialsJsonWriter.
///
/// Texture cooker rename (PNG only): <c>{name}_d.png -&gt; {name}.color.png</c>,
/// <c>{name}_n.png -&gt; {name}.normal.png</c>. The game's texture cooker compiles
/// these to DDS at load time (BC7 sRGB for .color, BC5 for .normal).
///
/// DAE structure: BeamNG hierarchy via <see cref="ColladaExporter.Export(BeamNgDaeScene, string)"/>.
/// <code>
/// base00
/// └── start01
///     ├── Colmesh-1           -> collision geometry (same mesh, materials stripped)
///     └── {name}_a{pixelSize} -> single LOD (we don't emit multi-LOD)
/// </code>
/// The collision mesh is a straight merge of the FBX geometry without materials —
/// accurate collision, no simplification (per user requirement).
/// </summary>
public sealed class Converter
{
    private readonly TextureMatcher _textures;
    private readonly MaterialsJsonWriter _materials;
    private readonly string _targetDir;
    private readonly string? _beamngPathPrefix;
    private readonly ColladaExporter _exporter;

    /// <param name="beamngPathPrefix">BeamNG-absolute path prefix with trailing '/',
    /// e.g. <c>/levels/rochester/art/shapes/bungalow/</c>. When null, materials.json
    /// emits bare filenames (useful for offline DAE generation but won't resolve in-game).</param>
    public Converter(TextureMatcher textures, MaterialsJsonWriter materials, string targetDir,
        string? beamngPathPrefix)
    {
        _textures = textures;
        _materials = materials;
        _targetDir = targetDir;
        _beamngPathPrefix = beamngPathPrefix;

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

        // 3) Copy textures into target dir (renamed to cooker convention for PNG)
        string? diffusePath = CopyAndResolve(diffuseSrc, baseName, "color");
        string? normalPath  = CopyAndResolve(normalSrc,  baseName, "normal");

        // 4) Write DAE with BeamNG hierarchy: base00 / start01 / (Colmesh-1 + LOD)
        var daePath = Path.Combine(_targetDir, baseName + ".dae");
        WriteBeamNgDae(meshes, baseName, daePath);

        // 5) Register material (only if we found at least a diffuse map —
        //    without a baseColorMap BeamNG will render the mesh magenta/checkered)
        if (diffusePath is not null)
            _materials.Add(materialName, diffusePath, normalPath);

        Console.WriteLine($"  -> {Path.GetFileName(daePath)} ({meshes.Count} mesh(es), "
                        + $"diffuse={diffusePath ?? "<missing>"}, normal={normalPath ?? "<missing>"})");
    }

    /// <summary>
    /// Wraps the loaded meshes in the BeamNG <c>base00/start01</c> scene hierarchy with
    /// a Colmesh-1 collision node (same geometry, materials stripped) and a single LOD
    /// at <see cref="BeamNgLodDefaults.SingleBuilding"/>'s Lod0 threshold (100 px).
    /// Matches the pattern in <c>BuildingDaeExporter.WriteColladaWithMultiLod</c>.
    /// </summary>
    private void WriteBeamNgDae(List<Mesh> meshes, string baseName, string daePath)
    {
        // CollisionMeshGenerator takes a Dictionary<string, Mesh>. Keys are only used
        // to enforce uniqueness inside the dict — values get merged into one Colmesh.
        // Index-suffixed keys tolerate multi-part FBX where two sub-meshes share a name.
        var lod0 = new Dictionary<string, Mesh>(meshes.Count);
        for (int i = 0; i < meshes.Count; i++)
            lod0[$"{meshes[i].Name}_{i}"] = meshes[i];
        var collisionMesh = CollisionMeshGenerator.GenerateFromLod0(lod0);

        var scene = new BeamNgDaeScene
        {
            BaseName = baseName,
            LodLevels =
            [
                new LodLevel(BeamNgLodDefaults.SingleBuilding.Lod0PixelSize, meshes),
            ],
            ColmeshMeshes = collisionMesh is { HasGeometry: true } ? [collisionMesh] : null,
        };

        _exporter.Export(scene, daePath);
    }

    /// <summary>
    /// Copies the source texture into the target directory, renaming PNGs to BeamNG's
    /// cooker convention ({baseName}.{slot}.png). Non-PNG textures are copied as-is
    /// (DDS is already compiled; JPG/TGA don't round-trip through the cooker).
    /// Returns the BeamNG-absolute path if a prefix was configured, else the bare filename.
    /// </summary>
    private string? CopyAndResolve(string? sourcePath, string baseName, string slot)
    {
        if (sourcePath is null) return null;

        var ext = Path.GetExtension(sourcePath);
        string newFileName = ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? $"{baseName}.{slot}.png"
            : baseName + ext;

        var dest = Path.Combine(_targetDir, newFileName);
        if (!File.Exists(dest) || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(dest))
            File.Copy(sourcePath, dest, overwrite: true);

        return _beamngPathPrefix is not null ? _beamngPathPrefix + newFileName : newFileName;
    }
}
