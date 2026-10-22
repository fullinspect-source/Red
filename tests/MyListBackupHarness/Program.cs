// Storage audit F2 harness: old save backups of reports that are no longer in My List are deleted (INSPECT's Archive
// retention + 1 day; 30 days when it is 0 or less), never a backup of a report still in My List, never anything in
// Review or outside the top level of My List. Sandbox folders under %TEMP% only; synthetic files only.
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

    private sealed class Box : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-storage-mylist-" + Guid.NewGuid().ToString("N"));
        public string IF => Path.Combine(Root, "Inspections");
        public string MyList => Path.Combine(IF, "MyList");
        public Box(bool withMyList = true)
        {
            foreach (var f in new[] { "Outbox", "Archive", "Review" }) Directory.CreateDirectory(Path.Combine(IF, f));
            if (withMyList) Directory.CreateDirectory(MyList);
        }
        public string File(string folder, string name, DateTime written, int size = 64)
        {
            string path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, new byte[size]);
            System.IO.File.SetLastWriteTime(path, written);
            return path;
        }
        public string Backup(string report, DateTime written, string? folder = null) =>
            File(folder ?? MyList, report + ".red-save-backup-" + Guid.NewGuid().ToString("N") + ".completed", written, 1000);
        public void Dispose()
        {
            // Audit S-28: the sandbox always goes; links are removed first so nothing outside it is touched.
            try
            {
                foreach (var link in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
                             .Where(d => (System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
                    Directory.Delete(link);
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Console.WriteLine("note: sandbox not fully deleted"); }
        }
    }

    public static int Run(string[] args)
    {
        try
        {
            LiveAndOrphans();
            Boundaries();
            OtherNamesAndFolders();
            CapAndLocks();
            LinksAndGates();
            SourceChecks(args.Length > 0 ? args[0] : FindRepoRoot());
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString());
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // A backup of a report still in My List is never touched, however old; an orphan is, once old enough.
    private static void LiveAndOrphans()
    {
        using var box = new Box();
        box.File(box.MyList, "1111111-CPP-1-PQE.ins", Now.AddDays(-200));
        var live = Enumerable.Range(0, 3).Select(i => box.Backup("1111111-CPP-1-PQE.ins", Now.AddDays(-150 - i))).ToList();
        box.File(box.MyList, "2222222-bwt-1-abc.ins", Now.AddDays(-1));
        string liveOtherCase = box.Backup("2222222-BWT-1-ABC.ins", Now.AddDays(-120));
        var orphans = Enumerable.Range(0, 3).Select(i => box.Backup("3333333-CPP-2-PQE.ins", Now.AddDays(-50 - i))).ToList();
        string young = box.Backup("4444444-CPP-1-PQE.ins", Now.AddDays(-2));

        var dry = MyListBackupCleanup.Run(box.IF, Now, days: 40, dryRun: true);
        Check("F2-dry", dry.Candidates == 3 && dry.KeptLive == 4 && dry.KeptRecent == 1 && orphans.All(System.IO.File.Exists),
            "a dry run counts (3 orphans past the cutoff, 4 live backups, 1 recent) and deletes nothing");

        var result = MyListBackupCleanup.Run(box.IF, Now, days: 40);
        Check("F2-live", live.All(System.IO.File.Exists) && System.IO.File.Exists(liveOtherCase) && result.KeptLive == 4,
            "backups of reports still in My List are never touched (150 days old, and with the name in other letter case)");
        Check("F2-orphan", orphans.All(p => !System.IO.File.Exists(p)) && result.Removed.Files == 3 && result.Removed.Bytes == 3000,
            "the backups of a report no longer in My List, older than Archive retention + 1 day, are deleted (counts and bytes)");
        Check("F2-recent", System.IO.File.Exists(young) && result.KeptRecent == 1,
            "an orphan backup newer than the cutoff is kept (the report may come back)");
        string line = MyListBackupCleanup.Describe(result, "at start");
        Check("F2-log", line.Contains("deleted 3 file(s)") && line.Contains("2026-09-11") && !line.Contains("3333333") && !line.Contains("1111111"),
            $"the log line has counts, sizes and the cutoff, never a report name ({line})");
    }

    // The same calendar-day rule as the Archive cleanup: N = 5 on 10/22 deletes what was last changed on 10/16 or before.
    private static void Boundaries()
    {
        using (var box = new Box())
        {
            string onCutoff = box.Backup("5555555-CPP-1-PQE.ins", new DateTime(2026, 10, 16, 23, 59, 0));
            string dayAfter = box.Backup("5555555-CPP-1-PQE.ins", new DateTime(2026, 10, 17, 0, 1, 0));
            MyListBackupCleanup.Run(box.IF, Now, days: 5);
            Check("F2-boundary", !System.IO.File.Exists(onCutoff) && System.IO.File.Exists(dayAfter),
                "N = 5: a backup changed on 10/16 (the cutoff day, today - 6) goes; one changed on 10/17 stays");
        }
        using (var box = new Box())
        {
            string d30 = box.Backup("6666666-CPP-1-PQE.ins", new DateTime(2026, 9, 22, 10, 0, 0));
            string d29 = box.Backup("6666666-CPP-1-PQE.ins", new DateTime(2026, 9, 23, 10, 0, 0));
            var zero = MyListBackupCleanup.Run(box.IF, Now, days: 0);
            Check("F2-zero", !System.IO.File.Exists(d30) && System.IO.File.Exists(d29) && zero.Cutoff.Date == new DateTime(2026, 9, 22),
                "Archive retention 0 or less: 30 days (9/22 goes, 9/23 stays), never kept forever");
            string old = box.Backup("6666666-CPP-1-PQE.ins", Now.AddDays(-400));
            MyListBackupCleanup.Run(box.IF, Now, days: 3650);
            Check("F2-long", System.IO.File.Exists(old), "Archive retention 3650: a 400-day-old orphan is still kept (INSPECT's own setting rules)");
        }
        using (var box = new Box())
        {
            System.IO.File.WriteAllText(Path.Combine(box.IF, "config.json"), "{\"ArchiveRetention\":10}");
            string d12 = box.Backup("7777777-CPP-1-PQE.ins", Now.AddDays(-12));
            string d10 = box.Backup("7777777-CPP-1-PQE.ins", Now.AddDays(-10));
            var read = MyListBackupCleanup.Run(box.IF, Now);
            Check("F2-config", read.Days == 10 && !System.IO.File.Exists(d12) && System.IO.File.Exists(d10),
                "the retention is read from INSPECT's config.json in the Inspections folder (10 days: 12 days old goes, 10 stays)");
        }
    }

    // Only RED's exact backup name at the top of My List: never Review, Archive, Outbox, staging or subfolders.
    private static void OtherNamesAndFolders()
    {
        using var box = new Box();
        DateTime old = Now.AddDays(-300);
        var untouched = new List<string>
        {
            box.File(box.MyList, "8888888-CPP-1-PQE.ins.red-save-backup-nothex.completed", old),
            box.File(box.MyList, "8888888-CPP-1-PQE.ins.red-save-backup-" + Guid.NewGuid().ToString("N"), old),      // unmarked: recovery evidence
            box.File(box.MyList, "8888888-CPP-1-PQE.ins.red-save-backup", old),                                     // legacy name
            box.File(box.MyList, "8888888-CPP-1-PQE.ins.red-" + Guid.NewGuid().ToString("N") + ".tmp", old),
            box.File(box.MyList, "notes.txt", old),
            box.Backup("8888888-CPP-1-PQE.ins", old, Path.Combine(box.MyList, "Subfolder")),
            box.Backup("8888888-CPP-1-PQE.ins", old, Path.Combine(box.MyList, ".red-save-staging", "Review")),
            box.Backup("8888888-CPP-1-PQE.ins", old, Path.Combine(box.MyList, ".red-save-staging", "Archive")),
            box.Backup("8888888-CPP-1-PQE.ins", old, Path.Combine(box.IF, "Review")),
            box.Backup("8888888-CPP-1-PQE.ins", old, Path.Combine(box.IF, "Archive")),
            box.Backup("8888888-CPP-1-PQE.ins", old, Path.Combine(box.IF, "Outbox")),
            box.File(Path.Combine(box.IF, "Review"), "9999999-CPP-1-PQE.ins", old),
            box.File(Path.Combine(box.IF, "Archive"), "9999999-CPP-1-PQE.ins", old),
        };
        var result = MyListBackupCleanup.Run(box.IF, Now, days: 1);
        Check("F2-review", untouched.All(System.IO.File.Exists) && result.Removed.Files == 0,
            "nothing in Review, Archive, Outbox, .red-save-staging or a My List subfolder is touched, nor unmarked, legacy or other names");
    }

    // At most 200 per run, oldest first; a backup another program has open is skipped and stays.
    private static void CapAndLocks()
    {
        using var box = new Box();
        var all = Enumerable.Range(0, 205).Select(i => box.Backup($"{1000000 + i}-CPP-1-PQE.ins", Now.AddDays(-100 + i * 0.1))).ToList();
        var first = MyListBackupCleanup.Run(box.IF, Now, days: 40);
        int left = all.Count(System.IO.File.Exists);
        Check("F2-cap", first.Removed.Files == 200 && left == 5 && all.Take(200).All(p => !System.IO.File.Exists(p)),
            "at most 200 deleted per run, oldest first; the rest wait for the next run");
        string locked = all[^1];
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var second = MyListBackupCleanup.Run(box.IF, Now, days: 40);
            Check("F2-locked", System.IO.File.Exists(locked) && second.Removed.Files == 4 && second.Removed.Skipped == 1,
                "a backup another program has open is skipped and kept");
        }
        MyListBackupCleanup.Run(box.IF, Now, days: 40);
        Check("F2-locked-later", !System.IO.File.Exists(locked), "it is deleted at a later run once it is closed");
    }

    private static void LinksAndGates()
    {
        using (var box = new Box(withMyList: false))
        {
            string real = Path.Combine(box.Root, "elsewhere");
            Directory.CreateDirectory(real);
            string victim = box.Backup("1212121-CPP-1-PQE.ins", Now.AddDays(-300), real);
            Junction(box.MyList, real);
            var result = MyListBackupCleanup.Run(box.IF, Now, days: 1);
            Check("F2-link", result.Declined && System.IO.File.Exists(victim), "a My List folder that is a link to another folder is not cleaned");
            Check("F2-log-declined", MyListBackupCleanup.Describe(result, "at start").Contains("not cleaned"), "and the log says so");
        }
        using (var box = new Box(withMyList: false))
        {
            var missing = MyListBackupCleanup.Run(box.IF, Now, days: 1);
            Check("F2-missing", missing.MissingFolder && missing.Removed.Files == 0, "no My List folder: nothing to do");
        }
        var on = new HousekeepingSettings();
        var off = new HousekeepingSettings { CleanArchive = false };
        string unreadableRoot = Path.Combine(Path.GetTempPath(), "red-storage-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unreadableRoot);
        System.IO.File.WriteAllText(Path.Combine(unreadableRoot, "housekeeping.json"), "{ not json");
        var unreadable = HousekeepingSettings.Load(unreadableRoot);
        Directory.Delete(unreadableRoot, true);
        Check("F2-gate", MyListBackupCleanup.Allowed(on, true) && !MyListBackupCleanup.Allowed(on, false) &&
                         !MyListBackupCleanup.Allowed(off, true) && unreadable.ReadFailed && !MyListBackupCleanup.Allowed(unreadable, true),
            "the Archive cleanup's gates: off in a dev build without RED_DEV_HOUSEKEEPING=1, with the switch off, or with housekeeping.json unreadable");
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
        int archive = housekeeping.IndexOf("ArchiveRetentionService.Run(folder", StringComparison.Ordinal);
        int sweep = housekeeping.IndexOf("SweepMyListBackups(\"at start\", settings)", StringComparison.Ordinal);
        Check("F2-start", archive > 0 && sweep > archive && housekeeping.Contains("MyListBackupCleanup.Allowed(settings ?? HousekeepingSettings.Load(), ArchiveCleanupAllowed)"),
            "at start the sweep runs after the Archive cleanup, behind the same gate");
        string folderViews = Read("InspectionPickerWindow.FolderViews.cs");
        Check("F2-triggers", folderViews.Contains("if (removed > 0) App.SweepMyListBackupsSoon();") &&
                             folderViews.Contains("if (moved > 0) App.SweepMyListBackupsSoon();") &&
                             Read("InspectionPickerWindow.AddToMyList.cs").Contains("if (copied > 0) App.SweepMyListBackupsSoon();") &&
                             Read("InspectionPickerWindow.BuyerWalkAddToMyList.cs").Contains("App.SweepMyListBackupsSoon();") &&
                             Read("MainWindow.Submit.cs").Contains("App.SweepMyListBackupsSoon();"),
            "the sweep runs again after Remove, Move, Add or Buyer Walk replacing a My List copy, and Submit");
        string service = Read("Services", "MyListBackupCleanup.cs");
        Check("F2-only-mylist", service.Contains("InspectFolders.PathOf(inspectionsFolder, InspectFolder.MyList)") &&
                                service.Contains("SearchOption.TopDirectoryOnly") && !service.Contains("InspectFolder.Review") &&
                                !service.Contains("File.Move(") && !service.Contains("File.Copy("),
            "the sweep lists only the top level of My List and only deletes (nothing moved out of Dropbox)");
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
