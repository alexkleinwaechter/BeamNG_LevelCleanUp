using BeamNgTerrainPoc.Terrain.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BeamNgTerrainPoc.Terrain.Services;

/// <summary>
///     Exports small legend images alongside debug visualizations.
///     Each legend entry is either a colored rectangle swatch or a filled circle,
///     with a text label rendered using a system font.
/// </summary>
public static class DebugLegendExporter
{
    private const int Padding = 10;
    private const int RowHeight = 22;
    private const int SwatchSize = 14;
    private const int TextOffsetX = SwatchSize + 8;
    private const float FontSize = 13f;

    /// <summary>
    ///     A single entry in a debug legend.
    /// </summary>
    public record LegendEntry(Rgba32 Color, string Label, LegendSwatchStyle Style = LegendSwatchStyle.Rectangle);

    /// <summary>
    ///     Swatch drawing style.
    /// </summary>
    public enum LegendSwatchStyle
    {
        Rectangle,
        Circle,
        CircleWithOutline,
        Spacer
    }

    /// <summary>
    ///     Exports a legend image to the given path.
    ///     Returns false if a system font could not be loaded.
    /// </summary>
    public static bool Export(string outputPath, IReadOnlyList<LegendEntry> entries)
    {
        var font = GetLegendFont(FontSize);
        if (font == null)
        {
            TerrainLogger.Warning("Could not load system font for legend — skipping legend export.");
            return false;
        }

        var imageWidth = 280;
        var imageHeight = Padding * 2 + entries.Count * RowHeight;

        using var legend = new Image<Rgba32>(imageWidth, imageHeight, new Rgba32(20, 20, 20, 255));
        var textColor = Color.FromRgba(220, 220, 220, 255);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Style == LegendSwatchStyle.Spacer || string.IsNullOrEmpty(entry.Label))
                continue;

            var y = Padding + i * RowHeight;
            var swatchCenterX = Padding + SwatchSize / 2;
            var swatchCenterY = y + SwatchSize / 2;

            switch (entry.Style)
            {
                case LegendSwatchStyle.Circle:
                    DrawFilledCircle(legend, swatchCenterX, swatchCenterY, SwatchSize / 2, entry.Color);
                    break;

                case LegendSwatchStyle.CircleWithOutline:
                    DrawFilledCircle(legend, swatchCenterX, swatchCenterY, SwatchSize / 2, entry.Color);
                    DrawCircleOutline(legend, swatchCenterX, swatchCenterY, SwatchSize / 2 + 2,
                        new Rgba32(255, 255, 255, 200));
                    break;

                default: // Rectangle
                    for (var sy = 0; sy < SwatchSize; sy++)
                    for (var sx = 0; sx < SwatchSize; sx++)
                    {
                        var px = Padding + sx;
                        var py = y + sy;
                        if (px < imageWidth && py < imageHeight)
                            legend[px, py] = entry.Color;
                    }

                    break;
            }

            var textOptions = new RichTextOptions(font)
            {
                Origin = new PointF(Padding + TextOffsetX, y)
            };
            legend.Mutate(ctx => ctx.DrawText(textOptions, entry.Label, textColor));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        legend.SaveAsPng(outputPath);
        TerrainLogger.Detail($"  Exported legend: {outputPath}");
        return true;
    }

    /// <summary>
    ///     Convenience: exports a legend next to an existing debug image (same name + _legend suffix).
    /// </summary>
    public static bool ExportAlongside(string debugImagePath, IReadOnlyList<LegendEntry> entries)
    {
        var dir = Path.GetDirectoryName(debugImagePath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(debugImagePath);
        var legendPath = Path.Combine(dir, name + "_legend.png");
        return Export(legendPath, entries);
    }

    #region Drawing Helpers

    private static void DrawFilledCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
            if (x * x + y * y <= radius * radius)
            {
                var px = cx + x;
                var py = cy + y;
                if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                    img[px, py] = color;
            }
    }

    private static void DrawCircleOutline(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
    {
        for (var angle = 0; angle < 360; angle += 2)
        {
            var rad = angle * MathF.PI / 180f;
            var px = cx + (int)(radius * MathF.Cos(rad));
            var py = cy + (int)(radius * MathF.Sin(rad));
            if (px >= 0 && px < img.Width && py >= 0 && py < img.Height)
                img[px, py] = color;
        }
    }

    private static Font? GetLegendFont(float size)
    {
        try
        {
            string[] preferredFonts = ["Segoe UI", "Arial", "Verdana", "Tahoma", "Calibri"];
            foreach (var name in preferredFonts)
                if (SystemFonts.TryGet(name, out var family))
                    return family.CreateFont(size);

            var families = SystemFonts.Collection.Families.ToArray();
            if (families.Length > 0)
                return families[0].CreateFont(size);
        }
        catch
        {
            // Ignore font errors
        }

        return null;
    }

    #endregion
}
