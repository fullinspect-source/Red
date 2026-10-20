// F-019 Remove from device, job-folder purge and logs harness (program/features/F-019-remove-housekeeping/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Sandbox folders under %TEMP% only: <SBX> (Inspections), <SBX-LOCAL> (stand-in for %LOCALAPPDATA%\Strand\Inspect),
// <RED-LOCAL> (RED's own log folder). Today = 2026-10-17 21:25:56 local unless stated. Synthetic data only.
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-f019-" + Guid.NewGuid().ToString("N"));
        public string IF => Path.Combine(Root, "Inspections");
        public string Local => Path.Combine(Root, "StrandInspect");
        public string RedLocal => Path.Combine(Root, "RedLogs");
        public string Jobs => Path.Combine(IF, "Jobs");
        public InspectLogWriter Log { get; }
        public Box(Func<DateTime>? clock = null)
        {
            foreach (var f in new[] { "MyList", "Outbox", "Archive", "Review", "Jobs" }) Directory.CreateDirectory(Path.Combine(IF, f));
            Directory.CreateDirectory(Local);
            Directory.CreateDirectory(RedLocal);
            Log = new InspectLogWriter(Local, Path.Combine(RedLocal, "inspect-pending"), clock ?? (() => Now));
        }
        public string Ins(string folder, string name)
        {
            string path = Path.Combine(IF, folder, name + ".ins");
            File.WriteAllText(path, "{}");
            return path;
        }
        public string Job(string name, int files = 1)
        {
            string path = Path.Combine(Jobs, name, "Engineering");
            Directory.CreateDirectory(path);
            for (int i = 0; i < files; i++) File.WriteAllText(Path.Combine(path, $"plan{i}.pdf"), "x");
            return Path.Combine(Jobs, name);
        }
        public string[] Lines(DateTime? day = null)
        {
            string file = Log.FileFor(day ?? Now);
            return File.Exists(file) ? File.ReadAllText(file).Split("\r\n", StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        }
        public static string Message(string line) => line.Contains(" | ") ? line.Substring(line.IndexOf(" | ", line.IndexOf(" | ") + 3) + 3) : line;
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private static bool Junction(string link, string target)
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        p!.WaitForExit(10000);
        return p.ExitCode == 0 && Directory.Exists(link);
    }

    public static int Run()
    {
        var on = new HousekeepingSettings { PurgeJobFolders = true };

        // ---------- A-019-01 / 02 Remove from device ----------
        using (var box = new Box())
        {
            string ins = box.Ins("MyList", "1234567-CPP-1-PQE");
            string job = box.Job("1234567");
            Check("A-019-01", RemoveFromDeviceService.Remove(ins, box.IF, _ => { }) && !File.Exists(ins) && Directory.Exists(job) &&
                              RemoveFromDeviceService.Question(1).StartsWith("Are you sure you want to remove this service from your device?") &&
                              RemoveFromDeviceService.Question(1).EndsWith("This removes it from every device that shares this Dropbox folder."),
                "one .ins removed with INSPECT's question and the every-device sentence; Jobs\\1234567 still exists");
            Check("A-019-02", RemoveFromDeviceService.Question(2).StartsWith("Are you sure you want to remove these services from your device?"),
                "two rows: INSPECT's plural question");
            string outside = Path.Combine(box.Root, "elsewhere.ins");
            File.WriteAllText(outside, "{}");
            string jobFile = Path.Combine(job, "Engineering", "plan0.pdf");
            Check("SAFE", !RemoveFromDeviceService.Remove(outside, box.IF, _ => { }) && File.Exists(outside) &&
                          !RemoveFromDeviceService.Remove(jobFile, box.IF, _ => { }) && File.Exists(jobFile),
                "Remove deletes only a top-level .ins of My List, Outbox, Archive or Review");
        }

        // ---------- A-019-10 Purge on by default, exact lines ----------
        using (var box = new Box())
        {
            box.Ins("MyList", "2222222-CPP-1-PQE");
            string live = box.Job("2222222");
            string orphan = box.Job("1111111", files: 3);
            var result = JobFolderPurgeService.Run(box.IF, box.Log, settings: new HousekeepingSettings());
            var messages = box.Lines().Select(Box.Message).ToArray();
            Check("A-019-10", new HousekeepingSettings().PurgeJobFolders && !Directory.Exists(orphan) && Directory.Exists(live) &&
                              messages.SequenceEqual(new[]
                              {
                                  "Purging Job Files",
                                  "Purge: 1 of 2 job folder(s) orphaned; removing 1 this run, 0 left for the next launch",
                                  "Deleted job folder 1111111: 3 file(s) removed, 0 failed",
                                  "Purge: removed 1 job folder(s), 0 failed, 0 left for the next launch"
                              }) && box.Lines().All(l => l.Contains(" | Info | ")),
                "1111111 deleted, 2222222 kept; INSPECT's four Info lines in order");
        }

        // ---------- A-019-04 Archive does not keep a job folder (owner 2026-10-20, INSPECT's rule; was the Q-26 default) ----------
        using (var box = new Box())
        {
            box.Ins("Archive", "7654321-ME-1-AB");
            box.Ins("MyList", "2222222-CPP-1-PQE");
            box.Ins("Outbox", "3333333-FRM-1-PQE");
            box.Ins("Review", "4444444-FRM-1-PQE");
            string archivedOnly = box.Job("7654321", files: 2);
            string myList = box.Job("2222222"), outbox = box.Job("3333333"), review = box.Job("4444444");
            var result = JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
            Check("A-019-04", !Directory.Exists(archivedOnly) && Directory.Exists(myList) && Directory.Exists(outbox) && Directory.Exists(review) &&
                              result.Removed == 1 && File.Exists(Path.Combine(box.IF, "Archive", "7654321-ME-1-AB.ins")) &&
                              JobFolderPurgeService.LiveFolders.SequenceEqual(new[] { InspectFolder.MyList, InspectFolder.Outbox, InspectFolder.Review }),
                "a job used only by an Archive inspection is purged (the Archive .ins stays); My List, Outbox and Review keep theirs");
        }
        using (var box = new Box())
        {
            box.Ins("Archive", "7654321-ME-1-AB");
            string job = box.Job("7654321");
            var result = JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
            Check("A-019-04", result.Declined && Directory.Exists(job),
                "with only Archive inspections there are no live ones: the purge declines and deletes nothing (P2)");
        }

        // ---------- A-019-05 Purge declines with no live inspections ----------
        using (var box = new Box())
        {
            string job = box.Job("1111111");
            var result = JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
            Check("A-019-05", result.Declined && Directory.Exists(job) &&
                              box.Lines().Any(l => l.Contains(" | Warn | Purge declined: no inspections found in ") && l.EndsWith(", so no job folder can be shown to be orphaned")),
                "nothing deleted and the Warn \"Purge declined: no inspections found in …\" line");
        }

        // ---------- A-019-06 Purge refuses links and caps at 20 ----------
        using (var box = new Box())
        {
            box.Ins("MyList", "0000000-CPP-1-PQE");
            for (int i = 1; i <= 24; i++) box.Job((1000000 + i).ToString());
            string target = Path.Combine(box.Root, "LinkTarget");
            Directory.CreateDirectory(target);
            string precious = Path.Combine(target, "keep.pdf");
            File.WriteAllText(precious, "do not delete");
            string link = Path.Combine(box.Jobs, "9999999");
            if (Junction(link, target))
            {
                var first = JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
                int realLeft = Directory.GetDirectories(box.Jobs).Count(d => !d.EndsWith("9999999"));
                Check("A-019-06", first.Removed == 20 && realLeft == 4 && Directory.Exists(link) && File.Exists(precious) &&
                                  box.Lines().Any(l => l.EndsWith("Purge: 25 of 25 job folder(s) orphaned; removing 20 this run, 5 left for the next launch")),
                    "20 real folders deleted, the junction remains, the log says \"removing 20 this run\"");
                var second = JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
                Check("A-019-06", second.Removed == 4 && second.Failed == 1 && Directory.Exists(link) && File.Exists(precious) &&
                                  box.Lines().Any(l => l.Contains("| Warn | Job folder 9999999 left in place: it is a link rather than a real folder")),
                    "the next run refuses the junction (nothing deleted through it) and removes the last 4");
                Directory.Delete(link); // the junction only
            }
            else Console.WriteLine("SKIP A-019-06 could not create a junction in the sandbox (not counted)");
        }

        // ---------- P6 a link inside a job folder ----------
        using (var box = new Box())
        {
            box.Ins("MyList", "0000000-CPP-1-PQE");
            string job = box.Job("1111111");
            string target = Path.Combine(box.Root, "Inner");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "keep.pdf"), "x");
            if (Junction(Path.Combine(job, "Engineering", "linked"), target))
            {
                JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
                Check("P6", Directory.Exists(job) && File.Exists(Path.Combine(target, "keep.pdf")) && File.Exists(Path.Combine(job, "Engineering", "plan0.pdf")) &&
                            box.Lines().Any(l => l.Contains("| Warn | Job folder 1111111 left in place: ") && l.EndsWith("is a link to somewhere else, not a folder")),
                    "a link at any depth leaves the whole job folder in place, nothing deleted");
                Directory.Delete(Path.Combine(job, "Engineering", "linked"));
            }
            else Console.WriteLine("SKIP P6 could not create a junction (not counted)");
        }

        // ---------- A-019-07 Dry run ----------
        using (var box = new Box())
        {
            box.Ins("MyList", "2222222-CPP-1-PQE");
            string orphan = box.Job("1111111");
            var result = JobFolderPurgeService.Run(box.IF, box.Log, dryRun: true, settings: new HousekeepingSettings { PurgeJobFolders = false });
            Check("A-019-07", result.Candidates.SequenceEqual(new[] { "1111111" }) && Directory.Exists(orphan) && box.Lines().Length == 0,
                "the dry run lists 1111111 and changes nothing, writes nothing (sandbox; the live dry run is the owner's)");
            var off = JobFolderPurgeService.Run(box.IF, box.Log, settings: new HousekeepingSettings { PurgeJobFolders = false });
            Check("N3", off.Disabled && Directory.Exists(orphan) && box.Lines().Length == 0, "the switch off: nothing deleted, no line");
        }

        // ---------- A-019-08 RED log retention ----------
        using (var box = new Box())
        {
            string old = Path.Combine(box.RedLocal, "red-2026-09-07.log"), recent = Path.Combine(box.RedLocal, "red-2026-10-07.log");
            string queue = Path.Combine(box.RedLocal, "inspect-pending"); Directory.CreateDirectory(queue);
            string pending = Path.Combine(queue, "pending-2026-09-01.log"), inspect = Path.Combine(box.Local, "2026-09-01.log");
            foreach (var (path, when) in new[] { (old, Now.AddDays(-40)), (recent, Now.AddDays(-10)), (pending, Now.AddDays(-46)), (inspect, Now.AddDays(-46)) })
            {
                File.WriteAllText(path, "x");
                File.SetLastWriteTime(path, when);
            }
            var prune = typeof(SurgicalSaveService).Assembly.GetType("InspectionEditor.Services.DiagnosticLogService")!.GetMethod("PruneOld", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            prune.Invoke(null, new object?[] { Now, box.RedLocal });
            Check("A-019-08", !File.Exists(old) && File.Exists(recent) && File.Exists(pending) && File.Exists(inspect),
                "the 40-day RED-only file goes; the 10-day one, the pending queue and INSPECT's local log stay");
        }

        // ---------- A-019-11 Exact line format ----------
        using (var box = new Box(() => new DateTime(2026, 10, 17, 10, 8, 55, 720).AddTicks(6000)))
        {
            var header = new JObject
            {
                ["InspectionNumber"] = "1234567-CPP-1-PQE", ["Address"] = "100 Example St", ["WorkOrderId"] = "00000000-0000-4000-8000-000000000001",
                ["Version"] = 0, ["LastDBDate"] = null
            };
            var model = new InspectionFile { Sections = new List<Section> { new() { Items = new List<Item>
            {
                new() { Pictures = new List<Picture> { new(), new() } }, new()
            } } } };
            InspectFormatLog.Writer = box.Log;
            InspectFormatLog.Opening(k => header[k], model);
            string text = File.ReadAllText(box.Log.FileFor(new DateTime(2026, 10, 17)));
            string expected =
                "2026-10-17 10:08:55.7206 | Info | Opening inspection 1234567-CPP-1-PQE\r\n" +
                "2026-10-17 10:08:55.7206 | Info |    Number 1234567-CPP-1-PQE\r\n" +
                "2026-10-17 10:08:55.7206 | Info |    Address 100 Example St\r\n" +
                "2026-10-17 10:08:55.7206 | Info |    Work Order Id 00000000-0000-4000-8000-000000000001\r\n" +
                "2026-10-17 10:08:55.7206 | Info |    Version 0\r\n" +
                "2026-10-17 10:08:55.7206 | Info |    Status Not Submitted\r\n" +
                "2026-10-17 10:08:55.7206 | Info |    Picture Count 2\r\n";
            byte[] raw = File.ReadAllBytes(box.Log.FileFor(new DateTime(2026, 10, 17)));
            Check("A-019-11", text == expected && raw[0] != 0xEF, "Opening and its detail block, character for character, CRLF, no BOM");
            header["LastDBDate"] = "2026-10-16T12:00:00-05:00";
            InspectFormatLog.Saving(k => header[k], model, @"C:\SBX\MyList\1234567-CPP-1-PQE.ins", written: true);
            InspectFormatLog.Closing("1234567-CPP-1-PQE");
            var messages = box.Lines(new DateTime(2026, 10, 17)).Select(Box.Message).ToArray();
            Check("L7", messages.Contains("Saving inspection 1234567-CPP-1-PQE") && messages.Contains("   Status Submitted") &&
                        messages.Contains(@"Creating file: C:\SBX\MyList\1234567-CPP-1-PQE.ins") && messages.Contains(@"File created: C:\SBX\MyList\1234567-CPP-1-PQE.ins") &&
                        messages.Last() == "Closing Inspection 1234567-CPP-1-PQE",
                "save lines with Creating/File created, Status Submitted once LastDBDate is set, Closing Inspection");
        }

        // ---------- A-019-12 Day rollover ----------
        {
            var times = new Queue<DateTime>(new[] { new DateTime(2026, 10, 17, 23, 59, 59, 999), new DateTime(2026, 10, 18, 0, 0, 0, 10) });
            using var box = new Box(() => times.Dequeue());
            box.Log.Info("before midnight");
            box.Log.Info("after midnight");
            Check("A-019-12", box.Lines(new DateTime(2026, 10, 17)).Last().StartsWith("2026-10-17 23:59:59.9990 | Info | before midnight") &&
                              box.Lines(new DateTime(2026, 10, 18)).First().StartsWith("2026-10-18 00:00:00.0100 | Info | after midnight"),
                "each entry goes to the file of its own date");
        }

        // ---------- A-019-13 File held by INSPECT 2026 ----------
        using (var box = new Box())
        {
            string day = box.Log.FileFor(Now);
            File.WriteAllText(day, "2026-10-17 08:00:00.0000 | Info | INSPECT's own line\r\n");
            byte[] before = File.ReadAllBytes(day);
            using (new FileStream(day, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete)) // as NLog 5
                box.Log.Info("first");
            bool unchanged = File.ReadAllBytes(day).AsSpan().SequenceEqual(before) && Directory.GetFiles(Path.Combine(box.RedLocal, "inspect-pending")).Length == 1;
            box.Log.Info("second");
            string text = File.ReadAllText(day);
            Check("A-019-13", unchanged && text.StartsWith("2026-10-17 08:00:00.0000 | Info | INSPECT's own line\r\n") &&
                              text.IndexOf("| first", StringComparison.Ordinal) < text.IndexOf("| second", StringComparison.Ordinal) &&
                              text.IndexOf("| first", StringComparison.Ordinal) > 0,
                "while held: no error, file unchanged, entry queued; then both appended in order after the existing bytes");
        }

        // ---------- A-019-14 / 15 / 19 / 16 Copy and prune ----------
        using (var box = new Box())
        {
            string logs = Path.Combine(box.IF, "Logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(box.IF, "config.json"), "{\"DropboxLogRetention\":10}");
            void LocalFile(string name, DateTime when) { string p = Path.Combine(box.Local, name); File.WriteAllText(p, name + "\r\n"); File.SetLastWriteTime(p, when); }
            void LogsFile(string name, DateTime when, string content) { string p = Path.Combine(logs, name); File.WriteAllText(p, content); File.SetLastWriteTime(p, when); }
            LocalFile("2026-10-06.log", new DateTime(2026, 10, 6, 12, 0, 0));
            LocalFile("2026-10-07.log", new DateTime(2026, 10, 7, 16, 41, 0));
            LocalFile("2026-10-16.log", new DateTime(2026, 10, 16, 16, 27, 0));
            LocalFile("2026-10-17.log", DateTime.Now);
            LogsFile("2026-10-16.log", new DateTime(2026, 10, 16, 18, 43, 0), "other device");
            LogsFile("2026-10-17.log", new DateTime(2026, 10, 17, 9, 0, 0), "older");
            LogsFile("2026-10-08.log", new DateTime(2026, 10, 8, 0, 1, 0), "keep");
            LogsFile("2026-09-30.log", new DateTime(2026, 10, 1, 8, 0, 0), "other device, old");
            InspectLogCopyService.CopyToDropbox(box.IF, box.Log, Now);
            string localToday = File.ReadAllText(Path.Combine(box.Local, "2026-10-17.log"));
            string copiedToday = File.ReadAllText(Path.Combine(logs, "2026-10-17.log"));
            Check("A-019-14", !File.Exists(Path.Combine(logs, "2026-10-06.log")) && File.ReadAllText(Path.Combine(logs, "2026-10-16.log")) == "other device" &&
                              copiedToday.TrimEnd().EndsWith("| Info | Copying Inspect log to Dropbox") &&
                              localToday.TrimEnd().EndsWith("| Info |    Log successfully copied to Dropbox") && localToday.StartsWith(copiedToday) &&
                              File.Exists(Path.Combine(logs, "2026-10-07.log")),
                "10-06 not copied; 10-16 keeps the other device's newer copy; today's copy ends with \"Copying Inspect log to Dropbox\"");
            int pruned = InspectLogCopyService.PruneDropbox(box.IF, box.Log, Now);
            Check("A-019-15", !File.Exists(Path.Combine(logs, "2026-10-07.log")) && !File.Exists(Path.Combine(logs, "2026-09-30.log")) &&
                              File.Exists(Path.Combine(logs, "2026-10-08.log")) && pruned == 2,
                "after the prune: 10-07 (copied, then on or before the cutoff) and 09-30 gone; 10-08 stays");
            File.WriteAllText(Path.Combine(box.IF, "config.json"), "{}");
            LogsFile("2026-10-07.log", new DateTime(2026, 10, 7, 16, 41, 0), "again");
            InspectLogCopyService.PruneDropbox(box.IF, box.Log, Now);
            bool goneWith10 = !File.Exists(Path.Combine(logs, "2026-10-07.log"));
            File.WriteAllText(Path.Combine(box.IF, "config.json"), "{\"DropboxLogRetention\":20}");
            LogsFile("2026-10-07.log", new DateTime(2026, 10, 7, 16, 41, 0), "again");
            InspectLogCopyService.PruneDropbox(box.IF, box.Log, Now);
            Check("A-019-19", goneWith10 && File.Exists(Path.Combine(logs, "2026-10-07.log")), "no key: N = 10 and the 10/7 file goes; 20: it stays");
            LogsFile("2026-10-01.log", new DateTime(2026, 10, 1), "old");
            InspectLogCopyService.CopyToDropbox(box.IF, box.Log, Now); // an editor open: copy only
            Check("A-019-16", File.Exists(Path.Combine(logs, "2026-10-01.log")) && box.Lines().Count(l => l.EndsWith("Copying Inspect log to Dropbox")) == 2,
                "the copy at editor open writes its lines and leaves the old Dropbox file (no prune)");
        }

        // ---------- A-019-20 Copy failure ----------
        using (var box = new Box())
        {
            string logs = Path.Combine(box.IF, "Logs");
            Directory.CreateDirectory(logs);
            string locked = Path.Combine(logs, "2026-10-17.log");
            File.WriteAllText(locked, "old");
            File.SetLastWriteTime(locked, new DateTime(2026, 10, 17, 1, 0, 0));
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            string old = Path.Combine(logs, "2026-09-01.log");
            File.WriteAllText(old, "x"); File.SetLastWriteTime(old, new DateTime(2026, 9, 1));
            box.Log.Info("today");
            InspectLogCopyService.CopyToDropbox(box.IF, box.Log, Now);
            int pruned = InspectLogCopyService.PruneDropbox(box.IF, box.Log, Now);
            string text = File.ReadAllText(box.Log.FileFor(Now));
            Check("A-019-20", Regex.IsMatch(text, @"\| Warn \|    Could not copy log to Dropbox\r\nSystem\.") && !File.Exists(old) && pruned == 1,
                "a read-only target: the Warn line with the exception text, and the prune still runs");
            File.SetAttributes(locked, FileAttributes.Normal);
        }

        // ---------- A-019-18 Launch and exit banners ----------
        using (var box = new Box())
        {
            InspectFormatLog.Writer = box.Log;
            InspectFormatLog.Launch("RED", "2.1.42");
            InspectFormatLog.Exit("RED");
            var messages = box.Lines().Select(Box.Message).ToArray();
            Check("A-019-18", messages.SequenceEqual(new[] { InspectFormatLog.Rule, "Starting RED version 2.1.42", InspectFormatLog.Rule, "Exiting RED" }) &&
                              InspectFormatLog.Rule == "------------------------------------" &&
                              !messages.Any(m => m.Contains("Inspect 2026") || m.Contains("Inspect 2022")),
                "launch banner and exit line with RED's own product name and version");
        }

        // ---------- A-019-09 INSPECT files untouched ----------
        using (var box = new Box())
        {
            string temp = Path.Combine(box.IF, "Temp", "2026-10-17"); Directory.CreateDirectory(temp);
            string autosave = Path.Combine(temp, "1234567-CPP-1-PQE.ins"); File.WriteAllText(autosave, "{\"autosave\":1}");
            File.SetLastWriteTime(autosave, new DateTime(2026, 9, 1));
            string strand = Path.Combine(box.Root, "TempStrand"); Directory.CreateDirectory(strand);
            string strandFile = Path.Combine(strand, "x.tmp"); File.WriteAllText(strandFile, "keep");
            string day = box.Log.FileFor(Now);
            File.WriteAllText(day, "2026-10-17 08:00:00.0000 | Info | INSPECT's own line\r\n");
            byte[] inspectBytes = File.ReadAllBytes(day);
            box.Ins("MyList", "2222222-CPP-1-PQE");
            box.Job("1111111");
            JobFolderPurgeService.Run(box.IF, box.Log, settings: on);
            InspectLogCopyService.CopyToDropbox(box.IF, box.Log, Now);
            InspectLogCopyService.PruneDropbox(box.IF, box.Log, Now);
            Check("A-019-09", File.ReadAllText(autosave) == "{\"autosave\":1}" && File.ReadAllText(strandFile) == "keep" &&
                              File.ReadAllBytes(day).AsSpan(0, inspectBytes.Length).SequenceEqual(inspectBytes),
                "Temp and a %TEMP%\\Strand stand-in unchanged; INSPECT's existing bytes in the day file unchanged (RED only appended)");
        }

        Console.WriteLine();
        Console.WriteLine($"F-019 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
