namespace BeamNG.Procedural3D.Exporters;

using System.Numerics;
using Assimp;
using BeamNG.Procedural3D.Core;

/// <summary>
/// Exports meshes to Collada (DAE) format using AssimpNet.
/// </summary>
public class ColladaExporter : IMeshExporter
{
    private readonly ColladaExportOptions _options;
    private readonly Dictionary<string, Core.Material> _materials;

    /// <summary>
    /// Creates a new ColladaExporter with default options.
    /// </summary>
    public ColladaExporter() : this(ColladaExportOptions.Default)
    {
    }

    /// <summary>
    /// Creates a new ColladaExporter with the specified options.
    /// </summary>
    public ColladaExporter(ColladaExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _materials = new Dictionary<string, Core.Material>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string FileExtension => ".dae";

    /// <summary>
    /// Registers a material to be included in the export.
    /// </summary>
    /// <param name="material">The material to register.</param>
    public void RegisterMaterial(Core.Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _materials[material.Name] = material;
    }

    /// <summary>
    /// Registers multiple materials to be included in the export.
    /// </summary>
    /// <param name="materials">The materials to register.</param>
    public void RegisterMaterials(IEnumerable<Core.Material> materials)
    {
        foreach (var material in materials)
        {
            RegisterMaterial(material);
        }
    }

    /// <inheritdoc />
    public void Export(Core.Mesh mesh, string filePath)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        Export([mesh], filePath);
    }

    /// <inheritdoc />
    public void Export(IEnumerable<Core.Mesh> meshes, string filePath)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var meshList = meshes.ToList();
        if (meshList.Count == 0)
        {
            throw new ArgumentException("At least one mesh is required for export.", nameof(meshes));
        }

