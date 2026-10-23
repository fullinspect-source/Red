using System;
using System.Globalization;
using System.Threading.Tasks;

namespace RedCamera.Core
{
    /// <summary>F-027 Z1: what a camera session can zoom by itself. Without it the engine zooms digitally.</summary>
    public sealed record ZoomCapabilities(bool Hardware, double Min, double Max, double Step)
    {
        /// <summary>No zoom in the camera: the engine crops, from 1x to <see cref="CameraZoom.DigitalMax"/>.</summary>
        public static readonly ZoomCapabilities DigitalOnly = new(false, 1.0, CameraZoom.DigitalMax, 0.0);
    }

    /// <summary>A centred region of a W x H image, in pixels.</summary>
    public readonly record struct ZoomCrop(int X, int Y, int Width, int Height);

    /// <summary>F-027 Z2: crops a still to the centred region a digital zoom shows.</summary>
    public interface ICameraStillCropper
    {
        /// <summary>The still cropped to <see cref="CameraZoom.CropFor"/> of its own size, at the crop's own pixel size
        /// (never scaled up), with its EXIF orientation kept and <see cref="CapturedStill.Zoom"/> set.</summary>
        Task<CapturedStill> CropAsync(CapturedStill still, double factor);

        /// <summary>F-027 L1 (1.3.0): the still cropped to <paramref name="region"/> of its stored pixels, at the region's
        /// own size, EXIF orientation kept. A cropper without it fails, and the engine then keeps the full photo.</summary>
        Task<CapturedStill> CropToAsync(CapturedStill still, ZoomCrop region) =>
            Task.FromException<CapturedStill>(new NotSupportedException("This cropper only crops to a zoom."));
    }

    /// <summary>F-027 Z1-Z3: the zoom rules shared by the engine and the screens that use it.</summary>
    public static class CameraZoom
    {
        /// <summary>The digital zoom limit (owner request 2026-10-21: about 1x to 4x).</summary>
        public const double DigitalMax = 4.0;

        /// <summary>A pinch that ends below this goes back to exactly 1x.</summary>
        public const double SnapToOneBelow = 1.05;

        /// <summary>The factor kept within 1x..<paramref name="max"/>; within 0.5% of 1x it is exactly 1x.</summary>
        public static double Clamp(double factor, double max)
        {
            if (!double.IsFinite(factor)) return 1.0;
            double limit = double.IsFinite(max) ? Math.Max(1.0, max) : 1.0;
            double zoom = Math.Min(limit, Math.Max(1.0, factor));
            return zoom < 1.005 ? 1.0 : zoom;
        }

        /// <summary>
        /// The centred region a digital zoom of <paramref name="factor"/> keeps of a <paramref name="width"/> x
        /// <paramref name="height"/> image: each side divided by the factor (rounded, at least 1 pixel). It is the same
        /// part of the picture at any image size, so the still is cropped to what the preview showed.
        /// </summary>
        public static ZoomCrop CropFor(int width, int height, double factor)
        {
            if (width <= 0 || height <= 0) return new ZoomCrop(0, 0, Math.Max(0, width), Math.Max(0, height));
            double zoom = double.IsFinite(factor) ? Math.Max(1.0, factor) : 1.0;
            int w = Math.Clamp((int)Math.Round(width / zoom), 1, width);
            int h = Math.Clamp((int)Math.Round(height / zoom), 1, height);
            return new ZoomCrop((width - w) / 2, (height - h) / 2, w, h);
        }

        /// <summary>The zoom readout, for example "2.0×"; empty at 1x.</summary>
        public static string Readout(double factor) =>
            factor > 1.0 ? factor.ToString("0.0", CultureInfo.InvariantCulture) + "×" : string.Empty;

        /// <summary>One mouse-wheel notch (delta 120) zooms in or out by 10%. Zooming out to below
        /// <see cref="SnapToOneBelow"/> gives exactly 1x (a touchpad pinch sends many small steps).</summary>
        public static double AfterWheel(double factor, int delta, double max)
        {
            double zoom = Clamp(factor * Math.Pow(1.1, delta / 120.0), max);
            return delta < 0 && zoom < SnapToOneBelow ? 1.0 : zoom;
        }
    }

    /// <summary>
    /// F-027 Z4: one pinch gesture. The scale changes are multiplied up unrounded, so a slow pinch still leaves 1x, and
    /// held at the limits, so pinching back in from the maximum responds at once.
    /// </summary>
    public sealed class ZoomGesture
    {
        private double _raw = 1.0;

        public void Begin(double currentZoom) => _raw = double.IsFinite(currentZoom) ? Math.Max(1.0, currentZoom) : 1.0;

        /// <summary>The zoom after a scale change of <paramref name="scaleDelta"/> (1 = no change).</summary>
        public double Update(double scaleDelta, double max)
        {
            if (scaleDelta > 0 && double.IsFinite(scaleDelta))
                _raw = Math.Min(Math.Max(1.0, max), Math.Max(1.0, _raw * scaleDelta));
            return CameraZoom.Clamp(_raw, max);
        }

        /// <summary>The zoom to keep when the fingers lift: below <see cref="CameraZoom.SnapToOneBelow"/> it is 1x.</summary>
        public static double End(double zoom) => zoom < CameraZoom.SnapToOneBelow ? 1.0 : zoom;
    }
}
