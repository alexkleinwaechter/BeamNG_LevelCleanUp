using System.Numerics;
using BeamNG.Procedural3D.Building;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Generates placeholder building textures for development and testing.
/// Produces solid-color JPG files at 256x256 px that can be replaced
/// with real CC0 textures (from ambientCG, Poly Haven, etc.) later.
/// </summary>
public static class BuildingTexturePlaceholderGenerator
{
    private const int TextureSize = 256;

    /// <summary>
    /// Generates a single placeholder texture file based on the filename suffix and material definition.
    /// Called by BuildingMaterialLibrary.DeployTextures when bundled textures are not found.
    /// </summary>
    /// <param name="textureFileName">The texture filename (e.g., "Bricks029_Color.jpg").</param>
    /// <param name="material">The material definition (provides DefaultColor).</param>
    /// <param name="targetPath">Absolute path where the texture file should be written.</param>
    public static void GeneratePlaceholder(string textureFileName, BuildingMaterialDefinition material, string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        Rgba32 color;
        if (textureFileName.Contains("_Normal", StringComparison.OrdinalIgnoreCase))
        {
            // Flat normal map: R=128, G=128, B=255 (pointing straight up)
            color = new Rgba32(128, 128, 255);
        }
        else if (textureFileName.Contains("_ORM", StringComparison.OrdinalIgnoreCase))
        {
            // ORM: AO=white (255), Roughness=medium (128), Metallic=none (0)
            color = new Rgba32(255, 128, 0);
        }
        else
        {
            // Color map: use material's default color
            var c = material.DefaultColor;
            color = new Rgba32(
                (byte)Math.Clamp(c.X, 0, 255),
                (byte)Math.Clamp(c.Y, 0, 255),
                (byte)Math.Clamp(c.Z, 0, 255));
        }

        using var image = new Image<Rgba32>(TextureSize, TextureSize, color);
        image.SaveAsJpeg(targetPath);
    }

    /// <summary>
    /// Ensures all expected placeholder textures exist in the given directory.
    /// Only creates missing files — existing files (real textures) are not overwritten.
    /// </summary>
    /// <param name="textureDirectory">The BuildingTextures resource directory.</param>
    /// <param name="materialLibrary">Material library to get all material definitions.</param>
    /// <returns>Number of placeholder files created.</returns>
    public static int EnsurePlaceholdersExist(string textureDirectory, BuildingMaterialLibrary materialLibrary)
    {
        Directory.CreateDirectory(textureDirectory);
        int created = 0;

        foreach (var material in materialLibrary.AllMaterials)
        {
            foreach (var textureFile in material.GetTextureFiles())
            {
                var path = Path.Combine(textureDirectory, textureFile);
                if (!File.Exists(path))
                {
                    GeneratePlaceholder(textureFile, material, path);
                    created++;
                }
            }
        }

        return created;
    }
}
