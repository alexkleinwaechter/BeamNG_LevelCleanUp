using System.Text.Json;
using BeamNgTerrainPoc.Terrain.Export;

namespace BeamNgTerrainPoc.Tests.Export;

/// <summary>
///     Tunnel plan Phase 3b: NDJSON scene emission for tunnel tubes (MT_tunnels SimGroup + TSStatic
///     per span at origin/identity) and the idempotent placeholder material.
/// </summary>
public class TunnelSceneWriterTests
{
    private static TunnelExportItem Item(int spanId) => new()
    {
        SpanId = spanId,
        SplineId = 1,
        DaeFileName = $"tunnel_{spanId}.dae",
        OutputPath = $"X:/nowhere/tunnel_{spanId}.dae",
        Vertices = 100,
        Triangles = 50,
        LengthMeters = 120f
    };

    [Fact]
    public void WriteSceneItems_OneTSStaticPerTunnel_AtOriginIdentity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelscene_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "items.level.json");
            var written = new TunnelSceneWriter().WriteSceneItems(
                [Item(11), Item(22)], path, "/levels/test/art/shapes/MT_tunnels/");

            Assert.Equal(2, written);
            var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            Assert.Equal(2, lines.Count);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal("TSStatic", root.GetProperty("class").GetString());
            Assert.Equal("tunnel_11", root.GetProperty("name").GetString());
            Assert.Equal("MT_tunnels", root.GetProperty("__parent").GetString());
            Assert.Equal("/levels/test/art/shapes/MT_tunnels/tunnel_11.dae",
                root.GetProperty("shapeName").GetString());
            var pos = root.GetProperty("position").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            Assert.Equal(new[] { 0f, 0f, 0f }, pos);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EnsureSimGroupInParent_Idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelscene_" + Guid.NewGuid().ToString("N"));
        try
        {
            var parent = Path.Combine(dir, "items.level.json");
            var writer = new TunnelSceneWriter();
            writer.EnsureSimGroupInParent(parent);
            writer.EnsureSimGroupInParent(parent);

            var groupLines = File.ReadAllLines(parent)
                .Where(l => l.Contains("MT_tunnels") && l.Contains("SimGroup"))
                .ToList();
            Assert.Single(groupLines);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PlaceholderMaterial_WrittenOnce_RealMaterialPreserved()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tunnelscene_" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "main.materials.json");
            var writer = new TunnelSceneWriter();

            Assert.True(writer.WritePlaceholderMaterial(path));
            Assert.False(writer.WritePlaceholderMaterial(path)); // second call: already present

            var content = File.ReadAllText(path);
            Assert.Contains(TunnelDaeExporter.DefaultMaterialName, content);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
