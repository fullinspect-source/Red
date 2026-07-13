using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    internal sealed class AppUpdateResult
    {
        public string CurrentVersion { get; init; } = AppIdentity.Version;
        public string LatestVersion { get; init; } = "";
        public bool SkippedByThrottle { get; init; }
        public bool UpdateAvailable { get; init; }
        public bool InstallerStarted { get; init; }
        public string? Error { get; init; }
    }

    internal static class AppUpdateService
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/fullinspect-source/Red/releases/latest";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private static readonly string LastCheckFile = Path.Combine(AppIdentity.LocalAppDataPath, ".last_app_update_check");

        public static async Task<AppUpdateResult> CheckAndInstallIfAvailableAsync(bool force = false)
        {
            if (AppIdentity.IsDevBuild)
            {
                return new AppUpdateResult
                {
                    LatestVersion = AppIdentity.Version,
                    Error = "Dev build skips app self-updates."
                };
            }

            try
            {
                Directory.CreateDirectory(AppIdentity.LocalAppDataPath);

                // Normal startup checks run once per day. A user triple-click always bypasses this marker.
                if (!force && File.Exists(LastCheckFile) &&
                    DateTime.Now - File.GetLastWriteTime(LastCheckFile) < CheckInterval)
                {
                    return new AppUpdateResult { SkippedByThrottle = true };
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "RED-AppUpdater");
                string apiJson = await http.GetStringAsync(LatestReleaseApi);
                File.WriteAllText(LastCheckFile, DateTime.Now.ToString("o"));

                string remoteVersion = ReadJsonString(apiJson, "tag_name").TrimStart('v', 'V');
                string zipUrl = Regex.Match(apiJson, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"").Groups[1].Value;

                if (string.IsNullOrWhiteSpace(remoteVersion))
                    return new AppUpdateResult { Error = "GitHub latest version was not readable." };

                if (!IsRemoteNewer(remoteVersion, AppIdentity.Version))
                    return new AppUpdateResult { LatestVersion = remoteVersion };

                if (string.IsNullOrWhiteSpace(zipUrl))
                {
                    return new AppUpdateResult
                    {
                        LatestVersion = remoteVersion,
                        UpdateAvailable = true,
                        Error = "GitHub release has no RED zip asset."
                    };
                }

                await DownloadExtractAndStartInstallerAsync(remoteVersion, zipUrl);
                return new AppUpdateResult
                {
                    LatestVersion = remoteVersion,
                    UpdateAvailable = true,
                    InstallerStarted = true
                };
            }
            catch (Exception ex)
            {
                return new AppUpdateResult
                {
                    Error = ex.Message.Length > 120 ? ex.Message[..120] + "..." : ex.Message
                };
            }
        }

        private static async Task DownloadExtractAndStartInstallerAsync(string remoteVersion, string zipUrl)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "RedUpdate");
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            string zipPath = Path.Combine(tempDir, $"Red-v{remoteVersion}.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.Add("User-Agent", "RED-AppUpdater");
                using var response = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(zipPath);
                await input.CopyToAsync(output);
            }

            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            if (!File.Exists(Path.Combine(extractDir, "Red.exe")))
                throw new FileNotFoundException("Downloaded RED release did not contain Red.exe.");

            string appExe = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "Red.exe");
            string destDir = Path.GetDirectoryName(appExe) ?? AppContext.BaseDirectory;
            int pid = Process.GetCurrentProcess().Id;

            var bat = new StringBuilder();
            bat.AppendLine("@echo off");
            bat.AppendLine("net session >nul 2>&1");
            bat.AppendLine("if %errorLevel% == 0 goto :admin");
            bat.AppendLine("echo Set o = CreateObject(\"Shell.Application\") > \"%TEMP%\\red_update_elevate.vbs\"");
            bat.AppendLine("echo o.ShellExecute \"cmd.exe\", \"/c \"\"%~f0\"\"\", \"\", \"runas\", 1 >> \"%TEMP%\\red_update_elevate.vbs\"");
            bat.AppendLine("cscript //nologo \"%TEMP%\\red_update_elevate.vbs\"");
            bat.AppendLine("del \"%TEMP%\\red_update_elevate.vbs\" >nul 2>&1");
            bat.AppendLine("exit /b");
            bat.AppendLine(":admin");
            bat.AppendLine($"title RED Update v{AppIdentity.Version} to v{remoteVersion}");
            bat.AppendLine("echo Waiting for RED to close...");
            bat.AppendLine(":wait");
            bat.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find /I \"Red.exe\" >nul 2>&1 && (ping -n 2 127.0.0.1 >nul & goto wait)");
            bat.AppendLine("echo Installing RED...");
            bat.AppendLine($"xcopy /E /Y /Q \"{extractDir}\\*\" \"{destDir}\\\"");
            bat.AppendLine("if %errorlevel% neq 0 (");
            bat.AppendLine("  echo RED update failed while copying files.");
            bat.AppendLine("  pause");
            bat.AppendLine("  exit /b 1");
            bat.AppendLine(")");
            bat.AppendLine($"start \"\" \"{appExe}\"");
            bat.AppendLine("ping -n 4 127.0.0.1 >nul");
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit");

            string batPath = Path.Combine(Path.GetTempPath(), "red_update.bat");
            File.WriteAllText(batPath, bat.ToString());
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                UseShellExecute = true
            });
        }

        private static string ReadJsonString(string json, string propertyName)
        {
            var match = Regex.Match(json, $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        private static bool IsRemoteNewer(string remote, string local)
        {
            if (Version.TryParse(NormalizeVersion(remote), out var remoteVersion) &&
                Version.TryParse(NormalizeVersion(local), out var localVersion))
                return remoteVersion > localVersion;

            return !string.Equals(remote, local, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeVersion(string value)
        {
            string cleaned = value.Trim().TrimStart('v', 'V');
            string[] parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                1 => $"{parts[0]}.0.0",
                2 => $"{parts[0]}.{parts[1]}.0",
                _ => cleaned
            };
        }
    }
}
