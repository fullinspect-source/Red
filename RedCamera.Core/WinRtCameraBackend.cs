using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 C4/C7 on Windows: WinRT <c>MediaCapture</c> with a <c>MediaFrameReader</c> preview.
    /// Every WinRT camera object is created, used and released on thread-pool (MTA) threads. A MediaCapture made on the
    /// UI (STA) thread and released from the thread pool crashed the process with an AccessViolation inside the WinRT
    /// event unsubscription (seen in the F-027 live check, 2026-10-19), so no call here runs on the caller's thread.
    /// </summary>
    public sealed class WinRtCameraBackend : ICameraBackend
    {
        private const string InstanceIdProperty = "System.Devices.DeviceInstanceId";
        private readonly Action<string> _log;

        public WinRtCameraBackend(Action<string>? log = null)
        {
            _log = log ?? (_ => { });
        }

        /// <summary>False when the WinRT capture API cannot be used on this Windows (F-027 F2).</summary>
        public static bool IsSupported()
        {
            try { return Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Capture.Frames.MediaFrameReader"); }
            catch (Exception) { return false; }
        }

        public Task<IReadOnlyList<CameraInfo>> FindCamerasAsync() => Task.Run(FindCamerasCoreAsync);

        private static async Task<IReadOnlyList<CameraInfo>> FindCamerasCoreAsync()
        {
            var cameras = new List<CameraInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sensorsOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Colour sources only (F-027 C11: infrared and depth sensors are never offered).
            foreach (var group in await MediaFrameSourceGroup.FindAllAsync())
            {
                foreach (var info in group.SourceInfos)
                {
                    var device = info.DeviceInformation;
                    if (device == null) continue;
                    bool colour = info.SourceKind == MediaFrameSourceKind.Color
                        && (info.MediaStreamType == MediaStreamType.VideoPreview || info.MediaStreamType == MediaStreamType.VideoRecord);
                    if (!colour) { sensorsOnly.Add(device.Id); continue; }
                    if (seen.Add(device.Id)) cameras.Add(ToCameraInfo(device));
                }
            }
            // Every other camera Windows lists (C11 cycling: some USB and phone cameras show no frame source group), except
            // the infrared and depth sensors found above.
            foreach (var device in await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture))
            {
                if (device.IsEnabled && !sensorsOnly.Contains(device.Id) && seen.Add(device.Id)) cameras.Add(ToCameraInfo(device));
            }
            return cameras;
        }

        private static CameraInfo ToCameraInfo(DeviceInformation device)
        {
            var location = device.EnclosureLocation;
            var panel = location == null
                ? CameraPanelKind.External
                : location.Panel switch
                {
                    Windows.Devices.Enumeration.Panel.Front => CameraPanelKind.Front,
                    Windows.Devices.Enumeration.Panel.Back => CameraPanelKind.Back,
                    _ => CameraPanelKind.Unknown,
                };
            int mount = location == null ? 0 : (int)location.RotationAngleInDegreesClockwise;
            return new CameraInfo(device.Id, device.Name, panel, mount);
        }

        /// <summary>The PnP device instance id behind a camera (F-027 C10), or null.</summary>
        public static Task<string?> GetInstanceIdAsync(CameraInfo camera) => Task.Run(async () =>
        {
            try
            {
                var device = await DeviceInformation.CreateFromIdAsync(camera.Id, new[] { InstanceIdProperty });
                return device.Properties.TryGetValue(InstanceIdProperty, out object? value) ? value as string : null;
            }
            catch (Exception)
            {
                return null;
            }
        });

        public Task<ICameraStream> OpenAsync(CameraInfo camera, CancellationToken cancellationToken) =>
            Task.Run(() => OpenCoreAsync(camera, cancellationToken), cancellationToken);

        private async Task<ICameraStream> OpenCoreAsync(CameraInfo camera, CancellationToken cancellationToken)
        {
            var capture = new MediaCapture();
            bool shared = false;
            try
            {
                await capture.InitializeAsync(Settings(camera, MediaCaptureSharingMode.ExclusiveControl)).AsTask(cancellationToken);
            }
            catch (Exception ex) when (CameraSessionController.Classify(ex.HResult) == CameraFailureKind.InUse)
            {
                // F-027 C4: another app holds the camera; read-only shared access still gives a preview and frames.
                _log($"Camera exclusive open refused (0x{ex.HResult:X8}); trying shared.");
                capture.Dispose();
                capture = new MediaCapture();
                shared = true;
                try { await capture.InitializeAsync(Settings(camera, MediaCaptureSharingMode.SharedReadOnly)).AsTask(cancellationToken); }
                catch { capture.Dispose(); throw; }
            }
            catch
            {
                capture.Dispose();
                throw;
            }

            var stream = new WinRtCameraStream(camera, capture, shared, _log);
            try
            {
                await stream.StartAsync();
                return stream;
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }

        private static MediaCaptureInitializationSettings Settings(CameraInfo camera, MediaCaptureSharingMode sharing) => new()
        {
            VideoDeviceId = camera.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = sharing,
        };
    }

    internal sealed class WinRtCameraStream : ICameraStream
    {
        private const uint PreviewMaxLong = 1280;
        private const uint PreviewMaxShort = 720;
        // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED: the device was removed under the session.
        private const int DeviceInvalidated = unchecked((int)0xC00DABE0);

        private readonly MediaCapture _capture;
        private readonly Action<string> _log;
        private readonly byte[][] _ring = new byte[3][];
        private MediaFrameSource? _source;
        private MediaFrameReader? _reader;
        private ZoomControl? _zoomControl;
        private int _ringIndex;
        private int _disposed;
        private int _frameErrorLogged;

        public WinRtCameraStream(CameraInfo camera, MediaCapture capture, bool shared, Action<string> log)
        {
            Camera = camera;
            _capture = capture;
            Shared = shared;
            _log = log;
        }

        public CameraInfo Camera { get; }
        public bool Shared { get; }
        public ZoomCapabilities ZoomCapabilities { get; private set; } = ZoomCapabilities.DigitalOnly;
        public FocusCapabilities FocusCapabilities { get; private set; } = FocusCapabilities.None;
        public event Action<CameraPreviewFrame>? FrameArrived;
        public event Action<int, string>? Failed;

        public async Task StartAsync()
        {
            _capture.Failed += OnCaptureFailed;
            _capture.CameraStreamStateChanged += OnCameraStreamStateChanged;

            var sources = _capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
            _source = sources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                ?? sources.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                ?? throw new InvalidOperationException("The camera has no colour video stream.");

            if (!Shared) await ChooseStillSizeAsync(_source);

            var format = _source.CurrentFormat?.VideoFormat;
            var size = PreviewSize(format?.Width ?? 1280, format?.Height ?? 720);
            _reader = await _capture.CreateFrameReaderAsync(_source, MediaEncodingSubtypes.Bgra8, size);
            _reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            _reader.FrameArrived += OnFrameArrived;
            var status = await _reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                int hr = status switch
                {
                    MediaFrameReaderStartStatus.ExclusiveControlNotAvailable => unchecked((int)0xC00DABE1),
                    MediaFrameReaderStartStatus.DeviceNotAvailable => DeviceInvalidated,
                    _ => CameraSessionController.TimeoutCode,
                };
                throw new CameraStartException($"Preview did not start: {status}", hr);
            }
            _log($"Camera stream {format?.Width}x{format?.Height} {_source.CurrentFormat?.Subtype}, preview {size.Width}x{size.Height}, "
                + $"characteristic {_capture.MediaCaptureSettings.VideoDeviceCharacteristic}.");
            if (!Shared) ZoomCapabilities = ReadZoomCapabilities();
            _log(ZoomCapabilities.Hardware
                ? $"Camera zoom: the camera's own, {ZoomCapabilities.Min:0.##}-{ZoomCapabilities.Max:0.##}x (step {ZoomCapabilities.Step:0.###})."
                : $"Camera zoom: digital, 1-{CameraZoom.DigitalMax:0}x.");
            if (!Shared) FocusCapabilities = ReadFocusCapabilities();
            var f = FocusCapabilities;
            _log(f.Supported
                ? $"Camera tap focus: regions {f.Regions} (focus {f.Focus}, exposure {f.Exposure}, white balance {f.WhiteBalance}), "
                  + $"single focus {f.SingleFocus}, continuous focus {f.ContinuousFocus}."
                : $"Camera tap focus: not supported, no regions (single focus {f.SingleFocus}); a tap only shows the mark.");
        }

        /// <summary>F-027 T1: regions of interest (focus, exposure, white balance) and single/continuous focus.</summary>
        private FocusCapabilities ReadFocusCapabilities()
        {
            try
            {
                var controller = _capture.VideoDeviceController;
                var roi = controller.RegionsOfInterestControl;
                bool regions = roi.MaxRegions > 0 && (roi.AutoFocusSupported || roi.AutoExposureSupported);
                var focus = controller.FocusControl;
                bool single = focus.Supported && focus.SupportedFocusModes.Contains(FocusMode.Single);
                bool continuous = focus.Supported && focus.SupportedFocusModes.Contains(FocusMode.Continuous);
                return new FocusCapabilities(regions, regions && roi.AutoFocusSupported, regions && roi.AutoExposureSupported,
                    regions && roi.AutoWhiteBalanceSupported, single, continuous);
            }
            catch (Exception ex)
            {
                _log($"Camera focus controls not read: {ex.GetType().Name} 0x{ex.HResult:X8}");
                return FocusCapabilities.None;
            }
        }

        private AutoFocusRange FocusRange()
        {
            var ranges = _capture.VideoDeviceController.FocusControl.SupportedFocusRanges;
            return ranges.Contains(AutoFocusRange.FullRange) || ranges.Count == 0 ? AutoFocusRange.FullRange : ranges[0];
        }

        public Task<bool> FocusOnRegionAsync(FocusRegion region) => Task.Run(async () =>
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            var caps = FocusCapabilities;
            var controller = _capture.VideoDeviceController;
            bool done = false;
            if (caps.Regions)
            {
                try
                {
                    var roi = new RegionOfInterest
                    {
                        AutoFocusEnabled = caps.Focus,
                        AutoExposureEnabled = caps.Exposure,
                        AutoWhiteBalanceEnabled = caps.WhiteBalance,
                        Bounds = new Rect(region.X, region.Y, region.Width, region.Height),
                        BoundsNormalized = true,
                        Type = RegionOfInterestType.Unknown,
                        Weight = 100,
                    };
                    // Not locked: the camera keeps metering this region while it is set (the hold), and many webcams
                    // refuse a lock (this PC's: exposure regions yes, AutoExposureLock no, 2026-10-22).
                    await controller.RegionsOfInterestControl.SetRegionsAsync(new[] { roi }, false);
                    done = true;
                }
                catch (Exception ex)
                {
                    _log($"Camera focus region not set: {ex.GetType().Name} 0x{ex.HResult:X8}");
                }
            }
            if (caps.FocusOnTap && done)
            {
                try
                {
                    controller.FocusControl.Configure(new FocusSettings { Mode = FocusMode.Single, AutoFocusRange = FocusRange() });
                    await controller.FocusControl.FocusAsync();
                    done = true;
                }
                catch (Exception ex)
                {
                    _log($"Camera single focus failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                }
            }
            return done;
        });

        public Task<bool> ReleaseFocusAsync() => Task.Run(async () =>
        {
            if (Volatile.Read(ref _disposed) == 1) return false;
            var caps = FocusCapabilities;
            var controller = _capture.VideoDeviceController;
            bool done = false;
            if (caps.Regions)
            {
                try { await controller.RegionsOfInterestControl.ClearRegionsAsync(); done = true; }
                catch (Exception ex) { _log($"Camera focus region not cleared: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
            }
            if (caps.FocusOnTap && caps.ContinuousFocus) // only a tap's single focus changed the focus mode
            {
                try
                {
                    controller.FocusControl.Configure(new FocusSettings { Mode = FocusMode.Continuous, AutoFocusRange = FocusRange() });
                    await controller.FocusControl.FocusAsync();
                    done = true;
                }
                catch (Exception ex)
                {
                    _log($"Camera continuous focus not restored: {ex.GetType().Name} 0x{ex.HResult:X8}");
                }
            }
            return done;
        });

        /// <summary>F-027 Z1: the camera's ZoomControl, used only when it zooms by a factor starting at 1x.</summary>
        private ZoomCapabilities ReadZoomCapabilities()
        {
            try
            {
                var zoom = _capture.VideoDeviceController.ZoomControl;
                if (zoom.Supported && zoom.Min >= 0.99f && zoom.Min <= 1.01f && zoom.Max >= zoom.Min + 0.1f)
                {
                    _zoomControl = zoom;
                    return new ZoomCapabilities(true, 1.0, zoom.Max, Math.Max(0f, zoom.Step));
                }
                if (zoom.Supported) _log($"Camera zoom control not used (range {zoom.Min}-{zoom.Max}).");
            }
            catch (Exception ex)
            {
                _log($"Camera zoom control not read: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
            return ZoomCapabilities.DigitalOnly;
        }

        public Task<bool> SetHardwareZoomAsync(double factor) => Task.Run(() =>
        {
            var zoom = _zoomControl;
            if (zoom == null || Volatile.Read(ref _disposed) == 1) return false;
            try
            {
                var caps = ZoomCapabilities;
                double value = factor;
                if (caps.Step > 0) value = caps.Min + Math.Round((value - caps.Min) / caps.Step) * caps.Step;
                value = Math.Clamp(value, caps.Min, caps.Max);
                zoom.Configure(new ZoomSettings { Mode = ZoomTransitionMode.Auto, Value = (float)value });
                return true;
            }
            catch (Exception ex)
            {
                _log($"Camera zoom not set: {ex.GetType().Name} 0x{ex.HResult:X8}");
                return false;
            }
        });

        /// <summary>F-027 C7: the largest still the camera offers.</summary>
        private async Task ChooseStillSizeAsync(MediaFrameSource source)
        {
            try
            {
                var characteristic = _capture.MediaCaptureSettings.VideoDeviceCharacteristic;
                var photoFrom = characteristic switch
                {
                    VideoDeviceCharacteristic.AllStreamsIdentical => source.Info.MediaStreamType,
                    VideoDeviceCharacteristic.PreviewPhotoStreamsIdentical => MediaStreamType.VideoPreview,
                    VideoDeviceCharacteristic.RecordPhotoStreamsIdentical => MediaStreamType.VideoRecord,
                    _ => MediaStreamType.Photo,
                };
                if (photoFrom == source.Info.MediaStreamType)
                {
                    // Photos come from the stream we preview: use its largest format with at least 15 fps.
                    var best = source.SupportedFormats
                        .Where(f => f.VideoFormat != null && f.VideoFormat.Width > 0)
                        .OrderByDescending(f => Fps(f) >= 15 ? 1 : 0)
                        .ThenByDescending(f => (long)f.VideoFormat.Width * f.VideoFormat.Height)
                        .ThenByDescending(Fps)
                        .FirstOrDefault();
                    if (best != null) await source.SetFormatAsync(best);
                    return;
                }
                var controller = _capture.VideoDeviceController;
                var largest = controller.GetAvailableMediaStreamProperties(photoFrom)
                    .Select(p => (Props: p, Pixels: Pixels(p)))
                    .OrderByDescending(p => p.Pixels)
                    .FirstOrDefault();
                if (largest.Props != null) await controller.SetMediaStreamPropertiesAsync(photoFrom, largest.Props);
            }
            catch (Exception ex)
            {
                _log($"Camera still size not set: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
        }

        private static double Fps(MediaFrameFormat format) =>
            format.FrameRate == null || format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

        private static long Pixels(IMediaEncodingProperties properties) => properties switch
        {
            ImageEncodingProperties image => (long)image.Width * image.Height,
            VideoEncodingProperties video => (long)video.Width * video.Height,
            _ => 0,
        };

        internal static BitmapSize PreviewSize(uint width, uint height)
        {
            if (width == 0 || height == 0) return new BitmapSize { Width = PreviewMaxLong, Height = PreviewMaxShort };
            bool landscape = width >= height;
            double maxW = landscape ? PreviewMaxLong : PreviewMaxShort;
            double maxH = landscape ? PreviewMaxShort : PreviewMaxLong;
            double scale = Math.Min(1.0, Math.Min(maxW / width, maxH / height));
            uint w = Math.Max(2u, (uint)(width * scale) & ~1u);
            uint h = Math.Max(2u, (uint)(height * scale) & ~1u);
            return new BitmapSize { Width = w, Height = h };
        }

        private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            try
            {
                using var frame = sender.TryAcquireLatestFrame();
                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap == null) return;
                SoftwareBitmap? converted = null;
                try
                {
                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    {
                        converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                        bitmap = converted;
                    }
                    int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
                    var buffer = NextBuffer(stride * height);
                    bitmap.CopyToBuffer(buffer.AsBuffer());
                    FrameArrived?.Invoke(new CameraPreviewFrame(width, height, stride, buffer,
                        CameraFrameProbe.IsAllBlack(buffer, width, height, stride)));
                }
                finally
                {
                    converted?.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _frameErrorLogged, 1) == 0)
                    _log($"Camera frame copy failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
        }

        private byte[] NextBuffer(int length)
        {
            int index = _ringIndex = (_ringIndex + 1) % _ring.Length;
            var buffer = _ring[index];
            if (buffer == null || buffer.Length != length) _ring[index] = buffer = new byte[length];
            return buffer;
        }

        private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
            Failed?.Invoke(unchecked((int)args.Code), args.Message ?? string.Empty);

        private void OnCameraStreamStateChanged(MediaCapture sender, object args)
        {
            try
            {
                switch (sender.CameraStreamState)
                {
                    case CameraStreamState.Shutdown:
                        Failed?.Invoke(DeviceInvalidated, "camera stream shut down");
                        break;
                    case CameraStreamState.BlockedForPrivacy:
                        Failed?.Invoke(CameraSessionController.BlockedForPrivacyCode, "camera blocked for privacy");
                        break;
                }
            }
            catch (Exception)
            {
                // the capture object may already be closing
            }
        }

        public Task<CapturedStill> CaptureStillAsync(int exifOrientation, CancellationToken cancellationToken) =>
            Task.Run(() => CaptureStillCoreAsync(exifOrientation, cancellationToken), cancellationToken);

        private async Task<CapturedStill> CaptureStillCoreAsync(int exifOrientation, CancellationToken cancellationToken)
        {
            byte[] jpeg;
            string source;
            try
            {
                using var photo = new InMemoryRandomAccessStream();
                await _capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), photo).AsTask(cancellationToken);
                jpeg = await ReadAllAsync(photo);
                source = "photo";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // F-027 C7 fallback: one full-size frame from the stream.
                _log($"Camera photo call failed (0x{ex.HResult:X8}); using a full-size frame.");
                jpeg = await GrabFullFrameJpegAsync(cancellationToken);
                source = "frame";
            }
            var (oriented, width, height) = await WithOrientationAsync(jpeg, exifOrientation);
            return new CapturedStill(oriented, width, height, source, exifOrientation);
        }

        private async Task<byte[]> GrabFullFrameJpegAsync(CancellationToken cancellationToken)
        {
            if (_source == null) throw new InvalidOperationException("No camera stream.");
            var reader = await _capture.CreateFrameReaderAsync(_source, MediaEncodingSubtypes.Bgra8);
            var first = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> handler = (r, _) =>
            {
                try
                {
                    using var frame = r.TryAcquireLatestFrame();
                    var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                    if (bitmap != null)
                        first.TrySetResult(SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied));
                }
                catch (Exception ex)
                {
                    first.TrySetException(ex);
                }
            };
            reader.FrameArrived += handler;
            try
            {
                var status = await reader.StartAsync();
                if (status != MediaFrameReaderStartStatus.Success) throw new CameraStartException($"Frame grab did not start: {status}", DeviceInvalidated);
                using var bitmap = await first.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
                using var output = new InMemoryRandomAccessStream();
                var properties = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(0.95, Windows.Foundation.PropertyType.Single) } };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                return await ReadAllAsync(output);
            }
            finally
            {
                reader.FrameArrived -= handler;
                try { await reader.StopAsync(); } catch (Exception) { }
                reader.Dispose();
            }
        }

        /// <summary>F-027 O3: write EXIF Orientation without re-encoding the pixels.</summary>
        private static async Task<(byte[] Jpeg, int Width, int Height)> WithOrientationAsync(byte[] jpeg, int exifOrientation)
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(jpeg.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
            if (exifOrientation is < 1 or > 8) return (jpeg, width, height);
            try
            {
                using var output = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                {
                    { "System.Photo.Orientation", new BitmapTypedValue((ushort)exifOrientation, Windows.Foundation.PropertyType.UInt16) },
                });
                await encoder.FlushAsync();
                return (await ReadAllAsync(output), width, height);
            }
            catch (Exception)
            {
                return (jpeg, width, height); // the photo is still good; F-023 then sees no orientation
            }
        }

        internal static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
        {
            uint size = (uint)stream.Size;
            var bytes = new byte[size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            reader.ReadBytes(bytes);
            return bytes;
        }

        public Task DebugStopFramesAsync() => Task.Run(async () =>
        {
            var reader = _reader;
            if (reader != null) await reader.StopAsync();
        });

        public async ValueTask DisposeAsync() => await Task.Run(DisposeCoreAsync);

        private async Task DisposeCoreAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _capture.Failed -= OnCaptureFailed;
            _capture.CameraStreamStateChanged -= OnCameraStreamStateChanged;
            var reader = _reader;
            _reader = null;
            if (reader != null)
            {
                reader.FrameArrived -= OnFrameArrived;
                try { await reader.StopAsync(); } catch (Exception) { }
                try { reader.Dispose(); } catch (Exception) { }
            }
            try { _capture.Dispose(); } catch (Exception) { }
        }
    }

    internal sealed class CameraStartException : Exception
    {
        public CameraStartException(string message, int hresult) : base(message) { HResult = hresult; }
    }

    /// <summary>F-027 W3: a frame whose sampled pixels are all exactly (0, 0, 0).</summary>
    public static class CameraFrameProbe
    {
        public static bool IsAllBlack(byte[] bgra, int width, int height, int stride, int grid = 8)
        {
            if (width <= 0 || height <= 0) return true;
            for (int gy = 0; gy < grid; gy++)
            {
                int y = (int)((gy + 0.5) * height / grid);
                for (int gx = 0; gx < grid; gx++)
                {
                    int x = (int)((gx + 0.5) * width / grid);
                    int i = y * stride + x * 4;
                    if (i + 2 >= bgra.Length) continue;
                    if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0) return false;
                }
            }
            return true;
        }
    }
}
