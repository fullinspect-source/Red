using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        // Bound headers AND body, independently of HttpClient's headers-only timeout.
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
        // Prepared work has no authority to launch an installer. Only the awaiting UI
        // (or startup owner) can commit it after checking cancellation and save safety.
        internal sealed record PreparedUpdate(AppUpdateResult Result, string? ExtractDirectory = null);
        // Shared with the data updater; a failed request does not prove the PC is offline.
        internal const string InternetRequiredMessage = "RED couldn't reach the update service. Please try again.";
        private const string LatestReleaseApi = "https://api.github.com/repos/fullinspect-source/Red/releases/latest";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        // Storage audit F1 (S-01, S-37): each update download goes into %TEMP%\RedUpdate\{guid} (about 315 MB with the
        // unpacked copy). It is deleted once the update is installed or has failed, and SweepTemp removes what a crash,
        // a declined Windows prompt or an older RED left behind.
        internal static string DefaultTempRoot => Path.Combine(Path.GetTempPath(), "RedUpdate");
        internal static readonly TimeSpan TempEntryMinAge = TimeSpan.FromHours(1);
        internal static readonly TimeSpan HandoffScriptMinAge = TimeSpan.FromDays(1);

        internal sealed class UpdateOptions
        {
            public string MarkerPath { get; init; } = Path.Combine(AppIdentity.LocalAppDataPath, ".last_app_update_check");
            public string TempRoot { get; init; } = DefaultTempRoot;
            public string TempDirectory { get; init; } = Path.Combine(DefaultTempRoot, Guid.NewGuid().ToString("N"));
            public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(30);
            public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(5);
            public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
            public Action<string, string> StartInstaller { get; init; } = AppUpdateService.StartInstaller;
        }

        public static async Task<AppUpdateResult> CheckAndInstallIfAvailableAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "RED-AppUpdater");
            return await CheckAndInstallIfAvailableAsync(http, new UpdateOptions(), force, cancellationToken);
        }

        // The production path is also exercised by the portable fake-handler harness.
        internal static async Task<AppUpdateResult> CheckAndInstallIfAvailableAsync(
            HttpClient http, UpdateOptions options, bool force = false, CancellationToken cancellationToken = default)
        {
            var prepared = await PrepareAsync(http, options, force, cancellationToken);
            return InstallPreparedUpdate(prepared, options, cancellationToken);
        }

        internal static async Task<PreparedUpdate> PrepareAsync(bool force, CancellationToken cancellationToken)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "RED-AppUpdater");
            return await PrepareAsync(http, new UpdateOptions(), force, cancellationToken);
        }

        internal static AppUpdateResult InstallPreparedUpdate(PreparedUpdate prepared,
            UpdateOptions options, CancellationToken cancellationToken = default)
        {
            if (prepared.ExtractDirectory == null) return prepared.Result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                options.StartInstaller(prepared.Result.LatestVersion, prepared.ExtractDirectory);
                RecordSuccessfulCheck(options.MarkerPath);
                return new AppUpdateResult { LatestVersion = prepared.Result.LatestVersion,
                    UpdateAvailable = true, InstallerStarted = true };
            }
            catch (Exception ex)
            {
                // The installer never started, so its download and unpacked copy are not needed any more.
                DeletePackageFolder(Path.GetDirectoryName(prepared.ExtractDirectory));
                return CreateFailureResult(ex, "start the update installer", prepared.Result.LatestVersion,
                    true, cancellationToken.IsCancellationRequested);
            }
        }

        internal static async Task<PreparedUpdate> PrepareAsync(
            HttpClient http, UpdateOptions options, bool force = false, CancellationToken cancellationToken = default)
        {
            if (AppIdentity.IsDevBuild)
                return new(new AppUpdateResult { LatestVersion = AppIdentity.Version, Error = "Dev build skips app self-updates." });

            string stage = "check for updates";
            string remoteVersion = "";
            bool updateAvailable = false;
            bool downloadStarted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!force && File.Exists(options.MarkerPath) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(options.MarkerPath) < CheckInterval)
                    return new(new AppUpdateResult { SkippedByThrottle = true });

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
                    cancellationToken.ThrowIfCancellationRequested();
                    RecordSuccessfulCheck(options.MarkerPath);
                    return new(new AppUpdateResult { LatestVersion = remoteVersion });
                }

                updateAvailable = true;
                string zipUrl = "";
                if (release.RootElement.TryGetProperty("assets", out var assets))
                    foreach (var asset in assets.EnumerateArray())
                        if (asset.TryGetProperty("browser_download_url", out var url) &&
                            url.GetString() is string candidate && candidate.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        { zipUrl = candidate; break; }
                if (string.IsNullOrWhiteSpace(zipUrl))
                    return new(new AppUpdateResult { LatestVersion = remoteVersion, UpdateAvailable = true, Error = "GitHub release has no RED zip asset." });

                stage = "download the update";
                string tempDir = options.TempDirectory;
                // Leftovers of earlier attempts go before a new 315 MB copy is made; this attempt's folder is kept.
                SweepDownloads(DateTime.UtcNow, tempDir, options.TempRoot);
                downloadStarted = true;
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);
                string zipPath = Path.Combine(tempDir, $"Red-v{remoteVersion}.zip");
                string downloadedPath = await WithRetryAsync(async token =>
                {
                    // Isolate attempts: a cancelled, non-cooperative stream must never
                    // race a retry for the same file or return an installable package.
                    string attemptPath = zipPath + "." + Guid.NewGuid().ToString("N") + ".partial";
                    try
                    {
                        using var response = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, token);
                        response.EnsureSuccessStatusCode();
                        await using var input = await response.Content.ReadAsStreamAsync(token);
                        await using var output = File.Create(attemptPath);
                        await input.CopyToAsync(output, token);
                        await output.FlushAsync(token);
                        token.ThrowIfCancellationRequested();
                        return attemptPath;
                    }
                    catch
                    {
                        if (File.Exists(attemptPath)) File.Delete(attemptPath);
                        throw;
                    }
                }, options.DownloadTimeout, options.RetryDelay, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(downloadedPath, zipPath, true);

                stage = "prepare the update installer";
                cancellationToken.ThrowIfCancellationRequested();
                string extractDir = ExtractAndValidate(zipPath, tempDir);
                // The unpacked copy is what gets installed; the 89 MB zip is not needed while the installer waits.
                try { File.Delete(zipPath); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return new(new AppUpdateResult { LatestVersion = remoteVersion, UpdateAvailable = true }, extractDir);
            }
            catch (Exception ex)
            {
                // A bad zip, a timeout, a cancel or an interrupted download: nothing of this attempt is kept.
                if (downloadStarted) await DeletePackageFolderAsync(options.TempDirectory);
                return new(CreateFailureResult(ex, stage, remoteVersion, updateAvailable, cancellationToken.IsCancellationRequested));
            }
        }

        // A download attempt given up on after a timeout closes and deletes its .partial a little later (an antivirus scan
        // can hold a new file too), so the folder is retried briefly here, then in the background for up to 30 seconds;
        // anything still held after that is removed by SweepTemp at the next start.
        private static async Task DeletePackageFolderAsync(string folder)
        {
            if (await TryDeleteFolderAsync(folder, 10, TimeSpan.FromMilliseconds(50))) return;
            _ = Task.Run(() => TryDeleteFolderAsync(folder, 60, TimeSpan.FromMilliseconds(500)));
        }

        private static async Task<bool> TryDeleteFolderAsync(string folder, int attempts, TimeSpan pause)
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    await Task.Delay(pause);
                }
            }
            return false;
        }

        // Best effort: a file still held (a late download task, an antivirus scan) leaves the rest for SweepTemp.
        private static void DeletePackageFolder(string? folder)
        {
            try { if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static readonly Regex PackageFolderName = new(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        private static readonly Regex LegacyPackageFile = new(@"^Red-v[0-9A-Za-z.\-]+\.zip(\.[0-9a-f]{32}\.partial)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex HandoffScriptName = new(@"^red_update(_[0-9a-f]{32})?\.bat$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Storage audit F1: deletes what earlier update attempts left in <paramref name="tempRoot"/> (default
        /// %TEMP%\RedUpdate): the {guid} download folders, and the "extracted" folder and Red-v*.zip of RED before 2.2,
        /// when last written more than an hour ago, except <paramref name="keep"/>. Also the installer hand-off scripts
        /// %TEMP%\red_update_{guid}.bat (and the old fixed red_update.bat) more than a day old. Nothing else in %TEMP%
        /// is touched. A file that is open or locked is skipped. Runs at each start and before each download.
        /// </summary>
        internal static CleanupTally SweepTemp(DateTime utcNow, string? keep = null, string? tempRoot = null, string? scriptFolder = null)
        {
            var tally = SweepDownloads(utcNow, keep, tempRoot);
            SweepHandoffScripts(utcNow, scriptFolder, tally);
            return tally;
        }

        internal static CleanupTally SweepDownloads(DateTime utcNow, string? keep, string? tempRoot)
        {
            var tally = new CleanupTally();
            try
            {
                var root = new DirectoryInfo(tempRoot ?? DefaultTempRoot);
                if (root.Exists && (root.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    string? kept = string.IsNullOrEmpty(keep) ? null : Path.GetFullPath(keep).TrimEnd('\\');
                    DateTime cutoff = utcNow - TempEntryMinAge;
                    foreach (var entry in root.EnumerateFileSystemInfos())
                    {
                        if (kept != null && string.Equals(entry.FullName.TrimEnd('\\'), kept, StringComparison.OrdinalIgnoreCase)) continue;
                        if (entry.LastWriteTimeUtc > cutoff) continue;
                        if (entry is DirectoryInfo folder && (PackageFolderName.IsMatch(folder.Name) ||
                            string.Equals(folder.Name, "extracted", StringComparison.OrdinalIgnoreCase)))
                            SafeDelete.Tree(folder, tally);
                        else if (entry is FileInfo file && LegacyPackageFile.IsMatch(file.Name))
                            SafeDelete.File(file, tally);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            return tally;
        }

        private static void SweepHandoffScripts(DateTime utcNow, string? scriptFolder, CleanupTally tally)
        {
            try
            {
                var scripts = new DirectoryInfo(scriptFolder ?? Path.GetTempPath());
                if (scripts.Exists)
                {
                    DateTime cutoff = utcNow - HandoffScriptMinAge;
                    foreach (var file in scripts.EnumerateFiles("red_update*.bat", SearchOption.TopDirectoryOnly))
                        if (HandoffScriptName.IsMatch(file.Name) && file.LastWriteTimeUtc <= cutoff)
                            SafeDelete.File(file, tally);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
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
                try { return await UpdateUiCoordinator.RunPreparationAsync(operation, timeout, cancellationToken); }
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

            string batPath = Path.Combine(Path.GetTempPath(), $"red_update_{Guid.NewGuid():N}.bat");
            File.WriteAllText(batPath, InstallerScript(remoteVersion, extractDir, appExe, destDir, pid));
            using var installer = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                UseShellExecute = true
            }) ?? throw new IOException("RED installer did not start.");
        }

        internal static string InstallerScript(string remoteVersion, string extractDir, string appExe, string destDir, int pid)
        {
            string removePackage = RemovePackageLine(extractDir);
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
            if (removePackage.Length > 0) bat.AppendLine("  " + removePackage);
            bat.AppendLine("  pause");
            bat.AppendLine("  exit /b 1");
            bat.AppendLine(")");
            if (removePackage.Length > 0) bat.AppendLine(removePackage);
            bat.AppendLine($"start \"\" \"{appExe}\"");
            bat.AppendLine("ping -n 4 127.0.0.1 >nul");
            bat.AppendLine("del \"%~f0\"");
            bat.AppendLine("exit");
            return bat.ToString();
        }

        // The hand-off script removes the download folder ({guid}, holding "extracted") after copying, whether the copy
        // worked or not. Only a folder this updater made (an "extracted" folder inside it) is ever named.
        internal static string RemovePackageLine(string extractDir)
        {
            string full = Path.GetFullPath(extractDir).TrimEnd('\\');
            string? package = Path.GetDirectoryName(full);
            if (package == null || !string.Equals(Path.GetFileName(full), "extracted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetPathRoot(package)?.TrimEnd('\\'), package.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "";
            return $"rd /s /q \"{package}\" >nul 2>&1";
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
