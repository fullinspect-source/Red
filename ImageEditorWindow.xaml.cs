using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.PixelFormats;

namespace InspectionEditor
{
    // Photo editor: RED's adjustments (lift, sharpness, straighten) plus F-023 T2 markup with INSPECT's tools: four pen
    // colours (the last one remembered as SketchPenColor in RED's profile), number stamps 1-9 with auto-advance, select and
    // move, per-action Undo and Redo. Done flattens the markup at the photo's own pixel size; the caller keeps the
    // picture's Caption and Description. The editor never re-applies the new-photo processing (F-023 T1).
    public partial class ImageEditorWindow : Window
    {
        private enum Tool { Adjust, Draw, Select, Label }

        private sealed record StampState(int Number, string Color, double Left, double Top);

        private sealed class Snapshot
        {
            public StrokeCollection Strokes = new();
            public List<StampState> Stamps = new();
            public int NextLabel;
        }

        // INSPECT's photo editor colours, in its order (I6).
        public static readonly string[] PenColorNames = { "Red", "Blue", "Black", "Green" };
        private const double StampSize = 30;

        // Original image data (for undo)
        private byte[] _originalImageBytes;
        private byte[] _currentImageBytes;

        // Adjustment values
        private float _gamma = 1f;       // 0.5 (brighter) to 2.0 (darker), 1.0 = no change
        private float _sharpness = 0f;   // 0 to 4 range
        private float _rotation = 0f;    // degrees, -15 to +15

        // Gesture tracking
        private System.Windows.Point? _gestureStartPoint;
        private float _gestureStartGamma;
        private float _gestureStartSharpness;
        private bool _isGesturing = false;

        // Markup (F-023 T2)
        private Tool _tool = Tool.Adjust;
        private string _penColor;
        private int _nextLabel = 1;
        private bool _restoring;
        private readonly List<Snapshot> _history = new();
        private int _historyIndex;
        private readonly Action<string> _savePenColor;
        private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
        private readonly Dictionary<string, ToggleButton> _colorButtons = new();
        private readonly ComboBox _labelPicker = new() { Width = 48, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _undoButton = ToolButton("↶ Undo");
        private readonly Button _redoButton = ToolButton("↷ Redo");

        // Result
        public bool ImageWasEdited { get; private set; } = false;
        public byte[] EditedImageBytes { get; private set; }

        // loadPenColor/savePenColor default to RED's profile (SketchPenColor); tests pass their own.
        public ImageEditorWindow(byte[] imageBytes, Func<string?>? loadPenColor = null, Action<string>? savePenColor = null)
        {
            InitializeComponent();

            _originalImageBytes = imageBytes;
            _currentImageBytes = imageBytes;
            EditedImageBytes = imageBytes;
            _savePenColor = savePenColor ?? InkItemService.RememberPenColor;
            // I6: the remembered colour, else INSPECT's starting Red.
            string? remembered = loadPenColor != null ? loadPenColor() : InspectorProfileService.Load()?.SketchPenColor;
            _penColor = PenColorNames.Contains(remembered) ? remembered! : "Red";

            MarkupCanvas.DefaultDrawingAttributes.Color = InkItemService.PenColor(_penColor);
            MarkupCanvas.StrokeCollected += (_, _) => Commit();
            MarkupCanvas.SelectionMoved += (_, _) => Commit();
            MarkupCanvas.SelectionResized += (_, _) => Commit();
            MarkupCanvas.StrokeErased += (_, _) => Commit();
            MarkupCanvas.ResizeEnabled = false;                              // a stamp is always 30 x 30 (I6)
            MarkupCanvas.EditingModeInverted = InkCanvasEditingMode.None;    // no pen-end eraser outside Undo
            foreach (var command in new[] { ApplicationCommands.Cut, ApplicationCommands.Copy, ApplicationCommands.Paste })
                MarkupCanvas.CommandBindings.Add(new CommandBinding(command, (_, e) => e.Handled = true, (_, e) => { e.CanExecute = false; e.Handled = true; }));
            MarkupCanvas.PreviewKeyDown += (_, e) =>
            {
                // Delete in Select mode removes the selection; record it as one action once InkCanvas has removed it.
                if (e.Key is Key.Delete or Key.Back && _tool == Tool.Select)
                    Dispatcher.BeginInvoke(new Action(Commit), System.Windows.Threading.DispatcherPriority.Background);
            };
            MarkupCanvas.PreviewMouseLeftButtonUp += MarkupCanvas_PreviewMouseLeftButtonUp;
            BuildToolbar();
            _history.Add(TakeSnapshot());
            UpdateHistoryButtons();

            LoadImageToDisplay(imageBytes);
            UpdateAdjustmentText();
        }

        private void LoadImageToDisplay(byte[] imageBytes)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(imageBytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            PhotoImage.Source = bitmap;
        }

        private void UpdateAdjustmentText()
        {
            // Gamma: 1.0 = neutral, <1 = brighter, >1 = darker
            // Display as "lift" percentage (how much shadows are lifted)
            int liftPercent = (int)((1f - _gamma) * 100);
            int sharpnessPercent = (int)(_sharpness * 50);

            BrightnessText.Text = $"Lift: {(liftPercent >= 0 ? "+" : "")}{liftPercent}%";
            SharpnessText.Text = $"Sharpness: +{sharpnessPercent}%";

            BrightnessText.Foreground = _gamma != 1f
                ? new SolidColorBrush(Colors.Gold)
                : new SolidColorBrush(Colors.Gray);
            SharpnessText.Foreground = _sharpness != 0
                ? new SolidColorBrush(Colors.DeepSkyBlue)
                : new SolidColorBrush(Colors.Gray);

            RotationText.Text = _rotation == 0f ? "Rotate: 0°" : $"Rotate: {_rotation:+0.#;-0.#}°";
            RotationText.Foreground = _rotation != 0f
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(144, 238, 144))
                : new SolidColorBrush(Colors.Gray);
        }

