using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

// INSPECT user type (owner Q-22; program spec F-003 O9/O10):
//  - exact ordinal classification; a blank value is Missing, never "Inspector";
//  - a blank UserType makes Add to My List refuse with the profile message;
//  - Preinspector sends no RequestFiles PUT and stamps IsPreinspection.
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

// Classification table (exact, case-sensitive, untrimmed).
var table = new (string? Value, InspectUserKind Kind)[]
{
    ("Inspector", InspectUserKind.Inspector),
    ("Preinspector", InspectUserKind.Preinspector),
    ("NonDropBoxInspector", InspectUserKind.NonDropBox),
    ("", InspectUserKind.Missing),
    ("   ", InspectUserKind.Missing),
    (null, InspectUserKind.Missing),
    ("PreInspector", InspectUserKind.Other),
    ("preinspector", InspectUserKind.Other),
    ("inspector", InspectUserKind.Other),
    (" Inspector", InspectUserKind.Other),
};
foreach (var (value, kind) in table)
    Check(InspectUserType.Classify(value) == kind, $"Classify({(value == null ? "null" : $"\"{value}\"")}) = {kind}");

// Reading config.json (O5): only an absent key means Inspector.
Check(InspectUserType.Classify(InspectUserType.Resolve(JObject.Parse("{\"Username\":\"x\"}"))) == InspectUserKind.Inspector,
    "config without a UserType key -> Inspector (INSPECT's default)");
Check(InspectUserType.Classify(InspectUserType.Resolve(JObject.Parse("{\"UserType\":\"\"}"))) == InspectUserKind.Missing,
    "blank UserType -> Missing (not Inspector)");
Check(InspectUserType.Classify(InspectUserType.Resolve(JObject.Parse("{\"UserType\":null}"))) == InspectUserKind.Missing,
    "null UserType -> Missing");
Check(InspectUserType.Classify(InspectUserType.Resolve(null)) == InspectUserKind.Missing, "no config at all -> Missing");
Check(InspectUserType.Classify(InspectUserType.Resolve(JObject.Parse("{\"usertype\":\"\"}"))) == InspectUserKind.Missing,
    "blank lower-case usertype key -> Missing (Newtonsoft matches keys ignoring case)");

// Blank UserType: Add to My List's refusal decision and message.
var blankKind = InspectUserType.Classify(InspectUserType.Resolve(JObject.Parse("{\"UserType\":\"  \"}")));
Check(blankKind == InspectUserKind.Missing && InspectUserType.ProfileIncompleteMessage == "Complete your inspector profile in RED Settings first.",
    "blank UserType refuses Add to My List with the profile message");

// Preinspector: no RequestFiles PUT. With no Strand session, any website call would throw StrandAuthRequiredException,
// so completing quietly proves nothing was sent; the Inspector control proves the call path is otherwise reached.
Check(!InspectUserType.RequestsJobFiles(InspectUserKind.Preinspector), "Preinspector does not request job files");
try
{
    await StrandMyListService.RequestJobFilesAsync(InspectUserKind.Preinspector, "2642023R1");
    Check(true, "Preinspector: RequestJobFilesAsync returns without any website call");
}
catch (Exception ex)
{
    Check(false, $"Preinspector: RequestJobFilesAsync tried the website ({ex.GetType().Name})");
}
try
{
    await StrandMyListService.RequestJobFilesAsync(InspectUserKind.Inspector, "2642023R1");
    Check(false, "Inspector control: expected an attempt to reach the website");
}
catch (StrandAuthRequiredException)
{
    Check(true, "Inspector control: RequestJobFilesAsync does try the website");
}
foreach (var kind in new[] { InspectUserKind.NonDropBox, InspectUserKind.Other, InspectUserKind.Missing })
    Check(!InspectUserType.RequestsJobFiles(kind), $"{kind} does not request job files");

// IsPreinspection stamped on new forms only for kind Preinspector (O6).
foreach (var (kind, expected) in new[] { (InspectUserKind.Preinspector, true), (InspectUserKind.Inspector, false), (InspectUserKind.Other, false) })
{
    var form = JObject.Parse("{\"InspectionNumber\":\"2642023-HET-1-TF\",\"IsPreinspection\":false,\"Sections\":[]}");
    StrandMyListService.PrepareForm(form, new StrandRequestDetail { JobNumber = "2642023R1" }, "Trent Fuller", kind);
    Check(form.Value<bool>("IsPreinspection") == expected, $"PrepareForm({kind}) IsPreinspection = {expected}");
}

