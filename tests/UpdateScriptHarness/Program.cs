// Storage audit F3 harness: scripts\update_red.bat backs up only what it restores, keeps the newest 3 backups, and
// deletes its download and unpacked copy on success and on failure. The script's own subroutines are copied verbatim
// into a test script and run against a sandbox (never C:\Red, the real %LOCALAPPDATA% or %TEMP%); the parts that need
// Administrator rights or the network are checked in the source. Exit 0 = all passed.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

return UpdateScriptHarness.Run(args);

static class UpdateScriptHarness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {what}");
        if (ok) _passed++; else _failed++;
    }

    public static int Run(string[] args)
    {
        string root = args.Length > 0 ? args[0] : FindRepoRoot();
        string scriptPath = Path.Combine(root, "scripts", "update_red.bat");
        if (!File.Exists(scriptPath))
        {
            Console.WriteLine("FAIL source scripts\\update_red.bat not found (pass the RED source folder as the first argument)");
            return 1;
        }
        string script = File.ReadAllText(scriptPath);
        string sandbox = Path.Combine(Path.GetTempPath(), "red-storage-updatebat-" + Guid.NewGuid().ToString("N"));
        try
        {
            SourceChecks(script);
            Behaviour(script, sandbox);
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString());
        }
        finally
        {
            // Audit S-28: the sandbox always goes (links first, so nothing outside it is touched).
            DeleteSandbox(sandbox);
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void SourceChecks(string script)
    {
        Check("F3-crlf", !Regex.IsMatch(script, "(?<!\r)\n") && script.All(c => c < 128),
            "the script is plain ASCII with CRLF line endings (cmd.exe needs them for labels)");

        int elevated = script.IndexOf(":elevated_start", StringComparison.Ordinal);
        int cancelPause = script.IndexOf("pause >nul", script.IndexOf("Close this window now to cancel", StringComparison.Ordinal), StringComparison.Ordinal);
        int firstMkdir = script.IndexOf("mkdir \"%BACKUP_DIR%\"", StringComparison.Ordinal);
        string beforePause = elevated < 0 || cancelPause < 0 ? "" : script[elevated..cancelPause];
        Check("F3-cancel", elevated > 0 && cancelPause > elevated && firstMkdir > cancelPause &&
                           !beforePause.Contains("call :") && !beforePause.Contains(">>") && !beforePause.Contains("mkdir"),
            "nothing is written before the cancel prompt: a cancelled run leaves no folder or log");

        Check("F3-profile", script.Contains("in the Windows account that approved") && script.Contains(":: The backup goes into the Windows account that approved"),
            "the script says the backup goes to the profile of whoever approved the Administrator prompt");

        string backup = Sub(script, ":backup_preserved");
        Check("F3-scope", backup.Length > 0 &&
                          !Regex.IsMatch(script, @"robocopy ""%INSTALL_DIR%"" ") &&
                          !script.Contains("LocalAppData-InspectionEditor") && !script.Contains("LocalAppData-RED-2.0-Dev") &&
                          !script.Contains(":backup_dir") &&
                          backup.Contains("call :copy_tree \"%LOCALAPPDATA%\\RED\" \"%SAVED%\"\r\n"),
            "the backup no longer copies the whole program folder, the whole data folder, or the legacy and dev folders");

        Check("F3-keep3", script.Contains("set \"BACKUPS_KEPT=3\"") && Sub(script, ":prune_backups").Contains("/a:d-l") &&
                          script.IndexOf("call :prune_backups", StringComparison.Ordinal) > script.IndexOf("call :backup_preserved", StringComparison.Ordinal),
            "after the backup, only the newest 3 before-red2-* backups are kept (never a link)");

        string fail = Sub(script, ":fail");
        int installed = script.IndexOf("echo          Installed.", StringComparison.Ordinal);
        int restore = script.IndexOf("call :restore_preserved_files", StringComparison.Ordinal);
        int cleanupOk = script.IndexOf("call :cleanup_download", restore < 0 ? 0 : restore, StringComparison.Ordinal);
        Check("F3-download", fail.StartsWith(":fail\r\ncall :cleanup_download\r\n", StringComparison.Ordinal) &&
                             cleanupOk > restore && cleanupOk < installed &&
                             Sub(script, ":cleanup_download").Contains("del /f /q \"%DOWNLOAD_ZIP%\"") &&
                             Sub(script, ":cleanup_download").Contains("rd /s /q \"%EXTRACT_DIR%\""),
            "the download zip and the unpacked copy are deleted after a successful install and at the start of :fail");
    }

    private static void Behaviour(string script, string sandbox)
    {
        string install = Path.Combine(sandbox, "Red");
        string lad = Path.Combine(sandbox, "LocalAppData");
        string red = Path.Combine(lad, "RED");
        string temp = Path.Combine(sandbox, "temp");
        string backups = Path.Combine(lad, "RED_Backups");
        string outside = Path.Combine(sandbox, "outside");
        foreach (var dir in new[] { install, red, temp, backups, outside }) Directory.CreateDirectory(dir);

        void Put(string path, int size = 16)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', size).ToArray());
        }
        foreach (var name in new[] { "license.lic", "preferences.txt", "notify_custom_message.txt", "settings.txt" })
            Put(Path.Combine(install, name));
        Put(Path.Combine(install, "userdata", "custom.json"));
        Put(Path.Combine(install, "Red.exe"), 200_000);
        Put(Path.Combine(install, "runtimes", "win-x64", "native.dll"), 200_000);
        Put(Path.Combine(red, "housekeeping.json"));
        Put(Path.Combine(red, "license.lic"));
        Put(Path.Combine(red, "userdata", "templates.json"));
        Put(Path.Combine(red, "Signature", "signature.png"));
        Put(Path.Combine(red, "Recovery", "failed-save-20261001T101010101Z-" + Guid.NewGuid().ToString("N") + ".ins"), 300_000);
        Put(Path.Combine(red, "PdfAttachments", "abc", "plan.pdf"), 300_000);
        Put(Path.Combine(red, "SubmitBackups", "20261001-101010101-1234567-CPP-1-PQE.ins"), 300_000);
        Put(Path.Combine(red, "offline", "jobs.json"), 300_000);
        Put(Path.Combine(red, "Logs", "red-2026-10-01.log"));
        Put(Path.Combine(red, "VerifiedSaves", "AB.json"));
        string[] oldBackups = { "before-red2-20260610-101010", "before-red2-20260712-101010", "before-red2-20260712-111111", "before-red2-20260712-121212" };
        foreach (var name in oldBackups) Put(Path.Combine(backups, name, "update-red2.log"));
        Put(Path.Combine(backups, "before-red2-notes", "keep.txt"));
        Put(Path.Combine(outside, "precious.txt"));
        Junction(Path.Combine(backups, "before-red2-20200101-000000"), outside);
        Put(Path.Combine(temp, "red2_update.zip"), 100_000);
        Put(Path.Combine(temp, "red2_extract", "Red.exe"), 100_000);

        string stamp = "20261022-120000";
        var test = new StringBuilder();
        test.Append("@echo off\r\nsetlocal EnableExtensions EnableDelayedExpansion\r\n");
        test.Append($"set \"INSTALL_DIR={install}\"\r\n");
        test.Append($"set \"LOCALAPPDATA={lad}\"\r\n");
        test.Append($"set \"DOWNLOAD_ZIP={Path.Combine(temp, "red2_update.zip")}\"\r\n");
        test.Append($"set \"EXTRACT_DIR={Path.Combine(temp, "red2_extract")}\"\r\n");
        // The script's own settings, taken from it, after the test's folders.
        foreach (var line in new[] { "set \"BACKUP_ROOT=", "set \"BACKUPS_KEPT=", "set \"BACKUP_DIR=", "set \"LOG_FILE=" })
            test.Append(script.Split("\r\n").First(l => l.StartsWith(line, StringComparison.Ordinal))).Append("\r\n");
        test.Append($"set \"STAMP={stamp}\"\r\nset \"BACKUP_DIR=%BACKUP_ROOT%\\before-red2-%STAMP%\"\r\nset \"LOG_FILE=%BACKUP_DIR%\\update-red2.log\"\r\n");
        test.Append("mkdir \"%BACKUP_DIR%\" >nul 2>&1\r\n");
        test.Append("call :backup_preserved\r\ncall :prune_backups\r\ncall :cleanup_download\r\nexit /b 0\r\n\r\n");
        foreach (var label in new[] { ":backup_preserved", ":copy_tree", ":prune_backups", ":cleanup_download", ":log" })
            test.Append(Sub(script, label)).Append("\r\n");
        string testPath = Path.Combine(sandbox, "test-update.bat");
        File.WriteAllText(testPath, test.ToString(), Encoding.ASCII);

        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{testPath}\" <nul")
               { WorkingDirectory = sandbox, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
        }

        string made = Path.Combine(backups, "before-red2-" + stamp);
        string saved = Path.Combine(made, "InstallFolder-C-Red");
        string savedRed = Path.Combine(made, "LocalAppData-RED");
        bool restoredItems = new[] { "license.lic", "preferences.txt", "notify_custom_message.txt", "settings.txt" }.All(n => File.Exists(Path.Combine(saved, n)))
                             && File.Exists(Path.Combine(saved, "userdata", "custom.json"))
                             && File.Exists(Path.Combine(savedRed, "license.lic")) && File.Exists(Path.Combine(savedRed, "housekeeping.json"))
                             && File.Exists(Path.Combine(savedRed, "userdata", "templates.json")) && File.Exists(Path.Combine(savedRed, "Signature", "signature.png"));
        Check("F3-backup", restoredItems, "the backup holds everything the restore step uses: license, preferences, notify message, settings, userdata, and RED's top-level files, userdata and Signature");

        bool lean = !File.Exists(Path.Combine(saved, "Red.exe")) && !Directory.Exists(Path.Combine(saved, "runtimes"))
                    && new[] { "Recovery", "PdfAttachments", "SubmitBackups", "offline", "Logs", "VerifiedSaves" }.All(n => !Directory.Exists(Path.Combine(savedRed, n)));
        long size = Directory.Exists(made) ? Directory.GetFiles(made, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : -1;
        Check("F3-lean", lean && size >= 0 && size < 50_000,
            $"no program files, recovery copies, PDFs, backups, caches or logs in the backup ({size} bytes)");

        var left = Directory.GetDirectories(backups).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        bool kept3 = left.Contains("before-red2-" + stamp) && left.Contains("before-red2-20260712-121212") && left.Contains("before-red2-20260712-111111")
                     && !left.Contains("before-red2-20260712-101010") && !left.Contains("before-red2-20260610-101010");
        Check("F3-keep3-run", kept3, "the newest 3 backups stay (this run's and the two newest older ones); the two oldest are deleted");
        Check("F3-keep3-safe", left.Contains("before-red2-notes") && File.Exists(Path.Combine(backups, "before-red2-notes", "keep.txt"))
                               && left.Contains("before-red2-20200101-000000") && File.Exists(Path.Combine(outside, "precious.txt")),
            "a folder with another name, and a link (and what it points to), are never deleted");

        Check("F3-download-run", !File.Exists(Path.Combine(temp, "red2_update.zip")) && !Directory.Exists(Path.Combine(temp, "red2_extract")),
            "the download zip and the unpacked copy are deleted");
        Check("F3-originals", File.Exists(Path.Combine(install, "Red.exe")) && Directory.GetFiles(Path.Combine(red, "Recovery")).Length == 1
                              && File.Exists(Path.Combine(red, "offline", "jobs.json")),
            "the backup only copies: the program folder and RED's data folder are unchanged");
        Check("F3-log", File.Exists(Path.Combine(made, "update-red2.log")) && File.ReadAllText(Path.Combine(made, "update-red2.log")).Contains("Removing old backup before-red2-20260610-101010"),
            "the run is logged in the new backup folder");
    }

    // A subroutine: from its label line to its "exit /b 0" (or, for :fail, its "exit /b 1").
    private static string Sub(string script, string label)
    {
        int start = script.IndexOf("\r\n" + label + "\r\n", StringComparison.Ordinal);
        if (start < 0) return "";
        start += 2;
        int end = script.IndexOf("\r\nexit /b ", start, StringComparison.Ordinal);
        if (end < 0) return "";
        end = script.IndexOf("\r\n", end + 2, StringComparison.Ordinal);
        return end < 0 ? script[start..] : script[start..(end + 2)];
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (!Directory.Exists(link)) throw new Exception("junction not made");
    }

    private static void DeleteSandbox(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var dir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                         .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
                Directory.Delete(dir); // the link only
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.WriteLine("note: sandbox not fully deleted: " + ex.GetType().Name);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InspectionEditor.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
