# Reinspection Guardrails & Free Reinspection Alerts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hard-coded inspection result guardrails with a data-driven spreadsheet config and add on-load free reinspection alerts that OCR prior PDFs and offer to open eligible companion files.

**Architecture:** A new `InspectionTypeService` loads a locally-cached CSV (`inspection_types.csv`) into typed `InspectionTypeConfig` objects; a new `FreeReinspectionChecker` uses the existing PdfPig/Tesseract OCR pipeline to detect prior "fail-next" results and surface alerts in the acknowledgment window; `ResultPickerWindow` consumes the config to show/gray buttons dynamically; `MainWindow` wires everything together with popup suppression for reinspection trips and companion windows.

**Tech Stack:** C# / WPF / .NET 8, PdfPig (text extraction), Tesseract + Docnet.Core (OCR fallback — already in project), existing `DataUpdateService` download pattern.

---

## File Map

| Action | File | Responsibility |
|--------|------|---------------|
| Create | `Services/InspectionTypeService.cs` | Model + CSV parser + in-memory cache |
| Create | `Services/FreeReinspectionChecker.cs` | Async OCR check + MyList scan |
| Modify | `Services/DataUpdateService.cs` | Add `inspection_types.csv` as third download target |
| Modify | `ResultPickerWindow.xaml.cs` | Data-driven buttons with gray+reason states |
| Modify | `RulesAcknowledgmentWindow.xaml` | Add amber panel for free reinspection alerts |
| Modify | `RulesAcknowledgmentWindow.xaml.cs` | Populate amber panel, expose `SelectedCompanionPaths` |
| Modify | `MainWindow.xaml.cs` | Wire checker, pass config+flags, open companions, suppress popup |

---

## Task 1: `InspectionTypeConfig` model + `InspectionTypeService`

**Files:**
- Create: `Services/InspectionTypeService.cs`

- [ ] **Step 1: Create the file with model + service**

`Services/InspectionTypeService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InspectionEditor.Services
{
    public class InspectionTypeConfig
    {
        public string InsType { get; set; } = "";
        public string Alias { get; set; } = "";
        public string OfferFileLabel { get; set; } = "";
        public List<string> FreeReinspectionTypes { get; set; } = new();  // col D
        public List<string> ExpirationStageTypes { get; set; } = new();   // col E
        public bool EngineerReview { get; set; }       // col F
        public bool ShowPass { get; set; }             // col G
        public bool ShowComplete { get; set; }         // col H
        public bool ShowCorrectAndProceed { get; set; }// col I
        public bool ShowFailNext { get; set; }         // col J
        public bool ShowFailPO { get; set; }           // col K
    }

    public class InspectionTypeService
    {
        private static readonly string CsvPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory,
            "inspection_types.csv");

        private Dictionary<string, InspectionTypeConfig>? _cache;

        public InspectionTypeConfig? GetConfig(string insType)
        {
            _cache ??= LoadAll();
            return _cache.TryGetValue(insType.Trim().ToUpperInvariant(), out var cfg) ? cfg : null;
        }

        // Called after DataUpdateService downloads a fresh CSV to bust the in-memory cache.
        public void InvalidateCache() => _cache = null;

        private Dictionary<string, InspectionTypeConfig> LoadAll()
        {
            var result = new Dictionary<string, InspectionTypeConfig>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(CsvPath)) return result;

            try
            {
                var lines = File.ReadAllLines(CsvPath);
                foreach (var line in lines.Skip(1)) // row 1 is header
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var f = ParseCsvLine(line);
                    if (f.Count < 11) continue;

                    string Get(int i) => i < f.Count ? f[i].Trim() : "";
                    bool GetBool(int i) => Get(i).Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    List<string> GetCodes(int i) => Get(i)
                        .Split(new[] { " or ", ",", "/" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().ToUpperInvariant())
                        .Where(s => s.Length > 0)
                        .ToList();

                    var cfg = new InspectionTypeConfig
                    {
                        InsType            = Get(0).ToUpperInvariant(),
                        Alias              = Get(1),
                        OfferFileLabel     = Get(2),
                        FreeReinspectionTypes = GetCodes(3),
                        ExpirationStageTypes  = GetCodes(4),
                        EngineerReview     = GetBool(5),
                        ShowPass           = GetBool(6),
                        ShowComplete       = GetBool(7),
                        ShowCorrectAndProceed = GetBool(8),
                        ShowFailNext       = GetBool(9),
                        ShowFailPO         = GetBool(10),
                    };

                    if (!string.IsNullOrEmpty(cfg.InsType))
                        result[cfg.InsType] = cfg;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InspectionTypeService load error: {ex.Message}");
            }

            return result;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; }
                else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
                else { current.Append(c); }
            }
            fields.Add(current.ToString());
            return fields;
        }
    }
}
```

