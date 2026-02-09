using BeamNG_LevelCleanUp.Communication;
using BeamNG_LevelCleanUp.Objects;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// Alias to disambiguate from System.Drawing types
using Color = SixLabors.ImageSharp.Color;
using FontFamily = SixLabors.Fonts.FontFamily;
using FontStyle = SixLabors.Fonts.FontStyle;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using SystemFonts = SixLabors.Fonts.SystemFonts;

namespace BeamNG_LevelCleanUp.Logic;

/// <summary>
///     Generates preview images for BeamNG levels
/// </summary>
public class LevelPreviewGenerator
{
    private const int PreviewWidth = 800;
    private const int PreviewHeight = 600;
    
    /// <summary>
    ///     Generates a preview image for a level with the display name centered
    /// </summary>
    /// <param name="targetLevelNamePath">Full path to the target level folder (e.g., .../levels/my_map)</param>
    /// <param name="levelPathName">The level folder name (e.g., my_map)</param>
    /// <param name="levelDisplayName">Display name for the level</param>
    /// <returns>The full path to the generated preview image</returns>
    public static string GeneratePreviewImage(string targetLevelNamePath, string levelPathName, string levelDisplayName)
    {
        try
        {
            var previewFileName = $"{levelPathName}_preview.jpg";
            var previewPath = Path.Join(targetLevelNamePath, previewFileName);
            
            // Create the image with black background
            using var image = new Image<Rgba32>(PreviewWidth, PreviewHeight, new Rgba32(0, 0, 0));
            
            // Define orange color for text
            var orangeColor = Color.FromRgb(255, 165, 0);
            
            // Get a system font or fallback
            var fontFamily = GetFontFamily();
            var font = fontFamily.CreateFont(48, FontStyle.Bold);
            
            // Measure the text to center it
            var textOptions = new RichTextOptions(font)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Origin = new SixLabors.ImageSharp.PointF(PreviewWidth / 2f, PreviewHeight / 2f)
            };
            
            // Draw the text
            image.Mutate(ctx => ctx.DrawText(textOptions, levelDisplayName, orangeColor));
            
            // Save as JPEG
            image.SaveAsJpeg(previewPath);
            
            PubSubChannel.SendMessage(PubSubMessageType.Info, 
                $"Generated preview image: {previewFileName}");
            
            return previewPath;
        }
        catch (Exception ex)
        {
            PubSubChannel.SendMessage(PubSubMessageType.Warning, 
                $"Could not generate preview image: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    ///     Gets the preview filename for a given level path name
    /// </summary>
    /// <param name="levelPathName">The level folder name (e.g., my_map)</param>
    /// <returns>The preview filename (e.g., my_map_preview.jpg)</returns>
    public static string GetPreviewFileName(string levelPathName)
    {
        return $"{levelPathName}_preview.jpg";
    }
    
    /// <summary>
    ///     Gets a font family, trying system fonts first then falling back to available fonts
    /// </summary>
    private static FontFamily GetFontFamily()
    {
        // Try to use system fonts
        if (SystemFonts.TryGet("Arial", out var arialFamily))
        {
            return arialFamily;
        }
        
        if (SystemFonts.TryGet("Segoe UI", out var segoeFamily))
        {
            return segoeFamily;
        }
        
        if (SystemFonts.TryGet("DejaVu Sans", out var dejaVuFamily))
        {
            return dejaVuFamily;
        }
        
        // Fallback to any available system font
        foreach (var family in SystemFonts.Families)
        {
            return family;
        }
        
        throw new InvalidOperationException("No fonts available on the system");
    }
}
