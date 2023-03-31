using BeamNG_LevelCleanUp.Communication;
using Pfim;
using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ImageFormat = Pfim.ImageFormat;
using System.Windows.Media;

namespace BeamNG_LevelCleanUp.Utils
{
    public class DDSImage
    {
        public float Ratio { get; set; }
        public string SaveAs(string sourceFile, System.Drawing.Imaging.ImageFormat targetFormat)
        {
            var fi = new System.IO.FileInfo(sourceFile);
            if (fi.Extension.ToUpper() != ".DDS") return sourceFile;

            using (var image = Pfimage.FromFile(sourceFile))
            {
                PixelFormat format;

                // Convert from Pfim's backend agnostic image format into GDI+'s image format
                switch (image.Format)
                {
                    case ImageFormat.Rgba32:
                        format = PixelFormats.Bgra32;
                        break;
                    case ImageFormat.Rgb24:
                        format = PixelFormats.Bgr24;
                        break;
                    case ImageFormat.Rgb8:
                        format = PixelFormats.Gray8;
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
                    Ratio = (float)image.Width / (float)image.Height;
                    var data = Marshal.UnsafeAddrOfPinnedArrayElement(image.Data, 0);
                    var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, format, null, data, image.Data.Length, image.Stride);
                    //var bitmap = new Bitmap(image.Width, image.Height, image.Stride, format, data);
                    switch (targetFormat.ToString())
                    {
                        case "Png":
                            retVal = Path.ChangeExtension(sourceFile, ".png");
                            using (var fileStream = new FileStream(retVal, FileMode.Create))
                            {
                                BitmapEncoder encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                encoder.Save(fileStream);
                            }
                            break;
                        case "Bmp":
                            retVal = Path.ChangeExtension(sourceFile, ".bmp");
                            using (var fileStream = new FileStream(retVal, FileMode.Create))
                            {
                                BitmapEncoder encoder = new BmpBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                encoder.Save(fileStream);
                            }
                            break;
                        case "Jpeg":
                            retVal = Path.ChangeExtension(sourceFile, ".jpg");
                            using (var fileStream = new FileStream(retVal, FileMode.Create))
                            {
                                BitmapEncoder encoder = new JpegBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                encoder.Save(fileStream);
                            }
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
