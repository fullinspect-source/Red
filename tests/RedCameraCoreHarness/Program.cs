// RedCamera.Core harness (F-027): drives the camera engine with a fake camera and a fake clock (ACCEPTANCE A-027-02..14,
// -17, -19, -21..-26, the W3 probe) and checks its file, preference, hold-signal and zoom helpers. It references only
// RedCamera.Core, so any app that uses the engine can run it. Exit 0 = all passed.
using System.IO;
using System.Runtime.InteropServices;
using RedCamera.Core;
using WpfImaging = System.Windows.Media.Imaging;
using WpfMedia = System.Windows.Media;

return await CameraHarness.RunAsync(args);

static class CameraHarness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {what}");
        if (ok) _passed++; else _failed++;
    }

    // ------------------------------------------------------------------ fakes

    private enum Plan { Stream, Fail, Hang }

    private sealed class FakeStream : ICameraStream
    {
        public FakeStream(CameraInfo camera, int number, ZoomCapabilities? zoom = null)
        {
            Camera = camera;
            Number = number;
            ZoomCapabilities = zoom ?? ZoomCapabilities.DigitalOnly;
        }
        public CameraInfo Camera { get; }
        public int Number { get; }
        public bool Shared => false;
        public bool Disposed { get; private set; }
        public int CaptureCalls { get; private set; }
        public TaskCompletionSource<CapturedStill>? PendingCapture { get; set; }
        public ZoomCapabilities ZoomCapabilities { get; }
        public List<double> HardwareZooms { get; } = new();
        public TaskCompletionSource<bool>? PendingZoom { get; set; }
        public bool RefuseZoom { get; set; }
        public event Action<CameraPreviewFrame>? FrameArrived;
        public event Action<int, string>? Failed;

        public Task<bool> SetHardwareZoomAsync(double factor)
        {
            HardwareZooms.Add(factor);
            var pending = PendingZoom;
            PendingZoom = null;
            return pending?.Task ?? Task.FromResult(!RefuseZoom);
        }

        public void Frame(bool black = false) =>
            FrameArrived?.Invoke(new CameraPreviewFrame(4, 4, 16, new byte[64], black));
        public void Fail(int hr) => Failed?.Invoke(hr, "fake failure");

        public Task<CapturedStill> CaptureStillAsync(int exifOrientation, CancellationToken cancellationToken)
        {
            CaptureCalls++;
            if (PendingCapture != null) return PendingCapture.Task;
            return Task.FromResult(new CapturedStill(new byte[2048], 4000, 3000, "photo", exifOrientation));
        }

        public Task DebugStopFramesAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeBackend : ICameraBackend
    {
        public List<CameraInfo> Cameras { get; } = new();
        public List<FakeStream> Streams { get; } = new();
        public List<CameraInfo> Opened { get; } = new();
        public int FindCalls { get; private set; }
        public Func<CameraInfo, int, (Plan Plan, int HResult)> PlanFor { get; set; } = (_, _) => (Plan.Stream, 0);
        public Func<CameraInfo, ZoomCapabilities?> ZoomFor { get; set; } = _ => null;
        public bool RefuseZoom { get; set; }
        public TaskCompletionSource<ICameraStream>? Hung { get; private set; }

        public Task<IReadOnlyList<CameraInfo>> FindCamerasAsync()
        {
            FindCalls++;
            return Task.FromResult<IReadOnlyList<CameraInfo>>(Cameras.ToList());
        }

        public Task<ICameraStream> OpenAsync(CameraInfo camera, CancellationToken cancellationToken)
        {
            int attemptOnCamera = Opened.Count(c => c.Id == camera.Id);
            Opened.Add(camera);
            var (plan, hr) = PlanFor(camera, attemptOnCamera);
            switch (plan)
            {
                case Plan.Fail:
                    return Task.FromException<ICameraStream>(new COMException("fake open failure", hr));
                case Plan.Hang:
                    Hung = new TaskCompletionSource<ICameraStream>();
                    return Hung.Task;
                default:
                    var stream = new FakeStream(camera, Streams.Count + 1, ZoomFor(camera)) { RefuseZoom = RefuseZoom };
                    Streams.Add(stream);
                    return Task.FromResult<ICameraStream>(stream);
            }
        }

        public FakeStream NewStream(CameraInfo camera)
        {
            var stream = new FakeStream(camera, Streams.Count + 1);
            Streams.Add(stream);
            return stream;
        }
    }

    private sealed class MemoryPreferences : ICameraPreferences
    {
        public string? DeviceId { get; set; }
        public CameraPanelKind? Panel { get; set; }
        public int Saves { get; private set; }
        public void Remember(CameraInfo camera) { DeviceId = camera.Id; Panel = camera.Panel; Saves++; }
    }

    /// <summary>Records each crop and returns a still of the crop's size (the real JPEG crop is tested separately).</summary>
    private sealed class FakeCropper : ICameraStillCropper
    {
        public List<double> Factors { get; } = new();
        public bool Throw { get; set; }

        public Task<CapturedStill> CropAsync(CapturedStill still, double factor)
        {
            Factors.Add(factor);
            if (Throw) return Task.FromException<CapturedStill>(new InvalidOperationException("fake crop failure"));
            var crop = CameraZoom.CropFor(still.Width, still.Height, factor);
            return Task.FromResult(still with { Jpeg = new byte[512], Width = crop.Width, Height = crop.Height, Zoom = factor });
        }
    }

    private sealed class Rig
    {
        public DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        public FakeBackend Backend { get; } = new();
        public MemoryPreferences Prefs { get; } = new();
        public FakeCropper Cropper { get; } = new();
        public CameraSessionController Controller { get; }
        public List<string> Log { get; } = new();

        public Rig(params CameraInfo[] cameras)
        {
            Backend.Cameras.AddRange(cameras);
            Controller = new CameraSessionController(Backend, Prefs, () => Now, log: Log.Add, runDetached: work => work(),
                cropper: Cropper);
        }

        public FakeStream? Current => Backend.Streams.LastOrDefault(s => !s.Disposed);

        /// <summary>Advance the fake clock in 100 ms ticks; the live stream (if any) delivers a frame each tick when
        /// <paramref name="frames"/> is set.</summary>
        public async Task Run(double seconds, bool frames = false, bool black = false)
        {
            int steps = (int)Math.Round(seconds * 10);
            for (int i = 0; i < steps; i++)
            {
                Now = Now.AddMilliseconds(100);
                if (frames) Current?.Frame(black);
                await Controller.TickAsync();
            }
        }

        public async Task OpenLive()
        {
            await Controller.OpenAsync();
            await Run(0.3, frames: true);
        }
    }

    private static readonly CameraInfo Back = new("cam-back", "Rear Camera", CameraPanelKind.Back, 0);
    private static readonly CameraInfo Front = new("cam-front", "Front Camera", CameraPanelKind.Front, 0);
    private static readonly CameraInfo Usb = new("cam-usb", "USB Camera", CameraPanelKind.External, 0);

    // ------------------------------------------------------------------ checks

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            await OpenAndClose();
            await MinimizeAndRestore();
            await SuspendAndResume();
            await DisplayOffAndLock();
            await StallRestarts();
            await FirstFrameLimit();
            await BlackFrames();
            await StreamFailedEvent();
            await BoundedRetries();
            await HungOpen();
            await FailureCauses();
            await ShutterGate();
            await OnePressOnePhoto();
            await CameraChoice();
            await HealthyResetAndCap();
            await HoldWhileGivenUp();
            await ResetOnlyOnRequest();
            Orientation();
            FrameProbe();
            await CoreFiles();
            await InspectWatchersSeeOneCreated();
            Preferences();
            await CaptureToFile();
            HoldWatcherMessages();
            ZoomRules();
            await DigitalZoomSession();
            await ZoomKeptAcrossRebuilds();
            await ZoomResets();
            await CameraOwnZoom();
            await CropFailureKeepsPhoto();
            StillMatchesPreview();
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString());
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task OpenAndClose()
    {
        var rig = new Rig(Back);
        await rig.Controller.OpenAsync();
        bool starting = rig.Controller.State == CameraState.Starting && rig.Controller.StatusText == CameraSessionController.StartingText;
        await rig.Run(0.2, frames: true);
        bool live = rig.Controller.State == CameraState.Live;
        var s1 = rig.Backend.Streams.Single();
        await rig.Controller.CloseAsync();
        Check("A-027-02", starting && live && rig.Backend.Opened.Count == 1 && s1.Disposed && rig.Controller.State == CameraState.Closed
            && rig.Backend.Streams.All(s => s.Disposed),
            "one session while open, live after a frame, disposed on Close");
    }

    private static async Task MinimizeAndRestore()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        var s1 = rig.Current!;
        int finds = rig.Backend.FindCalls;
        rig.Controller.SetHold(CameraHold.Minimized, true);
        bool released = s1.Disposed && rig.Controller.State == CameraState.Paused && rig.Controller.StatusText == CameraSessionController.PausedText;
        await rig.Run(10);
        bool noOpenWhileHeld = rig.Backend.Opened.Count == 1;
        rig.Controller.SetHold(CameraHold.Minimized, false);
        await rig.Run(1.4);
        bool waitedSettle = rig.Backend.Opened.Count == 1 && rig.Controller.State == CameraState.Paused;
        await rig.Run(0.2);
        var s2 = rig.Backend.Streams.Last();
        await rig.Run(0.2, frames: true);
        Check("A-027-03", released && noOpenWhileHeld && waitedSettle && rig.Backend.Opened.Count == 2 && s2 != s1 && !s2.Disposed
            && rig.Backend.FindCalls > finds && rig.Controller.State == CameraState.Live,
            "minimize releases at once; restore waits 1.5 s, enumerates again and opens a new session");
    }

    private static async Task SuspendAndResume()
    {
        var rig = new Rig(Back, Front);
        rig.Prefs.DeviceId = Front.Id;
        await rig.OpenLive();
        var s1 = rig.Current!;
        rig.Controller.SetHold(CameraHold.Suspended, true);
        bool released = s1.Disposed;
        await rig.Run(30);
        rig.Controller.SetHold(CameraHold.Suspended, false);
        await rig.Run(1.4);
        bool notYet = rig.Backend.Opened.Count == 1;
        await rig.Run(0.2);
        Check("A-027-04", released && notYet && rig.Backend.Opened.Count == 2 && rig.Backend.Opened[1].Id == Front.Id
            && rig.Backend.Streams.Last() != s1,
            "suspend disposes the session; 1.5 s after resume a new session opens on the remembered camera");
    }

    private static async Task DisplayOffAndLock()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        var s1 = rig.Current!;
        rig.Controller.SetHold(CameraHold.DisplayOff, true);
        bool released = s1.Disposed;
        rig.Controller.SetHold(CameraHold.Locked, true);
        await rig.Run(5);
        rig.Controller.SetHold(CameraHold.DisplayOff, false);
        await rig.Run(5);
        bool stillHeld = rig.Backend.Opened.Count == 1 && rig.Controller.State == CameraState.Paused;
        rig.Controller.SetHold(CameraHold.Locked, false);
        await rig.Run(1.6);
        await rig.Run(5, frames: true);
        Check("A-027-05", released && stillHeld && rig.Backend.Opened.Count == 2 && rig.Controller.State == CameraState.Live,
            "display off releases; no session while locked; one new session 1.5 s after unlock");
    }

    private static async Task StallRestarts()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        var s1 = rig.Current!;
        await rig.Run(1.9);
        bool liveAt19 = rig.Controller.State == CameraState.Live && !s1.Disposed;
        await rig.Run(0.2);
        bool restarting = rig.Controller.State == CameraState.Restarting && rig.Controller.StatusText == CameraSessionController.RestartingText
            && s1.Disposed && rig.Backend.Opened.Count == 1;
        await rig.Run(0.5);
        bool reopened = rig.Backend.Opened.Count == 2;
        await rig.Run(0.2, frames: true);
        Check("A-027-06", liveAt19 && restarting && reopened && rig.Controller.State == CameraState.Live,
            "2 s without frames: \"Restarting camera…\", old session disposed, new one 0.5 s later, live again");
    }

    private static async Task FirstFrameLimit()
    {
        var rig = new Rig(Back);
        await rig.Controller.OpenAsync();
        await rig.Run(4.9);
        bool waiting = rig.Controller.State == CameraState.Starting;
        await rig.Run(0.2);
        Check("A-027-07", waiting && rig.Controller.State == CameraState.Restarting && rig.Backend.Streams[0].Disposed,
            "no first frame within 5 s counts as a stall");
    }

    private static async Task BlackFrames()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        await rig.Run(2.9, frames: true, black: true);
        bool live = rig.Controller.State == CameraState.Live;
        await rig.Run(0.2, frames: true, black: true);
        bool stalled = rig.Controller.State == CameraState.Restarting;

        var rig2 = new Rig(Back);
        await rig2.OpenLive();
        await rig2.Run(2.0, frames: true, black: true);
        await rig2.Run(0.1, frames: true);
        await rig2.Run(1.2, frames: true, black: true);
        Check("A-027-08", live && stalled && rig2.Controller.State == CameraState.Live,
            "3 s of all-black frames counts as no frames; one non-black frame resets it");
    }

    private static async Task StreamFailedEvent()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        var s1 = rig.Current!;
        s1.Fail(unchecked((int)0xC00DABE0));
        await rig.Run(0.1, frames: false);
        Check("W4", rig.Controller.State == CameraState.Restarting && s1.Disposed,
            "the capture object's Failed event ends the session at the next tick");
    }

    private static async Task BoundedRetries()
    {
        var rig = new Rig(Back, Front);
        rig.Backend.PlanFor = (camera, _) => camera.Id == Front.Id ? (Plan.Fail, unchecked((int)0xC00DABE0)) : (Plan.Stream, 0);
        await rig.Controller.OpenAsync();
        var times = new List<double> { 0.0 };
        int last = rig.Backend.Opened.Count;
        for (int i = 0; i < 400; i++)
        {
            await rig.Run(0.1);
            if (rig.Backend.Opened.Count != last) { last = rig.Backend.Opened.Count; times.Add(i * 0.1 + 0.1); }
        }
        int onBack = rig.Backend.Opened.Count(c => c.Id == Back.Id);
        int onFront = rig.Backend.Opened.Count(c => c.Id == Front.Id);
        bool gaveUp = rig.Controller.State == CameraState.GaveUp && rig.Controller.FailureKind == CameraFailureKind.NotResponding
            && rig.Controller.StatusText == "The camera is not responding.";
        int opensAtGiveUp = rig.Backend.Opened.Count;
        await rig.Run(60);
        // Gaps: first-frame limit 5 s + 0.5, + 1.5, + 3 on Back, then 0.5 to Front.
        string gaps = string.Join(", ", times.Zip(times.Skip(1), (a, b) => (b - a).ToString("0.0")));
        Check("A-027-09", onBack == 4 && onFront == 1 && gaveUp && rig.Backend.Opened.Count == opensAtGiveUp
            && rig.Backend.Opened[0].Id == Back.Id && rig.Backend.Opened[4].Id == Front.Id,
            $"Back initial + 3 retries, Front once, then \"not responding\" and no more opens (gaps {gaps})");
        Check("A-027-09b", gaps == "5.6, 6.6, 8.1, 5.6",
            $"each attempt waits out the 5 s first-frame limit, then 0.5, 1.5, 3 s on Back and 0.5 s to Front (gaps {gaps})");
    }

    private static async Task HungOpen()
    {
        var rig = new Rig(Back);
        rig.Backend.PlanFor = (_, attempt) => attempt == 0 ? (Plan.Hang, 0) : (Plan.Stream, 0);
        await rig.Controller.OpenAsync();
        var hung = rig.Backend.Hung!;
        await rig.Run(9.9);
        bool stillWaiting = rig.Controller.State == CameraState.Starting && rig.Backend.Opened.Count == 1;
        await rig.Run(0.2);
        bool abandoned = rig.Controller.State == CameraState.Restarting;
        await rig.Run(0.6, frames: true);
        var good = rig.Current;
        var late = rig.Backend.NewStream(Back);
        hung.SetResult(late);
        await rig.Run(0.3, frames: true);
        Check("A-027-10", stillWaiting && abandoned && late.Disposed && good != null && good != late && !good.Disposed
            && rig.Controller.State == CameraState.Live,
            "an open that hangs is abandoned after 10 s; its late session is disposed and never used");
    }

    private static async Task FailureCauses()
    {
        var denied = new Rig(Back);
        denied.Backend.PlanFor = (_, _) => (Plan.Fail, unchecked((int)0x80070005));
        await denied.Controller.OpenAsync();
        await denied.Run(30);
        Check("A-027-11a", denied.Backend.Opened.Count == 1 && denied.Controller.State == CameraState.GaveUp
            && denied.Controller.StatusText == "Windows is blocking camera access for desktop apps.",
            "access denied: no retries, privacy message");

        var inUse = new Rig(Back);
        inUse.Backend.PlanFor = (_, _) => (Plan.Fail, unchecked((int)0xC00DABE1));
        await inUse.Controller.OpenAsync();
        await inUse.Run(30);
        Check("A-027-11b", inUse.Backend.Opened.Count == 4 && inUse.Controller.FailureKind == CameraFailureKind.InUse
            && inUse.Controller.StatusText == "Another app is using the camera. Close it and tap Try again.",
            "in use on every attempt: retries, then the in-use message");

        var none = new Rig();
        await none.Controller.OpenAsync();
        Check("A-027-11c", none.Backend.Opened.Count == 0 && none.Controller.StatusText == "No camera was found on this device.",
            "no cameras: message, no open");

        var blocked = new Rig(Back);
        await blocked.OpenLive();
        blocked.Current!.Fail(CameraSessionController.BlockedForPrivacyCode);
        await blocked.Run(0.1);
        Check("A-027-11d", blocked.Controller.FailureKind == CameraFailureKind.Blocked && blocked.Backend.Opened.Count == 1,
            "blocked for privacy: message, no retries");
    }

    private static async Task ShutterGate()
    {
        var rig = new Rig(Back);
        await rig.Controller.OpenAsync();
        var starting = await rig.Controller.CaptureAsync(1);
        await rig.Run(0.2, frames: true);
        var s1 = rig.Current!;
        await rig.Run(2.1); // stall
        var restarting = await rig.Controller.CaptureAsync(1);
        rig.Controller.SetHold(CameraHold.Minimized, true);
        var paused = await rig.Controller.CaptureAsync(1);
        rig.Controller.SetHold(CameraHold.Minimized, false);
        await rig.Run(1.6);
        await rig.Run(0.3, frames: true);
        int callsBeforeLive = rig.Backend.Streams.Sum(s => s.CaptureCalls);
        await rig.Run(1.0, frames: true);
        bool noReplay = rig.Backend.Streams.Sum(s => s.CaptureCalls) == 0 && callsBeforeLive == 0;

        var dead = new Rig();
        await dead.Controller.OpenAsync();
        var gaveUp = await dead.Controller.CaptureAsync(1);

        Check("A-027-12", starting.Still == null && starting.Message == CameraSessionController.NotLivePressText
            && restarting.Still == null && restarting.Message == CameraSessionController.NotLivePressText
            && paused.Message == CameraSessionController.PausedPressText
            && gaveUp.Still == null && gaveUp.Message == "No camera was found on this device." && noReplay
            && rig.Controller.State == CameraState.Live,
            "a press while not live takes nothing, says why, and is not replayed when the camera comes back");
    }

    private static async Task OnePressOnePhoto()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        var stream = rig.Current!;
        stream.PendingCapture = new TaskCompletionSource<CapturedStill>();
        var first = rig.Controller.CaptureAsync(6);
        var second = await rig.Controller.CaptureAsync(6);
        stream.PendingCapture.SetResult(new CapturedStill(new byte[4096], 4000, 3000, "photo", 6));
        var taken = await first;
        Check("A-027-13", stream.CaptureCalls == 1 && second == CaptureOutcome.Ignored && taken.Still != null
            && taken.Still.ExifOrientation == 6 && !rig.Controller.IsCapturing,
            "two quick presses: one capture call, the second press ignored");
    }

    private static async Task CameraChoice()
    {
        var pickDefault = CameraSessionController.PickPreferred(new[] { Front, Back }, null, null);
        var pickPanel = CameraSessionController.PickPreferred(new[] { Back, Front }, "gone", CameraPanelKind.Front);
        var pickId = CameraSessionController.PickPreferred(new[] { Back, Front, Usb }, Usb.Id, CameraPanelKind.Back);
        var pickFirst = CameraSessionController.PickPreferred(new[] { Usb }, null, null);

        var rig = new Rig(Front, Back);
        await rig.OpenLive();
        bool openedBack = rig.Backend.Opened[0].Id == Back.Id;
        rig.Controller.SwitchCamera();
        await rig.Run(0.3, frames: true);
        bool switched = rig.Backend.Opened.Last().Id == Front.Id && rig.Prefs.DeviceId == Front.Id && rig.Prefs.Panel == CameraPanelKind.Front
            && rig.Controller.State == CameraState.Live;

        // Recovery may move to the other camera, but the remembered choice stays.
        var rig2 = new Rig(Back, Front);
        rig2.Prefs.DeviceId = Back.Id;
        rig2.Backend.PlanFor = (camera, _) => camera.Id == Back.Id ? (Plan.Fail, unchecked((int)0xC00DABE0)) : (Plan.Stream, 0);
        await rig2.Controller.OpenAsync();
        await rig2.Run(6, frames: true);
        bool movedButRemembered = rig2.Controller.CurrentCamera?.Id == Front.Id && rig2.Controller.State == CameraState.Live
            && rig2.Prefs.DeviceId == Back.Id && rig2.Prefs.Saves == 0;

        Check("A-027-14", pickDefault == Back && pickPanel == Front && pickId == Usb && pickFirst == Usb && openedBack && switched
            && movedButRemembered,
            "rear by default; switching remembers id and panel; a missing id falls back to the panel; recovery keeps the choice");
    }

    private static async Task HealthyResetAndCap()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        await rig.Run(2.1);           // stall 1: queue built, first retry used
        await rig.Run(0.6, frames: true);
        await rig.Run(31, frames: true); // healthy 30 s resets the attempts
        int before = rig.Backend.Opened.Count;
        rig.Backend.PlanFor = (_, _) => (Plan.Fail, unchecked((int)0xC00DABE0));
        await rig.Run(2.1);           // stall 2: a fresh queue of 3 same-camera attempts
        await rig.Run(10);
        int retries = rig.Backend.Opened.Count - before;
        Check("C5-reset", retries == 3 && rig.Controller.State == CameraState.GaveUp,
            $"after 30 s of healthy frames a new stall gets all 3 attempts again ({retries})");

        var cap = new Rig(Back);
        await cap.OpenLive();
        for (int i = 0; i < 7 && cap.Controller.State != CameraState.GaveUp; i++)
        {
            await cap.Run(2.1);                 // stall
            await cap.Run(0.6, frames: true);   // recovered
            await cap.Run(31, frames: true);    // healthy long enough to reset the attempts
        }
        Check("C5-cap", cap.Controller.State == CameraState.GaveUp,
            "more than 6 recoveries within 5 minutes gives up instead of cycling forever");
    }

    private static async Task HoldWhileGivenUp()
    {
        var rig = new Rig(Back);
        rig.Backend.PlanFor = (_, attempt) => attempt < 4 ? (Plan.Fail, unchecked((int)0xC00DABE0)) : (Plan.Stream, 0);
        await rig.Controller.OpenAsync();
        await rig.Run(10);
        bool gaveUp = rig.Controller.State == CameraState.GaveUp;
        rig.Controller.SetHold(CameraHold.Suspended, true);
        bool paused = rig.Controller.State == CameraState.Paused;
        rig.Controller.SetHold(CameraHold.Suspended, false);
        await rig.Run(1.6);
        await rig.Run(0.3, frames: true);
        Check("C3-gaveup", gaveUp && paused && rig.Controller.State == CameraState.Live,
            "a sleep/wake after giving up starts a fresh session (the device may be back)");

        var retry = new Rig(Back);
        retry.Backend.PlanFor = (_, attempt) => attempt < 4 ? (Plan.Fail, unchecked((int)0xC00DABE0)) : (Plan.Stream, 0);
        await retry.Controller.OpenAsync();
        await retry.Run(10);
        await retry.Controller.RetryAsync();
        await retry.Run(0.3, frames: true);
        Check("C8-retry", retry.Controller.State == CameraState.Live && retry.Backend.Opened.Count == 5,
            "Try again opens a fresh session with the counters reset");
    }

    private sealed class FakeRunner : ICameraResetRunner
    {
        public List<string> Runs { get; } = new();
        public Task<int?> RunAsync(string instanceId) { Runs.Add(instanceId); return Task.FromResult<int?>(0); }
    }

    private static async Task ResetOnlyOnRequest()
    {
        // The controller cannot reach the reset at all: it holds no runner or flow.
        bool controllerHasNoReset = typeof(CameraSessionController)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .All(f => f.FieldType != typeof(ICameraResetRunner) && f.FieldType != typeof(CameraResetFlow));

        const string id = @"USB\VID_0000&PID_0000&MI_00\6&123ABC&0&0000";
        var runner = new FakeRunner();
        var declined = new CameraResetFlow(_ => Task.FromResult<string?>(id), _ => false, runner, _ => Task.CompletedTask);
        var (ranDeclined, _) = await declined.RunAsync(Back);
        var unsafeFlow = new CameraResetFlow(_ => Task.FromResult<string?>("USB\\X'; Remove-Item C:\\"), _ => true, runner, _ => Task.CompletedTask);
        var (ranUnsafe, unsafeMessage) = await unsafeFlow.RunAsync(Back);
        string? asked = null;
        var confirmed = new CameraResetFlow(_ => Task.FromResult<string?>(id), text => { asked = text; return true; }, runner, _ => Task.CompletedTask);
        var (ran, message) = await confirmed.RunAsync(Back);
        string command = CameraDeviceReset.BuildCommand(id);
        var start = CameraDeviceReset.BuildStartInfo(id);
        int disable = command.IndexOf("Disable-PnpDevice -InstanceId '" + id + "'", StringComparison.Ordinal);
        int enable = command.IndexOf("Enable-PnpDevice -InstanceId '" + id + "'", StringComparison.Ordinal);

        Check("A-027-17", controllerHasNoReset && !ranDeclined && !ranUnsafe && unsafeMessage == CameraResetFlow.CannotResetText
            && ran && message == null && runner.Runs.Count == 1 && runner.Runs[0] == id
            && asked != null && asked.Contains("administrator permission") && asked.Contains(Back.Name)
            && disable >= 0 && enable > disable && start.Verb == "runas" && start.UseShellExecute,
            "reset runs only after an explicit confirm, elevated, disable then enable; declined or unsafe ids run nothing");
    }

    private static void Orientation()
    {
        var front = new CameraInfo("f", "Front", CameraPanelKind.Front, 0);
        var back = new CameraInfo("b", "Back", CameraPanelKind.Back, 0);
        var ext = new CameraInfo("e", "USB", CameraPanelKind.External, 0);
        var rows = new (CameraInfo Cam, int DeviceCcw, int Exif, int Preview, bool Mirror)[]
        {
            (front, 0, 1, 0, true),
            (back, 0, 1, 0, false),
            (back, 90, 8, 270, false),
            (back, 270, 6, 90, false),
            (back, 180, 3, 180, false),
            (front, 90, 6, 90, true),
            (ext, 90, 1, 0, false),
        };
        var bad = rows.Where(r =>
                CameraOrientation.CaptureExifOrientation(r.Cam, r.DeviceCcw, nativePortrait: false) != r.Exif
                || CameraOrientation.PreviewRotationClockwise(r.Cam, r.DeviceCcw, nativePortrait: false) != r.Preview
                || CameraOrientation.MirrorPreview(r.Cam) != r.Mirror)
            .Select(r => $"{r.Cam.Panel}@{r.DeviceCcw}")
            .ToList();
        bool display = CameraOrientation.DisplayToDeviceCcw(0) == 0 && CameraOrientation.DisplayToDeviceCcw(1) == 270
            && CameraOrientation.DisplayToDeviceCcw(2) == 180 && CameraOrientation.DisplayToDeviceCcw(3) == 90;
        Check("A-027-19", bad.Count == 0 && display, "orientation table" + (bad.Count == 0 ? string.Empty : ": wrong " + string.Join(", ", bad)));
    }

    private static void FrameProbe()
    {
        var black = new byte[64 * 48 * 4];
        var dim = (byte[])black.Clone();
        dim[(24 * 64 + 36) * 4 + 1] = 1; // one sampled pixel slightly green
        bool ok = CameraFrameProbe.IsAllBlack(black, 64, 48, 64 * 4)
            && !CameraFrameProbe.IsAllBlack(Enumerable.Repeat((byte)12, black.Length).ToArray(), 64, 48, 64 * 4);
        Check("W3-probe", ok, "the black-frame probe flags exact (0,0,0) frames only");
    }

    // ------------------------------------------------------------------ library helpers

    private static string TempFolder() => Path.Combine(Path.GetTempPath(), "redcamera-core-harness-" + Guid.NewGuid().ToString("N"));

    private static Task CoreFiles()
    {
        string folder = TempFolder();
        try
        {
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
            var when = new DateTime(2026, 10, 20, 9, 5, 7, 123);
            string first = CameraFiles.SaveCopy(jpeg, Path.Combine(folder, "roll"), when);
            string second = CameraFiles.SaveCopy(jpeg, Path.Combine(folder, "roll"), when);
            string other = CameraFiles.SaveCopy(jpeg, Path.Combine(folder, "roll"), when, prefix: "CAM_");
            Check("C9-copy", Path.GetFileName(first) == "RED_20261020_090507_123.jpg" && Path.GetFileName(second) == "RED_20261020_090507_123_2.jpg"
                && Path.GetFileName(other) == "CAM_20261020_090507_123.jpg" && File.ReadAllBytes(first).SequenceEqual(jpeg)
                && Directory.GetFiles(Path.Combine(folder, "roll")).Length == 3,
                "stills saved as {prefix}yyyyMMdd_HHmmss_fff.jpg, never overwritten, no temporary file left");

            bool refused = false;
            try { CameraFiles.SaveAtomic(new byte[] { 1 }, first); } catch (IOException) { refused = true; }
            Check("C9-atomic", refused && File.ReadAllBytes(first).SequenceEqual(jpeg), "SaveAtomic never replaces an existing file");

            // INSPECT2026's camera roll (IL, R-red-camera-companion §3): the first root in order with any "Camera Roll*"
            // wins, its newest by LAST-WRITE time; no match → the first existing root; no root → null. Never a new folder.
            string pictures = Path.Combine(folder, "Pictures"), oneDrive = Path.Combine(folder, "OneDrive");
            Directory.CreateDirectory(pictures);
            Directory.CreateDirectory(Path.Combine(oneDrive, "Pictures"));
            var env = new Dictionary<string, string?> { ["OneDrive"] = oneDrive };
            Func<string, string?> lookup = v => env.GetValueOrDefault(v);
            string? bare = CameraFiles.ResolveCameraRoll(lookup, pictures);
            string? forSavingBare = CameraFiles.CameraRollFolderForSaving(lookup, pictures);
            bool noFolderMade = !Directory.EnumerateDirectories(pictures).Any();
            // A later root holds the newest folder; the earlier root still wins (the old RED rule picked the newest anywhere).
            string inPictures = Path.Combine(pictures, "Camera Roll");
            Directory.CreateDirectory(inPictures);
            Directory.SetLastWriteTimeUtc(inPictures, DateTime.UtcNow.AddDays(-2));
            string inOneDrive = Path.Combine(oneDrive, "Pictures", "Camera Roll 2026");
            Directory.CreateDirectory(inOneDrive);
            string? firstRootWins = CameraFiles.ResolveCameraRoll(lookup, pictures);
            // Within a root, last-write time decides, not creation time.
            string newerCreated = Path.Combine(pictures, "Camera Roll 2");
            Directory.CreateDirectory(newerCreated);
            Directory.SetCreationTimeUtc(newerCreated, DateTime.UtcNow);
            Directory.SetLastWriteTimeUtc(newerCreated, DateTime.UtcNow.AddDays(-5));
            Directory.SetCreationTimeUtc(inPictures, DateTime.UtcNow.AddDays(-10));
            Directory.SetLastWriteTimeUtc(inPictures, DateTime.UtcNow.AddDays(-1));
            string? byLastWrite = CameraFiles.ResolveCameraRoll(lookup, pictures);
            // The first root missing: the OneDrive one is used.
            string? skipMissing = CameraFiles.ResolveCameraRoll(lookup, Path.Combine(folder, "Missing"));
            string? none = CameraFiles.ResolveCameraRoll(_ => null, Path.Combine(folder, "Missing"));
            var roots = CameraFiles.CandidateRoots(v => v == "OneDrive" ? oneDrive : v == "USERPROFILE" ? oneDrive + "\\" : null, pictures);
            Check("T3-roll", bare == pictures && forSavingBare == pictures && noFolderMade
                && firstRootWins == inPictures && byLastWrite == inPictures && skipMissing == inOneDrive && none == null
                && roots.Count == 2,
                "INSPECT2026: first root with a Camera Roll* wins, newest by last-write; else the first existing root; nothing created; duplicate roots dropped");

            // Inspect2022: Pictures\Camera Roll if it exists, else Pictures, else null.
            string p22 = Path.Combine(folder, "P22");
            string? none22 = CameraFiles.ResolveInspect2022CameraRoll(p22);
            Directory.CreateDirectory(p22);
            string? root22 = CameraFiles.ResolveInspect2022CameraRoll(p22);
            Directory.CreateDirectory(Path.Combine(p22, "Camera Roll 9"));
            string? stillRoot22 = CameraFiles.ResolveInspect2022CameraRoll(p22);
            Directory.CreateDirectory(Path.Combine(p22, "Camera Roll"));
            string? roll22 = CameraFiles.ResolveInspect2022CameraRoll(p22);
            Check("T3-2022", none22 == null && root22 == p22 && stillRoot22 == p22 && roll22 == Path.Combine(p22, "Camera Roll"),
                "Inspect2022: exactly Pictures\\Camera Roll, else Pictures, else nothing");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Real FileSystemWatchers set up as each INSPECT watches (R-red-camera-companion §4): 2026 = no filter, subfolders,
    /// Created and Renamed; 2022 = "*.jpg", no subfolders, Created only. A saved still must reach both exactly once, as a
    /// complete file, and nothing else (no temporary name) may be seen in the folder.
    /// </summary>
    private static async Task InspectWatchersSeeOneCreated()
    {
        string folder = TempFolder();
        try
        {
            string roll = Path.Combine(folder, "Pictures", "Camera Roll");
            Directory.CreateDirectory(roll);
            var seen2026 = new System.Collections.Concurrent.ConcurrentBag<string>();
            var seen2022 = new System.Collections.Concurrent.ConcurrentBag<string>();
            var complete = new System.Collections.Concurrent.ConcurrentBag<bool>();
            using var w2026 = new FileSystemWatcher(roll)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
            };
            using var w2022 = new FileSystemWatcher(roll, "*.jpg")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            var jpeg = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
            jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
            w2026.Created += (_, e) =>
            {
                seen2026.Add("Created " + Path.GetFileName(e.FullPath));
                try { complete.Add(File.ReadAllBytes(e.FullPath).Length == jpeg.Length); } catch (IOException) { complete.Add(false); }
            };
            w2026.Renamed += (_, e) => seen2026.Add("Renamed " + Path.GetFileName(e.FullPath));
            w2022.Created += (_, e) => seen2022.Add("Created " + Path.GetFileName(e.FullPath));
            w2026.EnableRaisingEvents = true;
            w2022.EnableRaisingEvents = true;

            string saved = CameraFiles.SaveCopy(jpeg, roll, new DateTime(2026, 10, 20, 9, 5, 7, 123), createFolder: false);
            await Task.Delay(1500);
            string name = Path.GetFileName(saved);
            bool staged = !Directory.GetFiles(Path.Combine(folder, "Pictures")).Any() && Directory.GetFiles(roll).Length == 1;
            Check("C9-watchers", seen2026.OrderBy(s => s).SequenceEqual(new[] { "Created " + name })
                && seen2022.OrderBy(s => s).SequenceEqual(new[] { "Created " + name })
                && complete.All(c => c) && staged,
                $"one complete still reaches both INSPECT watchers as a single Created event (2026: {string.Join(", ", seen2026)}; 2022: {string.Join(", ", seen2022)})");

            bool refusedMissing = false;
            try { CameraFiles.SaveCopy(jpeg, Path.Combine(folder, "Pictures", "Camera Roll 7"), DateTime.Now, createFolder: false); }
            catch (DirectoryNotFoundException) { refusedMissing = true; }
            Check("C9-nofolder", refusedMissing && !Directory.Exists(Path.Combine(folder, "Pictures", "Camera Roll 7")),
                "with createFolder: false a missing folder is never created");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static void Preferences()
    {
        string folder = TempFolder();
        try
        {
            string path = Path.Combine(folder, "camera.json");
            var prefs = new JsonCameraPreferences(path);
            bool empty = prefs.DeviceId == null && prefs.Panel == null;
            prefs.Remember(Back);
            var again = new JsonCameraPreferences(path);
            File.WriteAllText(Path.Combine(folder, "bad.json"), "{ not json");
            var bad = new JsonCameraPreferences(Path.Combine(folder, "bad.json"));
            Check("C11-prefs", empty && again.DeviceId == Back.Id && again.Panel == CameraPanelKind.Back && bad.DeviceId == null,
                "the remembered camera survives a restart; an unreadable file means nothing remembered");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task CaptureToFile()
    {
        string folder = TempFolder();
        try
        {
            var rig = new Rig(Back);
            await rig.OpenLive();
            string path = Path.Combine(folder, "Camera Roll", "WIN_20261020_09_05_07_Pro.jpg");
            var (outcome, written) = await rig.Controller.CaptureToFileAsync(path, 1);
            bool ok = outcome.Still != null && written == path && File.Exists(path) && new FileInfo(path).Length == outcome.Still.Jpeg.Length
                && !Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Any();
            rig.Controller.SetHold(CameraHold.Suspended, true);
            var (paused, none) = await rig.Controller.CaptureToFileAsync(Path.Combine(folder, "x.jpg"), 1);
            Check("C7-file", ok && paused.Still == null && none == null && !File.Exists(Path.Combine(folder, "x.jpg")),
                "CaptureToFileAsync writes the full-size still to the path in one step; nothing is written while paused");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

    /// <summary>Real WM_POWERBROADCAST messages to the watcher's own hidden window (no visible window needed).</summary>
    private static void HoldWatcherMessages()
    {
        var seen = new List<string>();
        Exception? error = null;
        IntPtr handle = IntPtr.Zero;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                using var watcher = new CameraHoldWatcher(dispatcher, (hold, on) => seen.Add($"{hold}:{(on ? "on" : "off")}"));
                watcher.Attach();
                handle = watcher.Handle;
                SendMessage(handle, 0x0218, (IntPtr)0x0004, IntPtr.Zero); // PBT_APMSUSPEND
                SendMessage(handle, 0x0218, (IntPtr)0x0012, IntPtr.Zero); // PBT_APMRESUMEAUTOMATIC
                watcher.RaiseDisplayState(0);
                watcher.RaiseDisplayState(2); // dimmed: no change
                watcher.RaiseDisplayState(1);
                watcher.RaiseSessionSwitch(Microsoft.Win32.SessionSwitchReason.SessionLock);
                watcher.RaiseSessionSwitch(Microsoft.Win32.SessionSwitchReason.SessionUnlock);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        // The display-state registration itself may report the current state first; drop that before comparing.
        var events = seen.SkipWhile(e => e.StartsWith("DisplayOff")).ToList();
        string got = string.Join(" ", events);
        Check("H-watcher", error == null && handle != IntPtr.Zero
            && got == "Suspended:on Suspended:off DisplayOff:on DisplayOff:off Locked:on Locked:off",
            $"the watcher's hidden window turns real suspend/resume messages, display and lock signals into holds ({got}{error?.Message})");
    }

    // ------------------------------------------------------------------ zoom (F-027 Z1-Z5)

    private static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

    private static void ZoomRules()
    {
        bool clamp = CameraZoom.Clamp(0.5, 4) == 1 && CameraZoom.Clamp(5, 4) == 4 && CameraZoom.Clamp(double.NaN, 4) == 1
            && CameraZoom.Clamp(1.004, 4) == 1 && CameraZoom.Clamp(2.5, 4) == 2.5 && CameraZoom.Clamp(3, 2) == 2
            && CameraZoom.Clamp(2, 0.5) == 1;
        bool crops = CameraZoom.CropFor(1280, 720, 1) == new ZoomCrop(0, 0, 1280, 720)
            && CameraZoom.CropFor(1280, 720, 2) == new ZoomCrop(320, 180, 640, 360)
            && CameraZoom.CropFor(1280, 720, 4) == new ZoomCrop(480, 270, 320, 180)
            && CameraZoom.CropFor(4000, 3000, 3) == new ZoomCrop(1333, 1000, 1333, 1000)
            && CameraZoom.CropFor(1, 1, 4) == new ZoomCrop(0, 0, 1, 1);
        // The same part of the picture at the preview's size and the still's: within one preview pixel.
        var sameRegion = new List<string>();
        foreach (double z in new[] { 1.3, 2.0, 2.7, 4.0 })
        {
            foreach (var (sw, sh) in new[] { (3840, 2160), (1280, 720), (4000, 2250) })
            {
                var p = CameraZoom.CropFor(1280, 720, z);
                var s = CameraZoom.CropFor(sw, sh, z);
                double tol = 1.0 / 1280;
                if (!Near((double)p.X / 1280, (double)s.X / sw, tol) || !Near((double)p.Y / 720, (double)s.Y / sh, 1.0 / 720)
                    || !Near((double)p.Width / 1280, (double)s.Width / sw, tol) || !Near((double)p.Height / 720, (double)s.Height / sh, 1.0 / 720))
                    sameRegion.Add($"{z}x@{sw}x{sh}");
            }
        }
        bool readout = CameraZoom.Readout(1) == string.Empty && CameraZoom.Readout(2) == "2.0×" && CameraZoom.Readout(3.96) == "4.0×";
        bool wheel = Near(CameraZoom.AfterWheel(1, 120, 4), 1.1) && CameraZoom.AfterWheel(1, -120, 4) == 1
            && CameraZoom.AfterWheel(3.9, 240, 4) == 4 && CameraZoom.AfterWheel(CameraZoom.AfterWheel(1, 120, 4), -120, 4) == 1
            && CameraZoom.AfterWheel(1.05, -10, 4) == 1 && CameraZoom.AfterWheel(1.1, -10, 4) > 1.05
            && CameraZoom.AfterWheel(1, 10, 4) > 1;
        var gesture = new ZoomGesture();
        gesture.Begin(1);
        double slow = 1;
        for (int i = 0; i < 50; i++) slow = gesture.Update(1.003, 4);
        gesture.Begin(4);
        double atMax = gesture.Update(1.5, 4);
        double backIn = gesture.Update(0.9, 4);
        bool pinch = slow > 1.1 && atMax == 4 && Near(backIn, 3.6) && ZoomGesture.End(1.03) == 1 && ZoomGesture.End(1.2) == 1.2;
        Check("A-027-21", clamp && crops && sameRegion.Count == 0 && readout && wheel && pinch,
            "zoom stays within 1x..max (4x digital); the crop is the centred 1/zoom, the same region at any image size; "
            + "readout \"2.0×\"; wheel ±10% a notch, small touchpad steps leave 1x and zooming out near 1x snaps to 1x; "
            + "a slow pinch leaves 1x, no dead zone at the limit, ends near 1x snap to 1x"
            + (sameRegion.Count == 0 ? string.Empty : " (region differs: " + string.Join(", ", sameRegion) + ")"));
    }

    private static async Task DigitalZoomSession()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        int changes = 0;
        rig.Controller.ZoomChanged += () => changes++;
        rig.Controller.SetZoom(2.5);
        bool zoomed = rig.Controller.ZoomFactor == 2.5 && rig.Controller.PreviewZoom == 2.5 && !rig.Controller.IsHardwareZoom
            && rig.Controller.ZoomMax == CameraZoom.DigitalMax && changes == 1;
        rig.Controller.SetZoom(9);
        bool capped = rig.Controller.ZoomFactor == 4;
        var shot = await rig.Controller.CaptureAsync(6);
        bool cropped = rig.Cropper.Factors.SequenceEqual(new[] { 4.0 }) && shot.Still is { Width: 1000, Height: 750, Zoom: 4.0, ExifOrientation: 6 };
        rig.Controller.SetZoom(0.3);
        var plain = await rig.Controller.CaptureAsync(1);
        Check("A-027-22a", zoomed && capped && cropped && rig.Controller.ZoomFactor == 1 && plain.Still is { Width: 4000, Zoom: 1.0 }
            && rig.Cropper.Factors.Count == 1,
            "digital zoom: the preview is cropped by the zoom, capped at 4x; the still is cropped to the same centred region "
            + "(4000x3000 at 4x → 1000x750, orientation kept); at 1x nothing is cropped");
    }

    private static async Task ZoomKeptAcrossRebuilds()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        rig.Controller.SetZoom(2);
        var s1 = rig.Current!;
        await rig.Run(2.1);                 // watchdog: no frames for 2 s
        await rig.Run(0.6, frames: true);   // rebuilt
        bool rebuilt = s1.Disposed && rig.Current != s1 && rig.Controller.State == CameraState.Live;
        bool keptAfterStall = rig.Controller.ZoomFactor == 2 && rig.Controller.PreviewZoom == 2;
        rig.Controller.SetHold(CameraHold.Suspended, true);
        bool keptWhilePaused = rig.Controller.ZoomFactor == 2;
        rig.Controller.SetHold(CameraHold.Suspended, false);
        await rig.Run(1.6);
        await rig.Run(0.3, frames: true);
        var shot = await rig.Controller.CaptureAsync(1);

        // The camera's own zoom is set again on the new session.
        var hw = new Rig(Back);
        hw.Backend.ZoomFor = _ => new ZoomCapabilities(true, 1, 3, 0);
        await hw.OpenLive();
        hw.Controller.SetZoom(2.2);
        var h1 = hw.Current!;
        await hw.Run(2.1);
        await hw.Run(0.6, frames: true);
        var h2 = hw.Current!;
        hw.Controller.SetHold(CameraHold.DisplayOff, true);
        hw.Controller.SetHold(CameraHold.DisplayOff, false);
        await hw.Run(1.6);
        await hw.Run(0.3, frames: true);
        var h3 = hw.Current!;
        Check("A-027-23", rebuilt && keptAfterStall && keptWhilePaused && rig.Controller.State == CameraState.Live
            && shot.Still?.Zoom == 2 && rig.Cropper.Factors.SequenceEqual(new[] { 2.0 })
            && h1 != h2 && h2 != h3 && h2.HardwareZooms.LastOrDefault() == 2.2 && h3.HardwareZooms.LastOrDefault() == 2.2
            && hw.Controller.ZoomFactor == 2.2,
            "the zoom survives the watchdog rebuild and sleep/wake: still 2x and the next still cropped; the camera's own "
            + "zoom is set again on each new session");
    }

    private static async Task ZoomResets()
    {
        var hw = new Rig(Back);
        hw.Backend.ZoomFor = _ => new ZoomCapabilities(true, 1, 3, 0);
        await hw.OpenLive();
        hw.Controller.SetZoom(3);
        await hw.Controller.CloseAsync();
        await hw.Controller.OpenAsync();
        await hw.Run(0.3, frames: true);
        bool reopened = hw.Controller.ZoomFactor == 1 && hw.Current!.HardwareZooms.SequenceEqual(new[] { 1.0 });

        var rig = new Rig(Back, Front);
        await rig.OpenLive();
        rig.Controller.SetZoom(3);
        rig.Controller.SwitchCamera();
        bool resetAtOnce = rig.Controller.ZoomFactor == 1;
        await rig.Run(0.3, frames: true);
        var shot = await rig.Controller.CaptureAsync(1);

        var readout = new Rig(Back);
        await readout.OpenLive();
        readout.Controller.SetZoom(2);
        readout.Controller.ResetZoom();
        Check("A-027-24", reopened && resetAtOnce && rig.Controller.CurrentCamera?.Id == Front.Id && rig.Controller.ZoomFactor == 1
            && shot.Still?.Zoom == 1 && rig.Cropper.Factors.Count == 0 && readout.Controller.ZoomFactor == 1
            && readout.Log.Contains("Camera zoom back to 1x."),
            "opening the screen again and switching camera both start at 1x (the camera's own zoom is set back to 1x too); "
            + "ResetZoom goes back to 1x");
    }

    private static async Task CameraOwnZoom()
    {
        var rig = new Rig(Back);
        rig.Backend.ZoomFor = _ => new ZoomCapabilities(true, 1, 3, 0.1);
        await rig.OpenLive();
        var stream = rig.Current!;
        bool hardware = rig.Controller.IsHardwareZoom && rig.Controller.ZoomMax == 3 && stream.HardwareZooms.SequenceEqual(new[] { 1.0 });
        rig.Controller.SetZoom(5);
        bool capped = rig.Controller.ZoomFactor == 3 && rig.Controller.PreviewZoom == 1;
        // A pinch sends many values; while the camera is busy only the newest one is sent next.
        stream.HardwareZooms.Clear();
        var busy = new TaskCompletionSource<bool>();
        stream.PendingZoom = busy;
        rig.Controller.SetZoom(1.5);
        rig.Controller.SetZoom(2.0);
        rig.Controller.SetZoom(2.6);
        bool waited = stream.HardwareZooms.SequenceEqual(new[] { 1.5 });
        busy.SetResult(true);
        var coalesced = stream.HardwareZooms.ToList();
        var shot = await rig.Controller.CaptureAsync(1);
        bool notCropped = rig.Cropper.Factors.Count == 0 && shot.Still is { Width: 4000, Zoom: 2.6 };

        // A camera that refuses its own zoom from the start falls back to the digital zoom.
        var refuse = new Rig(Back);
        refuse.Backend.ZoomFor = _ => new ZoomCapabilities(true, 1, 3, 0);
        refuse.Backend.RefuseZoom = true;
        await refuse.OpenLive();
        refuse.Controller.SetZoom(2);
        var refusedShot = await refuse.Controller.CaptureAsync(1);

        // A camera that has already zoomed and then refuses one change keeps its own zoom (a digital crop on top would
        // multiply the zoom); the next change is sent again.
        var later = new Rig(Back);
        later.Backend.ZoomFor = _ => new ZoomCapabilities(true, 1, 3, 0);
        await later.OpenLive();
        later.Controller.SetZoom(2);
        later.Current!.RefuseZoom = true;
        later.Controller.SetZoom(2.4);
        bool keptOwn = later.Controller.IsHardwareZoom && later.Controller.PreviewZoom == 1;
        later.Current!.RefuseZoom = false;
        later.Controller.SetZoom(2.6);
        var laterShot = await later.Controller.CaptureAsync(1);
        bool retried = keptOwn && later.Current!.HardwareZooms.SequenceEqual(new[] { 1.0, 2.0, 2.4, 2.6 })
            && later.Cropper.Factors.Count == 0 && laterShot.Still is { Width: 4000, Zoom: 2.6 }
            && later.Log.Contains("Camera refused a zoom change; it is sent again with the next change.");
        // A zoom call that never answers (hung driver) is given up after ZoomTimeout; the next change is sent again.
        var hungBackend = new FakeBackend();
        hungBackend.Cameras.Add(Back);
        hungBackend.ZoomFor = _ => new ZoomCapabilities(true, 1, 3, 0);
        var hungLog = new List<string>();
        var hungController = new CameraSessionController(hungBackend, new MemoryPreferences(), () => DateTime.UtcNow,
            new CameraTimings { ZoomTimeout = TimeSpan.FromMilliseconds(100) }, hungLog.Add, work => work(), new FakeCropper());
        await hungController.OpenAsync();
        var hungStream = hungBackend.Streams.Last();
        hungStream.PendingZoom = new TaskCompletionSource<bool>(); // never completes
        hungController.SetZoom(2);
        await Task.Delay(400);
        hungController.SetZoom(2.5);
        bool resent = hungStream.HardwareZooms.SequenceEqual(new[] { 1.0, 2.0, 2.5 }) && hungController.IsHardwareZoom
            && hungLog.Any(l => l.Contains("did not answer"));

        Check("A-027-25", hardware && capped && waited && coalesced.SequenceEqual(new[] { 1.5, 2.6 }) && notCropped && resent
            && !refuse.Controller.IsHardwareZoom && refuse.Controller.PreviewZoom == 2 && refusedShot.Still?.Zoom == 2
            && refuse.Cropper.Factors.SequenceEqual(new[] { 2.0 }) && retried,
            $"a camera with its own zoom is used (1x..its max, preview not cropped, still not cropped); during a pinch only "
            + $"the newest value follows a busy call ({string.Join(", ", coalesced)}); a call that never answers is given up "
            + $"and the next change is sent ({string.Join(", ", hungStream.HardwareZooms)}); a camera refusing from the start "
            + "switches to the digital zoom; one refusal after zooming keeps the camera's own zoom and retries");
    }

    private static async Task CropFailureKeepsPhoto()
    {
        var rig = new Rig(Back);
        await rig.OpenLive();
        rig.Cropper.Throw = true;
        rig.Controller.SetZoom(2);
        var shot = await rig.Controller.CaptureAsync(1);
        await rig.Run(0.5, frames: true);
        Check("A-027-26", shot.Still is { Width: 4000, Height: 3000 } && rig.Controller.State == CameraState.Live
            && rig.Backend.Opened.Count == 1 && rig.Log.Any(l => l.Contains("zoom crop failed")),
            "a crop that fails keeps the full photo and is not treated as a camera failure");
    }

    // ------------------------------------------------------------------ zoom: real JPEG crop and real WPF preview

    /// <summary>A smooth test picture the same at every size: red across, green down, a gentle blue wave.</summary>
    private static byte[] Pattern(int width, int height, bool inverted = false)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            double v = (y + 0.5) / height;
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width;
                int i = (y * width + x) * 4;
                byte r = (byte)Math.Round(255 * u), g = (byte)Math.Round(255 * v);
                byte b = (byte)Math.Round(128 + 90 * Math.Sin(2 * Math.PI * 3 * u) * Math.Cos(2 * Math.PI * 2 * v));
                pixels[i] = inverted ? (byte)(255 - b) : b;
                pixels[i + 1] = inverted ? (byte)(255 - g) : g;
                pixels[i + 2] = inverted ? (byte)(255 - r) : r;
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }

    private static byte[] Jpeg(int width, int height)
    {
        var source = WpfImaging.BitmapSource.Create(width, height, 96, 96, WpfMedia.PixelFormats.Bgr32, null, Pattern(width, height), width * 4);
        var encoder = new WpfImaging.JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(WpfImaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The JPEG decoded and scaled to <paramref name="width"/> x <paramref name="height"/>, as BGRA.</summary>
    private static (byte[] Pixels, int Width, int Height, object? Orientation) Decode(byte[] jpeg, int width, int height)
    {
        var frame = WpfImaging.BitmapDecoder.Create(new MemoryStream(jpeg), WpfImaging.BitmapCreateOptions.PreservePixelFormat,
            WpfImaging.BitmapCacheOption.OnLoad).Frames[0];
        object? orientation = null;
        try { orientation = (frame.Metadata as WpfImaging.BitmapMetadata)?.GetQuery("System.Photo.Orientation"); } catch (Exception) { }
        WpfImaging.BitmapSource scaled = frame.PixelWidth == width && frame.PixelHeight == height ? frame
            : new WpfImaging.TransformedBitmap(frame, new WpfMedia.ScaleTransform((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
        var bgra = new WpfImaging.FormatConvertedBitmap(scaled, WpfMedia.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        return (pixels, frame.PixelWidth, frame.PixelHeight, orientation);
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        int n = 0;
        for (int i = 0; i + 3 < Math.Min(a.Length, b.Length); i += 4, n++)
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return n == 0 ? double.MaxValue : sum / (3.0 * n);
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// A-027-22: the real JPEG crop and the real WPF preview, side by side. A 2560x1440 still and a 1280x720 preview frame
    /// of the same picture; at 2x and 4x the preview shown in a 640x360 image must equal the cropped still scaled to
    /// 640x360. The crop is the crop's own size (never scaled up) and keeps the EXIF orientation.
    /// </summary>
    private static void StillMatchesPreview()
    {
        var lines = new List<string>();
        bool ok = true;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            System.Windows.Window? window = null;
            try
            {
                const int W = 640, H = 360;
                var image = new System.Windows.Controls.Image { Stretch = WpfMedia.Stretch.Uniform };
                WpfMedia.RenderOptions.SetBitmapScalingMode(image, WpfMedia.BitmapScalingMode.LowQuality);
                var host = new System.Windows.Controls.Grid { Width = W, Height = H, Background = WpfMedia.Brushes.Black };
                host.Children.Add(image);
                window = new System.Windows.Window
                {
                    Content = host, SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
                    WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize,
                    ShowInTaskbar = false, ShowActivated = false, Left = -6000, Top = -6000,
                };
                window.Show();
                Pump();

                var renderer = new CameraPreviewRenderer(image);
                var frame = new CameraPreviewFrame(1280, 720, 1280 * 4, Pattern(1280, 720), false);
                renderer.Push(frame);
                Pump();
                byte[] Shot()
                {
                    var target = new WpfImaging.RenderTargetBitmap(W, H, 96, 96, WpfMedia.PixelFormats.Pbgra32);
                    target.Render(host);
                    var pixels = new byte[W * H * 4];
                    target.CopyPixels(pixels, W * 4, 0);
                    return pixels;
                }

                byte[] still = Jpeg(2560, 1440);
                var cropper = new JpegStillCropper();
                var fullStill = Decode(still, W, H);
                foreach (double zoom in new[] { 2.0, 4.0 })
                {
                    renderer.SetZoom(zoom);
                    Pump();
                    byte[] preview = Shot();
                    var cropped = cropper.CropAsync(new CapturedStill(still, 2560, 1440, "photo", 6), zoom).GetAwaiter().GetResult();
                    var decoded = Decode(cropped.Jpeg, W, H);
                    double match = MeanDifference(preview, decoded.Pixels);
                    double control = MeanDifference(preview, fullStill.Pixels);
                    double expectedRed = 255 * (0.5 - 0.5 / zoom);
                    bool corner = Math.Abs(preview[2] - expectedRed) <= 6 && Math.Abs(decoded.Pixels[2] - expectedRed) <= 6;
                    var size = CameraZoom.CropFor(2560, 1440, zoom);
                    bool sized = cropped.Width == size.Width && cropped.Height == size.Height && decoded.Width == size.Width
                        && decoded.Height == size.Height && cropped.Zoom == zoom;
                    bool oriented = decoded.Orientation is ushort o && o == 6 && cropped.ExifOrientation == 6;
                    bool pass = match <= 5 && control >= 20 && corner && sized && oriented;
                    ok &= pass;
                    lines.Add($"{zoom}x: still {cropped.Width}x{cropped.Height}, difference to preview {match:0.0} (unzoomed {control:0.0}), "
                        + $"left edge red {preview[2]}/{decoded.Pixels[2]} (expected {expectedRed:0}), orientation {decoded.Orientation}");
                }

                // Frames written after zooming show at once in the cropped preview.
                renderer.SetZoom(2);
                Pump();
                byte[] before = Shot();
                renderer.Push(new CameraPreviewFrame(1280, 720, 1280 * 4, Pattern(1280, 720, inverted: true), false));
                Pump();
                double changed = MeanDifference(before, Shot());
                renderer.SetZoom(1);
                Pump();
                bool whole = Math.Abs(Shot()[2] - 255 * (1 - 0.5 / W)) <= 6; // inverted red at the left edge ≈ 255
                ok &= changed > 50 && whole;
                lines.Add($"new frame while zoomed changes the preview by {changed:0} levels; back at 1x the whole frame shows ({whole})");

                // A still at the preview's own size is cropped, never scaled up.
                var small = cropper.CropAsync(new CapturedStill(Jpeg(1280, 720), 1280, 720, "frame", 1), 4).GetAwaiter().GetResult();
                ok &= small.Width == 320 && small.Height == 180;
                lines.Add($"1280x720 still at 4x → {small.Width}x{small.Height}");
            }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Check("A-027-22", ok && error == null,
            "at 2x and 4x the cropped still shows exactly what the zoomed preview shows: " + string.Join("; ", lines) + (error == null ? string.Empty : " " + error));
    }
}
