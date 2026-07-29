using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
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
                TextureFileName = "backdrop_0_1.color.png", Vertices = 10, Triangles = 8 },
        new() { Cx = 2, Cy = 0, DaeFileName = "backdrop_2_0.dae", MaterialName = "mt_backdrop_2_0",
                TextureFileName = "backdrop_2_0.color.png", Vertices = 12, Triangles = 10 }
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
            // Explicit collision/decal type (collision-toggle change): an editor save persists these
            // fields, so the writer pins them instead of relying on the engine default. Collision-on
            // means "Visible Mesh Final" — physics from the visual mesh, no Colmesh in the DAE.
            Assert.Equal("Visible Mesh Final", root.GetProperty("collisionType").GetString());
            Assert.Equal("Visible Mesh Final", root.GetProperty("decalType").GetString());
        }
    }

    [Fact]
    public void WriteSceneItems_CollisionOff_SaysCollisionTypeNone()
    {
        var chunks = SampleChunks().Select(c => new BackdropChunkExportItem
        {
            Cx = c.Cx, Cy = c.Cy, DaeFileName = c.DaeFileName, MaterialName = c.MaterialName,
            TextureFileName = c.TextureFileName, Vertices = c.Vertices, Triangles = c.Triangles,
            HasCollision = false
        }).ToList();
        new BackdropSceneWriter().WriteSceneItems(chunks, GroupItemsPath, ShapePath);
        foreach (var doc in ReadNdjson(GroupItemsPath))
        {
            Assert.Equal("None", doc.RootElement.GetProperty("collisionType").GetString());
            Assert.Equal("None", doc.RootElement.GetProperty("decalType").GetString());
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
        Assert.Equal(TexturePath + "backdrop_0_1.color.png", (string)stages[0]["baseColorMap"]!);
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

    // The DAE never embeds collision geometry: drivability is scene-level (collisionType
    // "Visible Mesh Final" on the TSStatic — physics built from the visual mesh), so a Colmesh
    // node in the DAE would only double the payload. This pins the no-Colmesh convention and the
    // collisionEnabled flow into the export item (which drives the TSStatic strings).
    [Fact]
    public void ExportChunkDae_NeverEmbedsColmesh_CollisionFlagFlowsToItem()
    {
        var visual = new BeamNG.Procedural3D.Core.Mesh { Name = "backdrop_0_0" };
        visual.Vertices.Add(new(new(0, 0, 0))); visual.Vertices.Add(new(new(1, 0, 0)));
        visual.Vertices.Add(new(new(1, 1, 0))); visual.Vertices.Add(new(new(0, 1, 0)));
        visual.Triangles.Add(new(0, 1, 2)); visual.Triangles.Add(new(0, 2, 3));

        var chunk = new BackdropChunkDefinition
        {
            Cx = 0, Cy = 0, LatticeX = 0, LatticeY = 0, LatticeWidth = 1, LatticeHeight = 1,
            SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 1, SourceRectHeight = 1,
            DaeFileName = "backdrop_0_0.dae", TextureFileName = "backdrop_0_0.color.png",
            MaterialName = "mt_backdrop_0_0", TextureSize = 256, DistanceToTerrainMeters = 0
        };
        var result = new BackdropChunkMeshResult
            { VisualMesh = visual, LeafCount = 1, SurfaceTriangleCount = 2, SurfaceVertexCount = 4 };

        var shapesDir = Path.Combine(_root, "art", "shapes", "MT_backdrop");
        var item = new BackdropSceneWriter().ExportChunkDae(chunk, result, shapesDir,
            collisionEnabled: true);

        var daePath = Path.Combine(shapesDir, "backdrop_0_0.dae");
        Assert.True(File.Exists(daePath));
        var dae = File.ReadAllText(daePath);
        Assert.DoesNotContain("Colmesh", dae);          // even with collision ON — scene-level only
        Assert.DoesNotContain("backdrop_0_0_a", dae);   // digits must be letter-mangled inside the DAE
        Assert.Contains("backdrop_a_a", dae);           // 0→a per DigitsToLetters
        Assert.Equal(4, item.Vertices);
        Assert.True(item.HasCollision);

        var offItem = new BackdropSceneWriter().ExportChunkDae(chunk, result, shapesDir,
            collisionEnabled: false);
        Assert.False(offItem.HasCollision);
    }

    // Pins the FlipUVVertical=false convention (see BackdropSceneWriter class doc; in-game CONFIRMED
    // 2026-07-28, 04-placement-followup.md Outcome B): the game's .color DDS cook samples V inverted
    // vs the raw north-up PNG, so the mesher's south-origin V must pass through UNCHANGED. The old
    // true convention (V → 1-V per ColladaExporter.TransformUV: `FlipUVVertical ? (uv.X, 1 - uv.Y)
    // : uv`) was correct only before the .color cooker entered the chain (77c16a3).
    // This test hand-builds asymmetric, distinctly-identifiable input UVs and asserts the emitted
    // <float_array> texcoords verbatim, so any silent re-flip (either direction) fails the suite.
    [Fact]
    public void ExportChunkDae_ExportsUVsUnchanged_NoVerticalFlip()
    {
        var visual = new BeamNG.Procedural3D.Core.Mesh { Name = "backdrop_0_0" };
        // Distinct U per vertex (identification) + asymmetric V (0 and 0.25) as suggested by the review.
        visual.Vertices.Add(new(new(0, 0, 0), new(0, 0, 1), new(0.10f, 0f)));
        visual.Vertices.Add(new(new(1, 0, 0), new(0, 0, 1), new(0.90f, 0.25f)));
        visual.Vertices.Add(new(new(1, 1, 0), new(0, 0, 1), new(0.90f, 1f)));
        visual.Vertices.Add(new(new(0, 1, 0), new(0, 0, 1), new(0.10f, 1f)));
        visual.Triangles.Add(new(0, 1, 2)); visual.Triangles.Add(new(0, 2, 3));

        var chunk = new BackdropChunkDefinition
        {
            Cx = 0, Cy = 0, LatticeX = 0, LatticeY = 0, LatticeWidth = 1, LatticeHeight = 1,
            SourceRectX = 0, SourceRectY = 0, SourceRectWidth = 1, SourceRectHeight = 1,
            DaeFileName = "backdrop_0_0.dae", TextureFileName = "backdrop_0_0.color.png",
            MaterialName = "mt_backdrop_0_0", TextureSize = 256, DistanceToTerrainMeters = 0
        };
        var result = new BackdropChunkMeshResult
            { VisualMesh = visual, LeafCount = 1, SurfaceTriangleCount = 2, SurfaceVertexCount = 4 };

        var shapesDir = Path.Combine(_root, "art", "shapes", "MT_backdrop");
        new BackdropSceneWriter().ExportChunkDae(chunk, result, shapesDir);

        var daePath = Path.Combine(shapesDir, "backdrop_0_0.dae");
        XNamespace ns = "http://www.collada.org/2005/11/COLLADASchema";
        var doc = XDocument.Load(daePath);

        // The LOD geometry (the only one — backdrop DAEs embed no Colmesh) carries the texcoords
        // BeamNG samples against the material's texture.
        var lodGeometry = doc.Descendants(ns + "geometry").Single();
        var texcoordSource = lodGeometry.Descendants(ns + "source")
            .Single(s => ((string)s.Attribute("id")!).EndsWith("-map1"));
        var floatArray = texcoordSource.Element(ns + "float_array")!;

        var values = floatArray.Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        // stride 2 (S, T) per vertex, in mesh.Vertices order — vertex 0 then vertex 1.
        Assert.Equal(0.10f, values[0], 3);   // U unchanged
        Assert.Equal(0.00f, values[1], 3);   // V unchanged (no flip)
        Assert.Equal(0.90f, values[2], 3);   // U unchanged
        Assert.Equal(0.25f, values[3], 3);   // V unchanged (no flip)
    }
}
