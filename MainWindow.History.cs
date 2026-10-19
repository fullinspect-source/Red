using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-026 History: INSPECT's read-only "Inspection History" window, for every inspection (read-only ones too, H3).
    public partial class MainWindow
    {
        private Button? _historyButton;

        private void InstallHistoryButton()
        {
            if (SaveFileButton.Parent is not Panel panel || _historyButton != null)
                return;
            _historyButton = new Button { Content = "🕘 History", IsEnabled = false, ToolTip = "Show this inspection's history (read only)" };
            _historyButton.Click += (_, _) => ShowInspectionHistory();
            int index = _printButton != null ? panel.Children.IndexOf(_printButton) + 1 : panel.Children.IndexOf(SaveFileButton) + 1;
            panel.Children.Insert(index, _historyButton);
        }

        private void UpdateHistoryButton()
        {
            if (_historyButton != null)
                _historyButton.IsEnabled = _currentInspection != null && _saveService.HasFile;
        }

        private void ShowInspectionHistory()
        {
            if (_currentInspection == null || !_saveService.HasFile)
                return;
            var rows = InspectionHistory.Rows(Header("Events"));
            var grid = new GridView();
            foreach (var (header, path, width) in new[] { ("Date", nameof(InspectionHistory.Row.Date), 170.0), ("Description", nameof(InspectionHistory.Row.Description), 260.0), ("User", nameof(InspectionHistory.Row.User), 200.0) })
                grid.Columns.Add(new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) });
            var list = new ListView { View = grid, ItemsSource = rows, Margin = new Thickness(12, 12, 12, 0), MinHeight = 160 };
            var close = new Button { Content = "Close", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, IsDefault = true };
            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(list);
            var window = new Window
            {
                Title = InspectionHistory.Title,
                Owner = this,
                Width = 680,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };
            close.Click += (_, _) => window.Close();
            window.ShowDialog();
        }
    }
}