- [ ] **Step 2: Manual smoke test — verify parsing**

Temporarily add this block to `App.xaml.cs` `OnStartup`, run the app, and check the debug output window:

```csharp
var svc = new InspectionEditor.Services.InspectionTypeService();
var cpr = svc.GetConfig("CPR");
System.Diagnostics.Debug.WriteLine($"CPR ShowPass={cpr?.ShowPass}, Free={string.Join(",", cpr?.FreeReinspectionTypes ?? new())}");
// Expected: CPR ShowPass=True, Free=CPP
var bwt = svc.GetConfig("BWT");
System.Diagnostics.Debug.WriteLine($"BWT ShowComplete={bwt?.ShowComplete}");
// Expected: BWT ShowComplete=True
```

Remove the block after verifying.

- [ ] **Step 3: Commit**

```bash
git add Services/InspectionTypeService.cs
git commit -m "feat: add InspectionTypeService with CSV-driven InspectionTypeConfig model"
```

---

## Task 2: `DataUpdateService` — add `inspection_types.csv` download

**Files:**
- Modify: `Services/DataUpdateService.cs`

- [ ] **Step 1: Add URL constant, path constant, and CSV validator**

At the top of `DataUpdateService`, alongside the existing constants (after line 33 `INSPECTOR_STATS_URL`), add:

```csharp
private const string INSPECTION_TYPES_URL =
    "https://docs.google.com/spreadsheets/d/1tuT8L7OFWzebwsJ0qe9tegxwier-knASgWpp70qJbqY/export?format=csv";
private static readonly string InspectionTypesPath =
    Path.Combine(AppFolder, "inspection_types.csv");
```

- [ ] **Step 2: Update `DownloadIfNewerAsync` to accept a custom validator**

The current private method signature is:
```csharp
private static async Task DownloadIfNewerAsync(string url, string localPath)
```

Change it to accept an optional validator (default = JSON check):

```csharp
private static async Task DownloadIfNewerAsync(
    string url,
    string localPath,
    Func<string, bool>? isValid = null)
{
    if (url.Contains("PLACEHOLDER")) return;

    isValid ??= content => content.StartsWith("{") || content.StartsWith("[");

    try
    {
        var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) return;

        var remoteContent = (await response.Content.ReadAsStringAsync()).Trim();
        if (!isValid(remoteContent)) return;

        if (File.Exists(localPath) && File.ReadAllText(localPath) == remoteContent) return;

        File.WriteAllText(localPath, remoteContent);
        System.Diagnostics.Debug.WriteLine($"Updated {Path.GetFileName(localPath)} from cloud");
    }
    catch
    {
        // Silently fail for individual file
    }
}
```

- [ ] **Step 3: Add to `CheckForUpdatesAsync`**

In `CheckForUpdatesAsync`, update the `Task.WhenAll` call (currently around line 114) to include the third file:

```csharp
await Task.WhenAll(
    DownloadIfNewerAsync(QUICK_COMMENTS_URL,   QuickCommentsPath),
    DownloadIfNewerAsync(INSPECTOR_STATS_URL,  InspectorStatsPath),
    DownloadIfNewerAsync(INSPECTION_TYPES_URL, InspectionTypesPath,
        content => content.TrimStart().StartsWith("INS Type"))
);
```

- [ ] **Step 4: Add to `ForceUpdateStatsAsync`**

In `ForceUpdateStatsAsync`, after the line that saves `InspectorStatsPath` and calls `DownloadIfNewerAsync(QUICK_COMMENTS_URL, ...)`, add:

```csharp
await DownloadIfNewerAsync(INSPECTION_TYPES_URL, InspectionTypesPath,
    content => content.TrimStart().StartsWith("INS Type"));
```

- [ ] **Step 5: Add to stale-data check**

In `CheckForStaleData`, update to include the CSV:

```csharp
bool quickCommentsStale    = IsFileStale(QuickCommentsPath);
bool inspectorStatsStale   = IsFileStale(InspectorStatsPath);
bool inspectionTypesStale  = IsFileStale(InspectionTypesPath);

if (quickCommentsStale || inspectorStatsStale || inspectionTypesStale)
{
    var staleFiles = new System.Collections.Generic.List<string>();
    if (quickCommentsStale)   staleFiles.Add("Quick Comments");
    if (inspectorStatsStale)  staleFiles.Add("Inspector Stats");
    if (inspectionTypesStale) staleFiles.Add("Inspection Types");
    // rest unchanged
}
```

- [ ] **Step 6: Verify by running the app with internet, triple-clicking logo, checking app folder**

After running, confirm `inspection_types.csv` appears in the same folder as the RED executable. Open it and verify it matches the spreadsheet.

- [ ] **Step 7: Commit**

```bash
git add Services/DataUpdateService.cs
git commit -m "feat: add inspection_types.csv to DataUpdateService download pipeline"
```

