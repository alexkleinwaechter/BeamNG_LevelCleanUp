using System.Text.Json;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.Exporters;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Terrain.Backdrop;

/// <summary>
///     Writes backdrop chunk scene entries into BeamNG scene files (NDJSON <c>items.level.json</c>),
///     a textured <c>main.materials.json</c>, and per-chunk DAEs — mirrors
///     <see cref="BeamNgTerrainPoc.Terrain.Export.BridgeSceneWriter"/>/
///     <see cref="BeamNgTerrainPoc.Terrain.Export.TunnelSceneWriter"/> (spec §9).
///
///     Scene hierarchy produced:
///       MissionGroup
///         └─ MT_backdrop (SimGroup)
///             ├─ backdrop_0_1 (TSStatic → art/shapes/MT_backdrop/backdrop_0_1.dae)
///             └─ ...
///
///     Like bridge decks / tunnel tubes (and unlike buildings), backdrop chunk meshes carry world
///     coordinates, so every TSStatic sits at (0,0,0) with identity rotation.
///
///     UV/PNG-row-order convention (in-game validation watch item): the Task 8 mesher
///     (<see cref="BackdropQuadtreeMesher"/>) writes planar UVs with V=0 at the chunk's SOUTH edge
///     (<c>WorldMinY</c>), V=1 at the NORTH edge (<c>WorldMaxY</c>). BeamNG samples DAE texture
///     coordinates the DirectX way — V=0 is the texture's row 0 (see
///     <c>ai_docs/2026-04-23-fbx-to-dae-converter.md</c>: "BeamNG uses the DirectX convention").
///     Task 13's chunk PNGs are baked north-up (image row 0 = north). Under that model the mesher's
///     south-origin V is a vertical mirror, so the original convention exported with
///     <c>FlipUVVertical = true</c> (V → <c>1-V</c>; V=0 at the chunk's north edge).
///
///     **2026-07-28 in-game CONFIRMED (Outcome B of
///     <c>ai_docs/2026-07-27 Backdrop/04-placement-followup.md</c>):** exporting with
///     <c>FlipUVVertical = false</c> (mesher V passes through, V=0 at the chunk's SOUTH edge)
///     renders correctly — the engine samples the cooked <c>.color</c> DDS with V inverted relative
///     to the raw PNG, which supplies the north-up reconciliation by itself. This also explains why
///     the very first in-game run rendered correctly with <c>true</c>: that bake predated the
///     <c>.color</c> naming fix (<c>77c16a3</c>), so the game sampled the raw PNGs without the DDS
///     cook — the flip requirement changed together with the cooker entering the chain. If the
///     texture pipeline ever stops using the <c>.color</c> cook, revisit this flag.
/// </summary>
public class BackdropSceneWriter
{
    /// <summary>
    ///     Name of the SimGroup that contains all backdrop chunk TSStatic objects.
    /// </summary>
    public string GroupName { get; set; } = "MT_backdrop";

