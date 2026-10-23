using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RedCamera.Core
{
    /// <summary>F-027 C11: where a camera sits. External cameras have no enclosure location.</summary>
    public enum CameraPanelKind { Unknown, Front, Back, External }

    /// <summary>One colour camera RED can open (F-027 C11: infrared sensors are never listed).</summary>
    public sealed record CameraInfo(string Id, string Name, CameraPanelKind Panel, int MountRotationClockwise);

    /// <summary>F-027 C3: signals that make RED let go of the camera without closing the capture screen.</summary>
    public enum CameraHold { Minimized, Suspended, DisplayOff, Locked }

    public enum CameraState { Closed, Starting, Live, Restarting, Paused, GaveUp }

    /// <summary>F-027 C8: why RED gave up, which picks the message.</summary>
    public enum CameraFailureKind { None, AccessDenied, NoCamera, InUse, Blocked, NotResponding }

    /// <summary>A preview frame, BGRA8, already scaled for the screen. The pixel buffer is reused by the backend.</summary>
    public sealed record CameraPreviewFrame(int Width, int Height, int Stride, byte[] Pixels, bool AllBlack);

    /// <summary>A still as captured (before F-023 processing). Width/Height are the stored pixel size.</summary>
    public sealed record CapturedStill(byte[] Jpeg, int Width, int Height, string Source, int ExifOrientation)
    {
        /// <summary>F-027 Z2: the zoom the photo was taken at (1 = none). A digital zoom is already cropped into the photo.</summary>
        public double Zoom { get; init; } = 1.0;

        /// <summary>F-027 L1 (1.3.0): true when a photo taken upright was cut to its landscape band.</summary>
        public bool Landscaped { get; init; }
    }

    public interface ICameraBackend
    {
        Task<IReadOnlyList<CameraInfo>> FindCamerasAsync();

        /// <summary>Opens a new capture session (F-027 C4). Throws with the HRESULT in <see cref="Exception.HResult"/>.</summary>
        Task<ICameraStream> OpenAsync(CameraInfo camera, CancellationToken cancellationToken);
    }

    public interface ICameraStream : IAsyncDisposable
    {
        CameraInfo Camera { get; }

        /// <summary>True when only read-only shared access was available (another app holds the camera).</summary>
        bool Shared { get; }

        /// <summary>Raised on a worker thread for every preview frame.</summary>
        event Action<CameraPreviewFrame>? FrameArrived;

        /// <summary>Raised on any thread when the capture object reports a failure (F-027 W4).</summary>
        event Action<int, string>? Failed;

        Task<CapturedStill> CaptureStillAsync(int exifOrientation, CancellationToken cancellationToken);

        /// <summary>Dev builds only: stops the preview frames underneath the controller (simulated stall, A-027-16).</summary>
        Task DebugStopFramesAsync();

        /// <summary>F-027 Z1: the camera's own zoom, or <see cref="ZoomCapabilities.DigitalOnly"/> (then the engine crops
        /// the preview and the still). Read once the session is open.</summary>
        ZoomCapabilities ZoomCapabilities => ZoomCapabilities.DigitalOnly;

        /// <summary>F-027 Z1: sets the camera's own zoom; called only when <see cref="ZoomCapabilities"/> is hardware.
        /// False when the camera refused it (the engine then zooms digitally).</summary>
        Task<bool> SetHardwareZoomAsync(double factor) => Task.FromResult(false);

        /// <summary>F-027 L2 (1.3.0): the size of the stills this session takes, as chosen when it opened; (0, 0) when not
        /// known (the engine then takes the preview's shape).</summary>
        (int Width, int Height) PhotoSize => (0, 0);

        /// <summary>F-027 T1: what the camera can do with a tap (read once the session is open).</summary>
        FocusCapabilities FocusCapabilities => FocusCapabilities.None;

        /// <summary>F-027 T1: meter focus, exposure and white balance on <paramref name="region"/> and focus once there.
        /// False when the camera did none of it.</summary>
        Task<bool> FocusOnRegionAsync(FocusRegion region) => Task.FromResult(false);

        /// <summary>F-027 T3: clear the region and go back to continuous auto-focus and auto-exposure.</summary>
        Task<bool> ReleaseFocusAsync() => Task.FromResult(false);
    }

    /// <summary>The remembered camera (F-027 C11).</summary>
    public interface ICameraPreferences
    {
        string? DeviceId { get; }
        CameraPanelKind? Panel { get; }
        void Remember(CameraInfo camera);
    }

    /// <summary>F-027 timings; the harness shortens nothing, it drives a fake clock.</summary>
    public sealed class CameraTimings
    {
        public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(1.5);
        public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(2);
        public TimeSpan BlackTimeout { get; init; } = TimeSpan.FromSeconds(3);
        public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan CaptureTimeout { get; init; } = TimeSpan.FromSeconds(8);
        /// <summary>F-027 Z1: how long one call to the camera's own zoom may take before RED stops waiting for it.</summary>
        public TimeSpan ZoomTimeout { get; init; } = TimeSpan.FromSeconds(3);
        /// <summary>F-027 T3: how long a tap's focus and exposure are held before the camera goes back to its own.</summary>
        public TimeSpan FocusHold { get; init; } = TimeSpan.FromSeconds(10);
        /// <summary>F-027 T1: how long one focus call may take before RED stops waiting for it.</summary>
        public TimeSpan FocusTimeout { get; init; } = TimeSpan.FromSeconds(3);
        public TimeSpan[] SameCameraRetryDelays { get; init; } =
            { TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3) };
        public TimeSpan OtherCameraDelay { get; init; } = TimeSpan.FromSeconds(0.5);
        public TimeSpan HealthyReset { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan RecoveryWindow { get; init; } = TimeSpan.FromMinutes(5);
        public int MaxRecoveriesInWindow { get; init; } = 6;
    }

    /// <summary>Result of a shutter press (F-027 C6).</summary>
    public sealed record CaptureOutcome(CapturedStill? Still, string? Message)
    {
        public static CaptureOutcome Taken(CapturedStill still) => new(still, null);
        public static CaptureOutcome NotTaken(string message) => new(null, message);
        public static readonly CaptureOutcome Ignored = new(null, null);
    }
}
