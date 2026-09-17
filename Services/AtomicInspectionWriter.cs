using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public static class AtomicInspectionWriter
    {
        // Per-call seam, not global mutable state. Tests exercise the production retry loop.
        internal sealed class Operations
        {
            internal Func<string, byte[]> ReadAllBytes = File.ReadAllBytes;
            internal Action<string, string, string> Replace = File.Replace;
            internal Action<int> Delay = Thread.Sleep;
            internal Action<string, string> CompleteBackup = File.Move;
            internal Action<string> DeleteBackup = File.Delete;
        }

        // No truncate/copy fallback: an uncertain replacement is always a failed save.
        public static byte[] Write(string path, string json, byte[]? expectedBytes) =>
            Write(path, json, expectedBytes, new Operations());

        internal static byte[] Write(string path, string json, byte[]? expectedBytes, Operations operations)
        {
            JObject.Parse(json);
            string target = Path.GetFullPath(path);
            string temp = target + ".red-" + Guid.NewGuid().ToString("N") + ".tmp";
            // Same directory keeps Replace on one volume. Never reuse a prior save's backup,
            // which may still be open elsewhere. Keep this name stable across retries.
            string backup = target + ".red-save-backup-" + Guid.NewGuid().ToString("N");
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            int attempts = 0;
            string operation = "write-temp";
            Exception? firstIoFailure = null;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                operation = "verify-temp";
                if (!operations.ReadAllBytes(temp).SequenceEqual(bytes))
                    throw new IOException("Temporary report verification failed.");
                if (expectedBytes != null)
                {
                    // Four attempts, with only 700 ms of scheduled waiting in total.
                    int[] delays = { 100, 200, 400 };
                    while (true)
                    {
                        // Recheck AFTER waiting, immediately before every replacement. A failed
                        // Replace may have moved/deleted either file: never infer success or repair it.
                        attempts++;
                        try
                        {
                            operation = "verify-target";
                            if (!operations.ReadAllBytes(target).SequenceEqual(expectedBytes))
                            {
                                operation = "external-change";
                                throw new IOException("The report changed outside RED.");
                            }
                            operation = "verify-temp";
                            if (!operations.ReadAllBytes(temp).SequenceEqual(bytes))
                            {
                                operation = "temp-changed";
                                throw new IOException("Temporary report verification failed.");
                            }
                            // A failed native replacement may have already produced recovery bytes.
                            // Do not overwrite or delete that evidence, even if target/temp match.
                            operation = "verify-backup";
                            if (File.Exists(backup) || Directory.Exists(backup))
                            {
                                operation = "backup-created";
                                throw new IOException("A backup exists from an uncertain replacement.");
                            }
                            operation = "replace";
                            operations.Replace(temp, target, backup);
                            break;
                        }
                        catch (IOException ex)
                        {
                            firstIoFailure ??= ex;
                            if (!IsTransientReplacementFailure(ex) || attempts > delays.Length)
                                throw;
                            operation = "retry-delay";
                            operations.Delay(delays[attempts - 1]);
                        }
                    }
                }
                else
                {
                    // Save As creates a new file, never silently overwrites an unrelated report.
                    operation = "move-new";
                    attempts = 1;
                    File.Move(temp, target);
                }
                // Outside the retry loop: a consumed temp must never be replaced again.
                operation = "verify-saved-target";
                if (!operations.ReadAllBytes(target).SequenceEqual(bytes))
                    throw new IOException("Saved report verification failed.");
                if (expectedBytes != null)
                    CompleteAndTrimBackups(target, backup, operations);
                return bytes;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Exception original = firstIoFailure ?? ex;
                // Do not include exception messages, full paths, report contents, or keys.
                string filename = new string(Path.GetFileName(target)
                    .Select(c => char.IsControl(c) ? '_' : c).ToArray());
                string reason = operation switch
                {
                    "external-change" => "The report changed outside RED; no replacement was attempted for that version.",
                    "temp-changed" => "The pending save file changed; replacement was stopped.",
                    "backup-created" => "A recovery backup exists from an uncertain replacement; replacement was stopped.",
                    "verify-target" => "The existing report could not be read safely; replacement was stopped.",
                    "verify-temp" => "The pending save file could not be verified; replacement was stopped.",
                    _ => "Windows could not complete the safe file operation."
                };
                throw new IOException(
                    $"Save stopped for '{filename}'. {reason} Operation={operation}; attempts={attempts}; " +
                    $"HResult=0x{original.HResult:X8}; errorCode={original.HResult & 0xFFFF}; " +
                    $"lastHResult=0x{ex.HResult:X8}; lastErrorCode={ex.HResult & 0xFFFF}. " +
                    "Keep RED open; edits remain unsaved. Resolve any file conflict or lock before retrying.", original);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static void CompleteAndTrimBackups(string target, string backup, Operations operations)
        {
            // Only this call's confirmed native replacement can promote its backup.
            // Unmarked/legacy files are recovery evidence, never retention candidates.
            try
            {
                string completed = backup + ".completed";
                operations.CompleteBackup(backup, completed);
                File.SetLastWriteTimeUtc(completed, DateTime.UtcNow);
                string prefix = Path.GetFileName(target) + ".red-save-backup-";
                const string suffix = ".completed";
                var candidates = Directory.EnumerateFiles(Path.GetDirectoryName(target)!)
                    .Where(file => {
                        string name = Path.GetFileName(file);
                        return name.StartsWith(prefix, StringComparison.Ordinal) &&
                            name.EndsWith(suffix, StringComparison.Ordinal) &&
                            name.Length == prefix.Length + 32 + suffix.Length &&
                            Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _);
                    })
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenByDescending(file => file == completed)
                    .ThenBy(file => file, StringComparer.Ordinal)
                    .Skip(3).ToArray();
                foreach (string file in candidates)
                {
                    try { operations.DeleteBackup(file); } catch { }
                }
            }
            catch { /* Retention is best-effort and cannot turn a verified save into failure. */ }
        }

        private static bool IsTransientReplacementFailure(IOException exception)
        {
            // Only HRESULT_FROM_WIN32 sharing, lock, and unable-to-remove-replaced errors.
            // Do not classify unrelated HRESULT facilities by their low bits alone.
            uint value = unchecked((uint)exception.HResult);
            return value == 0x80070020 || value == 0x80070021 || value == 0x80070497;
        }
    }
}
