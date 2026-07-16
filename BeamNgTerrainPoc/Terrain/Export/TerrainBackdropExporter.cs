using System.Numerics;
using System.Text.Json;
using BeamNG.Procedural3D.Core;
using BeamNG.Procedural3D.Exporters;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeamNgTerrainPoc.Terrain.Export;

/// <summary>
///     Exports a low-detail, non-colliding visual terrain ring around the playable TerrainBlock.
///     The ring extends the finished terrain's edge heights outward and uses a downsampled copy
///     of the BaseColor Manager output. It does not enlarge or repaint the editable TerrainBlock.
/// </summary>
public static class TerrainBackdropExporter
{
    public const string GroupName = "MT_terrain_backdrop";
    public const string MaterialName = "mt_terrain_backdrop";
    public const string BaseColorFileName = "backdrop_basecolor.png";
    public const int MaximumTextureSize = 2048;

    private const string ShapeFileName = "terrain_backdrop.dae";
    private const float MinimumMeshStepMeters = 64f;
    private const int MaximumSegmentsAcross = 256;
    private const float SeamOffsetMeters = 0.05f;

    public static TerrainBackdropExportResult Export(
        float[,] heightMap,
        string levelPath,
        string levelName,
        int terrainSize,
        float metersPerPixel,
        float terrainBaseHeight,
        float distanceMeters)
    {
        ArgumentNullException.ThrowIfNull(heightMap);
        ArgumentException.ThrowIfNullOrWhiteSpace(levelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(levelName);

        if (terrainSize < 2 || heightMap.GetLength(0) < 2 || heightMap.GetLength(1) < 2)
            throw new ArgumentException("A terrain backdrop requires at least a 2 x 2 heightmap.", nameof(heightMap));
        if (metersPerPixel <= 0 || !float.IsFinite(metersPerPixel))
            throw new ArgumentOutOfRangeException(nameof(metersPerPixel), "Meters per pixel must be greater than zero.");
        if (distanceMeters <= 0 || !float.IsFinite(distanceMeters))
            throw new ArgumentOutOfRangeException(nameof(distanceMeters), "Backdrop distance must be greater than zero.");

        var terrainWorldSize = terrainSize * metersPerPixel;
        var innerHalfExtent = terrainWorldSize / 2f;
        var outerHalfExtent = innerHalfExtent + distanceMeters;
        var outerWorldSize = outerHalfExtent * 2f;
        var meshStep = Math.Max(MinimumMeshStepMeters, outerWorldSize / MaximumSegmentsAcross);

        var meshes = new List<Mesh>
        {
            BuildStrip("backdrop_north", -outerHalfExtent, outerHalfExtent, innerHalfExtent, outerHalfExtent),
            BuildStrip("backdrop_south", -outerHalfExtent, outerHalfExtent, -outerHalfExtent, -innerHalfExtent),
            BuildStrip("backdrop_west", -outerHalfExtent, -innerHalfExtent, -innerHalfExtent, innerHalfExtent),
            BuildStrip("backdrop_east", innerHalfExtent, outerHalfExtent, -innerHalfExtent, innerHalfExtent)
        };

        var shapesDirectory = Path.Combine(levelPath, "art", "shapes", GroupName);
        Directory.CreateDirectory(shapesDirectory);
        var daePath = Path.Combine(shapesDirectory, ShapeFileName);
        new ColladaExporter().ExportZUp(meshes, daePath);

        var materialPath = Path.Combine(shapesDirectory, "main.materials.json");
        WriteMaterial(materialPath, levelName);
        RefreshTexture(levelPath);
        WriteSceneEntry(levelPath, levelName);

        return new TerrainBackdropExportResult(
            daePath,
            materialPath,
            Path.Combine(shapesDirectory, BaseColorFileName),
            meshes.Sum(mesh => mesh.VertexCount),
            meshes.Sum(mesh => mesh.TriangleCount),
            distanceMeters,
            meshStep);

        Mesh BuildStrip(string name, float minX, float maxX, float minY, float maxY)
        {
            var segmentsX = Math.Max(1, (int)Math.Ceiling((maxX - minX) / meshStep));
            var segmentsY = Math.Max(1, (int)Math.Ceiling((maxY - minY) / meshStep));
            var mesh = new Mesh { Name = name, MaterialName = MaterialName };

            for (var y = 0; y <= segmentsY; y++)
            {
                var worldY = minY + (maxY - minY) * y / segmentsY;
                for (var x = 0; x <= segmentsX; x++)
                {
                    var worldX = minX + (maxX - minX) * x / segmentsX;
                    var sampledHeight = SampleClampedTerrainHeight(heightMap, worldX, worldY, innerHalfExtent);
                    var position = new Vector3(
                        worldX,
                        worldY,
                        terrainBaseHeight + sampledHeight - SeamOffsetMeters);
                    var u = (worldX + outerHalfExtent) / outerWorldSize;
                    var v = 1f - (worldY + outerHalfExtent) / outerWorldSize;
                    mesh.Vertices.Add(new Vertex(position, Vector3.UnitZ, new Vector2(u, v)));
                }
            }

            var stride = segmentsX + 1;
            for (var y = 0; y < segmentsY; y++)
            for (var x = 0; x < segmentsX; x++)
            {
                var bottomLeft = y * stride + x;
                var bottomRight = bottomLeft + 1;
                var topLeft = bottomLeft + stride;
                var topRight = topLeft + 1;
                mesh.Triangles.Add(new Triangle(bottomLeft, bottomRight, topRight));
                mesh.Triangles.Add(new Triangle(bottomLeft, topRight, topLeft));
            }

            ApplySmoothNormals(mesh);
            return mesh;
        }
    }

    /// <summary>
    ///     Refreshes the backdrop's intentionally lower-resolution texture from MT_basecolor.png.
    ///     If BaseColor Mode has not run yet, a neutral placeholder prevents a missing-texture error.
    /// </summary>
    public static bool RefreshTexture(string levelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(levelPath);
        var outputDirectory = Path.Combine(levelPath, "art", "shapes", GroupName);
        if (!Directory.Exists(outputDirectory))
            return false;

        var outputPath = Path.Combine(outputDirectory, BaseColorFileName);
        var baseColorPath = Path.Combine(levelPath, "art", "terrains", "MT_basecolor.png");
        if (File.Exists(baseColorPath))
        {
            var imageInfo = Image.Identify(baseColorPath);
            Size? targetSize = null;
            if (imageInfo.Width > MaximumTextureSize || imageInfo.Height > MaximumTextureSize)
            {
                var scale = Math.Min(
                    MaximumTextureSize / (double)imageInfo.Width,
                    MaximumTextureSize / (double)imageInfo.Height);
                targetSize = new Size(
                    Math.Max(1, (int)Math.Round(imageInfo.Width * scale)),
                    Math.Max(1, (int)Math.Round(imageInfo.Height * scale)));
            }

            var decoderOptions = new DecoderOptions { TargetSize = targetSize };
            using var image = Image.Load<Rgba32>(decoderOptions, baseColorPath);
            if (image.Width > MaximumTextureSize || image.Height > MaximumTextureSize)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaximumTextureSize, MaximumTextureSize),
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            SaveReplacing(image, outputPath);
            return true;
        }

        if (File.Exists(outputPath))
            return false;

        using var placeholder = new Image<Rgba32>(4, 4, new Rgba32(128, 128, 128, 255));
        placeholder.SaveAsPng(outputPath);
        return false;
    }

