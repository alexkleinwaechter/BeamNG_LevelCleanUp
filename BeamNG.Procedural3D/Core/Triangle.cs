namespace BeamNG.Procedural3D.Core;

/// <summary>
/// Represents a triangle face as three vertex indices.
/// </summary>
public readonly struct Triangle
{
    public int V0 { get; init; }
    public int V1 { get; init; }
    public int V2 { get; init; }

    public Triangle(int v0, int v1, int v2)
    {
        V0 = v0;
        V1 = v1;
        V2 = v2;
    }

    /// <summary>
    /// Returns a triangle with reversed winding order.
    /// </summary>
    public Triangle Reversed() => new(V0, V2, V1);

    /// <summary>
    /// Gets the vertex indices as an array.
    /// </summary>
    public int[] ToArray() => [V0, V1, V2];
}
