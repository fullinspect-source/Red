using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Diagnostics;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Camera service with continuous capture mode.
    /// Opens Windows Camera, watches for new photos, and fires an event for each one.
    /// Camera stays open until explicitly closed or timed out.
    /// </summary>
    public class CameraService
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const int SW_RESTORE = 9;
        private const int SW_MINIMIZE = 6;
        private const int SW_MAXIMIZE = 3;
        private const uint WM_SYSCOMMAND = 0x0112;
        private const uint SC_CLOSE = 0xF060;
        private const uint SC_MINIMIZE = 0xF020;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_SHOWWINDOW = 0x0040;
        
        private FileSystemWatcher? _watcher;
        private bool _isActive = false;
        private string? _lastProcessedFile;
        private DateTime _lastProcessedTime = DateTime.MinValue;
        
        // RED's own folder when nothing resolves (RED creates it, as before F-023).
        private static readonly string DefaultCameraRollPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Camera Roll");

        private string? _watchedPath;

        /// <summary>
        /// F-023 T3: INSPECT 2026's PathManager::GetCameraRollPath (DOC §3): candidate roots Pictures known folder,
        /// %OneDrive%\Pictures, %OneDriveConsumer%\Pictures, %OneDriveCommercial%\Pictures, %USERPROFILE%\Pictures
        /// (case-insensitively deduplicated); the most recently created "Camera Roll*" subfolder of the existing roots
        /// wins; else the first existing root; else null.
        /// </summary>
        public static string? ResolveCameraRoll(Func<string, string?>? environment = null, string? picturesFolder = null) =>
            RedCamera.Core.CameraFiles.ResolveCameraRoll(environment, picturesFolder); // F-027: one copy, shared with the camera engine

        /// <summary>
        /// Fired each time a new photo is captured. Byte array is the photo data.
        /// Always fired on the UI thread.
        /// </summary>
        public event Action<byte[]>? PhotoCaptured;

        /// <summary>
        /// Whether the camera session is currently active (watching for photos).
        /// </summary>
        public bool IsActive => _isActive;

        /// <summary>
        /// Opens the camera (or brings it to front if already open) and starts 
        /// watching for new photos. Each photo fires the PhotoCaptured event.
        /// Camera remains open and visible so additional photos can be taken continuously.
        /// Call StopSession() when done.
        /// </summary>
        public void OpenOrFocusCamera()
        {
            // Dismiss on-screen keyboard
            DismissTouchKeyboard();

            if (IsCameraRunning())
            {
                // Camera already open — bring it to front then reposition
                BringCameraToFront();
            }
            else
            {
                // Launch camera app then position it on the right side
                Process.Start(new ProcessStartInfo
                {
                    FileName = "microsoft.windows.camera:",
                    UseShellExecute = true
                });
                _ = PositionCameraRightAsync();
            }

            // Start the file watcher if not already active
            if (!_isActive)
            {
                StartWatching();
            }
        }

        /// <summary>
        /// Starts watching the Camera Roll folder for new photos.
        /// </summary>
        private void StartWatching()
        {
            if (_isActive) return;

            // F-023 T3: the resolved camera roll; RED's own Camera Roll folder (created) when nothing resolves.
            string path = ResolveCameraRoll() ?? DefaultCameraRollPath;
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            _watchedPath = path;
            CreateWatcher(path);
            _isActive = true;
        }

        private void CreateWatcher(string path)
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                Filter = "*.*",
                IncludeSubdirectories = true, // INSPECT 2026 watches subfolders
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFileCreated;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;
        }

        // INSPECT 2026: "Picture watcher error: {0}. Restarting." and the watcher is rebuilt.
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            DiagnosticLogService.Log("Picture watcher error. Restarting.", e.GetException());
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!_isActive) return;
                DisposeWatcher();
                try
                {
                    string path = ResolveCameraRoll() ?? DefaultCameraRollPath; // the roll may have moved (OneDrive unlinked)
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    _watchedPath = path;
                    CreateWatcher(path);
                }
                catch (Exception ex)
                {
                    DiagnosticLogService.Info($"Picture watcher could not restart: {ex.GetType().Name}");
                    _isActive = false; // the next Camera press starts a new watcher
                    MessageBox.Show("RED stopped watching the camera roll, so new photos will not be added. Press Camera again to restart it.",
                        "Camera", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        }

        private void DisposeWatcher()
        {
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileCreated;
            _watcher.Renamed -= OnFileRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }

        private async void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            await ProcessNewPhotoSafely("created", e.FullPath);
        }

        private async void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            await ProcessNewPhotoSafely("renamed", e.FullPath);
        }

        private async Task ProcessNewPhotoSafely(string eventType, string filePath)
        {
            try
            {
                await ProcessNewPhoto(filePath);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log($"Camera Roll {eventType} event failed for '{filePath}'", ex);
            }
        }

        private async Task ProcessNewPhoto(string filePath)
        {
            bool png = filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
            if (!filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) && !png)
            {
                return;
            }
            // INSPECT takes .jpg/.jpeg only; RED's .png extra applies inside a Camera Roll* folder, so a screenshot saved
            // under Pictures (watched when no Camera Roll folder exists) is never attached.
            if (png && (_watchedPath == null || !Path.GetFileName(_watchedPath.TrimEnd('\\')).StartsWith("Camera Roll", StringComparison.OrdinalIgnoreCase)))
                return;

            // Deduplicate: ignore if same file processed within last 2 seconds
            // (Windows Camera fires both Created and Renamed for the same photo)
            string fileName = Path.GetFileName(filePath);
            if (fileName == _lastProcessedFile && 
                (DateTime.Now - _lastProcessedTime).TotalSeconds < 2)
            {
                return;
            }
            _lastProcessedFile = fileName;
            _lastProcessedTime = DateTime.Now;

            // Wait for file to finish writing (500ms initial — camera apps need time to flush)
            await Task.Delay(500);

            byte[]? photoData = null;
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    photoData = await File.ReadAllBytesAsync(filePath);
                    
                    // Validate: must be at least 1KB (a real photo) and start with valid header
                    if (photoData.Length < 1024)
                    {
                        // Too small — file probably still being written
                        photoData = null;
                        await Task.Delay(300);
                        continue;
                    }
                    
                    // Check for valid JPEG (FF D8 FF) or PNG (89 50 4E 47) header
                    bool isValidJpeg = photoData.Length >= 3 && 
                        photoData[0] == 0xFF && photoData[1] == 0xD8 && photoData[2] == 0xFF;
                    bool isValidPng = photoData.Length >= 4 && 
                        photoData[0] == 0x89 && photoData[1] == 0x50 && 
                        photoData[2] == 0x4E && photoData[3] == 0x47;
                    
                    if (!isValidJpeg && !isValidPng)
                    {
                        // Invalid image header — file is corrupt or still being written
                        photoData = null;
                        await Task.Delay(300);
                        continue;
                    }
                    
                    // For JPEG, also verify it ends with FF D9 (end-of-image marker)
                    if (isValidJpeg)
                    {
                        bool hasEndMarker = photoData.Length >= 2 &&
                            photoData[photoData.Length - 2] == 0xFF && 
                            photoData[photoData.Length - 1] == 0xD9;
                        if (!hasEndMarker)
                        {
                            // JPEG not fully written yet — missing end marker
                            photoData = null;
                            await Task.Delay(300);
                            continue;
                        }
                    }
                    
                    break; // Valid photo data acquired
                }
                catch (IOException)
                {
                    photoData = null;
                    await Task.Delay(300);
                }
            }

            if (photoData != null)
            {
                // Add the photo on RED's UI thread, but leave Camera visible and active
                // so the inspector can take another photo without reopening it.
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PhotoCaptured?.Invoke(photoData);
                });
            }
            else
            {
                // Photo capture failed — notify user on UI thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        "⚠️ Photo was taken but could not be read from the camera roll.\n\n" +
                        "The photo is saved in your Camera Roll — you can add it manually using the 📎 button.\n\n" +
                        $"File: {fileName}",
                        "Photo Capture Issue",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
                
                BringRedToFront();
            }
        }

        /// <summary>
        /// Stops watching for photos and closes the camera app.
        /// </summary>
        public void StopSession()
        {
            _isActive = false;
            DisposeWatcher();

            CloseCameraApp();
        }

        /// <summary>
        /// Brings the Red main window to the foreground.
        /// Uses AttachThreadInput trick to bypass Windows focus-stealing prevention.
        /// </summary>
        public void BringRedToFront()
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = Application.Current.Windows
                        .OfType<InspectionEditor.MainWindow>()
                        .Where(w => w.IsVisible)
                        .Cast<Window>()
                        .LastOrDefault()
                        ?? Application.Current.MainWindow;
                    if (mainWindow == null) return;
                    
                    var helper = new WindowInteropHelper(mainWindow);
                    IntPtr hwnd = helper.Handle;
                    if (hwnd == IntPtr.Zero) return;

                    // Get the thread of the current foreground window (camera)
                    IntPtr foregroundHwnd = GetForegroundWindow();
                    uint foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
                    uint ourThread = GetCurrentThreadId();

                    // Temporarily attach our thread input to the foreground thread
                    // This tricks Windows into allowing SetForegroundWindow
                    bool attached = false;
                    if (foregroundThread != ourThread)
                    {
                        attached = AttachThreadInput(ourThread, foregroundThread, true);
                    }

                    try
                    {
                        if (mainWindow.WindowState == WindowState.Minimized)
                            ShowWindow(hwnd, SW_RESTORE);
                        
                        SetForegroundWindow(hwnd);
                        mainWindow.Activate();
                        mainWindow.Focus();
                    }
                    finally
                    {
                        if (attached)
                            AttachThreadInput(ourThread, foregroundThread, false);
                    }
                });
            }
            catch { }
        }

        private bool IsCameraRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName("WindowsCamera");
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                return running;
            }
            catch { return false; }
        }

        /// <summary>
        /// Minimizes the Windows Camera app.
        /// Finds it by process name rather than GetForegroundWindow(), which is unreliable
        /// because at the moment the file watcher fires, focus may not have fully switched
        /// to the camera yet (GetForegroundWindow could return RED's own handle).
        /// </summary>
        private void MinimizeCamera()
        {
            try
            {
                IntPtr cameraHwnd = FindCameraWindow();
                if (cameraHwnd != IntPtr.Zero)
                    ShowWindow(cameraHwnd, SW_MINIMIZE);
            }
            catch { }
        }

        private void BringCameraToFront()
        {
            try
            {
                // Re-launching the protocol URI restores/focuses the existing camera app.
                // This works reliably for UWP apps where MainWindowHandle isn't accessible.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "microsoft.windows.camera:",
                    UseShellExecute = true
                });
                _ = PositionCameraRightAsync();
            }
            catch { }
        }

        /// <summary>
        /// Polls for the camera window to appear, then moves it to the right half of the screen.
        /// UWP apps launch asynchronously so we retry until the window is found.
        /// </summary>
        private async Task PositionCameraRightAsync()
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(200);
                IntPtr hwnd = FindCameraWindow();
                if (hwnd != IntPtr.Zero)
                {
                    PositionWindowRight(hwnd);
                    return;
                }
            }
        }

        private IntPtr FindCameraWindow()
        {
            try
            {
                var procs = Process.GetProcessesByName("WindowsCamera");
                if (procs.Length == 0) return IntPtr.Zero;
                uint pid = (uint)procs[0].Id;
                foreach (var p in procs) p.Dispose();

                IntPtr found = IntPtr.Zero;
                EnumWindows((hWnd, _) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    GetWindowThreadProcessId(hWnd, out uint wPid);
                    if (wPid == pid) { found = hWnd; return false; }
                    return true;
                }, IntPtr.Zero);
                return found;
            }
            catch { return IntPtr.Zero; }
        }

        private void PositionWindowRight(IntPtr hwnd)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    double screenW = SystemParameters.PrimaryScreenWidth;
                    double screenH = SystemParameters.PrimaryScreenHeight;
                    int halfW = (int)(screenW / 2);
                    ShowWindow(hwnd, SW_RESTORE);
                    SetWindowPos(hwnd, IntPtr.Zero, halfW, 0, halfW, (int)screenH,
                        SWP_NOZORDER | SWP_SHOWWINDOW);
                });
            }
            catch { }
        }

        /// <summary>F-027 C2: RED's own capture screen dismisses the touch keyboard as the Camera app path does.</summary>
        public void DismissKeyboard() => DismissTouchKeyboard();

        private void DismissTouchKeyboard()
        {
            try
            {
                var touchKb = FindWindow("IPTIP_Main_Window", null);
                if (touchKb != IntPtr.Zero)
                    PostMessage(touchKb, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
                
                foreach (var proc in Process.GetProcessesByName("TabTip"))
                {
                    try { proc.CloseMainWindow(); proc.Dispose(); } catch { }
                }
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Input.Keyboard.ClearFocus();
                });
            }
            catch { }
        }

        private void CloseCameraApp()
        {
            try
            {
                var cameraProcesses = Process.GetProcessesByName("WindowsCamera");
                foreach (var proc in cameraProcesses)
                {
                    try
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(1000))
                            proc.Kill();
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            StopSession();
        }
    }
}
