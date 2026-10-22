using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 Z2 on Windows: crops a JPEG still to the centred region of a digital zoom. The result has the crop's own pixel
    /// size (never scaled up) and the same EXIF orientation. WinRT imaging runs on a thread-pool thread, as the backend's.
    /// </summary>
    public sealed class JpegStillCropper : ICameraStillCropper
    {
        public Task<CapturedStill> CropAsync(CapturedStill still, double factor) => Task.Run(() => CropCoreAsync(still, factor));

        private static async Task<CapturedStill> CropCoreAsync(CapturedStill still, double factor)
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(still.Jpeg.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            var crop = CameraZoom.CropFor(width, height, factor);
            if (crop.Width == width && crop.Height == height) return still with { Width = width, Height = height };

            // Stored pixels, orientation ignored: a centred crop is the same region whichever way the photo is shown.
            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds { X = (uint)crop.X, Y = (uint)crop.Y, Width = (uint)crop.Width, Height = (uint)crop.Height },
            };
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.95, Windows.Foundation.PropertyType.Single) } };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)crop.Width, (uint)crop.Height,
                decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
            if (still.ExifOrientation is >= 1 and <= 8)
            {
                try
                {
                    await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                    {
                        { "System.Photo.Orientation", new BitmapTypedValue((ushort)still.ExifOrientation, Windows.Foundation.PropertyType.UInt16) },
                    });
                }
                catch (Exception)
                {
                    // the crop is still good; F-023 then sees no orientation
                }
            }
            await encoder.FlushAsync();
            return still with { Jpeg = await WinRtCameraStream.ReadAllAsync(output), Width = crop.Width, Height = crop.Height, Zoom = factor };
        }
    }
}
