using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;
using Assimp;
using Material = BeamNG.Procedural3D.Core.Material;
using Mesh = BeamNG.Procedural3D.Core.Mesh;

namespace BeamNG.Procedural3D.Exporters;

/// <summary>
///     Exports meshes to Collada (DAE) format.
///     - Simple export (flat mesh list): uses AssimpNet
///     - BeamNG LOD export (BeamNgDaeScene): writes Collada XML directly to produce
///       one geometry per LOD with multiple triangles groups per material
/// </summary>
public class ColladaExporter : IMeshExporter
{
    private static readonly XNamespace Ns = "http://www.collada.org/2005/11/COLLADASchema";

    private readonly Dictionary<string, Material> _materials;
    private readonly ColladaExportOptions _options;

    /// <summary>
    ///     Creates a new ColladaExporter with default options.
    /// </summary>
    public ColladaExporter() : this(ColladaExportOptions.Default)
    {
    }

    /// <summary>
    ///     Creates a new ColladaExporter with the specified options.
    /// </summary>
    public ColladaExporter(ColladaExportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _materials = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string FileExtension => ".dae";

    /// <inheritdoc />
    public void Export(Mesh mesh, string filePath)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        Export([mesh], filePath);
    }

    /// <inheritdoc />
    public void Export(IEnumerable<Mesh> meshes, string filePath)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var meshList = meshes.ToList();
        if (meshList.Count == 0)
            throw new ArgumentException("At least one mesh is required for export.", nameof(meshes));

