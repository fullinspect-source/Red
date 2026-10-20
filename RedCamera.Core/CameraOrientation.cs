using System;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 O1-O3, following Microsoft's CameraRotationHelper (MediaCapture orientation guidance). All angles are
    /// counter-clockwise device rotations from the device's native orientation, in steps of 90.
    /// </summary>
    public static class CameraOrientation
    {
        public static bool IsExternal(CameraInfo camera) =>
            camera.Panel is CameraPanelKind.External or CameraPanelKind.Unknown;

        /// <summary>O2: a front camera's preview is mirrored.</summary>
        public static bool MirrorPreview(CameraInfo camera) => camera.Panel == CameraPanelKind.Front;

        /// <summary>The camera's rotation relative to the native orientation (enclosure angle, plus 90 on
        /// portrait-native devices, where built-in sensors are read at a 90-degree offset).</summary>
        public static int CameraRelativeCcw(CameraInfo camera, bool nativePortrait)
        {
            int ccw = Normalize(360 - camera.MountRotationClockwise);
            if (nativePortrait && !IsExternal(camera)) ccw = Normalize(ccw + 90);
            return ccw;
        }

        /// <summary>O3: EXIF Orientation for a still, from the device's counter-clockwise rotation.</summary>
        public static int CaptureExifOrientation(CameraInfo camera, int deviceCcw, bool nativePortrait)
        {
            if (IsExternal(camera)) return 1;
            int result = Normalize(deviceCcw - CameraRelativeCcw(camera, nativePortrait));
            if (MirrorPreview(camera)) result = Mirror(result);
            return ExifFromCcw(result);
        }

        /// <summary>O1: clockwise degrees to rotate the preview on screen, from the display's counter-clockwise rotation.</summary>
        public static int PreviewRotationClockwise(CameraInfo camera, int displayCcw, bool nativePortrait)
        {
            if (IsExternal(camera)) return 0;
            int result = Normalize(displayCcw - CameraRelativeCcw(camera, nativePortrait));
            if (MirrorPreview(camera)) result = Mirror(result);
            return Normalize(360 - result);
        }

        /// <summary>
        /// Display rotation (DEVMODE dmDisplayOrientation: 0 = default, 1 = 90, 2 = 180, 3 = 270) as a counter-clockwise
        /// device rotation: Microsoft maps Portrait (90) to 270 and Portrait flipped (270) to 90.
        /// </summary>
        public static int DisplayToDeviceCcw(int displayOrientationQuarterTurns) =>
            Normalize(-90 * displayOrientationQuarterTurns);

        /// <summary>PhotoOrientation / EXIF value for a counter-clockwise rotation (Normal 1, Rotate90 8, Rotate180 3,
        /// Rotate270 6).</summary>
        public static int ExifFromCcw(int ccw) => Normalize(ccw) switch
        {
            90 => 8,
            180 => 3,
            270 => 6,
            _ => 1,
        };

        private static int Mirror(int ccw) => Normalize(ccw) switch
        {
            90 => 270,
            270 => 90,
            var other => other,
        };

        private static int Normalize(int degrees)
        {
            int d = (int)Math.Round(degrees / 90.0) * 90 % 360;
            return d < 0 ? d + 360 : d;
        }
    }
}
