// Owner tablet test of RED 2.3.3 (2026-10-20): the editor's Submit hint and Sign button (F-013, F-011) and touch
// scrolling in the print preview (F-022). Owner tablet test of 2.3.4: the editor toolbar and A+/A- (F-013), the End
// section's Status list (F-007), the signature pad inside a panning page (F-011), the My List tab groups (F-014). Reads
// the app's own XAML and source; no other files, no network.
//   dotnet run -p:Platform=x64
// Touch is simulated with a WPF TouchDevice (the same input path a real finger takes into WPF's manipulation system), in a
// window placed off-screen. A real finger on the tablet stays an owner check.
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor;
using InspectionEditor.Services;
using InspectionEditor.Services.Reports;
using Newtonsoft.Json.Linq;

int result = 1;
var ui = new Thread(() => result = Harness.Run());
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();
return result;

// A finger: positions are in the window's root coordinates.
sealed class FakeTouch : TouchDevice
{
    private Point _position;
    public FakeTouch(int id) : base(id) { }

    public void Down(PresentationSource source, Point at)
    {
        _position = at;
        SetActiveSource(source);
        Activate();
        ReportDown();
    }

    public void Move(Point to)
    {
        _position = to;
        ReportMove();
    }

    public void Up()
    {
        ReportUp();
        Deactivate();
    }

    public override TouchPoint GetTouchPoint(IInputElement? relativeTo)
    {
        Point point = _position;
        if (relativeTo is Visual target && ActiveSource?.RootVisual is Visual root && !ReferenceEquals(target, root) && target.IsDescendantOf(root))
            point = root.TransformToDescendant(target).Transform(_position);
        return new TouchPoint(this, point, new Rect(point, new Size(2, 2)), TouchAction.Move);
    }

