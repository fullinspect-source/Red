using System;
using System.IO;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-023 T1 processing of every new photo (capture and import): orient, halve to INSPECT's size, brighten x1.25,
    /// 72 dpi, JPEG 85. Existing pictures are never passed through here. Keeps optional image-library loading outside the
    /// caller's JIT boundary.
    /// </summary>
    public static class PhotoProcessingService
    {
        public const int JpegQuality = 85;
        public const double Dpi = 72;

        // P3: INSPECT's Image::Scale(img, 0.5) on the oriented size, truncated, each side at least 1.
        public static (int Width, int Height) HalfSize(int width, int height) => (Math.Max(1, width / 2), Math.Max(1, height / 2));

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
            // P2: pixels oriented first (all 8 EXIF values), the tag reset to 1 so viewers never rotate twice.
            image.Mutate(ctx => ctx.AutoOrient());
            var (width, height) = HalfSize(image.Width, image.Height);
            image.Mutate(ctx =>
            {
                // P3: the exact target size (no box, no shrink-only test), default Bicubic resampler.
                ctx.Resize(new ResizeOptions { Size = new Size(width, height), Mode = ResizeMode.Stretch });
                // P4: R, G and B x1.25 on the 8-bit values, clamped; no sharpen step (P5).
                ctx.Brightness(1.25f);
            });
            // P6: JFIF density 72x72 dots per inch, and no other metadata (owner Q-A 2026-10-20: "strip metadata ... match
            // INSPECT"): INSPECT's redraw keeps no EXIF (camera, time, GPS), XMP, IPTC or ICC. The pixels were oriented
            // above (P2) before the tag goes, so the photo stays upright.
            StripMetadata(image);
            image.Metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            image.Metadata.HorizontalResolution = Dpi;
            image.Metadata.VerticalResolution = Dpi;
            using var output = new MemoryStream();
            image.Save(output, new JpegEncoder { Quality = JpegQuality }); // P7
            return output.ToArray();
        }

        // P6 (owner Q-A): the profiles that can carry camera, time, place or author data are dropped. Call only after
        // AutoOrient, which needs the EXIF Orientation.
        public static void StripMetadata(Image image)
        {
            image.Metadata.ExifProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.IccProfile = null;
        }
    }
}
