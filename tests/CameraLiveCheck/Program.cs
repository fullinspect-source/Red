// F-027 A-027-16 (and the capture half of A-027-15): drives the RedCamera.Core capture-session controller against this PC's
// camera. It never disables a device and never writes outside the folder given as the first argument (default: a temp
// folder). Exit 0 = all checks passed, 1 = a check failed, 3 = no camera on this PC (skipped).
using System.Diagnostics;
using System.IO;
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

        var controller = new CameraSessionController(backend, new MemoryPreferences(), () => DateTime.UtcNow, log: Write);
        int frames = 0;
        controller.PreviewFrame += _ => Interlocked.Increment(ref frames);
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

        await controller.CloseAsync();
        await Task.Delay(2500);
        Check("L11", controller.State == CameraState.Closed && !WebcamInUse(), "close releases the camera");
        return Finish(outDir);
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
