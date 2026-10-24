using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Owner 2026-10-24: the checklist search also lists an item when one of its common comments contains the search text
    // (all its quick comments, INSPECT's company and personal lists, My saved comments), even if its own text does not.
    // Such rows carry a small "🔍 in comments" marker, and while the search is typed the opened item lists its matching
    // comments first, highlighted, so one tap applies one. OFI, REQ and INC still apply on top; ALL clears the search.
    public partial class MainWindow
    {
        private static readonly Brush CommentSearchHighlightBackground = FrozenBrush(Color.FromRgb(254, 243, 199));
        private static readonly Brush CommentSearchHighlightBorder = FrozenBrush(Color.FromRgb(217, 119, 6));
        private static readonly Brush CommentSearchMarkerForeground = FrozenBrush(Color.FromRgb(146, 64, 14));
        private static readonly Brush QuickSuggestionDefaultBackground = FrozenBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        private static readonly Brush QuickSuggestionDefaultBorder = FrozenBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

        // "Keep typing smooth on a large report": when the last list build (with its layout) took this long, a typed search
        // waits for a pause in typing instead of rebuilding the list on every key. Quick reports rebuild at once, as before.
        private const double SlowChecklistBuildMs = 150;
        private static readonly TimeSpan SearchRebuildDelay = TimeSpan.FromMilliseconds(300);
        private double _lastChecklistBuildMs;
        private DispatcherTimer? _searchRebuildTimer;

        private ChecklistCommentIndex? _commentSearchIndex;
        private InspectionFile? _commentSearchIndexReport;

        // The search text the checklist was last built with; a row rebuilt on its own uses it too.
        private string _checklistSearchText = "";

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Read after the new rows' layout and rendering (Loaded runs after Render), before background work queued at open.
        private void NoteChecklistBuilt(Stopwatch timer) =>
            Dispatcher.BeginInvoke(new Action(() => _lastChecklistBuildMs = timer.Elapsed.TotalMilliseconds), DispatcherPriority.Loaded);

        private void ScheduleSearchRebuild()
        {
            if (_searchRebuildTimer == null)
            {
                _searchRebuildTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = SearchRebuildDelay };
                _searchRebuildTimer.Tick += (_, _) => FlushPendingSearchRebuild();
                // Leaving the box (a tap on a row or a comment box) builds the pending search once that tap is handled,
                // so the list is not rebuilt later under the user's next tap or typing.
                SearchFilterBox.LostKeyboardFocus += (_, _) =>
                    Dispatcher.BeginInvoke(new Action(FlushPendingSearchRebuild), DispatcherPriority.Input);
            }
            _searchRebuildTimer.Stop();
            _searchRebuildTimer.Start();
        }

        private void FlushPendingSearchRebuild()
        {
            if (_searchRebuildTimer?.IsEnabled != true)
                return;
            _searchRebuildTimer.Stop();
            if (IsLoaded)
                PopulateTreeView(SearchFilterBox.Text);
        }

        // After a list build with another search, the open item's comment lists follow it (the quick slots only while
        // they are shown; a dismissed overlay stays closed).
        private string _rightPanelSearchText = "";

        private void RefreshOpenItemCommentMatches()
        {
            if (_currentItem == null || string.Equals(_rightPanelSearchText, _checklistSearchText, StringComparison.Ordinal))
                return;
            LoadSavedComments();
            if (QuickSuggestionsOverlay.Visibility == Visibility.Visible)
                UpdateQuickSuggestionsOverlay();
        }

        // Called once per list build or item load while a search is typed. Each list is read once per index; a new index
        // is built when the report, the inspection type or any list's file changes.
        private ChecklistCommentIndex? RefreshCommentSearchIndex()
        {
            string? code = _currentInspectionCode;
            if (_currentInspection == null || string.IsNullOrWhiteSpace(code))
                return _commentSearchIndex = null;
            try
            {
                string? folder = CommentLibraryFolder();
                string stamp = string.Join("\u001e", code, folder ?? "", _quickCommentsService.Stamp,
                    InspectCommentLibrary.Stamp(folder), _userDataService?.SavedCommentsStamp(code) ?? "none");
                if (_commentSearchIndex != null && ReferenceEquals(_commentSearchIndexReport, _currentInspection) &&
                    _commentSearchIndex.Stamp == stamp)
                    return _commentSearchIndex;

                var quick = _quickCommentsService.AllSuggestionsFor(code);
                var company = InspectCommentLibrary.All(folder, InspectCommentLibrary.Kind.Company);
                var inspectUser = InspectCommentLibrary.All(folder, InspectCommentLibrary.Kind.User);
                var saved = _userDataService?.GetSavedCommentsByItem(code) ?? new Dictionary<string, List<string>>();
                _commentSearchIndexReport = _currentInspection;
                return _commentSearchIndex = new ChecklistCommentIndex(
                    item => CommonCommentsFor(item, quick, company, inspectUser, saved), stamp);
            }
            catch (Exception ex)
            {
                // The search then matches the items' own text only, as before; no message.
                DiagnosticLogService.Log("Checklist comment search: the comment lists could not be read", ex);
                return _commentSearchIndex = null;
            }
        }

        private IEnumerable<ChecklistComment> CommonCommentsFor(Item item,
            IReadOnlyDictionary<string, List<string>> quick,
            IReadOnlyDictionary<string, List<string>> company,
            IReadOnlyDictionary<string, List<string>> inspectUser,
            IReadOnlyDictionary<string, List<string>> saved)
        {
            string itemId = item.ItemId.ToString(CultureInfo.InvariantCulture);
            if (saved.TryGetValue(GetSavedCommentKey(item), out var mine) && mine != null)
                foreach (string text in mine)
                    yield return new ChecklistComment(ChecklistCommentSource.Saved, text);
            if (quick.TryGetValue(item.Number ?? "", out var common) && common != null)
                foreach (string text in common)
                    yield return new ChecklistComment(ChecklistCommentSource.Quick, text);
            if (company.TryGetValue(itemId, out var companyEntries) && companyEntries != null)
                foreach (string text in companyEntries)
                    yield return new ChecklistComment(ChecklistCommentSource.Company, text);
            if (inspectUser.TryGetValue(itemId, out var userEntries) && userEntries != null)
                foreach (string text in userEntries)
                    yield return new ChecklistComment(ChecklistCommentSource.InspectUser, text);
        }

        private ChecklistCommentIndex? CurrentCommentSearchIndex() =>
            _commentSearchIndex != null && ReferenceEquals(_commentSearchIndexReport, _currentInspection)
                ? _commentSearchIndex
                : RefreshCommentSearchIndex();

        private bool MatchesThroughComments(Item item, string? filter) =>
            !string.IsNullOrWhiteSpace(filter) && CurrentCommentSearchIndex()?.HasMatch(item, filter) == true;

        // Classic tree and v2.1 rows: true when only a common comment matches (the row shows the marker).
        private bool IsCommentOnlySearchMatch(Item item, bool labelMatches, string? filter) =>
            !string.IsNullOrWhiteSpace(filter) &&
            ChecklistCommentSearch.Match(labelMatches, item, filter, CurrentCommentSearchIndex()) == ChecklistSearchMatch.CommentOnly;

        // The v2.1 list's own-text rule (number, name, the item's comment, its value).
        private static bool InlineSearchLabelMatches(Item item, string filterLower) =>
            item.Name?.ToLower().Contains(filterLower) == true ||
            item.Number?.ToLower().Contains(filterLower) == true ||
            item.Comments?.ToLower().Contains(filterLower) == true ||
            item.Value?.ToString()?.ToLower().Contains(filterLower) == true;

        private bool IsInlineCommentOnlySearchMatch(Item item) =>
            !string.IsNullOrWhiteSpace(_checklistSearchText) &&
            IsCommentOnlySearchMatch(item, InlineSearchLabelMatches(item, _checklistSearchText.ToLower()), _checklistSearchText);

        // The opened item's comments that contain the search the list was built with (none without a search), so the
        // comment lists always agree with the list on screen. With a source, only that list's comments.
        private IReadOnlyList<ChecklistComment> SearchMatchedComments(Item? item, ChecklistCommentSource? source = null)
        {
            string filter = _checklistSearchText;
            if (item == null || string.IsNullOrWhiteSpace(filter))
                return Array.Empty<ChecklistComment>();
            return RefreshCommentSearchIndex()?.Matching(item, filter, source) ?? (IReadOnlyList<ChecklistComment>)Array.Empty<ChecklistComment>();
        }

        private static bool IsShownAsMatch(IReadOnlyList<ChecklistComment> matches, ChecklistCommentSource source, string text) =>
            matches.Any(match => match.Source == source && string.Equals(match.Text.Trim(), text?.Trim(), StringComparison.OrdinalIgnoreCase));

        // Classic tree: the marker follows the item's name.
        private void AddCommentSearchMarker(TextBlock nameText)
        {
            nameText.Inlines.Add(new Run("  " + ChecklistCommentSearch.MarkerText)
            {
                FontSize = Math.Max(9, _checklistFontSize - 2),
                FontStyle = FontStyles.Italic,
                Foreground = CommentSearchMarkerForeground,
                ToolTip = ChecklistCommentSearch.MarkerToolTip
            });
        }

        // v2.1 rows: a small chip beside the row's other chips.
        private Border CreateCommentSearchBadge()
        {
            var badge = CreateInlineBadge(ChecklistCommentSearch.MarkerText, CommentSearchHighlightBackground,
                CommentSearchMarkerForeground, FontWeights.Normal);
            badge.ToolTip = ChecklistCommentSearch.MarkerToolTip;
            return badge;
        }

        private static void HighlightMatchingComment(Control control)
        {
            control.Background = CommentSearchHighlightBackground;
            control.BorderBrush = CommentSearchHighlightBorder;
            control.BorderThickness = new Thickness(2);
        }

        private TextBlock CreateMatchingCommentsHeader(double fontSize) => new TextBlock
        {
            Text = $"🔍 Matches “{_checklistSearchText.Trim()}”",
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = CommentSearchMarkerForeground,
            Margin = new Thickness(2, 2, 2, 4),
            TextWrapping = TextWrapping.Wrap
        };

        private static StackPanel MatchingCommentContent(ChecklistComment match, double fontSize, double? maxTextHeight) => new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = match.Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = maxTextHeight ?? double.PositiveInfinity,
                    FontSize = fontSize,
                    Foreground = FrozenBrush(Color.FromRgb(30, 41, 59))
                },
                new TextBlock
                {
                    Text = ChecklistCommentSearch.SourceLabel(match.Source),
                    FontSize = Math.Max(8, fontSize - 2),
                    FontStyle = FontStyles.Italic,
                    Foreground = CommentSearchMarkerForeground
                }
            }
        };

        // Right panel ("MY SAVED COMMENTS"): the matching comments of every list go first, highlighted. A tap adds a saved
        // or common comment like a saved comment, an INSPECT entry the INSPECT way (as in the v2.1 drawer). A matching
        // saved comment keeps its X.
        private void AddMatchingCommentsToSavedPanel(IReadOnlyList<ChecklistComment> matches)
        {
            if (matches.Count == 0)
                return;
            SavedCommentsPanel.Children.Add(CreateMatchingCommentsHeader(11));
            foreach (var match in matches)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var button = new Button
                {
                    Content = MatchingCommentContent(match, 11, null),
                    Tag = match.Text,
                    Style = (Style)FindResource("SavedCommentButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Width = double.NaN,
                    MaxWidth = double.PositiveInfinity,
                    ToolTip = match.Text
                };
                HighlightMatchingComment(button);
                if (match.Source is ChecklistCommentSource.Company or ChecklistCommentSource.InspectUser)
                    button.Click += RightPanelInspectEntry_Click;
                else
                    button.Click += SavedCommentButton_Click;
                Grid.SetColumn(button, 0);
                row.Children.Add(button);
                if (match.Source == ChecklistCommentSource.Saved)
                {
                    var deleteButton = new Button { Content = "X", Tag = match.Text, Style = (Style)FindResource("DeleteButton") };
                    deleteButton.Click += DeleteSavedCommentButton_Click;
                    Grid.SetColumn(deleteButton, 1);
                    row.Children.Add(deleteButton);
                }
                SavedCommentsPanel.Children.Add(row);
            }
        }

        // F-024 B2: "{existing}, {entry}"; an empty comment takes the entry.
        private void RightPanelInspectEntry_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string entry } || _readOnlyMode)
                return;
            CommentsTextBox.Text = InspectCommentLibrary.Append(CommentsTextBox.Text, entry);
            CommentsTextBox.CaretIndex = CommentsTextBox.Text.Length;
            AutoSetFailIfApplicable();
            MarkUnsaved();
        }

        // v2.1 comment drawer's side panel: the same, with each list's own tap (saved and common comments are added like a
        // saved comment, INSPECT's entries the INSPECT way).
        private void AddInlineMatchingComments(Panel list, Item item, IReadOnlyList<ChecklistComment> matches)
        {
            if (matches.Count == 0)
                return;
            double fontSize = Math.Max(9, _checklistFontSize - 3);
            list.Children.Add(CreateMatchingCommentsHeader(fontSize));
            foreach (var match in matches)
            {
                if (match.Source == ChecklistCommentSource.Saved)
                {
                    list.Children.Add(CreateInlineSavedCommentRow(item, match.Text, highlight: true));
                    continue;
                }
                bool inspectList = match.Source is ChecklistCommentSource.Company or ChecklistCommentSource.InspectUser;
                var button = new Button
                {
                    Content = MatchingCommentContent(match, fontSize, 48),
                    Tag = inspectList ? new InspectCommentAction(item, match.Text) : new InlineQuickCommentAction(item, match.Text),
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    ToolTip = match.Text
                };
                HighlightMatchingComment(button);
                if (inspectList)
                    button.Click += InlineInspectCommentButton_Click;
                else
                    button.Click += InlineSavedCommentButton_Click;
                list.Children.Add(button);
            }
        }

        // Quick-comment slots: while a search is typed, the item's quick comments that contain it take the slots first (also
        // one the user saved too). Without a search the slots are exactly as before.
        private List<string> QuickCommentsMatchingFirst(Item? item, IEnumerable<string> topSuggestions, int slots)
        {
            var top = (topSuggestions ?? Enumerable.Empty<string>()).ToList();
            if (string.IsNullOrWhiteSpace(_checklistSearchText))
                return top;
            var matching = SearchMatchedComments(item, ChecklistCommentSource.Quick).Select(match => match.Text);
            return matching.Concat(top)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(slots)
                .ToList();
        }

        private void MarkQuickSuggestionMatches()
        {
            string filter = _checklistSearchText;
            foreach (var (button, text) in new[] { (QuickSuggestionA, QuickSuggestionAText), (QuickSuggestionB, QuickSuggestionBText), (QuickSuggestionC, QuickSuggestionCText) })
            {
                bool match = button.Visibility == Visibility.Visible && ChecklistCommentSearch.Contains(text.Text, filter);
                button.Background = match ? CommentSearchHighlightBackground : QuickSuggestionDefaultBackground;
                button.BorderBrush = match ? CommentSearchHighlightBorder : QuickSuggestionDefaultBorder;
                button.BorderThickness = new Thickness(match ? 2 : 1);
            }
        }
    }
}
