using InspectionEditor.Models;
using InspectionEditor.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace InspectionEditor
{
    public partial class PlanCheckWindow : Window
    {
        private readonly IReadOnlyList<PlanPdfAttachment> _attachments;
        private readonly bool _canAddToItem;
        private readonly List<string> _tempFiles = new();
        private List<PlanCheckFinding> _findings = new();
        private PlanPdfAttachment? _attachment;
        private string? _pdfPath;
        private int _pageCount;
        private int _pageIndex;
        private double _zoom = 1.25;
        private PlanCheckFinding? _selectedFinding;
        private CancellationTokenSource? _renderCancellation;
        private CancellationTokenSource? _zoomDebounce;
        private bool _initializing = true;
        private bool _mousePointerDown;
        private bool _mousePanning;
        private Point _mouseDownPoint;
        private double _mouseDownHorizontalOffset;
        private double _mouseDownVerticalOffset;
        private double _manipulationScale = 1.0;

        public PlanCheckResult? Result { get; private set; }

        public PlanCheckWindow(IReadOnlyList<PlanPdfAttachment> attachments, bool canAddToItem)
        {
            InitializeComponent();
            _attachments = attachments;
            _canAddToItem = canAddToItem;
            AttachmentPicker.ItemsSource = attachments.Select(a => a.Filename).ToList();
            Closed += (_, _) => Cleanup();
            Loaded += async (_, _) =>
            {
                _initializing = false;
                if (_attachments.Count > 0) AttachmentPicker.SelectedIndex = 0;
                else StatusText.Text = "No valid embedded PDF attachment was found in INS FileData.";
                await Task.CompletedTask;
            };
        }

        private async void AttachmentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing || AttachmentPicker.SelectedIndex < 0) return;
            await LoadAttachmentAsync(AttachmentPicker.SelectedIndex);
        }

        private async Task LoadAttachmentAsync(int index)
        {
            try
            {
                SetBusy(true, "Opening embedded plan and locating suggested checks…");
                _attachment = _attachments[index];
                _pdfPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RED_PlanCheck_{Guid.NewGuid():N}.pdf");
                await File.WriteAllBytesAsync(_pdfPath, _attachment.PdfBytes);
                _tempFiles.Add(_pdfPath);
                string path = _pdfPath;
                var load = await Task.Run(() =>
                {
                    int count = PlanCheckService.GetPageCount(path);
                    var findings = PlanCheckService.CreateFindings(path);
                    return (count, findings);
                });
                _pageCount = load.count;
                _findings = load.findings;
                _pageIndex = 0;
                _selectedFinding = _findings.FirstOrDefault();
                BuildChecksPanel();
                // Populate thumbnail placeholders, but show page one without waiting for every page.
                _ = BuildThumbnailsAsync();
                await RenderCurrentPageAsync();
                StatusText.Text = "Suggestions are labeled. Select a check and tap the plan to move its marker.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Unable to open this embedded PDF.";
                MessageBox.Show($"RED could not open the embedded plan.\n\n{ex.Message}", "Plan Check Beta",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private void BuildChecksPanel()
        {
            ChecksPanel.Children.Clear();
            foreach (var finding in _findings)
            {
                var border = new Border
                {
                    BorderBrush = finding == _selectedFinding ? Brushes.DarkRed : Brushes.LightGray,
                    BorderThickness = new Thickness(finding == _selectedFinding ? 2 : 1),
                    Background = Brushes.White,
                    Margin = new Thickness(0, 0, 0, 7),
                    Padding = new Thickness(7),
                    Tag = finding
                };
                border.MouseLeftButtonDown += (_, _) => { _selectedFinding = finding; if (finding.PageIndex != _pageIndex) _ = ShowPageAsync(finding.PageIndex); BuildChecksPanel(); };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = finding.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock
                {
                    Text = finding.SuggestionText,
                    Foreground = finding.IsSuggested ? Brushes.DarkGoldenrod : Brushes.DimGray,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 5)
                });
                var status = new Button
                {
                    Content = StateLabel(finding.State),
                    Background = StateBrush(finding.State),
                    Foreground = Brushes.White,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Tag = finding
                };
                status.Click += StatusButton_Click;
                panel.Children.Add(status);
                if (finding.State == PlanCheckState.Deficient)
                {
                    panel.Children.Add(new CheckBox
                    {
                        Content = _canAddToItem ? "Add cropped screenshot to selected item" : "No inspection item selected — screenshot will not be added",
                        IsEnabled = _canAddToItem,
                        IsChecked = _canAddToItem && finding.AddScreenshotToItem,
                        Margin = new Thickness(2, 5, 0, 0),
                        Tag = finding
                    });
                    ((CheckBox)panel.Children[^1]).Checked += (_, _) => finding.AddScreenshotToItem = true;
                    ((CheckBox)panel.Children[^1]).Unchecked += (_, _) => finding.AddScreenshotToItem = false;
                }
                border.Child = panel;
                ChecksPanel.Children.Add(border);
            }
            UpdateSaveState();
            DrawMarkers();
        }

        private void StatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: PlanCheckFinding finding }) return;
            CycleFinding(finding);
            e.Handled = true;
        }

        private void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: PlanCheckFinding finding }) return;
            CycleFinding(finding);
            e.Handled = true;
        }

        private void CycleFinding(PlanCheckFinding finding)
        {
            _selectedFinding = finding;
            finding.State = finding.State switch
            {
                PlanCheckState.Incomplete => PlanCheckState.Confirmed,
                PlanCheckState.Confirmed => PlanCheckState.Deficient,
                _ => PlanCheckState.Incomplete
            };
            BuildChecksPanel();
        }

        private static string StateLabel(PlanCheckState state) => state switch
        {
            PlanCheckState.Confirmed => "✓ CONFIRMED",
            PlanCheckState.Deficient => "! DEFICIENT",
            _ => "○ INCOMPLETE"
        };

        private static Brush StateBrush(PlanCheckState state) => state switch
        {
            PlanCheckState.Confirmed => Brushes.ForestGreen,
            PlanCheckState.Deficient => Brushes.Firebrick,
            _ => Brushes.Gray
        };

        private async Task BuildThumbnailsAsync()
        {
            ThumbnailList.Items.Clear();
            if (_pdfPath == null) return;
            for (int i = 0; i < _pageCount; i++)
            {
                var item = new StackPanel { Margin = new Thickness(5), Tag = i };
                item.Children.Add(new Border
                {
                    Width = 92,
                    Height = 112,
                    Background = Brushes.White,
                    Child = new TextBlock { Text = "Loading…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 10 }
                });
                item.Children.Add(new TextBlock { Text = $"Page {i + 1}", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
                ThumbnailList.Items.Add(item);
            }
            ThumbnailList.SelectedIndex = 0;

            string path = _pdfPath;
            for (int i = 0; i < _pageCount; i++)
            {
                try
                {
                    int captured = i;
                    var rendered = await Task.Run(() => PlanCheckService.RenderPage(path, captured, 0.22));
                    var source = CreateBitmap(rendered.Bytes, rendered.Width, rendered.Height);
                    if (ThumbnailList.Items[captured] is StackPanel panel && panel.Children[0] is Border border)
                        border.Child = new Image { Source = source, Stretch = Stretch.Uniform };
                }
                catch { /* A failed thumbnail must not block the main plan. */ }
            }
        }

        private async Task RenderCurrentPageAsync()
        {
            if (_pdfPath == null || _pageCount == 0) return;
            _renderCancellation?.Cancel();
            _renderCancellation = new CancellationTokenSource();
            var token = _renderCancellation.Token;
            string path = _pdfPath;
            int page = _pageIndex;
            double scale = 1.35 * _zoom;
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                var rendered = await Task.Run(() => PlanCheckService.RenderPage(path, page, scale), token);
                if (token.IsCancellationRequested || page != _pageIndex) return;
                PlanImage.Source = CreateBitmap(rendered.Bytes, rendered.Width, rendered.Height);
                PlanImage.Width = rendered.Width;
                PlanImage.Height = rendered.Height;
                PlanHost.Width = rendered.Width;
                PlanHost.Height = rendered.Height;
                MarkerCanvas.Width = rendered.Width;
                MarkerCanvas.Height = rendered.Height;
                DrawMarkers();
                PageText.Text = $"Page {_pageIndex + 1} / {_pageCount}";
                ZoomText.Text = $"{_zoom * 100:0}%";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusText.Text = $"Page render failed: {ex.Message}";
            }
            finally
            {
                if (!token.IsCancellationRequested) LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private static BitmapSource CreateBitmap(byte[] bytes, int width, int height)
        {
            int stride = width * 4;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private void DrawMarkers()
        {
            MarkerCanvas.Children.Clear();
            if (MarkerCanvas.Width <= 0 || MarkerCanvas.Height <= 0) return;
            foreach (var finding in _findings.Where(f => f.PageIndex == _pageIndex))
            {
                double x = finding.X * MarkerCanvas.Width;
                double y = finding.Y * MarkerCanvas.Height;
                Brush brush = StateBrush(finding.State);
                var ellipse = new Ellipse { Width = 24, Height = 24, Stroke = brush, StrokeThickness = 4, Fill = Brushes.Transparent, Tag = finding, Cursor = Cursors.Hand };
                ellipse.MouseLeftButtonDown += Marker_MouseLeftButtonDown;
                Canvas.SetLeft(ellipse, x - 12); Canvas.SetTop(ellipse, y - 12);
                MarkerCanvas.Children.Add(ellipse);
                var label = new Border
                {
                    Background = brush,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 2, 4, 2),
                    Child = new TextBlock { Text = finding.Label, Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold }
                };
                Canvas.SetLeft(label, x + 15); Canvas.SetTop(label, Math.Max(0, y - 12));
                MarkerCanvas.Children.Add(label);
            }
        }

        private void MoveSelectedMarker(Point point)
        {
            if (_selectedFinding == null || PlanImage.ActualWidth <= 0 || PlanImage.ActualHeight <= 0) return;
            if (point.X < 0 || point.Y < 0 || point.X > PlanImage.ActualWidth || point.Y > PlanImage.ActualHeight) return;
            _selectedFinding.PageIndex = _pageIndex;
            _selectedFinding.X = Math.Clamp(point.X / PlanImage.ActualWidth, 0, 1);
            _selectedFinding.Y = Math.Clamp(point.Y / PlanImage.ActualHeight, 0, 1);
            _selectedFinding.IsSuggested = false;
            _selectedFinding.SuggestionText = $"Inspector-positioned on page {_pageIndex + 1}.";
            BuildChecksPanel();
        }

        private void PlanScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Marker buttons own their click and must not begin a pan/reposition gesture.
            if (FindAncestorWithTag<PlanCheckFinding>(e.OriginalSource as DependencyObject) != null) return;
            _mousePointerDown = true;
            _mousePanning = false;
            _mouseDownPoint = e.GetPosition(PlanScroll);
            _mouseDownHorizontalOffset = PlanScroll.HorizontalOffset;
            _mouseDownVerticalOffset = PlanScroll.VerticalOffset;
            PlanScroll.CaptureMouse();
        }

        private static FrameworkElement? FindAncestorWithTag<T>(DependencyObject? current)
        {
            while (current != null)
            {
                if (current is FrameworkElement element && element.Tag is T) return element;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void PlanScroll_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_mousePointerDown || e.LeftButton != MouseButtonState.Pressed) return;
            Point current = e.GetPosition(PlanScroll);
            Vector delta = current - _mouseDownPoint;
            if (!_mousePanning && Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < 15) return;
            _mousePanning = true;
            PlanScroll.ScrollToHorizontalOffset(_mouseDownHorizontalOffset - delta.X);
            PlanScroll.ScrollToVerticalOffset(_mouseDownVerticalOffset - delta.Y);
            e.Handled = true;
        }

        private void PlanScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_mousePointerDown) return;
            Point planPoint = e.GetPosition(PlanImage);
            bool wasPan = _mousePanning;
            _mousePointerDown = false;
            _mousePanning = false;
            PlanScroll.ReleaseMouseCapture();
            if (!wasPan) MoveSelectedMarker(planPoint);
            e.Handled = true;
        }

        private async Task ShowPageAsync(int index)
        {
            if (index < 0 || index >= _pageCount || index == _pageIndex) return;
            _pageIndex = index;
            ThumbnailList.SelectedIndex = index;
            await RenderCurrentPageAsync();
        }

        private async void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThumbnailList.SelectedIndex >= 0 && ThumbnailList.SelectedIndex != _pageIndex)
                await ShowPageAsync(ThumbnailList.SelectedIndex);
        }
        private async void PreviousPage_Click(object sender, RoutedEventArgs e) => await ShowPageAsync(_pageIndex - 1);
        private async void NextPage_Click(object sender, RoutedEventArgs e) => await ShowPageAsync(_pageIndex + 1);
        private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(0.2);
        private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-0.2);

        private void PlanScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ChangeZoom(e.Delta > 0 ? 0.15 : -0.15);
            e.Handled = true;
        }

        private void PlanHost_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
        {
            double factor = e.DeltaManipulation.Scale.X;
            if (double.IsFinite(factor) && factor > 0)
            {
                _manipulationScale = Math.Clamp(_manipulationScale * factor, 0.5 / _zoom, 4.0 / _zoom);
                PlanHost.LayoutTransform = new ScaleTransform(_manipulationScale, _manipulationScale);
                ZoomText.Text = $"{Math.Clamp(_zoom * _manipulationScale, 0.5, 4.0) * 100:0}%";
            }
            e.Handled = true;
        }
        private async void PlanHost_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            _zoom = Math.Clamp(_zoom * _manipulationScale, 0.5, 4.0);
            _manipulationScale = 1.0;
            PlanHost.LayoutTransform = Transform.Identity;
            await RenderCurrentPageAsync();
        }

        private void ChangeZoom(double delta)
        {
            _zoom = Math.Clamp(_zoom + delta, 0.5, 4.0);
            ZoomText.Text = $"{_zoom * 100:0}%";
            _zoomDebounce?.Cancel();
            _zoomDebounce = new CancellationTokenSource();
            var token = _zoomDebounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(180, token);
                    await Dispatcher.InvokeAsync(async () => await RenderCurrentPageAsync());
                }
                catch (OperationCanceledException) { }
            });
        }

        private void UpdateSaveState()
        {
            int remaining = _findings.Count(f => f.State == PlanCheckState.Incomplete);
            SaveButton.IsEnabled = _findings.Count == 5 && remaining == 0;
            if (remaining > 0) StatusText.Text = $"Complete all five checks to save ({remaining} remaining).";
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_pdfPath == null || _attachment == null || _findings.Any(f => f.State == PlanCheckState.Incomplete)) return;
            try
            {
                SetBusy(true, "Creating deficiency crops and optional annotated PDF…");
                var result = new PlanCheckResult();
                result.Findings.AddRange(_findings);
                foreach (var finding in _findings.Where(f => f.State == PlanCheckState.Deficient))
                {
                    byte[] png = await Task.Run(() => PlanCheckService.CreateDeficiencyCrop(_pdfPath, finding));
                    result.Screenshots.Add(new PlanCheckScreenshot
                    {
                        PngBytes = png,
                        Filename = $"plan-check-{finding.Id}-p{finding.PageIndex + 1}.png",
                        Caption = $"Plan check deficiency: {finding.Label} (page {finding.PageIndex + 1})",
                        AddToCurrentItem = _canAddToItem && finding.AddScreenshotToItem
                    });
                }
                if (AddAnnotatedPdfCheck.IsChecked == true)
                {
                    byte[] pdf = await Task.Run(() => PlanCheckService.CreateAnnotatedPdf(_pdfPath, _findings));
                    result.AnnotatedAttachment = PlanCheckService.CreateAnnotatedAttachment(_attachment, pdf);
                }
                Result = result;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                MessageBox.Show($"Plan Check could not finish saving. The original attachment was not changed.\n\n{ex.Message}",
                    "Plan Check Beta", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetBusy(bool busy, string? message = null)
        {
            LoadingOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            AttachmentPicker.IsEnabled = !busy;
            SaveButton.IsEnabled = !busy && _findings.Count == 5 && _findings.All(f => f.State != PlanCheckState.Incomplete);
            if (message != null) StatusText.Text = message;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        private void Cleanup()
        {
            _renderCancellation?.Cancel();
            _zoomDebounce?.Cancel();
            foreach (string path in _tempFiles)
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
