using BeamNG.Procedural3D.Building;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Generates placeholder building textures for development and testing.
/// Produces solid-color 256×256 PNG files with BeamNG Texture Cooker naming conventions
/// that can be replaced with real CC0 textures (from ambientCG, Poly Haven, etc.) later.
/// </summary>
public static class BuildingTexturePlaceholderGenerator
{
    /// <summary>
    /// Generates a single placeholder PNG texture file based on the filename suffix and material definition.
    /// Called by BuildingMaterialLibrary.DeployTextures when no source textures are found.
    /// </summary>
    /// <param name="textureFileName">The texture filename (e.g., "Bricks029_Color.color.png").</param>
    /// <param name="material">The material definition (provides DefaultColor).</param>
    /// <param name="targetPath">Absolute path where the PNG file should be written.</param>
    public static void GeneratePlaceholder(string textureFileName, BuildingMaterialDefinition material, string targetPath)
    {
        BuildingTextureConverter.GeneratePlaceholderPng(textureFileName, material, targetPath);
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
