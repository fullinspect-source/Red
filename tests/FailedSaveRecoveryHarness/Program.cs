using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Text;

int passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); passed++; }
Exception Fail(Action action) { try { action(); } catch (Exception ex) { return ex; } throw new Exception("Unexpected save success"); }
string dir = Path.Combine(Path.GetTempPath(), "red-recovery-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string path = Path.Combine(dir, "source.ins"), root = Path.Combine(dir, "local", "Recovery");
    const string original = """
    {"UnknownRoot":{"keep":42},"Sections":[{"Items":[{"ItemId":7,"ItemResultId":"stable-id","UnknownItem":"keep","Comments":"old","Pictures":[]}]}],"Attachments":[{"Existing":"keep"}]}
    """;
    File.WriteAllText(path, original);
    string? candidate = null;
    bool fail = true;
    var winError = new IOException("simulated replace error 1175", unchecked((int)0x80070497));
    var ops = new AtomicInspectionWriter.Operations { Replace = (_, _, _) => throw winError, Delay = _ => { } };
    var service = new SurgicalSaveService(new FailedSaveRecoveryService(root), (p, json, expected) =>
    {
        candidate = json;
        return fail ? AtomicInspectionWriter.Write(p, json, expected, ops) : AtomicInspectionWriter.Write(p, json, expected);
    });
    var report = service.Load(path);
    var jsonField = typeof(SurgicalSaveService).GetField("_originalJson", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var bytesField = typeof(SurgicalSaveService).GetField("_lastSavedBytes", BindingFlags.NonPublic | BindingFlags.Instance)!;
    object previous = jsonField.GetValue(service)!, expectedBytes = bytesField.GetValue(service)!;
    report.Sections[0].Items[0].Comments = "unsaved full snapshot é";
    report.Sections[0].Items[0].Pictures.Add(new Picture { Data = "cGhvdG8=", Title = "new photo", Comment = "caption" });
    report.ExtensionData!["RedPlanCheck"] = JObject.Parse("{\"review\":\"pending\"}");
    report.Attachments!.Add(JObject.Parse("{\"New\":\"plan\"}"));
    service.SetResult(4, 2, "pending result");
    var error = Fail(() => service.Save(report));
    string recovery = Directory.GetFiles(root, "*.ins").Single();
    Check(File.ReadAllBytes(recovery).SequenceEqual(new UTF8Encoding(false).GetBytes(candidate!)), "recovery bytes equal entire surgically patched candidate");
    var snapshot = JObject.Parse(File.ReadAllText(recovery));
    Check((int?)snapshot["UnknownRoot"]!["keep"] == 42 && (string?)snapshot["Sections"]![0]!["Items"]![0]!["UnknownItem"] == "keep", "unknown original fields retained");
    Check((string?)snapshot["Sections"]![0]!["Items"]![0]!["Pictures"]![0]!["Image"] == "cGhvdG8=" && snapshot["Attachments"]!.Count() == 2 && (int?)snapshot["StatusId"] == 4 && snapshot["RedPlanCheck"] != null, "photos attachments metadata and result all retained");
    Check(error.Message.Contains(recovery) && error.Message.Contains("errorCode=1175") && ReferenceEquals(error.InnerException?.InnerException, winError), "exact path original diagnostics and exception chain surfaced");
    Check(File.ReadAllText(path) == original && ReferenceEquals(previous, jsonField.GetValue(service)) && ReferenceEquals(expectedBytes, bytesField.GetValue(service)) && service.FilePath == path, "failure preserves source original JSON expected bytes and source path");
    Check(report.Sections[0].Items[0].Comments == "unsaved full snapshot é", "unsaved model edits retained");
    byte[] firstSnapshot = File.ReadAllBytes(recovery);
    Fail(() => service.Save(report));
    Check(Directory.GetFiles(root, "*.ins").Length == 1 && File.ReadAllBytes(recovery).SequenceEqual(firstSnapshot), "identical failed candidates reuse freshly verified recovery");
    report.Sections[0].Items[0].Comments = "new changed unsaved text";
    Fail(() => service.Save(report));
    Check(Directory.GetFiles(root, "*.ins").Length == 2 && File.ReadAllBytes(recovery).SequenceEqual(firstSnapshot), "changed failed candidate preserves prior recovery and creates new snapshot");
    Check(Directory.GetFiles(root, "*.tmp").Length == 0, "atomic recovery publication leaves no temporary files");
    fail = false;
    service.Save(report);
    Check(File.ReadAllText(path) == candidate && Directory.GetFiles(root, "*.ins").Length == 2, "retry succeeds using unchanged expected bytes and creates no extra recovery");

    string blocked = Path.Combine(dir, "blocked-root"); File.WriteAllText(blocked, "occupied");
    var broken = new SurgicalSaveService(new FailedSaveRecoveryService(blocked), (_, _, _) => throw winError);
    var model = broken.Load(path);
    var both = Fail(() => broken.Save(model));
    Check(both.Message.Contains("Local recovery also failed") && both.Message.Contains("1175") && ReferenceEquals(both.InnerException, winError) && !both.Message.Contains("was verified at"), "recovery I/O failure reported honestly without losing primary error");

    foreach (bool failFinal in new[] { false, true })
    {
        string badRoot = Path.Combine(dir, failFinal ? "bad-final" : "bad-temp");
        var bad = new FailedSaveRecoveryService(badRoot, p => p.EndsWith(".tmp") == !failFinal ? new byte[] { 0 } : File.ReadAllBytes(p));
        var corrupt = Fail(() => bad.Preserve("{\"full\":true}"));
        Check(corrupt.Message.Contains("verification failed") && Directory.GetFiles(badRoot, "*.tmp").Length == 0, failFinal ? "post-publish verification failure throws" : "pre-publish verification failure throws and cleans temp");
    }
    var dedup = new FailedSaveRecoveryService(Path.Combine(dir, "dedup"));
    string d1 = dedup.Preserve("{\"full\":true}");
    File.WriteAllText(d1, "changed externally");
    string d2 = dedup.Preserve("{\"full\":true}");
    Check(d1 != d2 && File.ReadAllText(d1) == "changed externally", "altered prior recovery never overwritten or advertised");
    File.Delete(d2);
    string d3 = dedup.Preserve("{\"full\":true}");
    Check(d3 != d2 && File.Exists(d3), "missing recovery recreated at independent path");
    var invalid = Fail(() => new FailedSaveRecoveryService(Path.Combine(dir, "invalid")).Preserve("not json"));
    Check(!Directory.Exists(Path.Combine(dir, "invalid")), "invalid JSON never published");
    string other = Path.Combine(dir, "other.ins"); File.WriteAllText(other, "unrelated");
    var saveAsError = Fail(() => service.Save(report, other));
    Check(File.ReadAllText(other) == "unrelated" && service.FilePath == path && saveAsError.Message.Contains("was verified at"), "Save As conflict recovers without changing source or unrelated target");
    Console.WriteLine($"PASS {passed} failed-save recovery checks");
}
finally { Directory.Delete(dir, recursive: true); }
