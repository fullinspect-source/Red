using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-011 ink surface shared by the footer pad (770x105) and the Settings capture window (770x91): white background,
    // blue ink (G3: RED uses blue always), whole-surface render at 96 dpi.
    public sealed class SignatureSurface : Grid
    {
        private readonly InkCanvas _ink;
        public int PixelWidth { get; }
        public int PixelHeight { get; }
        public event EventHandler? StrokeFinished;

        public SignatureSurface(int width, int height)
        {
            PixelWidth = width;
            PixelHeight = height;
            Width = width;
            Height = height;
            Background = Brushes.White;
            ClipToBounds = true;
            _ink = new InkCanvas { Width = width, Height = height, Background = Brushes.Transparent };
            _ink.DefaultDrawingAttributes.Color = Color.FromRgb(0, 0, 255);
            _ink.StrokeCollected += (_, _) => StrokeFinished?.Invoke(this, EventArgs.Empty);
            Children.Add(_ink);
        }

        public bool IsEnabledForInk
        {
            get => _ink.IsEnabled;
            set => _ink.IsEnabled = value;
        }

        public bool HasContent => _ink.Strokes.Count > 0 || _ink.Children.Count > 0;

        // E2: an image at the top-left at its natural size (no stretch), on top of what is already there.
        public void AddImage(BitmapSource image)
        {
            var element = new Image
            {
                Source = image,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                Stretch = Stretch.None,
                IsHitTestVisible = false
            };
            InkCanvas.SetLeft(element, 0);
            InkCanvas.SetTop(element, 0);
            _ink.Children.Add(element);
        }

        public void Clear()
        {
            _ink.Strokes.Clear();
            _ink.Children.Clear();
        }

        public void AddStroke(params Point[] points)
        {
            var stroke = new Stroke(new StylusPointCollection(points.Select(p => new StylusPoint(p.X, p.Y))), _ink.DefaultDrawingAttributes.Clone());
            _ink.Strokes.Add(stroke);
            StrokeFinished?.Invoke(this, EventArgs.Empty);
        }

        public BitmapSource Render()
        {
            if (!IsLoaded || ActualWidth <= 0)
            {
                Measure(new Size(PixelWidth, PixelHeight));
                Arrange(new Rect(0, 0, PixelWidth, PixelHeight));
            }
            UpdateLayout();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, PixelWidth, PixelHeight));
                context.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, PixelWidth, PixelHeight));
            }
            var bitmap = new RenderTargetBitmap(PixelWidth, PixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        public string RenderJpegBase64() => InkItemService.EncodeJpegBase64(Render());

        public byte[] RenderPng()
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render()));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }

    // G3: Settings window "Signature" (770x91, Clear and Save). Save stores the signature at once (G2).
    public sealed class SignatureCaptureWindow : Window
    {
        private readonly SignatureSurface _surface = new(InspectorSignatureStore.SavedWidth, InspectorSignatureStore.SavedHeight);
        private readonly InspectorSignatureStore _store;
        public bool Saved { get; private set; }

        public SignatureCaptureWindow(InspectorSignatureStore store)
        {
            _store = store;
            Title = "Signature";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try
            {
                var existing = store.LoadImage();
                if (existing != null)
                    _surface.AddImage(existing);
            }
            catch (InvalidDataException)
            {
                // An unreadable saved signature: start blank; Save replaces it.
            }

            var clear = new Button { Content = "Clear", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
            clear.Click += (_, _) => _surface.Clear();
            var save = new Button { Content = "Save", Padding = new Thickness(18, 4, 18, 4), IsDefault = true };
            save.Click += (_, _) => SaveAndClose();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(clear);
            buttons.Children.Add(save);
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = "Sign in the box. Your saved signature is used for \"Use Saved Signature\".", Margin = new Thickness(0, 0, 0, 6) });
            root.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _surface, HorizontalAlignment = HorizontalAlignment.Left });
            root.Children.Add(buttons);
            Content = root;
        }

        public SignatureSurface Surface => _surface;

        public void SaveAndClose()
        {
            if (!_surface.HasContent)
            {
                MessageBox.Show(this, "Sign in the box first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                _store.Save(_surface.RenderPng(), Convert.FromBase64String(_surface.RenderJpegBase64()));
                Saved = true;
                try { DialogResult = true; }        // shown as a dialog
                catch (InvalidOperationException) { Close(); }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DiagnosticLogService.Log("Could not save the inspector signature", ex);
                MessageBox.Show(this, "RED could not save the signature: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
