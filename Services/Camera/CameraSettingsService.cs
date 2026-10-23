using System;
using System.IO;
using System.Text.Json;
using RedCamera.Core;

namespace InspectionEditor.Services.Camera
{
    /// <summary>
    /// F-027 C1/C11 settings in their own file (<c>camera_settings.json</c> in RED's app-data folder), so neither the
    /// Settings dialog's whole-file save nor "Import from INSPECT" can wipe them.
    /// </summary>
    public sealed class CameraSettingsService : ICameraPreferences
    {
        public const string RedCameraMode = "red";
        public const string WindowsCameraAppMode = "windows";

        private readonly string _path;

        public CameraSettingsService(string? path = null)
        {
            _path = path ?? Path.Combine(AppIdentity.LocalAppDataPath, "camera_settings.json");
        }

        public sealed class CameraSettings
        {
            public string Mode { get; set; } = RedCameraMode; // owner Q-60 default: RED camera
            public string? DeviceId { get; set; }
            public string? Panel { get; set; }
            public bool AlwaysLandscape { get; set; } = true; // F-027 L1, owner 2026-10-22: on unless switched off
        }

        public CameraSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<CameraSettings>(File.ReadAllText(_path)) ?? new CameraSettings();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                DiagnosticLogService.Info($"Camera settings unreadable ({ex.GetType().Name}); using defaults.");
            }
            return new CameraSettings();
        }

        public void Save(CameraSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DiagnosticLogService.Info($"Camera settings not saved ({ex.GetType().Name}).");
            }
        }

        public bool UsesWindowsCameraApp =>
            string.Equals(Load().Mode, WindowsCameraAppMode, StringComparison.OrdinalIgnoreCase);

        public void SetMode(bool windowsCameraApp)
        {
            var settings = Load();
            settings.Mode = windowsCameraApp ? WindowsCameraAppMode : RedCameraMode;
            Save(settings);
        }

        /// <summary>F-027 L1: photos are always saved landscape (the camera screen's toggle).</summary>
        public bool AlwaysLandscape => Load().AlwaysLandscape;

        public void SetAlwaysLandscape(bool on)
        {
            var settings = Load();
            settings.AlwaysLandscape = on;
            Save(settings);
        }

        public string? DeviceId => Load().DeviceId;

        public CameraPanelKind? Panel =>
            Enum.TryParse(Load().Panel, ignoreCase: true, out CameraPanelKind panel) ? panel : null;

        public void Remember(CameraInfo camera)
        {
            var settings = Load();
            settings.DeviceId = camera.Id;
            settings.Panel = camera.Panel.ToString();
            Save(settings);
        }
    }
}