        var scene = CreateScene(meshList);
        ExportScene(scene, filePath);
    }

    /// <summary>
    /// Creates an Assimp scene from the mesh collection.
    /// </summary>
    private Scene CreateScene(List<Core.Mesh> meshes)
    {
        var scene = new Scene();
        scene.RootNode = new Node(_options.RootNodeName);

        // Build material index mapping
        var materialIndexMap = BuildMaterialIndexMap(scene, meshes);

        // Add meshes to scene
        for (int i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            if (!mesh.HasGeometry)
            {
                continue;
            }

            int materialIndex = GetMaterialIndex(materialIndexMap, mesh.MaterialName);
            var assimpMesh = ConvertToAssimpMesh(mesh, materialIndex);

            int meshIndex = scene.MeshCount;
            scene.Meshes.Add(assimpMesh);

            // Create a node for this mesh
            var meshNode = new Node(mesh.Name, scene.RootNode);
            meshNode.MeshIndices.Add(meshIndex);
            scene.RootNode.Children.Add(meshNode);
        }

        return scene;
    }

    /// <summary>
    /// Builds the material index map and adds materials to the scene.
    /// </summary>
    private Dictionary<string, int> BuildMaterialIndexMap(Scene scene, List<Core.Mesh> meshes)
    {
        var materialIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Collect all unique material names from meshes
        var materialNames = meshes
            .Where(m => !string.IsNullOrEmpty(m.MaterialName))
            .Select(m => m.MaterialName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Add default material first (index 0)
        var defaultMaterial = CreateAssimpMaterial(Core.Material.CreateDefault());
        scene.Materials.Add(defaultMaterial);
        materialIndexMap[""] = 0;

        // Add materials for each unique name
        foreach (var materialName in materialNames)
        {
            if (_materials.TryGetValue(materialName, out var registeredMaterial))
            {
                var assimpMaterial = CreateAssimpMaterial(registeredMaterial);
                materialIndexMap[materialName] = scene.MaterialCount;
                scene.Materials.Add(assimpMaterial);
            }
            else
            {
                // Create a simple material with just the name
                var simpleMaterial = Core.Material.CreateDefault(materialName);
                var assimpMaterial = CreateAssimpMaterial(simpleMaterial);
                materialIndexMap[materialName] = scene.MaterialCount;
                scene.Materials.Add(assimpMaterial);
            }
        }

        return materialIndexMap;
    }

    /// <summary>
    /// Gets the material index for a given material name.
    /// </summary>
    private static int GetMaterialIndex(Dictionary<string, int> materialIndexMap, string? materialName)
    {
        if (string.IsNullOrEmpty(materialName))
        {
            return 0; // Default material
        }

        return materialIndexMap.TryGetValue(materialName, out int index) ? index : 0;
    }

    /// <summary>
    /// Creates an Assimp material from a Core.Material.
    /// </summary>
    private static Assimp.Material CreateAssimpMaterial(Core.Material material)
    {
        var assimpMaterial = new Assimp.Material
        {
            Name = material.Name
        };

        // Set colors
        assimpMaterial.ColorDiffuse = new Color4D(
            material.DiffuseColor.X,
            material.DiffuseColor.Y,
            material.DiffuseColor.Z,
            material.Opacity);

        assimpMaterial.ColorSpecular = new Color4D(
            material.SpecularColor.X,
            material.SpecularColor.Y,
            material.SpecularColor.Z,
            1f);

        assimpMaterial.ColorAmbient = new Color4D(
            material.AmbientColor.X,
            material.AmbientColor.Y,
            material.AmbientColor.Z,
            1f);

        assimpMaterial.Shininess = material.Shininess;
        assimpMaterial.Opacity = material.Opacity;

        // Set texture if provided
        if (!string.IsNullOrEmpty(material.DiffuseTexturePath))
        {
            assimpMaterial.TextureDiffuse = new TextureSlot
            {
                FilePath = material.DiffuseTexturePath,
                TextureType = TextureType.Diffuse,
                TextureIndex = 0,
                Mapping = TextureMapping.FromUV,
                UVIndex = 0,
                WrapModeU = TextureWrapMode.Wrap,
                WrapModeV = TextureWrapMode.Wrap
            };
        }

        if (!string.IsNullOrEmpty(material.NormalTexturePath))
        {
            assimpMaterial.TextureNormal = new TextureSlot
            {
                FilePath = material.NormalTexturePath,
                TextureType = TextureType.Normals,
                TextureIndex = 0,
                Mapping = TextureMapping.FromUV,
                UVIndex = 0
            };
        }

        if (!string.IsNullOrEmpty(material.SpecularTexturePath))
        {
            assimpMaterial.TextureSpecular = new TextureSlot
            {
                FilePath = material.SpecularTexturePath,
                TextureType = TextureType.Specular,
                TextureIndex = 0,
                Mapping = TextureMapping.FromUV,
                UVIndex = 0
            };
        }

        return assimpMaterial;
    }

    /// <summary>
    /// Converts a Core.Mesh to an Assimp.Mesh.
    /// </summary>
    private Assimp.Mesh ConvertToAssimpMesh(Core.Mesh mesh, int materialIndex)
    {
        var assimpMesh = new Assimp.Mesh(mesh.Name, PrimitiveType.Triangle)
        {
            MaterialIndex = materialIndex
        };

        // Convert vertices
        foreach (var vertex in mesh.Vertices)
        {
            var position = TransformPosition(vertex.Position);
            assimpMesh.Vertices.Add(new Vector3D(position.X, position.Y, position.Z));

            if (_options.IncludeNormals)
            {
                var normal = TransformNormal(vertex.Normal);
                assimpMesh.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
            }

            if (_options.IncludeUVs)
            {
                var uv = TransformUV(vertex.UV);
                assimpMesh.TextureCoordinateChannels[0].Add(new Vector3D(uv.X, uv.Y, 0));
            }
        }

        // Set UV component count if UVs are included
        if (_options.IncludeUVs && assimpMesh.TextureCoordinateChannels[0].Count > 0)
        {
            assimpMesh.UVComponentCount[0] = 2;
        }

        // Convert triangles to faces
        foreach (var triangle in mesh.Triangles)
        {
            var face = new Face();
            if (_options.FlipWindingOrder)
            {
                face.Indices.Add(triangle.V0);
                face.Indices.Add(triangle.V2);
                face.Indices.Add(triangle.V1);
            }
            else
            {
                face.Indices.Add(triangle.V0);
                face.Indices.Add(triangle.V1);
                face.Indices.Add(triangle.V2);
            }
            assimpMesh.Faces.Add(face);
        }

        return assimpMesh;
    }


    /// <summary>
    /// Transforms a position according to export options.
    /// </summary>
    private Vector3 TransformPosition(Vector3 position)
    {
        var result = position * _options.ScaleFactor;

        if (_options.ConvertToZUp)
        {
            // Convert from Z-up (BeamNG) to Y-up (Collada/Blender standard):
            // BeamNG: X=East, Y=North, Z=Up (right-handed)
            // Collada: X=Right, Y=Up, Z=Forward (right-handed)
            // 
            // To match Blender export with Forward=Y, Up=Z settings:
            // Transform: (-X, Z, Y) - negate X to maintain right-handedness
            // and match the orientation BeamNG expects from Blender exports
            result = new Vector3(-result.X, result.Z, result.Y);
        }

        return result;
    }

    /// <summary>
    /// Transforms a normal vector according to export options.
    /// </summary>
    private Vector3 TransformNormal(Vector3 normal)
    {
        if (_options.ConvertToZUp)
        {
            // Same transform as position: (-X, Z, Y)
            return new Vector3(-normal.X, normal.Z, normal.Y);
        }

        return normal;
    }

    /// <summary>
    /// Transforms UV coordinates according to export options.
    /// </summary>
    private Vector2 TransformUV(Vector2 uv)
    {
        if (_options.FlipUVVertical)
        {
            return new Vector2(uv.X, 1f - uv.Y);
        }

        return uv;
    }

    /// <summary>
    /// Exports the Assimp scene to a file.
    /// </summary>
    private static void ExportScene(Scene scene, string filePath)
    {
        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var context = new AssimpContext();

        // Get supported export formats and find Collada
        var exportFormats = context.GetSupportedExportFormats();
        var colladaFormat = exportFormats.FirstOrDefault(f =>
            f.FileExtension.Equals("dae", StringComparison.OrdinalIgnoreCase));

        if (colladaFormat == null)
        {
            throw new InvalidOperationException("Collada export format is not supported by the installed Assimp library.");
        }

        // Export the scene
        bool success = context.ExportFile(scene, filePath, colladaFormat.FormatId);

        if (!success)
        {
            throw new InvalidOperationException($"Failed to export scene to '{filePath}'.");
        }
    }
}
