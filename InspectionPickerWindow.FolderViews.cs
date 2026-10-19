using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor.Services;

namespace InspectionEditor
{
    public enum FolderView { MyList, Outbox, Archive, Review }

    // F-014 Outbox and Archive views in the home window: view buttons with counts, header-only rows with Status and
    // Processed columns, a live folder watcher (renames included), Move to My List / Move to Archive, and the Outbox
    // "Sync" button for kind NonDropBox only (L8). Review is F-018.
    public partial class InspectionPickerWindow
    {
        private FolderView _folderView = FolderView.MyList;
        private WrapPanel? _folderViewBar;
        private readonly Dictionary<FolderView, Button> _folderViewButtons = new();
        private Button? _moveToMyListButton, _moveToArchiveButton, _syncButton, _moveToReviewButton, _approveButton, _removeButton, _addServiceButton;
        private AddServiceWindow? _addWindow;
        private bool _addPressedWhileDownloading;
        private TextBlock? _folderViewNote;
        private FileSystemWatcher? _folderWatcher;
        private DispatcherTimer? _folderWatchDebounce;
        private GridViewColumn? _statusColumn, _processedColumn;

        public FolderView CurrentFolderView => _folderView;

        private string? InspectionsRoot()
        {
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (!string.IsNullOrWhiteSpace(folder)) return folder;
            return string.IsNullOrWhiteSpace(_currentFolderPath) ? null : Path.GetDirectoryName(_currentFolderPath.TrimEnd('\\'));
        }

        private static InspectFolder ToInspectFolder(FolderView view) => view switch
        {
            FolderView.Outbox => InspectFolder.Outbox,
            FolderView.Archive => InspectFolder.Archive,
            FolderView.Review => InspectFolder.Review,
            _ => InspectFolder.MyList
        };

        private static string Caption(FolderView view) => view == FolderView.MyList ? "My List" : view.ToString();

        // Called from InitializeColumnLayout: the two F-014 columns, shown only in the Outbox and Archive views.
        private void RegisterFolderViewColumns()
        {
            _statusColumn ??= new GridViewColumn { Header = "Status", Width = 130, DisplayMemberBinding = new Binding(nameof(InspectionFileInfo.StatusName)) };
            _processedColumn ??= new GridViewColumn { Header = "Processed", Width = 150, DisplayMemberBinding = new Binding(nameof(InspectionFileInfo.ProcessedDisplay)) };
            _columnsByKey["Status"] = _statusColumn;
            _columnsByKey["Processed"] = _processedColumn;
            if (!_columnOrder.Contains("Status")) _columnOrder.Add("Status");
            if (!_columnOrder.Contains("Processed")) _columnOrder.Add("Processed");
        }

        private bool SuppressFolderViewColumn(string key) => (key is "Status" or "Processed") && _folderView == FolderView.MyList;

