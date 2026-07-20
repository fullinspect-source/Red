using System.Text.RegularExpressions;
using InspectionEditor.Services;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static int PageNumberFromName(string path, int fallback)
{
    var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(?:page[-_]?|_p)(\d+)", RegexOptions.IgnoreCase);
    return match.Success && int.TryParse(match.Groups[1].Value, out int page) ? page : fallback;
}

if (args.Length == 1 && Directory.Exists(args[0]))
{
    var pages = Directory.GetFiles(args[0], "*.txt")
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select((path, index) =>
        {
            string text = File.ReadAllText(path);
            return new FramingPageText
            {
                PageNumber = PageNumberFromName(path, index + 1),
                SheetName = FramingDesignParser.DetectSheetName(text),
                Text = text
            };
        })
        .ToList();

    var parsed = FramingDesignParser.Parse("sample.pdf", pages, extractionComplete: true);
    Console.WriteLine(parsed.StatusText);
    foreach (string line in parsed.GetSummaryLines())
        Console.WriteLine(line);
    return;
}

var oneStoryPages = new[]
{
    new FramingPageText
    {
        PageNumber = 2,
        SheetName = "SW1",
        Text = "This design was designed based on a windspeed of 136 mph (Vult), 105 mph (Vasd) and Exposure: B. Engineered Shear Wall-Green T-Ply."
    },
    new FramingPageText
    {
        PageNumber = 4,
        SheetName = "FR1",
        Text = "All exterior walls shall be 2x4 SPF Stud Grade @ 16 in. O.C. UNO. All interior walls shall be 2x4 SPF Stud Grade @ 24 in. O.C. UNO. All ceiling joists shall be 2x6 SYP No.2 @ 24 in. O.C. UNO."
    },
    new FramingPageText
    {
        PageNumber = 5,
        SheetName = "FR3",
        Text = "All rafters shall be 2x6 SYP No.2 @ 24 in. O.C. UNO. Hips, valleys, and ridges shall be 2x8 SYP No.2 UNO."
    },
    new FramingPageText
    {
        PageNumber = 8,
        SheetName = "FR0.1",
        Text = "Roof sheathing shall be minimum 7/16 inches thickness sheathing with 24/16 span rating. Floor sheathing shall be minimum 23/32 inches thickness T&G sheathing with 48/24 span rating. All sills on concrete slabs shall be treated lumber."
    }
};

var oneStory = FramingDesignParser.Parse("one-story.pdf", oneStoryPages, extractionComplete: true);
Assert(oneStory.Values.Any(v => v.FieldKey == "WindUltimate" && v.Value == "136 mph Vult"), "ultimate wind missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "WindContinuous" && v.Value == "105 mph Vasd"), "continuous wind missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "ExposureCategory" && v.Value == "Exposure B"), "exposure missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "RafterSchedule" && v.Value.Contains("2x6 SYP No.2")), "rafter rule missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "HipValleyRidgeSchedule" && v.Value.Contains("2x8 SYP No.2")), "hip rule missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "RoofSheathing" && v.Value.Contains("7/16\"")), "roof sheathing missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "FloorSheathing" && v.Value.Contains("23/32\"")), "floor sheathing missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "StructuralSheathing" && v.Value.Contains("3\" edge / 6\" middle")), "structural T-Ply nailing missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "FloorTypeNotApplicable" && v.Value == "NI"), "one-story floor type NI missing");
Assert(oneStory.Values.Any(v => v.FieldKey == "FloorProductNotApplicable" && v.Value == "NI"), "one-story floor product NI missing");

var twoStoryPages = oneStoryPages.Concat(new[]
{
    new FramingPageText
    {
        PageNumber = 9,
        SheetName = "FJ1",
        Text = "SECOND FLOOR FRAMING PLAN. I-Joist per Plan."
    }
}).ToList();
var twoStory = FramingDesignParser.Parse("two-story.pdf", twoStoryPages, extractionComplete: true);
Assert(twoStory.Values.Any(v => v.FieldKey == "FloorType" && v.Value == "I-Joist"), "I-Joist floor type missing");
Assert(!twoStory.Values.Any(v => v.FieldKey == "FloorTypeNotApplicable"), "two-story floor type incorrectly marked NI");
Assert(twoStory.Values.Any(v => v.FieldKey == "FloorProductNotApplicable" && v.Value == "NI"), "I-Joist species/grade NI missing");