---

## Task 3: `FreeReinspectionChecker`

**Files:**
- Create: `Services/FreeReinspectionChecker.cs`

This service uses the existing PdfPig + Tesseract/Docnet OCR pipeline already established in `EnergyComplianceService`. It checks the first page only (the result appears near the top of the report).

- [ ] **Step 1: Create the file**

`Services/FreeReinspectionChecker.cs`:

```csharp
using Docnet.Core;
using Docnet.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Tesseract;
using UglyToad.PdfPig;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace InspectionEditor.Services
{
    public class FreeReinspectionAlert
    {
        public string InsType { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsInMyList { get; set; }
        public string? MyListFilePath { get; set; }
    }

    public static class FreeReinspectionChecker
    {
        // Text written into reports when result is "fail — check at next inspection"
        private const string FailNextText = "Failed items to be inspected at next inspection";

        public static async Task<List<FreeReinspectionAlert>> CheckAsync(
            string loadedFilePath,
            InspectionTypeConfig config,
            string myListFolder,
            string jobsFolder)
        {
            var alerts = new List<FreeReinspectionAlert>();
            if (config.FreeReinspectionTypes.Count == 0) return alerts;

            string fileName = Path.GetFileNameWithoutExtension(loadedFilePath);
            string[] parts = fileName.Split('-');
            if (parts.Length < 2) return alerts;
            string jobId = parts[0];

            string jobInspFolder = Path.Combine(jobsFolder, jobId, "Inspections");
            if (!Directory.Exists(jobInspFolder)) return alerts;

            foreach (string freeType in config.FreeReinspectionTypes)
            {
                try
                {
                    string? latestPdf = FindLatestPdf(jobInspFolder, jobId, freeType);
                    if (latestPdf == null) continue;

                    string ocrText = await ExtractFirstPageTextAsync(latestPdf);
                    if (!ocrText.Contains(FailNextText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Check if a companion .ins is already in MyList
                    string? companionPath = Directory
                        .GetFiles(myListFolder, $"{jobId}-{freeType}-*.ins",
                                  SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();

                    alerts.Add(new FreeReinspectionAlert
                    {
                        InsType       = freeType,
                        DisplayName   = GetDisplayName(freeType),
                        IsInMyList    = companionPath != null,
                        MyListFilePath = companionPath,
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FreeReinspectionChecker error for {freeType}: {ex.Message}");
                }
            }

            return alerts;
        }

        // Returns the PDF with the highest sequence number for the given job + type.
        // Filename format: {jobId}-{type}-{seq}-{initials}.pdf
        private static string? FindLatestPdf(string folder, string jobId, string insType)
        {
            return Directory
                .GetFiles(folder, $"{jobId}-{insType}-*.pdf", SearchOption.TopDirectoryOnly)
                .Select(f => new { Path = f, Seq = ParseSeq(Path.GetFileNameWithoutExtension(f)) })
                .Where(x => x.Seq >= 0)
                .OrderByDescending(x => x.Seq)
                .Select(x => x.Path)
                .FirstOrDefault();
        }

        private static int ParseSeq(string nameWithoutExt)
        {
            var p = nameWithoutExt.Split('-');
            return p.Length >= 3 && int.TryParse(p[2], out int seq) ? seq : -1;
        }

        private static async Task<string> ExtractFirstPageTextAsync(string pdfPath)
        {
            return await Task.Run(() =>
            {
                // 1. PdfPig text extraction (fast, works for computer-generated PDFs)
                try
                {
                    using var doc = PdfDocument.Open(pdfPath);
                    var page = doc.GetPage(1);
                    string text = page.Text;
                    if (text.Trim().Length >= 50) return text;
                }
                catch { }

                // 2. Tesseract fallback (for scanned/image-based PDFs)
                string? tessData = EnergyComplianceService.GetTessDataPathPublic();
                if (tessData == null) return "";

                try
                {
                    using var engine = new TesseractEngine(tessData, "eng", EngineMode.Default);
                    using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(3.0));
                    using var pageReader = docReader.GetPageReader(0); // first page only
                    byte[] rawBytes = pageReader.GetImage();
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    return OcrBytes(rawBytes, w, h, engine);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FreeReinspectionChecker OCR failed: {ex.Message}");
                    return "";
                }
            });
        }

        private static string OcrBytes(byte[] bgraBytes, int width, int height, TesseractEngine engine)
        {
            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(bgraBytes, 0, data.Scan0, bgraBytes.Length);
                bmp.UnlockBits(data);
                using var ms = new MemoryStream();
                bmp.Save(ms, DrawingImageFormat.Png);
                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var pg = engine.Process(pix);
                return pg.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FreeReinspectionChecker OcrBytes error: {ex.Message}");
                return "";
            }
        }

        private static string GetDisplayName(string insType) =>
            _typeNames.TryGetValue(insType.ToUpperInvariant(), out var n) ? n : insType;

        private static readonly Dictionary<string, string> _typeNames =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["AFI"]  = "ACCA 310 Field Inspection",
            ["BC"]   = "Builder Confirmation",
            ["BF"]   = "BMEP Final",
            ["BWT"]  = "New Home Orientation",
            ["COH"]  = "Flashing Sheathing Framing (COH)",
            ["CPP"]  = "Concrete Pre-Pour",
            ["CPR"]  = "Concrete Pour",
            ["FS"]   = "Frame",
            ["FSF"]  = "Flashing Sheathing Framing",
            ["FWI"]  = "Stage Three Fire",
            ["HEF"]  = "Final Energy Star",
            ["HER"]  = "HERS Energy Rough",
            ["HET"]  = "Energy Star Final Testing",
            ["IAP"]  = "Indoor Air Plus",
            ["IEF"]  = "Energy Star Final",
            ["IER"]  = "Energy Star Rough",
            ["ME"]   = "BMEP Rough",
            ["MP"]   = "BMEP Rough",
            ["PLY"]  = "Polyseal",
            ["PPE"]  = "Concrete Post Pour Elevations",
            ["QIER"] = "Energy Rough",
            ["SCI"]  = "Special Consult",
            ["SRP"]  = "Slab Repair Pre-Pour",
            ["STR"]  = "Stressing",
            ["SWI"]  = "Shearwall Inspection",
            ["TFF"]  = "TDI Final Frame",
            ["TPC"]  = "TDI Pre Cornice",
            ["TRDI"] = "TDI Roof Decking Inspection",
            ["TRSI"] = "TDI Roof Shingle Inspection",
        };
    }
}
```

