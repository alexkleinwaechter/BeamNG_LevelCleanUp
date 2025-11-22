using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Grille.BeamNG;
using Grille.BeamNG.IO.Binary;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace BeamNGTerrainGenerator.BeamNG;

public class ImageProjector<T> where T : unmanaged, IPixel<T>
{
    public Rectangle SrcRect;
    public Rectangle DstRect;
    public Image<T>? Image;

    public void UpdateRect(int resolution)
    {
        if (Image == null) throw new InvalidOperationException();

        int size = Math.Min(Image.Width, Image.Height);
        int x = (Image.Width - size) / 2;
        int y = (Image.Height - size) / 2;
        SrcRect = new Rectangle(x, y, size, size);
        DstRect = new Rectangle(0, 0, resolution, resolution);
    }

    public Image GetCroppedSrcImage(IResampler resampler)
    {
        if (Image == null) throw new InvalidOperationException();

        var cropped = Image.Clone(ctx => ctx.Crop(SrcRect)); // Crop the source area
        cropped.Mutate(ctx => ctx.Resize(DstRect.Size, resampler, false)); // Resize if needed
        return cropped;
    }

    public void DrawCropped(Image dst, IResampler resampler)
    {
        using var cropped = GetCroppedSrcImage(resampler);
        dst.Mutate(ctx => ctx.DrawImage(cropped, new Point(DstRect.X, DstRect.Y), 1f)); // Draw src onto dst
    }

    public void ProjectL16ToHeight(TerrainV9Binary terrain)
    {
        using var canvas = new Image<L16>((int)terrain.Size, (int)terrain.Size);
        DrawCropped(canvas, KnownResamplers.Bicubic);

        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                terrain.HeightData[y * canvas.Width + x] = canvas[x, y].PackedValue;
            }
        }
    }

    public void ProjectL8ToMaterials(TerrainV9Binary terrain)
    {
        using var canvas = new Image<L8>((int)terrain.Size, (int)terrain.Size);
        DrawCropped(canvas, KnownResamplers.NearestNeighbor);

        for (int y = 0; y < canvas.Height; y++)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                terrain.MaterialData[y * canvas.Width + x] = canvas[x, y].PackedValue;
            }
        }
    }

    public Image<Rgba32> CreateBaseColorRgba32Texture(int resolution)
    {
        var canvas = new Image<Rgba32>(resolution, resolution);
        DrawCropped(canvas, KnownResamplers.Bicubic);

        return canvas;
    }
}
