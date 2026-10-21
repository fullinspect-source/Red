using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    public enum OfflineCache { Jobs, Forms, Templates }

    public sealed record OfflineRefreshResult(IReadOnlyList<OfflineCache> Saved, IReadOnlyList<OfflineCache> Failed);

    /// <summary>
    /// F-020: INSPECT's three offline lists (forms, templates, jobs) in RED's own local folder, never in Dropbox (C2).
    /// A cache is stale when missing or written on an earlier calendar day (R1). Saved only from an HTTP 200 JSON body,
    /// atomically; a failed fetch keeps the previous file (R3). Contents are never logged (V3); the Jobs cache (buyer,
    /// agent and superintendent data) is deleted on Strand sign-out.
    /// </summary>
    public static class OfflineCacheService
    {
        public const string RequirementsMetText = "Offline requirements are met. Manual creation of jobs can take place.";
        public const string CancelQuestion = "Cancel request to retrieve updated offline work requirements?";
        public const string RequirementsMetCaption = "Offline Work Requirements Met";
        public const string RetrievedText = "Offline requirements retrieved successfully!";
        public const string PartialFailureText = "Unable to retrieve all offline requirements. Connect to WiFi and try again, or contact IT Support if issue persists.";
        public const string NotRetrievedHeader = "Below offline files were not retrieved:";
        public const string MissingText = "Unable to find offline data on your tablet. Please connect to the internet and then click 'Download Offline Data' within your settings window.";
        public const string MissingCaption = "Offline Requirements Missing";
        public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

        public static string Folder(string? root = null) => Path.Combine(root ?? AppIdentity.LocalAppDataPath, "offline");

        // F-021 E2: whether a download is running, and when one ends.
        private static int _running;
        public static bool IsDownloading => Volatile.Read(ref _running) > 0;
        public static event Action? DownloadFinished;

        public static string FileName(OfflineCache cache) => $"Offline{cache}.json";

        public static string PathOf(OfflineCache cache, string? root = null) => Path.Combine(Folder(root), FileName(cache));

        public static string Resource(OfflineCache cache) => cache switch
        {
            OfflineCache.Forms => "Inspect/Forms/GetOfflineList",
            OfflineCache.Templates => "Inspect/Templates/GetList",
            _ => "Inspect/Jobs/GetOfflineList"
        };

        // R4 list names.
        public static string DisplayName(OfflineCache cache) => cache switch
        {
            OfflineCache.Forms => "Offline forms",
            OfflineCache.Templates => "Offline templates",
            _ => "Offline jobs"
        };

        private static string Word(OfflineCache cache) => cache.ToString().ToLowerInvariant();

        // R1: missing, or last written on an earlier calendar day than today.
        public static bool IsStale(OfflineCache cache, DateTime now, string? root = null)
        {
            string path = PathOf(cache, root);
            return !File.Exists(path) || File.GetLastWriteTime(path).Date < now.Date;
        }

        // A cache file that parses as a JSON array (R5: anything else counts as missing at use time).
        public static bool IsJsonArray(string path)
        {
            try
            {
                using var reader = new Newtonsoft.Json.JsonTextReader(new StreamReader(path)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None };
                return Newtonsoft.Json.Linq.JToken.ReadFrom(reader) is Newtonsoft.Json.Linq.JArray;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { return false; }
        }

        public static bool HasAll(string? root = null) =>
            Enum.GetValues<OfflineCache>().All(cache => File.Exists(PathOf(cache, root)));

        public static List<OfflineCache> StaleCaches(DateTime now, string? root = null) =>
            Enum.GetValues<OfflineCache>().Where(cache => IsStale(cache, now, root)).ToList();

        // Whether Windows reports any usable network connection. Only a hint: the download itself is the real test.
        public static bool LooksOnline()
        {
            try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); }
            catch (Exception) { return true; }
        }

        // An explicit Strand sign-out in this run keeps the saved login from being used for downloads until the next
        // sign-in or launch (C2: the jobs list went with the session).
        private static int _savedLoginRefused;
        public static void RefuseSavedLoginForThisRun() => Volatile.Write(ref _savedLoginRefused, 1);
        public static void AllowSavedLogin() => Volatile.Write(ref _savedLoginRefused, 0);
        public static bool SavedLoginRefused => Volatile.Read(ref _savedLoginRefused) == 1;

        /// <summary>
        /// R2 and F-021 E2 (owner tablet test 2026-10-20): the website session a download (and the Add wizard) uses. The
        /// signed-in Strand session when there is one; otherwise a session of its own on RED's saved login (else INSPECT's,
        /// in the silent sign-in's order), so no ORDERS sign-in is needed first, as INSPECT uses its saved login. Null when
        /// no complete login is known.
        /// </summary>
        public static StrandWebsiteSession? SessionForDownload(StrandWebsiteSession signedIn, Func<StrandCredentials?>? savedLogin = null)
        {
            if (signedIn.HasCredentials)
                return signedIn;
            if (SavedLoginRefused)
                return null;
            var login = (savedLogin ?? SavedLogin)();
            if (login is not { IsComplete: true })
                return null;
            string username = login.Username, password = login.Password;
            lock (SavedLoginLock)
            {
                // One session per saved login, so its cookie and connections are reused by the next download or Add.
                if (_savedLoginSession == null || _savedLoginSession.Username != username || _savedLoginPassword != password)
                {
                    _savedLoginSession = new StrandWebsiteSession(StrandWebsiteSession.ConfiguredBase, () => (username, password));
                    _savedLoginPassword = password;
                }
                return _savedLoginSession;
            }
        }

        private static readonly object SavedLoginLock = new();
        private static StrandWebsiteSession? _savedLoginSession;
        private static string? _savedLoginPassword;

        // RED's saved login; INSPECT's when RED has none, or for the same account (as TrySilentStrandSignInAsync).
        private static StrandCredentials? SavedLogin()
        {
            var saved = StrandCredentialService.LoadSaved();
            if (saved is { IsComplete: true })
                return saved;
            var inspect = StrandCredentialService.ImportFromInspect();
            return inspect is { IsComplete: true } && (saved == null || string.Equals(inspect.Username, saved.Username, StringComparison.OrdinalIgnoreCase))
                ? inspect : null;
        }

        /// <summary>
        /// R2/R3: fetches the given caches in parallel on the website session and saves the good answers. Returns what was
        /// saved and what failed. An answer that is not a JSON array (a login page, an error page) is not saved.
        /// </summary>
        public static async Task<OfflineRefreshResult> RefreshAsync(StrandWebsiteSession session, IEnumerable<OfflineCache> caches,
            string? root = null, Action<string>? redLog = null, CancellationToken token = default)
        {
            redLog ??= DiagnosticLogService.Info;
            var list = caches.Distinct().ToList();
            var saved = new List<OfflineCache>();
            var failed = new List<OfflineCache>();
            if (list.Count == 0) return new OfflineRefreshResult(saved, failed);
            Interlocked.Increment(ref _running);
            try
            {
                try
                {
                    await session.EnsureAsync(force: false, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    redLog($"Offline data not requested: {ex.GetType().Name}");
                    return new OfflineRefreshResult(saved, list);
                }
                // One forced re-login per refresh, shared by the parallel fetches; a sign-out or account change meanwhile
                // keeps every answer from being saved (C2: no Jobs cache comes back after sign-out).
                var run = new RefreshRun(StrandScheduleService.SessionGeneration);
                var results = await Task.WhenAll(list.Select(async cache => (cache, ok: await FetchOneAsync(session, cache, root, redLog, run, token))));
                foreach (var (cache, ok) in results) (ok ? saved : failed).Add(cache);
                redLog($"Offline data download: {saved.Count} of {list.Count} saved"); // counts only (V3)
                return new OfflineRefreshResult(saved, failed);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                try { DownloadFinished?.Invoke(); } catch (Exception) { }
            }
        }

        private sealed class RefreshRun
        {
            public RefreshRun(int generation) => Generation = generation;
            public int Generation { get; }
            public SemaphoreSlim ReloginGate { get; } = new(1, 1);
            public bool Relogged;
            public bool SessionUnchanged => StrandScheduleService.SessionGeneration == Generation;
        }

        private static async Task<bool> FetchOneAsync(StrandWebsiteSession session, OfflineCache cache, string? root, Action<string> redLog, RefreshRun run, CancellationToken token)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, StrandWebsiteSession.Resource(session.Base, Resource(cache)));
                    using var response = await session.SendAsync(request, RequestTimeout, token);
                    if ((int)response.StatusCode != 200)
                        return false;
                    byte[] body = await response.Content.ReadAsByteArrayAsync(token);
                    string text = Encoding.UTF8.GetString(body).TrimStart('﻿', ' ', '\r', '\n', '\t');
                    if (text.Length == 0)
                    {
                        redLog($"Request completed with no {Word(cache)} data returned");
                        return false;
                    }
                    if (text.StartsWith("<", StringComparison.Ordinal) && attempt == 0)
                    {
                        // An expired cookie session answers with the login page: log on again once for the whole refresh,
                        // so one fetch's re-login never discards the cookie another fetch has just received.
                        await run.ReloginGate.WaitAsync(token);
                        try
                        {
                            if (!run.Relogged)
                            {
                                session.Discard();
                                await session.EnsureAsync(force: true, token);
                                run.Relogged = true;
                            }
                        }
                        finally { run.ReloginGate.Release(); }
                        continue;
                    }
                    if (!text.StartsWith("[", StringComparison.Ordinal))
                        return false;
                    if (!run.SessionUnchanged)
                    {
                        redLog($"Offline {Word(cache)} not saved: the Strand session changed during the download");
                        return false;
                    }
                    SaveAtomically(PathOf(cache, root), body);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    redLog($"Offline {Word(cache)} could not be retrieved: {ex.GetType().Name}");
                    return false;
                }
            }
            return false;
        }

        private static void SaveAtomically(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }

        // R2: the stale caches only; once more 10 s later for any still stale (a one-shot retry). everything (owner
        // 2026-10-20, tablet test of 2.3.4: "always do that for the inspector whenever inspector opens RED"): every cache,
        // at each start, then once more for any that failed. Silent: results go only to RED's log.
        public static async Task AutoRefreshAsync(StrandWebsiteSession session, Func<DateTime>? clock = null, string? root = null,
            Func<TimeSpan, Task>? delay = null, Action<string>? redLog = null, bool everything = false)
        {
            clock ??= () => DateTime.Now;
            delay ??= Task.Delay;
            // A saved-login session (SessionForDownload) stops once the user has signed out in this run, and once any sign-in
            // or sign-out has happened since it started (the retry must never save the previous account's jobs list; the
            // sign-in starts its own refresh).
            int generation = StrandScheduleService.SessionGeneration;
            bool Allowed() => session.HasCredentials && (ReferenceEquals(session, StrandWebsiteSession.Default) ||
                                                         !SavedLoginRefused && StrandScheduleService.SessionGeneration == generation);
            if (!Allowed()) return;
            List<OfflineCache> Stale() => Enum.GetValues<OfflineCache>().Where(cache => IsStale(cache, clock(), root)).ToList();
            var first = everything ? Enum.GetValues<OfflineCache>().ToList() : Stale();
            if (first.Count == 0) return;
            var result = await RefreshAsync(session, first, root, redLog);
            await delay(RetryDelay);
            var again = everything ? result.Failed.ToList() : Stale();
            if (again.Count > 0 && Allowed()) // signed out meanwhile: no retry
                await RefreshAsync(session, again, root, redLog);
        }
        // R4: the result text of a forced download.
        public static string ResultText(OfflineRefreshResult result) =>
            result.Failed.Count == 0
                ? RetrievedText
                : PartialFailureText + Environment.NewLine + Environment.NewLine + NotRetrievedHeader + Environment.NewLine +
                  string.Join(Environment.NewLine, result.Failed.OrderBy(c => c).Select(DisplayName));

        // C3 runs once per device: after this marker exists (written by the first bootstrap or by a sign-out), INSPECT's
        // copies are never imported again, so a sign-out cannot be undone by the next launch.
        public static string BootstrapMarker(string? root = null) => Path.Combine(Folder(root), "bootstrap.done");

        private static void MarkBootstrapped(string? root)
        {
            try
            {
                Directory.CreateDirectory(Folder(root));
                if (!File.Exists(BootstrapMarker(root))) File.WriteAllText(BootstrapMarker(root), "");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // C2: the Jobs cache holds personal data; it goes when the Strand account signs out (or changes).
        public static void DeleteJobsCache(string? root = null)
        {
            MarkBootstrapped(root);
            try
            {
                string path = PathOf(OfflineCache.Jobs, root);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DiagnosticLogService.Info($"The offline jobs cache could not be deleted at sign-out: {ex.GetType().Name}");
            }
        }

        // C3 (Windows only, optional): with no RED cache yet, take INSPECT's copies, read only. File.Copy keeps INSPECT's
        // write time, so their staleness carries over.
        public static int BootstrapFromInspect(string? inspectFolder = null, string? root = null)
        {
            int copied = 0;
            if (File.Exists(BootstrapMarker(root))) return 0;
            inspectFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strand", "Inspect", "Inspect");
            foreach (var cache in Enum.GetValues<OfflineCache>())
            {
                string target = PathOf(cache, root);
                string source = Path.Combine(inspectFolder, FileName(cache));
                if (File.Exists(target) || !File.Exists(source)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var output = new FileStream(target + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    // INSPECT may be part-way through rewriting its file: only a complete JSON array is taken.
                    if (!IsJsonArray(target + ".tmp"))
                    {
                        File.Delete(target + ".tmp");
                        continue;
                    }
                    File.SetLastWriteTime(target + ".tmp", File.GetLastWriteTime(source));
                    File.Move(target + ".tmp", target, overwrite: true);
                    copied++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    try { File.Delete(target + ".tmp"); } catch (Exception) { }
                }
            }
            MarkBootstrapped(root);
            return copied;
        }
    }
}
