using Pfim;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ImageFormat = Pfim.ImageFormat;

namespace BeamNG_LevelCleanUp.Utils
{
    public class DDSImage
    {
        public string SaveAs(string sourceFile, System.Drawing.Imaging.ImageFormat targetFormat) {
            var fi = new System.IO.FileInfo(sourceFile);
            if (fi.Extension.ToUpper() != ".DDS") return sourceFile;

            using (var image = Pfimage.FromFile(sourceFile))
            {
                PixelFormat format;

                // Convert from Pfim's backend agnostic image format into GDI+'s image format
                switch (image.Format)
                {
                    case ImageFormat.Rgba32:
                        format = PixelFormat.Format32bppArgb;
                        break;
                    case ImageFormat.Rgb24:
                        format = PixelFormat.Format24bppRgb;
                        break;
                    case ImageFormat.Rgba16:
                        format = PixelFormat.Format16bppArgb1555;
                        break;
                    case ImageFormat.Rgb8:
                        format = PixelFormat.Format8bppIndexed;
                        break;
                    default:
                        // see the sample for more details
                        throw new NotImplementedException();
                }

                // Pin pfim's data array so that it doesn't get reaped by GC, unnecessary
                // in this snippet but useful technique if the data was going to be used in
                // control like a picture box
                var handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
                var retVal = string.Empty;
                try
                {
                    var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
                    var bitmap = new Bitmap(image.Width, image.Height, image.Stride, format, data);
                    switch (targetFormat.ToString())
                    {
                        case "Png":
                            retVal = Path.ChangeExtension(sourceFile, ".png");
                            bitmap.Save(retVal, System.Drawing.Imaging.ImageFormat.Png);
                            break;
                        case "Bmp":
                            retVal = Path.ChangeExtension(sourceFile, ".bmp");
                            bitmap.Save(retVal, System.Drawing.Imaging.ImageFormat.Bmp);
                            break;
                        case "Jpeg":
                            retVal = Path.ChangeExtension(sourceFile, ".jpg");
                            bitmap.Save(retVal, System.Drawing.Imaging.ImageFormat.Jpeg);
                            break;
                    }
                }
                finally
                {
                    handle.Free();
                }
                return retVal;
            }
        }
    }
}
