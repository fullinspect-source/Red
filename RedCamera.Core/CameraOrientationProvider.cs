using System;
using System.Runtime.InteropServices;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 O1-O3 inputs from this device: the primary display's rotation (EnumDisplaySettings) and, when present, the
    /// orientation sensor. Call it on one thread (the UI thread).
    /// </summary>
    public sealed class CameraOrientationProvider
    {
        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

        private Windows.Devices.Sensors.SimpleOrientationSensor? _sensor;
        private bool _sensorChecked;
        private int _lastDeviceCcw;

        /// <summary>The display's counter-clockwise device rotation and whether the device is portrait-native.</summary>
        public (int DisplayCcw, bool NativePortrait) DisplayRotation()
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            int quarter = 0;
            bool currentPortrait = false;
            try
            {
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    quarter = mode.dmDisplayOrientation is >= 0 and <= 3 ? mode.dmDisplayOrientation : 0;
                    currentPortrait = mode.dmPelsHeight > mode.dmPelsWidth;
                }
            }
            catch (Exception)
            {
                // no display information: landscape, not rotated
            }
            bool nativePortrait = quarter % 2 == 0 ? currentPortrait : !currentPortrait;
            return (CameraOrientation.DisplayToDeviceCcw(quarter), nativePortrait);
        }

        /// <summary>The device's counter-clockwise rotation from the orientation sensor; face up or face down keeps the last
        /// upright reading; without a sensor, the display rotation.</summary>
        public int DeviceRotationCcw(int displayCcw)
        {
            try
            {
                if (!_sensorChecked)
                {
                    _sensorChecked = true;
                    _sensor = Windows.Devices.Sensors.SimpleOrientationSensor.GetDefault();
                }
                if (_sensor != null)
                {
                    switch (_sensor.GetCurrentOrientation())
                    {
                        case Windows.Devices.Sensors.SimpleOrientation.NotRotated: return _lastDeviceCcw = 0;
                        case Windows.Devices.Sensors.SimpleOrientation.Rotated90DegreesCounterclockwise: return _lastDeviceCcw = 90;
                        case Windows.Devices.Sensors.SimpleOrientation.Rotated180DegreesCounterclockwise: return _lastDeviceCcw = 180;
                        case Windows.Devices.Sensors.SimpleOrientation.Rotated270DegreesCounterclockwise: return _lastDeviceCcw = 270;
                        default: return _lastDeviceCcw;
                    }
                }
            }
            catch (Exception)
            {
                _sensor = null;
            }
            return displayCcw;
        }

        /// <summary>Preview rotation (clockwise degrees) and mirroring for <paramref name="camera"/> on this display (O1, O2).</summary>
        public (int RotationClockwise, bool Mirror) Preview(CameraInfo camera)
        {
            var (displayCcw, nativePortrait) = DisplayRotation();
            return (CameraOrientation.PreviewRotationClockwise(camera, displayCcw, nativePortrait), CameraOrientation.MirrorPreview(camera));
        }

        /// <summary>EXIF Orientation for a still taken now with <paramref name="camera"/> (O3).</summary>
        public int StillExifOrientation(CameraInfo? camera)
        {
            if (camera == null) return 1;
            var (displayCcw, nativePortrait) = DisplayRotation();
            return CameraOrientation.CaptureExifOrientation(camera, DeviceRotationCcw(displayCcw), nativePortrait);
        }
    }
}
