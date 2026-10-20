# RedCamera.Core

RED's reusable camera engine (program feature F-027). RED (`InspectionEditor.csproj`) and the RED Camera companion app both
use it, so a fix lands in both. Spec: `ios-port\program\features\F-027-reliable-camera\SPEC.md` (§3 C3-C11, O1-O3, §6).

## Boundary

**In the library:**
- the WinRT `MediaCapture` session lifecycle (C4);
- releasing the camera on suspend, display off, lock or minimize, and a fresh session after wake (C3);
- the frame watchdog and bounded recovery (C5);
- camera enumeration and the remembered choice (C11);
- full-resolution stills to memory or a file (C7);
- hung-driver detection and the failure texts (C8);
- the explicit, elevated device reset (C10);
- orientation (O1-O3);
- INSPECT 2026's camera-roll location (F-023 T3);
- a WPF preview renderer.

**Not in the library** (stays in RED):
- the capture screen;
- adding a photo to an item, and F-023 photo processing;
- RED's settings file and camera mode;
- the dev-build camera-roll redirect;
- RedAutoReport (iPad only).

The library references no RED code and no other project (`tests\CameraCaptureHarness` checks this).

## Target and dependencies

`net8.0-windows10.0.19041.0`, `UseWPF`. No NuGet dependencies. The WinRT projections come with the Windows TFM.

## Versioning

- `<Version>` in `RedCamera.Core.csproj` is the library's own version, separate from RED's.
- Raise it for every change a consumer must pick up: patch for fixes, minor for added API, major for breaking API.
- Build the package from the committed RED tree with:
  `dotnet pack RedCamera.Core\RedCamera.Core.csproj -c Release -o <feed>`

## Threading rules (important)

- Use `CameraSessionController` on one thread, the UI thread. Call `TickAsync()` about every 250 ms (a
  `DispatcherTimer`).
- `PreviewFrame` fires on a worker thread. Pass it to `CameraPreviewRenderer.Push`.
- `WinRtCameraBackend` keeps every WinRT object on thread-pool (MTA) threads. A `MediaCapture` created on a WPF (STA)
  thread and released from the pool crashed the process with an AccessViolation. Do not call WinRT capture APIs from
  the UI thread yourself.
- Create, `Attach()` and `Dispose()` the `CameraHoldWatcher` on the UI thread.

## Minimal use

```csharp
var controller = new CameraSessionController(new WinRtCameraBackend(log), new JsonCameraPreferences(prefsPath, log),
    () => DateTime.UtcNow, log: log);
var renderer = new CameraPreviewRenderer(previewImage, log);          // a WPF Image
controller.PreviewFrame += renderer.Push;
controller.StateChanged += UpdateUi;                                   // State, StatusText, FailureKind
var holds = new CameraHoldWatcher(Dispatcher, controller.SetHold, log);
holds.Attach();                                                        // suspend, display off, lock
holds.TrackWindow(window);                                             // minimize (window or its owner)
var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
timer.Tick += async (_, _) => await controller.TickAsync();
timer.Start();
await controller.OpenAsync();

var orientation = new CameraOrientationProvider();
string? roll = CameraFiles.CameraRollFolderForSaving();               // the folder INSPECT2026 watches now; null = none
if (roll != null)
{
    string path = Path.Combine(roll, $"RED_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");
    var (outcome, written) = await controller.CaptureToFileAsync(path,
        orientation.StillExifOrientation(controller.CurrentCamera), stagingFolder: null, createFolder: false);
    // outcome.Still == null → show outcome.Message (not live, paused, or given up); written == null → not saved
}

// on close:
timer.Stop(); holds.Dispose(); controller.PreviewFrame -= renderer.Push; await controller.CloseAsync();
```

## API summary

**`CameraSessionController`**
- Methods:
  - `OpenAsync`, `CloseAsync`, `SetHold(CameraHold, bool)`, `TickAsync`;
  - `CaptureAsync(exif)`, `CaptureToFileAsync(path, exif)`;
  - `SwitchCamera`, `RetryAsync`, `DebugStallAsync` (tests only).
- Properties: `State`, `StatusText`, `FailureKind`, `LastHResult`, `CurrentCamera`, `Cameras`, `IsShared`, `Holds`,
  `IsCapturing`.
- Events: `StateChanged`, `PreviewFrame`.
- Static: `FailureTitle`, `FailureDetail`, `Classify(hresult)`, `PickPreferred`, and the status texts.

**Backend:** `WinRtCameraBackend : ICameraBackend`, with `IsSupported()` and `GetInstanceIdAsync(camera)`. Its
sessions are `ICameraStream`s.

**Holds:** `CameraHoldWatcher`, with `Attach`, `TrackWindow`, `Handle`, `Raise*` (to simulate signals) and `Dispose`.

**Preview and orientation:**
- `CameraPreviewRenderer`;
- `CameraOrientationProvider`, with `Preview(camera)` and `StillExifOrientation(camera)`;
- `CameraOrientation` (the pure rules).

**Files:** `CameraFiles`.
- `SaveAtomic(bytes, path, stagingFolder, createFolder)` and `SaveCopy(jpeg, folder, now, prefix, stagingFolder,
  createFolder)`:
  - the bytes are written outside the destination folder on the same volume (the given staging folder, else the
    destination's parent) and moved in, so the file appears complete, as one Created event;
  - a file is never replaced, and with `createFolder: false` a folder is never created.
- `ResolveCameraRoll` and `CandidateRoots`: INSPECT2026's exact rule from its IL. The first root with a `Camera Roll*`
  folder wins, its newest by last-write time; else the first existing root; else null.
- `ResolveInspect2022CameraRoll`: `Pictures\Camera Roll`, else `Pictures`, else null.
- `CameraRollFolderForSaving`: INSPECT2026's folder, or null. It never creates one.

Sources: `ios-port\program\research\R-red-camera-companion.md` §3-§5, and F-023 SPEC I2/T3 (corrected 2026-10-20).

**Preferences:** `ICameraPreferences` and `JsonCameraPreferences`.

**Device reset (explicit only):** `CameraDeviceReset`, `ICameraResetRunner`, `ElevatedCameraResetRunner` and
`CameraResetFlow`.

**Models:**
- `CameraInfo`, `CameraPanelKind`, `CameraHold`, `CameraState`, `CameraFailureKind`;
- `CameraPreviewFrame`, `CapturedStill`, `CaptureOutcome`, `CameraTimings`;
- `CameraFrameProbe`.

## Tests

- `tests\RedCameraCoreHarness`: references only this library. It covers:
  - a fake camera and clock (A-027-02..14, -17, -19);
  - files, preferences and the hold watcher;
  - INSPECT's two folder rules;
  - real FileSystemWatchers set up like INSPECT2026 and Inspect2022, which must each see a saved still once, as Created.

## Changes

- 1.0.0 (2026-10-20): released with RED 2.3.2; the same code as 1.0.0-preview.2.
- 1.0.0-preview.2 (2026-10-20):
  - camera-roll rules follow INSPECT's IL (the first root with a match wins, by last-write time);
  - `ResolveInspect2022CameraRoll` added;
  - `CameraRollFolderForSaving` returns null and never creates a folder;
  - stills are staged outside the folder and moved in (a Created event, which Inspect2022 needs);
  - `createFolder` and `stagingFolder` parameters added.
- 1.0.0-preview.1 (2026-10-20): first cut, the engine moved out of RED.
- `tests\CameraLiveCheck`: the real camera on this PC (A-027-16). Not in the automatic suite.
