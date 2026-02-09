namespace BeamNG.Procedural3D.Builders;

using System.Numerics;
using BeamNG.Procedural3D.Core;

/// <summary>
/// General-purpose mesh builder with common operations for procedural mesh construction.
/// </summary>
public class MeshBuilder : IMeshBuilder
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<Triangle> _triangles = [];
    private string _name = "Mesh";
    private string? _materialName;

    /// <summary>
    /// Gets the current vertex count.
    /// </summary>
    public int VertexCount => _vertices.Count;

    /// <summary>
    /// Gets the current triangle count.
    /// </summary>
    public int TriangleCount => _triangles.Count;

    /// <summary>
    /// Sets the name for the mesh being built.
    /// </summary>
    public MeshBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>
    /// Sets the material name for the mesh being built.
    /// </summary>
    public MeshBuilder WithMaterial(string? materialName)
    {
        _materialName = materialName;
        return this;
    }

    /// <summary>
    /// Adds a vertex and returns its index.
    /// </summary>
    public int AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        _vertices.Add(new Vertex(position, normal, uv));
        return _vertices.Count - 1;
    }

    /// <summary>
    /// Adds a vertex with default normal and UV, returns its index.
    /// </summary>
    public int AddVertex(Vector3 position)
    {
        _vertices.Add(new Vertex(position));
        return _vertices.Count - 1;
    }

    /// <summary>
    /// Adds a vertex and returns its index.
    /// </summary>
    public int AddVertex(Vertex vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    /// <summary>
    /// Adds a triangle by vertex indices (counter-clockwise winding).
    /// </summary>
    public void AddTriangle(int v0, int v1, int v2)
    {
        _triangles.Add(new Triangle(v0, v1, v2));
    }

    /// <summary>
    /// Adds a quad as two triangles (counter-clockwise winding).
    /// Vertices should be in order: bottom-left, bottom-right, top-right, top-left.
    /// </summary>
    public void AddQuad(int v0, int v1, int v2, int v3)
    {
        // First triangle: v0, v1, v2
        _triangles.Add(new Triangle(v0, v1, v2));
        // Second triangle: v0, v2, v3
        _triangles.Add(new Triangle(v0, v2, v3));
    }

    /// <summary>
    /// Extrudes a 2D profile along a 3D path with specified up vectors.
    /// </summary>
    /// <param name="profile">2D cross-section profile (X = across, Y = up from path).</param>
    /// <param name="path">3D path points to extrude along.</param>
    /// <param name="upVectors">Up vector at each path point for orientation.</param>
    /// <param name="uvScaleU">UV scale factor along the path (meters per texture repeat).</param>
    /// <param name="uvScaleV">UV scale factor across the profile (meters per texture repeat).</param>
    public void AddExtrusion(
        IReadOnlyList<Vector2> profile,
        IReadOnlyList<Vector3> path,
        IReadOnlyList<Vector3> upVectors,
        float uvScaleU = 1f,
        float uvScaleV = 1f)
    {
        if (profile.Count < 2 || path.Count < 2)
            return;

        if (upVectors.Count != path.Count)
            throw new ArgumentException("upVectors must have same count as path");

        int profileCount = profile.Count;
        int pathCount = path.Count;
        int baseIndex = _vertices.Count;

        // Calculate cumulative distance along path for UV mapping
        var pathDistances = new float[pathCount];
        pathDistances[0] = 0f;
        for (int i = 1; i < pathCount; i++)
        {
            pathDistances[i] = pathDistances[i - 1] + Vector3.Distance(path[i - 1], path[i]);
        }

        // Calculate cumulative distance along profile for UV mapping
        var profileDistances = new float[profileCount];
        profileDistances[0] = 0f;
        for (int i = 1; i < profileCount; i++)
        {
            profileDistances[i] = profileDistances[i - 1] + Vector2.Distance(profile[i - 1], profile[i]);
        }

        // Generate vertices at each path point
        for (int pathIdx = 0; pathIdx < pathCount; pathIdx++)
        {
            Vector3 pathPoint = path[pathIdx];
            Vector3 up = Vector3.Normalize(upVectors[pathIdx]);

            // Calculate forward direction
            Vector3 forward;
            if (pathIdx == 0)
            {
                forward = Vector3.Normalize(path[1] - path[0]);
            }
            else if (pathIdx == pathCount - 1)
            {
                forward = Vector3.Normalize(path[pathCount - 1] - path[pathCount - 2]);
            }
            else
            {
                forward = Vector3.Normalize(path[pathIdx + 1] - path[pathIdx - 1]);
            }

            // Calculate right vector (perpendicular to forward and up)
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
            // Recalculate up to ensure orthogonality
            up = Vector3.Normalize(Vector3.Cross(right, forward));

            float u = pathDistances[pathIdx] / uvScaleU;

            // Place profile vertices at this path point
            for (int profIdx = 0; profIdx < profileCount; profIdx++)
            {
                Vector2 profPoint = profile[profIdx];
                Vector3 worldPos = pathPoint + right * profPoint.X + up * profPoint.Y;

                // Calculate normal (pointing outward from profile)
                Vector3 normal;
                if (profIdx == 0)
                {
                    Vector2 tangent = profile[1] - profile[0];
                    normal = Vector3.Normalize(right * tangent.Y - up * tangent.X);
                }
                else if (profIdx == profileCount - 1)
                {
                    Vector2 tangent = profile[profileCount - 1] - profile[profileCount - 2];
                    normal = Vector3.Normalize(right * tangent.Y - up * tangent.X);
                }
                else
                {
                    Vector2 tangent = profile[profIdx + 1] - profile[profIdx - 1];
                    normal = Vector3.Normalize(right * tangent.Y - up * tangent.X);
                }

                float v = profileDistances[profIdx] / uvScaleV;
                AddVertex(worldPos, normal, new Vector2(u, v));
            }
        }

        // Generate triangles connecting adjacent path segments
        for (int pathIdx = 0; pathIdx < pathCount - 1; pathIdx++)
        {
            int rowStart = baseIndex + pathIdx * profileCount;
            int nextRowStart = baseIndex + (pathIdx + 1) * profileCount;

            for (int profIdx = 0; profIdx < profileCount - 1; profIdx++)
            {
                int v0 = rowStart + profIdx;
                int v1 = rowStart + profIdx + 1;
                int v2 = nextRowStart + profIdx + 1;
                int v3 = nextRowStart + profIdx;

                AddQuad(v0, v3, v2, v1);
            }
        }
    }

    /// <summary>
    /// Creates a lofted surface between two profiles (must have same vertex count).
    /// </summary>
    public void AddLoft(IReadOnlyList<Vector3> profile1, IReadOnlyList<Vector3> profile2)
    {
        if (profile1.Count != profile2.Count || profile1.Count < 2)
            throw new ArgumentException("Profiles must have same count and at least 2 vertices");

        int count = profile1.Count;
        int baseIndex = _vertices.Count;

        // Add vertices for both profiles
        for (int i = 0; i < count; i++)
        {
            float v = (float)i / (count - 1);
            AddVertex(profile1[i], Vector3.UnitZ, new Vector2(0f, v));
        }
        for (int i = 0; i < count; i++)
        {
            float v = (float)i / (count - 1);
            AddVertex(profile2[i], Vector3.UnitZ, new Vector2(1f, v));
        }

        // Generate quads between profiles
        for (int i = 0; i < count - 1; i++)
        {
            int v0 = baseIndex + i;
            int v1 = baseIndex + i + 1;
            int v2 = baseIndex + count + i + 1;
            int v3 = baseIndex + count + i;

            AddQuad(v0, v1, v2, v3);
        }

        // Recalculate normals for the lofted section
        CalculateSmoothNormalsForRange(baseIndex, _vertices.Count - baseIndex);
    }

    /// <summary>
    /// Calculates flat (per-face) normals for all triangles.
    /// </summary>
    public void CalculateFlatNormals()
    {
        // For flat shading, each triangle needs its own vertices
        var newVertices = new List<Vertex>();
        var newTriangles = new List<Triangle>();

        foreach (var tri in _triangles)
        {
            Vector3 p0 = _vertices[tri.V0].Position;
            Vector3 p1 = _vertices[tri.V1].Position;
            Vector3 p2 = _vertices[tri.V2].Position;

            Vector3 normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));

            int baseIdx = newVertices.Count;
            newVertices.Add(_vertices[tri.V0].WithNormal(normal));
            newVertices.Add(_vertices[tri.V1].WithNormal(normal));
            newVertices.Add(_vertices[tri.V2].WithNormal(normal));

            newTriangles.Add(new Triangle(baseIdx, baseIdx + 1, baseIdx + 2));
        }

        _vertices.Clear();
        _vertices.AddRange(newVertices);
        _triangles.Clear();
        _triangles.AddRange(newTriangles);
    }

    /// <summary>
    /// Calculates smooth (averaged per-vertex) normals for all vertices.
    /// </summary>
    public void CalculateSmoothNormals()
    {
        CalculateSmoothNormalsForRange(0, _vertices.Count);
    }

    /// <summary>
    /// Calculates smooth normals for a range of vertices.
    /// </summary>
    private void CalculateSmoothNormalsForRange(int startIndex, int count)
    {
        var normalAccumulator = new Vector3[count];

        // Accumulate face normals for each vertex
        foreach (var tri in _triangles)
        {
            // Check if triangle uses vertices in our range
            bool v0InRange = tri.V0 >= startIndex && tri.V0 < startIndex + count;
            bool v1InRange = tri.V1 >= startIndex && tri.V1 < startIndex + count;
            bool v2InRange = tri.V2 >= startIndex && tri.V2 < startIndex + count;

            if (!v0InRange && !v1InRange && !v2InRange)
                continue;

            Vector3 p0 = _vertices[tri.V0].Position;
            Vector3 p1 = _vertices[tri.V1].Position;
            Vector3 p2 = _vertices[tri.V2].Position;

            Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0);

            if (v0InRange) normalAccumulator[tri.V0 - startIndex] += faceNormal;
            if (v1InRange) normalAccumulator[tri.V1 - startIndex] += faceNormal;
            if (v2InRange) normalAccumulator[tri.V2 - startIndex] += faceNormal;
        }

        // Normalize and apply
        for (int i = 0; i < count; i++)
        {
            Vector3 normal = normalAccumulator[i];
            if (normal.LengthSquared() > 0.0001f)
            {
                normal = Vector3.Normalize(normal);
                _vertices[startIndex + i] = _vertices[startIndex + i].WithNormal(normal);
            }
        }
    }

    /// <summary>
    /// Applies a transformation matrix to all vertices.
    /// </summary>
    public void Transform(Matrix4x4 matrix)
    {
        TransformRange(matrix, 0, _vertices.Count);
    }

    /// <summary>
    /// Applies a transformation matrix to a range of vertices.
    /// </summary>
    public void TransformRange(Matrix4x4 matrix, int startIndex, int count)
    {
        // Extract the rotation/scale part for normals (inverse transpose)
        Matrix4x4.Invert(matrix, out var invMatrix);
        var normalMatrix = Matrix4x4.Transpose(invMatrix);

        for (int i = startIndex; i < startIndex + count && i < _vertices.Count; i++)
        {
            var vertex = _vertices[i];
            Vector3 newPosition = Vector3.Transform(vertex.Position, matrix);
            Vector3 newNormal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normalMatrix));
            _vertices[i] = new Vertex(newPosition, newNormal, vertex.UV);
        }
    }

    /// <summary>
    /// Merges another mesh into this builder.
    /// </summary>
    public void Merge(Mesh other)
    {
        int baseIndex = _vertices.Count;

        foreach (var vertex in other.Vertices)
        {
            _vertices.Add(vertex);
        }

        foreach (var triangle in other.Triangles)
        {
            _triangles.Add(new Triangle(
                triangle.V0 + baseIndex,
                triangle.V1 + baseIndex,
                triangle.V2 + baseIndex));
        }
    }

    /// <summary>
    /// Merges vertices from another builder into this one.
    /// </summary>
    public void Merge(MeshBuilder other)
    {
        int baseIndex = _vertices.Count;

        _vertices.AddRange(other._vertices);

        foreach (var triangle in other._triangles)
        {
            _triangles.Add(new Triangle(
                triangle.V0 + baseIndex,
                triangle.V1 + baseIndex,
                triangle.V2 + baseIndex));
        }
    }

    /// <summary>
    /// Builds the final mesh from current state.
    /// </summary>
    public Mesh Build()
    {
        var mesh = new Mesh
        {
            Name = _name,
            MaterialName = _materialName
        };

        mesh.Vertices.AddRange(_vertices);
        mesh.Triangles.AddRange(_triangles);

        return mesh;
    }

    /// <summary>
    /// Clears all vertices and triangles for reuse.
    /// </summary>
    public void Clear()
    {
        _vertices.Clear();
        _triangles.Clear();
        _name = "Mesh";
        _materialName = null;
    }
}
