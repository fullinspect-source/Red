using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    // F-007 header sections: "T. Top" and "E. End" (Footer: status, next action, internal notes) around the template
    // sections. Every write goes through SurgicalSaveService.SetHeaderValue, so a save changes only the edited keys.
    // RED has no Fees section (owner Q-21).
    public partial class MainWindow
    {
        private const string TopSectionKey = "__f007_top__";
        private const string EndSectionKey = "__f007_end__";
        private readonly Dictionary<string, TextBlock> _headerMarkerBlocks = new(StringComparer.Ordinal);
        private TextBlock? _topSummaryText;
        private TextBlock? _endSummaryText;
        private ComboBox? _nextActionCombo;
        private TextBlock? _topDateBlock;
        private HeaderRow? _nextActionRow;
        internal StackPanel? FooterSignatureSlot { get; private set; } // F-011 adds the inspector signature pad here

        private JToken? Header(string key) => _saveService.HasFile ? _saveService.GetHeaderValue(key) : null;

        private string HeaderText(string key) => HeaderRules.Text(Header(key)) ?? "";

        private bool HeaderSectionsAvailable => _currentInspection != null && _saveService.HasFile;

        // Inline checklist: Top before the template sections, End after them. Both start collapsed (RED layout choice);
        // the header line shows how many markers are open.
        private void AddInlineHeaderSection(bool top)
        {
            if (!HeaderSectionsAvailable)
                return;
            string key = top ? TopSectionKey : EndSectionKey;
            bool expanded = _expandedHeaderSections.Contains(key);
            var header = CreateHeaderSectionHeader(top, expanded);
            InlineChecklistPanel.Children.Add(header);
            if (expanded)
            {
                var body = top ? CreateTopSectionBody() : CreateEndSectionBody();
                var border = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 12, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = body
                };
                StretchInlineWidth(border);
                InlineChecklistPanel.Children.Add(border);
            }
        }

        private readonly HashSet<string> _expandedHeaderSections = new(StringComparer.Ordinal);

        private Border CreateHeaderSectionHeader(bool top, bool expanded)
        {
            var summary = new TextBlock
            {
                FontSize = _checklistFontSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (top) _topSummaryText = summary; else _endSummaryText = summary;
            UpdateHeaderSummary(top);

            var panel = new DockPanel();
            panel.Children.Add(new TextBlock
            {
                Text = expanded ? "v" : ">",
                FontSize = _checklistFontSize + 1,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(summary);
            var border = new Border
            {
                Tag = top ? TopSectionKey : EndSectionKey,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 8, 0, 4),
                Cursor = Cursors.Hand,
                Child = panel,
                ToolTip = top ? "INSPECT's Top section: job, address and visit details" : "INSPECT's End section: status, next action and notes"
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (_scrollStarted) return;
                string key = top ? TopSectionKey : EndSectionKey;
                if (!_expandedHeaderSections.Add(key))
                    _expandedHeaderSections.Remove(key);
                PopulateInlineChecklist(SearchFilterBox.Text);
                e.Handled = true;
            };
            StretchInlineWidth(border);
            return border;
        }

        private void UpdateHeaderSummary(bool top)
        {
            var block = top ? _topSummaryText : _endSummaryText;
            if (block == null || !HeaderSectionsAvailable)
                return;
            var markers = CurrentHeaderMarkers().Where(m => IsEndKey(m.Key) != top).ToList();
            string title = top ? "T - Top" : "E - End (Status)";
            block.Text = markers.Count == 0 ? title : $"{title}  ({markers.Count} to fix)";
            block.Foreground = markers.Any(m => m.Blocking)
                ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
                : new SolidColorBrush(Color.FromRgb(30, 41, 59));
        }

        private static bool IsEndKey(string key) => key is "StatusId" or "NextActionId" or "NextActionText" or "InternalNotes" or "Signature";

        private List<HeaderMarker> CurrentHeaderMarkers() => HeaderSectionsAvailable ? HeaderRules.Markers(Header) : new List<HeaderMarker>();

        private UIElement CreateTopSectionBody()
        {
            var grid = NewHeaderGrid();
            AddDisplayRow(grid, "Strand Job", "JobNumber", fixInStrand: true);
            AddDisplayRow(grid, "Project", "Project", fixInStrand: true);
            AddDisplayRow(grid, "Address", "Address", fixInStrand: true);
            AddTextRow(grid, "Lot", "Lot");
            AddTextRow(grid, "Block", "Block");
            AddDisplayRow(grid, "City", "City", fixInStrand: true);
            AddDisplayRow(grid, "State", "State", fixInStrand: true);
            AddTextRow(grid, "Plan No", "PlanNumber");
            AddDisplayRow(grid, "Builder", "BuilderName", fixInStrand: true);
            AddDisplayRow(grid, "Inspector", "InspectorName", fixInStrand: true);
            _topDateBlock = AddDisplayRow(grid, "Date", "DateInspected", fixInStrand: false, date: true,
                hint: "Stamped when the inspector signs; nobody edits it here.");
            AddStructureRow(grid);
            AddTextRow(grid, "Contact", "Contact");
            AddTextRow(grid, "Contact phone", "ContactNumber");
            AddCheckRow(grid, "Re-inspection", "IsReinspection");
            AddTextRow(grid, "Attempt", "Attempt", isAttempt: true);
            AddCheckRow(grid, "PO Req'd", "PurchaseOrderRequired");
            AddTextRow(grid, "PO Number", "PurchaseOrderNumber");
            AddTextRow(grid, "Requisition Number", "RequisitionNumber");
            AddTextRow(grid, "Notify Who", "WhoNotified");
            AddTextRow(grid, "Notify When", "WhenNotified", isDate: true);
            AddTextRow(grid, "Requests", "Requests", multiline: true);
            RefreshHeaderMarkers();
            return grid;
        }

        private UIElement CreateEndSectionBody()
        {
            var grid = NewHeaderGrid();

            var statuses = HeaderRules.StatusList(Header("StatusList"));
            int statusId = HeaderRules.Int(Header("StatusId"));
            // Owner 2026-10-20: only the results the result picker allows for this inspection; the current status, when it
            // is not one of them (e.g. Ordered), is shown greyed out and cannot be picked again.
            var (code, _, failed, config, expirationStageDone) = ResultPickerInputs();
            var allowed = ResultPickerWindow.AllowedStatusIds(code, failed > 0, config, expirationStageDone, CurrentStatusListIds());
            var offered = HeaderRules.OfferedStatuses(statuses, allowed);
            var statusCombo = new ComboBox { MinWidth = 220, IsEnabled = !_readOnlyMode, Margin = new Thickness(0, 2, 0, 2) };
            foreach (var status in offered)
                statusCombo.Items.Add(new ComboBoxItem { Content = status.Name, Tag = status.Id });
            if (!offered.Any(s => s.Id == statusId))
            {
                string current = statuses.FirstOrDefault(s => s.Id == statusId)?.Name ?? $"(status {statusId}, not in this form's list)";
                statusCombo.Items.Insert(0, new ComboBoxItem { Content = current, Tag = statusId, IsEnabled = false });
            }
            statusCombo.SelectedItem = statusCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == statusId);
            statusCombo.SelectionChanged += (_, _) =>
            {
                if (statusCombo.SelectedItem is not ComboBoxItem { Tag: int id })
                    return;
                if (WriteHeader("StatusId", new JValue(id)))
                    UpdateNextActionVisibility();
            };
            AddRow(grid, "Status", statusCombo, "StatusId");

            _nextActionCombo = new ComboBox { MinWidth = 320, IsEnabled = !_readOnlyMode, Margin = new Thickness(0, 2, 0, 2) };
            foreach (var option in HeaderRules.NextActions)
                _nextActionCombo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option });
            int nextActionId = HeaderRules.Int(Header("NextActionId"));
            _nextActionCombo.SelectedIndex = nextActionId >= 0 && nextActionId < HeaderRules.NextActions.Count ? nextActionId : -1;
            _nextActionCombo.SelectionChanged += (_, _) =>
            {
                if (_nextActionCombo.SelectedItem is not ComboBoxItem { Tag: NextActionOption option })
                    return;
                bool changed = WriteHeader("NextActionId", new JValue(option.Id), refresh: false);
                changed |= WriteHeader("NextActionText", new JValue(option.Text));
            };
            _nextActionRow = AddRow(grid, "Next Action", _nextActionCombo, "NextActionId");
            UpdateNextActionVisibility();

            AddTextRow(grid, "Internal Notes", "InternalNotes", multiline: true);

            FooterSignatureSlot = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(FooterSignatureSlot, row);
            Grid.SetColumnSpan(FooterSignatureSlot, 2);
            grid.Children.Add(FooterSignatureSlot);
            OnFooterBuilt(FooterSignatureSlot);

            RefreshHeaderMarkers();
            return grid;
        }

        // F-011 fills the footer slot with the inspector signature pad.
        partial void OnFooterBuilt(StackPanel slot);

        private void UpdateNextActionVisibility()
        {
            if (_nextActionRow == null)
                return;
            // E2: the Next Action combo shows only for Failed. Leaving Failed hides it and keeps the stored values
            // (INFERRED, SPEC E2).
            bool failed = HeaderRules.Int(Header("StatusId")) == HeaderRules.FailedStatusId;
            _nextActionRow.SetVisible(failed);
            RefreshHeaderMarkers();
        }

        private static Grid NewHeaderGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private HeaderRow AddRow(Grid grid, string label, FrameworkElement editor, string markerKey, string? hint = null)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var labelBlock = new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 8, 2)
            };
            Grid.SetRow(labelBlock, row);
            grid.Children.Add(labelBlock);

            var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            stack.Children.Add(editor);
            // F-002 A2: RED never adds a header key the file does not have, so its editor is off.
            if (_saveService.HasFile && !_saveService.HasHeaderKey(markerKey))
            {
                editor.IsEnabled = false;
                hint = "Not in this report's file. RED does not add it.";
            }
            if (!string.IsNullOrEmpty(hint))
                stack.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), TextWrapping = TextWrapping.Wrap });
            var marker = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(marker);
            _headerMarkerBlocks[markerKey] = marker;
            Grid.SetRow(stack, row);
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            return new HeaderRow(labelBlock, stack);
        }

        // The label and the editor of one header row, shown or hidden together (Next Action row).
        private sealed class HeaderRow
        {
            private readonly UIElement _label, _editor;
            public HeaderRow(UIElement label, UIElement editor) { _label = label; _editor = editor; }
            public void SetVisible(bool visible)
            {
                _label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                _editor.Visibility = _label.Visibility;
            }
        }

        private TextBlock AddDisplayRow(Grid grid, string label, string key, bool fixInStrand, bool date = false, string? hint = null)
        {
            string text = date ? HeaderRules.DisplayDate(Header(key)) : HeaderText(key);
            var value = new TextBlock
            {
                Text = string.IsNullOrEmpty(text) ? "(blank)" : text,
                Foreground = string.IsNullOrEmpty(text) ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 6, 0, 2),
                TextWrapping = TextWrapping.Wrap,
                ToolTip = fixInStrand ? "Comes from the Strand job record. " + HeaderRules.FixInStrandHint + "." : null
            };
            AddRow(grid, label, value, key, hint ?? (fixInStrand ? HeaderRules.FixInStrandHint : null));
            return value;
        }

        private void AddStructureRow(Grid grid)
        {
            var stored = Header("StructureTypeId");
            int current = HeaderRules.Int(stored, -1);
            var combo = new ComboBox { MinWidth = 220, IsEnabled = !_readOnlyMode, Margin = new Thickness(0, 2, 0, 2) };
            foreach (var (id, name) in HeaderRules.Structures)
                combo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            if (stored != null && stored.Type != JTokenType.Null && !HeaderRules.Structures.Any(s => s.Id == current))
                combo.Items.Insert(0, new ComboBoxItem { Content = $"(structure {HeaderRules.Text(stored)})", Tag = current });
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current &&
                stored != null && stored.Type != JTokenType.Null);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: int id } && HeaderRules.Structures.Any(s => s.Id == id))
                    WriteHeader("StructureTypeId", new JValue(id));
            };
            AddRow(grid, "Structure", combo, "StructureTypeId");
        }

        private void AddCheckRow(Grid grid, string label, string key)
        {
            var box = new CheckBox
            {
                IsChecked = HeaderRules.Bool(Header(key)),
                IsEnabled = !_readOnlyMode,
                Margin = new Thickness(0, 6, 0, 2)
            };
            box.Click += (_, _) => WriteHeader(key, new JValue(box.IsChecked == true));
            AddRow(grid, label, box, key);
        }

        private void AddTextRow(Grid grid, string label, string key, bool multiline = false, bool isAttempt = false, bool isDate = false)
        {
            string shown = isDate ? HeaderRules.DisplayDate(Header(key)) : HeaderText(key);
            var box = new TextBox
            {
                Text = shown,
                IsReadOnly = _readOnlyMode,
                MinWidth = 220,
                Padding = new Thickness(4, 3, 4, 3),
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 54 : 0,
                MaxLength = isAttempt ? 4 : 0,
                ToolTip = isDate ? "Date and time, for example 10/19/2026 9:30 AM. Leave blank for none." : null
            };
            box.Tag = shown;
            void Commit()
            {
                if (_readOnlyMode || box.Text == (string)box.Tag)
                    return;
                if (isAttempt)
                {
                    if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int attempt))
                    {
                        MessageBox.Show(this, HeaderRules.AttemptTooLow, "Attempt", MessageBoxButton.OK, MessageBoxImage.Information);
                        box.Text = (string)box.Tag;
                        return;
                    }
                    SetAttempt(attempt);
                }
                else if (isDate)
                {
                    if (string.IsNullOrWhiteSpace(box.Text))
                        WriteHeader(key, JValue.CreateNull());
                    else if (HeaderRules.TryParseLocalDate(box.Text, out DateTime local))
                        WriteHeader(key, new JValue(HeaderRules.FormatDate(local)));
                    else
                    {
                        MessageBox.Show(this, "Enter a date and time such as 10/19/2026 9:30 AM.", label, MessageBoxButton.OK, MessageBoxImage.Information);
                        box.Text = (string)box.Tag;
                        return;
                    }
                    box.Text = HeaderRules.DisplayDate(Header(key));
                }
                else
                {
                    WriteHeader(key, new JValue(box.Text));
                }
                box.Tag = box.Text;
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            if (!multiline)
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            AddRow(grid, label, box, key);
        }

        // T4: an Attempt change rebuilds InspectionNumber at once and updates the editor header; the file keeps its name.
        private void SetAttempt(int attempt)
        {
            if (!WriteHeader("Attempt", new JValue(attempt), refresh: false))
            {
                RefreshHeaderMarkers();
                return;
            }
            string current = HeaderText("InspectionNumber");
            string number = HeaderRules.BuildInspectionNumber(HeaderText("JobNumber"), HeaderText("InspectionCode"), attempt,
                HeaderText("InspectorName"), HeaderRules.Bool(Header("IsPreinspection")), HeaderRules.InitialsOf(current));
            WriteHeader("InspectionNumber", new JValue(number));
            if (_currentInspection != null)
                _currentInspection.InspectionNumber = number;
            TripNumberBadge.Text = $"Trip #{attempt}";
            TripNumberBadge.ToolTip = $"Inspection number {number}. The file keeps its name until Submit.";
            TripNumberBadge.Visibility = Visibility.Visible;
        }

        private bool WriteHeader(string key, JToken value, bool refresh = true)
        {
            if (_readOnlyMode || !HeaderSectionsAvailable)
                return false;
            bool changed = _saveService.SetHeaderValue(key, value);
            if (changed)
            {
                MarkUnsaved();
                SyncModelHeader(key, value);
            }
            if (refresh)
                RefreshHeaderMarkers();
            return changed;
        }

        // Keep the typed model in step for the parts of RED that read it (header strip, stats, type rules).
        private void SyncModelHeader(string key, JToken value)
        {
            if (_currentInspection == null)
                return;
            string? text = HeaderRules.Text(value);
            switch (key)
            {
                case "Contact": _currentInspection.Contact = text; break;
                case "InspectionNumber": _currentInspection.InspectionNumber = text; break;
                // F-011: signing moves DateInspected; the orientation PDF and the header strip read the typed values.
                case "DateInspected": _currentInspection.DateInspected = text; break;
                case "InspectionName": _currentInspection.InspectionName = text; break;
                case "Address": _currentInspection.Address = text; break;
                case "City": _currentInspection.City = text; break;
                case "State": _currentInspection.State = text; break;
                case "BuilderName": _currentInspection.BuilderName = text; break;
                case "Project": _currentInspection.Project = text; break;
                case "ServiceId" when value.Type == JTokenType.Integer: _currentInspection.ServiceId = (int)value; break;
                default:
                    if (_currentInspection.ExtensionData != null && _currentInspection.ExtensionData.ContainsKey(key))
                        _currentInspection.ExtensionData[key] = value.DeepClone();
                    break;
            }
        }

        private void RefreshHeaderMarkers()
        {
            var markers = CurrentHeaderMarkers();
            foreach (var pair in _headerMarkerBlocks)
            {
                var texts = markers.Where(m => m.Key == pair.Key || (pair.Key == "PurchaseOrderNumber" && m.Key == "PurchaseOrderNumber"))
                    .Select(m => m.Text).Distinct().ToList();
                pair.Value.Text = string.Join(" ", texts);
                pair.Value.Visibility = texts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateHeaderSummary(top: true);
            UpdateHeaderSummary(top: false);
            UpdateResultButton(); // item 14: also after the End section's Status / Next Action combos
        }

        private void ClearHeaderSectionState()
        {
            _headerMarkerBlocks.Clear();
            _topSummaryText = null;
            _endSummaryText = null;
            _nextActionCombo = null;
            _nextActionRow = null;
            _topDateBlock = null;
            _signaturePad = null;
            FooterSignatureSlot = null;
        }

        // Classic tree: T and E nodes open the same panels in a window.
        private TreeViewItem CreateHeaderTreeNode(bool top)
        {
            var text = new TextBlock { FontWeight = FontWeights.Bold, FontSize = _checklistFontSize + 1, Margin = new Thickness(0, 2, 4, 2) };
            var markers = CurrentHeaderMarkers().Where(m => IsEndKey(m.Key) != top).ToList();
            string title = top ? "T - Top" : "E - End (Status)";
            text.Text = markers.Count == 0 ? title : $"{title}  ({markers.Count} to fix)";
            if (markers.Any(m => m.Blocking))
                text.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            var node = new TreeViewItem { Header = text, Tag = top ? TopSectionKey : EndSectionKey, ToolTip = "Click to edit" };
            // Opened by a click only, so section navigation that selects nodes never pops a window.
            text.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(new Action(() => OpenHeaderSectionWindow(top)));
            };
            return node;
        }

        // Owner tablet test of 2.3.4: press and drag scrolls the section (touch reaches RED as mouse input on the owner's
        // tablets); the signature pad, text boxes and drop-downs keep their own input (DragScroll).
        private static ScrollViewer HeaderSectionScroller(UIElement body)
        {
            var scroller = new ScrollViewer { Content = new Border { Padding = new Thickness(14), Child = body }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalFirst };
            DragScroll.Attach(scroller);
            return scroller;
        }

        // focusKey: a header field to scroll to once the window shows (the problem list's Go to, the Sign button).
        private void OpenHeaderSectionWindow(bool top, string? focusKey = null)
        {
            if (!HeaderSectionsAvailable)
                return;
            ClearHeaderSectionState();
            var body = top ? CreateTopSectionBody() : CreateEndSectionBody();
            body.IsEnabled = !_readOnlyMode; // F-014 R1 / F-011 P6: shown, not editable (hands-on pass 2026-10-19)
            var window = new Window
            {
                Title = top ? "T - Top" : "E - End (Status)",
                Owner = this,
                Width = 640,
                Height = top ? 760 : 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = HeaderSectionScroller(body)
            };
            if (focusKey != null)
                window.Loaded += (_, _) =>
                {
                    if (_headerMarkerBlocks.TryGetValue(focusKey, out var marker))
                        (marker.Parent as FrameworkElement ?? marker).BringIntoView();
                };
            window.ShowDialog();
            ClearHeaderSectionState();
            PopulateTreeView(SearchFilterBox.Text);
        }

        private IReadOnlyCollection<int>? CurrentStatusListIds()
        {
            var list = HeaderRules.StatusList(Header("StatusList"));
            return list.Count == 0 ? null : list.Select(s => s.Id).ToList();
        }
    }
}