    private static float SampleClampedTerrainHeight(
        float[,] heightMap,
        float worldX,
        float worldY,
        float innerHalfExtent)
    {
        var width = heightMap.GetLength(1);
        var height = heightMap.GetLength(0);
        var clampedX = Math.Clamp(worldX, -innerHalfExtent, innerHalfExtent);
        var clampedY = Math.Clamp(worldY, -innerHalfExtent, innerHalfExtent);
        var pixelX = (clampedX + innerHalfExtent) / (innerHalfExtent * 2f) * (width - 1);
        var pixelY = (clampedY + innerHalfExtent) / (innerHalfExtent * 2f) * (height - 1);

        var x0 = Math.Clamp((int)Math.Floor(pixelX), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(pixelY), 0, height - 1);
        var x1 = Math.Min(width - 1, x0 + 1);
        var y1 = Math.Min(height - 1, y0 + 1);
        var tx = pixelX - x0;
        var ty = pixelY - y0;
        var bottom = heightMap[y0, x0] + (heightMap[y0, x1] - heightMap[y0, x0]) * tx;
        var top = heightMap[y1, x0] + (heightMap[y1, x1] - heightMap[y1, x0]) * tx;
        return bottom + (top - bottom) * ty;
    }

    private static void ApplySmoothNormals(Mesh mesh)
    {
        var normals = new Vector3[mesh.VertexCount];
        foreach (var triangle in mesh.Triangles)
        {
            var a = mesh.Vertices[triangle.V0].Position;
            var b = mesh.Vertices[triangle.V1].Position;
            var c = mesh.Vertices[triangle.V2].Position;
            var faceNormal = Vector3.Cross(b - a, c - a);
            if (faceNormal.LengthSquared() <= float.Epsilon)
                continue;
            normals[triangle.V0] += faceNormal;
            normals[triangle.V1] += faceNormal;
            normals[triangle.V2] += faceNormal;
        }

        for (var index = 0; index < mesh.VertexCount; index++)
        {
            var normal = normals[index].LengthSquared() > float.Epsilon
                ? Vector3.Normalize(normals[index])
                : Vector3.UnitZ;
            mesh.Vertices[index] = mesh.Vertices[index].WithNormal(normal);
        }
    }

