namespace BeamNG.Procedural3D.Core;

using System.Numerics;

/// <summary>
/// Represents a material definition for 3D meshes.
/// </summary>
public class Material
{
    /// <summary>
    /// Name of the material.
    /// </summary>
    public string Name { get; set; } = "Material";

    /// <summary>
    /// Diffuse color (RGB, 0-1 range).
    /// </summary>
    public Vector3 DiffuseColor { get; set; } = new(0.8f, 0.8f, 0.8f);

    /// <summary>
    /// Specular color (RGB, 0-1 range).
    /// </summary>
    public Vector3 SpecularColor { get; set; } = new(0.2f, 0.2f, 0.2f);

    /// <summary>
    /// Ambient color (RGB, 0-1 range).
    /// </summary>
    public Vector3 AmbientColor { get; set; } = new(0.1f, 0.1f, 0.1f);

    /// <summary>
    /// Shininess/specular power (typically 1-128).
    /// </summary>
    public float Shininess { get; set; } = 32f;

    /// <summary>
    /// Opacity (0 = transparent, 1 = opaque).
    /// </summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>
    /// Optional path to diffuse/albedo texture.
    /// </summary>
    public string? DiffuseTexturePath { get; set; }

    /// <summary>
    /// Optional path to normal map texture.
    /// </summary>
    public string? NormalTexturePath { get; set; }

    /// <summary>
    /// Optional path to specular map texture.
    /// </summary>
    public string? SpecularTexturePath { get; set; }

    /// <summary>
    /// Creates a default material with the specified name.
    /// </summary>
    public static Material CreateDefault(string name = "DefaultMaterial")
    {
        return new Material { Name = name };
    }

    /// <summary>
    /// Creates a material with a diffuse texture.
    /// </summary>
    public static Material CreateWithTexture(string name, string diffuseTexturePath)
    {
        return new Material
        {
            Name = name,
            DiffuseTexturePath = diffuseTexturePath
        };
    }
}
