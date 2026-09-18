using InspectionEditor.Services;
using Newtonsoft.Json.Linq;
int passed = 0;
void Check(bool yes, string label) { if (!yes) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
void Fails(Action action) { try { action(); } catch(IOException) { return; } throw new Exception("Expected failure"); }
var now = DateTimeOffset.UtcNow;
Check(LastEditTime.Format(null, now) == "", "unknown blank");
foreach(var (seconds, text) in new[] {(0,"0 min"),(59,"0 min"),(60,"1 min"),(240,"4 min"),(3599,"59 min"),(3600,"1 hr"),(86400,"1 d"),(172800,"2 d"),(-60,"0 min"),(-61,"")})
    Check(LastEditTime.Format(now.AddSeconds(-seconds), now) == text, "relative " + seconds);
string dir = Path.Combine(Path.GetTempPath(), "red-last-edit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try {
    string p = Path.Combine(dir,"test.ins"), recovery = Path.Combine(dir,"recovery"), registry = Path.Combine(dir,"registry");
    DateTimeOffset? Read(string path) => LastEditTime.ReadForFile(path, File.ReadAllBytes(path), registry);
    File.WriteAllText(p, "{\"Sections\":[{\"Items\":[{\"ItemId\":7,\"ItemResultId\":\"stable\",\"Comments\":\"old\",\"Pictures\":[]}]}]}");
    File.SetLastWriteTimeUtc(p, DateTime.UtcNow);
    bool fail = false;
    int writes = 0;
    var saver = new SurgicalSaveService(new FailedSaveRecoveryService(recovery), (path,json,expected) => {
        writes++; if(fail) throw new IOException("injected failure");
        return AtomicInspectionWriter.Write(path,json,expected);
    }, registry);
    var model = saver.Load(p);
    Check(Read(p) == null, "download mtime/load do not imply edit");
    model.Sections[0].Items[0].Comments = "edited";
    saver.Save(model);
    var stamp = Read(p);
    Check(stamp >= now && stamp <= DateTimeOffset.UtcNow, "verified save persists UTC provenance");
    Check(JObject.Parse(File.ReadAllText(p))["RedLastSuccessfulSaveUtc"] == null, "no INS schema addition");
    saver.Save(model);
    Check(Read(p) == stamp && writes == 2, "no-op keeps timestamp and still calls writer");
    var reopened = saver.Load(p);
    saver.Save(reopened);
    Check(Read(p) == stamp, "reopen no-op preserves provenance");
    Check(LastEditTime.ReadForFile(Path.Combine(dir,".","test.ins"),File.ReadAllBytes(p),registry) == stamp, "normalized path identity");
    Check(LastEditTime.ReadForFile(p,new byte[]{1},registry) == null, "changed bytes hash mismatch blank");
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
    Check(Read(p) == null, "failed final readback never records successful save");
    Check(JObject.Parse(File.ReadAllText(p))["RedLastSuccessfulSaveUtc"] == null, "uncertain target contains no embedded stamp");

    string blocked = Path.Combine(dir,"blocked"); File.WriteAllText(blocked,"occupied");
    var noMetadata = new SurgicalSaveService(new FailedSaveRecoveryService(recovery), saveRegistryRoot: blocked);
    var noMetadataModel = noMetadata.Load(p); noMetadataModel.Sections[0].Items[0].Comments = "metadata unavailable";
    noMetadata.Save(noMetadataModel);
    noMetadata.Save(noMetadataModel);
    Check(File.ReadAllText(p).Contains("metadata unavailable") && LastEditTime.ReadForFile(p,File.ReadAllBytes(p),blocked) == null, "metadata I/O failure leaves successful save and retry working, age blank");
    foreach(string entry in Directory.GetFiles(registry,"*.json")) File.WriteAllText(entry,"bad json");
    Check(Read(saveAs) == null, "corrupt metadata blank");
} finally { Directory.Delete(dir,true); }
Console.WriteLine($"{passed} checks passed");
