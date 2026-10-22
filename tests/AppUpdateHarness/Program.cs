using System.IO.Compression;
using System.Net;
using InspectionEditor.Services;

int passed = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> body) { await body(); Console.WriteLine("PASS " + name); passed++; }
// Storage audit F1: a failed attempt's folder is deleted at once, or in the background once a stalled attempt or a
// virus scan lets go of its .partial (up to 30 s).
bool Gone(string dir) { for (int i = 0; i < 350 && Directory.Exists(dir); i++) Thread.Sleep(100); return !Directory.Exists(dir); }
HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
string Current = "{\"tag_name\":\"v2.1.31\"}";
string New = "{\"tag_name\":\"v2.1.32\",\"assets\":[{\"browser_download_url\":\"https://example.test/red.zip\"}]}";
byte[] Zip(bool exe = true, bool photo = true) {
    using var m = new MemoryStream();
    using (var z = new ZipArchive(m, ZipArchiveMode.Create, true)) {
        if (exe) using (var w = new StreamWriter(z.CreateEntry("Red.exe").Open())) w.Write("exe");
        if (photo) using (var w = new StreamWriter(z.CreateEntry("SixLabors.ImageSharp.dll").Open())) w.Write("dll");
    }
    return m.ToArray();
}
foreach (int code in new[] {401,403,404,429,500,503}) await Test("HTTP " + code, async () => {
    using var f = new Fixture((_,_) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
    var r = await f.Run();
    Assert(f.Handler.Calls == (code >= 500 || code == 429 ? 3 : 1), "retry classification");
    Assert(!r.InternetRequired && r.Error!.Contains("HTTP " + code) && !r.Error.Contains("offline"), "truthful status");
    Assert(!File.Exists(f.Options.MarkerPath), "failed marker");
});
await Test("transient transport recovers and throttle/force", async () => {
    using var f = new Fixture((n,_) => n == 1 ? throw new HttpRequestException("transport") : Task.FromResult(Json(Current)));
    Assert((await f.Run()).Error == null && f.Handler.Calls == 2, "recovery");
    Assert(File.Exists(f.Options.MarkerPath), "success marker");
    Assert((await f.Run()).SkippedByThrottle && f.Handler.Calls == 2, "throttle");
    Assert((await f.Run(true)).Error == null && f.Handler.Calls == 3, "force");
});
await Test("timeout exhausts bounded attempts", async () => {
    using var f = new Fixture(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Json(Current); });
    var r = await f.Run();
    Assert(f.Handler.Calls == 3 && r.Error!.Contains("timed out") && !File.Exists(f.Options.MarkerPath), "timeout");
});
await Test("timeout then success", async () => {
    using var f = new Fixture(async (n, ct) => { if (n == 1) await Task.Delay(Timeout.Infinite, ct); return Json(Current); });
    Assert((await f.Run()).Error == null && f.Handler.Calls == 2, "timeout recovery");
});
foreach (string invalid in new[] {"{", "{}", "{\"tag_name\":\"garbage\"}", "{\"tag_name\":\"2.1.32\"}"}) await Test("invalid metadata " + invalid, async () => {
    using var f = new Fixture((_,_) => Task.FromResult(Json(invalid)));
    Assert((await f.Run()).Error != null && !File.Exists(f.Options.MarkerPath), "metadata marker");
    await f.Run(); Assert(f.Handler.Calls == 2, "failed check retried on next call");
});
await Test("successful installer and marker", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) }));
    var r = await f.Run();
    Assert(r.InstallerStarted && f.Started == 1 && File.Exists(f.Options.MarkerPath), "installer success");
    // Storage audit F1: the zip goes once unpacked; the unpacked copy stays for the hand-off script, which removes it.
    Assert(!File.Exists(Path.Combine(f.Options.TempDirectory, "Red-v2.1.32.zip")) &&
           File.Exists(Path.Combine(f.Options.TempDirectory, "extracted", "Red.exe")), "zip deleted after unpacking");
});
foreach (int code in new[] {403,404,429,500}) await Test("download HTTP " + code, async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage((HttpStatusCode)code)));
    var r = await f.Run();
    Assert(r.UpdateAvailable && r.LatestVersion == "2.1.32" && r.Error!.Contains("download the update"), "stage/version");
    Assert(f.Handler.Calls == (code < 429 ? 2 : 4) && !File.Exists(f.Options.MarkerPath) && f.Started == 0, "download retry/marker");
});
foreach (bool stall in new[] {false,true}) await Test("partial stream restarts " + stall, async () => {
    var zip = Zip();
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = n == 2 ? new StreamContent(new BrokenStream(stall)) : new ByteArrayContent(zip) }));
    var r = await f.Run();
    Assert(r.InstallerStarted && f.Handler.Calls == 3, "partial retry success");
    // The zip is deleted once unpacked (storage audit F1); an appended partial would not have unpacked to these bytes.
    // (A stalled attempt that was given up on removes its own .partial a moment later; the hand-off script then
    // removes the whole folder.)
    Assert(File.ReadAllText(Path.Combine(f.Options.TempDirectory,"extracted","Red.exe")) == "exe" &&
           !File.Exists(Path.Combine(f.Options.TempDirectory,"Red-v2.1.32.zip")), "partial file not appended");
});
await Test("download body timeout exhausts", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(true)) }));
    var r = await f.Run();
    Assert(f.Handler.Calls == 4 && r.Error!.Contains("download the update") && r.Error.Contains("timed out"), "stream timeout");
    Assert(!File.Exists(f.Options.MarkerPath) && !File.Exists(Path.Combine(f.Options.TempDirectory,"Red-v2.1.32.zip")), "partial cleaned");
    Assert(Gone(f.Options.TempDirectory), "failed download folder deleted");
});
foreach (var pair in new[] {(false,true),(true,false)}) await Test("package validation " + pair, async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(pair.Item1,pair.Item2)) }));
    Assert((await f.Run()).Error!.Contains("prepare the update installer") && f.Started == 0 && !File.Exists(f.Options.MarkerPath), "package rejected");
    Assert(Gone(f.Options.TempDirectory), "rejected package folder deleted");
});
await Test("caller cancellation no retries or late install", async () => {
    using var cts = new CancellationTokenSource();
    using var f = new Fixture(async (_,ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite,ct); return Json(Current); });
    var r = await f.Run(ct: cts.Token);
    Assert(f.Handler.Calls == 1 && r.Error!.Contains("cancelled") && f.Started == 0 && !File.Exists(f.Options.MarkerPath), "cancel");
});
await Test("cancel immediately before installer", async () => {
    using var cts = new CancellationTokenSource();
    using var f = new Fixture((n,_) => { if (n == 1) return Task.FromResult(Json(New)); cts.Cancel(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) }); });
    await f.Run(ct: cts.Token);
    Assert(f.Started == 0 && !File.Exists(f.Options.MarkerPath), "late installer forbidden");
    Assert(Gone(f.Options.TempDirectory), "cancelled download folder deleted");
});
await Test("installer launch failure leaves retry available", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) }), true);
    Assert(!(await f.Run()).InstallerStarted && !File.Exists(f.Options.MarkerPath), "launch failure marker");
    Assert(Gone(f.Options.TempDirectory), "download folder deleted when the installer could not start");
});
await Test("shared helper HTTP errors", async () => {
    using var h = new FakeHandler((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
    using var client = new HttpClient(h);
    try { await UpdateNetworkService.GetStringAsync(client,"https://example.test"); throw new Exception("expected failure"); }
    catch (HttpRequestException e) { Assert(h.Calls == 1 && UpdateNetworkService.DescribeFailure(e,"check data").Contains("HTTP 403"), "shared helper"); }
});
await Test("metadata response body timeout", async () => {
    using var f = new Fixture((_,_) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(true)) }));
    var r = await f.Run();
    Assert(f.Handler.Calls == 3 && r.Error!.Contains("timed out") && !File.Exists(f.Options.MarkerPath), "metadata body deadline");
});
await Test("failed forced check preserves prior marker", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(Current) : new HttpResponseMessage(HttpStatusCode.Forbidden)));
    await f.Run();
    var marker = File.ReadAllText(f.Options.MarkerPath);
    var stamp = File.GetLastWriteTimeUtc(f.Options.MarkerPath);
    Assert((await f.Run(true)).Error != null && File.ReadAllText(f.Options.MarkerPath) == marker && File.GetLastWriteTimeUtc(f.Options.MarkerPath) == stamp, "failed check changed marker");
});
await Test("non-cooperative network metadata is hard bounded", async () => {
    var hung = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var f = new Fixture((_,_) => hung.Task);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var r = await f.Run().WaitAsync(TimeSpan.FromSeconds(2));
    Assert(r.Error!.Contains("timed out") && f.Handler.Calls == 3 && f.Started == 0 && watch.ElapsedMilliseconds < 1000, "hard deadline");
    hung.SetException(new IOException("late failure"));
});
foreach (bool download in new[] { false, true }) await Test("non-cooperative response body bounded: download=" + download, async () => {
    var hung = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var f = new Fixture((n,_) => Task.FromResult(download && n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonCooperativeStream(hung.Task)) }));
    var r = await f.Run().WaitAsync(TimeSpan.FromSeconds(2));
    Assert(r.Error!.Contains("timed out") && f.Started == 0 && !File.Exists(f.Options.MarkerPath), "body timeout/installer safety");
    hung.SetException(new IOException("late body failure"));
    await Task.Delay(30);
});
await Test("prepared package cannot install itself; cancelled handoff rejected", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) }));
    var prepared = await f.Prepare();
    Assert(prepared.ExtractDirectory != null && f.Started == 0 && !File.Exists(f.Options.MarkerPath), "preparation launched installer");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var r = AppUpdateService.InstallPreparedUpdate(prepared, f.Options, cancelled.Token);
    Assert(!r.InstallerStarted && f.Started == 0 && !File.Exists(f.Options.MarkerPath), "cancelled handoff installed");
    Assert(Gone(f.Options.TempDirectory), "cancelled handoff folder deleted");
});
await Test("visible timeout of late preparation cannot launch installer", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) }));
    var prepared = await f.Prepare();
    var late = new TaskCompletionSource<AppUpdateService.PreparedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
    try {
        var value = await UpdateUiCoordinator.RunPreparationAsync(_ => late.Task, TimeSpan.FromMilliseconds(30));
        AppUpdateService.InstallPreparedUpdate(value, f.Options);
        throw new Exception("missing timeout");
    } catch (TimeoutException) { }
    late.SetResult(prepared); await Task.Delay(30);
    Assert(f.Started == 0 && !File.Exists(f.Options.MarkerPath), "late install");
});

