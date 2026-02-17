namespace BeamNG.Procedural3D.Building;

using System.Numerics;

/// <summary>
/// Defines a building material with texture paths and UV scaling.
/// Maps an internal material key (e.g., "BRICK") to actual texture file names
/// and BeamNG material properties.
/// </summary>
public class BuildingMaterialDefinition
{
    /// <summary>
    /// Internal material key (e.g., "BUILDING_DEFAULT", "BRICK", "ROOF_DEFAULT").
    /// Used by BuildingData.WallMaterial / RoofMaterial to reference this definition.
    /// </summary>
    public required string MaterialKey { get; init; }

    /// <summary>
    /// Display name used in the BeamNG material system (e.g., "building_brick", "building_plaster").
    /// This becomes the material name in materials.json and DAE files.
    /// </summary>
    public required string MaterialName { get; init; }

    /// <summary>
    /// Filename of the color/albedo texture (e.g., "Bricks029_Color.jpg").
    /// Relative to the texture source/deployment directory.
    /// </summary>
    public required string ColorMapFile { get; init; }

    /// <summary>
    /// Filename of the normal map texture (e.g., "Bricks029_Normal.jpg").
    /// </summary>
    public string? NormalMapFile { get; init; }

    /// <summary>
    /// Filename of the ORM (Occlusion/Roughness/Metallic) packed texture (e.g., "Bricks029_ORM.jpg").
    /// Maps to compositeMap in BeamNG's material system.
    /// </summary>
    public string? OrmMapFile { get; init; }

    /// <summary>
    /// Texture scale in U direction (meters per texture repeat on walls).
    /// For walls: U = distance along wall. For roofs: U = world X.
    /// </summary>
    public float TextureScaleU { get; init; } = 3.0f;

    /// <summary>
    /// Texture scale in V direction (meters per texture repeat).
    /// For walls: V = height. For roofs: V = world Y.
    /// </summary>
    public float TextureScaleV { get; init; } = 3.0f;

    /// <summary>
    /// Whether this is a roof material (affects UV mapping approach).
    /// </summary>
    public bool IsRoofMaterial { get; init; }

    /// <summary>
    /// Default RGB color for this material (0-255 per channel).
    /// Used for placeholder texture generation and as diffuse color fallback.
    /// </summary>
    public Vector3 DefaultColor { get; init; } = new(180, 180, 180);

    /// <summary>
    /// Creates a Procedural3D Material for use with ColladaExporter.
    /// Texture paths are set relative to the DAE file location.
    /// </summary>
    /// <param name="textureRelativePath">Relative path from DAE to textures folder (e.g., "textures/").</param>
    public Core.Material ToExportMaterial(string textureRelativePath = "textures/")
    {
        var path = textureRelativePath.TrimEnd('/') + "/";
        return new Core.Material
        {
            Name = MaterialName,
            DiffuseColor = new Vector3(DefaultColor.X / 255f, DefaultColor.Y / 255f, DefaultColor.Z / 255f),
            DiffuseTexturePath = path + ColorMapFile,
            NormalTexturePath = NormalMapFile != null ? path + NormalMapFile : null,
            SpecularTexturePath = OrmMapFile != null ? path + OrmMapFile : null
        };
    }

    /// <summary>
    /// Gets all texture filenames referenced by this material definition.
    /// </summary>
    public IEnumerable<string> GetTextureFiles()
    {
        yield return ColorMapFile;
        if (NormalMapFile != null) yield return NormalMapFile;
        if (OrmMapFile != null) yield return OrmMapFile;
    }
}
