using System.Numerics;
using BeamNG.Procedural3D.Building;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BeamNgTerrainPoc.Terrain.Building;

/// <summary>
/// Converts source textures (JPG/PNG) to PNGs with BeamNG Texture Cooker naming conventions.
///
/// BeamNG's Texture Cooker (v0.20+) automatically converts properly-named 8-bit PNGs to DDS:
///   *.color.png  → BC7 sRGB   (diffuse/color maps)
///   *.normal.png → BC5         (normal maps)
///   *.data.png   → BC4 or BC7  (ORM, AO, roughness — linear space)
///
/// Naming convention preserves the original type identifier:
///   Bricks029_Color.color.png, Bricks029_Normal.normal.png, Bricks029_ORM.data.png
/// </summary>
public static class BuildingTextureConverter
{
    /// <summary>
    /// Checks whether a value is a power of two (e.g., 256, 512, 1024).
    /// </summary>
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    /// <summary>
    /// Checks whether an image file has power-of-2 dimensions (both width and height).
    /// Uses ImageSharp's Identify to read only the header (no full decode).
    /// Returns true if both dimensions are PoT, false otherwise (including on read failure).
    /// </summary>
    public static bool HasPowerOfTwoDimensions(string imagePath)
    {
        try
        {
            var info = Image.Identify(imagePath);
            return info != null && IsPowerOfTwo(info.Width) && IsPowerOfTwo(info.Height);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Strips the BeamNG Texture Cooker suffix from a filename, leaving a plain .png extension.
    /// E.g., "Bricks029_Color.color.png" -> "Bricks029_Color.png"
    ///        "Bricks029_Normal.normal.png" -> "Bricks029_Normal.png"
    ///        "Bricks029_ORM.data.png" -> "Bricks029_ORM.png"
    ///        "Foo.png" -> "Foo.png" (unchanged)
    /// </summary>
    public static string StripCookerSuffix(string filename)
    {
        if (filename.EndsWith(".color.png", StringComparison.OrdinalIgnoreCase))
            return filename[..^".color.png".Length] + ".png";
        if (filename.EndsWith(".normal.png", StringComparison.OrdinalIgnoreCase))
            return filename[..^".normal.png".Length] + ".png";
        if (filename.EndsWith(".data.png", StringComparison.OrdinalIgnoreCase))
            return filename[..^".data.png".Length] + ".png";
        return filename;
    }

    /// <summary>
    /// Returns the BeamNG Texture Cooker suffix for a texture filename based on its type identifier.
    /// </summary>
    public static string GetCookerSuffix(string filename)
    {
        if (filename.Contains("_Normal", StringComparison.OrdinalIgnoreCase))
            return ".normal.png";
        if (filename.Contains("_ORM", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("_AO", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("_Roughness", StringComparison.OrdinalIgnoreCase)
            || filename.Contains("_Metallic", StringComparison.OrdinalIgnoreCase))
            return ".data.png";
        return ".color.png";
    }

    /// <summary>
    /// Derives the source base name (without extension) from a BeamNG Texture Cooker filename.
    /// E.g., "Bricks029_Color.color.png" → "Bricks029_Color"
    /// </summary>
    public static string DeriveSourceBaseName(string beamngFileName)
    {
        if (beamngFileName.EndsWith(".color.png", StringComparison.OrdinalIgnoreCase))
            return beamngFileName[..^".color.png".Length];
        if (beamngFileName.EndsWith(".normal.png", StringComparison.OrdinalIgnoreCase))
            return beamngFileName[..^".normal.png".Length];
        if (beamngFileName.EndsWith(".data.png", StringComparison.OrdinalIgnoreCase))
            return beamngFileName[..^".data.png".Length];
        return Path.GetFileNameWithoutExtension(beamngFileName);
    }

    /// <summary>
    /// Converts a source image (JPG/PNG) to an 8-bit PNG for BeamNG's Texture Cooker.
    /// If the source is already a PNG, it is copied directly (no re-encoding needed).
    /// </summary>
    public static void ConvertToPng(string sourcePath, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // If source is already PNG, just copy it
        if (sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, outputPath, overwrite: true);
            return;
        }

        // Convert JPG (or other format) to PNG
        using var image = Image.Load<Rgba32>(sourcePath);
        image.SaveAsPng(outputPath);
    }

    /// <summary>
    /// Generates a solid-color 256×256 placeholder PNG texture.
    /// Used when no source texture is available.
    /// </summary>
    public static void GeneratePlaceholderPng(string textureFileName, BuildingMaterialDefinition material, string targetPath)
    {
        const int textureSize = 256;

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var suffix = GetCookerSuffix(textureFileName);

        Rgba32 color;
        if (suffix == ".normal.png")
        {
            // Flat normal map: R=128, G=128, B=255 (pointing straight up)
            color = new Rgba32(128, 128, 255);
        }
        else if (suffix == ".data.png")
        {
            // Channel-specific defaults for split PBR textures
            if (textureFileName.Contains("_AO", StringComparison.OrdinalIgnoreCase))
                color = new Rgba32(255, 255, 255); // AO: white = no occlusion
            else if (textureFileName.Contains("_Roughness", StringComparison.OrdinalIgnoreCase))
                color = new Rgba32(128, 128, 128); // Roughness: medium
            else if (textureFileName.Contains("_Metallic", StringComparison.OrdinalIgnoreCase))
                color = new Rgba32(0, 0, 0);       // Metallic: non-metallic
            else
                color = new Rgba32(255, 128, 0);   // ORM combined fallback
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

        using var image = new Image<Rgba32>(textureSize, textureSize, color);
        image.SaveAsPng(targetPath);
    }

    /// <summary>
    /// Splits a combined ORM (Occlusion/Roughness/Metallic) texture into three
    /// separate grayscale channel PNGs for BeamNG's individual PBR map slots.
    /// Follows glTF 2.0 channel convention: R=AO, G=Roughness, B=Metallic.
    /// Output files are saved in the specified output directory using names derived
    /// from the ORM filename via <see cref="OrmTextureHelper"/>.
    /// </summary>
    /// <param name="ormSourcePath">Absolute path to the source ORM image (PNG or JPG).</param>
    /// <param name="outputDirectory">Directory where split files are written.</param>
    /// <param name="ormFileName">The ORM filename with BeamNG cooker suffix
    /// (e.g., "Bricks029_ORM.data.png"). Used to derive output filenames.</param>
    public static void SplitOrmTexture(string ormSourcePath, string outputDirectory, string ormFileName)
    {
        using var ormImage = Image.Load<Rgba32>(ormSourcePath);
        var width = ormImage.Width;
        var height = ormImage.Height;

        using var aoImage = new Image<L8>(width, height);
        using var roughnessImage = new Image<L8>(width, height);
        using var metallicImage = new Image<L8>(width, height);

        ormImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                var ormRow = accessor.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    var pixel = ormRow[x];
                    aoImage[x, y] = new L8(pixel.R);
                    roughnessImage[x, y] = new L8(pixel.G);
                    metallicImage[x, y] = new L8(pixel.B);
                }
            }
        });

        Directory.CreateDirectory(outputDirectory);

        aoImage.SaveAsPng(Path.Combine(outputDirectory, OrmTextureHelper.DeriveAoFileName(ormFileName)));
        roughnessImage.SaveAsPng(Path.Combine(outputDirectory, OrmTextureHelper.DeriveRoughnessFileName(ormFileName)));
        metallicImage.SaveAsPng(Path.Combine(outputDirectory, OrmTextureHelper.DeriveMetallicFileName(ormFileName)));
    }
}
