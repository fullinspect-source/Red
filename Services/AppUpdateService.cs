using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        public bool InternetRequired { get; init; }
        public string? Error { get; init; }
    }

    internal static class UpdateNetworkService
    {
        // Caller should use HttpClient.Timeout = Timeout.InfiniteTimeSpan; this bounds headers AND body.
        internal static Task<string> GetStringAsync(HttpClient http, string url, CancellationToken cancellationToken = default) =>
            AppUpdateService.WithRetryAsync(async token =>
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(token);
            }, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), cancellationToken);

        internal static string DescribeFailure(Exception exception, string operation) =>
            exception is OperationCanceledException
                ? $"RED couldn't {operation}. The request timed out or was cancelled. Please try again."
                : AppUpdateService.CreateFailureResult(exception, operation).Error!;
    }

    internal static class AppUpdateService
    {
        // Shared with the data updater; a failed request does not prove the PC is offline.
        internal const string InternetRequiredMessage = "RED couldn't reach the update service. Please try again.";
        private const string LatestReleaseApi = "https://api.github.com/repos/fullinspect-source/Red/releases/latest";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        internal sealed class UpdateOptions
        {
            public string MarkerPath { get; init; } = Path.Combine(AppIdentity.LocalAppDataPath, ".last_app_update_check");
            public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "RedUpdate", Guid.NewGuid().ToString("N"));
            public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(30);
            public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(5);
            public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
            public Action<string, string> StartInstaller { get; init; } = AppUpdateService.StartInstaller;
        }

        public static async Task<AppUpdateResult> CheckAndInstallIfAvailableAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add("User-Agent", "RED-AppUpdater");
            return await CheckAndInstallIfAvailableAsync(http, new UpdateOptions(), force, cancellationToken);
        }

        // The production path is also exercised by the portable fake-handler harness.
        internal static async Task<AppUpdateResult> CheckAndInstallIfAvailableAsync(
            HttpClient http, UpdateOptions options, bool force = false, CancellationToken cancellationToken = default)
        {
            if (AppIdentity.IsDevBuild)
                return new AppUpdateResult { LatestVersion = AppIdentity.Version, Error = "Dev build skips app self-updates." };

            string stage = "check for updates";
            string remoteVersion = "";
            bool updateAvailable = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!force && File.Exists(options.MarkerPath) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(options.MarkerPath) < CheckInterval)
                    return new AppUpdateResult { SkippedByThrottle = true };

                string apiJson = await WithRetryAsync(async token =>
                {
                    using var response = await http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(token);
                }, options.CheckTimeout, options.RetryDelay, cancellationToken);

                using var release = JsonDocument.Parse(apiJson);
                remoteVersion = release.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
                if (!Version.TryParse(NormalizeVersion(remoteVersion), out _))
                    throw new InvalidDataException("GitHub latest version was not readable.");

                if (!IsRemoteNewer(remoteVersion, AppIdentity.Version))
                {
                    RecordSuccessfulCheck(options.MarkerPath);
                    return new AppUpdateResult { LatestVersion = remoteVersion };
                }

                updateAvailable = true;
                string zipUrl = "";
                if (release.RootElement.TryGetProperty("assets", out var assets))
                    foreach (var asset in assets.EnumerateArray())
                        if (asset.TryGetProperty("browser_download_url", out var url) &&
                            url.GetString() is string candidate && candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        { zipUrl = candidate; break; }
                if (string.IsNullOrWhiteSpace(zipUrl))
                    return new AppUpdateResult { LatestVersion = remoteVersion, UpdateAvailable = true, Error = "GitHub release has no RED zip asset." };

                stage = "download the update";
                string tempDir = options.TempDirectory;
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);
                string zipPath = Path.Combine(tempDir, $"Red-v{remoteVersion}.zip");
                await WithRetryAsync(async token =>
                {
                    // Every attempt truncates the previous partial file, never appends.
                    try
                    {
                        using var response = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, token);
                        response.EnsureSuccessStatusCode();
                        await using var input = await response.Content.ReadAsStreamAsync(token);
                        await using var output = File.Create(zipPath);
                        await input.CopyToAsync(output, token);
                        await output.FlushAsync(token);
                        return true;
                    }
                    catch
                    {
                        if (File.Exists(zipPath)) File.Delete(zipPath);
                        throw;
                    }
                }, options.DownloadTimeout, options.RetryDelay, cancellationToken);

                stage = "prepare the update installer";
                cancellationToken.ThrowIfCancellationRequested();
                string extractDir = ExtractAndValidate(zipPath, tempDir);
                cancellationToken.ThrowIfCancellationRequested();
                options.StartInstaller(remoteVersion, extractDir);
                RecordSuccessfulCheck(options.MarkerPath);
                return new AppUpdateResult { LatestVersion = remoteVersion, UpdateAvailable = true, InstallerStarted = true };
            }
            catch (Exception ex)
            {
                return CreateFailureResult(ex, stage, remoteVersion, updateAvailable, cancellationToken.IsCancellationRequested);
            }
        }

        private static void RecordSuccessfulCheck(string markerPath)
        {
            // A marker write failure must not hide an already-started installer.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                File.WriteAllText(markerPath, DateTime.UtcNow.ToString("o"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static AppUpdateResult CreateFailureResult(Exception exception, string stage = "check for updates",
            string latestVersion = "", bool updateAvailable = false, bool cancelled = false)
        {
            string reason = exception switch
            {
                OperationCanceledException when cancelled => "The operation was cancelled.",
                OperationCanceledException or TimeoutException => "The request timed out after retries.",
                HttpRequestException http when http.StatusCode.HasValue => $"The server returned HTTP {(int)http.StatusCode.Value}.",
                HttpRequestException => "The update service could not be reached after retries.",
                JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException => "The release information was invalid.",
                InvalidDataException or FileNotFoundException => "The release information or downloaded package was invalid or incomplete.",
                _ => "The operation failed."
            };
            return new AppUpdateResult
            {
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                Error = $"RED couldn't {stage}. {reason} Please try again."
            };
        }

        private static bool IsTransient(Exception exception) => exception switch
        {
            HttpRequestException http => !http.StatusCode.HasValue || (int)http.StatusCode.Value == 429 || (int)http.StatusCode.Value >= 500,
            OperationCanceledException or TimeoutException or IOException => true,
            _ => false
        };

        internal static async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation,
            TimeSpan timeout, TimeSpan retryDelay, CancellationToken cancellationToken)
        {
            const int attempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(timeout);
                try { return await operation(bounded.Token); }
                catch (Exception ex) when (attempt < attempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
                {
                    await Task.Delay(TimeSpan.FromTicks(retryDelay.Ticks * attempt), cancellationToken);
                }
            }
        }

        private static string ExtractAndValidate(string zipPath, string tempDir)
        {
            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            if (!File.Exists(Path.Combine(extractDir, "Red.exe")))
                throw new FileNotFoundException("Downloaded RED release did not contain Red.exe.");
            if (!File.Exists(Path.Combine(extractDir, "SixLabors.ImageSharp.dll")))
                throw new FileNotFoundException("Downloaded RED release is missing its photo processor. Nothing was installed; please download the update again.");

            return extractDir;
        }

        private static void StartInstaller(string remoteVersion, string extractDir)
        {
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

            string batPath = Path.Combine(Path.GetTempPath(), $"red_update_{Guid.NewGuid():N}.bat");
            File.WriteAllText(batPath, bat.ToString());
            using var installer = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                UseShellExecute = true
            }) ?? throw new IOException("RED installer did not start.");
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
