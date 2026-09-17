using System.Text;
using InspectionEditor.Services;

internal static class AtomicRetryProbe
{
    internal static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "red-atomic-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("Atomic retry: " + name);
            Console.WriteLine("PASS atomic retry " + name);
            checks++;
        }
        const string oldJson = "{\"value\":\"old\"}";
        const string newJson = "{\"value\":\"sensitive-report-value\"}";
        byte[] expected = Encoding.UTF8.GetBytes(oldJson);
        IOException Error(int code) => new IOException("private path / secret-key / report-data", unchecked((int)(0x80070000u | (uint)code)));
        try
        {
            void Scenario(string name, int[] errors, Action<string, string>? afterFailure = null,
                bool success = false, string? finalTarget = oldJson, bool missingTarget = false)
            {
                string target = Path.Combine(dir, name + ".ins");
                File.WriteAllText(target, oldJson);
                int attempts = 0;
                var delays = new List<int>();
                string? temporary = null;
                IOException? firstError = null;
                var operations = new AtomicInspectionWriter.Operations
                {
                    Replace = (temp, path, backup) =>
                    {
                        temporary = temp;
                        attempts++;
                        if (attempts <= errors.Length)
                        {
                            var error = Error(errors[attempts - 1]);
                            firstError ??= error;
                            throw error;
                        }
                        File.Replace(temp, path, backup);
                    },
                    Delay = milliseconds =>
                    {
                        delays.Add(milliseconds);
                        afterFailure?.Invoke(target, temporary!);
                    }
                };
                IOException? failure = null;
                byte[]? written = null;
                try { written = AtomicInspectionWriter.Write(target, newJson, expected, operations); }
                catch (IOException ex) { failure = ex; }
                Check(success ? failure == null && written!.SequenceEqual(Encoding.UTF8.GetBytes(newJson)) : failure != null,
                    name + " outcome");
                int wantedAttempts = success ? errors.Length + 1 : afterFailure != null ? 1 : Math.Min(errors.Length, 4);
                Check(attempts == wantedAttempts, name + " bounded attempts");
                Check(delays.Sum() <= 700 && delays.Count <= 3 && delays.SequenceEqual(new[] { 100, 200, 400 }.Take(delays.Count)), name + " bounded backoff");
                Check(missingTarget ? !File.Exists(target) : File.ReadAllText(target) == (success ? newJson : finalTarget), name + " target preserved");
                Check(!Directory.GetFiles(dir, "*.tmp").Any(), name + " temp cleanup");
                if (success)
                    Check(File.ReadAllText(target + ".red-save-backup") == oldJson, name + " original backup");
                else
                {
                    Check(ReferenceEquals(failure!.InnerException, firstError), name + " original exception retained");
                    Check(failure.Message.Contains("attempts=" + (afterFailure != null ? attempts + 1 : attempts)) && failure.Message.Contains("Operation=") &&
                        failure.Message.Contains("HResult=0x") && failure.Message.Contains("errorCode=" + errors[0]) &&
                        failure.Message.Contains(Path.GetFileName(target)) && !failure.Message.Contains(dir) &&
                        !failure.Message.Contains("secret-key") && !failure.Message.Contains("sensitive-report-value"), name + " sanitized diagnostics");
                }
                if (name == "permanent") Check(delays.Count == 0, "permanent fails immediately");
                if (name == "exhausted") Check(attempts == 4 && delays.Sum() == 700, "exact retry limit");
            }

            Scenario("sharing-then-1175", new[] { 32, 1175 }, success: true);
            Scenario("lock", new[] { 33 }, success: true);
            Scenario("changed-target", new[] { 1175 }, (path, _) => File.WriteAllText(path, "external"), finalTarget: "external");
            Scenario("missing-target", new[] { 1175 }, (path, _) => File.Delete(path), missingTarget: true);
            Scenario("missing-temp", new[] { 1175 }, (_, temp) => File.Delete(temp));
            Scenario("changed-temp", new[] { 32 }, (_, temp) => File.WriteAllText(temp, "wrong payload"));
            Scenario("permanent", new[] { 5 });
            Scenario("transient-then-permanent", new[] { 32, 5 });
            Scenario("unable-move-1176", new[] { 1176 });
            Scenario("unable-move-1177", new[] { 1177 });
            Scenario("exhausted", new[] { 1175, 1175, 1175, 1175 });
            // Simulate an ambiguous failure where native replacement consumed the temporary file.
            // Even if the target now holds intended bytes, do not claim success or replace again.
            Scenario("partial-replacement", new[] { 1175 }, (path, temp) => File.Replace(temp, path, path + ".red-save-backup"), finalTarget: newJson);
            foreach (bool changed in new[] { false, true })
            {
                string path = Path.Combine(dir, "read-lock-" + changed + ".ins");
                File.WriteAllText(path, oldJson);
                int reads = 0, replacements = 0, waits = 0;
                var original = Error(32);
                var operations = new AtomicInspectionWriter.Operations {
                    ReadAllBytes = file => {
                        if (file == path && ++reads == 1) throw original;
                        return File.ReadAllBytes(file);
                    },
                    Replace = (temp, target, backup) => { replacements++; File.Replace(temp, target, backup); },
                    Delay = milliseconds => { waits++; if (changed) File.WriteAllText(path, "external"); }
                };
                IOException? failure = null;
                try { AtomicInspectionWriter.Write(path, newJson, expected, operations); }
                catch (IOException ex) { failure = ex; }
                Check(reads == 2 && waits == 1, "read-lock retry revalidates target");
                Check(replacements == (changed ? 0 : 1), "read-lock no replace before successful validation");
                Check(File.ReadAllText(path) == (changed ? "external" : newJson), "read-lock target outcome");
                Check(changed ? failure != null && ReferenceEquals(failure.InnerException, original) && failure.Message.Contains("changed outside RED") : failure == null,
                    "read-lock conflict explanation and original exception");
                Check(!Directory.GetFiles(dir, "*.tmp").Any(), "read-lock temp cleanup");
            }
            Console.WriteLine($"{checks} atomic retry checks passed");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
