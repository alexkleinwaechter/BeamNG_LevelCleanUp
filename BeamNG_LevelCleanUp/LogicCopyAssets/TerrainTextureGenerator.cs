using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using System.Text.Json.Nodes;

namespace BeamNG_LevelCleanUp.LogicCopyAssets
{
    /// <summary>
    /// Generates replacement terrain textures (8-bit PNG files) for terrain materials
    /// </summary>
    public class TerrainTextureGenerator
    {
        private readonly string _terrainFolderPath;
        private readonly int _terrainSize;

        // Predefined texture types and their colors/properties
        private static readonly Dictionary<string, TextureProperties> TextureDefinitions = new()
        {
            { "baseColorBaseTex", new TextureProperties("#808080", "#base_color", TextureType.RGBA) },
            { "aoBaseTex", new TextureProperties("#FFFFFF", "#base_ao", TextureType.Grayscale) },
            { "heightBaseTex", new TextureProperties("#000000", "#base_h", TextureType.Grayscale) },
            { "normalBaseTex", new TextureProperties("#8080FF", "#base_nm", TextureType.Normal) },
            { "roughnessBaseTex", new TextureProperties("#EFEFEF", "#base_r", TextureType.Grayscale) }
        };

        public TerrainTextureGenerator(string terrainFolderPath, int terrainSize)
        {
            _terrainFolderPath = terrainFolderPath;
            _terrainSize = terrainSize;
        }

        /// <summary>
        /// Generates an 8-bit PNG file with a solid color at terrain size dimensions
        /// </summary>
        /// <param name="hexColor">The color in hex format (e.g., #505a2d)</param>
        /// <param name="fileName">The filename for the PNG (without extension)</param>
        /// <param name="textureType">The type of texture to generate</param>
        /// <returns>The full path to the generated PNG file</returns>
        public string GenerateSolidColorPng(string hexColor, string fileName, TextureType textureType)
        {
            try
            {
                // Ensure the hex color starts with #
                if (!hexColor.StartsWith("#"))
                {
                    hexColor = "#" + hexColor;
                }

                // Validate hex color format
                if (hexColor.Length != 7)
                {
                    throw new ArgumentException($"Invalid hex color format: {hexColor}. Expected format: #RRGGBB");
                }

                // Parse the hex color
                var r = Convert.ToByte(hexColor.Substring(1, 2), 16);
                var g = Convert.ToByte(hexColor.Substring(3, 2), 16);
                var b = Convert.ToByte(hexColor.Substring(5, 2), 16);

                // Create the output file path
                var outputPath = Path.Join(_terrainFolderPath, $"{fileName}.png");

                // Ensure the terrain folder exists
                Directory.CreateDirectory(_terrainFolderPath);

                // Check if file already exists to avoid regenerating
                if (File.Exists(outputPath))
                {
                    PubSubChannel.SendMessage(PubSubMessageType.Info,
                     $"Terrain texture {fileName}.png already exists, reusing.");
                    return outputPath;
                }

                // Create the image based on texture type
                switch (textureType)
                {
                    case TextureType.Grayscale:
                        GenerateGrayscaleImage(outputPath, r);
                        break;
                    case TextureType.Normal:
                        GenerateNormalMapImage(outputPath);
                        break;
                    case TextureType.RGBA:
                    default:
                        GenerateRgbaImage(outputPath, r, g, b);
                        break;
                }

                PubSubChannel.SendMessage(PubSubMessageType.Info,
                       $"Generated terrain texture: {fileName}.png ({_terrainSize}x{_terrainSize}, {textureType})");

                return outputPath;
            }
            catch (Exception ex)
            {
                PubSubChannel.SendMessage(PubSubMessageType.Error,
             $"Failed to generate terrain texture {fileName}: {ex.Message}");
                throw;
            }
        }

        private void GenerateRgbaImage(string outputPath, byte r, byte g, byte b)
        {
            using var image = new Image<Rgba32>(_terrainSize, _terrainSize);
            var color = new Rgba32(r, g, b);

            for (int y = 0; y < _terrainSize; y++)
            {
                for (int x = 0; x < _terrainSize; x++)
                {
                    image[x, y] = color;
                }
            }

            image.SaveAsPng(outputPath);
        }

        private void GenerateGrayscaleImage(string outputPath, byte grayscaleValue)
        {
            using var image = new Image<L8>(_terrainSize, _terrainSize);
            var color = new L8(grayscaleValue);

            for (int y = 0; y < _terrainSize; y++)
            {
                for (int x = 0; x < _terrainSize; x++)
                {
                    image[x, y] = color;
                }
            }

            image.SaveAsPng(outputPath);
        }

        private void GenerateNormalMapImage(string outputPath)
        {
            // Flat normal map: RGB(128, 128, 255) which represents normal vector pointing straight up (0, 0, 1)
            using var image = new Image<Rgba32>(_terrainSize, _terrainSize);
            var normalColor = new Rgba32(128, 128, 255);

            for (int y = 0; y < _terrainSize; y++)
            {
                for (int x = 0; x < _terrainSize; x++)
                {
                    image[x, y] = normalColor;
                }
            }

            image.SaveAsPng(outputPath);
        }

        /// <summary>
        /// Checks if a property should be replaced with a generated texture
        /// </summary>
        public static bool IsReplaceableTexture(string propertyName)
        {
            return TextureDefinitions.ContainsKey(propertyName);
        }

        /// <summary>
        /// Gets the texture properties for a given property name
        /// </summary>
        public static TextureProperties? GetTextureProperties(string propertyName)
        {
            return TextureDefinitions.TryGetValue(propertyName, out var props) ? props : null;
        }

        /// <summary>
        /// Checks if a property name indicates it's a baseColorBaseTex property
        /// </summary>
        public static bool IsBaseColorBaseTexProperty(string propertyName)
        {
            return propertyName.Equals("baseColorBaseTex", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a property name indicates it's a baseColorBaseTexSize property
        /// </summary>
        public static bool IsBaseColorBaseTexSizeProperty(string propertyName)
        {
            return propertyName.Equals("baseColorBaseTexSize", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Defines the type of texture to generate
    /// </summary>
    public enum TextureType
    {
        RGBA,
        Grayscale,
        Normal
    }

    /// <summary>
    /// Properties for texture generation
    /// </summary>
    public class TextureProperties
    {
        public string HexColor { get; }
        public string FileName { get; }
        public TextureType Type { get; }

        public TextureProperties(string hexColor, string fileName, TextureType type)
        {
            HexColor = hexColor;
            FileName = fileName;
            Type = type;
        }
    }
}
