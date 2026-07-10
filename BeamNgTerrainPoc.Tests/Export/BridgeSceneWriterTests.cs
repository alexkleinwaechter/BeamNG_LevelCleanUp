using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Export;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Step 3 of bridge-deck generation
///     (ai_docs/2026-06-03_bridge_generation/02-implementation-plan.md).
///
///     Verifies <see cref="BridgeSceneWriter"/> declares the <c>MT_bridges</c> SimGroup in the parent
///     (idempotently) and writes one <c>TSStatic</c> per deck pointing at its DAE, at world origin.
/// </summary>
public class BridgeSceneWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "beamng_bridge_scene_tests", Guid.NewGuid().ToString("N"));

    private string ParentItemsPath => Path.Combine(_root, "main", "MissionGroup", "items.level.json");
    private string GroupItemsPath => Path.Combine(_root, "main", "MissionGroup", "MT_bridges", "items.level.json");
    private const string ShapePath = "/levels/test_level/art/shapes/MT_bridges/";

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static List<BridgeDeckExportItem> SampleDecks() =>
    [
        new() { SplineId = 2, DaeFileName = "bridge_2.dae", OutputPath = "ignored", Vertices = 10, Triangles = 8 },
        new() { SplineId = 7, DaeFileName = "bridge_7.dae", OutputPath = "ignored", Vertices = 12, Triangles = 10 }
    ];

    private static List<JsonDocument> ReadNdjson(string path) =>
        File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l))
            .ToList();

    [Fact]
    public void EnsureSimGroupInParent_AddsGroup_AndIsIdempotent()
    {
        var writer = new BridgeSceneWriter();

        writer.EnsureSimGroupInParent(ParentItemsPath);
        writer.EnsureSimGroupInParent(ParentItemsPath); // second call must not duplicate

        var simGroups = ReadNdjson(ParentItemsPath)
            .Where(d => d.RootElement.TryGetProperty("class", out var c) && c.GetString() == "SimGroup"
                        && d.RootElement.TryGetProperty("name", out var n) && n.GetString() == "MT_bridges")
            .ToList();

        Assert.Single(simGroups);
        Assert.Equal("MissionGroup", simGroups[0].RootElement.GetProperty("__parent").GetString());
    }

    [Fact]
    public void WriteSceneItems_WritesOneTSStaticPerDeck_AtOriginPointingAtDae()
    {
        var writer = new BridgeSceneWriter();
        var decks = SampleDecks();

        var count = writer.WriteSceneItems(decks, GroupItemsPath, ShapePath);

        Assert.Equal(2, count);

        var entries = ReadNdjson(GroupItemsPath);
        Assert.Equal(2, entries.Count);

        foreach (var (doc, deck) in entries.Zip(decks))
        {
            var root = doc.RootElement;
            Assert.Equal("TSStatic", root.GetProperty("class").GetString());
            Assert.Equal($"bridge_{deck.SplineId}", root.GetProperty("name").GetString());
            Assert.Equal("MT_bridges", root.GetProperty("__parent").GetString());
            Assert.Equal(ShapePath + deck.DaeFileName, root.GetProperty("shapeName").GetString());

            var pos = root.GetProperty("position").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            Assert.Equal(new[] { 0f, 0f, 0f }, pos);
        }
    }

    [Fact]
    public void WriteSceneItems_AppendsTrailingSlashToShapePath()
    {
        var writer = new BridgeSceneWriter();
        var decks = SampleDecks();

        writer.WriteSceneItems(decks, GroupItemsPath, "/levels/test_level/art/shapes/MT_bridges"); // no trailing /

        var first = ReadNdjson(GroupItemsPath)[0].RootElement.GetProperty("shapeName").GetString();
        Assert.Equal("/levels/test_level/art/shapes/MT_bridges/bridge_2.dae", first);
    }

    [Fact]
    public void WritePlaceholderMaterial_WritesNamedFlatMaterial()
    {
        var materialsPath = Path.Combine(_root, "art", "shapes", "MT_bridges", "main.materials.json");
        var writer = new BridgeSceneWriter();

        var wrote = writer.WritePlaceholderMaterial(materialsPath, "bridge_deck_placeholder");

        Assert.True(wrote);
        Assert.True(File.Exists(materialsPath));

        // Keyed materials.json: { "bridge_deck_placeholder": { "class": "Material", ... } }
        using var doc = JsonDocument.Parse(File.ReadAllText(materialsPath));
        var mat = doc.RootElement.GetProperty("bridge_deck_placeholder");
        Assert.Equal("Material", mat.GetProperty("class").GetString());
        Assert.Equal("bridge_deck_placeholder", mat.GetProperty("name").GetString());

        var baseColor = mat.GetProperty("Stages").EnumerateArray().First()
            .GetProperty("baseColorFactor").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(4, baseColor.Length);
        Assert.Equal(1f, baseColor[3]); // opaque
    }

    [Fact]
    public void WritePlaceholderMaterial_IsIdempotent_AndPreservesOthers()
    {
        var materialsPath = Path.Combine(_root, "art", "shapes", "MT_bridges", "main.materials.json");
        var writer = new BridgeSceneWriter();

        Assert.True(writer.WritePlaceholderMaterial(materialsPath));
        Assert.False(writer.WritePlaceholderMaterial(materialsPath), "second write must be a no-op");

        using var doc = JsonDocument.Parse(File.ReadAllText(materialsPath));
        // Only one material key total, not duplicated.
        Assert.Equal(1, doc.RootElement.EnumerateObject().Count());
    }
}