- [ ] **Step 2: Expose `GetTessDataPathPublic` in `EnergyComplianceService`**

`GetTessDataPath()` in `EnergyComplianceService` is currently `private static`. Add a public wrapper at the bottom of the class:

```csharp
public static string? GetTessDataPathPublic() => GetTessDataPath();
```

- [ ] **Step 3: Build the solution and fix any compile errors**

```
dotnet build InspectionEditor.csproj
```

Expected: build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Services/FreeReinspectionChecker.cs Services/EnergyComplianceService.cs
git commit -m "feat: add FreeReinspectionChecker with OCR-based prior result detection"
```

---

## Task 4: `ResultPickerWindow` — data-driven buttons with gray states

**Files:**
- Modify: `ResultPickerWindow.xaml.cs`

- [ ] **Step 1: Update the constructor signature**

Replace the existing constructor (line 68):

```csharp
// BEFORE:
public ResultPickerWindow(string inspectionCode, int answeredItems, int failedItems)

// AFTER:
public ResultPickerWindow(
    string inspectionCode,
    int answeredItems,
    int failedItems,
    InspectionEditor.Services.InspectionTypeConfig? config = null,
    bool expirationStageDone = false)
```

Update the constructor body — replace the `BuildButtons` call:

```csharp
// BEFORE:
BuildButtons(inspectionCode.ToUpper(), failedItems > 0);