// The user type as RED reads it from one Inspections folder's config.json (then .bak).
static (InspectUserKind Kind, bool Unreadable, DateTime? BackupDate, string? Value) ReadType(string? folder)
{
    var read = StrandCredentialService.ReadInspectFormConfig(folder);
    string? value = read.Config == null ? null : InspectUserType.Resolve(read.Config);
    return (read.Config == null ? InspectUserKind.Missing : InspectUserType.Classify(value), read.Config == null, read.BackupDate, value);
}

// Reading {IF}\config.json on sandbox copies (F-003 S3/S4): blank, backup fallback, unreadable, absent, held open.
string sandbox = Path.Combine(Path.GetTempPath(), "red-usertype-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
try
{
    string config = Path.Combine(sandbox, "config.json"), backup = config + ".bak";

    File.WriteAllText(config, "{\"Username\":\"a@b.c\",\"UserType\":\"\"}");
    var blank = ReadType(sandbox);
    Check(!blank.Unreadable && blank.Kind == InspectUserKind.Missing && blank.BackupDate == null,
        "sandbox: blank UserType reads as Missing, not unreadable");

    File.WriteAllText(config, "{\"Username\":\"a@b.c\",\"UserType\":\"Preins"); // truncated mid-write
    File.WriteAllText(backup, "{\"Username\":\"a@b.c\",\"UserType\":\"Preinspector\"}");
    File.SetLastWriteTime(backup, new DateTime(2025, 7, 24, 9, 0, 0));
    var fromBackup = ReadType(sandbox);
    Check(!fromBackup.Unreadable && fromBackup.Kind == InspectUserKind.Preinspector && fromBackup.BackupDate?.Date == new DateTime(2025, 7, 24),
        "sandbox: truncated config.json falls back to config.json.bak");
    Check(InspectUserType.BackupMessage(fromBackup.BackupDate!.Value) ==
          "INSPECT's settings file could not be read. Values were taken from its backup dated 7/24/2025.",
        "sandbox: backup message wording and date (S4)");

    File.WriteAllText(backup, "not json");
    var unreadable = ReadType(sandbox);
    Check(unreadable.Unreadable && unreadable.Value == null && unreadable.Kind == InspectUserKind.Missing,
        "sandbox: neither file parses -> Unreadable; Add refuses as for Missing (O9)");

    File.Delete(config);
    File.Delete(backup);
    var absent = ReadType(sandbox);
    Check(absent.Unreadable && absent.Kind == InspectUserKind.Missing, "sandbox: no config files -> Unreadable (S4/V3 'could not read')");
    Check(ReadType(null).Unreadable, "no Inspect folder -> Unreadable");

    // A lock that outlasts a few tries but ends within F-002 R4's 100/200/400 ms still reads the live file.
    File.WriteAllText(config, "{\"UserType\":\"NonDropBoxInspector\"}");
    File.WriteAllText(backup, "{\"UserType\":\"Inspector\"}");
    var locker = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    var release = Task.Run(async () => { await Task.Delay(400); locker.Dispose(); });
    var afterLock = ReadType(sandbox);
    await release;
    Check(afterLock.Kind == InspectUserKind.NonDropBox && afterLock.BackupDate == null,
        "sandbox: a 400 ms exclusive lock is waited out (live value, not the .bak)");
    File.Delete(config);
    File.Delete(backup);

    // INSPECT holds its config open for writing (sharing Read only); RED still reads it.
    File.WriteAllText(config, "{\"Username\":\"a@b.c\",\"UserType\":\"Inspector\"}");
    using (new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        var held = ReadType(sandbox);
        Check(!held.Unreadable && held.Kind == InspectUserKind.Inspector, "sandbox: config held open for write by INSPECT still reads");
    }
    Check(File.ReadAllText(config) == "{\"Username\":\"a@b.c\",\"UserType\":\"Inspector\"}", "sandbox: RED never rewrote config.json");
    Check(Directory.GetFiles(sandbox).Length == 1, "sandbox: RED created no files");

    // S5: INSPECT's configured folder wins over a stale default folder, so its .bak is used (S4).
    string configured = Path.Combine(sandbox, "configured"), stale = Path.Combine(sandbox, "stale");
    Directory.CreateDirectory(configured);
    Directory.CreateDirectory(stale);
    File.WriteAllText(Path.Combine(configured, "config.json.bak"), "{\"UserType\":\"Preinspector\"}");
    File.WriteAllText(Path.Combine(stale, "config.json"), "{\"UserType\":\"Inspector\"}");
    string? picked = StrandCredentialService.SelectInspectorFolder(configured, new[] { stale });
    var pickedReading = ReadType(picked);
    Check(picked == configured && pickedReading.Kind == InspectUserKind.Preinspector && pickedReading.BackupDate != null,
        "S5: configured folder with only a .bak is picked over a default folder's config.json");
    Check(StrandCredentialService.SelectInspectorFolder(Path.Combine(sandbox, "missing"), new[] { stale }) == Path.Combine(sandbox, "missing"),
        "S5: a configured folder is never swapped for a default one (Add then reports the missing folder)");
    Check(StrandCredentialService.SelectInspectorFolder(null, new[] { Path.Combine(sandbox, "none"), configured, stale }) == configured,
        "no configured folder: first default with config.json or .bak");
}
finally
{
    Directory.Delete(sandbox, recursive: true);
}

// Settings lines (O10), one per kind.
Check(InspectUserType.KindLine(InspectUserKind.Missing) == "Not set. Set the Type in INSPECT Settings, then press Import from INSPECT.", "Settings line for Missing");
Check(InspectUserType.KindLine(InspectUserKind.Inspector) == "Submits through the Outbox; Dropbox delivers it.", "Settings line for Inspector");
Check(InspectUserType.KindLine(InspectUserKind.Preinspector) == "Submit uploads each inspection to Strand.", "Settings line for Preinspector");

// Assigned To list (owner 2026-10-18: one "[Unassigned]", as INSPECT's GetInspectorList binds the server's row).
var fromServer = StrandScheduleService.ArrangeInspectorOptions(new[]
{
    new StrandUserOption { Username = "zoe@example.com", DisplayName = "Zoe Smith" },
    new StrandUserOption { Username = "placeholder-id", DisplayName = "[Unassigned]" },
    new StrandUserOption { Username = "amy@example.com", DisplayName = "Amy Jones" },
    new StrandUserOption { Username = "second-id", DisplayName = " [unassigned] " },
    new StrandUserOption { Username = "", DisplayName = "No Login" },
});
Check(fromServer.Count(o => o.IsUnassigned) == 1 && fromServer[0].IsUnassigned && fromServer[0].Username == "placeholder-id",
    "Assigned To: exactly one [Unassigned], first, keeping the server's username");
Check(fromServer.Skip(1).Select(o => o.DisplayName).SequenceEqual(new[] { "Amy Jones", "Zoe Smith" }),
    "Assigned To: inspectors follow, sorted by name, rows without a username dropped");
var noServerRow = StrandScheduleService.ArrangeInspectorOptions(new[] { new StrandUserOption { Username = "amy@example.com", DisplayName = "Amy Jones" } });
Check(noServerRow.Count(o => o.IsUnassigned) == 1 && noServerRow[0].IsUnassigned && noServerRow[0].Username == "",
    "Assigned To: RED adds [Unassigned] only when the server sends none");
Check(StrandScheduleService.ArrangeInspectorOptions(Array.Empty<StrandUserOption>()).Single().IsUnassigned,
    "Assigned To: offline fallback is a single [Unassigned]");

// Inspector profile (F-003, owner 2026-10-18): full import, required items, changes list, RED's own store.
string profileBox = Path.Combine(Path.GetTempPath(), "red-profile-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profileBox);
try
{
    string inspections = Path.Combine(profileBox, "Inspections");
    Directory.CreateDirectory(inspections);
    string userConfig = Path.Combine(profileBox, "UserConfig.json");
    string formConfig = Path.Combine(inspections, "config.json");
    File.WriteAllText(userConfig, new JObject
    {
        ["InspectorFolder"] = inspections, ["ReportViewer_Pictures"] = false, ["SketchPenColor"] = "Blue"
    }.ToString());
    File.WriteAllText(formConfig, new JObject
    {
        ["Username"] = "pat@example.com", ["Password"] = InspectCipher.Encrypt("pw-one"), ["Apps2ApiURL"] = "https://apps.example",
        ["InspectorName"] = "Pat Q Example", ["Phone"] = "(555) 010-0100", ["UserType"] = "Preinspector", ["DefaultState"] = "tx",
        ["UseNumberPad"] = true, ["PdfAppProgID"] = "FoxitReader", ["Signature"] = "c2lnbmF0dXJl", ["ArchiveRetention"] = 8
    }.ToString());
    string HashOf(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    string userHash = HashOf(userConfig), formHash = HashOf(formConfig);
    var userTime = File.GetLastWriteTimeUtc(userConfig);

    var import = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026");
    var p = import.Profile!;
    Check(p.InspectorName == "Pat Q Example" && p.Phone == "(555) 010-0100" && p.UserType == "Preinspector" && p.DefaultState == "TX" &&
          p.InspectionsFolder == inspections && p.UseNumberPad && p.PdfAppProgId == "FoxitReader" && p.Signature == "c2lnbmF0dXJl" &&
          !p.ReportViewerDefectsOnly && !p.ReportViewerPictures && p.ReportViewerAttachments && p.SketchPenColor == "Blue" &&
          p.ImportedFrom == formConfig && import.BackupDate == null && import.Message == null,
        "import: every S1/S2 value is brought over (A-003-01)");
    Check(import.Login is { Username: "pat@example.com", Password: "pw-one", ApiUrl: "https://apps.example" },
        "import: the Strand login comes over with the decrypted password");
    Check(HashOf(userConfig) == userHash && HashOf(formConfig) == formHash && File.GetLastWriteTimeUtc(userConfig) == userTime &&
          Directory.GetFiles(inspections).Length == 1 && Directory.GetDirectories(inspections).Length == 0,
        "import: INSPECT's files are only read, and nothing is created (A-003-02, A-003-11)");

    // A-003-09: RED's selected folder B wins over the folder INSPECT's UserConfig names (A); a selected folder without
    // INSPECT settings falls back to INSPECT's folder, which the changes list then shows.
    string folderB = Path.Combine(profileBox, "InspectionsB");
    Directory.CreateDirectory(folderB);
    File.WriteAllText(Path.Combine(folderB, "config.json"), "{\"InspectorName\":\"Bea Other\",\"UserType\":\"Inspector\"}");
    var fromB = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026", folderB).Profile!;
    Check(fromB.InspectorName == "Bea Other" && fromB.InspectionsFolder == folderB, "import: RED's selected folder's settings are offered (A-003-09)");
    string emptyFolder = Path.Combine(profileBox, "Empty");
    Directory.CreateDirectory(emptyFolder);
    var fallback = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026", emptyFolder).Profile!;
    Check(fallback.InspectionsFolder == inspections && fallback.InspectorName == "Pat Q Example",
        "import: a selected folder without INSPECT settings falls back to INSPECT's folder");
    Check(InspectorProfileService.IsInside(Path.Combine(inspections, "MyList"), inspections) &&
          InspectorProfileService.IsInside(inspections + "\\", inspections) &&
          !InspectorProfileService.IsInside(inspections + "Old\\MyList", inspections),
        "My List folder check: inside the Inspections folder, not a look-alike sibling");

    File.WriteAllText(formConfig, "{\"Username\":\"pat@example.com\"}");
    var defaults = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026").Profile!;
    Check(defaults.UserType == "Inspector" && defaults.DefaultState == "TX", "import: absent keys keep INSPECT's defaults (Inspector, TX)");
    File.WriteAllText(formConfig, "{\"Username\":\"pat@example.com\",\"UserType\":\"\",\"DefaultState\":null}");
    var blanks = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026").Profile!;
    Check(blanks.UserType == "" && blanks.DefaultState == "", "import: blank values stay blank (never Inspector)");

    File.WriteAllText(formConfig, "{\"InspectorName\":\"Pat Exa"); // truncated mid-write
    File.WriteAllText(formConfig + ".bak", "{\"InspectorName\":\"Pat Example\",\"UserType\":\"Inspector\"}");
    File.SetLastWriteTime(formConfig + ".bak", new DateTime(2025, 7, 24, 9, 0, 0));
    var fromBak = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026");
    Check(fromBak.Profile?.InspectorName == "Pat Example" &&
          fromBak.Message == "INSPECT's settings file could not be read. Values were taken from its backup dated 7/24/2025.",
        "import: a damaged config.json falls back to .bak with the S4 message (A-003-03)");
    File.WriteAllText(formConfig + ".bak", "not json");
    var nothing = InspectorProfileService.ReadFromInspect(userConfig, Array.Empty<string>(), "Inspect 2026");
    Check(nothing.Profile == null && nothing.Message == "RED could not read INSPECT's settings. Enter them in RED Settings.",
        "import: nothing readable gives the S4 could-not-read message (A-003-04)");

    // Required items: INSPECT's start-up check (owner, 2026-10-18).
    var good = new InspectorProfile { InspectorName = "Pat Example", DefaultState = "TX", UserType = "Inspector", InspectionsFolder = inspections };
    var login = new StrandCredentials { Username = "pat@example.com", Password = "pw" };
    Check(InspectorProfileService.MissingItems(good, login).Count == 0, "complete profile: nothing missing");
    void Missing(string what, InspectorProfile? profile, StrandCredentials? strand, string expected) =>
        Check(InspectorProfileService.MissingItems(profile, strand).Any(m => m.StartsWith(expected)), $"missing {what} -> \"{expected}...\"");
    Missing("one-word name", CloneWith(good, x => x.InspectorName = "Pat"), login, "Full name");
    Missing("username without @", good, new StrandCredentials { Username = "inspector.example.com", Password = "pw" }, "Strand username");
    Missing("password", good, new StrandCredentials { Username = "pat@example.com" }, "Strand password");
    Missing("Strand login", good, null, "Strand password");
    Missing("state", CloneWith(good, x => x.DefaultState = ""), login, "Default state");
    Missing("blank user type", CloneWith(good, x => x.UserType = "  "), login, "User type");
    Missing("null user type", CloneWith(good, x => x.UserType = null), login, "User type");
    Missing("folder", CloneWith(good, x => x.InspectionsFolder = Path.Combine(profileBox, "nope")), login, "Inspections folder");
    Check(InspectorProfileService.MissingItems(null, null).Count == 6, "no profile and no login: all six items missing");
    Check(InspectorProfileService.MissingItems(CloneWith(good, x => x.UserType = "Field Tech"), login).Count == 0,
        "an unoffered user type (kind Other) counts as set, as in INSPECT");
    Check(InspectorProfileService.ValidateName("Pat") == "Full name required." &&
          InspectorProfileService.ValidateUsername("inspector.example.com") == "Not a valid email address." &&
          InspectorProfileService.ValidateFolder(Path.Combine(profileBox, "nope")) == "Folder does not exist.",
        "field messages match INSPECT's (A-003-06)");

    // Changes list (O2, A-003-05, A-003-14): old -> new, never a password or signature.
    var incoming = new InspectSettingsRead
    {
        Profile = CloneWith(good, x => { x.Phone = "(555) 010-0101"; x.UserType = "NonDropBoxInspector"; x.Signature = "bmV3"; }),
        Login = new StrandCredentials { Username = "pat@example.com", Password = "secret-new" }
    };
    var changes = InspectorProfileService.Diff(CloneWith(good, x => x.Phone = "(555) 010-0100"), login, incoming).Select(c => c.ToString()).ToList();
    Check(changes.Contains("Phone: (555) 010-0100 -> (555) 010-0101") && changes.Contains("User type: Inspector -> NonDropBoxInspector") &&
          changes.Any(c => c.StartsWith("Strand password:")) && changes.Any(c => c.StartsWith("Signature:")),
        "changes list shows each changed value as old -> new");
    Check(!changes.Any(c => c.Contains("secret-new") || c.Contains("pw") && c.StartsWith("Strand password") || c.Contains("bmV3")),
        "changes list never shows a password or the signature");
    Check(InspectorProfileService.Diff(good, login, new InspectSettingsRead { Profile = good.Clone(), Login = login }).Count == 0,
        "no changes when RED already matches INSPECT");

    // RED's own store round-trips, atomically, and keeps a null user type apart from a blank one.
    string store = Path.Combine(profileBox, "RED", "inspector_profile.json");
    InspectorProfileService.Save(CloneWith(p, x => x.UserType = null), store);
    InspectorProfileService.Save(CloneWith(p, x => x.UserType = null), store); // a second save replaces the first
    var back = InspectorProfileService.Load(store)!;
    Check(back.UserType == null && back.InspectorName == "Pat Q Example" && back.SketchPenColor == "Blue" &&
          Directory.GetFiles(Path.GetDirectoryName(store)!, "*.tmp").Length == 0,
        "profile store round-trips with no temp file left");
    // A failed replace (target locked) cleans up its temp file and leaves the saved profile as it was.
    bool saveFailed = false;
    using (new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try { InspectorProfileService.Save(CloneWith(p, x => x.InspectorName = "Changed Name"), store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveFailed = true; }
    }
    Check(saveFailed && Directory.GetFiles(Path.GetDirectoryName(store)!, "*.tmp").Length == 0 &&
          InspectorProfileService.Load(store)!.InspectorName == "Pat Q Example",
        "a failed save leaves no temp file and keeps the previous profile");
    File.WriteAllText(store, "{ broken");
    Check(InspectorProfileService.Load(store) == null, "a damaged profile file reads as no profile (locked, not a crash)");
}
finally
{
    Directory.Delete(profileBox, recursive: true);
}

static InspectorProfile CloneWith(InspectorProfile source, Action<InspectorProfile> change)
{
    var copy = source.Clone();
    change(copy);
    return copy;
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