var exteriorNonStructural = FramingDesignParser.Parse("exterior-nonstructural.pdf", new[]
{
    new FramingPageText
    {
        PageNumber = 2,
        SheetName = "SW1",
        Text = "Non-structural exterior T-Ply at the thermal boundary acts as the air barrier."
    }
}, extractionComplete: true);
Assert(exteriorNonStructural.Values.Any(v => v.FieldKey == "ExteriorNonStructuralSheathing" &&
                                                   v.Value.Contains("6\" edge / 6\" middle")),
    "exterior air-barrier T-Ply rule missing");

var interiorNonStructural = FramingDesignParser.Parse("interior-nonstructural.pdf", new[]
{
    new FramingPageText
    {
        PageNumber = 2,
        SheetName = "FR1",
        Text = "Interior non-structural T-Ply outside the thermal boundary."
    }
}, extractionComplete: true);
Assert(interiorNonStructural.Values.Any(v => v.FieldKey == "InteriorNonStructuralSheathing" &&
                                                   v.Value.Contains("6\" edge / 12\" middle")),
    "interior nonthermal T-Ply rule missing");

var fj2 = FramingDesignParser.Parse("fj2.pdf", oneStoryPages.Concat(new[]
{
    new FramingPageText { PageNumber = 9, SheetName = "FJ2", Text = "FJ2 FLOOR JOIST LAYOUT — second level joists per plan" }
}), extractionComplete: true);
Assert(fj2.HasSecondFloorDesign, "FJ2 floor-system evidence missing");
Assert(!fj2.Values.Any(v => v.FieldKey is "FloorTypeNotApplicable" or "FloorProductNotApplicable"),
    "FJ2 incorrectly produced floor NI");

var incomplete = FramingDesignParser.Parse("incomplete.pdf", oneStoryPages, extractionComplete: false);
Assert(!incomplete.Values.Any(v => v.FieldKey is "FloorTypeNotApplicable" or "FloorProductNotApplicable"),
    "incomplete extraction incorrectly produced floor NI");

var openWebSpecified = FramingDesignParser.Parse("open-web.pdf", new[]
{
    new FramingPageText { PageNumber = 7, SheetName = "FJ2", Text = "OPEN WEB FLOOR TRUSSES by MiTek Series 4x2" }
}, extractionComplete: true);
Assert(openWebSpecified.Values.Any(v => v.FieldKey == "FloorType" && v.Value == "Open Web"),
    "plural open-web floor truss type missing");
Assert(openWebSpecified.Values.Any(v => v.FieldKey == "FloorProduct" && v.Value.Contains("MiTek")),
    "specified open-web product missing");
Assert(!openWebSpecified.Values.Any(v => v.FieldKey == "FloorProductNotApplicable"),
    "specified open-web product incorrectly marked NI");

var unrelatedWind = FramingDesignParser.Parse("wind-guard.pdf", new[]
{
    new FramingPageText
    {
        PageNumber = 2,
        SheetName = "SW1",
        Text = "This plan was designed based on a windspeed of 136 mph (Vult), Exposure: B. Separate component test speed 90 mph (test)."
    }
}, extractionComplete: true);
Assert(unrelatedWind.Values.Any(v => v.FieldKey == "WindUltimate" && v.Value == "136 mph Vult"),
    "labeled Vult missing");
Assert(!unrelatedWind.Values.Any(v => v.FieldKey == "WindContinuous"),
    "unrelated MPH value was fabricated into Vasd");

var unrelatedTply = FramingDesignParser.Parse("tply-guard.pdf", new[]
{
    new FramingPageText
    {
        PageNumber = 2,
        SheetName = "SW1",
        Text = "Engineered Shear Wall-Green T-Ply. Exterior air barrier requirements apply. Gypsum is non-structural."
    }
}, extractionComplete: true);
Assert(!unrelatedTply.Values.Any(v => v.FieldKey is "ExteriorNonStructuralSheathing" or "InteriorNonStructuralSheathing"),
    "unrelated page terms fabricated non-structural T-Ply");

Assert(FramingDesignParser.AppendValue("SPF #3", "Treated Base") == "SPF #3 | Treated Base", "append behavior failed");
Assert(FramingDesignParser.ValueAlreadyContains("SPF #3 | Treated Base", "treated base"), "used-value suppression failed");

Console.WriteLine($"PASS framing parser self-test: {oneStory.Values.Count} one-story values; {twoStory.Values.Count} two-story values");
