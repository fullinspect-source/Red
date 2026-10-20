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

        public CameraPreviewRenderer(Image target, Action<string>? log = null)
        {
            _target = target;
            _log = log ?? (_ => { });
        }

        public void Push(CameraPreviewFrame frame)
        {
            Interlocked.Exchange(ref _pending, frame);
            if (Interlocked.Exchange(ref _queued, 1) == 0)
                _target.Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)Render);
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
                    _target.Source = _bitmap;
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
