using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeamNG_LevelCleanUp.Utils;
using Pfim;
using ImageFormat = Pfim.ImageFormat;
using TextureModel = HelixToolkit.Wpf.SharpDX.TextureModel;

namespace BeamNG_LevelCleanUp.Viewer3D;

/// <summary>
/// Utility class for loading textures from various formats including DDS.
/// Supports .link file resolution from game asset ZIPs.
/// </summary>
public static class TextureLoader
{
    /// <summary>
    /// Map type names that represent opacity maps.
    /// </summary>
    private static readonly HashSet<string> OpacityMapTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "opacitymap", "alphamap", "transparencymap"
    };

    /// <summary>
    /// Checks if a map type represents an opacity map.
    /// </summary>
    public static bool IsOpacityMap(string? mapType)
    {
        if (string.IsNullOrEmpty(mapType)) return false;
        return OpacityMapTypes.Contains(mapType.ToLowerInvariant());
    }

    /// <summary>
    /// Loads a texture file and returns a TextureModel.
    /// Handles DDS files by converting to a format Helix can use.
    /// Supports .link file resolution from game asset ZIPs.
    /// Detects actual content type from stream header, not file extension.
    /// </summary>
    public static TextureModel? LoadTexture(string filePath)
    {
        try
        {
            // Get file stream (handles .link files automatically)
            using var stream = LinkFileResolver.GetFileStream(filePath);
            if (stream == null)
                return null;

            // Detect actual content type from stream, not extension
            if (IsDdsStream(stream))
            {
                stream.Position = 0;
                return LoadDdsTextureFromStream(stream);
            }

            // Try loading as standard image
            try
            {
                stream.Position = 0;
                return TextureModel.Create(stream);
            }
            catch
            {
                // If standard loading fails, try as DDS (last resort)
                try
                {
                    stream.Position = 0;
                    return LoadDdsTextureFromStream(stream);
                }
                catch
                {
                    return null;
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads an albedo texture and merges an opacity map into its alpha channel.
    /// If no opacity map is provided, loads the albedo texture normally.
    /// </summary>
    /// <param name="albedoPath">Path to the albedo/color texture</param>
    /// <param name="opacityPath">Optional path to the opacity map (grayscale)</param>
    /// <returns>TextureModel with opacity embedded in alpha channel</returns>
    public static TextureModel? LoadTextureWithOpacity(string albedoPath, string? opacityPath)
    {
        // If no opacity map, just load the albedo normally
        if (string.IsNullOrEmpty(opacityPath) || !CanLoadTexture(opacityPath))
        {
            return LoadTexture(albedoPath);
        }

        try
        {
            // Load albedo as BitmapSource
            var albedoBitmap = LoadAsBitmapSource(albedoPath);
            if (albedoBitmap == null)
                return LoadTexture(albedoPath); // Fallback

            // Load opacity as BitmapSource
            var opacityBitmap = LoadAsBitmapSource(opacityPath);
            if (opacityBitmap == null)
                return LoadTexture(albedoPath); // Fallback

            // Merge opacity into albedo's alpha channel
            var mergedBitmap = MergeOpacityIntoAlpha(albedoBitmap, opacityBitmap);
            if (mergedBitmap == null)
                return LoadTexture(albedoPath); // Fallback

            // Convert to PNG stream and create TextureModel
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(mergedBitmap));

            var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;

            return TextureModel.Create(ms);
        }
        catch
        {
            // Fallback to loading albedo without opacity
            return LoadTexture(albedoPath);
        }
    }

    /// <summary>
    /// Loads a texture file as a BitmapSource for pixel manipulation.
    /// </summary>
    private static BitmapSource? LoadAsBitmapSource(string filePath)
    {
        try
        {
            using var stream = LinkFileResolver.GetFileStream(filePath);
            if (stream == null)
                return null;

            // Check if it's a DDS file
            if (IsDdsStream(stream))
            {
                stream.Position = 0;
                using var image = Pfimage.FromStream(stream);

                PixelFormat format = image.Format switch
                {
                    ImageFormat.Rgba32 => PixelFormats.Bgra32,
                    ImageFormat.Rgb24 => PixelFormats.Bgr24,
                    ImageFormat.Rgb8 => PixelFormats.Gray8,
                    _ => PixelFormats.Bgra32
                };

                var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
                try
                {
                    var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
                    var bitmap = BitmapSource.Create(
                        image.Width, image.Height,
                        96, 96,
                        format, null,
                        data, image.Data.Length, image.Stride);

                    // Freeze to make it cross-thread accessible
                    bitmap.Freeze();
                    return bitmap;
                }
                finally
                {
                    handle.Free();
                }
            }

            // Standard image formats
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Merges an opacity map (grayscale) into the alpha channel of an albedo bitmap.
    /// The opacity map's luminance values become the alpha values.
    /// </summary>
    private static BitmapSource? MergeOpacityIntoAlpha(BitmapSource albedo, BitmapSource opacity)
    {
        try
        {
            int width = albedo.PixelWidth;
            int height = albedo.PixelHeight;

            // Convert albedo to Bgra32 for manipulation
            var albedoBgra = new FormatConvertedBitmap(albedo, PixelFormats.Bgra32, null, 0);
            
            // Convert opacity to grayscale - we'll use the luminance as alpha
            var opacityGray = new FormatConvertedBitmap(opacity, PixelFormats.Gray8, null, 0);

            // Resize opacity to match albedo if dimensions differ
            BitmapSource resizedOpacity = opacityGray;
            if (opacity.PixelWidth != width || opacity.PixelHeight != height)
            {
                var scaleX = (double)width / opacity.PixelWidth;
                var scaleY = (double)height / opacity.PixelHeight;
                var transform = new ScaleTransform(scaleX, scaleY);
                resizedOpacity = new TransformedBitmap(opacityGray, transform);
                
                // Re-convert after transform to ensure Gray8 format
                resizedOpacity = new FormatConvertedBitmap(resizedOpacity, PixelFormats.Gray8, null, 0);
            }

            // Get albedo pixels (BGRA)
            int albedoStride = width * 4;
            byte[] albedoPixels = new byte[height * albedoStride];
            albedoBgra.CopyPixels(albedoPixels, albedoStride, 0);

            // Get opacity pixels (grayscale)
            int opacityStride = width;
            byte[] opacityPixels = new byte[height * opacityStride];
            resizedOpacity.CopyPixels(opacityPixels, opacityStride, 0);

            // Merge: set alpha channel from opacity grayscale value
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int albedoIdx = y * albedoStride + x * 4;
                    int opacityIdx = y * opacityStride + x;

                    // BGRA format: index+3 is alpha
                    albedoPixels[albedoIdx + 3] = opacityPixels[opacityIdx];
                }
            }

            // Create result bitmap
            var result = BitmapSource.Create(
                width, height,
                96, 96,
                PixelFormats.Bgra32, null,
                albedoPixels, albedoStride);

            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Loads a DDS texture from a stream and converts it to a TextureModel.
    /// </summary>
    public static TextureModel? LoadDdsTextureFromStream(Stream stream)
    {
        using var image = Pfimage.FromStream(stream);

        PixelFormat format = image.Format switch
        {
            ImageFormat.Rgba32 => PixelFormats.Bgra32,
            ImageFormat.Rgb24 => PixelFormats.Bgr24,
            ImageFormat.Rgb8 => PixelFormats.Gray8,
            _ => PixelFormats.Bgra32
        };

        var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
        try
        {
            var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
            var bitmap = BitmapSource.Create(
                image.Width, image.Height,
                96, 96,
                format, null,
                data, image.Data.Length, image.Stride);

            // Convert to PNG stream
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;

            return TextureModel.Create(ms);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Checks if a stream contains DDS data by examining the magic bytes.
    /// DDS files start with "DDS " (0x44 0x44 0x53 0x20).
    /// </summary>
    public static bool IsDdsStream(Stream stream)
    {
        if (stream.Length < 4)
            return false;

        var originalPosition = stream.Position;
        stream.Position = 0;

        Span<byte> magic = stackalloc byte[4];
        var bytesRead = stream.Read(magic);

        stream.Position = originalPosition;

        // DDS magic: "DDS " = 0x44, 0x44, 0x53, 0x20
        return bytesRead == 4 &&
               magic[0] == 0x44 &&
               magic[1] == 0x44 &&
               magic[2] == 0x53 &&
               magic[3] == 0x20;
    }

    /// <summary>
    /// Checks if a file can be loaded (exists directly or can be resolved via .link).
    /// </summary>
    public static bool CanLoadTexture(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        return File.Exists(filePath) || LinkFileResolver.CanResolve(filePath);
    }

    /// <summary>
    /// Gets the aspect ratio of an image file, including DDS and .link support.
    /// Detects actual content type from stream header, not file extension.
    /// </summary>
    public static float GetImageAspectRatio(string filePath)
    {
        try
        {
            // Get file stream (handles .link files automatically)
            using var stream = LinkFileResolver.GetFileStream(filePath);
            if (stream == null)
                return 1.0f;

            // Detect actual content type from stream, not extension
            if (IsDdsStream(stream))
            {
                stream.Position = 0;
                using var image = Pfimage.FromStream(stream);
                if (image.Width > 0 && image.Height > 0)
                    return (float)image.Width / image.Height;
                return 1.0f;
            }

            // Try Pfim first (handles more formats)
            try
            {
                stream.Position = 0;
                using var pfimImage = Pfimage.FromStream(stream);
                if (pfimImage.Width > 0 && pfimImage.Height > 0)
                    return (float)pfimImage.Width / pfimImage.Height;
            }
            catch
            {
                // Pfim couldn't read it, try GDI+ for standard formats
            }

            // Try GDI+ as fallback
            try
            {
                stream.Position = 0;
                using var img = System.Drawing.Image.FromStream(stream);
                if (img.Width > 0 && img.Height > 0)
                    return (float)img.Width / img.Height;
            }
            catch
            {
                // GDI+ couldn't read it either
            }

            return 1.0f;
        }
        catch
        {
            return 1.0f;
        }
    }
}
