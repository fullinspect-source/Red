using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;

if (args.Length < 2) throw new Exception("Usage: <current SCI.ins> <archive dir>");
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}

InspectionFile Load(string path) => JsonConvert.DeserializeObject<InspectionFile>(File.ReadAllText(path))!;
Item RequiredMemo(InspectionFile file) => file.Sections.SelectMany(s => s.Items)
    .Single(i => i.Required && string.Equals(i.ControlName, "Memo", StringComparison.OrdinalIgnoreCase));

var current = Load(args[0]);
var currentMemo = RequiredMemo(current);
Check(ItemRequirementService.RequiresComment(currentMemo), "required Memo is a required comment");
Check(!ItemRequirementService.RequiresValue(currentMemo), "required Memo is not a required value");
Check(ItemRequirementService.IsPrimaryRequirementSatisfied(currentMemo), "current SCI comment satisfies requirement despite blank Value");

var edited = Load(args[0]);
var editedMemo = RequiredMemo(edited);
object? originalValue = editedMemo.Value;
Check(EditorEditService.SetComment(edited, editedMemo, "Live required comment"), "comment edit captured through production editor service");
var reopened = JsonConvert.DeserializeObject<InspectionFile>(JsonConvert.SerializeObject(edited))!;
var reopenedMemo = RequiredMemo(reopened);
Check(reopenedMemo.Comments == "Live required comment", "comment edit survives report serialization and reopen");
Check(Equals(reopenedMemo.Value, originalValue), "comment edit does not fabricate a Value");
Check(ItemRequirementService.IsPrimaryRequirementSatisfied(reopenedMemo), "reopened comment satisfies requirement");

var commentMissing = new Item { Required = true, ControlName = "Memo", Value = "anything", Comments = "  " };
Check(ItemRequirementService.IsPrimaryRequirementMissing(commentMissing), "Value cannot satisfy required Memo when comment is blank");
var ordinary = new Item { Required = true, ControlName = "Text", Value = "42", Comments = "" };
Check(ItemRequirementService.RequiresValue(ordinary), "ordinary required Text remains value-required");
Check(ItemRequirementService.IsPrimaryRequirementSatisfied(ordinary), "ordinary required Text is satisfied by Value");

int archiveCount = 0;
foreach (string path in Directory.EnumerateFiles(args[1], "*SCI*.ins"))
{
    var memo = RequiredMemo(Load(path));
    Check(ItemRequirementService.RequiresComment(memo), Path.GetFileName(path) + " routes required Memo to Comments");
    if (!string.IsNullOrWhiteSpace(memo.Comments))
        Check(ItemRequirementService.IsPrimaryRequirementSatisfied(memo), Path.GetFileName(path) + " existing comment satisfies requirement");
    archiveCount++;
}
Check(archiveCount >= 8, "real SCI archive sample coverage");
Console.WriteLine($"{passed} item requirement checks passed across {archiveCount} archived SCI files");
