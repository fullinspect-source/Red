using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InspectionEditor
{
    public record ResultChoice(
        string Label,
        int StatusId,
        int NextActionId,
        string? NextActionText,
        string Background,
        bool Skip = false);

    public partial class ResultPickerWindow : Window
    {
        public ResultChoice? SelectedResult { get; private set; }
        public bool KeepOpen { get; private set; }

        // Full inspection type names
        private static readonly Dictionary<string, string> TypeNames = new()
        {
            ["AFI"]  = "ACCA 310 Field Inspection",
            ["BC"]   = "Builder Confirmation",
            ["BF"]   = "BMEP Final",
            ["BWT"]  = "New Home Orientation / Buyer Walk",
            ["COH"]  = "Flashing / Sheathing / Framing (COH)",
            ["CPP"]  = "Concrete Pre-Pour",
            ["CPR"]  = "Concrete Pour",
            ["FS"]   = "Frame Inspection",
            ["FSF"]  = "Flashing Sheathing Framing",
            ["FWI"]  = "Stage Three Fire",
            ["HEF"]  = "Final Energy Testing",
            ["HER"]  = "HERS Energy Rough",
            ["HET"]  = "Energy Star Final Testing",
            ["IAP"]  = "Indoor Air Plus",
            ["IEF"]  = "Energy Star Final",
            ["IER"]  = "Energy Star Rough",
            ["ME"]   = "BMEP Rough",
            ["MP"]   = "BMEP Rough",
            ["PLY"]  = "Polyseal",
            ["PPE"]  = "Concrete Post Pour Elevations",
            ["QIER"] = "Energy Rough",
            ["SCI"]  = "Special Consult",
            ["SRP"]  = "Slab Repair Pre-Pour",
            ["STR"]  = "Stressing",
            ["SWD"]  = "Shearwall",
            ["SWI"]  = "Shearwall Inspection",
            ["TFF"]  = "TDI Final Frame",
            ["TPC"]  = "TDI Pre Cornice",
            ["TRDI"] = "TDI Roof Decking Inspection",
            ["TRSI"] = "TDI Roof Shingle Inspection",
        };

        public ResultPickerWindow(
            string inspectionCode,
            int answeredItems,
            int failedItems,
            InspectionEditor.Services.InspectionTypeConfig? config = null,
            bool expirationStageDone = false)
        {
            InitializeComponent();

            string typeName = TypeNames.TryGetValue(inspectionCode.ToUpper(), out var n) ? n : inspectionCode;
            InspTypeText.Text = typeName;

            string failNote = failedItems > 0 ? $"  ·  {failedItems} failed" : "";
            SummaryText.Text = $"{answeredItems} items answered{failNote}";

            BuildButtons(inspectionCode.ToUpper(), failedItems > 0, config, expirationStageDone);
        }

        private void BuildButtons(
            string code,
            bool hasFails,
            InspectionEditor.Services.InspectionTypeConfig? config,
            bool expirationStageDone)
        {
            var options = new List<(ResultChoice choice, bool grayed, string grayReason)>();

            if (config != null)
            {
                if (config.EngineerReview)
                {
                    options.Add((new ResultChoice("✓  For Engineer Review", 24, 0, null, "#0D47A1"), false, ""));
                }
                else if (config.ShowComplete && !config.ShowPass && !config.ShowCorrectAndProceed
                         && !config.ShowFailNext && !config.ShowFailPO)
                {
                    options.Add((new ResultChoice("✓  Complete", 7, 0, null, "#1B5E20"), false, ""));
                }
                else
                {
                    if (config.ShowPass)
                    {
                        options.Add((
                            new ResultChoice("✓  Pass", 2, 0, null, "#1B5E20"),
                            hasFails,
                            "Not offering Pass — items are marked fail"));
                    }
                    if (config.ShowComplete)
                        options.Add((new ResultChoice("✓  Complete", 7, 0, null, "#1B5E20"), false, ""));

                    if (config.ShowCorrectAndProceed)
                    {
                        options.Add((
                            new ResultChoice("~  Correct & Proceed", 5, 0, null, "#E65100"),
                            !hasFails,
                            "Not offering C&P — all items pass"));
                    }
                    if (config.ShowFailNext)
                    {
                        bool gray = !hasFails || expirationStageDone;
                        string reason = expirationStageDone
                            ? "Not offering — next phase already complete"
                            : "Not offering — no failed items";
                        options.Add((
                            new ResultChoice("✗  Fail  —  items to be inspected at next phase",
                                3, 1, "Failed items to be inspected at next inspection", "#7B1FA2"),
                            gray, reason));
                    }
                    if (config.ShowFailPO)
                    {
                        options.Add((
                            new ResultChoice("✗  Fail  —  PO required for reinspection",
                                3, 2, "Request a reinspection. (PO required)", "#B71C1C"),
                            !hasFails,
                            "Not offering — no failed items"));
                    }
                }
            }
            else
            {
                // Fallback: original hard-coded behavior when CSV not yet downloaded
                var passFailOnly = new HashSet<string> { "BF", "STR" };
                var poFailOnly = new HashSet<string>
                {
                    "HER","HEF","HET","IER","IEF","IET","IAP",
                    "PLY","PPE","SRP","STR","TRDI","TRSI",
                    "CPR","FWI","BC","AFI",
                };

                if (code == "BWT")
                {
                    options.Add((new ResultChoice("✓  Complete", 7, 0, null, "#1B5E20"), false, ""));
                }
                else if (code == "SCI")
                {
                    options.Add((new ResultChoice("✓  For Engineer Review", 24, 0, null, "#0D47A1"), false, ""));
                }
                else
                {
                    if (!hasFails)
                        options.Add((new ResultChoice("✓  Pass", 2, 0, null, "#1B5E20"), false, ""));
                    if (!passFailOnly.Contains(code))
                        options.Add((new ResultChoice("~  Correct & Proceed", 5, 0, null, "#E65100"), false, ""));
                    if (code != "SCI")
                    {
                        options.Add((new ResultChoice("✗  Fail  —  PO required for reinspection",
                            3, 2, "Request a reinspection. (PO required)", "#B71C1C"), false, ""));
                        if (!poFailOnly.Contains(code))
                            options.Add((new ResultChoice("✗  Fail  —  items to be inspected at next phase",
                                3, 1, "Failed items to be inspected at next inspection", "#7B1FA2"), false, ""));
                    }
                }
            }

            foreach (var (choice, grayed, reason) in options)
            {
                if (grayed && !string.IsNullOrEmpty(reason))
                {
                    var wrapper = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
                    var btn = MakeButton(choice, grayed: true);
                    btn.Margin = new Thickness(0); // wrapper supplies the bottom spacing
                    wrapper.Children.Add(btn);
                    wrapper.Children.Add(new TextBlock
                    {
                        Text = reason,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(4, 2, 0, 0),
                    });
                    ButtonPanel.Children.Add(wrapper);
                }
                else
                {
                    ButtonPanel.Children.Add(MakeButton(choice, grayed: grayed));
                }
            }
        }

        private Button MakeButton(ResultChoice choice, bool grayed = false)
        {
            var btn = new Button
            {
                Content = choice.Label,
                Tag = choice,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(choice.Background)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(24, 22, 24, 22),
                Margin = new Thickness(0, 0, 0, 14),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = grayed ? Cursors.Arrow : Cursors.Hand,
                IsEnabled = !grayed,
                Opacity = grayed ? 0.4 : 1.0,
            };
            if (!grayed)
                btn.Click += ResultButton_Click;
            return btn;
        }

        private void ResultButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ResultChoice choice)
            {
                SelectedResult = choice;
                DialogResult = true;
                Close();
            }
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedResult = null;
            DialogResult = false;
            Close();
        }

        private void KeepOpenButton_Click(object sender, RoutedEventArgs e)
        {
            KeepOpen = true;
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                KeepOpen = true;
                DialogResult = false;
                Close();
            }
        }
    }
}
