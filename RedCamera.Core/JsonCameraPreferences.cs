using System;
using System.IO;
using System.Text.Json;

namespace RedCamera.Core
{
    /// <summary>The remembered camera (F-027 C11) in a small JSON file, for apps that have no settings store of their own.
    /// RED keeps its own camera settings file (with the camera mode) and implements <see cref="ICameraPreferences"/> itself.</summary>
    public sealed class JsonCameraPreferences : ICameraPreferences
    {
        private readonly string _path;
        private readonly Action<string> _log;

        public JsonCameraPreferences(string path, Action<string>? log = null)
        {
            _path = path;
            _log = log ?? (_ => { });
        }

        private sealed class Stored
        {
            public string? DeviceId { get; set; }
            public string? Panel { get; set; }
        }

        private Stored Load()
        {
            try
            {
                if (File.Exists(_path)) return JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path)) ?? new Stored();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                _log($"Camera preferences unreadable ({ex.GetType().Name}); using defaults.");
            }
            return new Stored();
        }

        public string? DeviceId => Load().DeviceId;

        public CameraPanelKind? Panel => Enum.TryParse(Load().Panel, ignoreCase: true, out CameraPanelKind panel) ? panel : null;

        public void Remember(CameraInfo camera)
        {
            try
            {
                string json = JsonSerializer.Serialize(new Stored { DeviceId = camera.Id, Panel = camera.Panel.ToString() },
                    new JsonSerializerOptions { WriteIndented = true });
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log($"Camera preferences not saved ({ex.GetType().Name}).");
            }
        }
    }
}
