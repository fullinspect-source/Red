// Owner fix list 2026-10-19 (p3): values RED changes for the inspector flash red 5 times, then show their normal
// background again (Services\ValueFlash.cs). No files, no network.
//   dotnet run -p:Platform=x64
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor.Services;

int result = 1;
var ui = new Thread(() => result = Harness.Run());
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();
return result;

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    // Runs the dispatcher until done() or the time limit.
    private static void Pump(Func<bool> done, TimeSpan max)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        poll.Tick += (_, _) => { if (done() || clock.Elapsed > max) { poll.Stop(); frame.Continue = false; } };
        poll.Start();
        Dispatcher.PushFrame(frame);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);

    public static int Run()
    {
        // ---------- FLASH-1 a value box flashes red 5 times and gets its own background back ----------
        {
            var own = new SolidColorBrush(Colors.White);
            var box = new TextBox { Background = own };
            var steps = new List<(int Step, bool Red, Color Seen)>();
            var clock = Stopwatch.StartNew();
            bool started = ValueFlash.Start(box, (step, red) => steps.Add((step, red, (box.Background as SolidColorBrush)?.Color ?? Colors.Transparent)));
            Pump(() => !ValueFlash.IsRunning(box), Limit);
            clock.Stop();
            int redPhases = steps.Count(s => s.Red);
            Check("FLASH-1", started && redPhases == ValueFlash.Times && ValueFlash.Times == 5 &&
                             steps.Where(s => s.Red).All(s => s.Seen == ValueFlash.FlashColor) &&
                             steps.Where(s => !s.Red).All(s => s.Seen == Colors.White) &&
                             steps.Select(s => s.Red).Take(10).SequenceEqual(Enumerable.Range(0, 10).Select(i => i % 2 == 0)),
                $"red, normal, red, normal ... {redPhases} red phases");
            Check("FLASH-2", ReferenceEquals(box.Background, own) && !ValueFlash.IsRunning(box) &&
                             clock.Elapsed >= TimeSpan.FromMilliseconds(ValueFlash.Half.TotalMilliseconds * 9) && clock.Elapsed < Limit,
                $"afterwards the box has its own background object again ({clock.Elapsed.TotalSeconds:F1} s)");
        }

        // ---------- FLASH-3 no local background: the style's background comes back ----------
        {
            var box = new TextBox();
            ValueFlash.Start(box);
            bool redAtStart = (box.Background as SolidColorBrush)?.Color == ValueFlash.FlashColor;
            Pump(() => !ValueFlash.IsRunning(box), Limit);
            Check("FLASH-3", redAtStart && box.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue,
                "a box without its own background is cleared again, not left red or white");
        }

        // ---------- FLASH-4 a bound background is bound again ----------
        {
            var source = new SourceHolder { Fill = new SolidColorBrush(Colors.LightYellow) };
            var box = new TextBox { DataContext = source };
            box.SetBinding(Control.BackgroundProperty, new Binding(nameof(SourceHolder.Fill)));
            int redSeen = 0;
            ValueFlash.Start(box, (_, red) => { if (red && (box.Background as SolidColorBrush)?.Color == ValueFlash.FlashColor) redSeen++; });
            Pump(() => !ValueFlash.IsRunning(box), Limit);
            source.Fill = new SolidColorBrush(Colors.LightBlue);
            Check("FLASH-4", redSeen == 5 && BindingOperations.IsDataBound(box, Control.BackgroundProperty) &&
                             (box.Background as SolidColorBrush)?.Color == Colors.LightBlue,
                "a bound background flashes 5 times and is bound again: a later source change still shows");
        }

        // ---------- FLASH-5 a second change during a flash restarts it and still ends on the own background ----------
        {
            var own = new SolidColorBrush(Colors.Honeydew);
            var box = new TextBox { Background = own };
            ValueFlash.Start(box);
            Pump(() => false, TimeSpan.FromMilliseconds(ValueFlash.Half.TotalMilliseconds * 0.5)); // inside the first red phase
            bool redAtRestart = (box.Background as SolidColorBrush)?.Color == ValueFlash.FlashColor;
            int secondRed = 0;
            ValueFlash.Start(box, (_, red) => { if (red) secondRed++; });
            Pump(() => !ValueFlash.IsRunning(box), Limit);
            Check("FLASH-5", redAtRestart && ReferenceEquals(box.Background, own) && secondRed == 5,
                "a second change while the field is red restarts the flash: 5 more times, then the field's own background, never stuck red");
        }

        // ---------- FLASH-6 rows and panels flash too; an element without a background is refused ----------
        {
            var row = new Border { Background = Brushes.WhiteSmoke };
            var panel = new DockPanel();
            bool rowStarted = ValueFlash.Start(row), panelStarted = ValueFlash.Start(panel);
            bool refused = !ValueFlash.Start(new TextBlock());
            Pump(() => !ValueFlash.IsRunning(row) && !ValueFlash.IsRunning(panel), Limit);
            Check("FLASH-6", rowStarted && panelStarted && refused && row.Background == Brushes.WhiteSmoke &&
                             panel.ReadLocalValue(Panel.BackgroundProperty) == DependencyProperty.UnsetValue,
                "a Border row and a tree-node panel flash and restore; a TextBlock is not a value field");
        }

        // ---------- FLASH-7 RED sets the background itself during a flash: the flash stops and keeps RED's value ----------
        {
            var box = new TextBox { Background = Brushes.White };
            ValueFlash.Start(box);
            Pump(() => false, TimeSpan.FromMilliseconds(ValueFlash.Half.TotalMilliseconds * 0.5));
            var redsOwn = new SolidColorBrush(Colors.Gainsboro); // e.g. the shared value box now shows a read-only item
            box.Background = redsOwn;
            Pump(() => !ValueFlash.IsRunning(box), Limit);
            Pump(() => false, TimeSpan.FromMilliseconds(ValueFlash.Half.TotalMilliseconds * 2));
            Check("FLASH-7", !ValueFlash.IsRunning(box) && ReferenceEquals(box.Background, redsOwn),
                "a background RED sets during the flash is kept: the flash stops instead of restoring an old one");
        }

        // ---------- FLASH-8 a combo box gets a red overlay (its template ignores Background) ----------
        {
            var combo = new ComboBox { IsEditable = true, Width = 160, Height = 30, Text = "U-Factor: 0.34" };
            var window = new Window { Content = new Grid { Children = { combo } }, Width = 300, Height = 120, ShowActivated = false, ShowInTaskbar = false, Left = -4000, Top = -4000 };
            window.Show();
            int overlaysSeen = 0, redSteps = 0;
            bool started = ValueFlash.Start(combo, (_, red) =>
            {
                if (!red) return;
                redSteps++;
                if (System.Windows.Documents.AdornerLayer.GetAdornerLayer(combo)?.GetAdorners(combo)?.Length > 0) overlaysSeen++;
            });
            Pump(() => !ValueFlash.IsRunning(combo), Limit);
            bool overlayGone = System.Windows.Documents.AdornerLayer.GetAdornerLayer(combo)?.GetAdorners(combo) == null;
            window.Close();
            Check("FLASH-8", started && redSteps == 5 && overlaysSeen == 5 && overlayGone,
                "a combo box flashes with a red overlay 5 times, and the overlay is gone afterwards");
        }

        // ---------- FLASH-9 the shared value box shows another item: the flash stops and the box is normal at once ----------
        {
            var own = new SolidColorBrush(Colors.White);
            var box = new TextBox { Background = own };
            bool sameItem = true;
            ValueFlash.Start(box, keepGoing: () => sameItem);
            Pump(() => false, TimeSpan.FromMilliseconds(ValueFlash.Half.TotalMilliseconds * 1.5)); // a normal phase
            sameItem = false; // the next item is loaded into the same box, with the same white brush
            Pump(() => !ValueFlash.IsRunning(box), Limit);
            Pump(() => false, TimeSpan.FromMilliseconds(ValueFlash.Half.TotalMilliseconds * 2));
            Check("FLASH-9", !ValueFlash.IsRunning(box) && ReferenceEquals(box.Background, own),
                "a value box that now shows another item stops flashing and is never turned red for that item");
        }

        Console.WriteLine();
        Console.WriteLine($"Value flash harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private sealed class SourceHolder : System.ComponentModel.INotifyPropertyChanged
    {
        private Brush? _fill;
        public Brush? Fill { get => _fill; set { _fill = value; PropertyChanged?.Invoke(this, new(nameof(Fill))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
