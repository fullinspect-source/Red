using InspectionEditor.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InspectionEditor
{
    public partial class RulesAcknowledgmentWindow : Window
    {
        private List<CheckBox> _ruleCheckboxes = new List<CheckBox>();
        public bool AllAcknowledged { get; private set; } = false;
        
        // Mouse-based scrolling (for tablets where touch reports as mouse)
        private Point? _scrollStartPoint;
        private double _scrollStartOffset;
        private bool _isScrolling = false;

        private readonly bool _enforceAcknowledgment;

        public List<string> SelectedCompanionPaths { get; } = new();
        private readonly List<(CheckBox cb, string filePath)> _companionCheckboxes = new();

        public RulesAcknowledgmentWindow(List<InspectionRule> rules, string inspectionCode, string clientName, bool enforceAcknowledgment = true)
        {
            _enforceAcknowledgment = enforceAcknowledgment;
            InitializeComponent();
            
            SubtitleText.Text = $"Inspection: {inspectionCode} | Client: {clientName}";
            
            PopulateRules(rules);
        }

        // Must be called before ShowDialog(). Has no effect if called after the window closes.
        public void SetFreeReinspectionAlerts(List<FreeReinspectionAlert> alerts)
        {
            FreeReinspectionPanel.Children.Clear();
            _companionCheckboxes.Clear();
            SelectedCompanionPaths.Clear();

            if (alerts.Count == 0)
            {
                FreeReinspectionPanel.Visibility = Visibility.Collapsed;
                return;
            }

            FreeReinspectionPanel.Visibility = Visibility.Visible;

            FreeReinspectionPanel.Children.Add(new TextBlock
            {
                Text = "Free Reinspections Included With This Service",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 100, 0)),
            });

            foreach (var alert in alerts)
            {
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(200, 140, 0)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromRgb(255, 251, 235)),
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(15),
                    CornerRadius = new CornerRadius(5),
                };

                var stack = new StackPanel();

                if (alert.IsInMyList && !string.IsNullOrEmpty(alert.MyListFilePath))
                {
                    var cb = new CheckBox
                    {
                        IsChecked = true,
                        FontSize = 13,
                        MinHeight = 40,
                        Padding = new Thickness(8, 0, 0, 0),
                        Content = $"Open free reinspection alongside this one: [{alert.InsType}: {alert.DisplayName}]",
                    };
                    cb.LayoutTransform = new ScaleTransform(1.3, 1.3);
                    _companionCheckboxes.Add((cb, alert.MyListFilePath));
                    stack.Children.Add(cb);
                }
                else
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"⚠  Free reinspection not in MyList",
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(160, 80, 0)),
                        FontSize = 13,
                        Margin = new Thickness(0, 0, 0, 6),
                    });
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"This service includes a free [{alert.InsType}: {alert.DisplayName}] reinspection but it is not in your MyList. Download it before opening this job.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = Brushes.DarkOliveGreen,
                    });
                }

                border.Child = stack;
                FreeReinspectionPanel.Children.Add(border);
            }
        }

        private void PopulateRules(List<InspectionRule> rules)
        {
            _ruleCheckboxes.Clear();
            RulesPanel.Children.Clear();

            foreach (var rule in rules.Where(r => r.RequireAck))
            {
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(139, 0, 0)),
                    BorderThickness = new Thickness(1),
                    Background = Brushes.White,
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(15),
                    CornerRadius = new CornerRadius(5)
                };

                var stack = new StackPanel();

                // Rule header
                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                header.Children.Add(new TextBlock 
                { 
                    Text = $"[{rule.ServiceType}]", 
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(41, 128, 185)),
                    Margin = new Thickness(0, 0, 10, 0)
                });
                header.Children.Add(new TextBlock 
                { 
                    Text = rule.ClientName, 
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(142, 68, 173))
                });
                stack.Children.Add(header);

                // Rule message
                var message = new TextBlock
                {
                    Text = rule.Message,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                stack.Children.Add(message);

                // Created by info
                var meta = new TextBlock
                {
                    Text = $"Created by: {rule.CreatedBy} ({rule.CreatedDate})",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                stack.Children.Add(meta);

                // Checkbox - larger for touch targets
                var checkbox = new CheckBox
                {
                    Content = "I have read and understand this rule",
                    FontSize = 14,
                    Tag = rule,
                    MinHeight = 40,
                    Padding = new Thickness(8, 0, 0, 0)
                };
                
                // Make the checkbox box itself larger
                checkbox.LayoutTransform = new ScaleTransform(1.5, 1.5);
                
                checkbox.Checked += Checkbox_Changed;
                checkbox.Unchecked += Checkbox_Changed;
                _ruleCheckboxes.Add(checkbox);
                stack.Children.Add(checkbox);

                border.Child = stack;
                RulesPanel.Children.Add(border);
            }

            // If no rules require acknowledgment, allow proceeding
            if (_ruleCheckboxes.Count == 0)
            {
                RulesPanel.Children.Add(new TextBlock
                {
                    Text = "No rules require acknowledgment for this inspection.",
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 20, 0, 0)
                });
                AcknowledgeAllButton.IsEnabled = true;
                AcknowledgeAllButton.Content = _enforceAcknowledgment ? "✓ Continue" : "✓ Close";
            }
            else if (!_enforceAcknowledgment)
            {
                AcknowledgeAllButton.IsEnabled = true;
                AcknowledgeAllButton.Content = "✓ Close";
            }
        }

        private void Checkbox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_enforceAcknowledgment) return;
            // Enable button only if all checkboxes are checked
            AcknowledgeAllButton.IsEnabled = _ruleCheckboxes.All(cb => cb.IsChecked == true);
        }

        private void AcknowledgeAllButton_Click(object sender, RoutedEventArgs e)
        {
            AllAcknowledged = true;
            SelectedCompanionPaths.Clear();
            foreach (var (cb, path) in _companionCheckboxes)
                if (cb.IsChecked == true)
                    SelectedCompanionPaths.Add(path);
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            AllAcknowledged = false;
            DialogResult = false;
        }
        
        #region Mouse-based Touch Scrolling
        private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _scrollStartPoint = e.GetPosition(RulesScrollViewer);
                _scrollStartOffset = RulesScrollViewer.VerticalOffset;
                _isScrolling = true;
            }
        }

        private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isScrolling && _scrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(RulesScrollViewer);
                double deltaY = _scrollStartPoint.Value.Y - currentPoint.Y;
                
                // Only scroll if moved more than 10 pixels (prevents accidental scrolls)
                if (Math.Abs(deltaY) > 10)
                {
                    RulesScrollViewer.ScrollToVerticalOffset(_scrollStartOffset + deltaY);
                }
            }
        }

        private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isScrolling = false;
            _scrollStartPoint = null;
        }
        #endregion
    }
}
