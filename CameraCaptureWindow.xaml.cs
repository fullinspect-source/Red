using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InspectionEditor.Services;
using InspectionEditor.Services.Camera;
using Microsoft.Win32;
using RedCamera.Core;

namespace InspectionEditor
{
    /// <summary>A still from the capture screen, bound to the item that was loaded when the shutter was pressed
    /// (F-027 C7). The main window sets <see cref="Added"/> or <see cref="Note"/>.</summary>
    public sealed class CameraPhoto
    {
        public CameraPhoto(byte[] jpeg, object boundItem, string? cameraRollPath, int exifOrientation)
        {
            Jpeg = jpeg;
            BoundItem = boundItem;
            CameraRollPath = cameraRollPath;
            ExifOrientation = exifOrientation;
        }

        public byte[] Jpeg { get; }
        public object BoundItem { get; }
        public string? CameraRollPath { get; }
        public int ExifOrientation { get; }
        public bool Added { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>
    /// F-027 capture screen: holds the camera only while open and usable (C3), shows the live preview, an always-visible
    /// shutter on the right edge (C6), the "Restarting camera…" and failure texts (C5, C8), camera cycling (C11), the
    /// explicit device reset (C10), pinch/wheel zoom with its readout (Z4, Z5), tap to focus (T1) and the hands-free
    /// shutter keys (K1). The engine (session, holds, watchdog, preview, orientation) is the RedCamera.Core library;
    /// this window holds only RED's screen and the hand-off to the item.
    /// </summary>
    public partial class CameraCaptureWindow : Window
    {
        private readonly Func<(object? Item, string Label)> _target;
        private readonly CameraSessionController _controller;
        private readonly CameraHoldWatcher _holds;
        private readonly CameraPreviewRenderer _renderer;
        private readonly CameraOrientationProvider _orientation = new();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _noteTimer;
        private readonly ZoomGesture _pinch = new();
        private readonly ShutterKeyGate _shutterKeys = new();
        private readonly CameraSettingsService _cameraSettings = new();
        private DateTime _noticeShownAt = DateTime.MinValue;
        private bool _loadingToggle, _devUpright;
        private readonly HashSet<int> _touchIds = new();
        private double _pinchStartZoom = 1.0;
        private bool _multiTouch, _hasLastTap;
        private int _touchDownAt, _lastTapAt; // touch event timestamps (ms), not the time the UI thread got to them
        private Point _touchDownPoint, _lastTapPoint;
        private int _photoCount;
        private bool _devSuspended, _devDisplayOff, _devLocked;

        /// <summary>Raised on the UI thread for each still; the main window adds it to the bound item.</summary>
        public event Action<CameraPhoto>? PhotoTaken;

        /// <summary>The user chose "Use Windows Camera app" in the failure message (raised after the camera is released).</summary>
        public event Action? UseWindowsCameraAppRequested;

        public CameraCaptureWindow(Window owner, Func<(object? Item, string Label)> target)
        {
            InitializeComponent();
            Owner = owner;
            _target = target;
            _controller = new CameraSessionController(new WinRtCameraBackend(Log), _cameraSettings,
                () => DateTime.UtcNow, log: Log);
            _loadingToggle = true;
            LandscapeToggle.IsChecked = _controller.AlwaysLandscape = _cameraSettings.AlwaysLandscape; // L1: on by default
            _loadingToggle = false;
            ShowLandscapeToggle();
            _controller.StateChanged += OnControllerStateChanged;
            _controller.ZoomChanged += OnZoomChanged;
            // C11: the new camera's name, unless a note about a skipped camera (which names it) is still being read.
            _controller.SwitchedTo += camera => OnUi(() =>
            {
                if (DateTime.UtcNow - _noticeShownAt > TimeSpan.FromSeconds(3)) ShowNote(camera.Name, seconds: 2.5);
            });
            _controller.Notice += text => OnUi(() =>
            {
                _noticeShownAt = DateTime.UtcNow;
                ShowNote(text, seconds: 5);
            });
            PreviewArea.SizeChanged += (_, _) =>
            {
                PlaceSwitchButton();
                UpdateLandscapeGuide();
            };
            _renderer = new CameraPreviewRenderer(PreviewImage, Log);
            _controller.PreviewFrame += _renderer.Push;
            _holds = new CameraHoldWatcher(Dispatcher, (hold, on) => _controller.SetHold(hold, on), Log);
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += OnTimerTick;
            _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); NoteBorder.Visibility = Visibility.Collapsed; };
            DevHintText.Visibility = AppIdentity.IsDevBuild ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = "No photos yet";
            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += OnClosed;
        }

