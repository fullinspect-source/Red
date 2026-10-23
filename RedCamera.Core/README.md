# RedCamera.Core

RED's reusable camera engine (program feature F-027). RED (`InspectionEditor.csproj`) and the RED Camera companion app both
use it, so a fix lands in both. Spec: `ios-port\program\features\F-027-reliable-camera\SPEC.md` (§3 C3-C11, O1-O3, Z1-Z5,
§6).

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
- zoom (Z1-Z3): the factor, the camera's own zoom or a digital crop of the preview and the still, and the gesture rules;
- tap to focus and expose (T1-T3), camera cycling with skip-on-failure (C11), the shutter-key gate (K1);
- landscape photos (L1): the band a photo taken upright keeps, and the guide's rectangle;
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

### Zoom (1.1.0)

```csharp
controller.ZoomChanged += () =>                                       // controller thread (the UI thread)
{
    renderer.SetZoom(controller.PreviewZoom);                          // digital: the centred crop; camera's own: 1
    readout.Text = CameraZoom.Readout(controller.ZoomFactor);          // "2.0×", empty at 1x
};
// Pinch (WPF manipulation on the preview area only, Mode = Scale):
//   ManipulationStarting: gesture.Begin(controller.ZoomFactor)
//   ManipulationDelta:    controller.SetZoom(gesture.Update(scaleDelta, controller.ZoomMax))   (complete if IsInertial)
//   ManipulationCompleted: controller.SetZoom(ZoomGesture.End(controller.ZoomFactor))
// Mouse wheel (a touchpad pinch arrives as Ctrl+wheel):
//   controller.SetZoom(CameraZoom.AfterWheel(controller.ZoomFactor, e.Delta, controller.ZoomMax))
// Double-tap, double-click or a tap on the readout: controller.ResetZoom()
```

### Tap to focus, camera cycling, shutter keys (1.2.0)

```csharp
// A single tap or click on the preview (not part of a pinch, not the second tap of a double-tap):
if (controller.State == CameraState.Live
    && CameraFocus.TryMapTap(previewImage, previewSurface, pointOnSurface, rotation.Angle, mirror.ScaleX < 0,
        controller.PreviewZoom, out double x, out double y))
{
    controller.TapToFocus(x, y);       // false = this camera can't; show the mark anyway
    ShowFocusMark(pointOnSurface);     // a crosshair that fades in about 1.5 s; never takes input
}
// The 10 s hold and its release run in TickAsync. A new session simply ends the hold.

controller.SwitchedTo += camera => ShowNote(camera.Name);  // after Switch, once the camera is live
controller.Notice += ShowNote;                             // "USB Camera did not start. Trying Front Camera."
await controller.SwitchCameraAsync();                      // every camera Windows lists, skipping ones that fail

var keys = new ShutterKeyGate();                           // PreviewKeyDown: Space, Enter, VolumeUp
if (keys.Press(e.IsRepeat, e.Timestamp)) await TakePhotoAsync();   // WM_APPCOMMAND volume up: keys.Pulse(tick)
```

### Landscape photos (1.3.0)

```csharp
controller.AlwaysLandscape = settings.AlwaysLandscape;     // RED: on unless the user switched it off
// Framing guide, when on and the picture is shown upright:
var shown = previewImage.TransformToAncestor(area).TransformBounds(new Rect(previewImage.RenderSize));
if (CameraLandscape.Band(shown) is { } kept) { /* shade shown minus kept; outline kept */ }
```

