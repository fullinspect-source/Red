# Design: Configurable Result Guardrails & Free Reinspection Alerts

**Date:** 2026-04-19
**Status:** Approved

## Overview

Two related features driven by a single remotely-managed Google Sheets CSV:

1. **Configurable result guardrails** — replace hard-coded `PassFailOnly`/`PoFailOnly` sets in `ResultPickerWindow` with per-INS-type config from the spreadsheet. Buttons that don't apply are grayed with a reason rather than hidden.
2. **Free reinspection alerts on load** — when a user opens an inspection whose type includes a contractually free reinspection (col D), RED checks whether the prior inspection for that job actually failed in a way that makes the free reinspection eligible, then alerts the user and offers to open the companion file.

The spreadsheet is the remote config knob. All installed RED instances pull updates from it the same way they pull `quick_comments.json` — downloaded to the app folder, updated on startup and triple-click logo, and fully functional offline after first download.

---

## Spreadsheet Source

**URL:** `https://docs.google.com/spreadsheets/d/1tuT8L7OFWzebwsJ0qe9tegxwier-knASgWpp70qJbqY/export?format=csv`
**Local cache file:** `inspection_types.csv` (app folder, alongside `quick_comments.json`)

### Column mapping (rows 2–30, row 1 is header)

| Col | Field | Notes |
|-----|-------|-------|
| A | INS Type code | e.g. `CPR`, `ME` |
| B | Alias / display name | e.g. `Concrete Pour` |
| C | Offer file label | Document type shown on left-pane button — already wired, now data-driven |
| D | Free reinspection types | Comma/or-separated codes; empty = none |
| E | Expiration stage types | If a PDF of this type exists in the job folder, Fail-Next is disabled |
| F | Engineer Review | `Yes` = show "For Engineer Review" instead of standard buttons |
| G | Pass | `Yes` = button available |
| H | Complete | `Yes` = button available |
| I | Correct & Proceed | `Yes` = button available |
| J | Fail — next inspection | `Yes` = button available |
| K | Fail — PO required | `Yes` = button available |

---

## File & Folder Conventions

All paths are resolved from the loaded `.ins` file path — no hardcoded Dropbox root needed.

**Filename format:** `{jobId}-{insType}-{sequence}-{initials}.ins` (e.g. `2538292-CPR-1-TF.ins`)

| Path | Derivation |
|------|------------|
| MyList folder | `Path.GetDirectoryName(filePath)` (the folder the file was opened from) |
| Inspections root | Parent of MyList folder |
| Jobs folder | `{inspectionsRoot}/Jobs/` |
| Job folder | `{jobsFolder}/{jobId}/Inspections/` |

**Sequence number** — the third hyphen-separated segment of the filename. Sequence `1` = first inspection. Any higher number = reinspection trip.

---

## New Files

### `Services/InspectionTypeService.cs`

Loads `inspection_types.csv` from the app folder into memory on first access. Parses "or"-separated and comma-separated values in cols D and E into `List<string>`.

```csharp
public class InspectionTypeConfig
{
    public string InsType { get; set; }
    public string Alias { get; set; }
    public string OfferFileLabel { get; set; }
    public List<string> FreeReinspectionTypes { get; set; }  // col D
    public List<string> ExpirationStageTypes { get; set; }   // col E
    public bool EngineerReview { get; set; }
    public bool ShowPass { get; set; }
    public bool ShowComplete { get; set; }
    public bool ShowCorrectAndProceed { get; set; }
    public bool ShowFailNext { get; set; }
    public bool ShowFailPO { get; set; }
}

public class InspectionTypeService
{
    public InspectionTypeConfig? GetConfig(string insType);
    // Loads lazily from app folder; returns null if CSV missing or type not found
}
```

### `Services/FreeReinspectionChecker.cs`

Runs async after a file loads. Returns a list of alerts (one per eligible free reinspection type found).

```csharp
public class FreeReinspectionAlert
{
    public string InsType { get; set; }          // e.g. "CPP"
    public string DisplayName { get; set; }      // e.g. "Concrete Pre-Pour"
    public bool IsInMyList { get; set; }
    public string? MyListFilePath { get; set; }  // set when IsInMyList = true
}

public class FreeReinspectionChecker
{
    public static async Task<List<FreeReinspectionAlert>> CheckAsync(
        string loadedFilePath,
        InspectionTypeConfig config,
        string myListFolder,
        string jobsFolder,
        string tessDataPath);
}
```

**Check algorithm:**
1. Parse `jobId` from filename (segment 0)
2. For each type in `config.FreeReinspectionTypes`:
   a. Scan `Jobs/{jobId}/Inspections/` for PDFs matching `{jobId}-{type}-*.pdf`
   b. Select the PDF with the highest sequence number
   c. OCR it (PdfPig first, Tesseract fallback — same pattern as `EnergyComplianceService`) — first page only
   d. If OCR text contains `"Failed items to be inspected at next inspection"` (case-insensitive):
      - Search MyList folder for `{jobId}-{type}-*.ins`
      - Build and return a `FreeReinspectionAlert`

