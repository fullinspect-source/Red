using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

var ordinaryHidden = new Item { HidePicturesButton = true, HideCommentsButton = true };
Check(!ChecklistItemVisibility.ShouldShow(ordinaryHidden), "ordinary fully hidden item stays hidden");

var normal = new Item { HidePicturesButton = false, HideCommentsButton = true };
Check(ChecklistItemVisibility.ShouldShow(normal), "ordinary editable item stays visible");

const string buyerWalkItemJson = """
{
  "ItemId": 15842,
  "Name": "Microwave Serial Number",
  "Number": "2.23",
  "ControlName": "Text",
  "HideCommentsButton": true,
  "HidePicturesButton": true,
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
    Check(ChecklistItemVisibility.ShouldShow(buyerWalk), "buyer-walk hidden serial item with photo is visible");

    buyerWalk.Pictures[0].Data = "";
    Check(!ChecklistItemVisibility.ShouldShow(buyerWalk), "empty photo shell does not reveal hidden item");

    buyerWalk.Pictures[0].Data = "  ";
    Check(!ChecklistItemVisibility.ShouldShow(buyerWalk), "whitespace photo shell does not reveal hidden item");
}
finally
{
    Directory.Delete(tempDir, recursive: true);
}

Console.WriteLine("All 6 checklist visibility checks passed.");
