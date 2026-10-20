using System;
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
    /// shutter (C6), the "Restarting camera…" and failure texts (C5, C8), camera switching (C11) and the explicit
    /// device reset (C10). The engine (session, holds, watchdog, preview, orientation) is the RedCamera.Core library;
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
            _controller = new CameraSessionController(new WinRtCameraBackend(Log), new CameraSettingsService(),
                () => DateTime.UtcNow, log: Log);
            _controller.StateChanged += OnControllerStateChanged;
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
            Closed += OnClosed;
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
                await _controller.TickAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Camera tick failed", ex);
            }
        }

        private void OnControllerStateChanged()
        {
            if (Dispatcher.CheckAccess()) UpdateUi();
            else Dispatcher.BeginInvoke((Action)UpdateUi);
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
            PreviewRotation.Angle = rotation;
            PreviewMirror.ScaleX = mirror ? -1 : 1;
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
                int exif = _orientation.StillExifOrientation(_controller.CurrentCamera);
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

        private void ShowNote(string text)
        {
            NoteText.Text = text;
            NoteBorder.Visibility = Visibility.Visible;
            _noteTimer.Stop();
            _noteTimer.Start();
        }

        // ------------------------------------------------------------------ buttons and keys

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
            if ((e.Key == Key.Space || e.Key == Key.Enter) && FailurePanel.Visibility != Visibility.Visible)
            {
                e.Handled = true;
                _ = TakePhotoAsync();
            }
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
