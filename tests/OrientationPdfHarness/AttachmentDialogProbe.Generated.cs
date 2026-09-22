// Generated from AttachmentsWindow.xaml.cs; only UI types/namespaces are seams.
#nullable enable
using AttachmentDialog.Stubs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AttachmentDialog
{
    public partial class AttachmentsWindow : Window
    {
        public sealed class PdfRow
        {
            public int Index { get; init; }
            public string DisplayIndex => Index < 0 ? "Pending" : (Index + 1).ToString();
            public bool IsChecked { get; set; }
            public string Filename { get; init; } = "";
            public string Size { get; init; } = "";
            public string Status { get; init; } = "";
            public bool CanOpen { get; init; } = true;
        }
        private readonly bool _readOnly;
        private readonly Func<List<PdfRow>> _enumerate;
        private readonly Action<int> _open;
        private readonly Func<string, Func<long, bool>, bool> _add;
        private readonly Func<IReadOnlyCollection<int>, bool> _delete;
        private readonly Action _retry;
        private List<PdfRow> _rows = new();

        public AttachmentsWindow(bool readOnly, Func<List<PdfRow>> enumerate, Action<int> open,
            Func<string, Func<long, bool>, bool> add, Func<IReadOnlyCollection<int>, bool> delete, Action retry)
        {
            InitializeComponent();
            _readOnly = readOnly; _enumerate = enumerate; _open = open; _add = add; _delete = delete; _retry = retry;
            OpenButton.IsEnabled = AddButton.IsEnabled = DeleteButton.IsEnabled = RetryButton.IsEnabled = !readOnly;
            ModeHint.Text = readOnly ? "Read-only inspection: attachment editing, adding and deleting are disabled."
                : "Open one checked PDF in your default editor. Save there; RED automatically captures saved disk changes. Closing this list does not stop monitoring.";
            RefreshRows();
        }

        public static string ReadableSize(long bytes) => $"{bytes / (1024d * 1024d):0.00} MiB ({bytes:N0} bytes)";

        public void RefreshRows()
        {
            var checkedIndices = _rows.Where(r => r.IsChecked).Select(r => r.Index).ToHashSet();
            var next = _enumerate();
            // Avoid replacing live checkbox/scroll state on every timer tick when nothing changed.
            if (_rows.Count == next.Count && _rows.Zip(next).All(p => p.First.Index == p.Second.Index &&
                p.First.Filename == p.Second.Filename && p.First.Size == p.Second.Size && p.First.Status == p.Second.Status &&
                p.First.CanOpen == p.Second.CanOpen)) return;
            foreach (var row in next) row.IsChecked = checkedIndices.Contains(row.Index);
            _rows = next;
            PdfList.ItemsSource = _rows;
        }

        private void Run(Action action)
        {
            try { ResultText.Text = ""; action(); RefreshRows(); }
            catch (Exception ex) { ResultText.Text = ex.Message + " Your original PDFs and pending working copies are retained."; }
        }
        private void Open_Click(object sender, RoutedEventArgs e) => Run(() =>
        {
            if (_readOnly) return;
            var selected = _rows.Where(r => r.IsChecked).ToList();
            if (selected.Count != 1) { ResultText.Text = "Check exactly one PDF to open."; return; }
            if (!selected[0].CanOpen)
            {
                ResultText.Text = "This PDF is damaged or unreadable and cannot be opened. Use Delete checked to remove it, or contact Trent to repair it.";
                return;
            }
            _open(selected[0].Index);
        });
        private void Add_Click(object sender, RoutedEventArgs e) => Run(() =>
        {
            if (_readOnly) return;
            var picker = new OpenFileDialog { Title = "Add embedded PDFs to this INS", Filter = "PDF documents (*.pdf)|*.pdf", Multiselect = true, CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            foreach (string file in picker.FileNames)
            {
                bool saved = _add(file, size => MessageBox.Show(this,
                    $"This PDF is {ReadableSize(size)}, larger than 15 MiB. Embed the complete PDF in this INS?\n\n{System.IO.Path.GetFileName(file)}",
                    "Large PDF attachment", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
                if (!saved) { ResultText.Text = "A PDF was not added (declined or save failed). Failed saves retain an owned working copy in the list; use Retry Save before leaving the report."; break; }
            }
        });
        private void Delete_Click(object sender, RoutedEventArgs e) => Run(() =>
        {
            if (_readOnly) return;
            var selected = _rows.Where(r => r.IsChecked).ToList();
            if (selected.Count == 0) { ResultText.Text = "Check the PDFs to delete."; return; }
            if (MessageBox.Show(this, $"Delete {selected.Count} embedded PDF(s) from this INS?\n\n" +
                string.Join("\n", selected.Select(r => $"#{r.DisplayIndex}: {r.Filename}" + (r.CanOpen ? "" : " (damaged / unreadable)"))) +
                "\n\nExternal job documents and original template files are not deleted.",
                "Delete embedded PDFs", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (!_delete(selected.Select(r => r.Index).ToArray())) ResultText.Text = "Delete was not saved. Pending PDF changes must be resolved first.";
            else { _rows.Clear(); PdfList.ItemsSource = null; }
        });
        private void Retry_Click(object sender, RoutedEventArgs e) => Run(() => { if (!_readOnly) _retry(); });
        private void Refresh_Click(object sender, RoutedEventArgs e) => Run(RefreshRows);
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
