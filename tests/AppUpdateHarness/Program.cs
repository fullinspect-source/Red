using System.IO.Compression;
using System.Net;
using InspectionEditor.Services;

int passed = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> body) { await body(); Console.WriteLine("PASS " + name); passed++; }
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
    Assert(File.ReadAllBytes(Path.Combine(f.Options.TempDirectory,"Red-v2.1.32.zip")).SequenceEqual(zip), "partial file not appended");
});
await Test("download body timeout exhausts", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(true)) }));
    var r = await f.Run();
    Assert(f.Handler.Calls == 4 && r.Error!.Contains("download the update") && r.Error.Contains("timed out"), "stream timeout");
    Assert(!File.Exists(f.Options.MarkerPath) && !File.Exists(Path.Combine(f.Options.TempDirectory,"Red-v2.1.32.zip")), "partial cleaned");
});
foreach (var pair in new[] {(false,true),(true,false)}) await Test("package validation " + pair, async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip(pair.Item1,pair.Item2)) }));
    Assert((await f.Run()).Error!.Contains("prepare the update installer") && f.Started == 0 && !File.Exists(f.Options.MarkerPath), "package rejected");
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
});
await Test("installer launch failure leaves retry available", async () => {
    using var f = new Fixture((n,_) => Task.FromResult(n == 1 ? Json(New) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip()) }), true);
    Assert(!(await f.Run()).InstallerStarted && !File.Exists(f.Options.MarkerPath), "launch failure marker");
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
Console.WriteLine($"{passed} production updater tests passed.");

sealed class Fixture : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "RedUpdaterTests-" + Guid.NewGuid());
    public FakeHandler Handler {get;}
    readonly HttpClient client;
    public AppUpdateService.UpdateOptions Options {get;}
    public int Started;
    public Fixture(Func<int,CancellationToken,Task<HttpResponseMessage>> response, bool launchFails = false) {
        Handler = new(response); client = new(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Options = new() { MarkerPath = Path.Combine(root,"marker"), TempDirectory = Path.Combine(root,"download"), CheckTimeout = TimeSpan.FromMilliseconds(40), DownloadTimeout = TimeSpan.FromMilliseconds(40), RetryDelay = TimeSpan.Zero, StartInstaller = (_,dir) => { if (launchFails) throw new IOException("launch failed"); if (!File.Exists(Path.Combine(dir,"Red.exe"))) throw new Exception("validation bypassed"); Started++; } };
    }
    public Task<AppUpdateResult> Run(bool force=false,CancellationToken ct=default) => AppUpdateService.CheckAndInstallIfAvailableAsync(client,Options,force,ct);
    public void Dispose() { client.Dispose(); if (Directory.Exists(root)) Directory.Delete(root,true); }
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
namespace InspectionEditor { internal static class AppIdentity { public static bool IsDevBuild=>false; public const string Version="2.1.31"; public static string LocalAppDataPath=>Path.GetTempPath(); } }
