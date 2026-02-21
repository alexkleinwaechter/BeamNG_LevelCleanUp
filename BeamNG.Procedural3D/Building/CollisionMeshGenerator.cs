namespace BeamNG.Procedural3D.Building;

using BeamNG.Procedural3D.Core;

/// <summary>
/// Generates collision meshes for BeamNG buildings (Colmesh-1 node in the DAE).
///
/// The collision mesh is built from LOD0 geometry (walls + roof) with materials stripped,
/// giving accurate collision for buildings with passages, courtyards, and complex shapes.
///
/// For clustered DAEs with multiple buildings, all LOD0 meshes are merged together.
/// </summary>
public static class CollisionMeshGenerator
{
    /// <summary>
    /// Creates a collision mesh from LOD0 meshes by merging all material groups
    /// into a single mesh without material assignment.
    /// This gives accurate collision that matches the visible geometry,
    /// including passages and complex building shapes.
    /// </summary>
    public static Mesh GenerateFromLod0(Dictionary<string, Mesh> lod0Meshes)
    {
        var result = new Mesh { Name = "Colmesh-1" };

        if (lod0Meshes.Count == 0)
            return result;

        foreach (var mesh in lod0Meshes.Values)
        {
            if (!mesh.HasGeometry) continue;

            int baseIndex = result.Vertices.Count;
            result.Vertices.AddRange(mesh.Vertices);

            foreach (var tri in mesh.Triangles)
            {
                result.Triangles.Add(new Triangle(
                    tri.V0 + baseIndex,
                    tri.V1 + baseIndex,
                    tri.V2 + baseIndex));
            }
        }

        // No material assignment — collision meshes don't need materials
        result.MaterialName = null;
        return result;
    }
}
