// RedCamera.Core harness (F-027): drives the camera engine with a fake camera and a fake clock (ACCEPTANCE A-027-02..14,
// -17, -19, the W3 probe) and checks its file, preference and hold-signal helpers. It references only RedCamera.Core, so any
// app that uses the engine can run it. Exit 0 = all passed.
using System.IO;
using System.Runtime.InteropServices;
using RedCamera.Core;

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
        public FakeStream(CameraInfo camera, int number) { Camera = camera; Number = number; }
        public CameraInfo Camera { get; }
        public int Number { get; }
        public bool Shared => false;
        public bool Disposed { get; private set; }
        public int CaptureCalls { get; private set; }
        public TaskCompletionSource<CapturedStill>? PendingCapture { get; set; }
        public event Action<CameraPreviewFrame>? FrameArrived;
        public event Action<int, string>? Failed;

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
                    var stream = new FakeStream(camera, Streams.Count + 1);
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

    private sealed class Rig
    {
        public DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        public FakeBackend Backend { get; } = new();
        public MemoryPreferences Prefs { get; } = new();
        public CameraSessionController Controller { get; }
        public List<string> Log { get; } = new();

        public Rig(params CameraInfo[] cameras)
        {
            Backend.Cameras.AddRange(cameras);
            Controller = new CameraSessionController(Backend, Prefs, () => Now, log: Log.Add, runDetached: work => work());
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
}
