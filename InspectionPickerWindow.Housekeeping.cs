using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Settings rows for RED's housekeeping, placed under F-003's Archive retention text: the Archive retention days (owner
    // 2026-10-20: edited here, one value with INSPECT), F-015 N3 "RED cleans the Archive", F-019 "RED purges job folders"
    // and F-020's Download Offline Data. Owner 2026-10-20 (tablet test of 2.3.4): no "Check ... (changes nothing)" buttons,
    // no Show Review setting, and Download Offline Data reports on its own line instead of in message boxes.
    public partial class InspectionPickerWindow
    {
        private CheckBox? _cleanArchiveBox, _purgeJobsBox;
        private TextBox? _retentionBox;
        private TextBlock? _retentionStatus;

        private void EnsureHousekeepingControls(string? inspectionsFolder)
        {
            if (_retentionBox != null) _retentionBox.Text = InspectLiveSettings.ReadArchiveRetention(inspectionsFolder).ToString(CultureInfo.InvariantCulture);
            if (_cleanArchiveBox != null || ArchiveRetentionRefusalText.Parent is not Panel panel)
                return;
            int index = panel.Children.IndexOf(ArchiveRetentionRefusalText) + 1;
            Style? quiet = TryFindResource("QuietButtonStyle") as Style;

            // Archive retention days, written into INSPECT's settings (InspectLiveSettings.WriteArchiveRetention).
            var retentionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            retentionRow.Children.Add(new TextBlock { Text = "Archive retention (days, shared with INSPECT): ", VerticalAlignment = VerticalAlignment.Center });
            _retentionBox = new TextBox
            {
                Width = 70, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center,
                Text = InspectLiveSettings.ReadArchiveRetention(inspectionsFolder).ToString(CultureInfo.InvariantCulture)
            };
            _retentionBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit); // whole numbers only
            var saveRetention = new Button { Content = "Save", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            if (quiet != null) saveRetention.Style = quiet;
            _retentionStatus = new TextBlock { Foreground = System.Windows.Media.Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            saveRetention.Click += (_, _) => SaveArchiveRetention();
            retentionRow.Children.Add(_retentionBox);
            retentionRow.Children.Add(saveRetention);
            retentionRow.Children.Add(_retentionStatus);

            var settings = HousekeepingSettings.Load();
            _cleanArchiveBox = new CheckBox { Content = "RED cleans the Archive", IsChecked = settings.CleanArchive, Margin = new Thickness(0, 8, 0, 0) };
            _cleanArchiveBox.Click += (_, _) =>
            {
                var s = HousekeepingSettings.Load();
                s.CleanArchive = _cleanArchiveBox.IsChecked == true;
                s.Save();
            };
            _purgeJobsBox = new CheckBox { Content = "RED purges job folders", IsChecked = settings.PurgeJobFolders, Margin = new Thickness(0, 4, 0, 0) };
            _purgeJobsBox.Click += (_, _) =>
            {
                var s = HousekeepingSettings.Load();
                s.PurgeJobFolders = _purgeJobsBox.IsChecked == true;
                s.Save();
            };

            // F-020 R2/R4: RED downloads the offline data by itself at every start (App.Housekeeping.cs); this button is
            // only for a manual refresh, and says how it went on the line beside it.
            var offlineRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var offline = new Button { Content = "Download Offline Data", Padding = new Thickness(10, 4, 10, 4) };
            if (quiet != null) offline.Style = quiet;
            var offlineStatus = new TextBlock { Foreground = System.Windows.Media.Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            offline.Click += async (_, _) => await DownloadOfflineDataAsync(offline, offlineStatus);
            offlineRow.Children.Add(offline);
            offlineRow.Children.Add(offlineStatus);

            panel.Children.Insert(index, offlineRow);
            panel.Children.Insert(index, _purgeJobsBox);
            panel.Children.Insert(index, _cleanArchiveBox);
            panel.Children.Insert(index, retentionRow);
        }

        private void SaveArchiveRetention()
        {
            if (_retentionBox == null || _retentionStatus == null) return;
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            string? problem = int.TryParse(_retentionBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int days)
                ? InspectLiveSettings.WriteArchiveRetention(folder, days)
                : $"Enter a number of days from 1 to {InspectLiveSettings.MaxArchiveRetention}.";
            _retentionStatus.Text = problem ?? $"Saved: {days} days, in INSPECT's settings too.";
            _retentionStatus.Foreground = problem == null ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.Firebrick;
            ShowArchiveRetention(folder);
        }

        private async System.Threading.Tasks.Task DownloadOfflineDataAsync(Button button, TextBlock status)
        {
            button.IsEnabled = false;
            status.Text = "Downloading…";
            try
            {
                // The website calls use the Strand session's login: the saved login is used when nobody is signed in.
                var session = OfflineCacheService.SessionForDownload(StrandWebsiteSession.Default);
                if (session == null)
                {
                    status.Text = "Sign in to Strand first (Settings > Strand Account, or the ORDERS tab).";
                    return;
                }
                var result = await OfflineCacheService.RefreshAsync(session, Enum.GetValues<OfflineCache>());
                status.Text = OfflineCacheService.ResultText(result);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Info($"Download Offline Data failed: {ex.GetType().Name}");
                status.Text = "The download did not finish. RED tries again at its next start.";
            }
            finally { button.IsEnabled = true; }
        }
    }
}