// AFTER:
BuildButtons(inspectionCode.ToUpper(), failedItems > 0, config, expirationStageDone);
```

- [ ] **Step 2: Remove hard-coded sets and rewrite `BuildButtons`**

Remove the two static sets:

```csharp
// DELETE these two lines:
private static readonly HashSet<string> PassFailOnly = new() { "BF", "STR" };
private static readonly HashSet<string> PoFailOnly = new() { ... };
```

Replace the entire `BuildButtons` method:

```csharp
private void BuildButtons(
    string code,
    bool hasFails,
    InspectionEditor.Services.InspectionTypeConfig? config,
    bool expirationStageDone)
{
    var options = new List<(ResultChoice choice, bool grayed, string grayReason)>();

    if (config != null)
    {
        // Data-driven path
        if (config.EngineerReview)
        {
            options.Add((new ResultChoice("✓  For Engineer Review", 24, 0, null, "#0D47A1"), false, ""));
        }
        else if (config.ShowComplete && !config.ShowPass && !config.ShowCorrectAndProceed
                 && !config.ShowFailNext && !config.ShowFailPO)
        {
            // BWT-style: Complete only
            options.Add((new ResultChoice("✓  Complete", 7, 0, null, "#1B5E20"), false, ""));
        }
        else
        {
            if (config.ShowPass)
            {
                bool gray = hasFails;
                options.Add((
                    new ResultChoice("✓  Pass", 2, 0, null, "#1B5E20"),
                    gray,
                    "Not offering Pass — items are marked fail"));
            }
            if (config.ShowComplete)
                options.Add((new ResultChoice("✓  Complete", 7, 0, null, "#1B5E20"), false, ""));

            if (config.ShowCorrectAndProceed)
            {
                bool gray = !hasFails;
                options.Add((
                    new ResultChoice("~  Correct & Proceed", 5, 0, null, "#E65100"),
                    gray,
                    "Not offering C&P — all items pass"));
            }
            if (config.ShowFailNext)
            {
                bool gray = !hasFails || expirationStageDone;
                string reason = expirationStageDone
                    ? "Not offering — next phase already complete"
                    : "Not offering — no failed items";
                options.Add((
                    new ResultChoice("✗  Fail  —  items to be inspected at next phase",
                        3, 1, "Failed items to be inspected at next inspection", "#7B1FA2"),
                    gray, reason));
            }
            if (config.ShowFailPO)
            {
                bool gray = !hasFails;
                options.Add((
                    new ResultChoice("✗  Fail  —  PO required for reinspection",
                        3, 2, "Request a reinspection. (PO required)", "#B71C1C"),
                    gray,
                    "Not offering — no failed items"));
            }
        }
    }
    else
    {
        // Fallback: replicate original hard-coded behavior when CSV not yet downloaded
        if (code == "BWT")
        {
            options.Add((new ResultChoice("✓  Complete", 7, 0, null, "#1B5E20"), false, ""));
        }
        else if (code == "SCI")
        {
            options.Add((new ResultChoice("✓  For Engineer Review", 24, 0, null, "#0D47A1"), false, ""));
        }
        else
        {
            var PassFailOnly = new HashSet<string> { "BF", "STR" };
            var PoFailOnly = new HashSet<string>
            {
                "HER","HEF","HET","IER","IEF","IET","IAP",
                "PLY","PPE","SRP","STR","TRDI","TRSI",
                "CPR","FWI","BC","AFI",
            };
            if (!hasFails)
                options.Add((new ResultChoice("✓  Pass", 2, 0, null, "#1B5E20"), false, ""));
            if (!PassFailOnly.Contains(code))
                options.Add((new ResultChoice("~  Correct & Proceed", 5, 0, null, "#E65100"), false, ""));
            if (code != "SCI")
            {
                options.Add((new ResultChoice("✗  Fail  —  PO required for reinspection",
                    3, 2, "Request a reinspection. (PO required)", "#B71C1C"), false, ""));
                if (!PoFailOnly.Contains(code))
                    options.Add((new ResultChoice("✗  Fail  —  items to be inspected at next phase",
                        3, 1, "Failed items to be inspected at next inspection", "#7B1FA2"), false, ""));
            }
        }
    }

    foreach (var (choice, grayed, reason) in options)
        ButtonPanel.Children.Add(MakeButton(choice, grayed, reason));
}
```

- [ ] **Step 3: Update `MakeButton` to support grayed state + reason label**

Replace the existing `MakeButton` method:

```csharp
private Button MakeButton(ResultChoice choice, bool grayed = false, string grayReason = "")
{
    var btn = new Button
    {
        Content = choice.Label,
        Tag = choice,
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(choice.Background)),
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(24, 22, 24, 22),
        Margin = new Thickness(0, 0, 0, grayed ? 4 : 14),
        HorizontalContentAlignment = HorizontalAlignment.Left,
        Cursor = grayed ? Cursors.Arrow : Cursors.Hand,
        IsEnabled = !grayed,
        Opacity = grayed ? 0.4 : 1.0,
    };
    if (!grayed) btn.Click += ResultButton_Click;

    if (grayed && !string.IsNullOrEmpty(grayReason))
    {
        var wrapper = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        btn.Margin = new Thickness(0);
        wrapper.Children.Add(btn);
        wrapper.Children.Add(new TextBlock
        {
            Text = grayReason,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            Foreground = Brushes.Gray,
            Margin = new Thickness(4, 2, 0, 0),
        });
        // Store wrapper as a fake button-panel child — wrap in a ContentControl so we can add it uniformly
        ButtonPanel.Children.Add(wrapper);
        return btn; // btn already added via wrapper; caller must NOT add btn again
    }

    return btn;
}
```

> **Note:** Because the grayed path adds the wrapper directly to `ButtonPanel`, update the loop in `BuildButtons` to only call `ButtonPanel.Children.Add(btn)` when the button is NOT grayed:

```csharp
foreach (var (choice, grayed, reason) in options)
{
    var btn = MakeButton(choice, grayed, reason);
    if (!grayed || string.IsNullOrEmpty(reason))
        ButtonPanel.Children.Add(btn);
    // grayed+reason case: MakeButton already added the wrapper
}
```

- [ ] **Step 4: Build and fix compile errors**

```
dotnet build InspectionEditor.csproj
```

Expected: 0 errors.

- [ ] **Step 5: Smoke test — open any inspection, close it, verify result picker appears unchanged**

The fallback branch (config=null) must produce the same button layout as before. Verify manually by opening a CPP and an IER inspection and closing each.

- [ ] **Step 6: Commit**

```bash
git add ResultPickerWindow.xaml.cs
git commit -m "feat: data-driven ResultPickerWindow with gray+reason button states"
```

---

## Task 5: `RulesAcknowledgmentWindow` — free reinspection alerts section

**Files:**
- Modify: `RulesAcknowledgmentWindow.xaml`
- Modify: `RulesAcknowledgmentWindow.xaml.cs`

- [ ] **Step 1: Add the free reinspection panel to the XAML**

Open `RulesAcknowledgmentWindow.xaml`. Find the `<ScrollViewer>` that contains `RulesPanel` and add a new `StackPanel` named `FreeReinspectionPanel` immediately above the existing `RulesPanel` inside the scroll viewer:

```xml
<!-- Free reinspection alerts — amber section, shown above rules -->
<StackPanel x:Name="FreeReinspectionPanel" Margin="0,0,0,12" Visibility="Collapsed"/>

