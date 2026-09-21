using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InspectionEditor.Services
{
    // Verified device-local save evidence and an explicitly labeled display-only fallback.
    public static class LastEditTime
    {
        public readonly record struct DisplayStamp(DateTimeOffset? Utc, bool IsVerifiedSave);

        private static string DefaultRoot => Path.Combine(AppIdentity.LocalAppDataPath, "VerifiedSaves");
        private static string Normalize(string path) => OperatingSystem.IsWindows()
            ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static string EntryPath(string root, string path) =>
            Path.Combine(root, Hash(Encoding.UTF8.GetBytes(Normalize(path))) + ".json");

        public static DateTimeOffset? ReadForFile(string path, byte[] bytes)
        {
            try { return ReadForFile(path, bytes, DefaultRoot); }
            catch { return null; }
        }
        public static DateTimeOffset? ReadForFile(string path, byte[] bytes, string storageRoot)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(EntryPath(storageRoot, path)));
                var entry = doc.RootElement;
                if (entry.GetProperty("PathHash").GetString() != Hash(Encoding.UTF8.GetBytes(Normalize(path))) ||
                    entry.GetProperty("ContentHash").GetString() != Hash(bytes)) return null;
                var stamp = entry.GetProperty("SavedUtc").GetDateTimeOffset();
                return IsValid(stamp, DateTimeOffset.UtcNow) ? stamp : null;
            }
            catch { return null; } // Missing, corrupt, or inaccessible evidence is unknown.
        }

        // bytes must come from a successful file read. Never use this display fallback
        // as save evidence: RecordSuccessfulSave must continue using ReadForFile only.
        public static DisplayStamp ReadDisplayForFile(string path, byte[] bytes)
        {
            var verified = ReadForFile(path, bytes);
            return ResolveDisplayStamp(path, verified);
        }

        public static DisplayStamp ReadDisplayForFile(string path, byte[] bytes, string storageRoot)
            => ResolveDisplayStamp(path, ReadForFile(path, bytes, storageRoot));

        private static DisplayStamp ResolveDisplayStamp(string path, DateTimeOffset? verified)
        {
            if (verified.HasValue) return new DisplayStamp(verified, true);
            try
            {
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                return IsValid(modified, DateTimeOffset.UtcNow) ? new DisplayStamp(modified, false) : default;
            }
            catch { return default; }
        }

        // Reject sentinel/pre-epoch values and tolerate only one minute of clock skew.
        private static bool IsValid(DateTimeOffset stamp, DateTimeOffset now) =>
            stamp.Offset == TimeSpan.Zero && stamp > DateTimeOffset.UnixEpoch &&
            now - stamp >= TimeSpan.FromMinutes(-1);

        public static string Tooltip(DisplayStamp stamp, DateTimeOffset now)
        {
            if (!stamp.Utc.HasValue || !IsValid(stamp.Utc.Value, now)) return "";
            string source = stamp.IsVerifiedSave
                ? "Last successful RED edit saved on this device (verified)"
                : "File modified (filesystem timestamp; not a verified RED save)";
            return $"{source}: {stamp.Utc.Value.ToLocalTime():g}";
        }

        // Call ONLY after the atomic writer has returned its verified exact bytes.
        public static void RecordSuccessfulSave(string path, byte[] bytes)
        {
            try { RecordSuccessfulSave(path, bytes, DefaultRoot); }
            catch { /* Even unavailable AppData must not affect the INS save. */ }
        }
        public static void RecordSuccessfulSave(string path, byte[] bytes, string storageRoot)
        {
            string? temp = null;
            try
            {
                // Identical verified content retains its original age, including after reopen.
                if (ReadForFile(path, bytes, storageRoot).HasValue) return;
                string target = EntryPath(storageRoot, path);
                Directory.CreateDirectory(storageRoot);
                temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] record = JsonSerializer.SerializeToUtf8Bytes(new {
                    PathHash = Hash(Encoding.UTF8.GetBytes(Normalize(path))),
                    ContentHash = Hash(bytes), SavedUtc = DateTimeOffset.UtcNow
                });
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(record, 0, record.Length);
                    stream.Flush(true);
                }
                File.Move(temp, target, true);
            }
            catch { /* Optional metadata must never fail an otherwise verified INS save. */ }
            finally { try { if (temp != null) File.Delete(temp); } catch { } }
        }

        public static string Format(DateTimeOffset? saved, DateTimeOffset now)
        {
            if (!saved.HasValue || !IsValid(saved.Value, now)) return "";
            var elapsed = now - saved.Value;
            if (elapsed.TotalMinutes < 1) return "0 min";
            if (elapsed.TotalHours < 1) return $"{(long)elapsed.TotalMinutes} min";
            if (elapsed.TotalDays < 1) return $"{(long)elapsed.TotalHours} hr";
            return $"{(long)elapsed.TotalDays} d";
        }
    }
}
