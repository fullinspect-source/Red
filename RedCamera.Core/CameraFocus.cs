using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RedCamera.Core
{
    /// <summary>F-027 T1: what a camera session can do with a tap.</summary>
    /// <param name="Regions">Regions of interest are supported (the camera meters the tapped area).</param>
    /// <param name="Focus">The region drives auto-focus.</param>
    /// <param name="Exposure">The region drives auto-exposure.</param>
    /// <param name="WhiteBalance">The region drives auto white balance.</param>
    /// <param name="SingleFocus">The camera can focus once on request (FocusMode.Single).</param>
    /// <param name="ContinuousFocus">The camera can go back to continuous auto-focus.</param>
    public sealed record FocusCapabilities(bool Regions, bool Focus, bool Exposure, bool WhiteBalance, bool SingleFocus,
        bool ContinuousFocus)
    {
        /// <summary>Nothing: a tap only shows the crosshair.</summary>
        public static readonly FocusCapabilities None = new(false, false, false, false, false, false);

        /// <summary>True when a tap can change what the camera does: it meters regions. Single focus alone is not enough:
        /// without a region it focuses on the camera's own area (usually the centre), not where the user tapped.</summary>
        public bool Supported => Regions;

        /// <summary>A tap also triggers one focus run on the region (the region drives auto-focus and single focus exists).</summary>
        public bool FocusOnTap => Regions && Focus && SingleFocus;
    }

    /// <summary>A region of the full camera frame, normalized to 0..1 (x right, y down).</summary>
    public readonly record struct FocusRegion(double X, double Y, double Width, double Height);

    /// <summary>
    /// F-027 T1-T2: from a tap on the shown preview to a region of the camera frame. The preview is shown as the frame
    /// (cropped by the digital zoom), mirrored for a front camera, then turned clockwise (the screen's LayoutTransform:
    /// mirror first, then rotation), and fitted into the image's box.
    /// </summary>
    public static class CameraFocus
    {
        /// <summary>The side of a tap region, as a part of the visible picture's shorter side.</summary>
        public const double RegionSize = 0.15;

        /// <summary>
        /// A point on the shown picture (0..1 across and down its box on screen) back to the point of the picture before it
        /// was turned and mirrored (0..1 across and down the frame as the camera delivers it, cropped by the zoom).
        /// </summary>
        public static (double U, double V) DisplayToSource(double displayX, double displayY, double rotationClockwise, bool mirrored)
        {
            double p = displayX - 0.5, q = displayY - 0.5;
            int quarter = ((int)Math.Round(rotationClockwise / 90.0) % 4 + 4) % 4;
            (double a, double b) = quarter switch
            {
                1 => (q, -p),   // turned 90: the picture's right edge is at the bottom
                2 => (-p, -q),
                3 => (-q, p),
                _ => (p, q),
            };
            if (mirrored) a = -a;
            return (a + 0.5, b + 0.5);
        }

        /// <summary>The other way round from <see cref="DisplayToSource"/>: a point of the picture as delivered to where it
        /// shows on screen (0..1 of the shown box), mirrored first, then turned clockwise.</summary>
        public static (double X, double Y) SourceToDisplay(double u, double v, double rotationClockwise, bool mirrored)
        {
            double a = u - 0.5, b = v - 0.5;
            if (mirrored) a = -a;
            int quarter = ((int)Math.Round(rotationClockwise / 90.0) % 4 + 4) % 4;
            (double p, double q) = quarter switch
            {
                1 => (-b, a),
                2 => (-a, -b),
                3 => (b, -a),
                _ => (a, b),
            };
            return (p + 0.5, q + 0.5);
        }

        /// <summary>A point of the zoomed (centred crop) picture to the same point of the full frame.</summary>
        public static (double X, double Y) SourceToFrame(double u, double v, double previewZoom)
        {
            double z = double.IsFinite(previewZoom) ? Math.Max(1.0, previewZoom) : 1.0;
            return (0.5 + (u - 0.5) / z, 0.5 + (v - 0.5) / z);
        }

        /// <summary>A tap on the shown picture to the point of the full frame (<see cref="DisplayToSource"/> then
        /// <see cref="SourceToFrame"/>).</summary>
        public static (double X, double Y) TapToFrame(double displayX, double displayY, double rotationClockwise, bool mirrored,
            double previewZoom)
        {
            var (u, v) = DisplayToSource(displayX, displayY, rotationClockwise, mirrored);
            return SourceToFrame(u, v, previewZoom);
        }

        /// <summary>
        /// The square (in pixels) region around a frame point that the camera meters: <see cref="RegionSize"/> of the visible
        /// picture's shorter side, so it covers the same area on screen at any zoom, moved inside the frame at the edges.
        /// </summary>
        public static FocusRegion RegionAround(double x, double y, double previewZoom, double frameAspect)
        {
            double z = double.IsFinite(previewZoom) ? Math.Max(1.0, previewZoom) : 1.0;
            double aspect = double.IsFinite(frameAspect) && frameAspect > 0 ? frameAspect : 16.0 / 9.0;
            double side = RegionSize / z;                       // of the shorter side
            double w = aspect >= 1 ? side / aspect : side;       // normalized to the frame's width
            double h = aspect >= 1 ? side : side * aspect;       // normalized to the frame's height
            double left = Math.Clamp(x - w / 2, 0, 1 - w);
            double top = Math.Clamp(y - h / 2, 0, 1 - h);
            return new FocusRegion(left, top, w, h);
        }

        /// <summary>
        /// WPF helper: a point on <paramref name="surface"/> (an ancestor of the preview image) to the full frame. False when
        /// the point is outside the shown picture (the bars around it) or the image has no size yet.
        /// </summary>
        public static bool TryMapTap(Image preview, UIElement surface, Point pointOnSurface, double rotationClockwise, bool mirrored,
            double previewZoom, out double x, out double y)
        {
            x = y = 0.5;
            if (preview.RenderSize.Width <= 0 || preview.RenderSize.Height <= 0 || !preview.IsDescendantOf(surface)) return false;
            Rect box = preview.TransformToAncestor(surface).TransformBounds(new Rect(preview.RenderSize));
            if (box.Width <= 0 || box.Height <= 0 || !box.Contains(pointOnSurface)) return false;
            (x, y) = TapToFrame((pointOnSurface.X - box.X) / box.Width, (pointOnSurface.Y - box.Y) / box.Height,
                rotationClockwise, mirrored, previewZoom);
            return true;
        }
    }

    /// <summary>
    /// F-027 K1: one photo per press of a shutter key (Volume Up from a Bluetooth camera remote, Enter, Space). Key repeat
    /// while held and presses within <see cref="MinIntervalMs"/> of the last accepted one take nothing.
    /// </summary>
    public sealed class ShutterKeyGate
    {
        private int _lastAccepted;
        private bool _accepted;

        public int MinIntervalMs { get; init; } = 400;

        /// <summary>A key press (<paramref name="timestampMs"/> from the input event). True = take one photo.</summary>
        public bool Press(bool isRepeat, int timestampMs) => !isRepeat && Pulse(timestampMs);

        /// <summary>A press without a key-up (an app command from a remote). True = take one photo.</summary>
        public bool Pulse(int timestampMs)
        {
            int since = unchecked(timestampMs - _lastAccepted);
            if (_accepted && since >= 0 && since < MinIntervalMs) return false;
            _accepted = true;
            _lastAccepted = timestampMs;
            return true;
        }
    }
}