// Storage audit F1: the start-up sweep of %TEMP%\RedUpdate and the hand-off scripts (sandbox folders only).
await Test("sweep: old download folders and legacy files go; fresh, kept and other names stay", () => {
    using var box = new Sandbox();
    DateTime now = DateTime.UtcNow;
    string oldGuid = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddHours(-2), withFiles: true);
    string freshGuid = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddMinutes(-10), withFiles: true);
    string keep = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddDays(-3), withFiles: true);
    string legacyExtract = box.Dir(box.Root, "extracted", now.AddDays(-6), withFiles: true);
    string legacyZip = box.File(box.Root, "Red-v2.1.43.zip", now.AddDays(-6), 4096);
    string legacyPartial = box.File(box.Root, "Red-v2.1.43.zip." + Guid.NewGuid().ToString("N") + ".partial", now.AddDays(-6), 100);
    string otherFile = box.File(box.Root, "notes.txt", now.AddDays(-30), 10);
    string otherDir = box.Dir(box.Root, "other", now.AddDays(-30), withFiles: true);
    string shortGuid = box.Dir(box.Root, "0123456789abcdef", now.AddDays(-30), withFiles: true);
    string oldScript = box.File(box.Scripts, "red_update_" + Guid.NewGuid().ToString("N") + ".bat", now.AddDays(-2), 50);
    string freshScript = box.File(box.Scripts, "red_update_" + Guid.NewGuid().ToString("N") + ".bat", now.AddHours(-1), 50);
    string oldFixedScript = box.File(box.Scripts, "red_update.bat", now.AddDays(-9), 50);
    string elevate = box.File(box.Scripts, "red_update_elevate.vbs", now.AddDays(-9), 50);
    string unrelated = box.File(box.Scripts, "setup_" + Guid.NewGuid().ToString("N") + ".bat", now.AddDays(-9), 50);
    var tally = AppUpdateService.SweepTemp(now, keep, box.Root, box.Scripts);
    Assert(!Directory.Exists(oldGuid) && !Directory.Exists(legacyExtract) && !File.Exists(legacyZip) && !File.Exists(legacyPartial), "old leftovers removed");
    Assert(Directory.Exists(freshGuid) && Directory.Exists(keep), "fresh folder and this attempt's folder kept");
    Assert(File.Exists(otherFile) && Directory.Exists(otherDir) && Directory.Exists(shortGuid), "names RED does not use are never touched");
    Assert(!File.Exists(oldScript) && !File.Exists(oldFixedScript) && File.Exists(freshScript), "hand-off scripts older than a day removed");
    Assert(File.Exists(elevate) && File.Exists(unrelated), "other scripts never touched");
    Assert(tally.Files == 2 * 2 + 2 + 2 && tally.Folders == 2 && tally.Bytes > 0 && tally.Skipped == 0, "tally " + tally);
    return Task.CompletedTask;
});
await Test("sweep: age boundaries (1 hour for downloads, 1 day for scripts)", () => {
    using var box = new Sandbox();
    DateTime now = DateTime.UtcNow;
    string young = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddMinutes(-59), withFiles: true);
    string old = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddMinutes(-61), withFiles: true);
    string youngScript = box.File(box.Scripts, "red_update_" + Guid.NewGuid().ToString("N") + ".bat", now.AddHours(-23), 50);
    string oldScript = box.File(box.Scripts, "red_update_" + Guid.NewGuid().ToString("N") + ".bat", now.AddHours(-25), 50);
    AppUpdateService.SweepTemp(now, null, box.Root, box.Scripts);
    Assert(Directory.Exists(young) && !Directory.Exists(old), "download boundary");
    Assert(File.Exists(youngScript) && !File.Exists(oldScript), "script boundary");
    return Task.CompletedTask;
});
await Test("sweep: a folder with an open file is left whole, then removed once it is closed", () => {
    using var box = new Sandbox();
    DateTime now = DateTime.UtcNow;
    string old = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddHours(-3), withFiles: true);
    string held = Path.Combine(old, "extracted", "Red.exe");
    using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var tally = AppUpdateService.SweepTemp(now, null, box.Root, box.Scripts);
        Assert(Directory.Exists(old) && File.Exists(Path.Combine(old, "Red-v2.1.32.zip")) && tally.Skipped == 1 && tally.Files == 0, "locked folder left whole " + tally);
    }
    Assert(AppUpdateService.SweepTemp(now, null, box.Root, box.Scripts).Folders == 1 && !Directory.Exists(old), "removed once closed");
    return Task.CompletedTask;
});
await Test("sweep: never deletes through a link", () => {
    using var box = new Sandbox();
    DateTime now = DateTime.UtcNow;
    string outside = box.Dir(box.Base, "outside", now, withFiles: true);
    string old = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddHours(-3), withFiles: false);
    box.Junction(Path.Combine(old, "extracted"), outside);
    Directory.SetLastWriteTimeUtc(old, now.AddHours(-3));
    var tally = AppUpdateService.SweepTemp(now, null, box.Root, box.Scripts);
    Assert(File.Exists(Path.Combine(outside, "extracted", "Red.exe")) && Directory.Exists(old) && tally.Skipped == 1, "link target untouched " + tally);
    string linkedRoot = Path.Combine(box.Base, "linked-root");
    box.Junction(linkedRoot, box.Root);
    string victim = box.Dir(box.Root, Guid.NewGuid().ToString("N"), now.AddHours(-3), withFiles: true);
    AppUpdateService.SweepTemp(now, null, linkedRoot, box.Scripts);
    Assert(Directory.Exists(victim), "a RedUpdate folder that is itself a link is not swept");
    Directory.Delete(Path.Combine(old, "extracted"));
    Directory.Delete(linkedRoot);
    return Task.CompletedTask;
});
await Test("hand-off script removes the download folder after copying and on failure", () => {
    string package = Path.Combine(Path.GetTempPath(), "RedUpdate", Guid.NewGuid().ToString("N"));
    string extract = Path.Combine(package, "extracted");
    string line = AppUpdateService.RemovePackageLine(extract);
    Assert(line == $"rd /s /q \"{package}\" >nul 2>&1", "rd line: " + line);
    Assert(AppUpdateService.RemovePackageLine(@"C:\extracted") == "" && AppUpdateService.RemovePackageLine(Path.Combine(package, "other")) == "", "only an updater 'extracted' folder");
    string script = AppUpdateService.InstallerScript("2.1.32", extract, @"C:\Red\Red.exe", @"C:\Red", 1234);
    string[] lines = script.Split("\r\n");
    int fail = Array.FindIndex(lines, l => l.Contains("failed while copying"));
    int exit1 = Array.FindIndex(lines, l => l.Trim() == "exit /b 1");
    int start = Array.FindIndex(lines, l => l.StartsWith("start "));
    int failRd = Array.FindIndex(lines, l => l.Trim() == line);
    int okRd = Array.FindLastIndex(lines, l => l == line);
    Assert(fail >= 0 && failRd > fail && failRd < exit1, "failure branch removes the folder before exit");
    Assert(okRd > exit1 && okRd < start, "success path removes the folder before starting RED");
    return Task.CompletedTask;
});
Console.WriteLine($"{passed} production updater tests passed.");

