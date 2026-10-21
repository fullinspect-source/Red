using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace InspectionEditor
{
    // ORDERS questions sized for a finger on the tablet (owner 2026-10-20): big buttons, one roomy row per order,
    // nothing crowded. Ask = a Yes/No question; ChooseMany = a checklist, nothing ticked at the start.
    internal sealed class OrdersTouchDialog : Window
    {
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(31, 41, 51));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(82, 97, 111));
        private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(196, 205, 213));
        private static readonly Brush Brand = new SolidColorBrush(Color.FromRgb(153, 0, 0));
        private static readonly Brush TickedBack = new SolidColorBrush(Color.FromRgb(236, 253, 243));
        private static readonly Brush TickedLine = new SolidColorBrush(Color.FromRgb(50, 213, 131));

        private readonly StackPanel _body = new() { Margin = new Thickness(24, 20, 24, 8) };
        private readonly Button _primary;
        private readonly Button _secondary;
        private readonly List<ToggleButton> _rows = new();
        private readonly string _primaryText;

        private OrdersTouchDialog(string caption, string text, string primaryText, string secondaryText)
        {
            Title = caption;
            Width = 600;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(246, 247, 248));
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 17;
            _primaryText = primaryText;

            _body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ink, Margin = new Thickness(0, 0, 0, 12) });

            _primary = BigButton(primaryText, primary: true);
            _primary.Click += (_, _) => DialogResult = true;
            _secondary = BigButton(secondaryText, primary: false);
            _secondary.Margin = new Thickness(12, 0, 0, 0);
            _secondary.IsCancel = true;
            _secondary.Click += (_, _) => DialogResult = false;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(24, 12, 24, 22) };
            buttons.Children.Add(_primary);
            buttons.Children.Add(_secondary);

            // A finger swipe scrolls a long list; as in the report preview (owner tablet test 2026-10-20), the window
            // does not bounce at the ends, and press-and-hold and flicks are off so a slow tap still ticks a row.
            var scroller = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, PanningMode = PanningMode.VerticalOnly };
            System.Windows.Input.Stylus.SetIsPressAndHoldEnabled(scroller, false);
            System.Windows.Input.Stylus.SetIsFlicksEnabled(scroller, false);
            scroller.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(scroller);
            Content = root;
        }

        // True only for Yes; No, Escape and closing the window all mean No.
        public static bool Ask(Window owner, string caption, string text, string yesText = "Yes", string noText = "No")
        {
            var dialog = new OrdersTouchDialog(caption, text, yesText, noText) { Owner = owner };
            dialog._primary.IsDefault = true;
            return dialog.ShowDialog() == true;
        }

        // The ticked items, in list order; empty for "Add none", Escape or closing the window.
        public static List<T> ChooseMany<T>(Window owner, string caption, string text, IReadOnlyList<(T Item, string Title, string Detail)> items,
                                            string chooseText, string noneText)
        {
            var dialog = new OrdersTouchDialog(caption, text, chooseText, noneText) { Owner = owner };
            foreach (var (_, title, detail) in items)
                dialog.AddRow(title, detail);
            dialog.UpdatePrimary();
            if (dialog.ShowDialog() != true)
                return new List<T>();
            return items.Where((_, i) => dialog._rows[i].IsChecked == true).Select(i => i.Item).ToList();
        }

        private void AddRow(string title, string detail)
        {
            var box = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(2), BorderBrush = Muted,
                Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0),
                Child = new TextBlock { Text = "", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Ink, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrWhiteSpace(detail))
                texts.Children.Add(new TextBlock { Text = detail, FontSize = 15, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var content = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(box, Dock.Left);
            content.Children.Add(box);
            content.Children.Add(texts);

            var row = new ToggleButton
            {
                Content = content, MinHeight = 64, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.White, BorderBrush = Line,
                BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, Focusable = true
            };
            row.Template = RowTemplate();
            void Show()
            {
                bool on = row.IsChecked == true;
                box.Background = on ? Brand : Brushes.White;
                box.BorderBrush = on ? Brand : Muted;
                ((TextBlock)box.Child).Text = on ? "✓" : "";
                row.Background = on ? TickedBack : Brushes.White;
                row.BorderBrush = on ? TickedLine : Line;
                UpdatePrimary();
            }
            row.Checked += (_, _) => Show();
            row.Unchecked += (_, _) => Show();
            System.Windows.Automation.AutomationProperties.SetName(row, string.IsNullOrWhiteSpace(detail) ? title : $"{title}, {detail}");
            _rows.Add(row);
            _body.Children.Add(row);
        }

        // "Add selected" needs at least one tick and shows how many.
        private void UpdatePrimary()
        {
            if (_rows.Count == 0)
                return;
            int ticked = _rows.Count(r => r.IsChecked == true);
            _primary.IsEnabled = ticked > 0;
            _primary.Content = ticked > 0 ? $"{_primaryText} ({ticked})" : _primaryText;
        }

        private static ControlTemplate RowTemplate() => PlainTemplate(typeof(ToggleButton), 8, HorizontalAlignment.Stretch);

        // A plain rounded control in our own colours: the default chrome would paint its own hover and pressed colours
        // over them (white text on light blue) and hide a disabled button's text. Pressed and disabled fade instead.
        private static ControlTemplate PlainTemplate(Type type, double radius, HorizontalAlignment content)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, content);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(type) { VisualTree = border };
            template.Triggers.Add(new Trigger { Property = ButtonBase.IsPressedProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 0.8) } });
            template.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.4) } });
            return template;
        }

        private static Button BigButton(string text, bool primary) => new()
        {
            Content = text, MinWidth = 150, MinHeight = 52, Padding = new Thickness(22, 10, 22, 10), FontSize = 17,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Background = primary ? Brand : new SolidColorBrush(Color.FromRgb(232, 236, 239)),
            Foreground = primary ? Brushes.White : Ink,
            BorderBrush = primary ? Brand : new SolidColorBrush(Color.FromRgb(209, 215, 221)),
            BorderThickness = new Thickness(1),
            Template = PlainTemplate(typeof(Button), 6, HorizontalAlignment.Center)
        };
    }
}
