using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor.Services;
using InspectionEditor.Services.Reports;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    // F-022 local report preview (SPEC §3, §9): the three toggles (O1), Print (X1), Save to PDF (X2) and Email (X3). The
    // report is rendered by Services\Reports from a snapshot of the saved inspection; temporary PDFs go when it closes (X4).
    public sealed class ReportPreviewWindow : Window
    {
        private const double PreviewScale = 1.5, PrintScale = 2.5;
        private readonly JObject _document;
        private readonly ReportSettings _settings;
        private readonly InspectorProfile? _profile;
        private readonly string _number, _address, _name;
        private readonly string _tempFolder = ReportService.NewTempFolder();
        private readonly CheckBox _defectsOnly = new() { Content = " Only Show Defects", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _pictures = new() { Content = " Show Pictures", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _attachments = new() { Content = " Show Attachments", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _print = Tool("🖨 Print"), _save = Tool("💾 Save to PDF"), _email = Tool("✉ Email"), _close = Tool("Close");
        private readonly StackPanel _pages = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _status = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private ReportService.Rendered? _rendered;
        private string? _pdfPath;
        private int _generation;
        private bool _busy, _loadingOptions, _closed, _exportLogged;

        public ReportPreviewWindow(JObject document, ReportSettings settings, InspectorProfile? profile)
        {
            _document = document;
            _settings = settings;
            _profile = profile;
            _number = document["InspectionNumber"]?.ToString() ?? "";
            _address = document["Address"]?.ToString() ?? "";
            _name = document["InspectionName"]?.ToString() ?? "";
            Title = string.IsNullOrWhiteSpace(_number) ? "Report preview" : $"Report preview - {_number}";
            Width = 980;
            Height = 900;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var options = ReportService.InitialOptions(settings, profile);
            _loadingOptions = true;
            _defectsOnly.IsChecked = options.DefectsOnly;
            _pictures.IsChecked = options.Pictures;
            _attachments.IsChecked = options.Attachments;
            _loadingOptions = false;
            foreach (var box in new[] { _defectsOnly, _pictures, _attachments })
            {
                box.Checked += (_, _) => OptionsChanged();
                box.Unchecked += (_, _) => OptionsChanged();
            }
            _print.Click += async (_, _) => await PrintAsync();
            _save.Click += (_, _) => SavePdf();
            _email.Click += (_, _) => Email();
            _close.Click += (_, _) => Close();

            var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(_print);
            right.Children.Add(_save);
            right.Children.Add(_email);
            right.Children.Add(_close);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_defectsOnly);
            left.Children.Add(_pictures);
            left.Children.Add(_attachments);
            left.Children.Add(_status);
            bar.Children.Add(left);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(new ScrollViewer
            {
                Content = _pages,
                Background = new SolidColorBrush(Color.FromRgb(96, 96, 96)),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
            Content = root;
            Loaded += async (_, _) => await RenderAsync();
            Closed += (_, _) =>
            {
                _closed = true;
                _generation++;
                ReportService.TryDelete(_tempFolder);
                if (System.IO.Directory.Exists(_tempFolder))
                {
                    string folder = _tempFolder;
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 10 && Directory.Exists(folder); i++)
                        {
                            await Task.Delay(500);
                            ReportService.TryDelete(folder);
                        }
                    });
                }
            }; // X4
        }

        private static Button Tool(string text) => new() { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };

        private ReportOptions CurrentOptions => new(_defectsOnly.IsChecked == true, _pictures.IsChecked == true, _attachments.IsChecked == true);

        private async void OptionsChanged()
        {
            if (_loadingOptions) return;
            try { ReportService.Remember(_settings, CurrentOptions); } // O1: remembered per device
            catch (Exception ex) { DiagnosticLogService.Info($"Report options not remembered: {ex.GetType().Name}"); }
            await RenderAsync();
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _status.Text = status;
            _print.IsEnabled = _save.IsEnabled = _email.IsEnabled = !busy && _rendered != null;
            _defectsOnly.IsEnabled = _pictures.IsEnabled = _attachments.IsEnabled = !busy;
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        }

        private async Task RenderAsync()
        {
            int generation = ++_generation;
            var options = CurrentOptions;
            _rendered = null;
            _pages.Children.Clear();
            SetBusy(true, "Building the report…");
            try
            {
                var document = (JObject)_document.DeepClone();
                var rendered = await Task.Run(() => ReportService.Render(document, _settings, options, DateTime.Now));
                if (generation != _generation) return;
                InspectFormatLog.ReportExport(_number, options.DefectsOnly, options.Pictures, options.Attachments, rendered.AttachmentsAdded, first: !_exportLogged);
                _exportLogged = true;
                string path = Path.Combine(_tempFolder, $"preview-{generation}.pdf");
                await File.WriteAllBytesAsync(path, rendered.Pdf);
                if (_pdfPath != null) TryDeleteFile(_pdfPath);
                _pdfPath = path;
                _rendered = rendered;
                int count = await Task.Run(() => PlanCheckService.GetPageCount(path));
                for (int i = 0; i < count; i++)
                {
                    int index = i;
                    var page = await Task.Run(() => PageImage(path, index, PreviewScale));
                    if (generation != _generation) return;
                    _pages.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 12, 0, 0),
                        Background = Brushes.White,
                        Child = new Image { Source = page, Width = 816, Height = 816.0 * page.PixelHeight / page.PixelWidth }
                    });
                    _status.Text = $"Page {index + 1} of {count}";
                }
                SetBusy(false, count == 1 ? "1 page" : $"{count} pages");
            }
            catch (Exception ex)
            {
                if (generation != _generation) return;
                DiagnosticLogService.Info($"Report preview failed: {ex.GetType().Name}");
                SetBusy(false, "The report could not be built.");
                MessageBox.Show(this, "The report could not be built. " + ex.Message, "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (_closed) ReportService.TryDelete(_tempFolder); // a render that outlived the window
            }
        }

        private static BitmapSource PageImage(string pdfPath, int index, double scale)
        {
            var (bytes, width, height) = PlanCheckService.RenderPage(pdfPath, index, scale);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        // X1: the platform print dialog; every page (report, Kyle page, attachments) printed as an image on Letter paper.
        private async Task PrintAsync()
        {
            if (_busy || _pdfPath == null) return;
            var dialog = new PrintDialog { UserPageRangeEnabled = false };
            if (dialog.ShowDialog() != true) return;
            string path = _pdfPath;
            SetBusy(true, "Preparing to print…");
            try
            {
                int count = await Task.Run(() => PlanCheckService.GetPageCount(path));
                dialog.PrintDocument(new PagePaginator(path, count), string.IsNullOrWhiteSpace(_number) ? "Inspection report" : _number);
                InspectFormatLog.Printed(count);
                SetBusy(false, "Sent to the printer.");
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Info($"Report print failed: {ex.GetType().Name}");
                SetBusy(false, "Not printed.");
                MessageBox.Show(this, "The report could not be printed. " + ex.Message, "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // X2.
        private void SavePdf()
        {
            if (_busy || _rendered == null) return;
            var dialog = new SaveFileDialog
            {
                Title = "Save to PDF",
                Filter = "Pdf Files|*.pdf",
                FileName = ReportService.PdfFileName(_number),
                AddExtension = true,
                DefaultExt = ".pdf"
            };
            if (dialog.ShowDialog(this) != true) return;
            string temp = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, _rendered.Pdf);
                File.Move(temp, dialog.FileName, overwrite: true);
                _status.Text = "Saved " + Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                TryDeleteFile(temp); // never leave a copy of the report next to the chosen file
                DiagnosticLogService.Info($"Report Save to PDF failed: {ex.GetType().Name}");
                MessageBox.Show(this, "The PDF could not be saved. " + ex.Message, "Save to PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // X3: an unsent draft opened by the default mail app; nothing is sent. From is left to the mail app's own account. The draft folder is not removed at close,
        // because the mail app may still be reading it; NewTempFolder sweeps folders older than a day.
        private void Email()
        {
            if (_busy || _rendered == null) return;
            string folder = ReportService.NewTempFolder();
            string eml = Path.Combine(folder, "Inspection Report.eml");
            try
            {
                string body = ReportService.EmailBody(_name, _address, _profile?.InspectorName);
                File.WriteAllText(eml, ReportService.BuildEml(ReportService.EmailSubject(_address, _name), body, null,
                    ReportService.PdfFileName(_number), _rendered.Pdf));
                Process.Start(new ProcessStartInfo(eml) { UseShellExecute = true });
                _status.Text = "E-mail draft opened. Nothing was sent.";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(this, $"No mail app is set up to open e-mail drafts. The draft was saved to\n{eml}", "Email", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Info($"Report e-mail draft failed: {ex.GetType().Name}");
                MessageBox.Show(this, "The e-mail draft could not be created. " + ex.Message, "Email", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // X1: one Letter page per PDF page, rendered when the print system asks for it.
        private sealed class PagePaginator : DocumentPaginator
        {
            private readonly string _path;
            private readonly int _count;
            public PagePaginator(string path, int count) { _path = path; _count = count; }
            public override bool IsPageCountValid => true;
            public override int PageCount => _count;
            public override Size PageSize { get; set; } = new(816, 1056);
            public override IDocumentPaginatorSource? Source => null;
            public override DocumentPage GetPage(int pageNumber)
            {
                var image = PageImage(_path, pageNumber, PrintScale);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    double scale = Math.Min(PageSize.Width / image.Width, PageSize.Height / image.Height);
                    double w = image.Width * scale, h = image.Height * scale;
                    context.DrawImage(image, new Rect((PageSize.Width - w) / 2, (PageSize.Height - h) / 2, w, h));
                }
                return new DocumentPage(visual, PageSize, new Rect(PageSize), new Rect(PageSize));
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }
}