<!-- existing rules panel below -->
<StackPanel x:Name="RulesPanel"/>
```

- [ ] **Step 2: Add model imports and properties to the code-behind**

At the top of `RulesAcknowledgmentWindow.xaml.cs`, add the using:

```csharp
using InspectionEditor.Services;
```

Add two new properties to the class:

```csharp
// Paths of companion .ins files the user checked Yes to open
public List<string> SelectedCompanionPaths { get; } = new();

// Tracks Yes/No checkboxes for companion files
private readonly List<(CheckBox cb, string filePath)> _companionCheckboxes = new();
```

- [ ] **Step 3: Add `SetFreeReinspectionAlerts` method**

Add this method to `RulesAcknowledgmentWindow.xaml.cs`:

```csharp
public void SetFreeReinspectionAlerts(List<FreeReinspectionAlert> alerts)
{
    FreeReinspectionPanel.Children.Clear();
    _companionCheckboxes.Clear();
    SelectedCompanionPaths.Clear();

    if (alerts.Count == 0)
    {
        FreeReinspectionPanel.Visibility = System.Windows.Visibility.Collapsed;
        return;
    }

    FreeReinspectionPanel.Visibility = System.Windows.Visibility.Visible;

    // Section header
    FreeReinspectionPanel.Children.Add(new TextBlock
    {
        Text = "Free Reinspections Included With This Service",
        FontWeight = FontWeights.Bold,
        FontSize = 14,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = new SolidColorBrush(Color.FromRgb(180, 100, 0)),
    });

    foreach (var alert in alerts)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 140, 0)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(255, 251, 235)),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(15),
            CornerRadius = new CornerRadius(5),
        };

        var stack = new StackPanel();

        if (alert.IsInMyList)
        {
            var cb = new CheckBox
            {
                IsChecked = true,
                FontSize = 13,
                MinHeight = 40,
                Padding = new Thickness(8, 0, 0, 0),
                Content = $"Open free reinspection alongside this one: " +
                          $"[{alert.InsType}: {alert.DisplayName}]",
            };
            cb.LayoutTransform = new ScaleTransform(1.3, 1.3);
            _companionCheckboxes.Add((cb, alert.MyListFilePath!));
            stack.Children.Add(cb);
        }
        else
        {
            var icon = new TextBlock
            {
                Text = "⚠  Free reinspection not in MyList",
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(160, 80, 0)),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 6),
            };
            var msg = new TextBlock
            {
                Text = $"This service includes a free [{alert.InsType}: {alert.DisplayName}] " +
                       $"reinspection but it is not in your MyList. " +
                       $"Download it before opening this job.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = Brushes.DarkOliveGreen,
            };
            stack.Children.Add(icon);
            stack.Children.Add(msg);
        }

        border.Child = stack;
        FreeReinspectionPanel.Children.Add(border);
    }
}
```

- [ ] **Step 4: Collect selected companions when the window closes (OK path)**

In `AcknowledgeAllButton_Click`, before setting `DialogResult = true`, add:

```csharp
SelectedCompanionPaths.Clear();
foreach (var (cb, path) in _companionCheckboxes)
    if (cb.IsChecked == true)
        SelectedCompanionPaths.Add(path);
```

- [ ] **Step 5: Build and fix any compile errors**

```
dotnet build InspectionEditor.csproj
```

- [ ] **Step 6: Commit**

```bash
git add RulesAcknowledgmentWindow.xaml RulesAcknowledgmentWindow.xaml.cs
git commit -m "feat: add free reinspection alert section to RulesAcknowledgmentWindow"
```

---

## Task 6: `MainWindow` — wire everything together

**Files:**
- Modify: `MainWindow.xaml.cs`

This task has the most changes but each is surgical. Work section by section.

- [ ] **Step 1: Add fields and service instance to MainWindow class**

Near the top of the `MainWindow` class (alongside other private fields, around line 108), add:

```csharp
// Inspection type config service — drives result picker and free reinspection check
private readonly InspectionEditor.Services.InspectionTypeService _inspTypeService =
    new InspectionEditor.Services.InspectionTypeService();

