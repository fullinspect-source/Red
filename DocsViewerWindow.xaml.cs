using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace InspectionEditor
{
    public partial class DocsViewerWindow : Window
    {
        private readonly string _jobId;
        private readonly string _jobsFolder;
        private readonly List<DocFile> _engineeringDocs = new List<DocFile>();
        private readonly List<DocFile> _inspectionDocs = new List<DocFile>();
        private readonly List<int> _availableRevisions = new List<int>();
        private int _selectedRevision = 0;
        
        // Touch scrolling
        private Point? _touchStartPoint;
        private double _touchStartOffset;
        
        // Engineering document suffix to description mapping
        private static readonly Dictionary<string, string> EngineeringSuffixNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"EC", "Energy Compliance Report"},
            {"EL", "Electrical Plan"},
            {"FFP", "Foundation Floor Plan"},
            {"FD", "Foundation Details"},
            {"FDD", "Foundation Details"},
            {"FRD", "Framing Details"},
            {"FRT", "Framing Plan"},
            {"FR", "Framing Plan"},
            {"SW", "Shearwall Plan"},
            {"PESR", "PESR Report"},
            {"ARCH", "Architectural Plan"},
            {"STUD", "Stud Layout"},
            {"HEB", "HEB Details"}
        };
        
        // Inspection code to human-readable name mapping (Strand inspection types)
        private static readonly Dictionary<string, string> InspectionCodeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"AFI", "ACCA Test"},
            {"BC", "Builder Confirmation"},
            {"BF", "BMEP Final"},
            {"BWT", "New Home Orientation"},
            {"COH", "BMEP Rough"},
            {"CPP", "Concrete Pre Pour"},
            {"CPR", "Concrete Pour"},
            {"FS", "Structural Frame"},
            {"FSF", "Flash Sheath Frame"},
            {"FWI", "No3 Firewall"},
            {"HEF", "Final Energy Testing"},
            {"HER", "HERS Energy Rough"},
            {"HET", "Final Energy Testing"},
            {"IAP", "Indoor Air Plus"},
            {"IEF", "HERS Energy Final"},
            {"IER", "IECC Energy Rough"},
            {"ME", "BMEP Rough"},
            {"MP", "BMEP Rough"},
            {"PLY", "Polyseal"},
            {"PPE", "Post Pour Elevations"},
            {"QIER", "Energy Rough"},
            {"SCI", "Special Consult"},
            {"SRP", "Slab Repair Prepour"},
            {"STR", "Stressing"},
            {"SWI", "Shearwall Inspection"},
            {"TFF", "TDI Final Frame"},
            {"TPC", "TDI Precornice"},
            {"TRDI", "TDI Roof Decking"},
            {"TRSI", "TDI Shingles"}
        };

        public DocsViewerWindow(string insFilePath)
        {
            InitializeComponent();
            
            // Parse job ID from filename: {jobID}-{inspType}-{tripCount}-{initials}.ins
            string filename = Path.GetFileNameWithoutExtension(insFilePath);
            var parts = filename.Split('-');
            _jobId = parts.Length > 0 ? parts[0] : "";
            
            // Find the Jobs folder (sibling to MyList)
            string? insFolder = Path.GetDirectoryName(insFilePath);
            if (insFolder != null)
            {
                string? inspectionsFolder = Path.GetDirectoryName(insFolder); // Go up from MyList
                _jobsFolder = inspectionsFolder != null ? Path.Combine(inspectionsFolder, "Jobs") : "";
            }
            else
            {
                _jobsFolder = "";
            }
            
            JobIdText.Text = $"Job ID: {_jobId}";
            
            LoadDocuments();
            BuildRevisionButtons();
            RefreshDocsList();
        }

        private void LoadDocuments()
        {
            if (string.IsNullOrEmpty(_jobId) || string.IsNullOrEmpty(_jobsFolder))
                return;

            string jobFolder = Path.Combine(_jobsFolder, _jobId);
            if (!Directory.Exists(jobFolder))
            {
                AddressText.Text = "Job folder not found";
                return;
            }

            // Load Engineering docs
            string engFolder = Path.Combine(jobFolder, "Engineering");
            if (Directory.Exists(engFolder))
            {
                foreach (var file in Directory.GetFiles(engFolder, "*.pdf"))
                {
                    var doc = ParseEngineeringFile(file);
                    _engineeringDocs.Add(doc);
                    
                    if (!_availableRevisions.Contains(doc.Revision))
                        _availableRevisions.Add(doc.Revision);
                }
            }

            // Load Inspection docs
            string inspFolder = Path.Combine(jobFolder, "Inspections");
            if (Directory.Exists(inspFolder))
            {
                foreach (var file in Directory.GetFiles(inspFolder, "*.pdf"))
                {
                    _inspectionDocs.Add(ParseInspectionFile(file));
                }
            }

            // Sort revisions and default to highest
            _availableRevisions.Sort();
            if (_availableRevisions.Count > 0)
                _selectedRevision = _availableRevisions.Max();
            
            // Update address text
            int totalDocs = _engineeringDocs.Count + _inspectionDocs.Count;
            AddressText.Text = $"{totalDocs} document{(totalDocs != 1 ? "s" : "")} available";
        }

        private DocFile ParseEngineeringFile(string filePath)
        {
            string filename = Path.GetFileName(filePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            
            // Parse pattern: {jobID}[R{n}]{suffix} or "{description} ({jobID}[R{n}]{suffix})"
            // Examples: 2555728.pdf, 2555728R1.pdf, 2555728EC.pdf, 2555728R1EC.pdf
            //           2555728 (with detail sheets).pdf, 506 Marek Fareway Court (2555728R1FDD).pdf
            
            int revision = 0;
            string docType = "";
            
            // Check if filename has description prefix with jobID in parentheses
            var parenMatch = Regex.Match(nameWithoutExt, $@"\({Regex.Escape(_jobId)}(R(\d+))?([A-Z]*)\)\s*$", RegexOptions.IgnoreCase);
            if (parenMatch.Success)
            {
                // Format: "Description (2555728R1FDD)"
                if (parenMatch.Groups[2].Success)
                    revision = int.Parse(parenMatch.Groups[2].Value);
                docType = parenMatch.Groups[3].Value.ToUpper();
                // Include the description prefix as part of docType for grouping
                string descPrefix = nameWithoutExt.Substring(0, parenMatch.Index).Trim();
                if (!string.IsNullOrEmpty(descPrefix))
                    docType = descPrefix + "|" + docType;
            }
            else
            {
                // Format: "2555728R1EC" or "2555728 (with detail sheets)"
                var directMatch = Regex.Match(nameWithoutExt, $@"^{Regex.Escape(_jobId)}(R(\d+))?(.*)$", RegexOptions.IgnoreCase);
                if (directMatch.Success)
                {
                    if (directMatch.Groups[2].Success)
                        revision = int.Parse(directMatch.Groups[2].Value);
                    docType = directMatch.Groups[3].Value.Trim().ToUpper();
                }
            }

            // Extract suffix for description lookup (remove any description prefix)
            string suffixForLookup = docType.Contains("|") ? docType.Split('|').Last() : docType;
            string description = "";
            if (!string.IsNullOrEmpty(suffixForLookup) && EngineeringSuffixNames.TryGetValue(suffixForLookup, out string? suffixDesc))
            {
                description = suffixDesc;
            }
            
            return new DocFile
            {
                FullPath = filePath,
                Filename = filename,
                Revision = revision,
                DocumentType = docType,
                Description = description
            };
        }

        private DocFile ParseInspectionFile(string filePath)
        {
            string filename = Path.GetFileName(filePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            string description = "";

            // Try to parse: {jobID}-{inspCode}-{trip}-{initials}
            var match = Regex.Match(nameWithoutExt, $@"^{Regex.Escape(_jobId)}-([A-Z]+)-(\d+)-([A-Z]+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string inspCode = match.Groups[1].Value.ToUpper();
                string trip = match.Groups[2].Value;
                
                if (InspectionCodeNames.TryGetValue(inspCode, out string? inspName))
                {
                    description = $"{inspName} {trip}";
                }
                else
                {
                    description = $"{inspCode} {trip}";
                }
            }

            return new DocFile
            {
                FullPath = filePath,
                Filename = filename,
                Revision = -1, // Inspections don't have revisions
                Description = description
            };
        }

        private void BuildRevisionButtons()
        {
            RevisionButtonsPanel.Children.Clear();

            if (_availableRevisions.Count == 0)
            {
                // Just show R0 if no revisions found
                _availableRevisions.Add(0);
            }

            foreach (int rev in _availableRevisions.OrderBy(r => r))
            {
                var btn = new ToggleButton
                {
                    Content = rev == 0 ? "Base" : $"R{rev}",
                    Tag = rev,
                    Style = (Style)FindResource("RevisionButton"),
                    IsChecked = rev == _selectedRevision
                };
                btn.Click += RevisionButton_Click;
                RevisionButtonsPanel.Children.Add(btn);
            }
        }

        private void RevisionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton clickedBtn && clickedBtn.Tag is int rev)
            {
                _selectedRevision = rev;
                
                // Update button states
                foreach (ToggleButton btn in RevisionButtonsPanel.Children.OfType<ToggleButton>())
                {
                    btn.IsChecked = (int)btn.Tag == _selectedRevision;
                }
                
                RefreshDocsList();
            }
        }

        private void RefreshDocsList()
        {
            DocsListPanel.Children.Clear();

            // ENGINEERING SECTION
            var filteredEngDocs = FilterEngineeringDocs();
            if (filteredEngDocs.Any())
            {
                AddSectionHeader("ENGINEERING");
                foreach (var doc in filteredEngDocs.OrderBy(d => d.Filename))
                {
                    AddDocRow(doc);
                }
            }

            // INSPECTIONS SECTION (not filtered)
            if (_inspectionDocs.Any())
            {
                AddSectionHeader("INSPECTIONS");
                foreach (var doc in _inspectionDocs.OrderBy(d => d.Filename))
                {
                    AddDocRow(doc);
                }
            }

            // No docs message
            if (!filteredEngDocs.Any() && !_inspectionDocs.Any())
            {
                DocsListPanel.Children.Add(new TextBlock
                {
                    Text = "No documents found for this job.",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(10),
                    FontSize = 13
                });
            }
        }

        private List<DocFile> FilterEngineeringDocs()
        {
            // Group documents by their type (suffix like EC, EL, FFP, etc.)
            // For each type, show only the selected revision if it exists, otherwise show base (R0)
            var result = new List<DocFile>();
            
            var groupedByType = _engineeringDocs.GroupBy(d => d.DocumentType);
            
            foreach (var group in groupedByType)
            {
                // Check if selected revision exists for this document type
                var selectedRevDoc = group.FirstOrDefault(d => d.Revision == _selectedRevision);
                
                if (selectedRevDoc != null)
                {
                    // Selected revision exists - show only that
                    result.Add(selectedRevDoc);
                }
                else
                {
                    // Selected revision doesn't exist for this type - show base (R0)
                    var baseDoc = group.FirstOrDefault(d => d.Revision == 0);
                    if (baseDoc != null)
                        result.Add(baseDoc);
                }
            }
            
            return result;
        }

        private void AddSectionHeader(string title)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 10, 0, 5)
            };
            border.Child = new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100))
            };
            DocsListPanel.Children.Add(border);
        }

        private void AddDocRow(DocFile doc)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

            var filenameBtn = new Button
            {
                Content = doc.Filename,
                Tag = doc.FullPath,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            filenameBtn.Click += DocButton_Click;
            
            // Hover effect
            filenameBtn.MouseEnter += (s, e) => filenameBtn.Background = new SolidColorBrush(Color.FromRgb(230, 245, 255));
            filenameBtn.MouseLeave += (s, e) => filenameBtn.Background = Brushes.Transparent;

            Grid.SetColumn(filenameBtn, 0);
            grid.Children.Add(filenameBtn);

            if (!string.IsNullOrEmpty(doc.Description))
            {
                var descText = new TextBlock
                {
                    Text = doc.Description,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 10, 0)
                };
                Grid.SetColumn(descText, 1);
                grid.Children.Add(descText);
            }

            DocsListPanel.Children.Add(grid);
        }

        private void DocButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string filePath)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open file: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Keyboard handler - Esc closes the dialog
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #region Touch/Mouse Swipe Scrolling
        // Surface Pro reports touch as mouse events, so use mouse-based drag handlers
        // with a 15px threshold to distinguish taps from swipes
        private bool _isSwipeScrolling = false;
        private const double SwipeThreshold = 15.0;

        private void ScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                _touchStartPoint = e.GetPosition(sv);
                _touchStartOffset = sv.VerticalOffset;
                _isSwipeScrolling = false;
            }
        }

        private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_touchStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed && sender is ScrollViewer sv)
            {
                var currentPoint = e.GetPosition(sv);
                double delta = _touchStartPoint.Value.Y - currentPoint.Y;

                if (!_isSwipeScrolling && Math.Abs(delta) >= SwipeThreshold)
                {
                    _isSwipeScrolling = true;
                    Mouse.Capture(sv);
                }

                if (_isSwipeScrolling)
                {
                    sv.ScrollToVerticalOffset(_touchStartOffset + delta);
                    e.Handled = true;
                }
            }
        }

        private void ScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSwipeScrolling)
            {
                _isSwipeScrolling = false;
                Mouse.Capture(null);
                e.Handled = true;
            }
            _touchStartPoint = null;
        }
        #endregion
    }

    internal class DocFile
    {
        public string FullPath { get; set; } = "";
        public string Filename { get; set; } = "";
        public int Revision { get; set; } // -1 for inspections, 0+ for engineering
        public string DocumentType { get; set; } = ""; // Suffix like EC, EL, FFP for grouping
        public string Description { get; set; } = "";
    }
}
