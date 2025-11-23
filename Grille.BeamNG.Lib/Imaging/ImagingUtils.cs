using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grille.BeamNG.Imaging;
public static unsafe class ImagingUtils
{
    /// <summary>
    /// Performs drawing operation on a Row-Mayjor buffer.
    /// </summary>
    public static void Draw<T>(in DrawArguments<T> args) where T : unmanaged
    {
        var srcRect = args.SrcRect;
        var dstRect = args.DstRect;

        int srcWidth = args.SrcSize.Width;
        int srcHeight = args.SrcSize.Height;
        int dstWidth = args.DstSize.Width;
        int dstHeight = args.DstSize.Height;

        if (srcRect.X < 0 || srcRect.Y < 0 || srcRect.Right > srcWidth || srcRect.Bottom > srcHeight)
        {
            throw new ArgumentException($"{nameof(srcRect)} out of bounds.", nameof(srcRect));
        }

        var srcBuffer = args.SrcBuffer;
        var dstBuffer = args.DstBuffer;


        for (int iy = 0; iy < dstRect.Width; iy++)
        {
            int dstY = dstRect.Y + iy;
            if (dstY < 0 || dstY >= dstHeight)
                continue;

            int srcY = (int)((float)iy / dstRect.Height * srcRect.Height + srcRect.Y);

            for (int ix = 0; ix < dstRect.Height; ix++)
            {
                int dstX = dstRect.X + ix;
                if (dstX < 0 || dstX >= dstWidth)
                    continue;

                int srcX = (int)((float)ix / dstRect.Width * srcRect.Width + srcRect.X);

                var srcPtr = srcBuffer + srcY * srcWidth + srcX;
                var dstPtr = dstBuffer + dstY * dstWidth + dstX;

                var opArgs = new DrawOperatorArguments<T>(dstPtr, srcPtr);
                args.Operator(opArgs);
            }
        }
    }
}
