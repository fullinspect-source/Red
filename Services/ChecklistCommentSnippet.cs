using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using InspectionEditor.Models;

namespace InspectionEditor.Services
{
    internal static class ChecklistCommentSnippet
    {
        public static TextBlock Create(Item item, double checklistFontSize)
        {
            var snippet = new TextBlock
            {
                FontSize = ChecklistCommentSnippetPolicy.FontSize(checklistFontSize),
                Height = ChecklistCommentSnippetPolicy.LineHeight(checklistFontSize),
                LineHeight = ChecklistCommentSnippetPolicy.LineHeight(checklistFontSize),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(8, 0, 12, 2),
                Focusable = false
            };
            // Bind to the original item instance, not selection or potentially duplicate IDs.
            snippet.SetBinding(TextBlock.TextProperty, CommentBinding(item, new PreviewConverter()));
            snippet.SetBinding(UIElement.VisibilityProperty, CommentBinding(item, new VisibilityConverter()));
            snippet.SetBinding(FrameworkElement.ToolTipProperty, CommentBinding(item));
            return snippet;
        }

        private static Binding CommentBinding(Item item, IValueConverter? converter = null) => new Binding(nameof(Item.Comments))
        {
            Source = item,
            Mode = BindingMode.OneWay,
            Converter = converter
        };

        private sealed class PreviewConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                ChecklistCommentSnippetPolicy.Preview(value as string);
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
        private sealed class VisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                ChecklistCommentSnippetPolicy.HasComment(value as string) ? Visibility.Visible : Visibility.Collapsed;
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
        }
    }
}