        // Bring the report back in front. Photos never activate the report (C7), so without this Windows activates the
        // next RED window in line, usually the list, and the report stays hidden behind it (owner, tablet test 2026-10-20).
        // Activating the owner while this window is still open is the reliable WPF way to hand the focus back.
        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var owner = Owner;
            if (owner == null || !owner.IsVisible) return;
            try
            {
                if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
                owner.Activate();
            }
            catch (InvalidOperationException)
            {
                // the report window is itself closing
            }
        }

        public CameraState State => _controller.State;

        private static void Log(string message) => DiagnosticLogService.Info("Camera: " + message);

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                PlaceOnRightHalf();
                RefreshTarget();
                _holds.Attach();
                _holds.TrackWindow(this); // H1: this window or RED's main window minimized
                _timer.Start();
                UpdateUi();
                UpdateZoomUi();
                _ = CameraRollCopy.PurgeOldInBackground(); // F-027 C9: backups older than 2 x Archive retention
                await _controller.OpenAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera screen could not start", ex);
                ShowNote("The camera could not start. Tap Try again.");
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _timer.Stop();
            _noteTimer.Stop();
            _holds.Dispose();
            _controller.PreviewFrame -= _renderer.Push;
            _ = _controller.CloseAsync(); // F-027 C12: release the camera
        }

        /// <summary>Pressing Camera again while the screen is open brings it forward (F-027 C2).</summary>
        public void BringToFront()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            RefreshTarget();
        }

        private void PlaceOnRightHalf()
        {
            Rect area = SystemParameters.WorkArea;
            if (Owner != null && PresentationSource.FromVisual(Owner) is HwndSource ownerSource)
            {
                var screen = System.Windows.Forms.Screen.FromHandle(ownerSource.Handle).WorkingArea;
                var fromDevice = ownerSource.CompositionTarget.TransformFromDevice;
                var topLeft = fromDevice.Transform(new Point(screen.Left, screen.Top));
                var bottomRight = fromDevice.Transform(new Point(screen.Right, screen.Bottom));
                area = new Rect(topLeft, bottomRight);
            }
            Width = Math.Max(MinWidth, area.Width / 2);
            Height = Math.Max(MinHeight, area.Height);
            Left = area.Left + area.Width - Width;
            Top = area.Top;
        }

        // ------------------------------------------------------------------ timer, state, preview

        private async void OnTimerTick(object? sender, EventArgs e)
        {
            try
            {
                RefreshTarget();
                UpdateOrientation();
                UpdateLandscapeGuide();
                await _controller.TickAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera tick failed", ex);
            }
        }

        private void OnControllerStateChanged() => OnUi(UpdateUi);

        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        }

        private void UpdateUi()
        {
            var state = _controller.State;
            bool live = state == CameraState.Live;
            bool waiting = state is CameraState.Starting or CameraState.Restarting or CameraState.Paused;
            StatusText.Text = _controller.StatusText;
            StatusOverlay.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
            PreviewImage.Opacity = live ? 1.0 : 0.35;
            ShutterButton.Opacity = live ? 1.0 : 0.55; // dimmed, never hidden or disabled (F-027 C6)

            bool gaveUp = state == CameraState.GaveUp;
            FailurePanel.Visibility = gaveUp ? Visibility.Visible : Visibility.Collapsed;
            if (gaveUp)
            {
                var kind = _controller.FailureKind;
                FailureTitleText.Text = CameraSessionController.FailureTitle(kind);
                FailureDetailText.Text = CameraSessionController.FailureDetail(kind);
                FailureDetailText.Visibility = string.IsNullOrEmpty(FailureDetailText.Text) ? Visibility.Collapsed : Visibility.Visible;
                PrivacySettingsButton.Visibility = kind == CameraFailureKind.AccessDenied ? Visibility.Visible : Visibility.Collapsed;
                ResetCameraButton.Visibility = kind == CameraFailureKind.NotResponding && CameraForReset() != null
                    ? Visibility.Visible : Visibility.Collapsed;
            }

            var camera = _controller.CurrentCamera;
            CameraNameText.Text = camera == null ? string.Empty
                : camera.Name + (_controller.IsShared ? " (shared with another app)" : string.Empty);
            SwitchCameraButton.Visibility = _controller.Cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            UpdateOrientation();
        }

        private void RefreshTarget()
        {
            var (item, label) = _target();
            TargetText.Text = item == null ? "Select an item in RED" : "Photo for: " + label;
        }

        // ------------------------------------------------------------------ orientation (F-027 O1-O3)

        private void UpdateOrientation()
        {
            var camera = _controller.CurrentCamera;
            if (camera == null) return;
            var (rotation, mirror) = _orientation.Preview(camera);
            if (_devUpright) rotation = (rotation + 90) % 360; // dev builds: as if the tablet were held upright
            PreviewRotation.Angle = rotation;
            PreviewMirror.ScaleX = mirror ? -1 : 1;
        }

        // ------------------------------------------------------------------ landscape photos (F-027 L1)

        private void LandscapeToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool on = LandscapeToggle.IsChecked == true;
            _controller.AlwaysLandscape = on;
            ShowLandscapeToggle();
            UpdateLandscapeGuide();
            if (_loadingToggle) return;
            _cameraSettings.SetAlwaysLandscape(on);
            Log($"landscape photos {(on ? "on" : "off")}.");
        }

        private void ShowLandscapeToggle() =>
            LandscapeToggle.Content = LandscapeToggle.IsChecked == true ? "Landscape: ON" : "Landscape: OFF";

        /// <summary>The EXIF orientation the next press uses (the orientation sensor, else the screen; O3).</summary>
        private int NextExifOrientation() => _devUpright ? 6 : _orientation.StillExifOrientation(_controller.CurrentCamera);

        /// <summary>
        /// L2: with landscape photos on, shade the parts of the preview the next photo will not keep and outline the band it
        /// keeps, worked out from that photo's own cut (its stored size and the orientation the next press uses), so it
        /// matches the saved photo even when the photo has another shape than the preview or the screen does not turn.
        /// </summary>
        private void UpdateLandscapeGuide()
        {
            Rect? kept = null;
            Rect shown = Rect.Empty;
            if (_controller.AlwaysLandscape && _controller.State == CameraState.Live && PreviewImage.Source != null
                && PreviewImage.RenderSize.Width > 0 && PreviewImage.IsDescendantOf(PreviewArea))
            {
                shown = PreviewImage.TransformToAncestor(PreviewArea).TransformBounds(new Rect(PreviewImage.RenderSize));
                var photo = _controller.PhotoSize;
                var frame = _controller.PreviewFrameSize;
                if (CameraLandscape.KeptOnScreen(photo.Width, photo.Height, NextExifOrientation(), frame.Width, frame.Height,
                        _controller.PreviewZoom, PreviewRotation.Angle, PreviewMirror.ScaleX < 0) is { } part)
                    kept = new Rect(shown.X + part.X * shown.Width, shown.Y + part.Y * shown.Height, part.Width * shown.Width,
                        part.Height * shown.Height);
            }
            if (kept is not { } k)
            {
                LandscapeGuide.Visibility = Visibility.Collapsed;
                return;
            }
            Place(GuideTop, new Rect(shown.X, shown.Y, shown.Width, k.Y - shown.Y));
            Place(GuideBottom, new Rect(shown.X, k.Bottom, shown.Width, shown.Bottom - k.Bottom));
            Place(GuideLeft, new Rect(shown.X, k.Y, k.X - shown.X, k.Height));
            Place(GuideRight, new Rect(k.Right, k.Y, shown.Right - k.Right, k.Height));
            Place(GuideFrame, k);
            LandscapeGuide.Visibility = Visibility.Visible;
        }

        private static void Place(FrameworkElement element, Rect at)
        {
            System.Windows.Controls.Canvas.SetLeft(element, at.X);
            System.Windows.Controls.Canvas.SetTop(element, at.Y);
            element.Width = Math.Max(0, at.Width);
            element.Height = Math.Max(0, at.Height);
        }

        // ------------------------------------------------------------------ zoom (F-027 Z1-Z5)

        private void OnZoomChanged()
        {
            if (Dispatcher.CheckAccess()) UpdateZoomUi();
            else Dispatcher.BeginInvoke((Action)UpdateZoomUi);
        }

        private void UpdateZoomUi()
        {
            _renderer.SetZoom(_controller.PreviewZoom);
            string readout = CameraZoom.Readout(_controller.ZoomFactor);
            ZoomReadoutButton.Content = readout;
            ZoomReadoutButton.Visibility = readout.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Z4: a two-finger pinch anywhere on the preview zooms smoothly; moving the fingers does nothing else.
        private void PreviewSurface_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
        {
            e.ManipulationContainer = PreviewSurface;
            e.Mode = ManipulationModes.Scale;
            _pinchStartZoom = _controller.ZoomFactor;
            _pinch.Begin(_pinchStartZoom);
            e.Handled = true;
        }

        private void PreviewSurface_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
        {
            e.Handled = true;
            if (e.IsInertial)
            {
                e.Complete(); // no coasting after the fingers lift
                return;
            }
            var scale = e.DeltaManipulation.Scale;
            _controller.SetZoom(_pinch.Update((scale.X + scale.Y) / 2, _controller.ZoomMax));
        }

        private void PreviewSurface_ManipulationCompleted(object? sender, ManipulationCompletedEventArgs e)
        {
            e.Handled = true;
            double zoom = ZoomGesture.End(_controller.ZoomFactor);
            if (zoom != _controller.ZoomFactor) _controller.SetZoom(zoom); // a pinch that ends near 1x is 1x
            if (_controller.ZoomFactor != _pinchStartZoom) Log($"zoom {_controller.ZoomFactor:0.0}x after a pinch.");
        }

        // Z4: the mouse wheel zooms; a touchpad pinch arrives as Ctrl+wheel and zooms the same way.
        private void PreviewSurface_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            _controller.SetZoom(CameraZoom.AfterWheel(_controller.ZoomFactor, e.Delta, _controller.ZoomMax));
        }

        // T1: a click (mouse or pen) focuses there; Z5: a double-click (mouse or pen) or a double-tap (touch) goes back to
        // 1x. Touch arrives through TouchUp; a click Windows makes from a touch is ignored here so a tap counts once.
        private void PreviewSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch) return;
            e.Handled = true;
            if (e.ClickCount == 2) _controller.ResetZoom();
            else if (e.ClickCount == 1) FocusAt(e.GetPosition(PreviewSurface));
        }

        private void PreviewSurface_TouchDown(object? sender, TouchEventArgs e)
        {
            _touchIds.RemoveWhere(id => !PreviewSurface.TouchesOver.Any(t => t.Id == id)); // fingers whose lift was missed
            _touchIds.Add(e.TouchDevice.Id);
            if (_touchIds.Count == 1)
            {
                _multiTouch = false;
                _touchDownAt = e.Timestamp;
                _touchDownPoint = e.GetTouchPoint(PreviewSurface).Position;
            }
            else
            {
                _multiTouch = true; // a pinch, never a tap
            }
        }

        private void PreviewSurface_TouchUp(object? sender, TouchEventArgs e)
        {
            _touchIds.Remove(e.TouchDevice.Id);
            if (_touchIds.Count > 0 || _multiTouch) return;
            int now = e.Timestamp;
            var point = e.GetTouchPoint(PreviewSurface).Position;
            bool tap = unchecked(now - _touchDownAt) is >= 0 and < 350 && (point - _touchDownPoint).Length < 24;
            if (!tap)
            {
                _hasLastTap = false;
                return;
            }
            if (_hasLastTap && unchecked(now - _lastTapAt) is >= 0 and < 450 && (point - _lastTapPoint).Length < 48)
            {
                _hasLastTap = false;
                _controller.ResetZoom();
                return;
            }
            _hasLastTap = true;
            _lastTapAt = now;
            _lastTapPoint = point;
            FocusAt(point); // T1: a single tap (never part of a pinch) focuses and exposes there
        }

        private void ZoomReadoutButton_Click(object sender, RoutedEventArgs e) => _controller.ResetZoom();

        /// <summary>C6/C11: Switch camera sits above the shutter (from the XAML) when the preview is tall enough for both
        /// (the shutter's half, a 16 px gap and the 56 px button above the centre); on a shorter preview it sits to the
        /// shutter's left, at the same height, so it is never squeezed or over the shutter.</summary>
        private void PlaceSwitchButton()
        {
            bool tall = PreviewArea.ActualHeight >= 2 * (44 + 16 + 56 + 8);
            SwitchCameraButton.Margin = tall ? new Thickness(0, 0, 32, 176) : new Thickness(0, 0, 120, 0);
            // On a short preview the zoom readout moves to the left so Switch never covers it.
            ZoomReadoutButton.HorizontalAlignment = tall ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            ZoomReadoutButton.Margin = tall ? new Thickness(0, 12, 0, 0) : new Thickness(12, 12, 0, 0);
        }

        // ------------------------------------------------------------------ tap to focus (F-027 T1-T3)

        /// <summary>A tap on the live picture: the camera focuses and exposes there for a while (when it can), and a mark
        /// shows briefly at the tap either way. Taps on the bars around the picture do nothing.</summary>
        private void FocusAt(Point onSurface)
        {
            if (_controller.State != CameraState.Live) return;
            if (!CameraFocus.TryMapTap(PreviewImage, PreviewSurface, onSurface, PreviewRotation.Angle, PreviewMirror.ScaleX < 0,
                    _controller.PreviewZoom, out double x, out double y))
                return;
            _controller.TapToFocus(x, y);
            ShowFocusMark(PreviewSurface.TranslatePoint(onSurface, FocusLayer));
        }

        private void ShowFocusMark(Point at)
        {
            System.Windows.Controls.Canvas.SetLeft(FocusMark, at.X - FocusMark.Width / 2);
            System.Windows.Controls.Canvas.SetTop(FocusMark, at.Y - FocusMark.Height / 2);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1500) };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(800))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
            FocusMark.BeginAnimation(OpacityProperty, fade);
        }

        // ------------------------------------------------------------------ shutter (F-027 C6, C7, C9)

        private async void ShutterButton_Click(object sender, RoutedEventArgs e) => await TakePhotoAsync();

        private async Task TakePhotoAsync()
        {
            try
            {
                var (item, _) = _target();
                if (item == null)
                {
                    ShowNote("Select an item in RED first.");
                    return;
                }
                int exif = NextExifOrientation();
                var outcome = await _controller.CaptureAsync(exif);
                if (outcome.Still == null)
                {
                    if (outcome.Message != null) ShowNote(outcome.Message);
                    return;
                }
                Flash();
                byte[] jpeg = outcome.Still.Jpeg;
                string? rollPath = await Task.Run(() => CameraRollCopy.TrySave(jpeg, DateTime.Now));
                var photo = new CameraPhoto(jpeg, item, rollPath, outcome.Still.ExifOrientation);
                PhotoTaken?.Invoke(photo);
                if (photo.Added)
                {
                    _photoCount++;
                    CountText.Text = _photoCount == 1 ? "1 photo taken" : $"{_photoCount} photos taken";
                    ShowThumbnail(photo);
                }
                else
                {
                    ShowNote(photo.Note ?? "The photo was not added.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera shutter failed", ex);
                ShowNote(CameraSessionController.CaptureFailedText);
            }
        }

        private void Flash()
        {
            var animation = new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(220));
            FlashOverlay.BeginAnimation(OpacityProperty, animation);
        }

        private void ShowThumbnail(CameraPhoto photo)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 144;
                image.StreamSource = new MemoryStream(photo.Jpeg);
                image.EndInit();
                image.Freeze();
                LastPhotoImage.Source = image;
                LastPhotoImage.LayoutTransform = new RotateTransform(photo.ExifOrientation switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 });
            }
            catch (Exception)
            {
                LastPhotoImage.Source = null;
            }
        }

        private void ShowNote(string text, double seconds = 4)
        {
            NoteText.Text = text;
            NoteBorder.Visibility = Visibility.Visible;
            _noteTimer.Stop();
            _noteTimer.Interval = TimeSpan.FromSeconds(seconds);
            _noteTimer.Start();
        }

        // ------------------------------------------------------------------ buttons and keys

        // K1 (owner 2026-10-22): Volume Up (a Bluetooth camera remote), Enter and Space take one photo per press while this
        // window is in front. Handling the key keeps Volume Up from changing the volume here.
        private static bool IsShutterKey(Key key) => key is Key.Space or Key.Enter or Key.VolumeUp;

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (AppIdentity.IsDevBuild && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && HandleDevKey(e.Key))
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }
            if (IsShutterKey(e.Key) && FailurePanel.Visibility != Visibility.Visible)
            {
                e.Handled = true;
                if (_shutterKeys.Press(e.IsRepeat, e.Timestamp)) _ = TakePhotoAsync();
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
        }

        private const int WmAppCommand = 0x0319, AppCommandVolumeUp = 10;

        /// <summary>K1: a remote that sends the Volume Up app command (not a key) takes a photo too.</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WmAppCommand) return IntPtr.Zero;
            int command = (int)((lParam.ToInt64() >> 16) & 0xFFFF) & ~0xF000;
            if (command != AppCommandVolumeUp || FailurePanel.Visibility == Visibility.Visible) return IntPtr.Zero;
            handled = true;
            if (_shutterKeys.Pulse(Environment.TickCount)) _ = TakePhotoAsync();
            return (IntPtr)1;
        }

        /// <summary>Dev builds only: raise the real hold handlers and stop the real frames (F-027 V2, A-027-16).</summary>
        private bool HandleDevKey(Key key)
        {
            switch (key)
            {
                case Key.F5:
                    _devSuspended = !_devSuspended;
                    _holds.RaisePowerMode(_devSuspended ? PowerModes.Suspend : PowerModes.Resume);
                    return true;
                case Key.F6:
                    _devDisplayOff = !_devDisplayOff;
                    _holds.RaiseDisplayState(_devDisplayOff ? 0 : 1);
                    return true;
                case Key.F7:
                    _devLocked = !_devLocked;
                    _holds.RaiseSessionSwitch(_devLocked ? SessionSwitchReason.SessionLock : SessionSwitchReason.SessionUnlock);
                    return true;
                case Key.F8:
                    Log("dev: stopping the preview frames (simulated stall).");
                    _ = _controller.DebugStallAsync();
                    return true;
                case Key.F9:
                    // L1 on a PC without an orientation sensor: as if the tablet were held upright (preview turned,
                    // stills tagged EXIF 6).
                    _devUpright = !_devUpright;
                    Log($"dev: held upright {(_devUpright ? "on" : "off")}.");
                    UpdateOrientation();
                    UpdateLandscapeGuide();
                    return true;
            }
            return false;
        }

        private void CloseCameraButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SwitchCameraButton_Click(object sender, RoutedEventArgs e) => _controller.SwitchCamera();

        private async void TryAgainButton_Click(object sender, RoutedEventArgs e)
        {
            try { await _controller.RetryAsync(); }
            catch (Exception ex) { DiagnosticLogService.Log("Camera try again failed", ex); }
        }

        private async void UseWindowsCameraButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Release first so the Windows Camera app can open the device (F-027 C8).
                await Task.WhenAny(_controller.CloseAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera release before the Windows Camera app failed", ex);
            }
            var handler = UseWindowsCameraAppRequested;
            Close();
            handler?.Invoke();
        }

        private void PrivacySettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true }); }
            catch (Exception ex) { DiagnosticLogService.Log("Camera privacy settings did not open", ex); }
        }

        private async void ResetCameraButton_Click(object sender, RoutedEventArgs e) => await RunResetAsync();

        private CameraInfo? CameraForReset() => _controller.CurrentCamera ?? _controller.Cameras.FirstOrDefault();

        /// <summary>F-027 C10: only from this button, only after the user confirms; Windows asks for administrator rights.</summary>
        private async Task RunResetAsync()
        {
            var camera = CameraForReset();
            if (camera == null) return;
            try
            {
                var flow = new CameraResetFlow(
                    WinRtCameraBackend.GetInstanceIdAsync,
                    text => MessageBox.Show(this, text, "Reset camera", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        MessageBoxResult.No) == MessageBoxResult.Yes,
                    new ElevatedCameraResetRunner(),
                    log: Log);
                ShowNote("Resetting the camera…");
                var (ran, message) = await flow.RunAsync(camera);
                if (message != null) ShowNote(message);
                if (ran) await _controller.RetryAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera reset failed", ex);
                ShowNote("The camera was not reset.");
            }
        }
    }
}
