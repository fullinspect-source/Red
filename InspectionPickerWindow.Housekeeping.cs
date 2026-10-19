using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Settings rows for RED's housekeeping: F-015 N3 "RED cleans the Archive" (on by default) with the V1 dry run, and
    // F-019 "RED purges job folders". Placed under F-003's Archive retention text.
    public partial class InspectionPickerWindow
    {
        private CheckBox? _cleanArchiveBox, _purgeJobsBox;

        private void EnsureHousekeepingControls(string? inspectionsFolder)
        {
            if (_cleanArchiveBox == null && ArchiveRetentionRefusalText.Parent is Panel panel)
            {
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
                var dryRun = new Button { Content = "Check Archive cleanup (changes nothing)", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
                if (TryFindResource("QuietButtonStyle") is Style style) dryRun.Style = style;
                dryRun.Click += (_, _) => ShowArchiveDryRun();
                // F-019 V3: the purge dry run (read only).
                var purgeDryRun = new Button { Content = "Check job folder purge (changes nothing)", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
                if (TryFindResource("QuietButtonStyle") is Style purgeStyle) purgeDryRun.Style = purgeStyle;
                purgeDryRun.Click += (_, _) => ShowPurgeDryRun();
                int index = panel.Children.IndexOf(ArchiveRetentionRefusalText) + 1;
                if (!settings.LiveCleanupVerified)
                {
                    // App.ArchiveCleanupAllowed: launch deletions wait for the owner's supervised check.
                    panel.Children.Insert(index, new TextBlock
                    {
                        Text = "Archive cleanup starts after the owner's first supervised check. Until then RED deletes nothing at start.",
                        TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
                        Foreground = System.Windows.Media.Brushes.DimGray
                    });
                }
                // F-020 R2/R4: forced Download Offline Data.
                var offline = new Button { Content = "Download Offline Data", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
                if (TryFindResource("QuietButtonStyle") is Style offlineStyle) offline.Style = offlineStyle;
                offline.Click += async (_, _) => await DownloadOfflineDataAsync(offline);
                panel.Children.Insert(index, offline);
                panel.Children.Insert(index, purgeDryRun);
                panel.Children.Insert(index, dryRun);
                panel.Children.Insert(index, _purgeJobsBox);
                panel.Children.Insert(index, _cleanArchiveBox);
                // F-018 W1: Show Review (auto: when the Review folder has inspections; always; never).
                var reviewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                reviewRow.Children.Add(new TextBlock { Text = "Show Review: ", VerticalAlignment = VerticalAlignment.Center });
                var reviewCombo = new ComboBox { MinWidth = 220 };
                foreach (var (value, text) in new[] { (ReviewVisibility.Auto, "Auto (when Review has inspections)"), (ReviewVisibility.Always, "Always"), (ReviewVisibility.Never, "Never") })
                    reviewCombo.Items.Add(new ComboBoxItem { Content = text, Tag = value });
                var current = ReviewSettings.Load().ShowReview;
                reviewCombo.SelectedItem = reviewCombo.Items.OfType<ComboBoxItem>().First(i => (ReviewVisibility)i.Tag == current);
                reviewCombo.SelectionChanged += (_, _) =>
                {
                    if (reviewCombo.SelectedItem is ComboBoxItem { Tag: ReviewVisibility chosen })
                    {
                        var s = ReviewSettings.Load();
                        s.ShowReview = chosen;
                        s.Save();
                        UpdateFolderViewBar();
                    }
                };
                reviewRow.Children.Add(reviewCombo);
                panel.Children.Insert(index, reviewRow);
            }
        }

        private async System.Threading.Tasks.Task DownloadOfflineDataAsync(Button button)
        {
            if (OfflineCacheService.HasAll() && MessageBox.Show(this,
                    OfflineCacheService.RequirementsMetText + Environment.NewLine + Environment.NewLine + OfflineCacheService.CancelQuestion,
                    OfflineCacheService.RequirementsMetCaption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                return;
            button.IsEnabled = false;
            try
            {
                // The website calls use the Strand session's login: sign in with the saved login first if needed.
                if (!StrandWebsiteSession.Default.HasCredentials)
                    await TrySilentStrandSignInAsync();
                if (!StrandWebsiteSession.Default.HasCredentials)
                {
                    MessageBox.Show(this, "Sign in to Strand first (Settings > Strand Account, or the ORDERS tab), then download the offline data again.",
                        "Download Offline Data", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var result = await OfflineCacheService.RefreshAsync(StrandWebsiteSession.Default, Enum.GetValues<OfflineCache>());
                MessageBox.Show(this, OfflineCacheService.ResultText(result), "Download Offline Data", MessageBoxButton.OK,
                    result.Failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally { button.IsEnabled = true; }
        }

        private void ShowPurgeDryRun()
        {
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(folder))
                return;
            var result = JobFolderPurgeService.Run(folder, InspectLogWriter.Default, dryRun: true);
            string text = result.Declined
                ? "The purge would decline: the live inspections could not be established, or there are none."
                : result.Orphaned == 0
                    ? $"{result.Present} job folder(s) present, none orphaned. Nothing would be removed."
                    : $"{result.Orphaned} of {result.Present} job folder(s) have no inspection in My List, Outbox, Review or Archive. " +
                      $"The next start would remove {result.Taken}: {string.Join(", ", result.Candidates)}. Nothing was changed.";
            MessageBox.Show(this, text, "Job folder purge check", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // F-015 V1: lists the candidate count and the cutoff; deletes nothing and writes no log line.
        private void ShowArchiveDryRun()
        {
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(folder))
                return;
            var result = ArchiveRetentionService.Run(folder, InspectLogWriter.Default, DateTime.Now, dryRun: true);
            string text = result.Refused
                ? InspectLiveSettings.ArchiveRetentionRefusal(result.Days) ?? ""
                : result.MissingFolder
                    ? "There is no Archive folder."
                    : result.Declined
                        ? "The Archive folder could not be read."
                        : $"Archive retention {result.Days} days: {result.Total} file(s) last changed on or before {result.Cutoff:M/d/yyyy h:mm:ss tt}. " +
                          $"The next start would remove {result.Taken}. Nothing was changed.";
            MessageBox.Show(this, text, "Archive cleanup check", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