`CaptureAsync` and `CaptureToFileAsync` crop a digital zoom's still to the region the preview shows, at the crop's own
size. The zoom is kept through a rebuilt session and sleep/wake, and is 1x after `OpenAsync` and `SwitchCamera`. RED's
`CameraCaptureWindow` is the reference screen (its taps are timed by the touch events' timestamps).

## API summary

**`CameraSessionController`**
- Methods:
  - `OpenAsync`, `CloseAsync`, `SetHold(CameraHold, bool)`, `TickAsync`;
  - `CaptureAsync(exif)`, `CaptureToFileAsync(path, exif)`;
  - `SwitchCamera`, `RetryAsync`, `DebugStallAsync` (tests only);
  - `SetZoom(factor)`, `ResetZoom()` (1.1.0);
  - `TapToFocus(x, y)`, `SwitchCameraAsync()` (1.2.0).
- Properties: `State`, `StatusText`, `FailureKind`, `LastHResult`, `CurrentCamera`, `Cameras`, `IsShared`, `Holds`,
  `IsCapturing`; zoom (1.1.0): `ZoomFactor`, `ZoomMin`, `ZoomMax`, `IsHardwareZoom`, `PreviewZoom`.
- Events: `StateChanged`, `PreviewFrame`, `ZoomChanged` (1.1.0), `SwitchedTo` and `Notice` (1.2.0).
- Properties (1.2.0): `FocusCapabilities`, `IsFocusHeld`.
- Constructor: an optional `ICameraStillCropper` (1.1.0; default `JpegStillCropper`), for tests.
- Static: `FailureTitle`, `FailureDetail`, `Classify(hresult)`, `PickPreferred`, and the status texts.

**Backend:** `WinRtCameraBackend : ICameraBackend`, with `IsSupported()` and `GetInstanceIdAsync(camera)`. Its
sessions are `ICameraStream`s. `ICameraStream.ZoomCapabilities` and `SetHardwareZoomAsync` (1.1.0) have default
implementations (digital only), so other implementations still compile.

**Zoom (1.1.0):** `CameraZoom` (`DigitalMax` 4, `Clamp`, `CropFor`, `Readout`, `AfterWheel`), `ZoomGesture` (one pinch),
`ZoomCapabilities`, `ZoomCrop`, `ICameraStillCropper` and `JpegStillCropper`. `CapturedStill.Zoom` is the zoom a photo
was taken at.

**Holds:** `CameraHoldWatcher`, with `Attach`, `TrackWindow`, `Handle`, `Raise*` (to simulate signals) and `Dispose`.

**Preview and orientation:**
- `CameraPreviewRenderer`, with `SetZoom(factor)` (1.1.0) for the digital crop;
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
  - real FileSystemWatchers set up like INSPECT2026 and Inspect2022, which must each see a saved still once, as Created;
  - zoom (A-027-21..26): the rules, the controller with a fake cropper, and the real JPEG crop against the real WPF
    preview in a window;
  - tap focus, cycling and keys (A-027-29..33): the mapping, also against WPF's own transforms in a window; the hold;
    the fallback; skip-on-failure; the key gate.
- `tests\CameraLiveCheck`: the real camera on this PC (A-027-16, A-027-28 zoom). Not in the automatic suite.

## Changes

- 1.3.0 (2026-10-22, owner: landscape photos), added API only:
  - `CameraSessionController.AlwaysLandscape` (off by default): a photo shown portrait is cut to its centred landscape
    band. It is one cut together with any digital zoom, in stored pixels, with the EXIF orientation kept;
  - `CameraLandscape` (`RegionFor`, `Upright`, `QuarterTurn`, `Band` for the on-screen guide) and
    `CapturedStill.Landscaped`;
  - `ICameraStillCropper.CropToAsync(still, region)` (a default that fails, so older croppers still compile; the
    engine then keeps the zoomed photo); `JpegStillCropper` implements it.

- 1.2.0 (2026-10-22, owner camera update, released with RED 2.3.8), added API only:
  - tap to focus and expose (F-027 T1-T3): `CameraFocus` (`TryMapTap`, `TapToFrame`, `DisplayToSource`,
    `SourceToFrame`, `RegionAround`), `FocusCapabilities`, `FocusRegion`; controller `TapToFocus(x, y)`,
    `FocusCapabilities`, `IsFocusHeld`; `CameraTimings.FocusHold` (10 s) and `FocusTimeout` (3 s). The WinRT
    stream sets an unlocked region of interest (focus, exposure, white balance as supported) and a single focus, and
    clears them for continuous focus. `ICameraStream` gained `FocusCapabilities`, `FocusOnRegionAsync` and
    `ReleaseFocusAsync`, with defaults;
  - camera cycling (C11): `SwitchCameraAsync` enumerates again and goes through every camera; a camera is remembered
    once it is live; one that does not start is skipped (`Notice`), and the screen shows the new camera's name
    (`SwitchedTo`). The WinRT camera list now also includes video devices without a frame source group, still
    without infrared or depth sensors;
  - `ShutterKeyGate` (K1): one photo per key press, with no auto-repeat and a 400 ms debounce.

- 1.1.1 (2026-10-21, released with RED 2.3.7): fixes from the zoom review, no API removed:
  - a call to the camera's own zoom that does not answer within `CameraTimings.ZoomTimeout` (new, 3 s) is given up, so
    a hung driver no longer blocks every later zoom change;
  - a camera that refuses one change after it has already zoomed keeps its own zoom (no digital crop on top) and gets
    the next change; only a camera that never accepted a zoom falls back to the digital zoom;
  - `CameraZoom.AfterWheel`: zooming out to below 1.05x gives exactly 1x (touchpad pinches send many small steps).
- 1.1.0 (2026-10-21): zoom (F-027 Z1-Z3), added API only:
  - the controller's zoom (`SetZoom`, `ResetZoom`, `ZoomFactor`, `ZoomMax`, `PreviewZoom`, `IsHardwareZoom`,
    `ZoomChanged`); kept through a rebuilt session and sleep/wake, 1x on open and on switching camera;
  - the camera's own `ZoomControl` when it zooms by a factor from 1x; otherwise a digital zoom to 4x that crops the
    preview (`CameraPreviewRenderer.SetZoom`) and the still to the same centred region, never scaled up;
  - `CameraZoom`, `ZoomGesture`, `ZoomCapabilities`, `ZoomCrop`, `ICameraStillCropper`, `JpegStillCropper`,
    `CapturedStill.Zoom`;
  - `ICameraStream` gained `ZoomCapabilities` and `SetHardwareZoomAsync`, with defaults;
  - the controller's constructor gained an optional `cropper` (source-compatible; recompile against 1.1.0).
- 1.0.0 (2026-10-20): released with RED 2.3.2; the same code as 1.0.0-preview.2.
- 1.0.0-preview.2 (2026-10-20):
  - camera-roll rules follow INSPECT's IL (the first root with a match wins, by last-write time);
  - `ResolveInspect2022CameraRoll` added;
  - `CameraRollFolderForSaving` returns null and never creates a folder;
  - stills are staged outside the folder and moved in (a Created event, which Inspect2022 needs);
  - `createFolder` and `stagingFolder` parameters added.
- 1.0.0-preview.1 (2026-10-20): first cut, the engine moved out of RED.
