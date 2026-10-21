// F-015 Archive retention harness (program/features/F-015-archive-retention/ACCEPTANCE.md) and the shared F-019 L1-L3
// INSPECT-format log writer.   dotnet run -p:Platform=x64
// Sandbox folders under %TEMP% only: a stand-in Inspections folder, a stand-in INSPECT log folder and RED queue folder.
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using InspectionEditor.Services;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
return Harness.Run();

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static readonly DateTime Now = new(2026, 10, 17, 21, 25, 56);

    private sealed class Box : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-f015-" + Guid.NewGuid().ToString("N"));
        public string IF => Path.Combine(Root, "Inspections");
        public string Archive => Path.Combine(IF, "Archive");
        public string Logs => Path.Combine(Root, "StrandInspect");
        public string Queue => Path.Combine(Root, "RedLogs", "inspect-pending");
        public List<string> RedOnly { get; } = new();
        public InspectLogWriter Log { get; }
        public Box(int? retention = 8, bool archive = true)
        {
            Directory.CreateDirectory(IF);
            if (archive) Directory.CreateDirectory(Archive);
            if (retention.HasValue) File.WriteAllText(Path.Combine(IF, "config.json"), $"{{\"ArchiveRetention\":{retention.Value}}}");
            Log = new InspectLogWriter(Logs, Queue, () => Now);
        }
        public string File_(string name, DateTime modified)
        {
            string path = Path.Combine(Archive, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
            File.SetLastWriteTime(path, modified);
            return path;
        }
        public string LogText => File.Exists(Log.FileFor(Now)) ? File.ReadAllText(Log.FileFor(Now)) : "";
        public ArchiveCleanupResult Run(bool dryRun = false, HousekeepingSettings? settings = null) =>
            ArchiveRetentionService.Run(IF, Log, Now, dryRun, RedOnly.Add, settings: settings ?? new HousekeepingSettings());
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    public static int Run()
    {
        // ---------- A-015-01 On by default ----------
        using (var box = new Box())
        {
            string old = box.File_("old.ins", new DateTime(2026, 10, 1));
            var result = box.Run(settings: HousekeepingSettings.Load(box.Root)); // no settings file yet: defaults
            Check("A-015-01", new HousekeepingSettings().CleanArchive && !File.Exists(old) && result.Removed == 1 &&
                              InspectLiveSettings.ArchiveRetentionLine(8, Now).StartsWith("Archive retention: 8 days (from INSPECT's settings)."),
                "on by default: the 10/1 file is deleted with no question; Settings text names 8 days");
        }

        // ---------- A-015-02 Date rule ----------
        using (var box = new Box())
        {
            string a = box.File_("a.ins", new DateTime(2026, 10, 8, 23, 59, 0));
            string b = box.File_("b.ins", new DateTime(2026, 10, 9, 0, 1, 0));
            string c = box.File_("c.ins", new DateTime(2026, 10, 1, 8, 0, 0));
            box.Run();
            Check("A-015-02", !File.Exists(a) && File.Exists(b) && !File.Exists(c), "N = 8, RED keeps 9 days: 10/8 23:59 and 10/1 deleted; 10/9 00:01 kept (calendar date rule)");
        }

        // ---------- A-015-03 Top level only ----------
        using (var box = new Box())
        {
            string old = box.File_("old.ins", new DateTime(2026, 10, 1));
            string nested = box.File_(Path.Combine("sub", "old2.ins"), new DateTime(2026, 10, 1));
            string note = box.File_("note.txt", new DateTime(2026, 10, 1));
            box.Run();
            Check("A-015-03", !File.Exists(old) && File.Exists(nested) && File.Exists(note), "only the top-level old.ins is deleted");
        }

        // ---------- A-015-04 / A-015-13 Cap, order and the exact lines ----------
        using (var box = new Box())
        {
            var start = new DateTime(2026, 9, 1, 8, 0, 0);
            for (int i = 0; i < 780; i++) box.File_($"{i:D4}.ins", start.AddMinutes(i));
            var first = box.Run();
            var remaining = Directory.GetFiles(box.Archive).Select(Path.GetFileName).OrderBy(n => n).ToList();
            string log = box.LogText;
            string folder = box.Archive;
            Check("A-015-04", first.Removed == 200 && remaining.Count == 580 && remaining[0] == "0200.ins",
                "the 200 oldest are deleted, no question asked (A-015-13)");
            Check("A-015-04", Regex.IsMatch(log, @"^2026-10-17 21:25:56\.\d{4} \| Info \| " + Regex.Escape($"Archive cleanup: 780 file(s) in {folder} older than 10/8/2026 9:25:56 PM; removing 200 this run, 580 left for the next launch") + "\r$", RegexOptions.Multiline) &&
                              Regex.IsMatch(log, @"\| Info \| " + Regex.Escape("Archive cleanup: removed 200 file(s), 0 failed, 580 left for the next launch") + "\r$", RegexOptions.Multiline),
                "INSPECT's two lines, character for character");
            byte[] raw = File.ReadAllBytes(box.Log.FileFor(Now));
            Check("L1", raw[0] != 0xEF && log.EndsWith("\r\n") && !log.Replace("\r\n", "").Contains('\n'), "UTF-8 without BOM, CRLF endings");
            var second = box.Run();
            Check("A-015-04", second.Removed == 200 && !File.Exists(Path.Combine(box.Archive, "0399.ins")) && File.Exists(Path.Combine(box.Archive, "0400.ins")),
                "the next launch removes the next 200 oldest");
        }

        // ---------- A-015-06 / A-015-14 Bounds ----------
        foreach (int days in new[] { 0, -5 })
        {
            using var box = new Box(retention: days);
            string old = box.File_("old.ins", new DateTime(2020, 1, 1));
            var result = box.Run();
            Check(days == 0 ? "A-015-06" : "A-015-14",
                result.Refused && File.Exists(old) && !box.LogText.Contains("Archive cleanup") &&
                box.RedOnly.SequenceEqual(new[] { $"Archive cleanup refused: Archive retention is {days} days; RED needs 1 or more." }) &&
                InspectLiveSettings.ArchiveRetentionRefusal(days) == $"INSPECT's Archive retention is {days} days. RED does not clean the Archive until it is 1 or more.",
                $"{days} days: nothing deleted, no INSPECT-format line, the RED-only line and the Settings message");
        }

        // ---------- A-015-07 Read failure declines ----------
        using (var box = new Box())
        {
            string old = box.File_("old.ins", new DateTime(2026, 10, 1));
            bool denied = Icacls($"\"{box.Archive}\" /deny \"{Environment.UserName}\":(RD)");
            if (denied)
            {
                var result = box.Run();
                Icacls($"\"{box.Archive}\" /remove:d \"{Environment.UserName}\"");
                Check("A-015-07", result.Declined && File.Exists(old) &&
                                  Regex.IsMatch(box.LogText, @"\| Warn \| " + Regex.Escape($"Archive cleanup declined: could not read {box.Archive}") + "\r\nSystem\\."),
                    "the Archive cannot be listed: nothing deleted, the Warn line followed by the exception text");
            }
            else
            {
                Console.WriteLine("SKIP A-015-07 could not set a deny ACL on the sandbox folder (not counted)");
            }
        }

        // ---------- A-015-08 Per-file failure isolated ----------
        using (var box = new Box())
        {
            string a = box.File_("a.ins", new DateTime(2026, 10, 1)), b = box.File_("b.ins", new DateTime(2026, 10, 2)), c = box.File_("c.ins", new DateTime(2026, 10, 3));
            ArchiveCleanupResult result;
            using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
                result = box.Run();
            Check("A-015-08", result.Removed == 2 && result.Failed == 1 && File.Exists(b) &&
                              box.LogText.Contains($"| Warn | Archive cleanup could not remove {b}\r\nSystem.IO.IOException") &&
                              box.LogText.Contains("Archive cleanup: removed 2 file(s), 1 failed, 1 left for the next launch"),
                "a locked file is logged with its exception and counted as failed; the run continues");
        }

        // ---------- A-015-11 Dry run ----------
        using (var box = new Box())
        {
            for (int i = 0; i < 5; i++) box.File_($"{i}.ins", new DateTime(2026, 10, 1).AddHours(i));
            box.File_("new.ins", new DateTime(2026, 10, 15));
            var result = box.Run(dryRun: true);
            Check("A-015-11", result.Total == 5 && result.Cutoff == Now.AddDays(-9) && Directory.GetFiles(box.Archive).Length == 6 &&
                              !File.Exists(box.Log.FileFor(Now)) && box.RedOnly.Count == 0,
                "the dry run counts 5 candidates and the cutoff; nothing deleted, no log line");
        }

        // ---------- A-015-12 No upper bound ----------
        using (var box = new Box(retention: 400))
        {
            string old = box.File_("old.ins", new DateTime(2025, 9, 1));
            box.Run();
            Check("A-015-12", !File.Exists(old) && box.LogText.Contains("older than 9/11/2025 9:25:56 PM; removing 1 this run"),
                "400 days (RED keeps 401): the 9/1/2025 file goes; the line reads \"older than 9/11/2025 9:25:56 PM\"");
        }

        // ---------- A-015-15 / A-015-17 N read live, fallbacks ----------
        using (var box = new Box())
        {
            string config = Path.Combine(box.IF, "config.json");
            Check("A-015-15", InspectLiveSettings.ReadArchiveRetention(box.IF) == 8, "N = 8 at this launch");
            File.WriteAllText(config, "{\"ArchiveRetention\":40}");
            var result = box.Run(dryRun: true);
            Check("A-015-15", result.Days == 40 && result.Cutoff == new DateTime(2026, 9, 6, 21, 25, 56), "after INSPECT's value changes to 40: cutoff 9/6/2026 9:25:56 PM (40 + 1 days)");
            File.WriteAllText(config, "{\"Other\":1}");
            Check("A-015-17", InspectLiveSettings.ReadArchiveRetention(box.IF) == 40, "(a) no key: 40");
            File.WriteAllText(config, "{not json");
            File.WriteAllText(config + ".bak", "{\"ArchiveRetention\":8}");
            string configHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(config)));
            Check("A-015-17", InspectLiveSettings.ReadArchiveRetention(box.IF) == 8, "(b) unparsable config.json, .bak 8: 8");
            File.Delete(config); File.Delete(config + ".bak");
            Check("A-015-17", InspectLiveSettings.ReadArchiveRetention(box.IF) == 40, "(c) neither readable: 40");
        }

        // ---------- A-015-18 Switch off ----------
        using (var box = new Box())
        {
            for (int i = 0; i < 5; i++) box.File_($"{i}.ins", new DateTime(2026, 10, 1));
            var result = box.Run(settings: new HousekeepingSettings { CleanArchive = false });
            Check("A-015-18", result.Disabled && Directory.GetFiles(box.Archive).Length == 5 && !File.Exists(box.Log.FileFor(Now)),
                "switch off: nothing deleted and no line written");
        }

        // ---------- A-015-19 Missing folder and nothing to do ----------
        using (var box = new Box(archive: false))
        {
            box.Run();
            Check("A-015-19", box.LogText.Contains($"| Info | Archive cleanup: {box.Archive} does not exist, nothing to do"), "(a) no Archive folder");
        }
        using (var box = new Box())
        {
            box.File_("new.ins", new DateTime(2026, 10, 10));
            box.Run();
            Check("A-015-19", box.LogText.Contains($"| Info | Archive cleanup: nothing in {box.Archive} older than 10/8/2026 9:25:56 PM") &&
                              Directory.GetFiles(box.Archive).Length == 1, "(b) nothing older than the cutoff");
        }

        // ---------- A-015-20 A file already removed counts as removed ----------
        using (var box = new Box())
        {
            string gone = box.File_("gone.ins", new DateTime(2026, 10, 1));
            var info = new FileInfo(gone);
            File.Delete(gone);
            bool threw = false;
            try { info.Delete(); } catch { threw = true; }
            Check("A-015-20", !threw, "FileInfo.Delete on a file another cleaner already removed does not throw, so RED counts it as removed (A8)");
        }

        // ---------- F-019 L3 pending queue while INSPECT holds the day file ----------
        using (var box = new Box())
        {
            Directory.CreateDirectory(box.Logs);
            string day = box.Log.FileFor(Now);
            File.WriteAllText(day, "2026-10-17 08:00:00.0000 | Info | INSPECT's own line\r\n");
            using (new FileStream(day, FileMode.Open, FileAccess.Write, FileShare.Read)) // INSPECT 2026: writer open, readers only
                box.Log.Info("first");
            bool queued = Directory.GetFiles(box.Queue).Length == 1 && !File.ReadAllText(day).Contains("first");
            box.Log.Info("second");
            string text = File.ReadAllText(day);
            Check("L3", queued && text.StartsWith("2026-10-17 08:00:00.0000 | Info | INSPECT's own line\r\n") &&
                        text.IndexOf("| first", StringComparison.Ordinal) < text.IndexOf("| second", StringComparison.Ordinal) &&
                        text.IndexOf("| first", StringComparison.Ordinal) > 0 && Directory.GetFiles(box.Queue).Length == 0,
                "while the file is held, the entry waits in RED's queue; the next write appends it first, then its own; INSPECT's line untouched");
        }

        // ---------- A-015-21 / A-015-22 Owner 2026-10-20: RED keeps one day more than INSPECT (setting 5) ----------
        foreach (var at in new[] { new DateTime(2026, 10, 20, 0, 0, 30), new DateTime(2026, 10, 20, 10, 0, 0), new DateTime(2026, 10, 20, 23, 59, 30) })
        {
            using var box = new Box(retention: 5);
            // Counting today (10/20) as day 1: 10/16 is the 5th day, 10/15 the 6th, 10/14 the 7th, 10/13 the 8th.
            string day5 = box.File_("day5.ins", new DateTime(2026, 10, 16, 0, 0, 0));
            string day6early = box.File_("day6-early.ins", new DateTime(2026, 10, 15, 0, 0, 1));
            string day6late = box.File_("day6-late.ins", new DateTime(2026, 10, 15, 23, 59, 59));
            string day7late = box.File_("day7-late.ins", new DateTime(2026, 10, 14, 23, 59, 59));
            string day7early = box.File_("day7-early.ins", new DateTime(2026, 10, 14, 0, 0, 1));
            string day8 = box.File_("day8.ins", new DateTime(2026, 10, 13, 12, 0, 0));
            var result = ArchiveRetentionService.Run(box.IF, new InspectLogWriter(box.Logs, box.Queue, () => at), at, redOnlyLog: box.RedOnly.Add, settings: new HousekeepingSettings());
            Check("A-015-21", File.Exists(day5) && File.Exists(day6early) && File.Exists(day6late) && !File.Exists(day7late) && !File.Exists(day7early) && !File.Exists(day8) &&
                              result.Days == 5 && result.Removed == 3 && result.Cutoff.Date == new DateTime(2026, 10, 14),
                $"setting 5 at {at:HH:mm:ss} on 10/20: 5- and 6-day-old files (10/16, 10/15) kept; from day 7 (10/14 and older) deleted");
        }
        {
            var at = new DateTime(2026, 10, 20, 10, 0, 0);
            Check("A-015-22", ArchiveRetentionService.ExtraDays == 1 && ArchiveRetentionService.CutoffFor(at, 5) == new DateTime(2026, 10, 14, 10, 0, 0) &&
                              InspectLiveSettings.ArchiveRetentionLine(5, at) == "Archive retention: 5 days (from INSPECT's settings). RED keeps one day more than INSPECT: " +
                              "at each start it deletes archived inspections last changed on or before 10/14/2026. They are removed from every device that shares this Dropbox folder.",
                "the cutoff is INSPECT's rule with one day more (10/14/2026 10:00:00 AM); Settings names 10/14/2026");
            using var box = new Box(retention: 5);
            box.File_("day7.ins", new DateTime(2026, 10, 14, 9, 0, 0));
            var log = new InspectLogWriter(box.Logs, box.Queue, () => at);
            ArchiveRetentionService.Run(box.IF, log, at, redOnlyLog: box.RedOnly.Add, settings: new HousekeepingSettings());
            string text = File.Exists(log.FileFor(at)) ? File.ReadAllText(log.FileFor(at)) : "";
            Check("A-015-22", text.Contains($"Archive cleanup: 1 file(s) in {box.Archive} older than 10/14/2026 10:00:00 AM; removing 1 this run, 0 left for the next launch"),
                "INSPECT's log line names the shifted cutoff");
        }

        // ---------- A-015-23 / A-019-22 Gates (owner 2026-10-20): Archive cleanup and job-folder purge run; the log prune waits ----------
        {
            var fresh = new HousekeepingSettings();
            Check("A-015-23", HousekeepingGates.ArchiveCleanup(devBuild: false, devHousekeeping: false) &&
                              !HousekeepingGates.ArchiveCleanup(devBuild: true, devHousekeeping: false) &&
                              HousekeepingGates.ArchiveCleanup(devBuild: true, devHousekeeping: true),
                "installed RED cleans the Archive with no supervised check; a dev build only with RED_DEV_HOUSEKEEPING=1");
            Check("A-019-22", HousekeepingGates.JobFolderPurge(devBuild: false, devHousekeeping: false) &&
                              !HousekeepingGates.JobFolderPurge(devBuild: true, devHousekeeping: false) &&
                              HousekeepingGates.JobFolderPurge(devBuild: true, devHousekeeping: true) && fresh.PurgeJobFolders,
                "installed RED purges job folders with no supervised check (its own switch, on by default); a dev build only with RED_DEV_HOUSEKEEPING=1");
            Check("A-019-22", !HousekeepingGates.DropboxLogPrune(fresh, devHousekeeping: false) &&
                              HousekeepingGates.DropboxLogPrune(new HousekeepingSettings { LiveCleanupVerified = true }, devHousekeeping: false) &&
                              HousekeepingGates.DropboxLogPrune(fresh, devHousekeeping: true),
                "the Dropbox log prune (no owner decision) still waits for LiveCleanupVerified");
        }

        // ---------- A-015-24 Archive retention edited in RED is INSPECT's value too (owner 2026-10-20) ----------
        using (var box = new Box(retention: null))
        {
            string config = Path.Combine(box.IF, "config.json");
            string original = "{\r\n  \"UserType\": \"Inspector\",\r\n  \"ArchiveRetention\": 5,\r\n  \"DropboxLogRetention\": 10,\r\n  \"Signature\": \"abc\"\r\n}";
            File.WriteAllBytes(config, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(original)).ToArray());
            string backups = Path.Combine(box.Root, "RedAppData");
            string? ok = InspectLiveSettings.WriteArchiveRetention(box.IF, 7, backups);
            byte[] after = File.ReadAllBytes(config);
            string afterText = Encoding.UTF8.GetString(after, 3, after.Length - 3);
            bool onlyNumber = afterText == original.Replace("\"ArchiveRetention\": 5", "\"ArchiveRetention\": 7") && after[0] == 0xEF;
            var saved = Directory.Exists(Path.Combine(backups, "Backups", "inspect-config")) ? Directory.GetFiles(Path.Combine(backups, "Backups", "inspect-config")) : Array.Empty<string>();
            Check("A-015-24", ok == null && onlyNumber && InspectLiveSettings.ReadArchiveRetention(box.IF) == 7 &&
                              saved.Length == 1 && File.ReadAllText(saved[0]).Contains("\"ArchiveRetention\": 5"),
                "Save writes 7 into INSPECT's config.json: only the number changes (BOM, layout and other keys kept), RED reads 7, and the old file is backed up in RED's app data");
            string before = File.ReadAllText(config);
            bool refused = InspectLiveSettings.WriteArchiveRetention(box.IF, 0, backups) != null &&
                           InspectLiveSettings.WriteArchiveRetention(box.IF, 4000, backups) != null && File.ReadAllText(config) == before;
            File.WriteAllText(config, "{\"Signature\":\"abc\"}");
            bool inserted = InspectLiveSettings.WriteArchiveRetention(box.IF, 12, backups) == null && InspectLiveSettings.ReadArchiveRetention(box.IF) == 12 &&
                            File.ReadAllText(config).Contains("\"Signature\":\"abc\"");
            File.WriteAllText(config, "{\"ArchiveRetention\":\"9\"}");
            bool quoted = InspectLiveSettings.WriteArchiveRetention(box.IF, 11, backups) == null && File.ReadAllText(config) == "{\"ArchiveRetention\":\"11\"}";
            File.Delete(config);
            bool missing = InspectLiveSettings.WriteArchiveRetention(box.IF, 6, backups) != null && !File.Exists(config);
            Check("A-015-24", refused && inserted && quoted && missing,
                "0 and 4000 are refused with nothing written; a file without the key gets it; a quoted value stays quoted; with no config.json nothing is created");
        }

        // ---------- Live-risk gate and settings safety ----------
        using (var box = new Box())
        {
            Check("GATE", !new HousekeepingSettings().LiveCleanupVerified, "the Dropbox log prune waits for the owner: LiveCleanupVerified is off by default");
            File.WriteAllText(Path.Combine(box.Root, "housekeeping.json"), "{ not json");
            var broken = HousekeepingSettings.Load(box.Root);
            Check("GATE", broken.ReadFailed && !broken.CleanArchive && !broken.PurgeJobFolders && !broken.LiveCleanupVerified,
                "an unreadable housekeeping.json turns cleanup and purge off for that launch");
            File.WriteAllText(Path.Combine(box.Root, "housekeeping.json"), "{\"CleanArchive\":false,\"LiveCleanupVerified\":true}");
            var saved = HousekeepingSettings.Load(box.Root);
            Check("GATE", !saved.ReadFailed && !saved.CleanArchive && saved.PurgeJobFolders && saved.LiveCleanupVerified, "a readable file is used as saved");
        }
        using (var box = new Box(archive: false))
        {
            string target = Path.Combine(box.Root, "ElsewhereArchive");
            Directory.CreateDirectory(target);
            string old = Path.Combine(target, "old.ins");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTime(old, new DateTime(2026, 10, 1));
            var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{box.Archive}\" \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            mk!.WaitForExit(10000);
            if (mk.ExitCode == 0 && Directory.Exists(box.Archive))
            {
                var result = box.Run();
                Check("SAFE", result.Declined && File.Exists(old) && box.RedOnly.Any(l => l.Contains("is a link to another folder")),
                    "an Archive that is a junction is declined: nothing deleted through the link");
                Directory.Delete(box.Archive); // removes the junction only
            }
            else Console.WriteLine("SKIP SAFE could not create a junction in the sandbox (not counted)");
        }

        Console.WriteLine();
        Console.WriteLine($"F-015 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static bool Icacls(string args)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            p!.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
