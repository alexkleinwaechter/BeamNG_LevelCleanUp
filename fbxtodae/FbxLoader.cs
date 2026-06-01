using System.Numerics;
using Assimp;
using BeamNG.Procedural3D.Core;
using Mesh = BeamNG.Procedural3D.Core.Mesh;

namespace FbxToDae;

/// <summary>
/// Loads an FBX file and converts all sub-meshes into a flat list of
/// BeamNG.Procedural3D Mesh instances. All meshes are assigned the same
/// material name (one material per FBX). Coordinate system is converted
/// from FBX (Y-up right-handed) to BeamNG (Z-up right-handed) here, so the
/// caller can hand the meshes to ColladaExporter.ExportZUp directly.
///
/// Conversion: (x, y, z)_fbx -> (x, -z, y)_zup
/// Normals:    (x, y, z)_fbx -> (x, -z, y)_zup
/// UVs:        unchanged (V flip handled by ColladaExporter if needed)
/// </summary>
public static class FbxLoader
{
    public static List<Mesh> Load(string fbxPath, string materialName)
    {
        if (!File.Exists(fbxPath))
            throw new FileNotFoundException("FBX file not found.", fbxPath);

        // PostProcessSteps:
        //   Triangulate              -> ensure every face is a triangle
        //   GenerateSmoothNormals    -> fill Normals if the file lacks them
        //   JoinIdenticalVertices    -> indexed mesh with 1:1 vertex/UV/normal
        //   FlipWindingOrder is NOT set - FBX is right-handed same as Collada
        //   GlobalScale              -> applies GlobalSettings.UnitScaleFactor
        //   PreTransformVertices     -> bake node transforms into vertex data
        //                               so we get world-space meshes (FBX node
        //                               hierarchies often rotate/scale parts)
        var steps = PostProcessSteps.Triangulate
                  | PostProcessSteps.GenerateSmoothNormals
                  | PostProcessSteps.JoinIdenticalVertices
                  | PostProcessSteps.GlobalScale
                  | PostProcessSteps.PreTransformVertices;

        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(fbxPath, steps)
            ?? throw new InvalidOperationException($"Assimp failed to load: {fbxPath}");

        var result = new List<Mesh>();
        for (int i = 0; i < scene.MeshCount; i++)
        {
            var a = scene.Meshes[i];
            if ((a.PrimitiveType & PrimitiveType.Triangle) == 0 || a.FaceCount == 0)
                continue;

            var mesh = new Mesh
            {
                Name = string.IsNullOrWhiteSpace(a.Name) ? $"part_{i}" : a.Name,
                MaterialName = materialName,
            };

            bool hasUv = a.HasTextureCoords(0);
            bool hasNormals = a.HasNormals;

            for (int v = 0; v < a.VertexCount; v++)
            {
                var p = a.Vertices[v];
                var n = hasNormals ? a.Normals[v] : new Vector3D(0, 1, 0);
                var uv = hasUv ? a.TextureCoordinateChannels[0][v] : new Vector3D(0, 0, 0);

                var position = YupToZup(new Vector3(p.X, p.Y, p.Z));
                var normal   = YupToZup(new Vector3(n.X, n.Y, n.Z));

                mesh.Vertices.Add(new Vertex(position, normal, new Vector2(uv.X, uv.Y)));
            }

            foreach (var face in a.Faces)
            {
                if (face.IndexCount != 3) continue; // should never happen after Triangulate
                mesh.Triangles.Add(new Triangle(
                    face.Indices[0],
                    face.Indices[1],
                    face.Indices[2]));
            }

            if (mesh.HasGeometry)
                result.Add(mesh);
        }

        if (result.Count == 0)
            throw new InvalidOperationException($"No triangulated meshes found in {fbxPath}");

        return result;
    }

    /// <summary>
    /// Converts a right-handed Y-up vector (FBX) to a right-handed Z-up vector
    /// (BeamNG). The rotation is +90° around the X axis:
    ///     new_x = x
    ///     new_y = -z
    ///     new_z =  y
    /// </summary>
    private static Vector3 YupToZup(Vector3 v) => new(v.X, -v.Z, v.Y);
}