// Set to true when this window was opened as a companion (suppresses acknowledgment popup)
private bool _openedAsCompanion = false;

// Sequence number from the loaded filename (1 = first trip, >1 = reinspection)
private int _currentSequenceNumber = 1;
```

- [ ] **Step 2: Add a public method to open a window as a companion**

Add a public static factory so MainWindow can open companions cleanly:

```csharp
public static MainWindow OpenAsCompanion(string filePath)
{
    var w = new MainWindow();
    w._openedAsCompanion = true;
    w.Show();
    w.LoadFileFromArgs(filePath);
    return w;
}
```

- [ ] **Step 3: Capture sequence number and config on file load**

In `LoadInspectionFileAsync`, after line 1827 (`var filenameParts = filename.Split('-');`), add:

```csharp
// Parse sequence number (3rd segment, e.g. "2538292-CPR-1-TF" → 1)
_currentSequenceNumber = 1;
if (filenameParts.Length >= 3 && int.TryParse(filenameParts[2], out int parsedSeq))
    _currentSequenceNumber = parsedSeq;
```

- [ ] **Step 4: Invalidate InspectionTypeService cache after a force-update**

In the triple-click logo handler (around line 679 where `ForceUpdateStatsAsync` is called), after the await, add:

```csharp
_inspTypeService.InvalidateCache();
```

- [ ] **Step 5: Run `FreeReinspectionChecker` during file load and pipe into `ShowRulesWindowAsync`**

In `LoadInspectionFileAsync`, replace the existing call to `ShowRulesWindowAsync(true)` (line 1912):

```csharp
// BEFORE:
if (!await ShowRulesWindowAsync(true))

// AFTER:
// Run free reinspection check async (non-blocking, first result used)
var config = _inspTypeService.GetConfig(_currentInspectionCode ?? "");
string? insFolder = Path.GetDirectoryName(filePath);
string? inspRoot = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
string jobsFolder = inspRoot != null ? Path.Combine(inspRoot, "Jobs") : "";
string myListFolder = insFolder ?? "";

List<InspectionEditor.Services.FreeReinspectionAlert> freeAlerts = new();
if (config != null && !string.IsNullOrEmpty(jobsFolder))
{
    try
    {
        freeAlerts = await InspectionEditor.Services.FreeReinspectionChecker.CheckAsync(
            filePath, config, myListFolder, jobsFolder);
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"FreeReinspectionChecker failed: {ex.Message}");
    }
}

if (!await ShowRulesWindowAsync(true, freeAlerts))
```

- [ ] **Step 6: Update `ShowRulesWindowAsync` signature and add suppression + companion opening**

Change the method signature (line 2039):

```csharp
// BEFORE:
private async Task<bool> ShowRulesWindowAsync(bool enforceAck)

// AFTER:
private async Task<bool> ShowRulesWindowAsync(
    bool enforceAck,
    List<InspectionEditor.Services.FreeReinspectionAlert>? freeAlerts = null)
```

At the very top of the method body, add suppression logic:

```csharp
// Suppress popup for reinspection trips and companion windows.
// Still return true (allow proceeding) — the check already ran and info is available.
bool suppressPopup = _openedAsCompanion || _currentSequenceNumber > 1;
if (suppressPopup)
    return true;
```

After the line that creates `var window = new RulesAcknowledgmentWindow(...)` (line 2062) and before `ShowDialog()`, add:

```csharp
// Attach free reinspection alerts if any
if (freeAlerts != null && freeAlerts.Count > 0)
    window.SetFreeReinspectionAlerts(freeAlerts);
