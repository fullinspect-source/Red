// F-027 A-027-16, A-027-28 (zoom) and the capture half of A-027-15: drives the RedCamera.Core capture-session controller
// against this PC's camera. It never disables a device and never writes outside the folder given as the first argument (default: a temp
// folder). Exit 0 = all checks passed, 1 = a check failed, 3 = no camera on this PC (skipped).
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RedCamera.Core;
using Microsoft.Win32;

int exitCode = 1;
string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "red-camera-live-check");
Directory.CreateDirectory(outDir);
var thread = new Thread(() =>
{
    var dispatcher = Dispatcher.CurrentDispatcher;
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
    dispatcher.BeginInvoke(async () =>
    {
        try { exitCode = await LiveCheck.RunAsync(outDir); }
        catch (Exception ex) { Console.WriteLine("FAIL harness " + ex); exitCode = 1; }
        finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
    });
    Dispatcher.Run();
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return exitCode;

static class LiveCheck
{
    private static int _passed, _failed;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<string> Log = new();

    private static void Check(string id, bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {what}");
        if (ok) _passed++; else _failed++;
    }

    private sealed class MemoryPreferences : ICameraPreferences
    {
        public string? DeviceId { get; set; }
        public CameraPanelKind? Panel { get; set; }
        public void Remember(CameraInfo camera) { DeviceId = camera.Id; Panel = camera.Panel; }
    }

    public static async Task<int> RunAsync(string outDir)
    {
        void Write(string line)
        {
            lock (Log) Log.Add($"{Clock.Elapsed.TotalSeconds,7:0.00} {line}");
        }

        var backend = new WinRtCameraBackend(Write);
        var cameras = await backend.FindCamerasAsync();
        Console.WriteLine($"Cameras: {cameras.Count}");
        foreach (var c in cameras) Console.WriteLine($"  {c.Name} panel={c.Panel} mount={c.MountRotationClockwise}");
        if (cameras.Count == 0) { Console.WriteLine("SKIP no camera"); return 3; }

        // A 2 s tap-focus hold (instead of 10 s) keeps L18 short; every other timing is the real one.
        var controller = new CameraSessionController(backend, new MemoryPreferences(), () => DateTime.UtcNow,
            new CameraTimings { FocusHold = TimeSpan.FromSeconds(2) }, log: Write);
        int frames = 0;
        controller.PreviewFrame += _ => Interlocked.Increment(ref frames);
        controller.PreviewFrame += frame =>
        {
            var wanted = Volatile.Read(ref _frameWanted);
            if (wanted != null && !frame.AllBlack)
                wanted.TrySetResult(new CameraPreviewFrame(frame.Width, frame.Height, frame.Stride, (byte[])frame.Pixels.Clone(), false));
        };
        var states = new List<(double T, CameraState S)>();
        controller.StateChanged += () => states.Add((Clock.Elapsed.TotalSeconds, controller.State));

        async Task<bool> TickUntil(Func<bool> condition, double seconds)
        {
            var until = Clock.Elapsed + TimeSpan.FromSeconds(seconds);
            while (Clock.Elapsed < until)
            {
                await controller.TickAsync();
                if (condition()) return true;
                await Task.Delay(100);
            }
            return condition();
        }

        // Open and go live.
        double t0 = Clock.Elapsed.TotalSeconds;
        await controller.OpenAsync();
        bool live = await TickUntil(() => controller.State == CameraState.Live, 15);
        Check("L1", live, $"camera live after open ({Clock.Elapsed.TotalSeconds - t0:0.0} s, shared={controller.IsShared})");
        if (!live) return Finish(outDir);
        bool inUse = WebcamInUse();
        Check("L1b", inUse, "Windows reports the camera in use by this process while live");

        frames = 0;
        await TickUntil(() => false, 3);
        Check("L2", frames >= 15, $"preview frames flowing ({frames / 3.0:0} per s)");

        // A-027-15 capture half: a full-size still with EXIF orientation 1.
        var shot = await controller.CaptureAsync(1);
        bool tookStill = shot.Still != null && shot.Still.Jpeg.Length > 1024 && shot.Still.Jpeg[0] == 0xFF && shot.Still.Jpeg[1] == 0xD8;
        // The still shows the room, so it is checked in memory and never written to disk.
        Check("L3", tookStill, $"still captured: {shot.Still?.Width}x{shot.Still?.Height} via {shot.Still?.Source}, {shot.Still?.Jpeg.Length} bytes");

        // A-027-16 watchdog: stop the real frames underneath the controller.
        double tStall = Clock.Elapsed.TotalSeconds;
        await controller.DebugStallAsync();
        bool restarting = await TickUntil(() => controller.State == CameraState.Restarting, 5);
        double tRestart = Clock.Elapsed.TotalSeconds - tStall;
        Check("L4", restarting && tRestart <= 2.6, $"stall detected, \"{CameraSessionController.RestartingText}\" after {tRestart:0.00} s");
        bool back = await TickUntil(() => controller.State == CameraState.Live, 15);
        Check("L5", back, $"live again with a new session {Clock.Elapsed.TotalSeconds - tStall:0.0} s after the stall");

        // A-027-16 suspend (H2) then resume.
        controller.SetHold(CameraHold.Suspended, true);
        bool paused = controller.State == CameraState.Paused;
        await TickUntil(() => false, 2.5);
        Check("L6", paused && !WebcamInUse(), "suspend: paused and Windows reports the camera released");
        double tResume = Clock.Elapsed.TotalSeconds;
        controller.SetHold(CameraHold.Suspended, false);
        bool stillPaused = await TickUntil(() => controller.State != CameraState.Paused, 1.3) == false;
        bool resumed = await TickUntil(() => controller.State == CameraState.Live, 15);
        Check("L7", stillPaused && resumed, $"resume: paused through the settle delay, live {Clock.Elapsed.TotalSeconds - tResume:0.0} s after resume");

        // Modern Standby order: display off, lock, display on (still locked), unlock.
        controller.SetHold(CameraHold.DisplayOff, true);
        controller.SetHold(CameraHold.Locked, true);
        await TickUntil(() => false, 2.5);
        bool released = !WebcamInUse() && controller.State == CameraState.Paused;
        controller.SetHold(CameraHold.DisplayOff, false);
        bool waitsForUnlock = await TickUntil(() => controller.State != CameraState.Paused, 2.5) == false;
        controller.SetHold(CameraHold.Locked, false);
        bool afterUnlock = await TickUntil(() => controller.State == CameraState.Live, 15);
        Check("L8", released && waitsForUnlock && afterUnlock, "display off + lock: released, stays paused until unlock, then live");

        // Minimize (H1).
        controller.SetHold(CameraHold.Minimized, true);
        await TickUntil(() => false, 2.5);
        bool minReleased = !WebcamInUse();
        controller.SetHold(CameraHold.Minimized, false);
        bool restored = await TickUntil(() => controller.State == CameraState.Live, 15);
        Check("L9", minReleased && restored, "minimize releases the camera; restore brings a new live session");

        var second = await controller.CaptureAsync(1);
        Check("L10", second.Still != null, "a still after all the cycles");

        // F-027 Z1-Z3 on the real camera: what it zooms with, stills at 2x and 4x showing the preview's region, the zoom
        // kept through a rebuilt session, and 1x again when the screen opens. Stills and frames stay in memory.
        Check("L12", controller.IsHardwareZoom ? controller.ZoomMax > 1 : controller.ZoomMax == CameraZoom.DigitalMax,
            controller.IsHardwareZoom ? $"zoom: the camera's own, up to {controller.ZoomMax:0.##}x" : $"zoom: digital (this camera has none of its own), up to {controller.ZoomMax:0}x");
        int fullW = shot.Still?.Width ?? 0, fullH = shot.Still?.Height ?? 0;

        async Task<(bool Ok, string Info)> ZoomShot(double zoom)
        {
            controller.SetZoom(zoom);
            zoom = controller.ZoomFactor; // a camera's own zoom may stop below the asked factor
            await TickUntil(() => false, 0.6); // a camera's own zoom needs a moment
            var frame = await NextFrameAsync();
            var taken = await controller.CaptureAsync(1);
            if (taken.Still == null || frame.Pixels == null) return (false, "no still or no preview frame");
            var expected = controller.IsHardwareZoom ? new ZoomCrop(0, 0, fullW, fullH) : CameraZoom.CropFor(fullW, fullH, zoom);
            bool sized = taken.Still.Width == expected.Width && taken.Still.Height == expected.Height && taken.Still.Zoom == zoom;
            var region = controller.IsHardwareZoom ? new ZoomCrop(0, 0, frame.Width, frame.Height) : CameraZoom.CropFor(frame.Width, frame.Height, zoom);
            var frameSource = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Stride);
            var still = BitmapDecoder.Create(new MemoryStream(taken.Still.Jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            // A photo of another shape than the preview (4:3 against 16:9) is compared on its centred part of the
            // preview's shape, and every picture is scaled to the same size.
            double aspect = (double)region.Width / region.Height;
            int w = 160, h = Math.Max(1, (int)Math.Round(160 / aspect));
            byte[] stillPx = Small(CenterToAspect(still, aspect), w, h);
            double match = MeanDifference(stillPx, Small(new CroppedBitmap(frameSource, new Int32Rect(region.X, region.Y, region.Width, region.Height)), w, h));
            double whole = MeanDifference(stillPx, Small(frameSource, w, h));
            bool same = match < 25 && match <= whole * 0.7;
            return (sized && same, $"{zoom}x: still {taken.Still.Width}x{taken.Still.Height} (expected {expected.Width}x{expected.Height}), "
                + $"difference to the preview's region {match:0.0} vs the whole preview {whole:0.0}");
        }

        var (z2, z2Info) = await ZoomShot(2);
        Check("L13", z2, "2x still shows the preview's region: " + z2Info);
        var (z4, z4Info) = await ZoomShot(4);
        Check("L14", z4, "4x still shows the preview's region: " + z4Info);

        controller.SetZoom(2);
        await controller.DebugStallAsync();
        await TickUntil(() => controller.State == CameraState.Restarting, 5);
        bool rebuilt = await TickUntil(() => controller.State == CameraState.Live, 15);
        bool kept = controller.ZoomFactor == 2;
        var (z2Again, z2AgainInfo) = await ZoomShot(2);
        Check("L15", rebuilt && kept && z2Again, "after a rebuilt session the zoom is still 2x and applies to the next still: " + z2AgainInfo);

        // F-027 C11: every camera Windows lists, once each (no infrared or depth sensor).
        var ids = cameras.Select(c => c.Id).ToList();
        Check("L17", ids.Count == ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() && cameras.Count >= 1,
            $"camera list: {string.Join(", ", cameras.Select(c => $"{c.Name} ({c.Panel})"))}");

        // F-027 T1-T3 on the real camera: a tap meters there (when the camera can), the hold ends by itself, the camera
        // stays live and a still still works.
        var focusCaps = controller.FocusCapabilities;
        bool tapped = controller.TapToFocus(0.3, 0.6);
        bool heldNow = controller.IsFocusHeld;
        frames = 0;
        await TickUntil(() => false, 2.6);
        bool releasedHold = !controller.IsFocusHeld;
        bool stillLive = controller.State == CameraState.Live && frames >= 20;
        var afterFocus = await controller.CaptureAsync(1);
        bool logged;
        lock (Log) logged = focusCaps.Supported
            ? Log.Any(l => l.Contains("tap focus at")) && Log.Any(l => l.Contains("tap focus released")) && !Log.Any(l => l.Contains("focus region not set"))
            : Log.Any(l => l.Contains("no tap focus"));
        Check("L18", tapped == focusCaps.Supported && heldNow == focusCaps.Supported && releasedHold && stillLive && afterFocus.Still != null && logged,
            $"tap focus on this camera: {(focusCaps.Supported ? "applied" : "not supported, mark only")} ({focusCaps}); hold ended after 2 s; "
            + "live throughout and a still afterwards");

        controller.SetZoom(3);
        await controller.CloseAsync();
        await Task.Delay(1500);
        await controller.OpenAsync();
        bool reopenedLive = await TickUntil(() => controller.State == CameraState.Live, 15);
        var atOne = await controller.CaptureAsync(1);
        Check("L16", reopenedLive && controller.ZoomFactor == 1 && atOne.Still?.Width == fullW && atOne.Still?.Height == fullH,
            $"opened again at 1x: still {atOne.Still?.Width}x{atOne.Still?.Height}");

        await controller.CloseAsync();
        await Task.Delay(2500);
        Check("L11", controller.State == CameraState.Closed && !WebcamInUse(), "close releases the camera");
        return Finish(outDir);
    }

    private static TaskCompletionSource<CameraPreviewFrame>? _frameWanted;

    /// <summary>A copy of the next non-black preview frame (the backend reuses its buffers).</summary>
    private static async Task<CameraPreviewFrame> NextFrameAsync()
    {
        var wanted = new TaskCompletionSource<CameraPreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _frameWanted, wanted);
        var done = await Task.WhenAny(wanted.Task, Task.Delay(3000));
        Volatile.Write(ref _frameWanted, null);
        return done == wanted.Task ? wanted.Task.Result : new CameraPreviewFrame(0, 0, 0, null!, true);
    }

    /// <summary>The centred part of the picture with the given width/height ratio.</summary>
    private static BitmapSource CenterToAspect(BitmapSource source, double aspect)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        if (Math.Abs((double)w / h - aspect) < 0.01) return source;
        int cw = Math.Min(w, (int)Math.Round(h * aspect)), ch = Math.Min(h, (int)Math.Round(w / aspect));
        return new CroppedBitmap(source, new Int32Rect((w - cw) / 2, (h - ch) / 2, cw, ch));
    }

    /// <summary>The picture scaled to <paramref name="w"/> x <paramref name="h"/>, as BGRA, for a coarse comparison.</summary>
    private static byte[] Small(BitmapSource source, int w, int h)
    {
        var scaled = new TransformedBitmap(source, new ScaleTransform((double)w / source.PixelWidth, (double)h / source.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.MaxValue;
        long sum = 0;
        for (int i = 0; i + 3 < a.Length; i += 4)
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return sum / (3.0 * (a.Length / 4));
    }

    private static int Finish(string outDir)
    {
        lock (Log) File.WriteAllLines(Path.Combine(outDir, "live-check-log.txt"), Log);
        Console.WriteLine($"{_passed} passed, {_failed} failed (log: {Path.Combine(outDir, "live-check-log.txt")})");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>Windows' own camera-in-use record for this exe (CapabilityAccessManager): LastUsedTimeStop is 0 while
    /// the camera is open. Read only.</summary>
    private static bool WebcamInUse()
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        string keyName = exe.Replace('\\', '#');
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged\" + keyName);
        if (key == null) return false;
        long start = Convert.ToInt64(key.GetValue("LastUsedTimeStart") ?? 0L);
        long stop = Convert.ToInt64(key.GetValue("LastUsedTimeStop") ?? 0L);
        return start != 0 && stop == 0;
    }
}