        var scene = CreateScene(meshList);
        ExportScene(scene, filePath);
    }

    /// <summary>
    ///     Registers a material to be included in the export.
    /// </summary>
    public void RegisterMaterial(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        _materials[material.Name] = material;
    }

    /// <summary>
    ///     Registers multiple materials to be included in the export.
    /// </summary>
    public void RegisterMaterials(IEnumerable<Material> materials)
    {
        foreach (var material in materials) RegisterMaterial(material);
    }

    /// <summary>
    ///     Exports a BeamNG-compatible DAE with LOD hierarchy and collision mesh.
    ///     Writes Collada XML directly (bypassing Assimp) to produce one geometry
    ///     per LOD with multiple triangles groups — matching the BeamNG convention.
    ///     Node tree:
    ///     base00
    ///     ├── nulldetail{N}        → cull threshold (optional, only if NullDetailPixelSize > 0)
    ///     ├── start01
    ///     │   ├── Colmesh-1        → collision geometry (LOD0 without materials)
    ///     │   ├── {name}_a{px}     → LOD0
    ///     │   ├── {name}_a{px}     → LOD1
    ///     │   └── {name}_a{px}     → LOD2
    ///     └── collision-1          → empty marker node
    /// </summary>
    public void Export(BeamNgDaeScene daeScene, string filePath)
    {
        ArgumentNullException.ThrowIfNull(daeScene);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (daeScene.LodLevels.Count == 0)
            throw new ArgumentException("At least one LOD level is required.", nameof(daeScene));

        WriteBeamNgDae(daeScene, filePath);
    }

    // =========================================================================
    // Direct Collada XML writer for BeamNG DAE files
    // =========================================================================

    /// <summary>
    ///     Writes a complete BeamNG-compatible Collada DAE file using XDocument.
    ///     Produces one geometry per LOD with shared vertex sources and multiple
    ///     triangles groups (one per material) — matching eca_bld_fv_house_a.DAE.
    /// </summary>
    private void WriteBeamNgDae(BeamNgDaeScene daeScene, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // BaseName must not contain digits inside the DAE — it confuses BeamNG's LOD system.
        var safeBaseName = DigitsToLetters(daeScene.BaseName);

        // Collect unique material names across all LODs and collision
        var uniqueMaterials = CollectUniqueMaterials(daeScene);

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "COLLADA",
                new XAttribute("version", "1.4.1"),
                BuildAssetElement(),
                BuildLibraryEffects(uniqueMaterials),
                BuildLibraryMaterials(uniqueMaterials),
                BuildLibraryGeometries(daeScene, safeBaseName),
                BuildLibraryVisualScenes(daeScene, safeBaseName, uniqueMaterials),
                new XElement(Ns + "scene",
                    new XElement(Ns + "instance_visual_scene",
                        new XAttribute("url", "#BeamNGScene")))));

        doc.Save(filePath);
    }

    /// <summary>
    ///     Collects all unique material names from a BeamNgDaeScene (LODs + collision).
    /// </summary>
    private static List<string> CollectUniqueMaterials(BeamNgDaeScene daeScene)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var lod in daeScene.LodLevels)
        foreach (var mesh in lod.Meshes)
        {
            if (!string.IsNullOrEmpty(mesh.MaterialName) && seen.Add(mesh.MaterialName))
                result.Add(mesh.MaterialName);
        }

        // ColmeshMeshes have no materials (collision only), so nothing to collect from them

        return result;
    }

    /// <summary>
    ///     Builds the COLLADA asset element with Z_UP coordinate system.
    /// </summary>
    private static XElement BuildAssetElement()
    {
        return new XElement(Ns + "asset",
            new XElement(Ns + "contributor",
                new XElement(Ns + "authoring_tool", "BeamNG.Procedural3D")),
            new XElement(Ns + "created", DateTime.UtcNow.ToString("O")),
            new XElement(Ns + "modified", DateTime.UtcNow.ToString("O")),
            new XElement(Ns + "unit",
                new XAttribute("meter", "1"),
                new XAttribute("name", "meter")),
            new XElement(Ns + "up_axis", "Z_UP"));
    }

    /// <summary>
    ///     Builds library_effects with a blinn effect per material.
    /// </summary>
    private XElement BuildLibraryEffects(List<string> materialNames)
    {
        var libEffects = new XElement(Ns + "library_effects");

        foreach (var matName in materialNames)
        {
            var mat = _materials.TryGetValue(matName, out var registered)
                ? registered
                : Material.CreateDefault(matName);

            libEffects.Add(BuildEffectElement(mat));
        }

        return libEffects;
    }

    /// <summary>
    ///     Builds a single Collada effect element (blinn shader).
    /// </summary>
    private static XElement BuildEffectElement(Material material)
    {
        var technique = new XElement(Ns + "technique",
            new XAttribute("sid", "common"));

        var blinn = new XElement(Ns + "blinn",
            new XElement(Ns + "emission",
                new XElement(Ns + "color", "0 0 0 1")),
            new XElement(Ns + "ambient",
                new XElement(Ns + "color", FormatColor(material.AmbientColor))),
            new XElement(Ns + "diffuse",
                new XElement(Ns + "color", FormatColor(material.DiffuseColor))),
            new XElement(Ns + "specular",
                new XElement(Ns + "color", FormatColor(material.SpecularColor))),
            new XElement(Ns + "shininess",
                new XElement(Ns + "float", F(material.Shininess))),
            new XElement(Ns + "transparent",
                new XAttribute("opaque", "A_ONE"),
                new XElement(Ns + "color", "1 1 1 1")),
            new XElement(Ns + "transparency",
                new XElement(Ns + "float", F(material.Opacity))));

        technique.Add(blinn);

        var profileCommon = new XElement(Ns + "profile_COMMON", technique);

        // If there's a diffuse texture, add sampler/surface params and use texture reference
        if (!string.IsNullOrEmpty(material.DiffuseTexturePath))
        {
            var surfaceSid = material.Name + "-surface";
            var samplerSid = material.Name + "-sampler";

            profileCommon.AddFirst(
                new XElement(Ns + "newparam",
                    new XAttribute("sid", surfaceSid),
                    new XElement(Ns + "surface",
                        new XAttribute("type", "2D"),
                        new XElement(Ns + "init_from", material.DiffuseTexturePath))),
                new XElement(Ns + "newparam",
                    new XAttribute("sid", samplerSid),
                    new XElement(Ns + "sampler2D",
                        new XElement(Ns + "source", surfaceSid))));

            // Replace diffuse color with texture reference
            var diffuseEl = blinn.Element(Ns + "diffuse");
            diffuseEl?.RemoveAll();
            diffuseEl?.Add(new XElement(Ns + "texture",
                new XAttribute("texture", samplerSid),
                new XAttribute("texcoord", "CHANNEL1")));
        }

        return new XElement(Ns + "effect",
            new XAttribute("id", material.Name),
            profileCommon);
    }

    /// <summary>
    ///     Builds library_materials referencing effects.
    /// </summary>
    private static XElement BuildLibraryMaterials(List<string> materialNames)
    {
        var libMaterials = new XElement(Ns + "library_materials");

        foreach (var matName in materialNames)
        {
            libMaterials.Add(new XElement(Ns + "material",
                new XAttribute("id", matName + "-material"),
                new XAttribute("name", matName),
                new XElement(Ns + "instance_effect",
                    new XAttribute("url", "#" + matName))));
        }

        return libMaterials;
    }

    /// <summary>
    ///     Builds library_geometries with one geometry per LOD and one for collision.
    /// </summary>
    private XElement BuildLibraryGeometries(BeamNgDaeScene daeScene, string safeBaseName)
    {
        var libGeo = new XElement(Ns + "library_geometries");

        // Colmesh collision geometry (placed under start01 in the visual scene)
        if (daeScene.ColmeshMeshes is { Count: > 0 })
        {
            var colmeshMeshes = daeScene.ColmeshMeshes.Where(m => m.HasGeometry).ToList();
            if (colmeshMeshes.Count > 0)
                libGeo.Add(BuildGeometryElement("Colmesh-1", colmeshMeshes));
        }

        // LOD geometries
        foreach (var lod in daeScene.LodLevels.OrderBy(l => l.PixelSize))
        {
            var lodName = $"{safeBaseName}_{lod.Suffix}";
            var meshes = lod.Meshes.Where(m => m.HasGeometry).ToList();
            if (meshes.Count > 0)
                libGeo.Add(BuildGeometryElement(lodName, meshes));
        }

        return libGeo;
    }

    /// <summary>
    ///     Builds a single geometry element with shared sources and one triangles group per mesh/material.
    ///     All meshes are concatenated into shared position/normal/texcoord arrays.
    ///     Since our Mesh has 1:1 vertex data, VERTEX/NORMAL/TEXCOORD indices are identical.
    /// </summary>
    private XElement BuildGeometryElement(string name, List<Mesh> meshes)
    {
        var geoId = "geom-" + name;
        var posId = geoId + "-positions";
        var normId = geoId + "-normals";
        var tcId = geoId + "-map1";
        var verticesId = geoId + "-vertices";

        // Concatenate all vertex data from all meshes
        var positions = new StringBuilder();
        var normals = new StringBuilder();
        var texcoords = new StringBuilder();
        int totalVertexCount = 0;
        bool hasNormals = _options.IncludeNormals;
        bool hasTexCoords = _options.IncludeUVs;

        // Track per-mesh base vertex offsets for triangle index offsetting
        var meshBaseOffsets = new List<int>();

        foreach (var mesh in meshes)
        {
            meshBaseOffsets.Add(totalVertexCount);

            foreach (var vertex in mesh.Vertices)
            {
                var pos = TransformPositionZUp(vertex.Position);
                if (positions.Length > 0) positions.Append(' ');
                positions.Append(F(pos.X)).Append(' ').Append(F(pos.Y)).Append(' ').Append(F(pos.Z));

                if (hasNormals)
                {
                    if (normals.Length > 0) normals.Append(' ');
                    normals.Append(F(vertex.Normal.X)).Append(' ')
                        .Append(F(vertex.Normal.Y)).Append(' ')
                        .Append(F(vertex.Normal.Z));
                }

                if (hasTexCoords)
                {
                    var uv = TransformUV(vertex.UV);
                    if (texcoords.Length > 0) texcoords.Append(' ');
                    texcoords.Append(F(uv.X)).Append(' ').Append(F(uv.Y));
                }
            }

            totalVertexCount += mesh.VertexCount;
        }

        // Build mesh XML
        var meshEl = new XElement(Ns + "mesh");

        // Positions source
        meshEl.Add(BuildSourceElement(posId, positions.ToString(), totalVertexCount, 3,
            [("X", "float"), ("Y", "float"), ("Z", "float")]));

        // Normals source
        if (hasNormals)
            meshEl.Add(BuildSourceElement(normId, normals.ToString(), totalVertexCount, 3,
                [("X", "float"), ("Y", "float"), ("Z", "float")]));

        // TexCoords source (stride=2: S, T)
        if (hasTexCoords)
            meshEl.Add(BuildSourceElement(tcId, texcoords.ToString(), totalVertexCount, 2,
                [("S", "float"), ("T", "float")]));

        // Vertices element (references positions)
        meshEl.Add(new XElement(Ns + "vertices",
            new XAttribute("id", verticesId),
            new XElement(Ns + "input",
                new XAttribute("semantic", "POSITION"),
                new XAttribute("source", "#" + posId))));

        // Triangles groups (one per mesh/material)
        for (int m = 0; m < meshes.Count; m++)
        {
            var mesh = meshes[m];
            var baseVertex = meshBaseOffsets[m];

            var triEl = new XElement(Ns + "triangles",
                new XAttribute("count", mesh.TriangleCount));

            if (!string.IsNullOrEmpty(mesh.MaterialName))
                triEl.Add(new XAttribute("material", mesh.MaterialName));

            int offset = 0;
            triEl.Add(new XElement(Ns + "input",
                new XAttribute("offset", offset++),
                new XAttribute("semantic", "VERTEX"),
                new XAttribute("source", "#" + verticesId)));

            if (hasNormals)
                triEl.Add(new XElement(Ns + "input",
                    new XAttribute("offset", offset++),
                    new XAttribute("semantic", "NORMAL"),
                    new XAttribute("source", "#" + normId)));

            if (hasTexCoords)
                triEl.Add(new XElement(Ns + "input",
                    new XAttribute("offset", offset),
                    new XAttribute("semantic", "TEXCOORD"),
                    new XAttribute("set", "0"),
                    new XAttribute("source", "#" + tcId)));

            // Build <p> index data
            var pData = BuildTriangleIndices(mesh, baseVertex, hasNormals, hasTexCoords);
            triEl.Add(new XElement(Ns + "p", pData));

            meshEl.Add(triEl);
        }

        return new XElement(Ns + "geometry",
            new XAttribute("id", geoId),
            new XAttribute("name", name),
            meshEl);
    }

    /// <summary>
    ///     Builds the triangle index string for a mesh. Each triangle vertex emits
    ///     the same index for VERTEX, NORMAL, and TEXCOORD (1:1 vertex data).
    /// </summary>
    private string BuildTriangleIndices(Mesh mesh, int baseVertex, bool hasNormals, bool hasTexCoords)
    {
        // Calculate how many indices per vertex
        int indicesPerVertex = 1; // VERTEX always present
        if (hasNormals) indicesPerVertex++;
        if (hasTexCoords) indicesPerVertex++;

        var sb = new StringBuilder(mesh.TriangleCount * 3 * indicesPerVertex * 6); // rough estimate

        bool first = true;
        foreach (var tri in mesh.Triangles)
        {
            int v0, v1, v2;
            if (_options.FlipWindingOrder)
            {
                v0 = tri.V0 + baseVertex;
                v1 = tri.V2 + baseVertex;
                v2 = tri.V1 + baseVertex;
            }
            else
            {
                v0 = tri.V0 + baseVertex;
                v1 = tri.V1 + baseVertex;
                v2 = tri.V2 + baseVertex;
            }

            if (!first) sb.Append(' ');
            first = false;

            AppendVertexIndices(sb, v0, hasNormals, hasTexCoords);
            sb.Append(' ');
            AppendVertexIndices(sb, v1, hasNormals, hasTexCoords);
            sb.Append(' ');
            AppendVertexIndices(sb, v2, hasNormals, hasTexCoords);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Appends the index set for a single vertex (same index for all semantics).
    /// </summary>
    private static void AppendVertexIndices(StringBuilder sb, int idx, bool hasNormals, bool hasTexCoords)
    {
        sb.Append(idx);
        if (hasNormals) sb.Append(' ').Append(idx);
        if (hasTexCoords) sb.Append(' ').Append(idx);
    }

    /// <summary>
    ///     Builds a Collada source element from raw float data.
    /// </summary>
    private static XElement BuildSourceElement(string sourceId, string floatData,
        int vertexCount, int stride, (string name, string type)[] parameters)
    {
        var arrayId = sourceId + "-array";
        int floatCount = vertexCount * stride;

        var accessor = new XElement(Ns + "accessor",
            new XAttribute("source", "#" + arrayId),
            new XAttribute("count", vertexCount),
            new XAttribute("stride", stride));

        foreach (var (name, type) in parameters)
            accessor.Add(new XElement(Ns + "param",
                new XAttribute("name", name),
                new XAttribute("type", type)));

        return new XElement(Ns + "source",
            new XAttribute("id", sourceId),
            new XElement(Ns + "float_array",
                new XAttribute("id", arrayId),
                new XAttribute("count", floatCount),
                floatData),
            new XElement(Ns + "technique_common", accessor));
    }

    /// <summary>
    ///     Builds library_visual_scenes with the BeamNG node hierarchy.
    ///     Structure matches real BeamNG DAEs (e.g., eca_bld_fv_house_a.DAE):
    ///     base00
    ///     ├── nulldetail{N}      (cull threshold, optional)
    ///     ├── start01
    ///     │   ├── Colmesh-1      (collision geometry, no materials)
    ///     │   ├── {name}_a40     (LOD0 - lowest detail)
    ///     │   ├── {name}_a100    (LOD1)
    ///     │   └── {name}_a250    (LOD2 - highest detail)
    ///     └── collision-1        (empty marker node)
    /// </summary>
    private XElement BuildLibraryVisualScenes(BeamNgDaeScene daeScene, string safeBaseName,
        List<string> allMaterials)
    {
        var start01 = new XElement(Ns + "node",
            new XAttribute("id", "node-start01"),
            new XAttribute("name", "start01"));

        // Colmesh-1 under start01 (collision geometry, no material bindings)
        if (daeScene.ColmeshMeshes is { Count: > 0 })
        {
            var colmeshMeshes = daeScene.ColmeshMeshes.Where(m => m.HasGeometry).ToList();
            if (colmeshMeshes.Count > 0)
                start01.Add(BuildNodeElement("Colmesh-1", []));
        }

        // LOD nodes under start01
        foreach (var lod in daeScene.LodLevels.OrderBy(l => l.PixelSize))
        {
            var lodName = $"{safeBaseName}_{lod.Suffix}";
            var meshes = lod.Meshes.Where(m => m.HasGeometry).ToList();
            if (meshes.Count == 0) continue;

            var lodMaterials = meshes
                .Where(m => !string.IsNullOrEmpty(m.MaterialName))
                .Select(m => m.MaterialName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            start01.Add(BuildNodeElement(lodName, lodMaterials));
        }

        // collision-1 - empty marker node under base00
        var collision1 = new XElement(Ns + "node",
            new XAttribute("id", "node-collision-1"),
            new XAttribute("name", "collision-1"));

        var base00 = new XElement(Ns + "node",
            new XAttribute("id", "node-base00"),
            new XAttribute("name", "base00"));

        // nulldetail{N} - cull threshold (only if NullDetailPixelSize > 0)
        if (daeScene.NullDetailPixelSize > 0)
        {
            var nullDetailName = $"nulldetail{daeScene.NullDetailPixelSize}";
            base00.Add(new XElement(Ns + "node",
                new XAttribute("id", $"node-{nullDetailName}"),
                new XAttribute("name", nullDetailName)));
        }

        base00.Add(start01);
        base00.Add(collision1);

        return new XElement(Ns + "library_visual_scenes",
            new XElement(Ns + "visual_scene",
                new XAttribute("id", "BeamNGScene"),
                base00));
    }

    /// <summary>
    ///     Builds a visual scene node with instance_geometry and bind_material.
    /// </summary>
    private static XElement BuildNodeElement(string name, List<string> materialNames)
    {
        var geoId = "geom-" + name;

        var instanceGeo = new XElement(Ns + "instance_geometry",
            new XAttribute("url", "#" + geoId));

        if (materialNames.Count > 0)
        {
            var techniqueCommon = new XElement(Ns + "technique_common");

            foreach (var matName in materialNames)
            {
                var instMat = new XElement(Ns + "instance_material",
                    new XAttribute("symbol", matName),
                    new XAttribute("target", "#" + matName + "-material"),
                    new XElement(Ns + "bind_vertex_input",
                        new XAttribute("input_semantic", "TEXCOORD"),
                        new XAttribute("input_set", "0"),
                        new XAttribute("semantic", "CHANNEL1")));
                techniqueCommon.Add(instMat);
            }

            instanceGeo.Add(new XElement(Ns + "bind_material", techniqueCommon));
        }

        return new XElement(Ns + "node",
            new XAttribute("id", "node-" + name),
            new XAttribute("name", name),
            instanceGeo);
    }

    // =========================================================================
    // Coordinate transforms
    // =========================================================================

    /// <summary>
    ///     Transforms a position for Z_UP DAE output. No axis swap needed since
    ///     our internal coordinates are already Z-up. Only applies scale factor.
    /// </summary>
    private Vector3 TransformPositionZUp(Vector3 position)
    {
        return position * _options.ScaleFactor;
    }

    /// <summary>
    ///     Transforms a position according to export options (Y-up conversion for Assimp path).
    /// </summary>
    private Vector3 TransformPosition(Vector3 position)
    {
        var result = position * _options.ScaleFactor;

        if (_options.ConvertToZUp)
            // Convert from Z-up (BeamNG) to Y-up (Collada/Blender standard):
            // Transform: (-X, Z, Y) - negate X to maintain right-handedness
            result = new Vector3(-result.X, result.Z, result.Y);

        return result;
    }

    /// <summary>
    ///     Transforms a normal vector according to export options (Y-up conversion for Assimp path).
    /// </summary>
    private Vector3 TransformNormal(Vector3 normal)
    {
        if (_options.ConvertToZUp)
            return new Vector3(-normal.X, normal.Z, normal.Y);

        return normal;
    }

    /// <summary>
    ///     Transforms UV coordinates according to export options.
    /// </summary>
    private Vector2 TransformUV(Vector2 uv)
    {
        if (_options.FlipUVVertical) return new Vector2(uv.X, 1f - uv.Y);

        return uv;
    }

    // =========================================================================
    // Float formatting helpers
    // =========================================================================

    /// <summary>
    ///     Formats a float for Collada output (invariant culture, 6 significant digits).
    /// </summary>
    private static string F(float value)
    {
        return value.ToString("G7", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Formats an RGB color as "R G B 1" string.
    /// </summary>
    private static string FormatColor(Vector3 color)
    {
        return $"{F(color.X)} {F(color.Y)} {F(color.Z)} 1";
    }

    // =========================================================================
    // Assimp-based export (simple flat mesh list — used by RoadNetworkDaeExporter)
    // =========================================================================

    /// <summary>
    ///     Creates an Assimp scene from the mesh collection.
    /// </summary>
    private Scene CreateScene(List<Mesh> meshes)
    {
        var scene = new Scene();
        scene.RootNode = new Node(_options.RootNodeName);

        var materialIndexMap = BuildMaterialIndexMap(scene, meshes);

        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            if (!mesh.HasGeometry) continue;

            var materialIndex = GetMaterialIndex(materialIndexMap, mesh.MaterialName);
            var assimpMesh = ConvertToAssimpMesh(mesh, materialIndex);

            var meshIndex = scene.MeshCount;
            scene.Meshes.Add(assimpMesh);

            var meshNode = new Node(mesh.Name, scene.RootNode);
            meshNode.MeshIndices.Add(meshIndex);
            scene.RootNode.Children.Add(meshNode);
        }

        return scene;
    }

    private Dictionary<string, int> BuildMaterialIndexMap(Scene scene, List<Mesh> meshes)
    {
        var materialIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var materialNames = meshes
            .Where(m => !string.IsNullOrEmpty(m.MaterialName))
            .Select(m => m.MaterialName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var defaultMaterial = CreateAssimpMaterial(Material.CreateDefault());
        scene.Materials.Add(defaultMaterial);
        materialIndexMap[""] = 0;

        foreach (var materialName in materialNames)
            if (_materials.TryGetValue(materialName, out var registeredMaterial))
            {
                var assimpMaterial = CreateAssimpMaterial(registeredMaterial);
                materialIndexMap[materialName] = scene.MaterialCount;
                scene.Materials.Add(assimpMaterial);
            }
            else
            {
                var simpleMaterial = Material.CreateDefault(materialName);
                var assimpMaterial = CreateAssimpMaterial(simpleMaterial);
                materialIndexMap[materialName] = scene.MaterialCount;
                scene.Materials.Add(assimpMaterial);
            }

        return materialIndexMap;
    }

    private static int GetMaterialIndex(Dictionary<string, int> materialIndexMap, string? materialName)
    {
        if (string.IsNullOrEmpty(materialName)) return 0;
        return materialIndexMap.TryGetValue(materialName, out var index) ? index : 0;
    }

    private static Assimp.Material CreateAssimpMaterial(Material material)
    {
        var assimpMaterial = new Assimp.Material
        {
            Name = material.Name
        };

        assimpMaterial.ColorDiffuse = new Color4D(
            material.DiffuseColor.X, material.DiffuseColor.Y, material.DiffuseColor.Z, material.Opacity);
        assimpMaterial.ColorSpecular = new Color4D(
            material.SpecularColor.X, material.SpecularColor.Y, material.SpecularColor.Z, 1f);
        assimpMaterial.ColorAmbient = new Color4D(
            material.AmbientColor.X, material.AmbientColor.Y, material.AmbientColor.Z, 1f);
        assimpMaterial.Shininess = material.Shininess;
        assimpMaterial.Opacity = material.Opacity;

        if (!string.IsNullOrEmpty(material.DiffuseTexturePath))
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

        if (!string.IsNullOrEmpty(material.NormalTexturePath))
            assimpMaterial.TextureNormal = new TextureSlot
            {
                FilePath = material.NormalTexturePath,
                TextureType = TextureType.Normals,
                TextureIndex = 0,
                Mapping = TextureMapping.FromUV,
                UVIndex = 0
            };

        if (!string.IsNullOrEmpty(material.SpecularTexturePath))
            assimpMaterial.TextureSpecular = new TextureSlot
            {
                FilePath = material.SpecularTexturePath,
                TextureType = TextureType.Specular,
                TextureIndex = 0,
                Mapping = TextureMapping.FromUV,
                UVIndex = 0
            };

        return assimpMaterial;
    }

    private Assimp.Mesh ConvertToAssimpMesh(Mesh mesh, int materialIndex)
    {
        var assimpMesh = new Assimp.Mesh(mesh.Name, PrimitiveType.Triangle)
        {
            MaterialIndex = materialIndex
        };

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

        if (_options.IncludeUVs && assimpMesh.TextureCoordinateChannels[0].Count > 0)
            assimpMesh.UVComponentCount[0] = 2;

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

    private static void ExportScene(Scene scene, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        using var context = new AssimpContext();

        var exportFormats = context.GetSupportedExportFormats();
        var colladaFormat = exportFormats.FirstOrDefault(f =>
            f.FileExtension.Equals("dae", StringComparison.OrdinalIgnoreCase));

        if (colladaFormat == null)
            throw new InvalidOperationException(
                "Collada export format is not supported by the installed Assimp library.");

        var success = context.ExportFile(scene, filePath, colladaFormat.FormatId);
        if (!success) throw new InvalidOperationException($"Failed to export scene to '{filePath}'.");
    }

    // =========================================================================
    // Utilities
    // =========================================================================

    /// <summary>
    ///     Replaces every ASCII digit with a letter (0→a, 1→b, … 9→j) so the
    ///     resulting string contains no numeric characters. The mapping is
    ///     bijective, so uniqueness of the input is preserved.
    /// </summary>
    private static string DigitsToLetters(string input)
    {
        return string.Create(input.Length, input, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = c is >= '0' and <= '9' ? (char)('a' + (c - '0')) : c;
            }
        });
    }
}
