using InspectionEditor.Models;
using InspectionEditor.Services;
using Microsoft.Win32;
using Newtonsoft.Json;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace InspectionEditor
{
    public partial class MainWindow : Window
    {
        private static readonly string AppVersion = AppIdentity.Version;
        
        private InspectionFile? _currentInspection;
        private Item? _currentItem;
        private Section? _currentSection; // Section that contains _currentItem
        private Item? _editorLoadedItem; // The specific item loaded in editor - used for Duplicate
        private byte[]? _currentPhotoData;
        private int _currentPhotoIndex = 0;
        private GrokApiClient? _grokClient;
        private SurgicalSaveService _saveService = new SurgicalSaveService();
        private UserDataService? _userDataService;
        private InspectionRulesService _rulesService = new InspectionRulesService();
        private CameraService _cameraService = new CameraService();
        private TemplateService _templateService = new TemplateService();
        private InspectorStatsService _statsService = new InspectorStatsService();
        private QuickCommentsService _quickCommentsService = new QuickCommentsService();
        private InspectionActivityService _activityService = new InspectionActivityService();
        private string? _currentInspectionCode;
        private string? _currentInspectorName;
        private string? _currentFilePath;
        private string? _currentBuilderName;
        private string? _currentProjectName;
        private string _currentClientName = "";
        private SlabEngineeringInfo? _currentSlabInfo;
        private EnergyComplianceInfo? _currentEcInfo;
        private string? _planPdfPath; // For non-CPP plan types (EC, FFP, Arch, Eng, etc.)
        private bool _designExtractionLoading;
        
        // Stats mode: which tier of averages to use for section counters
        private enum StatsMode { Global, Builder, Project }
        private StatsMode _statsMode = StatsMode.Global;
        private bool _hasUnsavedChanges = false;
        private bool _hasBeenEditedThisSession = false;
        private bool _skipResultCheck = false;
        private bool _rulesAcknowledged = false;
        private bool _isRefreshingTree = false;
        
        private string? _selectedPrefix;
        private List<string> _selectedSuffixes = new List<string>();
        private List<ToggleButton> _prefixButtons = new List<ToggleButton>();
        private List<ToggleButton> _suffixButtons = new List<ToggleButton>();
        private bool _isLoadingEditor = false; // Suppress button handlers during item load
        
        // Mouse-based scrolling (for tablets where touch reports as mouse)
        private Point? _scrollStartPoint;
        private double _scrollStartOffset;
        private bool _isScrolling = false;
        private bool _scrollStarted = false; // True once drag threshold exceeded
        private bool _checklistHorizontalSwipeStarted = false;
        private bool _inlineStripSwipeStarted = false;
        private bool _checklistSwipeStartedInInlineStrip = false;
        private bool _checklistSwipeStartedInInlineVerticalZone = false;
        private Item? _checklistSwipeItem;
        private Point? _globalInlineSwipeStartPoint;
        private bool _globalInlineSwipeStarted = false;
        private bool _globalInlineSwipeCanCollapse = false;
        private Point? _inlineHorizontalScrollStartPoint;
        private double _inlineHorizontalScrollStartOffset;
        private ScrollViewer? _inlineHorizontalScrollViewer;
        private bool _inlineHorizontalIsScrolling = false;
        private bool _inlineHorizontalScrollStarted = false;
        private Point? _inlineVerticalScrollStartPoint;
        private double _inlineVerticalScrollStartOffset;
        private ScrollViewer? _inlineVerticalScrollViewer;
        private bool _inlineVerticalIsScrolling = false;
        private bool _inlineVerticalScrollStarted = false;
        private bool _inlineNumberpadSliderDragActive = false;
        private System.Windows.Threading.DispatcherTimer? _inlineChipLongPressTimer;
        private InlinePrefixSuffixAction? _inlineChipLongPressAction;
        private bool _inlineChipLongPressFired = false;
        
        // OFI (Only Fail Items) filter
        private bool _ofiFilterActive = false;
        
        // Tracks whether a Fail→Pass clear action should jump to the next failed item.
        private bool _advanceToNextFailAfterPassClear = false;
        
        // REQ (Required Items Only) filter
        private bool _reqFilterActive = false;
        
        // INC (Incomplete Required Items) filter
        private bool _incFilterActive = false;
        
        // Checklist font size (adjustable)
        private const double DefaultChecklistFontSize = 18;
        private double _checklistFontSize = DefaultChecklistFontSize;
        
        // Read-only mode for unsupported inspection types (BWT, SCI)
        private bool _readOnlyMode = false;

        private const string SpecialistFlagMarker = "\u25A9";
        private const int SpecialistFlagMinimumCommentLength = 10;
        private bool _isLoadingFile = false;
        
        // Multi-instance locking: prevent same file or same inspection type across RED windows
        private Mutex? _fileMutex;
        private Mutex? _typeMutex;
        private string? _openLockMetadataPath;
        
        // Transcribe mode - when true, clicking suggestion puts value in Value field (not Comments)
        private bool _isTranscribeMode = false;
        private List<string>? _lastTranscriptionOptions;
        private string _aiTone = "Technical";

        // Bottom-right panel touch scroll state
        private Point? _savedCommentsScrollStartPoint;
        private double _savedCommentsScrollStartOffset;
        private double _savedCommentsParentScrollStartOffset;
        private bool _savedCommentsIsScrolling = false;
        private bool _savedCommentsScrollStarted = false;
        private Point? _suggestionsScrollStartPoint;
        private double _suggestionsScrollStartOffset;
        private double _suggestionsParentScrollStartOffset;
        private bool _suggestionsIsScrolling = false;
        private bool _suggestionsScrollStarted = false;

        // MegaStats overlay scroll state
        private Point? _megaScrollStartPoint;
        private double _megaScrollStartOffset;
        private bool _megaIsScrolling = false;
        private bool _megaScrollStarted = false;

        // Track banner states so we can detect transitions TO red for flash effect
        private EnergyComplianceService.BannerState _ecBannerState   = EnergyComplianceService.BannerState.Gray;
        private EnergyComplianceService.BannerState _slabBannerState = EnergyComplianceService.BannerState.Gray;
        
        // Remember last photo file picker directory across items
        private static string? _lastPhotoDirectory;
        
        // Welcome screen logo triple-click easter egg
        private int _logoClickCount = 0;
        private DateTime _lastLogoClick = DateTime.MinValue;
        private bool _taglineVisible = false;

        private readonly InspectionEditor.Services.InspectionTypeService _inspTypeService =
            new InspectionEditor.Services.InspectionTypeService();
        private bool _openedAsCompanion = false;
        private int _currentSequenceNumber = 1;

        private bool _inlineEditorMode = true;
        private double _classicLeftPanelWidth = 350;
        private string? _expandedInlineItemKey;
        private Item? _expandedInlineItemInstance;
        private readonly HashSet<string> _collapsedInlineSectionKeys = new HashSet<string>();
        private Item? _inlineQuickCommentsDismissedItem;
        private readonly Dictionary<Item, InlineAiResult> _inlineAiResults = new Dictionary<Item, InlineAiResult>();
        private bool _suppressAdministrativeTools = true;
        private InlineDrawerPreferences _inlineDrawerPreferences = new InlineDrawerPreferences();
        private static readonly string[] InlineStandardDrawers =
        {
            "Comments", "Photos", "AI Help"
        };
        private const string InlineExperimentalToolsDrawer = "Experimental Tools";
        private const string InlineBeamDimensionsDrawer = "Beam Dimensions";
        private const string InlineNumberpadDrawer = "Numberpad";
        private const string InlineToolSelectorDrawer = "Tool Selector";
        private const string InlineValueChoicesDrawer = "Value Choices";
        private static readonly DependencyProperty InlineValueDisplayProperty =
            DependencyProperty.RegisterAttached(
                "InlineValueDisplay",
                typeof(bool),
                typeof(MainWindow),
                new PropertyMetadata(false));
        
        public MainWindow()
        {
            InitializeComponent();
            Title = GetDefaultWindowTitle();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            this.Activated += MainWindow_Activated;
            this.Deactivated += MainWindow_Deactivated;
            this.PreviewMouseLeftButtonDown += MainWindow_GlobalPreviewMouseLeftButtonDown;
            this.PreviewMouseMove += MainWindow_GlobalPreviewMouseMove;
            this.PreviewMouseLeftButtonUp += MainWindow_GlobalPreviewMouseLeftButtonUp;
            
            // Load user preferences
            LoadPreferences();
            ApplyChecklistEditorMode();
            _ = Task.Run(() =>
            {
                try { _statsService.HasStats(); }
                catch { }
            });
        }

        private static string GetDefaultWindowTitle()
        {
            return AppIdentity.WindowTitle;
        }

        /// <summary>
        /// When this RED window loses focus (user switches to another RED window),
        /// stop the camera service to prevent photos from being added to multiple reports.
        /// BUT: Don't stop if we're just switching to the camera app we opened.
        /// </summary>
        private void MainWindow_Deactivated(object? sender, EventArgs e)
        {
            if (_cameraService.IsActive)
            {
                // Check if camera app is running — if so, we're switching to OUR camera, don't stop
                bool cameraIsRunning = false;
                try
                {
                    var cameraProcs = System.Diagnostics.Process.GetProcessesByName("WindowsCamera");
                    cameraIsRunning = cameraProcs.Length > 0;
                    foreach (var p in cameraProcs) p.Dispose();
                }
                catch { }
                
                // Only stop camera service if we're NOT switching to the camera app
                if (!cameraIsRunning)
                {
                    _cameraService.PhotoCaptured -= OnPhotoCaptured;
                    _cameraService.StopSession();
                    CameraButton.Content = "📷 Camera";
                }
            }
        }

        /// <summary>
        /// When this RED window regains focus, the camera stays closed
        /// (user must explicitly click Camera button again to resume).
        /// This prevents accidental multi-window photo adds.
        /// </summary>
        private void MainWindow_Activated(object? sender, EventArgs e)
        {
            // Do nothing — user must explicitly reopen camera after switching windows
        }
        
        private void LoadPreferences()
        {
            try
            {
                string prefPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preferences.txt");
                if (File.Exists(prefPath))
                {
                    var lines = File.ReadAllLines(prefPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("ChecklistFontSize="))
                        {
                            if (double.TryParse(line.Substring("ChecklistFontSize=".Length), out double size))
                            {
                                _checklistFontSize = size;
                            }
                        }
                        else if (line.StartsWith("LeftPanelWidth="))
                        {
                            if (double.TryParse(line.Substring("LeftPanelWidth=".Length), out double width))
                            {
                                // Set column width (clamp between min/max)
                                width = Math.Max(250, Math.Min(900, width));
                                _classicLeftPanelWidth = width;
                                LeftColumn.Width = new GridLength(width);
                            }
                        }
                        else if (line.StartsWith("LastPhotoDirectory="))
                        {
                            string dir = line.Substring("LastPhotoDirectory=".Length);
                            if (Directory.Exists(dir))
                                _lastPhotoDirectory = dir;
                        }
                        else if (line.StartsWith("AiTone="))
                        {
                            _aiTone = NormalizeAiTone(line.Substring("AiTone=".Length));
                        }
                        else if (line.StartsWith("InlineEditorModeV2="))
                        {
                            bool.TryParse(line.Substring("InlineEditorModeV2=".Length), out _inlineEditorMode);
                        }
                        else if (line.StartsWith("EditorWindowLeft="))
                        {
                            if (double.TryParse(line.Substring("EditorWindowLeft=".Length), out double left))
                                Left = left;
                        }
                        else if (line.StartsWith("EditorWindowTop="))
                        {
                            if (double.TryParse(line.Substring("EditorWindowTop=".Length), out double top))
                                Top = top;
                        }
                        else if (line.StartsWith("EditorWindowWidth="))
                        {
                            if (double.TryParse(line.Substring("EditorWindowWidth=".Length), out double width))
                                Width = Math.Max(MinWidth > 0 ? MinWidth : 900, width);
                        }
                        else if (line.StartsWith("EditorWindowHeight="))
                        {
                            if (double.TryParse(line.Substring("EditorWindowHeight=".Length), out double height))
                                Height = Math.Max(MinHeight > 0 ? MinHeight : 650, height);
                        }
                        else if (line.StartsWith("EditorWindowState="))
                        {
                            if (Enum.TryParse(line.Substring("EditorWindowState=".Length), out WindowState savedState) &&
                                savedState != WindowState.Minimized)
                            {
                                WindowState = savedState;
                            }
                        }
                    }
                }
                if (IsReasonableEditorWindowPosition(Left, Top))
                    WindowStartupLocation = WindowStartupLocation.Manual;
            }
            catch { /* Ignore preference load errors */ }

            LoadInlineDrawerPreferences();
            _suppressAdministrativeTools = SettingsWindow.LoadSuppressAdministrativeTools();
            _aiTone = SettingsWindow.LoadDefaultAiTone();
        }
        
        private void SavePreferences()
        {
            try
            {
                string prefPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preferences.txt");
                double leftPanelWidth = _inlineEditorMode ? _classicLeftPanelWidth : LeftColumn.ActualWidth;
                var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
                var content = $"ChecklistFontSize={_checklistFontSize}\n" +
                              $"LeftPanelWidth={leftPanelWidth}\n" +
                              $"LastPhotoDirectory={_lastPhotoDirectory ?? ""}\n" +
                              $"AiTone={NormalizeAiTone(_aiTone)}\n" +
                              $"InlineEditorModeV2={_inlineEditorMode}\n" +
                              $"EditorWindowLeft={bounds.Left}\n" +
                              $"EditorWindowTop={bounds.Top}\n" +
                              $"EditorWindowWidth={bounds.Width}\n" +
                              $"EditorWindowHeight={bounds.Height}\n" +
                              $"EditorWindowState={WindowState}\n";
                File.WriteAllText(prefPath, content);
            }
            catch { /* Ignore preference save errors */ }
        }

        private static bool IsReasonableEditorWindowPosition(double left, double top)
        {
            return left > SystemParameters.VirtualScreenLeft - 2000 &&
                   top > SystemParameters.VirtualScreenTop - 2000 &&
                   left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2000 &&
                   top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2000;
        }

        private string InlineDrawerPreferencesPath =>
            Path.Combine(AppIdentity.LocalAppDataPath, "inline-drawer-preferences.json");

        private void LoadInlineDrawerPreferences()
        {
            try
            {
                string path = InlineDrawerPreferencesPath;
                if (File.Exists(path))
                {
                    _inlineDrawerPreferences =
                        JsonConvert.DeserializeObject<InlineDrawerPreferences>(File.ReadAllText(path))
                        ?? new InlineDrawerPreferences();
                    _inlineDrawerPreferences.DefaultClosedDrawers ??= new List<string>();
                    _inlineDrawerPreferences.ItemClosedDrawers ??= new Dictionary<string, List<string>>();
                    _inlineDrawerPreferences.ItemOpenedSpecialDrawers ??= new Dictionary<string, List<string>>();
                    _inlineDrawerPreferences.ItemStandardToolOverrides ??= new List<string>();
                    _inlineDrawerPreferences.ValueUsageCounts ??= new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                    _inlineDrawerPreferences.NumberpadRanges ??= new Dictionary<string, InlineNumberpadRange>(StringComparer.OrdinalIgnoreCase);
                    _inlineDrawerPreferences.CustomNumberpadRanges ??= new Dictionary<string, InlineNumberpadRange>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _inlineDrawerPreferences = new InlineDrawerPreferences();
            }
        }

        private void SaveInlineDrawerPreferences()
        {
            try
            {
                Directory.CreateDirectory(AppIdentity.LocalAppDataPath);
                File.WriteAllText(
                    InlineDrawerPreferencesPath,
                    JsonConvert.SerializeObject(_inlineDrawerPreferences, Formatting.Indented));
            }
            catch { /* Ignore preference save errors */ }
        }
        
        private void GridSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            SavePreferences();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Save first so result/status data is current before deciding whether to prompt.
            if (_hasUnsavedChanges && _currentInspection != null)
            {
                SaveCurrentInspectionInPlace();
            }

            // Show result picker if this session had edits and result is not yet set
            if (!_skipResultCheck && ShouldShowResultPicker())
            {
                e.Cancel = true;
                ShowResultPicker(closeAfterPicker: true);
                return;
            }

            // Log activity: inspection closed
            _activityService.LogClose();
            SavePreferences();

            // Release multi-instance locks
            ReleaseInspectionLocks();

            // Clean up camera session
            _cameraService.PhotoCaptured -= OnPhotoCaptured;
            _cameraService.StopSession();
        }

        private bool ShouldShowResultPicker()
        {
            if (!_hasBeenEditedThisSession) return false;
            if (_currentInspection == null) return false;
            if (_stringIsNullOrWhiteSpace(_currentInspectionCode)) return false;

            // If the active save target was cleared during save/reset, rebuild from the in-memory inspection.
            int statusId = _saveService.HasFile
                ? _saveService.GetCurrentStatusId()
                : GetCurrentInspectionStatusId();

            return statusId <= 1; // 0 = unset, 1 = pending/in-progress
        }

        private int GetCurrentInspectionStatusId()
        {
            try
            {
                var prop = _currentInspection?.GetType().GetProperty("StatusId");
                if (prop?.GetValue(_currentInspection) is int statusId)
                    return statusId;
            }
            catch { }

            return 0;
        }

        private static bool _stringIsNullOrWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value);

        private int CountFailedItems()
        {
            if (_currentInspection?.Sections == null) return 0;
            int count = 0;
            foreach (var section in _currentInspection.Sections)
                foreach (var item in section.Items)
                {
                    string v = item.Value?.ToString()?.ToLower() ?? "";
                    if (v.Contains("fail")) count++;
                }
            return count;
        }

        private int CountAnsweredItems()
        {
            if (_currentInspection?.Sections == null) return 0;
            int count = 0;
            foreach (var section in _currentInspection.Sections)
                foreach (var item in section.Items)
                    if (!string.IsNullOrEmpty(item.Value?.ToString()))
                        count++;
            return count;
        }

        private bool ShowResultPicker(bool closeAfterPicker)
        {
            string code = _currentInspectionCode ?? _currentInspection?.InspectionCode ?? "";
            int answered = CountAnsweredItems();
            int failed   = CountFailedItems();

            var config = _inspTypeService.GetConfig(code);
            bool expirationStageDone = false;

            if (config?.ExpirationStageTypes.Count > 0 && _currentFilePath != null)
            {
                string? insFolder    = Path.GetDirectoryName(_currentFilePath);
                string? inspRoot     = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
                string jobsFolder    = inspRoot != null ? Path.Combine(inspRoot, "Jobs") : "";
                string filename      = Path.GetFileNameWithoutExtension(_currentFilePath);
                string jobId         = filename.Split('-')[0];
                string jobInspFolder = Path.Combine(jobsFolder, jobId, "Inspections");

                if (Directory.Exists(jobInspFolder))
                {
                    foreach (var expType in config.ExpirationStageTypes)
                    {
                        if (Directory.GetFiles(jobInspFolder, $"{jobId}-{expType}-*.pdf",
                            SearchOption.TopDirectoryOnly).Length > 0)
                        {
                            expirationStageDone = true;
                            break;
                        }
                    }
                }
            }

            var picker = new ResultPickerWindow(code, answered, failed, config, expirationStageDone)
            {
                Owner = this
            };

            bool? result = picker.ShowDialog();

            if (result == true && picker.SelectedResult != null)
            {
                var choice = picker.SelectedResult;
                _saveService.SetResult(choice.StatusId, choice.NextActionId, choice.NextActionText);
                _hasUnsavedChanges = true;
                SaveCurrentInspectionInPlace();
            }

            // User chose "Keep Open" — cancel the close, stay in the app.
            if (picker.KeepOpen) return false;

            if (!closeAfterPicker) return true;

            // Either the user picked a result or skipped — proceed to close.
            // Must use BeginInvoke: calling Close() directly while inside a cancelled
            // Closing event is ignored by WPF. Deferring lets the cancelled event unwind first.
            _skipResultCheck = true;
            Dispatcher.BeginInvoke(new Action(Close));
            return true;
        }

        private void ShowResultPickerAndClose()
        {
            ShowResultPicker(closeAfterPicker: true);
        }
        
        /// <summary>
        /// Release file and type mutexes from a previously loaded inspection.
        /// Safe to call even if no locks are held.
        /// </summary>
        private void ReleaseInspectionLocks()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_openLockMetadataPath) && File.Exists(_openLockMetadataPath))
                    File.Delete(_openLockMetadataPath);
            }
            catch { }
            _openLockMetadataPath = null;

            try { _fileMutex?.ReleaseMutex(); } catch { }
            try { _fileMutex?.Dispose(); } catch { }
            _fileMutex = null;
            
            try { _typeMutex?.ReleaseMutex(); } catch { }
            try { _typeMutex?.Dispose(); } catch { }
            _typeMutex = null;
        }

        public static MainWindow OpenAsCompanion(string filePath)
        {
            var w = new MainWindow();
            w._openedAsCompanion = true;
            w.Show();
            w.LoadFileFromArgs(filePath);
            return w;
        }

        internal bool HasOpenInspection => _currentInspection != null;

        internal void BringInspectionToFront()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        /// <summary>
        /// Called from App.xaml.cs when RED is launched with a file path argument (new window mode).
        /// </summary>
        public void LoadFileFromArgs(string filePath)
        {
            ShowOpeningInspectionState(filePath);
            // Defer to after window is fully rendered
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LoadInspectionFileAsync(filePath);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ShowOpeningInspectionState(string filePath)
        {
            Cursor = Cursors.Wait;
            Title = $"Opening {Path.GetFileName(filePath)}...";
            WelcomePanel.Visibility = Visibility.Visible;
            WelcomeHint.Text = $"Opening {Path.GetFileName(filePath)}...";
            WelcomeHint.Foreground = new SolidColorBrush(Color.FromRgb(139, 0, 0));
            WelcomeHint.Visibility = Visibility.Visible;
            FileNameText.Text = $"Opening {Path.GetFileName(filePath)}...";
        }
        
        // ========== KEYBOARD NAVIGATION ==========
        // Up/Down: Navigate checklist items
        // Left/Right: Navigate photos
        // Escape: Close overlays/popups
        // Enter: Apply current item (when in editor)
        private void Window_Activated(object sender, EventArgs e)
        {
            // On tablet, tapping RED while a PDF viewer is on top may not raise z-order.
            // Briefly set Topmost to force this window to the front, then remove it.
            Topmost = true;
            Topmost = false;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Skip if typing in a TextBox
            if (e.OriginalSource is TextBox)
            {
                // But still handle Escape to unfocus the textbox
                if (e.Key == Key.Escape)
                {
                    Keyboard.ClearFocus();
                    SectionsTreeView.Focus();
                    e.Handled = true;
                }
                return;
            }
            
            switch (e.Key)
            {
                case Key.Up:
                    NavigateChecklist(-1);
                    e.Handled = true;
                    break;
                    
                case Key.Down:
                    NavigateChecklist(1);
                    e.Handled = true;
                    break;
                    
                case Key.Left:
                    // Navigate to previous photo
                    if (_currentItem != null && PrevPhotoButton.IsEnabled)
                    {
                        PrevPhotoButton_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Right:
                    // Navigate to next photo
                    if (_currentItem != null && NextPhotoButton.IsEnabled)
                    {
                        NextPhotoButton_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Escape:
                    // Close Quick Suggestions overlay if visible
                    if (QuickSuggestionsOverlay.Visibility == Visibility.Visible)
                    {
                        QuickSuggestionsOverlay.Visibility = Visibility.Collapsed;
                        e.Handled = true;
                    }
                    // Otherwise just clear focus and return to tree
                    else
                    {
                        Keyboard.ClearFocus();
                        SectionsTreeView.Focus();
                        e.Handled = true;
                    }
                    break;
                    
                case Key.Enter:
                    // Apply current item if editor is loaded and not in a textbox
                    if (_currentItem != null && EditorPanel.Visibility == Visibility.Visible)
                    {
                        ApplyToItemButton_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
                    
                // Number keys 1/2/3 for Quick Suggestions when overlay is visible
                case Key.D1:
                case Key.NumPad1:
                    if (QuickSuggestionsOverlay.Visibility == Visibility.Visible)
                    {
                        QuickSuggestion_Click(QuickSuggestionA, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
                    
                case Key.D2:
                case Key.NumPad2:
                    if (QuickSuggestionsOverlay.Visibility == Visibility.Visible)
                    {
                        QuickSuggestion_Click(QuickSuggestionB, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
                    
                case Key.D3:
                case Key.NumPad3:
                    if (QuickSuggestionsOverlay.Visibility == Visibility.Visible && QuickSuggestionC.Visibility == Visibility.Visible)
                    {
                        QuickSuggestion_Click(QuickSuggestionC, new RoutedEventArgs());
                        e.Handled = true;
                    }
                    break;
                    
                case Key.F1:
                    HelpButton_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                    break;
                    
                case Key.D:
                    // Ctrl+Shift+D = Stats diagnostics
                    if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                    {
                        ShowStatsDiagnostics();
                        e.Handled = true;
                    }
                    break;
            }
        }
        
        private void ShowStatsDiagnostics()
        {
            string diag = _statsService.GetDiagnostics(_currentInspectorName, _currentInspectionCode);
            diag = $"Inspector: '{_currentInspectorName}'\nCode: '{_currentInspectionCode}'\n\n{diag}";
            MessageBox.Show(diag, "Stats Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private void NavigateChecklist(int direction)
        {
            if (_currentInspection == null) return;
            
            // Get all visible items from the tree
            var allItems = new List<TreeViewItem>();
            CollectVisibleTreeItems(SectionsTreeView, allItems);
            
            if (allItems.Count == 0) return;
            
            // Find current selected item
            int currentIndex = -1;
            for (int i = 0; i < allItems.Count; i++)
            {
                if (allItems[i].IsSelected)
                {
                    currentIndex = i;
                    break;
                }
            }
            
            // Calculate new index
            int newIndex;
            if (currentIndex == -1)
            {
                // Nothing selected, select first or last based on direction
                newIndex = direction > 0 ? 0 : allItems.Count - 1;
            }
            else
            {
                newIndex = currentIndex + direction;
                // Clamp to valid range
                if (newIndex < 0) newIndex = 0;
                if (newIndex >= allItems.Count) newIndex = allItems.Count - 1;
            }
            
            // Select the new item
            allItems[newIndex].IsSelected = true;
            allItems[newIndex].BringIntoView();
        }
        
        private void CollectVisibleTreeItems(ItemsControl parent, List<TreeViewItem> items)
        {
            foreach (var item in parent.Items)
            {
                var container = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (container != null)
                {
                    // Only add leaf nodes (actual items, not sections)
                    if (container.Items.Count == 0)
                    {
                        items.Add(container);
                    }
                    else if (container.IsExpanded)
                    {
                        // Recurse into expanded sections
                        CollectVisibleTreeItems(container, items);
                    }
                }
            }
        }
        // ========== END KEYBOARD NAVIGATION ==========

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeGrokClient();
            ApplyAiTonePreference();
            
            // Store user data in AppData (survives app updates)
            string userDataDir = AppIdentity.LocalAppDataPath;
            
            // Migrate old userdata from app folder if it exists
            MigrateOldUserData(userDataDir);
            
            _userDataService = new UserDataService(userDataDir);
            
            InitializePrefixSuffixButtons();
            
            // Set ALL button as default active filter
            ClearSearchButton.Background = new SolidColorBrush(Colors.White);
            ClearSearchButton.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
            ClearSearchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
            ClearSearchButton.BorderThickness = new Thickness(3);
            
            // Animate "Click Open INS" hint sliding in from the left
            StartWelcomeHintAnimation();
        }
        
        private void StartWelcomeHintAnimation()
        {
            var slideIn = new DoubleAnimation
            {
                From = -300,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(600),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = TimeSpan.FromMilliseconds(300) // Slight delay after load
            };
            HintTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
        }
        
        // Easter egg: triple-click on logo to toggle tagline + check for updates
        private void WelcomeLogo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var now = DateTime.Now;
            if ((now - _lastLogoClick).TotalMilliseconds > 500)
                _logoClickCount = 0;

            _logoClickCount++;
            _lastLogoClick = now;

            if (_logoClickCount >= 3)
            {
                _logoClickCount = 0;
                // Toggle tagline, and always run force update
                if (_taglineVisible) HideTagline(); else ShowTagline();
                CheckForUpdatesAsync();
            }
        }
        
        private void ShowTagline()
        {
            _taglineVisible = true;
            WelcomeTagline.Visibility = Visibility.Visible;
            
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            var slideDown = new DoubleAnimation
            {
                From = -20,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            
            WelcomeTagline.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            TaglineTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        }
        
        private void HideTagline()
        {
            _taglineVisible = false;
            
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            
            var slideUp = new DoubleAnimation
            {
                From = 0,
                To = -20,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            
            fadeOut.Completed += (s, e) => WelcomeTagline.Visibility = Visibility.Collapsed;
            
            WelcomeTagline.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            TaglineTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
        
        /// <summary>
        /// Update check - works via direct HTTP to public GitHub repo (no gh CLI needed).
        /// Triggered by triple-clicking the welcome logo, or automatically on startup.
        /// </summary>
        private async void CheckForUpdatesAsync(bool silent = false)
        {
            const string API_URL = "https://api.github.com/repos/fullinspect-source/Red/releases/latest";

            if (AppIdentity.IsDevBuild && !silent)
            {
                UpdatePanel.Visibility = Visibility.Visible;
                UpdateResultsGrid.Visibility = Visibility.Collapsed;
                UpdateStatusText.Visibility = Visibility.Visible;
                UpdateStatusText.Text = "RED 2.0 Dev skips app self-updates; refreshing datasets only...";
                UpdateProgressBar.IsIndeterminate = true;

                try
                {
                    var devStatsResult = await InspectionEditor.Services.DataUpdateService.ForceUpdateStatsAsync();
                    _inspTypeService.InvalidateCache();
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateProgressBar.Value = 100;
                    UpdateStatusText.Text = devStatsResult.Error == null
                        ? "RED 2.0 Dev datasets refreshed."
                        : $"Dataset refresh issue: {devStatsResult.Error}";
                }
                catch (Exception ex)
                {
                    UpdateProgressBar.IsIndeterminate = false;
                    UpdateStatusText.Text = $"Dataset refresh issue: {ex.Message}";
                }

                return;
            }

            if (AppIdentity.IsDevBuild && silent)
            {
                try
                {
                    await InspectionEditor.Services.DataUpdateService.ForceUpdateStatsAsync();
                    _inspTypeService.InvalidateCache();
                }
                catch { }
                return;
            }

            // Show panel and spinner
            UpdatePanel.Visibility = Visibility.Visible;
            UpdateResultsGrid.Visibility = Visibility.Collapsed;
            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "Checking for updates…";
            UpdateProgressBar.IsIndeterminate = true;
            UpdateProgressBar.Value = 0;

            // ── Run RED check and stats update in parallel ──────────────
            string remoteRedVersion = "";
            string? redDownloadUrl = null;
            string? redCheckError = null;
            InspectionEditor.Services.StatsUpdateResult? statsResult = null;
            InspectionEditor.Services.StatsUpdateResult? teamStatsResult = null;

            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Red-InspectionEditor");
                http.Timeout = TimeSpan.FromSeconds(15);

                var redTask   = http.GetStringAsync(API_URL);
                var statsTask = InspectionEditor.Services.DataUpdateService.ForceUpdateStatsAsync();

                await Task.WhenAll(redTask, statsTask);

                _inspTypeService.InvalidateCache();
                statsResult = statsTask.Result;
                // Team averages are in the same inspector_stats.json — no separate download needed
                teamStatsResult = new InspectionEditor.Services.StatsUpdateResult
                {
                    CurrentDate = statsResult.CurrentDate,
                    LatestDate  = statsResult.LatestDate,
                    Updated     = statsResult.Updated,
                    Error       = statsResult.Error
                };

                var apiJson = redTask.Result;
                var tagMatch = System.Text.RegularExpressions.Regex.Match(apiJson, "\"tag_name\":\\s*\"v?([^\"]+)\"");
                if (tagMatch.Success) remoteRedVersion = tagMatch.Groups[1].Value;

                var urlMatch = System.Text.RegularExpressions.Regex.Match(apiJson, "\"browser_download_url\":\\s*\"([^\"]+\\.zip)\"");
                if (urlMatch.Success) redDownloadUrl = urlMatch.Groups[1].Value;
            }
            catch (Exception ex)
            {
                redCheckError = ex.Message;
            }

            // ── If RED needs updating, download and install ──────────────
            bool redUpdated = false;
            string? redInstallError = null;

            if (redCheckError == null && !string.IsNullOrEmpty(remoteRedVersion) && remoteRedVersion != AppVersion)
            {
                if (!string.IsNullOrEmpty(redDownloadUrl))
                {
                    try
                    {
                        UpdateStatusText.Text = $"Downloading RED v{remoteRedVersion}…";
                        using var http2 = new System.Net.Http.HttpClient();
                        http2.DefaultRequestHeaders.Add("User-Agent", "Red-InspectionEditor");
                        http2.Timeout = TimeSpan.FromMinutes(5); // 96 MB needs time on slow connections

                        string tempDir = Path.Combine(Path.GetTempPath(), "RedUpdate");
                        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                        Directory.CreateDirectory(tempDir);

                        // Stream to disk with progress — avoids 96 MB memory spike and timeout on slow connections
                        string zipPath = Path.Combine(tempDir, $"Red-v{remoteRedVersion}.zip");
                        using (var resp2 = await http2.GetAsync(redDownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                        {
                            resp2.EnsureSuccessStatusCode();
                            long? totalBytes = resp2.Content.Headers.ContentLength;
                            using var netStream = await resp2.Content.ReadAsStreamAsync();
                            using var fileStream = File.Create(zipPath);
                            var buf = new byte[81920];
                            long got = 0; int read2;
                            while ((read2 = await netStream.ReadAsync(buf)) > 0)
                            {
                                await fileStream.WriteAsync(buf.AsMemory(0, read2));
                                got += read2;
                                string progress = totalBytes.HasValue
                                    ? $"{got / 1048576} / {totalBytes.Value / 1048576} MB"
                                    : $"{got / 1048576} MB";
                                UpdateStatusText.Text = $"Downloading RED v{remoteRedVersion}… {progress}";
                            }
                        }

                        string extractDir = Path.Combine(tempDir, "extracted");
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                        // Red.exe can't be overwritten while running — hand off to a batch script
                        // that waits for this process to exit, copies new files, then restarts.
                        string appDir = AppDomain.CurrentDomain.BaseDirectory;
                        string appExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                            ?? Path.Combine(appDir, "Red.exe");
                        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                        string destDir = appDir.TrimEnd('\\', '/');

                        // Build an elevated batch: wait for RED to exit naturally (no taskkill),
                        // then xcopy new files (elevated so C:\Red\ is writable), then relaunch.
                        var bat = new System.Text.StringBuilder();
                        bat.AppendLine("@echo off");
                        // Self-elevate via VBScript if not already admin
                        bat.AppendLine("net session >nul 2>&1");
                        bat.AppendLine("if %errorLevel% == 0 goto :admin");
                        bat.AppendLine("echo Set o = CreateObject(\"Shell.Application\") > \"%TEMP%\\reu.vbs\"");
                        bat.AppendLine("echo o.ShellExecute \"cmd.exe\", \"/c \"\"%~f0\"\"\", \"\", \"runas\", 1 >> \"%TEMP%\\reu.vbs\"");
                        bat.AppendLine("cscript //nologo \"%TEMP%\\reu.vbs\"");
                        bat.AppendLine("del \"%TEMP%\\reu.vbs\" >nul 2>&1");
                        bat.AppendLine("exit /b");
                        bat.AppendLine(":admin");
                        bat.AppendLine($"title RED Update  v{AppVersion} -> v{remoteRedVersion}");
                        bat.AppendLine("cls");
                        bat.AppendLine("echo.");
                        bat.AppendLine($"echo   RED Update  ^|  v{AppVersion} -^> v{remoteRedVersion}");
                        bat.AppendLine("echo.");
                        bat.AppendLine("echo   Waiting for RED to close...");
                        // Poll until the process exits naturally (no forced kill)
                        bat.AppendLine(":wait");
                        bat.AppendLine($"  tasklist /FI \"PID eq {pid}\" 2>nul | find /I \"Red.exe\" >nul 2>&1 && (ping -n 2 127.0.0.1 >nul & goto wait)");
                        bat.AppendLine("ping -n 3 127.0.0.1 >nul");
                        bat.AppendLine("echo   Installing RED v" + remoteRedVersion + "...");
                        bat.AppendLine($"xcopy /E /Y /Q \"{extractDir}\\*\" \"{destDir}\\\"");
                        bat.AppendLine("if %errorlevel% neq 0 (");
                        bat.AppendLine("  echo.");
                        bat.AppendLine("  echo   ERROR: Files could not be copied.");
                        bat.AppendLine("  echo   Press any key to close this window.");
                        bat.AppendLine("  pause >nul");
                        bat.AppendLine("  exit /b 1");
                        bat.AppendLine(")");
                        bat.AppendLine("echo   Update complete!");
                        bat.AppendLine("echo.");
                        bat.AppendLine($"echo   Launching RED v{remoteRedVersion}...");
                        bat.AppendLine($"start \"\" \"{appExe}\"");
                        bat.AppendLine("ping -n 4 127.0.0.1 >nul");
                        bat.AppendLine("del \"%~f0\"");
                        bat.AppendLine("exit");

                        string batPath = Path.Combine(Path.GetTempPath(), "red_update.bat");
                        File.WriteAllText(batPath, bat.ToString());
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c \"{batPath}\"",
                            UseShellExecute = true
                        });
                        // Do NOT delete tempDir — batch script needs the extracted files
                        redUpdated = true;
                    }
                    catch (Exception ex)
                    {
                        redInstallError = ex.Message;
                    }
                }
            }

            // ── Populate results table ───────────────────────────────────
            UpdateProgressBar.IsIndeterminate = false;
            UpdateProgressBar.Value = 100;
            UpdateStatusText.Visibility = Visibility.Collapsed;
            UpdateResultsGrid.Visibility = Visibility.Visible;

            // RED row
            RedHadText.Text = $"v{AppVersion}";
            if (redCheckError != null)
            {
                RedNowText.Text = "—";
                RedStatusText.Text = $"⚠ {redCheckError}";
                RedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (string.IsNullOrEmpty(remoteRedVersion))
            {
                RedNowText.Text = "—";
                RedStatusText.Text = "⚠ Version unknown";
                RedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (redUpdated)
            {
                RedNowText.Text = $"v{remoteRedVersion}";
                RedStatusText.Text = "↑ Restarting…";
                RedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192));
            }
            else if (redInstallError != null)
            {
                RedNowText.Text = $"v{remoteRedVersion}";
                RedStatusText.Text = $"⚠ {redInstallError}";
                RedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else
            {
                RedNowText.Text = $"v{remoteRedVersion}";
                RedStatusText.Text = "✓ Up to date";
                RedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
            }

            // Stats row
            if (statsResult == null)
            {
                StatsHadText.Text = InspectionEditor.Services.DataUpdateService.GetLocalStatsDate();
                StatsNowText.Text = "—";
                StatsStatusText.Text = "⚠ Check failed";
                StatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (statsResult.Error != null)
            {
                StatsHadText.Text = statsResult.CurrentDate;
                StatsNowText.Text = "—";
                StatsStatusText.Text = $"⚠ {statsResult.Error}";
                StatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (statsResult.Updated)
            {
                StatsHadText.Text = statsResult.CurrentDate;
                StatsNowText.Text = statsResult.LatestDate;
                StatsStatusText.Text = "↑ Updated";
                StatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192));
            }
            else
            {
                StatsHadText.Text = statsResult.CurrentDate;
                StatsNowText.Text = statsResult.LatestDate;
                StatsStatusText.Text = "✓ Up to date";
                StatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
            }

            // Team Stats row
            if (teamStatsResult == null)
            {
                TeamStatsHadText.Text = InspectionEditor.Services.DataUpdateService.GetLocalTeamStatsDate();
                TeamStatsNowText.Text = "—";
                TeamStatsStatusText.Text = "⚠ Check failed";
                TeamStatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (teamStatsResult.Error != null)
            {
                TeamStatsHadText.Text = teamStatsResult.CurrentDate;
                TeamStatsNowText.Text = "—";
                bool notConfigured = teamStatsResult.Error.Contains("not configured");
                TeamStatsStatusText.Text = notConfigured ? "— not configured" : $"⚠ {teamStatsResult.Error}";
                TeamStatsStatusText.Foreground = notConfigured ? new SolidColorBrush(Color.FromRgb(150, 150, 150))
                                                               : new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (teamStatsResult.Updated)
            {
                TeamStatsHadText.Text = teamStatsResult.CurrentDate;
                TeamStatsNowText.Text = teamStatsResult.LatestDate;
                TeamStatsStatusText.Text = "↑ Updated";
                TeamStatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192));
            }
            else
            {
                TeamStatsHadText.Text = teamStatsResult.CurrentDate;
                TeamStatsNowText.Text = teamStatsResult.LatestDate;
                TeamStatsStatusText.Text = "✓ Up to date";
                TeamStatsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
            }

            if (redUpdated)
            {
                // Count down and restart — batch script is already queued
                for (int i = 8; i > 0; i--)
                {
                    RedStatusText.Text = $"↑ Restarting in {i}s…";
                    await Task.Delay(1000);
                }
                Application.Current.Shutdown();
            }
            else
            {
                // Keep results visible for 2 minutes so user can read them
                await Task.Delay(120000);
                UpdatePanel.Visibility = Visibility.Collapsed;
                UpdateResultsGrid.Visibility = Visibility.Collapsed;
                UpdateStatusText.Visibility = Visibility.Visible;
            }
        }
        
        private void MigrateOldUserData(string newUserDataDir)
        {
            try
            {
                string appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                if (!Directory.Exists(newUserDataDir))
                    Directory.CreateDirectory(newUserDataDir);

                string newUserDataSubDir = Path.Combine(newUserDataDir, "userdata");
                if (!Directory.Exists(newUserDataSubDir))
                    Directory.CreateDirectory(newUserDataSubDir);

                MergeUserDataFolder(Path.Combine(appDir, "userdata"), newUserDataSubDir);
                MergeUserDataFolder(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        AppIdentity.LegacyAppDataFolderName,
                        "userdata"),
                    newUserDataSubDir);
                
                System.Diagnostics.Debug.WriteLine("Migrated old userdata to AppData");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Migration error: {ex.Message}");
                // Silently fail - not critical
            }
        }

        private void MergeUserDataFolder(string sourceUserDataDir, string destUserDataDir)
        {
            if (!Directory.Exists(sourceUserDataDir))
                return;

            foreach (var userFolder in Directory.GetDirectories(sourceUserDataDir))
            {
                string userName = Path.GetFileName(userFolder);
                string destFolder = Path.Combine(destUserDataDir, userName);
                Directory.CreateDirectory(destFolder);

                foreach (var file in Directory.GetFiles(userFolder))
                {
                    string destFile = Path.Combine(destFolder, Path.GetFileName(file));
                    if (Path.GetFileName(file).StartsWith("comments_", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(destFile))
                    {
                        MergeSavedCommentFiles(file, destFile);
                    }
                    else if ((Path.GetFileName(file).Equals("custom_prefixes.json", StringComparison.OrdinalIgnoreCase) ||
                              Path.GetFileName(file).Equals("custom_suffixes.json", StringComparison.OrdinalIgnoreCase)) &&
                             File.Exists(destFile))
                    {
                        MergeStringListFile(file, destFile);
                    }
                    else if (!File.Exists(destFile))
                    {
                        File.Copy(file, destFile, false);
                    }
                }
            }
        }

        private void MergeStringListFile(string sourceFile, string destFile)
        {
            try
            {
                var source = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(sourceFile))
                    ?? new List<string>();
                var dest = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(destFile))
                    ?? new List<string>();

                bool changed = false;
                foreach (string value in source.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    if (!dest.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        dest.Add(value);
                        changed = true;
                    }
                }

                if (changed)
                {
                    dest = dest
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    File.WriteAllText(destFile, JsonConvert.SerializeObject(dest, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Merge string list error: {ex.Message}");
            }
        }

        private void MergeSavedCommentFiles(string sourceFile, string destFile)
        {
            try
            {
                var source = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(sourceFile))
                    ?? new Dictionary<string, List<string>>();
                var dest = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(File.ReadAllText(destFile))
                    ?? new Dictionary<string, List<string>>();

                bool changed = false;
                foreach (var pair in source)
                {
                    if (!dest.TryGetValue(pair.Key, out var comments))
                    {
                        dest[pair.Key] = pair.Value
                            .Where(comment => !string.IsNullOrWhiteSpace(comment))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        changed = dest[pair.Key].Count > 0 || changed;
                        continue;
                    }

                    foreach (string comment in pair.Value.Where(comment => !string.IsNullOrWhiteSpace(comment)))
                    {
                        if (!comments.Contains(comment, StringComparer.OrdinalIgnoreCase))
                        {
                            comments.Add(comment);
                            changed = true;
                        }
                    }
                }

                if (changed)
                    File.WriteAllText(destFile, JsonConvert.SerializeObject(dest, Formatting.Indented));
            }
            catch
            {
                // A damaged comments file should not block app startup.
            }
        }

        private void InitializeGrokClient()
        {
            string? apiKey = SettingsWindow.LoadApiKeyFromFile();
            
            if (string.IsNullOrEmpty(apiKey))
            {
                var settingsWindow = new SettingsWindow { Owner = this };
                if (settingsWindow.ShowDialog() == true)
                {
                    apiKey = settingsWindow.ApiKey;
                }
            }

            if (!string.IsNullOrEmpty(apiKey))
            {
                _grokClient = new GrokApiClient(apiKey);
                _grokClient.OnModelFallback += ShowModelFallbackAlert;
            }
        }
        
        private void ShowModelFallbackAlert()
        {
            // Run on UI thread
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    "The AI call is taking too long. I'm switching to a faster AI for the rest of this session.\n\n" +
                    "Please close and reopen RED if you want to try the smarter AI again. " +
                    "It may work faster, but if not I'll switch to the faster one so you won't have to wait.",
                    "AI Model Switched",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }

        private void UpdateHeaderInfo()
        {
            if (_currentInspection == null)
            {
                AddressText.Text = "";
                BuilderText.Text = "";
                InspectorText.Text = "";
                AddressDivider.Visibility = Visibility.Collapsed;
                HeaderDivider.Visibility = Visibility.Collapsed;
                return;
            }

            // Get address
            string address = _currentInspection.Address ?? "";
            string city = _currentInspection.City ?? "";
            string fullAddress = address;
            if (!string.IsNullOrWhiteSpace(city))
            {
                fullAddress = string.IsNullOrWhiteSpace(address) ? city : $"{address}, {city}";
            }
            AddressText.Text = address;  // Just street, no city

            // Get builder client name (BuilderName field) for display and stats
            string builderClient = "";
            string contact = _currentInspection.Contact ?? "";
            string project = "";
            
            if (_currentInspection.ExtensionData != null)
            {
                // BuilderName is the client company (e.g., "DR Horton")
                if (_currentInspection.ExtensionData.TryGetValue("BuilderName", out var bnObj) && bnObj != null)
                    builderClient = bnObj.ToString()?.Trim() ?? "";
                
                // Project is the neighborhood/subdivision
                if (_currentInspection.ExtensionData.TryGetValue("Project", out var prjObj) && prjObj != null)
                    project = prjObj.ToString()?.Trim() ?? "";
            }
            
            // Fall back to Contact if BuilderName is empty
            string displayBuilder = !string.IsNullOrWhiteSpace(builderClient) ? builderClient : contact;
            
            // Store for stats lookups
            _currentBuilderName = builderClient;
            _currentProjectName = project;
            
            BuilderText.Text = !string.IsNullOrWhiteSpace(displayBuilder) ? displayBuilder : "unknown builder";

            // Get inspector name - prefer the top-level InspectorName field (most reliable)
            // Fall back to Events if InspectorName isn't available
            string inspector = "";
            
            // Primary: InspectorName from file's top-level fields (stored in ExtensionData)
            if (_currentInspection.ExtensionData != null)
            {
                if (_currentInspection.ExtensionData.TryGetValue("InspectorName", out var inspNameObj) && inspNameObj != null)
                {
                    inspector = inspNameObj.ToString()?.Trim() ?? "";
                }
            }
            
            // Fallback: Events (skip system/service accounts like "SAE.Inspect.MQ")
            if (string.IsNullOrWhiteSpace(inspector) && _currentInspection.Events != null && _currentInspection.Events.Count > 0)
            {
                var latestEvent = _currentInspection.Events
                    .Where(e => !string.IsNullOrWhiteSpace(e.UserDisplayName) &&
                                !e.UserDisplayName.Contains("SAE.") &&
                                !e.UserDisplayName.Contains(".Inspect."))
                    .LastOrDefault();
                
                if (latestEvent != null)
                {
                    inspector = latestEvent.UserDisplayName ?? "";
                }
            }

            // Store inspector name for stats lookup
            _currentInspectorName = inspector;
            
            if (!string.IsNullOrWhiteSpace(inspector))
            {
                InspectorText.Text = inspector;
            }
            else
            {
                InspectorText.Text = "";
            }

            // Update header tooltips for stats mode switching
            FileNameText.ToolTip = "📊 Global team stats (click to switch)";
            AddressText.ToolTip = !string.IsNullOrWhiteSpace(project)
                ? $"📊 Neighborhood stats: {project} (click to switch)"
                : "No neighborhood data available";
            BuilderText.ToolTip = !string.IsNullOrWhiteSpace(builderClient)
                ? $"📊 Builder stats: {builderClient} (click to switch)"
                : "No builder data available";

            // Show dividers between present fields
            // Builder slot always has content ("unknown builder" fallback), so dividers depend on neighbours
            AddressDivider.Visibility = !string.IsNullOrWhiteSpace(address)
                ? Visibility.Visible : Visibility.Collapsed;
            HeaderDivider.Visibility = !string.IsNullOrWhiteSpace(inspector)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Stats mode toggle (header radio buttons) ──

        private void FileNameText_Click(object sender, MouseButtonEventArgs e)
        {
            SetStatsMode(StatsMode.Global);
        }

        private void AddressText_Click(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentProjectName)) return;
            SetStatsMode(StatsMode.Project);
        }

        private void BuilderText_Click(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_currentBuilderName)) return;
            SetStatsMode(StatsMode.Builder);
        }

        private void SetStatsMode(StatsMode mode)
        {
            _statsMode = mode;
            UpdateStatsModeVisuals();
            
            // Check if data exists for the selected mode; if falling back to global, underline FileNameText
            bool fellBackToGlobal = false;
            if (mode == StatsMode.Builder && !string.IsNullOrWhiteSpace(_currentBuilderName))
            {
                bool hasBuilderData = _currentInspection?.Sections?.Any(s =>
                    _statsService.GetSectionAverageByBuilder(_currentInspectionCode, s.Number, _currentBuilderName) != null) == true;
                if (!hasBuilderData)
                {
                    fellBackToGlobal = true;
                    BuilderText.ToolTip = $"No stats for \"{_currentBuilderName}\" — showing global averages";
                    BuilderText.TextDecorations = null;
                    BuilderText.Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 170)); // Dimmer to show fallback
                }
            }
            else if (mode == StatsMode.Project && !string.IsNullOrWhiteSpace(_currentProjectName))
            {
                bool hasProjectData = _currentInspection?.Sections?.Any(s =>
                    _statsService.GetSectionAverageByProject(_currentInspectionCode, s.Number, _currentProjectName) != null) == true;
                if (!hasProjectData)
                {
                    fellBackToGlobal = true;
                    AddressText.ToolTip = $"No stats for \"{_currentProjectName}\" — showing global averages";
                    AddressText.TextDecorations = null;
                    AddressText.Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 170)); // Dimmer to show fallback
                }
            }
            
            // When falling back to global, restore white underline on the global header
            if (fellBackToGlobal)
            {
                FileNameText.Foreground = new SolidColorBrush(Colors.White);
                FileNameText.TextDecorations = MakeUnderline(Colors.White);
            }
            
            PopulateTreeView(SearchFilterBox.Text);
        }

        private static TextDecorationCollection MakeUnderline(Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), 1.5) { DashStyle = DashStyles.Solid };
            pen.Freeze();
            var td = new TextDecoration { Location = TextDecorationLocation.Underline, Pen = pen, PenThicknessUnit = TextDecorationUnit.Pixel };
            var col = new TextDecorationCollection { td };
            col.Freeze();
            return col;
        }

        private void UpdateStatsModeVisuals()
        {
            var activeColor   = new SolidColorBrush(Colors.White);
            var inactiveColor = new SolidColorBrush(Color.FromRgb(255, 204, 204)); // #FFCCCC

            var whiteUnderline = MakeUnderline(Colors.White);
            var blackUnderline = MakeUnderline(Color.FromRgb(30, 30, 30));

            bool hasProject = !string.IsNullOrWhiteSpace(_currentProjectName);
            bool hasBuilder = !string.IsNullOrWhiteSpace(_currentBuilderName);

            // Global — always available
            FileNameText.Foreground = _statsMode == StatsMode.Global ? activeColor : inactiveColor;
            FileNameText.TextDecorations = _statsMode == StatsMode.Global ? whiteUnderline : blackUnderline;

            // Project — offered when project name exists
            AddressText.Foreground = _statsMode == StatsMode.Project ? activeColor : inactiveColor;
            AddressText.TextDecorations = _statsMode == StatsMode.Project ? whiteUnderline
                                        : hasProject ? blackUnderline : null;

            // Builder — offered when builder name is known
            BuilderText.Foreground = _statsMode == StatsMode.Builder ? activeColor : inactiveColor;
            BuilderText.TextDecorations = _statsMode == StatsMode.Builder ? whiteUnderline
                                        : hasBuilder ? blackUnderline : null;
        }

        /// <summary>
        /// Get section average based on current stats mode (Global/Builder/Project).
        /// Falls back to global if builder/project data unavailable.
        /// </summary>
        private double? GetCurrentSectionAverage(string? sectionNumber)
        {
            double? result = null;
            switch (_statsMode)
            {
                case StatsMode.Builder:
                    result = _statsService.GetSectionAverageByBuilder(_currentInspectionCode, sectionNumber, _currentBuilderName);
                    break;
                case StatsMode.Project:
                    result = _statsService.GetSectionAverageByProject(_currentInspectionCode, sectionNumber, _currentProjectName);
                    break;
            }
            // Fall back to global if mode-specific data unavailable
            return result ?? _statsService.GetSectionAverage(_currentInspectionCode, sectionNumber);
        }

        /// <summary>
        /// Get total inspection average based on current stats mode.
        /// Falls back to global if builder/project data unavailable.
        /// </summary>
        private double? GetCurrentInspectionAverage()
        {
            double? result = null;
            switch (_statsMode)
            {
                case StatsMode.Builder:
                    result = _statsService.GetInspectionAverageByBuilder(_currentInspectionCode, _currentBuilderName);
                    break;
                case StatsMode.Project:
                    result = _statsService.GetInspectionAverageByProject(_currentInspectionCode, _currentProjectName);
                    break;
            }
            return result ?? _statsService.GetInspectionAverage(_currentInspectionCode);
        }

        // ── End stats mode toggle ──

        private void InitializePrefixSuffixButtons()
        {
            PrefixPanel.Children.Clear();
            _prefixButtons.Clear();
            
            int prefixTabIndex = 30;
            foreach (var prefix in UserDataService.DefaultPrefixes)
            {
                var btn = new ToggleButton
                {
                    Content = prefix,
                    Tag = prefix,
                    Style = (Style)FindResource("PrefixButton"),
                    TabIndex = prefixTabIndex++
                };
                btn.Checked += PrefixButton_Checked;
                btn.Unchecked += PrefixButton_Unchecked;
                // Right-click to remove custom prefixes
                btn.MouseRightButtonUp += PrefixButton_RightClick;
                PrefixPanel.Children.Add(btn);
                _prefixButtons.Add(btn);
            }

            SuffixPanel.Children.Clear();
            _suffixButtons.Clear();
            
            int suffixTabIndex = 61;
            foreach (var suffix in UserDataService.DefaultSuffixes)
            {
                var btn = new ToggleButton
                {
                    Content = suffix,
                    Tag = suffix,
                    Style = (Style)FindResource("SuffixButton"),
                    TabIndex = suffixTabIndex++
                };
                btn.Checked += SuffixButton_Changed;
                btn.Unchecked += SuffixButton_Changed;
                // Right-click to remove custom suffixes
                btn.MouseRightButtonUp += SuffixButton_RightClick;
                SuffixPanel.Children.Add(btn);
                _suffixButtons.Add(btn);
            }
        }

        private void PrefixButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingEditor) return; // Don't fire during item load
            var btn = sender as ToggleButton;
            if (btn == null) return;

            foreach (var other in _prefixButtons)
            {
                if (other != btn) other.IsChecked = false;
            }

            _selectedPrefix = btn.Tag?.ToString();
            UpdatePreviewText();
            MarkUnsaved();
            
            // Return focus to comments box so on-screen keyboard stays open
            CommentsTextBox.Focus();
            CommentsTextBox.CaretIndex = CommentsTextBox.Text?.Length ?? 0;
        }

        private void PrefixButton_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingEditor) return; // Don't fire during item load
            if (_prefixButtons.All(b => b.IsChecked != true))
                _selectedPrefix = null;
            UpdatePreviewText();
            
            CommentsTextBox.Focus();
            CommentsTextBox.CaretIndex = CommentsTextBox.Text?.Length ?? 0;
        }

        private void SuffixButton_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingEditor) return; // Don't fire during item load
            
            // Preserve any existing timestamp suffix while rebuilding from buttons
            var existingTimestamp = _selectedSuffixes.FirstOrDefault(s => IsTimestampSuffix(s));
            
            _selectedSuffixes.Clear();
            foreach (var btn in _suffixButtons)
            {
                if (btn.IsChecked == true)
                    _selectedSuffixes.Add(btn.Tag?.ToString() ?? "");
            }
            
            // Re-add the timestamp if it was present
            if (existingTimestamp != null)
                _selectedSuffixes.Add(existingTimestamp);
            
            UpdatePreviewText();
            MarkUnsaved();
        }
        
        /// <summary>
        /// Returns true if the suffix looks like a timestamp: (M/D/YYYY H AM/PM)
        /// </summary>
        private static bool IsTimestampSuffix(string s)
        {
            return !string.IsNullOrEmpty(s) && s.StartsWith("(") && s.EndsWith(")") &&
                   s.Contains("/") && (s.Contains("AM)") || s.Contains("PM)"));
        }

        private void MarkUnsaved()
        {
            _hasUnsavedChanges = true;
            _hasBeenEditedThisSession = true;
            if (!Title.EndsWith("*"))
                Title = Title + " *";
        }

        private void MarkSaved()
        {
            _hasUnsavedChanges = false;
            Title = Title.TrimEnd('*', ' ');
        }

        private void WriteOpenInspectionLockMetadata(string filePath, string inspectionType)
        {
            try
            {
                string processId = Process.GetCurrentProcess().Id.ToString();
                string safeName = string.Concat(Path.GetFileNameWithoutExtension(filePath)
                    .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
                _openLockMetadataPath = Path.Combine(Path.GetTempPath(), $"red-open-{processId}-{safeName}.lock");
                File.WriteAllText(_openLockMetadataPath,
                    $"path={Path.GetFullPath(filePath)}{Environment.NewLine}type={inspectionType?.Trim().ToUpperInvariant() ?? string.Empty}{Environment.NewLine}");
            }
            catch { }
        }

        private void UpdatePreviewText()
        {
            if (_currentItem == null) return;

            string currentText = CommentsTextBox.Text ?? "";
            string coreText = UserDataService.StripPrefixAndSuffix(currentText);
            string newText = UserDataService.BuildComment(_selectedPrefix ?? "", coreText, _selectedSuffixes);
            
            if (CommentsTextBox.Text != newText)
            {
                int caretPos = CommentsTextBox.CaretIndex;
                CommentsTextBox.Text = newText;
                CommentsTextBox.CaretIndex = Math.Min(caretPos, newText.Length);
            }
        }

        private void AddPrefixButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputDialog("Enter new trade name (without brackets):", "Add Custom Trade");
            dialog.Owner = this;
            
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                _userDataService?.AddCustomPrefix(dialog.ResponseText);
                InitializePrefixSuffixButtons();
                MessageBox.Show($"Added [{dialog.ResponseText.ToLower()}] to trades!", "Added", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void AddSuffixButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new InputDialog("Enter new location (without parentheses):", "Add Custom Location");
            dialog.Owner = this;
            
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                _userDataService?.AddCustomSuffix(dialog.ResponseText);
                InitializePrefixSuffixButtons();
                MessageBox.Show($"Added ({dialog.ResponseText.ToLower()}) to locations!", "Added", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void PrefixButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ToggleButton btn && btn.Tag is string prefix)
            {
                if (_userDataService != null && _userDataService.IsCustomPrefix(prefix))
                {
                    var result = MessageBox.Show(
                        $"Remove {prefix} from trades?",
                        "Remove Custom Trade",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        if (_selectedPrefix == prefix) _selectedPrefix = null;
                        _userDataService.RemoveCustomPrefix(prefix);
                        InitializePrefixSuffixButtons();
                        UpdatePreviewText();
                    }
                }
                e.Handled = true;
            }
        }

        private void SuffixButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ToggleButton btn && btn.Tag is string suffix)
            {
                if (_userDataService != null && _userDataService.IsCustomSuffix(suffix))
                {
                    var result = MessageBox.Show(
                        $"Remove {suffix} from locations?",
                        "Remove Custom Location",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        _selectedSuffixes.Remove(suffix);
                        _userDataService.RemoveCustomSuffix(suffix);
                        InitializePrefixSuffixButtons();
                        UpdatePreviewText();
                    }
                }
                e.Handled = true;
            }
        }

        private void DateTimeSuffixButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: if a date suffix already exists, remove it; otherwise add one
            var existingDate = _selectedSuffixes.FirstOrDefault(s => IsTimestampSuffix(s));
            
            if (existingDate != null)
            {
                // Date already present — remove it (toggle off)
                _selectedSuffixes.Remove(existingDate);
            }
            else
            {
                // Remove any stale timestamps first (safety), then add fresh one
                _selectedSuffixes.RemoveAll(s => IsTimestampSuffix(s));
                
                var now = DateTime.Now;
                string dateSuffix = $"({now.Month}/{now.Day}/{now.Year} {now:h tt})";
                _selectedSuffixes.Add(dateSuffix);
            }
            
            UpdatePreviewText();
            MarkUnsaved();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            CommentsTextBox.Text = "";
            _selectedPrefix = null;
            _selectedSuffixes.Clear();
            
            foreach (var btn in _prefixButtons) btn.IsChecked = false;
            foreach (var btn in _suffixButtons) btn.IsChecked = false;
            
            // Hide quick suggestions so the user can type fresh
            QuickSuggestionsOverlay.Visibility = Visibility.Collapsed;
            
            // Set focus to comment box so keyboard is ready
            CommentsTextBox.Focus();
            UpdateClassicSpecialistFlagButtonState();
        }

        private void SpecialistFlagButton_Click(object sender, RoutedEventArgs e)
        {
            string comment = CommentsTextBox.Text ?? "";
            if (!CanAddSpecialistFlag(comment))
            {
                UpdateClassicSpecialistFlagButtonState();
                return;
            }

            CommentsTextBox.Text = AddSpecialistFlag(comment);
            CommentsTextBox.CaretIndex = CommentsTextBox.Text.Length;
            CommentsTextBox.Focus();
            UpdateClassicSpecialistFlagButtonState();
            MarkUnsaved();
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editorLoadedItem == null)
            {
                MessageBox.Show("Please select an item first.", "No Item Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            try
            {
                // Subscribe to photo events if not already
                if (!_cameraService.IsActive)
                {
                    _cameraService.PhotoCaptured += OnPhotoCaptured;
                }
                
                // Open camera or bring it back to front for more photos
                _cameraService.OpenOrFocusCamera();
                CameraButton.Content = "📷 Camera Open";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Camera error: {ex.Message}\n\nTry using 'File' to select an image instead.", 
                    "Camera Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnPhotoCaptured(byte[] photoData)
        {
            // This fires on the UI thread for each photo taken in the camera app
            if (_editorLoadedItem == null) return;

            try
            {
                AddPhotoToCurrentItem(photoData);
                
                DisplayImage(photoData);
                _currentPhotoData = photoData;
                GetSuggestionsButton.IsEnabled = _grokClient != null;
                TranscribeButton.IsEnabled = _grokClient != null;
                
                // Update photo navigation to show new photo
                _currentPhotoIndex = _editorLoadedItem.Pictures.Count - 1;
                LoadCurrentPhoto();
                
                MarkUnsaved();
                
                // Update required field highlighting
                UpdateRequiredFieldHighlighting(_editorLoadedItem);

                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                Focus();
                
                // Brief visual feedback on the button
                CameraButton.Content = $"📷 {_editorLoadedItem.Pictures.Count} photo(s)";
            }
            catch (InvalidOperationException ex)
            {
                // Corrupt photo data — show user-friendly message
                MessageBox.Show(ex.Message, "Photo Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Photo capture error: {ex.Message}");
                MessageBox.Show(
                    $"⚠️ Error processing photo: {ex.Message}\n\nThe photo may be in your Camera Roll — try adding it manually with the 📎 button.",
                    "Photo Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        
        private void AddPhotoToCurrentItem(byte[] photoData)
        {
            // Use _editorLoadedItem (not _currentItem) to avoid touch-drift issues
            if (_editorLoadedItem == null) return;
            
            // Validate image data before processing — reject corrupt/truncated photos
            if (photoData == null || photoData.Length < 1024)
            {
                MessageBox.Show("⚠️ Photo data appears invalid or too small. Photo was not added.",
                    "Photo Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Log activity: first photo taken
            _activityService.LogPhotoTaken();
            
            // Auto-enhance: 25% lift + 50% sharpness
            byte[] enhancedData = ApplyAutoEnhancement(photoData);
            
            string base64Image = Convert.ToBase64String(enhancedData);
            string comment = CommentsTextBox.Text ?? "";
            int sortOrder = _editorLoadedItem.Pictures.Count;
            
            _editorLoadedItem.Pictures.Add(new Picture
            {
                PictureId = sortOrder,
                Title = _editorLoadedItem.Name,
                Comment = comment,
                Data = base64Image,
                Filename = $"{_editorLoadedItem.Number}_{sortOrder + 1}.jpg",
                SortOrder = sortOrder
            });
            
            // Auto-set to Fail for passfail-type items when photo is added WITH a text notation.
            // Photo alone = just documentation (e.g. mandatory driveway/patio pics) — don't auto-fail.
            // Photo + text notation = deficiency documented — auto-fail is appropriate.
            // BUT always respect explicit NI values - don't override inspector's choice.
            string controlName = _editorLoadedItem.ControlName?.ToLower() ?? "";
            bool isPassFailType = controlName.Contains("passfail");
            bool hasTextNotation = !string.IsNullOrWhiteSpace(comment);
            if (isPassFailType && hasTextNotation)
            {
                string oldValue = _editorLoadedItem.Value?.ToString()?.ToLower() ?? "";
                bool isExplicitlyNI = oldValue == "ni" || oldValue == "na" || oldValue == "n/a";
                
                // Only auto-set to Fail if not explicitly NI
                if (!isExplicitlyNI)
                {
                    _editorLoadedItem.Value = "Fail";
                    if (_currentItem == _editorLoadedItem)
                    {
                        _currentItem.Value = "Fail";
                    }
                    LoadStatusControls(_editorLoadedItem);
                }
            }
            
            // Refresh tree to show 📷 indicator and updated color
            PopulateTreeView(SearchFilterBox.Text);
        }
        
        // Max photo dimensions for INSPECT2022 compatibility
        // INSPECT2022 tablets save at 1632x918 or 1984x1116 — match the higher standard
        private const int MaxPhotoWidth = 1984;
        private const int MaxPhotoHeight = 1116;
        
        private byte[] ApplyAutoEnhancement(byte[] photoData)
        {
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(photoData);
                
                image.Mutate(ctx =>
                {
                    // Resize to INSPECT2022-compatible dimensions if larger
                    // Maintains aspect ratio, only downscales (never upscales)
                    if (image.Width > MaxPhotoWidth || image.Height > MaxPhotoHeight)
                    {
                        ctx.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                        {
                            Size = new SixLabors.ImageSharp.Size(MaxPhotoWidth, MaxPhotoHeight),
                            Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
                        });
                    }
                    
                    // 25% lift (brightness boost)
                    ctx.Brightness(1.25f);
                    // Sharpen removed for faster processing
                });
                
                using var ms = new MemoryStream();
                // Use quality 85 for good balance of size and clarity
                image.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder 
                { 
                    Quality = 85 
                });
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Auto-enhancement failed: {ex.Message}");
                
                // Try to at least resize even without enhancement
                try
                {
                    using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(photoData);
                    if (image.Width > MaxPhotoWidth || image.Height > MaxPhotoHeight)
                    {
                        image.Mutate(ctx => ctx.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
                        {
                            Size = new SixLabors.ImageSharp.Size(MaxPhotoWidth, MaxPhotoHeight),
                            Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
                        }));
                    }
                    using var ms = new MemoryStream();
                    image.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
                    return ms.ToArray();
                }
                catch
                {
                    throw new InvalidOperationException(
                        "Photo data appears to be corrupt and cannot be processed. " +
                        "The photo is in your Camera Roll — try adding it manually with the 📎 button.", ex);
                }
            }
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            // Extract embedded PDF to temp folder and open it
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // Resource name: {RootNamespace}.{filename}
                string resourceName = "InspectionEditor.RedHelp.pdf";
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    MessageBox.Show("Help PDF not found in application resources.", "Help Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string tempPath = Path.Combine(Path.GetTempPath(), "RedHelp.pdf");
                using (var file = File.Create(tempPath))
                    stream.CopyTo(file);
                Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open help file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            bool openInNewWindow = false;
            
            // If an inspection is already loaded, ask whether to open in new window or replace
            if (_currentInspection != null)
            {
                var choice = MessageBox.Show(
                    "You already have an inspection open.\n\n" +
                    "Yes — Keep this one open and open another RED window\n" +
                    "No — Close this one and open a new file here",
                    "Open Another Inspection",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);
                
                if (choice == MessageBoxResult.Cancel)
                    return;
                
                openInNewWindow = (choice == MessageBoxResult.Yes);
            }
            
            if (!openInNewWindow && _hasUnsavedChanges)
                SaveFileButton_Click(this, new RoutedEventArgs());

            // Pick the file first, then decide what to do with it
            string? selectedFile = null;
            
            // Default to user's Dropbox Inspections folder
            string defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Dropbox", "Inspections", "MyList"
            );

            // Show inspection picker modal
            if (Directory.Exists(defaultPath))
            {
                var picker = new InspectionPickerWindow(defaultPath) { Owner = this };
                if (picker.ShowDialog() == true && !string.IsNullOrEmpty(picker.SelectedFilePath))
                    selectedFile = picker.SelectedFilePath;
            }
            else
            {
                // Fallback to standard file dialog if folder doesn't exist
                var dialog = new OpenFileDialog
                {
                    Filter = "INS Files (*.ins)|*.ins|All Files (*.*)|*.*",
                    Title = "Open Inspection File",
                    InitialDirectory = ""
                };

                if (dialog.ShowDialog() == true)
                    selectedFile = dialog.FileName;
            }
            
            if (string.IsNullOrEmpty(selectedFile))
                return;
            
            try
            {
                if (openInNewWindow)
                {
                    // Launch a new RED process with the selected file
                    string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    // For single-file/self-contained publish, use the .exe not the .dll
                    if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        exePath = exePath.Substring(0, exePath.Length - 4) + ".exe";
                    
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"\"{selectedFile}\"",
                        UseShellExecute = false
                    });
                }
                else
                {
                    LoadInspectionFileAsync(selectedFile);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadInspectionFileAsync(string filePath)
        {
            // Prevent double-loading
            if (_isLoadingFile) return;
            _isLoadingFile = true;
            ShowOpeningInspectionState(filePath);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            
            try
            {
            // Reset read-only mode for new file
            _readOnlyMode = false;
            TripNumberBadge.Visibility = Visibility.Collapsed;
            
            // --- Multi-instance locking ---
            // Prevent the same .ins file or same inspection type from being open in another RED window.
            string filename = Path.GetFileNameWithoutExtension(filePath);
            var filenameParts = filename.Split('-');

            _currentSequenceNumber = 1;
            if (filenameParts.Length >= 3 && int.TryParse(filenameParts[2], out int parsedSeq))
                _currentSequenceNumber = parsedSeq;

            // File-level lock: hash the full path to create a unique mutex name
            string fullPathNorm = Path.GetFullPath(filePath).ToUpperInvariant();
            string pathHash;
            using (var sha = SHA256.Create())
            {
                pathHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fullPathNorm)))
                    .Replace("-", "").Substring(0, 16);
            }
            string fileMutexName = $"Global\\RED_FILE_{pathHash}";
            
            bool fileLockAcquired = false;
            Mutex newFileMutex;
            try
            {
                newFileMutex = new Mutex(true, fileMutexName, out fileLockAcquired);
            }
            catch
            {
                fileLockAcquired = false;
                newFileMutex = null!;
            }
            
            if (!fileLockAcquired)
            {
                newFileMutex?.Dispose();
                MessageBox.Show(
                    "This file is already open in another RED window.",
                    "File In Use",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            
            // Type-level lock: only one inspection of each type (IER, CPP, HET, etc.) across instances
            string inspTypeForLock = (filenameParts.Length > 1) ? filenameParts[1].ToUpperInvariant() : "";
            Mutex? newTypeMutex = null;
            if (!string.IsNullOrEmpty(inspTypeForLock))
            {
                string typeMutexName = $"Global\\RED_TYPE_{inspTypeForLock}";
                bool typeLockAcquired = false;
                try
                {
                    newTypeMutex = new Mutex(true, typeMutexName, out typeLockAcquired);
                }
                catch
                {
                    typeLockAcquired = false;
                }
                
                if (!typeLockAcquired)
                {
                    // Release the file lock we just acquired
                    newFileMutex.ReleaseMutex();
                    newFileMutex.Dispose();
                    newTypeMutex?.Dispose();
                    MessageBox.Show(
                        $"You already have a {inspTypeForLock} inspection open in another RED window.\n\n" +
                        "Only one inspection of each type can be open at a time.",
                        "Type Already Open",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            
            // Release previous locks (from previously loaded file)
            ReleaseInspectionLocks();
            
            // Store new locks
            _fileMutex = newFileMutex;
            _typeMutex = newTypeMutex;
            WriteOpenInspectionLockMetadata(filePath, inspTypeForLock);
            // --- End multi-instance locking ---
            
            _currentInspection = await Task.Run(() => _saveService.Load(filePath));
            _currentFilePath = filePath;
            _currentInspectionCode = _currentInspection.InspectionCode;
            _rulesAcknowledged = false;

            // Get builder/client name from inspection for rules matching
            // BuilderName (e.g. "DR Horton") is the correct field — Contact is the superintendent
            string clientName = _currentInspection.BuilderName ?? _currentInspection.Contact ?? "Unknown";
            _currentClientName = clientName;

            var insTypeConfig = _inspTypeService.GetConfig(_currentInspectionCode ?? "");
            string? insFolder = Path.GetDirectoryName(filePath);
            string? inspRoot = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
            string jobsFolderForChecker = inspRoot != null ? Path.Combine(inspRoot, "Jobs") : "";
            string myListFolder = insFolder ?? "";

            var freeAlerts = new List<InspectionEditor.Services.FreeReinspectionAlert>();
            if (insTypeConfig != null && !string.IsNullOrEmpty(jobsFolderForChecker))
            {
                try
                {
                    freeAlerts = await InspectionEditor.Services.FreeReinspectionChecker.CheckAsync(
                        filePath, insTypeConfig, myListFolder, jobsFolderForChecker);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FreeReinspectionChecker failed: {ex.Message}");
                }
            }

            if (!await ShowRulesWindowAsync(true, freeAlerts))
            {
                MessageBox.Show("You must acknowledge all rules before proceeding.",
                    "Rules Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _rulesAcknowledged = true;

            FileNameText.Text = $"{_currentInspection.InspectionCode} - {_currentInspection.InspectionName}";
            Title = _readOnlyMode 
                ? $"Red v{AppVersion} - {_currentInspection.InspectionCode} [READ-ONLY]" 
                : $"Red v{AppVersion} - {_currentInspection.InspectionCode}";
            
            // Reset stats mode to Global for new inspection
            _statsMode = StatsMode.Global;
            UpdateStatsModeVisuals();
            // Enable save button (always functional)
            SaveFileButton.IsEnabled = true;
            // In "read-only" mode: looks grayed but secretly still works
            SaveFileButton.Opacity = _readOnlyMode ? 0.4 : 1.0;
            
            // Show Transcribe button only for energy inspections (HER, IER, HET, IEF, IAP, AFI)
            string upperCode = (_currentInspectionCode ?? "").ToUpperInvariant();
            bool isEnergyInspection = upperCode == "HER" || upperCode == "IER" || 
                                       upperCode == "HET" || upperCode == "IEF" || 
                                       upperCode == "HEF" || upperCode == "IAP" || 
                                       upperCode == "AFI";
            TranscribeButton.Visibility = isEnergyInspection ? Visibility.Visible : Visibility.Collapsed;
            
            UpdateSeeDocsButton(filePath);
            SaveTemplateButton.IsEnabled = true;
            ApplyTemplateButton.IsEnabled =
                _templateService.HasTemplatesForForm(_currentInspection.FormId) ||
                (_userDataService?.HasTemplate(_currentInspectionCode ?? "") == true);
            WelcomePanel.Visibility = Visibility.Collapsed;
            _hasUnsavedChanges = false;
            _hasBeenEditedThisSession = false;
            _skipResultCheck = false;

            // Update header with builder and inspector info
            UpdateHeaderInfo();
            
            // Log activity: inspection loaded
            _activityService.StartSession(filePath, _currentInspectorName ?? "unknown", _currentInspectionCode ?? "unknown");

            // Check for FormId-based templates (only on first trip, not reinspections)
            // Filename format: {jobID}-{inspType}-{tripCount}-{initials}.ins
            bool isFirstTrip = true;
            if (filenameParts.Length >= 3 && int.TryParse(filenameParts[2], out int tripCount))
            {
                isFirstTrip = tripCount == 1;
                TripNumberBadge.Text = $"Trip #{tripCount}";
                TripNumberBadge.Visibility = Visibility.Visible;
            }
            else
            {
                TripNumberBadge.Visibility = Visibility.Collapsed;
            }

            _ = Dispatcher.BeginInvoke(new Action(LoadMegaStats),
                System.Windows.Threading.DispatcherPriority.Background);

            // Check the loaded checklist content instead of file size. Blank INS files
            // can be large, and photos make size an unreliable signal.
            bool reportAlreadyFilled = InspectionHasTemplateRelevantData();

            if (isFirstTrip && !reportAlreadyFilled)
            {
                var formTemplates = _templateService.GetTemplatesForForm(_currentInspection.FormId);
                if (formTemplates.Count > 0)
                {
                    PromptApplyTemplate(formTemplates);
                }
                // Fallback: Check for legacy InspectionCode-based template
                else if (_userDataService != null && _userDataService.HasTemplate(_currentInspectionCode ?? ""))
                {
                    var result = MessageBox.Show(
                        $"A saved template exists for {_currentInspectionCode} inspections.\n\nApply default values?",
                        "Apply Template?",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                        ApplyInspectionTemplate();
                }
            }

            // REQ filter always off by default
            _reqFilterActive = false;
            ReqFilterButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
            ReqFilterButton.Foreground = new SolidColorBrush(Colors.White);
            ReqFilterButton.BorderThickness = new Thickness(0);
            
            // Reset INC filter on new file load
            _incFilterActive = false;
            IncFilterButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            IncFilterButton.Foreground = new SolidColorBrush(Colors.White);
            IncFilterButton.BorderThickness = new Thickness(0);
            
            // Set ALL button as active (default filter state)
            _ofiFilterActive = false;
            ClearSearchButton.Background = new SolidColorBrush(Colors.White);
            ClearSearchButton.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
            ClearSearchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
            ClearSearchButton.BorderThickness = new Thickness(3);

            // Reset editor panel — no item selected yet in new inspection
            _currentItem = null;
            _editorLoadedItem = null;
            _currentPhotoData = null;
            _currentPhotoIndex = 0;
            _selectedPrefix = null;
            _selectedSuffixes.Clear();
            _collapsedInlineSectionKeys.Clear();
            EditorPanel.Visibility = Visibility.Collapsed;
            SelectItemPanel.Visibility = Visibility.Visible;
            
            PopulateTreeView();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading inspection:\n\n{ex.Message}\n\n{ex.StackTrace}", 
                    "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLoadingFile = false;
                Cursor = Cursors.Arrow;
            }
        }

        private async Task<bool> ShowRulesWindowAsync(
            bool enforceAck,
            List<InspectionEditor.Services.FreeReinspectionAlert>? freeAlerts = null)
        {
            if (_openedAsCompanion || _currentSequenceNumber > 1)
                return true;

            if (_currentInspection == null)
                return true;

            string clientName = !string.IsNullOrWhiteSpace(_currentClientName)
                ? _currentClientName
                : _currentInspection.BuilderName ?? _currentInspection.Contact ?? "Unknown";

            try
            {
                var rules = await _rulesService.GetApplicableRulesAsync(
                    _currentInspectionCode ?? "",
                    clientName,
                    _userDataService?.UserName ?? ""
                );

                var actionable = rules.Where(r => r.RequireAck).ToList();
                if (enforceAck && actionable.Count == 0 && (freeAlerts == null || freeAlerts.Count == 0))
                {
                    return true;
                }

                var window = new RulesAcknowledgmentWindow(actionable, _currentInspectionCode ?? "", clientName, enforceAck)
                {
                    Owner = this
                };

                if (freeAlerts != null && freeAlerts.Count > 0)
                    window.SetFreeReinspectionAlerts(freeAlerts);

                bool ackResult;
                if (enforceAck)
                {
                    var dialogResult = window.ShowDialog();
                    ackResult = dialogResult == true && window.AllAcknowledged;
                }
                else
                {
                    window.ShowDialog();
                    ackResult = true;
                }

                foreach (var companionPath in window.SelectedCompanionPaths)
                {
                    try { MainWindow.OpenAsCompanion(companionPath); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to open companion {companionPath}: {ex.Message}");
                    }
                }
                return ackResult;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Rules fetch error: {ex.Message}");
                return true;
            }
        }

        private void ApplyInspectionTemplate()
        {
            if (_userDataService == null || _currentInspection == null || string.IsNullOrEmpty(_currentInspectionCode))
                return;

            var template = _userDataService.GetTemplate(_currentInspectionCode);
            bool isSCI = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);
            
            foreach (var section in _currentInspection.Sections)
            {
                // Skip Administrative section - these are report-specific header fields
                if (!isSCI && (section.Number == "1" || section.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                foreach (var item in section.Items)
                {
                    if (template.TryGetValue(item.Number ?? "", out var defaultValue))
                    {
                        if (!string.IsNullOrEmpty(defaultValue))
                            item.Value = defaultValue;
                    }
                }
            }

            MarkUnsaved();
            RefreshAfterTemplateChange();
            // Removed success popup - user already knows they clicked Apply Template
        }

        #region FormId-Based Templates

        private bool InspectionHasTemplateRelevantData()
        {
            if (_currentInspection?.Sections == null)
                return false;

            bool isSCI = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);

            foreach (var section in _currentInspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (!isSCI && IsProperNameItem(item))
                        continue;

                    if (!string.IsNullOrWhiteSpace(item.Comments))
                        return true;

                    if (item.Pictures.Count > 0)
                        return true;

                    var value = item.Value?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return true;
                }
            }

            return false;
        }

        private void RefreshAfterTemplateChange()
        {
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            PopulateTreeView(SearchFilterBox.Text);

            if (_editorLoadedItem != null)
                LoadItemEditor(_editorLoadedItem);
        }

        private void PromptApplyTemplate(List<InspectionTemplate> templates)
        {
            if (_currentInspection == null) return;

            if (templates.Count == 1)
            {
                // Single template - simple yes/no
                var template = templates[0];
                var result = MessageBox.Show(
                    $"A saved template exists for this form type:\n\n\"{template.Name}\"\n\nApply default values?",
                    "Apply Template?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    int count = _templateService.ApplyTemplate(template, _currentInspection, _currentInspectionCode);
                    MarkUnsaved();
                    RefreshAfterTemplateChange();
                    // Removed success popup - user already clicked Yes to apply
                }
            }
            else
            {
                // Multiple templates - show selection dialog
                var templateNames = templates.Select(t => t.Name).ToList();
                templateNames.Insert(0, "(Don't apply any template)");
                
                // Create a simple selection window
                var selectionWindow = new Window
                {
                    Title = "Select Template",
                    Width = 350,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = System.Windows.ResizeMode.NoResize
                };

                var stack = new StackPanel { Margin = new Thickness(15) };
                stack.Children.Add(new TextBlock 
                { 
                    Text = $"Multiple templates exist for this form type.\nSelect one to apply:", 
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10) 
                });

                var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
                foreach (var name in templateNames)
                    combo.Items.Add(name);
                combo.SelectedIndex = 0;
                stack.Children.Add(combo);

                var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var okBtn = new Button { Content = "Apply", Width = 80, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
                var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
                
                okBtn.Click += (s, e) => { selectionWindow.DialogResult = true; selectionWindow.Close(); };
                cancelBtn.Click += (s, e) => { selectionWindow.DialogResult = false; selectionWindow.Close(); };
                
                buttonPanel.Children.Add(okBtn);
                buttonPanel.Children.Add(cancelBtn);
                stack.Children.Add(buttonPanel);

                selectionWindow.Content = stack;

                if (selectionWindow.ShowDialog() == true && combo.SelectedIndex > 0)
                {
                    var selectedTemplate = templates[combo.SelectedIndex - 1];
                    int count = _templateService.ApplyTemplate(selectedTemplate, _currentInspection, _currentInspectionCode);
                    MarkUnsaved();
                    RefreshAfterTemplateChange();
                    // Removed success popup - user already selected and applied template
                }
            }
        }

        private void ApplyTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInspection == null) return;
            AutoApplyCurrentItem();

            var templates = _templateService.GetTemplatesForForm(_currentInspection.FormId);
            if (templates.Count == 0)
            {
                if (_userDataService != null && _userDataService.HasTemplate(_currentInspectionCode ?? ""))
                {
                    ApplyInspectionTemplate();
                    return;
                }

                MessageBox.Show("No templates saved for this form type.", "No Templates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PromptApplyTemplate(templates);
        }

        private void SaveTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInspection == null) return;
            AutoApplyCurrentItem();

            // Check how many templates already exist for this form
            var existingTemplates = _templateService.GetTemplatesForForm(_currentInspection.FormId);
            
            // Show save dialog
            var saveWindow = new Window
            {
                Title = "Save Template",
                Width = 400,
                Height = existingTemplates.Count > 0 ? 320 : 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = System.Windows.ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(15) };
            
            stack.Children.Add(new TextBlock 
            { 
                Text = $"Save current values as a template for:\n{_currentInspection.InspectionName}", 
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 15) 
            });

            stack.Children.Add(new TextBlock { Text = "Template Name:", Margin = new Thickness(0, 0, 0, 5) });
            var nameBox = new TextBox 
            { 
                Text = existingTemplates.Count == 0 ? "Default" : $"Template {existingTemplates.Count + 1}",
                Margin = new Thickness(0, 0, 0, 10)
            };
            stack.Children.Add(nameBox);

            // Show existing templates if any
            ListBox? listBox = null;
            Button? deleteBtn = null;
            
            if (existingTemplates.Count > 0)
            {
                stack.Children.Add(new TextBlock 
                { 
                    Text = "Existing templates (click to overwrite):", 
                    Margin = new Thickness(0, 10, 0, 5),
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray
                });

                listBox = new ListBox { MaxHeight = 80 };
                foreach (var t in existingTemplates)
                    listBox.Items.Add($"{t.Name} (saved {t.Modified:MMM d})");
                
                listBox.SelectionChanged += (s, ev) =>
                {
                    if (listBox.SelectedIndex >= 0)
                    {
                        nameBox.Text = existingTemplates[listBox.SelectedIndex].Name;
                        if (deleteBtn != null) deleteBtn.IsEnabled = true;
                    }
                };
                stack.Children.Add(listBox);
                
                // Add delete button for existing templates
                deleteBtn = new Button 
                { 
                    Content = "🗑️ Delete Selected Template", 
                    Margin = new Thickness(0, 5, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(231, 76, 60)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 5, 8, 5),
                    IsEnabled = false
                };
                deleteBtn.Click += (s, ev) =>
                {
                    if (listBox.SelectedIndex >= 0)
                    {
                        var templateToDelete = existingTemplates[listBox.SelectedIndex];
                        var confirm = MessageBox.Show(
                            $"Delete template \"{templateToDelete.Name}\"?\n\nThis cannot be undone.",
                            "Confirm Delete",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        
                        if (confirm == MessageBoxResult.Yes)
                        {
                            _templateService.DeleteTemplate(templateToDelete.Id);
                            existingTemplates.RemoveAt(listBox.SelectedIndex);
                            listBox.Items.RemoveAt(listBox.SelectedIndex);
                            deleteBtn.IsEnabled = false;
                            nameBox.Text = existingTemplates.Count == 0 ? "Default" : $"Template {existingTemplates.Count + 1}";
                            
                            if (existingTemplates.Count == 0)
                            {
                                ApplyTemplateButton.IsEnabled = false;
                            }
                            
                            MessageBox.Show("Template deleted.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                };
                stack.Children.Add(deleteBtn);
            }

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var saveBtn = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            
            string? savedTemplateId = null;
            
            saveBtn.Click += (s, ev) =>
            {
                var templateName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(templateName))
                {
                    MessageBox.Show("Please enter a template name.", "Name Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check if updating existing
                var existing = existingTemplates.FirstOrDefault(t => t.Name.Equals(templateName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    var confirm = MessageBox.Show(
                        $"Overwrite existing template \"{existing.Name}\"?",
                        "Confirm Overwrite",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (confirm != MessageBoxResult.Yes) return;
                    
                    _templateService.UpdateTemplate(existing.Id, _currentInspection, _currentInspectionCode);
                    savedTemplateId = existing.Id;
                }
                else
                {
                    var template = _templateService.CreateTemplate(_currentInspection, _currentInspection.FormId, templateName, inspectionCode: _currentInspectionCode);
                    savedTemplateId = template.Id;
                }

                saveWindow.DialogResult = true;
                saveWindow.Close();
            };
            
            cancelBtn.Click += (s, ev) => { saveWindow.DialogResult = false; saveWindow.Close(); };
            
            buttonPanel.Children.Add(saveBtn);
            buttonPanel.Children.Add(cancelBtn);
            stack.Children.Add(buttonPanel);

            saveWindow.Content = stack;

            if (saveWindow.ShowDialog() == true)
            {
                MessageBox.Show("Template saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                ApplyTemplateButton.IsEnabled = true;
            }
        }

        #endregion

        private void UpdateSeeDocsButton(string filePath)
        {
            // Parse job ID from filename: {jobID}-{inspType}-{tripCount}-{initials}.ins
            string filename = Path.GetFileNameWithoutExtension(filePath);
            var parts = filename.Split('-');
            string jobId = parts.Length > 0 ? parts[0] : "";
            
            // Find the Jobs folder
            string? insFolder = Path.GetDirectoryName(filePath);
            string? inspectionsFolder = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
            string jobsFolder = inspectionsFolder != null ? Path.Combine(inspectionsFolder, "Jobs") : "";
            string jobFolder = !string.IsNullOrEmpty(jobsFolder) ? Path.Combine(jobsFolder, jobId) : "";
            
            // Count available docs
            int docCount = 0;
            if (Directory.Exists(jobFolder))
            {
                string docsEngFolder = Path.Combine(jobFolder, "Engineering");
                string inspFolder = Path.Combine(jobFolder, "Inspections");

                if (Directory.Exists(docsEngFolder))
                    docCount += Directory.GetFiles(docsEngFolder, "*.pdf").Length;
                if (Directory.Exists(inspFolder))
                    docCount += Directory.GetFiles(inspFolder, "*.pdf").Length;
            }
            
            string inspType = parts.Length > 1 ? parts[1] : "";
            string? offerFileLabel = _inspTypeService.GetConfig(inspType)?.OfferFileLabel;

            // Update button based on doc count
            if (docCount > 0)
            {
                string baseLabel = !string.IsNullOrEmpty(offerFileLabel) ? offerFileLabel : "📄 See All PDFs";
                SeeDocsButton.Content = $"{baseLabel} ({docCount})";
                SeeDocsButton.IsEnabled = true;
                SeeDocsButton.ToolTip = $"View {docCount} engineering plans and inspection reports";
            }
            else
            {
                SeeDocsButton.Content = !string.IsNullOrEmpty(offerFileLabel) ? offerFileLabel : "📄 See All PDFs";
                SeeDocsButton.IsEnabled = false;
                SeeDocsButton.ToolTip = "No documents found for this job";
            }

            // Prior report button: look for trip N-1 PDF in Jobs/{jobId}/Inspections/
            UpdatePriorReportButton(filePath, parts, jobsFolder);
            string engFolder = Path.Combine(jobFolder, "Engineering");

            _currentSlabInfo = null;
            _currentEcInfo = null;
            _planPdfPath = null;
            _designExtractionLoading = false;
            SlabEngButton.IsEnabled = false;
            SlabEngButton.Visibility = Visibility.Collapsed;
            EcDataHeaderBorder.Visibility = Visibility.Collapsed;
            UpdateDesignExtractionButton();

            if (inspType.Equals("CPP", StringComparison.OrdinalIgnoreCase))
            {
                // CPP: full OCR extraction for slab engineering data
                SlabEngButton.Content = "Slab Eng";
                SlabEngButton.Visibility = Visibility.Visible;
                SlabEngButton.ToolTip = "Loading slab engineering data…";

                _designExtractionLoading = true;
                UpdateDesignExtractionButton();

                string capturedFilePath = filePath;
                Task.Run(() => SlabEngineeringService.GetInfoForInspection(capturedFilePath))
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (_currentFilePath == capturedFilePath)
                                {
                                    _designExtractionLoading = false;
                                    UpdateDesignExtractionButton();
                                }
                            });
                            return;
                        }
                        var info = t.Result;
                        Dispatcher.Invoke(() =>
                        {
                            if (_currentFilePath != capturedFilePath) return;
                            _currentSlabInfo = info;
                            _designExtractionLoading = false;
                            bool hasPdf = !string.IsNullOrWhiteSpace(info.PdfPath) && File.Exists(info.PdfPath);
                            SlabEngButton.Content = GetSlabEngineeringButtonLabel(info);
                            SlabEngButton.IsEnabled = hasPdf;
                            SlabEngButton.ToolTip = hasPdf
                                ? $"Open latest slab engineering PDF: {info.DisplayName}"
                                : (info.StatusText ?? "No slab engineering PDF found for this job");
                            RefreshEngDataPanel();
                            UpdateDesignExtractionButton();
                            RefreshInlineChecklistForBackgroundData();
                        });
                    });
            }
            else
            {
                // Energy inspection types: load EC report in background
                bool isEnergyType = inspType.Equals("IER", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("HER", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("IEF", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("HEF", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("HET", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("AFI", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("PLY", StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("PS",  StringComparison.OrdinalIgnoreCase)
                    || inspType.Equals("ACI", StringComparison.OrdinalIgnoreCase);

                if (isEnergyType)
                {
                    string capturedFilePath = filePath;
                    _designExtractionLoading = true;
                    UpdateDesignExtractionButton();
                    Task.Run(() => EnergyComplianceService.GetInfoForInspection(capturedFilePath))
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    if (_currentFilePath == capturedFilePath)
                                    {
                                        _designExtractionLoading = false;
                                        UpdateDesignExtractionButton();
                                    }
                                });
                                return;
                            }
                            var info = t.Result;
                            Dispatcher.Invoke(() =>
                            {
                                if (_currentFilePath != capturedFilePath) return;
                                _currentEcInfo = info;
                                _designExtractionLoading = false;
                                RefreshEcDataPanel();
                                UpdateDesignExtractionButton();
                                RefreshInlineChecklistForBackgroundData();
                            });
                        });
                }

                // All other types: find the specific plan PDF by type mapping
                string? planPdf = FindPlanPdf(engFolder, inspType);
                if (planPdf != null)
                {
                    _planPdfPath = planPdf;
                    SlabEngButton.Content = GetPlanButtonLabel(inspType, planPdf);
                    SlabEngButton.ToolTip = $"Open: {Path.GetFileName(planPdf)}";
                    SlabEngButton.IsEnabled = true;
                    SlabEngButton.Visibility = Visibility.Visible;
                    UpdateDesignExtractionButton();
                }
            }
        }

        /// <summary>
        /// Shows "◀ Prior" button when a previous trip PDF exists for this job+inspType.
        /// Filename format: {jobId}-{inspType}-{trip}-{initials}.ins
        /// Prior PDF lives at: Jobs/{jobId}/Inspections/{jobId}-{inspType}-{trip-1}-*.pdf
        /// </summary>
        private void UpdatePriorReportButton(string filePath, string[] parts, string jobsFolder)
        {
            PriorReportButton.Visibility = Visibility.Collapsed;
            PriorReportButton.IsEnabled = false;
            PriorReportButton.Tag = null;

            if (parts.Length < 3) return;
            string jobId = parts[0];
            string inspType = parts[1];
            if (!int.TryParse(parts[2], out int trip) || trip <= 1) return;

            string jobFolder = Path.Combine(jobsFolder, jobId);
            string inspFolder = Path.Combine(jobFolder, "Inspections");
            if (!Directory.Exists(inspFolder)) return;

            int prevTrip = trip - 1;
            // Match any initials: {jobId}-{inspType}-{prevTrip}-*.pdf
            string pattern = $"{jobId}-{inspType}-{prevTrip}-*.pdf";
            var match = Directory.GetFiles(inspFolder, pattern, SearchOption.TopDirectoryOnly)
                                 .FirstOrDefault();

            if (match == null) return;

            PriorReportButton.Content = $"◀ Trip {prevTrip}";
            PriorReportButton.ToolTip = $"Open prior report: {Path.GetFileName(match)}";
            PriorReportButton.Tag = match;
            PriorReportButton.IsEnabled = true;
            PriorReportButton.Visibility = Visibility.Visible;
        }

        // ── Plan PDF helpers ─────────────────────────────────────────────────

        private static string? FindPlanPdf(string engFolder, string inspType)
        {
            if (!Directory.Exists(engFolder)) return null;
            var pdfs = Directory.GetFiles(engFolder, "*.pdf");
            if (pdfs.Length == 0) return null;

            return inspType.ToUpperInvariant() switch
            {
                // FD suffix — same scoring as SlabEngineeringService (CPP is handled separately with OCR)
                "CPR" or "STR" => FindBestPdf(pdfs, n =>
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\(\d+(?:R\d+)?FD\)$")) return 120;
                    if (n.EndsWith("FD")) return 80;
                    if (n.Contains("FDD") || n.Contains("FRD")) return -1;
                    return 0;
                }),

                // EC suffix
                "HEF" or "HER" or "HET" or "IAP" or "IEF" or "IER" or "PLY" or "QIER" =>
                    FindBestPdf(pdfs, n => n.EndsWith("EC") || n.EndsWith("-EC") || n.EndsWith("_EC") ? 100 : 0),

                // FFP suffix
                "PPE" => FindBestPdf(pdfs, n => n.Contains("FFP") ? 100 : 0),

                // Engineering with detail sheets (prefer detail-sheet PDF; fall back to plain job-number PDF)
                "AFI" or "COH" or "FS" or "FSF" or "ME" or "MP" or "SWI" or "TPC" =>
                    FindBestPdf(pdfs, n =>
                        n.Contains("WITH DETAIL") ? 100 :
                        n.Contains("DETAIL") ? 80 :
                        System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+$") ? 50 : 0),

                // Plain engineering (prefer bare job-number file; reject known-suffix files)
                "SCI" or "TFF" or "TRDI" or "TRSI" =>
                    FindBestPdf(pdfs, n =>
                        System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+$") ? 100 :
                        !n.Contains("FD") && !n.Contains("EC") && !n.Contains("FFP") && !n.Contains("ARCH") ? 20 : 0),

                // Architectural plan
                "BC" or "BF" or "BWT" or "FWI" =>
                    FindBestPdf(pdfs, n =>
                        n.Contains("ARCH") ? 100 :
                        n.Contains("PLAN") && !n.Contains("FD") ? 60 : 0),

                // Repair design
                "SRP" => FindBestPdf(pdfs, n =>
                    n.Contains("REPAIR") ? 100 :
                    n.Contains("SRP") ? 90 : 0),

                _ => null
            };
        }

        private static string? FindBestPdf(string[] pdfs, Func<string, int> scorer)
        {
            return pdfs
                .Select(pdf => new
                {
                    Path = pdf,
                    Score = scorer(Path.GetFileNameWithoutExtension(pdf).ToUpperInvariant()),
                    Revision = GetRevisionFromDesignPath(pdf)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Revision)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Path)
                .Select(x => x.Path)
                .FirstOrDefault();
        }

        private static string GetPlanButtonLabel(string inspType, string? pdfPath = null)
        {
            string baseLabel = inspType.ToUpperInvariant() switch
            {
                "CPR" or "STR"                                                    => "FD Plan",
                "HEF" or "HER" or "HET" or "IAP" or "IEF" or "IER" or "PLY" or "QIER" => "EC Plan",
                "PPE"                                                             => "FFP Plan",
                "AFI" or "COH" or "FS" or "FSF" or "ME" or "MP" or "SWI" or "TPC" => "Eng Plan",
                "SCI" or "TFF" or "TRDI" or "TRSI"                               => "Eng Plan",
                "BC" or "BF" or "BWT" or "FWI"                                   => "Arch Plan",
                "SRP"                                                             => "Repair Design",
                _                                                                 => "Plan"
            };

            int revision = GetRevisionFromDesignPath(pdfPath);
            return revision > 0 ? $"{baseLabel} R{revision}" : baseLabel;
        }

        private void PriorReportButton_Click(object sender, RoutedEventArgs e)
        {
            if (PriorReportButton.Tag is string pdfPath && File.Exists(pdfPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pdfPath,
                    UseShellExecute = true
                });
            }
        }

        private void SeeDocsButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;

            var docsWindow = new DocsViewerWindow(_currentFilePath) { Owner = this };
            docsWindow.ShowDialog();
        }

        private void SlabEngButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFilePath)) return;

            // Non-CPP types: open the pre-found plan PDF directly
            if (_planPdfPath != null)
            {
                if (File.Exists(_planPdfPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        { FileName = _planPdfPath, UseShellExecute = true });
                else
                    MessageBox.Show($"Plan file not found:\n{_planPdfPath}", "File Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // CPP: open the already-discovered slab PDF. Re-running the slab
            // service here can trigger OCR/PDF parsing on the UI click path.
            if (!string.IsNullOrWhiteSpace(_currentSlabInfo?.PdfPath))
            {
                if (File.Exists(_currentSlabInfo.PdfPath))
                    SlabEngineeringService.OpenPdf(_currentSlabInfo.PdfPath);
                else
                    MessageBox.Show($"Slab engineering file not found:\n{_currentSlabInfo.PdfPath}", "File Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Fallback only: this can be slower because it discovers/parses the slab PDF.
            try
            {
                SlabEngineeringService.OpenSlabEngineeringPdf(_currentFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Slab Eng", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SlabEngButton_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (_currentSlabInfo?.RevisionOptions == null || _currentSlabInfo.RevisionOptions.Count == 0)
                return;

            var menu = new ContextMenu();
            foreach (var option in _currentSlabInfo.RevisionOptions)
            {
                var item = new MenuItem
                {
                    Header = option.Label,
                    Tag = option.FullPath,
                    FontWeight = option.FullPath == _currentSlabInfo.PdfPath ? FontWeights.Bold : FontWeights.Normal
                };
                item.Click += (s, _) =>
                {
                    try
                    {
                        SlabEngineeringService.OpenPdf(option.FullPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Slab Eng", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                };
                menu.Items.Add(item);
            }

            SlabEngButton.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private void UpdateSlabCableInfoBanner(Item item)
        {
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            UpdateClearButton();
        }

        /// Shows the 🧹 clear button when the current item has design data (slab or EC) AND a value to clear.
        private void RefreshInlineChecklistForBackgroundData()
        {
            if (!_inlineEditorMode || _currentInspection == null)
                return;

            PopulateInlineChecklist(SearchFilterBox.Text);
        }

        private void UpdateClearButton()
        {
            if (_currentItem == null) { ClearValueButton.Visibility = Visibility.Collapsed; return; }
            bool hasValue = !string.IsNullOrWhiteSpace(_currentItem.Value?.ToString());
            ClearValueButton.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentItem == null) return;
            _currentItem.Value = null;
            _hasUnsavedChanges = true;
            LoadStatusControls(_currentItem);
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            PopulateTreeView();
        }

        /// <summary>
        /// Populates the engineering data panel with all available foundation values.
        /// Shows whenever any field was extracted (not limited to specific item numbers).
        /// </summary>
        private void RefreshEngDataPanel()
        {
            var info = _currentSlabInfo;

            bool hasAny = info != null &&
                (info.CableCount.HasValue || info.SlabThicknessInches.HasValue ||
                 info.BeamWidthInches.HasValue || info.HolddownCount.HasValue);

            SlabCableInfoHeaderBorder.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
            if (!hasAny) return;

            if (info!.CableCount.HasValue)
            {
                EngChip_Cables.Text = $"Strands: {info.CableCount}";
                EngChip_Cables.Visibility = Visibility.Visible;
            }
            else EngChip_Cables.Visibility = Visibility.Collapsed;

            if (info.SlabThicknessInches.HasValue)
            {
                EngChip_Slab.Text = $"Slab: {info.SlabThicknessInches}\"";
                EngChip_Slab.Visibility = Visibility.Visible;
            }
            else EngChip_Slab.Visibility = Visibility.Collapsed;

            if (info.BeamWidthInches.HasValue && info.BeamDepthInches.HasValue)
            {
                EngChip_Beam.Text = $"Beam: {info.BeamWidthInches}\"W × {info.BeamDepthInches}\"D";
                EngChip_Beam.Visibility = Visibility.Visible;
            }
            else EngChip_Beam.Visibility = Visibility.Collapsed;

            if (info.HolddownCount.HasValue)
            {
                EngChip_Holddowns.Text = $"Holddowns: {SlabEngineeringService.FormatHolddownDisplay(info)}";
                EngChip_Holddowns.Visibility = Visibility.Visible;
            }
            else EngChip_Holddowns.Visibility = Visibility.Collapsed;

            // Determine banner state for the currently active item
            string? itemNum    = _currentItem?.Number;
            string? actualValue = _currentItem?.Value?.ToString()?.Trim();
            string? slabValue  = EnergyComplianceService.GetSlabValueForItem(info, itemNum);
            string? slabLabel  = EnergyComplianceService.GetSlabLabelForItem(itemNum);

            // For cable count items, gather F2B (5.1.b) and S2S (5.1.c) from inspection
            int? cableF2B = null, cableR2L = null;
            if ((itemNum == "5.1.b" || itemNum == "5.1.c") && _currentInspection != null)
            {
                var byNum = _currentInspection.Sections
                    .SelectMany(s => s.Items)
                    .GroupBy(i => i.Number ?? "")
                    .ToDictionary(g => g.Key, g => g.First());
                if (byNum.TryGetValue("5.1.b", out var f2bItem) &&
                    int.TryParse(f2bItem.Value?.ToString(), out int f)) cableF2B = f;
                if (byNum.TryGetValue("5.1.c", out var s2sItem) &&
                    int.TryParse(s2sItem.Value?.ToString(), out int r)) cableR2L = r;
            }

            var state = EnergyComplianceService.GetSlabItemBannerState(info, itemNum, actualValue, cableF2B, cableR2L);
            SetSlabBannerBackground(state);

            bool hasCurrentValue = !string.IsNullOrWhiteSpace(actualValue);

            // Item-specific chip
            if (slabValue != null)
            {
                string chipText = slabLabel != null ? $"{slabLabel}: {slabValue}" : slabValue;
                if (state == EnergyComplianceService.BannerState.Red && hasCurrentValue)
                    chipText += $" (actual: {actualValue})";
                EngChip_Item.Text = chipText;
                EngChip_Item.Visibility = Visibility.Visible;

                // Apply button: always visible for mapped items; green+enabled when empty, gray+disabled when filled
                ApplySlabButton.Visibility = Visibility.Visible;
                SetApplyButtonState(ApplySlabButton, !hasCurrentValue);
            }
            else if (itemNum == "5.1.b" || itemNum == "5.1.c")
            {
                // Cable count items: show comparison chip; no Apply (can't split total into F2B/S2S)
                if (info.CableCount.HasValue)
                {
                    bool bothFilled = cableF2B.HasValue && cableR2L.HasValue;
                    int sum = (cableF2B ?? 0) + (cableR2L ?? 0);
                    EngChip_Item.Text = bothFilled
                        ? $"Plan: {info.CableCount} strands  (F2B {cableF2B} + S2S {cableR2L} = {sum})"
                        : $"Plan total: {info.CableCount} strands";
                    EngChip_Item.Visibility = Visibility.Visible;
                }
                else EngChip_Item.Visibility = Visibility.Collapsed;
                ApplySlabButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                EngChip_Item.Visibility = Visibility.Collapsed;
                ApplySlabButton.Visibility = Visibility.Collapsed;
            }

            // Chip foreground matches banner state
            string chipColor = state switch
            {
                EnergyComplianceService.BannerState.Green => "#1B5E20",
                EnergyComplianceService.BannerState.Red   => "#C62828",
                _                                          => "#5D4037",
            };
            var chipBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(chipColor));
            foreach (var chip in new[] { EngChip_Cables, EngChip_Slab, EngChip_Beam, EngChip_Holddowns, EngChip_Item })
                chip.Foreground = chipBrush;

            UpdateClearButton();
        }

        private void SetSlabBannerBackground(EnergyComplianceService.BannerState state)
        {
            (string bg, string border) = state switch
            {
                EnergyComplianceService.BannerState.Green => ("#E8F5E9", "#66BB6A"),
                EnergyComplianceService.BannerState.Red   => ("#FFEBEE", "#EF5350"),
                _                                          => ("#F5F5F5", "#BDBDBD"),
            };
            bool flashRed = state == EnergyComplianceService.BannerState.Red
                            && _slabBannerState != EnergyComplianceService.BannerState.Red;
            _slabBannerState = state;
            if (flashRed)
                FlashBannerRed(SlabCableInfoHeaderBorder, bg, border);
            else
            {
                SlabCableInfoHeaderBorder.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
                SlabCableInfoHeaderBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
            }
        }

        /// Sets an Apply button to green+enabled (canApply=true) or gray+disabled (canApply=false).
        private static void SetApplyButtonState(Button btn, bool canApply)
        {
            btn.IsEnabled   = canApply;
            btn.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(canApply ? "#A5D6A7" : "#E0E0E0"));
            btn.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(canApply ? "#66BB6A" : "#BDBDBD"));
            btn.Foreground  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(canApply ? "#1B5E20" : "#9E9E9E"));
        }

        private void EngDataBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var info = _currentSlabInfo;
            if (info == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"PDF: {info.DisplayName ?? "none"}");
            sb.AppendLine();
            sb.AppendLine($"Strands (cables): {(info.CableCount.HasValue ? info.CableCount.ToString() : "not found")}");
            sb.AppendLine($"Slab thickness:   {(info.SlabThicknessInches.HasValue ? $"{info.SlabThicknessInches}\"" : "not found")}");
            sb.AppendLine($"Beam width:       {(info.BeamWidthInches.HasValue ? $"{info.BeamWidthInches}\"" : "not found")}");
            sb.AppendLine($"Beam depth:       {(info.BeamDepthInches.HasValue ? $"{info.BeamDepthInches}\"" : "not found")}");
            sb.AppendLine($"Holddowns:        {(info.HolddownCount.HasValue ? SlabEngineeringService.FormatHolddownDisplay(info) : "not found")}");
            sb.AppendLine();
            sb.AppendLine($"Status: {info.StatusText}");

            string debugPath = Path.Combine(Path.GetTempPath(), "red_ocr_debug.txt");
            bool hasDebug = info.DebugText?.Contains("red_ocr_debug.txt") == true && File.Exists(debugPath);

            var popup = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = System.Windows.Media.Brushes.White,
            };

            // Declare close state before any lambdas that reference CloseEngPopup
            bool engPopupClosed = false;
            MouseButtonEventHandler? engMainHandler = null;

            void CloseEngPopup()
            {
                if (engPopupClosed) return;
                engPopupClosed = true;
                if (engMainHandler != null) this.PreviewMouseDown -= engMainHandler;
                popup.Close();
                Dispatcher.BeginInvoke(() => { this.Activate(); this.Topmost = true; this.Topmost = false; },
                    System.Windows.Threading.DispatcherPriority.Input);
            }

            var border = new Border
            {
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9A441")),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(16, 12, 16, 12),
            };

            var sp = new StackPanel { MinWidth = 300 };

            sp.Children.Add(new TextBlock
            {
                Text = "Slab Engineering Data",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B5A2B")),
                Margin = new Thickness(0, 0, 0, 8),
            });

            sp.Children.Add(new TextBlock
            {
                Text = sb.ToString(),
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

            if (hasDebug)
            {
                var link = new TextBlock
                {
                    Text = "📄 Open raw OCR text",
                    Foreground = System.Windows.Media.Brushes.DodgerBlue,
                    TextDecorations = TextDecorations.Underline,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 8, 0, 0),
                    FontSize = 11,
                };
                // MouseLeftButtonDown (bubble) fires target→root, so link handles it first
                // and marks Handled=true before the popup-level handler sees it.
                link.MouseLeftButtonDown += (s, ev) => ev.Handled = true;
                link.MouseLeftButtonUp += (s, ev) =>
                {
                    ev.Handled = true;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = debugPath, UseShellExecute = true }); }
                    catch { }
                    CloseEngPopup();
                };
                sp.Children.Add(link);
            }

            border.Child = sp;
            popup.Content = border;

            engMainHandler = (s, ev) => CloseEngPopup();
            this.PreviewMouseDown += engMainHandler;

            popup.Deactivated += (s, ev) => CloseEngPopup();
            popup.MouseLeftButtonDown += (s, ev) => { if (!ev.Handled) CloseEngPopup(); };

            popup.Show();
        }

        // ---------------------------------------------------------------
        // EC (Energy Compliance) banner
        // ---------------------------------------------------------------

        private void RefreshEcDataPanel()
        {
            var info = _currentEcInfo;

            // Show banner whenever we have an EC info object — even if data extraction failed,
            // so the user can click it and see the status/error message.
            EcDataHeaderBorder.Visibility = info != null ? Visibility.Visible : Visibility.Collapsed;
            if (info == null) return;

            void Show(System.Windows.Controls.TextBlock chip, string? text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                { chip.Text = text; chip.Visibility = Visibility.Visible; }
                else chip.Visibility = Visibility.Collapsed;
            }
            void HideAll()
            {
                EcChip_Hers.Visibility = EcChip_BlowerDoor.Visibility = EcChip_DuctLeak.Visibility =
                EcChip_FreshAir.Visibility = EcChip_Seer.Visibility = Visibility.Collapsed;
                ApplyEcButton.Visibility = Visibility.Collapsed;
            }

            if (info.IsLoaded)
            {
                HideAll();

                string? inspCode    = _currentInspection?.InspectionCode;
                string? itemNum     = _currentItem?.Number;
                string? ecValue     = EnergyComplianceService.GetValueForItem(info, inspCode, itemNum);
                string? ecLabel     = EnergyComplianceService.GetLabelForItem(inspCode, itemNum);
                string? actualValue = _currentItem?.Value?.ToString()?.Trim();

                var state = EnergyComplianceService.GetEcItemBannerState(info, inspCode, itemNum, actualValue);
                ApplyEcBannerColors(state);

                if (ecValue != null)
                {
                    bool hasCurrentValue = !string.IsNullOrWhiteSpace(actualValue);
                    string chipText = ecLabel != null ? $"{ecLabel}: {ecValue}" : ecValue;
                    if (state == EnergyComplianceService.BannerState.Red && hasCurrentValue)
                        chipText += $" (actual: {actualValue})";
                    EcChip_Hers.Foreground = EcDataHeaderBorder.BorderBrush; // match banner border
                    Show(EcChip_Hers, chipText);

                    // Apply button: always visible for mapped items; green+enabled when empty, gray+disabled when filled
                    ApplyEcButton.Visibility = Visibility.Visible;
                    SetApplyButtonState(ApplyEcButton, !hasCurrentValue);
                }
                // No chip / no Apply button when the active item has no EC mapping
            }
            else
            {
                // Amber banner — EC report found/attempted but data not extracted; tap for details
                EcDataHeaderBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF8E1"));
                EcDataHeaderBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F9A825"));
                HideAll();
                EcChip_Hers.Text = $"⚠ EC: {info.StatusText ?? "tap for details"}";
                EcChip_Hers.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7B5800"));
                EcChip_Hers.Visibility = Visibility.Visible;
            }

            // EC data loads asynchronously — update clear button once we know the EC state
            UpdateClearButton();
        }

        private void ApplyEcBannerColors(EnergyComplianceService.BannerState state)
        {
            (string bg, string border) = state switch
            {
                EnergyComplianceService.BannerState.Green => ("#E8F5E9", "#66BB6A"),
                EnergyComplianceService.BannerState.Red   => ("#FFEBEE", "#EF5350"),
                _                                          => ("#F5F5F5", "#BDBDBD"),
            };
            bool flashRed = state == EnergyComplianceService.BannerState.Red
                            && _ecBannerState != EnergyComplianceService.BannerState.Red;
            _ecBannerState = state;
            if (flashRed)
                FlashBannerRed(EcDataHeaderBorder, bg, border);
            else
            {
                EcDataHeaderBorder.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
                EcDataHeaderBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
            }
        }

        /// Strobes the given Border 5 times red/white then settles on red — catches attention on mismatch.
        private void FlashBannerRed(System.Windows.Controls.Border banner, string redBg, string redBorder)
        {
            int count = 0;
            var timer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(160) };
            timer.Tick += (s, e) =>
            {
                bool showRed = (count % 2 == 0);
                banner.Background  = new SolidColorBrush(showRed
                    ? (Color)ColorConverter.ConvertFromString(redBg)
                    : Colors.White);
                banner.BorderBrush = new SolidColorBrush(showRed
                    ? (Color)ColorConverter.ConvertFromString(redBorder)
                    : Colors.LightGray);
                count++;
                if (count >= 10) // 5 full on/off cycles (0-9)
                {
                    timer.Stop();
                    banner.Background  = new SolidColorBrush((Color)ColorConverter.ConvertFromString(redBg));
                    banner.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(redBorder));
                }
            };
            timer.Start();
        }

        private void EcDataBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var info = _currentEcInfo;
            if (info == null) return;

            string wallLine = info.WallRDetails ?? info.WallR ?? "not found";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"PDF: {info.DisplayName ?? "none"}");
            sb.AppendLine();
            sb.AppendLine($"HERS Index:        {info.HersIndex ?? "not found"}");
            sb.AppendLine($"Floor Area:        {info.ConditionedFloorArea ?? "not found"} sq ft");
            sb.AppendLine($"Volume:            {info.ConditionedVolume ?? "not found"} cu ft");
            sb.AppendLine($"Bedrooms:          {info.NumberOfBedrooms ?? "not found"}");
            sb.AppendLine();
            // Blower door — show 90% threshold; if derived show both ES 3.1 (5 ACH) and ES 3.2 (4.5 ACH) targets
            string? bdCfm = info.EffectiveBlowerDoorCfm;
            if (bdCfm != null && int.TryParse(bdCfm, out int bdVal))
            {
                int bd90 = (int)(bdVal * 0.9);
                if (info.BlowerDoorMaxCfm == null)
                {
                    // Derived — show both targets
                    string es32Note = "";
                    if (info.BlowerDoorDerivedCfmEs32 != null && int.TryParse(info.BlowerDoorDerivedCfmEs32, out int es32Val))
                        es32Note = $"  |  ES v3.2 (4.5 ACH): {es32Val} CFM · 90% = {(int)(es32Val * 0.9)} CFM";
                    sb.AppendLine($"Blower Door:       {bdCfm} CFM @ 50 Pa  (ES 3.1/code 5 ACH · 90% = {bd90} CFM{es32Note})");
                }
                else
                {
                    // Found in EC report — show 90% + both derived ACH targets for reference
                    string es31Note = "";
                    string es32Note = "";
                    if (info.BlowerDoorDerivedCfm == null && info.ConditionedVolume != null)
                    {
                        // Volume available — compute both derived targets for reference
                        if (double.TryParse(info.ConditionedVolume.Replace(",", ""), out double vol) && vol > 0)
                        {
                            int es31 = (int)Math.Round(vol * 5.0 / 60.0);
                            int es32 = (int)Math.Round(vol * 4.5 / 60.0);
                            es31Note = $"  |  5 ACH (ES 3.1): {es31} CFM";
                            es32Note = $"  |  4.5 ACH (ES 3.2): {es32} CFM";
                        }
                    }
                    else if (info.BlowerDoorDerivedCfmEs32 != null && int.TryParse(info.BlowerDoorDerivedCfmEs32, out int es32Val))
                    {
                        es32Note = $"  |  ES v3.2 target: {es32Val} CFM";
                    }
                    sb.AppendLine($"Blower Door:       {bdCfm} CFM @ 50 Pa  (90% = {bd90} CFM{es31Note}{es32Note})");
                }
            }
            else
            {
                sb.AppendLine("Blower Door:       not found  CFM @ 50 Pa");
            }
            // Duct leakage — flag if derived from area
            string? dlCfm = info.EffectiveDuctLeakageCfm;
            string dlNote = (dlCfm != null && info.DuctLeakageMaxCfm == null) ? " (est. from area)" : "";
            sb.AppendLine($"Duct Leakage:      {dlCfm ?? "not found"} CFM @ 25 Pa{dlNote}");
            sb.AppendLine($"Returns:           {info.NumberOfReturns ?? "not found"}");
            sb.AppendLine($"Supply Duct R:     {info.SupplyDuctR ?? "not found"}");
            sb.AppendLine($"Return Duct R:     {info.ReturnDuctR ?? "not found"}");
            sb.AppendLine();
            sb.AppendLine($"Window U-factor:   {info.WindowUFactor ?? "not found"}");
            sb.AppendLine($"Window SHGC:       {info.WindowSHGC ?? "not found"}");
            sb.AppendLine($"Wall R:            {wallLine}");
            sb.AppendLine($"Sloped Ceiling R:  {info.SlopedCeilingR ?? "not found"}");
            sb.AppendLine($"Attic Ceiling R:   {info.AtticCeilingR ?? "not found"}");
            sb.AppendLine($"Attic Wall R:      {info.AtticWallR ?? "not found"}");
            sb.AppendLine($"Attic Roof R:      {info.AtticRoofR ?? "not found"}");
            sb.AppendLine($"HW Pipe R:         {info.HotWaterPipeR ?? "not found"}");
            sb.AppendLine();
            sb.AppendLine($"Cooling SEER:      {info.HvacCoolingSeer ?? "not found"}");
            sb.AppendLine($"Tonnage:           {info.HvacTonnage ?? "not found"} tons");
            sb.AppendLine($"Design Airflow:    {info.DesignAirflowCfm ?? "not found"} CFM");
            sb.AppendLine();
            sb.AppendLine($"Fresh Air:         {info.TargetFreshAirCfm ?? "not found"} CFM");
            sb.AppendLine($"Run Time:          {info.TargetRunTime ?? "not found"} hrs/day");
            sb.AppendLine($"Fan Watts:         {info.VentFanWatts ?? "not found"}");
            sb.AppendLine();
            sb.AppendLine($"Water Heater:      {info.WaterHeaterFuel ?? ""} {info.WaterHeaterCapacity ?? ""}".Trim());
            sb.AppendLine($"Energy Star:       {info.EnergyStarProgram ?? "not found"}");
            if (info.IECCVersion != null)
                sb.AppendLine($"IECC Code:         {info.IECCVersion}");
            sb.AppendLine();
            sb.AppendLine($"Status: {info.StatusText}");

            // Build a non-modal popup that closes on any click anywhere
            string debugPath = EnergyComplianceService.DebugTextPath;
            bool hasDebug = File.Exists(debugPath);

            var popup = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = System.Windows.Media.Brushes.White,
            };

            var border = new Border
            {
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B0000")),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(16, 12, 16, 12),
            };

            var sp = new StackPanel { MinWidth = 340 };

            sp.Children.Add(new TextBlock
            {
                Text = "Energy Compliance Data",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B0000")),
                Margin = new Thickness(0, 0, 0, 8),
            });

            sp.Children.Add(new TextBlock
            {
                Text = sb.ToString(),
                FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New"),
                FontSize = 11,
                TextWrapping = TextWrapping.NoWrap,
            });

            sp.Children.Add(new TextBlock
            {
                Text = "click anywhere to close",
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            });

            border.Child = sp;
            popup.Content = border;

            // Guard against double-close: Deactivated can fire as a side-effect of Close(),
            // and PreviewMouseDown can race with Deactivated on a tap.
            bool popupClosed = false;
            MouseButtonEventHandler? mainWindowHandler = null;

            void ClosePopup()
            {
                if (popupClosed) return;
                popupClosed = true;
                if (mainWindowHandler != null) this.PreviewMouseDown -= mainWindowHandler;
                popup.Close();
                // Defer so the close sequence settles before we re-assert focus.
                // Also do the Topmost flip to guarantee z-order on tablet.
                Dispatcher.BeginInvoke(() => { this.Activate(); this.Topmost = true; this.Topmost = false; },
                    System.Windows.Threading.DispatcherPriority.Input);
            }

            // Tapping on the main RED window dismisses the popup (fires before Deactivated)
            mainWindowHandler = (s, ev) => ClosePopup();
            this.PreviewMouseDown += mainWindowHandler;

            popup.Deactivated += (s, ev) => ClosePopup();
            popup.MouseLeftButtonDown += (s, ev) => { if (!ev.Handled) ClosePopup(); };

            if (hasDebug)
            {
                var link = new TextBlock
                {
                    Text = "📄 Open raw PDF text",
                    Foreground = System.Windows.Media.Brushes.DodgerBlue,
                    TextDecorations = TextDecorations.Underline,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 8, 0, 0),
                    FontSize = 11,
                };
                // MouseLeftButtonDown (bubble) fires target→root, so link handles it first
                link.MouseLeftButtonDown += (s, ev) => ev.Handled = true;
                link.MouseLeftButtonUp += (s, ev) =>
                {
                    ev.Handled = true;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = debugPath, UseShellExecute = true }); }
                    catch { }
                    ClosePopup();
                };
                sp.Children.Add(link);
            }

            popup.Show();
        }

        private void ApplyEcButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEcInfo == null || _currentItem == null || _currentInspection == null) return;
            bool applied = EnergyComplianceService.ApplySingleItem(_currentEcInfo, _currentItem, _currentInspection.InspectionCode);
            if (applied)
            {
                _hasUnsavedChanges = true;
                LoadStatusControls(_currentItem);
                UpdateSlabCableInfoBanner(_currentItem); // refresh banner + button states
                PopulateTreeView();
            }
        }

        private void ApplySlabButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSlabInfo == null || _currentItem == null || _currentInspection == null) return;
            bool applied = EnergyComplianceService.ApplySlabToSingleItem(_currentSlabInfo, _currentItem);
            if (applied)
            {
                _hasUnsavedChanges = true;
                LoadStatusControls(_currentItem);
                UpdateSlabCableInfoBanner(_currentItem); // refresh banner + button states
                PopulateTreeView();
            }
        }

        private void UpdateDesignExtractionButton()
        {
            if (DesignExtractionButton == null || DesignExtractionButtonText == null)
                return;

            bool hasInspection = _currentInspection != null;
            if (!hasInspection)
            {
                DesignExtractionButton.Visibility = Visibility.Collapsed;
                return;
            }

            bool hasCompletedSource =
                (!string.IsNullOrWhiteSpace(_currentSlabInfo?.PdfPath) && File.Exists(_currentSlabInfo.PdfPath)) ||
                (!string.IsNullOrWhiteSpace(_currentEcInfo?.PdfPath) && File.Exists(_currentEcInfo.PdfPath)) ||
                (!string.IsNullOrWhiteSpace(_planPdfPath) && File.Exists(_planPdfPath));

            if (_designExtractionLoading)
            {
                DesignExtractionButton.Visibility = Visibility.Visible;
                DesignExtractionButton.IsEnabled = false;
                DesignExtractionButton.Opacity = 0.55;
                DesignExtractionButtonText.Text = "Design Extraction...";
                DesignExtractionButton.ToolTip = "Design data extraction is running in the background.";
                return;
            }

            if (!hasCompletedSource)
            {
                DesignExtractionButton.Visibility = Visibility.Collapsed;
                DesignExtractionButton.IsEnabled = false;
                DesignExtractionButton.Opacity = 1.0;
                return;
            }

            DesignExtractionButton.Visibility = Visibility.Visible;
            DesignExtractionButton.IsEnabled = true;
            DesignExtractionButton.Opacity = 1.0;

            int revision = GetDesignExtractionRevision();
            DesignExtractionButtonText.Text = $"Design Extraction R{revision}";

            string source = _currentSlabInfo != null
                ? (_currentSlabInfo.DisplayName ?? "slab engineering")
                : _currentEcInfo != null
                    ? (_currentEcInfo.DisplayName ?? "energy compliance")
                    : !string.IsNullOrWhiteSpace(_planPdfPath)
                        ? Path.GetFileName(_planPdfPath)
                        : "waiting for design extraction";
            DesignExtractionButton.ToolTip = $"Show extracted design data from {source}";
        }

        private int GetDesignExtractionRevision()
        {
            if (_currentSlabInfo?.RevisionOptions != null && !string.IsNullOrWhiteSpace(_currentSlabInfo.PdfPath))
            {
                var selected = _currentSlabInfo.RevisionOptions
                    .FirstOrDefault(option => string.Equals(option.FullPath, _currentSlabInfo.PdfPath, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                    return Math.Max(0, selected.Revision);
            }

            string? path = _currentEcInfo?.PdfPath ?? _planPdfPath;
            return GetRevisionFromDesignPath(path);
        }

        private static string GetSlabEngineeringButtonLabel(SlabEngineeringInfo? info)
        {
            int revision = 0;
            if (info?.RevisionOptions != null && !string.IsNullOrWhiteSpace(info.PdfPath))
            {
                var selected = info.RevisionOptions
                    .FirstOrDefault(option => string.Equals(option.FullPath, info.PdfPath, StringComparison.OrdinalIgnoreCase));
                revision = selected != null ? Math.Max(0, selected.Revision) : GetRevisionFromDesignPath(info.PdfPath);
            }
            else if (!string.IsNullOrWhiteSpace(info?.PdfPath))
            {
                revision = GetRevisionFromDesignPath(info.PdfPath);
            }

            return $"Slab Eng R{revision}";
        }

        private static int GetRevisionFromDesignPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return 0;

            string name = Path.GetFileNameWithoutExtension(path);
            var match = Regex.Match(name, @"(?:^|[^A-Za-z])R(?:EV(?:ISION)?)?\s*[-_. ]?(\d+)\b", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int revision))
                return Math.Max(0, revision);

            match = Regex.Match(name, @"\bREV(?:ISION)?\s*[-_. ]?(\d+)\b", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out revision)
                ? Math.Max(0, revision)
                : 0;
        }

        private void DesignExtractionButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (_currentSlabInfo != null)
            {
                EngDataBanner_Click(sender, e);
                return;
            }

            if (_currentEcInfo != null)
            {
                EcDataBanner_Click(sender, e);
                return;
            }

            ShowDesignExtractionFallbackPopup();
        }

        private void ShowDesignExtractionFallbackPopup()
        {
            string text = !string.IsNullOrWhiteSpace(_planPdfPath)
                ? $"PDF: {Path.GetFileName(_planPdfPath)}\n\nNo parsed extraction fields are available for this design file yet."
                : "Design extraction is still loading or no design PDF was found.";

            var popup = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = Brushes.White
            };

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(15, 118, 110)),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(16, 12, 16, 12),
                Child = new TextBlock
                {
                    Text = text + "\n\nclick anywhere to close",
                    FontFamily = new FontFamily("Consolas, Courier New"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MinWidth = 320
                }
            };
            popup.Content = border;

            bool closed = false;
            MouseButtonEventHandler? mainWindowHandler = null;
            void ClosePopup()
            {
                if (closed) return;
                closed = true;
                if (mainWindowHandler != null) PreviewMouseDown -= mainWindowHandler;
                popup.Close();
                Dispatcher.BeginInvoke(() => { Activate(); Topmost = true; Topmost = false; },
                    System.Windows.Threading.DispatcherPriority.Input);
            }

            mainWindowHandler = (s, ev) => ClosePopup();
            PreviewMouseDown += mainWindowHandler;
            popup.Deactivated += (s, ev) => ClosePopup();
            popup.MouseLeftButtonDown += (s, ev) => ClosePopup();
            popup.Show();
        }

        private void InlineEditorToggleButton_Click(object sender, RoutedEventArgs e)
        {
            AutoApplyCurrentItem();
            _inlineEditorMode = !_inlineEditorMode;
            ApplyChecklistEditorMode();
            SavePreferences();
            PopulateTreeView(SearchFilterBox.Text);
        }

        private void ApplyChecklistEditorMode()
        {
            if (SectionsTreeView == null || InlineChecklistPanel == null || InlineEditorToggleButton == null)
                return;

            SectionsTreeView.Visibility = _inlineEditorMode ? Visibility.Collapsed : Visibility.Visible;
            InlineChecklistPanel.Visibility = _inlineEditorMode ? Visibility.Visible : Visibility.Collapsed;
            if (_inlineEditorMode && LeftColumn.ActualWidth >= 250 && LeftColumn.ActualWidth <= 900)
                _classicLeftPanelWidth = LeftColumn.ActualWidth;
            ChecklistColumnGrid.SetValue(Grid.ColumnSpanProperty, _inlineEditorMode ? 3 : 1);
            ChecklistGridSplitter.Visibility = _inlineEditorMode ? Visibility.Collapsed : Visibility.Visible;
            EditorScrollViewer.Visibility = _inlineEditorMode ? Visibility.Collapsed : Visibility.Visible;
            LeftColumn.MaxWidth = _inlineEditorMode ? double.PositiveInfinity : 900;
            LeftColumn.Width = _inlineEditorMode
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(_classicLeftPanelWidth);
            InlineEditorToggleButton.Content = _inlineEditorMode ? "Classic" : "v2.0 UI";
            InlineEditorToggleButton.Background = _inlineEditorMode
                ? new SolidColorBrush(Color.FromRgb(139, 0, 0))
                : new SolidColorBrush(Color.FromRgb(37, 99, 235));
            InlineEditorToggleButton.Foreground = _inlineEditorMode
                ? Brushes.White
                : Brushes.White;
        }

        private void PopulateInlineChecklist(string? filter = null)
        {
            InlineChecklistPanel.Children.Clear();
            if (!_inlineEditorMode || _currentInspection?.Sections == null)
                return;

            bool isSCI = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);
            bool hasAnyRows = false;

            foreach (var section in _currentInspection.Sections)
            {
                var visibleItems = section.Items
                    .Where(item => ShouldShowInlineItem(item, filter, isSCI))
                    .ToList();

                if (visibleItems.Count == 0)
                    continue;

                InlineChecklistPanel.Children.Add(CreateInlineSectionHeader(section, visibleItems.Count));
                hasAnyRows = true;

                if (!_collapsedInlineSectionKeys.Contains(GetInlineSectionKey(section)))
                {
                    foreach (var item in visibleItems)
                        InlineChecklistPanel.Children.Add(CreateInlineItemRow(section, item));
                }
            }

            if (!hasAnyRows)
            {
                InlineChecklistPanel.Children.Add(new TextBlock
                {
                    Text = "No checklist items match the current filters.",
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    FontSize = _checklistFontSize,
                    Margin = new Thickness(8, 16, 8, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        private bool ShouldShowInlineItem(Item item, string? filter, bool isSCI)
        {
            if (!isSCI && IsProperNameItem(item))
            {
                if (_ofiFilterActive || _reqFilterActive || _incFilterActive)
                    return false;

                if (string.IsNullOrWhiteSpace(filter))
                    return true;

                string filterLower = filter.ToLower();
                return item.Name?.ToLower().Contains(filterLower) == true ||
                       item.Number?.ToLower().Contains(filterLower) == true ||
                       item.Value?.ToString()?.ToLower().Contains(filterLower) == true;
            }

            if (item.HidePicturesButton && item.HideCommentsButton)
                return false;

            if (!string.IsNullOrWhiteSpace(filter))
            {
                string filterLower = filter.ToLower();
                bool matchesFilter =
                    item.Name?.ToLower().Contains(filterLower) == true ||
                    item.Number?.ToLower().Contains(filterLower) == true ||
                    item.Comments?.ToLower().Contains(filterLower) == true ||
                    item.Value?.ToString()?.ToLower().Contains(filterLower) == true;

                if (!matchesFilter)
                    return false;
            }

            if (_ofiFilterActive)
            {
                string valueStr = item.Value?.ToString()?.ToLower() ?? "";
                bool isFail = valueStr.Contains("fail") ||
                              valueStr == "no" ||
                              valueStr == "x" ||
                              valueStr.Contains("defect") ||
                              valueStr.Contains("deficient");
                if (!isFail)
                    return false;
            }

            if (_reqFilterActive && !item.Required && !item.IsPictureRequired)
                return false;

            if (_incFilterActive)
            {
                bool valueIsMissing = string.IsNullOrEmpty(item.Value?.ToString()?.Trim() ?? "");
                bool picturesAreMissing = item.Pictures.Count == 0;
                if (!((item.Required && valueIsMissing) || (item.IsPictureRequired && picturesAreMissing)))
                    return false;
            }

            return true;
        }

        private Border CreateInlineSectionHeader(Section section, int visibleCount)
        {
            bool isCollapsed = _collapsedInlineSectionKeys.Contains(GetInlineSectionKey(section));
            bool sectionHasFail = SectionHasFailValue(section);
            var panel = new DockPanel { LastChildFill = true };

            if (section.Items.Any(item => (item.ControlName ?? "").Contains("passfail", StringComparison.OrdinalIgnoreCase)))
            {
                var sectionActions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                DockPanel.SetDock(sectionActions, Dock.Right);

                var niButton = CreateInlineSectionFillButton("NI", section, new SolidColorBrush(Color.FromRgb(126, 140, 158)));
                ConfigureSectionFillButtonState(niButton, sectionHasFail);
                niButton.Click += SectionNiButton_Click;
                sectionActions.Children.Add(niButton);

                var passButton = CreateInlineSectionFillButton("Pass", section, new SolidColorBrush(Color.FromRgb(55, 180, 98)));
                ConfigureSectionFillButtonState(passButton, sectionHasFail);
                passButton.Click += SectionPassButton_Click;
                sectionActions.Children.Add(passButton);

                panel.Children.Add(sectionActions);
            }

            panel.Children.Add(new TextBlock
            {
                Text = isCollapsed ? ">" : "v",
                FontSize = _checklistFontSize + 1,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"{section.Number} - {section.Name}  ({visibleCount})",
                FontSize = _checklistFontSize,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });

            var border = new Border
            {
                Tag = section,
                Background = new SolidColorBrush(Color.FromRgb(221, 228, 234)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(190, 201, 213)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 8, 0, 4),
                Cursor = Cursors.Hand,
                Child = panel
            };
            border.MouseLeftButtonUp += InlineSectionHeader_MouseLeftButtonUp;
            StretchInlineWidth(border);
            return border;
        }

        private Button CreateInlineSectionFillButton(string text, Section section, Brush background)
        {
            return new Button
            {
                Content = text,
                Tag = section,
                MinWidth = 76,
                MinHeight = 40,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 0, 0, 0),
                Background = background,
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderThickness = new Thickness(4),
                FontSize = Math.Max(11, _checklistFontSize - 1),
                FontWeight = FontWeights.ExtraBold,
                FocusVisualStyle = null,
                Style = CreateInlineStatusButtonStyle(),
                ToolTip = $"Bulk set this category to {text}"
            };
        }

        private void ConfigureSectionFillButtonState(Button button, bool sectionHasFail)
        {
            if (!sectionHasFail)
                return;

            button.IsEnabled = false;
            button.Opacity = 0.45;
            button.ToolTip = "Disabled because this category contains a failed item.";
        }

        private string GetInlineSectionKey(Section section)
        {
            return $"{section.Number}|{section.Name}".ToLowerInvariant();
        }

        private void InlineSectionHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_scrollStarted)
                return;

            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null)
                return;

            if (sender is not FrameworkElement { Tag: Section section })
                return;

            string key = GetInlineSectionKey(section);
            if (!_collapsedInlineSectionKeys.Add(key))
                _collapsedInlineSectionKeys.Remove(key);

            PopulateInlineChecklist(SearchFilterBox.Text);
            e.Handled = true;
        }

        private Border CreateInlineItemRow(Section section, Item item)
        {
            bool isExpanded = IsInlineItemExpanded(item);
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(CreateInlineItemHeader(section, item, isExpanded));
            if (isExpanded)
                panel.Children.Add(CreateInlineDrawerHost(item));

            var row = new Border
            {
                Tag = item,
                Background = isExpanded ? new SolidColorBrush(Color.FromRgb(247, 250, 252)) : Brushes.White,
                BorderBrush = isExpanded ? new SolidColorBrush(Color.FromRgb(120, 174, 205)) : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderThickness = isExpanded ? new Thickness(2) : new Thickness(0, 0, 0, 1),
                Margin = new Thickness(0, 0, 0, 4),
                Child = panel,
                Cursor = Cursors.Hand
            };
            row.MouseLeftButtonUp += InlineItemRow_MouseLeftButtonUp;
            StretchInlineWidth(row);
            return row;
        }

        private bool IsInlineItemExpanded(Item item)
        {
            if (ReferenceEquals(_expandedInlineItemInstance, item))
                return true;

            return _expandedInlineItemInstance == null &&
                   !string.IsNullOrWhiteSpace(_expandedInlineItemKey) &&
                   _expandedInlineItemKey == GetInlineItemKey(item);
        }

        private Grid CreateInlineItemHeader(Section section, Item item, bool isExpanded)
        {
            var grid = new Grid
            {
                MinHeight = 54,
                Background = isExpanded ? new SolidColorBrush(Color.FromRgb(235, 243, 248)) : Brushes.Transparent
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hudIcon = CreateInlineStatHudIcon(item);
            Grid.SetColumn(hudIcon, 0);
            grid.Children.Add(hudIcon);

            var chevron = new TextBlock
            {
                Text = isExpanded ? "v" : ">",
                FontSize = _checklistFontSize + 2,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0)
            };
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(chevron);

            var numberBadge = CreateInlineBadge(item.Number ?? "", GetStatusBrush(item), Brushes.White, FontWeights.Bold);
            numberBadge.Margin = new Thickness(0, 8, 8, 8);
            Grid.SetColumn(numberBadge, 2);
            grid.Children.Add(numberBadge);

            var prompt = new TextBlock
            {
                Text = ExpandAbbreviations(item.Name ?? ""),
                FontSize = _checklistFontSize + 2,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 8, 12, 8)
            };
            Grid.SetColumn(prompt, 3);
            grid.Children.Add(prompt);

            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 10, 6) };
            string value = item.Value?.ToString() ?? "";
            if (item.Pictures.Count > 0)
                chips.Children.Add(CreateInlineBadge($"Photo {item.Pictures.Count}", new SolidColorBrush(Color.FromRgb(37, 99, 235)), Brushes.White, FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(item.Comments))
                chips.Children.Add(CreateInlineBadge("Comment", new SolidColorBrush(Color.FromRgb(88, 80, 141)), Brushes.White, FontWeights.SemiBold));
            if ((item.Required && string.IsNullOrWhiteSpace(value)) || (item.IsPictureRequired && item.Pictures.Count == 0))
                chips.Children.Add(CreateInlineBadge("Required", new SolidColorBrush(Color.FromRgb(139, 0, 0)), Brushes.White, FontWeights.SemiBold));
            var designChip = CreateInlineDesignAssistChip(item);
            if (designChip != null)
                chips.Children.Add(designChip);
            Grid.SetColumn(chips, 4);
            grid.Children.Add(chips);

            var statusControl = CreateInlineStatusHeaderControl(item);
            Grid.SetColumn(statusControl, 5);
            grid.Children.Add(statusControl);

            return grid;
        }

        private Border CreateInlineStatHudIcon(Item item)
        {
            var stats = _statsService.GetItemStats(_currentInspectorName, _currentInspectionCode, item.Number);
            string icon = "";
            bool isStrength = false;
            bool isBlindSpot = false;

            if (stats != null)
            {
                if (stats.Deviation >= 100)
                {
                    icon = "★";
                    isStrength = true;
                }
                else if (stats.Deviation >= 10)
                {
                    icon = "☺";
                    isStrength = true;
                }
                else if (stats.Deviation <= -80)
                {
                    icon = "↓↓↓";
                    isBlindSpot = true;
                }
                else if (stats.Deviation <= -60)
                {
                    icon = "↓↓";
                    isBlindSpot = true;
                }
                else if (stats.Deviation <= -40)
                {
                    icon = "↓";
                    isBlindSpot = true;
                }

                if (isBlindSpot && stats.TeamRate <= 3.0)
                {
                    icon = "○";
                    isBlindSpot = false;
                }
            }

            var border = new Border
            {
                Width = 42,
                MinHeight = 28,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(2, 1, 2, 1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = stats != null && !string.IsNullOrEmpty(icon) ? GetDeviationTooltip(stats) : null
            };

            if (isBlindSpot)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(255, 230, 230));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60));
                border.BorderThickness = new Thickness(1.5);
                border.CornerRadius = new CornerRadius(3);
            }
            else if (isStrength)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(255, 215, 0));
                border.CornerRadius = new CornerRadius(3);
            }

            border.Child = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = Math.Max(12, _checklistFontSize - 1),
                FontWeight = FontWeights.Bold,
                Foreground = isBlindSpot
                    ? new SolidColorBrush(Color.FromRgb(139, 0, 0))
                    : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };

            return border;
        }

        private UIElement CreateInlineStatusHeaderControl(Item item)
        {
            string controlName = item.ControlName?.ToLower() ?? "";
            string[]? options = GetInlineStatusOptions(item, controlName);
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = "InlinePrefixSuffixSwipeZone"
            };

            if (HasInlineValueChoiceList(item, options))
            {
                var valueButton = new Button
                {
                    Content = string.IsNullOrWhiteSpace(item.Value?.ToString()) ? "" : item.Value?.ToString(),
                    Tag = item,
                    MinWidth = 78,
                    MaxWidth = 220,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    Background = string.IsNullOrWhiteSpace(item.Value?.ToString())
                        ? Brushes.White
                        : GetStatusBrushForValue(item.Value?.ToString() ?? ""),
                    Foreground = string.IsNullOrWhiteSpace(item.Value?.ToString()) ? new SolidColorBrush(Color.FromRgb(15, 23, 42)) : Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    FontSize = Math.Max(11, _checklistFontSize - 1),
                    FontWeight = FontWeights.SemiBold,
                    ToolTip = "Tap to show value choices"
                };
                valueButton.Click += InlineValueChoiceButton_Click;
                panel.Children.Add(valueButton);
                AddInlineClearValueButton(panel, item);
            }
            else if (options != null)
            {
                foreach (string option in OrderInlineQuickStatusOptions(options))
                {
                    bool selected = option.Equals(item.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                    var btn = new Button
                    {
                        Content = option,
                        Tag = new InlineValueAction(item, option),
                        Padding = new Thickness(9, 5, 9, 5),
                        Margin = new Thickness(0, 0, 5, 0),
                        Background = selected ? GetStatusBrushForValue(option) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                        Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                        FontSize = Math.Max(11, _checklistFontSize - 1),
                        FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                        FocusVisualStyle = null,
                        Style = CreateInlineStatusButtonStyle()
                    };
                    btn.Click += InlineStatusButton_Click;
                    panel.Children.Add(btn);
                }
            }

            if (options == null)
            {
                AddInlineClearValueButton(panel, item);
                var valueBox = new TextBox
                {
                    Text = item.Value?.ToString() ?? "",
                    Tag = item,
                    MinWidth = 150,
                    MaxWidth = 220,
                    MinHeight = 34,
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    BorderThickness = new Thickness(1),
                    FontSize = Math.Max(11, _checklistFontSize - 1),
                    FontWeight = string.IsNullOrWhiteSpace(item.Value?.ToString()) ? FontWeights.Normal : FontWeights.SemiBold,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = "Type a value for this item"
                };
                valueBox.SetValue(InlineValueDisplayProperty, true);
                valueBox.GotKeyboardFocus += InlineValueBox_GotKeyboardFocus;
                valueBox.LostFocus += InlineValueBox_LostFocus;
                valueBox.MouseLeftButtonUp += (_, e) => e.Handled = true;
                panel.Children.Add(valueBox);
            }

            var scroller = new ScrollViewer
            {
                Content = panel,
                MaxWidth = HasInlineValueChoiceList(item, options) ? 260 : 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.HorizontalOnly,
                CanContentScroll = false,
                Tag = "InlinePrefixSuffixSwipeZone",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 6)
            };
            scroller.PreviewMouseLeftButtonDown += InlineHorizontalScrollViewer_PreviewMouseLeftButtonDown;
            scroller.PreviewMouseMove += InlineHorizontalScrollViewer_PreviewMouseMove;
            scroller.PreviewMouseLeftButtonUp += InlineHorizontalScrollViewer_PreviewMouseLeftButtonUp;
            scroller.PreviewTouchDown += InlineHorizontalScrollViewer_PreviewTouchDown;
            scroller.PreviewTouchMove += InlineHorizontalScrollViewer_PreviewTouchMove;
            scroller.PreviewTouchUp += InlineHorizontalScrollViewer_PreviewTouchUp;
            scroller.MouseLeave += InlineHorizontalScrollViewer_MouseLeave;
            return scroller;
        }

        private void AddInlineClearValueButton(Panel panel, Item item)
        {
            if (string.IsNullOrWhiteSpace(item.Value?.ToString()))
                return;

            var clearButton = new Button
            {
                Content = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Tag = item,
                Width = 34,
                MinHeight = 34,
                Padding = new Thickness(5),
                Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
                Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(248, 180, 180)),
                ToolTip = "Clear value"
            };
            clearButton.Click += InlineClearValueButton_Click;
            panel.Children.Add(clearButton);
        }

        private UIElement? CreateInlineDesignAssistChip(Item item)
        {
            var assist = GetInlineDesignAssist(item);
            if (assist == null)
                return null;

            var brush = assist.State switch
            {
                EnergyComplianceService.BannerState.Green => new SolidColorBrush(Color.FromRgb(191, 255, 0)),
                EnergyComplianceService.BannerState.Red => new SolidColorBrush(Color.FromRgb(255, 77, 196)),
                _ => new SolidColorBrush(Color.FromRgb(241, 245, 249))
            };
            var foreground = assist.State switch
            {
                EnergyComplianceService.BannerState.Green => new SolidColorBrush(Color.FromRgb(20, 83, 45)),
                EnergyComplianceService.BannerState.Red => Brushes.White,
                _ => new SolidColorBrush(Color.FromRgb(51, 65, 85))
            };

            var button = new Button
            {
                Content = assist.Text,
                Tag = assist,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(3, 0, 0, 3),
                Background = brush,
                Foreground = foreground,
                BorderBrush = foreground,
                FontSize = Math.Max(10, _checklistFontSize - 2),
                FontWeight = assist.State == EnergyComplianceService.BannerState.Red ? FontWeights.Bold : FontWeights.SemiBold,
                ToolTip = assist.CanApply ? "Tap to use this plan/report value" : assist.ToolTip
            };
            if (assist.CanApply)
                button.Click += InlineDesignAssistButton_Click;
            return button;
        }

        private InlineDesignAssist? GetInlineDesignAssist(Item item)
        {
            string? actualValue = item.Value?.ToString()?.Trim();

            if (_currentSlabInfo != null)
            {
                int? cableF2B = null;
                int? cableS2S = null;
                if (_currentInspection != null)
                {
                    var byNum = _currentInspection.Sections
                        .SelectMany(s => s.Items)
                        .GroupBy(i => i.Number ?? "")
                        .ToDictionary(g => g.Key, g => g.First());
                    if (byNum.TryGetValue("5.1.b", out var f2bItem) &&
                        int.TryParse(f2bItem.Value?.ToString(), out int f2b))
                        cableF2B = f2b;
                    if (byNum.TryGetValue("5.1.c", out var s2sItem) &&
                        int.TryParse(s2sItem.Value?.ToString(), out int s2s))
                        cableS2S = s2s;
                }

                var slabState = EnergyComplianceService.GetSlabItemBannerState(_currentSlabInfo, item.Number, actualValue, cableF2B, cableS2S);
                string? slabValue = EnergyComplianceService.GetSlabValueForItem(_currentSlabInfo, item.Number);
                string? slabLabel = EnergyComplianceService.GetSlabLabelForItem(item.Number);
                if (!string.IsNullOrWhiteSpace(slabValue))
                {
                    return new InlineDesignAssist(
                        item,
                        slabValue,
                        slabLabel != null ? $"{slabLabel}: {slabValue}" : slabValue,
                        slabState,
                        CanApply: true,
                        Source: "slab",
                        ToolTip: "Slab engineering value");
                }

                if ((item.Number == "5.1.b" || item.Number == "5.1.c") && _currentSlabInfo.CableCount.HasValue)
                {
                    string text = cableF2B.HasValue && cableS2S.HasValue
                        ? $"Plan total: {_currentSlabInfo.CableCount} ({cableF2B + cableS2S})"
                        : $"Plan total: {_currentSlabInfo.CableCount}";
                    return new InlineDesignAssist(item, "", text, slabState, CanApply: false, Source: "slab", ToolTip: "F2B plus S2S must match the plan total");
                }
            }

            if (_currentEcInfo != null && _currentEcInfo.IsLoaded)
            {
                string? ecValue = EnergyComplianceService.GetValueForItem(_currentEcInfo, _currentInspectionCode, item.Number);
                if (!string.IsNullOrWhiteSpace(ecValue) && ShouldShowInlineEcAssist(item, ecValue))
                {
                    string? ecLabel = EnergyComplianceService.GetLabelForItem(_currentInspectionCode, item.Number);
                    var ecState = EnergyComplianceService.GetEcItemBannerState(_currentEcInfo, _currentInspectionCode, item.Number, actualValue);
                    return new InlineDesignAssist(
                        item,
                        ecValue,
                        FormatInlineEcAssistText(item, ecLabel, ecValue),
                        ecState,
                        CanApply: true,
                        Source: "ec",
                        ToolTip: "Energy compliance report value");
                }
            }

            return null;
        }

        private bool ShouldShowInlineEcAssist(Item item, string ecValue)
        {
            string number = item.Number ?? "";
            string prompt = (item.Name ?? "").ToLowerInvariant();

            if (EnergyComplianceService.NormalizeCode(_currentInspectionCode) == "IER")
            {
                if (number == "1.3")
                    return prompt.Contains("iecc") || prompt.Contains("energy star");

                if (number == "1.4")
                    return prompt.Contains("iecc") || prompt.Contains("code") || prompt.Contains("energy star");
            }

            return true;
        }

        private string FormatInlineEcAssistText(Item item, string? ecLabel, string ecValue)
        {
            string number = item.Number ?? "";
            string prompt = (item.Name ?? "").ToLowerInvariant();
            string normalizedCode = EnergyComplianceService.NormalizeCode(_currentInspectionCode);

            if (normalizedCode == "IER" && number == "1.3" && prompt.Contains("iecc"))
            {
                if (ecValue.StartsWith("Energy Star", StringComparison.OrdinalIgnoreCase))
                    return "Energy Star";
                return ecValue;
            }

            return ecLabel != null ? $"{ecLabel}: {ecValue}" : ecValue;
        }

        private bool HasInlineValueChoiceList(Item item, string[]? options)
        {
            return options != null &&
                   options.Length > 4 &&
                   item.ValueList != null &&
                   item.ValueList.Count > 0;
        }

        private IEnumerable<string> OrderInlineQuickStatusOptions(string[] options)
        {
            int Rank(string option)
            {
                string normalized = option.Trim().ToLowerInvariant();
                if (normalized is "n/a" or "na" or "ni")
                    return 0;
                if (normalized is "fail" or "no")
                    return 1;
                if (normalized is "pass" or "yes")
                    return 2;
                return 1;
            }

            return options.OrderBy(Rank).ThenBy(o => o, StringComparer.OrdinalIgnoreCase);
        }

        private Border CreateInlineDrawerHost(Item item)
        {
            var host = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(12, 0, 12, 10),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (string drawerName in GetDrawersForItem(item))
            {
                if (IsInlineDrawerOpen(item, drawerName))
                    host.Children.Add(CreateOpenInlineDrawer(item, drawerName));
            }

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 248, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(207, 216, 226)),
                BorderThickness = new Thickness(1, 0, 1, 1),
                Padding = new Thickness(0, 8, 0, 0),
                Child = host
            };
        }

        private IEnumerable<string> GetDrawersForItem(Item item)
        {
            bool useAdminSuppression = _suppressAdministrativeTools &&
                IsAdministrativeItem(item) &&
                !HasStandardToolsOverride(item);

            IEnumerable<string> standardDrawers = useAdminSuppression
                ? Array.Empty<string>()
                : InlineStandardDrawers;

            string[]? options = GetInlineStatusOptions(item, item.ControlName?.ToLower() ?? "");
            if (HasInlineValueChoiceList(item, options))
                yield return InlineValueChoicesDrawer;

            foreach (string drawer in standardDrawers)
            {
                if (!ShouldOfferInlineDrawer(item, drawer))
                    continue;
                yield return drawer;
            }
            yield return InlineToolSelectorDrawer;
            if (IsSpecialInlineDrawerRemembered(item, InlineNumberpadDrawer))
                yield return InlineNumberpadDrawer;
            if (IsSpecialInlineDrawerRemembered(item, InlineBeamDimensionsDrawer))
                yield return InlineBeamDimensionsDrawer;
        }

        private bool ShouldOfferInlineDrawer(Item item, string drawerName)
        {
            if (drawerName == "Saved Comments")
                return IsInlineDrawerOpen(item, "Comments") && HasInlineSavedComments(item);

            if (drawerName == "AI Help")
            {
                return false;
            }

            return true;
        }

        private Border CreateOpenInlineDrawer(Item item, string drawerName)
        {
            var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement content;
            switch (drawerName)
            {
                case "Status":
                    content = CreateInlineStatusDrawer(item);
                    break;
                case "Comments":
                    content = CreateInlineCommentsDrawer(item);
                    break;
                case "Photos":
                    content = CreateInlinePhotosDrawer(item);
                    break;
                case "Saved Comments":
                    content = CreateInlineSavedCommentsDrawer(item);
                    break;
                case "AI Help":
                    content = CreateInlineAiHelpDrawer(item);
                    break;
                case InlineToolSelectorDrawer:
                    content = CreateInlineToolSelectorDrawer(item);
                    break;
                case InlineValueChoicesDrawer:
                    content = CreateInlineValueChoicesDrawer(item);
                    break;
                case InlineNumberpadDrawer:
                    content = CreateInlineNumberpadDrawer(item);
                    break;
                case InlineBeamDimensionsDrawer:
                    content = CreateInlineInfoText("Beam dimension tools will live here.");
                    break;
                default:
                    content = CreateInlineInfoText(drawerName);
                    break;
            }

            Grid.SetColumn(content, 0);
            if (content is FrameworkElement contentElement)
                contentElement.HorizontalAlignment = drawerName == InlineNumberpadDrawer || drawerName == "Comments" || drawerName == "Photos"
                    ? HorizontalAlignment.Stretch
                    : HorizontalAlignment.Right;
            row.Children.Add(content);

            if (drawerName == InlineToolSelectorDrawer)
            {
                return new Border
                {
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 2, 8, 4),
                    Margin = new Thickness(0, 0, 0, 3),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = row
                };
            }

            if (drawerName == InlineValueChoicesDrawer)
            {
                return new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 0, 5),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Child = row
                };
            }

            var sidePanel = new Grid
            {
                MinWidth = 116,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            sidePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sidePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sidePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = drawerName,
                FontSize = Math.Max(11, _checklistFontSize - 1),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(title, 0);
            sidePanel.Children.Add(title);

            var hideButton = new Button
            {
                Content = "Hide",
                Tag = new InlineDrawerAction(item, drawerName, false),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                FontSize = Math.Max(10, _checklistFontSize - 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            hideButton.Click += InlineDrawerToggle_Click;
            Grid.SetRow(hideButton, 2);
            sidePanel.Children.Add(hideButton);

            Grid.SetColumn(sidePanel, 1);
            row.Children.Add(sidePanel);

            return new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = row
            };
        }

        private Border CreateClosedInlineDrawer(Item item, string drawerName)
        {
            string label = drawerName == InlineExperimentalToolsDrawer
                ? "Select experimental entry tools"
                : $"Show {drawerName}";
            var button = new Button
            {
                Content = label,
                Tag = new InlineDrawerAction(item, drawerName, true),
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 0, 5),
                FontSize = Math.Max(11, _checklistFontSize - 1)
            };
            button.Click += InlineDrawerToggle_Click;
            return new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = button
            };
        }

        private UIElement CreateInlineStatusDrawer(Item item)
        {
            string controlName = item.ControlName?.ToLower() ?? "";
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            string[]? options = GetInlineStatusOptions(item, controlName);

            if (options != null)
            {
                foreach (string option in options)
                {
                    bool selected = option.Equals(item.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                    var btn = new Button
                    {
                        Content = option,
                        Tag = new InlineValueAction(item, option),
                        Padding = new Thickness(12, 7, 12, 7),
                        Margin = new Thickness(0, 0, 6, 6),
                        Background = selected ? GetStatusBrushForValue(option) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                        Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                        FontSize = _checklistFontSize,
                        FocusVisualStyle = null,
                        Style = CreateInlineStatusButtonStyle()
                    };
                    btn.Click += InlineStatusButton_Click;
                    panel.Children.Add(btn);
                }
            }

            var valueBox = new TextBox
            {
                Text = item.Value?.ToString() ?? "",
                Tag = item,
                MinWidth = 180,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = _checklistFontSize,
                ToolTip = "Type a value for this item"
            };
            valueBox.GotKeyboardFocus += InlineValueBox_GotKeyboardFocus;
            valueBox.LostFocus += InlineValueBox_LostFocus;
            panel.Children.Add(valueBox);
            return CreateInlineHorizontalScroller(panel, 220);
        }

        private string[]? GetInlineStatusOptions(Item item, string controlName)
        {
            string[]? options = controlName switch
            {
                "yesno" => new[] { "Yes", "No", "N/A" },
                "yesnonani" => new[] { "Yes", "No", "NI" },
                "passfail" => new[] { "Pass", "Fail", "N/A" },
                "passfailnani" => new[] { "Pass", "Fail", "NI" },
                _ => item.ValueList?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(12)
                    .ToArray()
            };

            return options is { Length: > 0 } ? options : null;
        }

        private UIElement CreateInlineValueChoicesDrawer(Item item)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "InlinePrefixSuffixSwipeZone"
            };

            string currentValue = item.Value?.ToString() ?? "";
            foreach (string option in GetSortedInlineValueChoices(item))
            {
                bool selected = option.Equals(currentValue, StringComparison.OrdinalIgnoreCase);
                var button = new Button
                {
                    Content = option,
                    Tag = new InlineValueAction(item, option),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = selected ? GetStatusBrushForValue(option) : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    Foreground = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    FontSize = Math.Max(11, _checklistFontSize - 1),
                    FontWeight = selected || GetInlineUsageCount(item, option) > 1
                        ? FontWeights.Bold
                        : FontWeights.Normal,
                    FocusVisualStyle = null,
                    Style = CreateInlineStatusButtonStyle()
                };
                button.Click += InlineStatusButton_Click;
                panel.Children.Add(button);
            }

            return CreateInlineHorizontalScroller(panel, 80);
        }

        private IEnumerable<string> GetSortedInlineValueChoices(Item item)
        {
            var options = (item.ValueList ?? new List<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string key = GetInlineUsageKey(item, "value");
            _inlineDrawerPreferences.ValueUsageCounts.TryGetValue(key, out var counts);

            return options
                .OrderByDescending(option => counts != null && counts.TryGetValue(option, out int count) ? count : 0)
                .ThenBy(option => option, StringComparer.OrdinalIgnoreCase);
        }

        private UIElement CreateInlineCommentsDrawer(Item item)
        {
            string existingComment = item.Comments ?? "";
            string selectedPrefix = UserDataService.ExtractPrefix(existingComment);
            var selectedSuffixes = UserDataService.ExtractSuffixes(existingComment);

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            panel.Children.Add(CreateInlinePrefixSuffixStrip(
                item,
                GetInlinePrefixOptions(),
                selectedPrefix,
                isPrefix: true));

            var commentRow = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            commentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 220 });
            commentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star), MinWidth = 260 });
            commentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            commentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bool quickCommentsDismissed = ReferenceEquals(_inlineQuickCommentsDismissedItem, item);
            bool hasExistingComment = !string.IsNullOrWhiteSpace(existingComment);
            var quickComments = quickCommentsDismissed || hasExistingComment
                ? new List<string>()
                : GetInlineQuickComments(item);
            const double quickCommentAreaHeight = 90;
            const double commentAreaHeight = 84;

            var savedPanel = CreateInlineSavedCommentsSidePanel(item);
            Grid.SetColumn(savedPanel, 0);
            commentRow.Children.Add(savedPanel);

            var saveCommentButton = new Button
            {
                Content = "\uE74E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Tag = item,
                Width = 38,
                MinHeight = quickComments.Count > 0 ? quickCommentAreaHeight : commentAreaHeight,
                Padding = new Thickness(5),
                Margin = new Thickness(4, 3, 4, 3),
                Background = new SolidColorBrush(Color.FromRgb(245, 240, 255)),
                Foreground = new SolidColorBrush(Color.FromRgb(107, 33, 168)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(216, 180, 254)),
                ToolTip = "Save the current comment for this checklist item"
            };
            saveCommentButton.Click += InlineSaveCommentButton_Click;
            Grid.SetColumn(saveCommentButton, 1);
            commentRow.Children.Add(saveCommentButton);

            var commentBoxHost = new Grid();

            var box = new TextBox
            {
                Text = existingComment,
                Tag = item,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = quickComments.Count > 0 ? quickCommentAreaHeight : commentAreaHeight,
                MaxHeight = 134,
                Padding = quickComments.Count > 0 ? new Thickness(8, quickCommentAreaHeight + 8, 8, 8) : new Thickness(8, 8, 8, 8),
                Margin = new Thickness(8, 3, 8, 3),
                FontSize = _checklistFontSize,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                Background = Brushes.White
            };
            box.GotKeyboardFocus += InlineValueBox_GotKeyboardFocus;
            box.TextChanged += InlineCommentsBox_TextChanged;
            box.LostFocus += InlineCommentsBox_LostFocus;
            commentBoxHost.Children.Add(box);

            if (quickComments.Count > 0)
            {
                var quickPanel = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8, 8, 12, 0),
                    Height = quickCommentAreaHeight,
                    Tag = "InlinePrefixSuffixSwipeZone"
                };
                for (int i = 0; i < 3; i++)
                    quickPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int visibleQuickCount = Math.Min(3, quickComments.Count);
                for (int i = 0; i < visibleQuickCount; i++)
                {
                    string comment = quickComments[i];
                    var text = new TextBlock
                    {
                        Text = comment,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontSize = Math.Max(10, _checklistFontSize - 2),
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(49, 46, 129)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var button = new Button
                    {
                        Content = text,
                        Tag = new InlineQuickCommentAction(item, comment),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(i == 0 ? 0 : 3, 0, 3, 0),
                        Height = quickCommentAreaHeight,
                        VerticalContentAlignment = VerticalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Color.FromRgb(238, 242, 255)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(165, 180, 252)),
                        ToolTip = comment
                    };
                    button.Click += InlineQuickCommentButton_Click;
                    Grid.SetColumn(button, i);
                    quickPanel.Children.Add(button);
                }

                commentBoxHost.Children.Add(quickPanel);
            }

            Grid.SetColumn(commentBoxHost, 2);
            commentRow.Children.Add(commentBoxHost);

            {
                bool hasComment = !string.IsNullOrWhiteSpace(existingComment);
                var factCheckButton = new Button
                {
                    Content = "🧠",
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    Tag = item,
                    Width = 38,
                    MinHeight = quickComments.Count > 0 ? quickCommentAreaHeight : commentAreaHeight,
                    Padding = new Thickness(5),
                    Margin = new Thickness(0, 3, 4, 3),
                    Background = new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(147, 197, 253)),
                    ToolTip = "AI fact-check this comment"
                };
                factCheckButton.Click += InlineFactCheckCommentButton_Click;
                Grid.SetColumn(factCheckButton, 3);
                commentRow.Children.Add(factCheckButton);

                var flagAndClearPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = 38,
                    MinHeight = quickComments.Count > 0 ? quickCommentAreaHeight : commentAreaHeight,
                    Margin = new Thickness(0, 3, 0, 3)
                };

                var redFlagButton = new Button
                {
                    Content = "\U0001F6A9",
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    Tag = new InlineCommentFlagAction(item),
                    Width = 38,
                    Height = 38,
                    Padding = new Thickness(3),
                    Margin = new Thickness(0, 0, 0, 4),
                    Background = new SolidColorBrush(Color.FromRgb(255, 241, 242)),
                    Foreground = new SolidColorBrush(Color.FromRgb(190, 18, 60)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(251, 113, 133)),
                    ToolTip = "Flag this comment for specialist review"
                };
                UpdateSpecialistFlagButtonState(redFlagButton, existingComment);
                redFlagButton.Click += InlineSpecialistFlagButton_Click;
                flagAndClearPanel.Children.Add(redFlagButton);

                var clearCommentButton = new Button
                {
                    Content = "\uE74D",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    Tag = item,
                    Width = 38,
                    MinHeight = Math.Max(38, (quickComments.Count > 0 ? quickCommentAreaHeight : commentAreaHeight) - 42),
                    Padding = new Thickness(5),
                    Margin = new Thickness(0),
                    Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
                    Foreground = hasComment
                        ? new SolidColorBrush(Color.FromRgb(153, 27, 27))
                        : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(248, 180, 180)),
                    ToolTip = "Clear comment or quick comment"
                };
                clearCommentButton.Click += InlineClearCommentButton_Click;
                flagAndClearPanel.Children.Add(clearCommentButton);

                Grid.SetColumn(flagAndClearPanel, 4);
                commentRow.Children.Add(flagAndClearPanel);
            }

            panel.Children.Add(commentRow);

            panel.Children.Add(CreateInlinePrefixSuffixStrip(
                item,
                GetInlineSuffixOptions(),
                selectedSuffixes,
                isPrefix: false));

            return panel;
        }

        private static IEnumerable<string> GetInlinePrefixOptions()
        {
            return UserDataService.DefaultPrefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> GetInlineSuffixOptions()
        {
            return UserDataService.DefaultSuffixes
                .Where(suffix => !string.IsNullOrWhiteSpace(suffix))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(suffix => suffix, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private UIElement CreateInlineSavedCommentsSidePanel(Item item)
        {
            var shell = new Border
            {
                MinHeight = 100,
                MaxHeight = 172,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(253, 248, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(216, 180, 254)),
                BorderThickness = new Thickness(1)
            };

            var panel = new DockPanel { LastChildFill = true };

            var list = new StackPanel { Orientation = Orientation.Vertical };
            var saved = GetInlineSavedComments(item);
            if (saved.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "No saved comments",
                    FontSize = Math.Max(9, _checklistFontSize - 3),
                    FontStyle = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                    Margin = new Thickness(2, 5, 2, 0)
                });
            }
            else
            {
                foreach (string comment in saved)
                    list.Children.Add(CreateInlineSavedCommentRow(item, comment));
            }

            var scroller = new ScrollViewer
            {
                Content = list,
                MinHeight = 72,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                CanContentScroll = false,
                Tag = "InlineVerticalSwipeZone"
            };
            scroller.PreviewMouseLeftButtonDown += InlineVerticalScrollViewer_PreviewMouseLeftButtonDown;
            scroller.PreviewMouseMove += InlineVerticalScrollViewer_PreviewMouseMove;
            scroller.PreviewMouseLeftButtonUp += InlineVerticalScrollViewer_PreviewMouseLeftButtonUp;
            scroller.MouseLeave += InlineVerticalScrollViewer_MouseLeave;
            panel.Children.Add(scroller);

            shell.Child = panel;
            return shell;
        }

        private UIElement CreateInlineSavedCommentRow(Item item, string comment)
        {
            var row = new Grid
            {
                Margin = new Thickness(0, 0, 0, 4),
                MaxHeight = 62,
                ClipToBounds = true
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = comment,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 48,
                FontSize = Math.Max(9, _checklistFontSize - 3),
                Foreground = new SolidColorBrush(Color.FromRgb(88, 28, 135))
            };
            var applyButton = new Button
            {
                Content = text,
                Tag = new InlineQuickCommentAction(item, comment),
                Padding = new Thickness(6, 4, 6, 4),
                MaxHeight = 62,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(250, 245, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(216, 180, 254)),
                ToolTip = comment
            };
            applyButton.Click += InlineSavedCommentButton_Click;
            Grid.SetColumn(applyButton, 0);
            row.Children.Add(applyButton);

            var deleteButton = new Button
            {
                Content = "X",
                Tag = new InlineQuickCommentAction(item, comment),
                Width = 28,
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
                Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(248, 180, 180)),
                ToolTip = "Remove saved comment"
            };
            deleteButton.Click += InlineDeleteSavedCommentButton_Click;
            Grid.SetColumn(deleteButton, 1);
            row.Children.Add(deleteButton);

            return row;
        }

        private UIElement CreateInlineSavedCommentsDrawer(Item item)
        {
            var suggestions = GetInlineSavedComments(item);
            if (suggestions.Count == 0)
                return CreateInlineInfoText("No saved comments yet.");

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "InlinePrefixSuffixSwipeZone"
            };

            foreach (string comment in suggestions.Take(6))
            {
                var text = new TextBlock
                {
                    Text = comment,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = Math.Max(10, _checklistFontSize - 2),
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
                };
                var button = new Button
                {
                    Content = text,
                    Tag = new InlineQuickCommentAction(item, comment),
                    Width = 300,
                    MinHeight = 56,
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    ToolTip = comment
                };
                button.Click += InlineQuickCommentButton_Click;
                panel.Children.Add(button);
            }

            return CreateInlineHorizontalScroller(panel, 150);
        }

        private UIElement CreateInlineAiHelpDrawer(Item item)
        {
            return CreateInlineInfoText("AI tools are built into the Photos drawer.");
        }

        private Button CreateInlineAiButton(string text, string toolTip)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Background = text.Equals("Get 3", StringComparison.OrdinalIgnoreCase)
                    ? new SolidColorBrush(Color.FromRgb(230, 126, 34))
                    : new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                FontSize = Math.Max(11, _checklistFontSize - 1),
                FontWeight = FontWeights.SemiBold,
                ToolTip = toolTip,
                IsEnabled = false
            };
        }

        private Button CreateInlineMediaAiButton(string text, Item item, bool enabled, RoutedEventHandler clickHandler, string toolTip)
        {
            bool isGetThree = text.Equals("Get 3", StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = text,
                Tag = item,
                Padding = isGetThree ? new Thickness(18, 7, 18, 7) : new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 8, 6),
                MinWidth = isGetThree ? 110 : 0,
                Background = isGetThree
                    ? new SolidColorBrush(Color.FromRgb(88, 80, 141))
                    : new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.Transparent,
                FontSize = Math.Max(10, _checklistFontSize - 3),
                FontWeight = FontWeights.SemiBold,
                ToolTip = toolTip,
                IsEnabled = enabled
            };
            button.Click += clickHandler;
            return button;
        }

        private List<string> GetInlineQuickComments(Item item)
        {
            if (string.IsNullOrWhiteSpace(_currentInspectionCode) || string.IsNullOrWhiteSpace(item.Number))
                return new List<string>();

            return _quickCommentsService.GetSuggestions(_currentInspectionCode, item.Number, 3);
        }

        private bool HasInlineSavedComments(Item item) => GetInlineSavedComments(item).Count > 0;

        private List<string> GetInlineSavedComments(Item item)
        {
            if (_userDataService == null || string.IsNullOrWhiteSpace(_currentInspectionCode))
                return new List<string>();

            return _userDataService.GetSavedComments(_currentInspectionCode, GetSavedCommentKey(item));
        }

        private string GetSavedCommentKey(Item item)
        {
            return item.Number ?? "";
        }

        private bool HasInlineValueSurface(Item item)
        {
            string controlName = item.ControlName?.ToLowerInvariant() ?? "";
            return controlName.Contains("text") ||
                   controlName.Contains("lookup") ||
                   item.ValueList is { Count: > 0 } ||
                   !string.IsNullOrWhiteSpace(item.Value?.ToString());
        }

        private UIElement CreateInlinePrefixSuffixStrip(Item item, IEnumerable<string> options, object selected, bool isPrefix)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = "InlinePrefixSuffixSwipeZone"
            };

            foreach (string option in options)
            {
                bool isSelected = isPrefix
                    ? option.Equals(selected as string ?? "", StringComparison.OrdinalIgnoreCase)
                    : selected is List<string> suffixes && suffixes.Any(s => s.Equals(option, StringComparison.OrdinalIgnoreCase));

                var btn = new ToggleButton
                {
                    Content = option,
                    Tag = new InlinePrefixSuffixAction(item, option, isPrefix),
                    IsChecked = isSelected,
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(3, 0, 0, 3),
                    FontSize = Math.Max(10, _checklistFontSize - 2),
                    Background = isSelected
                        ? (isPrefix ? new SolidColorBrush(Color.FromRgb(52, 152, 219)) : new SolidColorBrush(Color.FromRgb(39, 174, 96)))
                        : new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    Foreground = isSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    FontWeight = isSelected || GetInlineUsageCount(item, option, isPrefix ? "prefix" : "suffix") > 1
                        ? FontWeights.Bold
                        : FontWeights.Normal
                };
                btn.Click += InlinePrefixSuffixButton_Click;
                btn.PreviewMouseLeftButtonDown += InlinePrefixSuffixButton_PreviewMouseLeftButtonDown;
                btn.PreviewMouseMove += InlinePrefixSuffixButton_PreviewMouseMove;
                btn.PreviewMouseLeftButtonUp += InlinePrefixSuffixButton_PreviewMouseLeftButtonUp;
                btn.MouseLeave += InlinePrefixSuffixButton_MouseLeave;
                btn.MouseRightButtonUp += InlinePrefixSuffixButton_MouseRightButtonUp;
                panel.Children.Add(btn);
            }

            if (!isPrefix)
            {
                var dateButton = new Button
                {
                    Content = "Date",
                    Tag = item,
                    Padding = new Thickness(7, 2, 7, 2),
                    Margin = new Thickness(3, 0, 0, 3),
                    FontSize = Math.Max(10, _checklistFontSize - 2),
                    Background = new SolidColorBrush(Color.FromRgb(46, 134, 171)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0)
                };
                dateButton.Click += InlineDateSuffixButton_Click;
                panel.Children.Add(dateButton);
            }

            var addButton = new Button
            {
                Content = "+",
                Tag = new InlineAddPrefixSuffixAction(item, isPrefix),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(6, 0, 0, 3),
                FontSize = Math.Max(10, _checklistFontSize - 2),
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                ToolTip = isPrefix ? "Add custom trade prefix" : "Add custom location suffix"
            };
            addButton.Click += InlineAddPrefixSuffixButton_Click;
            panel.Children.Add(addButton);

            return CreateInlineHorizontalScroller(panel, 380);
        }

        private ScrollViewer CreateInlineHorizontalScroller(FrameworkElement content, double subtract)
        {
            content.Tag = "InlinePrefixSuffixSwipeZone";
            content.HorizontalAlignment = HorizontalAlignment.Left;
            var scroller = new ScrollViewer
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.HorizontalOnly,
                CanContentScroll = false,
                Tag = "InlinePrefixSuffixSwipeZone"
            };
            BindInlineToolWidth(scroller, subtract);
            scroller.PreviewMouseLeftButtonDown += InlineHorizontalScrollViewer_PreviewMouseLeftButtonDown;
            scroller.PreviewMouseMove += InlineHorizontalScrollViewer_PreviewMouseMove;
            scroller.PreviewMouseLeftButtonUp += InlineHorizontalScrollViewer_PreviewMouseLeftButtonUp;
            scroller.MouseLeave += InlineHorizontalScrollViewer_MouseLeave;
            return scroller;
        }

        private UIElement CreateInlineToolSelectorDrawer(Item item)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 2)
            };

            if (HasHiddenStandardTools(item))
            {
                var standardButton = new Button
                {
                    Content = "Standard Tools",
                    Tag = item,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = Math.Max(11, _checklistFontSize - 1),
                    ToolTip = "Restore the default drawers for this checklist prompt"
                };
                standardButton.Click += InlineStandardToolsButton_Click;
                panel.Children.Add(standardButton);
            }

            if (HasAvailableOtherTools(item))
            {
                var otherToolsButton = new Button
                {
                    Content = "Other Tools",
                    Tag = item,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    FontSize = Math.Max(11, _checklistFontSize - 1),
                    ToolTip = "Choose optional entry tools"
                };
                otherToolsButton.Click += InlineOtherToolsButton_Click;
                panel.Children.Add(otherToolsButton);
            }

            if (item.IsCopied)
            {
                var removeButton = new Button
                {
                    Content = "Remove Duplicate",
                    Tag = item,
                    MinWidth = 136,
                    MinHeight = 36,
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                    FontSize = Math.Max(11, _checklistFontSize - 1),
                    Background = new SolidColorBrush(Color.FromRgb(254, 242, 242)),
                    Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(248, 180, 180)),
                    ToolTip = "Remove this duplicated checklist item"
                };
                removeButton.Click += InlineRemoveDuplicateButton_Click;
                panel.Children.Add(removeButton);
            }

            var duplicateButton = new Button
            {
                Content = "Duplicate +",
                Tag = item,
                MinWidth = 116,
                MinHeight = 36,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = Math.Max(11, _checklistFontSize - 1),
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(29, 78, 216)),
                ToolTip = "Duplicate this checklist item"
            };
            duplicateButton.Click += InlineDuplicateButton_Click;
            panel.Children.Add(duplicateButton);

            return panel;
        }

        private UIElement CreateInlineNumberpadDrawer(Item item)
        {
            var tool = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4),
                Tag = "InlinePrefixSuffixSwipeZone"
            };
            tool.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tool.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rangePanel = CreateInlineNumberpadRangePanel(item);
            Grid.SetColumn(rangePanel, 0);
            tool.Children.Add(rangePanel);

            var pad = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 230,
                MaxWidth = 230,
                Margin = new Thickness(8, 0, 0, 0)
            };

            bool showFractions = !ShouldSuppressNumberpadFractions(item);
            int rowCount = showFractions ? 5 : 4;

            for (int row = 0; row < rowCount; row++)
                pad.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int col = 0; col < 3; col++)
                pad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            void AddKey(string label, int row, int col, int colSpan = 1)
            {
                var button = new Button
                {
                    Content = label,
                    Tag = new InlineNumberpadAction(item, label),
                    MinHeight = 55,
                    Margin = new Thickness(3),
                    FontSize = Math.Max(15, _checklistFontSize + 1),
                    Background = label == "CLEAR"
                        ? new SolidColorBrush(Color.FromRgb(254, 242, 242))
                        : new LinearGradientBrush(
                            Color.FromRgb(250, 250, 250),
                            Color.FromRgb(230, 230, 230),
                            90),
                    Foreground = label == "CLEAR"
                        ? new SolidColorBrush(Color.FromRgb(153, 27, 27))
                        : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    BorderBrush = label == "CLEAR"
                        ? new SolidColorBrush(Color.FromRgb(248, 180, 180))
                        : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    BorderThickness = new Thickness(1),
                    ToolTip = label == "CLEAR" ? "Clear value" : $"Add {label}"
                };
                button.Click += InlineNumberpadButton_Click;
                Grid.SetRow(button, row);
                Grid.SetColumn(button, col);
                Grid.SetColumnSpan(button, colSpan);
                pad.Children.Add(button);
            }

            AddKey("1", 0, 0);
            AddKey("2", 0, 1);
            AddKey("3", 0, 2);
            AddKey("4", 1, 0);
            AddKey("5", 1, 1);
            AddKey("6", 1, 2);
            AddKey("7", 2, 0);
            AddKey("8", 2, 1);
            AddKey("9", 2, 2);
            AddKey("0", 3, 0);
            AddKey("CLEAR", 3, 1, 2);
            if (showFractions)
            {
                AddKey("1/4", 4, 0);
                AddKey("1/2", 4, 1);
                AddKey("3/4", 4, 2);
            }

            Grid.SetColumn(pad, 1);
            tool.Children.Add(pad);
            return tool;
        }

        private UIElement CreateInlineNumberpadRangePanel(Item item)
        {
            string key = GetInlineItemKey(item);
            InlineNumberpadRange? customRange = GetCustomNumberpadRange(key);
            bool allowFractions = !ShouldSuppressNumberpadFractions(item);
            double? currentNumericValue = TryParseNumberpadObservedValue(item.Value?.ToString() ?? "", out double parsedCurrent)
                ? SnapNumberpadSliderValue(parsedCurrent, allowFractions)
                : null;
            InlineNumberpadRange? range = EnsureRangeIncludesCurrentValue(
                customRange ?? GetObservedNumberpadRange(item),
                currentNumericValue,
                allowFractions);
            bool isCustomRange = customRange != null;

            var panel = new StackPanel
            {
                MinWidth = range == null ? 96 : 320,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 10, 0),
                Tag = "InlinePrefixSuffixSwipeZone"
            };

            var rangeButton = new Button
            {
                Content = range == null
                    ? "Range..."
                    : isCustomRange
                        ? $"{range.Minimum:g}-{range.Maximum:g}"
                        : $"Auto {range.Minimum:g}-{range.Maximum:g}",
                Tag = item,
                MinHeight = 34,
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = Math.Max(10, _checklistFontSize - 4),
                ToolTip = isCustomRange
                    ? "Custom slider range for this checklist prompt"
                    : range == null
                        ? "Set the slider range for this checklist prompt"
                        : "Auto-learned slider range from values entered for this checklist prompt"
            };
            rangeButton.Click += InlineNumberpadRangeButton_Click;
            panel.Children.Add(rangeButton);

            if (range == null)
                return panel;

            double current = currentNumericValue ?? ParseDoubleValue(item.Value?.ToString(), range.Minimum);
            current = Math.Max(range.Minimum, Math.Min(range.Maximum, current));
            current = SnapNumberpadSliderValue(current, allowFractions);

            var valueText = new TextBlock
            {
                Text = FormatNumberpadSliderValue(current, allowFractions),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontSize = Math.Max(24, _checklistFontSize + 6),
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Margin = new Thickness(0, 8, 0, 0)
            };

            var sliderLane = CreateInlineNumberpadTouchSlider(item, range, current, allowFractions, valueText);
            panel.Children.Add(valueText);
            panel.Children.Add(sliderLane);

            return panel;
        }

        private FrameworkElement CreateInlineNumberpadTouchSlider(
            Item item,
            InlineNumberpadRange range,
            double current,
            bool allowFractions,
            TextBlock valueText)
        {
            const double thumbSize = 52;

            var lane = new Grid
            {
                MinHeight = 72,
                Margin = new Thickness(0, 2, 0, 0),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = "InlinePrefixSuffixSwipeZone",
                ClipToBounds = false,
                Cursor = Cursors.Hand
            };

            var track = new Border
            {
                Height = 10,
                Margin = new Thickness(thumbSize / 2, 0, thumbSize / 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                CornerRadius = new CornerRadius(5)
            };
            lane.Children.Add(track);

            var fill = new Border
            {
                Height = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(thumbSize / 2, 0, 0, 0)
            };
            lane.Children.Add(fill);

            var thumb = new Border
            {
                Width = thumbSize,
                Height = thumbSize,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(thumbSize / 2),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 7,
                    ShadowDepth = 1,
                    Opacity = 0.25
                }
            };
            lane.Children.Add(thumb);

            var action = new InlineNumberpadSliderAction(item, valueText, allowFractions, range, lane, thumb, fill);
            lane.Tag = action;
            lane.SizeChanged += (_, _) => UpdateInlineNumberpadSliderVisual(action, current);
            lane.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(InlineNumberpadTouchSlider_MouseDown), true);
            lane.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(InlineNumberpadTouchSlider_MouseMove), true);
            lane.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(InlineNumberpadTouchSlider_MouseUp), true);
            lane.AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(InlineNumberpadTouchSlider_TouchDown), true);
            lane.AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(InlineNumberpadTouchSlider_TouchMove), true);
            lane.AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(InlineNumberpadTouchSlider_TouchUp), true);
            lane.AddHandler(UIElement.PreviewStylusDownEvent, new StylusDownEventHandler(InlineNumberpadTouchSlider_StylusDown), true);
            lane.AddHandler(UIElement.PreviewStylusMoveEvent, new StylusEventHandler(InlineNumberpadTouchSlider_StylusMove), true);
            lane.AddHandler(UIElement.PreviewStylusUpEvent, new StylusEventHandler(InlineNumberpadTouchSlider_StylusUp), true);
            lane.MouseLeave += InlineNumberpadTouchSlider_MouseLeave;

            return lane;
        }

        private UIElement CreateInlinePhotosDrawer(Item item)
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinHeight = 112
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });

            bool hasPhoto = item.Pictures.Count > 0;
            bool canUseAi = _grokClient != null && hasPhoto;

            var transcribePanel = CreateInlineMediaToolPanel();
            transcribePanel.Children.Add(CreateInlineMediaAiButton(
                "Transcribe",
                item,
                canUseAi && HasInlineValueSurface(item),
                InlineTranscribeButton_Click,
                "Read a label or sticker from the current photo into the value field"));
            AddInlineAiResultButtons(transcribePanel, item, InlineAiMode.Transcribe);
            Grid.SetColumn(transcribePanel, 0);
            grid.Children.Add(transcribePanel);

            var getThreePanel = CreateInlineMediaToolPanel();
            var getThreeControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 6)
            };
            getThreeControls.Children.Add(CreateInlineAiToneComboBox());
            getThreeControls.Children.Add(CreateInlineMediaAiButton(
                "Get 3",
                item,
                canUseAi && IsInlineDrawerOpen(item, "Comments"),
                InlineGetThreeButton_Click,
                "Send the current photo to AI and get three suggested comments"));
            getThreePanel.Children.Add(getThreeControls);
            AddInlineAiResultButtons(getThreePanel, item, InlineAiMode.GetThree);
            Grid.SetColumn(getThreePanel, 1);
            grid.Children.Add(getThreePanel);

            var photoPanel = CreateInlineMediaToolPanel();
            int photoIndex = 0;
            if (item == _editorLoadedItem && item.Pictures.Count > 0)
                photoIndex = Math.Max(0, Math.Min(_currentPhotoIndex, item.Pictures.Count - 1));

            var photoStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };

            var photoTop = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (item.Pictures.Count > 0)
            {
                var thumb = new Border
                {
                    Width = 136,
                    Height = 96,
                    Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Tap to edit/annotate photo",
                    Tag = new InlinePhotoAction(item, photoIndex)
                };

                var image = new Image
                {
                    Source = CreateInlinePhotoThumbnail(item.Pictures[photoIndex]),
                    Stretch = Stretch.UniformToFill,
                    Tag = new InlinePhotoAction(item, photoIndex)
                };
                thumb.Child = image;
                thumb.MouseLeftButtonUp += InlinePhotoThumbnail_Click;
                photoTop.Children.Add(thumb);
            }
            else
            {
                photoTop.Children.Add(CreateInlineInfoText("No photos"));
            }

            var addPhoto = new Button
            {
                Content = "File",
                Tag = item,
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = Math.Max(10, _checklistFontSize - 3)
            };
            addPhoto.Click += InlineAddPhotoButton_Click;
            photoTop.Children.Add(addPhoto);

            var camera = new Button
            {
                Content = "Camera",
                Tag = item,
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = Math.Max(10, _checklistFontSize - 3)
            };
            camera.Click += InlineCameraButton_Click;
            photoTop.Children.Add(camera);
            photoStack.Children.Add(photoTop);

            var photoNav = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };
            photoNav.Children.Add(CreateInlinePhotoNavButton("<", new InlinePhotoAction(item, photoIndex), item.Pictures.Count > 0 && photoIndex > 0, InlinePrevPhotoButton_Click, "Previous photo"));
            photoNav.Children.Add(new TextBlock
            {
                Text = item.Pictures.Count > 0 ? $"Photo {photoIndex + 1} of {item.Pictures.Count}" : "Photo 0 of 0",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                FontSize = Math.Max(10, _checklistFontSize - 3),
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
            });
            photoNav.Children.Add(CreateInlinePhotoNavButton(">", new InlinePhotoAction(item, photoIndex), item.Pictures.Count > 0 && photoIndex < item.Pictures.Count - 1, InlineNextPhotoButton_Click, "Next photo"));
            photoNav.Children.Add(CreateInlinePhotoNavButton("Delete", new InlinePhotoAction(item, photoIndex), item.Pictures.Count > 0, InlineDeletePhotoButton_Click, "Delete current photo"));
            photoStack.Children.Add(photoNav);
            photoPanel.Children.Add(photoStack);

            Grid.SetColumn(photoPanel, 2);
            grid.Children.Add(photoPanel);
            return grid;
        }

        private StackPanel CreateInlineMediaToolPanel()
        {
            return new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 10, 0)
            };
        }

        private Button CreateInlinePhotoNavButton(string text, InlinePhotoAction action, bool enabled, RoutedEventHandler clickHandler, string toolTip)
        {
            var isDelete = text.Equals("Delete", StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = text,
                Tag = action,
                IsEnabled = enabled,
                Padding = isDelete ? new Thickness(8, 4, 8, 4) : new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                MinWidth = isDelete ? 64 : 38,
                Background = isDelete
                    ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
                    : new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Foreground = isDelete ? Brushes.White : new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                FontSize = Math.Max(10, _checklistFontSize - 3),
                ToolTip = toolTip
            };
            button.Click += clickHandler;
            return button;
        }

        private ComboBox CreateInlineAiToneComboBox()
        {
            var combo = new ComboBox
            {
                Width = 172,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 6),
                FontSize = Math.Max(10, _checklistFontSize - 3),
                ToolTip = "AI tone and speed"
            };

            foreach (string tone in new[]
            {
                "Technical - fast",
                "Technical - careful",
                "Nice - fast",
                "Nice - careful",
                "Strict - fast",
                "Strict - careful"
            })
            {
                combo.Items.Add(new ComboBoxItem { Content = tone });
            }

            string selected = GetSelectedAiTone();
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), selected, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    break;
                }
            }
            if (combo.SelectedIndex < 0)
                combo.SelectedIndex = 0;

            combo.SelectionChanged += InlineAiToneComboBox_SelectionChanged;
            return combo;
        }

        private void AddInlineAiResultButtons(Panel panel, Item item, InlineAiMode mode)
        {
            if (!_inlineAiResults.TryGetValue(item, out var result) || result.Mode != mode)
                return;

            var resultStack = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            if (result.IsLoading)
            {
                resultStack.Children.Add(CreateInlineInfoText(mode == InlineAiMode.Transcribe ? "Transcribing..." : "Analyzing image..."));
                panel.Children.Add(CreateInlineAiResultScroller(resultStack));
                return;
            }

            foreach (string suggestion in result.Suggestions.Take(3))
            {
                if (IsAiStatusSuggestion(suggestion))
                {
                    resultStack.Children.Add(CreateInlineAiStatusText(suggestion, mode));
                    continue;
                }

                var button = new Button
                {
                    Content = new TextBlock
                    {
                        Text = suggestion,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        FontSize = Math.Max(10, _checklistFontSize - 2),
                        Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
                    },
                    Tag = new InlineAiSuggestionAction(item, suggestion, mode),
                    MinHeight = 44,
                    MaxHeight = 78,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 5),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = mode == InlineAiMode.Transcribe
                        ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                        : new SolidColorBrush(Color.FromRgb(245, 243, 255)),
                    BorderBrush = mode == InlineAiMode.Transcribe
                        ? new SolidColorBrush(Color.FromRgb(147, 197, 253))
                        : new SolidColorBrush(Color.FromRgb(196, 181, 253)),
                    ToolTip = suggestion
                };
                button.Click += InlineAiSuggestionButton_Click;
                resultStack.Children.Add(button);
            }

            panel.Children.Add(CreateInlineAiResultScroller(resultStack));
        }

        private ScrollViewer CreateInlineAiResultScroller(FrameworkElement content)
        {
            var scroller = new ScrollViewer
            {
                Content = content,
                Height = 150,
                MinHeight = 150,
                MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                PanningMode = PanningMode.VerticalOnly,
                CanContentScroll = false,
                Padding = new Thickness(0, 0, 4, 0),
                Margin = new Thickness(0, 0, 0, 4)
            };

            scroller.PreviewMouseLeftButtonDown += InlineVerticalScrollViewer_PreviewMouseLeftButtonDown;
            scroller.PreviewMouseMove += InlineVerticalScrollViewer_PreviewMouseMove;
            scroller.PreviewMouseLeftButtonUp += InlineVerticalScrollViewer_PreviewMouseLeftButtonUp;
            scroller.MouseLeave += InlineVerticalScrollViewer_MouseLeave;
            return scroller;
        }

        private static bool IsAiStatusSuggestion(string? suggestion)
        {
            if (string.IsNullOrWhiteSpace(suggestion))
                return true;

            string text = suggestion.Trim();
            return text.StartsWith("⚠", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Gemini is busy", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("temporarily overloaded", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("No internet connection", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Request timed out", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("AI returned no usable", StringComparison.OrdinalIgnoreCase);
        }

        private UIElement CreateInlineAiStatusText(string text, InlineAiMode mode)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 251, 235)),
                BorderBrush = mode == InlineAiMode.Transcribe
                    ? new SolidColorBrush(Color.FromRgb(147, 197, 253))
                    : new SolidColorBrush(Color.FromRgb(196, 181, 253)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 5),
                Child = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Math.Max(10, _checklistFontSize - 2),
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105))
                }
            };
        }

        private ImageSource? CreateInlinePhotoThumbnail(Picture picture)
        {
            string? data = !string.IsNullOrWhiteSpace(picture.ThumbnailData)
                ? picture.ThumbnailData
                : picture.Data;

            if (string.IsNullOrWhiteSpace(data))
                return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(data);
                using var ms = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 96;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private TextBlock CreateInlineInfoText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = _checklistFontSize,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Border CreateInlineBadge(string text, Brush background, Brush foreground, FontWeight weight)
        {
            return new Border
            {
                Background = background,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 5, 4),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = foreground,
                    FontWeight = weight,
                    FontSize = Math.Max(10, _checklistFontSize - 1),
                    TextWrapping = TextWrapping.NoWrap
                }
            };
        }

        private Brush GetStatusBrush(Item item) => GetStatusBrushForValue(item.Value?.ToString() ?? "");

        private Brush GetStatusBrushForValue(string value)
        {
            string valueLower = value.ToLower();
            if (valueLower.Contains("pass") || valueLower == "yes" || valueLower == "ok")
                return new SolidColorBrush(Color.FromRgb(22, 163, 74));
            if (valueLower.Contains("fail") || valueLower == "no" || valueLower.Contains("defect"))
                return new SolidColorBrush(Color.FromRgb(190, 18, 60));
            if (valueLower.Contains("pending") || valueLower == "na" || valueLower == "n/a" || valueLower == "ni")
                return new SolidColorBrush(Color.FromRgb(100, 116, 139));
            if (!string.IsNullOrWhiteSpace(value))
                return new SolidColorBrush(Color.FromRgb(37, 99, 235));
            return new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }

        private Style CreateInlineStatusButtonStyle()
        {
            var style = new Style(typeof(Button));

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            content.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            border.AppendChild(content);

            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private void StretchInlineWidth(FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            var widthBinding = new System.Windows.Data.Binding("ActualWidth")
            {
                Source = TreeScrollViewer,
                Converter = new SubtractConverter(),
                ConverterParameter = 14.0
            };
            element.SetBinding(FrameworkElement.WidthProperty, widthBinding);
        }

        private void BindInlineToolWidth(FrameworkElement element, double subtract)
        {
            var widthBinding = new System.Windows.Data.Binding("ActualWidth")
            {
                Source = TreeScrollViewer,
                Converter = new SubtractConverter(),
                ConverterParameter = subtract
            };
            element.SetBinding(FrameworkElement.WidthProperty, widthBinding);
        }

        private void InlineItemRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_scrollStarted) return;
            if (sender is not FrameworkElement element || element.Tag is not Item item) return;

            AutoApplyCurrentItem();
            ToggleInlineItem(item);
            e.Handled = true;
        }

        private void ToggleInlineItem(Item item)
        {
            SetInlineItemExpanded(item, !IsInlineItemExpanded(item));
        }

        private void SetInlineItemExpanded(Item item, bool expand)
        {
            if (!_inlineEditorMode)
                return;

            if (expand)
            {
                if (!ReferenceEquals(_expandedInlineItemInstance, item))
                    _inlineQuickCommentsDismissedItem = null;
                _expandedInlineItemKey = GetInlineItemKey(item);
                _expandedInlineItemInstance = item;
                LoadItemEditor(item);
                SelectItemInTreeView(item);
            }
            else if (IsInlineItemExpanded(item))
            {
                _expandedInlineItemKey = null;
                _expandedInlineItemInstance = null;
                _inlineQuickCommentsDismissedItem = null;
            }

            PopulateInlineChecklist(SearchFilterBox.Text);
            if (expand)
                KeepInlineItemHeaderVisible(item);
        }

        private void KeepInlineItemHeaderVisible(Item item)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (TreeScrollViewer == null)
                    return;

                var row = FindVisualChildren<Border>(InlineChecklistPanel)
                    .FirstOrDefault(border => ReferenceEquals(border.Tag, item));
                if (row == null)
                    return;

                try
                {
                    Point position = row.TransformToAncestor(TreeScrollViewer).Transform(new Point(0, 0));
                    double targetTop = 8;
                    double rowTop = position.Y;
                    double rowBottom = rowTop + row.ActualHeight;
                    double viewport = TreeScrollViewer.ViewportHeight;

                    if (rowTop < targetTop)
                    {
                        TreeScrollViewer.ScrollToVerticalOffset(TreeScrollViewer.VerticalOffset + rowTop - targetTop);
                    }
                    else if (rowBottom > viewport && rowTop > targetTop)
                    {
                        double desiredTop = Math.Min(rowTop, Math.Max(targetTop, viewport * 0.20));
                        TreeScrollViewer.ScrollToVerticalOffset(TreeScrollViewer.VerticalOffset + rowTop - desiredTop);
                    }
                }
                catch
                {
                    // Layout can briefly be unavailable while the inline list is rebuilding.
                }
            }));
        }

        private void InlineDrawerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlineDrawerAction action })
            {
                SetInlineDrawerOpen(action.Item, action.DrawerName, action.Open);
                SaveInlineDrawerPreferences();
                PopulateInlineChecklist(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlineStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlineValueAction action })
            {
                SetInlineItemValue(action.Item, action.Value);
                e.Handled = true;
            }
        }

        private void InlineClearValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                LoadItemEditor(item);
                if (!string.IsNullOrWhiteSpace(item.Value?.ToString()))
                {
                    item.Value = "";
                    MarkUnsaved();
                    LoadItemEditor(item);
                    RefreshEngDataPanel();
                    RefreshEcDataPanel();
                    PopulateTreeView(SearchFilterBox.Text);
                }
                e.Handled = true;
            }
        }

        private void InlineDesignAssistButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlineDesignAssist assist } && assist.CanApply)
            {
                LoadItemEditor(assist.Item);
                assist.Item.Value = assist.Value;
                RecordInlineValueUsage(assist.Item, assist.Value);
                MarkUnsaved();
                LoadItemEditor(assist.Item);
                RefreshEngDataPanel();
                RefreshEcDataPanel();
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlineQuickCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlineQuickCommentAction action })
            {
                LoadItemEditor(action.Item);
                _inlineQuickCommentsDismissedItem = action.Item;
                action.Item.Comments = BuildInlineCommentFromSuggestion(action.Item, action.Comment, appendToExisting: false);
                MarkUnsaved();
                LoadItemEditor(action.Item);
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private string BuildInlineCommentFromSuggestion(Item item, string suggestion, bool appendToExisting)
        {
            string existing = item.Comments ?? "";
            string existingPrefix = UserDataService.ExtractPrefix(existing);
            var existingSuffixes = UserDataService.ExtractSuffixes(existing);
            string suggestionPrefix = UserDataService.ExtractPrefix(suggestion);
            string prefix = string.IsNullOrWhiteSpace(suggestionPrefix) ? existingPrefix : suggestionPrefix;
            string suggestionCore = UserDataService.StripPrefixAndSuffix(suggestion);
            string core = suggestionCore;

            if (appendToExisting)
            {
                string existingCore = UserDataService.StripPrefixAndSuffix(existing).Trim();
                if (!string.IsNullOrWhiteSpace(existingCore))
                    core = $"{existingCore} | {suggestionCore}";
            }

            return UserDataService.BuildComment(prefix, core, existingSuffixes);
        }

        private string SyncInlineCommentTextFromOpenRow(Item item)
        {
            var inlineBox = FindVisualChildren<TextBox>(InlineChecklistPanel)
                .FirstOrDefault(box => ReferenceEquals(box.Tag, item) && box.AcceptsReturn);
            if (inlineBox == null)
                return item.Comments ?? "";

            string typed = inlineBox.Text ?? "";
            string existing = item.Comments ?? "";
            string typedPrefix = UserDataService.ExtractPrefix(typed);
            string prefix = string.IsNullOrWhiteSpace(typedPrefix)
                ? UserDataService.ExtractPrefix(existing)
                : typedPrefix;
            var typedSuffixes = UserDataService.ExtractSuffixes(typed);
            var suffixes = typedSuffixes.Count > 0
                ? typedSuffixes
                : UserDataService.ExtractSuffixes(existing);
            string core = UserDataService.StripPrefixAndSuffix(typed);
            string normalized = UserDataService.BuildComment(prefix, core, suffixes);
            if (!string.Equals(existing, normalized, StringComparison.Ordinal))
                MarkUnsaved();
            item.Comments = string.IsNullOrWhiteSpace(normalized) ? null : normalized;
            return item.Comments ?? "";
        }

        private static string CommentWithoutSpecialistFlag(string? comment)
        {
            return (comment ?? "").Replace(SpecialistFlagMarker, "").Trim();
        }

        private static bool HasSpecialistFlag(string? comment)
        {
            return !string.IsNullOrEmpty(comment) &&
                   comment.Contains(SpecialistFlagMarker, StringComparison.Ordinal);
        }

        private static bool CanAddSpecialistFlag(string? comment)
        {
            return CommentWithoutSpecialistFlag(comment).Length >= SpecialistFlagMinimumCommentLength &&
                   !HasSpecialistFlag(comment);
        }

        private static string AddSpecialistFlag(string? comment)
        {
            string value = (comment ?? "").TrimEnd();
            if (HasSpecialistFlag(value))
                return value;

            return string.IsNullOrWhiteSpace(value)
                ? SpecialistFlagMarker
                : $"{value} {SpecialistFlagMarker}";
        }

        private void UpdateSpecialistFlagButtonState(Button? button, string? comment)
        {
            if (button == null)
                return;

            bool canFlag = CanAddSpecialistFlag(comment);
            button.IsEnabled = canFlag;
            button.Opacity = canFlag ? 1.0 : 0.42;
        }

        private void UpdateInlineSpecialistFlagButtonState(Item item, string? comment)
        {
            foreach (var button in FindVisualChildren<Button>(InlineChecklistPanel))
            {
                if (button.Tag is InlineCommentFlagAction action && ReferenceEquals(action.Item, item))
                    UpdateSpecialistFlagButtonState(button, comment);
            }
        }

        private void UpdateClassicSpecialistFlagButtonState()
        {
            UpdateSpecialistFlagButtonState(SpecialistFlagButton, CommentsTextBox?.Text);
        }

        private void InlineSpecialistFlagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InlineCommentFlagAction action } button)
                return;

            string comment = SyncInlineCommentTextFromOpenRow(action.Item);
            if (!CanAddSpecialistFlag(comment))
            {
                UpdateSpecialistFlagButtonState(button, comment);
                e.Handled = true;
                return;
            }

            action.Item.Comments = AddSpecialistFlag(comment);
            AutoSetFailForItemWithComment(action.Item, action.Item.Comments, refreshClassicStatus: false);
            MarkUnsaved();
            LoadItemEditor(action.Item);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineSaveCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item })
                return;

            if (_userDataService == null || string.IsNullOrEmpty(_currentInspectionCode))
            {
                MessageBox.Show("No inspection loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string comment = SyncInlineCommentTextFromOpenRow(item);
            if (string.IsNullOrWhiteSpace(comment))
            {
                MessageBox.Show("No comment to save.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _userDataService.SaveComment(_currentInspectionCode, GetSavedCommentKey(item), comment);
            LoadItemEditor(item);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineSavedCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InlineQuickCommentAction action })
                return;

            LoadItemEditor(action.Item);
            action.Item.Comments = BuildInlineCommentFromSuggestion(action.Item, action.Comment, appendToExisting: true);
            if (ReferenceEquals(_inlineQuickCommentsDismissedItem, action.Item))
                _inlineQuickCommentsDismissedItem = null;
            AutoSetFailIfApplicable();
            MarkUnsaved();
            LoadItemEditor(action.Item);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineDeleteSavedCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InlineQuickCommentAction action } ||
                _userDataService == null ||
                string.IsNullOrEmpty(_currentInspectionCode))
            {
                return;
            }

            _userDataService.RemoveComment(_currentInspectionCode, GetSavedCommentKey(action.Item), action.Comment);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineClearCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                LoadItemEditor(item);
                _inlineQuickCommentsDismissedItem = item;
                if (!string.IsNullOrWhiteSpace(item.Comments))
                {
                    item.Comments = "";
                    MarkUnsaved();
                    LoadItemEditor(item);
                }
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlineCommentsBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox { Tag: Item item } box)
                return;

            MarkUnsaved();
            AutoSetFailForItemWithComment(item, box.Text, refreshClassicStatus: false);
            UpdateInlineSpecialistFlagButtonState(item, box.Text);
        }

        private async void InlineFactCheckCommentButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item } button)
                return;

            string comment = SyncInlineCommentTextFromOpenRow(item).Trim();
            if (string.IsNullOrWhiteSpace(comment))
            {
                MessageBox.Show("No comment to fact-check.", "AI Fact Check", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Handled = true;
                return;
            }

            if (_grokClient == null)
            {
                MessageBox.Show("AI is not configured yet.", "AI Fact Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
                return;
            }

            object originalContent = button.Content;
            bool originalEnabled = button.IsEnabled;
            var frames = new[] { "🧠", "•", "••", "•••" };
            int frame = 0;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(260)
            };
            timer.Tick += (_, _) =>
            {
                frame = (frame + 1) % frames.Length;
                button.Content = frames[frame];
            };

            try
            {
                button.IsEnabled = false;
                timer.Start();

                string sectionName = _currentInspection?.Sections?
                    .FirstOrDefault(s => s.Items.Contains(item))?.Name ?? "";
                byte[]? photoData = GetInlinePhotoBytes(item);

                string result = await _grokClient.FactCheckInspectionComment(
                    comment,
                    item.Number ?? "",
                    item.Name ?? "",
                    _currentInspectionCode ?? "",
                    sectionName,
                    photoData);

                var factCheckWindow = new FactCheckWindow(result)
                {
                    Owner = this
                };
                factCheckWindow.ShowDialog();
            }
            finally
            {
                timer.Stop();
                button.Content = originalContent;
                button.IsEnabled = originalEnabled;
            }

            e.Handled = true;
        }

        private void InlineValueChoiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                AutoApplyCurrentItem();
                SetInlineItemExpanded(item, expand: true);
                e.Handled = true;
            }
        }

        private void InlineValueBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: Item item })
                LoadItemEditor(item);
        }

        private void InlineValueBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && box.Tag is Item item)
            {
                string oldValue = item.Value?.ToString() ?? "";
                if (!string.Equals(oldValue, box.Text, StringComparison.Ordinal))
                {
                    item.Value = box.Text;
                    RecordInlineValueUsage(item, box.Text);
                    MarkUnsaved();
                    LoadItemEditor(item);
                    PopulateTreeView(SearchFilterBox.Text);
                }
            }
        }

        private void InlineCommentsBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && box.Tag is Item item)
            {
                string existing = item.Comments ?? "";
                string typed = box.Text ?? "";
                string typedPrefix = UserDataService.ExtractPrefix(typed);
                string prefix = string.IsNullOrWhiteSpace(typedPrefix)
                    ? UserDataService.ExtractPrefix(existing)
                    : typedPrefix;
                var typedSuffixes = UserDataService.ExtractSuffixes(typed);
                var suffixes = typedSuffixes.Count > 0
                    ? typedSuffixes
                    : UserDataService.ExtractSuffixes(existing);
                string core = UserDataService.StripPrefixAndSuffix(typed);
                string newComment = UserDataService.BuildComment(prefix, core, suffixes);

                if (!string.Equals(existing, newComment, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(core) &&
                        ReferenceEquals(_inlineQuickCommentsDismissedItem, item))
                    {
                        _inlineQuickCommentsDismissedItem = null;
                    }
                    item.Comments = newComment;
                    MarkUnsaved();
                    LoadItemEditor(item);
                    PopulateTreeView(SearchFilterBox.Text);
                }
            }
        }

        private void InlinePrefixSuffixButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: InlinePrefixSuffixAction action })
                return;

            string existing = action.Item.Comments ?? "";
            string prefix = UserDataService.ExtractPrefix(existing);
            var suffixes = UserDataService.ExtractSuffixes(existing);
            string core = UserDataService.StripPrefixAndSuffix(existing);

            if (action.IsPrefix)
            {
                bool removing = action.Value.Equals(prefix, StringComparison.OrdinalIgnoreCase);
                prefix = removing ? "" : action.Value;
                if (!removing)
                    RecordInlineUsage(action.Item, action.Value, "prefix");
            }
            else
            {
                var existingSuffix = suffixes.FirstOrDefault(s => s.Equals(action.Value, StringComparison.OrdinalIgnoreCase));
                if (existingSuffix != null)
                    suffixes.Remove(existingSuffix);
                else
                {
                    suffixes.Add(action.Value);
                    RecordInlineUsage(action.Item, action.Value, "suffix");
                }
            }

            action.Item.Comments = UserDataService.BuildComment(prefix, core, suffixes);
            MarkUnsaved();
            LoadItemEditor(action.Item);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineDateSuffixButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item })
                return;

            string existing = item.Comments ?? "";
            string prefix = UserDataService.ExtractPrefix(existing);
            var suffixes = UserDataService.ExtractSuffixes(existing);
            string core = UserDataService.StripPrefixAndSuffix(existing);
            suffixes.RemoveAll(IsTimestampSuffix);
            suffixes.Add($"({DateTime.Now:M/d/yyyy h tt})");
            item.Comments = UserDataService.BuildComment(prefix, core, suffixes);
            MarkUnsaved();
            LoadItemEditor(item);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineAddPrefixSuffixButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InlineAddPrefixSuffixAction action })
                return;

            var dialog = action.IsPrefix
                ? new InputDialog("Enter new trade name (without brackets):", "Add Custom Trade")
                : new InputDialog("Enter new location (without parentheses):", "Add Custom Location");
            dialog.Owner = this;

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
            {
                if (action.IsPrefix)
                    _userDataService?.AddCustomPrefix(dialog.ResponseText);
                else
                    _userDataService?.AddCustomSuffix(dialog.ResponseText);

                LoadItemEditor(action.Item);
                PopulateTreeView(SearchFilterBox.Text);
            }

            e.Handled = true;
        }

        private void InlineExperimentalToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlineExperimentalToolAction action })
            {
                SetInlineDrawerOpen(action.Item, action.DrawerName, true);
                SaveInlineDrawerPreferences();
                PopulateInlineChecklist(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlineOtherToolsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item } button)
                return;

            var menu = new ContextMenu
            {
                MinWidth = 260,
                Padding = new Thickness(6),
                FontSize = Math.Max(16, _checklistFontSize + 1),
                Placement = PlacementMode.Bottom
            };
            if (!IsSpecialInlineDrawerRemembered(item, InlineNumberpadDrawer))
            {
                var numberpadItem = new MenuItem
                {
                    Header = "Numberpad",
                    IsCheckable = true,
                    IsChecked = false,
                    Tag = new InlineExperimentalToolAction(item, InlineNumberpadDrawer),
                    MinHeight = 54,
                    Padding = new Thickness(18, 12, 18, 12),
                    FontSize = Math.Max(16, _checklistFontSize + 1)
                };
                numberpadItem.Click += (s, _) =>
                {
                    if (s is MenuItem { Tag: InlineExperimentalToolAction action })
                    {
                        SetInlineDrawerOpen(action.Item, action.DrawerName, true);
                        SaveInlineDrawerPreferences();
                        PopulateInlineChecklist(SearchFilterBox.Text);
                    }
                };
                menu.Items.Add(numberpadItem);
            }

            if (menu.Items.Count == 0)
                return;

            button.ContextMenu = menu;
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void InlineNumberpadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InlineNumberpadAction action })
                return;

            LoadItemEditor(action.Item);
            SetInlineDrawerOpen(action.Item, InlineNumberpadDrawer, true);

            string key = action.Key;
            string currentValue = action.Item.Value?.ToString() ?? "";
            action.Item.Value = key == "CLEAR"
                ? ""
                : currentValue + key;

            if (!string.IsNullOrWhiteSpace(action.Item.Value?.ToString()))
                RecordInlineValueUsage(action.Item, action.Item.Value?.ToString() ?? "");

            MarkUnsaved();
            SaveInlineDrawerPreferences();
            LoadItemEditor(action.Item);
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlineNumberpadRangeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item })
                return;

            string key = GetInlineItemKey(item);
            InlineNumberpadRange? current = GetCustomNumberpadRange(key);
            InlineNumberpadRange? observed = GetObservedNumberpadRange(item);
            if (current != null)
            {
                var menu = new ContextMenu();
                var edit = new MenuItem { Header = "Edit custom range" };
                edit.Click += (_, _) => PromptForInlineNumberpadRange(item, current);
                menu.Items.Add(edit);

                var clear = new MenuItem { Header = "Clear custom range" };
                clear.Click += (_, _) =>
                {
                    _inlineDrawerPreferences.CustomNumberpadRanges.Remove(key);
                    _inlineDrawerPreferences.NumberpadRanges.Remove(key);
                    SaveInlineDrawerPreferences();
                    PopulateTreeView(SearchFilterBox.Text);
                };
                menu.Items.Add(clear);

                ((Button)sender).ContextMenu = menu;
                menu.PlacementTarget = (Button)sender;
                menu.IsOpen = true;
                e.Handled = true;
                return;
            }

            PromptForInlineNumberpadRange(item, observed);
            e.Handled = true;
        }

        private void PromptForInlineNumberpadRange(Item item, InlineNumberpadRange? current)
        {
            string key = GetInlineItemKey(item);
            string prompt = current == null
                ? "Enter custom numberpad slider range, like 8-14:"
                : $"Enter custom range, like 8-14:\nCurrent/auto: {current.Minimum:g}-{current.Maximum:g}\nBlank clears custom range.";

            var dialog = new InputDialog(prompt, "Numberpad Range")
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            string response = dialog.ResponseText.Trim();
            if (string.IsNullOrWhiteSpace(response))
            {
                _inlineDrawerPreferences.CustomNumberpadRanges.Remove(key);
                _inlineDrawerPreferences.NumberpadRanges.Remove(key);
            }
            else if (TryParseNumberpadRange(response, out var range))
            {
                bool allowFractions = !ShouldSuppressNumberpadFractions(item);
                if (TryParseNumberpadObservedValue(item.Value?.ToString() ?? "", out double currentValue))
                {
                    double snappedCurrent = SnapNumberpadSliderValue(currentValue, allowFractions);
                    range = EnsureRangeIncludesCurrentValue(range, snappedCurrent, allowFractions) ?? range;
                }

                _inlineDrawerPreferences.CustomNumberpadRanges[key] = range;
                _inlineDrawerPreferences.NumberpadRanges[key] = range;
            }
            else
            {
                MessageBox.Show(
                    this,
                    "Use a range like 8-14 or 8 to 14.",
                    "Numberpad Range",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            SaveInlineDrawerPreferences();
            PopulateTreeView(SearchFilterBox.Text);
        }

        private void InlineNumberpadTouchSlider_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            _inlineNumberpadSliderDragActive = true;
            Mouse.Capture(lane);
            PreviewInlineNumberpadSliderValue(action, e.GetPosition(lane).X);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_inlineNumberpadSliderDragActive ||
                sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            PreviewInlineNumberpadSliderValue(action, e.GetPosition(lane).X);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            CommitInlineNumberpadSliderValue(action, e.GetPosition(lane).X);
            _inlineNumberpadSliderDragActive = false;
            Mouse.Capture(null);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_inlineNumberpadSliderDragActive || Mouse.LeftButton == MouseButtonState.Pressed)
                return;

            _inlineNumberpadSliderDragActive = false;
            Mouse.Capture(null);
        }

        private void InlineNumberpadTouchSlider_TouchDown(object sender, TouchEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            _inlineNumberpadSliderDragActive = true;
            lane.CaptureTouch(e.TouchDevice);
            PreviewInlineNumberpadSliderValue(action, e.GetTouchPoint(lane).Position.X);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_TouchMove(object sender, TouchEventArgs e)
        {
            if (!_inlineNumberpadSliderDragActive ||
                sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            PreviewInlineNumberpadSliderValue(action, e.GetTouchPoint(lane).Position.X);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_TouchUp(object sender, TouchEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            CommitInlineNumberpadSliderValue(action, e.GetTouchPoint(lane).Position.X);
            _inlineNumberpadSliderDragActive = false;
            lane.ReleaseTouchCapture(e.TouchDevice);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_StylusDown(object sender, StylusDownEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            _inlineNumberpadSliderDragActive = true;
            Stylus.Capture(lane);
            PreviewInlineNumberpadSliderValue(action, e.GetPosition(lane).X);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_StylusMove(object sender, StylusEventArgs e)
        {
            if (!_inlineNumberpadSliderDragActive ||
                sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            PreviewInlineNumberpadSliderValue(action, e.GetPosition(lane).X);
            e.Handled = true;
        }

        private void InlineNumberpadTouchSlider_StylusUp(object sender, StylusEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: InlineNumberpadSliderAction action } lane)
                return;

            CommitInlineNumberpadSliderValue(action, e.GetPosition(lane).X);
            _inlineNumberpadSliderDragActive = false;
            Stylus.Capture(null);
            e.Handled = true;
        }

        private void PreviewInlineNumberpadSliderValue(InlineNumberpadSliderAction action, double x)
        {
            double valueNumber = GetInlineNumberpadSliderValueAt(action, x);
            string value = FormatNumberpadSliderValue(valueNumber, action.AllowFractions);
            action.ValueText.Text = value;
            UpdateInlineNumberpadSliderVisualAt(action, x);
            action.ValueText.UpdateLayout();
            action.Lane.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private void CommitInlineNumberpadSliderValue(InlineNumberpadSliderAction action, double x)
        {
            double valueNumber = GetInlineNumberpadSliderValueAt(action, x);
            string value = FormatNumberpadSliderValue(valueNumber, action.AllowFractions);
            if (string.Equals(action.Item.Value?.ToString() ?? "", value, StringComparison.Ordinal))
                return;

            action.Item.Value = value;
            action.ValueText.Text = value;
            UpdateInlineValueDisplays(action.Item, value);
            UpdateInlineNumberpadSliderVisual(action, valueNumber);

            LoadItemEditor(action.Item);
            RecordInlineValueUsage(action.Item, value);
            MarkUnsaved();
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            PopulateInlineChecklist(SearchFilterBox.Text);
        }

        private double GetInlineNumberpadSliderValueAt(InlineNumberpadSliderAction action, double x)
        {
            double thumbSize = action.Thumb.Width > 0 ? action.Thumb.Width : 52;
            double usableWidth = Math.Max(1, action.Lane.ActualWidth - thumbSize);
            double clampedX = Math.Max(thumbSize / 2, Math.Min(action.Lane.ActualWidth - thumbSize / 2, x));
            double ratio = (clampedX - thumbSize / 2) / usableWidth;
            double rawValue = action.Range.Minimum + ratio * (action.Range.Maximum - action.Range.Minimum);
            return SnapNumberpadSliderValue(rawValue, action.AllowFractions);
        }

        private void UpdateInlineNumberpadSliderVisual(InlineNumberpadSliderAction action, double value)
        {
            double thumbSize = action.Thumb.Width > 0 ? action.Thumb.Width : 52;
            double usableWidth = Math.Max(1, action.Lane.ActualWidth - thumbSize);
            double span = Math.Max(0.0001, action.Range.Maximum - action.Range.Minimum);
            double ratio = Math.Max(0, Math.Min(1, (value - action.Range.Minimum) / span));
            double left = ratio * usableWidth;

            action.Thumb.RenderTransform = new TranslateTransform(left, 0);
            action.Fill.Width = Math.Max(0, left + thumbSize / 2);
        }

        private void UpdateInlineNumberpadSliderVisualAt(InlineNumberpadSliderAction action, double x)
        {
            double thumbSize = action.Thumb.Width > 0 ? action.Thumb.Width : 52;
            double clampedX = Math.Max(thumbSize / 2, Math.Min(action.Lane.ActualWidth - thumbSize / 2, x));
            double left = clampedX - thumbSize / 2;

            action.Thumb.RenderTransform = new TranslateTransform(left, 0);
            action.Fill.Width = Math.Max(0, clampedX);
        }

        private void UpdateInlineValueDisplays(Item item, string value)
        {
            UpdateInlineValueDisplays(InlineChecklistPanel, item, value);
        }

        private void UpdateInlineValueDisplays(DependencyObject source, Item item, string value)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(source);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(source, i);
                if (child is TextBox box &&
                    ReferenceEquals(box.Tag, item) &&
                    box.GetValue(InlineValueDisplayProperty) is true &&
                    !string.Equals(box.Text, value, StringComparison.Ordinal))
                {
                    box.Text = value;
                    box.FontWeight = string.IsNullOrWhiteSpace(value) ? FontWeights.Normal : FontWeights.SemiBold;
                }

                UpdateInlineValueDisplays(child, item, value);
            }
        }

        private bool ShouldSuppressNumberpadFractions(Item item)
        {
            string text = $"{item.Name} {item.ControlName}".ToLowerInvariant();
            return text.Contains("quantity");
        }

        private bool TryParseNumberpadRange(string text, out InlineNumberpadRange range)
        {
            range = new InlineNumberpadRange();
            var match = Regex.Match(
                text,
                @"^\s*(-?\d+(?:\.\d+)?)\s*(?:-|to|,)\s*(-?\d+(?:\.\d+)?)\s*$",
                RegexOptions.IgnoreCase);

            if (!match.Success ||
                !double.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double min) ||
                !double.TryParse(match.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double max))
                return false;

            if (max < min)
                (min, max) = (max, min);

            if (Math.Abs(max - min) < 0.0001)
            return false;

            range = new InlineNumberpadRange { Minimum = min, Maximum = max };
            return true;
        }

        private InlineNumberpadRange? EnsureRangeIncludesCurrentValue(
            InlineNumberpadRange? range,
            double? currentValue,
            bool allowFractions)
        {
            if (range == null || !currentValue.HasValue)
                return range;

            double current = SnapNumberpadSliderValue(currentValue.Value, allowFractions);
            double min = range.Minimum;
            double max = range.Maximum;
            bool changed = false;

            if (current < min)
            {
                min = current;
                changed = true;
            }

            if (current > max)
            {
                max = current;
                changed = true;
            }

            return changed
                ? new InlineNumberpadRange { Minimum = min, Maximum = max }
                : range;
        }

        private InlineNumberpadRange? GetCustomNumberpadRange(string key)
        {
            if (_inlineDrawerPreferences.CustomNumberpadRanges.TryGetValue(key, out var customRange))
                return customRange;

            if (_inlineDrawerPreferences.NumberpadRanges.TryGetValue(key, out var legacyRange))
            {
                _inlineDrawerPreferences.CustomNumberpadRanges[key] = legacyRange;
                return legacyRange;
            }

            return null;
        }

        private InlineNumberpadRange? GetObservedNumberpadRange(Item item)
        {
            string key = GetInlineUsageKey(item, "value");
            if (!_inlineDrawerPreferences.ValueUsageCounts.TryGetValue(key, out var counts))
                return null;

            var values = counts.Keys
                .Select(value => TryParseNumberpadObservedValue(value, out double parsed)
                    ? (Parsed: true, Value: parsed)
                    : (Parsed: false, Value: 0d))
                .Where(value => value.Parsed)
                .Select(value => value.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            if (values.Count < 3)
                return null;

            double observedMin = values.First();
            double observedMax = values.Last();
            double span = observedMax - observedMin;
            if (span <= 0)
                return null;

            double margin = span * 0.05;
            var range = new InlineNumberpadRange
            {
                Minimum = NormalizeObservedRangeEdge(observedMin - margin, roundDown: true),
                Maximum = NormalizeObservedRangeEdge(observedMax + margin, roundDown: false)
            };

            if (TryParseNumberpadObservedValue(item.Value?.ToString() ?? "", out double currentValue))
            {
                bool allowFractions = !ShouldSuppressNumberpadFractions(item);
                range = EnsureRangeIncludesCurrentValue(range, currentValue, allowFractions) ?? range;
            }

            return range;
        }

        private bool TryParseNumberpadObservedValue(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value))
                return true;

            var fraction = Regex.Match(text, @"^\s*(-?\d+)?\s+(\d+)\s*/\s*(\d+)\s*$");
            if (fraction.Success &&
                double.TryParse(fraction.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(fraction.Groups[3].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double denominator) &&
                Math.Abs(denominator) > 0.0001)
            {
                double whole = 0;
                if (fraction.Groups[1].Success &&
                    !double.TryParse(fraction.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out whole))
                    return false;
                value = whole + numerator / denominator;
                return true;
            }

            fraction = Regex.Match(text, @"^\s*(-?\d+)\s*/\s*(\d+)\s*$");
            if (fraction.Success &&
                double.TryParse(fraction.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out numerator) &&
                double.TryParse(fraction.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out denominator) &&
                Math.Abs(denominator) > 0.0001)
            {
                value = numerator / denominator;
                return true;
            }

            return false;
        }

        private double NormalizeObservedRangeEdge(double value, bool roundDown)
        {
            return roundDown ? Math.Floor(value) : Math.Ceiling(value);
        }

        private double ParseDoubleValue(string? text, double fallback)
        {
            if (double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out double value))
                return value;
            if (double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value))
                return value;
            return fallback;
        }

        private string FormatNumberpadSliderValue(double value, bool allowFractions)
        {
            double snapped = SnapNumberpadSliderValue(value, allowFractions);
            if (!allowFractions)
                return Math.Round(snapped).ToString(CultureInfo.InvariantCulture);

            int whole = (int)Math.Floor(snapped);
            int quarters = (int)Math.Round((snapped - whole) / 0.25);
            if (quarters >= 4)
            {
                whole++;
                quarters = 0;
            }

            return quarters switch
            {
                1 => $"{whole} 1/4",
                2 => $"{whole} 1/2",
                3 => $"{whole} 3/4",
                _ => whole.ToString(CultureInfo.InvariantCulture)
            };
        }

        private double SnapNumberpadSliderValue(double value, bool allowFractions)
        {
            double increment = allowFractions ? 0.25 : 1;
            return Math.Round(value / increment) * increment;
        }

        private double GetNumberpadSliderTickFrequency(bool allowFractions)
        {
            return allowFractions ? 0.25 : 1;
        }

        private void InlineStandardToolsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                string key = GetInlineItemKey(item);
                _inlineDrawerPreferences.ItemClosedDrawers.Remove(key);
                if (!_inlineDrawerPreferences.ItemStandardToolOverrides.Contains(key))
                    _inlineDrawerPreferences.ItemStandardToolOverrides.Add(key);
                SaveInlineDrawerPreferences();
                PopulateInlineChecklist(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlinePrefixSuffixButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _inlineChipLongPressFired = false;
            _inlineChipLongPressAction = (sender as ToggleButton)?.Tag as InlinePrefixSuffixAction;
            if (_inlineChipLongPressAction == null)
                return;

            _inlineChipLongPressTimer?.Stop();
            _inlineChipLongPressTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700)
            };
            _inlineChipLongPressTimer.Tick += (_, _) =>
            {
                _inlineChipLongPressTimer?.Stop();
                _inlineChipLongPressFired = true;
                RemoveInlinePrefixSuffixChip(_inlineChipLongPressAction);
            };
            _inlineChipLongPressTimer.Start();
        }

        private void InlinePrefixSuffixButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_inlineHorizontalScrollStarted)
                CancelInlineChipLongPress();
        }

        private void InlinePrefixSuffixButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            CancelInlineChipLongPress();
            if (_inlineChipLongPressFired)
                e.Handled = true;
        }

        private void InlinePrefixSuffixButton_MouseLeave(object sender, MouseEventArgs e)
        {
            CancelInlineChipLongPress();
        }

        private void InlinePrefixSuffixButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ToggleButton { Tag: InlinePrefixSuffixAction action })
            {
                RemoveInlinePrefixSuffixChip(action);
                e.Handled = true;
            }
        }

        private void CancelInlineChipLongPress()
        {
            _inlineChipLongPressTimer?.Stop();
            _inlineChipLongPressTimer = null;
            _inlineChipLongPressAction = null;
        }

        private void RemoveInlinePrefixSuffixChip(InlinePrefixSuffixAction action)
        {
            if (_userDataService == null)
                return;

            bool removed = action.IsPrefix
                ? _userDataService.IsCustomPrefix(action.Value) && _userDataService.RemoveCustomPrefix(action.Value)
                : _userDataService.IsCustomSuffix(action.Value) && _userDataService.RemoveCustomSuffix(action.Value);

            if (!removed)
                return;

            string existing = action.Item.Comments ?? "";
            string prefix = UserDataService.ExtractPrefix(existing);
            var suffixes = UserDataService.ExtractSuffixes(existing);
            string core = UserDataService.StripPrefixAndSuffix(existing);

            if (action.IsPrefix && prefix.Equals(action.Value, StringComparison.OrdinalIgnoreCase))
                prefix = "";
            if (!action.IsPrefix)
                suffixes.RemoveAll(s => s.Equals(action.Value, StringComparison.OrdinalIgnoreCase));

            action.Item.Comments = UserDataService.BuildComment(prefix, core, suffixes);
            MarkUnsaved();
            LoadItemEditor(action.Item);
            PopulateTreeView(SearchFilterBox.Text);
        }

        private void InlineAddPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                LoadItemEditor(item);
                SelectPhotoButton_Click(sender, e);
                _expandedInlineItemKey = GetInlineItemKey(item);
                _expandedInlineItemInstance = item;
                PopulateInlineChecklist(SearchFilterBox.Text);
                KeepInlineItemHeaderVisible(item);
                e.Handled = true;
            }
        }

        private void InlineCameraButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                LoadItemEditor(item);
                CameraButton_Click(sender, e);
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlinePhotoThumbnail_Click(object sender, MouseButtonEventArgs e)
        {
            InlinePhotoAction? action = null;
            if (sender is FrameworkElement { Tag: InlinePhotoAction directAction })
                action = directAction;
            else if (e.OriginalSource is FrameworkElement { Tag: InlinePhotoAction sourceAction })
                action = sourceAction;

            if (action == null)
                return;

            LoadItemEditor(action.Item);
            _currentPhotoIndex = Math.Max(0, Math.Min(action.PhotoIndex, action.Item.Pictures.Count - 1));
            LoadCurrentPhoto();
            PhotoImage_Click(sender, e);
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void InlinePrevPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlinePhotoAction action })
            {
                LoadItemEditor(action.Item);
                _currentPhotoIndex = Math.Max(0, action.PhotoIndex - 1);
                LoadCurrentPhoto();
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlineNextPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlinePhotoAction action })
            {
                LoadItemEditor(action.Item);
                _currentPhotoIndex = Math.Min(action.Item.Pictures.Count - 1, action.PhotoIndex + 1);
                LoadCurrentPhoto();
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private void InlineDeletePhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: InlinePhotoAction action })
            {
                LoadItemEditor(action.Item);
                _currentPhotoIndex = Math.Max(0, Math.Min(action.PhotoIndex, action.Item.Pictures.Count - 1));
                DeletePhotoButton_Click(sender, e);
                PopulateTreeView(SearchFilterBox.Text);
                e.Handled = true;
            }
        }

        private async void InlineGetThreeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                await RunInlineAiToolAsync(item, InlineAiMode.GetThree);
                e.Handled = true;
            }
        }

        private async void InlineTranscribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Item item })
            {
                await RunInlineAiToolAsync(item, InlineAiMode.Transcribe);
                e.Handled = true;
            }
        }

        private async Task RunInlineAiToolAsync(Item item, InlineAiMode mode)
        {
            if (_grokClient == null)
                return;

            byte[]? photoData = GetInlinePhotoBytes(item);
            if (photoData == null)
                return;

            LoadItemEditor(item);
            _currentPhotoData = photoData;
            _inlineAiResults[item] = new InlineAiResult(mode, new List<string>(), IsLoading: true);
            PopulateTreeView(SearchFilterBox.Text);

            try
            {
                List<string> suggestions;
                if (mode == InlineAiMode.Transcribe)
                {
                    _isTranscribeMode = true;
                    suggestions = await _grokClient.TranscribeLabelMultiple(photoData);
                    _lastTranscriptionOptions = suggestions;
                }
                else
                {
                    _isTranscribeMode = false;
                    string sectionName = _currentInspection?.Sections?
                        .FirstOrDefault(s => s.Items.Contains(item))?.Name ?? "";
                    var quickComments = _quickCommentsService.GetSuggestions(
                        _currentInspectionCode ?? "",
                        item.Number ?? "",
                        maxCount: 10);
                    suggestions = await _grokClient.GetInspectionSuggestions(
                        photoData,
                        item.Number ?? "",
                        item.Name ?? "",
                        _currentInspectionCode ?? "",
                        sectionName,
                        quickComments,
                        "",
                        GetCurrentCommentForAi(item),
                        GetSelectedAiTone());
                }

                _inlineAiResults[item] = new InlineAiResult(mode, suggestions);
            }
            catch (Exception ex)
            {
                _inlineAiResults[item] = new InlineAiResult(mode, new List<string> { $"Error: {ex.Message}" });
            }

            PopulateTreeView(SearchFilterBox.Text);
        }

        private byte[]? GetInlinePhotoBytes(Item item)
        {
            if (item.Pictures == null || item.Pictures.Count == 0)
                return null;

            int index = ReferenceEquals(item, _editorLoadedItem)
                ? Math.Max(0, Math.Min(_currentPhotoIndex, item.Pictures.Count - 1))
                : 0;

            string? data = item.Pictures[index].Data;
            if (string.IsNullOrWhiteSpace(data))
                data = item.Pictures[index].ThumbnailData;
            if (string.IsNullOrWhiteSpace(data))
                return null;

            try
            {
                return Convert.FromBase64String(data);
            }
            catch
            {
                return null;
            }
        }

        private void InlineAiSuggestionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: InlineAiSuggestionAction action })
                return;

            LoadItemEditor(action.Item);

            if (action.Mode == InlineAiMode.Transcribe)
            {
                action.Item.Value = action.Suggestion;
                RecordInlineValueUsage(action.Item, action.Suggestion);
                TryAutoFillAdjacentItems(action.Suggestion);
                try { Clipboard.SetText(action.Suggestion); } catch { }
            }
            else
            {
                action.Item.Comments = BuildInlineCommentFromSuggestion(action.Item, action.Suggestion, appendToExisting: false);
                AutoSetFailIfApplicable();
                _inlineQuickCommentsDismissedItem = action.Item;
            }

            MarkUnsaved();
            LoadItemEditor(action.Item);
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            PopulateTreeView(SearchFilterBox.Text);
            e.Handled = true;
        }

        private void SetInlineItemValue(Item item, string value)
        {
            LoadItemEditor(item);
            string currentValue = item.Value?.ToString() ?? "";
            bool shouldExpandTools = !value.Equals(currentValue, StringComparison.OrdinalIgnoreCase) &&
                IsInlineFailValue(value);
            if (value.Equals(currentValue, StringComparison.OrdinalIgnoreCase))
            {
                item.Value = "";
            }
            else
            {
                bool changingToPass = value.Equals("Pass", StringComparison.OrdinalIgnoreCase);
                bool wasFail = currentValue.Equals("Fail", StringComparison.OrdinalIgnoreCase);
                bool hasComment = !string.IsNullOrWhiteSpace(item.Comments);
                bool hasClearablePics = item.Pictures != null && item.Pictures.Count > 0 && !item.IsPictureRequired;
                item.Value = value;
                if (changingToPass && wasFail && (hasComment || hasClearablePics))
                    PromptClearOnPass(hasComment, hasClearablePics);
            }

            RecordInlineValueUsage(item, value);
            MarkUnsaved();
            LoadItemEditor(item);
            RefreshEngDataPanel();
            RefreshEcDataPanel();
            if (shouldExpandTools && _inlineEditorMode)
            {
                _expandedInlineItemKey = GetInlineItemKey(item);
                _expandedInlineItemInstance = item;
                _inlineQuickCommentsDismissedItem = null;
            }
            PopulateTreeView(SearchFilterBox.Text);
        }

        private bool IsInlineFailValue(string value)
        {
            string normalized = value.Trim().ToLowerInvariant();
            return normalized == "fail" || normalized.Contains("defect");
        }

        private void RecordInlineValueUsage(Item item, string value)
        {
            RecordInlineUsage(item, value, "value");
        }

        private int GetInlineUsageCount(Item item, string value, string bucket = "value")
        {
            string key = GetInlineUsageKey(item, bucket);
            return _inlineDrawerPreferences.ValueUsageCounts.TryGetValue(key, out var counts) &&
                   counts.TryGetValue(value.Trim(), out int count)
                ? count
                : 0;
        }

        private void RecordInlineUsage(Item item, string value, string bucket)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string key = GetInlineUsageKey(item, bucket);
            if (!_inlineDrawerPreferences.ValueUsageCounts.TryGetValue(key, out var counts))
            {
                counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _inlineDrawerPreferences.ValueUsageCounts[key] = counts;
            }

            string normalizedValue = value.Trim();
            counts.TryGetValue(normalizedValue, out int current);
            counts[normalizedValue] = current + 1;
            SaveInlineDrawerPreferences();
        }

        private string GetInlineUsageKey(Item item, string bucket)
        {
            return $"{GetInlineItemKey(item)}|{bucket}";
        }

        private string GetInlineItemKey(Item item)
        {
            string inspectionType = _currentInspectionCode ?? _currentInspection?.InspectionCode ?? "unknown";
            return $"{inspectionType}|{NormalizeInlinePrompt(item.Name ?? "")}";
        }

        private static string NormalizeInlinePrompt(string prompt)
        {
            string normalized = Regex.Replace(prompt.ToLowerInvariant(), @"\s+", " ").Trim();
            return Regex.Replace(normalized, @"[^\p{L}\p{Nd}\s?]", "");
        }

        private bool IsInlineDrawerOpen(Item item, string drawerName)
        {
            if (drawerName == InlineToolSelectorDrawer || drawerName == InlineValueChoicesDrawer)
                return true;

            bool isSpecial = !InlineStandardDrawers.Contains(drawerName);
            if (isSpecial)
                return IsSpecialInlineDrawerRemembered(item, drawerName);

            string key = GetInlineItemKey(item);
            if (_inlineDrawerPreferences.ItemClosedDrawers.TryGetValue(key, out var closedForItem))
                return !closedForItem.Contains(drawerName);

            return !_inlineDrawerPreferences.DefaultClosedDrawers.Contains(drawerName);
        }

        private bool HasHiddenStandardTools(Item item)
        {
            foreach (string drawerName in InlineStandardDrawers)
            {
                if (!ShouldOfferInlineDrawer(item, drawerName))
                    continue;

                if (!IsInlineDrawerOpen(item, drawerName))
                    return true;
            }

            bool useAdminSuppression = _suppressAdministrativeTools &&
                IsAdministrativeItem(item) &&
                !HasStandardToolsOverride(item);
            return useAdminSuppression;
        }

        private bool HasAvailableOtherTools(Item item)
        {
            return !IsSpecialInlineDrawerRemembered(item, InlineNumberpadDrawer);
        }

        private void SetInlineDrawerOpen(Item item, string drawerName, bool open)
        {
            string key = GetInlineItemKey(item);
            bool isSpecial = !InlineStandardDrawers.Contains(drawerName);

            if (isSpecial)
            {
                if (!_inlineDrawerPreferences.ItemOpenedSpecialDrawers.TryGetValue(key, out var openedSpecial))
                {
                    openedSpecial = new List<string>();
                    _inlineDrawerPreferences.ItemOpenedSpecialDrawers[key] = openedSpecial;
                }

                if (open && !openedSpecial.Contains(drawerName))
                    openedSpecial.Add(drawerName);
                else if (!open)
                    openedSpecial.Remove(drawerName);
                return;
            }

            if (!_inlineDrawerPreferences.ItemClosedDrawers.TryGetValue(key, out var closed))
            {
                closed = new List<string>();
                _inlineDrawerPreferences.ItemClosedDrawers[key] = closed;
            }

            if (open)
                closed.Remove(drawerName);
            else if (!closed.Contains(drawerName))
                closed.Add(drawerName);
        }

        private bool IsSpecialInlineDrawerRemembered(Item item, string drawerName)
        {
            string key = GetInlineItemKey(item);
            return _inlineDrawerPreferences.ItemOpenedSpecialDrawers.TryGetValue(key, out var opened) &&
                   opened.Contains(drawerName);
        }

        private bool HasStandardToolsOverride(Item item)
        {
            return _inlineDrawerPreferences.ItemStandardToolOverrides.Contains(GetInlineItemKey(item));
        }

        private bool IsAdministrativeItem(Item item)
        {
            if (_currentInspection?.Sections == null)
                return false;

            foreach (var section in _currentInspection.Sections)
            {
                if (!section.Items.Contains(item))
                    continue;

                return section.Number == "1" ||
                       section.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true;
            }

            return false;
        }

        private bool ShouldOfferBeamDrawer(Item item)
        {
            string text = $"{_currentInspectionCode} {item.Name} {item.ControlName}".ToLowerInvariant();
            return text.Contains("cpp") ||
                   text.Contains("pre-pour") ||
                   text.Contains("prepour") ||
                   text.Contains("beam") ||
                   text.Contains("bottom of beam") ||
                   text.Contains("top of form");
        }

        private sealed class InlineDrawerPreferences
        {
            public List<string> DefaultClosedDrawers { get; set; } = new List<string>();
            public Dictionary<string, List<string>> ItemClosedDrawers { get; set; } = new Dictionary<string, List<string>>();
            public Dictionary<string, List<string>> ItemOpenedSpecialDrawers { get; set; } = new Dictionary<string, List<string>>();
            public List<string> ItemStandardToolOverrides { get; set; } = new List<string>();
            public Dictionary<string, Dictionary<string, int>> ValueUsageCounts { get; set; } = new Dictionary<string, Dictionary<string, int>>();
            public Dictionary<string, InlineNumberpadRange> NumberpadRanges { get; set; } = new Dictionary<string, InlineNumberpadRange>();
            public Dictionary<string, InlineNumberpadRange> CustomNumberpadRanges { get; set; } = new Dictionary<string, InlineNumberpadRange>();
        }

        private sealed class InlineNumberpadRange
        {
            public double Minimum { get; set; }
            public double Maximum { get; set; }
        }

        private sealed record InlineDrawerAction(Item Item, string DrawerName, bool Open);
        private sealed record InlineValueAction(Item Item, string Value);
        private sealed record InlineDesignAssist(Item Item, string Value, string Text, EnergyComplianceService.BannerState State, bool CanApply, string Source, string ToolTip);
        private sealed record InlineQuickCommentAction(Item Item, string Comment);
        private sealed record InlineCommentFlagAction(Item Item);
        private sealed record InlinePrefixSuffixAction(Item Item, string Value, bool IsPrefix);
        private sealed record InlineAddPrefixSuffixAction(Item Item, bool IsPrefix);
        private sealed record InlineExperimentalToolAction(Item Item, string DrawerName);
        private sealed record InlineNumberpadAction(Item Item, string Key);
        private sealed record InlineNumberpadSliderAction(
            Item Item,
            TextBlock ValueText,
            bool AllowFractions,
            InlineNumberpadRange Range,
            FrameworkElement Lane,
            Border Thumb,
            Border Fill);
        private sealed record InlinePhotoAction(Item Item, int PhotoIndex);
        private enum InlineAiMode { GetThree, Transcribe }
        private sealed record InlineAiResult(InlineAiMode Mode, List<string> Suggestions, bool IsLoading = false);
        private sealed record InlineAiSuggestionAction(Item Item, string Suggestion, InlineAiMode Mode);

        private void PopulateTreeView(string filter = null)
        {
            SectionsTreeView.Items.Clear();
            if (_currentInspection?.Sections == null) return;

            bool hasFilter = !string.IsNullOrWhiteSpace(filter);
            string filterLower = filter?.ToLower() ?? "";

            // SCI (Special Consult) uses Section 1 as actual content — no locking for SCI
            bool isSCI = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);

            foreach (var section in _currentInspection.Sections)
            {
                // Create section header with bulk action buttons
                var sectionPanel = new DockPanel();
                
                var sectionText = new TextBlock
                {
                    Text = $"{section.Number} - {section.Name}",
                    FontWeight = FontWeights.Bold,
                    FontSize = _checklistFontSize + 1,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 4, 2)
                };
                // sectionText added last so it fills remaining space
                
                // ── Live section fail counter: (x / y) ──
                // x = current fail count in this section, y = team avg fails per inspection
                int currentFailCount = section.Items.Count(i =>
                    (i.Value?.ToString()?.ToLower() ?? "").Contains("fail"));
                
                double? teamAvg = GetCurrentSectionAverage(section.Number);
                
                // Build a right-docked panel for counter + bulk buttons
                var sectionControlsPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
                
                if (teamAvg.HasValue)
                {
                    bool meetsOrExceedsAvg = currentFailCount >= teamAvg.Value && currentFailCount > 0;
                    
                    string counterDisplay = meetsOrExceedsAvg
                        ? $"({currentFailCount} / {teamAvg.Value}) ★"
                        : $"({currentFailCount} / {teamAvg.Value})";
                    
                    var countText = new TextBlock
                    {
                        Text = counterDisplay,
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = meetsOrExceedsAvg
                            ? new SolidColorBrush(Color.FromRgb(30, 80, 180))
                            : new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                        ToolTip = meetsOrExceedsAvg
                            ? $"★ Above team average! ({currentFailCount} vs {teamAvg.Value} avg)"
                            : $"Your fails: {currentFailCount}\nTeam avg: {teamAvg.Value} per inspection"
                    };
                    countText.Tag = "SectionCounter";
                    sectionControlsPanel.Children.Add(countText);
                }
                
                bool hasPassFailItems = section.Items.Any(i => 
                    (i.ControlName?.ToLower() ?? "").Contains("passfail"));
                
                if (hasPassFailItems)
                {
                    bool sectionHasFail = SectionHasFailValue(section);
                    var passBtn = new Button
                    {
                        Content = "P",
                        Width = 44,
                        Height = 32,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, 0, 3, 0),
                        Padding = new Thickness(0),
                        Background = new SolidColorBrush(Color.FromRgb(226, 247, 232)),
                        Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                        BorderThickness = new Thickness(3),
                        Tag = section,
                        ToolTip = "Set all Pass/Fail items in this section to Pass"
                    };
                    ConfigureSectionFillButtonState(passBtn, sectionHasFail);
                    passBtn.Click += SectionPassButton_Click;
                    sectionControlsPanel.Children.Add(passBtn);

                    var niBtn = new Button
                    {
                        Content = "NI",
                        Width = 44,
                        Height = 32,
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Margin = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(0),
                        Background = new SolidColorBrush(Color.FromRgb(236, 240, 245)),
                        Foreground = new SolidColorBrush(Color.FromRgb(82, 95, 112)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                        BorderThickness = new Thickness(3),
                        Tag = section,
                        ToolTip = "Set all Pass/Fail items in this section to NI"
                    };
                    ConfigureSectionFillButtonState(niBtn, sectionHasFail);
                    niBtn.Click += SectionNiButton_Click;
                    sectionControlsPanel.Children.Add(niBtn);
                }
                
                // Dock controls right, then section name fills remaining space
                DockPanel.SetDock(sectionControlsPanel, Dock.Right);
                sectionPanel.Children.Add(sectionControlsPanel);
                sectionPanel.Children.Add(sectionText); // Last child fills remaining space
                
                bool isAdminSection = section.Number == "1" ||
                    section.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true;

                var sectionNode = new TreeViewItem
                {
                    Header = sectionPanel,
                    Tag = section,
                    // Collapse the Administrative section when OFI filter is active — it has no fail items
                    IsExpanded = !(_ofiFilterActive && isAdminSection)
                };

                foreach (var item in section.Items)
                {
                    bool isLocked = !isSCI && IsProperNameItem(item);

                    // Locked items (proper names) — show greyed read-only row regardless of HidePic/HideCmt
                    if (isLocked)
                    {
                        if (hasFilter)
                        {
                            bool matchesFilter =
                                item.Name?.ToLower().Contains(filterLower) == true ||
                                item.Number?.ToLower().Contains(filterLower) == true ||
                                item.Value?.ToString()?.ToLower().Contains(filterLower) == true;
                            if (!matchesFilter) continue;
                        }
                        // OFI / REQ / INC filters: locked items never fail or count as required
                        if (_ofiFilterActive || _reqFilterActive || _incFilterActive) continue;

                        string lockedValue = item.Value?.ToString() ?? "";
                        string lockedLabel = string.IsNullOrEmpty(lockedValue)
                            ? (item.Name ?? "")
                            : $"{item.Name}: {lockedValue}";
                        var lockedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                        lockedRow.Children.Add(new TextBlock
                        {
                            Text = $"🔒 {item.Number}",
                            FontSize = _checklistFontSize,
                            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                            Margin = new Thickness(0, 0, 8, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        lockedRow.Children.Add(new TextBlock
                        {
                            Text = lockedLabel,
                            FontSize = _checklistFontSize - 1,
                            FontStyle = FontStyles.Italic,
                            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                            TextWrapping = TextWrapping.Wrap
                        });
                        sectionNode.Items.Add(new TreeViewItem
                        {
                            Header = lockedRow,
                            Tag = item,
                            ToolTip = "Read-only: this field contains a proper name and cannot be edited in RED"
                        });
                        continue;
                    }

                    if (!item.HidePicturesButton || !item.HideCommentsButton)
                    {
                        // Apply text filter if present
                        if (hasFilter)
                        {
                            bool matchesFilter = 
                                item.Name?.ToLower().Contains(filterLower) == true ||
                                item.Number?.ToLower().Contains(filterLower) == true ||
                                item.Comments?.ToLower().Contains(filterLower) == true;
                            
                            if (!matchesFilter) continue;
                        }
                        
                        // Apply OFI (Only Fail Items) filter if active
                        if (_ofiFilterActive)
                        {
                            string valueStr = item.Value?.ToString()?.ToLower() ?? "";
                            bool isFail = valueStr.Contains("fail") || 
                                          valueStr == "no" || 
                                          valueStr == "✗" || 
                                          valueStr.Contains("defect") ||
                                          valueStr.Contains("deficient");
                            if (!isFail) continue;
                        }
                        
                        // Apply REQ (Required Items Only) filter if active
                        if (_reqFilterActive)
                        {
                            // Show items that require value OR require pictures
                            if (!item.Required && !item.IsPictureRequired) continue;
                        }
                        
                        // Apply INC (Incomplete Required Items) filter if active
                        if (_incFilterActive)
                        {
                            // Show items that are incomplete:
                            // 1. Required value but no value entered, OR
                            // 2. Required pictures but no pictures added
                            bool hasRequiredValue = item.Required;
                            bool valueIsMissing = string.IsNullOrEmpty(item.Value?.ToString()?.Trim() ?? "");
                            bool requiresPictures = item.IsPictureRequired;
                            bool picturesAreMissing = item.Pictures.Count == 0;
                            
                            bool isIncomplete = (hasRequiredValue && valueIsMissing) || 
                                               (requiresPictures && picturesAreMissing);
                            
                            if (!isIncomplete) continue;
                        }

                        string indicator = "";
                        // For non-required items with photos: show 📷 in the number badge
                        // IsPictureRequired items get their own colored square badge below
                        if (item.Pictures.Count > 0 && !item.IsPictureRequired)
                            indicator += "📷";
                        if (!string.IsNullOrEmpty(item.Comments)) indicator += "💬";
                        
                        // Create number badge with status color
                        var numberBorder = new Border
                        {
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(4, 1, 4, 1),
                            Margin = new Thickness(0, 0, 5, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        var numberText = new TextBlock
                        {
                            Text = $"{indicator}{item.Number}",
                            FontSize = _checklistFontSize,
                            FontWeight = FontWeights.SemiBold
                        };
                        numberBorder.Child = numberText;
                        
                        // Create name text
                        // Required: maroon (★ + name) until VALUE requirement met → black
                        //   (picture status is tracked independently by the photo badge)
                        //   Exception: picture-only required items go black when picture is taken
                        // Non-required: gray until any value entered → black
                        var nameText = new TextBlock
                        {
                            FontSize = _checklistFontSize,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 2, 4, 2)
                        };
                        bool isRequired = item.Required || item.IsPictureRequired;
                        bool hasEnteredValue = !string.IsNullOrWhiteSpace(item.Value?.ToString());
                        if (isRequired)
                        {
                            // Text turns black when the VALUE portion is satisfied.
                            // For picture-only items (no value requirement), text turns black when picture is taken.
                            bool textFulfilled = item.Required
                                ? hasEnteredValue
                                : item.Pictures.Count > 0; // picture-only required
                            var starColor = textFulfilled ? Color.FromRgb(30, 30, 30) : Color.FromRgb(180, 0, 0);
                            var nameColor = textFulfilled ? Color.FromRgb(20, 20, 20) : Color.FromRgb(139, 0, 0);
                            nameText.Inlines.Add(new System.Windows.Documents.Run("★ ")
                            {
                                Foreground = new SolidColorBrush(starColor),
                                FontWeight = FontWeights.Bold
                            });
                            nameText.Inlines.Add(new System.Windows.Documents.Run(item.Name)
                            {
                                Foreground = new SolidColorBrush(nameColor)
                            });
                        }
                        else
                        {
                            var nameColor = hasEnteredValue ? Color.FromRgb(20, 20, 20) : Color.FromRgb(110, 110, 110);
                            nameText.Inlines.Add(new System.Windows.Documents.Run(item.Name)
                            {
                                Foreground = new SolidColorBrush(nameColor)
                            });
                        }
                        
                        // Use DockPanel: dock number left, dock deviation RIGHT (stays aligned), name fills and wraps
                        var itemPanel = new DockPanel { LastChildFill = true };
                        
                        // Bind MaxWidth to constrain the panel and force wrapping
                        var widthBinding = new System.Windows.Data.Binding("ActualWidth")
                        {
                            Source = SectionsTreeView,
                            Converter = new SubtractConverter(),
                            ConverterParameter = 50.0 // Account for TreeView padding/margins
                        };
                        itemPanel.SetBinding(DockPanel.MaxWidthProperty, widthBinding);
                        
                        DockPanel.SetDock(numberBorder, Dock.Left);
                        itemPanel.Children.Add(numberBorder);

                        // Photo-required badge: monochrome camera glyph (Segoe MDL2 Assets \uE722)
                        // so Foreground color works: maroon = required+missing, black = satisfied
                        if (item.IsPictureRequired)
                        {
                            bool hasPics = item.Pictures.Count > 0;
                            var camColor = hasPics
                                ? Color.FromRgb(30, 30, 30)   // Black: satisfied
                                : Color.FromRgb(139, 0, 0);   // Maroon: required but missing
                            var photoBadge = new Border
                            {
                                CornerRadius = new CornerRadius(3),
                                Padding = new Thickness(2, 1, 2, 1),
                                Margin = new Thickness(0, 0, 4, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                BorderThickness = hasPics ? new Thickness(0) : new Thickness(1.5),
                                BorderBrush = hasPics ? null : new SolidColorBrush(Color.FromRgb(139, 0, 0)),
                                Child = new TextBlock
                                {
                                    Text = "\uE722", // Camera glyph — Segoe MDL2 Assets
                                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                                    FontSize = 13,
                                    Foreground = new SolidColorBrush(camColor),
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            };
                            DockPanel.SetDock(photoBadge, Dock.Left);
                            itemPanel.Children.Add(photoBadge);
                        }

                        // Add deviation indicators FIRST (will be docked right) before nameText
                        
                        // Add deviation indicator from stats - ALWAYS add spacer to keep consistent wrapping
                        // ⭐ = 100%+ above avg, 😃 = 10%+ above, (blank) = close to norm
                        // ↓ = 40-59% below, ↓↓ = 60-79% below, ↓↓↓ = 80%+ below
                        var itemStats = _statsService.GetItemStats(
                            _currentInspectorName, _currentInspectionCode, item.Number);
                        
                        bool hasDeviationIndicator = false;
                        
                        if (itemStats != null)
                        {
                            string deviationIndicator = "";
                            bool isBlindSpot = false;
                            bool isStrength = false;
                            
                            if (itemStats.Deviation >= 100)
                            {
                                deviationIndicator = "⭐";  // Star: 100%+ above average
                                isStrength = true;
                            }
                            else if (itemStats.Deviation >= 10)
                            {
                                deviationIndicator = "😃";  // Smile: 10%+ above average
                                isStrength = true;
                            }
                            else if (itemStats.Deviation <= -80)
                            {
                                deviationIndicator = "↓↓↓";  // 80%+ below norm
                                isBlindSpot = true;
                            }
                            else if (itemStats.Deviation <= -60)
                            {
                                deviationIndicator = "↓↓";   // 60-79% below norm
                                isBlindSpot = true;
                            }
                            else if (itemStats.Deviation <= -40)
                            {
                                deviationIndicator = "↓";    // 40-59% below norm
                                isBlindSpot = true;
                            }
                            // else: within normal range, show nothing
                            
                            // If team rate is too low (<= 3%), the item is too rare to be a meaningful blind spot
                            // Show gray circle instead of down arrows
                            if (isBlindSpot && itemStats.TeamRate <= 3.0)
                            {
                                deviationIndicator = "⚪";  // Gray circle: insufficient team data
                                isBlindSpot = false;  // Don't style as blind spot
                            }
                            
                            if (!string.IsNullOrEmpty(deviationIndicator))
                            {
                                hasDeviationIndicator = true;
                                var deviationText = new TextBlock
                                {
                                    Text = deviationIndicator,
                                    FontSize = 12,
                                    VerticalAlignment = VerticalAlignment.Center,
                                    ToolTip = GetDeviationTooltip(itemStats)
                                };
                                
                                if (isBlindSpot)
                                {
                                    // Red badge with border for blind spot arrows
                                    deviationText.Foreground = new SolidColorBrush(Color.FromRgb(139, 0, 0)); // Dark red text
                                    var blindSpotBadge = new Border
                                    {
                                        Background = new SolidColorBrush(Color.FromRgb(255, 230, 230)), // Light red/pink background
                                        BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)), // Crimson border
                                        BorderThickness = new Thickness(1.5),
                                        CornerRadius = new CornerRadius(3),
                                        Padding = new Thickness(3, 1, 3, 1),
                                        Margin = new Thickness(4, 0, 0, 0),
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Child = deviationText
                                    };
                                    DockPanel.SetDock(blindSpotBadge, Dock.Right);
                                    itemPanel.Children.Add(blindSpotBadge);
                                }
                                else if (isStrength)
                                {
                                    // Yellow badge background for strength indicators
                                    var strengthBadge = new Border
                                    {
                                        Background = new SolidColorBrush(Color.FromRgb(255, 215, 0)), // Gold/Yellow
                                        CornerRadius = new CornerRadius(3),
                                        Padding = new Thickness(3, 1, 3, 1),
                                        Margin = new Thickness(4, 0, 0, 0),
                                        VerticalAlignment = VerticalAlignment.Center,
                                        Child = deviationText
                                    };
                                    DockPanel.SetDock(strengthBadge, Dock.Right);
                                    itemPanel.Children.Add(strengthBadge);
                                }
                                else
                                {
                                    deviationText.Text = $" {deviationIndicator}";
                                    DockPanel.SetDock(deviationText, Dock.Right);
                                    itemPanel.Children.Add(deviationText);
                                }
                            }
                        }
                        
                        // If no deviation indicator, add invisible spacer to reserve right-side space for consistent wrapping
                        if (!hasDeviationIndicator)
                        {
                            var spacer = new Border
                            {
                                Width = 35, // Reserve ~35px for potential indicator
                                Visibility = Visibility.Hidden
                            };
                            DockPanel.SetDock(spacer, Dock.Right);
                            itemPanel.Children.Add(spacer);
                        }
                        
                        // Add nameText LAST so it fills remaining space and wraps
                        itemPanel.Children.Add(nameText);
                        
                        var itemNode = new TreeViewItem
                        {
                            Header = itemPanel,
                            Tag = item
                        };
                        
                        // Add color coding to NUMBER BADGE based on status/value
                        // Also underline item name when a value (text/measurement) has been entered
                        if (item.Value != null)
                        {
                            string valueStr = item.Value.ToString()?.ToLower() ?? "";
                            bool hasValue = !string.IsNullOrWhiteSpace(valueStr);
                            
                            if (valueStr.Contains("pass") || valueStr == "yes" || valueStr == "✓" || valueStr == "ok")
                            {
                                numberBorder.Background = new SolidColorBrush(Color.FromRgb(144, 238, 144)); // Light green
                                numberText.Foreground = new SolidColorBrush(Color.FromRgb(0, 80, 0)); // Dark green text
                            }
                            else if (valueStr.Contains("fail") || valueStr == "no" || valueStr == "✗" || valueStr.Contains("defect"))
                            {
                                numberBorder.Background = new SolidColorBrush(Color.FromRgb(255, 150, 150)); // Light red
                                numberText.Foreground = new SolidColorBrush(Color.FromRgb(139, 0, 0)); // Dark red text
                            }
                            else if (valueStr.Contains("pending") || valueStr == "na" || valueStr.Contains("n/a") || valueStr == "ni")
                            {
                                numberBorder.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200)); // Light gray
                                numberText.Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)); // Dark gray text
                            }
                            else if (hasValue)
                            {
                                // Non-status value entered (measurements, text input, etc.)
                                // Underline the item name + blue number badge so it's clear this item is filled in
                                nameText.TextDecorations = TextDecorations.Underline;
                                numberBorder.Background = new SolidColorBrush(Color.FromRgb(173, 216, 230)); // Light blue
                                numberText.Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 102)); // Dark blue text
                            }
                        }
                        
                        sectionNode.Items.Add(itemNode);
                    }
                }

                // Only add section if it has matching items (or no filter active)
                bool anyFilterActive = hasFilter || _ofiFilterActive || _reqFilterActive || _incFilterActive;
                if (!anyFilterActive || sectionNode.Items.Count > 0)
                {
                    SectionsTreeView.Items.Add(sectionNode);
                }
            }

            // ── Update inspection-wide fail counter next to "Inspection Items" header ──
            UpdateInspectionTotalCounter();
            ApplyChecklistEditorMode();
            PopulateInlineChecklist(filter);
        }

        /// <summary>
        /// Update the inspection-wide (x / y) fail counter next to the "Inspection Items" header.
        /// </summary>
        private void UpdateInspectionTotalCounter()
        {
            try
            {
                double? teamAvg = GetCurrentInspectionAverage();
                
                if (teamAvg.HasValue && _currentInspection?.Sections != null)
                {
                    // Count all "fail" items across all sections (skip section 1 / Administrative for non-SCI)
                    bool isSCI = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);
                    int totalFails = _currentInspection.Sections
                        .Where(s => isSCI || (s.Number != "1" && !s.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true))
                        .SelectMany(s => s.Items)
                        .Count(i => (i.Value?.ToString()?.ToLower() ?? "").Contains("fail"));

                    bool meetsOrExceedsAvg = totalFails >= teamAvg.Value && totalFails > 0;

                    InspectionTotalCounter.Text = $"({totalFails} / {teamAvg.Value})";
                    InspectionTotalCounter.Foreground = meetsOrExceedsAvg
                        ? new SolidColorBrush(Color.FromRgb(30, 80, 180))
                        : new SolidColorBrush(Color.FromRgb(120, 120, 120));
                    InspectionTotalCounter.ToolTip = $"Your total fails: {totalFails}\nTeam avg: {teamAvg.Value} per inspection";
                    InspectionTotalCounter.Visibility = Visibility.Visible;

                    InspectionTotalStar.Visibility = meetsOrExceedsAvg ? Visibility.Visible : Visibility.Collapsed;
                    if (meetsOrExceedsAvg)
                    {
                        InspectionTotalStar.ToolTip = $"Above team average! ({totalFails} vs {teamAvg.Value} avg)";
                    }
                }
                else
                {
                    InspectionTotalCounter.Visibility = Visibility.Collapsed;
                    InspectionTotalStar.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                InspectionTotalCounter.Visibility = Visibility.Collapsed;
                InspectionTotalStar.Visibility = Visibility.Collapsed;
            }
        }

        private string GetDeviationTooltip(InspectionEditor.Services.ItemStats stats)
        {
            if (stats == null) return "";

            string type;
            if (stats.Deviation >= 100)
                type = "STRENGTH ⭐ (100%+ above team)";
            else if (stats.Deviation >= 10)
                type = "STRENGTH (10%+ above team)";
            else if (stats.Deviation <= -80)
                type = "BLIND SPOT (80%+ below team)";
            else if (stats.Deviation <= -60)
                type = "BLIND SPOT (60-79% below team)";
            else if (stats.Deviation <= -40)
                type = "BLIND SPOT (40-59% below team)";
            else
                type = "NEAR AVERAGE";

            return $"{type}\n" +
                   $"Your rate: {stats.InspectorRate:F1}%\n" +
                   $"Team avg: {stats.TeamRate:F1}%\n" +
                   $"Deviation: {stats.Deviation:+0;-0;0}%\n" +
                   $"({stats.InspectorCatches} catches in {stats.InspectorInspections} inspections)";
        }

        private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchFilterBox.Text;
            PopulateTreeView(filter);
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            // Clear search text
            SearchFilterBox.Text = "";
            
            // Turn off all other filters (ALL is now the active "filter")
            _ofiFilterActive = false;
            _reqFilterActive = false;
            _incFilterActive = false;
            
            // ALL active style (white bg, dark gray text, red border = active)
            ClearSearchButton.Background = new SolidColorBrush(Colors.White);
            ClearSearchButton.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
            ClearSearchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
            ClearSearchButton.BorderThickness = new Thickness(3);
            
            // Reset OFI button to inactive state (red bg, white text)
            OfiFilterButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
            OfiFilterButton.Foreground = new SolidColorBrush(Colors.White);
            OfiFilterButton.BorderThickness = new Thickness(0);
            
            // Reset REQ button to inactive state (blue bg, white text)
            ReqFilterButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
            ReqFilterButton.Foreground = new SolidColorBrush(Colors.White);
            ReqFilterButton.BorderThickness = new Thickness(0);
            
            // Reset INC button to inactive state (amber bg, white text)
            IncFilterButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
            IncFilterButton.Foreground = new SolidColorBrush(Colors.White);
            IncFilterButton.BorderThickness = new Thickness(0);
            
            // Defer tree rebuild to reduce UI freeze - let button states render first
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PopulateTreeView("");
                SearchFilterBox.Focus();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OfiFilterButton_Click(object sender, RoutedEventArgs e)
        {
            // Radio button behavior: turn off other filters when this is activated
            if (!_ofiFilterActive)
            {
                // Turning OFI on - deactivate others
                _ofiFilterActive = true;
                _reqFilterActive = false;
                _incFilterActive = false;
                
                // OFI active style (red border = active)
                OfiFilterButton.Background = new SolidColorBrush(Colors.White);
                OfiFilterButton.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
                OfiFilterButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
                OfiFilterButton.BorderThickness = new Thickness(3);
                
                // REQ inactive style
                ReqFilterButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
                ReqFilterButton.Foreground = new SolidColorBrush(Colors.White);
                ReqFilterButton.BorderThickness = new Thickness(0);
                
                // INC inactive style
                IncFilterButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                IncFilterButton.Foreground = new SolidColorBrush(Colors.White);
                IncFilterButton.BorderThickness = new Thickness(0);
                
                // ALL inactive style
                ClearSearchButton.Background = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
                ClearSearchButton.Foreground = new SolidColorBrush(Colors.White);
                ClearSearchButton.BorderThickness = new Thickness(0);
            }
            else
            {
                // Toggle off - activate ALL instead
                _ofiFilterActive = false;
                
                OfiFilterButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
                OfiFilterButton.Foreground = new SolidColorBrush(Colors.White);
                OfiFilterButton.BorderThickness = new Thickness(0);
                
                // Activate ALL
                ClearSearchButton.Background = new SolidColorBrush(Colors.White);
                ClearSearchButton.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
                ClearSearchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
                ClearSearchButton.BorderThickness = new Thickness(3);
            }

            // Refresh tree with current search filter
            PopulateTreeView(SearchFilterBox.Text);
            
            // When activating OFI filter, jump to first fail item and load it in the editor
            if (_ofiFilterActive)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (TreeViewItem sectionNode in SectionsTreeView.Items)
                    {
                        if (sectionNode.Tag is Section)
                        {
                            foreach (TreeViewItem itemNode in sectionNode.Items)
                            {
                                if (itemNode.Tag is Item item)
                                {
                                    string itemValue = item.Value?.ToString()?.ToLower() ?? "";
                                    if (itemValue.Contains("fail"))
                                    {
                                        sectionNode.IsExpanded = true;
                                        itemNode.IsSelected = true;
                                        itemNode.BringIntoView();
                                        LoadItemEditor(item);
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void ReqFilterButton_Click(object sender, RoutedEventArgs e)
        {
            // Radio button behavior: turn off other filters when this is activated
            if (!_reqFilterActive)
            {
                // Turning REQ on - deactivate others
                _reqFilterActive = true;
                _ofiFilterActive = false;
                _incFilterActive = false;
                
                // REQ active style (red border = active)
                ReqFilterButton.Background = new SolidColorBrush(Colors.White);
                ReqFilterButton.Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
                ReqFilterButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
                ReqFilterButton.BorderThickness = new Thickness(3);
                
                // OFI inactive style
                OfiFilterButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
                OfiFilterButton.Foreground = new SolidColorBrush(Colors.White);
                OfiFilterButton.BorderThickness = new Thickness(0);
                
                // INC inactive style
                IncFilterButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                IncFilterButton.Foreground = new SolidColorBrush(Colors.White);
                IncFilterButton.BorderThickness = new Thickness(0);
                
                // ALL inactive style
                ClearSearchButton.Background = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
                ClearSearchButton.Foreground = new SolidColorBrush(Colors.White);
                ClearSearchButton.BorderThickness = new Thickness(0);
            }
            else
            {
                // Toggle off - activate ALL instead
                _reqFilterActive = false;
                
                ReqFilterButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
                ReqFilterButton.Foreground = new SolidColorBrush(Colors.White);
                ReqFilterButton.BorderThickness = new Thickness(0);
                
                // Activate ALL
                ClearSearchButton.Background = new SolidColorBrush(Colors.White);
                ClearSearchButton.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
                ClearSearchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
                ClearSearchButton.BorderThickness = new Thickness(3);
            }

            // Refresh tree with current search filter
            PopulateTreeView(SearchFilterBox.Text);
        }

        private void IncFilterButton_Click(object sender, RoutedEventArgs e)
        {
            // Radio button behavior: turn off other filters when this is activated
            if (!_incFilterActive)
            {
                // Turning INC on - deactivate others
                _incFilterActive = true;
                _ofiFilterActive = false;
                _reqFilterActive = false;
                
                // INC active style (red border = active)
                IncFilterButton.Background = new SolidColorBrush(Colors.White);
                IncFilterButton.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                IncFilterButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
                IncFilterButton.BorderThickness = new Thickness(3);
                
                // OFI inactive style
                OfiFilterButton.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)); // #DC2626
                OfiFilterButton.Foreground = new SolidColorBrush(Colors.White);
                OfiFilterButton.BorderThickness = new Thickness(0);
                
                // REQ inactive style
                ReqFilterButton.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); // #2563EB
                ReqFilterButton.Foreground = new SolidColorBrush(Colors.White);
                ReqFilterButton.BorderThickness = new Thickness(0);
                
                // ALL inactive style
                ClearSearchButton.Background = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
                ClearSearchButton.Foreground = new SolidColorBrush(Colors.White);
                ClearSearchButton.BorderThickness = new Thickness(0);
            }
            else
            {
                // Toggle off - activate ALL instead
                _incFilterActive = false;
                
                IncFilterButton.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                IncFilterButton.Foreground = new SolidColorBrush(Colors.White);
                IncFilterButton.BorderThickness = new Thickness(0);
                
                // Activate ALL
                ClearSearchButton.Background = new SolidColorBrush(Colors.White);
                ClearSearchButton.Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 85)); // #555
                ClearSearchButton.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 20, 60)); // Crimson red = active
                ClearSearchButton.BorderThickness = new Thickness(3);
            }

            PopulateTreeView(SearchFilterBox.Text);
        }

        private void TextSizeUp_Click(object sender, RoutedEventArgs e)
        {
            if (_checklistFontSize < 22) // Max size
            {
                _checklistFontSize += 1;
                PopulateTreeView(SearchFilterBox.Text);
                SavePreferences();
            }
        }

        private void TextSizeDown_Click(object sender, RoutedEventArgs e)
        {
            if (_checklistFontSize > 9) // Min size
            {
                _checklistFontSize -= 1;
                PopulateTreeView(SearchFilterBox.Text);
                SavePreferences();
            }
        }

        private async void MessagesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInspection == null)
            {
                MessageBox.Show("Load an inspection first to view the messages again.", "No Inspection Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await ShowRulesWindowAsync(false);
        }

        private void SectionNavRight_Click(object sender, RoutedEventArgs e)
        {
            if (SectionsTreeView.Items.Count == 0) return;

            int currentIndex = -1;
            for (int i = 0; i < SectionsTreeView.Items.Count; i++)
            {
                if (SectionsTreeView.Items[i] is TreeViewItem item && item.IsSelected)
                { currentIndex = i; break; }
                if (SectionsTreeView.Items[i] is TreeViewItem sectionItem)
                {
                    foreach (TreeViewItem child in sectionItem.Items)
                    {
                        if (child.IsSelected) { currentIndex = i; break; }
                    }
                }
            }

            int nextIndex = currentIndex + 1;
            if (nextIndex >= SectionsTreeView.Items.Count) nextIndex = 0;

            if (SectionsTreeView.Items[nextIndex] is TreeViewItem nextSection)
            {
                nextSection.IsSelected = true;
                ScrollSectionToTop(nextSection);
            }
        }

        private void SectionNavLeft_Click(object sender, RoutedEventArgs e)
        {
            if (SectionsTreeView.Items.Count == 0) return;

            int currentIndex = -1;
            for (int i = 0; i < SectionsTreeView.Items.Count; i++)
            {
                if (SectionsTreeView.Items[i] is TreeViewItem item && item.IsSelected)
                { currentIndex = i; break; }
                if (SectionsTreeView.Items[i] is TreeViewItem sectionItem)
                {
                    foreach (TreeViewItem child in sectionItem.Items)
                    {
                        if (child.IsSelected) { currentIndex = i; break; }
                    }
                }
            }

            int prevIndex = currentIndex - 1;
            if (prevIndex < 0) prevIndex = SectionsTreeView.Items.Count - 1;

            if (SectionsTreeView.Items[prevIndex] is TreeViewItem prevSection)
            {
                prevSection.IsSelected = true;
                ScrollSectionToTop(prevSection);
            }
        }

        /// <summary>
        /// Scrolls the TreeView so that the given section header appears at the top of the
        /// visible area instead of the bottom (which is what BringIntoView() does).
        /// </summary>
        private void ScrollSectionToTop(TreeViewItem sectionItem)
        {
            // BringIntoView first to ensure the item is realized in the visual tree
            sectionItem.BringIntoView();

            // After layout completes, reposition so the section is at the top
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(SectionsTreeView);
                if (scrollViewer == null) return;
                try
                {
                    var transform = sectionItem.TransformToAncestor(scrollViewer);
                    var pos = transform.Transform(new Point(0, 0));
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + pos.Y);
                }
                catch { }
            }));
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void SectionPassButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Section section)
            {
                if (SectionHasFailValue(section))
                {
                    e.Handled = true;
                    return;
                }

                SetSectionItemsValue(section, "Pass");
                e.Handled = true; // Prevent section from expanding/collapsing
            }
        }

        private void SectionNiButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Section section)
            {
                if (SectionHasFailValue(section))
                {
                    e.Handled = true;
                    return;
                }

                SetSectionItemsValue(section, "NI");
                e.Handled = true; // Prevent section from expanding/collapsing
            }
        }

        private static bool SectionHasFailValue(Section section)
        {
            return section.Items.Any(item =>
            {
                string controlName = item.ControlName?.ToLowerInvariant() ?? "";
                if (!controlName.Contains("passfail"))
                    return false;

                string value = item.Value?.ToString()?.Trim().ToLowerInvariant() ?? "";
                return value.Contains("fail") ||
                       value.Contains("defect") ||
                       value.Contains("deficient");
            });
        }

        private void SetSectionItemsValue(Section section, string value)
        {
            int count = 0;
            foreach (var item in section.Items)
            {
                string controlName = item.ControlName?.ToLower() ?? "";
                // Only set value for passfail-type items (not yesno, text, lookup, etc.)
                bool isPassFailType = controlName.Contains("passfail");
                
                if (isPassFailType)
                {
                    item.Value = value;
                    count++;
                }
            }
            
            if (count > 0)
            {
                MarkUnsaved();
                PopulateTreeView(SearchFilterBox.Text);
                
                // If an item from this section is currently loaded, refresh its status controls
                if (_editorLoadedItem != null && section.Items.Contains(_editorLoadedItem))
                {
                    LoadStatusControls(_editorLoadedItem);
                }
            }
        }

        private void DuplicateItemButton_Click(object sender, RoutedEventArgs e)
        {
            // Use _editorLoadedItem - this is set ONLY when LoadItemEditor is called
            // and won't change due to accidental touch/scroll events
            if (_currentInspection?.Sections == null || _editorLoadedItem == null) return;

            // Persist whatever is currently in the UI (comment text, value) to the original item
            // before switching the editor to the new blank duplicate.
            // CommentsTextBox_TextChanged does NOT write back to the model — SyncCurrentItemFromUI does.
            SyncCurrentItemFromUI();

            Item itemToDuplicate = _editorLoadedItem;
            
            // Find the section containing this item
            Section? parentSection = null;
            int itemIndex = -1;
            foreach (var section in _currentInspection.Sections)
            {
                int idx = section.Items.IndexOf(itemToDuplicate);
                if (idx >= 0)
                {
                    parentSection = section;
                    itemIndex = idx;
                    break;
                }
            }
            
            if (parentSection == null || itemIndex < 0) return;

            // Calculate ResultSortOrder - count existing items with same Number
            int resultSortOrder = parentSection.Items
                .Where(i => i.Number == itemToDuplicate.Number)
                .Count(); // This will be 1 for first duplicate (original is 0)

            // Create a duplicate matching INS format:
            // - Same ItemId, Name, Number, ControlName, DisplayLabel
            // - IsCopied = true, ResultSortOrder incremented
            // - Value, Comments, Pictures reset (fresh entry)
            var newItem = new Item
            {
                ItemId = itemToDuplicate.ItemId,  // Keep same ItemId
                Name = itemToDuplicate.Name,       // Keep same Name
                Number = itemToDuplicate.Number,   // Keep same Number
                ControlName = itemToDuplicate.ControlName,
                DisplayLabel = itemToDuplicate.DisplayLabel,
                ValueList = itemToDuplicate.ValueList != null ? new List<string>(itemToDuplicate.ValueList) : null,
                Required = itemToDuplicate.Required,
                SortOrder = itemToDuplicate.SortOrder,
                HidePicturesButton = itemToDuplicate.HidePicturesButton,
                HideCommentsButton = itemToDuplicate.HideCommentsButton,
                // Duplicate-specific fields
                IsCopied = true,
                ResultSortOrder = resultSortOrder,
                // Reset values for fresh entry
                Value = null,
                Comments = null,
                Pictures = new List<Picture>(),
                // Copy ExtensionData for any other properties
                ExtensionData = itemToDuplicate.ExtensionData != null 
                    ? new Dictionary<string, object>(itemToDuplicate.ExtensionData) 
                    : new Dictionary<string, object>()
            };

            // Find the last item with same Number and insert after it
            int insertIndex = itemIndex;
            for (int i = itemIndex; i < parentSection.Items.Count; i++)
            {
                if (parentSection.Items[i].Number == itemToDuplicate.Number)
                    insertIndex = i;
                else
                    break;
            }
            parentSection.Items.Insert(insertIndex + 1, newItem);

            // Refresh tree with flag to prevent selection handler interference
            _isRefreshingTree = true;
            string filter = SearchFilterBox.Text;
            PopulateTreeView(string.IsNullOrWhiteSpace(filter) ? null : filter);

            // Select the new item in the tree and load its editor
            // Keep _isRefreshingTree=true until AFTER selection to prevent handler interference
            foreach (TreeViewItem sectionNode in SectionsTreeView.Items)
            {
                foreach (TreeViewItem itemNode in sectionNode.Items)
                {
                    if (itemNode.Tag == newItem)
                    {
                        itemNode.IsSelected = true;
                        itemNode.BringIntoView();
                        break;
                    }
                }
            }
            
            // NOW set flag to false and load the editor
            _isRefreshingTree = false;
            _currentItem = newItem; // Explicitly set current item
            LoadItemEditor(newItem);
            
            _hasUnsavedChanges = true;
        }

        private void RemoveDuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            // Only allow removing duplicated items (IsCopied = true)
            if (_editorLoadedItem == null || !_editorLoadedItem.IsCopied) return;
            if (_currentInspection?.Sections == null) return;

            Item itemToRemove = _editorLoadedItem;
            
            // Find the section containing this item
            Section? parentSection = null;
            int itemIndex = -1;
            foreach (var section in _currentInspection.Sections)
            {
                int idx = section.Items.IndexOf(itemToRemove);
                if (idx >= 0)
                {
                    parentSection = section;
                    itemIndex = idx;
                    break;
                }
            }
            
            if (parentSection == null || itemIndex < 0) return;

            // Find an item to select after removal (prefer previous item with same number, then any previous)
            Item? nextSelection = null;
            for (int i = itemIndex - 1; i >= 0; i--)
            {
                if (parentSection.Items[i].Number == itemToRemove.Number)
                {
                    nextSelection = parentSection.Items[i];
                    break;
                }
            }
            if (nextSelection == null && itemIndex > 0)
            {
                nextSelection = parentSection.Items[itemIndex - 1];
            }
            else if (nextSelection == null && parentSection.Items.Count > 1)
            {
                nextSelection = parentSection.Items[itemIndex + 1 < parentSection.Items.Count ? itemIndex + 1 : 0];
            }

            // Remove the item
            parentSection.Items.Remove(itemToRemove);
            
            // Refresh tree
            _isRefreshingTree = true;
            string filter = SearchFilterBox.Text;
            PopulateTreeView(string.IsNullOrWhiteSpace(filter) ? null : filter);

            // Select the next item
            if (nextSelection != null)
            {
                foreach (TreeViewItem sectionNode in SectionsTreeView.Items)
                {
                    foreach (TreeViewItem itemNode in sectionNode.Items)
                    {
                        if (itemNode.Tag == nextSelection)
                        {
                            itemNode.IsSelected = true;
                            itemNode.BringIntoView();
                            break;
                        }
                    }
                }
            }
            
            _isRefreshingTree = false;
            if (nextSelection != null)
            {
                LoadItemEditor(nextSelection);
            }
            else
            {
                // No items left to select, hide editor
                EditorPanel.Visibility = Visibility.Collapsed;
                _currentItem = null;
                _editorLoadedItem = null;
            }
            
            MarkUnsaved();
        }

        private void InlineDuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item })
                return;

            LoadItemEditor(item);
            DuplicateItemButton_Click(sender, e);
            if (_editorLoadedItem != null)
            {
                _expandedInlineItemInstance = _editorLoadedItem;
                _expandedInlineItemKey = GetInlineItemKey(_editorLoadedItem);
                _inlineQuickCommentsDismissedItem = null;
                PopulateInlineChecklist(SearchFilterBox.Text);
                KeepInlineItemHeaderVisible(_editorLoadedItem);
            }

            e.Handled = true;
        }

        private void InlineRemoveDuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Item item })
                return;

            LoadItemEditor(item);
            RemoveDuplicateButton_Click(sender, e);
            _inlineQuickCommentsDismissedItem = null;
            if (_editorLoadedItem != null)
            {
                _expandedInlineItemInstance = _editorLoadedItem;
                _expandedInlineItemKey = GetInlineItemKey(_editorLoadedItem);
                PopulateInlineChecklist(SearchFilterBox.Text);
                KeepInlineItemHeaderVisible(_editorLoadedItem);
            }
            else
            {
                _expandedInlineItemInstance = null;
                _expandedInlineItemKey = null;
                PopulateInlineChecklist(SearchFilterBox.Text);
            }

            e.Handled = true;
        }

        private void SectionsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_isRefreshingTree) return; // Skip during tree refresh to avoid wrong item selection
            if (_scrollStarted) return; // Skip during scroll to prevent accidental item changes
            
            AutoApplyCurrentItem(); // Auto-apply when clicking a different item in the tree
            if (e.NewValue is TreeViewItem treeItem && treeItem.Tag is Item item)
                LoadItemEditor(item);
        }

        private void LoadItemEditor(Item item)
        {
            _isLoadingEditor = true; // Suppress prefix/suffix button handlers during load
            try
            {
            _currentItem = item;
            _currentSection = _currentInspection?.Sections?.FirstOrDefault(
                s => s.Items.Any(i => i.ItemId == item.ItemId && i.ResultSortOrder == item.ResultSortOrder));
            _editorLoadedItem = item; // Track specifically what's in editor for Duplicate
            _currentPhotoData = null;
            CameraButton.Content = "📷 Camera"; // Reset camera button text when switching items
            _currentPhotoIndex = 0;
            SelectItemPanel.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;

            ItemNumberText.Text = $"Item {item.Number}";
            ItemQuestionText.Text = ExpandAbbreviations(item.Name);
            UpdateSlabCableInfoBanner(item);

            // Update prev/next navigation buttons
            UpdateNavButtons(item);
            
            // Show Remove button only for duplicated items (IsCopied = true)
            RemoveDuplicateButton.Visibility = item.IsCopied ? Visibility.Visible : Visibility.Collapsed;

            // Load status/value controls based on ControlName
            LoadStatusControls(item);

            // Load photo with navigation support
            LoadCurrentPhoto();

            string existingComment = item.Comments ?? "";
            CommentsTextBox.Text = existingComment;
            UpdateClassicSpecialistFlagButtonState();

            // Show quick suggestions overlay if comment is empty
            UpdateQuickSuggestionsOverlay();

            string existingPrefix = UserDataService.ExtractPrefix(existingComment);
            _selectedPrefix = string.IsNullOrEmpty(existingPrefix) ? null : existingPrefix;
            foreach (var btn in _prefixButtons)
                btn.IsChecked = (btn.Tag?.ToString() == existingPrefix);

            // Always reset suffixes when loading a new item
            // Only pre-select if the item already has suffixes in its existing comment
            var existingSuffixes = UserDataService.ExtractSuffixes(existingComment);
            _selectedSuffixes = existingSuffixes; // Will be empty list if no existing suffixes
            foreach (var btn in _suffixButtons)
                btn.IsChecked = existingSuffixes.Contains(btn.Tag?.ToString() ?? "");

            LoadSavedComments();

            SuggestionsStack.Children.Clear();
            SuggestionsStack.Children.Add(NoSuggestionsText);
            NoSuggestionsText.Visibility = Visibility.Visible;
            }
            finally
            {
                _isLoadingEditor = false; // Re-enable prefix/suffix button handlers
            }
        }

        /// <summary>
        /// Returns true for items that contain proper names or personal contact info
        /// (Superintendent, Customer Name/Phone/Email, Sales Agent, etc.) that RED
        /// should never overwrite. These are shown greyed in the tree and read-only
        /// in the editor. SCI inspections are exempt — their Section 1 is real content.
        /// </summary>
        private static bool IsProperNameItem(Item item)
        {
            string controlName = (item.ControlName ?? "").ToLower();

            // SearchLookup is always a proper-name picker (e.g. Superintendent)
            if (controlName == "searchlookup") return true;

            // YesNo/PassFail controls are never proper names — they're checkboxes
            // (e.g. "Superintendent on site" YesNo in IEF/IER should NOT be locked)
            if (controlName is "yesno" or "yesnonani" or "passfail" or "passfailnani") return false;

            string name = (item.Name ?? "").ToLower();
            return name.Contains("superintendent") ||
                   name.Contains("inspector")      ||
                   name.Contains(" name")          ||   // "Customer Name", "Sales Agent Name"
                   name.Contains("phone")          ||
                   name.Contains("email");
        }

        private string ExpandAbbreviations(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            
            // Expand common inspection abbreviations (whole word matching, ALL CAPS)
            var replacements = new Dictionary<string, string>
            {
                { "TOF", "TOP OF FORM" },
                { "BOB", "BOTTOM OF BEAM" },
                { "TOG", "TOP OF GRADE" },
                { "BW", "BEAM WIDTH" },
                { "SD", "SLAB DEPTH" },
                { "BD", "BEAM DEPTH" }
            };
            
            foreach (var kvp in replacements)
            {
                // Use word boundary replacement to avoid partial matches
                text = System.Text.RegularExpressions.Regex.Replace(
                    text, 
                    $@"\b{kvp.Key}\b", 
                    kvp.Value, 
                    System.Text.RegularExpressions.RegexOptions.None);
            }
            
            return text;
        }

        private List<Item> GetVisibleItems()
        {
            // Get flat list of all items excluding Administrative section
            var items = new List<Item>();
            if (_currentInspection?.Sections == null) return items;
            
            // SCI uses Section 1 as actual content — include all its items, no locking
            bool isSCI = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);

            foreach (var section in _currentInspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    // Locked items (proper names) are visible in the tree but excluded from prev/next nav
                    if (!isSCI && IsProperNameItem(item)) continue;
                    if (!item.HidePicturesButton || !item.HideCommentsButton)
                        items.Add(item);
                }
            }
            return items;
        }

        private void UpdateNavButtons(Item currentItem)
        {
            var items = GetVisibleItems();
            int index = items.IndexOf(currentItem);
            
            PrevItemButton.IsEnabled = index > 0;
            NextItemButton.IsEnabled = index >= 0 && index < items.Count - 1;
            
            // Visual feedback for disabled state
            PrevItemButton.Opacity = PrevItemButton.IsEnabled ? 1.0 : 0.4;
            NextItemButton.Opacity = NextItemButton.IsEnabled ? 1.0 : 0.4;
        }

        private void PrevItemButton_Click(object sender, RoutedEventArgs e)
        {
            AutoApplyCurrentItem(); // Auto-apply before navigating away
            var items = GetVisibleItems();
            int index = items.IndexOf(_editorLoadedItem);
            
            if (index > 0)
            {
                var prevItem = items[index - 1];
                LoadItemEditor(prevItem);
                SelectItemInTreeView(prevItem);
            }
        }

        private void NextItemButton_Click(object sender, RoutedEventArgs e)
        {
            AutoApplyCurrentItem(); // Auto-apply before navigating away
            var items = GetVisibleItems();
            int index = items.IndexOf(_editorLoadedItem);
            
            if (index >= 0 && index < items.Count - 1)
            {
                var nextItem = items[index + 1];
                LoadItemEditor(nextItem);
                SelectItemInTreeView(nextItem);
            }
        }

        private void SelectItemInTreeView(Item item)
        {
            // Find and select the item in the TreeView
            foreach (TreeViewItem sectionNode in SectionsTreeView.Items)
            {
                foreach (TreeViewItem itemNode in sectionNode.Items)
                {
                    if (itemNode.Tag == item)
                    {
                        sectionNode.IsExpanded = true;
                        itemNode.IsSelected = true;
                        itemNode.BringIntoView();
                        return;
                    }
                }
            }
        }

        private List<ToggleButton> _statusButtons = new List<ToggleButton>();
        private bool _isLookupMode = false;

        private void LoadStatusControls(Item item)
        {
            StatusPanel.Children.Clear();
            _statusButtons.Clear();
            StatusTextBox.Visibility = Visibility.Collapsed;
            NiValueButton.Visibility = Visibility.Collapsed;
            _isLookupMode = false;
            int statusTabIndex = 20; // Status buttons get TabIndex 20-24

            string controlName = item.ControlName?.ToLower() ?? "";
            string currentValue = item.Value?.ToString() ?? "";

            // Normalize legacy "NA" values to "NI" for display/comparison
            if (currentValue.Equals("NA", StringComparison.OrdinalIgnoreCase))
            {
                currentValue = "NI";
            }

            // Read-only display for proper name / contact fields
            bool isSciCheck = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);
            if (!isSciCheck && IsProperNameItem(item))
            {
                StatusLabel.Text = "READ-ONLY";
                StatusTextBox.Text = currentValue;
                StatusTextBox.Visibility = Visibility.Visible;
                StatusTextBox.IsReadOnly = true;
                StatusTextBox.Background = new SolidColorBrush(Color.FromRgb(235, 235, 235));
                NiValueButton.Visibility = Visibility.Collapsed;
                StatusPanel.Children.Add(new TextBlock
                {
                    Text = "🔒 This field contains a proper name and cannot be edited here.",
                    Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
                    FontStyle = FontStyles.Italic,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                ClearValueButton.Visibility = Visibility.Collapsed;
                return;
            }

            // Determine what type of control to show
            if (controlName == "yesno")
            {
                StatusLabel.Text = "STATUS";
                var options = new[] { "Yes", "No", "N/A" };
                foreach (var option in options)
                {
                    var btn = CreateStatusButton(option, currentValue);
                    btn.TabIndex = statusTabIndex++;
                    StatusPanel.Children.Add(btn);
                    _statusButtons.Add(btn);
                }
            }
            else if (controlName == "yesnonani")
            {
                StatusLabel.Text = "STATUS";
                var options = new[] { "Yes", "No", "NI" };
                foreach (var option in options)
                {
                    var btn = CreateStatusButton(option, currentValue);
                    btn.TabIndex = statusTabIndex++;
                    StatusPanel.Children.Add(btn);
                    _statusButtons.Add(btn);
                }
            }
            else if (controlName == "passfailnani" || controlName == "passfail")
            {
                StatusLabel.Text = "STATUS";
                var options = new[] { "Pass", "Fail", "NI" };
                foreach (var option in options)
                {
                    var btn = CreateStatusButton(option, currentValue);
                    btn.TabIndex = statusTabIndex++;
                    StatusPanel.Children.Add(btn);
                    _statusButtons.Add(btn);
                }
            }
            else if ((controlName == "lookup" || controlName == "lookupnani" || controlName == "searchlookup") && item.ValueList != null && item.ValueList.Count > 0)
            {
                StatusLabel.Text = "VALUE (type or select)";
                _isLookupMode = true;
                
                // Show editable text box with current value
                StatusTextBox.Text = currentValue;
                StatusTextBox.Visibility = Visibility.Visible;
                NiValueButton.Visibility = Visibility.Visible;
                StatusTextBox.IsReadOnly = false;
                StatusTextBox.Background = Brushes.White;
                
                // Add lookup options as suggestion buttons
                foreach (var option in item.ValueList)
                {
                    var btn = CreateStatusButton(option, currentValue);
                    btn.TabIndex = statusTabIndex++;
                    StatusPanel.Children.Add(btn);
                    _statusButtons.Add(btn);
                }
                // For LookupNaNi, also add NI option
                if (controlName == "lookupnani")
                {
                    var niBtn = CreateStatusButton("NI", currentValue);
                    niBtn.TabIndex = statusTabIndex++;
                    StatusPanel.Children.Add(niBtn);
                    _statusButtons.Add(niBtn);
                }
            }
            else if (controlName == "numberpad")
            {
                StatusLabel.Text = "VALUE";
                // Show current value in text box (read-only display)
                StatusTextBox.Text = currentValue;
                StatusTextBox.Visibility = Visibility.Visible;
                NiValueButton.Visibility = Visibility.Visible;
                StatusTextBox.IsReadOnly = true;
                StatusTextBox.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                
                // Add number pad buttons [0]-[9] plus [.] [½] [¼] [¾] [⌫] and [C]
                var digits = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", "½", "¼", "¾", "⌫", "C" };
                foreach (var digit in digits)
                {
                    bool isFraction = digit == "½" || digit == "¼" || digit == "¾";
                    bool isAction = digit == "⌫" || digit == "C";
                    var btn = new Button
                    {
                        Content = digit,
                        Width = 44,
                        Height = 44,
                        Margin = new Thickness(2),
                        FontSize = isFraction ? 15 : 18,
                        FontWeight = FontWeights.Bold,
                        Background = isAction
                            ? new SolidColorBrush(Color.FromRgb(255, 200, 200))
                            : isFraction
                                ? new SolidColorBrush(Color.FromRgb(200, 220, 245)) // Light blue for fractions
                                : new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        BorderThickness = new Thickness(1),
                        TabIndex = statusTabIndex++
                    };
                    btn.Click += NumberPadButton_Click;
                    StatusPanel.Children.Add(btn);
                }
            }
            else if (controlName == "text" || controlName == "textbox" || controlName == "number")
            {
                StatusLabel.Text = controlName == "number" ? "VALUE (number)" : "VALUE (text)";
                StatusTextBox.Text = currentValue;
                StatusTextBox.Visibility = Visibility.Visible;
                NiValueButton.Visibility = Visibility.Visible;
                StatusTextBox.IsReadOnly = false;
                StatusTextBox.Background = Brushes.White;
            }
            else
            {
                // Generic fallback - show text box
                StatusLabel.Text = "VALUE";
                StatusTextBox.Text = currentValue;
                StatusTextBox.Visibility = Visibility.Visible;
                NiValueButton.Visibility = Visibility.Visible;
                StatusTextBox.IsReadOnly = false;
                StatusTextBox.Background = Brushes.White;
            }
            
            // Visual cue: red background if required field is empty
            UpdateRequiredFieldHighlighting(item);

            // Always sync the wastebasket button — show whenever the item has a value
            UpdateClearButton();

            // Inject RESNET multipoint button for blower door section items
            TryAddMultipointButton(item);
        }

        private void UpdateRequiredFieldHighlighting(Item item)
        {
            // Check if VALUE is required and missing
            bool valueRequired = item.Required;
            bool valueMissing = string.IsNullOrWhiteSpace(item.Value?.ToString());
            
            if (valueRequired && valueMissing)
            {
                StatusBorder.Background = new SolidColorBrush(Color.FromRgb(255, 214, 221)); // Saturated red for missing required
            }
            else
            {
                StatusBorder.Background = new SolidColorBrush(Color.FromRgb(240, 248, 255)); // Light blue when filled/not required
            }
            
            // Check if PICTURE is required and missing
            bool pictureRequired = item.IsPictureRequired;
            bool pictureMissing = item.Pictures == null || item.Pictures.Count == 0;
            
            if (pictureRequired && pictureMissing)
            {
                PhotoBorder.Background = new SolidColorBrush(Color.FromRgb(255, 214, 221)); // Saturated red for missing required
            }
            else
            {
                PhotoBorder.Background = new SolidColorBrush(Color.FromRgb(240, 248, 255)); // Light blue when filled/not required
            }
        }

        // ── RESNET Multipoint Blower Door ────────────────────────────────────

        /// <summary>
        /// Returns true when the current item lives in a blower door section.
        /// Matches by section name across HEF/HET/IEF.
        /// </summary>
        private bool IsInBlowerDoorSection() =>
            _currentSection?.Name?.Contains("blower door", StringComparison.OrdinalIgnoreCase) == true ||
            _currentSection?.Name?.Contains("blower", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Resolves the blower door CFM target for the current inspection.
        ///   HET → item 1.3  "Blower Door Max. CFM"
        ///   HEF → item 11.1 "Blower Doot Target"
        ///   IEF → item 10.9 "AC volume" × 4.5 / 60
        /// Falls back to _currentEcInfo.EffectiveBlowerDoorCfm if INS fields are empty.
        /// </summary>
        private double GetBlowerDoorTarget()
        {
            if (_currentInspection?.Sections == null) return 0;

            string code = (_currentInspectionCode ?? "").ToUpperInvariant() switch
            {
                "IER" or "HER" => "IEF",
                var c => c
            };

            // Locate target by item number depending on inspection type
            string? targetItemNumber = code switch
            {
                "HET"        => "1.3",
                "HEF"        => "11.1",
                "IEF"        => null,  // derived from volume
                _            => null
            };

            if (targetItemNumber != null)
            {
                foreach (var section in _currentInspection.Sections)
                    foreach (var item in section.Items)
                        if (item.Number == targetItemNumber)
                        {
                            string raw = (item.Value?.ToString() ?? "").Replace(",", "").Trim();
                            if (double.TryParse(raw, out double v) && v > 0) return v;
                        }
            }

            // IEF: volume × 5.0 ACH / 60 min
            if (code == "IEF")
            {
                foreach (var section in _currentInspection.Sections)
                    foreach (var item in section.Items)
                        if (item.Number == "10.9" &&
                            (item.Name?.Contains("volume", StringComparison.OrdinalIgnoreCase) == true ||
                             item.Name?.Contains("Volume", StringComparison.OrdinalIgnoreCase) == true))
                        {
                            string raw = (item.Value?.ToString() ?? "").Replace(",", "").Trim();
                            if (double.TryParse(raw, out double vol) && vol > 0)
                                return vol * 5.0 / 60.0;
                        }
            }

            // Fall back to EC OCR result
            if (_currentEcInfo?.EffectiveBlowerDoorCfm != null &&
                double.TryParse(_currentEcInfo.EffectiveBlowerDoorCfm.Replace(",", ""), out double ecVal))
                return ecVal;

            return 0;
        }

        /// <summary>
        /// Parses the single-point 50 Pa CFM reading from the blower door section.
        /// Handles "1920 of 2301" and plain numeric formats.
        /// </summary>
        private double Get50PaReading()
        {
            if (_currentInspection?.Sections == null) return 0;
            foreach (var section in _currentInspection.Sections)
            {
                if (!IsBlowerDoorSectionName(section.Name)) continue;
                foreach (var item in section.Items)
                {
                    if (!Is50PaCfmItem(item)) continue;
                    string raw = item.Value?.ToString()?.Trim() ?? "";
                    // "1920 of 2301" → take left part
                    int ofIdx = raw.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
                    if (ofIdx > 0) raw = raw[..ofIdx].Trim();
                    if (double.TryParse(raw.Replace(",", ""), out double v)) return v;
                }
            }
            return 0;
        }

        private static bool IsBlowerDoorSectionName(string? name) =>
            name?.Contains("blower", StringComparison.OrdinalIgnoreCase) == true;

        private static bool Is50PaCfmItem(Item item)
        {
            string n = item.Name ?? "";
            return (n.Contains("50 Pa", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains("50  Pa", StringComparison.OrdinalIgnoreCase)) &&
                   n.Contains("CFM", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Appends the RESNET Multipoint button to StatusPanel whenever the inspector
        /// is in the blower door section. Button is RED when multipoint is required
        /// (single-point reading ≥ 90 % of target).
        /// </summary>
        private void TryAddMultipointButton(Item item)
        {
            if (!IsInBlowerDoorSection()) return;

            double target  = GetBlowerDoorTarget();
            double reading = Get50PaReading();
            bool required  = target > 0 && reading > 0 && reading >= target * 0.9;

            var btn = new System.Windows.Controls.Button
            {
                Content     = "📊 RESNET Multipoint Test...",
                Margin      = new Thickness(0, 10, 0, 0),
                Padding     = new Thickness(10, 7, 10, 7),
                FontSize    = 12,
                FontWeight  = required ? FontWeights.Bold : FontWeights.Normal,
                Background  = required
                    ? new SolidColorBrush(Color.FromRgb(198, 40, 40))
                    : new SolidColorBrush(Color.FromRgb(66, 66, 66)),
                Foreground  = new SolidColorBrush(Colors.White),
                Cursor      = System.Windows.Input.Cursors.Hand,
                ToolTip     = required
                    ? $"Multipoint test is REQUIRED — reading ({reading:F0}) is ≥ 90% of target ({target:F0})"
                    : "Run RESNET 380 multipoint blower door test"
            };
            btn.Click += (_, _) => OpenMultipointDialog();
            StatusPanel.Children.Add(btn);
        }

        // ── Multipoint dialog orchestration ─────────────────────────────────

        private void OpenMultipointDialog()
        {
            if (_currentInspection == null) return;

            double target = GetBlowerDoorTarget();
            var dlg = new MultipointBlowerDoorWindow(target) { Owner = this };

            if (dlg.ShowDialog() != true) return;

            ApplyMultipointResults(dlg, target);
        }

        /// <summary>
        /// Writes RESNET multipoint results back into the INS blower door section fields.
        ///
        /// RESNET point → existing template field mapping:
        ///   10 Pa → "(20 Pa) CFM"   annotated as "XXX at 10 PA"
        ///   23 Pa → "(30 Pa) CFM"   annotated as "XXX at 23 PA"
        ///   35 Pa → "(40 Pa) CFM"   annotated as "XXX at 35 PA"
        ///   48 Pa → embedded in "(50 Pa) CFM" annotation
        ///   60 Pa → "(60 Pa) CFM"   annotated as "XXX at 60 PA"
        ///
        /// Old HEF files with only a single 50 Pa field receive a verbose all-in-one
        /// annotation in that field.
        ///
        /// Ring letters are written to the paired setup fields.
        /// Pass/Fail is auto-flipped based on cfm50 vs target.
        /// </summary>
        private void ApplyMultipointResults(MultipointBlowerDoorWindow dlg, double target)
        {
            if (_currentInspection?.Sections == null) return;

            // RESNET Pa order in the dialog arrays: [0]=60, [1]=48, [2]=35, [3]=23, [4]=10
            double cfm60 = dlg.EnteredCfm[0]; string rng60 = dlg.SelectedRing[0];
            double cfm48 = dlg.EnteredCfm[1]; string rng48 = dlg.SelectedRing[1];
            double cfm35 = dlg.EnteredCfm[2]; string rng35 = dlg.SelectedRing[2];
            double cfm23 = dlg.EnteredCfm[3]; string rng23 = dlg.SelectedRing[3];
            double cfm10 = dlg.EnteredCfm[4]; string rng10 = dlg.SelectedRing[4];
            double calc = dlg.CalculatedCfm50;

            string Fmt(double v) => v > 0 ? ((int)Math.Round(v)).ToString() : "NI";

            Section? bdSection = _currentInspection.Sections
                .FirstOrDefault(s => IsBlowerDoorSectionName(s.Name));

            if (bdSection == null) return;

            // Detect whether this is an old-HEF single-50Pa-only template
            bool has20PaField = bdSection.Items.Any(i =>
                (i.Name?.Contains("20 Pa", StringComparison.OrdinalIgnoreCase) == true ||
                 i.Name?.Contains("20Pa", StringComparison.OrdinalIgnoreCase) == true) &&
                i.Name.Contains("CFM", StringComparison.OrdinalIgnoreCase));

            if (has20PaField)
            {
                // ── Full template: write each RESNET reading to its closest field ──

                // 10 Pa → 20 Pa field
                WriteMultipointField(bdSection, "20 Pa", "CFM",
                    cfm10 > 0 ? $"{Fmt(cfm10)} at 10 PA" : "NI", rng10);

                // 23 Pa → 30 Pa field
                WriteMultipointField(bdSection, "30 Pa", "CFM",
                    cfm23 > 0 ? $"{Fmt(cfm23)} at 23 PA" : "NI", rng23);

                // 35 Pa → 40 Pa field
                WriteMultipointField(bdSection, "40 Pa", "CFM",
                    cfm35 > 0 ? $"{Fmt(cfm35)} at 35 PA" : "NI", rng35);

                // 48 Pa embedded into the 50 Pa field annotation
                Write50PaField(bdSection, target, calc, cfm48, rng48);

                // 60 Pa → 60 Pa field
                WriteMultipointField(bdSection, "60 Pa", "CFM",
                    cfm60 > 0 ? $"{Fmt(cfm60)} at 60 PA" : "NI", rng60);
            }
            else
            {
                // ── Old HEF: verbose single-field annotation ──
                string verbose = BuildVerboseAnnotation(bdSection, target, calc,
                    cfm10, cfm23, cfm35, cfm48, cfm60,
                    rng48);
                Write50PaFieldVerbose(bdSection, verbose, rng48);
            }

            // Flip Pass/Fail
            FlipBlowerDoorPassFail(bdSection, target, calc);

            // Reload the editor to reflect updated values
            _hasUnsavedChanges = true;
            DoSave();
            if (_currentItem != null) LoadItemEditor(_currentItem);
        }

        // ── Field-writing helpers ────────────────────────────────────────────

        /// <summary>
        /// Finds the CFM item whose name contains <paramref name="paLabel"/> and "CFM",
        /// sets its value to <paramref name="cfmAnnotation"/>, and sets the sibling
        /// ring-setup item to <paramref name="ring"/>.
        /// </summary>
        private static void WriteMultipointField(Section section,
            string paLabel, string cfmKeyword,
            string cfmAnnotation, string ring)
        {
            foreach (var item in section.Items)
            {
                string name = item.Name ?? "";
                if (name.Contains(paLabel, StringComparison.OrdinalIgnoreCase) &&
                    name.Contains(cfmKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    item.Value = cfmAnnotation;
                }

                if (name.Contains(paLabel, StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("ring setup", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(ring))
                {
                    item.Value = ring;
                }
            }
        }

        /// <summary>
        /// Transforms the 50 Pa CFM field.
        /// Before: "1920 of 2301"
        /// After:  "1920 at 50 PA of 2301 target (multipoint calc to 1834 with 1840 at 48pa)"
        /// </summary>
        private static void Write50PaField(Section section,
            double target, double calc, double cfm48, string ring48)
        {
            foreach (var item in section.Items)
            {
                if (!Is50PaCfmItem(item)) continue;

                string existing = item.Value?.ToString()?.Trim() ?? "";
                string singleReading = "";
                string targetPart    = target > 0 ? $"{(int)Math.Round(target)}" : "";

                // Parse "1920 of 2301" → singleReading = "1920", targetPart = "2301"
                int ofIdx = existing.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
                if (ofIdx > 0)
                {
                    singleReading = existing[..ofIdx].Trim();
                    string parsedTarget = existing[(ofIdx + 4)..].Trim();
                    if (!string.IsNullOrEmpty(parsedTarget)) targetPart = parsedTarget;
                }
                else if (!string.IsNullOrEmpty(existing) &&
                         !existing.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                         !existing.Equals("NI", StringComparison.OrdinalIgnoreCase))
                {
                    singleReading = existing;
                }

                string readingPart = !string.IsNullOrEmpty(singleReading)
                    ? $"{singleReading} at 50 PA"
                    : "at 50 PA";
                string targetClause = !string.IsNullOrEmpty(targetPart)
                    ? $" of {targetPart} target"
                    : "";
                string with48 = cfm48 > 0
                    ? $" with {(int)Math.Round(cfm48)} at 48pa"
                    : "";
                string calcPart = calc > 0
                    ? $" (multipoint calc to {(int)Math.Round(calc)}{with48})"
                    : "";

                item.Value = $"{readingPart}{targetClause}{calcPart}";
            }

            // Ring setup for 50 Pa row
            foreach (var item in section.Items)
            {
                string name = item.Name ?? "";
                if ((name.Contains("50 Pa", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("50  Pa", StringComparison.OrdinalIgnoreCase)) &&
                    name.Contains("ring setup", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(ring48))
                {
                    item.Value = ring48;
                }
            }
        }

        /// <summary>
        /// Old HEF (single 50 Pa field only): writes a verbose all-in-one annotation.
        /// "1075 at 50 PA of 1442 target (multipoint: 10pa=309, 23pa=389, 35pa=410, 48pa=1840, 60pa=XXXX → calc 1834 CFM)"
        /// </summary>
        private static string BuildVerboseAnnotation(Section section,
            double target, double calc,
            double cfm10, double cfm23, double cfm35, double cfm48, double cfm60,
            string ring48)
        {
            string Fmt(double v) => v > 0 ? ((int)Math.Round(v)).ToString() : "NI";

            // Reconstruct single-point reading from existing 50 Pa value
            string existing = "";
            foreach (var item in section.Items)
            {
                if (Is50PaCfmItem(item))
                {
                    existing = item.Value?.ToString()?.Trim() ?? "";
                    break;
                }
            }

            string singleReading = "";
            string targetPart    = target > 0 ? $"{(int)Math.Round(target)}" : "";
            int ofIdx = existing.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
            if (ofIdx > 0)
            {
                singleReading = existing[..ofIdx].Trim();
                string parsedTarget = existing[(ofIdx + 4)..].Trim();
                if (!string.IsNullOrEmpty(parsedTarget)) targetPart = parsedTarget;
            }
            else if (!string.IsNullOrEmpty(existing) &&
                     !existing.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                     !existing.Equals("NI", StringComparison.OrdinalIgnoreCase))
            {
                singleReading = existing;
            }

            string readingPart  = !string.IsNullOrEmpty(singleReading) ? $"{singleReading} at 50 PA" : "at 50 PA";
            string targetClause = !string.IsNullOrEmpty(targetPart) ? $" of {targetPart} target" : "";
            string detail       = $"10pa={Fmt(cfm10)}, 23pa={Fmt(cfm23)}, 35pa={Fmt(cfm35)}, 48pa={Fmt(cfm48)}, 60pa={Fmt(cfm60)}";
            string calcPart     = calc > 0 ? $" → calc {(int)Math.Round(calc)} CFM" : "";

            return $"{readingPart}{targetClause} (multipoint: {detail}{calcPart})";
        }

        private static void Write50PaFieldVerbose(Section section, string annotation, string ring48)
        {
            foreach (var item in section.Items)
            {
                if (Is50PaCfmItem(item))
                    item.Value = annotation;

                string name = item.Name ?? "";
                if ((name.Contains("50 Pa", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("50  Pa", StringComparison.OrdinalIgnoreCase)) &&
                    name.Contains("ring setup", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(ring48))
                {
                    item.Value = ring48;
                }
            }
        }

        /// <summary>
        /// Flips the blower door Pass/Fail item to Pass when cfm50 ≤ target,
        /// Fail otherwise. Leaves it unchanged if target is unknown.
        /// </summary>
        private static void FlipBlowerDoorPassFail(Section section, double target, double calc)
        {
            if (target <= 0 || calc <= 0) return;
            string verdict = calc <= target ? "Pass" : "Fail";

            foreach (var item in section.Items)
            {
                string name = item.Name ?? "";
                string ctrl = item.ControlName ?? "";
                bool isPassFail = ctrl.Contains("passfail", StringComparison.OrdinalIgnoreCase);
                bool isBlowerPassFail =
                    (name.Contains("blower door", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("blower pass", StringComparison.OrdinalIgnoreCase)) &&
                    isPassFail;

                if (isBlowerPassFail)
                    item.Value = verdict;
            }
        }

        private ToggleButton CreateStatusButton(string option, string currentValue)
        {
            bool isSelected = option.Equals(currentValue, StringComparison.OrdinalIgnoreCase);
            
            var btn = new ToggleButton
            {
                Content = isSelected ? $"✓ {option}" : option,
                Tag = option,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(2),
                FontSize = 12,
                FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal,
                BorderThickness = isSelected ? new Thickness(3) : new Thickness(1),
                IsChecked = isSelected
            };

            // Set colors based on option type and selection state
            string optionLower = option.ToLower();
            if (optionLower == "yes" || optionLower == "pass")
            {
                if (isSelected)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(100, 200, 100)); // Bright green
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 150, 0));
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(220, 245, 220)); // Light green
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(150, 200, 150));
                }
            }
            else if (optionLower == "no" || optionLower == "fail")
            {
                if (isSelected)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(220, 80, 80)); // Bright red
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(180, 0, 0));
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(255, 220, 220)); // Light red
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 150, 150));
                }
            }
            else if (optionLower == "n/a" || optionLower == "na" || optionLower == "ni")
            {
                if (isSelected)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(120, 120, 120)); // Dark gray
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80));
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(235, 235, 235)); // Light gray
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180));
                }
            }
            else
            {
                // Generic option (lookup values)
                if (isSelected)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)); // Steel blue
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(30, 90, 140));
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                }
            }

            btn.Click += StatusButton_Click;
            return btn;
        }

        private void StatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton clickedBtn && _currentItem != null)
            {
                string clickedValue = clickedBtn.Tag?.ToString() ?? "";
                string currentValue = _currentItem.Value?.ToString() ?? "";
                
                // In lookup mode, clicking a button fills the text box (for type-or-select behavior)
                if (_isLookupMode)
                {
                    // If clicking the already-selected button, clear the text box (toggle off)
                    if (clickedValue.Equals(currentValue, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusTextBox.Text = "";
                    }
                    else
                    {
                        StatusTextBox.Text = clickedValue;
                    }
                    // TextChanged handler will update _currentItem.Value, filter buttons, and MarkUnsaved()
                    PopulateTreeView();
                    return;
                }
                
                // If clicking the already-selected button, clear the value (toggle off)
                if (clickedValue.Equals(currentValue, StringComparison.OrdinalIgnoreCase))
                {
                    _currentItem.Value = "";
                }
                else
                {
                    // Check if changing TO Pass FROM Fail
                    bool changingToPass = clickedValue.Equals("Pass", StringComparison.OrdinalIgnoreCase);
                    bool wasFail = currentValue.Equals("Fail", StringComparison.OrdinalIgnoreCase);
                    bool hasComment = !string.IsNullOrWhiteSpace(_currentItem.Comments);
                    bool hasPictures = _currentItem.Pictures != null && _currentItem.Pictures.Count > 0;
                    
                    _currentItem.Value = clickedValue;

                    // Prompt to clear comment/pictures when changing from Fail to Pass.
                    // Required pictures are never clearable — exclude them from the offer entirely.
                    bool hasClearablePics = hasPictures && !(_currentItem.IsPictureRequired);
                    if (changingToPass && wasFail && (hasComment || hasClearablePics))
                    {
                        PromptClearOnPass(hasComment, hasClearablePics);
                    }
                }
                
                MarkUnsaved();

                // Refresh the status controls to show new selection styling
                LoadStatusControls(_currentItem);

                // Update required field highlighting
                UpdateRequiredFieldHighlighting(_currentItem);

                PopulateTreeView(); // Refresh tree to show color changes

                // Refresh design-data banner (covers lookup items like "2.1 Type: Performance IECC")
                RefreshEngDataPanel();
                RefreshEcDataPanel();

                if (_advanceToNextFailAfterPassClear)
                {
                    _advanceToNextFailAfterPassClear = false;
                    AdvanceToNextFailedItemOrTop();
                }
            }
        }
        
        private void UpdateLookupButtonStyles(string selectedValue)
        {
            foreach (var btn in _statusButtons)
            {
                string option = btn.Tag?.ToString() ?? "";
                bool isSelected = option.Equals(selectedValue, StringComparison.OrdinalIgnoreCase);
                
                btn.IsChecked = isSelected;
                btn.Content = isSelected ? $"✓ {option}" : option;
                btn.FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal;
                btn.BorderThickness = isSelected ? new Thickness(3) : new Thickness(1);
                
                // Standard lookup styling (gray with blue highlight when selected)
                if (isSelected)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(100, 149, 237)); // Cornflower blue
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 105, 225)); // Royal blue
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                    btn.Foreground = Brushes.Black;
                }
            }
        }
        
        private void FilterAndStyleLookupButtons(string typedText)
        {
            string filterLower = typedText?.ToLower() ?? "";
            bool hasFilter = !string.IsNullOrEmpty(filterLower);
            
            foreach (var btn in _statusButtons)
            {
                string option = btn.Tag?.ToString() ?? "";
                string optionLower = option.ToLower();
                
                // Show button if no filter OR if option contains the typed text
                bool matches = !hasFilter || optionLower.Contains(filterLower);
                btn.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                
                // Style: highlight if exact match
                bool isExactMatch = option.Equals(typedText, StringComparison.OrdinalIgnoreCase);
                
                btn.IsChecked = isExactMatch;
                btn.Content = isExactMatch ? $"✓ {option}" : option;
                btn.FontWeight = isExactMatch ? FontWeights.Bold : FontWeights.Normal;
                btn.BorderThickness = isExactMatch ? new Thickness(3) : new Thickness(1);
                
                if (isExactMatch)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(100, 149, 237)); // Cornflower blue
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 105, 225)); // Royal blue
                    btn.Foreground = Brushes.White;
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                    btn.Foreground = Brushes.Black;
                }
            }
        }
        
        private void PromptClearOnPass(bool hasComment, bool hasPictures)
        {
            // Use _editorLoadedItem (not _currentItem) to avoid touch-drift issues
            if (_editorLoadedItem == null) return;
            var itemToClear = _editorLoadedItem;

            // hasPictures here means "has clearable (non-required) pictures" — required pics
            // are excluded by the caller so we never offer to delete them.

            // Build the popup dialog
            var dialog = new Window
            {
                Title = "Clear on Pass?",
                Width = 420,
                Height = 230,
                MinHeight = 230,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = System.Windows.ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(22) };

            string message = hasPictures
                ? "Value changed from Fail to Pass.\n\nClear comment and pictures for this item?"
                : hasComment
                    ? "Value changed from Fail to Pass.\n\nClear comment for this item?"
                    : "Value changed from Fail to Pass.\n\nMove to the next failed item?";
            
            stack.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 24),
                TextWrapping = TextWrapping.Wrap
            });
            
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            };
            
            var yesBtn = new Button
            {
                Content = "Yes",
                Width = 140,
                Padding = new Thickness(0, 8, 0, 8),
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true,
                Background = new SolidColorBrush(Color.FromRgb(52, 152, 219)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            
            var noBtn = new Button
            {
                Content = "No",
                Width = 140,
                Padding = new Thickness(0, 8, 0, 8),
                IsCancel = true
            };
            
            yesBtn.Click += (s, ev) =>
            {
                bool clearedSomething = false;
                
                // Clear comment too when clearing a failed item to pass
                if (hasComment)
                {
                    itemToClear.Comments = "";
                    CommentsTextBox.Text = "";
                    clearedSomething = true;
                }
                
                if (hasPictures)
                {
                    itemToClear.Pictures?.Clear();
                    _currentPhotoIndex = 0;
                    LoadCurrentPhoto();
                    clearedSomething = true;
                }
                
                if (clearedSomething)
                {
                    _hasUnsavedChanges = true;
                    MarkUnsaved();
                }

                _advanceToNextFailAfterPassClear = true;
                dialog.DialogResult = true;
                dialog.Close();
            };
            
            noBtn.Click += (s, ev) =>
            {
                _advanceToNextFailAfterPassClear = false;
                dialog.DialogResult = false;
                dialog.Close();
            };
            
            buttonPanel.Children.Add(yesBtn);
            buttonPanel.Children.Add(noBtn);
            stack.Children.Add(buttonPanel);
            
            dialog.Content = stack;
            dialog.ShowDialog();
        }

        private void AdvanceToNextFailedItemOrTop()
        {
            if (_currentInspection?.Sections == null || _editorLoadedItem == null)
                return;

            var visibleItems = GetVisibleItems();
            if (visibleItems.Count == 0)
                return;

            int currentIndex = visibleItems.IndexOf(_editorLoadedItem);
            if (currentIndex < 0)
                currentIndex = -1;

            Item? nextFailedItem = visibleItems
                .Skip(currentIndex + 1)
                .FirstOrDefault(item => (item.Value?.ToString() ?? "").Equals("Fail", StringComparison.OrdinalIgnoreCase));

            if (nextFailedItem != null)
            {
                LoadItemEditor(nextFailedItem);
                SelectItemInTreeView(nextFailedItem);
                return;
            }

            var firstVisibleItem = visibleItems.FirstOrDefault();
            if (firstVisibleItem != null)
            {
                LoadItemEditor(firstVisibleItem);
                SelectItemInTreeView(firstVisibleItem);
            }
        }

        private void NumberPadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && _currentItem != null)
            {
                string digit = btn.Content?.ToString() ?? "";
                string currentValue = _currentItem.Value?.ToString() ?? "";
                
                if (digit == "C")
                {
                    // Clear - reset to empty
                    _currentItem.Value = "";
                }
                else if (digit == "⌫")
                {
                    // Backspace - remove last character
                    if (currentValue.Length > 0)
                    {
                        _currentItem.Value = currentValue.Substring(0, currentValue.Length - 1);
                    }
                }
                else if (digit == "½")
                {
                    // Append .5
                    _currentItem.Value = currentValue + ".5";
                }
                else if (digit == "¼")
                {
                    // Append .25
                    _currentItem.Value = currentValue + ".25";
                }
                else if (digit == "¾")
                {
                    // Append .75
                    _currentItem.Value = currentValue + ".75";
                }
                else
                {
                    // Append digit
                    _currentItem.Value = currentValue + digit;
                }
                
                // Update the display
                StatusTextBox.Text = _currentItem.Value?.ToString() ?? "";
                MarkUnsaved();
                RefreshEngDataPanel();
                RefreshEcDataPanel();

                // Update the tree item's color badge to reflect the value change
                UpdateCurrentItemBadgeColor();
            }
        }
        
        /// <summary>
        /// Auto-apply the current item when the user navigates away.
        /// Same as clicking Apply — syncs UI to data model, updates tree colors, marks unsaved.
        /// </summary>
        private void AutoApplyCurrentItem()
        {
            if (_editorLoadedItem == null) return;
            SyncCurrentItemFromUI();
            UpdateCurrentItemBadgeColor();
            MarkUnsaved();
        }
        
        /// <summary>
        /// Surgically update the current item's number badge color in the tree view
        /// without doing a full tree rebuild. Called when values change via number pad.
        /// </summary>
        private void UpdateCurrentItemBadgeColor()
        {
            if (_currentItem == null) return;
            
            // Find the selected TreeViewItem
            TreeViewItem? selectedTreeItem = null;
            foreach (TreeViewItem sectionNode in SectionsTreeView.Items)
            {
                foreach (TreeViewItem itemNode in sectionNode.Items)
                {
                    if (itemNode.Tag == _currentItem)
                    {
                        selectedTreeItem = itemNode;
                        break;
                    }
                }
                if (selectedTreeItem != null) break;
            }
            
            if (selectedTreeItem?.Header is DockPanel panel && panel.Children.Count > 0)
            {
                // First child is the Border (number badge), name TextBlock is last child
                if (panel.Children[0] is Border numberBorder && numberBorder.Child is TextBlock numberText)
                {
                    string valueStr = _currentItem.Value?.ToString()?.ToLower() ?? "";
                    bool hasValue = !string.IsNullOrWhiteSpace(valueStr);
                    
                    if (valueStr.Contains("pass") || valueStr == "yes" || valueStr == "✓" || valueStr == "ok")
                    {
                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(144, 238, 144));
                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(0, 80, 0));
                    }
                    else if (valueStr.Contains("fail") || valueStr == "no" || valueStr == "✗" || valueStr.Contains("defect"))
                    {
                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(255, 150, 150));
                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(139, 0, 0));
                    }
                    else if (valueStr.Contains("pending") || valueStr == "na" || valueStr.Contains("n/a") || valueStr == "ni")
                    {
                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80));
                    }
                    else if (hasValue)
                    {
                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(173, 216, 230)); // Light blue
                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(0, 51, 102)); // Dark blue text
                        
                        // Also underline the name (last child in DockPanel)
                        if (panel.Children[panel.Children.Count - 1] is TextBlock nameText)
                            nameText.TextDecorations = TextDecorations.Underline;
                    }
                    else
                    {
                        // Cleared — reset to default
                        numberBorder.Background = null;
                        numberText.Foreground = new SolidColorBrush(Colors.Black);
                        
                        if (panel.Children[panel.Children.Count - 1] is TextBlock nameText)
                            nameText.TextDecorations = null;
                    }
                }
            }
        }

        private void NiValueButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentItem != null)
            {
                _currentItem.Value = "NI";
                StatusTextBox.Text = "NI";
                MarkUnsaved();
                RefreshEngDataPanel();
                RefreshEcDataPanel();
            }
        }

        private void StatusTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentItem != null && StatusTextBox.Visibility == Visibility.Visible)
            {
                _currentItem.Value = StatusTextBox.Text;
                MarkUnsaved();

                // Update required field highlighting (right-side editor)
                UpdateRequiredFieldHighlighting(_currentItem);

                // In lookup mode, filter and highlight buttons based on typed text
                if (_isLookupMode)
                {
                    FilterAndStyleLookupButtons(StatusTextBox.Text);
                }

                // Refresh design-data banner so colors track what's typed (skip during item load)
                if (!_isLoadingEditor)
                {
                    RefreshEngDataPanel();
                    RefreshEcDataPanel();
                    // Refresh the left pane tree node color so required items go green when filled
                    UpdateCurrentItemBadgeColor();
                }
            }
        }

        private void StatusTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            RefreshEngDataPanel();
            RefreshEcDataPanel();
        }

        private void LoadSavedComments()
        {
            SavedCommentsPanel.Children.Clear();
            SavedCommentsPanel.MaxWidth = double.PositiveInfinity;
            
            if (_userDataService == null || _currentItem == null || string.IsNullOrEmpty(_currentInspectionCode))
            {
                SavedCommentsPanel.Children.Add(NoSavedCommentsText);
                NoSavedCommentsText.Visibility = Visibility.Visible;
                return;
            }

            var saved = _userDataService.GetSavedComments(_currentInspectionCode, _currentItem.Number ?? "");
            
            if (saved.Count == 0)
            {
                SavedCommentsPanel.Children.Add(NoSavedCommentsText);
                NoSavedCommentsText.Visibility = Visibility.Visible;
                return;
            }

            NoSavedCommentsText.Visibility = Visibility.Collapsed;

            foreach (var comment in saved)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                
                var btn = new Button
                {
                    Content = new TextBlock { Text = comment, TextWrapping = TextWrapping.Wrap },
                    Tag = comment,
                    Style = (Style)FindResource("SavedCommentButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Width = double.NaN,
                    MaxWidth = double.PositiveInfinity
                };
                Grid.SetColumn(btn, 0);
                btn.Click += SavedCommentButton_Click;
                
                var deleteBtn = new Button
                {
                    Content = "X",
                    Tag = comment,
                    Style = (Style)FindResource("DeleteButton")
                };
                deleteBtn.Click += DeleteSavedCommentButton_Click;
                
                Grid.SetColumn(deleteBtn, 1);
                row.Children.Add(btn);
                row.Children.Add(deleteBtn);
                SavedCommentsPanel.Children.Add(row);
            }
        }

        private void TouchScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender == SavedCommentsScrollViewer)
            {
                _savedCommentsScrollStartPoint = e.GetPosition(SavedCommentsScrollViewer);
                _savedCommentsScrollStartOffset = SavedCommentsScrollViewer.VerticalOffset;
                _savedCommentsParentScrollStartOffset = EditorScrollViewer.VerticalOffset;
                _savedCommentsIsScrolling = true;
                _savedCommentsScrollStarted = false;
            }
            else if (sender == SuggestionsScrollViewer)
            {
                _suggestionsScrollStartPoint = e.GetPosition(SuggestionsScrollViewer);
                _suggestionsScrollStartOffset = SuggestionsScrollViewer.VerticalOffset;
                _suggestionsParentScrollStartOffset = EditorScrollViewer.VerticalOffset;
                _suggestionsIsScrolling = true;
                _suggestionsScrollStarted = false;
            }
        }

        private void TouchScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender == SavedCommentsScrollViewer)
            {
                HandleTouchStyleScroll(SavedCommentsScrollViewer, ref _savedCommentsScrollStartPoint, ref _savedCommentsScrollStartOffset, ref _savedCommentsParentScrollStartOffset, ref _savedCommentsIsScrolling, ref _savedCommentsScrollStarted, e);
            }
            else if (sender == SuggestionsScrollViewer)
            {
                HandleTouchStyleScroll(SuggestionsScrollViewer, ref _suggestionsScrollStartPoint, ref _suggestionsScrollStartOffset, ref _suggestionsParentScrollStartOffset, ref _suggestionsIsScrolling, ref _suggestionsScrollStarted, e);
            }
        }

        private void TouchScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender == SavedCommentsScrollViewer)
            {
                FinishTouchStyleScroll(ref _savedCommentsIsScrolling, ref _savedCommentsScrollStartPoint, ref _savedCommentsScrollStarted, e);
            }
            else if (sender == SuggestionsScrollViewer)
            {
                FinishTouchStyleScroll(ref _suggestionsIsScrolling, ref _suggestionsScrollStartPoint, ref _suggestionsScrollStarted, e);
            }
        }

        private void HandleTouchStyleScroll(ScrollViewer scrollViewer, ref Point? startPoint, ref double startOffset, ref double parentStartOffset, ref bool isScrolling, ref bool scrollStarted, MouseEventArgs e)
        {
            if (!isScrolling || !startPoint.HasValue || e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPoint = e.GetPosition(scrollViewer);
            double deltaX = startPoint.Value.X - currentPoint.X;
            double deltaY = startPoint.Value.Y - e.GetPosition(scrollViewer).Y;
            if (!scrollStarted && Math.Abs(deltaY) > 15 && Math.Abs(deltaY) >= Math.Abs(deltaX))
            {
                scrollStarted = true;
                Mouse.Capture(scrollViewer);
            }

            if (scrollStarted)
            {
                if (CanScrollVertically(scrollViewer, deltaY))
                {
                    scrollViewer.ScrollToVerticalOffset(startOffset + deltaY);
                }
                else
                {
                    EditorScrollViewer.ScrollToVerticalOffset(parentStartOffset + deltaY);
                }
                e.Handled = true;
            }
        }

        private static bool CanScrollVertically(ScrollViewer scrollViewer, double deltaY)
        {
            if (scrollViewer.ScrollableHeight <= 0)
                return false;

            if (deltaY > 0)
                return scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;

            if (deltaY < 0)
                return scrollViewer.VerticalOffset > 0;

            return false;
        }

        private void FinishTouchStyleScroll(ref bool isScrolling, ref Point? startPoint, ref bool scrollStarted, MouseButtonEventArgs e)
        {
            bool wasScrolling = scrollStarted;
            isScrolling = false;
            startPoint = null;
            scrollStarted = false;

            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        private void DeleteSavedCommentButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null || _userDataService == null || _currentItem == null || string.IsNullOrEmpty(_currentInspectionCode))
                return;

            string comment = btn.Tag?.ToString() ?? "";
            
            // No confirmation needed - user already clicked the X button
            _userDataService.RemoveComment(_currentInspectionCode, _currentItem.Number ?? "", comment);
            LoadSavedComments();
        }

        private void SavedCommentButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            string savedComment = btn.Tag?.ToString() ?? "";
            
            // Extract trade prefix from saved comment (if any)
            string savedPrefix = UserDataService.ExtractPrefix(savedComment);
            
            // Suppress UpdatePreviewText while we set prefix buttons
            // (otherwise it rewrites CommentsTextBox mid-flow and causes double prefixes)
            _isLoadingEditor = true;
            try
            {
                if (!string.IsNullOrEmpty(savedPrefix))
                {
                    _selectedPrefix = savedPrefix;
                    foreach (var prefixBtn in _prefixButtons)
                    {
                        prefixBtn.IsChecked = (prefixBtn.Tag?.ToString()?.ToLower() == savedPrefix.ToLower());
                    }
                }
                else if (string.IsNullOrEmpty(_selectedPrefix))
                {
                    // No prefix on saved comment AND no prefix currently selected — clear prefix buttons
                    foreach (var prefixBtn in _prefixButtons)
                        prefixBtn.IsChecked = false;
                }
                // else: saved comment has no prefix but user has one selected — keep it (apply to this comment)
            }
            finally
            {
                _isLoadingEditor = false;
            }
            
            // Strip prefix/suffix from saved comment to get core text
            string coreComment = UserDataService.StripPrefixAndSuffix(savedComment);
            
            // Build full comment with current prefix and suffixes
            string fullComment = UserDataService.BuildComment(_selectedPrefix ?? "", coreComment, _selectedSuffixes);
            
            // Append to existing comment if there's already text (don't overwrite)
            string existingText = CommentsTextBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(existingText))
            {
                CommentsTextBox.Text = existingText + " | " + fullComment;
            }
            else
            {
                CommentsTextBox.Text = fullComment;
            }
            AutoSetFailIfApplicable();
            MarkUnsaved();
        }

        private void SelectPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editorLoadedItem == null)
            {
                MessageBox.Show("Please select an item first.", "No Item Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            // Use last browsed directory if available, otherwise default to Camera Roll
            string initialDir;
            if (!string.IsNullOrEmpty(_lastPhotoDirectory) && Directory.Exists(_lastPhotoDirectory))
            {
                initialDir = _lastPhotoDirectory;
            }
            else
            {
                string cameraRollPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "Camera Roll"
                );
                initialDir = Directory.Exists(cameraRollPath) ? cameraRollPath : 
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Image Files (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|All Files (*.*)|*.*",
                Title = "Select Photo",
                InitialDirectory = initialDir
            };

            if (dialog.ShowDialog() == true)
            {
                // Remember the directory for next time (persisted to preferences.txt)
                _lastPhotoDirectory = Path.GetDirectoryName(dialog.FileName);
                SavePreferences();
                
                try
                {
                    byte[] imageData = File.ReadAllBytes(dialog.FileName);
                    
                    // Add photo immediately to Pictures array
                    AddPhotoToCurrentItem(imageData);
                    
                    DisplayImage(imageData);
                    _currentPhotoData = imageData;
                    GetSuggestionsButton.IsEnabled = _grokClient != null;
                    TranscribeButton.IsEnabled = _grokClient != null;
                    
                    // Update photo navigation to show new photo
                    _currentPhotoIndex = _editorLoadedItem.Pictures.Count - 1;
                    LoadCurrentPhoto();
                    
                    MarkUnsaved();
                    
                    // Update required field highlighting
                    UpdateRequiredFieldHighlighting(_editorLoadedItem);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void DisplayImage(byte[] imageData)
        {
            using var ms = new MemoryStream(imageData);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            PhotoImage.Source = bitmap;
        }

        private void LoadCurrentPhoto()
        {
            // Use _editorLoadedItem to avoid touch-drift issues
            if (_editorLoadedItem == null || _editorLoadedItem.Pictures.Count == 0)
            {
                PhotoImage.Source = null;
                _currentPhotoData = null;
                GetSuggestionsButton.IsEnabled = false;
                TranscribeButton.IsEnabled = false;
                PrevPhotoButton.IsEnabled = false;
                NextPhotoButton.IsEnabled = false;
                DeletePhotoButton.IsEnabled = false;
                PhotoCounterText.Text = "No photos";
                // Red placeholder when picture is required but missing
                PhotoRequiredPlaceholder.Visibility =
                    (_editorLoadedItem?.IsPictureRequired == true)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                return;
            }
            // Has photos — hide placeholder
            PhotoRequiredPlaceholder.Visibility = Visibility.Collapsed;

            // Clamp index to valid range
            if (_currentPhotoIndex >= _editorLoadedItem.Pictures.Count)
                _currentPhotoIndex = _editorLoadedItem.Pictures.Count - 1;
            if (_currentPhotoIndex < 0)
                _currentPhotoIndex = 0;

            var picture = _editorLoadedItem.Pictures[_currentPhotoIndex];
            if (!string.IsNullOrEmpty(picture.Data))
            {
                try
                {
                    byte[] imageData = Convert.FromBase64String(picture.Data);
                    DisplayImage(imageData);
                    _currentPhotoData = imageData;
                    GetSuggestionsButton.IsEnabled = _grokClient != null;
                    TranscribeButton.IsEnabled = _grokClient != null;
                }
                catch
                {
                    PhotoImage.Source = null;
                    _currentPhotoData = null;
                    GetSuggestionsButton.IsEnabled = false;
                TranscribeButton.IsEnabled = false;
                }
            }
            else
            {
                PhotoImage.Source = null;
                _currentPhotoData = null;
                GetSuggestionsButton.IsEnabled = false;
                TranscribeButton.IsEnabled = false;
            }

            // Enable/disable navigation arrows based on position
            PrevPhotoButton.IsEnabled = _currentPhotoIndex > 0;
            NextPhotoButton.IsEnabled = _currentPhotoIndex < _editorLoadedItem.Pictures.Count - 1;
            DeletePhotoButton.IsEnabled = true; // Can delete current photo
            
            // Always show photo counter
            PhotoCounterText.Text = $"Photo {_currentPhotoIndex + 1} of {_editorLoadedItem.Pictures.Count}";
        }

        private void PhotoImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (_editorLoadedItem == null || _editorLoadedItem.Pictures.Count == 0) return;
            if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _editorLoadedItem.Pictures.Count) return;
            
            var currentPicture = _editorLoadedItem.Pictures[_currentPhotoIndex];
            if (string.IsNullOrEmpty(currentPicture.Data)) return;
            
            try
            {
                // Decode base64 image data
                byte[] imageBytes = Convert.FromBase64String(currentPicture.Data);
                
                // Open image editor
                var editor = new ImageEditorWindow(imageBytes) { Owner = this };
                if (editor.ShowDialog() == true && editor.ImageWasEdited)
                {
                    // Save edited image back to the picture
                    currentPicture.Data = Convert.ToBase64String(editor.EditedImageBytes);
                    
                    // Clear thumbnail to force regeneration
                    currentPicture.ThumbnailData = null;
                    
                    // Reload display
                    LoadCurrentPhoto();
                    _hasUnsavedChanges = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening image editor: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PrevPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editorLoadedItem == null || _currentPhotoIndex <= 0) return;
            _currentPhotoIndex--;
            LoadCurrentPhoto();
        }

        private void NextPhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editorLoadedItem == null || _currentPhotoIndex >= _editorLoadedItem.Pictures.Count - 1) return;
            _currentPhotoIndex++;
            LoadCurrentPhoto();
        }

        private void DeletePhotoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_editorLoadedItem == null || _editorLoadedItem.Pictures.Count == 0) return;
            if (_currentPhotoIndex < 0 || _currentPhotoIndex >= _editorLoadedItem.Pictures.Count) return;
            
            // Remove the current photo
            _editorLoadedItem.Pictures.RemoveAt(_currentPhotoIndex);
            
            // Adjust index if needed
            if (_currentPhotoIndex >= _editorLoadedItem.Pictures.Count)
                _currentPhotoIndex = _editorLoadedItem.Pictures.Count - 1;
            if (_currentPhotoIndex < 0)
                _currentPhotoIndex = 0;
            
            // Refresh display
            LoadCurrentPhoto();
            MarkUnsaved();
            
            // Update required field highlighting
            UpdateRequiredFieldHighlighting(_editorLoadedItem);
        }

        private async void GetSuggestionsButton_Click(object sender, RoutedEventArgs e)
        {
            _isTranscribeMode = false;
            await GetAISuggestionsAsync();
        }
        
        private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
        {
            _isTranscribeMode = true;
            await GetAISuggestionsAsync(transcribeMode: true);
        }
        
        private async Task GetAISuggestionsAsync(bool transcribeMode = false)
        {
            if (_grokClient == null || _currentPhotoData == null || _currentItem == null)
                return;

            SuggestionsStack.Children.Clear();
            NoSuggestionsText.Visibility = Visibility.Collapsed;
            LoadingText.Text = transcribeMode ? "⏳ Transcribing label..." : "⏳ Analyzing image...";
            LoadingText.Visibility = Visibility.Visible;
            GetSuggestionsButton.IsEnabled = false;
            TranscribeButton.IsEnabled = false;

            try
            {
                List<string> suggestions;
                
                if (transcribeMode)
                {
                    // Transcribe mode - get 3 transcription options (simple → verbose)
                    suggestions = await _grokClient.TranscribeLabelMultiple(_currentPhotoData);
                    _lastTranscriptionOptions = suggestions;
                }
                else
                {
                    // Get section name for context
                    string sectionName = "";
                    if (_currentInspection?.Sections != null)
                    {
                        var parentSection = _currentInspection.Sections
                            .FirstOrDefault(s => s.Items.Contains(_currentItem));
                        if (parentSection != null)
                            sectionName = parentSection.Name ?? "";
                    }
                    
                    // Get quick comments so Grok can suggest something different (get top 10 for exclusions)
                    var quickComments = _quickCommentsService.GetSuggestions(
                        _currentInspectionCode ?? "", 
                        _currentItem.Number ?? "",
                        maxCount: 10);
                    
                    suggestions = await _grokClient.GetInspectionSuggestions(
                        _currentPhotoData,
                        _currentItem.Number ?? "",
                        _currentItem.Name ?? "",
                        _currentInspectionCode ?? "",
                        sectionName,
                        quickComments,
                        "",
                        GetCurrentCommentForAi(),
                        GetSelectedAiTone()
                    );
                }

                LoadingText.Visibility = Visibility.Collapsed;
                DisplayAISuggestions(suggestions);
            }
            catch (Exception ex)
            {
                LoadingText.Visibility = Visibility.Collapsed;
                MessageBox.Show($"Error getting suggestions: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GetSuggestionsButton.IsEnabled = true;
                TranscribeButton.IsEnabled = true;
            }
        }

        private string GetCurrentCommentForAi(Item? item = null)
        {
            // Use the visible comment box because it contains the inspector's current unsaved typing.
            // That is what drives AI mode: question mark help, polish, or empty-photo analysis.
            if (_inlineEditorMode && item != null)
            {
                var inlineBox = FindVisualChildren<TextBox>(InlineChecklistPanel)
                    .FirstOrDefault(box => ReferenceEquals(box.Tag, item) && box.AcceptsReturn);
                if (inlineBox != null)
                    return inlineBox.Text?.Trim() ?? "";

                return item.Comments?.Trim() ?? "";
            }

            return CommentsTextBox?.Text?.Trim() ?? "";
        }

        private void DisplayAISuggestions(List<string> suggestions)
        {
            SuggestionsStack.Children.Clear();

            foreach (var suggestion in suggestions)
            {
                if (IsAiStatusSuggestion(suggestion))
                {
                    SuggestionsStack.Children.Add(CreateClassicAiStatusText(suggestion));
                    continue;
                }

                var btn = new Button
                {
                    Content = new TextBlock { Text = suggestion, TextWrapping = TextWrapping.Wrap, MaxWidth = 350 },
                    Tag = suggestion,
                    Style = (Style)FindResource("AISuggestionButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };
                btn.Click += AISuggestionButton_Click;
                SuggestionsStack.Children.Add(btn);
            }
        }

        private UIElement CreateClassicAiStatusText(string text)
        {
            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(253, 186, 116)),
                Background = new SolidColorBrush(Color.FromRgb(255, 247, 237)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(124, 45, 18)),
                    FontSize = 12
                }
            };
        }

        private string GetSelectedAiTone()
        {
            if (AiToneComboBox?.SelectedItem is ComboBoxItem item && item.Content != null)
                return NormalizeAiStyle(item.Content.ToString() ?? $"{_aiTone} - fast");

            return $"{NormalizeAiTone(_aiTone)} - fast";
        }

        private void ApplyAiTonePreference()
        {
            _aiTone = NormalizeAiTone(_aiTone);
            string desired = $"{_aiTone} - fast";

            if (AiToneComboBox == null) return;

            for (int i = 0; i < AiToneComboBox.Items.Count; i++)
            {
                if (AiToneComboBox.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), desired, StringComparison.OrdinalIgnoreCase))
                {
                    AiToneComboBox.SelectedIndex = i;
                    return;
                }
            }

            AiToneComboBox.SelectedIndex = 0; // Technical - fast
        }

        private void AiToneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Editor selection is intentionally per-use. Save the default tone from Settings.
        }

        private void InlineAiToneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Editor selection is intentionally per-use. Save the default tone from Settings.
        }

        private static string NormalizeAiTone(string tone)
        {
            string value = (tone ?? "").Trim().ToLowerInvariant();
            if (value.Contains("nice")) return "Nice";
            if (value.Contains("strict")) return "Strict";
            return "Technical";
        }

        private static string NormalizeAiStyle(string style)
        {
            string tone = NormalizeAiTone(style);
            bool careful = (style ?? "").Contains("careful", StringComparison.OrdinalIgnoreCase);
            return $"{tone} - {(careful ? "careful" : "fast")}";
        }

        private void AISuggestionButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            string suggestion = btn.Tag?.ToString() ?? "";
            
            if (_isTranscribeMode)
            {
                // Transcribe mode - put value in the Value field + copy to clipboard
                if (_currentItem != null)
                {
                    _currentItem.Value = suggestion;
                    LoadStatusControls(_currentItem); // Refresh to show new value
                    MarkUnsaved();
                    try { Clipboard.SetText(suggestion); } catch { }
                    TryAutoFillAdjacentItems(suggestion);
                }
                return;
            }
            
            // Normal mode - put in Comments field with trade prefix
            string aiPrefix = UserDataService.ExtractPrefix(suggestion);
            if (!string.IsNullOrEmpty(aiPrefix))
            {
                _selectedPrefix = aiPrefix;
                foreach (var prefixBtn in _prefixButtons)
                    prefixBtn.IsChecked = (prefixBtn.Tag?.ToString()?.ToLower() == aiPrefix.ToLower());
            }

            string coreText = UserDataService.StripPrefixAndSuffix(suggestion);
            string fullComment = UserDataService.BuildComment(_selectedPrefix ?? "", coreText, _selectedSuffixes);
            CommentsTextBox.Text = fullComment;
            AutoSetFailIfApplicable();
            MarkUnsaved();
        }
        
        // ── Transcribe: auto-fill adjacent checklist items ─────────────────────
        // When a transcription has multiple key=value pairs (e.g. "Model: X / Serial: Y"
        // or "U-Value = 0.30 / SHGC = 0.25"), automatically fill the next 1-2 items
        // whose names match a key in the transcription — for the cost of 1 API call.
        private void TryAutoFillAdjacentItems(string transcription)
        {
            // Use the most verbose transcription option — it has the most key-value pairs.
            // This handles the case where the user tapped the simple option (e.g. just "2.3")
            // but a more detailed option like "Post: 2.3 / U-Factor: 2.2" was also available.
            string bestTranscription = transcription;
            if (_lastTranscriptionOptions != null && _lastTranscriptionOptions.Count > 0)
            {
                // Pick the option with the most pairs (most "/" separators)
                bestTranscription = _lastTranscriptionOptions
                    .OrderByDescending(s => s.Count(c => c == '/'))
                    .First();
            }

            var pairs = ParseTranscriptionPairs(bestTranscription);
            if (pairs.Count < 2) return; // Only 1 pair — nothing extra to distribute

            var items = GetVisibleItems();
            var anchor = _editorLoadedItem ?? _currentItem;
            int idx = anchor != null ? items.IndexOf(anchor) : -1;
            if (idx < 0) return;

            var autoFilled = new List<string>();

            for (int offset = 1; offset <= 2 && idx + offset < items.Count; offset++)
            {
                var candidate = items[idx + offset];
                foreach (var (key, val) in pairs)
                {
                    if (!string.IsNullOrWhiteSpace(val) && TranscriptionKeyMatchesItem(key, candidate))
                    {
                        candidate.Value = val;
                        MarkUnsaved();
                        autoFilled.Add(candidate.DisplayLabel ?? candidate.Name ?? "next item");
                        break;
                    }
                }
            }

            if (autoFilled.Count > 0)
            {
                var note = new System.Windows.Controls.TextBlock
                {
                    Text = $"✓ Also filled: {string.Join(", ", autoFilled)}",
                    Foreground = System.Windows.Media.Brushes.Green,
                    FontSize = 12,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 8, 4, 0),
                    TextWrapping = TextWrapping.Wrap,
                };
                SuggestionsStack.Children.Add(note);
            }
        }

        // Splits "Model: X / Serial: Y" or "U-Value = 0.30 / SHGC = 0.25" into (key, value) pairs.
        private static List<(string key, string val)> ParseTranscriptionPairs(string text)
        {
            var pairs = new List<(string, string)>();
            foreach (var seg in Regex.Split(text.Trim(), @"\s*/\s*"))
            {
                var m = Regex.Match(seg.Trim(), @"^(.+?)\s*[:=]\s*(.+)$");
                if (m.Success)
                    pairs.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
            }
            return pairs;
        }

        // Returns true if the transcription key (e.g. "Serial", "SHGC") corresponds
        // to the checklist item by name. Specific keyword checks prevent false positives.
        private static bool TranscriptionKeyMatchesItem(string key, Item item)
        {
            string k = key.ToLowerInvariant().Trim();
            string name = (item.DisplayLabel ?? item.Name ?? "").ToLowerInvariant();

            if (k.Contains("serial") && name.Contains("serial")) return true;
            if (k.Contains("model") && !k.Contains("serial") && name.Contains("model") && !name.Contains("serial")) return true;
            if (k.Contains("shgc") && name.Contains("shgc")) return true;
            if ((k == "u-value" || k == "u value" || k == "u-factor" || k == "ufactor")
                && (name.Contains("u-factor") || name.Contains("u factor") || name.Contains("u-value") || name.Contains("u value"))) return true;
            if (k == "vt" && (name.Contains(" vt") || name.StartsWith("vt") || name.Contains("visible transmittance"))) return true;

            return false;
        }

        // ── MegaStats ────────────────────────────────────────────────────────────

        private void LoadMegaStats()
        {
            MegaStatsOverlay.Visibility = Visibility.Collapsed;
            MegaStatsCardPanel.Children.Clear();

            if (_statsService == null || string.IsNullOrWhiteSpace(_currentInspectionCode))
            {
                MegaStatsButton.Visibility = Visibility.Collapsed;
                return;
            }

            var items = _statsService!.GetItemsByTeamRate(_currentInspectionCode, maxItems: 15, minTeamRate: 3.0);
            if (items.Count == 0)
            {
                MegaStatsButton.Visibility = Visibility.Collapsed;
                return;
            }

            MegaStatsButtonText.Text = $"{items.Count} stats";
            MegaStatsButton.Visibility = Visibility.Visible;

            // Pre-build all cards so clicking opens instantly
            BuildMegaStatsCard(items);
        }

        private void BuildMegaStatsCard(List<ItemStats> items)
        {
            MegaStatsCardPanel.Children.Clear();

            foreach (var stat in items)
            {
                // ── Card border for each item ────────────────────────────────
                var card = new Border
                {
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xDD, 0xDD, 0xDD)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(10, 8, 10, 8),
                };
                var cardStack = new StackPanel();
                card.Child = cardStack;

                // Item number + description
                var titleBlock = new TextBlock
                {
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Text = string.IsNullOrWhiteSpace(stat.Description)
                        ? stat.Item ?? ""
                        : $"{stat.Item}  {stat.Description}",
                    Margin = new Thickness(0, 0, 0, 4),
                };
                cardStack.Children.Add(titleBlock);

                // TeamRate bar
                double pct = Math.Min(stat.TeamRate / 100.0, 1.0);
                var barBg = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xE5, 0xE7, 0xEB)),
                    CornerRadius = new CornerRadius(3),
                    Height = 8,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                var barFill = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x1E, 0x40, 0xAF)),
                    CornerRadius = new CornerRadius(3),
                    Height = 8,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                barFill.Loaded += (s, e) =>
                {
                    if (barBg.ActualWidth > 0)
                        barFill.Width = barBg.ActualWidth * pct;
                };
                barBg.SizeChanged += (s, e) => barFill.Width = e.NewSize.Width * pct;
                // Use a Grid so fill overlays background
                var barGrid = new System.Windows.Controls.Grid();
                barGrid.Children.Add(barBg);
                barGrid.Children.Add(barFill);
                cardStack.Children.Add(barGrid);

                // Rate line
                var ratePanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

                var teamLabel = new TextBlock
                {
                    Text = $"Team: {stat.TeamRate:F0}%",
                    FontSize = 11,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x40, 0xAF)),
                    FontWeight = FontWeights.SemiBold,
                };
                ratePanel.Children.Add(teamLabel);

                // Inspector's personal rate (if available)
                if (!string.IsNullOrWhiteSpace(_currentInspectorName) && _statsService != null)
                {
                    var myStats = _statsService.GetItemStats(_currentInspectorName, _currentInspectionCode ?? "", stat.Item ?? "");
                    if (myStats != null)
                    {
                        string indicator = myStats.Indicator switch
                        {
                            "strength" => "  ✓",
                            "blind_spot" => "  ⚠",
                            _ => ""
                        };
                        var myLabel = new TextBlock
                        {
                            Text = $"   You: {myStats.InspectorRate:F0}%{indicator}",
                            FontSize = 11,
                            Foreground = myStats.Indicator == "blind_spot"
                                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26))
                                : myStats.Indicator == "strength"
                                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A))
                                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
                        };
                        ratePanel.Children.Add(myLabel);
                    }
                }
                cardStack.Children.Add(ratePanel);

                // Top quick comments
                if (_quickCommentsService != null)
                {
                    var comments = _quickCommentsService.GetSuggestions(_currentInspectionCode ?? "", stat.Item ?? "", 3);
                    if (comments.Count > 0)
                    {
                        foreach (var qc in comments)
                        {
                            var commentBlock = new TextBlock
                            {
                                Text = $"› {qc}",
                                FontSize = 11,
                                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 1, 0, 0),
                            };
                            cardStack.Children.Add(commentBlock);
                        }
                    }
                }

                MegaStatsCardPanel.Children.Add(card);
            }
        }

        private void MegaStatsButton_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            MegaStatsOverlay.Visibility = MegaStatsOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void MegaStatsOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Close on backdrop click or ✕ click
            MegaStatsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void MegaStatsScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _megaScrollStartPoint = e.GetPosition(MegaStatsScrollViewer);
            _megaScrollStartOffset = MegaStatsScrollViewer.VerticalOffset;
            _megaIsScrolling = true;
            _megaScrollStarted = false;
        }

        private void MegaStatsScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_megaIsScrolling && _megaScrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                double deltaY = _megaScrollStartPoint.Value.Y - e.GetPosition(MegaStatsScrollViewer).Y;
                if (!_megaScrollStarted && Math.Abs(deltaY) > 15)
                {
                    _megaScrollStarted = true;
                    Mouse.Capture(MegaStatsScrollViewer);
                }
                if (_megaScrollStarted)
                {
                    MegaStatsScrollViewer.ScrollToVerticalOffset(_megaScrollStartOffset + deltaY);
                    e.Handled = true;
                }
            }
        }

        private void MegaStatsScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _megaScrollStarted;
            _megaIsScrolling = false;
            _megaScrollStartPoint = null;
            _megaScrollStarted = false;
            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// Auto-set item value to "Fail" when a comment is added, but only for Pass/Fail/NA type items.
        /// </summary>
        private void AutoSetFailIfApplicable()
        {
            // Use _editorLoadedItem to avoid drift issues from touch/scroll events
            if (_editorLoadedItem == null) return;

            AutoSetFailForItemWithComment(_editorLoadedItem, CommentsTextBox.Text, refreshClassicStatus: true);
        }

        private void AutoSetFailForItemWithComment(Item item, string? commentText, bool refreshClassicStatus)
        {
            string controlName = item.ControlName?.ToLower() ?? "";
            bool isPassFailType = controlName.Contains("passfail"); // passfail, passfailni, passfailna (not yesno)

            if (isPassFailType && !string.IsNullOrWhiteSpace(commentText))
            {
                string oldValue = item.Value?.ToString()?.ToLower() ?? "";
                if (oldValue == "fail")
                    return;

                item.Value = "Fail";

                // Also update _currentItem if it's the same item (consistency)
                if (_currentItem != null && _currentItem == item)
                {
                    _currentItem.Value = "Fail";
                }

                if (refreshClassicStatus)
                    LoadStatusControls(item); // Refresh the status buttons UI
                
                // Update just the one tree node's color instead of rebuilding the entire tree
                UpdateTreeNodeColor(item);
            }
        }

        /// <summary>
        /// Surgically update just one tree node's color badge without rebuilding the entire tree.
        /// This avoids the 15-20 second freeze that PopulateTreeView causes on tablets.
        /// </summary>
        private void UpdateTreeNodeColor(Item item)
        {
            try
            {
                foreach (TreeViewItem sectionNode in SectionsTreeView.Items)
                {
                    foreach (TreeViewItem itemNode in sectionNode.Items)
                    {
                        if (itemNode.Tag == item && itemNode.Header is DockPanel panel)
                        {
                            // Find the Border (number badge) - it's the first child of the panel
                            foreach (var child in panel.Children)
                            {
                                if (child is Border numberBorder && numberBorder.Child is TextBlock numberText)
                                {
                                    string valueStr = item.Value?.ToString()?.ToLower() ?? "";
                                    
                                    if (valueStr.Contains("pass") || valueStr == "yes" || valueStr == "✓" || valueStr == "ok")
                                    {
                                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(144, 238, 144));
                                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(0, 80, 0));
                                    }
                                    else if (valueStr.Contains("fail") || valueStr == "no" || valueStr == "✗" || valueStr.Contains("defect"))
                                    {
                                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(255, 150, 150));
                                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(139, 0, 0));
                                    }
                                    else if (valueStr.Contains("pending") || valueStr == "na" || valueStr.Contains("n/a") || valueStr == "ni")
                                    {
                                        numberBorder.Background = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                                        numberText.Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80));
                                    }
                                    
                                    // Also update the parent section's fail counter
                                    UpdateSectionCounter(sectionNode);
                                    
                                    return; // Found and updated, done
                                }
                            }
                        }
                    }
                }
            }
            catch { } // Fail silently - worst case the color doesn't update until next full refresh
        }

        /// <summary>
        /// Surgically update a section header's (x / y) fail counter
        /// without rebuilding the entire tree. Star is inline in counter text.
        /// </summary>
        private void UpdateSectionCounter(TreeViewItem sectionNode)
        {
            try
            {
                if (sectionNode.Tag is not Section section) return;
                if (sectionNode.Header is not DockPanel sectionPanel) return;

                // Count current fails in this section (only "fail" values)
                int currentFailCount = section.Items.Count(i =>
                    (i.Value?.ToString()?.ToLower() ?? "").Contains("fail"));

                double? teamAvg = GetCurrentSectionAverage(section.Number);
                if (!teamAvg.HasValue) return;

                bool meetsOrExceedsAvg = currentFailCount >= teamAvg.Value && currentFailCount > 0;

                // Find and update the counter TextBlock (tagged "SectionCounter")
                // It's nested inside a StackPanel (sectionControlsPanel) inside the DockPanel
                TextBlock? counterText = null;
                foreach (var child in sectionPanel.Children)
                {
                    if (child is TextBlock tb && tb.Tag?.ToString() == "SectionCounter")
                        counterText = tb;
                    else if (child is Panel innerPanel)
                    {
                        foreach (var innerChild in innerPanel.Children)
                        {
                            if (innerChild is TextBlock itb && itb.Tag?.ToString() == "SectionCounter")
                                counterText = itb;
                        }
                    }
                }

                if (counterText != null)
                {
                    counterText.Text = meetsOrExceedsAvg
                        ? $"({currentFailCount} / {teamAvg.Value}) ★"
                        : $"({currentFailCount} / {teamAvg.Value})";
                    counterText.Foreground = meetsOrExceedsAvg
                        ? new SolidColorBrush(Color.FromRgb(30, 80, 180))
                        : new SolidColorBrush(Color.FromRgb(120, 120, 120));
                    counterText.ToolTip = meetsOrExceedsAvg
                        ? $"★ Above team average! ({currentFailCount} vs {teamAvg.Value} avg)"
                        : $"Your fails: {currentFailCount}\nTeam avg: {teamAvg.Value} per inspection";
                }

                // Also update the inspection-wide total counter
                UpdateInspectionTotalCounter();
            }
            catch { } // Fail silently
        }

        private void CommentsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentItem != null)
                MarkUnsaved();

            AutoSetFailIfApplicable();
            UpdateClassicSpecialistFlagButtonState();
            
            // Hide quick suggestions overlay when user types something
            if (!string.IsNullOrEmpty(CommentsTextBox.Text))
            {
                QuickSuggestionsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Show quick suggestion buttons if available for the current item.
        /// These are AI-polished common comments from the Global Failure Library.
        /// </summary>
        private void UpdateQuickSuggestionsOverlay()
        {
            // Hide by default
            QuickSuggestionsOverlay.Visibility = Visibility.Collapsed;
            QuickSuggestionA.Visibility = Visibility.Collapsed;
            QuickSuggestionB.Visibility = Visibility.Collapsed;
            QuickSuggestionC.Visibility = Visibility.Collapsed;

            // Only show if comment box is empty
            if (!string.IsNullOrEmpty(CommentsTextBox.Text))
                return;

            // Need current item and inspection code
            if (_currentItem == null || string.IsNullOrEmpty(_currentInspectionCode))
                return;

            // Get suggestions for this app/item
            var suggestions = _quickCommentsService.GetSuggestions(_currentInspectionCode, _currentItem.Number ?? "");
            
            if (suggestions.Count == 0)
                return;

            // Show overlay with available suggestions
            QuickSuggestionsOverlay.Visibility = Visibility.Visible;

            if (suggestions.Count >= 1)
            {
                QuickSuggestionAText.Text = suggestions[0];
                QuickSuggestionA.Visibility = Visibility.Visible;
            }
            if (suggestions.Count >= 2)
            {
                QuickSuggestionBText.Text = suggestions[1];
                QuickSuggestionB.Visibility = Visibility.Visible;
            }
            if (suggestions.Count >= 3)
            {
                QuickSuggestionCText.Text = suggestions[2];
                QuickSuggestionC.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Handle click on a quick suggestion button - insert the text and hide overlay.
        /// </summary>
        private void QuickSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string? text = null;
                var tag = btn.Tag?.ToString();
                
                if (tag == "A") text = QuickSuggestionAText.Text;
                else if (tag == "B") text = QuickSuggestionBText.Text;
                else if (tag == "C") text = QuickSuggestionCText.Text;

                if (!string.IsNullOrEmpty(text))
                {
                    CommentsTextBox.Text = text;
                    CommentsTextBox.CaretIndex = text.Length;
                    CommentsTextBox.Focus();
                    
                    // Auto-select the prefix button if the comment has one
                    string prefix = UserDataService.ExtractPrefix(text);
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        // Find and check the matching prefix button
                        foreach (var prefixBtn in _prefixButtons)
                        {
                            if (prefixBtn.Tag?.ToString()?.ToLower() == prefix)
                            {
                                prefixBtn.IsChecked = true;
                                break;
                            }
                        }
                    }
                    
                    // Auto-set to Fail for Pass/Fail/NA type items
                    AutoSetFailIfApplicable();
                }

                // Hide overlay
                QuickSuggestionsOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void SaveToMyCommentsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_userDataService == null || _currentItem == null || string.IsNullOrEmpty(_currentInspectionCode))
            {
                MessageBox.Show("No inspection loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string comment = CommentsTextBox.Text;
            if (string.IsNullOrWhiteSpace(comment))
            {
                MessageBox.Show("No comment to save.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _userDataService.SaveComment(_currentInspectionCode, _currentItem.Number ?? "", comment);
            LoadSavedComments();
        }

        private void ApplyToItemButton_Click(object sender, RoutedEventArgs e)
        {
            // Use _editorLoadedItem to avoid touch-drift issues
            if (_editorLoadedItem == null) return;

            // Save whatever is in the UI (no validation required)
            SyncCurrentItemFromUI();

            // Refresh tree to show updated colors
            PopulateTreeView();
            
            // Refresh status controls if value changed
            LoadStatusControls(_editorLoadedItem);
            
            MarkUnsaved();
        }

        private void GenerateSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInspection == null) return;

            SyncCurrentItemFromUI();

            // Check if there are any items with trade prefixes
            bool hasTradeItems = _currentInspection.Sections
                .SelectMany(s => s.Items)
                .Any(item => !string.IsNullOrEmpty(item.Comments) && item.Comments.StartsWith("["));

            if (!hasTradeItems)
            {
                MessageBox.Show("No items with [trade] prefixed comments found.\n\nAdd comments starting with [trade] to items first.", 
                    "No Items", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Generate summary (duplicates last item with summary comment)
            GenerateSummaryInternal();
            
            PopulateTreeView();
            MarkUnsaved();
        }

        /// <summary>
        /// Auto-fix passfail items that have comments or photos but have NO value set.
        /// Called on save to catch any items that weren't manually "applied".
        /// NOTE: Does NOT override items that are explicitly set to NI/Pass - respects inspector's choice.
        /// </summary>
        private void AutoFixPassFailItems()
        {
            if (_currentInspection?.Sections == null) return;
            
            foreach (var section in _currentInspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    string controlName = item.ControlName?.ToLower() ?? "";
                    bool isPassFailType = controlName.Contains("passfail");
                    
                    if (!isPassFailType) continue;
                    
                    bool hasComment = !string.IsNullOrWhiteSpace(item.Comments);
                    bool hasPhotos = item.Pictures != null && item.Pictures.Count > 0;
                    string currentValue = item.Value?.ToString()?.Trim() ?? "";
                    
                    // Only auto-set to Fail if value is EMPTY (not set yet)
                    // Respect explicit NI/Pass values - don't override inspector's choice
                    bool hasNoValue = string.IsNullOrEmpty(currentValue);
                    
                    if ((hasComment || hasPhotos) && hasNoValue)
                    {
                        item.Value = "Fail";
                    }
                }
            }
        }

        private void SaveFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentInspection == null || !_saveService.HasFile) return;

            SyncCurrentItemFromUI();
            
            // Auto-fix: Set all passfail items with comments/photos to Fail
            AutoFixPassFailItems();
            
            // VALIDATION DISABLED per Trent 2026-02-24: No longer check/alert for missing required items or photos
            // Inspectors can save files without filling out required fields
            
            /* REMOVED: Required photos check
            // Check for required items missing required photos
            // Skip Section 1 (Administrative) — hidden from Red users, except for SCI inspections
            bool isSciInspection = (_currentInspectionCode ?? "").Equals("SCI", StringComparison.OrdinalIgnoreCase);
            var visibleSections = _currentInspection.Sections
                .Where(s => isSciInspection || (s.Number != "1" && !s.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true));
            
            var missingPhotos = visibleSections
                .SelectMany(s => s.Items)
                .Where(item => item.Required && item.IsPictureRequired && item.Pictures.Count == 0)
                .ToList();
            
            if (missingPhotos.Count > 0)
            {
                string itemList = string.Join("\n", missingPhotos.Take(5).Select(i => $"  • {i.Number} {i.Name}"));
                if (missingPhotos.Count > 5)
                    itemList += $"\n  ... and {missingPhotos.Count - 5} more";
                
                var result = MessageBox.Show(
                    $"⚠️ {missingPhotos.Count} required item(s) missing required photos:\n\n{itemList}\n\n" +
                    "Save anyway?",
                    "Missing Required Photos",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result != MessageBoxResult.Yes)
                    return; // Don't save
            }
            */
            
            /* REMOVED: Required values check
            // Check for required items with no value (blank/null) — skip Section 1 (Administrative)
            var missingValues = visibleSections
                .SelectMany(s => s.Items)
                .Where(item => item.Required && (item.Value == null || string.IsNullOrWhiteSpace(item.Value?.ToString())))
                .ToList();
            
            if (missingValues.Count > 0)
            {
                string message;
                if (missingValues.Count <= 5)
                {
                    string itemList = string.Join("\n", missingValues.Select(i => $"  • {i.Number} {i.Name}"));
                    message = $"⚠️ {missingValues.Count} required item(s) not filled out:\n\n{itemList}\n\n" +
                              "Save anyway?";
                }
                else
                {
                    message = $"⚠️ More than 5 required items are not filled out.\n\n" +
                              "Look for asterisks (*) or click the REQ button above the checklist to find them.\n\n" +
                              "Save anyway?";
                }
                
                var result = MessageBox.Show(
                    message,
                    "Missing Required Values",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                
                if (result != MessageBoxResult.Yes)
                    return; // Don't save
            }
            */
            
            // Check for failed items with comments but no [trade] prefix
            var missingTrade = _currentInspection.Sections
                .SelectMany(s => s.Items)
                .Where(item =>
                {
                    string valueStr = item.Value?.ToString()?.ToLower() ?? "";
                    bool isFail = valueStr.Contains("fail");
                    bool hasComment = !string.IsNullOrWhiteSpace(item.Comments);
                    bool hasTrade = hasComment && item.Comments.TrimStart().StartsWith("[");
                    return isFail && hasComment && !hasTrade;
                })
                .ToList();

            // Check if there are items with [trade] prefixed comments
            bool hasTradeItems = _currentInspection.Sections
                .SelectMany(s => s.Items)
                .Any(item => !string.IsNullOrEmpty(item.Comments) && item.Comments.TrimStart().StartsWith("["));
            
            // If there are trade items, offer to generate a timestamped summary
            if (hasTradeItems)
            {
                var result = MessageBox.Show(
                    "Items with [trade] prefixes detected.\n\n" +
                    "• Yes = Generate timestamped summary and save\n" +
                    "• No = Save without new summary",
                    "Generate New Summary?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    GenerateSummaryInternal(); // Generate timestamped summary
                    DoSave();
                    return;
                }
                // If No, continue with save
            }

            DoSave();
        }
        
        private void GenerateSummaryInternal()
        {
            if (_currentInspection == null) return;

            // Collect items with [trade] prefixed comments (exclude summary items)
            var itemsWithComments = new List<(string Number, string Comments)>();
            
            foreach (var section in _currentInspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (!string.IsNullOrEmpty(item.Comments) && 
                        item.Comments.StartsWith("[") && 
                        !item.Comments.StartsWith("Summary for"))
                        itemsWithComments.Add((item.Number ?? "", item.Comments));
                }
            }

            if (itemsWithComments.Count == 0) return;

            // Build summary text with date
            string dateStr = DateTime.Now.ToString("MMM d, yyyy");
            string summaryText = BuildSummaryComment(dateStr, itemsWithComments);
            string todaySummaryPrefix = $"Summary for {dateStr}";

            // Find Section 1 (Administrative) - summary belongs there
            var section1 = _currentInspection.Sections
                .FirstOrDefault(s => s.Number == "1" || 
                                     s.Name?.Trim().Equals("Administrative", StringComparison.OrdinalIgnoreCase) == true);
            
            if (section1 == null || section1.Items.Count == 0)
            {
                // Fallback to last section if Section 1 not found
                section1 = _currentInspection.Sections.LastOrDefault();
                if (section1 == null || section1.Items.Count == 0) return;
            }

            // Check if there's already a summary for TODAY in Section 1 - update it instead of creating new
            var existingTodaySummary = section1.Items
                .FirstOrDefault(i => !string.IsNullOrEmpty(i.Comments) && 
                                     i.Comments.StartsWith(todaySummaryPrefix));

            if (existingTodaySummary != null)
            {
                // Update existing summary for today
                existingTodaySummary.Comments = summaryText;
                return;
            }

            // Find the target item for summary: prefer 1.20 "Subcontractor", fallback to last item in Section 1
            var targetItem = section1.Items
                .FirstOrDefault(i => i.Number == "1.20" || 
                                     (i.Name?.IndexOf("Subcontractor", StringComparison.OrdinalIgnoreCase) >= 0));
            
            if (targetItem == null)
            {
                // Fallback to last item in Section 1
                targetItem = section1.Items.Last();
            }
            
            // Calculate ResultSortOrder for the duplicate
            int resultSortOrder = section1.Items
                .Where(i => i.Number == targetItem.Number)
                .Count();

            // Create a duplicate of the target item with the summary as its comment
            var summaryItem = new Item
            {
                ItemId = targetItem.ItemId,
                Name = targetItem.Name,
                Number = targetItem.Number,
                ControlName = targetItem.ControlName,
                DisplayLabel = targetItem.DisplayLabel,
                ValueList = targetItem.ValueList != null ? new List<string>(targetItem.ValueList) : null,
                Required = targetItem.Required,
                SortOrder = targetItem.SortOrder,
                HidePicturesButton = targetItem.HidePicturesButton,
                HideCommentsButton = targetItem.HideCommentsButton,
                IsCopied = true,
                ResultSortOrder = resultSortOrder,
                Value = targetItem.Value, // Copy parent's yes/no value for summary duplicates
                Comments = summaryText,
                Pictures = new List<Picture>()
            };
            
            // Add to Section 1
            section1.Items.Add(summaryItem);
        }
        
        private string BuildSummaryComment(string dateStr, List<(string Number, string Comments)> items)
        {
            // Group by trade prefix
            var grouped = items
                .GroupBy(i => UserDataService.ExtractPrefix(i.Comments))
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .OrderBy(g => g.Key);

            var tradeSections = new List<string>();
            
            foreach (var trade in grouped)
            {
                string tradeName = trade.Key.Trim('[', ']').ToLower();
                int count = trade.Count();
                string countText = count == 1 ? "1 item" : $"{count} items";
                
                var itemEntries = new List<string>();
                foreach (var item in trade.OrderBy(i => i.Number))
                {
                    string comment = UserDataService.StripPrefixAndSuffix(item.Comments);
                    itemEntries.Add($"{item.Number} - {comment}");
                }
                
                // Format: [framer] 2 items || 3.1 - comment || 3.2 - comment
                tradeSections.Add($"[{tradeName}] {countText} || {string.Join(" || ", itemEntries)}");
            }

            // Format: Summary for Jan 29, 2026 |||| [framer] 2 items || ... |||| [plumber] 1 item || ...
            return $"Summary for {dateStr} |||| {string.Join(" |||| ", tradeSections)}";
        }
        
        private void DoSave()
        {
            if (_currentInspection == null || !_saveService.HasFile) return;
            
            try
            {
                SaveCurrentInspectionInPlace();

                bool shouldShowResultPicker = !_skipResultCheck && ShouldShowResultPicker();
                if (shouldShowResultPicker && !ShowResultPicker(closeAfterPicker: false))
                {
                    return;
                }

                _activityService.LogClose();
                ReleaseInspectionLocks();
                
                // Reset state — return to welcome/splash screen
                _currentInspection = null;
                _currentItem = null;
                _editorLoadedItem = null;
                _currentFilePath = null;
                _currentInspectionCode = null;
                _currentPhotoData = null;
                _currentPhotoIndex = 0;
                _selectedPrefix = null;
                _selectedSuffixes.Clear();
                _statsMode = StatsMode.Global;
                
                // Clear the UI
                SectionsTreeView.Items.Clear();
                EditorPanel.Visibility = Visibility.Collapsed;
                SelectItemPanel.Visibility = Visibility.Collapsed;
                WelcomePanel.Visibility = Visibility.Visible;
                
                // Reset title and header
                Title = GetDefaultWindowTitle();
                FileNameText.Text = "";
                AddressText.Text = "";
                BuilderText.Text = "";
                
                // Disable buttons that need an open file
                SaveFileButton.IsEnabled = false;
                SaveTemplateButton.IsEnabled = false;
                ApplyTemplateButton.IsEnabled = false;
                
                MarkSaved();

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveCurrentInspectionInPlace()
        {
            if (_currentInspection == null || !_saveService.HasFile) return;

            // Sweep items with PassFail controls: any with [trade] prefix comment should be Fail
            // BUT respect explicit NI values - don't override inspector's choice
            foreach (var section in _currentInspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (!string.IsNullOrEmpty(item.Comments) && item.Comments.TrimStart().StartsWith("["))
                    {
                        string controlName = item.ControlName?.ToLower() ?? "";
                        string currentValue = item.Value?.ToString()?.Trim() ?? "";
                        bool hasNoValue = string.IsNullOrEmpty(currentValue);

                        if (controlName.Contains("passfail") && hasNoValue)
                        {
                            item.Value = "Fail";
                        }
                    }
                }
            }

            _saveService.Save(_currentInspection);
            _activityService.LogSave();
            MarkSaved();
        }

        private void SyncCurrentItemFromUI()
        {
            // Use _editorLoadedItem - this is the item that's actually shown in the editor
            // and won't drift due to touch/scroll events
            if (_editorLoadedItem == null) return;
            
            // Always sync comment text (even if empty, to allow clearing)
            _editorLoadedItem.Comments = string.IsNullOrWhiteSpace(CommentsTextBox.Text) ? null : CommentsTextBox.Text;
            
            // Sync value from StatusTextBox if it's visible (for text-input type controls)
            if (StatusTextBox.Visibility == Visibility.Visible && !StatusTextBox.IsReadOnly)
            {
                _editorLoadedItem.Value = StatusTextBox.Text;
            }
            
            // Determine if this should be marked as Fail:
            // - Has a trade prefix selected, OR
            // - Comment text starts with [
            bool hasTrade = !string.IsNullOrEmpty(_selectedPrefix) || 
                           (CommentsTextBox.Text?.TrimStart().StartsWith("[") ?? false);
            
            // Photos are now added immediately when captured/selected, no need to add here
            // Just update the comment on the current photo if there is one
            if (_editorLoadedItem.Pictures.Count > 0 && _currentPhotoIndex >= 0 && _currentPhotoIndex < _editorLoadedItem.Pictures.Count)
            {
                _editorLoadedItem.Pictures[_currentPhotoIndex].Comment = CommentsTextBox.Text ?? "";
            }
            
            // Mark as Fail if trade prefix is selected/present AND (has comment OR has photo)
            // BUT only for PassFail controls - not for YesNo or Text inputs
            // AND respect explicit NI values - don't override inspector's choice
            bool hasContent = !string.IsNullOrWhiteSpace(CommentsTextBox.Text) || _currentPhotoData != null || _editorLoadedItem.Pictures.Count > 0;
            string controlName = _editorLoadedItem.ControlName?.ToLower() ?? "";
            bool isPassFailType = controlName.Contains("passfail"); // not yesno
            string currentValue = _editorLoadedItem.Value?.ToString()?.ToLower() ?? "";
            bool isExplicitlyNI = currentValue == "ni" || currentValue == "na" || currentValue == "n/a";
            
            // Only auto-set to Fail if not explicitly NI
            if (hasTrade && hasContent && isPassFailType && !isExplicitlyNI)
            {
                _editorLoadedItem.Value = "Fail";
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            if (settingsWindow.ShowDialog() == true)
            {
                if (!string.IsNullOrEmpty(settingsWindow.ApiKey))
                {
                    _grokClient = new GrokApiClient(settingsWindow.ApiKey);
                    _grokClient.OnModelFallback += ShowModelFallbackAlert;
                }

                _suppressAdministrativeTools = SettingsWindow.LoadSuppressAdministrativeTools();
                _aiTone = SettingsWindow.LoadDefaultAiTone();
                ApplyAiTonePreference();
                if (_inlineEditorMode)
                    PopulateInlineChecklist(SearchFilterBox.Text);
            }
        }
        
        // Handle scroll wheel events to ensure scrolling works even when focus is on child controls
        private void EditorScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
        }
        
        // Handle scroll for left pane tree view
        private void TreeScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
        }

        private void MainWindow_GlobalPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _globalInlineSwipeStartPoint = null;
            _globalInlineSwipeStarted = false;
            _globalInlineSwipeCanCollapse = false;

            if (!_inlineEditorMode || string.IsNullOrWhiteSpace(_expandedInlineItemKey))
                return;

            var source = e.OriginalSource as DependencyObject;
            if (IsInsideTextEditingSurface(source) ||
                IsInsideInlineNumberpadSliderZone(source) ||
                IsInsideInlinePrefixSuffixSwipeZone(source) ||
                IsInsideInlineVerticalSwipeZone(source) ||
                FindAncestor<ComboBox>(source) != null)
            {
                return;
            }

            _globalInlineSwipeStartPoint = e.GetPosition(this);
            _globalInlineSwipeCanCollapse = true;
        }

        private void MainWindow_GlobalPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_globalInlineSwipeCanCollapse ||
                !_globalInlineSwipeStartPoint.HasValue ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point currentPoint = e.GetPosition(this);
            double deltaX = _globalInlineSwipeStartPoint.Value.X - currentPoint.X;
            double deltaY = _globalInlineSwipeStartPoint.Value.Y - currentPoint.Y;
            double absX = Math.Abs(deltaX);
            double absY = Math.Abs(deltaY);

            if (!_globalInlineSwipeStarted && deltaX > 45 && absX >= absY * 1.2)
            {
                _globalInlineSwipeStarted = true;
            }
        }

        private void MainWindow_GlobalPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool shouldCollapse = _globalInlineSwipeStarted &&
                                  _globalInlineSwipeCanCollapse &&
                                  !string.IsNullOrWhiteSpace(_expandedInlineItemKey);

            _globalInlineSwipeStartPoint = null;
            _globalInlineSwipeStarted = false;
            _globalInlineSwipeCanCollapse = false;

            if (shouldCollapse)
            {
                _expandedInlineItemKey = null;
                _expandedInlineItemInstance = null;
                _inlineQuickCommentsDismissedItem = null;
                AutoApplyCurrentItem();
                PopulateInlineChecklist(SearchFilterBox.Text);
                e.Handled = true;
            }
        }
        
        // Mouse-based scrolling for left pane (touch reports as mouse on this tablet)
        private void TreeScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsInsideTextEditingSurface(source) ||
                IsInsideInlineNumberpadSliderZone(source) ||
                IsInsideInlinePrefixSuffixSwipeZone(source))
            {
                _isScrolling = false;
                _scrollStarted = false;
                _checklistHorizontalSwipeStarted = false;
                _inlineStripSwipeStarted = false;
                _checklistSwipeStartedInInlineStrip = false;
                _checklistSwipeStartedInInlineVerticalZone = false;
                _checklistSwipeItem = null;
                return;
            }

            _scrollStartPoint = e.GetPosition(TreeScrollViewer);
            _scrollStartOffset = TreeScrollViewer.VerticalOffset;
            _isScrolling = true;
            _scrollStarted = false;
            _checklistHorizontalSwipeStarted = false;
            _inlineStripSwipeStarted = false;
            _checklistSwipeStartedInInlineVerticalZone = IsInsideInlineVerticalSwipeZone(source);
            _checklistSwipeStartedInInlineStrip = IsInsideInlinePrefixSuffixSwipeZone(source) ||
                _checklistSwipeStartedInInlineVerticalZone;
            _checklistSwipeItem = FindInlineItemFromSource(source) ?? _currentItem;
            // Don't capture yet - only capture once drag starts
        }
        
        private void TreeScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isScrolling && _scrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(TreeScrollViewer);
                double deltaX = _scrollStartPoint.Value.X - currentPoint.X;
                double deltaY = _scrollStartPoint.Value.Y - currentPoint.Y;
                
                // Decide direction after a small threshold. Vertical always scrolls the checklist;
                // horizontal opens/closes the focused inline item.
                if (!_scrollStarted)
                {
                    double absX = Math.Abs(deltaX);
                    double absY = Math.Abs(deltaY);
                    if (_checklistSwipeStartedInInlineVerticalZone)
                    {
                        return;
                    }
                    else if (absY > 15 && absY >= absX * 1.15)
                    {
                        _scrollStarted = true;
                        _checklistHorizontalSwipeStarted = false;
                        _inlineStripSwipeStarted = false;
                        Mouse.Capture(TreeScrollViewer);
                    }
                    else if (_checklistSwipeStartedInInlineStrip && absX > 18 && absX >= absY)
                    {
                        _scrollStarted = true;
                        _inlineStripSwipeStarted = true;
                        Mouse.Capture(TreeScrollViewer);
                    }
                    else if (_inlineEditorMode && absX > 30 && absX >= absY * 1.15)
                    {
                        _scrollStarted = true;
                        _checklistHorizontalSwipeStarted = !_checklistSwipeStartedInInlineStrip;
                        _inlineStripSwipeStarted = _checklistSwipeStartedInInlineStrip;
                        Mouse.Capture(TreeScrollViewer);
                    }
                }
                
                if (_scrollStarted && !_checklistHorizontalSwipeStarted)
                {
                    TreeScrollViewer.ScrollToVerticalOffset(_scrollStartOffset + deltaY);
                    e.Handled = true;
                }
                else if (_checklistHorizontalSwipeStarted || _inlineStripSwipeStarted)
                {
                    e.Handled = true;
                }
            }
        }
        
        private void TreeScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point? startPoint = _scrollStartPoint;
            Point endPoint = e.GetPosition(TreeScrollViewer);
            bool wasHorizontalSwipe = _checklistHorizontalSwipeStarted;
            bool wasInlineStripSwipe = _inlineStripSwipeStarted;
            bool wasScrolling = _scrollStarted;
            _isScrolling = false;
            _scrollStartPoint = null;
            _scrollStarted = false;
            _checklistHorizontalSwipeStarted = false;
            _inlineStripSwipeStarted = false;
            _checklistSwipeStartedInInlineStrip = false;
            _checklistSwipeStartedInInlineVerticalZone = false;
            
            if (wasScrolling)
            {
                Mouse.Capture(null);
                if (wasHorizontalSwipe && !wasInlineStripSwipe && startPoint.HasValue && _checklistSwipeItem != null)
                {
                    double deltaX = startPoint.Value.X - endPoint.X;
                    if (deltaX > 30)
                        SetInlineItemExpanded(_checklistSwipeItem, expand: false);
                    else if (deltaX < -30)
                        SetInlineItemExpanded(_checklistSwipeItem, expand: true);
                }
                _checklistSwipeItem = null;
                e.Handled = true; // Block the click only if we dragged
            }
            else
            {
                _checklistSwipeItem = null;
            }
            // If not scrolling, let the click through normally
        }

        private Item? FindInlineItemFromSource(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement { Tag: Item item })
                    return item;
                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private bool IsInsideInlinePrefixSuffixSwipeZone(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement { Tag: string tag } &&
                    tag == "InlinePrefixSuffixSwipeZone")
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private bool IsInsideInlineVerticalSwipeZone(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement { Tag: string tag } &&
                    tag == "InlineVerticalSwipeZone")
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private bool IsInsideInlineNumberpadSliderZone(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement { Tag: InlineNumberpadSliderAction })
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private bool IsInsideTextEditingSurface(DependencyObject? source)
        {
            return FindAncestor<TextBox>(source) != null ||
                   FindAncestor<PasswordBox>(source) != null ||
                   FindAncestor<RichTextBox>(source) != null;
        }
        
        // Mouse-based scrolling for right pane editor
        private Point? _editorScrollStartPoint;
        private double _editorScrollStartOffset;
        private bool _editorIsScrolling = false;
        private bool _editorScrollStarted = false;
        
        private void EditorScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Text editing and dropdowns need their normal selection behavior. Buttons still
            // participate so dragging over saved comments / AI buttons scrolls reliably.
            if (IsInsideTextEditingSurface(e.OriginalSource as DependencyObject) ||
                FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) != null)
            {
                return;
            }
            
            _editorScrollStartPoint = e.GetPosition(EditorScrollViewer);
            _editorScrollStartOffset = EditorScrollViewer.VerticalOffset;
            _editorIsScrolling = true;
            _editorScrollStarted = false;
            // Don't capture yet - only capture once drag starts
        }
        
        private void EditorScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_editorIsScrolling && _editorScrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                if (IsInsideVerticalNestedEditorScroller(e.OriginalSource as DependencyObject))
                    return;

                var currentPoint = e.GetPosition(EditorScrollViewer);
                double deltaX = _editorScrollStartPoint.Value.X - currentPoint.X;
                double deltaY = _editorScrollStartPoint.Value.Y - currentPoint.Y;
                
                if (!_editorScrollStarted && Math.Abs(deltaY) > 15 && Math.Abs(deltaY) >= Math.Abs(deltaX))
                {
                    _editorScrollStarted = true;
                    Mouse.Capture(EditorScrollViewer);
                }
                
                if (_editorScrollStarted)
                {
                    EditorScrollViewer.ScrollToVerticalOffset(_editorScrollStartOffset + deltaY);
                    e.Handled = true;
                }
            }
        }
        
        private void EditorScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _editorScrollStarted;
            _editorIsScrolling = false;
            _editorScrollStartPoint = null;
            _editorScrollStarted = false;
            
            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        private bool IsInsideVerticalNestedEditorScroller(DependencyObject? source)
        {
            return IsDescendantOf(source, SavedCommentsScrollViewer) ||
                   IsDescendantOf(source, SuggestionsScrollViewer);
        }

        private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
        {
            while (source != null)
            {
                if (ReferenceEquals(source, ancestor))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
        {
            while (source != null)
            {
                if (source is T target)
                    return target;
                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject? source) where T : DependencyObject
        {
            if (source == null)
                yield break;

            int childCount = VisualTreeHelper.GetChildrenCount(source);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(source, i);
                if (child is T target)
                    yield return target;

                foreach (T descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        private void InlineHorizontalScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ScrollViewer scroller)
                return;

            _inlineHorizontalScrollViewer = scroller;
            _inlineHorizontalScrollStartPoint = e.GetPosition(scroller);
            _inlineHorizontalScrollStartOffset = scroller.HorizontalOffset;
            _inlineHorizontalIsScrolling = true;
            _inlineHorizontalScrollStarted = false;
        }

        private void InlineHorizontalScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_inlineHorizontalIsScrolling ||
                !_inlineHorizontalScrollStartPoint.HasValue ||
                _inlineHorizontalScrollViewer == null ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentPoint = e.GetPosition(_inlineHorizontalScrollViewer);
            double deltaX = _inlineHorizontalScrollStartPoint.Value.X - currentPoint.X;
            double deltaY = _inlineHorizontalScrollStartPoint.Value.Y - currentPoint.Y;

            if (!_inlineHorizontalScrollStarted && Math.Abs(deltaX) > 12 && Math.Abs(deltaX) >= Math.Abs(deltaY))
            {
                _inlineHorizontalScrollStarted = true;
                CancelInlineChipLongPress();
                Mouse.Capture(_inlineHorizontalScrollViewer);
            }

            if (_inlineHorizontalScrollStarted)
            {
                _inlineHorizontalScrollViewer.ScrollToHorizontalOffset(_inlineHorizontalScrollStartOffset + deltaX);
                e.Handled = true;
            }
        }

        private void InlineHorizontalScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _inlineHorizontalScrollStarted;
            _inlineHorizontalIsScrolling = false;
            _inlineHorizontalScrollStartPoint = null;
            _inlineHorizontalScrollStarted = false;
            _inlineHorizontalScrollViewer = null;

            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        private void InlineHorizontalScrollViewer_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            if (sender is not ScrollViewer scroller)
                return;

            _inlineHorizontalScrollViewer = scroller;
            _inlineHorizontalScrollStartPoint = e.GetTouchPoint(scroller).Position;
            _inlineHorizontalScrollStartOffset = scroller.HorizontalOffset;
            _inlineHorizontalIsScrolling = true;
            _inlineHorizontalScrollStarted = false;
        }

        private void InlineHorizontalScrollViewer_PreviewTouchMove(object sender, TouchEventArgs e)
        {
            if (!_inlineHorizontalIsScrolling ||
                !_inlineHorizontalScrollStartPoint.HasValue ||
                _inlineHorizontalScrollViewer == null)
            {
                return;
            }

            var currentPoint = e.GetTouchPoint(_inlineHorizontalScrollViewer).Position;
            double deltaX = _inlineHorizontalScrollStartPoint.Value.X - currentPoint.X;
            double deltaY = _inlineHorizontalScrollStartPoint.Value.Y - currentPoint.Y;

            if (!_inlineHorizontalScrollStarted && Math.Abs(deltaX) > 10 && Math.Abs(deltaX) >= Math.Abs(deltaY))
            {
                _inlineHorizontalScrollStarted = true;
                CancelInlineChipLongPress();
                _inlineHorizontalScrollViewer.CaptureTouch(e.TouchDevice);
            }

            if (_inlineHorizontalScrollStarted)
            {
                _inlineHorizontalScrollViewer.ScrollToHorizontalOffset(_inlineHorizontalScrollStartOffset + deltaX);
                e.Handled = true;
            }
        }

        private void InlineHorizontalScrollViewer_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            bool wasScrolling = _inlineHorizontalScrollStarted;
            _inlineHorizontalScrollViewer?.ReleaseTouchCapture(e.TouchDevice);
            _inlineHorizontalIsScrolling = false;
            _inlineHorizontalScrollStartPoint = null;
            _inlineHorizontalScrollStarted = false;
            _inlineHorizontalScrollViewer = null;

            if (wasScrolling)
                e.Handled = true;
        }

        private void InlineHorizontalScrollViewer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_inlineHorizontalScrollStarted)
                Mouse.Capture(null);

            _inlineHorizontalIsScrolling = false;
            _inlineHorizontalScrollStartPoint = null;
            _inlineHorizontalScrollStarted = false;
            _inlineHorizontalScrollViewer = null;
            CancelInlineChipLongPress();
        }

        private void InlineVerticalScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ScrollViewer scroller)
                return;

            _inlineVerticalScrollViewer = scroller;
            _inlineVerticalScrollStartPoint = e.GetPosition(scroller);
            _inlineVerticalScrollStartOffset = scroller.VerticalOffset;
            _inlineVerticalIsScrolling = true;
            _inlineVerticalScrollStarted = false;
        }

        private void InlineVerticalScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_inlineVerticalIsScrolling ||
                !_inlineVerticalScrollStartPoint.HasValue ||
                _inlineVerticalScrollViewer == null ||
                e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            var currentPoint = e.GetPosition(_inlineVerticalScrollViewer);
            double deltaX = _inlineVerticalScrollStartPoint.Value.X - currentPoint.X;
            double deltaY = _inlineVerticalScrollStartPoint.Value.Y - currentPoint.Y;

            if (!_inlineVerticalScrollStarted && Math.Abs(deltaY) > 12 && Math.Abs(deltaY) >= Math.Abs(deltaX))
            {
                _inlineVerticalScrollStarted = true;
                Mouse.Capture(_inlineVerticalScrollViewer);
            }

            if (_inlineVerticalScrollStarted)
            {
                _inlineVerticalScrollViewer.ScrollToVerticalOffset(_inlineVerticalScrollStartOffset + deltaY);
                e.Handled = true;
            }
        }

        private void InlineVerticalScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _inlineVerticalScrollStarted;
            _inlineVerticalIsScrolling = false;
            _inlineVerticalScrollStartPoint = null;
            _inlineVerticalScrollStarted = false;
            _inlineVerticalScrollViewer = null;

            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        private void InlineVerticalScrollViewer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_inlineVerticalScrollStarted)
                Mouse.Capture(null);

            _inlineVerticalIsScrolling = false;
            _inlineVerticalScrollStartPoint = null;
            _inlineVerticalScrollStarted = false;
            _inlineVerticalScrollViewer = null;
        }
        
        // Mouse-based HORIZONTAL scrolling for PREFIX (trade buttons)
        private Point? _prefixScrollStartPoint;
        private double _prefixScrollStartOffset;
        private bool _prefixIsScrolling = false;
        private bool _prefixScrollStarted = false;
        
        private void PrefixScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _prefixScrollStartPoint = e.GetPosition(PrefixScrollViewer);
            _prefixScrollStartOffset = PrefixScrollViewer.HorizontalOffset;
            _prefixIsScrolling = true;
            _prefixScrollStarted = false;
        }
        
        private void PrefixScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_prefixIsScrolling && _prefixScrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(PrefixScrollViewer);
                double deltaX = _prefixScrollStartPoint.Value.X - currentPoint.X;
                
                if (!_prefixScrollStarted && Math.Abs(deltaX) > 15)
                {
                    _prefixScrollStarted = true;
                    Mouse.Capture(PrefixScrollViewer);
                }
                
                if (_prefixScrollStarted)
                {
                    PrefixScrollViewer.ScrollToHorizontalOffset(_prefixScrollStartOffset + deltaX);
                    e.Handled = true;
                }
            }
        }
        
        private void PrefixScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _prefixScrollStarted;
            _prefixIsScrolling = false;
            _prefixScrollStartPoint = null;
            _prefixScrollStarted = false;
            
            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }
        
        // Mouse-based HORIZONTAL scrolling for STATUS/VALUE buttons
        private Point? _statusScrollStartPoint;
        private double _statusScrollStartOffset;
        private bool _statusIsScrolling = false;
        private bool _statusScrollStarted = false;

        private void StatusScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _statusScrollStartPoint = e.GetPosition(StatusScrollViewer);
            _statusScrollStartOffset = StatusScrollViewer.HorizontalOffset;
            _statusIsScrolling = true;
            _statusScrollStarted = false;
        }

        private void StatusScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_statusIsScrolling && _statusScrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(StatusScrollViewer);
                double deltaX = _statusScrollStartPoint.Value.X - currentPoint.X;

                if (!_statusScrollStarted && Math.Abs(deltaX) > 15)
                {
                    _statusScrollStarted = true;
                    Mouse.Capture(StatusScrollViewer);
                }

                if (_statusScrollStarted)
                {
                    StatusScrollViewer.ScrollToHorizontalOffset(_statusScrollStartOffset + deltaX);
                    e.Handled = true;
                }
            }
        }

        private void StatusScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _statusScrollStarted;
            _statusIsScrolling = false;
            _statusScrollStartPoint = null;
            _statusScrollStarted = false;

            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }

        // Mouse-based HORIZONTAL scrolling for SUFFIX (location buttons)
        private Point? _suffixScrollStartPoint;
        private double _suffixScrollStartOffset;
        private bool _suffixIsScrolling = false;
        private bool _suffixScrollStarted = false;
        
        private void SuffixScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _suffixScrollStartPoint = e.GetPosition(SuffixScrollViewer);
            _suffixScrollStartOffset = SuffixScrollViewer.HorizontalOffset;
            _suffixIsScrolling = true;
            _suffixScrollStarted = false;
        }
        
        private void SuffixScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_suffixIsScrolling && _suffixScrollStartPoint.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var currentPoint = e.GetPosition(SuffixScrollViewer);
                double deltaX = _suffixScrollStartPoint.Value.X - currentPoint.X;
                
                if (!_suffixScrollStarted && Math.Abs(deltaX) > 15)
                {
                    _suffixScrollStarted = true;
                    Mouse.Capture(SuffixScrollViewer);
                }
                
                if (_suffixScrollStarted)
                {
                    SuffixScrollViewer.ScrollToHorizontalOffset(_suffixScrollStartOffset + deltaX);
                    e.Handled = true;
                }
            }
        }
        
        private void SuffixScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasScrolling = _suffixScrollStarted;
            _suffixIsScrolling = false;
            _suffixScrollStartPoint = null;
            _suffixScrollStarted = false;
            
            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true;
            }
        }
    }
    
    /// <summary>
    /// Converter to subtract a value from ActualWidth for constraining DockPanel/Grid width.
    /// </summary>
    public class SubtractConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is double actualWidth && parameter is double subtract)
            {
                double result = actualWidth - subtract;
                return result > 50 ? result : 50; // Minimum 50px
            }
            return 200.0; // Fallback
        }
        
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
