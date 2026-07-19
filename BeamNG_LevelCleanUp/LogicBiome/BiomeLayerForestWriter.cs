using System.Text.Json;
using BeamNG_LevelCleanUp.Objects;
using BeamNgTerrainPoc.Terrain.Biome;
using BeamNgTerrainPoc.Terrain.Utils;

namespace BeamNG_LevelCleanUp.LogicBiome;

/// <summary>
/// Streaming writer for one biome layer: each accepted placement goes straight to disk —
/// one NDJSON line into the layer's owned forest4.json and one identity-record line into
/// the MT_Biome/items sidecar (the delete ledger). Nothing is buffered in memory, so
/// 500k+ item layers cost no more RAM than 500 items.
/// Use as the sink of <see cref="BiomePlacementSampler.SampleZoneStreaming"/>.
/// </summary>
public sealed class BiomeLayerForestWriter : IDisposable
{
    private readonly string _levelPath;
    private readonly string _layerId;
    private readonly int _terrainSizePixels;
    private readonly float _metersPerPixel;
    private readonly string _forestAbsolutePath;
    private readonly JsonSerializerOptions _oneLineOptions = BeamJsonOptions.GetJsonSerializerOneLineOptions();

    private StreamWriter? _forestWriter;
    private StreamWriter? _sidecarWriter;

    // Reused per Write — with 500k+ items, per-item DTO allocations are pure GC pressure.
    private readonly Forest _forestItem = new()
    {
        type = string.Empty,
        pos = new List<double> { 0, 0, 0 },
        rotationMatrix = new List<double> { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
        scale = 1,
    };
    private readonly BiomeManifestItem _record = new();

    public int Count { get; private set; }

    public string ForestFileRelativePath { get; }

    public BiomeLayerForestWriter(string levelPath, string sourceKey, string layerId, int terrainSizePixels, float metersPerPixel)
    {
        _levelPath = levelPath;
        _layerId = layerId;
        _terrainSizePixels = terrainSizePixels;
        _metersPerPixel = metersPerPixel;

        ForestFileRelativePath = BiomeForestWriter.GetForestFileRelativePath(sourceKey, layerId);
        _forestAbsolutePath = Path.Join(levelPath, ForestFileRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(_forestAbsolutePath)!);

        var sidecarPath = BiomeManifestStore.GetLayerItemsPath(levelPath, layerId);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);

        _forestWriter = new StreamWriter(File.Create(_forestAbsolutePath));
        _sidecarWriter = new StreamWriter(File.Create(sidecarPath));
    }

    public void Write(BiomePlacement placement)
    {
        var world = BeamNgCoordinateTransformer.TerrainToWorld(
            placement.TerrainX, placement.TerrainY, placement.WorldZ,
            _terrainSizePixels, _metersPerPixel);

        var cos = Math.Cos(placement.YawRadians);
        var sin = Math.Sin(placement.YawRadians);

        _forestItem.type = placement.TypeName;
        _forestItem.pos[0] = world.X;
        _forestItem.pos[1] = world.Y;
        _forestItem.pos[2] = world.Z;
        _forestItem.rotationMatrix[0] = cos;
        _forestItem.rotationMatrix[1] = sin;
        _forestItem.rotationMatrix[3] = -sin;
        _forestItem.rotationMatrix[4] = cos;
        _forestItem.scale = placement.Scale;
        _forestWriter!.WriteLine(JsonSerializer.Serialize(_forestItem, _oneLineOptions));

        _record.Type = placement.TypeName;
        _record.Pos[0] = world.X;
        _record.Pos[1] = world.Y;
        _record.Pos[2] = world.Z;
        _record.Scale = placement.Scale;
        _sidecarWriter!.WriteLine(JsonSerializer.Serialize(_record, _oneLineOptions));

        Count++;
    }

    /// <summary>
    /// Closes both files and returns the forest file's SHA-256 (the fast-path delete check).
    /// </summary>
    public string Complete()
    {
        CloseWriters();
        return BiomeManifestStore.ComputeFileSha256(_forestAbsolutePath);
    }

    public void Dispose() => CloseWriters();

    private void CloseWriters()
    {
        _forestWriter?.Dispose();
        _forestWriter = null;
        _sidecarWriter?.Dispose();
        _sidecarWriter = null;
    }
}