sealed class Sandbox : IDisposable {
    public string Base { get; } = Path.Combine(Path.GetTempPath(), "RedUpdaterTests-" + Guid.NewGuid());
    public string Root => Path.Combine(Base, "RedUpdate");
    public string Scripts => Path.Combine(Base, "temp");
    public Sandbox() { Directory.CreateDirectory(Root); Directory.CreateDirectory(Scripts); }
    public string Dir(string parent, string name, DateTime writeUtc, bool withFiles) {
        string dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        if (withFiles) {
            System.IO.File.WriteAllBytes(Path.Combine(dir, "Red-v2.1.32.zip"), new byte[2048]);
            Directory.CreateDirectory(Path.Combine(dir, "extracted"));
            System.IO.File.WriteAllText(Path.Combine(dir, "extracted", "Red.exe"), "exe");
            Directory.SetLastWriteTimeUtc(Path.Combine(dir, "extracted"), writeUtc);
        }
        Directory.SetLastWriteTimeUtc(dir, writeUtc);
        return dir;
    }
    public string File(string parent, string name, DateTime writeUtc, int size) {
        string path = Path.Combine(parent, name);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        System.IO.File.SetLastWriteTimeUtc(path, writeUtc);
        return path;
    }
    public void Junction(string link, string target) {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        if (!Directory.Exists(link)) throw new Exception("junction not made");
    }
    public void Dispose() => Fixture.DeleteSandbox(Base);
}

