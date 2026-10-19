using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

// F-004 F2 / A-004-17: HidePicturesButton and HideCommentsButton never hide a row.
var bothFlags = new Item { HidePicturesButton = true, HideCommentsButton = true };
Check(ChecklistItemVisibility.ShouldShow(bothFlags), "A-004-17 item with both Hide*Button flags and no photo is visible");

var normal = new Item { HidePicturesButton = false, HideCommentsButton = true };
Check(ChecklistItemVisibility.ShouldShow(normal), "ordinary editable item stays visible");

// F-004 F1: only Hide true keeps a row out, as INSPECT's section builder does.
var hidden = new Item { ExtensionData = new Dictionary<string, object> { ["Hide"] = new JValue(true) } };
Check(!ChecklistItemVisibility.ShouldShow(hidden), "Hide true is not shown");

const string buyerWalkItemJson = """
{
  "ItemId": 15842,
  "Name": "Microwave Serial Number",
  "Number": "2.23",
  "ControlName": "Text",
  "Hide": false,
  "HideCommentsButton": true,
  "HidePicturesButton": true,
  "HideAddButton": true,
  "Pictures": [
    { "Path": null, "Caption": "Picture", "Description": null, "Image": "/9j/valid-base64-photo" }
  ]
}
""";
string tempDir = Path.Combine(Path.GetTempPath(), "red-hidden-photo-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    string file = Path.Combine(tempDir, "buyer-walk.ins");
    File.WriteAllText(file, $"{{\"Sections\":[{{\"SectionId\":1,\"Items\":[{buyerWalkItemJson}]}}]}}");
    var buyerWalk = new SurgicalSaveService().Load(file).Sections[0].Items[0];
    Check(buyerWalk.Pictures[0].Data == "/9j/valid-base64-photo", "production loader maps INS Image into RED photo data");
    Check(ChecklistItemVisibility.ShouldShow(buyerWalk), "buyer-walk serial item with all Hide*Button flags is visible");

    buyerWalk.Pictures.Clear();
    Check(ChecklistItemVisibility.ShouldShow(buyerWalk), "the same item without a photo is still visible");
}
finally
{
    Directory.Delete(tempDir, recursive: true);
}

Console.WriteLine("All 7 checklist visibility checks passed.");
