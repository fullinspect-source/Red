// Storage audit F4/F5 harness: RED's start-up cleanup of its own local safety copies and temporary files
// (Services\LocalHousekeeping.cs). Recovery copies 90 days and 1 GB; other safety copies and temp files 30 days; exact
// folders and names only; links, open files and the Inspections folder are never touched. Sandbox folders under %TEMP%
// stand in for %LOCALAPPDATA%\RED, %TEMP% and the Inspections folder; synthetic files only.
//   dotnet run -p:Platform=x64 [RED source folder]
// Exit 0 = all passed.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using InspectionEditor.Services;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
return Harness.Run(args);

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0);
    private static string Hex() => Guid.NewGuid().ToString("N");

    private sealed class Box : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-storage-local-" + Guid.NewGuid().ToString("N"));
        public string Lad => Path.Combine(Root, "LocalAppData", "RED");
        public string Temp => Path.Combine(Root, "Temp");
        public string IF => Path.Combine(Root, "Inspections");
        public Box()
        {
            Directory.CreateDirectory(Lad);
            Directory.CreateDirectory(Temp);
            foreach (var f in new[] { "MyList", "Review", "Archive", "Outbox" }) Directory.CreateDirectory(Path.Combine(IF, f));
        }
        public LocalHousekeeping.Locations Where(bool temp = true, long cap = LocalHousekeeping.RecoveryCapBytes) => new()
        {
            LocalAppData = Lad, Temp = Temp, InspectionsFolder = IF, IncludeTemp = temp, IncludeUpdater = temp,
            IncludeCamera = false, RecoveryCap = cap
        };
        public string File(double daysOld, int size, params string[] parts)
        {
            string path = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, new byte[size]);
            System.IO.File.SetLastWriteTime(path, Now.AddDays(-daysOld));
            return path;
        }
        public string Folder(double daysOld, params string[] parts)
        {
            string path = Path.Combine(parts);
            Directory.CreateDirectory(path);
            System.IO.File.WriteAllBytes(Path.Combine(path, "content.pdf"), new byte[50]);
            Directory.SetLastWriteTime(path, Now.AddDays(-daysOld));
            return path;
        }
        public void Dispose()
        {
            try
            {
                foreach (var link in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
                             .Where(d => (System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
                    Directory.Delete(link);
                foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    System.IO.File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Console.WriteLine("note: sandbox not fully deleted"); }
        }
    }

    public static int Run(string[] args)
    {
        try
        {
            Recovery();
            SafetyCopies();
            Records();
            StagingAndLegacy();
            TempFiles();
            NeverTouched();
            SourceChecks(args.Length > 0 ? args[0] : FindRepoRoot());
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString());
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Recovery()
    {
        using (var box = new Box())
        {
            string rec = Path.Combine(box.Lad, "Recovery");
            string old = box.File(91, 100, rec, $"failed-save-20260723T101010123Z-{Hex()}.ins");
            string oldReplaced = box.File(91, 100, rec, $"replaced-by-overwrite-20260723T101010123Z-{Hex()}.ins");
            string oldTemp = box.File(91, 100, rec, $"paused-edits-20260723T101010123Z-{Hex()}.ins.tmp");
            string young = box.File(89, 100, rec, $"paused-edits-20260725T101010123Z-{Hex()}.ins");
            string oldJob = box.File(91, 100, rec, "job-files", "1234567FD (replaced 20260723-101010).pdf");
            string youngJob = box.File(89, 100, rec, "job-files", "1234567FD (replaced 20260725-101010).pdf");
            string other = box.File(400, 100, rec, "notes.ins");
            string otherJob = box.File(400, 100, rec, "job-files", "1234567FD.pdf");
            var result = LocalHousekeeping.Run(Now, box.Where());
            Check("F4-recovery-age", !System.IO.File.Exists(old) && !System.IO.File.Exists(oldReplaced) && !System.IO.File.Exists(oldTemp) && !System.IO.File.Exists(oldJob) &&
                                     System.IO.File.Exists(young) && System.IO.File.Exists(youngJob) && result.Area("Recovery").Files == 4,
                "recovery copies (failed saves, overwrites, paused edits, replaced job PDFs) are kept 90 days: 91 days goes, 89 stays");
            Check("F4-recovery-names", System.IO.File.Exists(other) && System.IO.File.Exists(otherJob), "other names in Recovery are never touched");
        }
        using (var box = new Box())
        {
            string rec = Path.Combine(box.Lad, "Recovery");
            var copies = new[] { 60.0, 40, 20, 5 }.Select(d => box.File(d, 400, rec, $"failed-save-20260801T101010123Z-{Hex()}.ins")).ToList();
            string today = box.File(0.2, 400, rec, $"failed-save-20261022T101010123Z-{Hex()}.ins");
            // Limit 1,000 bytes for the test (the real one is 1 GB): 2,000 → oldest first until 1,000 or less.
            LocalHousekeeping.Run(Now, box.Where(cap: 1000));
            Check("F4-recovery-cap", !System.IO.File.Exists(copies[0]) && !System.IO.File.Exists(copies[1]) && !System.IO.File.Exists(copies[2]) &&
                                     System.IO.File.Exists(copies[3]) && System.IO.File.Exists(today),
                "over the limit the oldest recovery copies go first until it is met (2,000 → 800 bytes)");
            string big = box.File(0.1, 5000, rec, $"failed-save-20261022T111111123Z-{Hex()}.ins");
            LocalHousekeeping.Run(Now, box.Where(cap: 1000));
            Check("F4-recovery-today", System.IO.File.Exists(big) && System.IO.File.Exists(today) && !System.IO.File.Exists(copies[3]),
                "the limit never takes a copy from the last day, even when it alone is over");
            Check("F4-cap-constant", LocalHousekeeping.RecoveryCapBytes == 1L << 30 && LocalHousekeeping.RecoveryDays == 90 && LocalHousekeeping.SafetyCopyDays == 30,
                "the limits are 90 days and 1 GB for recovery copies, 30 days for the other copies");
        }
    }

    private static void SafetyCopies()
    {
        using var box = new Box();
        string sync = Path.Combine(box.Lad, "SyncBackups");
        string syncOld = box.Folder(0, sync, "2026-09-21", "MyList");          // folder name 31 days back
        string syncEdge = box.Folder(400, sync, "2026-09-22", "Review");       // 30 days back by name; old file times
        string syncOther = box.Folder(400, sync, "notes");
        string syncBad = box.Folder(400, sync, "2026-13-45");
        string move = Path.Combine(box.Lad, "MoveBackups");
        string moveOld = box.File(31, 10, move, "20260921-101010123-MyList-1234567-CPP-1-PQE.ins");
        string moveYoung = box.File(29, 10, move, "20260923-101010123-MyList-1234567-CPP-1-PQE.ins");
        string moveOther = box.File(300, 10, move, "keep.ins");
        string submit = Path.Combine(box.Lad, "SubmitBackups");
        string submitOld = box.File(31, 10, submit, "20260921-101010123-1234567-CPP-1-PQE.ins");
        string submitYoung = box.File(29, 10, submit, "20260923-101010123-1234567-CPP-1-PQE.ins");
        string sent = Path.Combine(box.Lad, "sync-sent-copies");
        System.IO.File.WriteAllText(Path.Combine(box.IF, "Archive", "2222222-CPP-1-PQE.ins"), "{}");
        string sentArchived = box.File(31, 10, sent, "20260921-101010123-2222222-CPP-1-PQE.ins");
        string sentNotArchived = box.File(31, 10, sent, "20260921-101010123-3333333-CPP-1-PQE.ins");
        string sentNotArchivedOld = box.File(91, 10, sent, "20260723-101010123-4444444-CPP-1-PQE.ins");
        var result = LocalHousekeeping.Run(Now, box.Where());
        Check("F4-syncbackups", !Directory.Exists(Path.GetDirectoryName(syncOld)) && Directory.Exists(Path.GetDirectoryName(syncEdge)) &&
                                Directory.Exists(syncOther) && Directory.Exists(syncBad),
            "SyncBackups\\{date} folders go by the date in the name after 30 days (file times are ignored); other folders stay");
        Check("F4-movebackups", !System.IO.File.Exists(moveOld) && System.IO.File.Exists(moveYoung) && System.IO.File.Exists(moveOther),
            "MoveBackups copies go after 30 days; other names stay");
        Check("F4-submitbackups", !System.IO.File.Exists(submitOld) && System.IO.File.Exists(submitYoung) && result.Area("SubmitBackups").Files == 1,
            "SubmitBackups keeps 30 days (now run by the start-up cleanup)");
        Check("F4-sentcopies", !System.IO.File.Exists(sentArchived) && System.IO.File.Exists(sentNotArchived) && !System.IO.File.Exists(sentNotArchivedOld),
            "a sent copy goes after 30 days once the Archive has the report, else after 90 days");
    }

    private static void Records()
    {
        using var box = new Box();
        string vs = Path.Combine(box.Lad, "VerifiedSaves");
        string vsOld = box.File(91, 10, vs, new string('A', 64) + ".json");
        string vsYoung = box.File(89, 10, vs, new string('B', 64) + ".json");
        string fc = Path.Combine(box.Lad, "framing-cache");
        string fcOld = box.File(91, 10, fc, new string('a', 64) + ".json");
        string fcYoung = box.File(89, 10, fc, new string('b', 64) + ".json");
        string cfg = Path.Combine(box.Lad, "Backups", "inspect-config");
        var configs = Enumerable.Range(1, 12).Select(i => box.File(0, 10, cfg, $"config-202610{i:00}-101010-123.json")).ToList();
        string cfgOther = box.File(0, 10, cfg, "config.json");
        LocalHousekeeping.Run(Now, box.Where());
        Check("F4-records", !System.IO.File.Exists(vsOld) && System.IO.File.Exists(vsYoung) && !System.IO.File.Exists(fcOld) && System.IO.File.Exists(fcYoung),
            "VerifiedSaves and framing-cache records not written for 90 days go");
        Check("F4-config-copies", !System.IO.File.Exists(configs[0]) && !System.IO.File.Exists(configs[1]) && configs.Skip(2).All(System.IO.File.Exists) &&
                                  System.IO.File.Exists(cfgOther),
            "of INSPECT's config.json copies the newest 10 stay");
    }

    private static void StagingAndLegacy()
    {
        using var box = new Box();
        string outbox = Path.Combine(box.Lad, "outbox-staging");
        string stagedOld = box.File(31, 10, outbox, $"1234567-CPP-1-PQE.ins.{Hex()}.tmp");
        string movedYoung = box.File(29, 10, box.Lad, "move-staging", $"1234567-CPP-1-PQE.ins.{Hex()}.tmp");
        string stateOld = box.File(31, 10, box.Lad, $"housekeeping.json.{Hex()}.tmp");
        string state = box.File(400, 10, box.Lad, "housekeeping.json");
        string pendingOld = box.File(31, 10, box.Lad, "sync-pending", $"pending-deletes.json.{Hex()}.tmp");
        string legacy = Path.Combine(box.Lad, "SaveStaging");
        string legacyOld = box.File(31, 10, legacy, $"1234567-CPP-1-PQE.ins.red-save-backup-{Hex()}.completed");
        string legacyNested = box.File(31, 10, legacy, "Temp", $"1234567-CPP-1-PQE.ins.red-{Hex()}.tmp");
        string legacyYoung = box.File(29, 10, legacy, $"7654321-CPP-1-PQE.ins.red-save-backup-{Hex()}.completed");
        string errorsLog = box.File(31, 10, box.Lad, "red_errors.log");
        var result = LocalHousekeeping.Run(Now, box.Where());
        Check("F4-staging", !System.IO.File.Exists(stagedOld) && !Directory.Exists(outbox) && System.IO.File.Exists(movedYoung) &&
                            !System.IO.File.Exists(stateOld) && System.IO.File.Exists(state) && !System.IO.File.Exists(pendingOld),
            "left-over staging and state .tmp files more than 30 days old go (an emptied staging folder too); the state files stay");
        Check("F4-legacy", !System.IO.File.Exists(legacyOld) && !System.IO.File.Exists(legacyNested) && !Directory.Exists(Path.Combine(legacy, "Temp")) &&
                           System.IO.File.Exists(legacyYoung) && !System.IO.File.Exists(errorsLog),
            "the old SaveStaging copies and red_errors.log (no RED writes them now) go after 30 days");
        string line = LocalHousekeeping.Describe(result);
        Check("F4-log", line.StartsWith("Local cleanup: ") && line.Contains("staging: ") && !line.Contains("1234567") && !line.Contains("housekeeping.json"),
            $"one log line with counts and sizes per area, no file names ({line})");
    }

    private static void TempFiles()
    {
        using (var box = new Box())
        {
            string jobs = Path.Combine(box.Temp, "RED-JobFiles");
            string planOld = box.Folder(31, jobs, Hex());
            System.IO.File.SetAttributes(Path.Combine(planOld, "content.pdf"), FileAttributes.ReadOnly);
            Directory.SetLastWriteTime(planOld, Now.AddDays(-31));
            string planYoung = box.Folder(29, jobs, Hex());
            string planOpen = box.Folder(31, jobs, Hex());
            string planOther = box.Folder(300, jobs, "not-a-guid");
            string check = box.File(31, 10, box.Temp, $"RED_PlanCheck_{Hex()}.pdf");
            string checkYoung = box.File(29, 10, box.Temp, $"RED_PlanCheck_{Hex()}.pdf");
            string checkOther = box.File(300, 10, box.Temp, "RED_PlanCheck_x.pdf");
            string reports = Path.Combine(box.Temp, "RED-Reports");
            string previewOld = box.Folder(2, reports, Hex());
            string previewYoung = box.Folder(0.5, reports, Hex());
            string sandboxOld = box.Folder(31, box.Temp, "red-save-loss-recovery-" + Hex());
            string sandboxOld2 = box.Folder(31, box.Temp, "RedUpdaterTests-" + Guid.NewGuid());
            string sandboxYoung = box.Folder(29, box.Temp, "red-f002-appdata-" + Hex());
            string otherApp = box.Folder(300, box.Temp, "red-something-else");
            string otherApp2 = box.Folder(300, box.Temp, Hex());
            string update = box.Folder(1, box.Temp, "RedUpdate", Hex());
            Directory.SetLastWriteTime(update, Now.AddDays(-1));
            CleanupTally result;
            using (new FileStream(Path.Combine(planOpen, "content.pdf"), FileMode.Open, FileAccess.Read, FileShare.Read))
                result = LocalHousekeeping.Run(Now, box.Where()).Total;
            Check("F5-plans", !Directory.Exists(planOld) && Directory.Exists(planYoung) && Directory.Exists(planOpen) && Directory.Exists(planOther),
                "read-only plan copies in %TEMP%\\RED-JobFiles go after 30 days (RED's read-only flag cleared); one still open in a PDF app stays");
            Check("F5-plancheck", !System.IO.File.Exists(check) && System.IO.File.Exists(checkYoung) && System.IO.File.Exists(checkOther),
                "Plan Check PDFs left in %TEMP% go after 30 days");
            Check("F5-previews", !Directory.Exists(previewOld) && Directory.Exists(previewYoung),
                "preview, print and e-mail files in %TEMP%\\RED-Reports keep their 1-day rule, now also at start");
            Check("F5-sandboxes", !Directory.Exists(sandboxOld) && !Directory.Exists(sandboxOld2) && Directory.Exists(sandboxYoung) &&
                                  Directory.Exists(otherApp) && Directory.Exists(otherApp2),
                "RED test sandboxes left in %TEMP% go after 30 days, by their exact names; other programs' folders stay");
            Check("F5-updater", !Directory.Exists(update) && result.Skipped >= 1,
                "the in-app updater's old download folder goes too (open files are counted as skipped)");
        }
        using (var box = new Box())
        {
            string planOld = box.Folder(31, box.Temp, "RED-JobFiles", Hex());
            string sandboxOld = box.Folder(31, box.Temp, "red-save-loss-recovery-" + Hex());
            string recovery = box.File(91, 10, box.Lad, "Recovery", $"failed-save-20260723T101010123Z-{Hex()}.ins");
            LocalHousekeeping.Run(Now, box.Where(temp: false));
            Check("F5-dev", Directory.Exists(planOld) && Directory.Exists(sandboxOld) && !System.IO.File.Exists(recovery),
                "a dev build without RED_DEV_HOUSEKEEPING=1 cleans only its own app data, never the shared %TEMP%");
        }
    }

    private static void NeverTouched()
    {
        using var box = new Box();
        // The Inspections folder, Review included, is never part of this cleanup, whatever the names and ages.
        var inspections = new List<string>
        {
            box.File(400, 10, box.IF, "Review", "1234567-CPP-1-PQE.ins"),
            box.File(400, 10, box.IF, "Review", $"1234567-CPP-1-PQE.ins.red-save-backup-{Hex()}.completed"),
            box.File(400, 10, box.IF, "MyList", $"failed-save-20250101T101010123Z-{Hex()}.ins"),
            box.File(400, 10, box.IF, "Archive", "2222222-CPP-1-PQE.ins"),
            box.File(400, 10, box.IF, "MyList", ".red-save-staging", "Review", $"x.ins.red-save-backup-{Hex()}.completed"),
        };
        // A Recovery folder that is a link, and a link inside a plan-copy folder: nothing behind them is touched.
        string elsewhere = Path.Combine(box.Root, "elsewhere");
        string precious = box.File(400, 10, elsewhere, $"failed-save-20250101T101010123Z-{Hex()}.ins");
        Junction(Path.Combine(box.Lad, "Recovery"), elsewhere);
        string plan = box.Folder(31, box.Temp, "RED-JobFiles", Hex());
        Junction(Path.Combine(plan, "link"), elsewhere);
        Directory.SetLastWriteTime(plan, Now.AddDays(-31));
        // A step that fails (a file where a folder should be) does not stop the others.
        System.IO.File.WriteAllText(Path.Combine(box.Lad, "MoveBackups"), "not a folder");
        string submitOld = box.File(31, 10, box.Lad, "SubmitBackups", "20260921-101010123-1234567-CPP-1-PQE.ins");
        LocalHousekeeping.Result? result = null;
        Exception? thrown = null;
        try { result = LocalHousekeeping.Run(Now, box.Where()); } catch (Exception ex) { thrown = ex; }
        Check("F4-inspections", inspections.All(System.IO.File.Exists), "nothing in the Inspections folder (My List, Review, Archive, staging) is touched");
        Check("F4-links", System.IO.File.Exists(precious) && Directory.Exists(plan), "links are never followed: their targets, and a plan folder holding one, stay");
        Check("F4-robust", thrown == null && result != null && !System.IO.File.Exists(submitOld), "an odd folder does not stop the other steps or reach RED");
    }

    private static void SourceChecks(string root)
    {
        string Read(params string[] parts)
        {
            string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
        }
        string housekeeping = Read("App.Housekeeping.cs");
        if (housekeeping.Length == 0)
        {
            Check("source", false, "RED source not found (pass the RED source folder as the first argument)");
            return;
        }
        int launch = housekeeping.IndexOf("private static async Task RunLaunchHousekeepingAsync()", StringComparison.Ordinal);
        int run = housekeeping.IndexOf("LocalHousekeeping.RunAtStart(DateTime.Now, AppIdentity.IsDevBuild, DevHousekeepingRequested)", StringComparison.Ordinal);
        int folderCheck = housekeeping.IndexOf("string? folder = InspectorProfileService.Load()?.InspectionsFolder;", launch < 0 ? 0 : launch, StringComparison.Ordinal);
        Check("F4-start", launch > 0 && run > launch && run < folderCheck,
            "the cleanup starts at every launch, before the Inspections-folder check (so it also runs on a device with none)");
        string viewer = Read("DocsViewerWindow.xaml.cs");
        Check("F5-at-copy", viewer.Contains("LocalHousekeeping.SweepPlanCopies(DateTime.Now)") && viewer.Contains("LocalHousekeeping.PlanCopiesFolderName"),
            "old plan copies are also swept each time a new read-only copy is made");
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (!Directory.Exists(link)) throw new Exception("junction not made");
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null && !System.IO.File.Exists(Path.Combine(dir.FullName, "InspectionEditor.csproj"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
