using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RedCamera.Core
{
    /// <summary>Windows' auto-rotation state: user32 <c>GetAutoRotationState</c>'s AR_STATE flags.</summary>
    [Flags]
    public enum AutoRotationState
    {
        Enabled = 0x0,
        Disabled = 0x1,       // rotation locked by the user
        Suppressed = 0x2,     // an app has suppressed it for now
        RemoteSession = 0x4,
        MultiMonitor = 0x8,
        NoSensor = 0x10,
        NotSupported = 0x20,
        Docked = 0x40,
        Laptop = 0x80
    }

    /// <summary>Whether the "Landscape only / Allow all orientations" switch is offered, and whether photos must be landscape.</summary>
    public readonly record struct OrientationSwitchDecision(bool ShowSwitch, bool ForceLandscape, string Reason);

    /// <summary>
    /// Owner rule 2026-10-23 for the orientation switch: "if the windows is rotation locked ... and the rotation locked is
    /// already in horizontal / landscape view, then the control for override should not be shown .. however if the device
    /// is not rotation locked then the control / override button should appear". A screen that cannot rotate at all (no
    /// sensor, a laptop or docked tablet, several monitors, a remote session) also hides it. A hidden switch means
    /// "Landscape only"; the stored choice is kept for when the switch is shown again.
    /// </summary>
    public static class CameraRotationLock
    {
        /// <summary>Any of these: the screen cannot rotate, so there is nothing to choose.</summary>
        public const AutoRotationState CannotRotate = AutoRotationState.RemoteSession | AutoRotationState.MultiMonitor |
            AutoRotationState.NoSensor | AutoRotationState.NotSupported | AutoRotationState.Docked | AutoRotationState.Laptop;

        /// <param name="state">Windows' state, or null when it could not be read (then the switch is shown, as before).</param>
        /// <param name="displayLandscape">The window's monitor is wider than tall; null when unknown.</param>
        public static OrientationSwitchDecision Decide(AutoRotationState? state, bool? displayLandscape)
        {
            if (state is not AutoRotationState s)
                return new(true, false, "rotation state unknown: switch shown");
            if ((s & CannotRotate) != 0)
                return new(false, true, $"the screen cannot rotate ({s}): switch hidden, landscape only");
            if ((s & AutoRotationState.Disabled) != 0)
                return displayLandscape switch
                {
                    true => new(false, true, "rotation locked in landscape: switch hidden, landscape only"),
                    false => new(true, false, "rotation locked in portrait: switch shown"),
                    null => new(true, false, "rotation locked, screen shape unknown: switch shown")
                };
            return new(true, false, "auto-rotate on: switch shown");
        }

        /// <summary>Windows' auto-rotation state now, or null when it cannot be read.</summary>
        public static AutoRotationState? ReadState()
        {
            try { return GetAutoRotationState(out int state) ? (AutoRotationState)state : null; }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
        }

        /// <summary>Whether the monitor the window is on (the primary one before it has a handle) is wider than tall.</summary>
        public static bool? IsDisplayLandscape(IntPtr hwnd)
        {
            try
            {
                IntPtr monitor = hwnd != IntPtr.Zero
                    ? MonitorFromWindow(hwnd, MonitorDefaultToNearest)
                    : MonitorFromPoint(new NativePoint(), MonitorDefaultToPrimary);
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                    return null;
                int width = info.Monitor.Right - info.Monitor.Left, height = info.Monitor.Bottom - info.Monitor.Top;
                return width <= 0 || height <= 0 ? null : width >= height;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return null; }
        }

        private const uint MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

        [DllImport("user32.dll")] private static extern bool GetAutoRotationState(out int state);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }

    /// <summary>
    /// Keeps <see cref="CameraRotationLock"/>'s decision current for a camera window: when the window is activated, on
    /// display changes (WM_DISPLAYCHANGE, WM_SETTINGCHANGE) and with a light poll (2 s), since switching rotation lock on or
    /// off sends no message. <see cref="Changed"/> is raised on the window's thread, only when the decision changes.
    /// </summary>
    public sealed class OrientationSwitchWatcher : IDisposable
    {
        public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(2);
        private const int WmDisplayChange = 0x007E, WmSettingChange = 0x001A;

        private readonly Func<AutoRotationState?> _readState;
        private readonly Func<bool?> _readLandscape;
        private Window? _window;
        private HwndSource? _source;
        private DispatcherTimer? _poll;

        /// <summary>A watcher on the given readers; <see cref="Refresh"/> re-reads them. Tests pass their own.</summary>
        public OrientationSwitchWatcher(Func<AutoRotationState?> readState, Func<bool?> readLandscape)
        {
            _readState = readState;
            _readLandscape = readLandscape;
            Current = Evaluate();
        }

        /// <summary>Windows' rotation lock and the window's own monitor, kept current while the window is open.</summary>
        public static OrientationSwitchWatcher ForWindow(Window window, TimeSpan? poll = null)
        {
            var watcher = new OrientationSwitchWatcher(CameraRotationLock.ReadState,
                () => CameraRotationLock.IsDisplayLandscape(new WindowInteropHelper(window).Handle));
            watcher.Attach(window, poll);
            return watcher;
        }

        public OrientationSwitchDecision Current { get; private set; }

        public event Action<OrientationSwitchDecision>? Changed;

        /// <summary>Starts watching; stops when the window closes. Attaching to the same window again does nothing.</summary>
        public void Attach(Window window, TimeSpan? poll = null)
        {
            if (ReferenceEquals(_window, window))
                return;
            if (_window != null)
                throw new InvalidOperationException("The watcher is already attached to another window.");
            _window = window;
            window.Activated += OnActivated;
            window.Closed += OnClosed;
            if (PresentationSource.FromVisual(window) is HwndSource source) Hook(source);
            else window.SourceInitialized += OnSourceInitialized;
            _poll = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = poll ?? DefaultPoll };
            _poll.Tick += (_, _) => Refresh();
            _poll.Start();
        }

        /// <summary>Reads Windows again; raises <see cref="Changed"/> and returns true when the decision changed.</summary>
        public bool Refresh()
        {
            var decision = Evaluate();
            if (decision == Current)
                return false;
            Current = decision;
            Changed?.Invoke(decision);
            return true;
        }

        private OrientationSwitchDecision Evaluate()
        {
            AutoRotationState? state;
            bool? landscape;
            try { state = _readState(); } catch (Exception) { state = null; }
            try { landscape = _readLandscape(); } catch (Exception) { landscape = null; }
            return CameraRotationLock.Decide(state, landscape);
        }

        private void OnActivated(object? sender, EventArgs e) => Refresh();

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (_window != null && PresentationSource.FromVisual(_window) is HwndSource source) Hook(source);
        }

        private void Hook(HwndSource source)
        {
            _source = source;
            source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg is WmDisplayChange or WmSettingChange)
                _window?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Refresh()));
            return IntPtr.Zero;
        }

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            _poll?.Stop();
            _poll = null;
            _source?.RemoveHook(WndProc);
            _source = null;
            if (_window != null)
            {
                _window.Activated -= OnActivated;
                _window.Closed -= OnClosed;
                _window.SourceInitialized -= OnSourceInitialized;
                _window = null;
            }
        }
    }
}
