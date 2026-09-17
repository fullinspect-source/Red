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
                var backups = new List<string>();
                IOException? firstError = null;
                var operations = new AtomicInspectionWriter.Operations
                {
                    Replace = (temp, path, backup) =>
                    {
                        temporary = temp;
                        backups.Add(backup);
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
                Check(backups.Distinct().Count() == 1 && Path.GetDirectoryName(backups[0]) == dir &&
                    backups[0] != target + ".red-save-backup", name + " same-directory per-save backup reused only for retries");
                if (success)
                    Check(File.ReadAllText(backups[0] + ".completed") == oldJson, name + " original backup");
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
                Check(reads == (changed ? 2 : 3) && waits == 1, "read-lock retry revalidates target");
                Check(replacements == (changed ? 0 : 1), "read-lock no replace before successful validation");
                Check(File.ReadAllText(path) == (changed ? "external" : newJson), "read-lock target outcome");
                Check(changed ? failure != null && ReferenceEquals(failure.InnerException, original) && failure.Message.Contains("changed outside RED") : failure == null,
                    "read-lock conflict explanation and original exception");
                Check(!Directory.GetFiles(dir, "*.tmp").Any(), "read-lock temp cleanup");
            }
            string repeated = Path.Combine(dir, "repeated.ins");
            File.WriteAllText(repeated, oldJson);
            File.WriteAllText(repeated + ".red-save-backup", "legacy recovery bytes");
            var savedBackups = new List<string>();
            var repeatedOperations = new AtomicInspectionWriter.Operations {
                Replace = (temp, target, backup) => {
                    savedBackups.Add(backup);
                    File.Replace(temp, target, backup);
                }
            };
            var firstWritten = AtomicInspectionWriter.Write(repeated, newJson, expected, repeatedOperations);
            AtomicInspectionWriter.Write(repeated, oldJson, firstWritten, repeatedOperations);
            Check(savedBackups.Count == 2 && savedBackups.Distinct().Count() == 2, "distinct backup destinations across saves");
            Check(File.ReadAllText(savedBackups[0] + ".completed") == oldJson && File.ReadAllText(savedBackups[1] + ".completed") == newJson,
                "both prior versions preserved");
            Check(File.ReadAllText(repeated + ".red-save-backup") == "legacy recovery bytes", "legacy backup untouched");

            string partial = Path.Combine(dir, "partial-backup.ins");
            File.WriteAllText(partial, oldJson);
            int partialAttempts = 0;
            string? partialBackup = null;
            IOException? partialFailure = null;
            var partialOperations = new AtomicInspectionWriter.Operations {
                Replace = (temp, target, backup) => {
                    partialAttempts++;
                    partialBackup = backup;
                    File.WriteAllText(backup, "partial recovery bytes");
                    throw Error(1175);
                },
                Delay = _ => { }
            };
            try { AtomicInspectionWriter.Write(partial, newJson, expected, partialOperations); }
            catch (IOException ex) { partialFailure = ex; }
            Check(partialFailure != null && partialAttempts == 1, "partial backup stops further replacement");
            Check(File.ReadAllText(partialBackup!) == "partial recovery bytes" && File.ReadAllText(partial) == oldJson,
                "partial backup and target retained on failure");

            // Seed exact-report uncertain evidence and near-match completed names.
            string uncertain = repeated + ".red-save-backup-" + Guid.NewGuid().ToString("N");
            string malformed = repeated + ".red-save-backup-not-a-guid.completed";
            string otherReport = repeated + ".other.red-save-backup-" + Guid.NewGuid().ToString("N") + ".completed";
            foreach (string evidence in new[] { uncertain, malformed, otherReport })
                File.WriteAllText(evidence, "untouchable");
            foreach (string saved in savedBackups)
                File.SetLastWriteTimeUtc(saved + ".completed", DateTime.UtcNow.AddDays(-1));
            byte[] current = expected;
            for (int i = 0; i < 8; i++)
            {
                current = AtomicInspectionWriter.Write(repeated, "{\"revision\":" + i + "}", current, repeatedOperations);
                // Deterministic completion ordering even on coarse timestamp filesystems.
                File.SetLastWriteTimeUtc(savedBackups.Last() + ".completed", DateTime.UtcNow.AddMinutes(i - 20));
            }
            var retained = savedBackups.Where(file => File.Exists(file + ".completed")).ToArray();
            Check(retained.Length == 3 && retained.SequenceEqual(savedBackups.TakeLast(3)), "only newest three completed backups retained");
            Check(new[] { uncertain, malformed, otherReport }.All(file => File.ReadAllText(file) == "untouchable"), "uncertain and near-match backups untouched");
            Check(File.ReadAllText(partialBackup!) == "partial recovery bytes" && !File.Exists(partialBackup + ".completed"), "failed replacement backup remains unmarked");
            Check(File.ReadAllText(repeated + ".red-save-backup") == "legacy recovery bytes", "legacy survives retention");
            int deletes = 0;
            var cleanupFailure = new AtomicInspectionWriter.Operations {
                DeleteBackup = _ => { deletes++; throw new IOException("cleanup blocked"); }
            };
            current = AtomicInspectionWriter.Write(repeated, newJson, current, cleanupFailure);
            Check(deletes > 0 && File.ReadAllText(repeated) == newJson, "cleanup deletion failure does not fail save");
            current = AtomicInspectionWriter.Write(repeated, oldJson, current, new AtomicInspectionWriter.Operations {
                CompleteBackup = (_, _) => throw new IOException("rename blocked")
            });
            Check(current.SequenceEqual(expected) && File.ReadAllText(repeated) == oldJson, "completion rename failure does not fail save");
            string? corruptBackup = null;
            int corruptAttempts = 0;
            IOException? corruptFailure = null;
            try {
                AtomicInspectionWriter.Write(repeated, newJson, current, new AtomicInspectionWriter.Operations {
                    Replace = (temp, target, backup) => {
                        corruptAttempts++; corruptBackup = backup;
                        File.Replace(temp, target, backup);
                        File.WriteAllText(target, "corrupted");
                    }
                });
            } catch (IOException ex) { corruptFailure = ex; }
            Check(corruptFailure != null && corruptAttempts == 1 && File.Exists(corruptBackup) && !File.Exists(corruptBackup + ".completed"),
                "corrupt saved target fails without marking backup or retrying");

            foreach (int hresult in new[] { unchecked((int)0x80040020), unchecked((int)0x80040497) })
            {
                int calls = 0, waits = 0;
                IOException? failure = null;
                var operations = new AtomicInspectionWriter.Operations {
                    Replace = (_, _, _) => { calls++; throw new IOException("wrong facility", hresult); },
                    Delay = _ => waits++
                };
                try { AtomicInspectionWriter.Write(partial, newJson, expected, operations); }
                catch (IOException ex) { failure = ex; }
                Check(failure != null && calls == 1 && waits == 0 && File.ReadAllText(partial) == oldJson,
                    "unrelated HRESULT facility never retried " + hresult);
            }
            Console.WriteLine($"{checks} atomic retry checks passed");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
