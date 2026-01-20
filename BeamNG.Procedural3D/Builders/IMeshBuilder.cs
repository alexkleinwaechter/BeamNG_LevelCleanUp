namespace BeamNG.Procedural3D.Builders;

using BeamNG.Procedural3D.Core;

/// <summary>
/// Interface for procedural mesh builders.
/// </summary>
public interface IMeshBuilder
{
    /// <summary>
    /// Builds the mesh from the configured parameters.
    /// </summary>
    /// <returns>The constructed mesh.</returns>
    Mesh Build();

    /// <summary>
    /// Resets the builder for reuse.
    /// </summary>
    void Clear();
}
