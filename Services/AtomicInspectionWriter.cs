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
        }

        // No truncate/copy fallback: an uncertain replacement is always a failed save.
        public static byte[] Write(string path, string json, byte[]? expectedBytes) =>
            Write(path, json, expectedBytes, new Operations());

        internal static byte[] Write(string path, string json, byte[]? expectedBytes, Operations operations)
        {
            JObject.Parse(json);
            string target = Path.GetFullPath(path);
            string temp = target + ".red-" + Guid.NewGuid().ToString("N") + ".tmp";
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
                            operation = "replace";
                            operations.Replace(temp, target, target + ".red-save-backup");
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

        private static bool IsTransientReplacementFailure(IOException exception)
        {
            // Only HRESULT_FROM_WIN32 sharing, lock, and unable-to-remove-replaced errors.
            // Do not classify unrelated HRESULT facilities by their low bits alone.
            uint value = unchecked((uint)exception.HResult);
            return value == 0x80070020 || value == 0x80070021 || value == 0x80070497;
        }
    }
}
