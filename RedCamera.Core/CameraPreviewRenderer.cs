using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RedCamera.Core
{
    /// <summary>
    /// Shows <see cref="CameraSessionController.PreviewFrame"/> frames in a WPF <see cref="Image"/>. <see cref="Push"/> may
    /// be called from any thread; only the newest frame is rendered, on the image's dispatcher.
    /// </summary>
    public sealed class CameraPreviewRenderer
    {
        private readonly Image _target;
        private readonly Action<string> _log;
        private CameraPreviewFrame? _pending;
        private int _queued;
        private WriteableBitmap? _bitmap;
        private int _errorLogged;
        private double _zoom = 1.0;

        public CameraPreviewRenderer(Image target, Action<string>? log = null)
        {
            _target = target;
            _log = log ?? (_ => { });
        }

        /// <summary>The digital zoom the preview shows (1 = whole frame).</summary>
        public double Zoom => _zoom;

        public void Push(CameraPreviewFrame frame)
        {
            Interlocked.Exchange(ref _pending, frame);
            if (Interlocked.Exchange(ref _queued, 1) == 0)
                _target.Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)Render);
        }

        /// <summary>
        /// F-027 Z2 digital zoom: the image shows only the centred <see cref="CameraZoom.CropFor"/> region of each frame,
        /// in the same space, which is the region the controller crops the still to. Pass
        /// <see cref="CameraSessionController.PreviewZoom"/> (1 when the camera zooms by itself). Call on the image's thread;
        /// the change shows at once, without waiting for the next frame.
        /// </summary>
        public void SetZoom(double factor)
        {
            double zoom = double.IsFinite(factor) ? Math.Max(1.0, factor) : 1.0;
            if (zoom == _zoom) return;
            _zoom = zoom;
            ShowBitmap();
        }

        private void ShowBitmap()
        {
            if (_bitmap == null) return;
            if (_zoom <= 1.0)
            {
                _target.Source = _bitmap;
                return;
            }
            // The crop is drawn from the live bitmap, so every frame written to it shows without rebuilding this.
            int width = _bitmap.PixelWidth, height = _bitmap.PixelHeight;
            var crop = CameraZoom.CropFor(width, height, _zoom);
            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)) };
            group.Children.Add(new ImageDrawing(_bitmap, new Rect(-crop.X, -crop.Y, width, height)));
            _target.Source = new DrawingImage(group);
        }

        private void Render()
        {
            Interlocked.Exchange(ref _queued, 0);
            var frame = Interlocked.Exchange(ref _pending, null);
            if (frame == null || !_target.IsLoaded) return;
            try
            {
                if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                {
                    // Bgr32: the camera's alpha byte is ignored (it can be 0).
                    _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    ShowBitmap();
                }
                _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _errorLogged, 1) == 0)
                    _log($"Camera preview render failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
