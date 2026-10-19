using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Small dialogs used by the ORDERS tab. Built in code so they share one look.
    internal abstract class OrdersDialogBase : Window
    {
        protected readonly StackPanel Body = new() { Margin = new Thickness(20, 18, 20, 8) };
        protected readonly Button OkButton;

        protected OrdersDialogBase(string title, string okText)
        {
            Title = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(246, 247, 248));
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 15;

            OkButton = new Button { Content = okText, MinWidth = 96, Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
            OkButton.Click += (_, _) => { if (Validate()) DialogResult = true; };
            var cancel = new Button { Content = "Cancel", MinWidth = 96, Padding = new Thickness(14, 7, 14, 7), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 18) };
            buttons.Children.Add(OkButton);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(Body);
            Content = root;
        }

        protected virtual bool Validate() => true;

        protected static TextBlock Label(string text) =>
            new() { Text = text, Margin = new Thickness(0, 8, 0, 4), Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 51)), TextWrapping = TextWrapping.Wrap };
    }

    internal sealed class StrandSignInWindow : OrdersDialogBase
    {
        private readonly TextBox _usernameBox = new() { Padding = new Thickness(6, 5, 6, 5) };
        private readonly PasswordBox _passwordBox = new() { Padding = new Thickness(6, 5, 6, 5) };

        public string Username => _usernameBox.Text.Trim();
        public string Password => _passwordBox.Password;

        public StrandSignInWindow(string lastUsername) : base("Sign in to Strand", "Sign In")
        {
            Body.Children.Add(new TextBlock
            {
                Text = "ORDERS uses your Strand (Inspect) account to load the live schedule. RED saves it for your Windows account; change it any time in Settings.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(102, 115, 127))
            });
            Body.Children.Add(Label("Username"));
            Body.Children.Add(_usernameBox);
            Body.Children.Add(Label("Password"));
            Body.Children.Add(_passwordBox);

            _usernameBox.Text = lastUsername;
            Loaded += (_, _) => { if (string.IsNullOrEmpty(lastUsername)) _usernameBox.Focus(); else _passwordBox.Focus(); };
        }

        protected override bool Validate()
        {
            if (Username.Length > 0 && Password.Length > 0)
                return true;
            MessageBox.Show(this, "Enter your Strand username and password.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    internal sealed class OrderAssignWindow : OrdersDialogBase
    {
        private readonly ComboBox _inspectorCombo = new() { Padding = new Thickness(6, 5, 6, 5) };

        public StrandUserOption? SelectedInspector => _inspectorCombo.SelectedItem as StrandUserOption;

        public OrderAssignWindow(IReadOnlyList<StrandUserOption> inspectors, int selectedCount) : base("Assign Selected", "Assign")
        {
            Body.Children.Add(Label(selectedCount == 1
                ? "Who do you want to assign to the selected request?"
                : $"Who do you want to assign to the {selectedCount} selected requests?"));
            Body.Children.Add(_inspectorCombo);
            _inspectorCombo.ItemsSource = inspectors;
        }

        protected override bool Validate()
        {
            if (SelectedInspector != null)
                return true;
            MessageBox.Show(this, "Please select an inspector to be assigned.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }

    // Inspect's ChooseFormWindow: shown when a service has more than one form.
    internal sealed class OrderFormChooserWindow : OrdersDialogBase
    {
        private readonly List<RadioButton> _options = new();

        public int ChosenFormId => _options.FirstOrDefault(o => o.IsChecked == true)?.Tag as int? ?? -1;

        public OrderFormChooserWindow(IReadOnlyList<StrandFormOption> forms, string serviceName) : base("Choose Form", "Done")
        {
            Body.Children.Add(Label("Please choose a form for the service below to continue."));
            Body.Children.Add(new TextBlock { Text = $"Service: {serviceName}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 6) });
            foreach (var form in forms)
            {
                var option = new RadioButton { Content = form.Name, Tag = form.Id, Margin = new Thickness(0, 6, 0, 0) };
                _options.Add(option);
                Body.Children.Add(option);
            }
        }

        protected override bool Validate()
        {
            if (ChosenFormId >= 0)
                return true;
            MessageBox.Show(this, "Please select a form to continue.", "Error(s)", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    internal sealed class OrderDeleteReasonWindow : OrdersDialogBase
    {
        private static readonly string[] PresetReasons =
        {
            "The builder cancelled ahead of scheduled date/time.",
            "The inspection has already been completed.",
            "No purchase order was given.",
            "This service is not in contract."
        };

        private readonly List<RadioButton> _options = new();
        private readonly RadioButton _otherOption = new() { Content = "Another reason:", Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBox _otherText = new() { Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(22, 4, 0, 0), IsEnabled = false };

        public string ReasonText => _otherOption.IsChecked == true
            ? _otherText.Text.Trim()
            : _options.FirstOrDefault(o => o.IsChecked == true)?.Content as string ?? "";

        public OrderDeleteReasonWindow(int requestCount) : base("Delete Request", "Continue")
        {
            Body.Children.Add(Label($"Enter the reason for deleting {(requestCount == 1 ? "this request" : "these requests")} to continue:"));
            foreach (string reason in PresetReasons)
            {
                var option = new RadioButton { Content = reason, Margin = new Thickness(0, 6, 0, 0) };
                _options.Add(option);
                Body.Children.Add(option);
            }

            _otherOption.Checked += (_, _) => { _otherText.IsEnabled = true; _otherText.Focus(); };
            _otherOption.Unchecked += (_, _) => _otherText.IsEnabled = false;
            Body.Children.Add(_otherOption);
            Body.Children.Add(_otherText);
        }

        protected override bool Validate()
        {
            if (ReasonText.Length > 0)
                return true;
            MessageBox.Show(this, "Choose a reason, or enter another reason, to continue.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
    }
}