        private void EnsureFolderViewBar()
        {
            if (_folderViewBar != null || FolderPathText.Parent is not Panel header)
                return;
            _folderViewBar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var view in new[] { FolderView.MyList, FolderView.Outbox, FolderView.Archive, FolderView.Review })
            {
                var button = new Button { Content = Caption(view), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0), Tag = view };
                if (TryFindResource("QuietButtonStyle") is Style style) button.Style = style;
                button.Click += (_, _) => ShowFolderView(view);
                _folderViewButtons[view] = button;
                _folderViewBar.Children.Add(button);
            }
            _moveToMyListButton = CommandButton("Move to My List…", () => ShowMoveDialog(InspectFolder.MyList));
            _moveToArchiveButton = CommandButton("Move to Archive…", () => ShowMoveDialog(InspectFolder.Archive));
            _syncButton = CommandButton("Sync", async () => await RunSyncAsync());
            _moveToReviewButton = CommandButton("Move to Review…", () => ShowMoveDialog(InspectFolder.Review)); // F-018 M1
            _approveButton = CommandButton("Approve…", async () => await ShowApproveDialogAsync());           // F-018 A4
            _addServiceButton = CommandButton("Add…", OnAddServiceClick); // F-021 E1: My List only
            _folderViewBar.Children.Add(new Border { Width = 14 });
            _folderViewBar.Children.Add(_addServiceButton);
            _folderViewBar.Children.Add(_approveButton);
            _folderViewBar.Children.Add(_moveToMyListButton);
            _folderViewBar.Children.Add(_moveToArchiveButton);
            _folderViewBar.Children.Add(_moveToReviewButton);
            _folderViewBar.Children.Add(_syncButton);
            _removeButton = CommandButton("Remove…", ShowRemoveDialog); // F-019 D1, every view
            _folderViewBar.Children.Add(_removeButton);
            header.Children.Add(_folderViewBar);
            _folderViewNote = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            header.Children.Add(_folderViewNote);
            UpdateFolderViewBar();
        }

        private Button CommandButton(string text, Action click)
        {
            var button = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0) };
            if (TryFindResource("QuietButtonStyle") is Style style) button.Style = style;
            button.Click += (_, _) => click();
            return button;
        }

        // L5: captions with counts; the Outbox shows an empty or filled indicator.
        private void UpdateFolderViewBar()
        {
            if (_folderViewBar == null)
                return;
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) &&
                !string.Equals(_folderWatcher?.Path, _currentFolderPath, StringComparison.OrdinalIgnoreCase))
                StartFolderWatcher(_currentFolderPath); // L6 for whichever folder is listed
            string? root = InspectionsRoot();
            bool reviewer = ReviewAccess.Allowed(root); // F-018 W1/W2
            if (!reviewer && _folderView == FolderView.Review && root != null)
            {
                _folderView = FolderView.MyList; // the Review folder emptied or the setting changed
                LoadInspections(InspectFolders.PathOf(root, InspectFolder.MyList));
            }
            foreach (var (view, button) in _folderViewButtons)
            {
                button.Visibility = view != FolderView.Review || reviewer ? Visibility.Visible : Visibility.Collapsed;
                int count = 0;
                try
                {
                    string folder = root == null ? "" : InspectFolders.PathOf(root, ToInspectFolder(view));
                    if (Directory.Exists(folder))
                        count = Directory.EnumerateFiles(folder, "*.ins", SearchOption.TopDirectoryOnly).Count();
                }
                catch (Exception) { }
                string indicator = view == FolderView.Outbox ? (count > 0 ? " ●" : " ○") : "";
                button.Content = $"{Caption(view)} ({count}){indicator}";
                button.FontWeight = view == _folderView ? FontWeights.Bold : FontWeights.Normal;
                button.Foreground = view == FolderView.Outbox && count > 0
                    ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
                    : new SolidColorBrush(Color.FromRgb(31, 41, 51));
            }
            _moveToMyListButton!.Visibility = _folderView == FolderView.MyList ? Visibility.Collapsed : Visibility.Visible;
            _moveToArchiveButton!.Visibility = _folderView == FolderView.Archive ? Visibility.Collapsed : Visibility.Visible;
            _moveToReviewButton!.Visibility = reviewer && _folderView != FolderView.Review ? Visibility.Visible : Visibility.Collapsed;
            _approveButton!.Visibility = reviewer && _folderView == FolderView.Review ? Visibility.Visible : Visibility.Collapsed;
            _addServiceButton!.Visibility = _folderView == FolderView.MyList ? Visibility.Visible : Visibility.Collapsed;
            var kind = InspectUserType.Classify(InspectorProfileService.Load()?.UserType);
            _syncButton!.Visibility = _folderView == FolderView.Outbox && kind == InspectUserKind.NonDropBox ? Visibility.Visible : Visibility.Collapsed;
            UpdateFolderViewNote(root, kind);
        }

        private void UpdateFolderViewNote(string? root, InspectUserKind kind)
        {
            if (_folderViewNote == null || root == null)
                return;
            var notes = new List<string>();
            if (_folderView == FolderView.Outbox)
            {
                var waiting = SubmittedTracker.Load().Where(e => File.Exists(Path.Combine(root, "Outbox", e.Number + ".ins")))
                    .Select(e => (e.Number, Note: SubmittedTracker.OutboxNote(e.Number, root, kind, DateTime.Now)))
                    .Where(n => n.Note == SubmittedTracker.NotPickedUpYet || n.Note == SubmittedTracker.NotSentYet).ToList();
                notes.AddRange(waiting.Select(w => $"{w.Number}: {w.Note}"));
            }
            if (_folderView != FolderView.MyList)
                notes.AddRange(SubmittedTracker.NotReturned(root, DateTime.Now).Select(n => $"{n}: {SubmittedTracker.NotReturnedYet}"));
            _folderViewNote.Text = string.Join(Environment.NewLine, notes);
            _folderViewNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowFolderView(FolderView view)
        {
            string? root = InspectionsRoot();
            if (root == null)
                return;
            _folderView = view;
            if (FolderPathText.Parent is Panel header && header.Children.OfType<TextBlock>().FirstOrDefault() is { } title && !ReferenceEquals(title, FolderPathText))
                title.Text = Caption(view);
            ApplyColumnSettings();
            string folder = InspectFolders.PathOf(root, ToInspectFolder(view));
            LoadInspections(folder);
            StartFolderWatcher(folder);
            UpdateFolderViewBar();
        }

        // Rows for the list: My List keeps RED's full parse; Outbox and Archive read the header only (L3).
        private InspectionFileInfo? ParseListRow(string filePath)
        {
            string folderName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "";
            if (!folderName.Equals("Outbox", StringComparison.OrdinalIgnoreCase) && !folderName.Equals("Archive", StringComparison.OrdinalIgnoreCase) &&
                !folderName.Equals("Review", StringComparison.OrdinalIgnoreCase)) // F-018 V1: Review reads the header only, like Outbox
                return ParseInspectionFile(filePath);
            try
            {
                var header = InspectionHeaderReader.Read(filePath);
                if (header == null)
                    return ParseInspectionFile(filePath);
                string address = header["Address"] ?? "";
                string city = header["City"] ?? "";
                DateTime date = DateTimeOffset.TryParse(header["DateInspected"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.LocalDateTime : File.GetLastWriteTime(filePath);
                return new InspectionFileInfo
                {
                    FilePath = filePath,
                    Address = string.IsNullOrWhiteSpace(city) ? address : $"{address}, {city}",
                    Subdivision = header["Project"] ?? "",
                    InspectionCode = header["InspectionCode"] ?? GetInspectionCodeFromFileName(filePath),
                    Builder = header["BuilderName"] ?? "",
                    Date = date,
                    ContactName = header["Contact"] ?? "",
                    ContactPhone = header["ContactNumber"] ?? "",
                    InspectorName = header["InspectorName"] ?? "",
                    InspectorPhone = header["InspectorPhone"] ?? "",
                    LotNumber = header["Lot"] ?? "",
                    EditStatus = header.HasSignature ? "S" : "Y",
                    StatusName = header.StatusName,
                    ProcessedDisplay = header.ProcessedDisplay
                };
            }
            catch (Exception)
            {
                return ParseInspectionFile(filePath);
            }
        }

        // L6: live refresh on created, changed, deleted and renamed .ins files, debounced.
        private void StartFolderWatcher(string folder)
        {
            try
            {
                _folderWatcher?.Dispose();
                _folderWatcher = null;
                if (!Directory.Exists(folder))
                    return;
                _folderWatchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                _folderWatchDebounce.Tick -= FolderWatchDebounce_Tick;
                _folderWatchDebounce.Tick += FolderWatchDebounce_Tick;
                _folderWatcher = new FileSystemWatcher(folder, "*.ins")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(new Action(() => { _folderWatchDebounce.Stop(); _folderWatchDebounce.Start(); }));
                _folderWatcher.Created += changed;
                _folderWatcher.Changed += changed;
                _folderWatcher.Deleted += changed;
                _folderWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(new Action(() => { _folderWatchDebounce.Stop(); _folderWatchDebounce.Start(); }));
                _folderWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Folder watcher could not start", ex);
            }
        }

        private void FolderWatchDebounce_Tick(object? sender, EventArgs e)
        {
            _folderWatchDebounce?.Stop();
            if (!IsLoaded || MyListPanel.Visibility != Visibility.Visible)
                return;
            RefreshCurrentMyListFolder();
            UpdateFolderViewBar();
        }

        // M1-M5: pick rows, move them one by one, report "{n} of {m} inspections were moved."
        private void ShowMoveDialog(InspectFolder destination)
        {
            string? root = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(root))
                return;
            // F-018 W2: only a reviewer moves files to Review.
            if (destination == InspectFolder.Review && !ReviewAccess.Allowed(root))
            {
                MessageBox.Show(this, ReviewAccess.MoveRefused, "Move", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // F-013 B0: no Move while Sync works on these folders.
            if (StrandSyncService.IsRunning)
            {
                MessageBox.Show(this, "Wait for the Sync to finish, then move.", "Move", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // M1: Move works only on this view's own folder of the Inspections folder, never on a folder picked with Browse.
            string viewFolder = InspectFolders.PathOf(root, ToInspectFolder(_folderView));
            if (string.IsNullOrWhiteSpace(_currentFolderPath) ||
                !string.Equals(Path.GetFullPath(_currentFolderPath).TrimEnd('\\'), Path.GetFullPath(viewFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"Move works only on the {Caption(_folderView)} folder of your Inspections folder. Open the {Caption(_folderView)} view again to move files.",
                    "Move", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string destCaption = destination == InspectFolder.MyList ? "My List" : destination.ToString();
            var chosen = PickRows($"Move to {destCaption}", $"Move to {destCaption}", "There is nothing to move in this view.");
            if (chosen == null)
                return;

            var openPaths = Application.Current.Windows.OfType<MainWindow>().Where(w => w.HasOpenInspection).Select(w => w.OpenInspectionFilePath).ToList();
            int moved = 0;
            var problems = new List<string>();
            foreach (var row in chosen)
            {
                if (openPaths.Any(p => PathEquals(p, row.FilePath)))
                {
                    problems.Add($"{row.FileName} is open in an editor; close it first.");
                    continue;
                }
                var result = FolderMoveService.Move(row.FilePath, root, destination,
                    question => MessageBox.Show(this, question, "Move", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
                if (result.Outcome == MoveOutcome.Moved) moved++;
                else if (result.Outcome != MoveOutcome.Cancelled && result.Message.Length > 0) problems.Add(result.Message);
            }
            string text = FolderMoveService.Summary(moved, chosen.Count);
            if (problems.Count > 0) text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, problems);
            MessageBox.Show(this, text, $"Move to {destCaption}", MessageBoxButton.OK, problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            RefreshCurrentMyListFolder();
            UpdateFolderViewBar();
        }

        // F-021 E1-E3: one Add window at a time; the three offline caches present and from today; the profile guard.
        private void OnAddServiceClick()
        {
            if (_addWindow != null) { _addWindow.Activate(); return; }
            var profile = ProfileGuard.Require(this, "Add");
            if (profile == null)
                return;
            if (!AddServiceService.CachesReady(DateTime.Now))
            {
                if (OfflineCacheService.IsDownloading)
                {
                    if (!_addPressedWhileDownloading)
                    {
                        _addPressedWhileDownloading = true; // one flag for any number of presses
                        OfflineCacheService.DownloadFinished += OnOfflineDownloadFinishedForAdd;
                    }
                    MessageBox.Show(this, AddServiceService.ReceivingText, AddServiceService.ReceivingCaption, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DiagnosticLogService.Info("Add refused: the offline data is missing or not from today.");
                MessageBox.Show(this, AddServiceService.MissingText, AddServiceService.MissingCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            OpenAddWindow(profile);
        }

        // E2: when the download ends, open the wizard only if the caches are now fresh and no Add window is open.
        private void OnOfflineDownloadFinishedForAdd() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (OfflineCacheService.IsDownloading || !_addPressedWhileDownloading)
                return;
            OfflineCacheService.DownloadFinished -= OnOfflineDownloadFinishedForAdd;
            _addPressedWhileDownloading = false;
            if (_addWindow != null)
                return;
            if (!AddServiceService.CachesReady(DateTime.Now))
            {
                MessageBox.Show(this, AddServiceService.MissingText, AddServiceService.MissingCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var profile = ProfileGuard.Require(this, "Add");
            if (profile != null) OpenAddWindow(profile);
        }));

        private void OpenAddWindow(CompleteProfile profile)
        {
            var window = new AddServiceWindow(profile, InspectorProfileService.Load()?.UserType, StrandWebsiteSession.Default, IsOpenInRed) { Owner = this };
            if (!window.CachesUsable)
            {
                // R5: a cache that cannot be read counts as missing.
                DiagnosticLogService.Info("Add refused: an offline cache could not be read.");
                MessageBox.Show(this, OfflineCacheService.MissingText, OfflineCacheService.MissingCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _addWindow = window;
            _addWindow.Closed += (_, _) =>
            {
                var window = _addWindow;
                _addWindow = null;
                RefreshCurrentMyListFolder();
                UpdateFolderViewBar();
                if (window?.OpenRequested == true && window.CreatedPath != null)
                    InspectionOpenRequested?.Invoke(this, window.CreatedPath);
            };
            _addWindow.Show();
        }

        // F-019 D1-D5: remove the chosen .ins files from this device (and, through Dropbox, every device). Open editors
        // for them close without saving first (their unsaved edits are kept as a recovery copy). Job folders stay.
        private void ShowRemoveDialog()
        {
            string? root = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(root))
                return;
            if (StrandSyncService.IsRunning)
            {
                MessageBox.Show(this, "Wait for the Sync to finish, then remove.", "Remove", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string viewFolder = InspectFolders.PathOf(root, ToInspectFolder(_folderView));
            if (string.IsNullOrWhiteSpace(_currentFolderPath) ||
                !string.Equals(Path.GetFullPath(_currentFolderPath).TrimEnd('\\'), Path.GetFullPath(viewFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"Remove works only on the {Caption(_folderView)} folder of your Inspections folder.", "Remove", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var chosen = PickRows("Remove from device", "Remove", "There is nothing to remove in this view.");
            if (chosen == null)
                return;
            if (MessageBox.Show(this, RemoveFromDeviceService.Question(chosen.Count), "Remove", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            int removed = 0;
            var problems = new List<string>();
            foreach (var row in chosen)
            {
                var editor = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.HasOpenInspection && PathEquals(w.OpenInspectionFilePath, row.FilePath));
                if (editor != null && !editor.CloseForRemoval())
                {
                    problems.Add($"{row.FileName} is still open in an editor; it was not removed.");
                    continue;
                }
                if (RemoveFromDeviceService.Remove(row.FilePath, root)) removed++;
                else problems.Add($"{row.FileName} could not be removed.");
            }
            string text = $"{removed} of {chosen.Count} inspections were removed.";
            if (problems.Count > 0) text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, problems);
            MessageBox.Show(this, text, "Remove", MessageBoxButton.OK, problems.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            RefreshCurrentMyListFolder();
            UpdateFolderViewBar();
        }

        // F-018 A4: Approve from the Review list, in list order; the first failure stops the run.
        private async Task ShowApproveDialogAsync()
        {
            string? root = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(root) || _folderView != FolderView.Review)
                return;
            if (!ReviewAccess.Allowed(root))
            {
                MessageBox.Show(this, ReviewAccess.ApprovePermissionRefused, "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (StrandSyncService.IsRunning)
            {
                MessageBox.Show(this, "Wait for the Sync to finish, then approve.", "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // W3: only the Review folder of the Inspections folder, never a folder picked with Browse.
            if (string.IsNullOrWhiteSpace(_currentFolderPath) ||
                !string.Equals(Path.GetFullPath(_currentFolderPath).TrimEnd('\\'), Path.GetFullPath(ReviewAccess.ReviewFolder(root)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Approve works only on the Review folder of your Inspections folder. Open the Review view again to approve.", "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var chosen = PickRows("Approve", "Approve", "There is nothing to approve in this view.");
            if (chosen == null)
                return;
            var profile = ProfileGuard.Require(this, "Approve");
            if (profile == null)
                return;
            if (!SubmitState.IsLiveVerified("Approve") && MessageBox.Show(this,
                    "This is the first live Approve on this device. The back office has never received an approval flag (F-018 §7 V3). Is the owner present?",
                    "First live Approve", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var openPaths = Application.Current.Windows.OfType<MainWindow>().Where(w => w.HasOpenInspection).Select(w => w.OpenInspectionFilePath).ToList();
            _approveButton!.IsEnabled = false;
            ApproveRunResult run;
            try
            {
                run = await ReviewAccess.ApproveFilesAsync(chosen.Select(r => r.FilePath).ToList(), profile,
                    InspectorProfileService.Load()?.UserType, path => openPaths.Any(p => PathEquals(p, path)));
            }
            finally { _approveButton.IsEnabled = true; }
            MessageBox.Show(this, run.Summary, "Approve", MessageBoxButton.OK, run.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (run.Approved > 0 && !SubmitState.IsLiveVerified("Approve") && MessageBox.Show(this,
                    "Owner: mark the first live Approve on this device as checked (processed by Strand as expected)?\n\nNo asks again next time.",
                    "First live Approve", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SubmitState.MarkLiveVerified("Approve");
            RefreshCurrentMyListFolder();
            UpdateFolderViewBar();
        }

        // The pick list shared by Move and Approve: list order, Select All / Unselect All. Null when nothing was chosen.
        private List<InspectionFileInfo>? PickRows(string title, string action, string emptyText)
        {
            var rows = _allInspections.ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, emptyText, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            var checks = new List<(CheckBox Box, InspectionFileInfo Row)>();
            var list = new StackPanel();
            foreach (var row in rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var box = new CheckBox { Content = $"{row.FileName}   {row.Address}", Margin = new Thickness(0, 2, 0, 2) };
                checks.Add((box, row));
                list.Children.Add(box);
            }
            var window = new Window
            {
                Title = title,
                Owner = this,
                Width = 560,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var move = new Button { Content = action, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            var selectAll = new Button { Content = "Select All", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0) };
            var unselectAll = new Button { Content = "Unselect All", Padding = new Thickness(10, 4, 10, 4) };
            selectAll.Click += (_, _) => checks.ForEach(c => c.Box.IsChecked = true);
            unselectAll.Click += (_, _) => checks.ForEach(c => c.Box.IsChecked = false);
            bool go = false;
            move.Click += (_, _) => { go = true; window.Close(); };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(selectAll); top.Children.Add(unselectAll);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            bottom.Children.Add(move); bottom.Children.Add(cancel);
            var dock = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top); dock.Children.Add(bottom);
            dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            window.Content = dock;
            window.ShowDialog();
            var chosen = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Row).ToList();
            return go && chosen.Count > 0 ? chosen : null;
        }

        // L8 / F-013 §3B: the Outbox Sync button, kind NonDropBox only.
        private async Task RunSyncAsync()
        {
            string? root = InspectionsRoot();
            if (root == null || StrandSyncService.IsRunning)
                return;
            if (InspectUserType.Classify(InspectorProfileService.Load()?.UserType) != InspectUserKind.NonDropBox)
                return;
            if (!SubmitState.IsLiveVerified(InspectUserKind.NonDropBox.ToString()) &&
                MessageBox.Show(this, "This is the first live Sync for a NonDropBox user on this device. Is the owner present?", "First live Sync",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _syncButton!.IsEnabled = false;
            StatusText.Text = StrandSyncService.SendingText;
            SyncReport report;
            try
            {
                var openUnsaved = Application.Current.Windows.OfType<MainWindow>().Where(w => w.HasUnsavedChanges).Select(w => w.OpenInspectionFilePath).ToList();
                report = await StrandSyncService.SyncAsync(new SyncOptions
                {
                    InspectionsFolder = root,
                    Session = StrandWebsiteSession.Default,
                    IsOpenWithUnsavedChanges = path => openUnsaved.Any(p => PathEquals(p, path))
                });
            }
            finally
            {
                _syncButton.IsEnabled = true;
            }
            StatusText.Text = report.Headline;
            MessageBox.Show(this, report.Text, "Sync", MessageBoxButton.OK, report.Stopped ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (!report.Stopped && !SubmitState.IsLiveVerified(InspectUserKind.NonDropBox.ToString()) &&
                MessageBox.Show(this, "Owner: mark the first live NonDropBox Sync on this device as checked?", "First live Sync",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                SubmitState.MarkLiveVerified(InspectUserKind.NonDropBox.ToString());
            RefreshCurrentMyListFolder();
            UpdateFolderViewBar();
        }
    }
}