        private void ApplyAdjustmentsPreview()
        {
            if (_gamma == 1f && _sharpness == 0 && _rotation == 0f)
            {
                _currentImageBytes = _originalImageBytes;
                LoadImageToDisplay(_originalImageBytes);
                return;
            }

            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(_originalImageBytes);

                image.Mutate(ctx =>
                {
                    if (_rotation != 0f)
                        ctx.Rotate(_rotation);

                    // Brightness with smaller range + contrast to preserve some depth
                    if (_gamma != 1f)
                    {
                        float lift = (1f - _gamma) * 1.0f;
                        ctx.Brightness(1f + lift);
                        if (lift > 0)
                            ctx.Contrast(1f + (lift * 0.2f));
                    }

                    if (_sharpness > 0)
                        ctx.GaussianSharpen(_sharpness);
                });

                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms);
                _currentImageBytes = ms.ToArray();

                LoadImageToDisplay(_currentImageBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Image processing error: {ex.Message}");
            }
        }

        #region Gesture Handling

        private void ImageContainer_TouchDown(object sender, TouchEventArgs e)
        {
            if (_tool != Tool.Adjust)
                return; // Let InkCanvas handle markup

            _gestureStartPoint = e.GetTouchPoint(ImageContainer).Position;
            _gestureStartGamma = _gamma;
            _gestureStartSharpness = _sharpness;
            _isGesturing = true;
            e.Handled = true;
        }

        private void ImageContainer_TouchMove(object sender, TouchEventArgs e)
        {
            if (!_isGesturing || !_gestureStartPoint.HasValue)
                return;

            var currentPoint = e.GetTouchPoint(ImageContainer).Position;
            ProcessGesture(_gestureStartPoint.Value, currentPoint);
            e.Handled = true;
        }

        private void ImageContainer_TouchUp(object sender, TouchEventArgs e)
        {
            _isGesturing = false;
            _gestureStartPoint = null;
        }

