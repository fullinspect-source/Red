// Storage audit F7 harness: RED's own backup photos in Camera Roll\red-camera are kept 2 x Archive retention, but never
// more than 90 days (also when the retention is 0 or less, which used to mean "forever"), and never more than 2 GB in
// total (oldest first, never one from the last day). Sandbox folders under %TEMP% only; synthetic files only.
//   dotnet run -p:Platform=x64 [RED source folder]
// Exit 0 = all passed.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using InspectionEditor.Services;
using InspectionEditor.Services.Camera;

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
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-storage-camera-" + Guid.NewGuid().ToString("N"));
        public string Roll => Path.Combine(Root, "Camera Roll");
        public string Backups => Path.Combine(Roll, "red-camera");
        public Box() { Directory.CreateDirectory(Backups); }
        public string Photo(double daysOld, int size = 100, string? name = null, string? folder = null)
        {
            DateTime when = Now.AddDays(-daysOld);
            name ??= $"RED_{when:yyyyMMdd_HHmmss_fff}.jpg";
            string path = Path.Combine(folder ?? Backups, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTime(path, when);
            return path;
        }
        public void Dispose()
        {
            try
            {
                foreach (var link in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
                             .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
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
            AgeLimits();
            AgePurge();
            SizeCap();
            NamesLinksLocks();
            SourceChecks(args.Length > 0 ? args[0] : FindRepoRoot());
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString());
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void AgeLimits()
    {
        Check("F7-limit", CameraRollCopy.AgeLimitDays(5) == 10 && CameraRollCopy.AgeLimitDays(40) == 80 && CameraRollCopy.AgeLimitDays(45) == 90 &&
                          CameraRollCopy.AgeLimitDays(46) == 90 && CameraRollCopy.AgeLimitDays(3650) == 90 && CameraRollCopy.AgeLimitDays(int.MaxValue) == 90 &&
                          CameraRollCopy.AgeLimitDays(0) == 90 && CameraRollCopy.AgeLimitDays(-3) == 90,
            "kept 2 x Archive retention, at most 90 days; 90 days when the retention is 0 or less (5→10, 40→80, 45→90, 3650→90, 0→90)");
        Check("F7-constants", CameraRollCopy.MaxAgeDays == 90 && CameraRollCopy.MaxFolderBytes == 2L * 1024 * 1024 * 1024,
            "the limits are 90 days and 2 GB");
    }

    private static void AgePurge()
    {
        using (var box = new Box())
        {
            string d79 = box.Photo(79), d81 = box.Photo(81);
            int deleted = CameraRollCopy.PurgeOld(Now, 40, box.Backups);
            Check("F7-default", deleted == 1 && File.Exists(d79) && !File.Exists(d81), "default retention 40: 79 days kept, 81 days deleted (80-day rule unchanged)");
        }
        using (var box = new Box())
        {
            string d89 = box.Photo(89), d91 = box.Photo(91);
            CameraRollCopy.PurgeOld(Now, 0, box.Backups);
            Check("F7-zero", File.Exists(d89) && !File.Exists(d91), "retention 0: no longer kept forever; 89 days kept, 91 deleted");
        }
        using (var box = new Box())
        {
            string d89 = box.Photo(89), d91 = box.Photo(91), d400 = box.Photo(400);
            var tally = CameraRollCopy.Purge(Now, 3650, box.Backups);
            Check("F7-long", File.Exists(d89) && !File.Exists(d91) && !File.Exists(d400) && tally.Files == 2 && tally.Bytes == 200,
                "retention 3650: capped at 90 days (counts and bytes reported)");
        }
    }

    private static void SizeCap()
    {
        using var box = new Box();
        // Limit 1,000 bytes for the test (the real one is 2 GB): 5 x 300 bytes, oldest goes first until 1,000 or less.
        var photos = new[] { 10.0, 8, 6, 4, 2 }.Select(d => box.Photo(d, 300)).ToList();
        var tally = CameraRollCopy.Purge(Now, 40, box.Backups, maxBytes: 1000);
        Check("F7-size", !File.Exists(photos[0]) && !File.Exists(photos[1]) && photos.Skip(2).All(File.Exists) && tally.Files == 2,
            "over the size limit the oldest copies go until it is met (1,500 → 900 bytes)");

        using var fresh = new Box();
        var today = new[] { 0.9, 0.5, 0.1 }.Select(d => fresh.Photo(d, 600)).ToList();
        string older = fresh.Photo(3, 600);
        CameraRollCopy.Purge(Now, 40, fresh.Backups, maxBytes: 1000);
        Check("F7-size-today", !File.Exists(older) && today.All(File.Exists),
            "the size limit never takes a photo from the last day (it stops there even when still over)");
    }

    private static void NamesLinksLocks()
    {
        using (var box = new Box())
        {
            var keep = new[]
            {
                box.Photo(200, name: "holiday.jpg"),
                box.Photo(200, name: "RED_notadate.jpg"),
                box.Photo(200, name: "IMG_20250101_101010.jpg"),
                box.Photo(200, folder: Path.Combine(box.Backups, "subfolder")),
                box.Photo(200, folder: box.Roll),                                   // RED Camera's originals in the camera roll
                box.Photo(200, name: ".RED_x.jpg.tmp", folder: box.Roll),
                box.Photo(0.5, name: $".RED_20261022_000000_000.jpg.{Guid.NewGuid():N}.tmp", folder: box.Roll),
            };
            string staleTemp = box.Photo(2, name: $".RED_20261020_101010_123.jpg.{Guid.NewGuid():N}.tmp", folder: box.Roll);
            string staleTemp2 = box.Photo(2, name: $".RED_20261020_101010_123_2.jpg.{Guid.NewGuid():N}.tmp", folder: box.Roll);
            string old = box.Photo(120);
            var tally = CameraRollCopy.Purge(Now, 40, box.Backups);
            Check("F7-names", keep.All(File.Exists) && !File.Exists(old),
                "only RED_yyyyMMdd_HHmmss_fff.jpg directly in red-camera: other photos, RED Camera's originals in the camera roll and subfolders stay");
            Check("F7-staging", !File.Exists(staleTemp) && !File.Exists(staleTemp2) && tally.Files == 3,
                "RED's own interrupted photo writes (.RED_*.jpg.{guid}.tmp) more than a day old are deleted from the camera roll; newer ones stay");
        }
        using (var box = new Box())
        {
            string locked = box.Photo(120);
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var tally = CameraRollCopy.Purge(Now, 40, box.Backups);
                Check("F7-locked", File.Exists(locked) && tally.Skipped == 1 && tally.Files == 0, "a photo another program has open is skipped");
            }
        }
        using (var box = new Box())
        {
            string elsewhere = Path.Combine(box.Root, "elsewhere");
            string victim = box.Photo(300, folder: elsewhere);
            string linked = Path.Combine(box.Roll, "red-camera-link");
            Junction(linked, elsewhere);
            CameraRollCopy.Purge(Now, 40, linked);
            Check("F7-link", File.Exists(victim), "a red-camera folder that is a link to another folder is never cleaned");
        }
    }

    private static void SourceChecks(string root)
    {
        string Read(params string[] parts)
        {
            string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        string local = Read("Services", "LocalHousekeeping.cs");
        string capture = Read("CameraCaptureWindow.xaml.cs");
        if (local.Length == 0 || capture.Length == 0)
        {
            Check("source", false, "RED source not found (pass the RED source folder as the first argument)");
            return;
        }
        Check("F7-when", local.Contains("Camera.CameraRollCopy.Purge(now,") && capture.Contains("CameraRollCopy.PurgeOldInBackground()") &&
                         !Read("App.Housekeeping.cs").Contains("CameraRollCopy.PurgeOld("),
            "the purge runs at every start (LocalHousekeeping, also with no Inspections folder) and when the camera opens");
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
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InspectionEditor.csproj"))) dir = dir.Parent;
            if (dir != null) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