---

## Modified Files

### `Services/DataUpdateService.cs`

Add third download target:

```csharp
private const string INSPECTION_TYPES_URL =
    "https://docs.google.com/spreadsheets/d/1tuT8L7OFWzebwsJ0qe9tegxwier-knASgWpp70qJbqY/export?format=csv";
private static readonly string InspectionTypesPath =
    Path.Combine(AppFolder, "inspection_types.csv");
```

CSV validation: check that trimmed content starts with `"INS Type"` (first column header) rather than `{`/`[`. Include in both `CheckForUpdatesAsync()` and `ForceUpdateStatsAsync()` flows. Include in stale-data check.

### `ResultPickerWindow.xaml.cs`

**Constructor signature change:**
```csharp
public ResultPickerWindow(
    string inspectionCode,
    int answeredItems,
    int failedItems,
    InspectionTypeConfig? config,       // null = fall back to current hard-coded behavior
    bool expirationStageDone = false)
```

**Remove:** `PassFailOnly` and `PoFailOnly` static sets.

**Button rendering — grayed with reason instead of hidden:**

| Button | Shown if | Grayed when | Gray label |
|--------|----------|-------------|------------|
| Pass | `ShowPass` | `hasFails` | "Not offering Pass — items are marked fail" |
| Complete | `ShowComplete` | never | — |
| Correct & Proceed | `ShowCorrectAndProceed` | `!hasFails` | "Not offering C&P — all items pass" |
| Fail — next inspection | `ShowFailNext` | `!hasFails` OR `expirationStageDone` | "Not offering — next phase already complete" |
| Fail — PO required | `ShowFailPO` | `!hasFails` | "Not offering — no failed items" |

Grayed buttons: `IsEnabled = false`, `Opacity = 0.4`, small italic `TextBlock` beneath with reason text.

`EngineerReview = true` drives the "For Engineer Review" special case (replaces `code == "SCI"` check).

**Skip button:** unchanged.

**Fallback:** if `config` is null, preserve current hard-coded behavior so nothing breaks if the CSV hasn't been downloaded yet.

### `RulesAcknowledgmentWindow.xaml.cs`

Add a `SetFreeReinspectionAlerts(List<FreeReinspectionAlert> alerts)` method called before `ShowDialog()`. Renders an amber-bordered section above the existing red-bordered rule items.

Each alert renders as:
- **Companion in MyList:** checkbox (Yes checked by default) — *"This service includes a free reinspection — [CPP: Concrete Pre-Pour] is ready in your list. Open it alongside this one?"*
- **Companion NOT in MyList:** grayed row with warning icon (⚠) — *"This service includes a free [CPP: Concrete Pre-Pour] reinspection but it is not in your MyList. Download it before opening this job."* — user taps to acknowledge; no open option.

After `ShowDialog()`, caller reads `SelectedCompanionPaths` (list of `.ins` paths the user said Yes to).

### `MainWindow.xaml.cs`

**On file load:**
1. Parse filename parts to get `jobId`, `insType`, `sequence`
2. Load `InspectionTypeConfig` from `InspectionTypeService`
3. Make col C offer-file button label data-driven from `config.OfferFileLabel`
4. Run `FreeReinspectionChecker.CheckAsync(...)` in background
5. **Popup suppression — skip showing popup if either:**
   - `sequence > 1` (reinspection trip)
   - `openedAsCompanion == true` (flag on this MainWindow instance)
   - In both cases the check still runs silently; a compact read-only notice is appended to the bottom of the left pane listing any eligible free reinspections found (no action required from the user)
6. If popup is shown and user selects Yes companions: open each in a new `MainWindow` instance with `openedAsCompanion = true`

**Before opening `ResultPickerWindow`:**
1. Compute `expirationStageDone`: check whether any PDF matching `{jobId}-{expirationStageType}-*.pdf` exists in `Jobs/{jobId}/Inspections/` — reuses same folder-walk pattern as `UpdatePriorReportButton`
2. Pass `config` and `expirationStageDone` to `ResultPickerWindow` constructor

---

## Popup Suppression Rules

Both rules skip the acknowledgment popup display but allow the background check to complete so left-pane info stays current.

| Condition | Suppress popup | Background check |
|-----------|---------------|-----------------|
| Filename sequence > 1 | Yes | Yes, runs silently |
| `openedAsCompanion = true` | Yes | Yes, runs silently |
| Sequence = 1, not a companion | No | Yes, shown in popup |

---

## Error & Offline Handling

- If `inspection_types.csv` is missing: `InspectionTypeService.GetConfig()` returns null; `ResultPickerWindow` falls back to current hard-coded behavior; no crash.
- If OCR fails on a prior PDF: `FreeReinspectionChecker` skips that alert silently — same silent-fail pattern as the rest of the services.
- If Jobs folder doesn't exist: skip all checks silently.