    public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) => new();
}

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static StackPanel Pages()
    {
        var pages = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 6; i++)
            pages.Children.Add(new Border { Width = 500, Height = 1000, Margin = new Thickness(0, 12, 0, 0), Background = Brushes.White,
                Child = new Image { Width = 500, Height = 1000 } });
        return pages;
    }

    private static Window Host(ScrollViewer scroller) => new()
    {
        Content = scroller, Width = 600, Height = 500, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        WindowStyle = WindowStyle.None
    };

    private static int _fingerId = 100;

    // A swipe from 'from' to 'to' in 'steps' moves of 16 ms each, then lift; then wait (up to 4 s) until the page area has
    // finished moving (ManipulationCompleted, after any glide). Returns the number of gliding (inertial) moves seen.
    private static int Swipe(Window window, ScrollViewer scroller, Point from, Point to, int steps = 12)
    {
        bool completed = false;
        int inertial = 0;
        var onDelta = new EventHandler<ManipulationDeltaEventArgs>((_, e) => { if (e.IsInertial) inertial++; });
        var onCompleted = new EventHandler<ManipulationCompletedEventArgs>((_, _) => completed = true);
        scroller.AddHandler(UIElement.ManipulationDeltaEvent, onDelta, handledEventsToo: true);
        scroller.AddHandler(UIElement.ManipulationCompletedEvent, onCompleted, handledEventsToo: true);
        var finger = new FakeTouch(++_fingerId); // a new finger each time, as on the tablet
        finger.Down(PresentationSource.FromVisual(window)!, from);
        Pump(TimeSpan.FromMilliseconds(20));
        for (int i = 1; i <= steps; i++)
        {
            finger.Move(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            if (i < steps) Pump(TimeSpan.FromMilliseconds(16)); // lifted while still moving: a flick
        }
        finger.Up();
        var clock = Stopwatch.StartNew();
        while (!completed && clock.Elapsed < TimeSpan.FromSeconds(4))
            Pump(TimeSpan.FromMilliseconds(30));
        Pump(TimeSpan.FromMilliseconds(30));
        scroller.RemoveHandler(UIElement.ManipulationDeltaEvent, onDelta);
        scroller.RemoveHandler(UIElement.ManipulationCompletedEvent, onCompleted);
        return inertial;
    }

    public static int Run()
    {
        // ---------- A-013-46 Submit hint ----------
        {
            string myList = @"C:\Sbx\Inspections\MyList\1234567-CPP-1-PQE.ins", outbox = @"C:\Sbx\Inspections\Outbox\1234567-CPP-1-PQE.ins",
                   archive = @"C:\Sbx\Inspections\Archive\1234567-CPP-1-PQE.ins", review = @"C:\Sbx\Inspections\Review\1234567-CPP-1-PQE.ins";
            var ready = SubmitAvailability.For(true, true, myList, false);
            var fromOutbox = SubmitAvailability.For(true, true, outbox, false);
            var fromArchive = SubmitAvailability.For(true, true, archive, false);
            var notSaved = SubmitAvailability.For(true, false, null, false);
            var running = SubmitAvailability.For(true, true, myList, true);
            var fromReview = SubmitAvailability.For(true, true, review, false);
            var none = SubmitAvailability.For(false, false, null, false);
            Check("A-013-46", ready is { Visible: true, Enabled: true, Hint: "" }, "My List report: Submit enabled, no hint");
            Check("A-013-46", fromOutbox is { Visible: true, Enabled: false, Hint: "Submit: only from My List" } &&
                              fromArchive.Hint == "Submit: only from My List" && SubmitAvailability.OnlyFromMyList == "Submit: only from My List",
                "Outbox or Archive report: disabled, hint \"Submit: only from My List\"");
            Check("A-013-46", notSaved is { Visible: true, Enabled: false, Hint: "Submit: save first" }, "report with no file yet: hint \"Submit: save first\"");
            Check("A-013-46", running is { Enabled: false, Hint: "Submitting…" } && none is { Visible: true, Enabled: false, Hint: "" },
                "while a submit runs: \"Submitting…\"; no report open: disabled with no hint");
            Check("A-013-46", fromReview is { Visible: false, Hint: "" }, "Review report: no Submit at all (F-018)");
        }

        // ---------- A-011-14 Sign button ----------
        {
            var unsigned = SignButtonLook.For(signed: false, readOnly: false);
            var signed = SignButtonLook.For(signed: true, readOnly: false);
            var lockedUnsigned = SignButtonLook.For(signed: false, readOnly: true);
            Check("A-011-14", unsigned.Text == "✍ Sign" && unsigned.Foreground == "#B91C1C" && unsigned.Border == "#DC2626" &&
                              signed.Text == "✍ Signed" && signed.Foreground == "#166534" && lockedUnsigned.Text == "✍ Not signed",
                "\"✍ Sign\" in red until signed, \"✍ Signed\" in green, \"✍ Not signed\" (grey) on a read-only report");
        }

        // ---------- A-013-48 / A-013-49 Editor toolbar and A+/A- (owner 2026-10-20, tablet test of 2.3.4) ----------
        {
            string xaml = File.ReadAllText(Path.Combine("..", "..", "MainWindow.xaml"));
            string code = File.ReadAllText(Path.Combine("..", "..", "MainWindow.xaml.cs"));
            Check("A-013-48", !xaml.Contains("x:Name=\"OpenFileButton\"") && !xaml.Contains("Content=\"📂") && !code.Contains("InstallHistoryButton();") &&
                              !code.Contains("OpenFileButton_Click"),
                "no Open INS button and no History button in the editor toolbar (source check)");

            // ---------- A-014-13 My List tabs in groups (owner 2026-10-20) ----------
            string picker = File.ReadAllText(Path.Combine("..", "..", "InspectionPickerWindow.xaml"));
            int tabs = picker.IndexOf("<Button x:Name=\"MyListTabButton\"", StringComparison.Ordinal);
            var order = System.Text.RegularExpressions.Regex.Matches(picker.Substring(tabs), "x:Name=\"(\\w+)\"").Select(m => m.Groups[1].Value)
                .TakeWhile(n => n != "CloseAppButton").Append("CloseAppButton").ToList();
            var gaps = System.Text.RegularExpressions.Regex.Matches(picker, "<Border x:Name=\"(TabGap\\w+)\" Height=\"(\\d+)\"/>")
                .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value));
            Check("A-014-13", order.SequenceEqual(new[] { "MyListTabButton", "OutboxTabButton", "TabGapSmall1", "ArchiveTabButton", "ReviewTabButton", "TabGapLarge1",
                                  "OrdersTabButton", "BuyerWalkTabButton", "TabGapLarge2", "SettingsTabButton", "AboutTabButton", "TabGapSmall2", "CloseAppButton" }) &&
                              gaps.Count == 4 && gaps["TabGapSmall1"] < gaps["TabGapLarge1"] && gaps["TabGapSmall2"] < gaps["TabGapLarge2"],
                $"tabs: My List, Outbox | Archive, Review || ORDERS, BUYER WALK || Settings, About | Close App ({string.Join(", ", order)})");

            // The toolbar's own button style, rendered disabled over the toolbar's dark red, against WPF's default template.
            int start = xaml.IndexOf("<WrapPanel.Resources>", StringComparison.Ordinal) + "<WrapPanel.Resources>".Length;
            int end = xaml.IndexOf("</WrapPanel.Resources>", StringComparison.Ordinal);
            string styleXaml = xaml.Substring(start, end - start).Replace(" BasedOn=\"{StaticResource {x:Type Button}}\"", "");
            var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" + styleXaml + "</ResourceDictionary>");
            var toolbarStyle = (Style)resources[typeof(Button)];
            static double Luminance(Color c)
            {
                static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
                return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
            }
            static double Ratio(Color a, Color b) { double x = Luminance(a), y = Luminance(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
            double Contrast(Style? style, Brush? background, Brush? foreground)
            {
                var button = new Button { Content = "Apply Template", IsEnabled = false, FontSize = 13, Padding = new Thickness(12, 6, 12, 6), Width = 200, Height = 40 };
                if (style != null) button.Style = style;
                if (background != null) button.Background = background;
                if (foreground != null) button.Foreground = foreground;
                var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0x8B, 0, 0)), Padding = new Thickness(10), Child = button };
                host.Measure(new Size(220, 60));
                host.Arrange(new Rect(0, 0, 220, 60));
                host.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(220, 60, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var pixels = new byte[220 * 60 * 4];
                bitmap.CopyPixels(pixels, 220 * 4, 0);
                Color At(int x, int y) { int i = (y * 220 + x) * 4; return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]); }
                Color back = At(15, 30); // inside the button, left of the text (padding 12)
                double best = 1;
                for (int y = 18; y < 42; y++)
                    for (int x = 30; x < 190; x++)
                        best = Math.Max(best, Ratio(back, At(x, y)));
                return best;
            }
            var white = Brushes.White;
            double apply = Contrast(toolbarStyle, new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xAB)), white);
            double saveTemplate = Contrast(toolbarStyle, new SolidColorBrush(Color.FromRgb(0xA2, 0x3B, 0x72)), white);
            double save = Contrast(toolbarStyle, new SolidColorBrush(Color.FromRgb(0x22, 0x8B, 0x22)), white);
            double plain = Contrast(toolbarStyle, null, null);
            double before = Contrast(null, new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xAB)), white);
            Check("A-013-49", apply >= 4.5 && saveTemplate >= 4.5 && save >= 4.5 && plain >= 4.5 && before < apply,
                $"greyed-out toolbar buttons stay readable: Apply Template {apply:0.0}:1, Save Template {saveTemplate:0.0}:1, Save {save:0.0}:1, plain {plain:0.0}:1 (control, WPF's default template: {before:0.0}:1)");

            var scale = typeof(MainWindow).GetMethod("EditorScale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            double S(double size) => (double)scale.Invoke(null, new object[] { size })!;
            Check("A-013-50", S(16) == 1 && S(20) == 1.25 && S(22) == 1.375 && S(9) == 0.5625 &&
                              xaml.Contains("<Grid x:Name=\"EditorScaleRoot\">") &&
                              System.Text.RegularExpressions.Regex.Matches(code, @"_checklistFontSize [+-]= 1;\s+PopulateTreeView\(SearchFilterBox\.Text\);\s+ApplyEditorScale\(\);").Count == 2 &&
                              System.Text.RegularExpressions.Regex.IsMatch(code, @"LoadPreferences\(\);\s+ApplyEditorScale\(\);"),
                "A+/A- scale the right panel with the left list (16 = 100%, 22 = 137.5%, 9 = 56%), also at start from the saved size");
        }

        // ---------- A-022-29 / A-003-15 Press-and-drag scrolling and the Settings page (owner tablet test of 2.3.4, items 5 and 7) ----------
        {
            string preview = File.ReadAllText(Path.Combine("..", "..", "ReportPreviewWindow.cs"));
            string header = File.ReadAllText(Path.Combine("..", "..", "MainWindow.HeaderSections.cs"));
            string add = File.ReadAllText(Path.Combine("..", "..", "AddServiceWindow.cs"));
            string drag = File.ReadAllText(Path.Combine("..", "..", "DragScroll.cs"));
            Check("A-022-29", preview.Contains("DragScroll.Attach(scroller)") && header.Contains("DragScroll.Attach(scroller)") &&
                              add.Contains("DragScroll.AttachWhenLoaded(_jobs)") && add.Contains("DragScroll.AttachWhenLoaded(_formsList)") && add.Contains("!Dragged(_jobs)") &&
                              drag.Contains("SignatureSurface") && drag.Contains("TextBoxBase") && drag.Contains("ComboBox") && drag.Contains("ScrollBar") &&
                              DragScroll.Threshold == 10,
                "press-and-drag scrolls the print preview, the End window and the Add lists (a drag is never a row pick); the signature pad, text boxes, drop-downs and scroll bars keep their own input (source check)");
            var viewer = new ScrollViewer { Content = new Border { Height = 2000, Width = 200 } };
            DragScroll.Attach(viewer);
            DragScroll.Attach(viewer); // a second attach is ignored
            Check("A-022-29", !DragScroll.IsDragging(viewer), "a viewer is not dragging until a press moves 10 px");

            string picker = File.ReadAllText(Path.Combine("..", "..", "InspectionPickerWindow.xaml"));
            string house = File.ReadAllText(Path.Combine("..", "..", "InspectionPickerWindow.Housekeeping.cs"));
            int import = picker.IndexOf("x:Name=\"ProfileImportButton\"", StringComparison.Ordinal);
            int profile = picker.IndexOf("Text=\"Inspector Profile\"", StringComparison.Ordinal);
            Check("A-003-15", import > 0 && import < profile && !picker.Contains("FeeTokenPanel") && !picker.Contains("Text=\"iPad fee token") &&
                              !house.Contains("\"Check Archive cleanup") && !house.Contains("\"Check job folder purge") && !house.Contains("\"Show Review") &&
                              !house.Contains("MessageBox.Show") && house.Contains("InspectLiveSettings.WriteArchiveRetention("),
                "Settings: Import from INSPECT first; no iPad fee token, no Check buttons, no Show Review; Download Offline Data reports on its line; Archive retention editable (source check)");
        }

        // ---------- A-007-14 The End section's Status offers only the allowed results (owner 2026-10-20) ----------
        {
            var statuses = new List<StatusOption> { new(0, "Ordered"), new(2, "Passed"), new(3, "Failed"), new(5, "Correct & Proceed"), new(7, "Complete"), new(24, "For Engineer Review") };
            var listIds = statuses.Select(s => s.Id).ToList();
            var config = new InspectionTypeConfig { ShowPass = true, ShowCorrectAndProceed = true, ShowFailPO = true, ShowFailNext = true };
            string Names(IReadOnlyCollection<int> allowed) => string.Join("/", HeaderRules.OfferedStatuses(statuses, allowed).Select(s => s.Name));
            string clean = Names(ResultPickerWindow.AllowedStatusIds("cpr", false, config, false, listIds));
            string withFails = Names(ResultPickerWindow.AllowedStatusIds("CPR", true, config, false, listIds));
            string complete = Names(ResultPickerWindow.AllowedStatusIds("BWT", false, new InspectionTypeConfig { ShowComplete = true }, false, listIds));
            string review = Names(ResultPickerWindow.AllowedStatusIds("SCI", false, new InspectionTypeConfig { EngineerReview = true }, false, listIds));
            string nextDone = Names(ResultPickerWindow.AllowedStatusIds("CPR", true, new InspectionTypeConfig { ShowFailNext = true, ShowCorrectAndProceed = true }, true, listIds));
            Check("A-007-14", clean == "Passed" && withFails == "Failed/Correct & Proceed" && complete == "Complete" && review == "For Engineer Review" &&
                              nextDone == "Correct & Proceed" && Names(new List<int>()) == string.Join("/", statuses.Select(s => s.Name)),
                $"no fails: {clean}; fails: {withFails}; BWT: {complete}; SCI: {review}; next phase done: {nextDone} (the result picker's own rule, in the form's order)");
            string headerSource = File.ReadAllText(Path.Combine("..", "..", "MainWindow.HeaderSections.cs"));
            Check("A-007-14", headerSource.Contains("ResultPickerWindow.AllowedStatusIds(") && headerSource.Contains("HeaderRules.OfferedStatuses(") &&
                              headerSource.Contains("IsEnabled = false"),
                "the End section builds its Status list from that rule; a current status outside it shows greyed out (source check)");
        }

        // ---------- A-011-16 A stroke on the pad switches off panning around it until it ends (owner tablet test of 2.3.4) ----------
        {
            var surface = new SignatureSurface(770, 105);
            var inner = new ScrollViewer { Content = surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            var outer = new ScrollViewer { Content = new StackPanel { Children = { new Border { Height = 200 }, inner, new Border { Height = 900 } } }, PanningMode = PanningMode.VerticalFirst };
            var window = new Window { Content = outer, Width = 900, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            window.UpdateLayout();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(SignatureSurface).GetMethod("SuspendPanning", flags)!.Invoke(surface, null);
            var during = outer.PanningMode;
            typeof(SignatureSurface).GetMethod("ResumePanning", flags)!.Invoke(surface, null);
            var after = outer.PanningMode;
            bool gesturesOff = !Stylus.GetIsFlicksEnabled(surface) && !Stylus.GetIsPressAndHoldEnabled(surface) && !surface.IsManipulationEnabled;
            window.Close();
            Check("A-011-16", during == PanningMode.None && after == PanningMode.VerticalFirst && gesturesOff,
                $"while a stroke is down the page around the pad does not pan ({during}), afterwards it pans again ({after}); flicks and press-and-hold are off on the pad");
        }

        // ---------- A-022-26 to A-022-28 Print preview scrolls with a finger ----------
        {
            var real = new ReportPreviewWindow(new JObject { ["InspectionNumber"] = "1234567-CPP-1-PQE" }, new ReportSettings(), null);
            var pages = real.Pages;
            Check("A-022-26", pages.PanningMode == PanningMode.VerticalFirst && pages.PanningDeceleration > 0 &&
                              !Stylus.GetIsPressAndHoldEnabled(pages) && !Stylus.GetIsFlicksEnabled(pages),
                "the preview's page area pans by touch, with inertia; press-and-hold and flicks are off");
            real.Close();

            // Warm-up: the first manipulation in a process loads and compiles WPF's manipulation code, which delays the
            // first deltas by a few hundred milliseconds. One unmeasured swipe first.
            var warmScroller = ReportPreviewWindow.TouchScroller(Pages());
            var warm = Host(warmScroller);
            warm.Show();
            Pump(TimeSpan.FromMilliseconds(200));
            Swipe(warm, warmScroller, new Point(300, 420), new Point(300, 60));
            warm.Close();

            // The simulated finger is timed by dispatcher timers, which a busy PC can delay, and WPF then sometimes drops the
            // gesture after its first move. Each touch check gets up to three attempts; the control below must fail all three.
            const int Attempts = 3;
            var swipes = new List<(double Offset, int Glided)>();
            for (int attempt = 0; attempt < Attempts && !swipes.Any(s => s.Offset >= 250 && s.Glided > 0); attempt++)
            {
                var scroller = ReportPreviewWindow.TouchScroller(Pages());
                var window = Host(scroller);
                window.Show();
                Pump(TimeSpan.FromMilliseconds(200));
                int glided = Swipe(window, scroller, new Point(300, 420), new Point(300, 60));
                swipes.Add((scroller.VerticalOffset, glided));
                window.Close();
            }
            string tried = string.Join(", ", swipes.Select(s => $"{s.Offset:F0} px / {s.Glided} gliding moves"));
            Check("A-022-26", swipes.Any(s => s.Offset >= 250), $"an upward swipe of 360 px scrolls the pages ({tried})");
            Check("A-022-27", swipes.Any(s => s.Glided > 0), $"after the finger lifts the pages keep gliding (inertia; {tried})");

            // At the first page a swipe down has nowhere to go: the window must not bounce, and a swipe up afterwards still
            // scrolls (never stuck).
            {
                var scroller = ReportPreviewWindow.TouchScroller(Pages());
                var window = Host(scroller);
                int bounced = 0, bouncedUnhandled = 0;
                scroller.AddHandler(UIElement.ManipulationBoundaryFeedbackEvent, new EventHandler<ManipulationBoundaryFeedbackEventArgs>((_, e) =>
                {
                    bounced++;
                    if (!e.Handled) bouncedUnhandled++;
                }), handledEventsToo: true);
                window.Show();
                Pump(TimeSpan.FromMilliseconds(200));
                var shifted = new List<double>();
                window.LayoutUpdated += (_, _) => shifted.Add(scroller.TranslatePoint(new Point(0, 0), window).Y);
                for (int attempt = 0; attempt < Attempts && bounced == 0; attempt++)
                    Swipe(window, scroller, new Point(300, 60), new Point(300, 460));
                double top = scroller.VerticalOffset;
                bool windowStill = window.Left == -20000 && window.Top == -20000 && shifted.All(y => Math.Abs(y) < 0.5);
                var after = new List<double>();
                for (int attempt = 0; attempt < Attempts && !after.Any(o => o > 150); attempt++)
                {
                    scroller.ScrollToTop();
                    Pump(TimeSpan.FromMilliseconds(100));
                    Swipe(window, scroller, new Point(300, 420), new Point(300, 120));
                    after.Add(scroller.VerticalOffset);
                }
                Check("A-022-28", top < 1 && bounced > 0 && bouncedUnhandled == 0 && windowStill,
                    $"a swipe down at the first page: {bounced} boundary events, all handled; the window and page area do not move");
                Check("A-022-28", after.Any(o => o > 150), $"the next swipe up scrolls again ({string.Join(", ", after.Select(o => $"{o:F0} px"))})");
                window.Close();
            }

            // Control: the 2.3.3 page area (a plain ScrollViewer) does not move under the same finger, in any attempt.
            var control = new List<double>();
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                var plain = new ScrollViewer { Content = Pages(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
                var oldWindow = Host(plain);
                oldWindow.Show();
                Pump(TimeSpan.FromMilliseconds(200));
                Swipe(oldWindow, plain, new Point(300, 420), new Point(300, 60));
                control.Add(plain.VerticalOffset);
                oldWindow.Close();
            }
            Check("A-022-26", control.All(o => o < 1), $"control: RED 2.3.3's preview scroller ignores the same swipe ({string.Join(", ", control.Select(o => $"{o:F0} px"))}), as the owner saw");

            // ---------- A-011-15 A finger on the signature pad does not scroll the page around it ----------
            {
                var onPad = new List<double>();
                var beside = new List<double>();
                for (int attempt = 0; attempt < Attempts && !(onPad.Count > 0 && beside.Any(o => o > 150)); attempt++)
                {
                    var content = new StackPanel();
                    var pad = new SignatureSurface(InspectorSignatureStore.PadWidth, InspectorSignatureStore.PadHeight);
                    content.Children.Add(new Border { Height = 150, Background = Brushes.LightGray });
                    content.Children.Add(pad);
                    content.Children.Add(Pages());
                    // The inline checklist's own setting (MainWindow.xaml: PanningMode="VerticalOnly").
                    var page = new ScrollViewer { Content = content, PanningMode = PanningMode.VerticalOnly, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    var window = Host(page);
                    window.Width = 900;
                    window.Show();
                    Pump(TimeSpan.FromMilliseconds(200));
                    Swipe(window, page, new Point(300, 200), new Point(300, 160)); // a short stroke on the pad (y 150-255)
                    Swipe(window, page, new Point(300, 240), new Point(300, 170));
                    onPad.Add(page.VerticalOffset);
                    page.ScrollToTop();
                    Pump(TimeSpan.FromMilliseconds(100));
                    Swipe(window, page, new Point(300, 480), new Point(300, 280)); // below the pad
                    beside.Add(page.VerticalOffset);
                    window.Close();
                }
                Check("A-011-15", onPad.Count > 0 && onPad.All(o => o < 1) && beside.Any(o => o > 100),
                    $"strokes on the pad leave the page where it is ({string.Join(", ", onPad.Select(o => $"{o:F0} px"))}); a swipe below the pad still scrolls ({string.Join(", ", beside.Select(o => $"{o:F0} px"))})");

                // Control: a bare InkCanvas of the same size (RED 2.3.3's pad without the fix) lets the same stroke scroll the page.
                var bare = new List<double>();
                for (int attempt = 0; attempt < Attempts && !bare.Any(o => o > 20); attempt++)
                {
                    var content = new StackPanel();
                    content.Children.Add(new Border { Height = 150, Background = Brushes.LightGray });
                    content.Children.Add(new InkCanvas { Width = InspectorSignatureStore.PadWidth, Height = InspectorSignatureStore.PadHeight, Background = Brushes.White });
                    content.Children.Add(Pages());
                    var page = new ScrollViewer { Content = content, PanningMode = PanningMode.VerticalOnly, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    var window = Host(page);
                    window.Width = 900;
                    window.Show();
                    Pump(TimeSpan.FromMilliseconds(200));
                    Swipe(window, page, new Point(300, 240), new Point(300, 160));
                    bare.Add(page.VerticalOffset);
                    window.Close();
                }
                Check("A-011-15", bare.Any(o => o > 20), $"control: on a bare ink canvas the same stroke scrolls the page ({string.Join(", ", bare.Select(o => $"{o:F0} px"))})");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Tablet UI harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
