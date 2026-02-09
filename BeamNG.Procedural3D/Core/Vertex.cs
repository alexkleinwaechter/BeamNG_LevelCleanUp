namespace BeamNG.Procedural3D.Core;

using System.Numerics;

/// <summary>
/// Represents a vertex with position, normal, and UV coordinates.
/// </summary>
public readonly struct Vertex
{
    public Vector3 Position { get; init; }
    public Vector3 Normal { get; init; }
    public Vector2 UV { get; init; }

    public Vertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        Position = position;
        Normal = normal;
        UV = uv;
    }

    public Vertex(Vector3 position) : this(position, Vector3.UnitZ, Vector2.Zero)
    {
    }

    public Vertex(Vector3 position, Vector3 normal) : this(position, normal, Vector2.Zero)
    {
    }

    /// <summary>
    /// Creates a new vertex with the specified normal.
    /// </summary>
    public Vertex WithNormal(Vector3 normal) => new(Position, normal, UV);

    /// <summary>
    /// Creates a new vertex with the specified UV coordinates.
    /// </summary>
    public Vertex WithUV(Vector2 uv) => new(Position, Normal, uv);
}