    /// <summary>
    ///     Ensures the <see cref="GroupName"/> SimGroup exists in the parent items.level.json
    ///     (idempotent). Must be called so BeamNG discovers the MT_backdrop subfolder.
    /// </summary>
    /// <param name="parentItemsPath">Path to the parent items.level.json (main/MissionGroup/items.level.json).</param>
    /// <param name="parentGroupName">Name of the parent SimGroup (e.g., "MissionGroup").</param>
    public void EnsureSimGroupInParent(string parentItemsPath, string parentGroupName = "MissionGroup")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(parentItemsPath)!);

        var lines = File.Exists(parentItemsPath)
            ? File.ReadAllLines(parentItemsPath).ToList()
            : new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("class", out var cls) && cls.GetString() == "SimGroup" &&
                    root.TryGetProperty("name", out var name) && name.GetString() == GroupName)
                {
                    // Already registered — nothing to do.
                    return;
                }
            }
            catch (JsonException) { }
        }

        var entry = new Dictionary<string, object>
        {
            { "name", GroupName },
            { "class", "SimGroup" },
            { "persistentId", Guid.NewGuid().ToString() },
            { "__parent", parentGroupName }
        };
        lines.Add(JsonSerializer.Serialize(entry));
        File.WriteAllLines(parentItemsPath, lines);

        Console.WriteLine($"BackdropSceneWriter: Added '{GroupName}' SimGroup to {parentItemsPath}");
    }

    /// <summary>
    ///     Writes the backdrop chunk items.level.json (NDJSON) — one TSStatic per chunk. The SimGroup
    ///     declaration belongs in the parent (see <see cref="EnsureSimGroupInParent"/>); this file holds
    ///     only the leaf TSStatic entries.
    /// </summary>
    /// <param name="chunks">The exported chunks (from <see cref="ExportChunkDae"/>).</param>
    /// <param name="outputPath">Absolute path for the items.level.json file.</param>
    /// <param name="shapePath">
    ///     BeamNG-relative path prefix for the chunk DAE files
    ///     (e.g., "/levels/myLevel/art/shapes/MT_backdrop/"). A trailing '/' is added if missing.
    /// </param>
    /// <returns>Number of TSStatic entries written.</returns>
    public int WriteSceneItems(
        IReadOnlyList<BackdropChunkExportItem> chunks,
        string outputPath,
        string shapePath)
    {
        if (chunks.Count == 0)
            return 0;

        if (!shapePath.EndsWith('/'))
            shapePath += "/";

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var items = new List<JsonDict>();
        foreach (var chunk in chunks)
            items.Add(CreateTSStaticEntry(chunk, shapePath));

        SimItemsJsonSerializer.Save(outputPath, items);

        Console.WriteLine($"BackdropSceneWriter: Wrote {items.Count} TSStatic entries to {outputPath}");
        return items.Count;
    }

    /// <summary>
    ///     Writes one textured Material entry per chunk, merged idempotent-by-name against whatever
    ///     already exists at <paramref name="outputPath"/> (spec §9 — "never clobbers a user-edited
    ///     material"). Mirrors <see cref="BeamNgTerrainPoc.Terrain.Export.BridgeSceneWriter.WritePlaceholderMaterial"/>'s
    ///     merge pattern, NOT <see cref="BeamNgTerrainPoc.Terrain.Building.BuildingSceneWriter"/>'s overwrite-all: existing entries
    ///     (including foreign/user materials that share no name with a chunk) are preserved, and a
    ///     material name that already exists is left untouched rather than replaced.
    /// </summary>
    /// <param name="chunks">The exported chunks (from <see cref="ExportChunkDae"/>).</param>
    /// <param name="outputPath">Absolute path for the materials.json file.</param>
    /// <param name="texturePath">
    ///     BeamNG-relative path prefix for the chunk textures
    ///     (e.g., "/levels/myLevel/art/shapes/MT_backdrop/textures/").
    /// </param>
    /// <returns>Number of new material entries written (0 if every chunk's material already existed).</returns>
    public int WriteMaterials(
        IReadOnlyList<BackdropChunkExportItem> chunks,
        string outputPath,
        string texturePath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var existing = File.Exists(outputPath)
            ? ArtItemsJsonSerializer.Load(outputPath).ToList()
            : new List<JsonDict>();

        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in existing)
            if (m.TryGetValue("name", out var n) && n is string s)
                existingNames.Add(s);

        var added = 0;
        foreach (var chunk in chunks)
        {
            if (!existingNames.Add(chunk.MaterialName)) continue;
            existing.Add(CreateMaterialEntry(chunk.MaterialName, texturePath, chunk.TextureFileName));
            added++;
        }

        if (added > 0)
        {
            ArtItemsJsonSerializer.Save(outputPath, existing);
            Console.WriteLine($"BackdropSceneWriter: Wrote {added} material entries to {outputPath}");
        }

        return added;
    }

    /// <summary>
    ///     Exports one backdrop chunk to a BeamNG-compatible DAE: a single tiny-pixel-size LOD level
    ///     (spec §9 — far chunks must stay visible, so a huge or bounds-scaled pixel threshold is
    ///     wrong) carrying the chunk's satellite-textured material. The DAE never embeds a Colmesh:
    ///     drivability (when <paramref name="collisionEnabled"/>) comes from the TSStatic entry's
    ///     <c>collisionType "Visible Mesh Final"</c> — the game builds physics from the visual mesh
    ///     itself, so an embedded copy would only double the DAE payload for nothing. Mirrors
    ///     <see cref="BeamNgTerrainPoc.Terrain.Export.BridgeDeckDaeExporter"/>'s
    ///     <c>WriteBeamNgBridgeDae</c> pattern otherwise.
    ///
    ///     Conscious deviation from spec §9's "BeamNgLodDefaults.ComputeForBounds per chunk" wording:
    ///     ComputeForBounds scales pixel sizes UP with bounds (LOD0 ≈ 6600 px for a 2 km chunk), which
    ///     would hide far chunks — the opposite of the stated goal "distant chunks stay visible". A
    ///     single LOD at a tiny fixed pixel size (2) + no nulldetail keeps every chunk rendered at any
    ///     distance. If in-game validation shows popping/perf issues, revisit with ComputeForBounds'
    ///     bias parameter — the knob is confined to this method.
    /// </summary>
    public BackdropChunkExportItem ExportChunkDae(BackdropChunkDefinition chunk,
        BackdropChunkMeshResult meshResult, string shapesDirectory, bool collisionEnabled = true)
    {
        Directory.CreateDirectory(shapesDirectory);
        meshResult.VisualMesh.MaterialName = chunk.MaterialName;

        var scene = new BeamNgDaeScene
        {
            BaseName = $"backdrop_{chunk.Cx}_{chunk.Cy}",          // digits mangled to letters by the exporter
            LodLevels = [new LodLevel(2, new List<Mesh> { meshResult.VisualMesh })], // small pixel size → visible almost forever
            NullDetailPixelSize = 0                                 // no nulldetail → chunks never vanish (spec §9;
                                                                     // in-game validation item — see manual checklist)
        };

        // FlipUVVertical = false (in-game CONFIRMED 2026-07-28, 04-placement-followup.md Outcome B):
        // the game's .color DDS cook samples V inverted vs the raw north-up PNG, so the mesher's
        // south-origin V (Task 8 — V=0 at chunk WorldMinY) must pass through unflipped. `true` was
        // correct only in the pre-.color raw-PNG era — see class doc before touching this flag.
        var exporter = new ColladaExporter(new ColladaExportOptions
        {
            ConvertToZUp = true,
            FlipWindingOrder = false,
            FlipUVVertical = false
        });
        exporter.RegisterMaterial(Material.CreateWithTexture(chunk.MaterialName, "textures/" + chunk.TextureFileName));
        exporter.Export(scene, Path.Combine(shapesDirectory, chunk.DaeFileName));

        return new BackdropChunkExportItem
        {
            Cx = chunk.Cx, Cy = chunk.Cy,
            DaeFileName = chunk.DaeFileName, MaterialName = chunk.MaterialName,
            TextureFileName = chunk.TextureFileName,
            Vertices = meshResult.VisualMesh.VertexCount, Triangles = meshResult.VisualMesh.TriangleCount,
            HasCollision = collisionEnabled
        };
    }

    /// <summary>
    ///     Deletes previous backdrop output directories so regeneration starts fresh — mirrors
    ///     <c>TerrainCreator.CleanBridgeOutputDirectories</c>. Both <c>art/shapes/MT_backdrop/</c>
    ///     (textures included — the plan and bakes are fully regenerated) and
    ///     <c>main/MissionGroup/MT_backdrop/</c> are removed; the parent items.level.json is left
    ///     untouched, since <see cref="EnsureSimGroupInParent"/>'s upsert keeps it correct on the next run.
    /// </summary>
    public static void CleanPreviousOutputs(string levelPath)
    {
        var shapesDir = Path.Combine(levelPath, "art", "shapes", "MT_backdrop");
        var sceneDir = Path.Combine(levelPath, "main", "MissionGroup", "MT_backdrop");

        foreach (var dir in new[] { shapesDir, sceneDir })
        {
            if (Directory.Exists(dir))
            {
                Console.WriteLine($"BackdropSceneWriter: Cleaning previous backdrop output: {dir}");
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    ///     Creates a TSStatic scene entry for a backdrop chunk. The chunk mesh carries world
    ///     coordinates, so the object is placed at (0,0,0) with identity rotation (matching the
    ///     bridge/tunnel/road mesh exporter convention). <c>collisionType</c>/<c>decalType</c> are
    ///     always emitted explicitly — a world-editor save persists these fields, so relying on the
    ///     engine default would let an editor round-trip silently strip drivability. Enabled ⇒ both
    ///     "Visible Mesh Final" (the game builds physics/decal projection from the visual mesh — no
    ///     Colmesh in the DAE needed; value string per the editor's Collision dropdown); disabled ⇒
    ///     both "None".
    /// </summary>
    private JsonDict CreateTSStaticEntry(BackdropChunkExportItem chunk, string shapePath)
    {
        return new JsonDict
        {
            ["class"] = "TSStatic",
            ["name"] = $"backdrop_{chunk.Cx}_{chunk.Cy}",
            ["__parent"] = GroupName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            // World-coordinate mesh → place at origin so it aligns with the terrain.
            ["position"] = new float[] { 0f, 0f, 0f },
            ["rotationMatrix"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
            ["shapeName"] = shapePath + chunk.DaeFileName,
            ["collisionType"] = chunk.HasCollision ? "Visible Mesh Final" : "None",
            ["decalType"] = chunk.HasCollision ? "Visible Mesh Final" : "None",
            ["isRenderEnabled"] = true,
            ["useInstanceRenderData"] = true
        };
    }

    /// <summary>
    ///     Builds a JsonDict representing a BeamNG Material entry for one backdrop chunk: a single
    ///     textured stage (satellite baseColorMap, untinted, fully rough — the texture IS the color).
    /// </summary>
    private static JsonDict CreateMaterialEntry(string materialName, string texturePath, string textureFile)
    {
        var stage0 = new JsonDict
        {
            ["baseColorMap"] = texturePath + textureFile,          // "/levels/{level}/art/shapes/MT_backdrop/textures/backdrop_0_1.color.png"
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
}
