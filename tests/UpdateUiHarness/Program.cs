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
foreach (var hangs in new[] { (true, false), (false, true), (true, true) }) {
    var hungApp = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var hungStats = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    bool spinning = true, running = true;
    string status = "Checking for updates...";
    var watch = Stopwatch.StartNew();
    var budget = TimeSpan.FromMilliseconds(60);
    using var deadline = new CancellationTokenSource(budget);
    await UpdateUiCoordinator.RunVisibleAsync(async () => {
        var app = UpdateUiCoordinator.CaptureAsync(
            () => UpdateUiCoordinator.RunPreparationAsync(_ => hangs.Item1 ? hungApp.Task : Task.FromResult("app ok"), budget, deadline.Token),
            _ => "app timed out; retry");
        var stats = UpdateUiCoordinator.CaptureAsync(
            () => UpdateUiCoordinator.RunPreparationAsync(_ => hangs.Item2 ? hungStats.Task : Task.FromResult("stats ok"), budget, deadline.Token),
            _ => "stats timed out; retry");
        await Task.WhenAll(app, stats);
        status = await app + "; " + await stats;
    }, _ => status = "failed; retry", () => { spinning = false; running = false; });
    Check(watch.ElapsedMilliseconds < 600 && !spinning && !running && status.Contains("retry"),
        $"non-cooperative app/stats {hangs}: terminal retryable UI in {watch.ElapsedMilliseconds} ms (60 ms budget)");
    Check(status.Contains(hangs.Item1 ? "app timed out" : "app ok") && status.Contains(hangs.Item2 ? "stats timed out" : "stats ok"), "independent terminal results preserved");
    string terminal = status;
    hungApp.TrySetException(new IOException("late app failure"));
    hungStats.TrySetResult("late stats success");
    await Task.Delay(20);
    Check(status == terminal && !spinning, "late completion cannot change terminal UI");
}
using (var gate = new ManualResetEventSlim()) {
    var watch = Stopwatch.StartNew();
    try {
        await UpdateUiCoordinator.RunPreparationAsync(_ => { gate.Wait(); return Task.FromResult(1); }, TimeSpan.FromMilliseconds(40));
        throw new Exception("missing deadline");
    } catch (TimeoutException) { Check(watch.ElapsedMilliseconds < 600, "synchronous non-cooperative preparation cannot pin dispatcher"); }
    finally { gate.Set(); }
}
bool finalized = false;
await UpdateUiCoordinator.RunVisibleAsync(() => throw new IOException(), _ => { }, () => finalized = true);
Check(finalized, "unexpected synchronous failure still clears visible running state");
Console.WriteLine($"{passed} checks passed in {clock.ElapsedMilliseconds} ms");
