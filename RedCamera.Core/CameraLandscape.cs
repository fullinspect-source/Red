using System;
using System.Windows;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 L1 (owner 2026-10-22): saved photos are always landscape, whichever way the tablet is held. A photo that would
    /// be shown portrait (the tablet held upright) keeps its centred landscape band: its full width as shown, and a height
    /// that gives it the camera's own landscape shape (a 9:16 portrait becomes a 16:9 picture, 4:3 stays 4:3). The pixels
    /// are cut in their stored orientation and the EXIF orientation is kept, so the picture is upright as before.
    /// </summary>
    public static class CameraLandscape
    {
        /// <summary>True when the EXIF orientation turns the stored picture a quarter turn (5-8).</summary>
        public static bool QuarterTurn(int exifOrientation) => exifOrientation is >= 5 and <= 8;

        /// <summary>The size of a stored W x H picture as shown (after its EXIF orientation).</summary>
        public static (int Width, int Height) Upright(int storedWidth, int storedHeight, int exifOrientation) =>
            QuarterTurn(exifOrientation) ? (storedHeight, storedWidth) : (storedWidth, storedHeight);

        /// <summary>
        /// The part of <paramref name="within"/> (stored pixels, for example the whole picture or a zoom crop) to keep so
        /// that the picture, shown upright, is landscape: <paramref name="within"/> itself when it already is.
        /// </summary>
        public static ZoomCrop RegionFor(ZoomCrop within, int exifOrientation)
        {
            var (shownWidth, shownHeight) = Upright(within.Width, within.Height, exifOrientation);
            if (shownWidth <= 0 || shownHeight <= shownWidth) return within;
            int band = Math.Max(1, (int)Math.Round((double)shownWidth * shownWidth / shownHeight)); // shown height kept
            if (QuarterTurn(exifOrientation))
            {
                // The shown height runs along the stored width.
                int w = Math.Min(within.Width, band);
                return new ZoomCrop(within.X + (within.Width - w) / 2, within.Y, w, within.Height);
            }
            int h = Math.Min(within.Height, band);
            return new ZoomCrop(within.X, within.Y + (within.Height - h) / 2, within.Width, h);
        }

        /// <summary>
        /// The simple case of the framing guide: in a shown picture box that is portrait, the centred landscape band of a
        /// photo with the preview's own shape; null when the picture is already landscape. Screens use
        /// <see cref="KeptOnScreen"/>, which also handles a photo of another shape and a tablet whose screen does not turn.
        /// </summary>
        public static Rect? Band(Rect shown)
        {
            if (shown.Width <= 0 || shown.Height <= shown.Width) return null;
            double height = shown.Width * shown.Width / shown.Height;
            return new Rect(shown.X, shown.Y + (shown.Height - height) / 2, shown.Width, height);
        }

        /// <summary>
        /// L2: the part of the shown preview (0..1 across and down the shown picture) that the next photo keeps when it is
        /// cut to landscape, or null when it will not be cut. It is worked out from the cut itself: the photo's stored size
        /// and the EXIF orientation the next press uses (from the orientation sensor, which may differ from how the screen
        /// is turned), the digital zoom, and the preview's rotation and mirroring. The photo and the preview frame are taken
        /// to share the field of view along their long side, centred (a 4:3 photo shows more above and below a 16:9
        /// preview); the result is clipped to what the preview shows.
        /// </summary>
        public static Rect? KeptOnScreen(int photoWidth, int photoHeight, int exifOrientation, int previewWidth, int previewHeight,
            double previewZoom, double rotationClockwise, bool mirrored)
        {
            if (photoWidth <= 0 || photoHeight <= 0) (photoWidth, photoHeight) = (previewWidth, previewHeight);
            if (photoWidth <= 0 || photoHeight <= 0 || previewWidth <= 0 || previewHeight <= 0) return null;
            double z = double.IsFinite(previewZoom) ? Math.Max(1.0, previewZoom) : 1.0;
            var zoomed = CameraZoom.CropFor(photoWidth, photoHeight, z);
            var keep = RegionFor(zoomed, exifOrientation);
            if (keep == zoomed) return null;

            // Photo → preview frame (shared long side), → shown picture (the zoom's centred crop), → screen (mirror, turn).
            double sx = 1, sy = 1;
            if (photoWidth >= photoHeight && previewWidth >= previewHeight)
                sy = ((double)photoHeight / photoWidth) / ((double)previewHeight / previewWidth);
            else if (photoWidth < photoHeight && previewWidth < previewHeight)
                sx = ((double)photoWidth / photoHeight) / ((double)previewWidth / previewHeight);
            Point Map(double x, double y)
            {
                double u = 0.5 + ((0.5 + (x - 0.5) * sx) - 0.5) * z;
                double v = 0.5 + ((0.5 + (y - 0.5) * sy) - 0.5) * z;
                var (dx, dy) = CameraFocus.SourceToDisplay(u, v, rotationClockwise, mirrored);
                return new Point(dx, dy);
            }
            var a = Map((double)keep.X / photoWidth, (double)keep.Y / photoHeight);
            var b = Map((double)(keep.X + keep.Width) / photoWidth, (double)(keep.Y + keep.Height) / photoHeight);
            double left = Math.Clamp(Math.Min(a.X, b.X), 0, 1), right = Math.Clamp(Math.Max(a.X, b.X), 0, 1);
            double top = Math.Clamp(Math.Min(a.Y, b.Y), 0, 1), bottom = Math.Clamp(Math.Max(a.Y, b.Y), 0, 1);
            return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : null;
        }
    }
}
