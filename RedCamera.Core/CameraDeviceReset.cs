using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RedCamera.Core
{
    /// <summary>
    /// F-027 C10: the optional administrator fix for a hung camera driver (turn the camera device off and on again).
    /// It runs only from the capture screen's "Reset camera…" button after the user confirms, never automatically.
    /// </summary>
    public static class CameraDeviceReset
    {
        private static readonly Regex SafeInstanceId = new(@"^[A-Za-z0-9_&\\.#{}-]+$", RegexOptions.CultureInvariant);

        public static bool IsSafeInstanceId(string? instanceId) =>
            !string.IsNullOrWhiteSpace(instanceId) && instanceId.Length <= 400 && SafeInstanceId.IsMatch(instanceId);

        /// <summary>The PowerShell command: disable, wait 2 s, enable.</summary>
        public static string BuildCommand(string instanceId)
        {
            if (!IsSafeInstanceId(instanceId)) throw new ArgumentException("Unsafe device instance id.", nameof(instanceId));
            return $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false -ErrorAction Stop; "
                + "Start-Sleep -Seconds 2; "
                + $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false -ErrorAction Stop";
        }

        /// <summary>An elevated (UAC) PowerShell start for <see cref="BuildCommand"/>.</summary>
        public static ProcessStartInfo BuildStartInfo(string instanceId) => new()
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + BuildCommand(instanceId) + "\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
    }

    /// <summary>Runs the reset (F-027 C10). Returns the exit code, or null when the user declined the UAC prompt or the
    /// command could not start.</summary>
    public interface ICameraResetRunner
    {
        Task<int?> RunAsync(string instanceId);
    }

    public sealed class ElevatedCameraResetRunner : ICameraResetRunner
    {
        public async Task<int?> RunAsync(string instanceId)
        {
            try
            {
                using var process = Process.Start(CameraDeviceReset.BuildStartInfo(instanceId));
                if (process == null) return null;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return null; // ERROR_CANCELLED: the user said No to the UAC prompt
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// F-027 C10 flow, kept out of the window so the harness can prove it runs only on explicit request:
    /// look up the instance id, ask for confirmation, run elevated, then Try again.
    /// </summary>
    public sealed class CameraResetFlow
    {
        private readonly Func<CameraInfo, Task<string?>> _instanceIdOf;
        private readonly Func<string, bool> _confirm;
        private readonly ICameraResetRunner _runner;
        private readonly Func<TimeSpan, Task> _delay;
        private readonly Action<string> _log;

        public CameraResetFlow(Func<CameraInfo, Task<string?>> instanceIdOf, Func<string, bool> confirm,
            ICameraResetRunner runner, Func<TimeSpan, Task>? delay = null, Action<string>? log = null)
        {
            _instanceIdOf = instanceIdOf;
            _confirm = confirm;
            _runner = runner;
            _delay = delay ?? (t => Task.Delay(t));
            _log = log ?? (_ => { });
        }

        public const string CannotResetText = "This camera cannot be reset from RED. Restart the tablet instead.";

        public static string ConfirmText(CameraInfo camera) =>
            $"Reset the camera \"{camera.Name}\"?\n\nWindows will turn this camera device off and on again. "
            + "This needs administrator permission, and Windows will ask for it next.";

        /// <summary>Returns a message for the capture screen, or null when the reset ran (the caller then runs Try again).</summary>
        public async Task<(bool Ran, string? Message)> RunAsync(CameraInfo camera)
        {
            string? instanceId = await _instanceIdOf(camera);
            if (!CameraDeviceReset.IsSafeInstanceId(instanceId))
            {
                _log("Camera reset refused: no usable device instance id.");
                return (false, CannotResetText);
            }
            if (!_confirm(ConfirmText(camera)))
            {
                _log("Camera reset declined by the user.");
                return (false, null);
            }
            int? exit = await _runner.RunAsync(instanceId!);
            _log($"Camera reset finished: exit {(exit?.ToString() ?? "none (declined or not started)")}.");
            if (exit == null) return (false, "The camera was not reset.");
            await _delay(TimeSpan.FromSeconds(3));
            return (true, exit == 0 ? null : "Windows could not reset the camera. Restart the tablet.");
        }
    }
}