```

After the `window.ShowDialog()` call in the `enforceAck` branch, add companion opening:

```csharp
// Open any companion files the user selected Yes to
if (window.SelectedCompanionPaths.Count > 0)
{
    foreach (var companionPath in window.SelectedCompanionPaths)
    {
        try { InspectionEditor.MainWindow.OpenAsCompanion(companionPath); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open companion {companionPath}: {ex.Message}");
        }
    }
}
```

- [ ] **Step 7: Update `ShowResultPickerAndClose` to pass config and `expirationStageDone`**

Replace the block at lines 289-307:

```csharp
private void ShowResultPickerAndClose()
{
    string code = _currentInspectionCode ?? _currentInspection?.InspectionCode ?? "";
    int answered = CountAnsweredItems();
    int failed   = CountFailedItems();

    // Load config and compute expiration stage
    var config = _inspTypeService.GetConfig(code);
    bool expirationStageDone = false;

    if (config?.ExpirationStageTypes.Count > 0 && _currentFilePath != null)
    {
        string? insFolder   = Path.GetDirectoryName(_currentFilePath);
        string? inspRoot    = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
        string jobsFolder   = inspRoot != null ? Path.Combine(inspRoot, "Jobs") : "";
        string filename     = Path.GetFileNameWithoutExtension(_currentFilePath);
        string jobId        = filename.Split('-')[0];
        string jobInspFolder = Path.Combine(jobsFolder, jobId, "Inspections");

        if (Directory.Exists(jobInspFolder))
        {
            foreach (var expType in config.ExpirationStageTypes)
            {
                if (Directory.GetFiles(jobInspFolder, $"{jobId}-{expType}-*.pdf",
                    SearchOption.TopDirectoryOnly).Length > 0)
                {
                    expirationStageDone = true;
                    break;
                }
            }
        }
    }

    var picker = new ResultPickerWindow(code, answered, failed, config, expirationStageDone)
    {
        Owner = this
    };

    bool? result = picker.ShowDialog();

    if (result == true && picker.SelectedResult != null)
    {
        var choice = picker.SelectedResult;
        _saveService.SetResult(choice.StatusId, choice.NextActionId, choice.NextActionText);
        _hasUnsavedChanges = true;
    }

    _skipResultCheck = true;
    Dispatcher.BeginInvoke(new Action(Close));
}
```

- [ ] **Step 8: Make offer-file button label data-driven (col C)**

Find `UpdateSeeDocsButton` (line 2369). It currently maps `inspType` to a document label using hard-coded logic. After the `parts` parsing, add:

```csharp
// Override offer-file label with spreadsheet config if available
var typeConfig = _inspTypeService.GetConfig(inspType);
if (typeConfig != null && !string.IsNullOrWhiteSpace(typeConfig.OfferFileLabel))
{
    // Use typeConfig.OfferFileLabel as the button label instead of any hard-coded value.
    // Find wherever the button text/tooltip is set and replace with typeConfig.OfferFileLabel.
    SeeDocsButton.Content = typeConfig.OfferFileLabel;  // adjust to actual button name
    SeeDocsButton.Visibility = System.Windows.Visibility.Visible;
}
```

> **Note:** The exact button name and property will be visible in the XAML near the `UpdateSeeDocsButton` implementation. Adjust `SeeDocsButton.Content` to match the real control name.

- [ ] **Step 9: Build and fix all compile errors**

```
dotnet build InspectionEditor.csproj
```

Expected: 0 errors.

- [ ] **Step 10: End-to-end smoke test**

1. Open a CPR inspection (trip 1) from MyList. Verify acknowledgment popup appears. If there is a prior CPP PDF in the corresponding job folder with "Failed items to be inspected at next inspection," verify the amber alert section appears.
2. Open a CPR inspection (trip 2, e.g. `2538292-CPR-2-TF.ins`). Verify NO popup appears.
3. Close an inspection and verify the result picker buttons match the spreadsheet config for that type.
4. Close a CPP inspection where CPR PDF exists in the job folder — verify "Fail — items to be inspected at next phase" is grayed with the expiration message.
5. Triple-click logo, verify `inspection_types.csv` is refreshed and cache is busted.

- [ ] **Step 11: Commit**

```bash
git add MainWindow.xaml.cs
git commit -m "feat: wire InspectionTypeService and FreeReinspectionChecker into MainWindow"
```

---

## Self-Review Checklist (completed inline)

- **Spec coverage:**
  - ✅ `InspectionTypeConfig` model with all 11 columns
  - ✅ `InspectionTypeService` lazy-loading CSV, `GetConfig()`, `InvalidateCache()`
  - ✅ `DataUpdateService` third download target with CSV validator
  - ✅ `FreeReinspectionChecker` — latest PDF by sequence, PdfPig+Tesseract OCR, MyList scan
  - ✅ `ResultPickerWindow` — data-driven, grayed buttons, fallback when CSV missing, Skip unchanged
  - ✅ `RulesAcknowledgmentWindow` — amber section, Yes/No checkboxes, grayed warning for missing companions
  - ✅ Popup suppression: `_openedAsCompanion` flag + sequence > 1 check
  - ✅ Companion files opened via `OpenAsCompanion` with `_openedAsCompanion = true`
  - ✅ Expiration stage check before opening ResultPickerWindow
  - ✅ Offer-file button (col C) data-driven
  - ✅ Cache bust on triple-click logo update
  - ✅ Offline/null fallback in ResultPickerWindow and all services

- **Type consistency:** `FreeReinspectionAlert` defined in Task 3, used in Task 5 and 6. `InspectionTypeConfig` defined in Task 1, used in Tasks 4, 5, 6. `SelectedCompanionPaths` set in Task 5 step 4, read in Task 6 step 6. All consistent.
