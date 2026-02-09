namespace BeamNG.Procedural3D.Core;

/// <summary>
/// Represents a 3D mesh with vertices, triangles, and optional material.
/// </summary>
public class Mesh
{
    /// <summary>
    /// Name of the mesh.
    /// </summary>
    public string Name { get; set; } = "Mesh";

    /// <summary>
    /// List of vertices in the mesh.
    /// </summary>
    public List<Vertex> Vertices { get; } = [];

    /// <summary>
    /// List of triangles (faces) in the mesh.
    /// </summary>
    public List<Triangle> Triangles { get; } = [];

    /// <summary>
    /// Optional material name for this mesh.
    /// </summary>
    public string? MaterialName { get; set; }

    /// <summary>
    /// Gets the number of vertices in the mesh.
    /// </summary>
    public int VertexCount => Vertices.Count;

    /// <summary>
    /// Gets the number of triangles in the mesh.
    /// </summary>
    public int TriangleCount => Triangles.Count;

    /// <summary>
    /// Returns true if the mesh has at least one triangle.
    /// </summary>
    public bool HasGeometry => Triangles.Count > 0 && Vertices.Count >= 3;

    /// <summary>
    /// Clears all vertices and triangles from the mesh.
    /// </summary>
    public void Clear()
    {
        Vertices.Clear();
        Triangles.Clear();
    }
}
