using System.Text.Json;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Writes bridge-deck scene entries into BeamNG scene files (NDJSON <c>items.level.json</c>):
/// - a <c>MT_bridges</c> <c>SimGroup</c> declared in the parent (<c>main/MissionGroup/items.level.json</c>)
/// - one <c>TSStatic</c> per exported deck in <c>main/MissionGroup/MT_bridges/items.level.json</c>
///
/// Scene hierarchy produced:
///   MissionGroup
///     └─ MT_bridges (SimGroup)
///         ├─ bridge_12 (TSStatic → art/shapes/MT_bridges/bridge_12.dae)
///         └─ ...
///
/// Mirrors <see cref="Building.BuildingSceneWriter"/>. Unlike buildings (local-coordinate meshes placed at
/// their world position), bridge decks are exported in BeamNG world coordinates (like the road mesh), so the
/// <c>TSStatic</c> sits at position (0,0,0) and the geometry aligns with the terrain/approach roads.
/// </summary>
public class BridgeSceneWriter
{
    /// <summary>
    /// Name of the SimGroup that contains all bridge deck TSStatic objects.
    /// </summary>
    public string GroupName { get; set; } = "MT_bridges";

    /// <summary>
    /// Ensures the <see cref="GroupName"/> SimGroup exists in the parent items.level.json (idempotent).
    /// Must be called so BeamNG discovers the MT_bridges subfolder.
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

        Console.WriteLine($"BridgeSceneWriter: Added '{GroupName}' SimGroup to {parentItemsPath}");
    }

    /// <summary>
    /// Writes the bridge deck items.level.json (NDJSON) — one TSStatic per deck. The SimGroup declaration
    /// belongs in the parent (see <see cref="EnsureSimGroupInParent"/>); this file holds only the leaf
    /// TSStatic entries.
    /// </summary>
    /// <param name="decks">The exported decks (from <see cref="BridgeDeckDaeExporter"/>).</param>
    /// <param name="outputPath">Absolute path for the items.level.json file.</param>
    /// <param name="shapePath">
    /// BeamNG-relative path prefix for the deck DAE files
    /// (e.g., "/levels/myLevel/art/shapes/MT_bridges/"). A trailing '/' is added if missing.
    /// </param>
    /// <returns>Number of TSStatic entries written.</returns>
    public int WriteSceneItems(
        IReadOnlyList<BridgeDeckExportItem> decks,
        string outputPath,
        string shapePath)
    {
        if (decks.Count == 0)
            return 0;

        if (!shapePath.EndsWith('/'))
            shapePath += "/";

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var items = new List<JsonDict>();
        foreach (var deck in decks)
            items.Add(CreateTSStaticEntry(deck, shapePath));

        SimItemsJsonSerializer.Save(outputPath, items);

        Console.WriteLine($"BridgeSceneWriter: Wrote {items.Count} TSStatic entries to {outputPath}");
        return items.Count;
    }

    /// <summary>
    /// Writes a minimal placeholder Material (flat base color, no textures) so the deck mesh's material
    /// reference resolves and the surface renders shaded instead of as a missing-material error (D3/D4).
    /// Written as a keyed materials.json (e.g. alongside the decks: art/shapes/MT_bridges/main.materials.json).
    /// Idempotent on the material name: if the file already defines a material with this name it is left
    /// untouched, so a real material added later is not clobbered.
    /// </summary>
    /// <param name="outputPath">Absolute path for the materials.json file.</param>
    /// <param name="materialName">Material name — must match the name the deck mesh references.</param>
    /// <returns>True if the placeholder was written; false if a material with that name already existed.</returns>
    public bool WritePlaceholderMaterial(
        string outputPath,
        string materialName = BridgeDeckDaeExporter.DefaultMaterialName)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Preserve any existing materials; don't overwrite a real material that shares the name.
        var existing = File.Exists(outputPath)
            ? ArtItemsJsonSerializer.Load(outputPath).ToList()
            : new List<JsonDict>();

        if (existing.Any(m => m.TryGetValue("name", out var n) && (n as string) == materialName))
            return false;

        existing.Add(CreatePlaceholderMaterialEntry(materialName));
        ArtItemsJsonSerializer.Save(outputPath, existing);

        Console.WriteLine($"BridgeSceneWriter: Wrote placeholder material '{materialName}' to {outputPath}");
        return true;
    }

    /// <summary>
    /// Builds a flat-color BeamNG Material entry (concrete-gray base color, fully rough, no maps).
    /// </summary>
    private static JsonDict CreatePlaceholderMaterialEntry(string materialName)
    {
        // Concrete-gray flat color so an unconfigured deck still reads as a solid surface.
        var stage0 = new JsonDict
        {
            ["baseColorFactor"] = new[] { 0.55f, 0.55f, 0.55f, 1f },
            ["roughnessFactor"] = 1.0f,
            ["metallicFactor"] = 0.0f
        };

        return new JsonDict
        {
            ["class"] = "Material",
            ["name"] = materialName,
            ["mapTo"] = materialName,
            ["internalName"] = materialName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            ["version"] = 1.5f,
            ["Stages"] = new[] { stage0, new JsonDict(), new JsonDict(), new JsonDict() }
        };
    }

    /// <summary>
    /// Creates a TSStatic scene entry for a bridge deck. The deck mesh carries world coordinates, so the
    /// object is placed at (0,0,0) with identity rotation (matching the road mesh exporter convention).
    /// </summary>
    private JsonDict CreateTSStaticEntry(BridgeDeckExportItem deck, string shapePath)
    {
        var dict = new JsonDict
        {
            ["class"] = "TSStatic",
            ["name"] = $"bridge_{deck.SplineId}",
            ["__parent"] = GroupName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            // World-coordinate mesh → place at origin so it aligns with terrain + approach roads.
            ["position"] = new float[] { 0f, 0f, 0f },
            ["rotationMatrix"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
            ["shapeName"] = shapePath + deck.DaeFileName,
            ["isRenderEnabled"] = true,
            ["useInstanceRenderData"] = true
        };

        return dict;
    }
}
