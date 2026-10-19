using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-023 T1 Windows fallback when ImageSharp cannot load: WPF decode, EXIF orientation 1-8, the exact INSPECT size,
    /// 72 dpi, JPEG 85, no metadata copied and no brightening (P1-P3, P6-P7). Uses WPF types only, so it runs where
    /// ImageSharp is missing. Invalid image data is never returned as a successful photo.
    /// </summary>
    public static class WpfPhotoFallback
    {
        public static byte[] Process(byte[] photoData)
        {
            using var input = new MemoryStream(photoData, writable: false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            BitmapSource bitmap = frame;
            ushort orientation = 1;
            if (frame.Metadata is BitmapMetadata metadata)
            {
                // JPEG and TIFF metadata expose orientation under different query paths.
                foreach (string query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    try
                    {
                        if (metadata.GetQuery(query) is ushort value)
                        {
                            orientation = value;
                            break;
                        }
                    }
                    catch (NotSupportedException) { }
                }
            }

            var transform = new TransformGroup();
            switch (orientation)
            {
                case 2: transform.Children.Add(new ScaleTransform(-1, 1)); break;
                case 3: transform.Children.Add(new RotateTransform(180)); break;
                case 4: transform.Children.Add(new ScaleTransform(1, -1)); break;
                case 5:
                    transform.Children.Add(new ScaleTransform(-1, 1));
                    transform.Children.Add(new RotateTransform(270));
                    break;
                case 6: transform.Children.Add(new RotateTransform(90)); break;
                case 7:
                    transform.Children.Add(new ScaleTransform(-1, 1));
                    transform.Children.Add(new RotateTransform(90));
                    break;
                case 8: transform.Children.Add(new RotateTransform(270)); break;
            }
            if (transform.Children.Count > 0)
                bitmap = new TransformedBitmap(bitmap, transform);

            // P3: an explicit W' x H' scale, always applied; any rounding WPF adds is cropped off.
            var (width, height) = PhotoProcessingService.HalfSize(bitmap.PixelWidth, bitmap.PixelHeight);
            BitmapSource scaled = new TransformedBitmap(bitmap, new ScaleTransform((double)width / bitmap.PixelWidth, (double)height / bitmap.PixelHeight));
            if (scaled.PixelWidth != width || scaled.PixelHeight != height)
                scaled = new CroppedBitmap(scaled, new Int32Rect(0, 0, Math.Min(width, scaled.PixelWidth), Math.Min(height, scaled.PixelHeight)));

            // P6: the pixels copied into a 72 dpi bitmap, so the encoder writes a 72x72 dpi JFIF density. Source metadata is
            // not copied: the pixels are already oriented, and an old orientation tag would rotate them again.
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgr32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var output72 = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, PhotoProcessingService.Dpi, PhotoProcessingService.Dpi,
                PixelFormats.Bgr32, null, pixels, stride);

            var encoder = new JpegBitmapEncoder { QualityLevel = PhotoProcessingService.JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(output72, null, null, null));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
    }
}
