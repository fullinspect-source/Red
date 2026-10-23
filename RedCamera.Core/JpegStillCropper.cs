using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 Z2 and L1 on Windows: crops a JPEG still to the centred region of a digital zoom, or to any region of its stored
    /// pixels (the landscape band). The result has the crop's own pixel size (never scaled up) and the same EXIF
    /// orientation. WinRT imaging runs on a thread-pool thread, as the backend's.
    /// </summary>
    public sealed class JpegStillCropper : ICameraStillCropper
    {
        public Task<CapturedStill> CropAsync(CapturedStill still, double factor) =>
            Task.Run(() => CropCoreAsync(still, (w, h) => CameraZoom.CropFor(w, h, factor), factor));

        public Task<CapturedStill> CropToAsync(CapturedStill still, ZoomCrop region) =>
            Task.Run(() => CropCoreAsync(still, (w, h) => Inside(region, w, h), still.Zoom));

        /// <summary>The region kept within a W x H picture (a still decoded at another size than recorded).</summary>
        private static ZoomCrop Inside(ZoomCrop region, int width, int height)
        {
            int x = Math.Clamp(region.X, 0, Math.Max(0, width - 1)), y = Math.Clamp(region.Y, 0, Math.Max(0, height - 1));
            return new ZoomCrop(x, y, Math.Clamp(region.Width, 1, width - x), Math.Clamp(region.Height, 1, height - y));
        }

        private static async Task<CapturedStill> CropCoreAsync(CapturedStill still, Func<int, int, ZoomCrop> regionFor, double zoom)
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(still.Jpeg.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            var crop = regionFor(width, height);
            if (crop.X == 0 && crop.Y == 0 && crop.Width == width && crop.Height == height) return still with { Width = width, Height = height };

            // Stored pixels, orientation ignored: the region is given in the stored orientation.
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
            return still with { Jpeg = await WinRtCameraStream.ReadAllAsync(output), Width = crop.Width, Height = crop.Height, Zoom = zoom };
        }
    }
}
