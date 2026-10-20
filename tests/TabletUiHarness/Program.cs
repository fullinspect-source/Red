// Owner tablet test of RED 2.3.3 (2026-10-20): the editor's Submit hint and Sign button (F-013, F-011) and touch
// scrolling in the print preview (F-022). No files, no network.
//   dotnet run -p:Platform=x64
// Touch is simulated with a WPF TouchDevice (the same input path a real finger takes into WPF's manipulation system), in a
// window placed off-screen. A real finger on the tablet stays an owner check.
using System.Diagnostics;
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
