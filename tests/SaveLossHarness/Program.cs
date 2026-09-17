using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

LifecycleControlFlow.LifecycleProbe.Run();
AtomicRetryProbe.Run();
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Throws(Action action, string name) { try { action(); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Check(true, name); return; } throw new Exception("Expected IOException: " + name); }
string dir = Path.Combine(Path.GetTempPath(), "red-save-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string path = Path.Combine(dir, "report.ins");
    const string original = """
    {"UnknownRoot":{"keep":42},"StatusId":1,"Sections":[{"SectionId":1,"Items":[
      {"ItemId":7,"Number":"2.1","Value":"old","Comments":"old comment","IsCopied":false,"ResultSortOrder":0,"UnknownItem":"preserved","Pictures":[]},
      {"ItemId":7,"Number":"2.1","Value":"duplicate","Comments":"duplicate comment","IsCopied":true,"ResultSortOrder":1,"Pictures":[]}
    ]}],"Attachments":[{"UnknownAttachment":"keep"}]}
    """;
    File.WriteAllText(path, original);
    var save = new SurgicalSaveService();
    var report = save.Load(path);
    var first = report.Sections[0].Items[0]; var duplicate = report.Sections[0].Items[1];
    Check(EditorEditService.SetComment(report, first, "typed without blur"), "capture comment without focus change");
    Check(EditorEditService.SetValue(report, first, "12.5"), "capture value without focus change");
    save.Save(report);
    var disk = JObject.Parse(File.ReadAllText(path));
    Check((string?)disk["Sections"]![0]!["Items"]![0]!["Comments"] == "typed without blur", "close/save sees still-focused comment");
    Check((string?)disk["Sections"]![0]!["Items"]![0]!["Value"] == "12.5", "close/save sees still-focused value");
    Check(duplicate.Comments == "duplicate comment" && (string?)disk["Sections"]![0]!["Items"]![1]!["Value"] == "duplicate", "duplicate ID is not edit owner");
    Check((int?)disk["UnknownRoot"]!["keep"] == 42 && (string?)disk["Sections"]![0]!["Items"]![0]!["UnknownItem"] == "preserved", "unknown fields preserved");
    Check(File.ReadAllText(Directory.GetFiles(dir, "report.ins.red-save-backup-*.completed").Single()) == original, "previous report retained in backup");
    Check(!EditorEditService.SetComment(report, first, "typed without blur"), "unchanged capture stays clean");
    EditorEditService.SetComment(report, first, ""); EditorEditService.SetValue(report, first, ""); save.Save(report);
    disk = JObject.Parse(File.ReadAllText(path));
    Check((string?)disk["Sections"]![0]!["Items"]![0]!["Comments"] == "" && (string?)disk["Sections"]![0]!["Items"]![0]!["Value"] == "", "intentional empty fields persist");
    first.Value = null; save.Save(report);
    Check(JObject.Parse(File.ReadAllText(path))["Sections"]![0]!["Items"]![0]!["Value"]!.Type == JTokenType.Null, "null value clears original");
    var otherReport = new InspectionFile();
    Check(!EditorEditService.SetComment(otherReport, first, "stale") && first.Comments == "", "old report owner rejected");
    report.Sections[0].Items.Remove(duplicate);
    Check(!EditorEditService.SetValue(report, duplicate, "stale"), "removed item editor rejected");
    var beforeFailure = File.ReadAllBytes(path);
    first.Comments = "retain after failure";
    File.WriteAllText(path, "{\"external\":true}");
    Throws(() => save.Save(report), "external update blocks overwrite");
    Check(File.ReadAllText(path) == "{\"external\":true}" && first.Comments == "retain after failure", "conflict preserves disk and edits");
    File.WriteAllBytes(path, beforeFailure);
    // An obstruction at the legacy shared backup name must not block a fresh save.
    Directory.CreateDirectory(path + ".red-save-backup");
    save.Save(report);
    Check((string?)JObject.Parse(File.ReadAllText(path))["Sections"]![0]!["Items"]![0]!["Comments"] == "retain after failure",
        "legacy backup obstruction no longer blocks save");
    Check(Directory.Exists(path + ".red-save-backup"), "legacy backup obstruction left untouched");
    Check(Directory.GetFiles(dir, "report.ins.red-save-backup-*.completed").Any(file => File.ReadAllBytes(file).SequenceEqual(beforeFailure)),
        "obstructed legacy backup still retains previous report in unique backup");
    Check(!Directory.GetFiles(dir, "*.tmp").Any(), "successful save cleans temporary siblings");
    Directory.Delete(path + ".red-save-backup");
    save.SetResult(4, 2, "retry result"); save.Save(report);
    disk = JObject.Parse(File.ReadAllText(path));
    Check((string?)disk["Sections"]![0]!["Items"]![0]!["Comments"] == "retain after failure" && (int?)disk["StatusId"] == 4, "retry persists edits and result");
    string other = Path.Combine(dir, "other.ins"); File.WriteAllText(other, "unrelated");
    Throws(() => save.Save(report, other), "Save As refuses unrelated existing target");
    Check(save.FilePath == path && File.ReadAllText(other) == "unrelated", "failed Save As retains ownership and target");
    string fresh = Path.Combine(dir, "fresh.ins"); save.Save(report, fresh);
    Check(save.FilePath == fresh && JObject.Parse(File.ReadAllText(fresh))["StatusId"]!.Value<int>() == 4, "new Save As succeeds");
    Console.WriteLine($"{passed} behavioral checks passed");
}
finally { Directory.Delete(dir, recursive: true); }
