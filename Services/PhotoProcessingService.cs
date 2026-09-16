using System;
using System.IO;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InspectionEditor.Services
{
    /// <summary>Keeps optional image-library loading outside the caller's JIT boundary.</summary>
    public static class PhotoProcessingService
    {
        public const int MaxPhotoWidth = 1984;
        public const int MaxPhotoHeight = 1116;

        // This method's signature, locals and catch clauses must use framework types only.
        public static byte[] Process(byte[] photoData, Func<byte[], byte[]> fallback)
        {
            ArgumentNullException.ThrowIfNull(photoData);
            ArgumentNullException.ThrowIfNull(fallback);
            try
            {
                return EnhanceCore(photoData);
            }
            catch (Exception ex) when (ex is FileNotFoundException ||
                                       ex is FileLoadException ||
                                       ex is BadImageFormatException)
            {
                // The core only reads in-memory bytes, so these are dependency-loader errors,
                // not missing photo files. Decoder/corrupt-image errors must NOT take this path.
                System.Diagnostics.Debug.WriteLine($"Photo enhancement dependency unavailable: {ex.Message}");
                return fallback(photoData);
            }
        }

        // Essential: the JIT may resolve ImageSharp before entering this method's body.
        // NoInlining makes that failure occur inside Process's protected call site.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] EnhanceCore(byte[] photoData)
        {
            using var image = Image.Load<Rgba32>(photoData);
            // Normalize pixels before sizing and reset EXIF orientation to prevent double rotation.
            image.Mutate(ctx => ctx.AutoOrient());
            image.Mutate(ctx =>
            {
                if (image.Width > MaxPhotoWidth || image.Height > MaxPhotoHeight)
                {
                    ctx.Resize(new ResizeOptions
                    {
                        Size = new Size(MaxPhotoWidth, MaxPhotoHeight),
                        Mode = ResizeMode.Max
                    });
                }
                ctx.Brightness(1.25f);
            });
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = 85 });
            return output.ToArray();
        }
    }
}
