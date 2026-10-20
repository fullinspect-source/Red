using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Owner 2026-10-19 (fix list p3): RED never asks the inspector to confirm a value it fills in. Instead every value
    /// field it changed flashes a red background 5 times and then shows its normal background again. A ComboBox is
    /// flashed with a red overlay (Windows' default ComboBox template ignores Background).
    /// </summary>
    public static class ValueFlash
    {
        public const int Times = 5;
        public static readonly TimeSpan Half = TimeSpan.FromMilliseconds(220); // red, then normal: 5 x 0.44 s
        public static readonly Color FlashColor = Color.FromRgb(239, 68, 68);

        private sealed class Run
        {
            public required DispatcherTimer Timer;
            public required DependencyProperty Property;
            public required object Original; // the element's own local value (or a binding, or UnsetValue)
        }

        private sealed class Overlay
        {
            public required DispatcherTimer Timer;
            public required AdornerLayer Layer;
            public required FlashAdorner Adorner;
            public bool Shown;
        }

        private sealed class FlashAdorner : Adorner
        {
            private static readonly Brush Fill = Frozen(new SolidColorBrush(FlashColor) { Opacity = 0.6 });
            public FlashAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;
            protected override void OnRender(DrawingContext dc) =>
                dc.DrawRoundedRectangle(Fill, null, new Rect(AdornedElement.RenderSize), 3, 3);
        }

        private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

        private static readonly ConditionalWeakTable<FrameworkElement, Run> Running = new();
        private static readonly ConditionalWeakTable<FrameworkElement, Overlay> Overlays = new();

        public static DependencyProperty? BackgroundPropertyOf(DependencyObject element) => element switch
        {
            Control => Control.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            Border => Border.BackgroundProperty,
            _ => null
        };

        /// <summary>
        /// Starts the flash on <paramref name="element"/>. A flash already running there restarts from the element's
        /// own background, so two quick changes never leave the field red. <paramref name="keepGoing"/>, checked at
        /// every step, ends the flash early (e.g. a shared value box now shows another item). <paramref name="onStep"/>
        /// (tests) sees each step: its index and whether the field is red. Returns false when nothing can be flashed.
        /// </summary>
        public static bool Start(FrameworkElement element, Action<int, bool>? onStep = null, Func<bool>? keepGoing = null)
        {
            if (element is ComboBox)
                return StartOverlay(element, onStep, keepGoing);
            var property = BackgroundPropertyOf(element);
            if (property == null)
                return false;

            object original;
            if (Running.TryGetValue(element, out var previous))
            {
                previous.Timer.Stop();
                original = previous.Original;
                Running.Remove(element);
            }
            else
            {
                original = element.ReadLocalValue(property);
            }

            var red = new SolidColorBrush(FlashColor);
            red.Freeze();
            var timer = new DispatcherTimer(DispatcherPriority.Normal, element.Dispatcher) { Interval = Half };
            var run = new Run { Timer = timer, Property = property, Original = original };
            Running.Add(element, run);

            int step = 0;
            object lastSet = DependencyProperty.UnsetValue; // what the flash itself put there last
            void Show()
            {
                bool isRed = step % 2 == 0;
                if (isRed)
                {
                    element.SetValue(property, red);
                    lastSet = red;
                }
                else
                {
                    lastSet = Restore(element, run);
                }
                onStep?.Invoke(step, isRed);
            }

            void Forget()
            {
                timer.Stop();
                if (Running.TryGetValue(element, out var current) && ReferenceEquals(current, run))
                    Running.Remove(element);
            }

            timer.Tick += (_, _) =>
            {
                // RED set this background itself meanwhile: the flash stops and leaves RED's value alone.
                if (!SameLocalValue(element.ReadLocalValue(property), lastSet))
                {
                    Forget();
                    return;
                }
                // The field no longer shows the changed item: its own background comes back at once.
                if (keepGoing?.Invoke() == false)
                {
                    Forget();
                    Restore(element, run);
                    return;
                }
                step++;
                if (step >= Times * 2)
                {
                    Forget();
                    Restore(element, run);
                    onStep?.Invoke(step, false);
                    return;
                }
                Show();
            };
            Show();
            timer.Start();
            return true;
        }

        private static bool StartOverlay(FrameworkElement element, Action<int, bool>? onStep, Func<bool>? keepGoing)
        {
            var layer = AdornerLayer.GetAdornerLayer(element);
            if (layer == null)
                return false;
            if (Overlays.TryGetValue(element, out var previous))
            {
                previous.Timer.Stop();
                if (previous.Shown) previous.Layer.Remove(previous.Adorner);
                Overlays.Remove(element);
            }

            var timer = new DispatcherTimer(DispatcherPriority.Normal, element.Dispatcher) { Interval = Half };
            var overlay = new Overlay { Timer = timer, Layer = layer, Adorner = new FlashAdorner(element) };
            Overlays.Add(element, overlay);

            int step = 0;
            void Show()
            {
                bool isRed = step % 2 == 0;
                if (isRed && !overlay.Shown) layer.Add(overlay.Adorner);
                if (!isRed && overlay.Shown) layer.Remove(overlay.Adorner);
                overlay.Shown = isRed;
                onStep?.Invoke(step, isRed);
            }

            void End()
            {
                timer.Stop();
                if (overlay.Shown) layer.Remove(overlay.Adorner);
                overlay.Shown = false;
                if (Overlays.TryGetValue(element, out var current) && ReferenceEquals(current, overlay))
                    Overlays.Remove(element);
            }

            timer.Tick += (_, _) =>
            {
                if (keepGoing?.Invoke() == false || !element.IsLoaded)
                {
                    End();
                    return;
                }
                step++;
                if (step >= Times * 2)
                {
                    End();
                    onStep?.Invoke(step, false);
                    return;
                }
                Show();
            };
            Show();
            timer.Start();
            return true;
        }

        public static bool IsRunning(FrameworkElement element) => Running.TryGetValue(element, out _) || Overlays.TryGetValue(element, out _);

        private static bool SameLocalValue(object current, object expected) =>
            ReferenceEquals(current, expected) ||
            current == DependencyProperty.UnsetValue && expected == DependencyProperty.UnsetValue;

        // Puts the element's own background back; returns the local value it now has.
        private static object Restore(FrameworkElement element, Run run)
        {
            if (run.Original == DependencyProperty.UnsetValue)
            {
                element.ClearValue(run.Property);
                return DependencyProperty.UnsetValue;
            }
            if (run.Original is BindingExpressionBase binding)
                return BindingOperations.SetBinding(element, run.Property, binding.ParentBindingBase);
            element.SetValue(run.Property, run.Original);
            return run.Original;
        }
    }
}
