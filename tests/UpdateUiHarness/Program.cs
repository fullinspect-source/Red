using InspectionEditor.Services;
using System.Diagnostics;
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var clock = Stopwatch.StartNew();
bool cancelled = false, finished = false;
int result = await UpdateUiCoordinator.RunStartupAsync(async token => {
    try { await Task.Delay(5000, token); }
    catch (OperationCanceledException) { cancelled = true; }
    await Task.Delay(30); finished = true; return 42;
}, TimeSpan.FromMilliseconds(20));
Check(cancelled && finished && result == 42, "timeout cancels AND awaits cleanup before editor can open");
Check(clock.ElapsedMilliseconds < 1000, "cooperative timeout bounded in measured execution");
result = await UpdateUiCoordinator.RunStartupAsync(_ => Task.FromResult(7), TimeSpan.FromSeconds(1));
Check(result == 7, "completed installer result retained");
result = await UpdateUiCoordinator.RunStartupAsync(async token => { await Task.Delay(45); return 9; }, TimeSpan.FromMilliseconds(5));
Check(result == 9, "completion racing timeout observed, never abandoned");
try { await UpdateUiCoordinator.RunStartupAsync<int>(async token => { await Task.Delay(5000, token); return 0; }, TimeSpan.FromMilliseconds(5)); throw new Exception("missing cancellation"); }
catch (OperationCanceledException) { Check(true, "cancelled task exception observed"); }
foreach (bool appFails in new[] { true, false }) {
    var app = UpdateUiCoordinator.CaptureAsync(() => appFails ? Task.FromException<string>(new IOException()) : Task.FromResult("app ok"), _ => "app failed");
    var stats = UpdateUiCoordinator.CaptureAsync(() => appFails ? Task.FromResult("stats ok") : Task.FromException<string>(new IOException()), _ => "stats failed");
    await Task.WhenAll(app, stats);
    Check(await app == (appFails ? "app failed" : "app ok") && await stats == (appFails ? "stats ok" : "stats failed"), "independent results: appFails=" + appFails);
}
Check(await UpdateUiCoordinator.CaptureAsync<int>(() => throw new IOException(), _ => 5) == 5, "synchronous failure captured independently");
Console.WriteLine($"{passed} checks passed in {clock.ElapsedMilliseconds} ms");
