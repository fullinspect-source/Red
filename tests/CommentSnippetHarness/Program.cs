using System;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;

int count = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
foreach (var blank in new string?[] { null, "", "  ", "\r\n\t", "\u2003" }) {
    Check(!ChecklistCommentSnippetPolicy.HasComment(blank), "blank collapses");
    Check(ChecklistCommentSnippetPolicy.Preview(blank) == "", "blank preview");
}
Check(ChecklistCommentSnippetPolicy.Preview("  Short comment.  ") == "Short comment.", "whole short comment");
Check(ChecklistCommentSnippetPolicy.Preview("First\r\nSecond\tthird") == "First Second third", "multiline single line");
var longComment = new string('x', 10000);
Check(ChecklistCommentSnippetPolicy.Preview(longComment) == longComment, "long text retained for WPF ellipsis");
foreach (double font in new[] { 12d, 14d, 16d, 20d, 28d }) {
    double height = ChecklistCommentSnippetPolicy.LineHeight(font) + 2;
    Check(height <= 16 && height >= 14, "bounded extra row height");
    foreach (double dpi in new[] { 1d, 1.25, 1.5, 2, 3 })
        Check((48 * dpi + height * dpi) / (48 * dpi) <= 1.34, "DPI-independent <=34% inline increase");
}
var first = new Item { ItemId = 7 };
var duplicate = new Item { ItemId = 7 };
int firstEvents = 0, duplicateEvents = 0;
first.PropertyChanged += (sender, e) => { Check(ReferenceEquals(sender, first) && e.PropertyName == "Comments", "owning model notification"); firstEvents++; };
duplicate.PropertyChanged += (_, _) => duplicateEvents++;
first.Comments = "typing";
first.Comments = "typing";
first.Comments = "typing more";
first.Comments = "  \n";
Check(firstEvents == 3 && duplicateEvents == 0, "changed only, duplicate ID isolated");
Check(!ChecklistCommentSnippetPolicy.HasComment(first.Comments), "whitespace clear collapses");
first.Comments = "";
Check(firstEvents == 4, "explicit empty clear not lost");
first.Comments = "line 1\nline 2";
var json = JsonConvert.SerializeObject(first);
Check(!json.Contains("PropertyChanged") && !json.Contains("_comments"), "no binding state in INS schema");
Check(JsonConvert.DeserializeObject<Item>(json)!.Comments == first.Comments, "full comment roundtrip unchanged");
Console.WriteLine($"{count} checks passed");
