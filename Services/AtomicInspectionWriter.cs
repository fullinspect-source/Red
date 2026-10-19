using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-002 E1: the report on disk is no longer the version RED loaded or last saved (INSPECT, a Dropbox sync from
    /// another computer, or the owner's fee automation changed, moved or deleted it). Nothing was written.
    /// </summary>
    public sealed class InspectionChangedOnDiskException : IOException
    {
        public bool TargetMissing { get; }

        // The verified local recovery copy of RED's edits made when the save stopped; null when none could be made.
        public string? RecoveryPath { get; init; }

        public InspectionChangedOnDiskException(string message, Exception? inner, bool targetMissing)
            : base(message, inner) => TargetMissing = targetMissing;
    }

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

        // F-002 A10: RED's temp and backup files may sit only beside My List reports, never in these folders
        // (nor in INSPECT's Temp\{M-d-yyyy} snapshot folders: RED never writes into INSPECT's Temp).
        private static readonly string[] SidecarFreeFolders = { "Outbox", "Archive", "Review" };

        /// <summary>
        /// Where the temp and backup files of a save go: beside the report, except for Outbox, Archive, Review and
        /// Temp. Those are staged in a hidden folder inside the same tree's MyList (the one place A10 allows RED
        /// sidecars): {Inspections}\MyList\.red-save-staging\{Review|Archive|Outbox|Temp}. It is always on the report's
        /// own volume (File.Replace stays atomic, whatever drive, share, junction or mount point the tree is on) and is
        /// separate per tree and per folder, so backup trimming never reaches another report's backups.
        /// </summary>
        internal static string SidecarDirectory(string target)
        {
            string folder = Path.GetDirectoryName(target)!;
            string name = Path.GetFileName(folder);
            string? parentName = Path.GetFileName(Path.GetDirectoryName(folder) ?? "");
            bool inTempDateFolder = string.Equals(parentName, "Temp", StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{1,2}-\d{1,2}-\d{4}$");
            if (!inTempDateFolder && !SidecarFreeFolders.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                return folder;
            string inspectionsRoot = Path.GetDirectoryName(inTempDateFolder ? Path.GetDirectoryName(folder)! : folder)!;
            string redStaging = Path.Combine(inspectionsRoot, "MyList", ".red-save-staging");
            string staging = Path.Combine(redStaging, inTempDateFolder ? "Temp" : name);
            Directory.CreateDirectory(staging);
            try { File.SetAttributes(redStaging, File.GetAttributes(redStaging) | FileAttributes.Hidden); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* hidden is cosmetic */ }
            return staging;
        }

        internal static byte[] Write(string path, string json, byte[]? expectedBytes, Operations operations)
        {
            JObject.Parse(json);
            string target = Path.GetFullPath(path);
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            int attempts = 0;
            string operation = "stage";
            string temp = "", backup = "", sidecars = "";
            Exception? firstIoFailure = null;
            try
            {
                // Same volume keeps Replace atomic. Never reuse a prior save's backup,
                // which may still be open elsewhere. Keep this name stable across retries.
                sidecars = SidecarDirectory(target);
                temp = Path.Combine(sidecars, Path.GetFileName(target) + ".red-" + Guid.NewGuid().ToString("N") + ".tmp");
                backup = Path.Combine(sidecars, Path.GetFileName(target) + ".red-save-backup-" + Guid.NewGuid().ToString("N"));
                operation = "write-temp";
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
                            byte[] current;
                            try { current = operations.ReadAllBytes(target); }
                            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                            {
                                // A report gone from a folder that is still there was moved; a missing folder or drive
                                // (unplugged, share dropped) is only unavailable, so the save stays retryable.
                                operation = ReportFolderAvailable(target) ? "target-missing" : "folder-unavailable";
                                throw;
                            }
                            if (!current.SequenceEqual(expectedBytes))
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
                if (!ReadWithSharingRetry(target, operations).SequenceEqual(bytes))
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
                    "target-missing" => "The report was moved or deleted outside RED; it was not recreated.",
                    "folder-unavailable" => "The report's folder or drive is not available right now; the report was not changed.",
                    "stage" => "RED could not prepare its save folder (MyList\\.red-save-staging) beside the report; the report was not changed.",
                    "temp-changed" => "The pending save file changed; replacement was stopped.",
                    "backup-created" => "A recovery backup exists from an uncertain replacement; replacement was stopped.",
                    "verify-target" => "The existing report could not be read safely; replacement was stopped.",
                    "verify-temp" => "The pending save file could not be verified; replacement was stopped.",
                    _ => "Windows could not complete the safe file operation."
                };
                string message =
                    $"Save stopped for '{filename}'. {reason} Operation={operation}; attempts={attempts}; " +
                    $"HResult=0x{original.HResult:X8}; errorCode={original.HResult & 0xFFFF}; " +
                    $"lastHResult=0x{ex.HResult:X8}; lastErrorCode={ex.HResult & 0xFFFF}. " +
                    "Keep RED open; edits remain unsaved. Resolve any file conflict or lock before retrying.";
                if (operation == "external-change" || operation == "target-missing")
                    throw new InspectionChangedOnDiskException(message, original, operation == "target-missing");
                throw new IOException(message, original);
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
                var candidates = Directory.EnumerateFiles(Path.GetDirectoryName(backup)!)
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

        // True when the report's folder is reachable, so a missing report there really was moved or deleted.
        internal static bool ReportFolderAvailable(string reportPath)
        {
            try { return Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(reportPath))); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return false; }
        }

        // A sync client may hold the freshly replaced file for a moment; a sharing violation is not a failed save.
        private static byte[] ReadWithSharingRetry(string path, Operations operations)
        {
            int[] delays = { 100, 200, 400 };
            for (int attempt = 0; ; attempt++)
            {
                try { return operations.ReadAllBytes(path); }
                catch (IOException ex) when (attempt < delays.Length &&
                    (unchecked((uint)ex.HResult) == 0x80070020 || unchecked((uint)ex.HResult) == 0x80070021))
                {
                    operations.Delay(delays[attempt]);
                }
            }
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
