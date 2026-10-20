using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 C3 hold signals: H2 system suspend (<c>PowerModeChanged</c> and <c>WM_POWERBROADCAST</c>), H3 display off
    /// (<c>GUID_CONSOLE_DISPLAY_STATE</c>, the Modern Standby signal), H4 session lock, and, for each window passed to
    /// <see cref="TrackWindow"/>, H1 minimized (the window or its owner). It owns a hidden top-level window for the power
    /// messages, so it also works in an app with no visible window. Every signal reaches the callback on the dispatcher's
    /// thread. Create, attach and dispose it on that thread.
    /// </summary>
    public sealed class CameraHoldWatcher : IDisposable
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public uint DataLength;
            public byte Data;
        }

        private readonly Dispatcher _dispatcher;
        private readonly Action<CameraHold, bool> _onHold;
        private readonly Action<string> _log;
        private HwndSource? _source;
        private IntPtr _displayNotification;
        private Window? _window;
        private Window? _owner;
        private bool _attached;

        public CameraHoldWatcher(Dispatcher dispatcher, Action<CameraHold, bool> onHold, Action<string>? log = null)
        {
            _dispatcher = dispatcher;
            _onHold = onHold;
            _log = log ?? (_ => { });
        }

        /// <summary>Starts listening for suspend, display and lock signals.</summary>
        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            // A hidden top-level window: it receives the broadcast WM_POWERBROADCAST and the display-state notification.
            _source = new HwndSource(new HwndSourceParameters("RedCameraHoldWatcher")
            {
                Width = 0,
                Height = 0,
                WindowStyle = WS_POPUP,
            });
            _source.AddHook(WndProc);
            var display = ConsoleDisplayState;
            _displayNotification = RegisterPowerSettingNotification(_source.Handle, ref display, DEVICE_NOTIFY_WINDOW_HANDLE);
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }

        /// <summary>H1: the camera is held while this window or its owner is minimized.</summary>
        public void TrackWindow(Window window)
        {
            UntrackWindow();
            _window = window;
            _owner = window.Owner;
            _window.StateChanged += OnWindowStateChanged;
            if (_owner != null) _owner.StateChanged += OnWindowStateChanged;
            OnWindowStateChanged(this, EventArgs.Empty);
        }

        // The same handlers Windows calls; test and dev tools raise them to simulate sleep and wake (F-027 V2, A-027-16).
        public void RaisePowerMode(PowerModes mode) => OnPowerModeChanged(this, new PowerModeChangedEventArgs(mode));
        public void RaiseSessionSwitch(SessionSwitchReason reason) => OnSessionSwitch(this, new SessionSwitchEventArgs(reason));
        public void RaiseDisplayState(int state) => OnDisplayState(state);

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) Post(CameraHold.Suspended, true);
            else if (e.Mode == PowerModes.Resume) Post(CameraHold.Suspended, false);
        }

        private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect:
                    Post(CameraHold.Locked, true);
                    break;
                case SessionSwitchReason.SessionUnlock:
                case SessionSwitchReason.SessionLogon:
                    Post(CameraHold.Locked, false);
                    break;
            }
        }

        private void OnDisplayState(int state)
        {
            if (state == 0) Post(CameraHold.DisplayOff, true);
            else if (state == 1) Post(CameraHold.DisplayOff, false); // 2 = dimmed: keep the camera
        }

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            bool minimized = _window?.WindowState == WindowState.Minimized || _owner?.WindowState == WindowState.Minimized;
            Post(CameraHold.Minimized, minimized);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_POWERBROADCAST) return IntPtr.Zero;
            switch (wParam.ToInt32())
            {
                case PBT_APMSUSPEND:
                    Post(CameraHold.Suspended, true);
                    break;
                case PBT_APMRESUMESUSPEND:
                case PBT_APMRESUMEAUTOMATIC:
                    Post(CameraHold.Suspended, false);
                    break;
                case PBT_POWERSETTINGCHANGE when lParam != IntPtr.Zero:
                    var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == ConsoleDisplayState && setting.DataLength >= 1) OnDisplayState(setting.Data);
                    break;
            }
            return IntPtr.Zero;
        }

        /// <summary>The hidden window's handle, for tests that post real <c>WM_POWERBROADCAST</c> messages.</summary>
        public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

        private void Post(CameraHold hold, bool on)
        {
            void Raise()
            {
                try { _onHold(hold, on); }
                catch (Exception ex) { _log($"Camera hold handler failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            if (_dispatcher.CheckAccess()) Raise();
            else _dispatcher.BeginInvoke((Action)Raise);
        }

        private void UntrackWindow()
        {
            if (_window != null) _window.StateChanged -= OnWindowStateChanged;
            if (_owner != null) _owner.StateChanged -= OnWindowStateChanged;
            _window = null;
            _owner = null;
        }

        public void Dispose()
        {
            UntrackWindow();
            if (!_attached) return;
            _attached = false;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (_displayNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = IntPtr.Zero;
            if (_source != null)
            {
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }
    }
}