        // Mouse fallback for testing
        private void ImageContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_tool != Tool.Adjust || e.LeftButton != MouseButtonState.Pressed)
                return;

            _gestureStartPoint = e.GetPosition(ImageContainer);
            _gestureStartGamma = _gamma;
            _gestureStartSharpness = _sharpness;
            _isGesturing = true;
            ImageContainer.CaptureMouse();
        }

        private void ImageContainer_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isGesturing || !_gestureStartPoint.HasValue)
                return;

            var currentPoint = e.GetPosition(ImageContainer);
            ProcessGesture(_gestureStartPoint.Value, currentPoint);
        }

        private void ImageContainer_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _isGesturing = false;
            _gestureStartPoint = null;
            ImageContainer.ReleaseMouseCapture();
        }

        private void ProcessGesture(System.Windows.Point start, System.Windows.Point current)
        {
            // Vertical movement = gamma/lift (up = brighter/lower gamma)
            double deltaY = start.Y - current.Y;
            // Horizontal movement = sharpness (right = sharper)
            double deltaX = current.X - start.X;

            // Gamma: swiping up 200px goes from 1.0 to 0.5 (brighter)
            // Gamma range: 0.4 (very bright) to 1.5 (darker)
            float gammaChange = (float)(deltaY / 400.0);  // Slower adjustment
            float sharpnessChange = (float)(deltaX / 100.0);

            _gamma = Math.Clamp(_gestureStartGamma - gammaChange, 0.4f, 1.5f);
            _sharpness = Math.Clamp(_gestureStartSharpness + sharpnessChange, 0f, 4f);

            UpdateAdjustmentText();
            ApplyAdjustmentsPreview();
        }

        #endregion

        #region Markup (F-023 T2)

        private static Button ToolButton(string text) => new()
        {
            Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2, 0, 2, 0), FontSize = 13,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(85, 85, 85)), Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };

        private void BuildToolbar()
        {
            foreach (var (tool, text) in new[] { (Tool.Adjust, "☀ Adjust"), (Tool.Draw, "✏️ Draw"), (Tool.Select, "⬚ Select"), (Tool.Label, "① Label") })
            {
                var button = new ToggleButton
                {
                    Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2, 0, 2, 0), FontSize = 13,
                    Foreground = Brushes.White, BorderThickness = new Thickness(0), IsChecked = tool == _tool
                };
                button.Click += (_, _) => SetTool(tool);
                _toolButtons[tool] = button;
                ToolPanel.Children.Add(button);
            }
            for (int n = 1; n <= 9; n++)
                _labelPicker.Items.Add(n);
            _labelPicker.SelectedItem = _nextLabel;
            _labelPicker.SelectionChanged += (_, _) =>
            {
                if (!_restoring && _labelPicker.SelectedItem is int n)
                    _nextLabel = n;
            };
            ToolPanel.Children.Add(_labelPicker);
            foreach (string name in PenColorNames)
            {
                var button = new ToggleButton
                {
                    Content = name, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(2, 0, 2, 0), FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = name == "Black" ? Brushes.White : new SolidColorBrush(InkItemService.PenColor(name)),
                    Background = name == "Black" ? Brushes.Black : new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
                    BorderThickness = new Thickness(0), IsChecked = name == _penColor
                };
                button.Click += (_, _) => SetPenColor(name, remember: true);
                _colorButtons[name] = button;
                ToolPanel.Children.Add(button);
            }
            _undoButton.Click += (_, _) => Undo();
            _redoButton.Click += (_, _) => Redo();
            ToolPanel.Children.Add(_undoButton);
            ToolPanel.Children.Add(_redoButton);
            UpdateToolButtons();
        }

        private void UpdateToolButtons()
        {
            foreach (var pair in _toolButtons)
            {
                pair.Value.IsChecked = pair.Key == _tool;
                pair.Value.Background = new SolidColorBrush(pair.Key == _tool ? System.Windows.Media.Color.FromRgb(192, 57, 43) : System.Windows.Media.Color.FromRgb(85, 85, 85));
            }
            foreach (var pair in _colorButtons)
            {
                pair.Value.IsChecked = pair.Key == _penColor;
                pair.Value.BorderThickness = new Thickness(pair.Key == _penColor ? 3 : 0);
                pair.Value.BorderBrush = Brushes.Gold;
            }
        }

        public string PenColorName => _penColor;
        public int NextLabelNumber => _nextLabel;
        public int StrokeCount => MarkupCanvas.Strokes.Count;
        public IReadOnlyList<int> StampNumbers => CurrentStamps().Select(s => s.Number).ToList();

        public void SetTool(string tool) => SetTool(Enum.Parse<Tool>(tool));

        private void SetTool(Tool tool)
        {
            _tool = tool;
            MarkupCanvas.EditingMode = tool switch
            {
                Tool.Draw => InkCanvasEditingMode.Ink,
                Tool.Select => InkCanvasEditingMode.Select,
                _ => InkCanvasEditingMode.None
            };
            UpdateToolButtons();
        }

        public void SetPenColor(string name, bool remember)
        {
            if (!PenColorNames.Contains(name)) return;
            _penColor = name;
            MarkupCanvas.DefaultDrawingAttributes.Color = InkItemService.PenColor(name);
            UpdateToolButtons();
            if (remember) _savePenColor(name);
        }

        private void MarkupCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_tool != Tool.Label)
                return;
            PlaceLabel(e.GetPosition(MarkupCanvas));
            e.Handled = true;
        }

        // I6: each tap places the current number (30 x 30 at the tap) and advances; 9 wraps to 1.
        public void PlaceLabel(System.Windows.Point point)
        {
            AddStamp(new StampState(_nextLabel, _penColor, point.X - StampSize / 2, point.Y - StampSize / 2));
            _nextLabel = InkItemService.NextLabel(_nextLabel);
            SyncLabelPicker();
            Commit();
        }

        public void StartLabelsAt(int number)
        {
            _nextLabel = Math.Clamp(number, 1, 9);
            SyncLabelPicker();
        }

        private void SyncLabelPicker()
        {
            _restoring = true;
            try { _labelPicker.SelectedItem = _nextLabel; }
            finally { _restoring = false; }
        }

        private void AddStamp(StampState stamp)
        {
            var brush = new SolidColorBrush(InkItemService.PenColor(stamp.Color));
            var element = new Border
            {
                Width = StampSize,
                Height = StampSize,
                BorderBrush = brush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(StampSize / 2),
                Background = Brushes.Transparent,
                Tag = stamp,
                Child = new TextBlock
                {
                    Text = stamp.Number.ToString(CultureInfo.InvariantCulture),
                    Foreground = brush,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            InkCanvas.SetLeft(element, stamp.Left);
            InkCanvas.SetTop(element, stamp.Top);
            MarkupCanvas.Children.Add(element);
        }

        // Stamps where they are now (a stamp moved with Select keeps its offset in a RenderTransform).
        private List<StampState> CurrentStamps()
        {
            var stamps = new List<StampState>();
            foreach (var element in MarkupCanvas.Children.OfType<Border>())
            {
                if (element.Tag is not StampState state)
                    continue;
                double left = InkCanvas.GetLeft(element), top = InkCanvas.GetTop(element);
                if (element.RenderTransform is MatrixTransform matrix)
                {
                    left += matrix.Matrix.OffsetX;
                    top += matrix.Matrix.OffsetY;
                }
                stamps.Add(state with { Left = double.IsNaN(left) ? 0 : left, Top = double.IsNaN(top) ? 0 : top });
            }
            return stamps;
        }

        private Snapshot TakeSnapshot() => new() { Strokes = MarkupCanvas.Strokes.Clone(), Stamps = CurrentStamps(), NextLabel = _nextLabel };

        private void Restore(Snapshot snapshot)
        {
            _restoring = true;
            try
            {
                MarkupCanvas.Strokes = snapshot.Strokes.Clone();
                MarkupCanvas.Children.Clear();
                foreach (var stamp in snapshot.Stamps)
                    AddStamp(stamp);
                _nextLabel = snapshot.NextLabel;
                _labelPicker.SelectedItem = _nextLabel;
            }
            finally { _restoring = false; }
        }

        // One markup action (stroke, stamp, move): recorded for per-action Undo and Redo.
        private void Commit()
        {
            if (_restoring)
                return;
            var snapshot = TakeSnapshot();
            if (SameMarkup(snapshot, _history[_historyIndex]))
                return; // nothing changed (e.g. Delete with nothing selected): no empty Undo step
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(snapshot);
            _historyIndex = _history.Count - 1;
            UpdateHistoryButtons();
        }

        private static bool SameMarkup(Snapshot a, Snapshot b) =>
            a.Strokes.Count == b.Strokes.Count && a.Stamps.SequenceEqual(b.Stamps) &&
            a.Strokes.Zip(b.Strokes).All(p => p.First.GetBounds() == p.Second.GetBounds() && p.First.DrawingAttributes.Color == p.Second.DrawingAttributes.Color);

        public void Undo()
        {
            if (_historyIndex <= 0)
                return;
            _historyIndex--;
            Restore(_history[_historyIndex]);
            UpdateHistoryButtons();
        }

        public void Redo()
        {
            if (_historyIndex >= _history.Count - 1)
                return;
            _historyIndex++;
            Restore(_history[_historyIndex]);
            UpdateHistoryButtons();
        }

        private void UpdateHistoryButtons()
        {
            _undoButton.IsEnabled = _historyIndex > 0;
            _redoButton.IsEnabled = _historyIndex < _history.Count - 1;
        }

        // Adds a stroke as if drawn (tests and automation).
        public void AddStroke(IEnumerable<System.Windows.Point> points)
        {
            var stroke = new Stroke(new StylusPointCollection(points.Select(p => new StylusPoint(p.X, p.Y))),
                MarkupCanvas.DefaultDrawingAttributes.Clone());
            MarkupCanvas.Strokes.Add(stroke);
            Commit();
        }

        private bool HasMarkup => MarkupCanvas.Strokes.Count > 0 || MarkupCanvas.Children.OfType<Border>().Any();

        #endregion

        #region Buttons

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset everything to original
            _gamma = 1f;
            _sharpness = 0f;
            _rotation = 0f;
            RotationSlider.Value = 0;
            _currentImageBytes = _originalImageBytes;
            _restoring = true;
            try
            {
                MarkupCanvas.Strokes.Clear();
                MarkupCanvas.Children.Clear();
            }
            finally { _restoring = false; }
            _history.Clear();
            _history.Add(TakeSnapshot());
            _historyIndex = 0;
            UpdateHistoryButtons();

            LoadImageToDisplay(_originalImageBytes);
            UpdateAdjustmentText();
        }

        private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _rotation = (float)RotationSlider.Value;
            UpdateAdjustmentText();
            ApplyAdjustmentsPreview();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ImageWasEdited = false;
            DialogResult = false;
            Close();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if any edits were made
                bool hasAdjustments = _gamma != 1f || _sharpness != 0 || _rotation != 0f;

                if (!hasAdjustments && !HasMarkup)
                {
                    // No changes
                    ImageWasEdited = false;
                    DialogResult = true;
                    Close();
                    return;
                }

                // Flatten markup onto image if needed
                EditedImageBytes = HasMarkup ? FlattenWithMarkup() : _currentImageBytes;

                ImageWasEdited = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving image: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Where the Uniform-stretched photo sits in the canvas: display = image × scale + offset. Not laid out yet (tests):
        // the canvas is taken to be the photo at 1:1.
        private (double Scale, double OffsetX, double OffsetY) DisplayMapping(int pixelWidth, int pixelHeight)
        {
            double width = MarkupCanvas.ActualWidth, height = MarkupCanvas.ActualHeight;
            if (width <= 0 || height <= 0 || pixelWidth <= 0 || pixelHeight <= 0)
                return (1, 0, 0);
            double scale = Math.Min(width / pixelWidth, height / pixelHeight);
            return (scale, (width - pixelWidth * scale) / 2, (height - pixelHeight * scale) / 2);
        }

        // T2: the photo (with any adjustments) at its own pixel size, the markup mapped from the screen onto it; JPEG 90 at
        // 96 dpi as before. The new-photo processing (halving, brightening) is not applied again.
        public byte[] FlattenWithMarkup()
        {
            var source = (BitmapSource)PhotoImage.Source;
            int width = source.PixelWidth, height = source.PixelHeight;
            var (scale, offsetX, offsetY) = DisplayMapping(width, height);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(source, new Rect(0, 0, width, height));
                context.PushTransform(new MatrixTransform(1 / scale, 0, 0, 1 / scale, -offsetX / scale, -offsetY / scale));
                foreach (var stroke in MarkupCanvas.Strokes)
                    stroke.Draw(context);
                foreach (var stamp in CurrentStamps())
                    DrawStamp(context, stamp);
                context.Pop();
            }
            var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(visual);

            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(renderTarget));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        private void DrawStamp(DrawingContext context, StampState stamp)
        {
            var brush = new SolidColorBrush(InkItemService.PenColor(stamp.Color));
            var centre = new System.Windows.Point(stamp.Left + StampSize / 2, stamp.Top + StampSize / 2);
            context.DrawEllipse(null, new Pen(brush, 2), centre, StampSize / 2 - 1, StampSize / 2 - 1);
            var text = new FormattedText(stamp.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 16, brush, 1.0);
            context.DrawText(text, new System.Windows.Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
        }

        #endregion

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CancelButton_Click(sender, e);
            }
        }
    }
}