    private static void WriteMaterial(string materialPath, string levelName)
    {
        var material = new Dictionary<string, object?>
        {
            ["class"] = "Material",
            ["name"] = MaterialName,
            ["mapTo"] = MaterialName,
            ["internalName"] = MaterialName,
            ["persistentId"] = Guid.NewGuid().ToString(),
            ["version"] = 1.5f,
            ["doubleSided"] = false,
            ["translucentBlendOp"] = "None",
            ["Stages"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["baseColorMap"] = $"/levels/{levelName}/art/shapes/{GroupName}/{BaseColorFileName}",
                    ["baseColorFactor"] = new[] { 1f, 1f, 1f, 1f },
                    ["roughnessFactor"] = 1f
                },
                new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                new Dictionary<string, object>()
            }
        };

        var root = new Dictionary<string, object?> { [MaterialName] = material };
        File.WriteAllText(materialPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSceneEntry(string levelPath, string levelName)
    {
        var parentItemsPath = Path.Combine(levelPath, "main", "MissionGroup", "items.level.json");
        Directory.CreateDirectory(Path.GetDirectoryName(parentItemsPath)!);
        var lines = File.Exists(parentItemsPath)
            ? File.ReadAllLines(parentItemsPath).ToList()
            : new List<string>();

        var hasGroup = lines.Any(line => IsNamedSceneObject(line, "SimGroup", GroupName));
        if (!hasGroup)
        {
            lines.Add(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = GroupName,
                ["class"] = "SimGroup",
                ["persistentId"] = Guid.NewGuid().ToString(),
                ["__parent"] = "MissionGroup"
            }));
            File.WriteAllLines(parentItemsPath, lines);
        }

        var sceneDirectory = Path.Combine(levelPath, "main", "MissionGroup", GroupName);
        Directory.CreateDirectory(sceneDirectory);
        var sceneItem = new Dictionary<string, object>
        {
            ["name"] = GroupName,
            ["class"] = "TSStatic",
            ["persistentId"] = Guid.NewGuid().ToString(),
            ["__parent"] = GroupName,
            ["position"] = new[] { 0f, 0f, 0f },
            ["rotationMatrix"] = new[] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f },
            ["shapeName"] = $"/levels/{levelName}/art/shapes/{GroupName}/{ShapeFileName}",
            ["collisionType"] = "None",
            ["decalType"] = "None",
            ["useInstanceRenderData"] = true
        };
        File.WriteAllText(
            Path.Combine(sceneDirectory, "items.level.json"),
            JsonSerializer.Serialize(sceneItem) + Environment.NewLine);
    }

    private static bool IsNamedSceneObject(string line, string className, string name)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("class", out var classElement) &&
                   classElement.GetString() == className &&
                   document.RootElement.TryGetProperty("name", out var nameElement) &&
                   nameElement.GetString() == name;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void SaveReplacing(Image<Rgba32> image, string outputPath)
    {
        if (File.Exists(outputPath))
            File.Delete(outputPath);
        image.SaveAsPng(outputPath);
    }
}

public sealed record TerrainBackdropExportResult(
    string DaePath,
    string MaterialPath,
    string TexturePath,
    int VertexCount,
    int TriangleCount,
    float DistanceMeters,
    float MeshStepMeters);
