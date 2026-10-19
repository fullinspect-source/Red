using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-024: INSPECT's company and user comment lists in the comment drawer, read only. A tap appends "{existing}, {entry}".
    public partial class MainWindow
    {
        private sealed record InspectCommentAction(Item Item, string Entry);

        // {IF} of the open report when it sits in a list folder (MyList, Review, Outbox, Archive); otherwise the profile's
        // Inspections folder, never an unrelated "<grandparent>\Data".
        private string? CommentLibraryFolder()
        {
            string? list = string.IsNullOrEmpty(_currentFilePath) ? null : System.IO.Path.GetDirectoryName(_currentFilePath);
            string name = list == null ? "" : System.IO.Path.GetFileName(list);
            bool inList = Array.Exists(new[] { "MyList", "Review", "Outbox", "Archive" }, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return inList ? CurrentInspectionsFolder() : InspectorProfileService.Load()?.InspectionsFolder;
        }

        // B1/B5: one section per library that has entries for this item; nothing when a library is missing or empty.
        private void AddInspectCommentSections(Panel list, Item item)
        {
            string? folder = CommentLibraryFolder();
            foreach (var (kind, title) in new[] { (InspectCommentLibrary.Kind.Company, InspectCommentLibrary.CompanyTitle), (InspectCommentLibrary.Kind.User, InspectCommentLibrary.UserTitle) })
            {
                var entries = InspectCommentLibrary.Entries(folder, kind, item.ItemId);
                if (entries.Count == 0)
                    continue;
                list.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = Math.Max(9, _checklistFontSize - 3),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                    Margin = new Thickness(2, 6, 2, 2)
                });
                foreach (string entry in entries)
                {
                    var button = new Button
                    {
                        Content = new TextBlock
                        {
                            Text = entry,
                            TextWrapping = TextWrapping.Wrap,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxHeight = 48,
                            FontSize = Math.Max(9, _checklistFontSize - 3),
                            Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
                        },
                        Tag = new InspectCommentAction(item, entry),
                        Padding = new Thickness(6, 4, 6, 4),
                        Margin = new Thickness(0, 0, 0, 4),
                        MaxHeight = 62,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                        ToolTip = entry
                    };
                    button.Click += InlineInspectCommentButton_Click;
                    list.Children.Add(button);
                }
            }
        }

        private void InlineInspectCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InspectCommentAction action } || _readOnlyMode)
                return;
            LoadItemEditor(action.Item);
            action.Item.Comments = InspectCommentLibrary.Append(action.Item.Comments, action.Entry); // B2
            if (ReferenceEquals(_inlineQuickCommentsDismissedItem, action.Item))
                _inlineQuickCommentsDismissedItem = null;
            AutoSetFailIfApplicable();
            MarkUnsaved();
            LoadItemEditor(action.Item);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }
    }
}
