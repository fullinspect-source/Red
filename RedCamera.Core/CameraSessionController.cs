using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 capture session: owns the camera only while the capture screen is open and not held (C3), opens sessions
    /// (C4), watches the preview frames and rebuilds a dead session with bounded retries (C5), gates the shutter (C6),
    /// remembers the camera choice (C11) and keeps the zoom (Z1-Z3). It holds no WinRT types so
    /// tests\RedCameraCoreHarness can drive it with a fake camera, clock and cropper.
    /// Threading: every public member except the frame path is called on one thread (the capture window's UI thread),
    /// and <see cref="TickAsync"/> runs about every 250 ms. Frames and failures arrive on worker threads; they only
    /// record state here, and the next tick acts on it.
    /// </summary>
    public sealed class CameraSessionController
    {
        public const string StartingText = "Starting camera…";
        public const string RestartingText = "Restarting camera…";
        public const string PausedText = "Camera paused";
        public const string NotLivePressText = "Camera is restarting. Tap again in a moment.";
        public const string PausedPressText = "Camera paused. It starts again when RED is back on screen.";
        public const string CaptureFailedText = "The photo was not taken. Tap again in a moment.";

        /// <summary>RED-internal code a backend reports when Windows blocks the stream for privacy (switch or shutter).</summary>
        public const int BlockedForPrivacyCode = unchecked((int)0xA0F70001);
        /// <summary>RED-internal code for "the attempt took too long" (open limit, first frame, stall).</summary>
        public const int TimeoutCode = unchecked((int)0xA0F70002);

        private readonly ICameraBackend _backend;
        private readonly ICameraPreferences _preferences;
        private readonly Func<DateTime> _clock;
        private readonly CameraTimings _timings;
        private readonly Action<string> _log;
        private readonly Func<Func<Task>, Task> _runDetached;
        private readonly ICameraStillCropper _cropper;

        private readonly HashSet<CameraHold> _holds = new();
        private readonly List<DateTime> _recoveries = new();
        private IReadOnlyList<CameraInfo> _cameras = Array.Empty<CameraInfo>();
        private Queue<(CameraInfo Camera, TimeSpan Delay)>? _retryQueue;
        private bool _open;
        private bool _tickBusy;
        private bool _capturing;

        // Current or pending session. _generation changes whenever a session is released or abandoned, so late opens,
        // frames and failures from an older session are ignored.
        private int _generation;
        private ICameraStream? _stream;
        private CameraInfo? _current;
        private bool _opening;
        private DateTime _openStartedAt;
        private DateTime _streamOpenedAt;
        private DateTime _liveSince;
        private long _lastFrameTicks;
        private long _lastNonBlackTicks;
        private int _firstFrameSeen;
        private PendingFailure? _pendingFailure;
        private Task _lastDispose = Task.CompletedTask;

        private DateTime? _scheduledAt;
        private CameraInfo? _scheduledCamera; // null = fresh start (enumerate, remembered camera)
        private bool _scheduledRecovering;

        // F-027 Z1-Z3: the zoom asked for. It outlives a rebuilt session (watchdog, sleep/wake) and is applied again to the
        // new one; opening the screen and switching camera set it back to 1x. The camera's own zoom is used when the
        // session has one, otherwise the preview and the still are cropped.
        private double _zoom = 1.0;
        private ZoomCapabilities _zoomCaps = ZoomCapabilities.DigitalOnly;
        private ICameraStream? _zoomAppliedTo;
        private double _zoomApplied;
        private bool _zoomPushing;

        private sealed record PendingFailure(int Generation, int HResult, string Reason);

        public CameraSessionController(ICameraBackend backend, ICameraPreferences preferences, Func<DateTime> clock,
            CameraTimings? timings = null, Action<string>? log = null, Func<Func<Task>, Task>? runDetached = null,
            ICameraStillCropper? cropper = null)
        {
            _backend = backend;
            _preferences = preferences;
            _clock = clock;
            _timings = timings ?? new CameraTimings();
            _log = log ?? (_ => { });
            _runDetached = runDetached ?? (work => Task.Run(work));
            _cropper = cropper ?? new JpegStillCropper();
        }

        public CameraState State { get; private set; } = CameraState.Closed;
        public CameraFailureKind FailureKind { get; private set; }
        public int LastHResult { get; private set; }
        public CameraInfo? CurrentCamera => _current;
        public IReadOnlyList<CameraInfo> Cameras => _cameras;
        public bool IsShared => _stream?.Shared == true;
        public bool IsCapturing => _capturing;
        public IReadOnlyCollection<CameraHold> Holds => _holds;

        /// <summary>Raised on the controller's thread when <see cref="State"/> or the camera changes.</summary>
        public event Action? StateChanged;

        /// <summary>Raised on a worker thread for each preview frame of the current session.</summary>
        public event Action<CameraPreviewFrame>? PreviewFrame;

        /// <summary>F-027 Z1: the zoom factor (1 = none).</summary>
        public double ZoomFactor => _zoom;

        /// <summary>F-027 Z1: always 1x.</summary>
        public double ZoomMin => 1.0;

        /// <summary>F-027 Z1: the camera's own maximum, or <see cref="CameraZoom.DigitalMax"/> for a digital zoom.</summary>
        public double ZoomMax => _zoomCaps.Hardware ? _zoomCaps.Max : CameraZoom.DigitalMax;

        /// <summary>F-027 Z1: true when the open session zooms in the camera itself.</summary>
        public bool IsHardwareZoom => _zoomCaps.Hardware;

        /// <summary>F-027 Z2: what the screen crops the preview by (<see cref="CameraPreviewRenderer.SetZoom"/>): the zoom
        /// when it is digital, 1 when the camera zooms its own frames.</summary>
        public double PreviewZoom => _zoomCaps.Hardware ? 1.0 : _zoom;

        /// <summary>Raised on the controller's thread when <see cref="ZoomFactor"/>, <see cref="ZoomMax"/> or
        /// <see cref="PreviewZoom"/> may have changed.</summary>
        public event Action? ZoomChanged;

        public string StatusText => State switch
        {
            CameraState.Starting => StartingText,
            CameraState.Restarting => RestartingText,
            CameraState.Paused => PausedText,
            CameraState.GaveUp => FailureTitle(FailureKind),
            _ => string.Empty,
        };

        public static string FailureTitle(CameraFailureKind kind) => kind switch
        {
            CameraFailureKind.AccessDenied => "Windows is blocking camera access for desktop apps.",
            CameraFailureKind.NoCamera => "No camera was found on this device.",
            CameraFailureKind.InUse => "Another app is using the camera. Close it and tap Try again.",
            CameraFailureKind.Blocked => "The camera is turned off by a privacy switch or shutter. Open it and tap Try again.",
            _ => "The camera is not responding.",
        };

        public static string FailureDetail(CameraFailureKind kind) => kind switch
        {
            CameraFailureKind.AccessDenied =>
                "Open camera privacy settings and turn on \"Let desktop apps access your camera\", then tap Try again.",
            CameraFailureKind.NotResponding =>
                "Windows lost the camera, usually after the tablet slept, and its driver did not come back. "
                + "Tap Try again. If that fails, use the Windows Camera app this time or tap Reset camera. "
                + "If Reset does not help, restart the tablet.",
            _ => string.Empty,
        };

        /// <summary>F-027 C8 causes.</summary>
        public static CameraFailureKind Classify(int hresult) => unchecked((uint)hresult) switch
        {
            0x80070005 => CameraFailureKind.AccessDenied,
            0xC00DABE1 or 0xC00D3704 or 0x80070020 or 0xA00F4243 => CameraFailureKind.InUse,
            0xA0F70001 => CameraFailureKind.Blocked,
            _ => CameraFailureKind.NotResponding,
        };

        /// <summary>F-027 C11 order: remembered id, remembered panel, rear camera, first camera.</summary>
        public static CameraInfo? PickPreferred(IReadOnlyList<CameraInfo> cameras, string? deviceId, CameraPanelKind? panel)
        {
            if (cameras.Count == 0) return null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                var byId = cameras.FirstOrDefault(c => string.Equals(c.Id, deviceId, StringComparison.OrdinalIgnoreCase));
                if (byId != null) return byId;
            }
            if (panel is { } p && p != CameraPanelKind.Unknown)
            {
                var byPanel = cameras.FirstOrDefault(c => c.Panel == p);
                if (byPanel != null) return byPanel;
            }
            return cameras.FirstOrDefault(c => c.Panel == CameraPanelKind.Back) ?? cameras[0];
        }

        // ------------------------------------------------------------------ open, close, holds

        /// <summary>The capture screen opened (F-027 C3): start unless something holds the camera.</summary>
        public async Task OpenAsync()
        {
            if (_open) return;
            _open = true;
            _recoveries.Clear();
            _log("Camera screen opened.");
            SetZoomFactor(1.0); // Z3: every opening starts at 1x
            if (_holds.Count > 0) { SetState(CameraState.Paused); return; }
            await BeginFreshAsync(recovering: false);
        }

        /// <summary>The capture screen closed (F-027 C12): release the camera.</summary>
        public Task CloseAsync()
        {
            if (!_open) return _lastDispose;
            _open = false;
            ClearSchedule();
            _retryQueue = null;
            ReleaseStream("screen closed");
            SetState(CameraState.Closed);
            return _lastDispose;
        }

        /// <summary>F-027 C3 H1-H4. Any hold releases the camera; when every hold clears, a new session starts after the
        /// settle delay.</summary>
        public void SetHold(CameraHold hold, bool on)
        {
            bool changed = on ? _holds.Add(hold) : _holds.Remove(hold);
            if (!changed) return;
            _log($"Camera hold {hold} {(on ? "on" : "off")}.");
            if (!_open) return;
            if (on)
            {
                if (_holds.Count == 1)
                {
                    ClearSchedule();
                    _retryQueue = null;
                    ReleaseStream($"hold {hold}");
                    SetState(CameraState.Paused);
                }
                return;
            }
            if (_holds.Count == 0)
            {
                _recoveries.Clear();
                Schedule(null, _clock() + _timings.SettleDelay, recovering: false);
                SetState(CameraState.Paused); // stays "paused" through the settle delay
            }
        }

        /// <summary>F-027 C8 Try again: fresh enumeration and open, counters reset.</summary>
        public async Task RetryAsync()
        {
            if (!_open) return;
            _recoveries.Clear();
            _retryQueue = null;
            ClearSchedule();
            ReleaseStream("try again");
            if (_holds.Count > 0) { SetState(CameraState.Paused); return; }
            await BeginFreshAsync(recovering: false);
        }

        /// <summary>F-027 C11: the next camera in the list, remembered at once.</summary>
        public void SwitchCamera()
        {
            if (!_open || _cameras.Count < 2) return;
            int index = _current == null ? -1 : IndexOf(_current);
            var next = _cameras[(index + 1) % _cameras.Count];
            _preferences.Remember(next);
            _log($"Camera switched to {Describe(next)}.");
            _retryQueue = null;
            _recoveries.Clear();
            ClearSchedule();
            ReleaseStream("switch camera");
            _current = next;
            SetZoomFactor(1.0); // Z3: another camera starts at 1x
            if (_holds.Count > 0) { SetState(CameraState.Paused); return; }
            StartAttempt(next, recovering: false);
        }

        // ------------------------------------------------------------------ zoom (F-027 Z1-Z3)

        /// <summary>Zooms to <paramref name="factor"/>, kept within <see cref="ZoomMin"/>..<see cref="ZoomMax"/>. Cheap
        /// enough to call for every step of a pinch: the camera's own zoom gets only the newest value.</summary>
        public void SetZoom(double factor) => SetZoomFactor(CameraZoom.Clamp(factor, ZoomMax));

        /// <summary>Back to 1x (double-tap or the readout).</summary>
        public void ResetZoom()
        {
            if (_zoom != 1.0) _log("Camera zoom back to 1x.");
            SetZoomFactor(1.0);
        }

        private void SetZoomFactor(double zoom)
        {
            if (zoom == _zoom) return;
            _zoom = zoom;
            ZoomChanged?.Invoke();
            if (_zoomCaps.Hardware) _ = PushHardwareZoomAsync();
        }

        /// <summary>A new session: read what it can zoom and apply the kept zoom to it (Z3).</summary>
        private void ApplyZoomToSession(ICameraStream stream)
        {
            ZoomCapabilities caps;
            try { caps = stream.ZoomCapabilities ?? ZoomCapabilities.DigitalOnly; }
            catch (Exception) { caps = ZoomCapabilities.DigitalOnly; }
            _zoomCaps = caps;
            _zoomAppliedTo = null; // the camera may still hold an older zoom: always set it
            _zoom = CameraZoom.Clamp(_zoom, ZoomMax);
            if (_zoom > 1.0) _log($"Camera zoom {_zoom:0.0}x applied again ({(caps.Hardware ? "camera" : "digital")}).");
            ZoomChanged?.Invoke();
            if (caps.Hardware) _ = PushHardwareZoomAsync();
        }

        /// <summary>Sends the newest zoom to the camera, one call at a time, until the camera has the current value.</summary>
        private async Task PushHardwareZoomAsync()
        {
            if (_zoomPushing) return;
            _zoomPushing = true;
            try
            {
                while (true)
                {
                    var stream = _stream;
                    if (stream == null || !_zoomCaps.Hardware) return;
                    double target = _zoom;
                    if (ReferenceEquals(stream, _zoomAppliedTo) && target == _zoomApplied) return;
                    bool ok;
                    try
                    {
                        var call = stream.SetHardwareZoomAsync(target);
                        if (await Task.WhenAny(call, Task.Delay(_timings.ZoomTimeout)) != call)
                        {
                            // A hung driver must not block every later zoom: stop waiting; the next change sends again.
                            _ = call.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                            _log("Camera zoom did not answer; it is sent again with the next change.");
                            return;
                        }
                        ok = await call;
                    }
                    catch (Exception) { ok = false; }
                    if (!ReferenceEquals(stream, _stream)) continue; // released meanwhile: serve the new session, if any
                    if (!ok && ReferenceEquals(stream, _zoomAppliedTo))
                    {
                        // The camera already zooms by itself; cropping on top would multiply the zoom. Try again later.
                        _log("Camera refused a zoom change; it is sent again with the next change.");
                        return;
                    }
                    if (!ok)
                    {
                        _log("Camera refused its own zoom; zooming digitally instead.");
                        _zoomCaps = ZoomCapabilities.DigitalOnly;
                        _zoom = CameraZoom.Clamp(_zoom, ZoomMax);
                        ZoomChanged?.Invoke();
                        return;
                    }
                    _zoomAppliedTo = stream;
                    _zoomApplied = target;
                }
            }
            finally
            {
                _zoomPushing = false;
            }
        }

        // ------------------------------------------------------------------ frames and failures (any thread)

        private void OnFrame(int generation, CameraPreviewFrame frame)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            long now = _clock().Ticks;
            Interlocked.Exchange(ref _lastFrameTicks, now);
            if (!frame.AllBlack) Interlocked.Exchange(ref _lastNonBlackTicks, now);
            else Interlocked.CompareExchange(ref _lastNonBlackTicks, now, 0); // start of a black run
            Interlocked.Exchange(ref _firstFrameSeen, 1);
            try { PreviewFrame?.Invoke(frame); }
            catch (Exception ex) { _log($"Camera preview handler failed: {ex.GetType().Name}"); }
        }

        private void OnStreamFailed(int generation, int hresult, string reason)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            Interlocked.Exchange(ref _pendingFailure, new PendingFailure(generation, hresult, reason));
        }

        // ------------------------------------------------------------------ tick: watchdog and scheduled work

        /// <summary>Runs the watchdog (F-027 W1-W4), the open limit (C4) and any scheduled attempt.</summary>
        public async Task TickAsync()
        {
            if (_tickBusy || !_open) return;
            _tickBusy = true;
            try
            {
                DateTime now = _clock();

                var failure = Interlocked.Exchange(ref _pendingFailure, null);
                if (failure != null && failure.Generation == _generation && _stream != null)
                {
                    HandleFailure(failure.HResult, $"camera reported failure: {failure.Reason}");
                    return;
                }

                if (_opening && now - _openStartedAt > _timings.OpenTimeout)
                {
                    _opening = false;
                    Interlocked.Increment(ref _generation); // the late result is disposed when it arrives
                    HandleFailure(TimeoutCode, $"opening took longer than {_timings.OpenTimeout.TotalSeconds:0} s");
                    return;
                }

                if (_stream != null)
                {
                    bool first = Volatile.Read(ref _firstFrameSeen) == 1;
                    if (!first)
                    {
                        if (now - _streamOpenedAt > _timings.FirstFrameTimeout)
                        {
                            HandleFailure(TimeoutCode, $"no first frame within {_timings.FirstFrameTimeout.TotalSeconds:0} s");
                            return;
                        }
                    }
                    else
                    {
                        var lastFrame = new DateTime(Interlocked.Read(ref _lastFrameTicks));
                        var lastNonBlack = new DateTime(Interlocked.Read(ref _lastNonBlackTicks));
                        if (now - lastFrame > _timings.StallTimeout)
                        {
                            HandleFailure(TimeoutCode, $"no frames for {_timings.StallTimeout.TotalSeconds:0} s");
                            return;
                        }
                        if (now - lastNonBlack > _timings.BlackTimeout)
                        {
                            HandleFailure(TimeoutCode, $"only black frames for {_timings.BlackTimeout.TotalSeconds:0} s");
                            return;
                        }
                        if (State != CameraState.Live)
                        {
                            _liveSince = now;
                            _log($"Camera live: {Describe(_current)}{(IsShared ? " (shared)" : string.Empty)}.");
                            SetState(CameraState.Live);
                        }
                        else if (_retryQueue != null && now - _liveSince >= _timings.HealthyReset)
                        {
                            _retryQueue = null; // F-027 C5: healthy for 30 s resets the attempts
                        }
                    }
                }

                if (_scheduledAt is { } at && now >= at && _holds.Count == 0)
                {
                    var camera = _scheduledCamera;
                    bool recovering = _scheduledRecovering;
                    ClearSchedule();
                    if (camera == null) await BeginFreshAsync(recovering);
                    else await RunScheduledAttemptAsync(camera, recovering);
                }
            }
            finally
            {
                _tickBusy = false;
            }
        }

        // ------------------------------------------------------------------ shutter

        /// <summary>F-027 C6: one press, one still, only while live; never queued.</summary>
        public async Task<CaptureOutcome> CaptureAsync(int exifOrientation)
        {
            if (_capturing) return CaptureOutcome.Ignored;
            if (State != CameraState.Live || _stream == null)
            {
                return CaptureOutcome.NotTaken(State switch
                {
                    CameraState.Paused => PausedPressText,
                    CameraState.GaveUp => FailureTitle(FailureKind),
                    CameraState.Closed => PausedPressText,
                    _ => NotLivePressText,
                });
            }

            _capturing = true;
            var stream = _stream;
            int generation = _generation;
            double zoom = _zoom; // Z2: the zoom shown at the press
            bool digital = zoom > 1.0 && !_zoomCaps.Hardware;
            try
            {
                using var cts = new CancellationTokenSource(_timings.CaptureTimeout);
                var capture = stream.CaptureStillAsync(exifOrientation, cts.Token);
                var finished = await Task.WhenAny(capture, Task.Delay(_timings.CaptureTimeout));
                if (finished != capture)
                {
                    _ = capture.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    if (generation == _generation) HandleFailure(TimeoutCode, "still capture timed out");
                    return CaptureOutcome.NotTaken(CaptureFailedText);
                }
                var still = await capture;
                if (digital) still = await CropStillAsync(still, zoom);
                else if (zoom > 1.0) still = still with { Zoom = zoom };
                string zoomNote = still.Zoom > 1.0 ? $", zoom {still.Zoom:0.0}x {(digital ? "digital" : "camera")}" : string.Empty;
                _log($"Camera still {still.Width}x{still.Height} via {still.Source}, orientation {still.ExifOrientation}{zoomNote}.");
                return CaptureOutcome.Taken(still);
            }
            catch (Exception ex)
            {
                _log($"Camera still failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                if (generation == _generation && _stream != null) HandleFailure(ex.HResult, "still capture failed");
                return CaptureOutcome.NotTaken(CaptureFailedText);
            }
            finally
            {
                _capturing = false;
            }
        }

        /// <summary>
        /// One still (as <see cref="CaptureAsync"/>) written as captured, full size, to <paramref name="path"/> with
        /// <see cref="CameraFiles.SaveAtomic"/>: staged outside the folder and moved in, so a folder watcher sees one
        /// complete file as a Created event. <c>Still</c> is null and <c>Message</c> says why when no photo was taken or it
        /// could not be written; a press while a capture is running is ignored (both null).
        /// </summary>
        public async Task<(CaptureOutcome Outcome, string? Path)> CaptureToFileAsync(string path, int exifOrientation,
            string? stagingFolder = null, bool createFolder = true)
        {
            var outcome = await CaptureAsync(exifOrientation);
            if (outcome.Still == null) return (outcome, null);
            try
            {
                byte[] jpeg = outcome.Still.Jpeg;
                await Task.Run(() => CameraFiles.SaveAtomic(jpeg, path, stagingFolder, createFolder));
                return (outcome, path);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                _log($"Camera still not written ({ex.GetType().Name}).");
                return (CaptureOutcome.NotTaken("The photo was taken but could not be saved."), null);
            }
        }

        /// <summary>Z2: the still cropped to the preview's region. A crop that fails keeps the full photo: it was taken, and
        /// the camera is fine, so this never counts as a camera failure.</summary>
        private async Task<CapturedStill> CropStillAsync(CapturedStill still, double zoom)
        {
            try
            {
                var crop = _cropper.CropAsync(still, zoom);
                var finished = await Task.WhenAny(crop, Task.Delay(_timings.CaptureTimeout));
                if (finished == crop) return await crop;
                _ = crop.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                _log("Camera zoom crop took too long; the full photo is kept.");
            }
            catch (Exception ex)
            {
                _log($"Camera zoom crop failed ({ex.GetType().Name}); the full photo is kept.");
            }
            return still;
        }

        /// <summary>Test and dev tools only (A-027-16): stop the real preview frames underneath the watchdog.</summary>
        public Task DebugStallAsync() => _stream?.DebugStopFramesAsync() ?? Task.CompletedTask;

        // ------------------------------------------------------------------ internals

        private async Task BeginFreshAsync(bool recovering)
        {
            SetState(recovering ? CameraState.Restarting : CameraState.Starting);
            var cameras = await FindCamerasSafeAsync();
            if (!_open || _holds.Count > 0) return;
            if (cameras == null)
            {
                LastHResult = TimeoutCode;
                GiveUp(CameraFailureKind.NotResponding, "camera list did not load");
                return;
            }
            _cameras = cameras;
            var pick = PickPreferred(cameras, _preferences.DeviceId, _preferences.Panel);
            if (pick == null)
            {
                GiveUp(CameraFailureKind.NoCamera, "no camera found");
                return;
            }
            _current = pick;
            StartAttempt(pick, recovering);
        }

        private async Task RunScheduledAttemptAsync(CameraInfo queued, bool recovering)
        {
            // The camera may have been re-enumerated (F-027 C3): resolve the queued one again by id, then by panel and name.
            var cameras = await FindCamerasSafeAsync();
            if (!_open || _holds.Count > 0) return;
            CameraInfo target = queued;
            if (cameras is { Count: > 0 })
            {
                _cameras = cameras;
                target = cameras.FirstOrDefault(c => string.Equals(c.Id, queued.Id, StringComparison.OrdinalIgnoreCase))
                    ?? cameras.FirstOrDefault(c => c.Panel == queued.Panel && c.Name == queued.Name)
                    ?? queued;
            }
            _current = target;
            StartAttempt(target, recovering);
        }

        private async Task<IReadOnlyList<CameraInfo>?> FindCamerasSafeAsync()
        {
            try
            {
                var find = _backend.FindCamerasAsync();
                var finished = await Task.WhenAny(find, Task.Delay(TimeSpan.FromSeconds(5)));
                if (finished != find) { _log("Camera list timed out."); return null; }
                return await find;
            }
            catch (Exception ex)
            {
                _log($"Camera list failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                return null;
            }
        }

        private void StartAttempt(CameraInfo camera, bool recovering)
        {
            _ = StartAttemptAsync(camera, recovering);
        }

        private async Task StartAttemptAsync(CameraInfo camera, bool recovering)
        {
            int generation = Interlocked.Increment(ref _generation);
            _current = camera;
            _opening = true;
            _openStartedAt = _clock();
            ResetFrameStats();
            SetState(recovering ? CameraState.Restarting : CameraState.Starting);
            _log($"Camera opening {Describe(camera)}{(recovering ? " (recovery)" : string.Empty)}.");

            ICameraStream? stream = null;
            Exception? error = null;
            try
            {
                await Task.WhenAny(_lastDispose, Task.Delay(TimeSpan.FromSeconds(3)));
                if (generation != Volatile.Read(ref _generation)) return;
                stream = await _backend.OpenAsync(camera, CancellationToken.None);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (generation != Volatile.Read(ref _generation) || !_open || _holds.Count > 0)
            {
                if (stream != null)
                {
                    _log("Camera open finished after it was abandoned; disposing it.");
                    DisposeDetached(stream);
                }
                return;
            }
            _opening = false;
            if (error != null || stream == null)
            {
                int hr = error?.HResult ?? TimeoutCode;
                _log($"Camera open failed: {error?.GetType().Name} 0x{hr:X8}");
                HandleFailure(hr, "open failed");
                return;
            }

            _stream = stream;
            _streamOpenedAt = _clock();
            ResetFrameStats();
            stream.FrameArrived += frame => OnFrame(generation, frame);
            stream.Failed += (hr, reason) => OnStreamFailed(generation, hr, reason);
            _log($"Camera opened {Describe(camera)}{(stream.Shared ? " in shared mode" : string.Empty)}.");
            ApplyZoomToSession(stream);
            StateChanged?.Invoke();
        }

        private void HandleFailure(int hresult, string reason)
        {
            var kind = Classify(hresult);
            LastHResult = hresult;
            var failedCamera = _current;
            _log($"Camera failure ({kind}, 0x{hresult:X8}): {reason}.");
            ReleaseStream(reason);
            if (!_open || _holds.Count > 0) return;

            if (kind is CameraFailureKind.AccessDenied or CameraFailureKind.Blocked)
            {
                GiveUp(kind, "no retry for this cause");
                return;
            }

            if (_retryQueue == null)
            {
                // A new incident (F-027 C5): 3 more attempts on this camera, then each other camera once.
                DateTime now = _clock();
                _recoveries.Add(now);
                _recoveries.RemoveAll(t => now - t > _timings.RecoveryWindow);
                if (_recoveries.Count > _timings.MaxRecoveriesInWindow)
                {
                    GiveUp(kind, $"more than {_timings.MaxRecoveriesInWindow} recoveries in {_timings.RecoveryWindow.TotalMinutes:0} minutes");
                    return;
                }
                _retryQueue = new Queue<(CameraInfo, TimeSpan)>();
                if (failedCamera != null)
                {
                    foreach (var delay in _timings.SameCameraRetryDelays) _retryQueue.Enqueue((failedCamera, delay));
                }
                foreach (var other in _cameras.Where(c => failedCamera == null || !string.Equals(c.Id, failedCamera.Id, StringComparison.OrdinalIgnoreCase)))
                    _retryQueue.Enqueue((other, _timings.OtherCameraDelay));
            }

            if (_retryQueue.Count == 0)
            {
                GiveUp(kind, "attempts used up");
                return;
            }
            var (camera, wait) = _retryQueue.Dequeue();
            Schedule(camera, _clock() + wait, recovering: true);
            SetState(CameraState.Restarting);
        }

        private void GiveUp(CameraFailureKind kind, string reason)
        {
            ReleaseStream("gave up");
            ClearSchedule();
            _retryQueue = null;
            FailureKind = kind;
            _log($"Camera gave up ({kind}): {reason}.");
            SetState(CameraState.GaveUp);
        }

        private void ReleaseStream(string reason)
        {
            Interlocked.Increment(ref _generation);
            _opening = false;
            Interlocked.Exchange(ref _pendingFailure, null);
            var stream = _stream;
            _stream = null;
            ResetFrameStats();
            if (stream != null)
            {
                _log($"Camera released ({reason}).");
                DisposeDetached(stream);
            }
        }

        private void DisposeDetached(ICameraStream stream)
        {
            // Never wait on a hung driver on the UI thread (F-027 C5).
            _lastDispose = _runDetached(async () =>
            {
                try { await stream.DisposeAsync(); }
                catch (Exception ex) { _log($"Camera dispose failed: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
            });
        }

        private void ResetFrameStats()
        {
            Interlocked.Exchange(ref _firstFrameSeen, 0);
            Interlocked.Exchange(ref _lastFrameTicks, 0);
            Interlocked.Exchange(ref _lastNonBlackTicks, 0);
        }

        private void Schedule(CameraInfo? camera, DateTime at, bool recovering)
        {
            _scheduledAt = at;
            _scheduledCamera = camera;
            _scheduledRecovering = recovering;
        }

        private void ClearSchedule()
        {
            _scheduledAt = null;
            _scheduledCamera = null;
            _scheduledRecovering = false;
        }

        private void SetState(CameraState state)
        {
            if (state != CameraState.GaveUp) FailureKind = CameraFailureKind.None;
            State = state;
            StateChanged?.Invoke();
        }

        private int IndexOf(CameraInfo camera)
        {
            for (int i = 0; i < _cameras.Count; i++)
                if (string.Equals(_cameras[i].Id, camera.Id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string Describe(CameraInfo? camera) => camera == null ? "(none)" : $"'{camera.Name}' ({camera.Panel})";
    }
}
