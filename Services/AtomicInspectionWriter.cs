using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public static class AtomicInspectionWriter
    {
        // No truncate/copy fallback: unsupported/locked replacement must fail with the old report intact.
        public static byte[] Write(string path, string json, byte[]? expectedBytes)
        {
            JObject.Parse(json);
            string target = Path.GetFullPath(path);
            string temp = target + ".red-" + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                if (!File.ReadAllBytes(temp).SequenceEqual(bytes))
                    throw new IOException("Temporary report verification failed.");
                if (expectedBytes != null)
                {
                    if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(expectedBytes))
                        throw new IOException("The report changed outside RED. Save stopped to avoid overwriting another version. Keep RED open and resolve the file conflict before retrying.");
                    File.Replace(temp, target, target + ".red-save-backup");
                }
                else
                {
                    // Save As creates a new file, never silently overwrites an unrelated report.
                    File.Move(temp, target);
                }
                return bytes;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}