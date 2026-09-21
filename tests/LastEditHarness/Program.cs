using InspectionEditor.Services;
using Newtonsoft.Json.Linq;
// Reconstruct display metadata in a fresh process for either timestamp source.
if (args.Length == 5 && args[0] == "--display-reopen")
{
    var display = LastEditTime.ReadDisplayForFile(args[1], File.ReadAllBytes(args[1]), args[2]);
    var expected = DateTimeOffset.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    if (display.Utc != expected || display.IsVerifiedSave != bool.Parse(args[4]) ||
        LastEditTime.Format(display.Utc, expected.AddHours(2)) != "2 hr")
        throw new Exception("Fresh-process display timestamp/source/aging mismatch");
    Console.WriteLine("PASS fresh-process display reload: " + (display.IsVerifiedSave ? "verified save" : "file modified"));
    return;
}
// A separate process proves the timestamp survives without any session state.
if (args.Length == 4 && args[0] == "--reopen")
{
    var persisted = LastEditTime.ReadForFile(args[1], File.ReadAllBytes(args[1]), args[2]);
    var expected = DateTimeOffset.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
    if (persisted != expected || LastEditTime.Format(persisted, expected.AddHours(2)) != "2 hr")
        throw new Exception("Fresh-process persisted timestamp/aging mismatch");
    Console.WriteLine("PASS fresh-process reopen retains verified timestamp and ages to 2 hr");
    return;
}
int passed = 0;
void Check(bool yes, string label) { if (!yes) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
void Fails(Action action) { try { action(); } catch(IOException) { return; } throw new Exception("Expected failure"); }
void CheckDisplayRestart(string path, string registry, LastEditTime.DisplayStamp stamp)
{
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    foreach (string argument in new[] { "--display-reopen", path, registry, stamp.Utc!.Value.ToString("O"), stamp.IsVerifiedSave.ToString() })
        start.ArgumentList.Add(argument);
    using var child = System.Diagnostics.Process.Start(start)!;
    bool exited = child.WaitForExit(30000);
    if (!exited) child.Kill(true);
    Check(exited && child.ExitCode == 0, "display survives restart with correct source: " + stamp.IsVerifiedSave);
}
var now = DateTimeOffset.UtcNow;
Check(LastEditTime.Format(null, now) == "", "unknown blank");
foreach (var invalid in new[] { DateTimeOffset.MinValue, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue, now.AddMinutes(2), now.ToOffset(TimeSpan.FromHours(2)) })
{
    Check(LastEditTime.Format(invalid, now) == "", "invalid/future/non-UTC age blank: " + invalid.ToString("O"));
    Check(LastEditTime.Tooltip(new(invalid, false), now) == "", "invalid/future tooltip blank");
}
foreach(var (seconds, text) in new[] {(0,"0 min"),(59,"0 min"),(60,"1 min"),(240,"4 min"),(3599,"59 min"),(3600,"1 hr"),(86400,"1 d"),(172800,"2 d"),(-60,"0 min"),(-61,"")})
    Check(LastEditTime.Format(now.AddSeconds(-seconds), now) == text, "relative " + seconds);
string dir = Path.Combine(Path.GetTempPath(), "red-last-edit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try {
    string p = Path.Combine(dir,"test.ins"), recovery = Path.Combine(dir,"recovery"), registry = Path.Combine(dir,"registry");
    DateTimeOffset? Read(string path) => LastEditTime.ReadForFile(path, File.ReadAllBytes(path), registry);
    File.WriteAllText(p, "{\"Sections\":[{\"Items\":[{\"ItemId\":7,\"ItemResultId\":\"stable\",\"Comments\":\"old\",\"Pictures\":[]}]}]}");
    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddHours(-3));
    LastEditTime.DisplayStamp Display(string path) => LastEditTime.ReadDisplayForFile(path, File.ReadAllBytes(path), registry);
    var unopened = Display(p);
    Check(unopened.Utc == new DateTimeOffset(File.GetLastWriteTimeUtc(p)) && !unopened.IsVerifiedSave,
        "unopened file gets actual UTC last-write fallback without VerifiedSaves");
    Check(LastEditTime.Format(unopened.Utc, now.AddMinutes(1)) == "3 hr", "unopened age is running, not blank");
    Check(LastEditTime.Tooltip(unopened, now).Contains("filesystem timestamp; not a verified RED save"), "fallback tooltip honest");
    CheckDisplayRestart(p, registry, unopened);
    bool fail = false;
    int writes = 0;
    var saver = new SurgicalSaveService(new FailedSaveRecoveryService(recovery), (path,json,expected) => {
        writes++; if(fail) throw new IOException("injected failure");
        return AtomicInspectionWriter.Write(path,json,expected);
    }, registry);
    var model = saver.Load(p);
    Check(Read(p) == null && Display(p) == unopened, "load does not fabricate verified save; display uses file modified time");
    model.Sections[0].Items[0].Comments = "edited";
    saver.Save(model);
    var stamp = Read(p);
    Check(stamp >= now && stamp <= DateTimeOffset.UtcNow, "verified save persists UTC provenance");
    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-2));
    var verifiedDisplay = Display(p);
    Check(verifiedDisplay.IsVerifiedSave && verifiedDisplay.Utc == stamp, "matching verified save wins over different filesystem timestamp");
    Check(LastEditTime.Tooltip(verifiedDisplay, DateTimeOffset.UtcNow).Contains("saved on this device (verified)"), "verified tooltip honest");
    CheckDisplayRestart(p, registry, verifiedDisplay);
    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    // Also support framework-dependent 'dotnet LastEditHarness.dll' invocation.
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    foreach (string argument in new[] { "--reopen", p, registry, stamp!.Value.ToString("O") })
        start.ArgumentList.Add(argument);
    using (var child = System.Diagnostics.Process.Start(start)!)
    {
        bool exited = child.WaitForExit(30000);
        if (!exited) child.Kill(true);
        Check(exited && child.ExitCode == 0, "persisted evidence survives a fresh RED-service process");
    }
    Check(JObject.Parse(File.ReadAllText(p))["RedLastSuccessfulSaveUtc"] == null, "no INS schema addition");
    saver.Save(model);
    Check(Read(p) == stamp && writes == 2, "no-op keeps timestamp and still calls writer");
    var reopened = saver.Load(p);
    saver.Save(reopened);
    Check(Read(p) == stamp, "reopen no-op preserves provenance");
    Check(LastEditTime.ReadForFile(Path.Combine(dir,".","test.ins"),File.ReadAllBytes(p),registry) == stamp, "normalized path identity");
    Check(LastEditTime.ReadForFile(p,new byte[]{1},registry) == null, "changed bytes hash mismatch is not verified");
    string saved = File.ReadAllText(p);
    reopened.Sections[0].Items[0].Comments = "unsaved";
    fail = true;
    Fails(() => saver.Save(reopened));
    Check(File.ReadAllText(p) == saved && Read(p) == stamp, "failed save preserves prior evidence");
    Check(Read(Directory.GetFiles(recovery,"*.ins").Single()) == null, "recovery never advertises success");
    fail = false;
    saver.Save(reopened);
    Check(Read(p) > stamp, "successful changed save advances timestamp");
    string other = Path.Combine(dir,"copy.ins"); File.Copy(p,other);
    Check(Read(other) == null, "copied bytes at different path unknown");
    string saveAs = Path.Combine(dir,"save-as.ins"); saver.Save(reopened,saveAs);
    Check(Read(saveAs).HasValue, "verified save-as recorded");

    // Real atomic replacement succeeds; the production final verification read fails.
    bool replaced = false;
    var ops = new AtomicInspectionWriter.Operations {
        Replace = (temp,target,backup) => { File.Replace(temp,target,backup); replaced = true; },
        ReadAllBytes = path => replaced && path == Path.GetFullPath(p) ? throw new IOException("final read failure") : File.ReadAllBytes(path)
    };
    var uncertain = new SurgicalSaveService(new FailedSaveRecoveryService(recovery),
        (path,json,expected) => AtomicInspectionWriter.Write(path,json,expected,ops),registry);
    var uncertainModel = uncertain.Load(p);
    uncertainModel.Sections[0].Items[0].Comments = "replacement landed but unverified";
    Fails(() => uncertain.Save(uncertainModel));
    Check(replaced && File.ReadAllText(p).Contains("replacement landed but unverified"), "actual replacement completed before readback failure");
    Check(Read(p) == null && !Display(p).IsVerifiedSave, "failed final readback never records successful save");
    Check(JObject.Parse(File.ReadAllText(p))["RedLastSuccessfulSaveUtc"] == null, "uncertain target contains no embedded stamp");

    string blocked = Path.Combine(dir,"blocked"); File.WriteAllText(blocked,"occupied");
    var noMetadata = new SurgicalSaveService(new FailedSaveRecoveryService(recovery), saveRegistryRoot: blocked);
    var noMetadataModel = noMetadata.Load(p); noMetadataModel.Sections[0].Items[0].Comments = "metadata unavailable";
    noMetadata.Save(noMetadataModel);
    noMetadata.Save(noMetadataModel);
    Check(File.ReadAllText(p).Contains("metadata unavailable") && LastEditTime.ReadForFile(p,File.ReadAllBytes(p),blocked) == null, "metadata I/O failure leaves successful save and retry working, verified evidence absent");
    var unavailableDisplay = LastEditTime.ReadDisplayForFile(p, File.ReadAllBytes(p), blocked);
    Check(unavailableDisplay.Utc.HasValue && !unavailableDisplay.IsVerifiedSave, "inaccessible metadata uses display fallback");
    foreach(string entry in Directory.GetFiles(registry,"*.json")) File.WriteAllText(entry,"bad json");
    Check(Read(saveAs) == null && Display(saveAs).Utc.HasValue && !Display(saveAs).IsVerifiedSave, "corrupt metadata uses fallback without claiming verification");

    // An external edit changes content and mtime but must not inherit the RED-save claim.
    LastEditTime.RecordSuccessfulSave(p, File.ReadAllBytes(p), registry);
    File.AppendAllText(p, "\nexternal content");
    File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(-12));
    var external = Display(p);
    Check(!external.IsVerifiedSave && external.Utc == new DateTimeOffset(File.GetLastWriteTimeUtc(p)), "external content mismatch falls back to actual file modified time");
    CheckDisplayRestart(p, registry, external);

    // Readable but malformed INS parser-fallback rows can still hydrate a display stamp.
    string malformed = Path.Combine(dir, "malformed.ins");
    File.WriteAllText(malformed, "not JSON");
    File.SetLastWriteTimeUtc(malformed, DateTime.UtcNow.AddMinutes(-5));
    Check(Display(malformed).Utc.HasValue && !Display(malformed).IsVerifiedSave, "readable parser-fallback file gets modified timestamp");
    foreach (var invalidTime in new[] { DateTime.UtcNow.AddHours(2), DateTime.UnixEpoch })
    {
        File.SetLastWriteTimeUtc(malformed, invalidTime);
        var invalidDisplay = Display(malformed);
        Check(invalidDisplay == default && LastEditTime.Format(invalidDisplay.Utc, now) == "", "future/sentinel filesystem timestamp blank safely");
    }

    // Invalid verified records must be rejected too, using the same formatter bounds.
    string isolated = Path.Combine(dir, "isolated-registry");
    LastEditTime.RecordSuccessfulSave(p, File.ReadAllBytes(p), isolated);
    string recordPath = Directory.GetFiles(isolated, "*.json").Single();
    var record = JObject.Parse(File.ReadAllText(recordPath));
    foreach (var invalid in new[] { DateTimeOffset.MinValue, DateTimeOffset.UnixEpoch, now.AddDays(1) })
    {
        record["SavedUtc"] = invalid.ToString("O");
        File.WriteAllText(recordPath, record.ToString());
        Check(LastEditTime.ReadForFile(p, File.ReadAllBytes(p), isolated) == null, "invalid verified timestamp rejected");
        var fallback = LastEditTime.ReadDisplayForFile(p, File.ReadAllBytes(p), isolated);
        Check(fallback.Utc == external.Utc && !fallback.IsVerifiedSave, "invalid verified record falls back to valid mtime");
    }

    // Snapshot aging requires neither file nor registry. UI wiring is statically guarded.
    File.Delete(p);
    Directory.Delete(registry, true);
    foreach (var snapshot in new[] { unopened, verifiedDisplay, external })
    {
        foreach (int minutes in new[] { 1, 2, 59, 60, 120 })
        {
            var clock = snapshot.Utc!.Value.AddMinutes(minutes);
            string expected = minutes < 60 ? $"{minutes} min" : $"{minutes / 60} hr";
            Check(LastEditTime.Format(snapshot.Utc, clock) == expected && LastEditTime.Tooltip(snapshot, clock) != "", "cached timestamp ages without files/registry: " + snapshot.IsVerifiedSave + "/" + minutes);
        }
    }
    Check(LastEditTime.ReadDisplayForFile(p, Array.Empty<byte>(), registry) == default, "missing file sentinel does not display ancient age");
} finally { Directory.Delete(dir,true); }
Console.WriteLine($"{passed} checks passed");
