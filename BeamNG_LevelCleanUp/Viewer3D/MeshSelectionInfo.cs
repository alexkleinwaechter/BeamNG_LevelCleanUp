using BeamNG_LevelCleanUp.Objects;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
///     Information about a selected mesh in the 3D viewer.
/// </summary>
public class MeshSelectionInfo
{
    /// <summary>The name of the mesh geometry.</summary>
    public string MeshName { get; set; } = string.Empty;

    /// <summary>The material name associated with this mesh.</summary>
    public string MaterialName { get; set; } = string.Empty;

    /// <summary>The MaterialJson definition if found.</summary>
    public MaterialJson? Material { get; set; }

    /// <summary>List of texture files associated with this mesh's material.</summary>
    public List<MaterialFile> TextureFiles { get; set; } = new();

    /// <summary>Indicates whether this mesh has any textures associated with it.</summary>
    public bool HasTextures => TextureFiles.Count > 0;
}
