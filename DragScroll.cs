using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace InspectionEditor
{
    // Owner tablet test of 2.3.4: on the owner's tablets a finger reaches RED as mouse input, so a swipe never scrolls a
    // ScrollViewer ("these tablets have to use the mouse click down event instead of the swipe event to scroll"). Press and
    // drag scrolls it: past 10 px of movement the drag takes the mouse and moves the content with the finger; a press
    // without movement stays a normal tap. Text boxes, drop-downs, scroll bars, sliders and ink surfaces keep their own
    // dragging. Real touch that WPF already pans (PanningMode) is left to the panning.
    public static class DragScroll
    {
        public const double Threshold = 10;

        private sealed class State
        {
            public Point? Start;
            public double StartVertical, StartHorizontal;
            public bool Dragging;
        }

        private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

        // True while a press-and-drag is scrolling this viewer (callers that act on a mouse-up can skip it).
        public static bool IsDragging(ScrollViewer viewer) => States.TryGetValue(viewer, out var state) && state.Dragging;

        public static void Attach(ScrollViewer viewer)
        {
            if (States.TryGetValue(viewer, out _)) return;
            var state = new State();
            States.Add(viewer, state);
            viewer.PreviewMouseLeftButtonDown += (_, e) =>
            {
                state.Start = null;
                state.Dragging = false;
                if (Excluded(e.OriginalSource as DependencyObject, viewer)) return;
                if (e.StylusDevice != null && viewer.PanningMode != PanningMode.None) return; // real touch: panning handles it
                state.Start = e.GetPosition(viewer);
                state.StartVertical = viewer.VerticalOffset;
                state.StartHorizontal = viewer.HorizontalOffset;
            };
            viewer.PreviewMouseMove += (_, e) =>
            {
                if (state.Start is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(viewer);
                double dx = start.X - now.X, dy = start.Y - now.Y;
                if (!state.Dragging)
                {
                    if (Math.Abs(dx) < Threshold && Math.Abs(dy) < Threshold) return;
                    state.Dragging = true;
                    viewer.CaptureMouse();
                }
                viewer.ScrollToVerticalOffset(state.StartVertical + dy);
                viewer.ScrollToHorizontalOffset(state.StartHorizontal + dx);
                e.Handled = true;
            };
            viewer.PreviewMouseLeftButtonUp += (_, e) =>
            {
                bool dragged = state.Dragging;
                state.Start = null;
                state.Dragging = false;
                if (!dragged) return;
                e.Handled = true; // a drag is never a tap on what is under the finger
                viewer.ReleaseMouseCapture();
            };
            viewer.LostMouseCapture += (_, _) =>
            {
                if (!state.Dragging) return;
                state.Start = null;
                state.Dragging = false;
            };
        }

        // A ScrollViewer inside a list (ListView, ListBox) after its template is applied.
        public static void AttachWhenLoaded(ItemsControl list)
        {
            void Hook()
            {
                if (FindDescendant<ScrollViewer>(list) is ScrollViewer viewer) Attach(viewer);
            }
            if (list.IsLoaded) Hook();
            list.Loaded += (_, _) => Hook();
        }

        public static ScrollViewer? ViewerOf(ItemsControl list) => FindDescendant<ScrollViewer>(list);

        private static bool Excluded(DependencyObject? source, ScrollViewer viewer)
        {
            for (var node = source; node != null && !ReferenceEquals(node, viewer); node = Parent(node))
                if (node is TextBoxBase or PasswordBox or ComboBox or ScrollBar or Slider or InkCanvas or SignatureSurface)
                    return true;
            return false;
        }

        private static DependencyObject? Parent(DependencyObject node) =>
            node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is T deeper) return deeper;
            }
            return null;
        }
    }
}
