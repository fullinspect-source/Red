// F-020 offline caches harness (program/features/F-020-offline-caches/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// A loopback mock Strand website and a sandbox cache folder under %TEMP%. Synthetic list data only (no real jobs).
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using InspectionEditor.Services;

return await Harness.Run();

sealed class MockStrand : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public Func<string, (int Status, string Body)> Handler { get; set; } = _ => (200, "[]");
    public Uri Base { get; }

    public MockStrand()
    {
        _listener.Start();
        Base = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        });
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new MemoryStream();
            int b, state = 0;
            while (state < 4 && (b = stream.ReadByte()) >= 0)
            {
                header.WriteByte((byte)b);
                state = (b == '\r' && (state == 0 || state == 2)) || (b == '\n' && (state == 1 || state == 3)) ? state + 1 : 0;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var first = lines[0].Split(' ');
            int length = 0;
            foreach (var line in lines.Skip(1))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
            if (length > 0) { var body = new byte[length]; int read = 0; while (read < length) { int r = await stream.ReadAsync(body.AsMemory(read)); if (r <= 0) break; read += r; } }
            string path = first.Length > 1 ? first[1] : "/";
            Requests.Enqueue(first[0] + " " + path);
            bool logOn = path.StartsWith("/Security/LogOn", StringComparison.OrdinalIgnoreCase);
            var (status, text) = logOn ? (200, "") : Handler(path);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            string extra = logOn ? "Set-Cookie: ASP.NET_SessionId=mock1; path=/\r\nSet-Cookie: .ASPXAUTH=mock2; path=/\r\n" : "";
            string head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n{extra}Connection: close\r\n\r\n";
            try { await stream.WriteAsync(Encoding.ASCII.GetBytes(head)); await stream.WriteAsync(bytes); } catch { }
        }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private const string Forms = "[{\"ServiceId\":1,\"FormId\":20,\"Name\":\"Concrete Pre Pour\",\"Abbreviation\":\"CPP\"}]";
    private const string Templates = "[{\"FormId\":20,\"InspectionCode\":\"CPP\",\"Name\":\"Concrete Pre Pour\",\"Sections\":[]}]";
    private const string Jobs = "[{\"JobNumber\":\"1234567\",\"Address\":\"100 Example St\",\"BuyerName\":\"Example Buyer\"}]";

    public static async Task<int> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f020-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var mock = new MockStrand();
            var session = StrandWebsiteSession.ForLoopbackMock(mock.Base, () => ("mockuser", "mock-" + Guid.NewGuid().ToString("N")));
            var redLog = new List<string>();
            Task NoDelay(TimeSpan _) => Task.CompletedTask;

            // ---------- A-020-01 Calendar-day staleness ----------
            {
                var now = new DateTime(2026, 10, 17, 0, 1, 0);
                Directory.CreateDirectory(OfflineCacheService.Folder(root));
                File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Forms, root), Forms);
                File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Forms, root), new DateTime(2026, 10, 16, 23, 59, 0));
                File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Templates, root), Templates);
                File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Templates, root), new DateTime(2026, 10, 17, 0, 0, 0));
                File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Jobs, root), Jobs);
                File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Jobs, root), new DateTime(2026, 10, 17, 0, 0, 0));
                mock.Handler = path => path.Contains("Forms") ? (200, Forms) : path.Contains("Templates") ? (200, Templates) : (200, Jobs);
                Check("A-020-01", OfflineCacheService.IsStale(OfflineCache.Forms, now, root) && !OfflineCacheService.IsStale(OfflineCache.Templates, now, root),
                    "written yesterday 23:59 is stale at 00:01; written today 00:00 is not");
                await OfflineCacheService.AutoRefreshAsync(session, () => now, root, NoDelay, redLog.Add);
                var gets = mock.Requests.Where(r => r.StartsWith("GET ")).ToList();
                Check("A-020-01", gets.SequenceEqual(new[] { "GET /Inspect/Forms/GetOfflineList" }),
                    "only /Inspect/Forms/GetOfflineList is requested (the fresh ones are not, and the 10 s retry finds nothing stale)");
            }

            // ---------- A-020-02 Failed fetch keeps the old file ----------
            {
                string jobs = OfflineCacheService.PathOf(OfflineCache.Jobs, root);
                File.WriteAllText(jobs, Jobs);
                byte[] before = File.ReadAllBytes(jobs);
                mock.Handler = _ => (500, "boom");
                var result = await OfflineCacheService.RefreshAsync(session, new[] { OfflineCache.Jobs }, root, redLog.Add);
                Check("A-020-02", result.Failed.SequenceEqual(new[] { OfflineCache.Jobs }) && File.ReadAllBytes(jobs).AsSpan().SequenceEqual(before),
                    "HTTP 500: the old Jobs file is unchanged");
            }

            // ---------- A-020-03 Empty response not saved ----------
            {
                string jobs = OfflineCacheService.PathOf(OfflineCache.Jobs, root);
                byte[] before = File.ReadAllBytes(jobs);
                redLog.Clear();
                mock.Handler = _ => (200, "");
                await OfflineCacheService.RefreshAsync(session, new[] { OfflineCache.Jobs }, root, redLog.Add);
                Check("A-020-03", File.ReadAllBytes(jobs).AsSpan().SequenceEqual(before) && redLog.Contains("Request completed with no jobs data returned"),
                    "HTTP 200 with an empty body: unchanged, RED-only line \"Request completed with no jobs data returned\"");
            }

            // ---------- R3 a login page is never saved; a fresh login is tried once ----------
            {
                string forms = OfflineCacheService.PathOf(OfflineCache.Forms, root);
                byte[] before = File.ReadAllBytes(forms);
                int logOnsBefore = mock.Requests.Count(r => r.Contains("/Security/LogOn"));
                mock.Handler = _ => (200, "<html>Log on</html>");
                var result = await OfflineCacheService.RefreshAsync(session, new[] { OfflineCache.Forms }, root, redLog.Add);
                Check("R3", result.Failed.Count == 1 && File.ReadAllBytes(forms).AsSpan().SequenceEqual(before) &&
                            mock.Requests.Count(r => r.Contains("/Security/LogOn")) > logOnsBefore,
                    "a login page answer: one fresh logon, then nothing saved");
                mock.Handler = _ => (200, Forms);
                var saved = await OfflineCacheService.RefreshAsync(session, new[] { OfflineCache.Forms }, root, redLog.Add);
                Check("R3", saved.Saved.Count == 1 && File.ReadAllText(forms) == Forms && !Directory.GetFiles(OfflineCacheService.Folder(root), "*.tmp").Any(),
                    "a JSON answer is saved whole and atomically (no temp file left)");
            }

            // ---------- A-020-04 / A-020-05 Forced download texts ----------
            {
                Check("A-020-04", OfflineCacheService.HasAll(root) && OfflineCacheService.CancelQuestion == "Cancel request to retrieve updated offline work requirements?" &&
                                  OfflineCacheService.RequirementsMetCaption == "Offline Work Requirements Met",
                    "all three present: RED asks INSPECT's question under its caption");
                mock.Handler = path => path.Contains("Jobs") ? (500, "") : path.Contains("Forms") ? (200, Forms) : (200, Templates);
                var result = await OfflineCacheService.RefreshAsync(session, Enum.GetValues<OfflineCache>(), root, redLog.Add);
                string text = OfflineCacheService.ResultText(result);
                Check("A-020-05", result.Failed.SequenceEqual(new[] { OfflineCache.Jobs }) &&
                                  text == "Unable to retrieve all offline requirements. Connect to WiFi and try again, or contact IT Support if issue persists." +
                                          Environment.NewLine + Environment.NewLine + "Below offline files were not retrieved:" + Environment.NewLine + "Offline jobs",
                    "jobs failed: INSPECT's partial-failure text listing \"Offline jobs\"");
                Check("R4", OfflineCacheService.ResultText(new OfflineRefreshResult(Enum.GetValues<OfflineCache>(), Array.Empty<OfflineCache>())) == "Offline requirements retrieved successfully!" &&
                            OfflineCacheService.MissingText == "Unable to find offline data on your tablet. Please connect to the internet and then click 'Download Offline Data' within your settings window.",
                    "the success and the missing-at-use texts are INSPECT's");
            }

            // ---------- A-020-06 Local only ----------
            Check("A-020-06", !OfflineCacheService.Folder().Contains("Dropbox", StringComparison.OrdinalIgnoreCase) &&
                              OfflineCacheService.Folder().StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase),
                "the caches live in RED's local app data, never under Dropbox");

            // ---------- V3 privacy: no cache value in RED's log lines ----------
            Check("V3", !redLog.Any(l => l.Contains("Example Buyer") || l.Contains("100 Example St") || l.Contains("1234567")),
                "RED-only log lines carry no value from the caches");

            // ---------- A-020-07 Sign-out removes the jobs cache ----------
            OfflineCacheService.DeleteJobsCache(root);
            Check("A-020-07", !File.Exists(OfflineCacheService.PathOf(OfflineCache.Jobs, root)) && File.Exists(OfflineCacheService.PathOf(OfflineCache.Forms, root)),
                "the Jobs cache is deleted; the forms cache stays");

            // ---------- C3 bootstrap from INSPECT's copies (sandbox stand-in), read only ----------
            {
                string inspect = Path.Combine(root, "StrandInspectInspect");
                string fresh = Path.Combine(root, "fresh");
                Directory.CreateDirectory(inspect);
                File.WriteAllText(Path.Combine(inspect, "OfflineJobs.json"), Jobs);
                var when = new DateTime(2026, 10, 15, 8, 0, 0);
                File.SetLastWriteTime(Path.Combine(inspect, "OfflineJobs.json"), when);
                int copied = OfflineCacheService.BootstrapFromInspect(inspect, fresh);
                Check("C3", copied == 1 && File.ReadAllText(OfflineCacheService.PathOf(OfflineCache.Jobs, fresh)) == Jobs &&
                            File.GetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Jobs, fresh)) == when &&
                            File.GetLastWriteTime(Path.Combine(inspect, "OfflineJobs.json")) == when,
                    "INSPECT's copy is taken with its own write time (so staleness carries over); the source is untouched");
                File.WriteAllText(Path.Combine(inspect, "OfflineJobs.json"), "[{\"changed\":1}]");
                Check("C3", OfflineCacheService.BootstrapFromInspect(inspect, fresh) == 0 && File.ReadAllText(OfflineCacheService.PathOf(OfflineCache.Jobs, fresh)) == Jobs,
                    "an existing RED cache is never replaced by the bootstrap");

                // Review fixes: the bootstrap runs once per device, never after a sign-out, and takes only a complete file.
                File.Delete(OfflineCacheService.PathOf(OfflineCache.Jobs, fresh));
                Check("C3", OfflineCacheService.BootstrapFromInspect(inspect, fresh) == 0 && !File.Exists(OfflineCacheService.PathOf(OfflineCache.Jobs, fresh)),
                    "once bootstrapped, INSPECT's copies are not imported again (a deleted RED cache stays deleted)");
                string signedOut = Path.Combine(root, "signedout");
                OfflineCacheService.DeleteJobsCache(signedOut);
                Check("C2", OfflineCacheService.BootstrapFromInspect(inspect, signedOut) == 0 && !File.Exists(OfflineCacheService.PathOf(OfflineCache.Jobs, signedOut)),
                    "after a sign-out the next launch does not bring INSPECT's jobs list back");
                string truncated = Path.Combine(root, "truncated");
                File.WriteAllText(Path.Combine(inspect, "OfflineJobs.json"), "[{\"JobNumber\":\"1234567\",");
                Check("C3", OfflineCacheService.BootstrapFromInspect(inspect, truncated) == 0 && !File.Exists(OfflineCacheService.PathOf(OfflineCache.Jobs, truncated)) &&
                            !Directory.GetFiles(OfflineCacheService.Folder(truncated), "*.tmp").Any(),
                    "a file INSPECT is still writing (not a complete JSON array) is not taken");
            }

            // ---------- C2 a sign-out during a download saves nothing ----------
            {
                string during = Path.Combine(root, "during");
                var generation = typeof(StrandScheduleService).GetProperty(nameof(StrandScheduleService.SessionGeneration))!;
                mock.Handler = path =>
                {
                    // The session ends while the jobs answer is on its way.
                    generation.SetValue(null, StrandScheduleService.SessionGeneration + 1);
                    return (200, Jobs);
                };
                var result = await OfflineCacheService.RefreshAsync(session, new[] { OfflineCache.Jobs }, during, redLog.Add);
                Check("C2", result.Failed.SequenceEqual(new[] { OfflineCache.Jobs }) && !File.Exists(OfflineCacheService.PathOf(OfflineCache.Jobs, during)) &&
                            redLog.Contains("Offline jobs not saved: the Strand session changed during the download"),
                    "an answer that arrives after a sign-out or account change is not saved");
            }
        }
        catch (Exception ex)
        {
            Check("harness", false, "unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-020 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
