using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-006 ink items (customer Signature item, Sketch, FoundationSketch): the row control, the right-pane editor and
    // the drawing window. Storage rules live in Services\InkItemService.cs.
    public partial class MainWindow
    {
        private string? CurrentInspectionsFolder()
        {
            // As RED's other job lookups: the report sits in {IF}\{MyList|Review|...}\{num}.ins.
            string? folder = string.IsNullOrEmpty(_currentFilePath) ? null : Path.GetDirectoryName(_currentFilePath);
            return folder != null ? Path.GetDirectoryName(folder) : null;
        }

        private string? CurrentJobNumber()
        {
            if (_currentInspection?.ExtensionData != null &&
                _currentInspection.ExtensionData.TryGetValue("JobNumber", out var job) && job != null)
            {
                string text = job.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            // Fall back to the file name's job part, as UpdateSeeDocsButton does.
            return string.IsNullOrEmpty(_currentFilePath) ? null : Path.GetFileNameWithoutExtension(_currentFilePath).Split('-')[0];
        }

        private static string InkActionText(Item item) => InkItemService.KindOf(item) switch
        {
            InkControlKind.Signature => InkItemService.ShowsEmpty(item) ? "Sign" : "Re-sign",
            _ => InkItemService.ShowsEmpty(item) ? "Draw" : "Edit drawing"
        };

        private static string InkStateText(Item item) => InkItemService.ShowsEmpty(item)
            ? "Empty"
            : InkItemService.KindOf(item) == InkControlKind.Signature ? "Signed" : "Drawn";

        private UIElement CreateInlineInkControl(Item item)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 5, 6)
            };
            bool empty = InkItemService.ShowsEmpty(item);
            var thumbnail = InkItemService.DecodePicture(item);
            if (thumbnail != null)
            {
                panel.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new Image { Source = thumbnail, Height = 34, MaxWidth = 160, Stretch = Stretch.Uniform }
                });
            }
            panel.Children.Add(new TextBlock
            {
                Text = InkStateText(item),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                Foreground = empty ? new SolidColorBrush(Color.FromRgb(100, 116, 139)) : new SolidColorBrush(Color.FromRgb(22, 101, 52))
            });
            var button = new Button
            {
                Content = InkActionText(item),
                Tag = item,
                Padding = new Thickness(12, 5, 12, 5),
                FontSize = Math.Max(11, _checklistFontSize - 1),
                FontWeight = FontWeights.SemiBold,
                IsEnabled = !_readOnlyMode,
                ToolTip = "Open the drawing window"
            };
            button.Click += (_, e) =>
            {
                OpenInkEditor(item);
                e.Handled = true;
            };
            panel.Children.Add(button);
            return panel;
        }

        // Right pane for an ink item. Returns false for every other item.
        private bool TryLoadInkItemEditor(Item item)
        {
            var kind = InkItemService.KindOf(item);
            if (kind == InkControlKind.None)
                return false;

            StatusLabel.Text = kind switch
            {
                InkControlKind.Signature => "CUSTOMER SIGNATURE",
                InkControlKind.FoundationSketch => "FOUNDATION SKETCH",
                _ => "SKETCH"
            };
            NiValueButton.Visibility = Visibility.Collapsed;
            if (item.HideAddButton())
                DuplicateItemButton.Visibility = Visibility.Collapsed; // SPEC S6: ink items honor HideAddButton

            var thumbnail = InkItemService.DecodePicture(item);
            if (thumbnail != null)
            {
                StatusPanel.Children.Add(new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 4, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image { Source = thumbnail, MaxWidth = 360, Stretch = Stretch.Uniform }
                });
            }
            StatusQuickPanel.Children.Add(new TextBlock
            {
                Text = InkStateText(item),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            var open = new Button { Content = InkActionText(item) + "…", Padding = new Thickness(12, 5, 12, 5), IsEnabled = !_readOnlyMode };
            open.Click += (_, _) => OpenInkEditor(item);
            StatusQuickPanel.Children.Add(open);

            UpdateRequiredFieldHighlighting(item);
            ClearValueButton.Visibility = Visibility.Collapsed; // Clear lives in the drawing window (S4)
            return true;
        }

        private void OpenInkEditor(Item item)
        {
            if (_readOnlyMode || !EditorEditService.Owns(_currentInspection, item))
                return;
            if (!ReferenceEquals(item, _editorLoadedItem))
            {
                AutoApplyCurrentItem();
                LoadItemEditor(item);
            }

            string? ffpPath = InkItemService.KindOf(item) == InkControlKind.FoundationSketch
                ? InkItemService.FfpPath(CurrentInspectionsFolder(), CurrentJobNumber())
                : null;
            var window = new InkItemEditorWindow(item, ffpPath, OnInkItemChanged) { Owner = this };
            window.ShowDialog();
            if (window.ItemChanged) // only a real drawing change marks the report unsaved
                OnInkItemChanged(item);
        }

        private void OnInkItemChanged(Item item)
        {
            MarkUnsaved();
            RefreshInlineItemRow(item);
            if (ReferenceEquals(item, _editorLoadedItem))
            {
                _currentPhotoIndex = 0;
                LoadCurrentPhoto();
                LoadStatusControls(item);
            }
        }

        // C1: an ink item holds exactly one picture, made by its drawing window; ordinary photo tools never add one.
        private bool RefuseOrdinaryPictureForInk(Item? item)
        {
            if (!InkItemService.IsInk(item))
                return false;
            MessageBox.Show(this, "This item stores its drawing as its only picture. Use the drawing window instead.",
                "Drawing item", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
    }

    internal static class InkItemExtensions
    {
        public static bool HideAddButton(this Item item) =>
            item.ExtensionData != null && item.ExtensionData.TryGetValue("HideAddButton", out var raw) && raw != null &&
            bool.TryParse(raw.ToString(), out bool hide) && hide;
    }
}