sealed class Fixture : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "RedUpdaterTests-" + Guid.NewGuid());
    public FakeHandler Handler {get;}
    readonly HttpClient client;
    public AppUpdateService.UpdateOptions Options {get;}
    public int Started;
    public Fixture(Func<int,CancellationToken,Task<HttpResponseMessage>> response, bool launchFails = false) {
        Handler = new(response); client = new(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        string tempRoot = Path.Combine(root, "RedUpdate");
        Options = new() { MarkerPath = Path.Combine(root,"marker"), TempRoot = tempRoot, TempDirectory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")), CheckTimeout = TimeSpan.FromMilliseconds(40), DownloadTimeout = TimeSpan.FromMilliseconds(40), RetryDelay = TimeSpan.Zero, StartInstaller = (_,dir) => { if (launchFails) throw new IOException("launch failed"); if (!File.Exists(Path.Combine(dir,"Red.exe"))) throw new Exception("validation bypassed"); Started++; } };
    }
    public Task<AppUpdateResult> Run(bool force=false,CancellationToken ct=default) => AppUpdateService.CheckAndInstallIfAvailableAsync(client,Options,force,ct);
    public Task<AppUpdateService.PreparedUpdate> Prepare() => AppUpdateService.PrepareAsync(client,Options,true);
    public void Dispose() { client.Dispose(); DeleteSandbox(root); }
    // Audit S-28: the sandbox always goes, also after a failed check; a late background download may still hold a file
    // for a moment, so it is retried briefly.
    public static void DeleteSandbox(string path) {
        for (int attempt = 0; attempt < 20 && Directory.Exists(path); attempt++) {
            try { Directory.Delete(path, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
sealed class FakeHandler(Func<int,CancellationToken,Task<HttpResponseMessage>> response) : HttpMessageHandler {
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => response(++Calls,ct);
}
sealed class BrokenStream(bool stall) : Stream {
    bool first = true;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) {
        if (first) { first=false; buffer.Span[0]=42; return 1; }
        if (stall) await Task.Delay(Timeout.Infinite,ct);
        throw new IOException("connection reset mid-body");
    }
    public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException(); public override long Position {get=>0;set=>throw new NotSupportedException();}
    public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override void Flush(){} public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long l)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
sealed class NonCooperativeStream(Task<int> stalled) : Stream {
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct=default) => new(stalled);
    public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException(); public override long Position {get=>0;set=>throw new NotSupportedException();}
    public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException(); public override void Flush(){} public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException(); public override void SetLength(long l)=>throw new NotSupportedException(); public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
namespace InspectionEditor { internal static class AppIdentity { public static bool IsDevBuild=>false; public const string Version="2.1.31"; public static string LocalAppDataPath=>Path.GetTempPath(); } }
