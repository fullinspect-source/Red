using InspectionEditor.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InspectionEditor
{
    /// <summary>
    /// Dialog for entering RESNET 380 multipoint blower door test data.
    /// Collects CFM readings at 60, 48, 35, 23, 10 Pa and calculates
    /// the corrected cfm50 via log-linear regression.
    /// </summary>
    public partial class MultipointBlowerDoorWindow : Window
    {
        // ── Pressure points in RESNET 380 order (high → low) ────────────────
        private static readonly double[] ResnetPa = { 60, 48, 35, 23, 10 };

        // ── Inputs exposed to MainWindow after Apply ─────────────────────────
        public double CalculatedCfm50 { get; private set; }
        public double PressureExponentN { get; private set; }

        // Per-point results (Pa order: 60, 48, 35, 23, 10)
        public double[] EnteredCfm { get; } = new double[5];
        public string[] SelectedRing { get; } = new string[5]; // "A", "B", "C", or ""

        private readonly double _targetCfm;

        // ── Control arrays for programmatic access ───────────────────────────
        private TextBox[] _cfmBoxes = null!;
        private (RadioButton A, RadioButton B, RadioButton C)[] _ringButtons = null!;

        public MultipointBlowerDoorWindow(double targetCfm)
        {
            InitializeComponent();
            _targetCfm = targetCfm;

            _cfmBoxes = new[] { Cfm60, Cfm48, Cfm35, Cfm23, Cfm10 };
            _ringButtons = new[]
            {
                (Ring60A, Ring60B, Ring60C),
                (Ring48A, Ring48B, Ring48C),
                (Ring35A, Ring35B, Ring35C),
                (Ring23A, Ring23B, Ring23C),
                (Ring10A, Ring10B, Ring10C),
            };

            // Show target and 90% threshold
            if (targetCfm > 0)
            {
                TargetLabel.Text   = $"{targetCfm:F0} CFM @ 50 Pa";
                ThresholdLabel.Text = $"{targetCfm * 0.9:F0} CFM";
            }
            else
            {
                TargetLabel.Text    = "not available";
                ThresholdLabel.Text = "—";
            }

            // Default ring A for all rows
            foreach (var (a, _, _) in _ringButtons)
                a.IsChecked = true;

            Cfm60.Focus();
        }

        // ── Calculate ────────────────────────────────────────────────────────

        private void CalculateButton_Click(object sender, RoutedEventArgs e)
        {
            RunCalculation();
        }

        private void RunCalculation()
        {
            var points = new System.Collections.Generic.List<(double Pa, double Cfm)>();

            for (int i = 0; i < 5; i++)
            {
                if (double.TryParse(_cfmBoxes[i].Text.Trim(), out double cfm) && cfm > 0)
                    points.Add((ResnetPa[i], cfm));
            }

            var result = MultipointCalculator.Calculate(points);

            if (!result.IsValid)
            {
                ResultBorder.Visibility = Visibility.Visible;
                ResultBorder.Background = new SolidColorBrush(Color.FromRgb(255, 235, 238));
                ResultCfm50Label.Text = "Error";
                ResultNLabel.Text = result.ErrorMessage ?? "Unknown error";
                ResultPassFailLabel.Text = "";
                ApplyButton.IsEnabled = false;
                return;
            }

            CalculatedCfm50 = result.Cfm50;
            PressureExponentN = result.N;

            ResultBorder.Visibility = Visibility.Visible;
            ResultCfm50Label.Text = $"{result.Cfm50:F0} CFM";
            ResultNLabel.Text = $"  (n = {result.N:F3})";

            bool pass = _targetCfm > 0 && result.Cfm50 <= _targetCfm;
            if (_targetCfm > 0)
            {
                ResultPassFailLabel.Text = pass ? "✓ PASS" : "✗ FAIL";
                ResultPassFailLabel.Foreground = pass
                    ? new SolidColorBrush(Color.FromRgb(27, 94, 32))
                    : new SolidColorBrush(Color.FromRgb(183, 28, 28));
                ResultBorder.Background = pass
                    ? new SolidColorBrush(Color.FromRgb(232, 245, 233))
                    : new SolidColorBrush(Color.FromRgb(255, 235, 238));
            }
            else
            {
                ResultPassFailLabel.Text = "";
                ResultBorder.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            }

            ApplyButton.IsEnabled = true;
        }

        // ── Apply ────────────────────────────────────────────────────────────

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot ring selections and CFM values into the public arrays
            for (int i = 0; i < 5; i++)
            {
                var (a, b, c) = _ringButtons[i];
                SelectedRing[i] = a.IsChecked == true ? "A"
                                : b.IsChecked == true ? "B"
                                : c.IsChecked == true ? "C"
                                : "";

                _ = double.TryParse(_cfmBoxes[i].Text.Trim(), out EnteredCfm[i]);
            }

            DialogResult = true;
        }

        // ── Cancel ───────────────────────────────────────────────────────────

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
