using System.Text.Json;
using Grille.BeamNG.IO.Text;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
/// Writes tunnel scene entries into BeamNG scene files (NDJSON <c>items.level.json</c>) — the
/// <see cref="BridgeSceneWriter"/> clone for tunnels (tunnel plan Phase 3b):
///   MissionGroup
///     └─ MT_tunnels (SimGroup)
///         ├─ tunnel_{SpanId} (TSStatic → art/shapes/MT_tunnels/tunnel_{SpanId}.dae)
///         └─ ...
/// Tunnel tubes are exported in BeamNG world coordinates, so each TSStatic sits at (0,0,0) with
/// identity rotation. Idempotent SimGroup registration; placeholder material write preserves any
/// real material of the same name.
/// </summary>
public class TunnelSceneWriter
{
    /// <summary>Name of the SimGroup that contains all tunnel TSStatic objects.</summary>
    public string GroupName { get; set; } = "MT_tunnels";

    /// <summary>
    /// Ensures the <see cref="GroupName"/> SimGroup exists in the parent items.level.json (idempotent).
    /// </summary>
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

        Console.WriteLine($"TunnelSceneWriter: Added '{GroupName}' SimGroup to {parentItemsPath}");
    }

    /// <summary>
    /// Writes the tunnel items.level.json (NDJSON) — one TSStatic per exported tube.
    /// </summary>
    public int WriteSceneItems(
        IReadOnlyList<TunnelExportItem> tunnels,
        string outputPath,
        string shapePath)
    {
        if (tunnels.Count == 0)
            return 0;

        if (!shapePath.EndsWith('/'))
            shapePath += "/";

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var items = new List<JsonDict>();
        foreach (var tunnel in tunnels)
        {
            items.Add(new JsonDict
            {
                ["class"] = "TSStatic",
                ["name"] = $"tunnel_{tunnel.SpanId}",
                ["__parent"] = GroupName,
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["position"] = new float[] { 0f, 0f, 0f },
                ["rotationMatrix"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
                ["shapeName"] = shapePath + tunnel.DaeFileName,
                ["isRenderEnabled"] = true,
                ["useInstanceRenderData"] = true
            });
        }

        SimItemsJsonSerializer.Save(outputPath, items);

        Console.WriteLine($"TunnelSceneWriter: Wrote {items.Count} TSStatic entries to {outputPath}");
        return items.Count;
    }

    /// <summary>
    /// Writes a minimal placeholder Material (flat dark-concrete base color) so the tube mesh's
    /// material reference resolves. Idempotent on the material name.
    /// </summary>
    public bool WritePlaceholderMaterial(
        string outputPath,
        string materialName = TunnelDaeExporter.DefaultMaterialName)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var existing = File.Exists(outputPath)
            ? ArtItemsJsonSerializer.Load(outputPath).ToList()
            : new List<JsonDict>();

        if (existing.Any(m => m.TryGetValue("name", out var n) && (n as string) == materialName))
            return false;

        // Darker than bridge concrete — an unlit bore should read dim, not bright gray.
        var stage0 = new JsonDict
        {
            ["baseColorFactor"] = new[] { 0.42f, 0.42f, 0.42f, 1f },
            ["roughnessFactor"] = 1.0f,
            ["metallicFactor"] = 0.0f
        };

        existing.Add(new JsonDict
        {
            ["class"] = "Material",
            ["name"] = materialName,
            ["mapTo"] = materialName,
            ["internalName"] = materialName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            ["version"] = 1.5f,
            ["Stages"] = new[] { stage0, new JsonDict(), new JsonDict(), new JsonDict() }
        });
        ArtItemsJsonSerializer.Save(outputPath, existing);

        Console.WriteLine($"TunnelSceneWriter: Wrote placeholder material '{materialName}' to {outputPath}");
        return true;
    }
}
