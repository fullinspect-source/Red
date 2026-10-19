using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-006 drawing window for the customer Signature item, Sketch and FoundationSketch. After every action the whole
    // canvas is rendered at 96 dpi and stored on the item as its one JPEG picture with Value "ok" (SPEC S1-S3).
    public sealed class InkItemEditorWindow : Window
    {
        private enum Tool { Draw, Select, Label }

        private sealed record StampState(int Number, string Color, double Left, double Top);

        private sealed class Snapshot
        {
            public StrokeCollection Strokes = new();
            public List<StampState> Stamps = new();
            public int NextLabel;
            public bool ShowExisting;
        }

        private readonly Item _item;
        private readonly InkControlKind _kind;
        private readonly Action<Item> _changed;
        private readonly int _width, _height;
        private readonly Grid _surface;
        private readonly Image _ffpImage = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Image _existingImage = new() { Stretch = Stretch.Fill };
        private readonly InkCanvas _ink;
        private readonly List<Snapshot> _history = new();
        private readonly ComboBox _labelPicker = new() { Width = 52, Margin = new Thickness(2, 0, 8, 0) };
        private readonly Dictionary<string, ToggleButton> _colorButtons = new();
        private readonly Dictionary<Tool, ToggleButton> _toolButtons = new();
        private readonly Button _undoButton = new() { Content = "Undo", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2) };
        private readonly Button _redoButton = new() { Content = "Redo", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2) };
        private int _historyIndex = -1;
        private int _nextLabel = 1;
        private string _penColor;
        private Tool _tool = Tool.Draw;
        private bool _restoring;

        private readonly Action<string> _savePenColor;

        // loadPenColor/savePenColor default to RED's profile (SketchPenColor); tests pass their own.
        public InkItemEditorWindow(Item item, string? ffpPath, Action<Item> changed,
            Func<string?>? loadPenColor = null, Action<string>? savePenColor = null)
        {
            _item = item;
            _kind = InkItemService.KindOf(item);
            _changed = changed;
            _savePenColor = savePenColor ?? InkItemService.RememberPenColor;
            (_width, _height) = InkItemService.CanvasSize(_kind);
            Title = InkItemService.Label(item);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));

            var existing = InkItemService.DecodePicture(item);
            _existingImage.Source = existing;
            _existingImage.Width = _width;
            _existingImage.Height = _height;

            // Signature: INSPECT sets blue only when re-opening an existing signature; a first one uses the InkCanvas
            // default (black). Sketches use the remembered SketchPenColor.
            _penColor = _kind == InkControlKind.Signature
                ? (existing != null ? "Blue" : "Black")
                : InkItemService.NormalizePenColor(loadPenColor != null ? loadPenColor() : InspectorProfileService.Load()?.SketchPenColor);

            _ink = new InkCanvas
            {
                Width = _width,
                Height = _height,
                Background = Brushes.Transparent,
                EditingMode = InkCanvasEditingMode.Ink
            };
            _ink.DefaultDrawingAttributes.Color = InkItemService.PenColor(_penColor);
            _ink.StrokeCollected += (_, _) => Commit();
            _ink.SelectionMoved += (_, _) => Commit();
            _ink.SelectionResized += (_, _) => Commit();
            _ink.PreviewMouseLeftButtonUp += Ink_PreviewMouseLeftButtonUp;

            _surface = new Grid { Width = _width, Height = _height, Background = Brushes.White, ClipToBounds = true };
            string? hint = null;
            if (_kind == InkControlKind.FoundationSketch)
            {
                var background = LoadFfpBackground(ffpPath);
                if (background != null)
                {
                    _ffpImage.Source = background;
                    _ffpImage.Width = _width; // page 1 rendered to the canvas width (SPEC §5)
                }
                else
                {
                    hint = InkItemService.NoFfpMessage;
                }
            }
            _surface.Children.Add(_ffpImage);
            _surface.Children.Add(_existingImage);
            _surface.Children.Add(_ink);

            var root = new DockPanel { Margin = new Thickness(10) };
            var toolbar = BuildToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            var done = new Button { Content = "Done", Padding = new Thickness(18, 5, 18, 5), IsDefault = true, IsCancel = true };
            done.Click += (_, _) => Close();
            DockPanel.SetDock(done, Dock.Right);
            bottom.Children.Add(done);
            bottom.Children.Add(new TextBlock
            {
                Text = hint ?? "Every stroke is stored on the item at once.",
                Foreground = hint != null ? new SolidColorBrush(Color.FromRgb(146, 64, 14)) : new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontWeight = hint != null ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(bottom);

            root.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Child = _surface,
                HorizontalAlignment = HorizontalAlignment.Left
            });
            Content = root;

            _originalPictures = _item.Pictures.ToList();
            _originalValue = _item.Value;
            _history.Add(TakeSnapshot(showExisting: existing != null));
            _historyIndex = 0;
            UpdateButtons();
        }

        // C3: undoing every action gives back the item exactly as it opened (the same Picture objects and Value), so an
        // untouched drawing is never re-encoded.
        private readonly List<Picture> _originalPictures;
        private readonly object? _originalValue;
        private bool _baselineIsOriginal = true;
        public bool ItemChanged { get; private set; }

        public InkControlKind Kind => _kind;
        public int NextLabelNumber => _nextLabel;
        public string PenColorName => _penColor;

        private FrameworkElement BuildToolbar()
        {
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (_kind != InkControlKind.Signature)
            {
                foreach (var tool in new[] { Tool.Select, Tool.Draw, Tool.Label })
                {
                    var button = new ToggleButton { Content = tool.ToString(), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2), IsChecked = tool == _tool };
                    button.Click += (_, _) => SetTool(tool);
                    _toolButtons[tool] = button;
                    bar.Children.Add(button);
                }
                for (int n = 1; n <= 9; n++)
                    _labelPicker.Items.Add(n);
                _labelPicker.SelectedItem = _nextLabel;
                _labelPicker.SelectionChanged += (_, _) =>
                {
                    if (!_restoring && _labelPicker.SelectedItem is int n)
                        _nextLabel = n;
                };
                bar.Children.Add(_labelPicker);

                foreach (var name in InkItemService.PenColorNames)
                {
                    var button = new ToggleButton
                    {
                        Content = name,
                        Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(2),
                        Foreground = new SolidColorBrush(InkItemService.PenColor(name)),
                        FontWeight = FontWeights.SemiBold,
                        IsChecked = name == _penColor
                    };
                    button.Click += (_, _) => SetPenColor(name, remember: true);
                    _colorButtons[name] = button;
                    bar.Children.Add(button);
                }

                _undoButton.Click += (_, _) => Undo();
                _redoButton.Click += (_, _) => Redo();
                bar.Children.Add(_undoButton);
                bar.Children.Add(_redoButton);
            }

            var clear = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 2, 2, 2) };
            clear.Click += (_, _) => ClearCanvas(ask: _kind != InkControlKind.Signature);
            bar.Children.Add(clear);
            return bar;
        }

        private static BitmapSource? LoadFfpBackground(string? ffpPath)
        {
            if (string.IsNullOrEmpty(ffpPath) || !System.IO.File.Exists(ffpPath))
                return null;
            try
            {
                var page = PlanCheckService.RenderPage(ffpPath, 0, 2.0);
                var bitmap = BitmapSource.Create(page.Width, page.Height, 96, 96, PixelFormats.Bgra32, null, page.Bytes, page.Width * 4);
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not render the FFP background", ex);
                return null;
            }
        }

        private void SetTool(Tool tool)
        {
            _tool = tool;
            foreach (var pair in _toolButtons)
                pair.Value.IsChecked = pair.Key == tool;
            _ink.EditingMode = tool switch
            {
                Tool.Draw => InkCanvasEditingMode.Ink,
                Tool.Select => InkCanvasEditingMode.Select,
                _ => InkCanvasEditingMode.None
            };
        }

        public void SetPenColor(string name, bool remember)
        {
            _penColor = InkItemService.NormalizePenColor(name);
            _ink.DefaultDrawingAttributes.Color = InkItemService.PenColor(_penColor);
            foreach (var pair in _colorButtons)
                pair.Value.IsChecked = pair.Key == _penColor;
            if (!remember || _kind == InkControlKind.Signature)
                return;
            _savePenColor(_penColor);
        }


        private void Ink_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_tool != Tool.Label)
                return;
            var point = e.GetPosition(_ink);
            PlaceLabel(point);
            e.Handled = true;
        }

        // §4: each tap places the current number and advances; 9 wraps to 1.
        public void PlaceLabel(Point point)
        {
            AddStamp(new StampState(_nextLabel, _penColor, point.X - 15, point.Y - 15));
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
                Width = 30,
                Height = 30,
                BorderBrush = brush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(15),
                Background = Brushes.Transparent,
                Tag = stamp,
                Child = new TextBlock
                {
                    Text = stamp.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Foreground = brush,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            InkCanvas.SetLeft(element, stamp.Left);
            InkCanvas.SetTop(element, stamp.Top);
            _ink.Children.Add(element);
        }

        public IReadOnlyList<int> StampNumbers => _ink.Children.OfType<Border>()
            .Select(b => b.Tag is StampState s ? s.Number : 0).ToList();

        private Snapshot TakeSnapshot(bool showExisting)
        {
            var snapshot = new Snapshot { Strokes = _ink.Strokes.Clone(), NextLabel = _nextLabel, ShowExisting = showExisting };
            foreach (var element in _ink.Children.OfType<Border>())
            {
                if (element.Tag is not StampState state)
                    continue;
                double left = InkCanvas.GetLeft(element);
                double top = InkCanvas.GetTop(element);
                // A moved stamp keeps its offset in a RenderTransform (Select tool).
                if (element.RenderTransform is MatrixTransform matrix)
                {
                    left += matrix.Matrix.OffsetX;
                    top += matrix.Matrix.OffsetY;
                }
                snapshot.Stamps.Add(state with { Left = double.IsNaN(left) ? 0 : left, Top = double.IsNaN(top) ? 0 : top });
            }
            return snapshot;
        }

        private void Restore(Snapshot snapshot)
        {
            _restoring = true;
            try
            {
                _ink.Strokes = snapshot.Strokes.Clone();
                _ink.Children.Clear();
                foreach (var stamp in snapshot.Stamps)
                    AddStamp(stamp);
                _nextLabel = snapshot.NextLabel;
                _labelPicker.SelectedItem = _nextLabel;
                _existingImage.Visibility = snapshot.ShowExisting ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _restoring = false; }
        }

        private bool ExistingShown => _existingImage.Source != null && _existingImage.Visibility == Visibility.Visible;

        // One action: record it for Undo/Redo, render the canvas, store it on the item.
        private void Commit()
        {
            if (_restoring)
                return;
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(TakeSnapshot(ExistingShown));
            _historyIndex = _history.Count - 1;
            StoreCanvas();
            UpdateButtons();
        }

        public void Undo()
        {
            if (_historyIndex <= 0)
                return;
            _historyIndex--;
            Restore(_history[_historyIndex]);
            StoreAfterHistoryMove();
        }

        public void Redo()
        {
            if (_historyIndex >= _history.Count - 1)
                return;
            _historyIndex++;
            Restore(_history[_historyIndex]);
            StoreAfterHistoryMove();
        }

        private void StoreAfterHistoryMove()
        {
            bool empty = _ink.Strokes.Count == 0 && !_ink.Children.OfType<Border>().Any() && !ExistingShown;
            if (_historyIndex == 0 && _baselineIsOriginal)
            {
                _item.Pictures.Clear();
                _item.Pictures.AddRange(_originalPictures);
                _item.Value = _originalValue;
                _changed(_item);
            }
            else if (empty)
            {
                // Back to a canvas with nothing drawn: the item is as it was before the first stroke.
                InkItemService.ClearInk(_item);
                _changed(_item);
            }
            else
            {
                StoreCanvas();
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _undoButton.IsEnabled = _historyIndex > 0;
            _redoButton.IsEnabled = _historyIndex < _history.Count - 1;
        }

        public void ClearCanvas(bool ask)
        {
            bool hasContent = _ink.Strokes.Count > 0 || _ink.Children.Count > 0 || ExistingShown || InkItemService.HasInkPicture(_item);
            if (ask && hasContent &&
                MessageBox.Show(this, InkItemService.ClearSketchQuestion, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _restoring = true;
            try
            {
                _ink.Strokes.Clear();
                _ink.Children.Clear();
                _existingImage.Visibility = Visibility.Collapsed;
            }
            finally { _restoring = false; }
            _history.Clear();
            _history.Add(TakeSnapshot(showExisting: false));
            _historyIndex = 0;
            _baselineIsOriginal = false;
            ItemChanged = true;
            InkItemService.ClearInk(_item); // S4: Pictures [] and Value null
            _changed(_item);
            UpdateButtons();
        }

        // Adds a stroke as if drawn (tests and automation).
        public void AddStroke(IEnumerable<Point> points)
        {
            var stroke = new Stroke(new StylusPointCollection(points.Select(p => new StylusPoint(p.X, p.Y))),
                _ink.DefaultDrawingAttributes.Clone());
            _ink.Strokes.Add(stroke);
            Commit();
        }

        public BitmapSource RenderCanvas()
        {
            if (!_surface.IsLoaded || _surface.ActualWidth <= 0)
            {
                // Not shown (tests, automation): lay the canvas out on its own before rendering.
                _surface.Measure(new Size(_width, _height));
                _surface.Arrange(new Rect(0, 0, _width, _height));
            }
            // Stamps added or restored this moment are not laid out yet; render only after layout.
            _surface.UpdateLayout();
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, _width, _height));
                context.DrawRectangle(new VisualBrush(_surface) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, _width, _height));
            }
            var bitmap = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private void StoreCanvas()
        {
            try
            {
                InkItemService.ApplyInk(_item, InkItemService.EncodeJpegBase64(RenderCanvas()));
                ItemChanged = true;
                _changed(_item);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not store the drawing", ex);
                MessageBox.Show(this, "RED could not store this drawing. Your strokes are still on the canvas; try again.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
