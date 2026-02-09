namespace BeamNG.Procedural3D.Exporters;

using BeamNG.Procedural3D.Core;

/// <summary>
/// Interface for exporting meshes to various file formats.
/// </summary>
public interface IMeshExporter
{
    /// <summary>
    /// Exports a single mesh to a file.
    /// </summary>
    /// <param name="mesh">The mesh to export.</param>
    /// <param name="filePath">The output file path.</param>
    void Export(Mesh mesh, string filePath);

    /// <summary>
    /// Exports multiple meshes to a single file (scene).
    /// </summary>
    /// <param name="meshes">The meshes to export.</param>
    /// <param name="filePath">The output file path.</param>
    void Export(IEnumerable<Mesh> meshes, string filePath);

    /// <summary>
    /// Gets the file extension for this exporter (e.g., ".dae").
    /// </summary>
    string FileExtension { get; }
}
